using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text.Json;

namespace FaceShield.Services.Video;

/// <summary>
/// Optional UI selection metadata; never determines mask ownership or export
/// coverage. A missing, stale or damaged selection cannot prevent opening the
/// independently persisted manual-target masks.
/// </summary>
internal static class ManualOverlayTargetSelectionStore
{
    private const int Version = 1;

    private sealed record SelectionRecord(int Version, string SourceEvidence, Guid TargetId);

    internal static Guid? Load(string videoPath)
    {
        try
        {
            // Resolve the path INSIDE the error boundary: an invalid path or
            // missing source must not turn optional UI metadata into a fatal
            // workspace loading failure.
            string path = GetPath(videoPath);
            if (!File.Exists(path))
                return null;
            SelectionRecord? record = JsonSerializer.Deserialize<SelectionRecord>(File.ReadAllText(path));
            return record != null && record.Version == Version &&
                   record.TargetId != Guid.Empty &&
                   string.Equals(record.SourceEvidence, GetSourceEvidence(videoPath),
                       StringComparison.Ordinal)
                ? record.TargetId
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                    JsonException or ArgumentException or SecurityException or
                                    NotSupportedException)
        {
            Debug.WriteLine($"[ManualOverlayTargetSelection] Ignoring invalid UI selection: {ex.Message}");
            return null;
        }
    }

    internal static void Save(string videoPath, Guid targetId)
    {
        if (targetId == Guid.Empty)
            throw new ArgumentException("A real manual target ID is required.", nameof(targetId));
        string path = GetPath(videoPath);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new SelectionRecord(
                Version, GetSourceEvidence(videoPath), targetId)));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"[ManualOverlayTargetSelection] Temporary UI metadata cleanup failed: {ex.Message}");
            }
        }
    }

    private static string GetPath(string videoPath)
        => ManualOverlayWorkspaceStore.GetPathForVideo(videoPath) + ".selection";

    private static string GetSourceEvidence(string videoPath)
    {
        var info = new FileInfo(videoPath);
        info.Refresh();
        if (!info.Exists)
            throw new FileNotFoundException("Manual target source video was not found.", videoPath);
        return $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
    }
}
