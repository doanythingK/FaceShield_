// FILE: Services/FaceDetection/FaceOnnxDetector.cs
using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media.Imaging;
using FaceONNX;
using FaceShield.Models.Analysis;
using Microsoft.ML.OnnxRuntime;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace FaceShield.Services.FaceDetection
{
    public sealed class FaceOnnxDetector : IFaceDetector
    {
        private readonly FaceDetector _detector;

        public FaceOnnxDetector()
        {
            _detector = new FaceDetector(); // 확실함
        }

        public FaceOnnxDetector(FaceOnnxDetectorOptions? options)
        {
            if (options == null || !options.UseOrtOptimization)
            {
                _detector = new FaceDetector();
                return;
            }

            // 기본 임계값을 유지하기 위해 1회 생성 후 값 재사용
            using var temp = new FaceDetector();
            float detection = temp.DetectionThreshold;
            float confidence = temp.ConfidenceThreshold;
            float nms = temp.NmsThreshold;

            var so = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
            };

            if (options.IntraOpNumThreads.HasValue)
                so.IntraOpNumThreads = options.IntraOpNumThreads.Value;
            if (options.InterOpNumThreads.HasValue)
                so.InterOpNumThreads = options.InterOpNumThreads.Value;

            _detector = new FaceDetector(so, detection, confidence, nms);
        }

        public IReadOnlyList<FaceDetectionResult> DetectFaces(WriteableBitmap frame)
        {
            if (frame is null)
                return Array.Empty<FaceDetectionResult>();

            using var img = ConvertToImageSharp(frame);
            return DetectFaces(img);
        }

        public IReadOnlyList<FaceDetectionResult> DetectFaces(Image<Rgb24> img)
        {
            var input = ConvertToImageArray(img);

            // FaceONNX의 Forward는 확실히 존재함
            var rects = _detector.Forward(input);

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

        private static float[][,] ConvertToImageArray(Image<Rgb24> img)
        {
            int w = img.Width;
            int h = img.Height;

            float[][,] data = new float[3][,]
            {
                new float[h, w],
                new float[h, w],
                new float[h, w]
            };

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
    }
}
