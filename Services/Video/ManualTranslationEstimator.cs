using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace FaceShield.Services.Video;

/// <summary>
/// A deliberately restricted fallback when there are too few consistent points
/// to estimate rotation and scale. Requires multiple spatially separated,
/// forward/backward-validated features; it never guesses from a blank patch.
/// </summary>
internal static class ManualTranslationEstimator
{
    private const int MinimumMatches = 3;
    private const double MaximumDisplacement = 24.0;
    private const double MaximumAppearanceError = 0.12;
    private const double ConsensusRadiusSquared = 2.25;

    internal static bool TryEstimate(
        IReadOnlyList<ManualFeatureMatch> matches, CancellationToken cancellationToken,
        out ManualMotion motion, out List<ManualPoint> inliers, out double confidence)
    {
        motion = ManualMotion.Identity;
        inliers = new List<ManualPoint>();
        confidence = 0;
        if (matches.Count < MinimumMatches) return false;

        List<ManualFeatureMatch>? best = null;
        double bestResidual = double.PositiveInfinity;
        int required = Math.Max(MinimumMatches, (int)Math.Ceiling(matches.Count * 0.75));
        for (int candidate = 0; candidate < matches.Count; candidate++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ManualFeatureMatch reference = matches[candidate];
            if (!IsUsable(reference)) continue;
            double dx = reference.To.X - reference.From.X;
            double dy = reference.To.Y - reference.From.Y;
            var group = new List<ManualFeatureMatch>();
            double residual = 0;
            foreach (ManualFeatureMatch match in matches)
            {
                if (!IsUsable(match)) continue;
                double ex = (match.To.X - match.From.X) - dx;
                double ey = (match.To.Y - match.From.Y) - dy;
                double error = ex * ex + ey * ey;
                if (error > ConsensusRadiusSquared) continue;
                group.Add(match);
                residual += error;
            }
            if (group.Count >= required &&
                (best == null || group.Count > best.Count ||
                 (group.Count == best.Count && residual < bestResidual)))
            {
                best = group;
                bestResidual = residual;
            }
        }
        if (best == null) return false;

        // Median motion is resistant to individual flow outliers, and a wide
        // baseline prevents multiple near-identical points from claiming a track.
        double[] dxs = best.Select(m => m.To.X - m.From.X).OrderBy(v => v).ToArray();
        double[] dys = best.Select(m => m.To.Y - m.From.Y).OrderBy(v => v).ToArray();
        double medianDx = dxs[dxs.Length / 2];
        double medianDy = dys[dys.Length / 2];
        if (Math.Abs(medianDx) > MaximumDisplacement ||
            Math.Abs(medianDy) > MaximumDisplacement) return false;

        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        double residualSum = 0, appearanceSum = 0;
        foreach (ManualFeatureMatch match in best)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double ex = (match.To.X - match.From.X) - medianDx;
            double ey = (match.To.Y - match.From.Y) - medianDy;
            double squared = ex * ex + ey * ey;
            if (squared > ConsensusRadiusSquared) continue;
            inliers.Add(match.To);
            residualSum += squared;
            appearanceSum += match.Error;
            minX = Math.Min(minX, match.From.X);
            maxX = Math.Max(maxX, match.From.X);
            minY = Math.Min(minY, match.From.Y);
            maxY = Math.Max(maxY, match.From.Y);
        }
        if (inliers.Count < required ||
            (maxX - minX) * (maxX - minX) +
            (maxY - minY) * (maxY - minY) < 36) return false;
        double rms = Math.Sqrt(residualSum / inliers.Count);
        double appearance = appearanceSum / inliers.Count;
        if (rms > 0.9 || appearance > MaximumAppearanceError) return false;
        confidence = Math.Min(0.85,
            inliers.Count / (double)matches.Count *
            Math.Max(0, 1 - rms / 3) * Math.Max(0, 1 - appearance));
        if (!double.IsFinite(confidence) || confidence < 0.52) return false;
        motion = new ManualMotion(1, 0, medianDx, medianDy);
        return true;
    }

    private static bool IsUsable(ManualFeatureMatch match)
    {
        if (!double.IsFinite(match.From.X) || !double.IsFinite(match.From.Y) ||
            !double.IsFinite(match.To.X) || !double.IsFinite(match.To.Y) ||
            !double.IsFinite(match.Error) || match.Error < 0 ||
            match.Error > MaximumAppearanceError) return false;
        double dx = match.To.X - match.From.X;
        double dy = match.To.Y - match.From.Y;
        return Math.Abs(dx) <= MaximumDisplacement &&
            Math.Abs(dy) <= MaximumDisplacement;
    }
}
