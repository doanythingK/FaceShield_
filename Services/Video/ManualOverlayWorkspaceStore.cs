using FaceShield.Services.Workspace;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace FaceShield.Services.Video;

/// <summary>
/// Video-aware entry point for the separate manual overlay state. Reuses the
/// workspace's platform-aware path identity, not the legacy tracking file.
/// Neither loading nor saving decodes video frames.
/// </summary>
internal static class ManualOverlayWorkspaceStore
{
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
