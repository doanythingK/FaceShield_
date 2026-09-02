using FaceShield.Services.FaceDetection;
using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace FaceShield.Services.Models;

internal static class YoloModelDownloadService
{
    private static readonly HttpClient HttpClient = new();

    internal sealed record DownloadInfo(
        string FileName,
        string DownloadUrl,
        string SourceLabel,
        string LicenseLabel,
        long ExpectedSize,
        string ExpectedSha256);

    internal static DownloadInfo GetInfo(YoloFaceModelType modelType)
    {
        if (modelType == YoloFaceModelType.Yolo5Face)
        {
            return new DownloadInfo(
                "YoloV5Face.onnx",
                "https://huggingface.co/hayashiLin/deepfacelivemodels/resolve/b6f5aa06757a2601fe1e35bdec6d97916494802d/YoloV5Face.onnx?download=true",
                "Hugging Face hayashiLin/deepfacelivemodels @ b6f5aa0",
                "GPL-3.0 표시",
                28_282_217,
                "907c295f79eba1b0f4be59bcf5d8aabe4e2a9002ec44c5d1c518b97eb9fb13da");
        }

        return new DownloadInfo(
            "yolov8n-face-lindevs.onnx",
            "https://github.com/lindevs/yolov8-face/releases/download/1.0.1/yolov8n-face-lindevs.onnx",
            "GitHub lindevs/yolov8-face 1.0.1",
            "MIT 표시 + YOLOv8 upstream license caveat",
            12_250_955,
            "8d0bfb0c3383c5bd7a78dd24ef79a21e2aa456619b6ab5e53867092d1c7dc414");
    }

    internal static string GetDownloadDirectory()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string root = string.IsNullOrWhiteSpace(localAppData)
            ? Path.Combine(Path.GetTempPath(), "FaceShield")
            : Path.Combine(localAppData, "FaceShield");

        return Path.Combine(root, "Models", "Yolo");
    }

    internal static string GetDestinationPath(YoloFaceModelType modelType)
    {
        var info = GetInfo(modelType);
        return Path.Combine(GetDownloadDirectory(), info.FileName);
    }

    internal static bool IsDownloaded(YoloFaceModelType modelType)
    {
        DownloadInfo info = GetInfo(modelType);
        string path = GetDestinationPath(modelType);
        return IsExpectedArtifact(path, info);
    }

    internal static async Task<string> DownloadAsync(
        YoloFaceModelType modelType,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        var info = GetInfo(modelType);
        string directory = GetDownloadDirectory();
        Directory.CreateDirectory(directory);

        string destinationPath = Path.Combine(directory, info.FileName);
        if (IsExpectedArtifact(destinationPath, info))
        {
            progress?.Report(100);
            return destinationPath;
        }

        string tempPath = destinationPath + ".download";
        try
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);

            using var request = new HttpRequestMessage(HttpMethod.Get, info.DownloadUrl);
            request.Headers.UserAgent.ParseAdd("FaceShield/1.0");

            using var response = await HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long? totalBytes = response.Content.Headers.ContentLength;
            await using var source = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var destination = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 128,
                useAsync: true);

            var buffer = new byte[1024 * 128];
            long readBytes = 0;
            while (true)
            {
                int read = await source.ReadAsync(buffer.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                if (read <= 0)
                    break;

                await destination.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken).ConfigureAwait(false);
                readBytes += read;

                if (totalBytes.HasValue && totalBytes.Value > 0)
                {
                    int percent = Math.Clamp(
                        (int)Math.Round(readBytes * 100.0 / totalBytes.Value),
                        0,
                        99);
                    progress?.Report(percent);
                }
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            destination.Close();

            if (readBytes <= 0)
                throw new InvalidDataException("다운로드된 모델 파일이 비어 있습니다.");

            if (totalBytes.HasValue &&
                totalBytes.Value > 0 &&
                readBytes != totalBytes.Value)
            {
                throw new InvalidDataException(
                    $"모델 다운로드 크기가 HTTP Content-Length와 다릅니다(expected={totalBytes.Value}, actual={readBytes}).");
            }

            if (readBytes != info.ExpectedSize)
            {
                throw new InvalidDataException(
                    $"모델 다운로드 크기가 고정 배포 파일과 다릅니다(expected={info.ExpectedSize}, actual={readBytes}).");
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!HasExpectedSha256(tempPath, info.ExpectedSha256))
            {
                throw new InvalidDataException(
                    $"모델 SHA-256 검증에 실패했습니다: {info.FileName}");
            }

            File.Move(tempPath, destinationPath, overwrite: true);
            progress?.Report(100);
            return destinationPath;
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
                // Cleanup is best-effort; preserve the original download failure.
            }

            throw;
        }
    }

    private static bool IsExpectedArtifact(string path, DownloadInfo info)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists &&
                   file.Length == info.ExpectedSize &&
                   HasExpectedSha256(path, info.ExpectedSha256);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasExpectedSha256(string path, string expectedSha256)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            byte[] digest = SHA256.HashData(stream);
            string actual = Convert.ToHexString(digest);
            return string.Equals(
                actual,
                expectedSha256,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
