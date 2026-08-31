using System;
using System.Collections.Generic;

namespace FaceShield.Services.Video;

internal static class VideoExportFrameRangePolicy
{
    internal static HashSet<int> BuildBlurFrameSet(FrameMaskProvider provider)
    {
        var result = new HashSet<int>();
        if (provider == null || !provider.HasAnyMaskEntries())
            return result;

        foreach (int index in provider.GetStoredMaskFrameIndices())
        {
            if (index < 0)
            {
                throw new VideoExportIntegrityException(
                    $"음수 프레임 마스크 인덱스는 내보낼 수 없습니다 (frame={index}).");
            }
            result.Add(index);
        }

        foreach (int index in provider.GetFaceMaskFrameIndices())
        {
            if (index < 0)
            {
                throw new VideoExportIntegrityException(
                    $"음수 얼굴 마스크 인덱스는 내보낼 수 없습니다 (frame={index}).");
            }
            result.Add(index);
        }

        return result;
    }

    internal static int[] GetMissingExpectedFrameIndices(
        IReadOnlySet<int>? expectedFrameIndices,
        IReadOnlySet<int> appliedFrameIndices)
    {
        if (expectedFrameIndices == null || expectedFrameIndices.Count == 0)
            return Array.Empty<int>();

        var missing = new List<int>();
        foreach (int frameIndex in expectedFrameIndices)
        {
            if (!appliedFrameIndices.Contains(frameIndex))
                missing.Add(frameIndex);
        }

        missing.Sort();
        return missing.ToArray();
    }

    internal static List<(int Start, int EndExclusive)> BuildBlurFrameRanges(HashSet<int> blurFrameSet)
    {
        var ranges = new List<(int Start, int EndExclusive)>();
        if (blurFrameSet == null || blurFrameSet.Count == 0)
            return ranges;

        int[] sorted = new int[blurFrameSet.Count];
        blurFrameSet.CopyTo(sorted);
        Array.Sort(sorted);

        int start = sorted[0];
        int end = start + 1;
        for (int i = 1; i < sorted.Length; i++)
        {
            int current = sorted[i];
            if (current == end)
            {
                end++;
                continue;
            }

            ranges.Add((start, end));
            start = current;
            end = current + 1;
        }

        ranges.Add((start, end));
        return ranges;
    }

    internal static bool IsFrameInBlurRanges(
        int frameIndex,
        List<(int Start, int EndExclusive)> ranges,
        ref int cursor)
    {
        while (cursor < ranges.Count && frameIndex >= ranges[cursor].EndExclusive)
            cursor++;

        if (cursor >= ranges.Count)
            return false;

        var current = ranges[cursor];
        return frameIndex >= current.Start && frameIndex < current.EndExclusive;
    }

    internal static List<(int Start, int EndExclusive)> AlignRangesToKeyframes(
        List<(int Start, int EndExclusive)> ranges,
        List<int> keyframes,
        int totalFrames)
    {
        if (ranges == null || ranges.Count == 0 || keyframes == null || keyframes.Count == 0)
            return ranges ?? new List<(int Start, int EndExclusive)>();

        keyframes.Sort();
        if (keyframes[0] > 0)
            keyframes.Insert(0, 0);

        var aligned = new List<(int Start, int EndExclusive)>(ranges.Count);
        foreach (var (start, endExclusive) in ranges)
        {
            int safeStart = Math.Max(0, start);
            int safeEnd = Math.Max(safeStart + 1, endExclusive);
            int alignedStart = FindFloorKeyframe(keyframes, safeStart);
            int alignedEnd = FindNextKeyframe(keyframes, safeEnd - 1);
            if (alignedEnd <= alignedStart)
                alignedEnd = safeEnd;
            if (totalFrames > 0)
                alignedEnd = Math.Min(alignedEnd, totalFrames);
            aligned.Add((alignedStart, alignedEnd));
        }

        if (aligned.Count <= 1)
            return aligned;

        aligned.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(int Start, int EndExclusive)>(aligned.Count);
        int curStart = aligned[0].Start;
        int curEnd = aligned[0].EndExclusive;
        for (int i = 1; i < aligned.Count; i++)
        {
            var next = aligned[i];
            if (next.Start <= curEnd)
            {
                curEnd = Math.Max(curEnd, next.EndExclusive);
                continue;
            }

            merged.Add((curStart, curEnd));
            curStart = next.Start;
            curEnd = next.EndExclusive;
        }
        merged.Add((curStart, curEnd));
        return merged;
    }

    internal static bool IsKeyframeFrameIndex(List<int> keyframes, int frameIndex)
    {
        if (keyframes == null || keyframes.Count == 0)
            return false;

        int lo = 0;
        int hi = keyframes.Count - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            int current = keyframes[mid];
            if (current == frameIndex)
                return true;

            if (current < frameIndex)
                lo = mid + 1;
            else
                hi = mid - 1;
        }

        return false;
    }

    private static int FindFloorKeyframe(List<int> keyframes, int value)
    {
        int lo = 0;
        int hi = keyframes.Count - 1;
        int result = keyframes[0];
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            int current = keyframes[mid];
            if (current <= value)
            {
                result = current;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return result;
    }

    private static int FindNextKeyframe(List<int> keyframes, int value)
    {
        int lo = 0;
        int hi = keyframes.Count - 1;
        int result = keyframes[hi];
        bool found = false;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            int current = keyframes[mid];
            if (current > value)
            {
                result = current;
                found = true;
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return found ? result : Math.Max(value + 1, keyframes[keyframes.Count - 1] + 1);
    }
}
