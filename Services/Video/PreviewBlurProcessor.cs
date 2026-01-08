using Avalonia;
using Avalonia.Media.Imaging;
using System;
using System.Collections.Generic;

namespace FaceShield.Services.Video
{
    // ✅ 마스크(알파>0) 영역에만 블러를 적용해 "프리뷰"용 비트맵을 생성
    public static class PreviewBlurProcessor
    {
        public static WriteableBitmap CreateBlurredFrame(WriteableBitmap src, int blurRadius)
        {
            if (blurRadius < 1) blurRadius = 1;

            var size = src.PixelSize;
            var outBmp = new WriteableBitmap(
                size,
                new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888,
                Avalonia.Platform.AlphaFormat.Premul);

            using var sfb = src.Lock();
            using var ofb = outBmp.Lock();

            unsafe
            {
                int w = size.Width;
                int h = size.Height;
                int sStride = sfb.RowBytes;
                int oStride = ofb.RowBytes;
                int window = blurRadius * 2 + 1;

                var temp = new byte[h * sStride];
                var dst = new byte[h * sStride];

                byte* sBase = (byte*)sfb.Address;
                fixed (byte* tempBase = temp)
                fixed (byte* dstBase = dst)
                {
                    for (int y = 0; y < h; y++)
                    {
                        byte* sRow = sBase + y * sStride;
                        int rowOffset = y * sStride;

                        for (int c = 0; c < 3; c++)
                        {
                            int sum = 0;
                            for (int i = -blurRadius; i <= blurRadius; i++)
                            {
                                int xi = i < 0 ? 0 : i >= w ? w - 1 : i;
                                sum += sRow[xi * 4 + c];
                            }

                            for (int x = 0; x < w; x++)
                            {
                                tempBase[rowOffset + x * 4 + c] = (byte)(sum / window);

                                int removeX = x - blurRadius;
                                int addX = x + blurRadius + 1;
                                int removeClamped = removeX < 0 ? 0 : removeX;
                                int addClamped = addX >= w ? w - 1 : addX;
                                sum += sRow[addClamped * 4 + c] - sRow[removeClamped * 4 + c];
                            }
                        }

                        for (int x = 0; x < w; x++)
                            tempBase[rowOffset + x * 4 + 3] = 255;
                    }

                    for (int x = 0; x < w; x++)
                    {
                        for (int c = 0; c < 3; c++)
                        {
                            int sum = 0;
                            for (int i = -blurRadius; i <= blurRadius; i++)
                            {
                                int yi = i < 0 ? 0 : i >= h ? h - 1 : i;
                                sum += tempBase[yi * sStride + x * 4 + c];
                            }

                            for (int y = 0; y < h; y++)
                            {
                                int idx = y * sStride + x * 4 + c;
                                dstBase[idx] = (byte)(sum / window);

                                int removeY = y - blurRadius;
                                int addY = y + blurRadius + 1;
                                int removeClamped = removeY < 0 ? 0 : removeY;
                                int addClamped = addY >= h ? h - 1 : addY;
                                sum += tempBase[addClamped * sStride + x * 4 + c]
                                    - tempBase[removeClamped * sStride + x * 4 + c];
                            }
                        }

                        for (int y = 0; y < h; y++)
                            dstBase[y * sStride + x * 4 + 3] = 255;
                    }

                    byte* oBase = (byte*)ofb.Address;
                    int copy = Math.Min(sStride, oStride);
                    for (int y = 0; y < h; y++)
                    {
                        Buffer.MemoryCopy(
                            dstBase + y * sStride,
                            oBase + y * oStride,
                            oStride,
                            copy);
                    }
                }
            }

            return outBmp;
        }

        public static WriteableBitmap ComposeMaskedPreview(
            WriteableBitmap src,
            WriteableBitmap blurred,
            WriteableBitmap mask,
            WriteableBitmap? target = null,
            Rect? dirtyRect = null)
        {
            var size = src.PixelSize;
            if (mask.PixelSize.Width != size.Width || mask.PixelSize.Height != size.Height)
                throw new InvalidOperationException("Mask size must match source size.");
            if (blurred.PixelSize.Width != size.Width || blurred.PixelSize.Height != size.Height)
                throw new InvalidOperationException("Blurred size must match source size.");

            var outBmp = target;
            bool fullUpdate = false;
            if (outBmp == null || outBmp.PixelSize.Width != size.Width || outBmp.PixelSize.Height != size.Height)
            {
                outBmp = new WriteableBitmap(
                    size,
                    new Vector(96, 96),
                    Avalonia.Platform.PixelFormat.Bgra8888,
                    Avalonia.Platform.AlphaFormat.Premul);
                fullUpdate = true;
            }

            int w = size.Width;
            int h = size.Height;
            int x0 = 0;
            int y0 = 0;
            int x1 = w - 1;
            int y1 = h - 1;

            if (!fullUpdate && dirtyRect != null)
            {
                var rect = dirtyRect.Value;
                x0 = Math.Clamp((int)Math.Floor(rect.X), 0, w - 1);
                y0 = Math.Clamp((int)Math.Floor(rect.Y), 0, h - 1);
                x1 = Math.Clamp((int)Math.Ceiling(rect.Right) - 1, 0, w - 1);
                y1 = Math.Clamp((int)Math.Ceiling(rect.Bottom) - 1, 0, h - 1);
                if (x1 < x0 || y1 < y0)
                    return outBmp;
            }

            using var sfb = src.Lock();
            using var bfb = blurred.Lock();
            using var mfb = mask.Lock();
            using var ofb = outBmp.Lock();

            unsafe
            {
                int sStride = sfb.RowBytes;
                int bStride = bfb.RowBytes;
                int mStride = mfb.RowBytes;
                int oStride = ofb.RowBytes;

                byte* sBase = (byte*)sfb.Address;
                byte* bBase = (byte*)bfb.Address;
                byte* mBase = (byte*)mfb.Address;
                byte* oBase = (byte*)ofb.Address;

                for (int y = y0; y <= y1; y++)
                {
                    byte* sRow = sBase + y * sStride;
                    byte* bRow = bBase + y * bStride;
                    byte* mRow = mBase + y * mStride;
                    byte* oRow = oBase + y * oStride;

                    for (int x = x0; x <= x1; x++)
                    {
                        byte a = mRow[x * 4 + 3];
                        byte* sP = sRow + x * 4;
                        byte* bP = bRow + x * 4;
                        byte* oP = oRow + x * 4;

                        if (a == 0)
                        {
                            oP[0] = sP[0];
                            oP[1] = sP[1];
                            oP[2] = sP[2];
                            oP[3] = 255;
                        }
                        else if (a == 255)
                        {
                            oP[0] = bP[0];
                            oP[1] = bP[1];
                            oP[2] = bP[2];
                            oP[3] = 255;
                        }
                        else
                        {
                            int inv = 255 - a;
                            oP[0] = (byte)((bP[0] * a + sP[0] * inv + 127) / 255);
                            oP[1] = (byte)((bP[1] * a + sP[1] * inv + 127) / 255);
                            oP[2] = (byte)((bP[2] * a + sP[2] * inv + 127) / 255);
                            oP[3] = 255;
                        }
                    }
                }
            }

            return outBmp;
        }

        // blurRadius: 1~30 정도 권장
        public static WriteableBitmap CreateBlurPreview(
            WriteableBitmap src,
            WriteableBitmap mask,
            int blurRadius,
            IReadOnlyList<Rect>? faces = null)
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
                if (faces != null && faces.Count > 0)
                {
                    foreach (var face in faces)
                    {
                        int r = GetFaceBlurRadius(face, w, h, blurRadius);
                        if (r <= 0)
                            continue;

                        var rect = GetPaddedRect(face, w, h);
                        int xStart = Math.Max(0, (int)Math.Floor(rect.X));
                        int yStart = Math.Max(0, (int)Math.Floor(rect.Y));
                        int xEnd = Math.Min(w - 1, (int)Math.Ceiling(rect.Right) - 1);
                        int yEnd = Math.Min(h - 1, (int)Math.Ceiling(rect.Bottom) - 1);

                        for (int y = yStart; y <= yEnd; y++)
                        {
                            byte* mRow = mBase + y * mStride;
                            byte* oRow = oBase + y * oStride;

                            for (int x = xStart; x <= xEnd; x++)
                            {
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

                                byte* outP = oRow + x * 4;
                                if (a == 255)
                                {
                                    outP[0] = b;
                                    outP[1] = g;
                                    outP[2] = rC;
                                    outP[3] = 255;
                                }
                                else
                                {
                                    int inv = 255 - a;
                                    outP[0] = (byte)((b * a + outP[0] * inv + 127) / 255);
                                    outP[1] = (byte)((g * a + outP[1] * inv + 127) / 255);
                                    outP[2] = (byte)((rC * a + outP[2] * inv + 127) / 255);
                                    outP[3] = 255;
                                }
                            }
                        }
                    }
                }
                else
                {
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

                            byte* outP = oRow + x * 4;
                            if (a == 255)
                            {
                                outP[0] = b;
                                outP[1] = g;
                                outP[2] = rC;
                                outP[3] = 255;
                            }
                            else
                            {
                                int inv = 255 - a;
                                outP[0] = (byte)((b * a + outP[0] * inv + 127) / 255);
                                outP[1] = (byte)((g * a + outP[1] * inv + 127) / 255);
                                outP[2] = (byte)((rC * a + outP[2] * inv + 127) / 255);
                                outP[3] = 255;
                            }
                        }
                    }
                }
            }

            return outBmp;
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
    }
}
