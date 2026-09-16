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
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    internal static IReadOnlyList<ManualMaskTrackSegment> Load(string videoPath)
    {
        if (string.IsNullOrWhiteSpace(videoPath))
            return Array.Empty<ManualMaskTrackSegment>();

        try
        {
            string path = GetTrackPath(videoPath);
            if (!File.Exists(path))
            {
                // On Windows, a differing legacy key means the path may live in a
                // case-sensitive directory. The old all-uppercase identity can
                // collide with a distinct source file there, so never fall back to it.
                if (OperatingSystem.IsWindows())
                    return Array.Empty<ManualMaskTrackSegment>();

                string legacyPath = GetLegacyTrackPath(videoPath);
                if (!string.Equals(path, legacyPath, StringComparison.Ordinal) &&
                    File.Exists(legacyPath))
                {
                    path = legacyPath;
                }
                else
                {
                    return Array.Empty<ManualMaskTrackSegment>();
                }
            }

            string json = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize<ManualMaskTrackStoreState>(json, JsonOptions);
            if (state == null ||
                state.Version != CurrentVersion ||
                !string.Equals(
                    state.SourceEvidence,
                    BuildSourceEvidence(videoPath),
                    StringComparison.Ordinal))
            {
                return Array.Empty<ManualMaskTrackSegment>();
            }

            return state.Segments?
                .Where(static segment =>
                    segment.SourceKeyframe >= 0 &&
                    !string.IsNullOrWhiteSpace(segment.SourceMaskFingerprint) &&
                    segment.Components != null &&
                    segment.Components.Count > 0)
                .Select(static segment => segment.Clone())
                .ToArray()
                ?? Array.Empty<ManualMaskTrackSegment>();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ManualMaskTrackStore] load failed: {ex.Message}");
            return Array.Empty<ManualMaskTrackSegment>();
        }
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
        string tempPath = path + ".tmp";
        try
        {
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
            }
            throw;
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
