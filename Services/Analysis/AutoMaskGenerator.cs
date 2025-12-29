using Avalonia;
using Avalonia.Media.Imaging;
using FaceShield.Services.FaceDetection;
using FaceShield.Services.Video;
using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
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
    /// - 얼굴 bbox 기반 마스크(WriteableBitmap) 작성
    /// - FrameMaskProvider에 저장
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
                        ct.ThrowIfCancellationRequested();
                        onFrameProcessed?.Invoke(idx);

                        if (_maskProvider.GetFinalMask(idx) != null)
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

                        var mask = CreateMaskFromFaces(frame, faces);
                        _maskProvider.SetMask(idx, mask);

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
                    ct.ThrowIfCancellationRequested();

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
                        var mask = CreateMaskFromFaces(frame, faces);
                        _maskProvider.SetMask(frameIndex, mask);
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

        private static WriteableBitmap CreateMaskFromFaces(
            WriteableBitmap frame,
            IReadOnlyList<FaceDetectionResult> faces)
        {
            var size = frame.PixelSize;
            var mask = new WriteableBitmap(
                size,
                new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888,
                Avalonia.Platform.AlphaFormat.Premul);

            using var fb = mask.Lock();

            unsafe
            {
                byte* basePtr = (byte*)fb.Address;
                int stride = fb.RowBytes;
                int w = size.Width;
                int h = size.Height;

                foreach (var face in faces)
                {
                    var r = face.Bounds;

                    int x0 = Math.Clamp((int)Math.Floor(r.X), 0, Math.Max(0, w - 1));
                    int y0 = Math.Clamp((int)Math.Floor(r.Y), 0, Math.Max(0, h - 1));
                    int x1 = Math.Clamp((int)Math.Ceiling(r.X + r.Width), 0, w);
                    int y1 = Math.Clamp((int)Math.Ceiling(r.Y + r.Height), 0, h);

                    double cx = (x0 + x1 - 1) / 2.0;
                    double cy = (y0 + y1 - 1) / 2.0;
                    double rx = Math.Max(1.0, (x1 - x0) / 2.0);
                    double ry = Math.Max(1.0, (y1 - y0) / 2.0);
                    double rx2 = rx * rx;
                    double ry2 = ry * ry;

                    for (int y = y0; y < y1; y++)
                    {
                        byte* row = basePtr + y * stride;
                        double dy = y - cy;
                        double dy2 = dy * dy;
                        for (int x = x0; x < x1; x++)
                        {
                            double dx = x - cx;
                            if ((dx * dx) / rx2 + dy2 / ry2 > 1.0)
                                continue;

                            byte* p = row + x * 4;
                            p[0] = 255;
                            p[1] = 255;
                            p[2] = 255;
                            p[3] = 255;
                        }
                    }
                }
            }

            return mask;
        }

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
