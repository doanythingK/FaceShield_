using Avalonia.Media.Imaging;
using FFmpeg.AutoGen;
using System;

namespace FaceShield.Services.Video;

public unsafe sealed class MaskedVideoExporter
{
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

        for (int y = 0; y < h; y++)
        {
            byte* row = data + y * stride;
            int mi = y * w;

            for (int x = 0; x < w; x++)
            {
                byte alpha = (byte)(m[mi + x] >> 24);
                if (alpha == 0) continue;

                BoxBlurPixel(data, stride, w, h, x, y, blurRadius);
            }
        }
    }

    private static void BoxBlurPixel(byte* data, int stride, int w, int h, int cx, int cy, int r)
    {
        int sumB = 0, sumG = 0, sumR = 0, sumA = 0, count = 0;

        for (int yy = cy - r; yy <= cy + r; yy++)
        {
            if ((uint)yy >= (uint)h) continue;
            byte* row = data + yy * stride;

            for (int xx = cx - r; xx <= cx + r; xx++)
            {
                if ((uint)xx >= (uint)w) continue;

                byte* p = row + xx * 4;
                sumB += p[0];
                sumG += p[1];
                sumR += p[2];
                sumA += p[3];
                count++;
            }
        }

        if (count == 0) return;

        byte* dst = data + cy * stride + cx * 4;
        dst[0] = (byte)(sumB / count);
        dst[1] = (byte)(sumG / count);
        dst[2] = (byte)(sumR / count);
        dst[3] = (byte)(sumA / count);
    }
}
