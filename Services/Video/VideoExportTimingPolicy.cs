using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;

namespace FaceShield.Services.Video;

internal static class VideoExportTimingPolicy
{
    internal static unsafe long ResolveEncodePts(
        AVFrame* frame,
        AVRational sourceTimeBase,
        AVRational targetTimeBase,
        int fallbackFrameIndex,
        double sourceFps,
        ref long lastPts,
        ref bool hasLastPts)
    {
        long frameStep = GetVideoFrameStep(sourceFps, targetTimeBase);
        long rawPts = frame != null
            ? (frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE
                ? frame->best_effort_timestamp
                : frame->pts)
            : ffmpeg.AV_NOPTS_VALUE;

        long normalizedPts;
        if (rawPts == ffmpeg.AV_NOPTS_VALUE)
        {
            normalizedPts = hasLastPts
                ? lastPts + frameStep
                : fallbackFrameIndex * frameStep;
        }
        else
        {
            normalizedPts = rawPts;
            if (sourceTimeBase.num > 0 &&
                sourceTimeBase.den > 0 &&
                targetTimeBase.num > 0 &&
                targetTimeBase.den > 0)
            {
                normalizedPts = ffmpeg.av_rescale_q(rawPts, sourceTimeBase, targetTimeBase);
            }
        }

        if (!hasLastPts)
        {
            hasLastPts = true;
        }
        else if (normalizedPts <= lastPts)
        {
            normalizedPts = lastPts + Math.Max(1, frameStep);
        }

        lastPts = normalizedPts;
        return normalizedPts;
    }

    internal static long GetVideoFrameStep(double sourceFps, AVRational targetTimeBase)
    {
        if (sourceFps <= 0.0 || targetTimeBase.den == 0 || targetTimeBase.num == 0)
            return 1;

        double tickSeconds = ffmpeg.av_q2d(targetTimeBase);
        if (tickSeconds <= 0.0 || double.IsNaN(tickSeconds) || double.IsInfinity(tickSeconds))
            return 1;

        double frameStep = (1.0 / sourceFps) / tickSeconds;
        if (double.IsNaN(frameStep) || double.IsInfinity(frameStep))
            return 1;

        return Math.Max(1, (long)Math.Round(frameStep));
    }

    internal static unsafe bool NormalizeEncodedPacketTimestamps(
        AVPacket* packet,
        ref long lastPacketPts,
        ref bool hasLastPacketPts,
        ref long lastPacketDts,
        ref bool hasLastPacketDts,
        long expectedFrameStep = 1)
    {
        if (packet == null)
            return false;

        expectedFrameStep = Math.Max(1, expectedFrameStep);
        bool hadAdjustment = false;

        long noValue = ffmpeg.AV_NOPTS_VALUE;
        long originalPts = packet->pts;
        long originalDts = packet->dts;

        long normalizedPts = packet->pts;
        if (normalizedPts == noValue)
        {
            normalizedPts = packet->dts != noValue
                ? packet->dts
                : hasLastPacketPts
                    ? lastPacketPts + expectedFrameStep
                    : hasLastPacketDts
                        ? lastPacketDts + expectedFrameStep
                        : 0;
            hadAdjustment = true;
        }

        long normalizedDts = packet->dts;
        if (normalizedDts == noValue)
        {
            normalizedDts = normalizedPts;
            hadAdjustment = true;
        }

        if (normalizedPts != originalPts || normalizedDts != originalDts)
            hadAdjustment = true;

        packet->pts = normalizedPts;
        packet->dts = normalizedDts;

        lastPacketPts = normalizedPts;
        hasLastPacketPts = true;
        lastPacketDts = normalizedDts;
        hasLastPacketDts = true;

        return hadAdjustment;
    }

    internal static unsafe bool NormalizeCopiedPacketTimestamps(
        AVPacket* packet,
        ref long lastPacketPts,
        ref bool hasLastPacketPts,
        ref long lastPacketDts,
        ref bool hasLastPacketDts,
        long expectedFrameStep = 1)
        => NormalizeEncodedPacketTimestamps(
            packet,
            ref lastPacketPts,
            ref hasLastPacketPts,
            ref lastPacketDts,
            ref hasLastPacketDts,
            expectedFrameStep);

    internal static unsafe long ResolveEncodeDuration(
        AVFrame* frame,
        AVRational sourceTimeBase,
        AVRational targetTimeBase)
    {
        long duration = frame == null ? 0 : frame->duration;
        if (duration <= 0 ||
            sourceTimeBase.num <= 0 ||
            sourceTimeBase.den <= 0 ||
            targetTimeBase.num <= 0 ||
            targetTimeBase.den <= 0)
            return 0;

        return Math.Max(
            0,
            ffmpeg.av_rescale_q(duration, sourceTimeBase, targetTimeBase));
    }

    internal static long ResolveEncodedPacketDuration(
        long packetDuration,
        long encoderPacketPts,
        Dictionary<long, long> submittedEncodedFrameDurations)
    {
        if (encoderPacketPts == ffmpeg.AV_NOPTS_VALUE ||
            !submittedEncodedFrameDurations.Remove(
                encoderPacketPts,
                out long submittedDuration))
        {
            return packetDuration;
        }

        return packetDuration == 0 && submittedDuration > 0
            ? submittedDuration
            : packetDuration;
    }

    internal static unsafe void ApplyEncodingTiming(AVFrame* frame, long pts, long duration)
    {
        if (frame == null)
            return;

        frame->pts = pts;
        frame->pkt_dts = ffmpeg.AV_NOPTS_VALUE;
        frame->duration = Math.Max(0, duration);
    }

    internal static unsafe int ResolveFrameIndexFromPacket(
        AVPacket* packet,
        AVRational timeBase,
        double sourceFps,
        int fallback,
        int totalFrames,
        ref long timestampBase,
        ref bool hasTimestampBase)
    {
        if (packet == null || sourceFps <= 0.0)
        {
            if (totalFrames > 0)
                return Math.Clamp(fallback, 0, totalFrames - 1);
            return Math.Max(0, fallback);
        }

        if (totalFrames > 0)
            fallback = Math.Clamp(fallback, 0, totalFrames - 1);
        else if (fallback < 0)
            fallback = 0;

        long ts = packet->dts != ffmpeg.AV_NOPTS_VALUE
            ? packet->dts
            : packet->pts;
        if (ts == ffmpeg.AV_NOPTS_VALUE)
            return fallback;

        long normalizedTs = hasTimestampBase
            ? ts - timestampBase
            : ts;

        if (!hasTimestampBase)
        {
            timestampBase = ts;
            hasTimestampBase = true;
            normalizedTs = 0;
        }

        double seconds = normalizedTs * ffmpeg.av_q2d(timeBase);
        if (double.IsNaN(seconds) || double.IsInfinity(seconds))
            return fallback;

        int index = (int)Math.Floor(seconds * sourceFps);
        if (index < 0)
            index = 0;
        if (totalFrames > 0 && index >= totalFrames)
            index = Math.Max(0, totalFrames - 1);
        return index;
    }

    internal static (int OutlierCount, long MaxOutputGap) EvaluateEncodedPresentationGaps(
        IReadOnlySet<long> submittedEncodedFramePts,
        IReadOnlyDictionary<long, int> emittedEncodedFramePts,
        IReadOnlyCollection<long> emittedEncodedMuxPts)
    {
        var submitted = new List<long>(submittedEncodedFramePts.Count);
        foreach (long pts in submittedEncodedFramePts)
        {
            if (pts == ffmpeg.AV_NOPTS_VALUE)
                throw new ArgumentException("Submitted PTS contains AV_NOPTS_VALUE.");
            submitted.Add(pts);
        }
        submitted.Sort();

        int emittedCount = 0;
        foreach (int count in emittedEncodedFramePts.Values)
        {
            if (count > 0)
                emittedCount = checked(emittedCount + count);
        }

        var emitted = new List<long>(emittedCount);
        foreach (var (pts, count) in emittedEncodedFramePts)
        {
            if (count > 0 && pts == ffmpeg.AV_NOPTS_VALUE)
                throw new ArgumentException("Emitted PTS contains AV_NOPTS_VALUE.");
            for (int i = 0; i < count; i++)
                emitted.Add(pts);
        }
        emitted.Sort();

        int submittedGapCount = Math.Max(0, submitted.Count - 1);
        int emittedGapCount = Math.Max(0, emitted.Count - 1);
        int commonGapCount = Math.Min(submittedGapCount, emittedGapCount);
        int outlierCount = Math.Abs(submittedGapCount - emittedGapCount);

        for (int i = 1; i <= commonGapCount; i++)
        {
            ulong submittedGap = GetSortedPresentationGap(submitted[i - 1], submitted[i]);
            ulong emittedGap = GetSortedPresentationGap(emitted[i - 1], emitted[i]);
            if (submittedGap != emittedGap)
                outlierCount++;
        }

        return (outlierCount, GetMaxSortedPresentationGap(emittedEncodedMuxPts));
    }

    internal static long GetMaxSortedPresentationGap(IReadOnlyCollection<long> presentationPts)
    {
        var sorted = new List<long>(presentationPts.Count);
        foreach (long pts in presentationPts)
        {
            if (pts == ffmpeg.AV_NOPTS_VALUE)
                throw new ArgumentException("Presentation PTS contains AV_NOPTS_VALUE.");
            sorted.Add(pts);
        }

        if (sorted.Count < 2)
            return 0;

        sorted.Sort();
        ulong maxGap = 0;
        for (int i = 1; i < sorted.Count; i++)
        {
            ulong gap = GetSortedPresentationGap(sorted[i - 1], sorted[i]);
            if (gap > maxGap)
                maxGap = gap;
        }

        return maxGap > long.MaxValue ? long.MaxValue : (long)maxGap;
    }

    private static ulong GetSortedPresentationGap(long previousPts, long currentPts)
    {
        if (currentPts < previousPts)
            throw new ArgumentOutOfRangeException(nameof(currentPts));

        return unchecked((ulong)currentPts - (ulong)previousPts);
    }
}
