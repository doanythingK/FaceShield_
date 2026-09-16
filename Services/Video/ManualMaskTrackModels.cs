using FaceShield.Services.Workspace;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FaceShield.Services.Video;

internal sealed class ManualMaskTrackSample
{
    public int FrameIndex { get; set; }
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public double Scale { get; set; } = 1.0;
    public double Confidence { get; set; } = 1.0;
}

internal sealed class ManualMaskTrackComponent
{
    public int ComponentIndex { get; set; }
    public double SourceBoundsX { get; set; }
    public double SourceBoundsY { get; set; }
    public double SourceBoundsWidth { get; set; }
    public double SourceBoundsHeight { get; set; }
    public List<ManualMaskTrackSample> Samples { get; set; } = new();

    internal ManualMaskTrackComponent Clone()
        => new()
        {
            ComponentIndex = ComponentIndex,
            SourceBoundsX = SourceBoundsX,
            SourceBoundsY = SourceBoundsY,
            SourceBoundsWidth = SourceBoundsWidth,
            SourceBoundsHeight = SourceBoundsHeight,
            Samples = Samples
                .Select(static sample => new ManualMaskTrackSample
                {
                    FrameIndex = sample.FrameIndex,
                    OffsetX = sample.OffsetX,
                    OffsetY = sample.OffsetY,
                    Scale = sample.Scale,
                    Confidence = sample.Confidence
                })
                .ToList()
        };
}

internal sealed class ManualMaskTrackSegment
{
    public int SourceKeyframe { get; set; }
    public string SourceMaskFingerprint { get; set; } = string.Empty;
    public int EndExclusive { get; set; }
    public bool StoppedByFailure { get; set; }
    public int? StopFrame { get; set; }
    public string? StopReason { get; set; }
    public List<ManualMaskTrackComponent> Components { get; set; } = new();

    internal ManualMaskTrackSegment Clone()
        => new()
        {
            SourceKeyframe = SourceKeyframe,
            SourceMaskFingerprint = SourceMaskFingerprint,
            EndExclusive = EndExclusive,
            StoppedByFailure = StoppedByFailure,
            StopFrame = StopFrame,
            StopReason = StopReason,
            Components = Components
                .OrderBy(static component => component.ComponentIndex)
                .Select(static component => component.Clone())
                .ToList()
        };
}

internal sealed record ManualMaskTrackResult(
    ManualMaskTrackSegment Segment,
    int ProcessedFrames,
    bool ReachedBoundary);

internal sealed class ManualMaskTrackStoreState
{
    public int Version { get; set; } = 3;
    public string SourceEvidence { get; set; } = string.Empty;
    public List<ManualMaskTrackSegment> Segments { get; set; } = new();
}

internal static class ManualMaskTrackStore
{
    private const int CurrentVersion = 3;
    private static readonly object SaveGate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    // Deserializing the complete state in one pass makes a single malformed numeric
    // field destroy every other segment before tolerant validation can run.
    private sealed class TrackHeader
    {
        public int Version { get; set; } = CurrentVersion;
        public string SourceEvidence { get; set; } = string.Empty;
    }

    internal static IReadOnlyList<ManualMaskTrackSegment> Load(string videoPath)
    {
        if (string.IsNullOrWhiteSpace(videoPath))
            return Array.Empty<ManualMaskTrackSegment>();

        try
        {
            return LoadCore(videoPath, strict: false);
        }
        catch (Exception ex)
        {
            // A missing/corrupt whole document, unsupported version or stale source
            // evidence cannot be repaired by removing individual segments.
            Debug.WriteLine($"[ManualMaskTrackStore] load failed: {ex.Message}");
            return Array.Empty<ManualMaskTrackSegment>();
        }
    }

    /// <summary>
    /// Strict read used by export safety checks. A missing track file is valid and
    /// means there is no persisted tracking metadata, but an existing unreadable,
    /// incompatible, stale-evidence, or structurally invalid file must not be treated
    /// as an empty successful state.
    /// </summary>
    internal static IReadOnlyList<ManualMaskTrackSegment> LoadForExport(string videoPath)
    {
        if (string.IsNullOrWhiteSpace(videoPath))
            throw new ArgumentException("Video path is required.", nameof(videoPath));

        return LoadCore(videoPath, strict: true);
    }

    private static IReadOnlyList<ManualMaskTrackSegment> LoadCore(
        string videoPath,
        bool strict)
    {
        string? path = ResolveExistingTrackPath(videoPath);
        if (path == null)
            return Array.Empty<ManualMaskTrackSegment>();

        string json = File.ReadAllText(path);
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Manual tracking state must be a JSON object.");

        TrackHeader? header = JsonSerializer.Deserialize<TrackHeader>(json, JsonOptions);
        if (header == null)
            throw new InvalidDataException("Manual tracking state has no header.");
        if (header.Version != CurrentVersion)
        {
            throw new InvalidDataException(
                $"Unsupported manual tracking state version {header.Version}; expected {CurrentVersion}.");
        }

        string expectedEvidence = BuildSourceEvidence(videoPath);
        if (!string.Equals(header.SourceEvidence, expectedEvidence, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Manual tracking state does not match the current source video evidence.");
        }

        // Match the serializer's case-insensitive property handling. Missing or
        // non-array collections are file-level failures, not recoverable segments.
        JsonElement segmentsElement = default;
        bool foundSegments = false;
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (string.Equals(property.Name, "Segments", StringComparison.OrdinalIgnoreCase))
            {
                segmentsElement = property.Value;
                foundSegments = true;
            }
        }
        if (!foundSegments || segmentsElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Manual tracking state has no segment collection.");

        var result = new List<ManualMaskTrackSegment>(segmentsElement.GetArrayLength());
        var keyframes = new HashSet<int>();
        foreach (JsonElement element in segmentsElement.EnumerateArray())
        {
            ManualMaskTrackSegment? segment;
            try
            {
                segment = JsonSerializer.Deserialize<ManualMaskTrackSegment>(element, JsonOptions);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                if (strict)
                {
                    throw new InvalidDataException(
                        "Manual tracking state contains an undecodable segment.", ex);
                }

                Debug.WriteLine($"[ManualMaskTrackStore] skipping undecodable segment: {ex.Message}");
                continue;
            }

            if (!TryValidateSegment(segment, out string reason))
            {
                if (strict)
                {
                    throw new InvalidDataException(
                        $"Manual tracking state contains an invalid segment: {reason}");
                }

                Debug.WriteLine($"[ManualMaskTrackStore] skipping invalid segment: {reason}");
                continue;
            }

            if (!keyframes.Add(segment!.SourceKeyframe))
            {
                if (strict)
                    throw new InvalidDataException("Manual tracking state has duplicate source keyframes.");

                Debug.WriteLine("[ManualMaskTrackStore] skipping duplicate source keyframe.");
                continue;
            }

            result.Add(segment!.Clone());
        }

        return result;
    }

    private static bool TryValidateSegment(
        ManualMaskTrackSegment? segment,
        out string reason)
    {
        if (segment == null || segment.SourceKeyframe < 0 ||
            string.IsNullOrWhiteSpace(segment.SourceMaskFingerprint) ||
            segment.EndExclusive <= segment.SourceKeyframe ||
            segment.Components == null || segment.Components.Count == 0)
        {
            reason = "missing or invalid segment header";
            return false;
        }

        if (segment.StoppedByFailure)
        {
            if (segment.StopFrame != segment.EndExclusive)
            {
                reason = "failure boundary does not match end-exclusive frame";
                return false;
            }
        }
        else if (segment.StopFrame.HasValue)
        {
            reason = "non-failed segment has a failure frame";
            return false;
        }

        var componentIndices = new HashSet<int>();
        foreach (ManualMaskTrackComponent? component in segment.Components)
        {
            if (component == null || component.ComponentIndex < 0 ||
                !componentIndices.Add(component.ComponentIndex) ||
                !double.IsFinite(component.SourceBoundsX) ||
                !double.IsFinite(component.SourceBoundsY) ||
                !double.IsFinite(component.SourceBoundsWidth) ||
                !double.IsFinite(component.SourceBoundsHeight) ||
                component.SourceBoundsX < 0 || component.SourceBoundsY < 0 ||
                component.SourceBoundsWidth <= 0 || component.SourceBoundsHeight <= 0 ||
                !double.IsFinite(component.SourceBoundsX + component.SourceBoundsWidth) ||
                !double.IsFinite(component.SourceBoundsY + component.SourceBoundsHeight) ||
                component.Samples == null)
            {
                reason = "invalid component index, bounds or sample collection";
                return false;
            }

            int previousFrame = segment.SourceKeyframe;
            foreach (ManualMaskTrackSample? sample in component.Samples)
            {
                if (sample == null || sample.FrameIndex <= previousFrame ||
                    sample.FrameIndex >= segment.EndExclusive ||
                    !double.IsFinite(sample.OffsetX) ||
                    !double.IsFinite(sample.OffsetY) ||
                    !double.IsFinite(sample.Scale) ||
                    sample.Scale < 0.25 || sample.Scale > 4.0 ||
                    !double.IsFinite(sample.Confidence) ||
                    sample.Confidence < 0 || sample.Confidence > 1 ||
                    !double.IsFinite(component.SourceBoundsX + sample.OffsetX) ||
                    !double.IsFinite(component.SourceBoundsY + sample.OffsetY) ||
                    !double.IsFinite(component.SourceBoundsWidth * sample.Scale) ||
                    !double.IsFinite(component.SourceBoundsHeight * sample.Scale))
                {
                    reason = "invalid, duplicate or unordered frame sample/transform";
                    return false;
                }

                previousFrame = sample.FrameIndex;
            }
        }

        reason = string.Empty;
        return true;
    }

    private static string? ResolveExistingTrackPath(string videoPath)
    {
        string path = GetTrackPath(videoPath);
        if (File.Exists(path))
            return path;

        // On Windows, a differing legacy key means the path may live in a
        // case-sensitive directory. The old all-uppercase identity can collide with
        // a distinct source file there, so never fall back to it.
        if (OperatingSystem.IsWindows())
            return null;

        string legacyPath = GetLegacyTrackPath(videoPath);
        if (!string.Equals(path, legacyPath, StringComparison.Ordinal) &&
            File.Exists(legacyPath))
        {
            return legacyPath;
        }

        return null;
    }

    internal static void Save(
        string videoPath,
        IReadOnlyCollection<ManualMaskTrackSegment> segments)
    {
        if (string.IsNullOrWhiteSpace(videoPath))
            throw new ArgumentException("Video path is required.", nameof(videoPath));

        string path = GetTrackPath(videoPath);
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);

        var state = new ManualMaskTrackStoreState
        {
            Version = CurrentVersion,
            SourceEvidence = BuildSourceEvidence(videoPath),
            Segments = segments
                .OrderBy(static segment => segment.SourceKeyframe)
                .Select(static segment => segment.Clone())
                .ToList()
        };

        string json = JsonSerializer.Serialize(state, JsonOptions);
        lock (SaveGate)
        {
            string tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, path, overwrite: true);
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch
                {
                }
            }
        }
    }

    private static string GetTrackPath(string videoPath)
    {
        string identityKey = WorkspacePathIdentity.CreateIdentityKey(videoPath);
        return BuildTrackPath(identityKey);
    }

    private static string GetLegacyTrackPath(string videoPath)
    {
        string normalized = Path.GetFullPath(videoPath);
        if (OperatingSystem.IsWindows())
            normalized = normalized.ToUpperInvariant();
        return BuildTrackPath(normalized);
    }

    private static string BuildTrackPath(string identityKey)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identityKey));
        string key = Convert.ToHexString(hash).ToLowerInvariant();
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FaceShield",
            "manual-tracks");
        return Path.Combine(root, key + ".json");
    }

    private static string BuildSourceEvidence(string videoPath)
    {
        try
        {
            var info = new FileInfo(videoPath);
            if (!info.Exists)
                return "missing";
            return $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            return "unavailable";
        }
    }
}
