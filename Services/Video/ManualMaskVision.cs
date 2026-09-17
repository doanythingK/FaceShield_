using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace FaceShield.Services.Video;

internal readonly record struct ManualPoint(double X, double Y);

// x' = A*x - B*y + X; y' = B*x + A*y + Y.
internal readonly record struct ManualMotion(double A, double B, double X, double Y)
{
    internal static ManualMotion Identity => new(1, 0, 0, 0);
    internal double Scale => Math.Sqrt(A * A + B * B);
    internal double Angle => Math.Atan2(B, A);
    internal ManualPoint Apply(ManualPoint p) => new(A * p.X - B * p.Y + X, B * p.X + A * p.Y + Y);
    internal ManualMotion After(ManualMotion before) => new(
        A * before.A - B * before.B,
        B * before.A + A * before.B,
        A * before.X - B * before.Y + X,
        B * before.X + A * before.Y + Y);
}

internal readonly record struct ManualFeatureMatch(ManualPoint From, ManualPoint To, double Error);

internal static class ManualSimilarityEstimator
{
    internal static bool TryEstimate(IReadOnlyList<ManualFeatureMatch> matches, int frame,
        CancellationToken ct, out ManualMotion result, out List<ManualPoint> inliers, out double confidence)
    {
        result = ManualMotion.Identity;
        inliers = new List<ManualPoint>();
        confidence = 0;
        if (matches.Count < 6) return false;
        int bestCount = 0;
        double bestError = double.PositiveInfinity;
        ManualMotion best = result;
        uint seed = unchecked((uint)frame * 747796405u + 2891336453u);
        for (int trial = 0; trial < 128; trial++)
        {
            if ((trial & 15) == 0) ct.ThrowIfCancellationRequested();
            seed = unchecked(seed * 1664525u + 1013904223u);
            int i = (int)(seed % (uint)matches.Count);
            seed = unchecked(seed * 1664525u + 1013904223u);
            int j = (i + 1 + (int)(seed % (uint)(matches.Count - 1))) % matches.Count;
            ManualPoint p = matches[i].From, q = matches[j].From;
            ManualPoint u = matches[i].To, v = matches[j].To;
            double dx = q.X - p.X, dy = q.Y - p.Y;
            double denominator = dx * dx + dy * dy;
            if (denominator < 36) continue;
            double vx = v.X - u.X, vy = v.Y - u.Y;
            double a = (dx * vx + dy * vy) / denominator;
            double b = (dx * vy - dy * vx) / denominator;
            var hypothesis = new ManualMotion(a, b, u.X - a * p.X + b * p.Y,
                u.Y - b * p.X - a * p.Y);
            if (!AcceptStep(hypothesis)) continue;
            int count = 0;
            double error = 0;
            foreach (ManualFeatureMatch match in matches)
            {
                double d = DistanceSquared(hypothesis.Apply(match.From), match.To);
                if (d > 9) continue;
                count++;
                error += d;
            }
            if (count > bestCount || (count == bestCount && error < bestError))
            {
                best = hypothesis;
                bestCount = count;
                bestError = error;
            }
        }
        if (bestCount < 6 || bestCount < (int)Math.Ceiling(matches.Count * 0.60)) return false;
        var accepted = matches.Where(m => DistanceSquared(best.Apply(m.From), m.To) <= 9).ToList();
        if (accepted.Count < 6) return false;
        double fx = accepted.Average(m => m.From.X), fy = accepted.Average(m => m.From.Y);
        double tx = accepted.Average(m => m.To.X), ty = accepted.Average(m => m.To.Y);
        double dot = 0, cross = 0, variance = 0;
        foreach (ManualFeatureMatch match in accepted)
        {
            double x = match.From.X - fx, y = match.From.Y - fy;
            double u = match.To.X - tx, v = match.To.Y - ty;
            dot += x * u + y * v;
            cross += x * v - y * u;
            variance += x * x + y * y;
        }
        if (variance < 36) return false;
        double ra = dot / variance, rb = cross / variance;
        result = new ManualMotion(ra, rb, tx - ra * fx + rb * fy, ty - rb * fx - ra * fy);
        if (!AcceptStep(result)) return false;
        double residual = 0, appearance = 0;
        foreach (ManualFeatureMatch match in accepted)
        {
            double error = DistanceSquared(result.Apply(match.From), match.To);
            if (error > 9) continue;
            inliers.Add(match.To);
            residual += error;
            appearance += match.Error;
        }
        if (inliers.Count < 6 || inliers.Count < (int)Math.Ceiling(matches.Count * 0.60)) return false;
        double rms = Math.Sqrt(residual / inliers.Count);
        confidence = inliers.Count / (double)matches.Count *
            Math.Max(0, 1 - rms / 6) * Math.Max(0, 1 - appearance / inliers.Count);
        return double.IsFinite(confidence);
    }

    private static bool AcceptStep(ManualMotion motion) =>
        double.IsFinite(motion.A) && double.IsFinite(motion.B) &&
        double.IsFinite(motion.X) && double.IsFinite(motion.Y) &&
        motion.Scale >= 0.90 && motion.Scale <= 1.10 &&
        Math.Abs(motion.Angle) <= 0.35; // about 20 degrees between adjacent frames

    private static double DistanceSquared(ManualPoint a, ManualPoint b)
    {
        double x = a.X - b.X, y = a.Y - b.Y;
        return x * x + y * y;
    }
}

// Each level is a real downsampled image. Coordinates remain in level-zero pixels externally.
// A tracking run keeps two instances and reloads their arrays rather than allocating
// ~0.69 MB of new floats for every decoded 480x270 frame.
internal sealed class ManualImagePyramid
{
    private readonly record struct Level(float[] Gray, int Width, int Height);
    private readonly List<Level> _levels = new();

    internal ManualImagePyramid(int width, int height)
    {
        _levels.Add(new Level(new float[checked(width * height)], width, height));
        while (_levels.Count < 4 && Math.Min(width, height) >= 40)
        {
            int nw = width / 2, nh = height / 2;
            _levels.Add(new Level(new float[checked(nw * nh)], nw, nh));
            width = nw;
            height = nh;
        }
    }

    internal ManualImagePyramid(byte[] bgra, int stride, int width, int height, CancellationToken ct)
        : this(width, height)
    {
        Reload(bgra, stride, ct);
    }

    // Only call on the inactive pyramid: the previous frame must remain unchanged
    // through forward/backward flow, scene validation, and sample commit.
    internal void Reload(byte[] bgra, int stride, CancellationToken ct)
    {
        Level first = _levels[0];
        int width = first.Width, height = first.Height;
        float[] gray = first.Gray;
        for (int y = 0; y < height; y++)
        {
            if ((y & 15) == 0) ct.ThrowIfCancellationRequested();
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                int i = row + x * 4;
                gray[y * width + x] = (float)(bgra[i] * 0.114 + bgra[i + 1] * 0.587 + bgra[i + 2] * 0.299);
            }
        }
        for (int level = 1; level < _levels.Count; level++)
        {
            ct.ThrowIfCancellationRequested();
            Level source = _levels[level - 1], destination = _levels[level];
            float[] down = destination.Gray;
            for (int y = 0; y < destination.Height; y++)
            {
                if ((y & 15) == 0) ct.ThrowIfCancellationRequested();
                for (int x = 0; x < destination.Width; x++)
                {
                    int p = 2 * y * source.Width + 2 * x;
                    down[y * destination.Width + x] = (source.Gray[p] + source.Gray[p + 1] +
                        source.Gray[p + source.Width] + source.Gray[p + source.Width + 1]) * 0.25f;
                }
            }
        }
    }

    internal bool TryTrack(ManualImagePyramid next, ManualPoint point, CancellationToken ct,
        out ManualPoint found, out double error)
    {
        found = point;
        error = double.PositiveInfinity;
        if (_levels.Count != next._levels.Count) return false;
        double deltaX = 0, deltaY = 0;
        for (int level = _levels.Count - 1; level >= 0; level--)
        {
            ct.ThrowIfCancellationRequested();
            Level from = _levels[level], to = next._levels[level];
            double factor = 1 << level;
            double px = point.X / factor, py = point.Y / factor;
            if (!Inside(from, px, py, 5)) return false;
            if (level == _levels.Count - 1)
            {
                // Seed the coarsest optical flow with a small, bounded patch search.
                double best = double.PositiveInfinity;
                for (int dy = -8; dy <= 8; dy += 2)
                    for (int dx = -8; dx <= 8; dx += 2)
                    {
                        double e = PatchError(from, to, px, py, px + dx, py + dy);
                        if (e < best) { best = e; deltaX = dx; deltaY = dy; }
                    }
                if (!double.IsFinite(best)) return false;
            }
            else
            {
                deltaX *= 2;
                deltaY *= 2;
            }
            double qx = px + deltaX, qy = py + deltaY;
            for (int iteration = 0; iteration < 10; iteration++)
            {
                if (!Inside(to, qx, qy, 5)) return false;
                double xx = 0, xy = 0, yy = 0, bx = 0, by = 0;
                for (int v = -3; v <= 3; v++)
                    for (int u = -3; u <= 3; u++)
                    {
                        double sx = qx + u, sy = qy + v;
                        double gx = (Sample(to, sx + 1, sy) - Sample(to, sx - 1, sy)) * 0.5;
                        double gy = (Sample(to, sx, sy + 1) - Sample(to, sx, sy - 1)) * 0.5;
                        double difference = Sample(from, px + u, py + v) - Sample(to, sx, sy);
                        xx += gx * gx; xy += gx * gy; yy += gy * gy;
                        bx += gx * difference; by += gy * difference;
                    }
                double determinant = xx * yy - xy * xy;
                if (determinant < 1e-4) return false;
                double stepX = (yy * bx - xy * by) / determinant;
                double stepY = (xx * by - xy * bx) / determinant;
                if (!double.IsFinite(stepX) || !double.IsFinite(stepY)) return false;
                stepX = Math.Clamp(stepX, -2.5, 2.5);
                stepY = Math.Clamp(stepY, -2.5, 2.5);
                qx += stepX;
                qy += stepY;
                if (stepX * stepX + stepY * stepY < 0.0025) break;
            }
            if (!Inside(to, qx, qy, 5)) return false;
            deltaX = qx - px;
            deltaY = qy - py;
        }
        found = new ManualPoint(point.X + deltaX, point.Y + deltaY);
        error = PatchError(_levels[0], next._levels[0], point.X, point.Y, found.X, found.Y);
        return double.IsFinite(error);
    }

    private static bool Inside(Level level, double x, double y, int margin) =>
        x >= margin && y >= margin && x < level.Width - margin - 1 && y < level.Height - margin - 1;

    private static double Sample(Level level, double x, double y)
    {
        int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
        if (ix < 0 || iy < 0 || ix + 1 >= level.Width || iy + 1 >= level.Height) return 0;
        double fx = x - ix, fy = y - iy;
        int p = iy * level.Width + ix;
        return (level.Gray[p] * (1 - fx) + level.Gray[p + 1] * fx) * (1 - fy) +
            (level.Gray[p + level.Width] * (1 - fx) + level.Gray[p + level.Width + 1] * fx) * fy;
    }

    private static double PatchError(Level from, Level to, double x, double y, double qx, double qy)
    {
        if (!Inside(from, x, y, 4) || !Inside(to, qx, qy, 4)) return double.PositiveInfinity;
        double avgFrom = 0, avgTo = 0;
        for (int v = -2; v <= 2; v++)
            for (int u = -2; u <= 2; u++)
            {
                avgFrom += Sample(from, x + u, y + v);
                avgTo += Sample(to, qx + u, qy + v);
            }
        avgFrom /= 25;
        avgTo /= 25;
        double sum = 0;
        for (int v = -2; v <= 2; v++)
            for (int u = -2; u <= 2; u++)
                sum += Math.Abs((Sample(from, x + u, y + v) - avgFrom) -
                                (Sample(to, qx + u, qy + v) - avgTo));
        return sum / (25 * 255);
    }
}

internal readonly record struct ManualSceneEvidence(double Color, double Spatial, double Pixel, double Structure)
{
    internal bool StrongCut => (Color >= 0.38 && Spatial >= 0.28) ||
        (Pixel >= 0.32 && Spatial >= 0.30 && Structure >= 0.22) ||
        (Pixel >= 0.40 && Structure >= 0.40 && Spatial >= 0.16);
    internal bool Suspicious => Pixel >= 0.19 && (Spatial >= 0.11 || Structure >= 0.20);
    internal double Score => Math.Max(Math.Max(Color, Spatial), Math.Max(Pixel, Structure));
}

internal static class ManualSceneCut
{
    internal static ManualSceneEvidence Compare(byte[] previous, int prevStride, byte[] current, int stride,
        int width, int height, CancellationToken ct)
    {
        var globalA = new int[3 * 8]; var globalB = new int[3 * 8];
        var spatialA = new int[16 * 3 * 8]; var spatialB = new int[16 * 3 * 8];
        int step = Math.Max(2, Math.Min(width, height) / 64);
        int samples = 0;
        double pixels = 0, structure = 0;
        for (int y = step; y < height - step; y += step)
        {
            ct.ThrowIfCancellationRequested();
            for (int x = step; x < width - step; x += step)
            {
                int p = y * prevStride + x * 4, q = y * stride + x * 4;
                int cell = Math.Min(3, y * 4 / height) * 4 + Math.Min(3, x * 4 / width);
                for (int channel = 0; channel < 3; channel++)
                {
                    int a = previous[p + channel], b = current[q + channel];
                    globalA[channel * 8 + a / 32]++;
                    globalB[channel * 8 + b / 32]++;
                    spatialA[cell * 24 + channel * 8 + a / 32]++;
                    spatialB[cell * 24 + channel * 8 + b / 32]++;
                    pixels += Math.Abs(a - b) / (3.0 * 255);
                    int gxA = previous[p + channel + 4] - previous[p + channel - 4];
                    int gyA = previous[p + channel + prevStride] - previous[p + channel - prevStride];
                    int gxB = current[q + channel + 4] - current[q + channel - 4];
                    int gyB = current[q + channel + stride] - current[q + channel - stride];
                    structure += (Math.Abs(gxA - gxB) + Math.Abs(gyA - gyB)) / (6.0 * 255);
                }
                samples++;
            }
        }
        if (samples == 0) return default;
        double color = 0, spatial = 0;
        for (int i = 0; i < globalA.Length; i++) color += Math.Abs(globalA[i] - globalB[i]);
        for (int i = 0; i < spatialA.Length; i++) spatial += Math.Abs(spatialA[i] - spatialB[i]);
        return new ManualSceneEvidence(color / (6 * samples), spatial / (6 * samples),
            pixels / samples, structure / samples);
    }
}
