using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace FaceShield.Services.Video;

internal sealed record VideoHybridCopyPlan(
    bool AllowHybridCopy,
    int TotalFrames,
    List<(int Start, int EndExclusive)>? BlurRanges,
    (int Start, int EndExclusive)? EncodeWindow,
    List<int>? CandidateKeyframes,
    bool Attempted,
    string? FallbackReason,
    int WindowStartFrame,
    int WindowEndFrame);

internal static class VideoHybridCopyPolicy
{
    internal const bool EnableHybridCopyWindow = false;
    private const int MinHybridCopyFrames = 240;
    private const double MinHybridCopyRatio = 0.05;
    private const int MinHybridCopySideFrames = 24;
    internal const int MaxHybridCopyTimestampFixBeforeFallback = 0;
    internal const int MaxHybridCopyModeTransitionsBeforeFallback = 2;
    internal const int MaxHybridFrameGapBeforeFallback = 32;
    private const double MaxEstimatedFrameCountSkewRatio = 1.25;
    private const int MaxEstimatedFrameCountSkewAbsolute = 1200;
    internal const long MaxHybridFrameStepTolerance = 1;
    internal const long MaxHybridCopyPtsJitterDivisor = 10;
    internal const int MaxHybridPacketFrameIndexUnreliableSequence = 4;

    internal static VideoHybridCopyPlan BuildPlan(
        string inputPath,
        double sourceFps,
        int totalFrames,
        AVRational sourceTimeBase,
        bool allowHybridCopy,
        List<(int Start, int EndExclusive)>? blurRanges)
    {
        bool allowHybridCopyCurrent = allowHybridCopy;
        bool attempted = false;
        string? fallbackReason = null;
        (int Start, int EndExclusive)? encodeWindow = null;
        List<int>? candidateKeyframes = null;
        int windowStartFrame = -1;
        int windowEndFrame = -1;

        if (allowHybridCopyCurrent &&
            (sourceTimeBase.den <= 0 ||
             sourceTimeBase.num <= 0 ||
             sourceFps <= 0.0))
        {
            allowHybridCopyCurrent = false;
            attempted = true;
            fallbackReason =
                $"하이브리드 사용 보류(타임스탬프 기준 불안정): " +
                $"sourceFps={sourceFps:0.###}, " +
                $"timeBase={sourceTimeBase.num}/{sourceTimeBase.den}";
        }

        bool canCopyOutsideBlurWindow =
            allowHybridCopyCurrent &&
            blurRanges != null &&
            blurRanges.Count > 0 &&
            sourceFps > 0.0 &&
            totalFrames > 0 &&
            (blurRanges[0].Start > 0 ||
             blurRanges[^1].EndExclusive < totalFrames);

        if (!canCopyOutsideBlurWindow)
        {
            return new VideoHybridCopyPlan(
                allowHybridCopyCurrent,
                totalFrames,
                blurRanges,
                encodeWindow,
                candidateKeyframes,
                attempted,
                fallbackReason,
                windowStartFrame,
                windowEndFrame);
        }

        List<int> keyframes =
            VideoExportCopyPolicy.CollectKeyframeFrameIndices(
                inputPath,
                sourceFps,
                totalFrames,
                out int estimatedTotalFrames);
        candidateKeyframes = keyframes;

        if (keyframes.Count < 2)
        {
            canCopyOutsideBlurWindow = false;
            allowHybridCopyCurrent = false;
            attempted = true;
            string reason =
                $"키프레임 후보가 불충분해 하이브리드 비활성 " +
                $"(keyframes={keyframes.Count}, reported={totalFrames}, " +
                $"estimated={estimatedTotalFrames})";
            fallbackReason = AppendReason(fallbackReason, reason);
        }

        if (estimatedTotalFrames > 0)
        {
            int frameCountSkew = estimatedTotalFrames - totalFrames;
            int absFrameCountSkew = Math.Abs(frameCountSkew);
            bool disableHybridForUnstableFrameCount =
                absFrameCountSkew > MaxEstimatedFrameCountSkewAbsolute ||
                estimatedTotalFrames >
                    (int)(totalFrames * MaxEstimatedFrameCountSkewRatio) ||
                estimatedTotalFrames <
                    (int)(totalFrames / MaxEstimatedFrameCountSkewRatio);

            if (disableHybridForUnstableFrameCount &&
                canCopyOutsideBlurWindow)
            {
                canCopyOutsideBlurWindow = false;
                allowHybridCopyCurrent = false;
                attempted = true;
                string reason =
                    "키프레임 기반 총 프레임 추정치 불일치로 하이브리드 비활성 " +
                    $"(estimated={estimatedTotalFrames}, reported={totalFrames}, " +
                    $"delta={frameCountSkew})";
                fallbackReason = AppendReason(fallbackReason, reason);
            }
            else if (estimatedTotalFrames > totalFrames &&
                     canCopyOutsideBlurWindow)
            {
                Debug.WriteLine(
                    "[Export] detected frame count expansion from keyframe scan: " +
                    $"meta={totalFrames}, estimated={estimatedTotalFrames}");
                totalFrames = estimatedTotalFrames;
            }
        }

        blurRanges = VideoExportFrameRangePolicy.AlignRangesToKeyframes(
            blurRanges!,
            keyframes,
            totalFrames);

        if (blurRanges.Count == 0)
        {
            fallbackReason =
                "키프레임 정렬 후 블러 구간이 비어 하이브리드 후보가 사라짐";
            return new VideoHybridCopyPlan(
                allowHybridCopyCurrent,
                totalFrames,
                blurRanges,
                encodeWindow,
                candidateKeyframes,
                attempted,
                fallbackReason,
                windowStartFrame,
                windowEndFrame);
        }

        int encodeStart = blurRanges[0].Start;
        int encodeEnd = blurRanges[^1].EndExclusive;
        int copiedFrames = totalFrames - (encodeEnd - encodeStart);
        int leadingCopyFrames = encodeStart;
        int trailingCopyFrames = totalFrames - encodeEnd;
        int minExpectedCopyFrames = Math.Max(
            MinHybridCopyFrames,
            (int)Math.Ceiling(totalFrames * MinHybridCopyRatio));

        if (copiedFrames < minExpectedCopyFrames)
        {
            fallbackReason =
                $"하이브리드 복사 구간 이득이 미미함(복사구간={copiedFrames}, " +
                $"총={totalFrames}, 임계치={minExpectedCopyFrames})";
        }
        else if (encodeStart == 0 || encodeEnd == totalFrames)
        {
            fallbackReason =
                "하이브리드 구간이 영상 끝단에 붙어 있어 한쪽 재인코드 경계로만 " +
                $"동작합니다(start={encodeStart}, end={encodeEnd}, total={totalFrames})";
        }
        else if ((encodeStart > 0 &&
                  leadingCopyFrames < MinHybridCopySideFrames) ||
                 (encodeEnd < totalFrames &&
                  trailingCopyFrames < MinHybridCopySideFrames))
        {
            fallbackReason =
                $"복사 쪽 경계 보강 필요(leading={leadingCopyFrames}, " +
                $"trailing={trailingCopyFrames}, 최소={MinHybridCopySideFrames})";
        }
        else if (encodeStart > 0 || encodeEnd < totalFrames)
        {
            encodeWindow = (encodeStart, encodeEnd);
            windowStartFrame = encodeStart;
            windowEndFrame = encodeEnd;
            attempted = true;
        }
        else
        {
            fallbackReason = null;
        }

        return new VideoHybridCopyPlan(
            allowHybridCopyCurrent,
            totalFrames,
            blurRanges,
            encodeWindow,
            candidateKeyframes,
            attempted,
            fallbackReason,
            windowStartFrame,
            windowEndFrame);
    }

    internal static string AppendReason(
        string? existingReason,
        string reason)
    {
        return string.IsNullOrWhiteSpace(existingReason)
            ? reason
            : $"{existingReason}; {reason}";
    }
}
