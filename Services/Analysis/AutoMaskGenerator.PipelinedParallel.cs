using Avalonia;
using FaceShield.Services.FaceDetection;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace FaceShield.Services.Analysis
{
    public sealed partial class AutoMaskGenerator
    {
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
    }
}
