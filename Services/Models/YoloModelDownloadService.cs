using FaceShield.Services.FaceDetection;
using System;
using System.IO;
using System.Net.Http;
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
        string LicenseLabel);

    internal static DownloadInfo GetInfo(YoloFaceModelType modelType)
    {
        if (modelType == YoloFaceModelType.Yolo5Face)
        {
            return new DownloadInfo(
                "YoloV5Face.onnx",
                "https://huggingface.co/hayashiLin/deepfacelivemodels/resolve/main/YoloV5Face.onnx?download=true",
                "Hugging Face hayashiLin/deepfacelivemodels",
                "GPL-3.0 표시");
        }

        return new DownloadInfo(
            "yolov8n-face-lindevs.onnx",
            "https://github.com/lindevs/yolov8-face/releases/download/1.0.1/yolov8n-face-lindevs.onnx",
            "GitHub lindevs/yolov8-face 1.0.1",
            "MIT 표시 + YOLOv8 upstream license caveat");
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
        string path = GetDestinationPath(modelType);
        return File.Exists(path) && new FileInfo(path).Length > 0;
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
        if (File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 0)
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
}
