using Avalonia;
using FaceShield.Models.Analysis;
using FaceShield.Services.FaceDetection;
using FaceShield.Services.Video;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace FaceShield.Services.Analysis
{
    [Flags]
    internal enum YoloRiskReason
    {
        None = 0,
        ExpectedTrackMissing = 1,
        LowConfidence = 2,
        SmallFace = 4,
        EdgeFace = 8,
        PeriodicGlobal = 16,
        Confirmation = 32
    }

    internal sealed class YoloRiskCascadeStep
    {
        private const double ExistingOverlapIou = 0.45;
        private const double TemporalSupportIou = 0.25;
        private const double TemporalSupportMaxCenterShift = 0.60;
        private const double TemporalSupportMaxAreaRatio = 2.5;
        private const double SceneDifferenceThreshold = 0.20;
        private const float SecondaryStrongConfidence = 0.75f;

        public YoloRiskCascadeResult Apply(
            FrameMaskProvider maskProvider,
            string videoPath,
            int totalFrames,
            double sourceFps,
            AutoMaskOptions options,
            IReadOnlyDictionary<int, FrameTimingSample> frameTimings,
            CancellationToken cancellationToken)
        {
            if (!options.EnableYoloRiskCascade ||
                options.FilterProfile != FaceFilterProfile.Yolo ||
                options.YoloSecondaryDetectorOptions == null ||
                totalFrames <= 0 ||
                string.IsNullOrWhiteSpace(videoPath))
            {
                return YoloRiskCascadeResult.Empty;
            }

            if (options.DetectEveryNFrames > 1)
            {
                return YoloRiskCascadeResult.Empty with
                {
                    Enabled = true,
                    Error = "risk cascade requires DetectEveryNFrames=1"
                };
            }

            var totalTimer = Stopwatch.StartNew();
            var primaryEntries = maskProvider.GetFaceMaskEntries()
                .ToDictionary(static entry => entry.Key, static entry => entry.Value);
            var riskFrames = BuildRiskFrames(primaryEntries, totalFrames, options, frameTimings);
            foreach (int frameIndex in riskFrames.Keys.Where(frame =>
                         !frameTimings.TryGetValue(frame, out var timing) ||
                         timing.Source != FrameTimingSource.PresentationTimestamp).ToArray())
            {
                riskFrames.Remove(frameIndex);
            }
            if (riskFrames.Count == 0)
            {
                return YoloRiskCascadeResult.Empty with
                {
                    Enabled = true,
                    Error = "risk cascade has no timing-aligned frames in the current run"
                };
            }

            int attempts = 0;
            int hitFrames = 0;
            int secondaryCandidates = 0;
            long decodeMs = 0;
            long detectMs = 0;
            string? error = null;
            var strongResults = new Dictionary<int, StrongFrameResult>();

            try
            {
                var secondaryFactory = FaceDetectorFactory.ForOnnx(options.YoloSecondaryDetectorOptions);
                using IFaceDetector secondaryBase = secondaryFactory.CreateDetector();
                if (secondaryBase is not IBgraFaceDetector secondary)
                    return YoloRiskCascadeResult.Empty with { Enabled = true, Error = "secondary detector does not support BGRA input" };

                using var extractor = new FfFrameExtractor(videoPath, enableHardware: false);
                foreach (var range in BuildContiguousRanges(riskFrames.Keys))
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;
                    if (frameTimings.TryGetValue(range.Start, out var rangeStartTiming) &&
                        rangeStartTiming.Source == FrameTimingSource.PresentationTimestamp)
                    {
                        extractor.StartSequentialReadAtTimestamp(range.Start, rangeStartTiming.TimestampSeconds);
                    }
                    else
                    {
                        extractor.StartSequentialRead(range.Start);
                    }
                    int nextFrameIndex = range.Start;

                    while (!cancellationToken.IsCancellationRequested && nextFrameIndex <= range.End)
                    {
                        var decodeTimer = Stopwatch.StartNew();
                        bool ok = extractor.TryGetNextFrameRaw(
                            cancellationToken,
                            requireBgra: true,
                            out var bgra,
                            out int frameIndex);
                        decodeTimer.Stop();
                        decodeMs += decodeTimer.ElapsedMilliseconds;
                        if (!ok || frameIndex > range.End)
                            break;
                        nextFrameIndex = frameIndex + 1;

                        if (!IsFrameTimingAligned(
                            frameIndex,
                            extractor.LastDecodedTimestampSeconds,
                            frameTimings,
                            sourceFps))
                        {
                            error = $"secondary frame timing mismatch at frame {frameIndex}";
                            break;
                        }

                        if (!riskFrames.ContainsKey(frameIndex) || maskProvider.TryGetStoredMask(frameIndex, out _))
                            continue;

                        attempts++;
                        if (bgra.Data == IntPtr.Zero)
                            continue;

                        var detectTimer = Stopwatch.StartNew();
                        var faces = secondary.DetectFacesBgra(
                            bgra.Data,
                            bgra.Stride,
                            bgra.Width,
                            bgra.Height,
                            1.0,
                            DownscaleQuality.BalancedBilinear);
                        detectTimer.Stop();
                        detectMs += detectTimer.ElapsedMilliseconds;

                        var strongFaces = faces
                            .Where(static face => face.Confidence >= SecondaryStrongConfidence)
                            .ToArray();
                        if (strongFaces.Length > 0)
                        {
                            hitFrames++;
                            secondaryCandidates += strongFaces.Length;
                        }

                        strongResults[frameIndex] = new StrongFrameResult(
                            new PixelSize(bgra.Width, bgra.Height),
                            strongFaces,
                            CaptureSceneSample(bgra.Data, bgra.Stride, bgra.Width, bgra.Height));
                    }

                    if (error != null)
                        break;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            int acceptedFaces = 0;
            int acceptedFrames = 0;
            int rejectedFaces = 0;
            if (error == null && !cancellationToken.IsCancellationRequested)
            {
                foreach (var entry in strongResults.OrderBy(static entry => entry.Key))
                {
                    int frameIndex = entry.Key;
                    if (!riskFrames.TryGetValue(frameIndex, out var riskReason) ||
                        (riskReason & ~YoloRiskReason.Confirmation) == YoloRiskReason.None)
                    {
                        continue;
                    }

                    var strong = entry.Value;
                    primaryEntries.TryGetValue(frameIndex, out var primary);
                    IReadOnlyList<Rect> existingFaces = primary.Faces ?? Array.Empty<Rect>();
                    IReadOnlyList<float> existingConfidences = primary.Confidences ?? Array.Empty<float>();
                    var mergedFaces = new List<Rect>(existingFaces);
                    var mergedConfidences = new List<float>(existingFaces.Count + strong.Faces.Count);
                    for (int i = 0; i < existingFaces.Count; i++)
                    {
                        mergedConfidences.Add(i < existingConfidences.Count
                            ? existingConfidences[i]
                            : primary.MinConfidence ?? 1.0f);
                    }

                    int acceptedOnFrame = 0;
                    foreach (var candidate in strong.Faces)
                    {
                        if (mergedFaces.Any(existing =>
                                ComputeIou(existing, candidate.Bounds) >= ExistingOverlapIou))
                            continue;

                        bool supported = HasStrongTemporalSupport(
                            candidate.Bounds,
                            frameIndex,
                            strong,
                            strongResults,
                            Math.Max(1, options.YoloStrongConfirmationFrames));
                        if (!supported)
                        {
                            rejectedFaces++;
                            continue;
                        }

                        mergedFaces.Add(candidate.Bounds);
                        mergedConfidences.Add(candidate.Confidence);
                        acceptedFaces++;
                        acceptedOnFrame++;
                    }

                    if (acceptedOnFrame <= 0)
                        continue;

                    acceptedFrames++;
                    float? minConfidence = mergedConfidences.Count > 0
                        ? mergedConfidences.Min()
                        : null;
                    maskProvider.SetFaceRects(
                        frameIndex,
                        mergedFaces,
                        primary.Size.Width > 0 && primary.Size.Height > 0 ? primary.Size : strong.Size,
                        minConfidence,
                        mergedConfidences);
                }
            }

            totalTimer.Stop();
            int periodicFrames = riskFrames.Count(static entry =>
                (entry.Value & YoloRiskReason.PeriodicGlobal) != 0);
            string reasonBreakdown = string.Join(
                ",",
                Enum.GetValues<YoloRiskReason>()
                    .Where(static reason => reason != YoloRiskReason.None)
                    .Select(reason => $"{reason}={riskFrames.Count(entry => (entry.Value & reason) != 0)}"));

            Debug.WriteLine(
                $"[YoloRiskCascade] enabled=true riskFrames={riskFrames.Count} periodicFrames={periodicFrames} attempts={attempts} hitFrames={hitFrames} candidates={secondaryCandidates} acceptedFrames={acceptedFrames} acceptedFaces={acceptedFaces} rejectedFaces={rejectedFaces} decodeMs={decodeMs} detectMs={detectMs} totalMs={totalTimer.ElapsedMilliseconds} reasons={reasonBreakdown} error={error ?? "none"}");

            return new YoloRiskCascadeResult(
                Enabled: true,
                RiskFrames: riskFrames.Count,
                PeriodicFrames: periodicFrames,
                Attempts: attempts,
                HitFrames: hitFrames,
                CandidateFaces: secondaryCandidates,
                AcceptedFrames: acceptedFrames,
                AcceptedFaces: acceptedFaces,
                RejectedFaces: rejectedFaces,
                DecodeMs: decodeMs,
                DetectMs: detectMs,
                TotalMs: totalTimer.ElapsedMilliseconds,
                ReasonBreakdown: reasonBreakdown,
                Error: error);
        }

        private static Dictionary<int, YoloRiskReason> BuildRiskFrames(
            IReadOnlyDictionary<int, FrameMaskProvider.FaceMaskData> entries,
            int totalFrames,
            AutoMaskOptions options,
            IReadOnlyDictionary<int, FrameTimingSample> frameTimings)
        {
            var risks = new Dictionary<int, YoloRiskReason>();
            int maxGap = Math.Max(1, options.YoloRiskMaxTrackGapFrames);
            int confirmationRadius = Math.Max(1, options.YoloStrongConfirmationFrames);
            double intervalSeconds = Math.Max(0.25, options.YoloStrongFullScanIntervalSeconds);
            var orderedTimings = frameTimings
                .Where(entry => entry.Key >= 0 &&
                    entry.Key < totalFrames &&
                    entry.Value.Source == FrameTimingSource.PresentationTimestamp &&
                    double.IsFinite(entry.Value.TimestampSeconds))
                .OrderBy(static entry => entry.Key)
                .ToArray();
            if (orderedTimings.Length > 0)
            {
                double nextScanSeconds = orderedTimings[0].Value.TimestampSeconds;
                foreach (var timing in orderedTimings)
                {
                    if (timing.Value.TimestampSeconds + 0.000001 < nextScanSeconds)
                        continue;

                    AddRisk(risks, timing.Key, YoloRiskReason.PeriodicGlobal, totalFrames);
                    do
                    {
                        nextScanSeconds += intervalSeconds;
                    }
                    while (nextScanSeconds <= timing.Value.TimestampSeconds);
                }
            }

            var orderedFrames = entries.Keys.OrderBy(static frame => frame).ToArray();
            for (int i = 1; i < orderedFrames.Length; i++)
            {
                int previous = orderedFrames[i - 1];
                int current = orderedFrames[i];
                int missing = current - previous - 1;
                if (missing > 0 && missing <= maxGap)
                {
                    for (int frameIndex = previous + 1; frameIndex < current; frameIndex++)
                        AddRisk(risks, frameIndex, YoloRiskReason.ExpectedTrackMissing, totalFrames);
                }

                if (current == previous + 1 &&
                    entries.TryGetValue(previous, out var previousData) &&
                    entries.TryGetValue(current, out var currentData) &&
                    previousData.Faces.Any(previousFace =>
                        !currentData.Faces.Any(currentFace => IsGeometrySupported(previousFace, currentFace))))
                {
                    AddRiskWindowByTimestamp(
                        risks,
                        current,
                        intervalSeconds,
                        frameTimings,
                        YoloRiskReason.ExpectedTrackMissing,
                        totalFrames,
                        maxGap);
                }
            }

            var detectionsByFrame = new Dictionary<int, IReadOnlyList<FaceTrackDetection>>();
            foreach (var entry in entries)
            {
                var detections = new FaceTrackDetection[entry.Value.Faces.Count];
                for (int i = 0; i < detections.Length; i++)
                {
                    float confidence = i < entry.Value.Confidences.Count
                        ? entry.Value.Confidences[i]
                        : entry.Value.MinConfidence ?? 1.0f;
                    detections[i] = new FaceTrackDetection(
                        entry.Key,
                        entry.Value.Faces[i],
                        entry.Value.Size,
                        confidence);
                }

                detectionsByFrame[entry.Key] = detections;
            }

            var tracks = new FaceTrackBuilder().Build(
                detectionsByFrame,
                new FaceTrackPostProcessOptions { MaxTrackGap = maxGap });
            foreach (var track in tracks)
            {
                for (int i = 1; i < track.Detections.Count; i++)
                {
                    int previous = track.Detections[i - 1].FrameIndex;
                    int current = track.Detections[i].FrameIndex;
                    int missing = current - previous - 1;
                    if (missing <= 0 || missing > maxGap)
                        continue;

                    for (int frameIndex = previous + 1; frameIndex < current; frameIndex++)
                        AddRisk(risks, frameIndex, YoloRiskReason.ExpectedTrackMissing, totalFrames);
                }
            }

            foreach (var entry in entries)
            {
                int frameIndex = entry.Key;
                var data = entry.Value;
                double frameArea = Math.Max(1.0, data.Size.Width * (double)data.Size.Height);
                double edgeX = data.Size.Width * Math.Clamp(options.YoloRiskEdgeMarginRatio, 0.0, 0.25);
                double edgeY = data.Size.Height * Math.Clamp(options.YoloRiskEdgeMarginRatio, 0.0, 0.25);
                bool lowConfidence = data.Confidences.Any(confidence =>
                    confidence <= options.YoloRiskLowConfidenceThreshold);
                bool smallFace = data.Faces.Any(face =>
                    face.Width * face.Height / frameArea <= options.YoloRiskSmallFaceAreaRatio);
                bool edgeFace = data.Faces.Any(face =>
                    face.X <= edgeX ||
                    face.Y <= edgeY ||
                    face.Right >= data.Size.Width - edgeX ||
                    face.Bottom >= data.Size.Height - edgeY);

                if (lowConfidence)
                    AddRisk(risks, frameIndex, YoloRiskReason.LowConfidence, totalFrames);
                if (smallFace)
                    AddRisk(risks, frameIndex, YoloRiskReason.SmallFace, totalFrames);
                if (edgeFace)
                    AddRisk(risks, frameIndex, YoloRiskReason.EdgeFace, totalFrames);
            }

            int[] seedFrames = risks.Keys.ToArray();
            foreach (int seedFrame in seedFrames)
            {
                for (int offset = 1; offset <= confirmationRadius; offset++)
                {
                    AddRisk(risks, seedFrame - offset, YoloRiskReason.Confirmation, totalFrames);
                    AddRisk(risks, seedFrame + offset, YoloRiskReason.Confirmation, totalFrames);
                }
            }

            return risks;
        }

        private static void AddRiskWindowByTimestamp(
            IDictionary<int, YoloRiskReason> risks,
            int startFrame,
            double durationSeconds,
            IReadOnlyDictionary<int, FrameTimingSample> frameTimings,
            YoloRiskReason reason,
            int totalFrames,
            int fallbackFrames)
        {
            if (frameTimings.TryGetValue(startFrame, out var startTiming) &&
                startTiming.Source == FrameTimingSource.PresentationTimestamp &&
                double.IsFinite(startTiming.TimestampSeconds))
            {
                double endTimestamp = startTiming.TimestampSeconds + Math.Max(0.0, durationSeconds);
                for (int frameIndex = startFrame; frameIndex < totalFrames; frameIndex++)
                {
                    if (!frameTimings.TryGetValue(frameIndex, out var timing) ||
                        timing.Source != FrameTimingSource.PresentationTimestamp ||
                        !double.IsFinite(timing.TimestampSeconds) ||
                        timing.TimestampSeconds > endTimestamp)
                    {
                        break;
                    }

                    AddRisk(risks, frameIndex, reason, totalFrames);
                }
                return;
            }

            for (int offset = 0; offset <= Math.Max(0, fallbackFrames); offset++)
                AddRisk(risks, startFrame + offset, reason, totalFrames);
        }

        private static void AddRisk(
            IDictionary<int, YoloRiskReason> risks,
            int frameIndex,
            YoloRiskReason reason,
            int totalFrames)
        {
            if (frameIndex < 0 || frameIndex >= totalFrames)
                return;

            risks[frameIndex] = risks.TryGetValue(frameIndex, out var existing)
                ? existing | reason
                : reason;
        }

        private static IReadOnlyList<FrameRange> BuildContiguousRanges(IEnumerable<int> frames)
        {
            var ordered = frames.Distinct().OrderBy(static frame => frame).ToArray();
            if (ordered.Length == 0)
                return Array.Empty<FrameRange>();

            var ranges = new List<FrameRange>();
            int start = ordered[0];
            int end = start;
            for (int i = 1; i < ordered.Length; i++)
            {
                if (ordered[i] == end + 1)
                {
                    end = ordered[i];
                    continue;
                }

                ranges.Add(new FrameRange(start, end));
                start = ordered[i];
                end = start;
            }

            ranges.Add(new FrameRange(start, end));
            return ranges;
        }

        private static bool IsFrameTimingAligned(
            int frameIndex,
            double decodedTimestampSeconds,
            IReadOnlyDictionary<int, FrameTimingSample> frameTimings,
            double sourceFps)
        {
            if (!double.IsFinite(decodedTimestampSeconds) ||
                !frameTimings.TryGetValue(frameIndex, out var expected) ||
                expected.Source != FrameTimingSource.PresentationTimestamp ||
                !double.IsFinite(expected.TimestampSeconds))
            {
                return false;
            }

            double fallbackStep = sourceFps > 0 ? 1.0 / sourceFps : 1.0 / 30.0;
            double lowerBound = expected.TimestampSeconds - fallbackStep * 0.5;
            double upperBound = expected.TimestampSeconds + fallbackStep * 0.5;

            if (frameTimings.TryGetValue(frameIndex - 1, out var previous) &&
                previous.Source == FrameTimingSource.PresentationTimestamp &&
                double.IsFinite(previous.TimestampSeconds))
            {
                if (previous.TimestampSeconds >= expected.TimestampSeconds)
                    return false;
                lowerBound = (previous.TimestampSeconds + expected.TimestampSeconds) * 0.5;
            }

            if (frameTimings.TryGetValue(frameIndex + 1, out var next) &&
                next.Source == FrameTimingSource.PresentationTimestamp &&
                double.IsFinite(next.TimestampSeconds))
            {
                if (next.TimestampSeconds <= expected.TimestampSeconds)
                    return false;
                upperBound = (expected.TimestampSeconds + next.TimestampSeconds) * 0.5;
            }

            return decodedTimestampSeconds >= lowerBound && decodedTimestampSeconds < upperBound;
        }

        private static bool HasStrongTemporalSupport(
            Rect candidate,
            int frameIndex,
            StrongFrameResult current,
            IReadOnlyDictionary<int, StrongFrameResult> strongResults,
            int confirmationFrames)
        {
            bool hasPrevious = false;
            bool hasNext = false;
            for (int distance = 1; distance <= confirmationFrames; distance++)
            {
                if (strongResults.TryGetValue(frameIndex - distance, out var previous) &&
                    ComputeSceneDifference(current.SceneSample, previous.SceneSample) <= SceneDifferenceThreshold &&
                    previous.Faces.Any(face => IsGeometrySupported(candidate, face.Bounds)))
                {
                    hasPrevious = true;
                }

                if (strongResults.TryGetValue(frameIndex + distance, out var next) &&
                    ComputeSceneDifference(current.SceneSample, next.SceneSample) <= SceneDifferenceThreshold &&
                    next.Faces.Any(face => IsGeometrySupported(candidate, face.Bounds)))
                {
                    hasNext = true;
                }

                if (hasPrevious && hasNext)
                    return true;
            }

            return false;
        }

        private static unsafe byte[] CaptureSceneSample(
            IntPtr data,
            int stride,
            int width,
            int height)
        {
            const int sampleColumns = 24;
            const int sampleRows = 14;
            var sample = new byte[sampleColumns * sampleRows];
            if (data == IntPtr.Zero || stride <= 0 || width <= 0 || height <= 0)
                return sample;

            byte* basePtr = (byte*)data;
            int index = 0;
            for (int sy = 0; sy < sampleRows; sy++)
            {
                int y = Math.Clamp((int)Math.Round((sy + 0.5) * height / sampleRows), 0, height - 1);
                byte* row = basePtr + y * stride;
                for (int sx = 0; sx < sampleColumns; sx++)
                {
                    int x = Math.Clamp((int)Math.Round((sx + 0.5) * width / sampleColumns), 0, width - 1);
                    byte* pixel = row + x * 4;
                    sample[index++] = (byte)((pixel[0] + pixel[1] * 2 + pixel[2]) / 4);
                }
            }

            return sample;
        }

        private static double ComputeSceneDifference(byte[] a, byte[] b)
        {
            if (a.Length == 0 || a.Length != b.Length)
                return 1.0;

            long total = 0;
            for (int i = 0; i < a.Length; i++)
                total += Math.Abs(a[i] - b[i]);

            return total / (a.Length * 255.0);
        }

        private static bool IsGeometrySupported(Rect candidate, Rect reference)
        {
            if (ComputeIou(candidate, reference) >= TemporalSupportIou)
                return true;

            double candidateArea = Math.Max(1.0, candidate.Width * candidate.Height);
            double referenceArea = Math.Max(1.0, reference.Width * reference.Height);
            double areaRatio = Math.Max(candidateArea, referenceArea) / Math.Min(candidateArea, referenceArea);
            if (areaRatio > TemporalSupportMaxAreaRatio)
                return false;

            double dx = candidate.Center.X - reference.Center.X;
            double dy = candidate.Center.Y - reference.Center.Y;
            double scale = Math.Max(1.0, Math.Max(candidate.Width, candidate.Height));
            return Math.Sqrt(dx * dx + dy * dy) / scale <= TemporalSupportMaxCenterShift;
        }

        private static double ComputeIou(Rect a, Rect b)
        {
            double left = Math.Max(a.Left, b.Left);
            double top = Math.Max(a.Top, b.Top);
            double right = Math.Min(a.Right, b.Right);
            double bottom = Math.Min(a.Bottom, b.Bottom);
            double intersection = Math.Max(0.0, right - left) * Math.Max(0.0, bottom - top);
            if (intersection <= 0)
                return 0.0;

            double union = a.Width * a.Height + b.Width * b.Height - intersection;
            return union > 0 ? intersection / union : 0.0;
        }

        private readonly record struct StrongFrameResult(
            PixelSize Size,
            IReadOnlyList<FaceDetectionResult> Faces,
            byte[] SceneSample);

        private readonly record struct FrameRange(int Start, int End);
    }

    internal sealed record YoloRiskCascadeResult(
        bool Enabled,
        int RiskFrames,
        int PeriodicFrames,
        int Attempts,
        int HitFrames,
        int CandidateFaces,
        int AcceptedFrames,
        int AcceptedFaces,
        int RejectedFaces,
        long DecodeMs,
        long DetectMs,
        long TotalMs,
        string ReasonBreakdown,
        string? Error)
    {
        public static YoloRiskCascadeResult Empty { get; } = new(
            Enabled: false,
            RiskFrames: 0,
            PeriodicFrames: 0,
            Attempts: 0,
            HitFrames: 0,
            CandidateFaces: 0,
            AcceptedFrames: 0,
            AcceptedFaces: 0,
            RejectedFaces: 0,
            DecodeMs: 0,
            DetectMs: 0,
            TotalMs: 0,
            ReasonBreakdown: "none",
            Error: null);
    }
}
