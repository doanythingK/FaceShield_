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
        private const double MinFaceAreaRatio = 0.00075;
        private const double MinSmallFaceAreaRatio = 0.00025;
        private const double MinFaceAspectRatio = 0.5;
        private const double MaxFaceAspectRatio = 2.0;
        private const float SmallFaceConfidenceMin = 0.72f;
        private const double MinSkinRatio = 0.013;
        private const double MinEdgeRatio = 0.016;
        private const double MinLumaVariance = 160.0;
        private const float StatsBypassConfidence = 0.80f;
        private const double OffModeYoloMinSmallFaceAreaRatio = 0.00035;
        private const float OffModeYoloSmallFaceConfidenceMin = 0.60f;
        private const double OffModeYoloMaxFaceAspectRatio = 2.25;
        private const int StatsSampleStep = 5;
        private const int MinStatsSamples = 16;
        private const double SparseTrackIouMin = 0.12;
        private const double SparseTrackMaxCenterShiftRatio = 1.2;
        private const double SparseTrackMaxAreaChangeRatio = 4.0;
        private const double SparseSceneCutDifferenceThreshold = 0.32;
        private const double OffModeSparseSceneCutDifferenceThreshold = 0.18;
        private const int SparseSceneCutSignatureColumns = 24;
        private const int SparseSceneCutSignatureRows = 14;
        private const double OffModeSceneCutSignatureDiffThreshold = 0.24;
        private const int OffModeSceneCutCarryClearFrames = 2;

        private readonly record struct FaceFilterSettings(
            double MinFaceAreaRatio,
            double MinSmallFaceAreaRatio,
            double MinFaceAspectRatio,
            double MaxFaceAspectRatio,
            float SmallFaceConfidenceMin,
            bool UseStatsFilter);

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
        private readonly IFaceDetectorFactory? _detectorFactory;
        private double _sourceFpsForSummary;

        public AutoMaskRunSummary? LastRunSummary { get; private set; }

        public AutoMaskGenerator(
            IFaceDetector detector,
            FrameMaskProvider maskProvider,
            AutoMaskOptions? options = null,
            IFaceDetectorFactory? detectorFactory = null)
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

            _sourceFpsForSummary = fps;

            LastRunSummary = null;

            try
            {
                await Task.Run(() =>
                {
                    bool canPipeline = _options.DetectEveryNFrames <= 1;
                    bool canSparsePipeline = _options.UseTracking &&
                        _options.DetectEveryNFrames > 1 &&
                        _detector is IBgraFaceDetector &&
                        _detectorFactory != null &&
                        _options.ParallelDetectorCount >= 1;

                    if (canPipeline && _detector is IBgraFaceDetector bgraDetector)
                    {
                        if (_detectorFactory != null && _options.ParallelDetectorCount > 1)
                        {
                            Debug.WriteLine($"[AutoMask] mode=pipe-parallel({_options.ParallelDetectorCount})");
                            var detectors = new List<IBgraFaceDetector> { bgraDetector };
                            try
                            {
                                int toCreate = Math.Max(1, _options.ParallelDetectorCount) - 1;
                                for (int i = 0; i < toCreate; i++)
                                {
                                    var created = _detectorFactory.CreateDetector();
                                    if (created is IBgraFaceDetector extra)
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
                        GeneratePipelinedDetectAll(videoPath, bgraDetector, progress, ct, startFrameIndex, totalFrames, onFrameProcessed);
                        return;
                    }

                    if (canSparsePipeline && _detector is IBgraFaceDetector sparseBgraDetector)
                    {
                        Debug.WriteLine($"[AutoMask] mode=sparse-pipe-parallel({_options.ParallelDetectorCount})");
                        var detectors = new List<IBgraFaceDetector> { sparseBgraDetector };
                        try
                        {
                            int toCreate = Math.Max(1, _options.ParallelDetectorCount) - 1;
                            for (int i = 0; i < toCreate; i++)
                            {
                                var created = _detectorFactory!.CreateDetector();
                                if (created is IBgraFaceDetector extra)
                                    detectors.Add(extra);
                                else
                                {
                                    created.Dispose();
                                    break;
                                }
                            }

                            GenerateSparsePipelinedTrackingParallel(videoPath, detectors, progress, ct, startFrameIndex, totalFrames, onFrameProcessed);
                            return;
                        }
                        finally
                        {
                            for (int i = 1; i < detectors.Count; i++)
                                detectors[i].Dispose();
                        }
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
            bool useRaw = _detector is IBgraFaceDetector;
            int start = Math.Clamp(startFrameIndex, 0, Math.Max(0, totalFrames - 1));
            using var extractor = CreateExtractorWithFallback(videoPath, start, useRaw, ct);

            IReadOnlyList<FaceDetectionResult>? lastFaces = null;

            int nextIndex = start;
            PixelSize? frameSize = null;
            var geometry = CreateDetectionGeometry(extractor.FrameSize);
            PixelSize fullSize = geometry.FullSize;
            IBgraFaceDetector? bgraDetector = _detector as IBgraFaceDetector;
            bool useProxy = useRaw && geometry.UseProxy;
            bool useBilinear = geometry.UseBilinear;
            int proxyWidth = geometry.TargetWidth;
            int proxyHeight = geometry.TargetHeight;
            double scaleX = geometry.ScaleX;
            double scaleY = geometry.ScaleY;
            var swTotal = Stopwatch.StartNew();
            long readMs = 0;
            long detectMs = 0;
            long maskMs = 0;
            int processed = 0;
            var roiStats = new RoiDetectStats();
            var filterStats = new FaceFilterStats();
            var progressState = new ProgressState();
            double[]? previousSceneSignature = null;
            bool forceDetectAfterSceneCut = false;
            while (!ct.IsCancellationRequested)
            {
                bool shouldDetect = _options.DetectEveryNFrames <= 1
                    || nextIndex % _options.DetectEveryNFrames == 0
                    || forceDetectAfterSceneCut
                    || lastFaces == null;
                forceDetectAfterSceneCut = false;

                bool forceSceneCutDetection = _options.FilterProfile == FaceFilterProfile.Yolo &&
                    !_options.EnablePostProcessing &&
                    _options.UseTracking &&
                    lastFaces != null;

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

                if (forceSceneCutDetection)
                {
                    double[]? currentSceneSignature = null;
                    if (useRaw)
                    {
                        if (bgra.Data != IntPtr.Zero)
                        {
                            unsafe
                            {
                                currentSceneSignature = ComputeFrameSignature(
                                    (byte*)bgra.Data,
                                    bgra.Stride,
                                    bgra.Width,
                                    bgra.Height);
                            }
                        }
                    }
                    else if (frame != null)
                    {
                        using var fb = frame.Lock();
                        unsafe
                        {
                            currentSceneSignature = ComputeFrameSignature(
                                (byte*)fb.Address,
                                fb.RowBytes,
                                frame.PixelSize.Width,
                                frame.PixelSize.Height);
                        }
                    }

                    if (previousSceneSignature != null &&
                        currentSceneSignature != null &&
                        currentSceneSignature.Length > 0 &&
                        previousSceneSignature.Length > 0 &&
                        ComputeSignatureDifference(currentSceneSignature, previousSceneSignature) >= OffModeSceneCutSignatureDiffThreshold)
                    {
                        shouldDetect = true;
                        forceDetectAfterSceneCut = true;
                        lastFaces = null;

                        int clearRadius = Math.Max(0, _options.DetectEveryNFrames);
                        int clearStart = Math.Max(0, idx - OffModeSceneCutCarryClearFrames);
                        int clearEnd = Math.Min(totalFrames, idx + clearRadius + OffModeSceneCutCarryClearFrames + 1);
                        int clearBeforeStart = clearStart;
                        int clearBeforeEnd = Math.Min(totalFrames, idx);
                        int clearAfterStart = idx;
                        int clearAfterEnd = clearEnd;
                        int removed = _maskProvider.RemoveFaceMasksRange(clearStart, clearEnd);
                        Debug.WriteLine(
                            $"[AutoMask] scene-cut reset idx={idx} clearFrom={clearStart} clearTo={clearEnd} removed={removed} clearRadius={clearRadius} offModeRadius={OffModeSceneCutCarryClearFrames} diff={ComputeSignatureDifference(currentSceneSignature, previousSceneSignature):0.###} rebuildBefore={clearBeforeStart}:{clearBeforeEnd} rebuildAfter={clearAfterStart}:{clearAfterEnd} phase=off");
                    }

                    previousSceneSignature = currentSceneSignature;
                }

                bool hadExisting = _maskProvider.HasEntry(idx);
                if (hadExisting && !shouldDetect)
                {
                    ReportProgress(progress, idx, totalFrames, progressState);
                    continue;
                }

                if (hadExisting)
                    _maskProvider.RemoveFaceMask(idx);

                IReadOnlyList<FaceDetectionResult> faces;

                if (shouldDetect)
                {
                    var tDetect = Stopwatch.StartNew();
                    if (useRaw)
                    {
                        if (bgra.Data == IntPtr.Zero || bgraDetector == null)
                        {
                            tDetect.Stop();
                            detectMs += tDetect.ElapsedMilliseconds;
                            ReportProgress(progress, idx, totalFrames, progressState);
                            continue;
                        }

                        frameSize = fullSize;
                        faces = DetectFacesBgraSmart(
                            bgraDetector,
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
                            ReportProgress(progress, idx, totalFrames, progressState);
                            continue;
                        }

                        frameSize = frame.PixelSize;
                        faces = DetectFacesWithOptions(frame);
                    }
                    tDetect.Stop();
                    detectMs += tDetect.ElapsedMilliseconds;

                    if (faces.Count > 0)
                    {
                        int rawFaceCount = faces.Count;
                        if (useRaw && bgra.Data != IntPtr.Zero)
                        {
                            unsafe
                            {
                                byte* src = (byte*)bgra.Data;
                                faces = FilterFacesByAreaAndStats(
                                    faces,
                                    fullSize,
                                    src,
                                    bgra.Stride,
                                    bgra.Width,
                                    bgra.Height,
                                    scaleX,
                                    scaleY,
                                    GetFaceFilterSettings(_options.FilterProfile, !_options.EnablePostProcessing),
                                    filterStats);
                            }
                        }
                        else if (!useRaw && frame != null)
                        {
                            using var fb = frame.Lock();
                            unsafe
                            {
                                byte* src = (byte*)fb.Address;
                                faces = FilterFacesByAreaAndStats(
                                    faces,
                                    frame.PixelSize,
                                    src,
                                    fb.RowBytes,
                                    frame.PixelSize.Width,
                                    frame.PixelSize.Height,
                                    1.0,
                                    1.0,
                                    GetFaceFilterSettings(_options.FilterProfile, !_options.EnablePostProcessing),
                                    filterStats);
                            }
                        }
                        else
                        {
                            faces = FilterFacesByArea(faces, fullSize, GetFaceFilterSettings(_options.FilterProfile, !_options.EnablePostProcessing), filterStats);
                        }

                        WriteDetectionDiagnostics(idx, rawFaceCount, faces, fullSize);
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
                    ReportProgress(progress, idx, totalFrames, progressState);
                    continue;
                }

                if (frameSize.HasValue)
                {
                    var payload = BuildMaskPayload(faces);
                    var tMask = Stopwatch.StartNew();
                    _maskProvider.SetFaceRects(
                        idx,
                        payload.Bounds,
                        frameSize.Value,
                        payload.MinConfidence,
                        payload.Confidences);
                    tMask.Stop();
                    maskMs += tMask.ElapsedMilliseconds;
                }

                ReportProgress(progress, idx, totalFrames, progressState);
                processed++;

                if (processed % 60 == 0)
                {
                    Debug.WriteLine(
                        $"[AutoMask] frames={processed}, readMs={readMs}, detectMs={detectMs}, maskMs={maskMs}, totalMs={swTotal.ElapsedMilliseconds}, roi={BuildDetectionSummary(roiStats, filterStats)}");
                }
            }

            progress?.Report(100);
            Debug.WriteLine(
                $"[AutoMask] done frames={processed}, readMs={readMs}, detectMs={detectMs}, maskMs={maskMs}, totalMs={swTotal.ElapsedMilliseconds}, roi={BuildDetectionSummary(roiStats, filterStats)}");
            SetLastRunSummary(new AutoMaskRunSummary(
                "sequential",
                totalFrames,
                start,
                processed,
                processed,
                processed,
                0,
                readMs,
                0,
                detectMs,
                maskMs,
                0,
                swTotal.ElapsedMilliseconds,
                _options.DownscaleRatio,
                _options.DownscaleQuality,
                _options.UseTracking,
                _options.DetectEveryNFrames,
                _options.ParallelDetectorCount,
                BuildDetectionSummary(roiStats, filterStats),
                _sourceFpsForSummary,
                _options.RunId,
                GetDetectorName()));
            ApplyPostProcessResultToRunSummary(
                RunAutoPostProcessIfNeeded(videoPath, totalFrames, ct));
        }

        private sealed class BgraBuffer
        {
            public int Index { get; init; }
            public byte[] Data { get; init; } = Array.Empty<byte>();
            public int Stride { get; init; }
            public int Width { get; init; }
            public int Height { get; init; }
            public double[] SceneSignature { get; init; } = Array.Empty<double>();
        }

        private sealed class DetectionResult
        {
            public int Index { get; init; }
            public Rect[] Bounds { get; init; } = Array.Empty<Rect>();
            public PixelSize Size { get; init; }
            public float? MinConfidence { get; init; }
            public float[] Confidences { get; init; } = Array.Empty<float>();
            public double[] FrameSignature { get; init; } = Array.Empty<double>();
        }

        private readonly record struct SparseSceneCutTransition(int SourceFrameIndex, int NextFrameIndex);

        private readonly record struct SparseMaterializeResult(
            int Interpolated,
            int SceneCutStops,
            IReadOnlyList<SparseSceneCutTransition> SceneCutTransitions);

        private sealed class ProgressState
        {
            public int LastPercent = -1;
        }

        private readonly record struct DetectionGeometry(
            PixelSize FullSize,
            bool UseProxy,
            bool UseBilinear,
            int TargetWidth,
            int TargetHeight,
            double ScaleX,
            double ScaleY);

        private DetectionGeometry CreateDetectionGeometry(PixelSize fullSize)
        {
            bool useProxy = _options.DownscaleRatio > 0 && _options.DownscaleRatio < 1.0;
            bool useBilinear = _options.DownscaleQuality == DownscaleQuality.BalancedBilinear;
            int targetWidth = useProxy
                ? Math.Max(1, (int)Math.Round(fullSize.Width * _options.DownscaleRatio))
                : fullSize.Width;
            int targetHeight = useProxy
                ? Math.Max(1, (int)Math.Round(fullSize.Height * _options.DownscaleRatio))
                : fullSize.Height;
            double scaleX = useProxy && targetWidth > 0 ? (double)fullSize.Width / targetWidth : 1.0;
            double scaleY = useProxy && targetHeight > 0 ? (double)fullSize.Height / targetHeight : 1.0;

            return new DetectionGeometry(fullSize, useProxy, useBilinear, targetWidth, targetHeight, scaleX, scaleY);
        }

        private void GeneratePipelinedDetectAll(
            string videoPath,
            IBgraFaceDetector detector,
            IProgress<int>? progress,
            CancellationToken ct,
            int startFrameIndex,
            int totalFrames,
            Action<int>? onFrameProcessed)
        {
            int start = Math.Clamp(startFrameIndex, 0, Math.Max(0, totalFrames - 1));
            using var extractor = CreateExtractorWithFallback(videoPath, start, useRaw: true, ct);

            var geometry = CreateDetectionGeometry(extractor.FrameSize);
            PixelSize fullSize = geometry.FullSize;
            bool useProxy = geometry.UseProxy;
            bool useBilinear = geometry.UseBilinear;
            int proxyWidth = geometry.TargetWidth;
            int proxyHeight = geometry.TargetHeight;
            double scaleX = geometry.ScaleX;
            double scaleY = geometry.ScaleY;

            using var queue = new System.Collections.Concurrent.BlockingCollection<BgraBuffer>(4);
            using var results = new System.Collections.Concurrent.BlockingCollection<DetectionResult>(8);
            var pool = System.Buffers.ArrayPool<byte>.Shared;
            long decodeMs = 0;
            long detectMs = 0;
            int processed = 0;
            var swTotal = Stopwatch.StartNew();
            var progressState = new ProgressState();
            IReadOnlyList<FaceDetectionResult>? lastFaces = null;
            var roiStats = new RoiDetectStats();
            var filterStats = new FaceFilterStats();
            bool applyOffModeSceneCutReset = _options.FilterProfile == FaceFilterProfile.Yolo
                && !_options.EnablePostProcessing
                && _options.UseTracking;

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

                        double[] sceneSignature = Array.Empty<double>();
                        if (applyOffModeSceneCutReset)
                        {
                            unsafe
                            {
                                fixed (byte* src = buffer)
                                {
                                    sceneSignature = ComputeFrameSignature(src, stride, targetW, targetH);
                                }
                            }
                        }

                        try
                        {
                            queue.Add(new BgraBuffer
                            {
                                Index = idx,
                                Data = buffer,
                                Stride = stride,
                                Width = targetW,
                                Height = targetH,
                                SceneSignature = sceneSignature
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
                double[]? previousSceneSignature = null;
                int sceneCutClearUntilExclusive = int.MinValue;
                int lastSceneCutFrame = -1;

                foreach (var result in results.GetConsumingEnumerable())
                {
                    if (ct.IsCancellationRequested)
                        break;

                    bool isSceneCut = false;
                    int clearStart = 0;
                    int clearEnd = 0;
                    if (applyOffModeSceneCutReset &&
                        previousSceneSignature != null &&
                        result.FrameSignature.Length > 0 &&
                        previousSceneSignature.Length > 0)
                    {
                        double diff = ComputeSignatureDifference(result.FrameSignature, previousSceneSignature);
                        if (diff >= OffModeSceneCutSignatureDiffThreshold)
                        {
                            isSceneCut = true;
                            int clearRadius = Math.Max(0, _options.DetectEveryNFrames);
                            clearStart = Math.Max(0, result.Index - OffModeSceneCutCarryClearFrames);
                            clearEnd = Math.Min(totalFrames, result.Index + clearRadius + OffModeSceneCutCarryClearFrames + 1);
                            int clearBeforeStart = clearStart;
                            int clearBeforeEnd = Math.Min(totalFrames, result.Index);
                            int clearAfterStart = result.Index;
                            int clearAfterEnd = clearEnd;
                            int removed = _maskProvider.RemoveFaceMasksRange(clearStart, clearEnd);
                            sceneCutClearUntilExclusive = Math.Max(sceneCutClearUntilExclusive, clearEnd);
                            lastSceneCutFrame = result.Index;
                            Debug.WriteLine(
                                $"[AutoMask] scene-cut reset idx={result.Index} clearFrom={clearStart} clearTo={clearEnd} removed={removed} clearRadius={clearRadius} offModeRadius={OffModeSceneCutCarryClearFrames} diff={diff:0.###} rebuildBefore={clearBeforeStart}:{clearBeforeEnd} rebuildAfter={clearAfterStart}:{clearAfterEnd} phase=off-pipe");
                        }
                    }

                    if (result.FrameSignature.Length > 0)
                        previousSceneSignature = result.FrameSignature;

                    onFrameProcessed?.Invoke(result.Index);

                    bool shouldWrite = !(!isSceneCut
                        && result.Index > lastSceneCutFrame
                        && result.Index < sceneCutClearUntilExclusive);
                    if (shouldWrite && result.Bounds.Length > 0)
                    {
                        _maskProvider.SetFaceRects(
                            result.Index,
                            result.Bounds,
                            result.Size,
                            result.MinConfidence,
                            result.Confidences);
                    }

                    ReportProgress(progress, result.Index, totalFrames, progressState);
                    processed++;
                    if (processed % 60 == 0)
                    {
                        Debug.WriteLine(
                            $"[AutoMaskPipe] frames={processed}, decodeMs={decodeMs}, detectMs={detectMs}, totalMs={swTotal.ElapsedMilliseconds}, roi={BuildDetectionSummary(roiStats, filterStats)}");
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
                        float[] confidences = Array.Empty<float>();
                        PixelSize resultSize = useProxy ? fullSize : new PixelSize(item.Width, item.Height);
                        float? minConfidence = null;

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
                                        lastFaces,
                                        fullSize,
                                        scaleX,
                                        scaleY,
                                        roiStats);
                                    tDetect.Stop();
                                    detectMs += tDetect.ElapsedMilliseconds;

                                    if (faces.Count > 0)
                                    {
                                        int rawFaceCount = faces.Count;
                                        faces = FilterFacesByAreaAndStats(
                                            faces,
                                            resultSize,
                                            src,
                                            item.Stride,
                                            item.Width,
                                            item.Height,
                                            useProxy ? scaleX : 1.0,
                                            useProxy ? scaleY : 1.0,
                                            GetFaceFilterSettings(_options.FilterProfile, !_options.EnablePostProcessing),
                                            filterStats);
                                        WriteDetectionDiagnostics(item.Index, rawFaceCount, faces, resultSize);
                                        var payload = BuildMaskPayload(faces);
                                        bounds = payload.Bounds;
                                        confidences = payload.Confidences;
                                        minConfidence = payload.MinConfidence;
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
                                    Size = resultSize,
                                    MinConfidence = minConfidence,
                                    Confidences = confidences,
                                    FrameSignature = item.SceneSignature
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
                $"[AutoMaskPipe] done frames={processed}, decodeMs={decodeMs}, detectMs={detectMs}, totalMs={swTotal.ElapsedMilliseconds}, roi={BuildDetectionSummary(roiStats, filterStats)}");
            SetLastRunSummary(new AutoMaskRunSummary(
                "pipe-single",
                totalFrames,
                start,
                processed,
                processed,
                processed,
                0,
                0,
                decodeMs,
                detectMs,
                0,
                0,
                swTotal.ElapsedMilliseconds,
                _options.DownscaleRatio,
                _options.DownscaleQuality,
                _options.UseTracking,
                _options.DetectEveryNFrames,
                _options.ParallelDetectorCount,
                BuildDetectionSummary(roiStats, filterStats),
                _sourceFpsForSummary,
                _options.RunId,
                GetDetectorName()));
            ApplyPostProcessResultToRunSummary(
                RunAutoPostProcessIfNeeded(videoPath, totalFrames, ct));
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
                    if (faces.Count > 0)
                    {
                        int rawFaceCount = faces.Count;
                        using var fb = frame.Lock();
                        unsafe
                        {
                            byte* src = (byte*)fb.Address;
                            faces = FilterFacesByAreaAndStats(
                                faces,
                                frame.PixelSize,
                                src,
                                fb.RowBytes,
                                frame.PixelSize.Width,
                                frame.PixelSize.Height,
                                1.0,
                                1.0,
                                GetFaceFilterSettings(_options.FilterProfile, !_options.EnablePostProcessing));
                        }
                        WriteDetectionDiagnostics(frameIndex, rawFaceCount, faces, frame.PixelSize);
                    }

                    if (faces != null && faces.Count > 0)
                    {
                        var payload = BuildMaskPayload(faces);
                        _maskProvider.SetFaceRects(
                            frameIndex,
                            payload.Bounds,
                            frame.PixelSize,
                            payload.MinConfidence,
                            payload.Confidences);
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

            if (_detector is not IBgraFaceDetector bgraDetector)
                return _detector.DetectFaces(frame);

            var faces = bgraDetector.DetectFacesDownscaled(frame, ratio, _options.DownscaleQuality);

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
            IReadOnlyList<IBgraFaceDetector> detectors,
            IProgress<int>? progress,
            CancellationToken ct,
            int startFrameIndex,
            int totalFrames,
            Action<int>? onFrameProcessed)
        {
            int start = Math.Clamp(startFrameIndex, 0, Math.Max(0, totalFrames - 1));
            using var extractor = CreateExtractorWithFallback(videoPath, start, useRaw: true, ct);

            var geometry = CreateDetectionGeometry(extractor.FrameSize);
            PixelSize fullSize = geometry.FullSize;
            bool useProxy = geometry.UseProxy;
            bool useBilinear = geometry.UseBilinear;
            int proxyWidth = geometry.TargetWidth;
            int proxyHeight = geometry.TargetHeight;
            double scaleX = geometry.ScaleX;
            double scaleY = geometry.ScaleY;

            int queueDepth = Math.Max(4, detectors.Count * 3);
            using var queue = new System.Collections.Concurrent.BlockingCollection<BgraBuffer>(queueDepth);
            using var results = new System.Collections.Concurrent.BlockingCollection<DetectionResult>(queueDepth * 2);
            var pool = System.Buffers.ArrayPool<byte>.Shared;
            long decodeMs = 0;
            long detectMs = 0;
            int processed = 0;
            var swTotal = Stopwatch.StartNew();
            var progressState = new ProgressState();
            var filterStats = new FaceFilterStats();
            bool applyOffModeSceneCutReset = _options.FilterProfile == FaceFilterProfile.Yolo
                && !_options.EnablePostProcessing
                && _options.UseTracking;

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

                        double[] sceneSignature = Array.Empty<double>();
                        if (applyOffModeSceneCutReset)
                        {
                            unsafe
                            {
                                fixed (byte* src = buffer)
                                {
                                    sceneSignature = ComputeFrameSignature(src, stride, targetW, targetH);
                                }
                            }
                        }

                        try
                        {
                            queue.Add(new BgraBuffer
                            {
                                Index = idx,
                                Data = buffer,
                                Stride = stride,
                                Width = targetW,
                                Height = targetH,
                                SceneSignature = sceneSignature
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
                            float[] confidences = Array.Empty<float>();
                            PixelSize resultSize = useProxy ? fullSize : new PixelSize(item.Width, item.Height);
                            float? minConfidence = null;

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
                                        {
                                            int rawFaceCount = faces.Count;
                                            faces = FilterFacesByAreaAndStats(
                                                faces,
                                                resultSize,
                                                src,
                                                item.Stride,
                                                item.Width,
                                                item.Height,
                                                useProxy ? scaleX : 1.0,
                                                useProxy ? scaleY : 1.0,
                                                GetFaceFilterSettings(_options.FilterProfile, !_options.EnablePostProcessing),
                                                filterStats);
                                            WriteDetectionDiagnostics(item.Index, rawFaceCount, faces, resultSize);
                                            var payload = BuildMaskPayload(faces);
                                            bounds = payload.Bounds;
                                            confidences = payload.Confidences;
                                            minConfidence = payload.MinConfidence;
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
                                Size = resultSize,
                                MinConfidence = minConfidence,
                                Confidences = confidences,
                                FrameSignature = item.SceneSignature
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
                var orderedResults = new Dictionary<int, DetectionResult>();
                int nextFrameToWrite = start;
                double[]? previousSceneSignature = null;
                int sceneCutClearUntilExclusive = int.MinValue;
                int lastSceneCutFrame = -1;

                foreach (var result in results.GetConsumingEnumerable())
                {
                    if (ct.IsCancellationRequested)
                        break;

                    orderedResults[result.Index] = result;

                    while (orderedResults.TryGetValue(nextFrameToWrite, out var orderedResult))
                    {
                        orderedResults.Remove(nextFrameToWrite);

                        bool isSceneCut = false;
                        int clearStart = 0;
                        int clearEnd = 0;
                        if (applyOffModeSceneCutReset &&
                            previousSceneSignature != null &&
                            orderedResult.FrameSignature.Length > 0 &&
                            previousSceneSignature.Length > 0)
                        {
                            double diff = ComputeSignatureDifference(orderedResult.FrameSignature, previousSceneSignature);
                            if (diff >= OffModeSceneCutSignatureDiffThreshold)
                            {
                                isSceneCut = true;
                                int clearRadius = Math.Max(0, _options.DetectEveryNFrames);
                                clearStart = Math.Max(0, orderedResult.Index - OffModeSceneCutCarryClearFrames);
                                clearEnd = Math.Min(totalFrames, orderedResult.Index + clearRadius + OffModeSceneCutCarryClearFrames + 1);
                                int clearBeforeStart = clearStart;
                                int clearBeforeEnd = Math.Min(totalFrames, orderedResult.Index);
                                int clearAfterStart = orderedResult.Index;
                                int clearAfterEnd = clearEnd;
                                int removed = _maskProvider.RemoveFaceMasksRange(clearStart, clearEnd);
                                sceneCutClearUntilExclusive = Math.Max(sceneCutClearUntilExclusive, clearEnd);
                                lastSceneCutFrame = orderedResult.Index;
                                Debug.WriteLine(
                                    $"[AutoMask] scene-cut reset idx={orderedResult.Index} clearFrom={clearStart} clearTo={clearEnd} removed={removed} clearRadius={clearRadius} offModeRadius={OffModeSceneCutCarryClearFrames} diff={diff:0.###} rebuildBefore={clearBeforeStart}:{clearBeforeEnd} rebuildAfter={clearAfterStart}:{clearAfterEnd} phase=off-pipe-parallel");
                            }
                        }

                        if (orderedResult.FrameSignature.Length > 0)
                            previousSceneSignature = orderedResult.FrameSignature;

                        onFrameProcessed?.Invoke(orderedResult.Index);
                        bool shouldWrite = !(!isSceneCut
                            && orderedResult.Index > lastSceneCutFrame
                            && orderedResult.Index < sceneCutClearUntilExclusive);
                        if (shouldWrite)
                        {
                            if (orderedResult.Bounds.Length > 0)
                            {
                                _maskProvider.SetFaceRects(
                                    orderedResult.Index,
                                    orderedResult.Bounds,
                                    orderedResult.Size,
                                    orderedResult.MinConfidence,
                                    orderedResult.Confidences);
                            }
                        }

                        ReportProgress(progress, orderedResult.Index, totalFrames, progressState);
                        int done = Interlocked.Increment(ref processed);
                        if (done % 60 == 0)
                        {
                            Debug.WriteLine(
                                $"[AutoMaskPipe] frames={done}, decodeMs={decodeMs}, detectMs={detectMs}, totalMs={swTotal.ElapsedMilliseconds}, filter={filterStats.BuildSummary()}");
                        }

                        nextFrameToWrite++;
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
                $"[AutoMaskPipe] done frames={processed}, decodeMs={decodeMs}, detectMs={detectMs}, totalMs={swTotal.ElapsedMilliseconds}, filter={filterStats.BuildSummary()}");
            SetLastRunSummary(new AutoMaskRunSummary(
                "pipe-parallel",
                totalFrames,
                start,
                processed,
                processed,
                processed,
                0,
                0,
                decodeMs,
                detectMs,
                0,
                0,
                swTotal.ElapsedMilliseconds,
                _options.DownscaleRatio,
                _options.DownscaleQuality,
                _options.UseTracking,
                _options.DetectEveryNFrames,
                _options.ParallelDetectorCount,
                filterStats.BuildSummary(),
                _sourceFpsForSummary,
                _options.RunId,
                GetDetectorName()));
            ApplyPostProcessResultToRunSummary(
                RunAutoPostProcessIfNeeded(videoPath, totalFrames, ct));
        }

        private void GenerateSparsePipelinedTrackingParallel(
            string videoPath,
            IReadOnlyList<IBgraFaceDetector> detectors,
            IProgress<int>? progress,
            CancellationToken ct,
            int startFrameIndex,
            int totalFrames,
            Action<int>? onFrameProcessed)
        {
            int start = Math.Clamp(startFrameIndex, 0, Math.Max(0, totalFrames - 1));
            using var extractor = CreateExtractorWithFallback(videoPath, start, useRaw: true, ct);

            var geometry = CreateDetectionGeometry(extractor.FrameSize);
            PixelSize fullSize = geometry.FullSize;
            bool useProxy = geometry.UseProxy;
            bool useBilinear = geometry.UseBilinear;
            int targetW = useProxy ? geometry.TargetWidth : fullSize.Width;
            int targetH = useProxy ? geometry.TargetHeight : fullSize.Height;
            double scaleX = geometry.ScaleX;
            double scaleY = geometry.ScaleY;

            int interval = Math.Max(1, _options.DetectEveryNFrames);
            int queueDepth = Math.Max(4, detectors.Count * 3);
            using var queue = new System.Collections.Concurrent.BlockingCollection<BgraBuffer>(queueDepth);
            var results = new System.Collections.Concurrent.ConcurrentDictionary<int, DetectionResult>();
            var pool = System.Buffers.ArrayPool<byte>.Shared;
            long decodeMs = 0;
            long detectMs = 0;
            int decoded = 0;
            int detected = 0;
            int highestDecodedFrame = start - 1;
            var swTotal = Stopwatch.StartNew();
            var progressState = new ProgressState();
            var filterStats = new FaceFilterStats();

            var producer = Task.Run(() =>
            {
                int nextIndex = start;
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        bool shouldDetect = nextIndex == start || nextIndex % interval == 0;
                        int idx;
                        if (!shouldDetect)
                        {
                            var tSkip = Stopwatch.StartNew();
                            if (useProxy)
                            {
                                if (!extractor.TryGetNextFrameRawScaled(ct, requireBgra: false, targetW, targetH, useBilinear, out _, out idx))
                                    break;
                            }
                            else
                            {
                                if (!extractor.TryGetNextFrameRaw(ct, requireBgra: false, out _, out idx))
                                    break;
                            }
                            tSkip.Stop();
                            Interlocked.Add(ref decodeMs, tSkip.ElapsedMilliseconds);

                            if (idx >= totalFrames)
                                break;

                            nextIndex = idx + 1;
                            highestDecodedFrame = idx;
                            onFrameProcessed?.Invoke(idx);
                            ReportProgress(progress, idx, totalFrames, progressState);
                            Interlocked.Increment(ref decoded);
                            continue;
                        }

                        int stride;
                        int size = targetW * 4 * targetH;
                        byte[] buffer = pool.Rent(size);
                        var tDecode = Stopwatch.StartNew();
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

                        nextIndex = idx + 1;
                        highestDecodedFrame = idx;
                        onFrameProcessed?.Invoke(idx);
                        ReportProgress(progress, idx, totalFrames, progressState);
                        Interlocked.Increment(ref decoded);

                        if (_maskProvider.HasEntry(idx))
                        {
                            pool.Return(buffer);
                            continue;
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
                            float[] confidences = Array.Empty<float>();
                            float? minConfidence = null;
                            double[] frameSignature = Array.Empty<double>();
                            PixelSize resultSize = useProxy ? fullSize : new PixelSize(item.Width, item.Height);

                            unsafe
                            {
                                fixed (byte* src = item.Data)
                                {
                                    frameSignature = ComputeFrameSignature(
                                        src,
                                        item.Stride,
                                        item.Width,
                                        item.Height);

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
                                    {
                                        int rawFaceCount = faces.Count;
                                        faces = FilterFacesByAreaAndStats(
                                            faces,
                                            resultSize,
                                            src,
                                            item.Stride,
                                            item.Width,
                                            item.Height,
                                            useProxy ? scaleX : 1.0,
                                            useProxy ? scaleY : 1.0,
                                            GetFaceFilterSettings(_options.FilterProfile, !_options.EnablePostProcessing),
                                            filterStats);
                                        WriteDetectionDiagnostics(item.Index, rawFaceCount, faces, resultSize);
                                        var payload = BuildMaskPayload(faces);
                                        bounds = payload.Bounds;
                                        confidences = payload.Confidences;
                                        minConfidence = payload.MinConfidence;
                                    }
                                }
                            }

                            results[item.Index] = new DetectionResult
                            {
                                Index = item.Index,
                                Bounds = bounds,
                                Size = resultSize,
                                MinConfidence = minConfidence,
                                Confidences = confidences,
                                FrameSignature = frameSignature
                            };
                            if (bounds.Length > 0 && !_maskProvider.HasEntry(item.Index))
                            {
                                _maskProvider.SetFaceRects(
                                    item.Index,
                                    bounds,
                                    resultSize,
                                    minConfidence,
                                    confidences);
                            }
                            int done = Interlocked.Increment(ref detected);
                            if (done % 20 == 0)
                            {
                                Debug.WriteLine(
                                    $"[AutoMaskSparsePipe] detects={done}, decoded={decoded}, decodeMs={decodeMs}, detectMs={detectMs}, totalMs={swTotal.ElapsedMilliseconds}, filter={filterStats.BuildSummary()}");
                            }
                        }
                        finally
                        {
                            pool.Return(item.Data);
                        }
                    }
                }, ct));
            }

            try
            {
                producer.Wait(ct);
                Task.WaitAll(consumers.ToArray());
            }
            catch
            {
                // cancellation is handled by the caller
            }

            int materializeEndExclusive = ct.IsCancellationRequested
                ? Math.Min(totalFrames, Math.Max(start, highestDecodedFrame) + 1)
                : totalFrames;
            var materialized = MaterializeSparseTrackingResults(results, start, materializeEndExclusive);
            int interpolated = materialized.Interpolated;

            if (!ct.IsCancellationRequested)
                progress?.Report(100);
            Debug.WriteLine(
                $"[AutoMaskSparsePipe] done decoded={decoded}, detects={detected}, interpolated={interpolated}, sparseSceneCuts={materialized.SceneCutStops}, sparseSceneCutPairs={FormatSparseSceneCutTransitions(materialized.SceneCutTransitions)}, decodeMs={decodeMs}, detectMs={detectMs}, totalMs={swTotal.ElapsedMilliseconds}, filter={filterStats.BuildSummary()}");
            SetLastRunSummary(new AutoMaskRunSummary(
                "sparse-pipe-parallel",
                totalFrames,
                start,
                decoded,
                decoded,
                detected,
                interpolated,
                0,
                decodeMs,
                detectMs,
                0,
                0,
                swTotal.ElapsedMilliseconds,
                _options.DownscaleRatio,
                _options.DownscaleQuality,
                _options.UseTracking,
                _options.DetectEveryNFrames,
                _options.ParallelDetectorCount,
                filterStats.BuildSummary(),
                _sourceFpsForSummary,
                _options.RunId,
                GetDetectorName()));
            ApplyPostProcessResultToRunSummary(
                RunAutoPostProcessIfNeeded(videoPath, totalFrames, ct));
        }

        private AutoMaskPostProcessResult RunAutoPostProcessIfNeeded(string videoPath, int totalFrames, CancellationToken ct)
        {
            if (ct.IsCancellationRequested || totalFrames <= 0)
                return AutoMaskPostProcessResult.Empty;

            var postProcess = new AutoMaskPostProcessPipeline(
                _maskProvider,
                _options,
                totalFrames);

            return postProcess.Apply(
                videoPath,
                ct,
                _detector as IBgraFaceDetector,
                _options.RoiRefinerDetectorOptions,
                _options.UseFaceOnnxRoiRefiner);
        }

        private void ApplyPostProcessResultToRunSummary(AutoMaskPostProcessResult postProcessResult)
        {
            if (LastRunSummary == null)
                return;

            var finalSummary = postProcessResult.FinalSummary;
            LastRunSummary = LastRunSummary with
            {
                FinalMaskFrames = finalSummary.FinalFrameCount,
                FinalMaskRows = finalSummary.FinalRowCount,
                FinalMaskShortGapCount = finalSummary.FinalShortGapCount,
                FinalMaskPerFaceShortGapCount = finalSummary.FinalPerFaceShortGapCount,
                FinalMaskLargeJumpGapCount = finalSummary.FinalLargeJumpGapCount,
                FinalSceneCutPreGuardPairCount = finalSummary.FinalSceneCutPreGuardPairCount,
                FinalSceneCutPreStrongProbePairCount = finalSummary.FinalSceneCutPreStrongProbePairCount,
                FinalSceneCutPostGuardPairCount = finalSummary.FinalSceneCutPostGuardPairCount,
                FinalSceneCutPostStrongProbePairCount = finalSummary.FinalSceneCutPostStrongProbePairCount,
                FinalSceneCutCarryPairCount = finalSummary.FinalSceneCutCarryPairCount,
                FinalSceneCutCarryRemovedCount = finalSummary.FinalSceneCutCarryRemovedCount,
                FinalSceneCutProtectedFrameCount = finalSummary.FinalSceneCutProtectedFrameCount,
                FinalSceneCutPostGapFillCarryPairCount = finalSummary.FinalSceneCutPostGapFillCarryPairCount,
                FinalSceneCutPostGapFillCarryRemovedCount = finalSummary.FinalSceneCutPostGapFillCarryRemovedCount,
                FinalSceneCutPostGapFillProtectedFrameCount = finalSummary.FinalSceneCutPostGapFillProtectedFrameCount,
                FinalProtectedSceneCarryFrameCount = finalSummary.ProtectedSceneCarryFrameCount,
                FinalMaskReviewRequired = finalSummary.FinalReviewRequired,
                FinalMaskReviewReasons = string.IsNullOrWhiteSpace(finalSummary.FinalReviewReasons)
                    ? "none"
                    : finalSummary.FinalReviewReasons,
                FinalMissRecoveryFillCount = finalSummary.FinalMissRecoveryFillCount,
                FinalFalsePositiveSuppressedCount = finalSummary.FinalFalsePositiveSuppressedCount,
                FinalGapFillRecoveredCount = finalSummary.FinalGapFillRecoveredCount,
                FinalGapFillBlockedCutGapFrames = finalSummary.FinalGapFillBlockedCutGapFrames,
                FinalGapFillBlockedCleanupGapFrames = finalSummary.FinalGapFillBlockedCleanupGapFrames,
                FinalGapFillBlockedSceneCarryGapFrames = finalSummary.FinalGapFillBlockedSceneCarryGapFrames,
                FinalGapFillSuppressedWeakGeometryAnchorChecks = finalSummary.FinalGapFillSuppressedWeakGeometryAnchorChecks,
                FinalGapFillSuppressedRiskyGeometryAnchorChecks = finalSummary.FinalGapFillSuppressedRiskyGeometryAnchorChecks,
                FinalGapFillUnsupportedWeakAnchorChecks = finalSummary.FinalGapFillUnsupportedWeakAnchorChecks,
                PostProcessMs = postProcessResult.PostProcessElapsedMs,
                SampleWindowFrames = finalSummary.SampleWindowFrames,
                SampleFrameCount = finalSummary.SampleFrameCount,
                SampleRowCount = finalSummary.SampleRowCount,
                SampleShortGapCount = finalSummary.SampleShortGapCount,
                SamplePerFaceShortGapCount = finalSummary.SamplePerFaceShortGapCount,
                SampleIsolatedFrameCount = finalSummary.SampleIsolatedFrameCount,
                SampleLargeJumpGapCount = finalSummary.SampleLargeJumpGapCount,
                SampleReviewRequired = finalSummary.SampleReviewRequired,
                SampleReviewReasons = string.IsNullOrWhiteSpace(finalSummary.SampleReviewReasons)
                    ? "none"
                    : finalSummary.SampleReviewReasons,
                SampleProtectedSceneCarryFrameCount = finalSummary.SampleProtectedSceneCarryFrameCount,
                SampleMissRecoveryFillCount = finalSummary.SampleMissRecoveryFillCount,
                SampleFalsePositiveSuppressionCount = finalSummary.SampleFalsePositiveSuppressionCount,
                SampleWindowIssueFrameCount = finalSummary.SampleWindowIssueFrameCount,
                SampleWindowIssueCandidateCount = finalSummary.SampleWindowIssueCandidateCount,
                SampleWindowStartReason = string.IsNullOrWhiteSpace(finalSummary.SampleWindowStartReason)
                    ? "none"
                    : finalSummary.SampleWindowStartReason
            };
            Debug.WriteLine(LastRunSummary.ToLogLine());
            LogAutoMaskQualityGate(LastRunSummary);
        }

        private static void LogAutoMaskQualityGate(AutoMaskRunSummary summary)
        {
            if (summary == null)
                return;

            int riskScore = 0;
            if (summary.FinalMaskReviewRequired)
                riskScore++;
            if (summary.FinalMaskShortGapCount > 0 || summary.FinalMaskPerFaceShortGapCount > 0 || summary.FinalMaskLargeJumpGapCount > 0)
                riskScore++;
            if (summary.FinalProtectedSceneCarryFrameCount > 0)
                riskScore++;

            int sampleWindowFrames = Math.Max(
                0,
                summary.SampleWindowFrames > 0
                    ? summary.SampleWindowFrames
                    : (summary.SourceFps > 0
                        ? Math.Min(summary.TotalFrames, (int)Math.Round(summary.SourceFps * 30.0))
                        : Math.Min(summary.TotalFrames, 900)));
            double sampleMissRecoveryRate = sampleWindowFrames > 0
                ? summary.SampleMissRecoveryFillCount / (double)sampleWindowFrames
                : 0.0;
            double sampleFpSuppressedRate = sampleWindowFrames > 0
                ? summary.SampleFalsePositiveSuppressionCount / (double)sampleWindowFrames
                : 0.0;
            double finalSceneCutRemovalRate = summary.FinalSceneCutCarryPairCount > 0
                ? summary.FinalSceneCutCarryRemovedCount / (double)summary.FinalSceneCutCarryPairCount
                : 0.0;
            double finalSceneCutProtectedRate = summary.FinalSceneCutCarryPairCount > 0
                ? summary.FinalSceneCutProtectedFrameCount / (double)summary.FinalSceneCutCarryPairCount
                : 0.0;
            double finalPostGapFillRemovalRate = summary.FinalSceneCutPostGapFillCarryPairCount > 0
                ? summary.FinalSceneCutPostGapFillCarryRemovedCount / (double)summary.FinalSceneCutPostGapFillCarryPairCount
                : 0.0;
            double finalPostGapFillProtectedRate = summary.FinalSceneCutPostGapFillCarryPairCount > 0
                ? summary.FinalSceneCutPostGapFillProtectedFrameCount / (double)summary.FinalSceneCutPostGapFillCarryPairCount
                : 0.0;
            string riskLabel = riskScore >= 3
                ? "high"
                : riskScore >= 1
                    ? "medium"
                    : "low";
            int sampleRiskScore = 0;
            if (summary.SampleReviewRequired)
                sampleRiskScore++;
            if (summary.SampleShortGapCount > 0 || summary.SamplePerFaceShortGapCount > 0 || summary.SampleLargeJumpGapCount > 0)
                sampleRiskScore++;
            if (summary.SampleProtectedSceneCarryFrameCount > 0)
                sampleRiskScore++;
            string sampleRiskLabel = sampleRiskScore >= 3
                ? "high"
                : sampleRiskScore >= 1
                    ? "medium"
                    : "low";
            double detectionFps = summary.ProcessedFrames > 0 && summary.TotalMs > 0
                ? summary.ProcessedFrames * 1000.0 / summary.TotalMs
                : 0.0;

            System.Diagnostics.Debug.WriteLine(
                $"[AutoMaskQualityGate] runId={summary.RunId ?? "n/a"}, mode={summary.Mode}, risk={riskLabel}, detectionFps={detectionFps:0.00}, totalFrames={summary.TotalFrames}, processed={summary.ProcessedFrames}, finalMaskFrames={summary.FinalMaskFrames}, finalRows={summary.FinalMaskRows}, reviewRequired={summary.FinalMaskReviewRequired.ToString().ToLowerInvariant()}, reviewReasons={summary.FinalMaskReviewReasons}, post={summary.EnablePostProcessing}, roiPost={summary.EnableRoiPostProcess}, weakIso={summary.EnableYoloWeakIsolatedCleanup}, gapFill={summary.EnableYoloGapFill}, scene={summary.EnableYoloSceneCutCarryCleanup}, smooth={summary.EnableYoloTemporalSmoothing}, shortGaps={summary.FinalMaskShortGapCount}, perFaceShortGaps={summary.FinalMaskPerFaceShortGapCount}, largeJumps={summary.FinalMaskLargeJumpGapCount}, carryFrames={summary.FinalProtectedSceneCarryFrameCount}, sceneCut=preGuard:{summary.FinalSceneCutPreGuardPairCount},preStrong:{summary.FinalSceneCutPreStrongProbePairCount},postGuard:{summary.FinalSceneCutPostGuardPairCount},postStrong:{summary.FinalSceneCutPostStrongProbePairCount},carryPairs:{summary.FinalSceneCutCarryPairCount},carryRemoved:{summary.FinalSceneCutCarryRemovedCount},carryProtected:{summary.FinalSceneCutProtectedFrameCount}, postGapFillCarryPairs:{summary.FinalSceneCutPostGapFillCarryPairCount},postGapFillRemoved:{summary.FinalSceneCutPostGapFillCarryRemovedCount},postGapFillProtected:{summary.FinalSceneCutPostGapFillProtectedFrameCount}, sampleWindowFrames={sampleWindowFrames}");
            System.Diagnostics.Debug.WriteLine(
                $"[AutoMaskQualityGate] final runId={summary.RunId ?? "n/a"}, missRecovery={summary.FinalMissRecoveryFillCount}, fpSuppressed={summary.FinalFalsePositiveSuppressedCount}, gapFillRecovered={summary.FinalGapFillRecoveredCount}, gapFillBlocked={summary.FinalGapFillBlockedCutGapFrames}/{summary.FinalGapFillBlockedCleanupGapFrames}/{summary.FinalGapFillBlockedSceneCarryGapFrames}, gapFillAnchorChecks={summary.FinalGapFillSuppressedWeakGeometryAnchorChecks}/{summary.FinalGapFillSuppressedRiskyGeometryAnchorChecks}/{summary.FinalGapFillUnsupportedWeakAnchorChecks}, sceneCutRemovalRate={finalSceneCutRemovalRate:0.0000}, sceneCutProtectedRate={finalSceneCutProtectedRate:0.0000}, postGapFillRemovalRate={finalPostGapFillRemovalRate:0.0000}, postGapFillProtectedRate={finalPostGapFillProtectedRate:0.0000}, postProcessMs={summary.PostProcessMs}");
            System.Diagnostics.Debug.WriteLine(
                $"[AutoMaskQualityGate] sample runId={summary.RunId ?? "n/a"}, mode={summary.Mode}, risk={sampleRiskLabel}, sampleWindow={summary.SampleWindowFrames}, sampleFrames={summary.SampleFrameCount}, sampleRows={summary.SampleRowCount}, shortGaps={summary.SampleShortGapCount}, perFaceShortGaps={summary.SamplePerFaceShortGapCount}, isolated={summary.SampleIsolatedFrameCount}, largeJumps={summary.SampleLargeJumpGapCount}, protectedCarry={summary.SampleProtectedSceneCarryFrameCount}, reviewRequired={summary.SampleReviewRequired.ToString().ToLowerInvariant()}, reviewReasons={summary.SampleReviewReasons}");
            System.Diagnostics.Debug.WriteLine(
                $"[AutoMaskQualityGate] sample runId={summary.RunId ?? "n/a"}, sampleIssueFrames={summary.SampleWindowIssueFrameCount}, sampleIssueCandidates={summary.SampleWindowIssueCandidateCount}, sampleWindowStartReason={summary.SampleWindowStartReason}, missRecovery={summary.SampleMissRecoveryFillCount}, missRecoveryRate={sampleMissRecoveryRate:0.0000}, fpSuppressed={summary.SampleFalsePositiveSuppressionCount}, fpSuppressedRate={sampleFpSuppressedRate:0.0000}, riskScore={sampleRiskScore}, riskLabel={sampleRiskLabel}");
        }

        private void SetLastRunSummary(AutoMaskRunSummary summary)
        {
            bool postEnabled = _options.EnablePostProcessing;
            bool postRoiEnabled = postEnabled && _options.EnableRoiPostProcess;
            bool postWeakIsoEnabled = postEnabled && _options.EnableYoloWeakIsolatedCleanup;
            bool postGapFillEnabled = _options.FilterProfile == FaceFilterProfile.Yolo
                && _options.UseTracking
                && _options.EnableYoloGapFill
                && (_options.EnablePostProcessing || _options.DetectEveryNFrames > 1);
            bool postSceneCutEnabled = postEnabled && _options.UseTracking && _options.EnableYoloSceneCutCarryCleanup;
            bool postTemporalEnabled = postEnabled && _options.EnableYoloTemporalSmoothing;

            LastRunSummary = summary with
            {
                EnablePostProcessing = postEnabled,
                EnableRoiPostProcess = postRoiEnabled,
                EnableYoloWeakIsolatedCleanup = postWeakIsoEnabled,
                EnableYoloGapFill = postGapFillEnabled,
                EnableYoloSceneCutCarryCleanup = postSceneCutEnabled,
                EnableYoloTemporalSmoothing = postTemporalEnabled,
                SourceFps = _sourceFpsForSummary
            };
            Debug.WriteLine(LastRunSummary.ToLogLine());
        }

        private string GetDetectorName()
        {
            if (_detector is FaceOnnxDetector)
            {
                string provider = FaceOnnxDetector.GetLastExecutionProviderLabel();
                string? error = FaceOnnxDetector.GetLastExecutionProviderError();
                if (!string.IsNullOrWhiteSpace(error))
                    return $"{_detector.GetType().Name}/{provider}({error})";

                return $"{_detector.GetType().Name}/{provider}";
            }

            if (_detector is YoloFaceOnnxDetector)
            {
                string provider = YoloFaceOnnxDetector.GetLastExecutionProviderLabel();
                string? error = YoloFaceOnnxDetector.GetLastExecutionProviderError();
                if (!string.IsNullOrWhiteSpace(error))
                    return $"{_detector.GetType().Name}/{provider}({error})";

                return $"{_detector.GetType().Name}/{provider}";
            }

            return _detector.GetType().Name;
        }

        private SparseMaterializeResult MaterializeSparseTrackingResults(
            System.Collections.Concurrent.ConcurrentDictionary<int, DetectionResult> results,
            int start,
            int endExclusive)
        {
            if (results == null || results.Count == 0)
                return new SparseMaterializeResult(0, 0, Array.Empty<SparseSceneCutTransition>());
            if (endExclusive <= start)
                return new SparseMaterializeResult(0, 0, Array.Empty<SparseSceneCutTransition>());

            int[] keys = results.Keys.ToArray();
            Array.Sort(keys);

            int materialized = 0;
            int sceneCutStops = 0;
            var sceneCutTransitions = new List<SparseSceneCutTransition>();
            int maxBridgeFrames = Math.Max(1, _options.DetectEveryNFrames * 2);
            bool guardSceneCuts = _options.FilterProfile == FaceFilterProfile.Yolo;
            double sceneCutThreshold = (_options.FilterProfile == FaceFilterProfile.Yolo && !_options.EnablePostProcessing)
                ? OffModeSparseSceneCutDifferenceThreshold
                : SparseSceneCutDifferenceThreshold;

            for (int i = 0; i < keys.Length; i++)
            {
                int key = keys[i];
                if (key < start || key >= endExclusive)
                    continue;
                if (!results.TryGetValue(key, out var current) || current.Bounds.Length == 0)
                    continue;

                DetectionResult? nextPositive = FindNextPositiveResult(results, keys, i + 1, endExclusive, key, maxBridgeFrames);
                    bool canBridge = nextPositive != null &&
                    CanBridgeSparseResults(
                        current,
                        nextPositive,
                        maxBridgeFrames,
                        guardSceneCuts,
                        sceneCutThreshold);

                int nextKey = FindNextDetectionKey(keys, i + 1, endExclusive);
                    bool stopAtSceneCut = false;
                    if (nextKey >= 0 &&
                        results.TryGetValue(nextKey, out var nextDetection) &&
                        ShouldStopSparseSceneCarry(
                            key,
                            nextKey,
                            maxBridgeFrames,
                            guardSceneCuts,
                            current.FrameSignature,
                            nextDetection.FrameSignature,
                            sceneCutThreshold))
                    {
                        stopAtSceneCut = true;
                        sceneCutStops++;
                        sceneCutTransitions.Add(new SparseSceneCutTransition(key, nextKey));
                    }

                int segmentEnd = stopAtSceneCut
                    ? key + 1
                    : canBridge
                        ? nextPositive!.Index
                        : nextKey >= 0
                            ? Math.Min(nextKey, key + Math.Max(1, _options.DetectEveryNFrames))
                            : Math.Min(endExclusive, key + Math.Max(1, _options.DetectEveryNFrames));
                if (segmentEnd <= key + 1)
                    continue;

                for (int frame = key + 1; frame < segmentEnd; frame++)
                {
                    if (_maskProvider.HasEntry(frame))
                        continue;

                    DetectionResult payload = canBridge
                        ? InterpolateSparseResult(current, nextPositive!, frame)
                        : current;

                    if (payload.Bounds.Length == 0)
                        continue;

                    _maskProvider.SetFaceRects(
                        frame,
                        payload.Bounds,
                        payload.Size,
                        payload.MinConfidence,
                        payload.Confidences);
                    materialized++;
                }
            }

            return new SparseMaterializeResult(materialized, sceneCutStops, sceneCutTransitions.ToArray());
        }

        private static int FindNextDetectionKey(int[] keys, int startIndex, int endExclusive)
        {
            if (startIndex < 0 || startIndex >= keys.Length)
                return -1;

            int key = keys[startIndex];
            if (key >= endExclusive)
                return -1;

            return key;
        }

        private static DetectionResult? FindNextPositiveResult(
            System.Collections.Concurrent.ConcurrentDictionary<int, DetectionResult> results,
            int[] keys,
            int startIndex,
            int endExclusive,
            int currentKey,
            int maxBridgeFrames)
        {
            for (int i = startIndex; i < keys.Length; i++)
            {
                int key = keys[i];
                if (key >= endExclusive)
                    return null;
                if (!results.TryGetValue(key, out var result))
                    continue;
                if (key - currentKey > maxBridgeFrames)
                    return null;
                if (result.Bounds.Length > 0)
                    return result;
            }

            return null;
        }

        private static bool CanBridgeSparseResults(
            DetectionResult current,
            DetectionResult next,
            int maxBridgeFrames,
            bool guardSceneCuts,
            double sceneCutThreshold)
        {
            if (next.Index <= current.Index || next.Index - current.Index > maxBridgeFrames)
                return false;
            if (current.Bounds.Length == 0 || next.Bounds.Length == 0)
                return false;
            if (guardSceneCuts && IsSparseSceneCut(current, next, sceneCutThreshold))
                return false;

            int matches = 0;
            var used = new bool[next.Bounds.Length];
            for (int i = 0; i < current.Bounds.Length; i++)
            {
                int match = FindBestSparseMatch(current.Bounds[i], next.Bounds, used);
                if (match < 0)
                    continue;

                used[match] = true;
                matches++;
            }

            return matches > 0;
        }

        private static DetectionResult InterpolateSparseResult(
            DetectionResult current,
            DetectionResult next,
            int frameIndex)
        {
            if (next.Index <= current.Index)
                return current;

            double t = (frameIndex - current.Index) / (double)(next.Index - current.Index);
            t = Math.Clamp(t, 0.0, 1.0);

            var bounds = new List<Rect>(current.Bounds.Length);
            var confidences = new List<float>(current.Bounds.Length);
            var used = new bool[next.Bounds.Length];

            for (int i = 0; i < current.Bounds.Length; i++)
            {
                var from = current.Bounds[i];
                int match = FindBestSparseMatch(from, next.Bounds, used);
                if (match >= 0)
                {
                    used[match] = true;
                    var to = next.Bounds[match];
                    bounds.Add(LerpRect(from, to, t));
                    confidences.Add(LerpConfidence(current.Confidences, next.Confidences, i, match, t));
                }
            }

            float? minConfidence = GetMinConfidence(confidences);
            return new DetectionResult
            {
                Index = frameIndex,
                Bounds = bounds.ToArray(),
                Size = current.Size.Width > 0 && current.Size.Height > 0 ? current.Size : next.Size,
                MinConfidence = minConfidence,
                Confidences = confidences.ToArray()
            };
        }

        private static int FindBestSparseMatch(Rect source, IReadOnlyList<Rect> candidates, bool[] used)
        {
            int bestIndex = -1;
            double bestScore = 0.0;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (i < used.Length && used[i])
                    continue;

                var candidate = candidates[i];
                if (!IsReasonableSparseTrack(source, candidate))
                    continue;

                double iou = IoU(source, candidate);
                double score = iou > 0.0
                    ? iou
                    : 0.01 / Math.Max(0.01, GetCenterDistanceRatio(source, candidate));
                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }

            return bestIndex;
        }

        private static bool IsReasonableSparseTrack(Rect current, Rect next)
        {
            if (current.Width <= 0 || current.Height <= 0 || next.Width <= 0 || next.Height <= 0)
                return false;

            double area = Math.Max(1.0, current.Width * current.Height);
            double nextArea = Math.Max(1.0, next.Width * next.Height);
            double areaRatio = area / nextArea;
            if (areaRatio > SparseTrackMaxAreaChangeRatio || areaRatio < 1.0 / SparseTrackMaxAreaChangeRatio)
                return false;

            double iou = IoU(current, next);
            if (iou >= SparseTrackIouMin)
                return true;

            return GetCenterDistanceRatio(current, next) <= SparseTrackMaxCenterShiftRatio;
        }

        private static bool IsSparseSceneCut(
            DetectionResult current,
            DetectionResult next,
            double sceneCutThreshold)
            => ComputeSignatureDifference(current.FrameSignature, next.FrameSignature) >= sceneCutThreshold;

        private static string FormatSparseSceneCutTransitions(IReadOnlyList<SparseSceneCutTransition> transitions)
        {
            if (transitions.Count == 0)
                return "none";

            const int maxTransitions = 16;
            var selected = transitions
                .Take(maxTransitions)
                .Select(static x => string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"{x.SourceFrameIndex}->{x.NextFrameIndex}"));
            string text = string.Join(",", selected);
            return transitions.Count > maxTransitions
                ? string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"{text},+{transitions.Count - maxTransitions}")
                : text;
        }

        private static bool ShouldStopSparseSceneCarry(
            int currentKey,
            int nextKey,
            int maxBridgeFrames,
            bool guardSceneCuts,
            IReadOnlyList<double> currentSignature,
            IReadOnlyList<double> nextSignature,
            double sceneCutThreshold)
            => guardSceneCuts &&
                nextKey >= 0 &&
                nextKey > currentKey &&
                nextKey - currentKey <= maxBridgeFrames &&
                ComputeSignatureDifference(currentSignature, nextSignature) >= sceneCutThreshold;

        private static unsafe double[] ComputeFrameSignature(
            byte* basePtr,
            int stride,
            int width,
            int height)
        {
            if (basePtr == null || width <= 0 || height <= 0 || stride <= 0)
                return Array.Empty<double>();

            var signature = new double[SparseSceneCutSignatureColumns * SparseSceneCutSignatureRows];
            int index = 0;
            for (int sy = 0; sy < SparseSceneCutSignatureRows; sy++)
            {
                int y = Math.Clamp((int)Math.Round((sy + 0.5) * height / SparseSceneCutSignatureRows), 0, height - 1);
                byte* row = basePtr + y * stride;
                for (int sx = 0; sx < SparseSceneCutSignatureColumns; sx++)
                {
                    int x = Math.Clamp((int)Math.Round((sx + 0.5) * width / SparseSceneCutSignatureColumns), 0, width - 1);
                    byte* pixel = row + x * 4;
                    signature[index++] = ((77 * pixel[2]) + (150 * pixel[1]) + (29 * pixel[0])) / (255.0 * 256.0);
                }
            }

            return signature;
        }

        private static double ComputeSignatureDifference(
            IReadOnlyList<double> current,
            IReadOnlyList<double> next)
        {
            int count = Math.Min(current.Count, next.Count);
            if (count == 0)
                return 0.0;

            double total = 0.0;
            for (int i = 0; i < count; i++)
                total += Math.Abs(current[i] - next[i]);

            return total / count;
        }

        private static double GetCenterDistanceRatio(Rect a, Rect b)
        {
            double ax = a.X + a.Width * 0.5;
            double ay = a.Y + a.Height * 0.5;
            double bx = b.X + b.Width * 0.5;
            double by = b.Y + b.Height * 0.5;
            double dx = ax - bx;
            double dy = ay - by;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            double maxDim = Math.Max(1.0, Math.Max(Math.Max(a.Width, a.Height), Math.Max(b.Width, b.Height)));
            return distance / maxDim;
        }

        private static double IoU(Rect a, Rect b)
        {
            double ax1 = a.X;
            double ay1 = a.Y;
            double ax2 = a.X + a.Width;
            double ay2 = a.Y + a.Height;

            double bx1 = b.X;
            double by1 = b.Y;
            double bx2 = b.X + b.Width;
            double by2 = b.Y + b.Height;

            double ix1 = Math.Max(ax1, bx1);
            double iy1 = Math.Max(ay1, by1);
            double ix2 = Math.Min(ax2, bx2);
            double iy2 = Math.Min(ay2, by2);

            double iw = Math.Max(0.0, ix2 - ix1);
            double ih = Math.Max(0.0, iy2 - iy1);
            double inter = iw * ih;
            if (inter <= 0.0)
                return 0.0;

            double union = a.Width * a.Height + b.Width * b.Height - inter;
            if (union <= 0.0)
                return 0.0;
            return inter / union;
        }

        private static Rect LerpRect(Rect from, Rect to, double t)
        {
            double keep = 1.0 - t;
            return new Rect(
                from.X * keep + to.X * t,
                from.Y * keep + to.Y * t,
                Math.Max(0.0, from.Width * keep + to.Width * t),
                Math.Max(0.0, from.Height * keep + to.Height * t));
        }

        private static float LerpConfidence(
            IReadOnlyList<float> from,
            IReadOnlyList<float> to,
            int fromIndex,
            int toIndex,
            double t)
        {
            double keep = 1.0 - t;
            return (float)(GetConfidence(from, fromIndex) * keep + GetConfidence(to, toIndex) * t);
        }

        private static float GetConfidence(IReadOnlyList<float> values, int index)
        {
            if (index < 0 || index >= values.Count)
                return 1.0f;
            return values[index];
        }

        private static float? GetMinConfidence(IReadOnlyList<float> values)
        {
            if (values.Count == 0)
                return null;

            float min = float.MaxValue;
            for (int i = 0; i < values.Count; i++)
                min = Math.Min(min, values[i]);
            return min == float.MaxValue ? null : min;
        }

        private static IReadOnlyList<FaceDetectionResult> DetectFacesBgraSmart(
            IBgraFaceDetector detector,
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
                        var faces = detector.DetectFacesBgra(
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
            var fullFaces = detector.DetectFacesBgra(
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

        private sealed class FaceFilterStats
        {
            private long _regularKept;
            private long _smallKept;
            private long _rejected;
            private long _statsRejected;

            public void AddKept(FaceCandidateKind kind)
            {
                if (kind == FaceCandidateKind.Small)
                    Interlocked.Increment(ref _smallKept);
                else if (kind == FaceCandidateKind.Regular)
                    Interlocked.Increment(ref _regularKept);
            }

            public void AddRejected()
            {
                Interlocked.Increment(ref _rejected);
            }

            public void AddStatsRejected()
            {
                Interlocked.Increment(ref _statsRejected);
            }

            public string BuildSummary()
            {
                return
                    $"regular={Interlocked.Read(ref _regularKept)}, small={Interlocked.Read(ref _smallKept)}, rejected={Interlocked.Read(ref _rejected)}, statsRejected={Interlocked.Read(ref _statsRejected)}";
            }
        }

        private static string BuildDetectionSummary(RoiDetectStats roiStats, FaceFilterStats filterStats)
            => $"{roiStats.BuildSummary()}, filter={filterStats.BuildSummary()}";

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

        private static void ReportProgress(
            IProgress<int>? progress,
            int frameIndex,
            int totalFrames,
            ProgressState state)
        {
            if (progress == null)
                return;

            int percent = (int)Math.Round(frameIndex * 100.0 / Math.Max(1, totalFrames - 1));
            if (percent > 100) percent = 100;
            if (percent == state.LastPercent)
                return;
            state.LastPercent = percent;
            progress.Report(percent);
        }

        private static (Rect[] Bounds, float[] Confidences, float? MinConfidence) BuildMaskPayload(
            IReadOnlyList<FaceDetectionResult> faces)
        {
            int count = faces.Count;
            if (count == 0)
                return (Array.Empty<Rect>(), Array.Empty<float>(), null);

            var bounds = new Rect[count];
            var confidences = new float[count];
            float min = float.MaxValue;

            for (int i = 0; i < count; i++)
            {
                var face = faces[i];
                bounds[i] = face.Bounds;
                float conf = face.Confidence;
                confidences[i] = conf;
                if (conf < min)
                    min = conf;
            }

            return (bounds, confidences, min == float.MaxValue ? null : min);
        }

        private void WriteDetectionDiagnostics(
            int frameIndex,
            int rawFaceCount,
            IReadOnlyList<FaceDetectionResult> filteredFaces,
            PixelSize size)
        {
            if (!_options.DumpDetectionDiagnostics)
                return;

            int filteredCount = filteredFaces.Count;
            string confidenceSummary = filteredCount == 0
                ? "none"
                : string.Join(
                    "/",
                    filteredFaces
                        .Select(f => f.Confidence)
                        .OrderBy(v => v)
                        .Take(3)
                        .Select(v => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)));
            string topBox = "none";
            if (filteredCount > 0)
            {
                var best = filteredFaces.OrderByDescending(f => f.Confidence).First();
                double areaRatio = best.Bounds.Width * best.Bounds.Height /
                    Math.Max(1.0, size.Width * (double)size.Height);
                double aspect = best.Bounds.Height > 0 ? best.Bounds.Width / best.Bounds.Height : 0.0;
                topBox =
                    $"x={best.Bounds.X:0.0},y={best.Bounds.Y:0.0},w={best.Bounds.Width:0.0},h={best.Bounds.Height:0.0},areaRatio={areaRatio:0.######},aspect={aspect:0.###},conf={best.Confidence:0.###}";
            }

            Debug.WriteLine(
                $"[AutoMaskDetectionDump] runId={_options.RunId ?? "none"}, frame={frameIndex}, profile={_options.FilterProfile}, raw={rawFaceCount}, postFilter={filteredCount}, confLow3={confidenceSummary}, top={topBox}");
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

        private static IReadOnlyList<FaceDetectionResult> FilterFacesByArea(
            IReadOnlyList<FaceDetectionResult> faces,
            PixelSize size,
            FaceFilterSettings settings,
            FaceFilterStats? stats = null)
        {
            if (faces.Count == 0)
                return faces;

            double frameArea = Math.Max(1.0, size.Width * (double)size.Height);
            double minArea = frameArea * settings.MinFaceAreaRatio;
            if (minArea <= 1.0)
                return faces;

            List<FaceDetectionResult>? kept = null;
            bool filtered = false;

            for (int i = 0; i < faces.Count; i++)
            {
                var kind = ClassifyFaceCandidate(faces[i], frameArea, settings);
                if (kind != FaceCandidateKind.Rejected)
                {
                    stats?.AddKept(kind);
                    kept ??= new List<FaceDetectionResult>(faces.Count);
                    kept.Add(faces[i]);
                }
                else
                {
                    stats?.AddRejected();
                    filtered = true;
                }
            }

            if (!filtered)
                return faces;

            if (kept != null)
                return kept;
            return Array.Empty<FaceDetectionResult>();
        }

        private static unsafe IReadOnlyList<FaceDetectionResult> FilterFacesByAreaAndStats(
            IReadOnlyList<FaceDetectionResult> faces,
            PixelSize size,
            byte* basePtr,
            int stride,
            int width,
            int height,
            double scaleX,
            double scaleY,
            FaceFilterSettings settings,
            FaceFilterStats? stats = null)
        {
            if (faces.Count == 0)
                return faces;

            if (basePtr == null || stride <= 0 || width <= 0 || height <= 0)
                return FilterFacesByArea(faces, size, settings, stats);

            double frameArea = Math.Max(1.0, size.Width * (double)size.Height);
            double minArea = frameArea * settings.MinSmallFaceAreaRatio;
            if (minArea <= 1.0)
                return faces;

            List<FaceDetectionResult>? kept = null;
            bool filtered = false;

            for (int i = 0; i < faces.Count; i++)
            {
                var rect = faces[i].Bounds;
                var kind = ClassifyFaceCandidate(faces[i], frameArea, settings);
                if (kind != FaceCandidateKind.Rejected)
                {
                    if (settings.UseStatsFilter && !PassesStatsBgra(
                            basePtr,
                            stride,
                            width,
                            height,
                            rect,
                            scaleX,
                            scaleY,
                            faces[i].Confidence,
                            requireStats: kind == FaceCandidateKind.Small))
                    {
                        stats?.AddStatsRejected();
                        filtered = true;
                        continue;
                    }

                    stats?.AddKept(kind);
                    kept ??= new List<FaceDetectionResult>(faces.Count);
                    kept.Add(faces[i]);
                }
                else
                {
                    stats?.AddRejected();
                    filtered = true;
                }
            }

            if (!filtered)
                return faces;

            if (kept != null)
                return kept;
            return Array.Empty<FaceDetectionResult>();
        }

        private static FaceCandidateKind ClassifyFaceCandidate(
            FaceDetectionResult face,
            double frameArea,
            FaceFilterSettings settings)
        {
            var rect = face.Bounds;
            double area = Math.Max(0.0, rect.Width * rect.Height);
            double ratio = rect.Height > 0 ? rect.Width / rect.Height : 0.0;
            if (ratio < settings.MinFaceAspectRatio || ratio > settings.MaxFaceAspectRatio)
                return FaceCandidateKind.Rejected;

            if (area >= frameArea * settings.MinFaceAreaRatio)
                return FaceCandidateKind.Regular;

            if (area >= frameArea * settings.MinSmallFaceAreaRatio && face.Confidence >= settings.SmallFaceConfidenceMin)
                return FaceCandidateKind.Small;

            return FaceCandidateKind.Rejected;
        }

        private static FaceFilterSettings GetFaceFilterSettings(
            FaceFilterProfile profile,
            bool usePrecisionMode)
        {
            if (profile == FaceFilterProfile.Scrfd)
            {
                return new FaceFilterSettings(
                    MinFaceAreaRatio,
                    MinSmallFaceAreaRatio,
                    MinFaceAspectRatio,
                    2.5,
                    0.25f,
                    UseStatsFilter: false);
            }

            if (profile == FaceFilterProfile.Yolo)
            {
                double minSmallFaceAreaRatio = usePrecisionMode
                    ? OffModeYoloMinSmallFaceAreaRatio
                    : MinSmallFaceAreaRatio * 0.70;
                float smallFaceConfidenceMin = usePrecisionMode
                    ? OffModeYoloSmallFaceConfidenceMin
                    : 0.30f;
                double yoloMaxFaceAspectRatio = usePrecisionMode
                    ? OffModeYoloMaxFaceAspectRatio
                    : 2.7;
                bool useStatsFilter = usePrecisionMode;

                return new FaceFilterSettings(
                    MinFaceAreaRatio,
                    minSmallFaceAreaRatio,
                    MinFaceAspectRatio,
                    yoloMaxFaceAspectRatio,
                    smallFaceConfidenceMin,
                    UseStatsFilter: useStatsFilter);
            }

            return new FaceFilterSettings(
                MinFaceAreaRatio,
                MinSmallFaceAreaRatio,
                MinFaceAspectRatio,
                MaxFaceAspectRatio,
                SmallFaceConfidenceMin,
                UseStatsFilter: true);
        }

        private static float[] ExtractConfidences(IReadOnlyList<FaceDetectionResult> faces)
        {
            int count = faces.Count;
            if (count == 0)
                return Array.Empty<float>();

            var list = new float[count];
            for (int i = 0; i < count; i++)
                list[i] = faces[i].Confidence;
            return list;
        }

        private static unsafe bool PassesStatsBgra(
            byte* basePtr,
            int stride,
            int width,
            int height,
            Rect rect,
            double scaleX,
            double scaleY,
            float confidence,
            bool requireStats = false)
        {
            if (!requireStats && confidence >= StatsBypassConfidence)
                return true;
            if (basePtr == null || stride <= 0 || width <= 0 || height <= 0)
                return true;

            double x = rect.X / Math.Max(0.0001, scaleX);
            double y = rect.Y / Math.Max(0.0001, scaleY);
            double w = rect.Width / Math.Max(0.0001, scaleX);
            double h = rect.Height / Math.Max(0.0001, scaleY);
            var mapped = new Rect(x, y, w, h);
            return EvaluateFaceStatsBgra(basePtr, stride, width, height, mapped);
        }

        private static unsafe bool EvaluateFaceStatsBgra(
            byte* basePtr,
            int stride,
            int width,
            int height,
            Rect rect)
        {
            int x0 = Math.Clamp((int)Math.Floor(rect.X), 0, Math.Max(0, width - 1));
            int y0 = Math.Clamp((int)Math.Floor(rect.Y), 0, Math.Max(0, height - 1));
            int x1 = Math.Clamp((int)Math.Ceiling(rect.Right) - 1, 0, Math.Max(0, width - 1));
            int y1 = Math.Clamp((int)Math.Ceiling(rect.Bottom) - 1, 0, Math.Max(0, height - 1));

            if (x1 <= x0 || y1 <= y0)
                return false;

            int boxW = x1 - x0 + 1;
            int boxH = y1 - y0 + 1;
            int step = StatsSampleStep;
            int baseStep = (int)Math.Floor(Math.Sqrt((boxW * (double)boxH) / Math.Max(1, MinStatsSamples)));
            step = Math.Clamp(baseStep, 1, StatsSampleStep);
            int sampleCount = 0;
            int skinCount = 0;
            int edgeCount = 0;
            int edgeSamples = 0;
            double mean = 0;
            double m2 = 0;

            int samplesX = (boxW - 1) / step + 1;
            int samplesY = (boxH - 1) / step + 1;
            int totalSamples = samplesX * samplesY;
            int totalEdgeSamples = samplesY * Math.Max(0, samplesX - 1) +
                Math.Max(0, samplesY - 1) * samplesX;
            int edgeProcessed = 0;
            double minSkinNeeded = MinSkinRatio * totalSamples;
            double minEdgeNeeded = MinEdgeRatio * totalEdgeSamples;

            for (int y = y0; y <= y1; y += step)
            {
                byte* row = basePtr + y * stride;
                for (int x = x0; x <= x1; x += step)
                {
                    byte* p = row + x * 4;
                    byte b = p[0];
                    byte g = p[1];
                    byte r = p[2];

                    int luma = (77 * r + 150 * g + 29 * b) >> 8;
                    sampleCount++;

                    double delta = luma - mean;
                    mean += delta / sampleCount;
                    m2 += delta * (luma - mean);

                    int cb = 128 + ((-43 * r - 85 * g + 128 * b) >> 8);
                    int cr = 128 + ((128 * r - 107 * g - 21 * b) >> 8);
                    if (cb >= 77 && cb <= 127 && cr >= 133 && cr <= 173)
                        skinCount++;

                    if (x + step <= x1)
                    {
                        byte* pr = row + (x + step) * 4;
                        int l2 = (77 * pr[2] + 150 * pr[1] + 29 * pr[0]) >> 8;
                        if (Math.Abs(luma - l2) > 20)
                            edgeCount++;
                        edgeSamples++;
                        edgeProcessed++;
                    }

                    if (y + step <= y1)
                    {
                        byte* pd = basePtr + (y + step) * stride + x * 4;
                        int l2 = (77 * pd[2] + 150 * pd[1] + 29 * pd[0]) >> 8;
                        if (Math.Abs(luma - l2) > 20)
                            edgeCount++;
                        edgeSamples++;
                        edgeProcessed++;
                    }

                    int remainingSamples = totalSamples - sampleCount;
                    if (skinCount + remainingSamples < minSkinNeeded)
                        return false;
                    int remainingEdges = totalEdgeSamples - edgeProcessed;
                    if (edgeCount + remainingEdges < minEdgeNeeded)
                        return false;
                }
            }

            if (sampleCount < MinStatsSamples)
                return false;
            double variance = sampleCount > 1 ? m2 / (sampleCount - 1) : 0.0;
            double skinRatio = skinCount / (double)Math.Max(1, sampleCount);
            double edgeRatio = edgeCount / (double)Math.Max(1, edgeSamples);

            return skinRatio >= MinSkinRatio &&
                edgeRatio >= MinEdgeRatio &&
                variance >= MinLumaVariance;
        }

        private enum FaceCandidateKind
        {
            Rejected,
            Regular,
            Small
        }

        private static float? GetMinConfidence(IReadOnlyList<FaceDetectionResult> faces)
        {
            if (faces == null || faces.Count == 0)
                return null;

            float min = float.MaxValue;
            for (int i = 0; i < faces.Count; i++)
            {
                float conf = faces[i].Confidence;
                if (conf < min)
                    min = conf;
            }

            return min == float.MaxValue ? null : min;
        }

        private unsafe static (double fps, int totalFrames, double durationSeconds) ReadVideoInfo(string path)
        {
            AVFormatContext* fmt = null;

            try
            {
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_QUIET);

                int openResult = ffmpeg.avformat_open_input(&fmt, path, null, null);
                FFmpegErrorHelper.ThrowIfError(openResult, $"Failed to open video: {path}");

                int streamInfo = ffmpeg.avformat_find_stream_info(fmt, null);
                FFmpegErrorHelper.ThrowIfError(streamInfo, $"Failed to read stream info: {path}");

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
