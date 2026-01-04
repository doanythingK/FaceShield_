using Avalonia.Media.Imaging;
using FFmpeg.AutoGen;
using System;

namespace FaceShield.Services.Video;

public unsafe sealed class MaskedVideoExporter
{
    private byte[]? _temp;
    private byte[]? _blurred;

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

        EnsureBuffers(stride, h);

        int r = Math.Min(blurRadius, Math.Max(w, h));
        int kernel = r * 2 + 1;

        fixed (byte* tempPtr = _temp)
        fixed (byte* blurPtr = _blurred)
        {
            // Horizontal blur.
            for (int y = 0; y < h; y++)
            {
                byte* srcRow = data + y * stride;
                byte* tempRow = tempPtr + y * stride;

                int sumB = 0, sumG = 0, sumR = 0, sumA = 0;
                for (int x = -r; x <= r; x++)
                {
                    int cx = x < 0 ? 0 : (x >= w ? w - 1 : x);
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

                for (int x = 1; x < w; x++)
                {
                    int addX = x + r;
                    if (addX >= w) addX = w - 1;
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
            for (int x = 0; x < w; x++)
            {
                int sumB = 0, sumG = 0, sumR = 0, sumA = 0;
                for (int y = -r; y <= r; y++)
                {
                    int cy = y < 0 ? 0 : (y >= h ? h - 1 : y);
                    byte* p = tempPtr + cy * stride + x * 4;
                    sumB += p[0];
                    sumG += p[1];
                    sumR += p[2];
                    sumA += p[3];
                }

                byte* dst = blurPtr + x * 4;
                dst[0] = (byte)(sumB / kernel);
                dst[1] = (byte)(sumG / kernel);
                dst[2] = (byte)(sumR / kernel);
                dst[3] = (byte)(sumA / kernel);

                for (int y = 1; y < h; y++)
                {
                    int addY = y + r;
                    if (addY >= h) addY = h - 1;
                    int subY = y - r - 1;
                    if (subY < 0) subY = 0;

                    byte* pAdd = tempPtr + addY * stride + x * 4;
                    byte* pSub = tempPtr + subY * stride + x * 4;
                    sumB += pAdd[0] - pSub[0];
                    sumG += pAdd[1] - pSub[1];
                    sumR += pAdd[2] - pSub[2];
                    sumA += pAdd[3] - pSub[3];

                    dst = blurPtr + y * stride + x * 4;
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
                byte* blurRow = blurPtr + y * stride;
                int mi = y * w;

                for (int x = 0; x < w; x++)
                {
                    byte alpha = (byte)(m[mi + x] >> 24);
                    if (alpha == 0) continue;

                    byte* dst = srcRow + x * 4;
                    byte* blur = blurRow + x * 4;

                    if (alpha == 255)
                    {
                        dst[0] = blur[0];
                        dst[1] = blur[1];
                        dst[2] = blur[2];
                        dst[3] = blur[3];
                    }
                    else
                    {
                        int inv = 255 - alpha;
                        dst[0] = (byte)((blur[0] * alpha + dst[0] * inv + 127) / 255);
                        dst[1] = (byte)((blur[1] * alpha + dst[1] * inv + 127) / 255);
                        dst[2] = (byte)((blur[2] * alpha + dst[2] * inv + 127) / 255);
                        dst[3] = 255;
                    }
                }
            }
        }
    }

    private void EnsureBuffers(int stride, int height)
    {
        int size = stride * height;
        if (_temp == null || _temp.Length < size)
            _temp = new byte[size];
        if (_blurred == null || _blurred.Length < size)
            _blurred = new byte[size];
    }
}
