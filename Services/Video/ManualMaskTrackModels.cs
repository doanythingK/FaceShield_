using FaceShield.Services.Workspace;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FaceShield.Services.Video;

internal sealed class ManualMaskTrackSample
{
    [JsonRequired] public int FrameIndex { get; set; }
    [JsonRequired] public double OffsetX { get; set; }
    [JsonRequired] public double OffsetY { get; set; }
    [JsonRequired] public double Scale { get; set; } = 1.0;
    [JsonRequired] public double Confidence { get; set; } = 1.0;
    // Optional in v3: existing saved tracks do not have this field and mean zero rotation.
    public double RotationRadians { get; set; }
}

internal sealed class ManualMaskTrackComponent
{
    [JsonRequired] public int ComponentIndex { get; set; }
    [JsonRequired] public double SourceBoundsX { get; set; }
    [JsonRequired] public double SourceBoundsY { get; set; }
    [JsonRequired] public double SourceBoundsWidth { get; set; }
    [JsonRequired] public double SourceBoundsHeight { get; set; }
    [JsonRequired] public List<ManualMaskTrackSample> Samples { get; set; } = new();

    internal ManualMaskTrackComponent Clone() => new()
    {
        ComponentIndex = ComponentIndex,
        SourceBoundsX = SourceBoundsX,
        SourceBoundsY = SourceBoundsY,
        SourceBoundsWidth = SourceBoundsWidth,
        SourceBoundsHeight = SourceBoundsHeight,
        Samples = Samples.Select(static sample => new ManualMaskTrackSample
        {
            FrameIndex = sample.FrameIndex,
            OffsetX = sample.OffsetX,
            OffsetY = sample.OffsetY,
            Scale = sample.Scale,
            Confidence = sample.Confidence,
            RotationRadians = sample.RotationRadians
        }).ToList()
    };
}

internal sealed class ManualMaskTrackSegment
{
    [JsonRequired] public int SourceKeyframe { get; set; }
    [JsonRequired] public string SourceMaskFingerprint { get; set; } = string.Empty;
    [JsonRequired] public int EndExclusive { get; set; }
    [JsonRequired] public bool StoppedByFailure { get; set; }
    public int? StopFrame { get; set; }
    public string? StopReason { get; set; }
    [JsonRequired] public List<ManualMaskTrackComponent> Components { get; set; } = new();

    internal ManualMaskTrackSegment Clone() => new()
    {
        SourceKeyframe = SourceKeyframe,
        SourceMaskFingerprint = SourceMaskFingerprint,
        EndExclusive = EndExclusive,
        StoppedByFailure = StoppedByFailure,
        StopFrame = StopFrame,
        StopReason = StopReason,
        Components = Components.OrderBy(static component => component.ComponentIndex)
            .Select(static component => component.Clone()).ToList()
    };
}

internal sealed record ManualMaskTrackResult(ManualMaskTrackSegment Segment, int ProcessedFrames, bool ReachedBoundary);

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

    private sealed class TrackHeader
    {
        [JsonRequired] public int Version { get; set; } = CurrentVersion;
        [JsonRequired] public string SourceEvidence { get; set; } = string.Empty;
    }

    internal static IReadOnlyList<ManualMaskTrackSegment> Load(string videoPath)
    {
        if (string.IsNullOrWhiteSpace(videoPath)) return Array.Empty<ManualMaskTrackSegment>();
        try { return LoadCore(videoPath, strict: false); }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ManualMaskTrackStore] load failed: {ex.Message}");
            return Array.Empty<ManualMaskTrackSegment>();
        }
    }

    // An absent file means no tracking. A present invalid file must block export.
    internal static IReadOnlyList<ManualMaskTrackSegment> LoadForExport(string videoPath)
    {
        if (string.IsNullOrWhiteSpace(videoPath))
            throw new ArgumentException("Video path is required.", nameof(videoPath));
        return LoadCore(videoPath, strict: true);
    }

    private static IReadOnlyList<ManualMaskTrackSegment> LoadCore(string videoPath, bool strict)
    {
        string? path = ResolveExistingTrackPath(videoPath);
        if (path == null) return Array.Empty<ManualMaskTrackSegment>();
        string json = File.ReadAllText(path);
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Manual tracking state must be a JSON object.");
        TrackHeader? header = JsonSerializer.Deserialize<TrackHeader>(json, JsonOptions);
        if (header == null)
            throw new InvalidDataException("Manual tracking state has no header.");
        if (header.Version != CurrentVersion)
            throw new InvalidDataException($"Unsupported manual tracking state version {header.Version}; expected {CurrentVersion}.");
        if (!string.Equals(header.SourceEvidence, BuildSourceEvidence(videoPath), StringComparison.Ordinal))
            throw new InvalidDataException("Manual tracking state does not match the current source video evidence.");
        JsonElement segmentsElement = default;
        bool found = false;
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
            if (string.Equals(property.Name, "Segments", StringComparison.OrdinalIgnoreCase))
            {
                segmentsElement = property.Value;
                found = true;
            }
        if (!found || segmentsElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Manual tracking state has no segment collection.");
        var result = new List<ManualMaskTrackSegment>(segmentsElement.GetArrayLength());
        var keyframes = new HashSet<int>();
        foreach (JsonElement element in segmentsElement.EnumerateArray())
        {
            ManualMaskTrackSegment? segment;
            try { segment = JsonSerializer.Deserialize<ManualMaskTrackSegment>(element, JsonOptions); }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                if (strict) throw new InvalidDataException("Manual tracking state contains an undecodable segment.", ex);
                Debug.WriteLine($"[ManualMaskTrackStore] skipping undecodable segment: {ex.Message}");
                continue;
            }
            if (!TryValidateSegment(segment, out string reason))
            {
                if (strict) throw new InvalidDataException($"Manual tracking state contains an invalid segment: {reason}");
                Debug.WriteLine($"[ManualMaskTrackStore] skipping invalid segment: {reason}");
                continue;
            }
            if (!keyframes.Add(segment!.SourceKeyframe))
            {
                if (strict) throw new InvalidDataException("Manual tracking state has duplicate source keyframes.");
                Debug.WriteLine("[ManualMaskTrackStore] skipping duplicate source keyframe.");
                continue;
            }
            result.Add(segment!.Clone());
        }
        return result;
    }

    private static bool TryValidateSegment(ManualMaskTrackSegment? segment, out string reason)
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
                // An isolated later sample is not proof that the missing frames
                // in between were tracked. Reject incomplete sequences at load
                // and before committing a new state file.
                if (sample == null ||
                    previousFrame == int.MaxValue ||
                    sample.FrameIndex != previousFrame + 1 ||
                    sample.FrameIndex >= segment.EndExclusive ||
                    !double.IsFinite(sample.OffsetX) || !double.IsFinite(sample.OffsetY) ||
                    !double.IsFinite(sample.Scale) || sample.Scale < 0.25 || sample.Scale > 4.0 ||
                    !double.IsFinite(sample.RotationRadians) || Math.Abs(sample.RotationRadians) > Math.PI ||
                    !double.IsFinite(sample.Confidence) || sample.Confidence < 0 || sample.Confidence > 1 ||
                    !double.IsFinite(component.SourceBoundsX + sample.OffsetX) ||
                    !double.IsFinite(component.SourceBoundsY + sample.OffsetY) ||
                    !double.IsFinite(component.SourceBoundsWidth * sample.Scale) ||
                    !double.IsFinite(component.SourceBoundsHeight * sample.Scale))
                {
                    reason = "invalid, missing or unordered frame sample/transform";
                    return false;
                }
                previousFrame = sample.FrameIndex;
            }
            // Every component must cover the same complete interval up to the
            // exclusive boundary; a missing tail must not appear as a success.
            if (previousFrame != segment.EndExclusive - 1)
            {
                reason = "manual track component ends before its declared boundary";
                return false;
            }
        }
        reason = string.Empty;
        return true;
    }

    private static string? ResolveExistingTrackPath(string videoPath)
    {
        string path = GetTrackPath(videoPath);
        if (File.Exists(path)) return path;
        // A Windows legacy uppercase path can collide with a distinct file: do not use it.
        if (OperatingSystem.IsWindows()) return null;
        string legacy = GetLegacyTrackPath(videoPath);
        return !string.Equals(path, legacy, StringComparison.Ordinal) && File.Exists(legacy)
            ? legacy : null;
    }

    internal static void Save(string videoPath, IReadOnlyCollection<ManualMaskTrackSegment> segments)
    {
        if (string.IsNullOrWhiteSpace(videoPath))
            throw new ArgumentException("Video path is required.", nameof(videoPath));
        ArgumentNullException.ThrowIfNull(segments);
        var keyframes = new HashSet<int>();
        foreach (ManualMaskTrackSegment segment in segments)
        {
            if (!TryValidateSegment(segment, out string reason))
                throw new InvalidDataException($"Cannot persist an invalid manual track: {reason}");
            if (!keyframes.Add(segment.SourceKeyframe))
                throw new InvalidDataException("Cannot persist duplicate manual source keyframes.");
        }
        string path = GetTrackPath(videoPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var state = new ManualMaskTrackStoreState
        {
            Version = CurrentVersion,
            SourceEvidence = BuildSourceEvidence(videoPath),
            Segments = segments.OrderBy(static segment => segment.SourceKeyframe)
                .Select(static segment => segment.Clone()).ToList()
        };
        string json = JsonSerializer.Serialize(state, JsonOptions);
        lock (SaveGate)
        {
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, json);
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch { }
            }
        }
    }

    private static string GetTrackPath(string videoPath) =>
        BuildTrackPath(WorkspacePathIdentity.CreateIdentityKey(videoPath));

    private static string GetLegacyTrackPath(string videoPath)
    {
        string normalized = Path.GetFullPath(videoPath);
        if (OperatingSystem.IsWindows()) normalized = normalized.ToUpperInvariant();
        return BuildTrackPath(normalized);
    }

    private static string BuildTrackPath(string identityKey)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identityKey));
        string key = Convert.ToHexString(hash).ToLowerInvariant();
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FaceShield", "manual-tracks", key + ".json");
    }

    private static string BuildSourceEvidence(string videoPath)
    {
        try
        {
            var info = new FileInfo(videoPath);
            return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "missing";
        }
        catch { return "unavailable"; }
    }
}
