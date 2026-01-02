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
                    bool canPipeline = !_options.UseTracking && _options.DetectEveryNFrames <= 1;

                    if (canPipeline && _detector is FaceOnnxDetector onnx)
                    {
                        GeneratePipelinedDetectAll(videoPath, onnx, progress, ct, startFrameIndex, totalFrames, onFrameProcessed);
                        return;
                    }

                    GenerateSequential(videoPath, progress, ct, startFrameIndex, totalFrames, onFrameProcessed);
                }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 취소는 정상 흐름으로 처리 (디버그 예외 노이즈 방지)
            }
        }

        private void GenerateSequential(
            string videoPath,
            IProgress<int>? progress,
            CancellationToken ct,
            int startFrameIndex,
            int totalFrames,
            Action<int>? onFrameProcessed)
        {
            using var extractor = new FfFrameExtractor(videoPath);

            IReadOnlyList<FaceDetectionResult>? lastFaces = null;

            int start = Math.Clamp(startFrameIndex, 0, Math.Max(0, totalFrames - 1));
            extractor.StartSequentialRead(start);

            int nextIndex = start;
            PixelSize? frameSize = null;
            bool useRaw = _detector is FaceOnnxDetector;
            FaceOnnxDetector? onnx = _detector as FaceOnnxDetector;
            while (!ct.IsCancellationRequested)
            {
                bool shouldDetect = _options.DetectEveryNFrames <= 1
                    || nextIndex % _options.DetectEveryNFrames == 0
                    || lastFaces == null;

                int idx;
                WriteableBitmap? frame = null;
                FfFrameExtractor.BgraFrame bgra = default;

                if (useRaw)
                {
                    if (!extractor.TryGetNextFrameRaw(ct, shouldDetect, out bgra, out idx))
                        break;
                }
                else
                {
                    if (!extractor.TryGetNextFrame(ct, shouldDetect, out frame, out idx))
                        break;
                }

                nextIndex = idx + 1;

                if (idx >= totalFrames)
                    break;

                onFrameProcessed?.Invoke(idx);

                if (_maskProvider.HasEntry(idx))
                {
                    ReportProgress(progress, idx, totalFrames);
                    continue;
                }

                IReadOnlyList<FaceDetectionResult> faces;

                if (shouldDetect)
                {
                    if (useRaw)
                    {
                        if (bgra.Data == IntPtr.Zero || onnx == null)
                        {
                            ReportProgress(progress, idx, totalFrames);
                            continue;
                        }

                        frameSize = new PixelSize(bgra.Width, bgra.Height);
                        faces = onnx.DetectFacesBgra(
                            bgra.Data,
                            bgra.Stride,
                            bgra.Width,
                            bgra.Height,
                            _options.DownscaleRatio,
                            _options.DownscaleQuality);
                    }
                    else
                    {
                        if (frame == null)
                        {
                            ReportProgress(progress, idx, totalFrames);
                            continue;
                        }

                        frameSize = frame.PixelSize;
                        faces = DetectFacesWithOptions(frame);
                    }

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
                {
                    ReportProgress(progress, idx, totalFrames);
                    continue;
                }

                if (frameSize.HasValue)
                {
                    _maskProvider.SetFaceRects(idx, ExtractBounds(faces), frameSize.Value);
                }

                ReportProgress(progress, idx, totalFrames);
            }

            progress?.Report(100);
        }

        private sealed class BgraBuffer
        {
            public int Index { get; init; }
            public byte[] Data { get; init; } = Array.Empty<byte>();
            public int Stride { get; init; }
            public int Width { get; init; }
            public int Height { get; init; }
        }

        private void GeneratePipelinedDetectAll(
            string videoPath,
            FaceOnnxDetector onnx,
            IProgress<int>? progress,
            CancellationToken ct,
            int startFrameIndex,
            int totalFrames,
            Action<int>? onFrameProcessed)
        {
            using var extractor = new FfFrameExtractor(videoPath);
            int start = Math.Clamp(startFrameIndex, 0, Math.Max(0, totalFrames - 1));
            extractor.StartSequentialRead(start);

            using var queue = new System.Collections.Concurrent.BlockingCollection<BgraBuffer>(2);
            var pool = System.Buffers.ArrayPool<byte>.Shared;

            var producer = Task.Run(() =>
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        if (!extractor.TryGetNextFrameRaw(ct, requireBgra: true, out var bgra, out var idx))
                            break;

                        if (idx >= totalFrames)
                            break;

                        int size = bgra.Stride * bgra.Height;
                        var buffer = pool.Rent(size);

                        unsafe
                        {
                            fixed (byte* dst = buffer)
                            {
                                byte* src = (byte*)bgra.Data;
                                int srcStride = bgra.Stride;
                                int rowBytes = bgra.Stride;
                                for (int y = 0; y < bgra.Height; y++)
                                {
                                    Buffer.MemoryCopy(
                                        src + y * srcStride,
                                        dst + y * srcStride,
                                        rowBytes,
                                        rowBytes);
                                }
                            }
                        }

                        queue.Add(new BgraBuffer
                        {
                            Index = idx,
                            Data = buffer,
                            Stride = bgra.Stride,
                            Width = bgra.Width,
                            Height = bgra.Height
                        }, ct);
                    }
                }
                finally
                {
                    queue.CompleteAdding();
                }
            }, ct);

            try
            {
                foreach (var item in queue.GetConsumingEnumerable(ct))
                {
                    if (ct.IsCancellationRequested)
                        break;

                    try
                    {
                        onFrameProcessed?.Invoke(item.Index);

                        if (!_maskProvider.HasEntry(item.Index))
                        {
                            unsafe
                            {
                                fixed (byte* src = item.Data)
                                {
                                    var faces = onnx.DetectFacesBgra(
                                        (IntPtr)src,
                                        item.Stride,
                                        item.Width,
                                        item.Height,
                                        _options.DownscaleRatio,
                                        _options.DownscaleQuality);

                                    if (faces.Count > 0)
                                    {
                                        _maskProvider.SetFaceRects(
                                            item.Index,
                                            ExtractBounds(faces),
                                            new PixelSize(item.Width, item.Height));
                                    }
                                }
                            }
                        }

                        ReportProgress(progress, item.Index, totalFrames);
                    }
                    finally
                    {
                        pool.Return(item.Data);
                    }
                }
            }
            finally
            {
                try { producer.Wait(ct); } catch { }
            }

            progress?.Report(100);
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
                        _maskProvider.SetFaceRects(frameIndex, ExtractBounds(faces), frame.PixelSize);
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

            var faces = onnx.DetectFacesDownscaled(frame, ratio, _options.DownscaleQuality);

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

        private static void ReportProgress(IProgress<int>? progress, int frameIndex, int totalFrames)
        {
            if (progress == null)
                return;

            int percent = (int)Math.Round(frameIndex * 100.0 / Math.Max(1, totalFrames - 1));
            if (percent > 100) percent = 100;
            progress.Report(percent);
        }

        private static Rect[] ExtractBounds(IReadOnlyList<FaceDetectionResult> faces)
        {
            int count = faces.Count;
            if (count == 0)
                return Array.Empty<Rect>();

            var bounds = new Rect[count];
            for (int i = 0; i < count; i++)
                bounds[i] = faces[i].Bounds;
            return bounds;
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
