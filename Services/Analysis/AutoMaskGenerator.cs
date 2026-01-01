using Avalonia;
using Avalonia.Media.Imaging;
using FaceShield.Services.FaceDetection;
using FaceShield.Services.Video;
using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace FaceShield.Services.Analysis
{
    /// <summary>
    /// 영상 전체 프레임을 돌면서
    /// - 얼굴 검출(IFaceDetector)
    /// - 얼굴 bbox 추출
    /// - FrameMaskProvider에 bbox 저장
    /// 까지 한 번에 수행하는 서비스.
    /// </summary>
    public sealed class AutoMaskGenerator
    {
        private readonly IFaceDetector _detector;
        private readonly FrameMaskProvider _maskProvider;
        private readonly AutoMaskOptions _options;

        public AutoMaskGenerator(
            IFaceDetector detector,
            FrameMaskProvider maskProvider,
            AutoMaskOptions? options = null)
        {
            _detector = detector ?? throw new ArgumentNullException(nameof(detector));
            _maskProvider = maskProvider ?? throw new ArgumentNullException(nameof(maskProvider));
            _options = options ?? new AutoMaskOptions();
        }

        public async Task GenerateAsync(
            string videoPath,
            IProgress<int>? progress,
            CancellationToken ct,
            int startFrameIndex = 0,
            Action<int>? onFrameProcessed = null)
        {
            if (string.IsNullOrWhiteSpace(videoPath))
                throw new ArgumentException("videoPath is null or empty.", nameof(videoPath));

            var (fps, totalFrames, _) = ReadVideoInfo(videoPath);

            if (fps <= 0 || totalFrames <= 0)
                return;

            try
            {
                await Task.Run(() =>
                {
                    using var extractor = new FfFrameExtractor(videoPath);

                    IReadOnlyList<FaceDetectionResult>? lastFaces = null;

                    int start = Math.Clamp(startFrameIndex, 0, Math.Max(0, totalFrames - 1));

                    for (int idx = start; idx < totalFrames; idx++)
                    {
                        if (ct.IsCancellationRequested)
                            return;
                        onFrameProcessed?.Invoke(idx);

                        if (_maskProvider.HasEntry(idx))
                        {
                            if (progress != null)
                            {
                                int percent = (int)Math.Round(idx * 100.0 / Math.Max(1, totalFrames - 1));
                                if (percent > 100) percent = 100;
                                progress.Report(percent);
                            }
                            continue;
                        }

                        var frame = extractor.GetFrameByIndex(idx);
                        if (frame == null)
                            continue;

                        bool shouldDetect = _options.DetectEveryNFrames <= 1
                            || idx % _options.DetectEveryNFrames == 0
                            || lastFaces == null;

                        IReadOnlyList<FaceDetectionResult> faces;

                        if (shouldDetect)
                        {
                            faces = DetectFacesWithOptions(frame);
                            lastFaces = faces.Count > 0 ? faces : null;
                        }
                        else if (_options.UseTracking && lastFaces != null)
                        {
                            faces = lastFaces;
                        }
                        else
                        {
                            faces = Array.Empty<FaceDetectionResult>();
                        }

                        if (faces == null || faces.Count == 0)
                            continue;

                        _maskProvider.SetFaceRects(idx, faces.Select(f => f.Bounds).ToArray(), frame.PixelSize);

                        if (progress != null)
                        {
                            int percent = (int)Math.Round(idx * 100.0 / Math.Max(1, totalFrames - 1));
                            if (percent > 100) percent = 100;
                            progress.Report(percent);
                        }
                    }

                    progress?.Report(100);

                }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 취소는 정상 흐름으로 처리 (디버그 예외 노이즈 방지)
            }
        }

        public async Task<bool> GenerateFrameAsync(
            string videoPath,
            int frameIndex,
            IProgress<int>? progress,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(videoPath))
                throw new ArgumentException("videoPath is null or empty.", nameof(videoPath));
            if (frameIndex < 0)
                return false;

            try
            {
                return await Task.Run(() =>
                {
                    if (ct.IsCancellationRequested)
                        return false;

                    using var extractor = new FfFrameExtractor(videoPath);
                    var frame = extractor.GetFrameByIndex(frameIndex);
                    if (frame == null)
                    {
                        progress?.Report(100);
                        return false;
                    }

                    var faces = DetectFacesWithOptions(frame);
                    if (faces != null && faces.Count > 0)
                    {
                        _maskProvider.SetFaceRects(frameIndex, faces.Select(f => f.Bounds).ToArray(), frame.PixelSize);
                        progress?.Report(100);
                        return true;
                    }

                    progress?.Report(100);
                    return false;
                }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return false;
            }
        }

        // 마스크는 필요 시 FrameMaskProvider에서 생성

        private IReadOnlyList<FaceDetectionResult> DetectFacesWithOptions(WriteableBitmap frame)
        {
            double ratio = _options.DownscaleRatio;

            if (ratio >= 1.0)
                return _detector.DetectFaces(frame);

            if (_detector is not FaceOnnxDetector onnx)
                return _detector.DetectFaces(frame);

            using var img = FaceOnnxDetector.ConvertToImageSharp(frame);

            int newW = Math.Max(1, (int)Math.Round(img.Width * ratio));
            int newH = Math.Max(1, (int)Math.Round(img.Height * ratio));

            if (newW != img.Width || newH != img.Height)
            {
                img.Mutate(x => x.Resize(newW, newH));
            }

            var faces = onnx.DetectFaces(img);

            if (faces.Count == 0)
                return faces;

            double scale = ratio > 0 ? 1.0 / ratio : 1.0;
            var scaled = new List<FaceDetectionResult>(faces.Count);

            foreach (var f in faces)
            {
                var b = f.Bounds;
                scaled.Add(new FaceDetectionResult
                {
                    Bounds = new Rect(b.X * scale, b.Y * scale, b.Width * scale, b.Height * scale),
                    Confidence = f.Confidence
                });
            }

            return scaled;
        }

        private unsafe static (double fps, int totalFrames, double durationSeconds) ReadVideoInfo(string path)
        {
            AVFormatContext* fmt = null;

            try
            {
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_QUIET);

                if (ffmpeg.avformat_open_input(&fmt, path, null, null) < 0)
                    throw new InvalidOperationException("Failed to open video.");

                if (ffmpeg.avformat_find_stream_info(fmt, null) < 0)
                    throw new InvalidOperationException("Failed to read stream info.");

                AVStream* videoStream = null;

                for (int i = 0; i < fmt->nb_streams; i++)
                {
                    if (fmt->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                    {
                        videoStream = fmt->streams[i];
                        break;
                    }
                }

                if (videoStream == null)
                    throw new InvalidOperationException("Video stream not found.");

                double fpsValue =
                    videoStream->avg_frame_rate.num != 0
                        ? ffmpeg.av_q2d(videoStream->avg_frame_rate)
                        : videoStream->r_frame_rate.num != 0
                            ? ffmpeg.av_q2d(videoStream->r_frame_rate)
                            : 30.0;

                double durationSeconds;

                if (videoStream->duration > 0)
                {
                    durationSeconds =
                        videoStream->duration * ffmpeg.av_q2d(videoStream->time_base);
                }
                else if (fmt->duration > 0)
                {
                    durationSeconds =
                        fmt->duration / (double)ffmpeg.AV_TIME_BASE;
                }
                else
                {
                    durationSeconds = 0;
                }

                int frames = (int)Math.Floor(durationSeconds * fpsValue);

                return (
                    fps: fpsValue,
                    totalFrames: Math.Max(frames, 0),
                    durationSeconds: Math.Max(durationSeconds, 0));
            }
            finally
            {
                if (fmt != null)
                    ffmpeg.avformat_close_input(&fmt);
            }
        }
    }
}
