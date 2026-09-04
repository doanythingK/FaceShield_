using Avalonia;
using Avalonia.Media.Imaging;
using FaceShield.Services.FaceDetection;
using FaceShield.Services.Video;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace FaceShield.Services.Analysis
{
    public sealed partial class AutoMaskGenerator
    {
        private void GenerateSequential(
            string videoPath,
            IProgress<int>? progress,
            CancellationToken ct,
            int startFrameIndex,
            int totalFrames,
            Action<int>? onFrameProcessed)
        {
            bool useRaw = _detector is IBgraFaceDetector;
            int start = Math.Max(0, startFrameIndex);
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
            int decoded = 0;
            int detected = 0;
            int offModeSceneCutResetPairs = 0;
            int offModeSceneCutResetRemovedFrameCount = 0;
            int offModeSceneCutResetBeforeWindowFrameCount = 0;
            int offModeSceneCutResetAfterWindowFrameCount = 0;
            int offModeSceneCutResetRemovedBeforeFrameCount = 0;
            int offModeSceneCutResetRemovedAfterFrameCount = 0;
            var roiStats = new RoiDetectStats();
            var filterStats = new FaceFilterStats();
            var progressState = new ProgressState();
            double[]? previousSceneSignature = null;
            bool forceDetectAfterSceneCut = false;
            bool collectSceneCutBoundaries = _options.UseTracking &&
                _options.ProcessingMode != AutoMaskProcessingMode.Raw &&
                (_options.FilterProfile == FaceFilterProfile.Yolo ||
                 _options.ProcessingMode != AutoMaskProcessingMode.Legacy);
            while (!ct.IsCancellationRequested)
            {
                bool shouldDetect = _options.DetectEveryNFrames <= 1
                    || nextIndex % _options.DetectEveryNFrames == 0
                    || forceDetectAfterSceneCut
                    || lastFaces == null;
                forceDetectAfterSceneCut = false;

                bool resetTrackingAtSceneCut = collectSceneCutBoundaries && lastFaces != null;
                bool requireFrame = shouldDetect || collectSceneCutBoundaries;

                int idx;
                WriteableBitmap? frame = null;
                FfFrameExtractor.BgraFrame bgra = default;

                var tRead = Stopwatch.StartNew();
                if (useRaw)
                {
                    if (useProxy)
                    {
                        if (!extractor.TryGetNextFrameRawScaled(ct, requireFrame, proxyWidth, proxyHeight, useBilinear, out bgra, out idx))
                            break;
                    }
                    else
                    {
                        if (!extractor.TryGetNextFrameRaw(ct, requireFrame, out bgra, out idx))
                            break;
                    }
                }
                else
                {
                    if (!extractor.TryGetNextFrame(ct, requireFrame, out frame, out idx))
                        break;
                }
                tRead.Stop();
                readMs += tRead.ElapsedMilliseconds;

                nextIndex = idx + 1;

                RecordFrameTiming(extractor, idx);

                onFrameProcessed?.Invoke(idx);
                decoded++;

                if (collectSceneCutBoundaries)
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
                        _sceneCutStarts.Add(idx);
                        if (resetTrackingAtSceneCut)
                        {
                            shouldDetect = true;
                            forceDetectAfterSceneCut = true;
                            lastFaces = null;
                            if (_options.ProcessingMode == AutoMaskProcessingMode.Legacy &&
                                !_options.EnablePostProcessing)
                            {
                                offModeSceneCutResetPairs++;
                            }
                        }
                        Debug.WriteLine(
                            $"[AutoMask] scene-cut boundary idx={idx} trackingReset={resetTrackingAtSceneCut.ToString().ToLowerInvariant()} preservedMasks=true diff={ComputeSignatureDifference(currentSceneSignature, previousSceneSignature):0.###} phase=sequential");
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
                    detected++;
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
                            ShouldUsePrimaryRoiShortcut(_options) ? lastFaces : null,
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
                        if (_options.ProcessingMode == AutoMaskProcessingMode.Raw)
                        {
                            // Raw는 detector가 반환한 후보를 그대로 기록해 기준선을 오염시키지 않습니다.
                        }
                        else if (useRaw && bgra.Data != IntPtr.Zero)
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
                                    _faceFilterSettings,
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
                                    _faceFilterSettings,
                                    filterStats);
                            }
                        }
                        else
                        {
                            faces = FilterFacesByArea(faces, fullSize, _faceFilterSettings, filterStats);
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
                    ct.ThrowIfCancellationRequested();
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

                if (decoded % 60 == 0)
                {
                    Debug.WriteLine(
                        $"[AutoMask] decoded={decoded}, detects={detected}, readMs={readMs}, detectMs={detectMs}, maskMs={maskMs}, totalMs={swTotal.ElapsedMilliseconds}, roi={BuildDetectionSummary(roiStats, filterStats)}");
                }
            }

            Debug.WriteLine(
                $"[AutoMask] done decoded={decoded}, detects={detected}, readMs={readMs}, detectMs={detectMs}, maskMs={maskMs}, totalMs={swTotal.ElapsedMilliseconds}, roi={BuildDetectionSummary(roiStats, filterStats)}");
            SetLastRunSummary(new AutoMaskRunSummary(
                "sequential",
                totalFrames,
                start,
                decoded,
                decoded,
                detected,
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
