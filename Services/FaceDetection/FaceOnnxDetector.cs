// FILE: Services/FaceDetection/FaceOnnxDetector.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using FaceONNX;
using FaceShield.Models.Analysis;
using FaceShield.Services.Analysis;
using Microsoft.ML.OnnxRuntime;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace FaceShield.Services.FaceDetection
{
    public sealed class FaceOnnxDetector : IFaceDetector
    {
        private readonly FaceDetector _detector;
        private readonly bool _enablePreprocessOptimizations = true;
        private float[][,]? _reusableBuffer;
        private int _bufferWidth;
        private int _bufferHeight;
        private const int ParallelMinPixels = 300_000;
        private static readonly object _statusLock = new();
        private static string _lastExecutionProviderLabel = "CPU";
        private static string? _lastExecutionProviderError;
        private static readonly object _perfLock = new();
        private static long _perfCalls;
        private static long _perfPreMs;
        private static long _perfInferMs;
        private static long _perfTotalMs;

        public FaceOnnxDetector()
        {
            _detector = new FaceDetector(); // 확실함
        }

        public static (float Detection, float Confidence, float Nms) GetDefaultThresholds()
        {
            using var temp = new FaceDetector();
            return (temp.DetectionThreshold, temp.ConfidenceThreshold, temp.NmsThreshold);
        }

        public FaceOnnxDetector(FaceOnnxDetectorOptions? options)
        {
            if (options == null || (!options.UseOrtOptimization && !options.UseGpu))
            {
                _detector = new FaceDetector();
                UpdateExecutionProviderLabel("CPU");
                UpdateExecutionProviderError(null);
                return;
            }

            // 기본 임계값을 유지하기 위해 1회 생성 후 값 재사용
            var defaults = GetDefaultThresholds();
            float detection = options.DetectionThreshold ?? defaults.Detection;
            float confidence = options.ConfidenceThreshold ?? defaults.Confidence;
            float nms = options.NmsThreshold ?? defaults.Nms;

            var so = new SessionOptions
            {
                GraphOptimizationLevel = options.UseOrtOptimization
                    ? GraphOptimizationLevel.ORT_ENABLE_ALL
                    : GraphOptimizationLevel.ORT_DISABLE_ALL
            };

            string? gpuProvider = null;
            if (options.UseGpu)
                gpuProvider = TryAppendGpuExecutionProvider(so);

            if (options.UseGpu && gpuProvider == null && GetLastExecutionProviderError() == null)
                UpdateExecutionProviderError("GPU 실행 공급자 로드 실패(패키지/의존성 확인)");

            if (options.IntraOpNumThreads.HasValue)
                so.IntraOpNumThreads = options.IntraOpNumThreads.Value;
            if (options.InterOpNumThreads.HasValue)
                so.InterOpNumThreads = options.InterOpNumThreads.Value;

            try
            {
                _detector = new FaceDetector(so, detection, confidence, nms);
                UpdateExecutionProviderLabel(gpuProvider != null
                    ? $"GPU:{gpuProvider}"
                    : options.UseGpu ? "CPU(가속 실패)" : "CPU");
                if (gpuProvider != null)
                    UpdateExecutionProviderError(null);
            }
            catch (Exception ex)
            {
                _detector = new FaceDetector(detection, confidence, nms);
                UpdateExecutionProviderLabel("CPU(가속 실패)");
                UpdateExecutionProviderError(ex.Message);
            }
        }

        public IReadOnlyList<FaceDetectionResult> DetectFaces(WriteableBitmap frame)
        {
            if (frame is null)
                return Array.Empty<FaceDetectionResult>();

            return DetectFacesWithTiming(() => ConvertToImageArray(frame));
        }

        internal IReadOnlyList<FaceDetectionResult> DetectFacesDownscaled(WriteableBitmap frame, double ratio)
        {
            return DetectFacesDownscaled(frame, ratio, DownscaleQuality.BalancedBilinear);
        }

        internal IReadOnlyList<FaceDetectionResult> DetectFacesDownscaled(
            WriteableBitmap frame,
            double ratio,
            DownscaleQuality quality)
        {
            if (frame is null)
                return Array.Empty<FaceDetectionResult>();

            if (ratio >= 1.0 || ratio <= 0)
                return DetectFaces(frame);

            bool useBilinear = quality == DownscaleQuality.BalancedBilinear;
            return DetectFacesWithTiming(() => ConvertToImageArrayDownscaled(frame, ratio, useBilinear));
        }

        internal IReadOnlyList<FaceDetectionResult> DetectFacesBgra(
            IntPtr data,
            int stride,
            int width,
            int height,
            double ratio,
            DownscaleQuality quality)
        {
            if (data == IntPtr.Zero || width <= 0 || height <= 0)
                return Array.Empty<FaceDetectionResult>();

            if (ratio >= 1.0 || ratio <= 0)
            {
                return DetectFacesWithTiming(() => ConvertToImageArrayFromBgra(data, stride, width, height));
            }

            bool useBilinear = quality == DownscaleQuality.BalancedBilinear;
            return DetectFacesWithTiming(() => ConvertToImageArrayFromBgraDownscaled(data, stride, width, height, ratio, useBilinear));
        }

        public IReadOnlyList<FaceDetectionResult> DetectFaces(Image<Rgb24> img)
        {
            return DetectFacesWithTiming(() => ConvertToImageArray(img));
        }

        private IReadOnlyList<FaceDetectionResult> DetectFacesWithTiming(Func<float[][,]> buildInput)
        {
            var pre = Stopwatch.StartNew();
            var input = buildInput();
            pre.Stop();
            return DetectFaces(input, pre.ElapsedMilliseconds);
        }

        private IReadOnlyList<FaceDetectionResult> DetectFaces(float[][,] input, long preMs)
        {
            // FaceONNX의 Forward는 확실히 존재함
            var infer = Stopwatch.StartNew();
            var rects = _detector.Forward(input);
            infer.Stop();

            RecordPerf(preMs, infer.ElapsedMilliseconds);

            var results = new List<FaceDetectionResult>();

            foreach (var r in rects)
            {
                var b = r.Rectangle; // System.Drawing.Rectangle

                var rect = new Rect(b.X, b.Y, b.Width, b.Height);

                float conf = r.Score;

                results.Add(new FaceDetectionResult
                {
                    Bounds = rect,
                    Confidence = conf
                });
            }

            return results;
        }

        private static void RecordPerf(long preMs, long inferMs)
        {
            lock (_perfLock)
            {
                _perfCalls++;
                _perfPreMs += preMs;
                _perfInferMs += inferMs;
                _perfTotalMs += preMs + inferMs;

                if (_perfCalls % 60 == 0)
                {
                    Debug.WriteLine(
                        $"[OnnxPerf] calls={_perfCalls}, preMs={_perfPreMs}, inferMs={_perfInferMs}, totalMs={_perfTotalMs}");
                }
            }
        }

        private float[][,] ConvertToImageArray(WriteableBitmap bmp)
        {
            int w = bmp.PixelSize.Width;
            int h = bmp.PixelSize.Height;

            if (_reusableBuffer == null || _bufferWidth != w || _bufferHeight != h)
            {
                _reusableBuffer = new float[3][,]
                {
                    new float[h, w],
                    new float[h, w],
                    new float[h, w]
                };
                _bufferWidth = w;
                _bufferHeight = h;
            }

            var data = _reusableBuffer;

            using var fb = bmp.Lock();
            unsafe
            {
                byte* src = (byte*)fb.Address;
                int stride = fb.RowBytes;
                const float inv = 1f / 255f;

                bool useParallel = _enablePreprocessOptimizations && (w * h) >= ParallelMinPixels;
                if (useParallel)
                {
                    Parallel.For(0, h, y =>
                    {
                        byte* srcRow = src + y * stride;
                        for (int x = 0; x < w; x++)
                        {
                            int idx = x * 4;
                            data[0][y, x] = srcRow[idx + 0] * inv; // B
                            data[1][y, x] = srcRow[idx + 1] * inv; // G
                            data[2][y, x] = srcRow[idx + 2] * inv; // R
                        }
                    });
                }
                else
                {
                    for (int y = 0; y < h; y++)
                    {
                        byte* srcRow = src + y * stride;
                        for (int x = 0; x < w; x++)
                        {
                            int idx = x * 4;
                            data[0][y, x] = srcRow[idx + 0] * inv; // B
                            data[1][y, x] = srcRow[idx + 1] * inv; // G
                            data[2][y, x] = srcRow[idx + 2] * inv; // R
                        }
                    }
                }
            }

            return data;
        }

        private float[][,] ConvertToImageArrayFromBgra(IntPtr dataPtr, int stride, int width, int height)
        {
            if (_reusableBuffer == null || _bufferWidth != width || _bufferHeight != height)
            {
                _reusableBuffer = new float[3][,]
                {
                    new float[height, width],
                    new float[height, width],
                    new float[height, width]
                };
                _bufferWidth = width;
                _bufferHeight = height;
            }

            var data = _reusableBuffer;

            unsafe
            {
                byte* src = (byte*)dataPtr;
                const float inv = 1f / 255f;

                bool useParallel = _enablePreprocessOptimizations && (width * height) >= ParallelMinPixels;
                if (useParallel)
                {
                    Parallel.For(0, height, y =>
                    {
                        byte* srcRow = src + y * stride;
                        for (int x = 0; x < width; x++)
                        {
                            int idx = x * 4;
                            data[0][y, x] = srcRow[idx + 0] * inv; // B
                            data[1][y, x] = srcRow[idx + 1] * inv; // G
                            data[2][y, x] = srcRow[idx + 2] * inv; // R
                        }
                    });
                }
                else
                {
                    for (int y = 0; y < height; y++)
                    {
                        byte* srcRow = src + y * stride;
                        for (int x = 0; x < width; x++)
                        {
                            int idx = x * 4;
                            data[0][y, x] = srcRow[idx + 0] * inv; // B
                            data[1][y, x] = srcRow[idx + 1] * inv; // G
                            data[2][y, x] = srcRow[idx + 2] * inv; // R
                        }
                    }
                }
            }

            return data;
        }

        private float[][,] ConvertToImageArrayDownscaled(WriteableBitmap bmp, double ratio, bool useBilinear)
        {
            int srcW = bmp.PixelSize.Width;
            int srcH = bmp.PixelSize.Height;

            int w = Math.Max(1, (int)Math.Round(srcW * ratio));
            int h = Math.Max(1, (int)Math.Round(srcH * ratio));

            if (_reusableBuffer == null || _bufferWidth != w || _bufferHeight != h)
            {
                _reusableBuffer = new float[3][,]
                {
                    new float[h, w],
                    new float[h, w],
                    new float[h, w]
                };
                _bufferWidth = w;
                _bufferHeight = h;
            }

            var data = _reusableBuffer;

            using var fb = bmp.Lock();
            unsafe
            {
                byte* src = (byte*)fb.Address;
                int stride = fb.RowBytes;
                const float inv = 1f / 255f;
                double invRatio = 1.0 / ratio;

                bool useParallel = _enablePreprocessOptimizations && (w * h) >= ParallelMinPixels;
                if (useParallel)
                {
                    Parallel.For(0, h, y =>
                    {
                        if (!useBilinear)
                        {
                            int srcYInt = (int)(y * invRatio);
                            if (srcYInt < 0) srcYInt = 0;
                            if (srcYInt >= srcH) srcYInt = srcH - 1;

                            byte* srcRow = src + srcYInt * stride;
                            for (int x = 0; x < w; x++)
                            {
                                int srcX = (int)(x * invRatio);
                                if (srcX < 0) srcX = 0;
                                if (srcX >= srcW) srcX = srcW - 1;

                                int idx = srcX * 4;
                                data[0][y, x] = srcRow[idx + 0] * inv; // B
                                data[1][y, x] = srcRow[idx + 1] * inv; // G
                                data[2][y, x] = srcRow[idx + 2] * inv; // R
                            }
                            return;
                        }

                        double srcYPos = y * invRatio;
                        int y0 = (int)Math.Floor(srcYPos);
                        if (y0 < 0) y0 = 0;
                        if (y0 >= srcH) y0 = srcH - 1;
                        int y1 = Math.Min(y0 + 1, srcH - 1);
                        double fy = srcYPos - y0;
                        double wy0 = 1.0 - fy;
                        double wy1 = fy;

                        byte* row0 = src + y0 * stride;
                        byte* row1 = src + y1 * stride;

                        for (int x = 0; x < w; x++)
                        {
                            double srcX = x * invRatio;
                            int x0 = (int)Math.Floor(srcX);
                            if (x0 < 0) x0 = 0;
                            if (x0 >= srcW) x0 = srcW - 1;
                            int x1 = Math.Min(x0 + 1, srcW - 1);
                            double fx = srcX - x0;
                            double wx0 = 1.0 - fx;
                            double wx1 = fx;

                            int idx00 = x0 * 4;
                            int idx10 = x1 * 4;

                            double b =
                                row0[idx00 + 0] * wx0 * wy0 +
                                row0[idx10 + 0] * wx1 * wy0 +
                                row1[idx00 + 0] * wx0 * wy1 +
                                row1[idx10 + 0] * wx1 * wy1;
                            double g =
                                row0[idx00 + 1] * wx0 * wy0 +
                                row0[idx10 + 1] * wx1 * wy0 +
                                row1[idx00 + 1] * wx0 * wy1 +
                                row1[idx10 + 1] * wx1 * wy1;
                            double r =
                                row0[idx00 + 2] * wx0 * wy0 +
                                row0[idx10 + 2] * wx1 * wy0 +
                                row1[idx00 + 2] * wx0 * wy1 +
                                row1[idx10 + 2] * wx1 * wy1;

                            data[0][y, x] = (float)(b * inv); // B
                            data[1][y, x] = (float)(g * inv); // G
                            data[2][y, x] = (float)(r * inv); // R
                        }
                    });
                }
                else
                {
                    for (int y = 0; y < h; y++)
                    {
                        if (!useBilinear)
                        {
                            int srcYInt = (int)(y * invRatio);
                            if (srcYInt < 0) srcYInt = 0;
                            if (srcYInt >= srcH) srcYInt = srcH - 1;

                            byte* srcRow = src + srcYInt * stride;
                            for (int x = 0; x < w; x++)
                            {
                                int srcX = (int)(x * invRatio);
                                if (srcX < 0) srcX = 0;
                                if (srcX >= srcW) srcX = srcW - 1;

                                int idx = srcX * 4;
                                data[0][y, x] = srcRow[idx + 0] * inv; // B
                                data[1][y, x] = srcRow[idx + 1] * inv; // G
                                data[2][y, x] = srcRow[idx + 2] * inv; // R
                            }
                            continue;
                        }

                        double srcYPos = y * invRatio;
                        int y0 = (int)Math.Floor(srcYPos);
                        if (y0 < 0) y0 = 0;
                        if (y0 >= srcH) y0 = srcH - 1;
                        int y1 = Math.Min(y0 + 1, srcH - 1);
                        double fy = srcYPos - y0;
                        double wy0 = 1.0 - fy;
                        double wy1 = fy;

                        byte* row0 = src + y0 * stride;
                        byte* row1 = src + y1 * stride;

                        for (int x = 0; x < w; x++)
                        {
                            double srcX = x * invRatio;
                            int x0 = (int)Math.Floor(srcX);
                            if (x0 < 0) x0 = 0;
                            if (x0 >= srcW) x0 = srcW - 1;
                            int x1 = Math.Min(x0 + 1, srcW - 1);
                            double fx = srcX - x0;
                            double wx0 = 1.0 - fx;
                            double wx1 = fx;

                            int idx00 = x0 * 4;
                            int idx10 = x1 * 4;

                            double b =
                                row0[idx00 + 0] * wx0 * wy0 +
                                row0[idx10 + 0] * wx1 * wy0 +
                                row1[idx00 + 0] * wx0 * wy1 +
                                row1[idx10 + 0] * wx1 * wy1;
                            double g =
                                row0[idx00 + 1] * wx0 * wy0 +
                                row0[idx10 + 1] * wx1 * wy0 +
                                row1[idx00 + 1] * wx0 * wy1 +
                                row1[idx10 + 1] * wx1 * wy1;
                            double r =
                                row0[idx00 + 2] * wx0 * wy0 +
                                row0[idx10 + 2] * wx1 * wy0 +
                                row1[idx00 + 2] * wx0 * wy1 +
                                row1[idx10 + 2] * wx1 * wy1;

                            data[0][y, x] = (float)(b * inv); // B
                            data[1][y, x] = (float)(g * inv); // G
                            data[2][y, x] = (float)(r * inv); // R
                        }
                    }
                }
            }

            return data;
        }

        private float[][,] ConvertToImageArrayFromBgraDownscaled(
            IntPtr dataPtr,
            int stride,
            int srcW,
            int srcH,
            double ratio,
            bool useBilinear)
        {
            int w = Math.Max(1, (int)Math.Round(srcW * ratio));
            int h = Math.Max(1, (int)Math.Round(srcH * ratio));

            if (_reusableBuffer == null || _bufferWidth != w || _bufferHeight != h)
            {
                _reusableBuffer = new float[3][,]
                {
                    new float[h, w],
                    new float[h, w],
                    new float[h, w]
                };
                _bufferWidth = w;
                _bufferHeight = h;
            }

            var data = _reusableBuffer;

            unsafe
            {
                byte* src = (byte*)dataPtr;
                const float inv = 1f / 255f;
                double invRatio = 1.0 / ratio;

                bool useParallel = _enablePreprocessOptimizations && (w * h) >= ParallelMinPixels;
                if (useParallel)
                {
                    Parallel.For(0, h, y =>
                    {
                        if (!useBilinear)
                        {
                            int srcYInt = (int)(y * invRatio);
                            if (srcYInt < 0) srcYInt = 0;
                            if (srcYInt >= srcH) srcYInt = srcH - 1;

                            byte* srcRow = src + srcYInt * stride;
                            for (int x = 0; x < w; x++)
                            {
                                int srcX = (int)(x * invRatio);
                                if (srcX < 0) srcX = 0;
                                if (srcX >= srcW) srcX = srcW - 1;

                                int idx = srcX * 4;
                                data[0][y, x] = srcRow[idx + 0] * inv; // B
                                data[1][y, x] = srcRow[idx + 1] * inv; // G
                                data[2][y, x] = srcRow[idx + 2] * inv; // R
                            }
                            return;
                        }

                        double srcYPos = y * invRatio;
                        int y0 = (int)Math.Floor(srcYPos);
                        if (y0 < 0) y0 = 0;
                        if (y0 >= srcH) y0 = srcH - 1;
                        int y1 = Math.Min(y0 + 1, srcH - 1);
                        double fy = srcYPos - y0;
                        double wy0 = 1.0 - fy;
                        double wy1 = fy;

                        byte* row0 = src + y0 * stride;
                        byte* row1 = src + y1 * stride;

                        for (int x = 0; x < w; x++)
                        {
                            double srcXPos = x * invRatio;
                            int x0 = (int)Math.Floor(srcXPos);
                            if (x0 < 0) x0 = 0;
                            if (x0 >= srcW) x0 = srcW - 1;
                            int x1 = Math.Min(x0 + 1, srcW - 1);
                            double fx = srcXPos - x0;
                            double wx0 = 1.0 - fx;
                            double wx1 = fx;

                            int idx00 = x0 * 4;
                            int idx10 = x1 * 4;

                            double b =
                                row0[idx00 + 0] * wx0 * wy0 +
                                row0[idx10 + 0] * wx1 * wy0 +
                                row1[idx00 + 0] * wx0 * wy1 +
                                row1[idx10 + 0] * wx1 * wy1;
                            double g =
                                row0[idx00 + 1] * wx0 * wy0 +
                                row0[idx10 + 1] * wx1 * wy0 +
                                row1[idx00 + 1] * wx0 * wy1 +
                                row1[idx10 + 1] * wx1 * wy1;
                            double r =
                                row0[idx00 + 2] * wx0 * wy0 +
                                row0[idx10 + 2] * wx1 * wy0 +
                                row1[idx00 + 2] * wx0 * wy1 +
                                row1[idx10 + 2] * wx1 * wy1;

                            data[0][y, x] = (float)(b * inv); // B
                            data[1][y, x] = (float)(g * inv); // G
                            data[2][y, x] = (float)(r * inv); // R
                        }
                    });
                }
                else
                {
                    for (int y = 0; y < h; y++)
                    {
                        if (!useBilinear)
                        {
                            int srcYInt = (int)(y * invRatio);
                            if (srcYInt < 0) srcYInt = 0;
                            if (srcYInt >= srcH) srcYInt = srcH - 1;

                            byte* srcRow = src + srcYInt * stride;
                            for (int x = 0; x < w; x++)
                            {
                                int srcX = (int)(x * invRatio);
                                if (srcX < 0) srcX = 0;
                                if (srcX >= srcW) srcX = srcW - 1;

                                int idx = srcX * 4;
                                data[0][y, x] = srcRow[idx + 0] * inv; // B
                                data[1][y, x] = srcRow[idx + 1] * inv; // G
                                data[2][y, x] = srcRow[idx + 2] * inv; // R
                            }
                            continue;
                        }

                        double srcYPos = y * invRatio;
                        int y0 = (int)Math.Floor(srcYPos);
                        if (y0 < 0) y0 = 0;
                        if (y0 >= srcH) y0 = srcH - 1;
                        int y1 = Math.Min(y0 + 1, srcH - 1);
                        double fy = srcYPos - y0;
                        double wy0 = 1.0 - fy;
                        double wy1 = fy;

                        byte* row0 = src + y0 * stride;
                        byte* row1 = src + y1 * stride;

                        for (int x = 0; x < w; x++)
                        {
                            double srcXPos = x * invRatio;
                            int x0 = (int)Math.Floor(srcXPos);
                            if (x0 < 0) x0 = 0;
                            if (x0 >= srcW) x0 = srcW - 1;
                            int x1 = Math.Min(x0 + 1, srcW - 1);
                            double fx = srcXPos - x0;
                            double wx0 = 1.0 - fx;
                            double wx1 = fx;

                            int idx00 = x0 * 4;
                            int idx10 = x1 * 4;

                            double b =
                                row0[idx00 + 0] * wx0 * wy0 +
                                row0[idx10 + 0] * wx1 * wy0 +
                                row1[idx00 + 0] * wx0 * wy1 +
                                row1[idx10 + 0] * wx1 * wy1;
                            double g =
                                row0[idx00 + 1] * wx0 * wy0 +
                                row0[idx10 + 1] * wx1 * wy0 +
                                row1[idx00 + 1] * wx0 * wy1 +
                                row1[idx10 + 1] * wx1 * wy1;
                            double r =
                                row0[idx00 + 2] * wx0 * wy0 +
                                row0[idx10 + 2] * wx1 * wy0 +
                                row1[idx00 + 2] * wx0 * wy1 +
                                row1[idx10 + 2] * wx1 * wy1;

                            data[0][y, x] = (float)(b * inv); // B
                            data[1][y, x] = (float)(g * inv); // G
                            data[2][y, x] = (float)(r * inv); // R
                        }
                    }
                }
            }

            return data;
        }

        internal static Image<Rgb24> ConvertToImageSharp(WriteableBitmap bmp)
        {
            var w = bmp.PixelSize.Width;
            var h = bmp.PixelSize.Height;
            var img = new Image<Rgb24>(w, h);

            using var fb = bmp.Lock();

            unsafe
            {
                byte* src = (byte*)fb.Address;
                int stride = fb.RowBytes;

                img.ProcessPixelRows(rows =>
                {
                    for (int y = 0; y < h; y++)
                    {
                        var row = rows.GetRowSpan(y);
                        byte* srcRow = src + y * stride;

                        for (int x = 0; x < w; x++)
                        {
                            byte b = srcRow[x * 4 + 0];
                            byte g = srcRow[x * 4 + 1];
                            byte r = srcRow[x * 4 + 2];
                            row[x] = new Rgb24(r, g, b);
                        }
                    }
                });
            }

            return img;
        }

        private float[][,] ConvertToImageArray(Image<Rgb24> img)
        {
            int w = img.Width;
            int h = img.Height;

            if (_reusableBuffer == null || _bufferWidth != w || _bufferHeight != h)
            {
                _reusableBuffer = new float[3][,]
                {
                    new float[h, w],
                    new float[h, w],
                    new float[h, w]
                };
                _bufferWidth = w;
                _bufferHeight = h;
            }

            var data = _reusableBuffer;

            img.ProcessPixelRows(rows =>
            {
                for (int y = 0; y < h; y++)
                {
                    var row = rows.GetRowSpan(y);

                    for (int x = 0; x < w; x++)
                    {
                        var p = row[x];
                        // FaceONNX expects BGR channel order.
                        data[0][y, x] = p.B / 255f;
                        data[1][y, x] = p.G / 255f;
                        data[2][y, x] = p.R / 255f;
                    }
                }
            });

            return data;
        }

        public void Dispose()
        {
            _detector?.Dispose();
        }

        public static string GetLastExecutionProviderLabel()
        {
            lock (_statusLock)
            {
                return _lastExecutionProviderLabel;
            }
        }

        public static string? GetLastExecutionProviderError()
        {
            lock (_statusLock)
            {
                return _lastExecutionProviderError;
            }
        }

        private static void UpdateExecutionProviderLabel(string label)
        {
            lock (_statusLock)
            {
                _lastExecutionProviderLabel = label;
            }
        }

        private static void UpdateExecutionProviderError(string? error)
        {
            lock (_statusLock)
            {
                _lastExecutionProviderError = error;
            }
        }

        private static string? TryAppendGpuExecutionProvider(SessionOptions options)
        {
            // Use OS-appropriate providers when available; fall back silently.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                if (TryAppendExecutionProvider(options, "AppendExecutionProvider_CoreML", "Microsoft.ML.OnnxRuntime.CoreML"))
                    return "CoreML";
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (TryAppendExecutionProvider(options, "AppendExecutionProvider_DML", "Microsoft.ML.OnnxRuntime.DirectML"))
                {
                    UpdateExecutionProviderLabel("GPU:DirectML");
                    UpdateExecutionProviderError(null);
                    return "DirectML";
                }

                UpdateExecutionProviderLabel("CPU(DirectML 로드 실패)");
                if (GetLastExecutionProviderError() == null)
                    UpdateExecutionProviderError(BuildDirectMlDiagnostics());
                return null;
            }

            if (TryAppendExecutionProvider(options, "AppendExecutionProvider_DML", "Microsoft.ML.OnnxRuntime.DirectML"))
            {
                UpdateExecutionProviderLabel("GPU:DirectML");
                UpdateExecutionProviderError(null);
                return "DirectML";
            }

            return null;
        }

        public static void EnsureRuntimeAvailable()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return;

            string baseDir = AppContext.BaseDirectory;

            EnsureNativeLibrary(
                "ONNX Runtime(libonnxruntime.dylib)",
                Path.Combine(baseDir, "libonnxruntime.dylib"),
                Path.Combine(baseDir, "libonnxruntime.1.23.2.dylib"),
                "libonnxruntime.dylib");

            EnsureNativeLibrary(
                "OpenMP(libomp.dylib)",
                Path.Combine(baseDir, "libomp.dylib"),
                "/opt/homebrew/opt/libomp/lib/libomp.dylib",
                "/usr/local/opt/libomp/lib/libomp.dylib",
                "libomp.dylib");
        }

        private static void EnsureNativeLibrary(string label, params string[] candidates)
        {
            foreach (var path in candidates)
            {
                if (string.IsNullOrWhiteSpace(path))
                    continue;

                try
                {
                    if (NativeLibrary.TryLoad(path, out var handle))
                    {
                        NativeLibrary.Free(handle);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"{label} 로드 실패: {ex.Message}", ex);
                }
            }

            throw new DllNotFoundException(
                $"{label}을(를) 찾을 수 없습니다. macOS에서는 Homebrew로 'brew install libomp' 실행 후 다시 시도하고, 앱 폴더(.app/Contents/MacOS)에 onnxruntime 관련 dylib가 포함되어 있는지 확인하세요.");
        }

        private static bool TryAppendExecutionProvider(SessionOptions options, string methodName, string assemblyName)
        {
            TryLoadAssembly(assemblyName);
            TryLoadAssemblyFromBaseDir(assemblyName);

            if (TryInvokeSessionOptionsMethod(options, methodName))
                return true;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t != null).Cast<Type>().ToArray();
                }
                catch
                {
                    continue;
                }

                foreach (var type in types)
                {
                    var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .Where(m => m.Name == methodName)
                        .ToArray();

                    if (methods.Length == 0)
                        continue;

                    foreach (var method in methods)
                    {
                        var parameters = method.GetParameters();
                        try
                        {
                            if (parameters.Length == 1 && parameters[0].ParameterType == typeof(SessionOptions))
                            {
                                method.Invoke(null, new object?[] { options });
                                return true;
                            }

                            if (parameters.Length == 2 && parameters[0].ParameterType == typeof(SessionOptions))
                            {
                                var pType = parameters[1].ParameterType;
                                object? arg = pType == typeof(uint)
                                    ? 0u
                                    : pType == typeof(int)
                                        ? 0
                                        : null;

                                if (arg == null)
                                    continue;

                                method.Invoke(null, new object?[] { options, arg });
                                return true;
                            }
                        }
                        catch (Exception ex)
                        {
                            UpdateExecutionProviderError(ex.InnerException?.Message ?? ex.Message);
                            return false;
                        }
                    }
                }
            }

            return false;
        }

        private static void TryLoadAssembly(string assemblyName)
        {
            if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == assemblyName))
                return;

            try
            {
                Assembly.Load(assemblyName);
            }
            catch
            {
                // Optional dependency not available.
            }
        }

        private static void TryLoadAssemblyFromBaseDir(string assemblyName)
        {
            string path = Path.Combine(AppContext.BaseDirectory, $"{assemblyName}.dll");
            if (!File.Exists(path))
                return;

            try
            {
                Assembly.LoadFrom(path);
            }
            catch
            {
                // Optional dependency not available.
            }
        }

        private static bool TryInvokeSessionOptionsMethod(SessionOptions options, string methodName)
        {
            var methods = typeof(SessionOptions)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == methodName)
                .ToArray();

            if (methods.Length == 0)
                return false;

            foreach (var method in methods)
            {
                var parameters = method.GetParameters();
                try
                {
                    if (parameters.Length == 0)
                    {
                        method.Invoke(options, null);
                        return true;
                    }

                    if (parameters.Length == 1)
                    {
                        var pType = parameters[0].ParameterType;
                        object? arg = pType == typeof(uint)
                            ? 0u
                            : pType == typeof(int)
                                ? 0
                                : null;

                        if (arg == null)
                            continue;

                        method.Invoke(options, new[] { arg });
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    UpdateExecutionProviderError(ex.InnerException?.Message ?? ex.Message);
                    return false;
                }
            }

            return false;
        }

        private static string BuildDirectMlDiagnostics()
        {
            string baseDir = AppContext.BaseDirectory;
            string onnxRuntime = System.IO.Path.Combine(baseDir, "onnxruntime.dll");
            string managed = System.IO.Path.Combine(baseDir, "Microsoft.ML.OnnxRuntime.dll");
            string sharedProvider = System.IO.Path.Combine(baseDir, "onnxruntime_providers_shared.dll");

            bool hasManaged = System.IO.File.Exists(managed);
            bool hasOnnx = System.IO.File.Exists(onnxRuntime);
            bool hasShared = System.IO.File.Exists(sharedProvider);

            if (!hasManaged && !hasOnnx && !hasShared)
                return "DirectML 파일 누락(Microsoft.ML.OnnxRuntime.dll/onnxruntime.dll/onnxruntime_providers_shared.dll)";

            if (!hasOnnx)
                return "onnxruntime.dll 누락";
            if (!hasShared)
                return "onnxruntime_providers_shared.dll 누락";
            if (!hasManaged)
                return "Microsoft.ML.OnnxRuntime.dll 누락";

            return "DirectML 초기화 실패(드라이버/권한/런타임 확인)";
        }
    }
}
