using Avalonia;
using Avalonia.Media.Imaging;
using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;

namespace FaceShield.Services.Video;

public unsafe sealed class MaskedVideoExporter
{
    private byte[]? _temp;
    private byte[]? _blurred;
    private uint[]? _integralB;
    private uint[]? _integralG;
    private uint[]? _integralR;
    private uint[]? _integralA;
    private byte[]? _radiusMap;
    private byte[]? _nativeAlpha;
    private long[]? _nativeIntegral;

    public static bool CanApplyNativeYuv(AVFrame* frame)
    {
        if (frame == null || frame->width <= 0 || frame->height <= 0)
            return false;

        AVPixelFormat format = (AVPixelFormat)frame->format;
        if (!TryGetNativeYuvLayout(format, out var layout))
            return false;

        int width = frame->width;
        int height = frame->height;
        int chromaWidth = DivideRoundUp(width, layout.ChromaShiftX);
        int chromaHeight = DivideRoundUp(height, layout.ChromaShiftY);
        if (!IsValidPlane(frame->data[0], frame->linesize[0], width * layout.BytesPerSample, height))
            return false;

        if (layout.InterleavedChroma)
        {
            return IsValidPlane(
                frame->data[1],
                frame->linesize[1],
                chromaWidth * layout.BytesPerSample * 2,
                chromaHeight);
        }

        int chromaRowBytes = chromaWidth * layout.BytesPerSample;
        return IsValidPlane(frame->data[1], frame->linesize[1], chromaRowBytes, chromaHeight) &&
               IsValidPlane(frame->data[2], frame->linesize[2], chromaRowBytes, chromaHeight);
    }

    public bool TryApplyFaceRectsAndBlurNative(
        AVFrame* frame,
        IReadOnlyList<Rect> faces,
        int blurRadius)
    {
        if (!CanApplyNativeYuv(frame) || faces == null || faces.Count == 0 || blurRadius <= 0)
            return false;

        int width = frame->width;
        int height = frame->height;
        var shapes = BuildFaceMaskShapes(faces, width, height);
        if (shapes.Length == 0)
            return false;

        int radius = Math.Max(1, blurRadius);
        var (rx0, ry0, rx1, ry1) = GetFaceBounds(faces, width, height);
        if (rx1 <= rx0 || ry1 <= ry0)
            return false;

        int px0 = Math.Max(0, rx0 - radius);
        int py0 = Math.Max(0, ry0 - radius);
        int px1 = Math.Min(width, rx1 + radius);
        int py1 = Math.Min(height, ry1 + radius);
        int paddedWidth = px1 - px0;
        int paddedHeight = py1 - py0;
        if (paddedWidth <= 0 || paddedHeight <= 0)
            return false;

        int alphaWidth = rx1 - rx0;
        int alphaHeight = ry1 - ry0;
        byte[] alpha = EnsureNativeAlpha(alphaWidth * alphaHeight);
        void BuildAlphaRow(int y)
        {
            int alphaRow = (y - ry0) * alphaWidth;
            for (int x = rx0; x < rx1; x++)
                alpha[alphaRow + x - rx0] = GetFaceAlpha(shapes, x, y);
        }
        int alphaArea = alphaWidth * alphaHeight;
        int maxWorkers = Math.Max(1, Environment.ProcessorCount - 2);
        if (maxWorkers > 1 && alphaArea >= 220_000)
        {
            System.Threading.Tasks.Parallel.For(
                ry0,
                ry1,
                new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = maxWorkers },
                BuildAlphaRow);
        }
        else
        {
            for (int y = ry0; y < ry1; y++)
                BuildAlphaRow(y);
        }

        byte[] radiusMap = EnsureRadiusMap(paddedWidth, paddedHeight);
        Array.Clear(radiusMap, 0, paddedWidth * paddedHeight);
        foreach (var face in faces)
        {
            int faceRadius = FaceBlurGeometry.GetRadius(face, width, height, radius);
            if (faceRadius <= 0)
                continue;

            Rect rect = FaceBlurGeometry.GetPaddedRect(face, width, height);
            int fx0 = Math.Max(px0, (int)Math.Floor(rect.X));
            int fy0 = Math.Max(py0, (int)Math.Floor(rect.Y));
            int fx1 = Math.Min(px1 - 1, (int)Math.Ceiling(rect.Right) - 1);
            int fy1 = Math.Min(py1 - 1, (int)Math.Ceiling(rect.Bottom) - 1);
            for (int y = fy0; y <= fy1; y++)
            {
                int row = (y - py0) * paddedWidth;
                for (int x = fx0; x <= fx1; x++)
                {
                    int index = row + x - px0;
                    if (faceRadius > radiusMap[index])
                        radiusMap[index] = (byte)Math.Min(byte.MaxValue, faceRadius);
                }
            }
        }

        ApplyNativeMaskPlanes(
            frame,
            rx0,
            ry0,
            rx1,
            ry1,
            px0,
            py0,
            px1,
            py1,
            alpha,
            alphaWidth,
            alphaHeight,
            radiusMap,
            paddedWidth,
            paddedHeight,
            radius);

        return true;
    }

    public bool TryApplyMaskAndBlurNative(
        AVFrame* frame,
        WriteableBitmap mask,
        int blurRadius)
    {
        if (!CanApplyNativeYuv(frame) || mask == null || blurRadius <= 0)
            return false;

        int width = frame->width;
        int height = frame->height;
        if (mask.PixelSize.Width != width || mask.PixelSize.Height != height)
            return false;

        using var framebuffer = mask.Lock();
        if (framebuffer.Address == IntPtr.Zero || framebuffer.RowBytes < width * 4)
            return false;

        byte* maskData = (byte*)framebuffer.Address;
        int maskStride = framebuffer.RowBytes;
        var (rx0, ry0, rx1, ry1) = GetMaskBounds(maskData, maskStride, width, height);
        if (rx1 <= rx0 || ry1 <= ry0)
            return false;

        int radius = Math.Max(1, blurRadius);
        int px0 = Math.Max(0, rx0 - radius);
        int py0 = Math.Max(0, ry0 - radius);
        int px1 = Math.Min(width, rx1 + radius);
        int py1 = Math.Min(height, ry1 + radius);
        int paddedWidth = px1 - px0;
        int paddedHeight = py1 - py0;
        if (paddedWidth <= 0 || paddedHeight <= 0)
            return false;

        int alphaWidth = rx1 - rx0;
        int alphaHeight = ry1 - ry0;
        byte[] alpha = EnsureNativeAlpha(alphaWidth * alphaHeight);
        for (int y = ry0; y < ry1; y++)
        {
            byte* maskRow = maskData + y * maskStride;
            int alphaRow = (y - ry0) * alphaWidth;
            for (int x = rx0; x < rx1; x++)
                alpha[alphaRow + x - rx0] = maskRow[x * 4 + 3];
        }

        byte[] radiusMap = EnsureRadiusMap(paddedWidth, paddedHeight);
        Array.Clear(radiusMap, 0, paddedWidth * paddedHeight);
        ApplyNativeMaskPlanes(
            frame,
            rx0,
            ry0,
            rx1,
            ry1,
            px0,
            py0,
            px1,
            py1,
            alpha,
            alphaWidth,
            alphaHeight,
            radiusMap,
            paddedWidth,
            paddedHeight,
            radius);

        return true;
    }

    public bool ApplyMaskAndBlur(
        AVFrame* bgraFrame,
        WriteableBitmap mask,
        int blurRadius,
        IReadOnlyList<Rect>? faces = null)
    {
        if (bgraFrame == null) throw new ArgumentNullException(nameof(bgraFrame));
        if (blurRadius <= 0) return false;

        int w = bgraFrame->width;
        int h = bgraFrame->height;

        if (w <= 0 || h <= 0) return false;
        if (mask.PixelSize.Width != w || mask.PixelSize.Height != h) return false;

        using var fb = mask.Lock();

        byte* data = bgraFrame->data[0];          // BGRA
        int stride = bgraFrame->linesize[0];
        byte* maskData = (byte*)fb.Address;
        int maskStride = fb.RowBytes;

        int r = Math.Max(1, blurRadius);

        fixed (byte* _ = _temp)
        fixed (byte* __ = _blurred)
        {
            var (rx0, ry0, rx1, ry1) = faces != null && faces.Count > 0
                ? GetFaceBounds(faces, w, h)
                : GetMaskBounds(maskData, maskStride, w, h);
            if (rx1 <= rx0 || ry1 <= ry0)
                return false;

            int px0 = Math.Max(0, rx0 - r);
            int py0 = Math.Max(0, ry0 - r);
            int px1 = Math.Min(w, rx1 + r);
            int py1 = Math.Min(h, ry1 + r);
            int pw = Math.Max(1, px1 - px0);
            int ph = Math.Max(1, py1 - py0);

            EnsureIntegralBuffers(pw, ph);
            byte[]? radiusMap = null;

            int rowStride = pw + 1;
            for (int y = 1; y <= ph; y++)
            {
                int sy = py0 + y - 1;
                byte* srcRow = data + sy * stride + px0 * 4;
                int rowIndex = y * rowStride;
                int prevIndex = (y - 1) * rowStride;

                uint sumB = 0, sumG = 0, sumR = 0, sumA = 0;
                for (int x = 1; x <= pw; x++)
                {
                    byte* p = srcRow + (x - 1) * 4;
                    sumB += p[0];
                    sumG += p[1];
                    sumR += p[2];
                    sumA += p[3];

                    int idx = rowIndex + x;
                    _integralB![idx] = unchecked(_integralB[prevIndex + x] + sumB);
                    _integralG![idx] = unchecked(_integralG[prevIndex + x] + sumG);
                    _integralR![idx] = unchecked(_integralR[prevIndex + x] + sumR);
                    _integralA![idx] = unchecked(_integralA[prevIndex + x] + sumA);
                }
            }

            if (faces != null && faces.Count > 0)
            {
                radiusMap = EnsureRadiusMap(pw, ph);
                Array.Clear(radiusMap, 0, pw * ph);

                foreach (var face in faces)
                {
                    int faceRadius = FaceBlurGeometry.GetRadius(face, w, h, r);
                    if (faceRadius <= 0)
                        continue;

                    var rect = FaceBlurGeometry.GetPaddedRect(face, w, h);
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
            int roiArea = (rx1 - rx0) * (ry1 - ry0);
            bool useParallel = maxWorkers > 1 && roiArea >= 220_000;

            void ProcessRow(int y)
            {
                byte* srcRow = data + y * stride;
                byte* maskRow = maskData + y * maskStride;

                for (int x = rx0; x < rx1; x++)
                {
                    byte alpha = maskRow[x * 4 + 3];
                    if (alpha == 0) continue;

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
                    uint sumB = GetIntegralSum(_integralB!, idxA, idxB, idxC, idxD);
                    uint sumG = GetIntegralSum(_integralG!, idxA, idxB, idxC, idxD);
                    uint sumR = GetIntegralSum(_integralR!, idxA, idxB, idxC, idxD);
                    uint sumA = GetIntegralSum(_integralA!, idxA, idxB, idxC, idxD);

                    byte blurB = (byte)(sumB / (uint)area);
                    byte blurG = (byte)(sumG / (uint)area);
                    byte blurR = (byte)(sumR / (uint)area);
                    byte blurA = (byte)(sumA / (uint)area);

                    if (alpha == 255)
                    {
                        dst[0] = blurB;
                        dst[1] = blurG;
                        dst[2] = blurR;
                        dst[3] = blurA;
                    }
                    else
                    {
                        int inv = 255 - alpha;
                        dst[0] = (byte)((blurB * alpha + dst[0] * inv + 127) / 255);
                        dst[1] = (byte)((blurG * alpha + dst[1] * inv + 127) / 255);
                        dst[2] = (byte)((blurR * alpha + dst[2] * inv + 127) / 255);
                        dst[3] = 255;
                    }
                }
            }

            if (useParallel)
            {
                System.Threading.Tasks.Parallel.For(ry0, ry1, parallelOptions, ProcessRow);
            }
            else
            {
                for (int y = ry0; y < ry1; y++)
                    ProcessRow(y);
            }
        }

        return true;
    }

    public bool ApplyFaceRectsAndBlur(
        AVFrame* bgraFrame,
        IReadOnlyList<Rect> faces,
        int blurRadius)
    {
        if (bgraFrame == null) throw new ArgumentNullException(nameof(bgraFrame));
        if (faces == null || faces.Count == 0) return false;
        if (blurRadius <= 0) return false;

        int w = bgraFrame->width;
        int h = bgraFrame->height;
        if (w <= 0 || h <= 0) return false;

        byte* data = bgraFrame->data[0];
        int stride = bgraFrame->linesize[0];

        int r = Math.Max(1, blurRadius);
        var (rx0, ry0, rx1, ry1) = GetFaceBounds(faces, w, h);
        if (rx1 <= rx0 || ry1 <= ry0)
            return false;

        int px0 = Math.Max(0, rx0 - r);
        int py0 = Math.Max(0, ry0 - r);
        int px1 = Math.Min(w, rx1 + r);
        int py1 = Math.Min(h, ry1 + r);
        int pw = Math.Max(1, px1 - px0);
        int ph = Math.Max(1, py1 - py0);

        var shapes = BuildFaceMaskShapes(faces, w, h);
        if (shapes.Length == 0)
            return false;

        if (shapes.Length == 1)
            return ApplySingleFaceRectAndBlur(bgraFrame, faces[0], shapes[0], blurRadius);

        EnsureIntegralBuffers(pw, ph);
        byte[] radiusMap = EnsureRadiusMap(pw, ph);
        Array.Clear(radiusMap, 0, pw * ph);

        int rowStride = pw + 1;
        for (int y = 1; y <= ph; y++)
        {
            int sy = py0 + y - 1;
            byte* srcRow = data + sy * stride + px0 * 4;
            int rowIndex = y * rowStride;
            int prevIndex = (y - 1) * rowStride;

            uint sumB = 0, sumG = 0, sumR = 0, sumA = 0;
            for (int x = 1; x <= pw; x++)
            {
                byte* p = srcRow + (x - 1) * 4;
                sumB += p[0];
                sumG += p[1];
                sumR += p[2];
                sumA += p[3];

                int idx = rowIndex + x;
                _integralB![idx] = unchecked(_integralB[prevIndex + x] + sumB);
                _integralG![idx] = unchecked(_integralG[prevIndex + x] + sumG);
                _integralR![idx] = unchecked(_integralR[prevIndex + x] + sumR);
                _integralA![idx] = unchecked(_integralA[prevIndex + x] + sumA);
            }
        }

        foreach (var face in faces)
        {
            int faceRadius = FaceBlurGeometry.GetRadius(face, w, h, r);
            if (faceRadius <= 0)
                continue;

            var rect = FaceBlurGeometry.GetPaddedRect(face, w, h);
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
        int roiArea = (rx1 - rx0) * (ry1 - ry0);
        bool useParallel = maxWorkers > 1 && roiArea >= 220_000;

        void ProcessRow(int y)
        {
            byte* srcRow = data + y * stride;

            for (int x = rx0; x < rx1; x++)
            {
                byte alpha = GetFaceAlpha(shapes, x, y);
                if (alpha == 0) continue;

                byte* dst = srcRow + x * 4;
                int localR = r;
                int radiusIdx = (y - py0) * pw + (x - px0);
                byte mapped = radiusMap[radiusIdx];
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
                uint sumB = GetIntegralSum(_integralB!, idxA, idxB, idxC, idxD);
                uint sumG = GetIntegralSum(_integralG!, idxA, idxB, idxC, idxD);
                uint sumR = GetIntegralSum(_integralR!, idxA, idxB, idxC, idxD);
                uint sumA = GetIntegralSum(_integralA!, idxA, idxB, idxC, idxD);

                byte blurB = (byte)(sumB / (uint)area);
                byte blurG = (byte)(sumG / (uint)area);
                byte blurR = (byte)(sumR / (uint)area);
                byte blurA = (byte)(sumA / (uint)area);

                if (alpha == 255)
                {
                    dst[0] = blurB;
                    dst[1] = blurG;
                    dst[2] = blurR;
                    dst[3] = blurA;
                }
                else
                {
                    int inv = 255 - alpha;
                    dst[0] = (byte)((blurB * alpha + dst[0] * inv + 127) / 255);
                    dst[1] = (byte)((blurG * alpha + dst[1] * inv + 127) / 255);
                    dst[2] = (byte)((blurR * alpha + dst[2] * inv + 127) / 255);
                    dst[3] = 255;
                }
            }
        }

        if (useParallel)
        {
            System.Threading.Tasks.Parallel.For(ry0, ry1, parallelOptions, ProcessRow);
        }
        else
        {
            for (int y = ry0; y < ry1; y++)
                ProcessRow(y);
        }

        return true;
    }

    private bool ApplySingleFaceRectAndBlur(
        AVFrame* bgraFrame,
        Rect face,
        FaceMaskShape shape,
        int blurRadius)
    {
        int w = bgraFrame->width;
        int h = bgraFrame->height;
        byte* data = bgraFrame->data[0];
        int stride = bgraFrame->linesize[0];
        int r = Math.Max(1, blurRadius);
        int faceRadius = FaceBlurGeometry.GetRadius(face, w, h, r);
        if (faceRadius <= 0)
            return false;

        int rx0 = shape.X0;
        int ry0 = shape.Y0;
        int rx1 = shape.X1;
        int ry1 = shape.Y1;
        if (rx1 <= rx0 || ry1 <= ry0)
            return false;

        int px0 = Math.Max(0, rx0 - faceRadius);
        int py0 = Math.Max(0, ry0 - faceRadius);
        int px1 = Math.Min(w, rx1 + faceRadius);
        int py1 = Math.Min(h, ry1 + faceRadius);
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

            uint sumB = 0, sumG = 0, sumR = 0, sumA = 0;
            for (int x = 1; x <= pw; x++)
            {
                byte* p = srcRow + (x - 1) * 4;
                sumB += p[0];
                sumG += p[1];
                sumR += p[2];
                sumA += p[3];

                int idx = rowIndex + x;
                _integralB![idx] = unchecked(_integralB[prevIndex + x] + sumB);
                _integralG![idx] = unchecked(_integralG[prevIndex + x] + sumG);
                _integralR![idx] = unchecked(_integralR[prevIndex + x] + sumR);
                _integralA![idx] = unchecked(_integralA[prevIndex + x] + sumA);
            }
        }

        int maxWorkers = Math.Max(1, Environment.ProcessorCount - 2);
        var parallelOptions = new System.Threading.Tasks.ParallelOptions
        {
            MaxDegreeOfParallelism = maxWorkers
        };
        int roiArea = (rx1 - rx0) * (ry1 - ry0);
        bool useParallel = maxWorkers > 1 && roiArea >= 220_000;

        void ProcessRow(int y)
        {
            byte* srcRow = data + y * stride;

            for (int x = rx0; x < rx1; x++)
            {
                byte alpha = GetSingleFaceAlpha(shape, x, y);
                if (alpha == 0)
                    continue;

                byte* dst = srcRow + x * 4;
                int x0 = Math.Max(px0, x - faceRadius);
                int x1 = Math.Min(px1 - 1, x + faceRadius);
                int y0 = Math.Max(py0, y - faceRadius);
                int y1 = Math.Min(py1 - 1, y + faceRadius);

                int ix0 = x0 - px0;
                int ix1 = x1 - px0;
                int iy0 = y0 - py0;
                int iy1 = y1 - py0;

                int idxA = (iy1 + 1) * rowStride + (ix1 + 1);
                int idxB = iy0 * rowStride + (ix1 + 1);
                int idxC = (iy1 + 1) * rowStride + ix0;
                int idxD = iy0 * rowStride + ix0;

                int area = (ix1 - ix0 + 1) * (iy1 - iy0 + 1);
                uint sumB = GetIntegralSum(_integralB!, idxA, idxB, idxC, idxD);
                uint sumG = GetIntegralSum(_integralG!, idxA, idxB, idxC, idxD);
                uint sumR = GetIntegralSum(_integralR!, idxA, idxB, idxC, idxD);
                uint sumA = GetIntegralSum(_integralA!, idxA, idxB, idxC, idxD);

                byte blurB = (byte)(sumB / (uint)area);
                byte blurG = (byte)(sumG / (uint)area);
                byte blurR = (byte)(sumR / (uint)area);
                byte blurA = (byte)(sumA / (uint)area);

                if (alpha == 255)
                {
                    dst[0] = blurB;
                    dst[1] = blurG;
                    dst[2] = blurR;
                    dst[3] = blurA;
                }
                else
                {
                    int inv = 255 - alpha;
                    dst[0] = (byte)((blurB * alpha + dst[0] * inv + 127) / 255);
                    dst[1] = (byte)((blurG * alpha + dst[1] * inv + 127) / 255);
                    dst[2] = (byte)((blurR * alpha + dst[2] * inv + 127) / 255);
                    dst[3] = 255;
                }
            }
        }

        if (useParallel)
        {
            System.Threading.Tasks.Parallel.For(ry0, ry1, parallelOptions, ProcessRow);
        }
        else
        {
            for (int y = ry0; y < ry1; y++)
                ProcessRow(y);
        }

        return true;
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

    private static bool IsValidPlane(byte* data, int stride, int requiredRowBytes, int height)
    {
        return data != null && height > 0 && stride >= requiredRowBytes;
    }

    private static int DivideRoundUp(int value, int shift)
    {
        int divisor = 1 << shift;
        return (value + divisor - 1) >> shift;
    }

    private static bool TryGetNativeYuvLayout(AVPixelFormat format, out NativeYuvLayout layout)
    {
        layout = format switch
        {
            AVPixelFormat.AV_PIX_FMT_YUV420P => new(1, 0, 1, 1, false),
            AVPixelFormat.AV_PIX_FMT_YUV422P => new(1, 0, 1, 0, false),
            AVPixelFormat.AV_PIX_FMT_YUV444P => new(1, 0, 0, 0, false),

            AVPixelFormat.AV_PIX_FMT_YUV420P9LE or
            AVPixelFormat.AV_PIX_FMT_YUV420P10LE or
            AVPixelFormat.AV_PIX_FMT_YUV420P12LE or
            AVPixelFormat.AV_PIX_FMT_YUV420P14LE or
            AVPixelFormat.AV_PIX_FMT_YUV420P16LE => new(2, 0, 1, 1, false),

            AVPixelFormat.AV_PIX_FMT_YUV422P9LE or
            AVPixelFormat.AV_PIX_FMT_YUV422P10LE or
            AVPixelFormat.AV_PIX_FMT_YUV422P12LE or
            AVPixelFormat.AV_PIX_FMT_YUV422P14LE or
            AVPixelFormat.AV_PIX_FMT_YUV422P16LE => new(2, 0, 1, 0, false),

            AVPixelFormat.AV_PIX_FMT_YUV444P9LE or
            AVPixelFormat.AV_PIX_FMT_YUV444P10LE or
            AVPixelFormat.AV_PIX_FMT_YUV444P12LE or
            AVPixelFormat.AV_PIX_FMT_YUV444P14LE or
            AVPixelFormat.AV_PIX_FMT_YUV444P16LE => new(2, 0, 0, 0, false),

            AVPixelFormat.AV_PIX_FMT_NV12 => new(1, 0, 1, 1, true),
            AVPixelFormat.AV_PIX_FMT_P010LE => new(2, 6, 1, 1, true),
            AVPixelFormat.AV_PIX_FMT_P012LE => new(2, 4, 1, 1, true),
            AVPixelFormat.AV_PIX_FMT_P016LE => new(2, 0, 1, 1, true),
            _ => default
        };
        return layout.BytesPerSample != 0;
    }

    private byte[] EnsureNativeAlpha(int size)
    {
        if (_nativeAlpha == null || _nativeAlpha.Length < size)
            _nativeAlpha = new byte[size];
        return _nativeAlpha;
    }

    private long[] EnsureNativeIntegral(int width, int height)
    {
        int size = checked((width + 1) * (height + 1));
        if (_nativeIntegral == null || _nativeIntegral.Length < size)
        {
            _nativeIntegral = new long[size];
        }
        else
        {
            int rowStride = width + 1;
            Array.Clear(_nativeIntegral, 0, rowStride);
            for (int y = 1; y <= height; y++)
                _nativeIntegral[y * rowStride] = 0;
        }
        return _nativeIntegral;
    }

    private void ApplyNativeMaskPlanes(
        AVFrame* frame,
        int rx0,
        int ry0,
        int rx1,
        int ry1,
        int px0,
        int py0,
        int px1,
        int py1,
        byte[] alpha,
        int alphaWidth,
        int alphaHeight,
        byte[] radiusMap,
        int radiusMapWidth,
        int radiusMapHeight,
        int baseRadius)
    {
        int width = frame->width;
        int height = frame->height;
        AVPixelFormat format = (AVPixelFormat)frame->format;
        if (!TryGetNativeYuvLayout(format, out var layout))
            return;

        ApplyNativePlane(
            frame->data[0],
            frame->linesize[0],
            width,
            height,
            layout.BytesPerSample,
            layout.BytesPerSample,
            0,
            layout.ValueShift,
            rx0,
            ry0,
            rx1,
            ry1,
            px0,
            py0,
            px1,
            py1,
            alpha,
            alphaWidth,
            alphaHeight,
            radiusMap,
            radiusMapWidth,
            radiusMapHeight,
            baseRadius,
            chroma: false);

        int chromaWidth = DivideRoundUp(width, layout.ChromaShiftX);
        int chromaHeight = DivideRoundUp(height, layout.ChromaShiftY);
        int chromaRx0 = rx0 >> layout.ChromaShiftX;
        int chromaRy0 = ry0 >> layout.ChromaShiftY;
        int chromaRx1 = DivideRoundUp(rx1, layout.ChromaShiftX);
        int chromaRy1 = DivideRoundUp(ry1, layout.ChromaShiftY);
        int chromaPx0 = px0 >> layout.ChromaShiftX;
        int chromaPy0 = py0 >> layout.ChromaShiftY;
        int chromaPx1 = DivideRoundUp(px1, layout.ChromaShiftX);
        int chromaPy1 = DivideRoundUp(py1, layout.ChromaShiftY);

        if (!layout.InterleavedChroma)
        {
            for (int plane = 1; plane <= 2; plane++)
            {
                ApplyNativePlane(
                    frame->data[(uint)plane],
                    frame->linesize[(uint)plane],
                    chromaWidth,
                    chromaHeight,
                    layout.BytesPerSample,
                    layout.BytesPerSample,
                    0,
                    layout.ValueShift,
                    chromaRx0,
                    chromaRy0,
                    chromaRx1,
                    chromaRy1,
                    chromaPx0,
                    chromaPy0,
                    chromaPx1,
                    chromaPy1,
                    alpha,
                    alphaWidth,
                    alphaHeight,
                    radiusMap,
                    radiusMapWidth,
                    radiusMapHeight,
                    baseRadius,
                    chroma: true,
                    chromaShiftX: layout.ChromaShiftX,
                    chromaShiftY: layout.ChromaShiftY,
                    lumaRx0: rx0,
                    lumaRy0: ry0,
                    lumaPx0: px0,
                    lumaPy0: py0);
            }
        }
        else
        {
            int componentStride = layout.BytesPerSample * 2;
            ApplyNativePlane(
                frame->data[1], frame->linesize[1], chromaWidth, chromaHeight,
                layout.BytesPerSample, componentStride, 0, layout.ValueShift,
                chromaRx0, chromaRy0, chromaRx1, chromaRy1,
                chromaPx0, chromaPy0, chromaPx1, chromaPy1,
                alpha, alphaWidth, alphaHeight, radiusMap, radiusMapWidth, radiusMapHeight, baseRadius,
                chroma: true, chromaShiftX: layout.ChromaShiftX, chromaShiftY: layout.ChromaShiftY,
                lumaRx0: rx0, lumaRy0: ry0, lumaPx0: px0, lumaPy0: py0);
            ApplyNativePlane(
                frame->data[1], frame->linesize[1], chromaWidth, chromaHeight,
                layout.BytesPerSample, componentStride, layout.BytesPerSample, layout.ValueShift,
                chromaRx0, chromaRy0, chromaRx1, chromaRy1,
                chromaPx0, chromaPy0, chromaPx1, chromaPy1,
                alpha, alphaWidth, alphaHeight, radiusMap, radiusMapWidth, radiusMapHeight, baseRadius,
                chroma: true, chromaShiftX: layout.ChromaShiftX, chromaShiftY: layout.ChromaShiftY,
                lumaRx0: rx0, lumaRy0: ry0, lumaPx0: px0, lumaPy0: py0);
        }
    }

    private void ApplyNativePlane(
        byte* data,
        int stride,
        int planeWidth,
        int planeHeight,
        int bytesPerSample,
        int sampleStride,
        int componentOffset,
        int valueShift,
        int rx0,
        int ry0,
        int rx1,
        int ry1,
        int px0,
        int py0,
        int px1,
        int py1,
        byte[] alpha,
        int alphaWidth,
        int alphaHeight,
        byte[] radiusMap,
        int radiusMapWidth,
        int radiusMapHeight,
        int baseRadius,
        bool chroma,
        int chromaShiftX = 0,
        int chromaShiftY = 0,
        int lumaRx0 = 0,
        int lumaRy0 = 0,
        int lumaPx0 = 0,
        int lumaPy0 = 0)
    {
        rx0 = Math.Clamp(rx0, 0, planeWidth);
        ry0 = Math.Clamp(ry0, 0, planeHeight);
        rx1 = Math.Clamp(rx1, 0, planeWidth);
        ry1 = Math.Clamp(ry1, 0, planeHeight);
        px0 = Math.Clamp(px0, 0, planeWidth);
        py0 = Math.Clamp(py0, 0, planeHeight);
        px1 = Math.Clamp(px1, 0, planeWidth);
        py1 = Math.Clamp(py1, 0, planeHeight);
        int paddedWidth = px1 - px0;
        int paddedHeight = py1 - py0;
        if (rx1 <= rx0 || ry1 <= ry0 || paddedWidth <= 0 || paddedHeight <= 0)
            return;

        long[] integral = EnsureNativeIntegral(paddedWidth, paddedHeight);
        int integralStride = paddedWidth + 1;
        for (int y = 1; y <= paddedHeight; y++)
        {
            byte* sourceRow = data + (py0 + y - 1) * stride + px0 * sampleStride + componentOffset;
            int rowIndex = y * integralStride;
            int previousRowIndex = (y - 1) * integralStride;
            long rowSum = 0;
            for (int x = 1; x <= paddedWidth; x++)
            {
                byte* sample = sourceRow + (x - 1) * sampleStride;
                rowSum += ReadNativeSample(sample, bytesPerSample, valueShift);
                integral[rowIndex + x] = integral[previousRowIndex + x] + rowSum;
            }
        }

        void ProcessNativeRow(int y)
        {
            byte* destinationRow = data + y * stride + rx0 * sampleStride + componentOffset;
            for (int x = rx0; x < rx1; x++)
            {
                byte smooth;
                int localRadiusX;
                int localRadiusY;
                if (chroma)
                {
                    smooth = GetChromaAlpha(
                        alpha,
                        alphaWidth,
                        alphaHeight,
                        lumaRx0,
                        lumaRy0,
                        x,
                        y,
                        chromaShiftX,
                        chromaShiftY);
                    int lumaRadius = GetChromaRadius(
                        radiusMap,
                        radiusMapWidth,
                        radiusMapHeight,
                        lumaPx0,
                        lumaPy0,
                        x,
                        y,
                        baseRadius,
                        chromaShiftX,
                        chromaShiftY);
                    localRadiusX = Math.Max(1, DivideRoundUp(lumaRadius, chromaShiftX));
                    localRadiusY = Math.Max(1, DivideRoundUp(lumaRadius, chromaShiftY));
                }
                else
                {
                    smooth = alpha[(y - ry0) * alphaWidth + x - rx0];
                    int radiusIndex = (y - py0) * radiusMapWidth + x - px0;
                    int mappedRadius = radiusMap[radiusIndex];
                    localRadiusX = mappedRadius > 0 ? mappedRadius : baseRadius;
                    localRadiusY = localRadiusX;
                }

                if (smooth == 0)
                {
                    destinationRow += sampleStride;
                    continue;
                }

                int x0 = Math.Max(px0, x - localRadiusX);
                int x1 = Math.Min(px1 - 1, x + localRadiusX);
                int y0 = Math.Max(py0, y - localRadiusY);
                int y1 = Math.Min(py1 - 1, y + localRadiusY);
                int ix0 = x0 - px0;
                int ix1 = x1 - px0;
                int iy0 = y0 - py0;
                int iy1 = y1 - py0;

                int indexA = (iy1 + 1) * integralStride + ix1 + 1;
                int indexB = iy0 * integralStride + ix1 + 1;
                int indexC = (iy1 + 1) * integralStride + ix0;
                int indexD = iy0 * integralStride + ix0;
                long sum = integral[indexA] - integral[indexB] - integral[indexC] + integral[indexD];
                int area = (ix1 - ix0 + 1) * (iy1 - iy0 + 1);
                int blurred = (int)(sum / area);
                int original = ReadNativeSample(destinationRow, bytesPerSample, valueShift);
                int blended = smooth == byte.MaxValue
                    ? blurred
                    : (blurred * smooth + original * (byte.MaxValue - smooth) + 127) / byte.MaxValue;
                WriteNativeSample(destinationRow, bytesPerSample, valueShift, blended);
                destinationRow += sampleStride;
            }
        }

        int roiArea = (rx1 - rx0) * (ry1 - ry0);
        int maxWorkers = Math.Max(1, Environment.ProcessorCount - 2);
        if (maxWorkers > 1 && roiArea >= 220_000)
        {
            System.Threading.Tasks.Parallel.For(
                ry0,
                ry1,
                new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = maxWorkers },
                ProcessNativeRow);
        }
        else
        {
            for (int y = ry0; y < ry1; y++)
                ProcessNativeRow(y);
        }
    }

    private static int ReadNativeSample(byte* sample, int bytesPerSample, int valueShift)
    {
        return bytesPerSample == 1
            ? sample[0]
            : (*(ushort*)sample) >> valueShift;
    }

    private static void WriteNativeSample(byte* sample, int bytesPerSample, int valueShift, int value)
    {
        if (bytesPerSample == 1)
            sample[0] = (byte)value;
        else
            *(ushort*)sample = (ushort)(value << valueShift);
    }

    private static byte GetChromaAlpha(
        byte[] alpha,
        int alphaWidth,
        int alphaHeight,
        int lumaRx0,
        int lumaRy0,
        int chromaX,
        int chromaY,
        int chromaShiftX,
        int chromaShiftY)
    {
        byte best = 0;
        int lumaX = chromaX << chromaShiftX;
        int lumaY = chromaY << chromaShiftY;
        int sampleWidth = 1 << chromaShiftX;
        int sampleHeight = 1 << chromaShiftY;
        for (int dy = 0; dy < sampleHeight; dy++)
        {
            int ay = lumaY + dy - lumaRy0;
            if (ay < 0 || ay >= alphaHeight)
                continue;
            for (int dx = 0; dx < sampleWidth; dx++)
            {
                int ax = lumaX + dx - lumaRx0;
                if (ax < 0 || ax >= alphaWidth)
                    continue;
                byte value = alpha[ay * alphaWidth + ax];
                if (value > best)
                    best = value;
            }
        }
        return best;
    }

    private static int GetChromaRadius(
        byte[] radiusMap,
        int radiusMapWidth,
        int radiusMapHeight,
        int lumaPx0,
        int lumaPy0,
        int chromaX,
        int chromaY,
        int baseRadius,
        int chromaShiftX,
        int chromaShiftY)
    {
        int best = 0;
        int lumaX = chromaX << chromaShiftX;
        int lumaY = chromaY << chromaShiftY;
        int sampleWidth = 1 << chromaShiftX;
        int sampleHeight = 1 << chromaShiftY;
        for (int dy = 0; dy < sampleHeight; dy++)
        {
            int ry = lumaY + dy - lumaPy0;
            if (ry < 0 || ry >= radiusMapHeight)
                continue;
            for (int dx = 0; dx < sampleWidth; dx++)
            {
                int rx = lumaX + dx - lumaPx0;
                if (rx < 0 || rx >= radiusMapWidth)
                    continue;
                int value = radiusMap[ry * radiusMapWidth + rx];
                if (value > best)
                    best = value;
            }
        }
        return best > 0 ? best : baseRadius;
    }

    private readonly record struct NativeYuvLayout(
        int BytesPerSample,
        int ValueShift,
        int ChromaShiftX,
        int ChromaShiftY,
        bool InterleavedChroma);

    private void EnsureIntegralBuffers(int width, int height)
    {
        int size = checked((width + 1) * (height + 1));
        if (_integralB == null || _integralB.Length < size)
        {
            _integralB = new uint[size];
            _integralG = new uint[size];
            _integralR = new uint[size];
            _integralA = new uint[size];
        }
        else
        {
            var integralB = _integralB!;
            var integralG = _integralG!;
            var integralR = _integralR!;
            var integralA = _integralA!;
            int rowStride = width + 1;
            Array.Clear(integralB, 0, rowStride);
            Array.Clear(integralG, 0, rowStride);
            Array.Clear(integralR, 0, rowStride);
            Array.Clear(integralA, 0, rowStride);

            for (int y = 1; y <= height; y++)
            {
                int idx = y * rowStride;
                integralB[idx] = 0;
                integralG[idx] = 0;
                integralR[idx] = 0;
                integralA[idx] = 0;
            }
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static uint GetIntegralSum(
        uint[] integral,
        int bottomRight,
        int topRight,
        int bottomLeft,
        int topLeft)
    {
        return unchecked(
            integral[bottomRight] -
            integral[topRight] -
            integral[bottomLeft] +
            integral[topLeft]);
    }

    private byte[] EnsureRadiusMap(int width, int height)
    {
        int size = width * height;
        if (_radiusMap == null || _radiusMap.Length < size)
        {
            _radiusMap = new byte[size];
        }
        return _radiusMap;
    }

    private readonly record struct FaceMaskShape(
        int X0,
        int Y0,
        int X1,
        int Y1,
        double CenterX,
        double CenterY,
        double RadiusX2,
        double RadiusY2,
        double Inner2);

    private static FaceMaskShape[] BuildFaceMaskShapes(IReadOnlyList<Rect> faces, int width, int height)
    {
        var shapes = new List<FaceMaskShape>(faces.Count);
        double inner = Math.Max(0.0, 1.0 - FaceBlurGeometry.SoftEdgeRatio);
        double inner2 = inner * inner;

        foreach (var face in faces)
        {
            var r = FaceBlurGeometry.GetPaddedRect(face, width, height);
            int x0 = Math.Clamp((int)Math.Floor(r.X), 0, Math.Max(0, width - 1));
            int y0 = Math.Clamp((int)Math.Floor(r.Y), 0, Math.Max(0, height - 1));
            int x1 = Math.Clamp((int)Math.Ceiling(r.Right), 0, width);
            int y1 = Math.Clamp((int)Math.Ceiling(r.Bottom), 0, height);
            if (x1 <= x0 || y1 <= y0)
                continue;

            double centerX = (x0 + x1 - 1) / 2.0;
            double centerY = (y0 + y1 - 1) / 2.0;
            double radiusX = Math.Max(1.0, (x1 - x0) / 2.0);
            double radiusY = Math.Max(1.0, (y1 - y0) / 2.0);
            shapes.Add(new FaceMaskShape(
                x0,
                y0,
                x1,
                y1,
                centerX,
                centerY,
                radiusX * radiusX,
                radiusY * radiusY,
                inner2));
        }

        return shapes.ToArray();
    }

    private static byte GetFaceAlpha(IReadOnlyList<FaceMaskShape> shapes, int x, int y)
    {
        byte best = 0;
        for (int i = 0; i < shapes.Count; i++)
        {
            var shape = shapes[i];
            if (x < shape.X0 || x >= shape.X1 || y < shape.Y0 || y >= shape.Y1)
                continue;

            double dx = x - shape.CenterX;
            double dy = y - shape.CenterY;
            double d2 = (dx * dx) / shape.RadiusX2 + (dy * dy) / shape.RadiusY2;
            if (d2 > 1.0)
                continue;

            byte alpha;
            if (d2 <= shape.Inner2 || FaceBlurGeometry.SoftEdgeRatio <= 0.0)
            {
                alpha = 255;
            }
            else
            {
                double t = (d2 - shape.Inner2) / (1.0 - shape.Inner2);
                t = Math.Clamp(t, 0.0, 1.0);
                t = t * t * (3.0 - 2.0 * t);
                alpha = (byte)Math.Round((1.0 - t) * 255.0);
            }

            if (alpha > best)
            {
                best = alpha;
                if (best == 255)
                    return best;
            }
        }

        return best;
    }

    private static byte GetSingleFaceAlpha(FaceMaskShape shape, int x, int y)
    {
        double dx = x - shape.CenterX;
        double dy = y - shape.CenterY;
        double d2 = (dx * dx) / shape.RadiusX2 + (dy * dy) / shape.RadiusY2;
        if (d2 > 1.0)
            return 0;

        if (d2 <= shape.Inner2 || FaceBlurGeometry.SoftEdgeRatio <= 0.0)
            return 255;

        double t = (d2 - shape.Inner2) / (1.0 - shape.Inner2);
        t = Math.Clamp(t, 0.0, 1.0);
        t = t * t * (3.0 - 2.0 * t);
        return (byte)Math.Round((1.0 - t) * 255.0);
    }

    private static (int x0, int y0, int x1, int y1) GetFaceBounds(IReadOnlyList<Rect> faces, int width, int height)
    {
        if (faces == null || faces.Count == 0 || width <= 0 || height <= 0)
            return (0, 0, 0, 0);

        int minX = width;
        int minY = height;
        int maxX = -1;
        int maxY = -1;

        foreach (var face in faces)
        {
            var padded = FaceBlurGeometry.GetPaddedRect(face, width, height);
            int x0 = Math.Clamp((int)Math.Floor(padded.X), 0, width);
            int y0 = Math.Clamp((int)Math.Floor(padded.Y), 0, height);
            int x1 = Math.Clamp((int)Math.Ceiling(padded.Right), 0, width);
            int y1 = Math.Clamp((int)Math.Ceiling(padded.Bottom), 0, height);
            if (x1 <= x0 || y1 <= y0)
                continue;

            if (x0 < minX) minX = x0;
            if (y0 < minY) minY = y0;
            if (x1 > maxX) maxX = x1;
            if (y1 > maxY) maxY = y1;
        }

        if (maxX <= minX || maxY <= minY)
            return (0, 0, 0, 0);
        return (minX, minY, maxX, maxY);
    }

    private static (int x0, int y0, int x1, int y1) GetMaskBounds(
        byte* mask,
        int stride,
        int w,
        int h)
    {
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
        {
            byte* row = mask + y * stride;
            for (int x = 0; x < w; x++)
            {
                if (row[x * 4 + 3] == 0)
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
