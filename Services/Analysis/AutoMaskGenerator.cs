using Avalonia;
using Avalonia.Media.Imaging;
using FaceShield.Services.Diagnostics;
using FaceShield.Services.FaceDetection;
using FaceShield.Services.Video;
using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.ExceptionServices;
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
    public sealed partial class AutoMaskGenerator
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
        private const double OffModeSceneCutSignatureDiffThreshold = 0.24;
        private const int OffModeSceneCutCarryClearFrames = 2;

        private readonly record struct FaceFilterSettings(
            double MinFaceAreaRatio,
            double MinSmallFaceAreaRatio,
            double MinFaceAspectRatio,
            double MaxFaceAspectRatio,
            float SmallFaceConfidenceMin,
            bool UseStatsFilter);

        private static bool IsHardwareTransferFailure(FfFrameExtractor extractor)
        {
            if (extractor.HardwareTransferFailed)
                return true;

            string status = extractor.DecodeStatus;
            string? error = extractor.DecodeError;

            if (!string.IsNullOrWhiteSpace(error) &&
                error.Contains("av_hwframe_transfer_data 실패", StringComparison.Ordinal))
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(status) &&
                status.Contains("HW 프레임 전송 실패", StringComparison.Ordinal);
        }

        private static FfFrameExtractor CreateExtractorWithFallback(
            string videoPath,
            int startFrameIndex,
            bool useRaw,
            CancellationToken ct)
        {
            var extractor = new FfFrameExtractor(videoPath, enableHardware: true, cancellationToken: ct);

            try
            {
                extractor.StartSequentialRead(startFrameIndex, ct);

                bool ok = useRaw
                    ? extractor.TryGetNextFrameRaw(ct, requireBgra: true, out _, out _)
                    : extractor.TryGetNextFrame(ct, requireBitmap: true, out _, out _);

                if (!ok && !ct.IsCancellationRequested && IsHardwareTransferFailure(extractor))
                {
                    Debug.WriteLine("[AutoMask] HW decode failed; falling back to SW.");
                    extractor.Dispose();
                    extractor = new FfFrameExtractor(videoPath, enableHardware: false, cancellationToken: ct);
                }
            }
            catch
            {
                extractor.Dispose();
                throw;
            }

            extractor.StartSequentialRead(startFrameIndex, ct);
            return extractor;
        }
        private readonly IFaceDetector _detector;
        private readonly FrameMaskProvider _maskProvider;
        private readonly AutoMaskOptions _options;
        private readonly FaceFilterSettings _faceFilterSettings;
        private readonly IFaceDetectorFactory? _detectorFactory;
        private readonly Dictionary<int, FrameTimingSample> _frameTimings = new();
        private readonly HashSet<int> _sceneCutStarts = new();
        private double _sourceFpsForSummary;
        private int _postProcessStartFrameIndex;

        public AutoMaskRunSummary? LastRunSummary { get; private set; }

        public AutoMaskGenerator(
            IFaceDetector detector,
            FrameMaskProvider maskProvider,
            AutoMaskOptions? options = null,
            IFaceDetectorFactory? detectorFactory = null)
        {
            _detector = detector ?? throw new ArgumentNullException(nameof(detector));
            _maskProvider = maskProvider ?? throw new ArgumentNullException(nameof(maskProvider));
            _options = (options ?? new AutoMaskOptions()).ResolveProcessingMode();
            _faceFilterSettings = GetRuntimeFaceFilterSettings(_options);
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

            ct.ThrowIfCancellationRequested();
            VideoMetadataInfo metadata =
                VideoMetadataReader.Read(videoPath, ct);
            double fps = metadata.Fps;
            int totalFrames = metadata.GetFrameCountEstimate();

            if (fps <= 0)
                return;

            int requestedStartFrameIndex = Math.Max(0, startFrameIndex);
            int effectiveStartFrameIndex = ResolveDecodeStartFrameIndex(requestedStartFrameIndex);
            if (effectiveStartFrameIndex < requestedStartFrameIndex)
            {
                onFrameProcessed?.Invoke(effectiveStartFrameIndex);
                int removed = _maskProvider.GetFaceMaskEntries().Count;
                _maskProvider.ClearFaceMasks();
                Debug.WriteLine(
                    $"[AutoMaskResumeReset] reason=tracked-continuity-requires-full-timeline requestedStart={requestedStartFrameIndex} effectiveStart={effectiveStartFrameIndex} removedFaceMasks={removed}");
            }

            _sourceFpsForSummary = fps;
            _postProcessStartFrameIndex = effectiveStartFrameIndex;
            _frameTimings.Clear();
            _sceneCutStarts.Clear();
            if (_postProcessStartFrameIndex > 0)
                _sceneCutStarts.Add(_postProcessStartFrameIndex);

            LastRunSummary = null;

            try
            {
                await Task.Run(() =>
                {
                    AutoMaskGenerationStrategyKind strategy =
                        AutoMaskGenerationStrategyPlanner.Resolve(
                            _options,
                            _detector,
                            _detectorFactory);

                    switch (strategy)
                    {
                        case AutoMaskGenerationStrategyKind.PipelinedParallel:
                        {
                            var primary = (IBgraFaceDetector)_detector;
                            using var pool = AutoMaskGenerationStrategyPlanner.CreateCompatibleDetectorPool(
                                primary,
                                _detectorFactory!,
                                _options.ParallelDetectorCount);
                            if (pool.Detectors.Count > 1)
                            {
                                Debug.WriteLine($"[AutoMask] mode=pipe-parallel({pool.Detectors.Count})");
                                GeneratePipelinedDetectAllParallel(
                                    videoPath,
                                    pool.Detectors,
                                    progress,
                                    ct,
                                    effectiveStartFrameIndex,
                                    totalFrames,
                                    onFrameProcessed);
                                return;
                            }

                            Debug.WriteLine("[AutoMask] mode=pipe-single");
                            GeneratePipelinedDetectAll(
                                videoPath,
                                primary,
                                progress,
                                ct,
                                effectiveStartFrameIndex,
                                totalFrames,
                                onFrameProcessed);
                            return;
                        }

                        case AutoMaskGenerationStrategyKind.PipelinedSingle:
                            Debug.WriteLine("[AutoMask] mode=pipe-single");
                            GeneratePipelinedDetectAll(
                                videoPath,
                                (IBgraFaceDetector)_detector,
                                progress,
                                ct,
                                effectiveStartFrameIndex,
                                totalFrames,
                                onFrameProcessed);
                            return;

                        case AutoMaskGenerationStrategyKind.SparsePipelinedParallel:
                        {
                            var primary = (IBgraFaceDetector)_detector;
                            using var pool = AutoMaskGenerationStrategyPlanner.CreateCompatibleDetectorPool(
                                primary,
                                _detectorFactory!,
                                _options.ParallelDetectorCount);
                            Debug.WriteLine($"[AutoMask] mode=sparse-pipe-parallel({pool.Detectors.Count})");
                            GenerateSparsePipelinedTrackingParallel(
                                videoPath,
                                pool.Detectors,
                                progress,
                                ct,
                                effectiveStartFrameIndex,
                                totalFrames,
                                onFrameProcessed);
                            return;
                        }

                        default:
                            break;
                    }

                    Debug.WriteLine("[AutoMask] mode=sequential");
                    GenerateSequential(videoPath, progress, ct, effectiveStartFrameIndex, totalFrames, onFrameProcessed);
                }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }

            if (ShouldRestartAfterOutOfRangeResume(LastRunSummary, effectiveStartFrameIndex, ct))
            {
                onFrameProcessed?.Invoke(0);
                int removed = _maskProvider.GetFaceMaskEntries().Count;
                _maskProvider.ClearFaceMasks();
                Debug.WriteLine(
                    $"[AutoMaskResumeReset] reason=resume-start-beyond-decoder-eof requestedStart={requestedStartFrameIndex} effectiveStart={effectiveStartFrameIndex} removedFaceMasks={removed}");
                await GenerateAsync(
                    videoPath,
                    progress,
                    ct,
                    startFrameIndex: 0,
                    onFrameProcessed: onFrameProcessed);
            }
        }

        private int ResolveDecodeStartFrameIndex(int requestedStartFrameIndex)
        {
            int start = Math.Max(0, requestedStartFrameIndex);
            bool sparsePipelineAvailable =
                AutoMaskGenerationStrategyPlanner.Resolve(
                    _options,
                    _detector,
                    _detectorFactory) ==
                AutoMaskGenerationStrategyKind.SparsePipelinedParallel;
            bool requiresFullTimeline = start > 0 &&
                (!CanResumeFromFrame(_options, start) ||
                 (_options.UseTracking && !sparsePipelineAvailable));
            return requiresFullTimeline ? 0 : start;
        }

        public static bool RequiresFullTimelineResume(AutoMaskOptions? options)
        {
            AutoMaskOptions effective = (options ?? new AutoMaskOptions()).ResolveProcessingMode();
            bool legacyYoloMissRecovery =
                effective.ProcessingMode == AutoMaskProcessingMode.Legacy &&
                effective.FilterProfile == FaceFilterProfile.Yolo &&
                effective.UseTracking &&
                !effective.EnablePostProcessing;
            return effective.UseTracking &&
                (effective.DetectEveryNFrames <= 1 ||
                 effective.EnablePostProcessing ||
                 effective.EnableYoloGapFill ||
                 effective.EnableYoloWeakIsolatedCleanup ||
                 effective.EnableYoloSceneCutCarryCleanup ||
                 effective.EnableYoloTemporalSmoothing ||
                 effective.EnableYoloRiskCascade ||
                 legacyYoloMissRecovery);
        }

        public static bool CanResumeFromFrame(AutoMaskOptions? options, int resumeFrameIndex)
        {
            int start = Math.Max(0, resumeFrameIndex);
            if (start == 0)
                return true;

            AutoMaskOptions effective = (options ?? new AutoMaskOptions()).ResolveProcessingMode();
            if (RequiresFullTimelineResume(effective))
                return false;
            if (!effective.UseTracking)
                return true;

            int interval = Math.Max(1, effective.DetectEveryNFrames);
            return interval > 1 && start % interval == 0;
        }

        private static bool ShouldRestartAfterOutOfRangeResume(
            AutoMaskRunSummary? summary,
            int effectiveStartFrameIndex,
            CancellationToken ct)
        {
            return effectiveStartFrameIndex > 0 &&
                !ct.IsCancellationRequested &&
                summary != null &&
                summary.ReachedDecoderEof &&
                !summary.DecodeCancelled &&
                string.Equals(summary.DecodeError, "none", StringComparison.OrdinalIgnoreCase) &&
                summary.DecodedFrames == 0;
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

        private sealed class ProgressState
        {
            public int LastPercent = -1;
        }

        private sealed class PipelineExecutionScope : IDisposable
        {
            private readonly CancellationToken _callerToken;
            private readonly CancellationTokenSource _pipelineCancellation;
            private ExceptionDispatchInfo? _firstFailure;

            public PipelineExecutionScope(CancellationToken callerToken)
            {
                _callerToken = callerToken;
                _pipelineCancellation = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
            }

            public CancellationToken Token => _pipelineCancellation.Token;

            public bool HasFailure => Volatile.Read(ref _firstFailure) != null;

            public Task Start(Action action)
            {
                return Task.Run(() => Execute(action));
            }

            public void Execute(Action action)
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Capture(ex);
                }
            }

            public void Capture(Exception exception)
            {
                if (exception is OperationCanceledException &&
                    (_callerToken.IsCancellationRequested ||
                     (_pipelineCancellation.IsCancellationRequested &&
                      Volatile.Read(ref _firstFailure) != null)))
                {
                    return;
                }

                CaptureFailure(exception);
            }

            public void ThrowIfFailedOrCanceled()
            {
                ExceptionDispatchInfo? failure = Volatile.Read(ref _firstFailure);
                if (failure != null)
                    failure.Throw();

                _callerToken.ThrowIfCancellationRequested();
            }

            public void Dispose()
            {
                _pipelineCancellation.Dispose();
            }

            private void CaptureFailure(Exception exception)
            {
                var captured = ExceptionDispatchInfo.Capture(exception);
                if (Interlocked.CompareExchange(ref _firstFailure, captured, null) != null)
                    return;

                try
                {
                    _pipelineCancellation.Cancel();
                }
                catch (Exception cancellationException)
                {
                    Debug.WriteLine(
                        $"[AutoMaskPipeline] cancellation callback failed: {cancellationException.Message}");
                }
            }
        }

        private static void ReturnQueuedBuffers(
            System.Collections.Concurrent.BlockingCollection<BgraBuffer> queue,
            System.Buffers.ArrayPool<byte> pool)
        {
            while (queue.TryTake(out BgraBuffer? item))
            {
                if (item.Data.Length > 0)
                    pool.Return(item.Data);
            }
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
            int start = Math.Max(0, startFrameIndex);
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
            int offModeSceneCutResetPairs = 0;
            int offModeSceneCutResetRemovedFrameCount = 0;
            int offModeSceneCutResetBeforeWindowFrameCount = 0;
            int offModeSceneCutResetAfterWindowFrameCount = 0;
            int offModeSceneCutResetRemovedBeforeFrameCount = 0;
            int offModeSceneCutResetRemovedAfterFrameCount = 0;
            bool collectSceneCutBoundaries = _options.UseTracking &&
                _options.ProcessingMode != AutoMaskProcessingMode.Raw &&
                (_options.FilterProfile == FaceFilterProfile.Yolo ||
                 _options.ProcessingMode != AutoMaskProcessingMode.Legacy);
            bool applyOffModeSceneCutReset = collectSceneCutBoundaries &&
                _options.ProcessingMode == AutoMaskProcessingMode.Legacy &&
                !_options.EnablePostProcessing;
            using var pipeline = new PipelineExecutionScope(ct);
            CancellationToken pipelineToken = pipeline.Token;

            var producer = pipeline.Start(() =>
            {
                try
                {
                    while (true)
                    {
                        pipelineToken.ThrowIfCancellationRequested();
                        var tDecode = Stopwatch.StartNew();
                        int targetW = useProxy ? proxyWidth : fullSize.Width;
                        int targetH = useProxy ? proxyHeight : fullSize.Height;
                        int stride;
                        int idx;
                        int size = targetW * 4 * targetH;
                        var buffer = pool.Rent(size);
                        bool queued = false;
                        try
                        {
                            bool ok = extractor.TryGetNextFrameRawToBuffer(
                                pipelineToken,
                                targetW,
                                targetH,
                                useBilinear,
                                buffer,
                                out idx,
                                out stride);
                            tDecode.Stop();
                            decodeMs += tDecode.ElapsedMilliseconds;
                            if (!ok)
                                break;

                            RecordFrameTiming(extractor, idx);

                            double[] sceneSignature = Array.Empty<double>();
                            if (collectSceneCutBoundaries)
                            {
                                unsafe
                                {
                                    fixed (byte* src = buffer)
                                    {
                                        sceneSignature = ComputeFrameSignature(src, stride, targetW, targetH);
                                    }
                                }
                            }

                            queue.Add(new BgraBuffer
                            {
                                Index = idx,
                                Data = buffer,
                                Stride = stride,
                                Width = targetW,
                                Height = targetH,
                                SceneSignature = sceneSignature
                            }, pipelineToken);
                            queued = true;
                        }
                        finally
                        {
                            if (!queued)
                                pool.Return(buffer);
                        }
                    }
                }
                finally
                {
                    queue.CompleteAdding();
                }
            });

            var writer = pipeline.Start(() =>
            {
                double[]? previousSceneSignature = null;

                foreach (var result in results.GetConsumingEnumerable(pipelineToken))
                {
                    if (collectSceneCutBoundaries &&
                        previousSceneSignature != null &&
                        result.FrameSignature.Length > 0 &&
                        previousSceneSignature.Length > 0)
                    {
                        double diff = ComputeSignatureDifference(result.FrameSignature, previousSceneSignature);
                        if (diff >= OffModeSceneCutSignatureDiffThreshold)
                        {
                            _sceneCutStarts.Add(result.Index);
                            if (applyOffModeSceneCutReset)
                                offModeSceneCutResetPairs++;
                            Debug.WriteLine(
                                $"[AutoMask] scene-cut boundary idx={result.Index} legacyReset={applyOffModeSceneCutReset.ToString().ToLowerInvariant()} preservedMasks=true diff={diff:0.###} phase=pipe");
                        }
                    }

                    if (result.FrameSignature.Length > 0)
                        previousSceneSignature = result.FrameSignature;

                    onFrameProcessed?.Invoke(result.Index);

                    if (result.Bounds.Length > 0)
                    {
                        pipelineToken.ThrowIfCancellationRequested();
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
            });

            try
            {
                foreach (var item in queue.GetConsumingEnumerable(pipelineToken))
                {
                    try
                    {
                        pipelineToken.ThrowIfCancellationRequested();
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
                                        ShouldUsePrimaryRoiShortcut(_options) ? lastFaces : null,
                                        fullSize,
                                        scaleX,
                                        scaleY,
                                        roiStats);
                                    tDetect.Stop();
                                    detectMs += tDetect.ElapsedMilliseconds;

                                    if (faces.Count > 0)
                                    {
                                        int rawFaceCount = faces.Count;
                                        if (_options.ProcessingMode != AutoMaskProcessingMode.Raw)
                                        {
                                            faces = FilterFacesByAreaAndStats(
                                                faces,
                                                resultSize,
                                                src,
                                                item.Stride,
                                                item.Width,
                                                item.Height,
                                                useProxy ? scaleX : 1.0,
                                                useProxy ? scaleY : 1.0,
                                                _faceFilterSettings,
                                                filterStats);
                                        }
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

                        results.Add(new DetectionResult
                        {
                            Index = item.Index,
                            Bounds = bounds,
                            Size = resultSize,
                            MinConfidence = minConfidence,
                            Confidences = confidences,
                            FrameSignature = item.SceneSignature
                        }, pipelineToken);
                    }
                    finally
                    {
                        pool.Return(item.Data);
                    }
                }
            }
            catch (Exception ex)
            {
                pipeline.Capture(ex);
            }
            finally
            {
                results.CompleteAdding();
                Task.WaitAll(producer, writer);
                ReturnQueuedBuffers(queue, pool);
            }
            pipeline.ThrowIfFailedOrCanceled();

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
                GetDetectorName())
            {
                FinalOffModeSceneCutResetPairCount = offModeSceneCutResetPairs,
                FinalOffModeSceneCutResetRemovedFrameCount = offModeSceneCutResetRemovedFrameCount,
                FinalOffModeSceneCutResetBeforeWindowFrameCount = offModeSceneCutResetBeforeWindowFrameCount,
                FinalOffModeSceneCutResetAfterWindowFrameCount = offModeSceneCutResetAfterWindowFrameCount,
                FinalOffModeSceneCutResetRemovedBeforeFrameCount = offModeSceneCutResetRemovedBeforeFrameCount,
                FinalOffModeSceneCutResetRemovedAfterFrameCount = offModeSceneCutResetRemovedAfterFrameCount
            });
            FinalizeRunAfterDecode(extractor, videoPath, totalFrames, progress, ct);
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

            using var extractor = new FfFrameExtractor(videoPath, cancellationToken: ct);
                    using var frame = extractor.GetFrameByIndex(frameIndex, ct);
                    if (frame == null)
                    {
                        progress?.Report(100);
                        return false;
                    }

                    var faces = DetectFacesWithOptions(frame);
                    ct.ThrowIfCancellationRequested();
                    if (faces.Count > 0)
                    {
                        int rawFaceCount = faces.Count;
                        if (_options.ProcessingMode != AutoMaskProcessingMode.Raw)
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
                                    _faceFilterSettings);
                            }
                            ct.ThrowIfCancellationRequested();
                        }
                        WriteDetectionDiagnostics(frameIndex, rawFaceCount, faces, frame.PixelSize);
                    }

                    ct.ThrowIfCancellationRequested();
                    if (faces != null && faces.Count > 0)
                    {
                        var payload = BuildMaskPayload(faces);
                        ct.ThrowIfCancellationRequested();
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
            int start = Math.Max(0, startFrameIndex);
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
            int offModeSceneCutResetPairs = 0;
            int offModeSceneCutResetRemovedFrameCount = 0;
            int offModeSceneCutResetBeforeWindowFrameCount = 0;
            int offModeSceneCutResetAfterWindowFrameCount = 0;
            int offModeSceneCutResetRemovedBeforeFrameCount = 0;
            int offModeSceneCutResetRemovedAfterFrameCount = 0;
            bool collectSceneCutBoundaries = _options.UseTracking &&
                _options.ProcessingMode != AutoMaskProcessingMode.Raw &&
                (_options.FilterProfile == FaceFilterProfile.Yolo ||
                 _options.ProcessingMode != AutoMaskProcessingMode.Legacy);
            bool applyOffModeSceneCutReset = collectSceneCutBoundaries &&
                _options.ProcessingMode == AutoMaskProcessingMode.Legacy &&
                !_options.EnablePostProcessing;
            using var pipeline = new PipelineExecutionScope(ct);
            CancellationToken pipelineToken = pipeline.Token;

            var producer = pipeline.Start(() =>
            {
                try
                {
                    while (true)
                    {
                        pipelineToken.ThrowIfCancellationRequested();
                        var tDecode = Stopwatch.StartNew();
                        int targetW = useProxy ? proxyWidth : fullSize.Width;
                        int targetH = useProxy ? proxyHeight : fullSize.Height;
                        int stride;
                        int idx;
                        int size = targetW * 4 * targetH;
                        var buffer = pool.Rent(size);
                        bool queued = false;
                        try
                        {
                            bool ok = extractor.TryGetNextFrameRawToBuffer(
                                pipelineToken,
                                targetW,
                                targetH,
                                useBilinear,
                                buffer,
                                out idx,
                                out stride);
                            tDecode.Stop();
                            Interlocked.Add(ref decodeMs, tDecode.ElapsedMilliseconds);
                            if (!ok)
                                break;

                            RecordFrameTiming(extractor, idx);

                            double[] sceneSignature = Array.Empty<double>();
                            if (collectSceneCutBoundaries)
                            {
                                unsafe
                                {
                                    fixed (byte* src = buffer)
                                    {
                                        sceneSignature = ComputeFrameSignature(src, stride, targetW, targetH);
                                    }
                                }
                            }

                            queue.Add(new BgraBuffer
                            {
                                Index = idx,
                                Data = buffer,
                                Stride = stride,
                                Width = targetW,
                                Height = targetH,
                                SceneSignature = sceneSignature
                            }, pipelineToken);
                            queued = true;
                        }
                        finally
                        {
                            if (!queued)
                                pool.Return(buffer);
                        }
                    }
                }
                finally
                {
                    queue.CompleteAdding();
                }
            });

            var consumers = new List<Task>(detectors.Count);
            foreach (var detector in detectors)
            {
                consumers.Add(pipeline.Start(() =>
                {
                    foreach (var item in queue.GetConsumingEnumerable(pipelineToken))
                    {
                        try
                        {
                            pipelineToken.ThrowIfCancellationRequested();
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
                                            if (_options.ProcessingMode != AutoMaskProcessingMode.Raw)
                                            {
                                                faces = FilterFacesByAreaAndStats(
                                                    faces,
                                                    resultSize,
                                                    src,
                                                    item.Stride,
                                                    item.Width,
                                                    item.Height,
                                                    useProxy ? scaleX : 1.0,
                                                    useProxy ? scaleY : 1.0,
                                                    _faceFilterSettings,
                                                    filterStats);
                                            }
                                            WriteDetectionDiagnostics(item.Index, rawFaceCount, faces, resultSize);
                                            var payload = BuildMaskPayload(faces);
                                            bounds = payload.Bounds;
                                            confidences = payload.Confidences;
                                            minConfidence = payload.MinConfidence;
                                        }
                                    }
                                }
                            }

                            results.Add(new DetectionResult
                            {
                                Index = item.Index,
                                Bounds = bounds,
                                Size = resultSize,
                                MinConfidence = minConfidence,
                                Confidences = confidences,
                                FrameSignature = item.SceneSignature
                            }, pipelineToken);
                        }
                        finally
                        {
                            pool.Return(item.Data);
                        }
                    }
                }));
            }

            var writer = pipeline.Start(() =>
            {
                var orderedResults = new Dictionary<int, DetectionResult>();
                int nextFrameToWrite = start;
                double[]? previousSceneSignature = null;

                foreach (var result in results.GetConsumingEnumerable(pipelineToken))
                {
                    orderedResults[result.Index] = result;

                    while (orderedResults.TryGetValue(nextFrameToWrite, out var orderedResult))
                    {
                        orderedResults.Remove(nextFrameToWrite);

                        if (collectSceneCutBoundaries &&
                            previousSceneSignature != null &&
                            orderedResult.FrameSignature.Length > 0 &&
                            previousSceneSignature.Length > 0)
                        {
                            double diff = ComputeSignatureDifference(orderedResult.FrameSignature, previousSceneSignature);
                            if (diff >= OffModeSceneCutSignatureDiffThreshold)
                            {
                                _sceneCutStarts.Add(orderedResult.Index);
                                if (applyOffModeSceneCutReset)
                                    offModeSceneCutResetPairs++;
                                Debug.WriteLine(
                                    $"[AutoMask] scene-cut boundary idx={orderedResult.Index} legacyReset={applyOffModeSceneCutReset.ToString().ToLowerInvariant()} preservedMasks=true diff={diff:0.###} phase=pipe-parallel");
                            }
                        }

                        if (orderedResult.FrameSignature.Length > 0)
                            previousSceneSignature = orderedResult.FrameSignature;

                        onFrameProcessed?.Invoke(orderedResult.Index);
                        if (orderedResult.Bounds.Length > 0)
                        {
                            pipelineToken.ThrowIfCancellationRequested();
                            _maskProvider.SetFaceRects(
                                orderedResult.Index,
                                orderedResult.Bounds,
                                orderedResult.Size,
                                orderedResult.MinConfidence,
                                orderedResult.Confidences);
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
            });

            Task.WaitAll(consumers.ToArray());
            results.CompleteAdding();
            Task.WaitAll(producer, writer);
            ReturnQueuedBuffers(queue, pool);
            pipeline.ThrowIfFailedOrCanceled();

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
                GetDetectorName())
            {
                FinalOffModeSceneCutResetPairCount = offModeSceneCutResetPairs,
                FinalOffModeSceneCutResetRemovedFrameCount = offModeSceneCutResetRemovedFrameCount,
                FinalOffModeSceneCutResetBeforeWindowFrameCount = offModeSceneCutResetBeforeWindowFrameCount,
                FinalOffModeSceneCutResetAfterWindowFrameCount = offModeSceneCutResetAfterWindowFrameCount,
                FinalOffModeSceneCutResetRemovedBeforeFrameCount = offModeSceneCutResetRemovedBeforeFrameCount,
                FinalOffModeSceneCutResetRemovedAfterFrameCount = offModeSceneCutResetRemovedAfterFrameCount
            });
            FinalizeRunAfterDecode(extractor, videoPath, totalFrames, progress, ct);
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
            int start = Math.Max(0, startFrameIndex);
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
            using var pipeline = new PipelineExecutionScope(ct);
            CancellationToken pipelineToken = pipeline.Token;

            var producer = pipeline.Start(() =>
            {
                int nextIndex = start;
                try
                {
                    while (true)
                    {
                        pipelineToken.ThrowIfCancellationRequested();
                        bool shouldDetect = nextIndex == start || nextIndex % interval == 0;
                        int idx;
                        if (!shouldDetect)
                        {
                            var tSkip = Stopwatch.StartNew();
                            if (useProxy)
                            {
                                if (!extractor.TryGetNextFrameRawScaled(pipelineToken, requireBgra: false, targetW, targetH, useBilinear, out _, out idx))
                                    break;
                            }
                            else
                            {
                                if (!extractor.TryGetNextFrameRaw(pipelineToken, requireBgra: false, out _, out idx))
                                    break;
                            }
                            tSkip.Stop();
                            Interlocked.Add(ref decodeMs, tSkip.ElapsedMilliseconds);

                            RecordFrameTiming(extractor, idx);

                            nextIndex = idx + 1;
                            highestDecodedFrame = idx;
                            ReportProgress(progress, idx, totalFrames, progressState);
                            Interlocked.Increment(ref decoded);
                            continue;
                        }

                        int stride;
                        int size = targetW * 4 * targetH;
                        byte[] buffer = pool.Rent(size);
                        bool queued = false;
                        try
                        {
                            var tDecode = Stopwatch.StartNew();
                            bool ok = extractor.TryGetNextFrameRawToBuffer(
                                pipelineToken,
                                targetW,
                                targetH,
                                useBilinear,
                                buffer,
                                out idx,
                                out stride);
                            tDecode.Stop();
                            Interlocked.Add(ref decodeMs, tDecode.ElapsedMilliseconds);
                            if (!ok)
                                break;

                            RecordFrameTiming(extractor, idx);

                            nextIndex = idx + 1;
                            highestDecodedFrame = idx;
                            ReportProgress(progress, idx, totalFrames, progressState);
                            Interlocked.Increment(ref decoded);

                            if (_maskProvider.HasEntry(idx))
                                continue;

                            queue.Add(new BgraBuffer
                            {
                                Index = idx,
                                Data = buffer,
                                Stride = stride,
                                Width = targetW,
                                Height = targetH
                            }, pipelineToken);
                            queued = true;
                        }
                        finally
                        {
                            if (!queued)
                                pool.Return(buffer);
                        }
                    }
                }
                finally
                {
                    queue.CompleteAdding();
                }
            });

            var consumers = new List<Task>(detectors.Count);
            foreach (var detector in detectors)
            {
                consumers.Add(pipeline.Start(() =>
                {
                    foreach (var item in queue.GetConsumingEnumerable(pipelineToken))
                    {
                        try
                        {
                            pipelineToken.ThrowIfCancellationRequested();
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
                                        if (_options.ProcessingMode != AutoMaskProcessingMode.Raw)
                                        {
                                            faces = FilterFacesByAreaAndStats(
                                                faces,
                                                resultSize,
                                                src,
                                                item.Stride,
                                                item.Width,
                                                item.Height,
                                                useProxy ? scaleX : 1.0,
                                                useProxy ? scaleY : 1.0,
                                                _faceFilterSettings,
                                                filterStats);
                                        }
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
                }));
            }

            Task.WaitAll(consumers.Append(producer).ToArray());
            ReturnQueuedBuffers(queue, pool);
            int decodedEndExclusive = highestDecodedFrame >= start
                ? highestDecodedFrame == int.MaxValue
                    ? int.MaxValue
                    : highestDecodedFrame + 1
                : start;
            bool completedAtDecoderEof =
                !pipeline.HasFailure &&
                !ct.IsCancellationRequested &&
                extractor.SequentialReachedEndOfStream &&
                !extractor.SequentialReadCancelled &&
                string.IsNullOrWhiteSpace(extractor.SequentialDecodeError);
            int resumeWatermark = completedAtDecoderEof
                ? decodedEndExclusive - 1
                : FindSparseResumeWatermark(results, start, decodedEndExclusive, interval);
            int materializeEndExclusive = resumeWatermark >= start
                ? resumeWatermark + 1
                : start;
            pipeline.ThrowIfFailedOrCanceled();
            pipelineToken.ThrowIfCancellationRequested();
            var sparseWorkingCopy = _maskProvider.CreateSparseFaceMaskWorkingCopy(pipelineToken);
            var materialized = SparseTrackingMaterializer.Materialize(
                results,
                sparseWorkingCopy,
                _options,
                start,
                materializeEndExclusive,
                pipelineToken);
            pipelineToken.ThrowIfCancellationRequested();
            _maskProvider.CommitFaceMasksFrom(
                sparseWorkingCopy,
                pipelineToken);
            foreach (var transition in materialized.SceneCutTransitions)
            {
                pipelineToken.ThrowIfCancellationRequested();
                for (int frame = transition.SourceFrameIndex + 1; frame <= transition.NextFrameIndex; frame++)
                {
                    pipelineToken.ThrowIfCancellationRequested();
                    _sceneCutStarts.Add(frame);
                }
            }
            int interpolated = materialized.Interpolated;

            if (resumeWatermark >= start)
                pipeline.Execute(() => onFrameProcessed?.Invoke(resumeWatermark));

            pipeline.ThrowIfFailedOrCanceled();

            Debug.WriteLine(
                $"[AutoMaskSparsePipe] done decoded={decoded}, detects={detected}, interpolated={interpolated}, sparseSceneCuts={materialized.SceneCutStops}, sparseSceneCutPairs={SparseTrackingMaterializer.FormatSceneCutTransitions(materialized.SceneCutTransitions)}, decodeMs={decodeMs}, detectMs={detectMs}, totalMs={swTotal.ElapsedMilliseconds}, filter={filterStats.BuildSummary()}");
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
            FinalizeRunAfterDecode(extractor, videoPath, totalFrames, progress, ct);
        }

        private static int FindSparseResumeWatermark(
            System.Collections.Concurrent.ConcurrentDictionary<int, DetectionResult> results,
            int start,
            int decodedEndExclusive,
            int interval)
        {
            if (decodedEndExclusive <= start || !results.ContainsKey(start))
                return -1;

            int lastContiguousAnchor = start;
            int lastPositiveAnchor = results.TryGetValue(start, out var startResult) &&
                startResult.Bounds.Length > 0
                    ? start
                    : -1;
            int current = start;
            int detectInterval = Math.Max(1, interval);
            while (true)
            {
                long nextLong = ((long)current / detectInterval + 1L) * detectInterval;
                if (nextLong >= decodedEndExclusive || nextLong > int.MaxValue)
                    break;

                int next = (int)nextLong;
                if (!results.ContainsKey(next))
                    break;

                lastContiguousAnchor = next;
                if (results.TryGetValue(next, out var result) && result.Bounds.Length > 0)
                    lastPositiveAnchor = next;
                current = next;
            }

            return lastPositiveAnchor >= start
                ? lastPositiveAnchor
                : lastContiguousAnchor;
        }

        private void FinalizeRunAfterDecode(
            FfFrameExtractor extractor,
            string videoPath,
            int totalFrames,
            IProgress<int>? progress,
            CancellationToken ct)
        {
            if (LastRunSummary == null)
                return;

            bool decodeCancelled = ct.IsCancellationRequested || extractor.SequentialReadCancelled;
            string decodeError = string.IsNullOrWhiteSpace(extractor.SequentialDecodeError)
                ? "none"
                : extractor.SequentialDecodeError;
            bool reachedDecoderEof = extractor.SequentialReachedEndOfStream;
            int effectiveTotalFrames = ResolveEffectiveTotalFrames(
                totalFrames,
                LastRunSummary.StartFrameIndex,
                LastRunSummary.DecodedFrames,
                reachedDecoderEof,
                decodeCancelled,
                decodeError);
            LastRunSummary = LastRunSummary with
            {
                TotalFrames = effectiveTotalFrames,
                ReachedDecoderEof = reachedDecoderEof,
                DecodeCancelled = decodeCancelled,
                DecodeError = decodeError
            };

            if (reachedDecoderEof && effectiveTotalFrames != totalFrames)
            {
                Debug.WriteLine(
                    $"[AutoRunFrameCountAdjusted] runId={LastRunSummary.RunId ?? "n/a"}, reported={totalFrames}, actual={effectiveTotalFrames}, startFrame={LastRunSummary.StartFrameIndex}, decoded={LastRunSummary.DecodedFrames}");
            }

            int expectedFrames = Math.Max(0, effectiveTotalFrames - LastRunSummary.StartFrameIndex);
            bool resumeStartBeyondEof = LastRunSummary.StartFrameIndex > 0 &&
                LastRunSummary.DecodedFrames == 0 &&
                LastRunSummary.ReachedDecoderEof;
            bool complete = LastRunSummary.ReachedDecoderEof &&
                !LastRunSummary.DecodeCancelled &&
                string.Equals(LastRunSummary.DecodeError, "none", StringComparison.OrdinalIgnoreCase) &&
                !resumeStartBeyondEof &&
                LastRunSummary.DecodedFrames >= expectedFrames;
            if (!complete)
            {
                string incompleteLine =
                    $"[AutoRunDecodeIncomplete] runId={LastRunSummary.RunId ?? "n/a"}, totalFrames={effectiveTotalFrames}, reportedTotalFrames={totalFrames}, startFrame={LastRunSummary.StartFrameIndex}, expected={expectedFrames}, decoded={LastRunSummary.DecodedFrames}, eof={LastRunSummary.ReachedDecoderEof.ToString().ToLowerInvariant()}, cancelled={LastRunSummary.DecodeCancelled.ToString().ToLowerInvariant()}, error={LastRunSummary.DecodeError}";
                Debug.WriteLine(incompleteLine);
                RunMetricsLog.AppendRunLines(
                    LastRunSummary.RunId,
                    LastRunSummary.ToLogLine(),
                    incompleteLine);
                return;
            }

            ApplyPostProcessResultToRunSummary(
                RunAutoPostProcessIfNeeded(videoPath, effectiveTotalFrames, ct));
            progress?.Report(100);
        }

        private static int ResolveEffectiveTotalFrames(
            int reportedTotalFrames,
            int startFrameIndex,
            int decodedFrames,
            bool reachedDecoderEof,
            bool decodeCancelled,
            string? decodeError)
        {
            bool cleanEof = reachedDecoderEof &&
                !decodeCancelled &&
                string.Equals(decodeError, "none", StringComparison.OrdinalIgnoreCase);
            if (!cleanEof)
                return Math.Max(0, reportedTotalFrames);
            if (startFrameIndex > 0 && decodedFrames == 0)
                return Math.Max(0, reportedTotalFrames);

            long actualEndExclusive = (long)Math.Max(0, startFrameIndex) + Math.Max(0, decodedFrames);
            return actualEndExclusive >= int.MaxValue
                ? int.MaxValue
                : (int)actualEndExclusive;
        }

        private AutoMaskPostProcessResult RunAutoPostProcessIfNeeded(string videoPath, int totalFrames, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (totalFrames <= 0)
                return AutoMaskPostProcessResult.Empty;

            using var workingProvider = _maskProvider.CreateSnapshot(
                out long providerVersion,
                ct);

            var cascadeResult = new YoloRiskCascadeStep().Apply(
                workingProvider,
                videoPath,
                totalFrames,
                LastRunSummary?.StartFrameIndex ?? 0,
                _frameTimings.Count,
                _sourceFpsForSummary,
                _options,
                _frameTimings,
                ct);

            ct.ThrowIfCancellationRequested();

            var postProcess = new AutoMaskPostProcessPipeline(
                workingProvider,
                _options,
                totalFrames,
                _sourceFpsForSummary,
                _frameTimings,
                _sceneCutStarts,
                _postProcessStartFrameIndex);

            var postProcessResult = postProcess.Apply(
                videoPath,
                ct,
                _detector as IBgraFaceDetector,
                _options.RoiRefinerDetectorOptions,
                _options.UseFaceOnnxRoiRefiner);

            ct.ThrowIfCancellationRequested();

            // Detection results remain on the live provider while risk cascade and
            // post-processing mutate the isolated working copy. The live face state is
            // replaced only after the whole staged phase succeeds.
            _maskProvider.CommitFaceMasksFrom(
                workingProvider,
                providerVersion,
                ct);

            ApplyYoloRiskCascadeResultToRunSummary(cascadeResult);
            return postProcessResult;
        }

        private void ApplyYoloRiskCascadeResultToRunSummary(YoloRiskCascadeResult result)
        {
            if (LastRunSummary == null)
                return;

            LastRunSummary = LastRunSummary with
            {
                YoloRiskCascadeEnabled = result.Enabled,
                YoloRiskFrameCount = result.RiskFrames,
                YoloPeriodicGlobalFrameCount = result.PeriodicFrames,
                YoloTimelineFrameCount = result.TimelineFrames,
                YoloPtsTimingFrameCount = result.PtsTimingFrames,
                YoloUnalignedTimelineFrameCount = result.UnalignedTimelineFrames,
                YoloUnalignedRiskFrameCount = result.UnalignedRiskFrames,
                YoloSecondaryAttemptCount = result.Attempts,
                YoloProtectedStoredMaskFrameCount = result.ProtectedStoredMaskFrames,
                YoloSecondaryHitFrameCount = result.HitFrames,
                YoloSecondaryCandidateFaceCount = result.CandidateFaces,
                YoloSecondaryAcceptedFrameCount = result.AcceptedFrames,
                YoloSecondaryAcceptedFaceCount = result.AcceptedFaces,
                YoloSecondaryRejectedFaceCount = result.RejectedFaces,
                YoloCascadeDecodeMs = result.DecodeMs,
                YoloCascadeDetectMs = result.DetectMs,
                YoloCascadeTotalMs = result.TotalMs,
                YoloCascadeReasonBreakdown = string.IsNullOrWhiteSpace(result.ReasonBreakdown)
                    ? "none"
                    : result.ReasonBreakdown,
                YoloCascadeError = string.IsNullOrWhiteSpace(result.Error) ? "none" : result.Error
            };
            Debug.WriteLine(LastRunSummary.ToYoloCascadeLogLine());
        }

        private void RecordFrameTiming(FfFrameExtractor extractor, int frameIndex)
        {
            if (_options.FilterProfile != FaceFilterProfile.Yolo)
                return;

            double timestampSeconds = extractor.LastDecodedTimestampSeconds;
            if (!double.IsFinite(timestampSeconds))
            {
                _frameTimings[frameIndex] = new FrameTimingSample(
                    double.NaN,
                    FrameTimingSource.None);
                return;
            }

            FrameTimingSource source = string.Equals(
                extractor.LastDecodedTimestampSource,
                "pts",
                StringComparison.Ordinal)
                ? FrameTimingSource.PresentationTimestamp
                : FrameTimingSource.FpsFallback;

            _frameTimings[frameIndex] = new FrameTimingSample(timestampSeconds, source);
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
                FinalSceneCutPreGuardWindowCount = finalSummary.FinalSceneCutPreGuardWindowCount,
                FinalSceneCutPreStrongCarryWindowCount = finalSummary.FinalSceneCutPreStrongCarryWindowCount,
                FinalSceneCutPostGuardWindowCount = finalSummary.FinalSceneCutPostGuardWindowCount,
                FinalSceneCutPostStrongCarryWindowCount = finalSummary.FinalSceneCutPostStrongCarryWindowCount,
                FinalSceneCutCarryWindowCount = finalSummary.FinalSceneCutCarryWindowCount,
                FinalSceneCutPostGapFillWindowCount = finalSummary.FinalSceneCutPostGapFillWindowCount,
                FinalSceneCutPostGapFillCarryPairCount = finalSummary.FinalSceneCutPostGapFillCarryPairCount,
                FinalSceneCutPostGapFillCarryRemovedCount = finalSummary.FinalSceneCutPostGapFillCarryRemovedCount,
                FinalSceneCutPostGapFillProtectedFrameCount = finalSummary.FinalSceneCutPostGapFillProtectedFrameCount,
                FinalSceneCutPairSourceBreakdown = finalSummary.FinalSceneCutPairSourceBreakdown,
                FinalSceneCutPostGapFillPairSourceBreakdown = finalSummary.FinalSceneCutPostGapFillPairSourceBreakdown,
                FinalProtectedSceneCarryFrameCount = finalSummary.ProtectedSceneCarryFrameCount,
                FinalMaskReviewRequired = finalSummary.FinalReviewRequired,
                FinalMaskReviewReasons = string.IsNullOrWhiteSpace(finalSummary.FinalReviewReasons)
                    ? "none"
                    : finalSummary.FinalReviewReasons,
                FinalMissRecoveryFillCount = finalSummary.FinalMissRecoveryFillCount,
                FinalFalsePositiveSuppressedCount = finalSummary.FinalFalsePositiveSuppressedCount,
                FinalOffModeWeakCleanupCount = finalSummary.FinalOffModeWeakCleanupCount,
                FinalGapFillRecoveredCount = finalSummary.FinalGapFillRecoveredCount,
                FinalGapFillBlockedCutGapFrames = finalSummary.FinalGapFillBlockedCutGapFrames,
                FinalGapFillBlockedCutGapFramesBeforeCut = finalSummary.FinalGapFillBlockedCutGapFramesBeforeCut,
                FinalGapFillBlockedCutGapFramesAfterCut = finalSummary.FinalGapFillBlockedCutGapFramesAfterCut,
                FinalGapFillBlockedCleanupGapFrames = finalSummary.FinalGapFillBlockedCleanupGapFrames,
                FinalGapFillBlockedSceneCarryGapFrames = finalSummary.FinalGapFillBlockedSceneCarryGapFrames,
                FinalGapFillSuppressedWeakGeometryAnchorChecks = finalSummary.FinalGapFillSuppressedWeakGeometryAnchorChecks,
                FinalGapFillSuppressedRiskyGeometryAnchorChecks = finalSummary.FinalGapFillSuppressedRiskyGeometryAnchorChecks,
                FinalGapFillUnsupportedWeakAnchorChecks = finalSummary.FinalGapFillUnsupportedWeakAnchorChecks,
                PostProcessMs = postProcessResult.PostProcessElapsedMs,
                SampleWindowFrames = finalSummary.SampleWindowFrames,
                SampleWindowStartFrame = finalSummary.SampleWindowStartFrame,
                SampleWindowEndFrame = finalSummary.SampleWindowEndFrame,
                SampleWindowStartSeconds = finalSummary.SampleWindowStartSeconds,
                SampleWindowEndSeconds = finalSummary.SampleWindowEndSeconds,
                SampleWindowDurationSeconds = finalSummary.SampleWindowDurationSeconds,
                SampleWindowTimingSource = finalSummary.SampleWindowTimingSource,
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
                SampleOffModeWeakCleanupSuppressionCount = finalSummary.SampleOffModeWeakCleanupSuppressionCount,
                SampleGapFillBlockedCutGapFrames = finalSummary.SampleGapFillBlockedCutGapFrames,
                SampleGapFillBlockedCutGapFramesBeforeCut = finalSummary.SampleGapFillBlockedCutGapFramesBeforeCut,
                SampleGapFillBlockedCutGapFramesAfterCut = finalSummary.SampleGapFillBlockedCutGapFramesAfterCut,
                SampleGapFillBlockedCleanupGapFrames = finalSummary.SampleGapFillBlockedCleanupGapFrames,
                SampleGapFillBlockedSceneCarryGapFrames = finalSummary.SampleGapFillBlockedSceneCarryGapFrames,
                SampleWindowIssueFrameCount = finalSummary.SampleWindowIssueFrameCount,
                SampleWindowIssueCandidateCount = finalSummary.SampleWindowIssueCandidateCount,
                SampleWindowStartReason = string.IsNullOrWhiteSpace(finalSummary.SampleWindowStartReason)
                    ? "none"
                    : finalSummary.SampleWindowStartReason
            };
            Debug.WriteLine(LastRunSummary.ToLogLine());
            Debug.WriteLine(LastRunSummary.ToSampleTimingLogLine());
            Debug.WriteLine(LastRunSummary.ToYoloCascadeLogLine());
            RunMetricsLog.AppendRunLines(
                LastRunSummary.RunId,
                LastRunSummary.ToLogLine(),
                LastRunSummary.ToSampleTimingLogLine(),
                LastRunSummary.ToYoloCascadeLogLine(),
                finalSummary.EvidenceLogLine);
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
            double offModeSceneCutResetRate = summary.FinalOffModeSceneCutResetPairCount > 0
                ? summary.FinalOffModeSceneCutResetRemovedFrameCount / (double)summary.FinalOffModeSceneCutResetPairCount
                : 0.0;
            double offModeSceneCutResetBeforeRate = summary.FinalOffModeSceneCutResetBeforeWindowFrameCount > 0
                ? summary.FinalOffModeSceneCutResetRemovedBeforeFrameCount / (double)summary.FinalOffModeSceneCutResetBeforeWindowFrameCount
                : 0.0;
            double offModeSceneCutResetAfterRate = summary.FinalOffModeSceneCutResetAfterWindowFrameCount > 0
                ? summary.FinalOffModeSceneCutResetRemovedAfterFrameCount / (double)summary.FinalOffModeSceneCutResetAfterWindowFrameCount
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
            double detectionFps = summary.ProcessedFrames > 0 && summary.AnalysisTotalMs > 0
                ? summary.ProcessedFrames * 1000.0 / summary.AnalysisTotalMs
                : 0.0;

            System.Diagnostics.Debug.WriteLine(
                $"[AutoMaskQualityGate] runId={summary.RunId ?? "n/a"}, mode={summary.Mode}, risk={riskLabel}, detectionFps={detectionFps:0.00}, detectorTotalMs={summary.TotalMs}, analysisTotalMs={summary.AnalysisTotalMs}, totalFrames={summary.TotalFrames}, processed={summary.ProcessedFrames}, finalMaskFrames={summary.FinalMaskFrames}, finalRows={summary.FinalMaskRows}, reviewRequired={summary.FinalMaskReviewRequired.ToString().ToLowerInvariant()}, reviewReasons={summary.FinalMaskReviewReasons}, post={summary.EnablePostProcessing}, roiPost={summary.EnableRoiPostProcess}, weakIso={summary.EnableYoloWeakIsolatedCleanup}, gapFill={summary.EnableYoloGapFill}, scene={summary.EnableYoloSceneCutCarryCleanup}, smooth={summary.EnableYoloTemporalSmoothing}, shortGaps={summary.FinalMaskShortGapCount}, perFaceShortGaps={summary.FinalMaskPerFaceShortGapCount}, largeJumps={summary.FinalMaskLargeJumpGapCount}, carryFrames={summary.FinalProtectedSceneCarryFrameCount}, sceneCut=preGuard:{summary.FinalSceneCutPreGuardPairCount},preStrong:{summary.FinalSceneCutPreStrongProbePairCount},postGuard:{summary.FinalSceneCutPostGuardPairCount},postStrong:{summary.FinalSceneCutPostStrongProbePairCount},carryPairs:{summary.FinalSceneCutCarryPairCount},carryRemoved:{summary.FinalSceneCutCarryRemovedCount},carryProtected:{summary.FinalSceneCutProtectedFrameCount}, offModeResetPairs:{summary.FinalOffModeSceneCutResetPairCount},offModeResetRemoved:{summary.FinalOffModeSceneCutResetRemovedFrameCount},offModeResetWindows={summary.FinalOffModeSceneCutResetBeforeWindowFrameCount}/{summary.FinalOffModeSceneCutResetAfterWindowFrameCount},offModeResetRemovedWindows={summary.FinalOffModeSceneCutResetRemovedBeforeFrameCount}/{summary.FinalOffModeSceneCutResetRemovedAfterFrameCount}, offModeResetRate={offModeSceneCutResetRate:0.0000}, offModeResetBeforeRate={offModeSceneCutResetBeforeRate:0.0000}, offModeResetAfterRate={offModeSceneCutResetAfterRate:0.0000}, postGapFillCarryPairs:{summary.FinalSceneCutPostGapFillCarryPairCount},postGapFillRemoved:{summary.FinalSceneCutPostGapFillCarryRemovedCount},postGapFillProtected:{summary.FinalSceneCutPostGapFillProtectedFrameCount}, sceneCutSourceBreakdown={summary.FinalSceneCutPairSourceBreakdown}, postGapFillPairSourceBreakdown={summary.FinalSceneCutPostGapFillPairSourceBreakdown}, sampleWindowFrames={sampleWindowFrames}");
            System.Diagnostics.Debug.WriteLine(
                $"[AutoMaskQualityGate] final runId={summary.RunId ?? "n/a"}, missRecovery={summary.FinalMissRecoveryFillCount}, fpSuppressed={summary.FinalFalsePositiveSuppressedCount}, offModeWeakCleanupSuppressed={summary.FinalOffModeWeakCleanupCount}, gapFillRecovered={summary.FinalGapFillRecoveredCount}, gapFillBlocked={summary.FinalGapFillBlockedCutGapFrames}/{summary.FinalGapFillBlockedCutGapFramesBeforeCut}/{summary.FinalGapFillBlockedCutGapFramesAfterCut}/{summary.FinalGapFillBlockedCleanupGapFrames}/{summary.FinalGapFillBlockedSceneCarryGapFrames}, gapFillAnchorChecks={summary.FinalGapFillSuppressedWeakGeometryAnchorChecks}/{summary.FinalGapFillSuppressedRiskyGeometryAnchorChecks}/{summary.FinalGapFillUnsupportedWeakAnchorChecks}, sceneCutRemovalRate={finalSceneCutRemovalRate:0.0000}, sceneCutProtectedRate={finalSceneCutProtectedRate:0.0000}, postGapFillRemovalRate={finalPostGapFillRemovalRate:0.0000}, postGapFillProtectedRate={finalPostGapFillProtectedRate:0.0000}, postProcessMs={summary.PostProcessMs}");
            System.Diagnostics.Debug.WriteLine(
                $"[AutoMaskQualityGate] sample runId={summary.RunId ?? "n/a"}, mode={summary.Mode}, risk={sampleRiskLabel}, sampleWindow={summary.SampleWindowFrames}, sampleFrames={summary.SampleFrameCount}, sampleRows={summary.SampleRowCount}, shortGaps={summary.SampleShortGapCount}, perFaceShortGaps={summary.SamplePerFaceShortGapCount}, isolated={summary.SampleIsolatedFrameCount}, largeJumps={summary.SampleLargeJumpGapCount}, protectedCarry={summary.SampleProtectedSceneCarryFrameCount}, reviewRequired={summary.SampleReviewRequired.ToString().ToLowerInvariant()}, reviewReasons={summary.SampleReviewReasons}");
            System.Diagnostics.Debug.WriteLine(
                $"[AutoMaskQualityGate] sample runId={summary.RunId ?? "n/a"}, sampleIssueFrames={summary.SampleWindowIssueFrameCount}, sampleIssueCandidates={summary.SampleWindowIssueCandidateCount}, sampleWindowStartReason={summary.SampleWindowStartReason}, missRecovery={summary.SampleMissRecoveryFillCount}, missRecoveryRate={sampleMissRecoveryRate:0.0000}, fpSuppressed={summary.SampleFalsePositiveSuppressionCount}, fpSuppressedRate={sampleFpSuppressedRate:0.0000}, sampleOffModeWeakCleanupSuppressed={summary.SampleOffModeWeakCleanupSuppressionCount}, sampleGapFillBlocked={summary.SampleGapFillBlockedCutGapFrames}/{summary.SampleGapFillBlockedCutGapFramesBeforeCut}/{summary.SampleGapFillBlockedCutGapFramesAfterCut}/{summary.SampleGapFillBlockedCleanupGapFrames}/{summary.SampleGapFillBlockedSceneCarryGapFrames}, riskScore={sampleRiskScore}, riskLabel={sampleRiskLabel}");
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
                ProcessingMode = _options.ProcessingMode,
                EnablePostProcessing = postEnabled,
                EnableRoiPostProcess = postRoiEnabled,
                EnableYoloWeakIsolatedCleanup = postWeakIsoEnabled,
                EnableYoloGapFill = postGapFillEnabled,
                EnableYoloSceneCutCarryCleanup = postSceneCutEnabled,
                EnableYoloTemporalSmoothing = postTemporalEnabled,
                SourceFps = _sourceFpsForSummary
            };
            Debug.WriteLine(LastRunSummary.ToLogLine());
            Debug.WriteLine(LastRunSummary.ToSampleTimingLogLine());
        }

        private string GetDetectorName()
        {
            if (_detector is IExecutionProviderDiagnostics diagnostics)
            {
                string provider = diagnostics.ExecutionProviderLabel;
                string? error = diagnostics.ExecutionProviderError;
                if (!string.IsNullOrWhiteSpace(error))
                    return $"{_detector.GetType().Name}/{provider}({error})";

                return $"{_detector.GetType().Name}/{provider}";
            }

            return _detector.GetType().Name;
        }

        private static unsafe double[] ComputeFrameSignature(
            byte* basePtr,
            int stride,
            int width,
            int height)
            => SparseSceneSignature.Compute(basePtr, stride, width, height);

        private static double ComputeSignatureDifference(
            IReadOnlyList<double> current,
            IReadOnlyList<double> next)
            => SparseTrackingMath.SignatureDifference(current, next);

        private static double GetCenterDistanceRatio(Rect a, Rect b)
            => SparseTrackingMath.CenterDistanceRatio(a, b);

        private static double IoU(Rect a, Rect b)
            => SparseTrackingMath.IoU(a, b);

        private static Rect LerpRect(Rect from, Rect to, double t)
            => SparseTrackingMath.LerpRect(from, to, t);

        private static float LerpConfidence(
            IReadOnlyList<float> from,
            IReadOnlyList<float> to,
            int fromIndex,
            int toIndex,
            double t)
            => SparseTrackingMath.LerpConfidence(from, to, fromIndex, toIndex, t);

        private static float? GetMinConfidence(IReadOnlyList<float> values)
            => SparseTrackingMath.MinConfidence(values);

        private static bool ShouldUsePrimaryRoiShortcut(AutoMaskOptions options)
        {
            if (options.ProcessingMode != AutoMaskProcessingMode.Legacy)
                return false;

            return options.FilterProfile != FaceFilterProfile.Yolo ||
                options.EnableYoloPrimaryRoiShortcut;
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

            int percent = (int)Math.Floor(frameIndex * 100.0 / Math.Max(1, totalFrames - 1));
            if (percent > 99) percent = 99;
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

        private static FaceFilterSettings GetRuntimeFaceFilterSettings(AutoMaskOptions options)
        {
            bool usePrecisionMode = options.ProcessingMode == AutoMaskProcessingMode.Legacy &&
                !options.EnablePostProcessing;
            FaceFilterSettings settings = GetFaceFilterSettings(
                options.FilterProfile,
                usePrecisionMode);
            bool disableFrameStats = options.FilterProfile == FaceFilterProfile.FaceOnnx &&
                options.ProcessingMode == AutoMaskProcessingMode.Tracked &&
                !options.EnablePostProcessing;
            return disableFrameStats
                ? settings with { UseStatsFilter = false }
                : settings;
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

    }
}
