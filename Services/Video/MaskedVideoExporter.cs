using Avalonia.Media.Imaging;
using FFmpeg.AutoGen;
using System;

namespace FaceShield.Services.Video;

public unsafe sealed class MaskedVideoExporter
{
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

    public void ApplyMaskAndBlur(AVFrame* bgraFrame, WriteableBitmap mask, int blurRadius)
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
                    int x0 = Math.Max(px0, x - r);
                    int x1 = Math.Min(px1 - 1, x + r);
                    int y0 = Math.Max(py0, y - r);
                    int y1 = Math.Min(py1 - 1, y + r);

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
