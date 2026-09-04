using Avalonia;
using FaceShield.Services.FaceDetection;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FaceShield.Services.Analysis
{
    public sealed partial class AutoMaskGenerator
    {
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
    }
}
