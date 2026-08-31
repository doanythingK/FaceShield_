using System;
using System.Diagnostics;
using System.IO;

namespace FaceShield.Services.Video;

internal static class VideoExportStagingPolicy
{
    internal static string BuildStagedOutputPath(string finalOutputPath)
    {
        string directory =
            Path.GetDirectoryName(finalOutputPath) ?? Directory.GetCurrentDirectory();
        string name = Path.GetFileNameWithoutExtension(finalOutputPath);
        string extension = Path.GetExtension(finalOutputPath);
        return Path.Combine(
            directory,
            $".{name}.faceshield-{Guid.NewGuid():N}{extension}");
    }

    internal static void TryDeleteStagedOutput(string stagedOutputPath)
    {
        try
        {
            if (File.Exists(stagedOutputPath))
                File.Delete(stagedOutputPath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[Export] 임시 출력 정리 실패: {stagedOutputPath}, {ex.Message}");
        }
    }
}
