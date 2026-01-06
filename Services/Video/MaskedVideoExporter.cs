using Avalonia.Media.Imaging;
using FFmpeg.AutoGen;
using System;

namespace FaceShield.Services.Video;

public unsafe sealed class MaskedVideoExporter
{
    private byte[]? _temp;
    private byte[]? _blurred;
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

        int downscale = blurRadius >= 8 ? 2 : 1;
        int sw = Math.Max(1, w / downscale);
        int sh = Math.Max(1, h / downscale);
        int sStride = sw * 4;
        int sSize = sStride * sh;

        EnsureBuffers(stride, h, sSize);
        EnsureMaskHistory(w, h);

        int r = Math.Max(1, blurRadius / downscale);
        int kernel = r * 2 + 1;

        fixed (byte* tempPtr = _temp)
        fixed (byte* blurPtr = _blurred)
        {
            byte* srcSmall = blurPtr;
            byte* tmpSmall = tempPtr;

            if (downscale > 1)
            {
                // Downscale BGRA to small buffer (nearest).
                for (int y = 0; y < sh; y++)
                {
                    int sy = Math.Min(h - 1, y * downscale);
                    byte* srcRow = data + sy * stride;
                    byte* dstRow = srcSmall + y * sStride;
                    for (int x = 0; x < sw; x++)
                    {
                        int sx = Math.Min(w - 1, x * downscale);
                        byte* src = srcRow + sx * 4;
                        byte* dst = dstRow + x * 4;
                        dst[0] = src[0];
                        dst[1] = src[1];
                        dst[2] = src[2];
                        dst[3] = src[3];
                    }
                }
            }
            else
            {
                Buffer.MemoryCopy(data, srcSmall, sSize, sSize);
            }

            // Horizontal blur.
            for (int y = 0; y < sh; y++)
            {
                byte* srcRow = srcSmall + y * sStride;
                byte* tempRow = tmpSmall + y * sStride;

                int sumB = 0, sumG = 0, sumR = 0, sumA = 0;
                for (int x = -r; x <= r; x++)
                {
                    int cx = x < 0 ? 0 : (x >= sw ? sw - 1 : x);
                    byte* p = srcRow + cx * 4;
                    sumB += p[0];
                    sumG += p[1];
                    sumR += p[2];
                    sumA += p[3];
                }

                byte* dst = tempRow;
                dst[0] = (byte)(sumB / kernel);
                dst[1] = (byte)(sumG / kernel);
                dst[2] = (byte)(sumR / kernel);
                dst[3] = (byte)(sumA / kernel);

                for (int x = 1; x < sw; x++)
                {
                    int addX = x + r;
                    if (addX >= sw) addX = sw - 1;
                    int subX = x - r - 1;
                    if (subX < 0) subX = 0;

                    byte* pAdd = srcRow + addX * 4;
                    byte* pSub = srcRow + subX * 4;
                    sumB += pAdd[0] - pSub[0];
                    sumG += pAdd[1] - pSub[1];
                    sumR += pAdd[2] - pSub[2];
                    sumA += pAdd[3] - pSub[3];

                    dst = tempRow + x * 4;
                    dst[0] = (byte)(sumB / kernel);
                    dst[1] = (byte)(sumG / kernel);
                    dst[2] = (byte)(sumR / kernel);
                    dst[3] = (byte)(sumA / kernel);
                }
            }

            // Vertical blur.
            for (int x = 0; x < sw; x++)
            {
                int sumB = 0, sumG = 0, sumR = 0, sumA = 0;
                for (int y = -r; y <= r; y++)
                {
                    int cy = y < 0 ? 0 : (y >= sh ? sh - 1 : y);
                    byte* p = tmpSmall + cy * sStride + x * 4;
                    sumB += p[0];
                    sumG += p[1];
                    sumR += p[2];
                    sumA += p[3];
                }

                byte* dst = srcSmall + x * 4;
                dst[0] = (byte)(sumB / kernel);
                dst[1] = (byte)(sumG / kernel);
                dst[2] = (byte)(sumR / kernel);
                dst[3] = (byte)(sumA / kernel);

                for (int y = 1; y < sh; y++)
                {
                    int addY = y + r;
                    if (addY >= sh) addY = sh - 1;
                    int subY = y - r - 1;
                    if (subY < 0) subY = 0;

                    byte* pAdd = tmpSmall + addY * sStride + x * 4;
                    byte* pSub = tmpSmall + subY * sStride + x * 4;
                    sumB += pAdd[0] - pSub[0];
                    sumG += pAdd[1] - pSub[1];
                    sumR += pAdd[2] - pSub[2];
                    sumA += pAdd[3] - pSub[3];

                    dst = srcSmall + y * sStride + x * 4;
                    dst[0] = (byte)(sumB / kernel);
                    dst[1] = (byte)(sumG / kernel);
                    dst[2] = (byte)(sumR / kernel);
                    dst[3] = (byte)(sumA / kernel);
                }
            }

            // Apply blurred pixels where mask alpha is set.
            for (int y = 0; y < h; y++)
            {
                byte* srcRow = data + y * stride;
                int mi = y * w;

                for (int x = 0; x < w; x++)
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
                    int sx = downscale > 1 ? Math.Min(sw - 1, x / downscale) : x;
                    int sy = downscale > 1 ? Math.Min(sh - 1, y / downscale) : y;
                    byte* blur = srcSmall + sy * sStride + sx * 4;

                    if (smooth == 255)
                    {
                        dst[0] = blur[0];
                        dst[1] = blur[1];
                        dst[2] = blur[2];
                        dst[3] = blur[3];
                    }
                    else
                    {
                        int inv = 255 - smooth;
                        dst[0] = (byte)((blur[0] * smooth + dst[0] * inv + 127) / 255);
                        dst[1] = (byte)((blur[1] * smooth + dst[1] * inv + 127) / 255);
                        dst[2] = (byte)((blur[2] * smooth + dst[2] * inv + 127) / 255);
                        dst[3] = 255;
                    }
                }
            }
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
}
