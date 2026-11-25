using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace FaceShield.Services.Video;

internal readonly record struct VideoExportOutputCommitResult(
    string Mode,
    long ElapsedMilliseconds);

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

    internal static VideoExportOutputCommitResult CommitStagedOutput(
        string stagedOutputPath,
        string finalOutputPath,
        bool allowOverwrite,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(stagedOutputPath))
        {
            throw new InvalidOperationException(
                "검증된 임시 출력 파일이 생성되지 않았습니다.");
        }

        var timer = Stopwatch.StartNew();
        string mode;
        if (!allowOverwrite)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(stagedOutputPath, finalOutputPath, overwrite: false);
                mode = "move-no-overwrite";
            }
            catch (IOException ex) when (File.Exists(finalOutputPath))
            {
                throw new IOException(
                    $"내보내기 대상 파일이 작업 중 생성되어 덮어쓰기를 중단했습니다: {finalOutputPath}",
                    ex);
            }
        }
        else if (File.Exists(finalOutputPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Replace(
                stagedOutputPath,
                finalOutputPath,
                destinationBackupFileName: null,
                ignoreMetadataErrors: true);
            mode = "replace";
        }
        else
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(stagedOutputPath, finalOutputPath);
            mode = "move";
        }

        timer.Stop();
        return new VideoExportOutputCommitResult(
            mode,
            timer.ElapsedMilliseconds);
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
