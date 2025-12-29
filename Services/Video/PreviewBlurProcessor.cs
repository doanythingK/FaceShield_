using Avalonia;
using Avalonia.Media.Imaging;
using System;

namespace FaceShield.Services.Video
{
    // ✅ 마스크(알파>0) 영역에만 블러를 적용해 "프리뷰"용 비트맵을 생성
    public static class PreviewBlurProcessor
    {
        // blurRadius: 1~20 정도 권장
        public static WriteableBitmap CreateBlurPreview(WriteableBitmap src, WriteableBitmap mask, int blurRadius)
        {
            if (blurRadius < 1) blurRadius = 1;

            var size = src.PixelSize;
            if (mask.PixelSize.Width != size.Width || mask.PixelSize.Height != size.Height)
                throw new InvalidOperationException("Mask size must match source size.");

            var outBmp = new WriteableBitmap(
                size,
                new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888,
                Avalonia.Platform.AlphaFormat.Premul);

            using var sfb = src.Lock();
            using var mfb = mask.Lock();
            using var ofb = outBmp.Lock();

            unsafe
            {
                int w = size.Width;
                int h = size.Height;

                int sStride = sfb.RowBytes;
                int mStride = mfb.RowBytes;
                int oStride = ofb.RowBytes;

                byte* sBase = (byte*)sfb.Address;
                byte* mBase = (byte*)mfb.Address;
                byte* oBase = (byte*)ofb.Address;

                // 우선 원본 복사
                for (int y = 0; y < h; y++)
                {
                    Buffer.MemoryCopy(
                        sBase + y * sStride,
                        oBase + y * oStride,
                        oStride,
                        Math.Min(sStride, oStride));
                }

                // 마스크 영역만 블러로 덮어쓰기
                int r = blurRadius;
                for (int y = 0; y < h; y++)
                {
                    byte* mRow = mBase + y * mStride;
                    byte* oRow = oBase + y * oStride;

                    for (int x = 0; x < w; x++)
                    {
                        // mask는 BGRA. 알파만 보면 됨.
                        byte a = mRow[x * 4 + 3];
                        if (a == 0) continue;

                        int x0 = Math.Max(0, x - r);
                        int x1 = Math.Min(w - 1, x + r);
                        int y0 = Math.Max(0, y - r);
                        int y1 = Math.Min(h - 1, y + r);

                        int count = 0;
                        int sumB = 0, sumG = 0, sumR = 0;

                        for (int yy = y0; yy <= y1; yy++)
                        {
                            byte* sRow = sBase + yy * sStride;
                            for (int xx = x0; xx <= x1; xx++)
                            {
                                byte* p = sRow + xx * 4;
                                sumB += p[0];
                                sumG += p[1];
                                sumR += p[2];
                                count++;
                            }
                        }

                        if (count <= 0) continue;

                        byte b = (byte)(sumB / count);
                        byte g = (byte)(sumG / count);
                        byte rC = (byte)(sumR / count);

                        // premul 기준: 알파는 원본 프레임 그대로(255) 유지
                        byte* outP = oRow + x * 4;
                        outP[0] = b;
                        outP[1] = g;
                        outP[2] = rC;
                        outP[3] = 255;
                    }
                }
            }

            return outBmp;
        }
    }
}
