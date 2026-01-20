using Avalonia;
using Avalonia.Media.Imaging;
using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;

namespace FaceShield.Services.Video;

public unsafe sealed class MaskedVideoExporter
{
    private const double SoftEdgeRatio = 0.35;
    private byte[]? _temp;
    private byte[]? _blurred;
    private int[]? _integralB;
    private int[]? _integralG;
    private int[]? _integralR;
    private int[]? _integralA;
    private int _integralW;
    private int _integralH;
    private byte[]? _prevMaskAlpha;
    private int _prevMaskW;
    private int _prevMaskH;
    private byte[]? _radiusMap;
    private int _radiusMapW;
    private int _radiusMapH;

    private readonly struct FaceEllipse
    {
        public int X0 { get; init; }
        public int X1 { get; init; }
        public int Y0 { get; init; }
        public int Y1 { get; init; }
        public double Cx { get; init; }
        public double Cy { get; init; }
        public double Rx2 { get; init; }
        public double Ry2 { get; init; }
        public double Inner2 { get; init; }
        public double InvInner { get; init; }
        public bool SoftEdge { get; init; }
    }

    public void ApplyMaskAndBlur(
        AVFrame* bgraFrame,
        WriteableBitmap mask,
        int blurRadius,
        IReadOnlyList<Rect>? faces = null)
    {
        if (bgraFrame == null) throw new ArgumentNullException(nameof(bgraFrame));
        if (blurRadius <= 0) return;

        int w = bgraFrame->width;
        int h = bgraFrame->height;

        if (w <= 0 || h <= 0) return;
        if (mask.PixelSize.Width != w || mask.PixelSize.Height != h) return;

        using var fb = mask.Lock();

        byte* data = bgraFrame->data[0];          // BGRA
        int stride = bgraFrame->linesize[0];
        uint* m = (uint*)fb.Address;

        EnsureMaskHistory(w, h);

        int r = Math.Max(1, blurRadius);

        fixed (byte* _ = _temp)
        fixed (byte* __ = _blurred)
        {
            var (rx0, ry0, rx1, ry1) = GetMaskBounds(m, w, h);
            if (rx1 <= rx0 || ry1 <= ry0)
                return;

            int px0 = Math.Max(0, rx0 - r);
            int py0 = Math.Max(0, ry0 - r);
            int px1 = Math.Min(w, rx1 + r);
            int py1 = Math.Min(h, ry1 + r);
            int pw = Math.Max(1, px1 - px0);
            int ph = Math.Max(1, py1 - py0);

            EnsureIntegralBuffers(pw, ph);
            byte[]? radiusMap = null;
            int maxRadius = r;

            int rowStride = pw + 1;
            for (int y = 1; y <= ph; y++)
            {
                int sy = py0 + y - 1;
                byte* srcRow = data + sy * stride + px0 * 4;
                int rowIndex = y * rowStride;
                int prevIndex = (y - 1) * rowStride;

                int sumB = 0, sumG = 0, sumR = 0, sumA = 0;
                for (int x = 1; x <= pw; x++)
                {
                    byte* p = srcRow + (x - 1) * 4;
                    sumB += p[0];
                    sumG += p[1];
                    sumR += p[2];
                    sumA += p[3];

                    int idx = rowIndex + x;
                    _integralB![idx] = _integralB[prevIndex + x] + sumB;
                    _integralG![idx] = _integralG[prevIndex + x] + sumG;
                    _integralR![idx] = _integralR[prevIndex + x] + sumR;
                    _integralA![idx] = _integralA[prevIndex + x] + sumA;
                }
            }

            if (faces != null && faces.Count > 0)
            {
                maxRadius = r;
                radiusMap = EnsureRadiusMap(pw, ph);
                Array.Clear(radiusMap, 0, pw * ph);

                foreach (var face in faces)
                {
                    int faceRadius = GetFaceBlurRadius(face, w, h, r);
                    if (faceRadius <= 0)
                        continue;

                    var rect = GetPaddedRect(face, w, h);
                    int fx0 = Math.Max(px0, (int)Math.Floor(rect.X));
                    int fy0 = Math.Max(py0, (int)Math.Floor(rect.Y));
                    int fx1 = Math.Min(px1 - 1, (int)Math.Ceiling(rect.Right) - 1);
                    int fy1 = Math.Min(py1 - 1, (int)Math.Ceiling(rect.Bottom) - 1);

                    for (int y = fy0; y <= fy1; y++)
                    {
                        int row = (y - py0) * pw;
                        for (int x = fx0; x <= fx1; x++)
                        {
                            int idx = row + (x - px0);
                            if (faceRadius > radiusMap[idx])
                                radiusMap[idx] = (byte)faceRadius;
                        }
                    }
                }
            }

            // Apply blurred pixels where mask alpha is set.
            int maxWorkers = Math.Max(1, Environment.ProcessorCount - 2);
            var parallelOptions = new System.Threading.Tasks.ParallelOptions
            {
                MaxDegreeOfParallelism = maxWorkers
            };

            System.Threading.Tasks.Parallel.For(ry0, ry1, parallelOptions, y =>
            {
                byte* srcRow = data + y * stride;
                int mi = y * w;

                for (int x = rx0; x < rx1; x++)
                {
                    int maskIndex = mi + x;
                    byte alpha = (byte)(m[maskIndex] >> 24);
                    byte prev = _prevMaskAlpha![maskIndex];
                    byte smooth = alpha == 0
                        ? (byte)(prev * 3 / 4)
                        : prev == 0
                            ? alpha
                            : (byte)Math.Max(alpha, prev * 3 / 4);
                    _prevMaskAlpha[maskIndex] = smooth;

                    if (smooth == 0) continue;

                    byte* dst = srcRow + x * 4;
                    int localR = r;
                    if (radiusMap != null)
                    {
                        int idx = (y - py0) * pw + (x - px0);
                        byte mapped = radiusMap[idx];
                        if (mapped > 0)
                            localR = mapped;
                    }

                    int x0 = Math.Max(px0, x - localR);
                    int x1 = Math.Min(px1 - 1, x + localR);
                    int y0 = Math.Max(py0, y - localR);
                    int y1 = Math.Min(py1 - 1, y + localR);

                    int ix0 = x0 - px0;
                    int ix1 = x1 - px0;
                    int iy0 = y0 - py0;
                    int iy1 = y1 - py0;

                    int idxA = (iy1 + 1) * rowStride + (ix1 + 1);
                    int idxB = iy0 * rowStride + (ix1 + 1);
                    int idxC = (iy1 + 1) * rowStride + ix0;
                    int idxD = iy0 * rowStride + ix0;

                    int area = (ix1 - ix0 + 1) * (iy1 - iy0 + 1);
                    int sumB = _integralB![idxA] - _integralB[idxB] - _integralB[idxC] + _integralB[idxD];
                    int sumG = _integralG![idxA] - _integralG[idxB] - _integralG[idxC] + _integralG[idxD];
                    int sumR = _integralR![idxA] - _integralR[idxB] - _integralR[idxC] + _integralR[idxD];
                    int sumA = _integralA![idxA] - _integralA[idxB] - _integralA[idxC] + _integralA[idxD];

                    byte blurB = (byte)(sumB / area);
                    byte blurG = (byte)(sumG / area);
                    byte blurR = (byte)(sumR / area);
                    byte blurA = (byte)(sumA / area);

                    if (smooth == 255)
                    {
                        dst[0] = blurB;
                        dst[1] = blurG;
                        dst[2] = blurR;
                        dst[3] = blurA;
                    }
                    else
                    {
                        int inv = 255 - smooth;
                        dst[0] = (byte)((blurB * smooth + dst[0] * inv + 127) / 255);
                        dst[1] = (byte)((blurG * smooth + dst[1] * inv + 127) / 255);
                        dst[2] = (byte)((blurR * smooth + dst[2] * inv + 127) / 255);
                        dst[3] = 255;
                    }
                }
            });
        }
    }

    public void ApplyFaceRectsAndBlur(
        AVFrame* bgraFrame,
        PixelSize frameSize,
        IReadOnlyList<Rect> faces,
        int blurRadius)
    {
        if (bgraFrame == null) throw new ArgumentNullException(nameof(bgraFrame));
        if (faces == null || faces.Count == 0) return;
        if (blurRadius <= 0) return;

        int w = bgraFrame->width;
        int h = bgraFrame->height;
        if (w <= 0 || h <= 0) return;
        if (frameSize.Width != w || frameSize.Height != h) return;

        if (!TryBuildFaceEllipses(faces, w, h, out var ellipses, out var rx0, out var ry0, out var rx1, out var ry1))
            return;

        byte* data = bgraFrame->data[0];          // BGRA
        int stride = bgraFrame->linesize[0];

        EnsureMaskHistory(w, h);

        int r = Math.Max(1, blurRadius);

        int px0 = Math.Max(0, rx0 - r);
        int py0 = Math.Max(0, ry0 - r);
        int px1 = Math.Min(w, rx1 + r);
        int py1 = Math.Min(h, ry1 + r);
        int pw = Math.Max(1, px1 - px0);
        int ph = Math.Max(1, py1 - py0);

        EnsureIntegralBuffers(pw, ph);

        int rowStride = pw + 1;
        for (int y = 1; y <= ph; y++)
        {
            int sy = py0 + y - 1;
            byte* srcRow = data + sy * stride + px0 * 4;
            int rowIndex = y * rowStride;
            int prevIndex = (y - 1) * rowStride;

            int sumB = 0, sumG = 0, sumR = 0, sumA = 0;
            for (int x = 1; x <= pw; x++)
            {
                byte* p = srcRow + (x - 1) * 4;
                sumB += p[0];
                sumG += p[1];
                sumR += p[2];
                sumA += p[3];

                int idx = rowIndex + x;
                _integralB![idx] = _integralB[prevIndex + x] + sumB;
                _integralG![idx] = _integralG[prevIndex + x] + sumG;
                _integralR![idx] = _integralR[prevIndex + x] + sumR;
                _integralA![idx] = _integralA[prevIndex + x] + sumA;
            }
        }

        byte[]? radiusMap = null;
        radiusMap = EnsureRadiusMap(pw, ph);
        Array.Clear(radiusMap, 0, pw * ph);

        foreach (var face in faces)
        {
            int faceRadius = GetFaceBlurRadius(face, w, h, r);
            if (faceRadius <= 0)
                continue;

            var rect = GetPaddedRect(face, w, h);
            int fx0 = Math.Max(px0, (int)Math.Floor(rect.X));
            int fy0 = Math.Max(py0, (int)Math.Floor(rect.Y));
            int fx1 = Math.Min(px1 - 1, (int)Math.Ceiling(rect.Right) - 1);
            int fy1 = Math.Min(py1 - 1, (int)Math.Ceiling(rect.Bottom) - 1);

            for (int y = fy0; y <= fy1; y++)
            {
                int row = (y - py0) * pw;
                for (int x = fx0; x <= fx1; x++)
                {
                    int idx = row + (x - px0);
                    if (faceRadius > radiusMap[idx])
                        radiusMap[idx] = (byte)faceRadius;
                }
            }
        }

        int maxWorkers = Math.Max(1, Environment.ProcessorCount - 2);
        var parallelOptions = new System.Threading.Tasks.ParallelOptions
        {
            MaxDegreeOfParallelism = maxWorkers
        };

        System.Threading.Tasks.Parallel.For(ry0, ry1, parallelOptions, y =>
        {
            byte* srcRow = data + y * stride;
            int mi = y * w;

            for (int x = rx0; x < rx1; x++)
            {
                byte alpha = ComputeFaceAlpha(x, y, ellipses);
                int maskIndex = mi + x;
                byte prev = _prevMaskAlpha![maskIndex];
                byte smooth = alpha == 0
                    ? (byte)(prev * 3 / 4)
                    : prev == 0
                        ? alpha
                        : (byte)Math.Max(alpha, prev * 3 / 4);
                _prevMaskAlpha[maskIndex] = smooth;

                if (smooth == 0) continue;

                byte* dst = srcRow + x * 4;
                int localR = r;
                int idx = (y - py0) * pw + (x - px0);
                byte mapped = radiusMap[idx];
                if (mapped > 0)
                    localR = mapped;

                int x0 = Math.Max(px0, x - localR);
                int x1 = Math.Min(px1 - 1, x + localR);
                int y0 = Math.Max(py0, y - localR);
                int y1 = Math.Min(py1 - 1, y + localR);

                int ix0 = x0 - px0;
                int ix1 = x1 - px0;
                int iy0 = y0 - py0;
                int iy1 = y1 - py0;

                int idxA = (iy1 + 1) * rowStride + (ix1 + 1);
                int idxB = iy0 * rowStride + (ix1 + 1);
                int idxC = (iy1 + 1) * rowStride + ix0;
                int idxD = iy0 * rowStride + ix0;

                int area = (ix1 - ix0 + 1) * (iy1 - iy0 + 1);
                int sumB = _integralB![idxA] - _integralB[idxB] - _integralB[idxC] + _integralB[idxD];
                int sumG = _integralG![idxA] - _integralG[idxB] - _integralG[idxC] + _integralG[idxD];
                int sumR = _integralR![idxA] - _integralR[idxB] - _integralR[idxC] + _integralR[idxD];
                int sumA = _integralA![idxA] - _integralA[idxB] - _integralA[idxC] + _integralA[idxD];

                byte blurB = (byte)(sumB / area);
                byte blurG = (byte)(sumG / area);
                byte blurR = (byte)(sumR / area);
                byte blurA = (byte)(sumA / area);

                if (smooth == 255)
                {
                    dst[0] = blurB;
                    dst[1] = blurG;
                    dst[2] = blurR;
                    dst[3] = blurA;
                }
                else
                {
                    int inv = 255 - smooth;
                    dst[0] = (byte)((blurB * smooth + dst[0] * inv + 127) / 255);
                    dst[1] = (byte)((blurG * smooth + dst[1] * inv + 127) / 255);
                    dst[2] = (byte)((blurR * smooth + dst[2] * inv + 127) / 255);
                    dst[3] = 255;
                }
            }
        });
    }

    private void EnsureBuffers(int stride, int height, int scaledSize)
    {
        int size = stride * height;
        int required = Math.Max(size, scaledSize);
        if (_temp == null || _temp.Length < required)
            _temp = new byte[required];
        if (_blurred == null || _blurred.Length < required)
            _blurred = new byte[required];
    }

    private void EnsureIntegralBuffers(int width, int height)
    {
        int size = (width + 1) * (height + 1);
        if (_integralB == null || _integralB.Length < size || _integralW != width || _integralH != height)
        {
            _integralB = new int[size];
            _integralG = new int[size];
            _integralR = new int[size];
            _integralA = new int[size];
            _integralW = width;
            _integralH = height;
        }
        else
        {
            Array.Clear(_integralB, 0, size);
            Array.Clear(_integralG, 0, size);
            Array.Clear(_integralR, 0, size);
            Array.Clear(_integralA, 0, size);
        }
    }

    private void EnsureMaskHistory(int width, int height)
    {
        int size = width * height;
        if (_prevMaskAlpha == null || _prevMaskAlpha.Length < size || _prevMaskW != width || _prevMaskH != height)
        {
            _prevMaskAlpha = new byte[size];
            _prevMaskW = width;
            _prevMaskH = height;
        }
    }

    private byte[] EnsureRadiusMap(int width, int height)
    {
        int size = width * height;
        if (_radiusMap == null || _radiusMap.Length < size || _radiusMapW != width || _radiusMapH != height)
        {
            _radiusMap = new byte[size];
            _radiusMapW = width;
            _radiusMapH = height;
        }
        return _radiusMap;
    }

    private static bool TryBuildFaceEllipses(
        IReadOnlyList<Rect> faces,
        int width,
        int height,
        out List<FaceEllipse> ellipses,
        out int rx0,
        out int ry0,
        out int rx1,
        out int ry1)
    {
        ellipses = new List<FaceEllipse>(faces.Count);
        rx0 = width;
        ry0 = height;
        rx1 = 0;
        ry1 = 0;

        for (int i = 0; i < faces.Count; i++)
        {
            var rect = GetPaddedRect(faces[i], width, height);
            int x0 = Math.Clamp((int)Math.Floor(rect.X), 0, Math.Max(0, width - 1));
            int y0 = Math.Clamp((int)Math.Floor(rect.Y), 0, Math.Max(0, height - 1));
            int x1 = Math.Clamp((int)Math.Ceiling(rect.Right), 0, width);
            int y1 = Math.Clamp((int)Math.Ceiling(rect.Bottom), 0, height);

            if (x1 <= x0 || y1 <= y0)
                continue;

            double cx = (x0 + x1 - 1) / 2.0;
            double cy = (y0 + y1 - 1) / 2.0;
            double rx = Math.Max(1.0, (x1 - x0) / 2.0);
            double ry = Math.Max(1.0, (y1 - y0) / 2.0);
            double rx2 = rx * rx;
            double ry2 = ry * ry;
            double inner = 1.0 - SoftEdgeRatio;
            if (inner < 0.0)
                inner = 0.0;
            double inner2 = inner * inner;
            bool softEdge = SoftEdgeRatio > 0.0 && inner2 < 0.999;
            double invInner = softEdge ? (1.0 - inner2) : 1.0;
            if (invInner <= 0.0)
                invInner = 1.0;

            ellipses.Add(new FaceEllipse
            {
                X0 = x0,
                X1 = x1,
                Y0 = y0,
                Y1 = y1,
                Cx = cx,
                Cy = cy,
                Rx2 = rx2,
                Ry2 = ry2,
                Inner2 = inner2,
                InvInner = invInner,
                SoftEdge = softEdge
            });

            rx0 = Math.Min(rx0, x0);
            ry0 = Math.Min(ry0, y0);
            rx1 = Math.Max(rx1, x1);
            ry1 = Math.Max(ry1, y1);
        }

        if (ellipses.Count == 0)
        {
            rx0 = 0;
            ry0 = 0;
            rx1 = 0;
            ry1 = 0;
            return false;
        }

        return true;
    }

    private static byte ComputeFaceAlpha(int x, int y, IReadOnlyList<FaceEllipse> ellipses)
    {
        byte alpha = 0;
        for (int i = 0; i < ellipses.Count; i++)
        {
            var e = ellipses[i];
            if (x < e.X0 || x >= e.X1 || y < e.Y0 || y >= e.Y1)
                continue;

            double dx = x - e.Cx;
            double dy = y - e.Cy;
            double d2 = (dx * dx) / e.Rx2 + (dy * dy) / e.Ry2;
            if (d2 > 1.0)
                continue;

            byte a;
            if (!e.SoftEdge || d2 <= e.Inner2)
            {
                a = 255;
            }
            else
            {
                double t = (d2 - e.Inner2) / e.InvInner;
                if (t < 0.0) t = 0.0;
                if (t > 1.0) t = 1.0;
                t = t * t * (3.0 - 2.0 * t);
                a = (byte)Math.Round((1.0 - t) * 255.0);
            }

            if (a > alpha)
            {
                alpha = a;
                if (alpha == 255)
                    break;
            }
        }

        return alpha;
    }

    private static int GetFaceBlurRadius(Rect face, int frameW, int frameH, int baseRadius)
    {
        if (baseRadius <= 1)
            return Math.Max(1, baseRadius);

        double area = Math.Max(1.0, face.Width * face.Height);
        double frameArea = Math.Max(1.0, frameW * (double)frameH);
        double percent = area / frameArea * 100.0;

        double scale = percent <= 1.0 ? 0.4
            : percent <= 3.0 ? 0.55
            : percent <= 5.0 ? 0.7
            : 1.0;

        int r = (int)Math.Round(baseRadius * scale);
        return Math.Clamp(r, 1, baseRadius);
    }

    private static Rect GetPaddedRect(Rect face, int width, int height)
    {
        double padX = Math.Max(6.0, face.Width * 0.15);
        double padY = Math.Max(6.0, face.Height * 0.25);
        double x = Math.Max(0, face.X - padX);
        double y = Math.Max(0, face.Y - padY);
        double right = Math.Min(width, face.X + face.Width + padX);
        double bottom = Math.Min(height, face.Y + face.Height + padY);
        return new Rect(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }

    private static (int x0, int y0, int x1, int y1) GetMaskBounds(uint* mask, int w, int h)
    {
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                if (((mask[row + x] >> 24) & 0xFF) == 0)
                    continue;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }

        if (maxX < minX || maxY < minY)
            return (0, 0, 0, 0);

        return (minX, minY, maxX + 1, maxY + 1);
    }
}
