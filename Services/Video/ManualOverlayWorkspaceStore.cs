using FaceShield.Services.Workspace;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FaceShield.Services.Video;

/// <summary>
/// Video-aware entry point for the separate manual overlay state. Reuses the
/// workspace's platform-aware path identity, not the legacy tracking file.
/// Neither loading nor saving decodes video frames.
/// </summary>
internal static class ManualOverlayWorkspaceStore
{
    private static readonly object CommitGate = new();

    internal static string GetPathForVideo(string videoPath, string? stateDirectory = null)
    {
        string identity = WorkspacePathIdentity.CreateIdentityKey(videoPath);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();
        string directory = stateDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FaceShield", "manual-overlays");
        return Path.Combine(directory, key + ".json");
    }

    internal static IReadOnlyList<ManualOverlayStoredTarget> LoadForVideo(
        string videoPath, string? stateDirectory = null)
    {
        string evidence = GetSourceEvidence(videoPath);
        return ManualOverlayStateStore.Load(GetPathForVideo(videoPath, stateDirectory), evidence);
    }

    internal static void SaveForVideo(
        string videoPath,
        IReadOnlyCollection<ManualOverlayStoredTarget> targets,
        string? stateDirectory = null)
    {
        string evidence = GetSourceEvidence(videoPath);
        ManualOverlayStateStore.Save(GetPathForVideo(videoPath, stateDirectory), evidence, targets);
    }

    /// <summary>
    /// Refuse to overwrite another writer's saved correction or track. Both
    /// target-owned committers provide the exact state they started from.
    /// A persistent lock file coordinates other instances using this method;
    /// it cannot constrain older/uncooperative writers that bypass this API.
    /// </summary>
    internal static void CommitIfUnchangedForVideo(
        string videoPath,
        IReadOnlyCollection<ManualOverlayStoredTarget> expected,
        IReadOnlyCollection<ManualOverlayStoredTarget> updated)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(updated);
        string evidence = GetSourceEvidence(videoPath);
        string statePath = GetPathForVideo(videoPath);
        string fullPath = Path.GetFullPath(statePath);

        // Keep the lock file in place after closing it: removing it would
        // permit a second process to lock a different inode mid-transaction.
        lock (CommitGate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            using var lockFile = new FileStream(
                fullPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None);
            IReadOnlyList<ManualOverlayStoredTarget> actual =
                ManualOverlayStateStore.Load(fullPath, evidence);
            if (!CanonicalState(actual).AsSpan().SequenceEqual(CanonicalState(expected)))
                throw new IOException(
                    "Manual face state changed on disk. Reload the video before saving to avoid overwriting another edit.");

            // Validation and atomic replacement use the source evidence read
            // at the start of this commit, avoiding two different headers.
            ManualOverlayStateStore.Save(fullPath, evidence, updated);
        }
    }

    private static byte[] CanonicalState(
        IEnumerable<ManualOverlayStoredTarget> targets)
    {
        // Normalize collection order, but preserve every keyframe alpha byte,
        // tracking sample, stop boundary and reason in the comparison.
        var canonical = targets.OrderBy(static target => target.Id).Select(target => new
        {
            target.Id,
            Keyframes = target.Keyframes.OrderBy(static keyframe => keyframe.FrameIndex)
                .ToArray(),
            Segments = (target.Segments ?? Array.Empty<ManualMaskTrackSegment>())
                .OrderBy(static segment => segment.SourceKeyframe)
                .Select(static segment => segment.Clone())
                .ToArray()
        }).ToArray();
        return JsonSerializer.SerializeToUtf8Bytes(canonical);
    }

    private static string GetSourceEvidence(string videoPath)
    {
        if (string.IsNullOrWhiteSpace(videoPath))
            throw new ArgumentException("A source video path is required.", nameof(videoPath));
        var info = new FileInfo(videoPath);
        info.Refresh();
        if (!info.Exists)
            throw new FileNotFoundException("Manual overlay source video was not found.", videoPath);
        // Same cheap metadata evidence contract used by the legacy manual-track
        // store. It prevents ordinary source replacements but is not a content hash.
        return $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
    }
}
