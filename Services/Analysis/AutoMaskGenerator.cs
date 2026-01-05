using Avalonia;
using Avalonia.Media.Imaging;
using FaceShield.Services.FaceDetection;
using FaceShield.Services.Video;
using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        private static bool IsHardwareTransferFailure()
        {
            string status = FfFrameExtractor.GetLastDecodeStatus();
            string? error = FfFrameExtractor.GetLastDecodeError();

            if (!string.IsNullOrWhiteSpace(error) && error.Contains("av_hwframe_transfer_data 실패", StringComparison.Ordinal))
                return true;

            return !string.IsNullOrWhiteSpace(status) &&
                status.Contains("HW 프레임 전송 실패", StringComparison.Ordinal);
        }

        private static FfFrameExtractor CreateExtractorWithFallback(
            string videoPath,
            int startFrameIndex,
            bool useRaw,
            CancellationToken ct)
        {
            var extractor = new FfFrameExtractor(videoPath, enableHardware: true);

            try
            {
                extractor.StartSequentialRead(startFrameIndex);

                bool ok = useRaw
                    ? extractor.TryGetNextFrameRaw(ct, requireBgra: true, out _, out _)
                    : extractor.TryGetNextFrame(ct, requireBitmap: true, out _, out _);

                if (!ok && !ct.IsCancellationRequested && IsHardwareTransferFailure())
                {
                    Debug.WriteLine("[AutoMask] HW decode failed; falling back to SW.");
                    extractor.Dispose();
                    extractor = new FfFrameExtractor(videoPath, enableHardware: false);
                }
            }
            catch
            {
                extractor.Dispose();
                throw;
            }

            extractor.StartSequentialRead(startFrameIndex);
            return extractor;
        }
        private readonly IFaceDetector _detector;
        private readonly FrameMaskProvider _maskProvider;
        private readonly AutoMaskOptions _options;
        private readonly Func<IFaceDetector>? _detectorFactory;

        public AutoMaskGenerator(
            IFaceDetector detector,
            FrameMaskProvider maskProvider,
            AutoMaskOptions? options = null,
            Func<IFaceDetector>? detectorFactory = null)
        {
            _detector = detector ?? throw new ArgumentNullException(nameof(detector));
            _maskProvider = maskProvider ?? throw new ArgumentNullException(nameof(maskProvider));
            _options = options ?? new AutoMaskOptions();
            _detectorFactory = detectorFactory;
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
                        if (_detectorFactory != null && _options.ParallelDetectorCount > 1)
                        {
                            Debug.WriteLine($"[AutoMask] mode=pipe-parallel({_options.ParallelDetectorCount})");
                            var detectors = new List<FaceOnnxDetector> { onnx };
                            try
                            {
                                int toCreate = Math.Max(1, _options.ParallelDetectorCount) - 1;
                                for (int i = 0; i < toCreate; i++)
                                {
                                    var created = _detectorFactory();
                                    if (created is FaceOnnxDetector extra)
                                        detectors.Add(extra);
                                    else
                                    {
                                        created.Dispose();
                                        break;
                                    }
                                }

                                if (detectors.Count > 1)
                                {
                                    GeneratePipelinedDetectAllParallel(videoPath, detectors, progress, ct, startFrameIndex, totalFrames, onFrameProcessed);
                                    return;
                                }
                            }
                            finally
                            {
                                for (int i = 1; i < detectors.Count; i++)
                                    detectors[i].Dispose();
                            }
                        }

                        Debug.WriteLine("[AutoMask] mode=pipe-single");
                        GeneratePipelinedDetectAll(videoPath, onnx, progress, ct, startFrameIndex, totalFrames, onFrameProcessed);
                        return;
                    }

                    Debug.WriteLine("[AutoMask] mode=sequential");
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
            bool useRaw = _detector is FaceOnnxDetector;
            int start = Math.Clamp(startFrameIndex, 0, Math.Max(0, totalFrames - 1));
            using var extractor = CreateExtractorWithFallback(videoPath, start, useRaw, ct);

            IReadOnlyList<FaceDetectionResult>? lastFaces = null;

            int nextIndex = start;
            PixelSize? frameSize = null;
            PixelSize fullSize = extractor.FrameSize;
            FaceOnnxDetector? onnx = _detector as FaceOnnxDetector;
            bool useProxy = useRaw && _options.DownscaleRatio > 0 && _options.DownscaleRatio < 1.0;
            bool useBilinear = _options.DownscaleQuality == DownscaleQuality.BalancedBilinear;
            int proxyWidth = useProxy ? Math.Max(1, (int)Math.Round(fullSize.Width * _options.DownscaleRatio)) : fullSize.Width;
            int proxyHeight = useProxy ? Math.Max(1, (int)Math.Round(fullSize.Height * _options.DownscaleRatio)) : fullSize.Height;
            double scaleX = useProxy && proxyWidth > 0 ? (double)fullSize.Width / proxyWidth : 1.0;
            double scaleY = useProxy && proxyHeight > 0 ? (double)fullSize.Height / proxyHeight : 1.0;
            var swTotal = Stopwatch.StartNew();
            long readMs = 0;
            long detectMs = 0;
            long maskMs = 0;
            int processed = 0;
            var roiStats = new RoiDetectStats();
            while (!ct.IsCancellationRequested)
            {
                bool shouldDetect = _options.DetectEveryNFrames <= 1
                    || nextIndex % _options.DetectEveryNFrames == 0
                    || lastFaces == null;

                int idx;
                WriteableBitmap? frame = null;
                FfFrameExtractor.BgraFrame bgra = default;

                var tRead = Stopwatch.StartNew();
                if (useRaw)
                {
                    if (useProxy)
                    {
                        if (!extractor.TryGetNextFrameRawScaled(ct, shouldDetect, proxyWidth, proxyHeight, useBilinear, out bgra, out idx))
                            break;
                    }
                    else
                    {
                        if (!extractor.TryGetNextFrameRaw(ct, shouldDetect, out bgra, out idx))
                            break;
                    }
                }
                else
                {
                    if (!extractor.TryGetNextFrame(ct, shouldDetect, out frame, out idx))
                        break;
                }
                tRead.Stop();
                readMs += tRead.ElapsedMilliseconds;

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
                    var tDetect = Stopwatch.StartNew();
                    if (useRaw)
                    {
                        if (bgra.Data == IntPtr.Zero || onnx == null)
                        {
                            tDetect.Stop();
                            detectMs += tDetect.ElapsedMilliseconds;
                            ReportProgress(progress, idx, totalFrames);
                            continue;
                        }

                        frameSize = fullSize;
                        faces = DetectFacesBgraSmart(
                            onnx,
                            bgra.Data,
                            bgra.Stride,
                            bgra.Width,
                            bgra.Height,
                            useProxy,
                            _options.DownscaleRatio,
                            _options.DownscaleQuality,
                            lastFaces,
                            fullSize,
                            scaleX,
                            scaleY,
                            roiStats);
                    }
                    else
                    {
                        if (frame == null)
                        {
                            tDetect.Stop();
                            detectMs += tDetect.ElapsedMilliseconds;
                            ReportProgress(progress, idx, totalFrames);
                            continue;
                        }

                        frameSize = frame.PixelSize;
                        faces = DetectFacesWithOptions(frame);
                    }
                    tDetect.Stop();
                    detectMs += tDetect.ElapsedMilliseconds;

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
                    var tMask = Stopwatch.StartNew();
                    _maskProvider.SetFaceRects(idx, ExtractBounds(faces), frameSize.Value);
                    tMask.Stop();
                    maskMs += tMask.ElapsedMilliseconds;
                }

                ReportProgress(progress, idx, totalFrames);
                processed++;

                if (processed % 60 == 0)
                {
                    Debug.WriteLine(
                        $"[AutoMask] frames={processed}, readMs={readMs}, detectMs={detectMs}, maskMs={maskMs}, totalMs={swTotal.ElapsedMilliseconds}, roi={roiStats.BuildSummary()}");
                }
            }

            progress?.Report(100);
            Debug.WriteLine(
                $"[AutoMask] done frames={processed}, readMs={readMs}, detectMs={detectMs}, maskMs={maskMs}, totalMs={swTotal.ElapsedMilliseconds}, roi={roiStats.BuildSummary()}");
        }

        private sealed class BgraBuffer
        {
            public int Index { get; init; }
            public byte[] Data { get; init; } = Array.Empty<byte>();
            public int Stride { get; init; }
            public int Width { get; init; }
            public int Height { get; init; }
        }

        private sealed class DetectionResult
        {
            public int Index { get; init; }
            public Rect[] Bounds { get; init; } = Array.Empty<Rect>();
            public PixelSize Size { get; init; }
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
            int start = Math.Clamp(startFrameIndex, 0, Math.Max(0, totalFrames - 1));
            using var extractor = CreateExtractorWithFallback(videoPath, start, useRaw: true, ct);

            PixelSize fullSize = extractor.FrameSize;
            bool useProxy = _options.DownscaleRatio > 0 && _options.DownscaleRatio < 1.0;
            bool useBilinear = _options.DownscaleQuality == DownscaleQuality.BalancedBilinear;
            int proxyWidth = useProxy ? Math.Max(1, (int)Math.Round(fullSize.Width * _options.DownscaleRatio)) : fullSize.Width;
            int proxyHeight = useProxy ? Math.Max(1, (int)Math.Round(fullSize.Height * _options.DownscaleRatio)) : fullSize.Height;
            double scaleX = useProxy && proxyWidth > 0 ? (double)fullSize.Width / proxyWidth : 1.0;
            double scaleY = useProxy && proxyHeight > 0 ? (double)fullSize.Height / proxyHeight : 1.0;

            using var queue = new System.Collections.Concurrent.BlockingCollection<BgraBuffer>(4);
            using var results = new System.Collections.Concurrent.BlockingCollection<DetectionResult>(8);
            var pool = System.Buffers.ArrayPool<byte>.Shared;
            long decodeMs = 0;
            long detectMs = 0;
            int processed = 0;
            var swTotal = Stopwatch.StartNew();
            IReadOnlyList<FaceDetectionResult>? lastFaces = null;
            var roiStats = new RoiDetectStats();

            var producer = Task.Run(() =>
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var tDecode = Stopwatch.StartNew();
                        int targetW = useProxy ? proxyWidth : fullSize.Width;
                        int targetH = useProxy ? proxyHeight : fullSize.Height;
                        int stride;
                        int idx;
                        int size = targetW * 4 * targetH;
                        var buffer = pool.Rent(size);

                        bool ok = extractor.TryGetNextFrameRawToBuffer(
                            ct,
                            targetW,
                            targetH,
                            useBilinear,
                            buffer,
                            out idx,
                            out stride);
                        tDecode.Stop();
                        decodeMs += tDecode.ElapsedMilliseconds;
                        if (!ok)
                        {
                            pool.Return(buffer);
                            break;
                        }

                        if (idx >= totalFrames)
                        {
                            pool.Return(buffer);
                            break;
                        }

                        try
                        {
                            queue.Add(new BgraBuffer
                            {
                                Index = idx,
                                Data = buffer,
                                Stride = stride,
                                Width = targetW,
                                Height = targetH
                            }, ct);
                        }
                        catch (OperationCanceledException)
                        {
                            pool.Return(buffer);
                            break;
                        }
                    }
                }
                finally
                {
                    queue.CompleteAdding();
                }
            }, ct);

            var writer = Task.Run(() =>
            {
                foreach (var result in results.GetConsumingEnumerable())
                {
                    if (ct.IsCancellationRequested)
                        break;

                    onFrameProcessed?.Invoke(result.Index);
                    if (result.Bounds.Length > 0)
                        _maskProvider.SetFaceRects(result.Index, result.Bounds, result.Size);

                    ReportProgress(progress, result.Index, totalFrames);
                    processed++;
                    if (processed % 60 == 0)
                    {
                        Debug.WriteLine(
                            $"[AutoMaskPipe] frames={processed}, decodeMs={decodeMs}, detectMs={detectMs}, totalMs={swTotal.ElapsedMilliseconds}, roi={roiStats.BuildSummary()}");
                    }
                }
            }, ct);

            try
            {
                foreach (var item in queue.GetConsumingEnumerable())
                {
                    if (ct.IsCancellationRequested)
                        break;

                    try
                    {
                        Rect[] bounds = Array.Empty<Rect>();
                        PixelSize resultSize = useProxy ? fullSize : new PixelSize(item.Width, item.Height);

                        if (!_maskProvider.HasEntry(item.Index))
                        {
                            unsafe
                            {
                                fixed (byte* src = item.Data)
                                {
                                    var tDetect = Stopwatch.StartNew();
                                    var faces = DetectFacesBgraSmart(
                                        onnx,
                                        (IntPtr)src,
                                        item.Stride,
                                        item.Width,
                                        item.Height,
                                        useProxy,
                                        _options.DownscaleRatio,
                                        _options.DownscaleQuality,
                                        lastFaces,
                                        fullSize,
                                        scaleX,
                                        scaleY,
                                        roiStats);
                                    tDetect.Stop();
                                    detectMs += tDetect.ElapsedMilliseconds;

                                    if (faces.Count > 0)
                                    {
                                        bounds = ExtractBounds(faces);
                                        lastFaces = faces;
                                    }
                                }
                            }
                        }

                        try
                        {
                            results.Add(new DetectionResult
                            {
                                Index = item.Index,
                                Bounds = bounds,
                                Size = resultSize
                            }, ct);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                    finally
                    {
                        pool.Return(item.Data);
                    }
                }
            }
            finally
            {
                results.CompleteAdding();
                try { producer.Wait(ct); } catch { }
                try { writer.Wait(ct); } catch { }
            }

            progress?.Report(100);
            Debug.WriteLine(
                $"[AutoMaskPipe] done frames={processed}, decodeMs={decodeMs}, detectMs={detectMs}, totalMs={swTotal.ElapsedMilliseconds}, roi={roiStats.BuildSummary()}");
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

        private void GeneratePipelinedDetectAllParallel(
            string videoPath,
            IReadOnlyList<FaceOnnxDetector> detectors,
            IProgress<int>? progress,
            CancellationToken ct,
            int startFrameIndex,
            int totalFrames,
            Action<int>? onFrameProcessed)
        {
            int start = Math.Clamp(startFrameIndex, 0, Math.Max(0, totalFrames - 1));
            using var extractor = CreateExtractorWithFallback(videoPath, start, useRaw: true, ct);

            PixelSize fullSize = extractor.FrameSize;
            bool useProxy = _options.DownscaleRatio > 0 && _options.DownscaleRatio < 1.0;
            bool useBilinear = _options.DownscaleQuality == DownscaleQuality.BalancedBilinear;
            int proxyWidth = useProxy ? Math.Max(1, (int)Math.Round(fullSize.Width * _options.DownscaleRatio)) : fullSize.Width;
            int proxyHeight = useProxy ? Math.Max(1, (int)Math.Round(fullSize.Height * _options.DownscaleRatio)) : fullSize.Height;
            double scaleX = useProxy && proxyWidth > 0 ? (double)fullSize.Width / proxyWidth : 1.0;
            double scaleY = useProxy && proxyHeight > 0 ? (double)fullSize.Height / proxyHeight : 1.0;

            int queueDepth = Math.Max(4, detectors.Count * 3);
            using var queue = new System.Collections.Concurrent.BlockingCollection<BgraBuffer>(queueDepth);
            using var results = new System.Collections.Concurrent.BlockingCollection<DetectionResult>(queueDepth * 2);
            var pool = System.Buffers.ArrayPool<byte>.Shared;
            long decodeMs = 0;
            long detectMs = 0;
            int processed = 0;
            var swTotal = Stopwatch.StartNew();

            var producer = Task.Run(() =>
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var tDecode = Stopwatch.StartNew();
                        int targetW = useProxy ? proxyWidth : fullSize.Width;
                        int targetH = useProxy ? proxyHeight : fullSize.Height;
                        int stride;
                        int idx;
                        int size = targetW * 4 * targetH;
                        var buffer = pool.Rent(size);

                        bool ok = extractor.TryGetNextFrameRawToBuffer(
                            ct,
                            targetW,
                            targetH,
                            useBilinear,
                            buffer,
                            out idx,
                            out stride);
                        tDecode.Stop();
                        Interlocked.Add(ref decodeMs, tDecode.ElapsedMilliseconds);
                        if (!ok)
                        {
                            pool.Return(buffer);
                            break;
                        }

                        if (idx >= totalFrames)
                        {
                            pool.Return(buffer);
                            break;
                        }

                        try
                        {
                            queue.Add(new BgraBuffer
                            {
                                Index = idx,
                                Data = buffer,
                                Stride = stride,
                                Width = targetW,
                                Height = targetH
                            }, ct);
                        }
                        catch (OperationCanceledException)
                        {
                            pool.Return(buffer);
                            break;
                        }
                    }
                }
                finally
                {
                    queue.CompleteAdding();
                }
            }, ct);

            var consumers = new List<Task>(detectors.Count);
            foreach (var detector in detectors)
            {
                consumers.Add(Task.Run(() =>
                {
                    foreach (var item in queue.GetConsumingEnumerable())
                    {
                        if (ct.IsCancellationRequested)
                            break;

                        try
                        {
                            Rect[] bounds = Array.Empty<Rect>();
                            PixelSize resultSize = useProxy ? fullSize : new PixelSize(item.Width, item.Height);

                            if (!_maskProvider.HasEntry(item.Index))
                            {
                                unsafe
                                {
                                    fixed (byte* src = item.Data)
                                    {
                                        var tDetect = Stopwatch.StartNew();
                                        var faces = DetectFacesBgraSmart(
                                            detector,
                                            (IntPtr)src,
                                            item.Stride,
                                            item.Width,
                                            item.Height,
                                            useProxy,
                                            _options.DownscaleRatio,
                                            _options.DownscaleQuality,
                                            lastFacesFull: null,
                                            fullSize,
                                            scaleX,
                                            scaleY,
                                            stats: null);
                                        tDetect.Stop();
                                        Interlocked.Add(ref detectMs, tDetect.ElapsedMilliseconds);

                                        if (faces.Count > 0)
                                            bounds = ExtractBounds(faces);
                                    }
                                }
                            }

                            try
                            {
                                results.Add(new DetectionResult
                                {
                                    Index = item.Index,
                                    Bounds = bounds,
                                    Size = resultSize
                                }, ct);
                            }
                            catch (OperationCanceledException)
                            {
                                break;
                            }
                        }
                        finally
                        {
                            pool.Return(item.Data);
                        }
                    }
                }, ct));
            }

            var writer = Task.Run(() =>
            {
                foreach (var result in results.GetConsumingEnumerable())
                {
                    if (ct.IsCancellationRequested)
                        break;

                    onFrameProcessed?.Invoke(result.Index);
                    if (result.Bounds.Length > 0)
                        _maskProvider.SetFaceRects(result.Index, result.Bounds, result.Size);

                    ReportProgress(progress, result.Index, totalFrames);
                    int done = Interlocked.Increment(ref processed);
                    if (done % 60 == 0)
                    {
                        Debug.WriteLine(
                            $"[AutoMaskPipe] frames={done}, decodeMs={decodeMs}, detectMs={detectMs}, totalMs={swTotal.ElapsedMilliseconds}");
                    }
                }
            }, ct);

            try
            {
                Task.WaitAll(consumers.ToArray());
            }
            catch
            {
                // ignore cancellation
            }
            finally
            {
                results.CompleteAdding();
                try { producer.Wait(ct); } catch { }
                try { writer.Wait(ct); } catch { }
            }

            progress?.Report(100);
            Debug.WriteLine(
                $"[AutoMaskPipe] done frames={processed}, decodeMs={decodeMs}, detectMs={detectMs}, totalMs={swTotal.ElapsedMilliseconds}");
        }

        private static IReadOnlyList<FaceDetectionResult> DetectFacesBgraSmart(
            FaceOnnxDetector onnx,
            IntPtr data,
            int stride,
            int width,
            int height,
            bool useProxy,
            double downscaleRatio,
            DownscaleQuality quality,
            IReadOnlyList<FaceDetectionResult>? lastFacesFull,
            PixelSize fullSize,
            double scaleX,
            double scaleY,
            RoiDetectStats? stats)
        {
            if (data == IntPtr.Zero || width <= 0 || height <= 0)
                return Array.Empty<FaceDetectionResult>();

            if (lastFacesFull != null && lastFacesFull.Count > 0)
            {
                var roiFull = BuildRoiFromFaces(lastFacesFull, fullSize);
                var roi = useProxy
                    ? new Rect(roiFull.X / scaleX, roiFull.Y / scaleY, roiFull.Width / scaleX, roiFull.Height / scaleY)
                    : roiFull;
                roi = ClampRectToSize(roi, width, height);

                if (roi.Width >= 64 && roi.Height >= 64 &&
                    !(roi.Width >= width * 0.9 && roi.Height >= height * 0.9))
                {
                    stats?.AddAttempt(roi.Width * roi.Height);
                    int rx = (int)Math.Floor(roi.X);
                    int ry = (int)Math.Floor(roi.Y);
                    int rw = (int)Math.Ceiling(roi.Width);
                    int rh = (int)Math.Ceiling(roi.Height);
                    if (rx < 0) rx = 0;
                    if (ry < 0) ry = 0;
                    if (rx + rw > width) rw = width - rx;
                    if (ry + rh > height) rh = height - ry;

                    if (rw > 0 && rh > 0)
                    {
                        IntPtr roiPtr = IntPtr.Add(data, ry * stride + rx * 4);
                        var faces = onnx.DetectFacesBgra(
                            roiPtr,
                            stride,
                            rw,
                            rh,
                            useProxy ? 1.0 : downscaleRatio,
                            quality);

                        if (faces.Count > 0)
                        {
                            stats?.AddHit();
                            faces = OffsetFaces(faces, rx, ry);
                            if (useProxy)
                                faces = ScaleFaces(faces, scaleX, scaleY);
                            return faces;
                        }
                    }
                }
            }

            stats?.AddFallback();
            var fullFaces = onnx.DetectFacesBgra(
                data,
                stride,
                width,
                height,
                useProxy ? 1.0 : downscaleRatio,
                quality);

            if (useProxy)
                fullFaces = ScaleFaces(fullFaces, scaleX, scaleY);

            return fullFaces;
        }

        private static Rect BuildRoiFromFaces(IReadOnlyList<FaceDetectionResult> faces, PixelSize fullSize)
        {
            double minX = double.MaxValue;
            double minY = double.MaxValue;
            double maxX = double.MinValue;
            double maxY = double.MinValue;

            foreach (var f in faces)
            {
                var b = f.Bounds;
                minX = Math.Min(minX, b.X);
                minY = Math.Min(minY, b.Y);
                maxX = Math.Max(maxX, b.X + b.Width);
                maxY = Math.Max(maxY, b.Y + b.Height);
            }

            if (minX == double.MaxValue || minY == double.MaxValue)
                return new Rect(0, 0, fullSize.Width, fullSize.Height);

            double w = Math.Max(1, maxX - minX);
            double h = Math.Max(1, maxY - minY);
            double padX = Math.Max(w * 0.35, 32);
            double padY = Math.Max(h * 0.35, 32);

            double x = Math.Max(0, minX - padX);
            double y = Math.Max(0, minY - padY);
            double right = Math.Min(fullSize.Width, maxX + padX);
            double bottom = Math.Min(fullSize.Height, maxY + padY);

            double roiW = right - x;
            double roiH = bottom - y;

            const double minSize = 64;
            if (roiW < minSize)
            {
                double half = (minSize - roiW) / 2;
                x = Math.Max(0, x - half);
                right = Math.Min(fullSize.Width, x + minSize);
                x = Math.Max(0, right - minSize);
                roiW = right - x;
            }
            if (roiH < minSize)
            {
                double half = (minSize - roiH) / 2;
                y = Math.Max(0, y - half);
                bottom = Math.Min(fullSize.Height, y + minSize);
                y = Math.Max(0, bottom - minSize);
                roiH = bottom - y;
            }

            return new Rect(x, y, roiW, roiH);
        }

        private static Rect ClampRectToSize(Rect rect, int width, int height)
        {
            double x = Math.Max(0, rect.X);
            double y = Math.Max(0, rect.Y);
            double right = Math.Min(width, rect.X + rect.Width);
            double bottom = Math.Min(height, rect.Y + rect.Height);
            double w = Math.Max(0, right - x);
            double h = Math.Max(0, bottom - y);
            return new Rect(x, y, w, h);
        }

        private static IReadOnlyList<FaceDetectionResult> OffsetFaces(
            IReadOnlyList<FaceDetectionResult> faces,
            double offsetX,
            double offsetY)
        {
            if (faces.Count == 0)
                return faces;

            var shifted = new List<FaceDetectionResult>(faces.Count);
            foreach (var f in faces)
            {
                var b = f.Bounds;
                shifted.Add(new FaceDetectionResult
                {
                    Bounds = new Rect(b.X + offsetX, b.Y + offsetY, b.Width, b.Height),
                    Confidence = f.Confidence
                });
            }
            return shifted;
        }

        private sealed class RoiDetectStats
        {
            private long _attempts;
            private long _hits;
            private long _fallbacks;
            private long _areaSum;

            public void AddAttempt(double area)
            {
                Interlocked.Increment(ref _attempts);
                Interlocked.Add(ref _areaSum, (long)Math.Round(area));
            }

            public void AddHit()
            {
                Interlocked.Increment(ref _hits);
            }

            public void AddFallback()
            {
                Interlocked.Increment(ref _fallbacks);
            }

            public string BuildSummary()
            {
                long attempts = Interlocked.Read(ref _attempts);
                long hits = Interlocked.Read(ref _hits);
                long fallbacks = Interlocked.Read(ref _fallbacks);
                long areaSum = Interlocked.Read(ref _areaSum);
                double avgArea = attempts > 0 ? (double)areaSum / attempts : 0;
                return $"attempts={attempts}, hits={hits}, fallbacks={fallbacks}, avgArea={avgArea:F0}";
            }
        }

        private static IReadOnlyList<FaceDetectionResult> ScaleFaces(
            IReadOnlyList<FaceDetectionResult> faces,
            double scaleX,
            double scaleY)
        {
            if (faces.Count == 0)
                return faces;
            if (Math.Abs(scaleX - 1.0) < 0.0001 && Math.Abs(scaleY - 1.0) < 0.0001)
                return faces;

            var scaled = new List<FaceDetectionResult>(faces.Count);
            foreach (var f in faces)
            {
                var b = f.Bounds;
                scaled.Add(new FaceDetectionResult
                {
                    Bounds = new Rect(b.X * scaleX, b.Y * scaleY, b.Width * scaleX, b.Height * scaleY),
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
