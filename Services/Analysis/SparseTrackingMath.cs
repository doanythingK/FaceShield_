using Avalonia;
using System;
using System.Collections.Generic;

namespace FaceShield.Services.Analysis;

internal static class SparseTrackingMath
{
    private const double TrackIouMin = 0.12;
    private const double MaxCenterShiftRatio = 1.2;
    private const double MaxAreaChangeRatio = 4.0;

    internal static int FindBestMatch(Rect source, IReadOnlyList<Rect> candidates, bool[] used)
    {
        int bestIndex = -1;
        double bestScore = 0.0;
        for (int i = 0; i < candidates.Count; i++)
        {
            if (i < used.Length && used[i])
                continue;

            var candidate = candidates[i];
            if (!IsReasonableTrack(source, candidate))
                continue;

            double iou = IoU(source, candidate);
            double score = iou > 0.0
                ? iou
                : 0.01 / Math.Max(0.01, CenterDistanceRatio(source, candidate));
            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    internal static bool IsReasonableTrack(Rect current, Rect next)
    {
        if (current.Width <= 0 || current.Height <= 0 || next.Width <= 0 || next.Height <= 0)
            return false;

        double area = Math.Max(1.0, current.Width * current.Height);
        double nextArea = Math.Max(1.0, next.Width * next.Height);
        double areaRatio = area / nextArea;
        if (areaRatio > MaxAreaChangeRatio || areaRatio < 1.0 / MaxAreaChangeRatio)
            return false;

        double iou = IoU(current, next);
        if (iou >= TrackIouMin)
            return true;

        return CenterDistanceRatio(current, next) <= MaxCenterShiftRatio;
    }

    internal static double SignatureDifference(
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

    internal static double CenterDistanceRatio(Rect a, Rect b)
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

    internal static double IoU(Rect a, Rect b)
    {
        double ix1 = Math.Max(a.X, b.X);
        double iy1 = Math.Max(a.Y, b.Y);
        double ix2 = Math.Min(a.Right, b.Right);
        double iy2 = Math.Min(a.Bottom, b.Bottom);

        double iw = Math.Max(0.0, ix2 - ix1);
        double ih = Math.Max(0.0, iy2 - iy1);
        double inter = iw * ih;
        if (inter <= 0.0)
            return 0.0;

        double union = a.Width * a.Height + b.Width * b.Height - inter;
        return union <= 0.0 ? 0.0 : inter / union;
    }

    internal static Rect LerpRect(Rect from, Rect to, double t)
    {
        double keep = 1.0 - t;
        return new Rect(
            from.X * keep + to.X * t,
            from.Y * keep + to.Y * t,
            Math.Max(0.0, from.Width * keep + to.Width * t),
            Math.Max(0.0, from.Height * keep + to.Height * t));
    }

    internal static float LerpConfidence(
        IReadOnlyList<float> from,
        IReadOnlyList<float> to,
        int fromIndex,
        int toIndex,
        double t)
    {
        double keep = 1.0 - t;
        return (float)(GetConfidence(from, fromIndex) * keep + GetConfidence(to, toIndex) * t);
    }

    internal static float? MinConfidence(IReadOnlyList<float> values)
    {
        if (values.Count == 0)
            return null;

        float min = float.MaxValue;
        for (int i = 0; i < values.Count; i++)
            min = Math.Min(min, values[i]);
        return min == float.MaxValue ? null : min;
    }

    private static float GetConfidence(IReadOnlyList<float> values, int index)
        => index < 0 || index >= values.Count ? 1.0f : values[index];
}
