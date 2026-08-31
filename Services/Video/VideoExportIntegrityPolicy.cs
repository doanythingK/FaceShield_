using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace FaceShield.Services.Video;

internal sealed record VideoExportIntegrityResult(
    int SampleWindowLimit,
    int SampleWindowSourceFrames,
    int SampleWindowProducedFrames,
    int SampleWindowFrameShortfall,
    int ExpectedHybridWindowEncodedFrames,
    int EncodedWindowFrameShortfall,
    int OutputPacketPtsGapOutlierCount,
    long MaxOutputPacketPtsGap,
    int MissingEncodedFrameCount,
    int UnexpectedEncodedFrameCount,
    int VideoFrameCoverageMismatch,
    int VideoFrameDropCount,
    int OutputPacketCountMismatch,
    int DroppedVideoPacketsForSummary,
    bool ShouldRetryWithFullEncode,
    string? PacketDropFallbackReason,
    string? PacketLossFallbackReason);

internal static class VideoExportIntegrityPolicy
{
    private const int MaxAllowedOutputPacketLoss = 0;

    internal static VideoExportIntegrityResult Evaluate(
        int totalFrames,
        int exportSampleWindowFrames,
        int copiedSourceVideoPacketCount,
        int copiedVideoPacketCount,
        IReadOnlySet<long> submittedEncodedFramePts,
        IReadOnlyDictionary<long, int> emittedEncodedFramePts,
        IReadOnlyList<long> emittedEncodedMuxPts,
        bool useHybridCopyWindow,
        int encodeWindowStart,
        int encodeWindowEnd,
        int encodedWindowFrameCount,
        int outputVideoPacketCount,
        VideoPacketTimestampIntegrity encodedTimestampIntegrity,
        bool allowPacketDropRetry,
        int inputVideoPacketCount,
        int submittedVideoFrameCount,
        int copyGapOutlierCount,
        long maxCopyGap,
        string? packetDropFallbackReason)
    {
        int sampleWindowLimit = totalFrames > 0
            ? Math.Min(exportSampleWindowFrames, totalFrames)
            : exportSampleWindowFrames;

        int sampleWindowSourceFrames = Math.Min(
            sampleWindowLimit,
            copiedSourceVideoPacketCount + submittedEncodedFramePts.Count);
        int sampleWindowProducedFrames = Math.Min(
            sampleWindowLimit,
            copiedVideoPacketCount + emittedEncodedFramePts.Count);
        int sampleWindowFrameShortfall = Math.Max(
            0,
            sampleWindowSourceFrames - sampleWindowProducedFrames);

        int expectedHybridWindowEncodedFrames =
            useHybridCopyWindow && encodeWindowEnd > encodeWindowStart
                ? encodeWindowEnd - encodeWindowStart
                : 0;
        int encodedWindowFrameShortfall = Math.Max(
            0,
            expectedHybridWindowEncodedFrames - encodedWindowFrameCount);
        int hybridCopySourcePacketLoss = useHybridCopyWindow
            ? Math.Max(
                0,
                copiedSourceVideoPacketCount - copiedVideoPacketCount)
            : 0;
        int hybridEncodedWindowFrameLoss = useHybridCopyWindow
            ? encodedWindowFrameShortfall
            : 0;

        var frameCoverage =
            VideoExportPacketPolicy.EvaluateVideoFrameCoverage(
                copiedSourceVideoPacketCount,
                copiedVideoPacketCount,
                submittedEncodedFramePts,
                emittedEncodedFramePts);
        var presentationGapIntegrity =
            VideoExportTimingPolicy.EvaluateEncodedPresentationGaps(
                submittedEncodedFramePts,
                emittedEncodedFramePts,
                emittedEncodedMuxPts);

        int outputPacketPtsGapOutlierCount =
            presentationGapIntegrity.OutlierCount;
        long maxOutputPacketPtsGap =
            presentationGapIntegrity.MaxOutputGap;
        int missingEncodedFrameCount =
            frameCoverage.MissingEncodedFrames;
        int unexpectedEncodedFrameCount =
            frameCoverage.UnexpectedEncodedFrames;
        int expectedOutputVideoFrames =
            frameCoverage.ExpectedOutputFrames;
        int videoFrameCoverageMismatch =
            frameCoverage.MismatchCount;
        int videoFrameDropCount = useHybridCopyWindow
            ? Math.Max(
                frameCoverage.DropCount,
                hybridCopySourcePacketLoss +
                hybridEncodedWindowFrameLoss)
            : frameCoverage.DropCount;
        int outputPacketCountMismatch =
            frameCoverage.CopiedPacketMismatch;
        int droppedVideoPackets = hybridCopySourcePacketLoss;
        bool canRetryWithFullEncode =
            allowPacketDropRetry && useHybridCopyWindow;
        bool shouldRetryWithFullEncode = false;

        if (expectedOutputVideoFrames <= 0 ||
            outputVideoPacketCount <= 0)
        {
            string reason =
                $"final-output-frame-coverage-invalid=" +
                $"{expectedOutputVideoFrames}/{outputVideoPacketCount}";
            packetDropFallbackReason =
                VideoHybridCopyPolicy.AppendReason(
                    packetDropFallbackReason,
                    reason);

            if (canRetryWithFullEncode)
                shouldRetryWithFullEncode = true;
            else
            {
                throw new VideoExportIntegrityException(
                    "Invalid argument: 최종 출력 비디오 패킷 수가 유효하지 않아 " +
                    "품질 보전을 위해 중단합니다.");
            }
        }

        if (encodedTimestampIntegrity.MissingPacketTimestamps > 0 ||
            encodedTimestampIntegrity.PacketTimestampAdjustments > 0)
        {
            string reason =
                "encoded-packet-timestamps " +
                $"missing={encodedTimestampIntegrity.MissingPacketTimestamps}, " +
                $"adjustments={encodedTimestampIntegrity.PacketTimestampAdjustments}";
            packetDropFallbackReason =
                VideoHybridCopyPolicy.AppendReason(
                    packetDropFallbackReason,
                    reason);

            if (canRetryWithFullEncode)
                shouldRetryWithFullEncode = true;
            else
            {
                throw new VideoExportIntegrityException(
                    "Invalid argument: 인코더 출력 타임스탬프 보정이 필요해 " +
                    "품질 보전을 위해 중단합니다.");
            }
        }

        if (allowPacketDropRetry &&
            useHybridCopyWindow &&
            expectedHybridWindowEncodedFrames > 0 &&
            encodedWindowFrameShortfall > 0)
        {
            shouldRetryWithFullEncode = true;
            string reason =
                $"encode-window-frame-loss={encodedWindowFrameShortfall} " +
                $"(expected={expectedHybridWindowEncodedFrames}, " +
                $"produced={encodedWindowFrameCount})";
            packetDropFallbackReason =
                VideoHybridCopyPolicy.AppendReason(
                    packetDropFallbackReason,
                    reason);
        }

        if (allowPacketDropRetry &&
            useHybridCopyWindow &&
            inputVideoPacketCount > 0 &&
            videoFrameDropCount > 0)
        {
            shouldRetryWithFullEncode = true;
            string reason =
                $"hybrid-output-frame-loss={videoFrameDropCount} / " +
                $"inputPackets={inputVideoPacketCount}";
            packetDropFallbackReason =
                VideoHybridCopyPolicy.AppendReason(
                    packetDropFallbackReason,
                    reason);
        }

        if (videoFrameCoverageMismatch > MaxAllowedOutputPacketLoss)
        {
            string reason =
                "final-output-frame-coverage-mismatch=" +
                $"missing:{missingEncodedFrameCount}," +
                $"unexpected:{unexpectedEncodedFrameCount}," +
                $"copy:{copiedSourceVideoPacketCount}/{copiedVideoPacketCount}," +
                $"ptsGapOutliers:{outputPacketPtsGapOutlierCount}," +
                $"maxPtsGap:{maxOutputPacketPtsGap}";
            packetDropFallbackReason =
                VideoHybridCopyPolicy.AppendReason(
                    packetDropFallbackReason,
                    reason);

            if (canRetryWithFullEncode)
                shouldRetryWithFullEncode = true;
            else
            {
                throw new VideoExportIntegrityException(
                    "Invalid argument: 최종 출력 패킷 손실 감지로 " +
                    "품질 보전을 위해 중단합니다.");
            }
        }

        if (videoFrameDropCount > 0)
        {
            if (useHybridCopyWindow)
            {
                Debug.WriteLine(
                    "[Export] frameDropHint " +
                    $"copySource={copiedSourceVideoPacketCount}, " +
                    $"copyOutput={copiedVideoPacketCount}, " +
                    $"copyLoss={hybridCopySourcePacketLoss}, " +
                    $"encodeWindow={encodeWindowStart}-{encodeWindowEnd}, " +
                    $"encodedWindowFrames={encodedWindowFrameCount}, " +
                    $"encodedShortfall={hybridEncodedWindowFrameLoss}, " +
                    $"dropped={videoFrameDropCount}");
            }
            else
            {
                Debug.WriteLine(
                    "[Export] frameCoverageHint " +
                    $"submittedFrames={submittedVideoFrameCount}, " +
                    $"outputVideoPackets={outputVideoPacketCount}, " +
                    $"dropped={videoFrameDropCount}");
            }

            if (canRetryWithFullEncode)
            {
                shouldRetryWithFullEncode = true;
                string reason =
                    $"copy-source={copiedSourceVideoPacketCount}, " +
                    $"copy-output={copiedVideoPacketCount}, " +
                    $"droppedFrames={videoFrameDropCount}";
                packetDropFallbackReason =
                    VideoHybridCopyPolicy.AppendReason(
                        packetDropFallbackReason,
                        reason);
            }
        }

        if (outputPacketPtsGapOutlierCount > 0)
        {
            string reason =
                $"output-pts-gap-outlier-count={outputPacketPtsGapOutlierCount}, " +
                $"maxGap={maxOutputPacketPtsGap}";
            packetDropFallbackReason =
                VideoHybridCopyPolicy.AppendReason(
                    packetDropFallbackReason,
                    reason);

            if (canRetryWithFullEncode)
                shouldRetryWithFullEncode = true;
            else
            {
                throw new VideoExportIntegrityException(
                    "Invalid argument: 최종 출력 PTS 간격 이상치가 남아 " +
                    "품질 보전을 위해 중단합니다.");
            }
        }

        if (copyGapOutlierCount > 0 &&
            !shouldRetryWithFullEncode)
        {
            Debug.WriteLine(
                "[Export] hybrid copy source PTS gap observed " +
                $"count={copyGapOutlierCount}, maxGap={maxCopyGap}");
        }

        int droppedVideoPacketsForSummary =
            Math.Max(0, droppedVideoPackets);
        string? packetLossFallbackReason =
            shouldRetryWithFullEncode
                ? $"fallback-full-encode={packetDropFallbackReason}"
                : null;

        return new VideoExportIntegrityResult(
            sampleWindowLimit,
            sampleWindowSourceFrames,
            sampleWindowProducedFrames,
            sampleWindowFrameShortfall,
            expectedHybridWindowEncodedFrames,
            encodedWindowFrameShortfall,
            outputPacketPtsGapOutlierCount,
            maxOutputPacketPtsGap,
            missingEncodedFrameCount,
            unexpectedEncodedFrameCount,
            videoFrameCoverageMismatch,
            videoFrameDropCount,
            outputPacketCountMismatch,
            droppedVideoPacketsForSummary,
            shouldRetryWithFullEncode,
            packetDropFallbackReason,
            packetLossFallbackReason);
    }
}
