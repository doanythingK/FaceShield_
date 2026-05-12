using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Media.Imaging;
using FaceShield.Models.Analysis;
using FaceShield.Services.Analysis;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace FaceShield.Services.FaceDetection
{
    public sealed class YuNetOnnxDetector : IBgraFaceDetector
    {
        private static readonly int[] Strides = { 8, 16, 32 };
        private readonly InferenceSession _session;
        private readonly string _inputName;
        private readonly int _inputWidth;
        private readonly int _inputHeight;
        private readonly YuNetOnnxDetectorOptions _options;

        public YuNetOnnxDetector(YuNetOnnxDetectorOptions? options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrWhiteSpace(_options.ModelPath))
                throw new InvalidOperationException("YuNet ONNX model path is required.");
            if (!File.Exists(_options.ModelPath))
                throw new FileNotFoundException("YuNet ONNX model was not found.", _options.ModelPath);

            var sessionOptions = new SessionOptions
            {
                GraphOptimizationLevel = _options.UseOrtOptimization
                    ? GraphOptimizationLevel.ORT_ENABLE_ALL
                    : GraphOptimizationLevel.ORT_DISABLE_ALL
            };

            if (_options.IntraOpNumThreads.HasValue)
                sessionOptions.IntraOpNumThreads = _options.IntraOpNumThreads.Value;
            if (_options.InterOpNumThreads.HasValue)
                sessionOptions.InterOpNumThreads = _options.InterOpNumThreads.Value;
            if (_options.UseParallelExecution == true)
                sessionOptions.ExecutionMode = ExecutionMode.ORT_PARALLEL;

            _session = new InferenceSession(_options.ModelPath, sessionOptions);
            _inputName = _session.InputMetadata.Keys.First();

            var dims = _session.InputMetadata[_inputName].Dimensions;
            _inputHeight = ReadInputDimension(dims, 2, 640);
            _inputWidth = ReadInputDimension(dims, 3, 640);
        }

        public IReadOnlyList<FaceDetectionResult> DetectFaces(WriteableBitmap frame)
        {
            if (frame == null)
                return Array.Empty<FaceDetectionResult>();

            using var fb = frame.Lock();
            return DetectFacesBgra(fb.Address, fb.RowBytes, frame.PixelSize.Width, frame.PixelSize.Height, 1.0, DownscaleQuality.BalancedBilinear);
        }

        public IReadOnlyList<FaceDetectionResult> DetectFacesDownscaled(WriteableBitmap frame, double ratio, DownscaleQuality quality)
        {
            if (frame == null)
                return Array.Empty<FaceDetectionResult>();

            using var fb = frame.Lock();
            return DetectFacesBgra(fb.Address, fb.RowBytes, frame.PixelSize.Width, frame.PixelSize.Height, ratio, quality);
        }

        public IReadOnlyList<FaceDetectionResult> DetectFacesBgra(
            IntPtr data,
            int stride,
            int width,
            int height,
            double ratio,
            DownscaleQuality quality)
        {
            if (data == IntPtr.Zero || width <= 0 || height <= 0)
                return Array.Empty<FaceDetectionResult>();

            int sourceWidth = width;
            int sourceHeight = height;
            if (ratio > 0 && ratio < 1.0)
            {
                sourceWidth = Math.Max(1, (int)Math.Round(width * ratio));
                sourceHeight = Math.Max(1, (int)Math.Round(height * ratio));
            }

            var pre = Stopwatch.StartNew();
            var candidates = new List<Candidate>();
            if (!_options.UseTiling || _options.IncludeFullFrameWhenTiling)
                RunRegion(data, stride, width, height, 0, 0, sourceWidth, sourceHeight, ratio, quality, candidates);
            if (_options.UseTiling)
                RunTiles(data, stride, width, height, sourceWidth, sourceHeight, ratio, quality, candidates);
            pre.Stop();

            var detections = candidates;
            if (ratio > 0 && ratio < 1.0)
                detections = detections.Select(d => new Candidate(
                    d.X / (float)ratio,
                    d.Y / (float)ratio,
                    d.Width / (float)ratio,
                    d.Height / (float)ratio,
                    d.Score)).ToList();

            var final = ApplyNms(detections, _options.NmsThreshold, _options.TopK)
                .Select(d => new FaceDetectionResult
                {
                    Bounds = new Rect(d.X, d.Y, d.Width, d.Height),
                    Confidence = d.Score
                })
                .ToArray();

            Debug.WriteLine($"[YuNetOnnx] totalMs={pre.ElapsedMilliseconds}, faces={final.Length}, tiling={_options.UseTiling}");
            return final;
        }

        public void Dispose()
        {
            _session.Dispose();
        }

        private void RunTiles(
            IntPtr data,
            int stride,
            int width,
            int height,
            int sourceWidth,
            int sourceHeight,
            double ratio,
            DownscaleQuality quality,
            List<Candidate> candidates)
        {
            int columns = Math.Clamp(_options.TileColumns, 1, 8);
            int rows = Math.Clamp(_options.TileRows, 1, 8);
            double overlap = Math.Clamp(_options.TileOverlapRatio, 0.0, 0.45);
            int baseTileWidth = Math.Max(1, (int)Math.Ceiling(sourceWidth / (double)columns));
            int baseTileHeight = Math.Max(1, (int)Math.Ceiling(sourceHeight / (double)rows));
            int overlapX = (int)Math.Round(baseTileWidth * overlap);
            int overlapY = (int)Math.Round(baseTileHeight * overlap);

            for (int row = 0; row < rows; row++)
            {
                for (int col = 0; col < columns; col++)
                {
                    int x1 = Math.Max(0, col * baseTileWidth - overlapX);
                    int y1 = Math.Max(0, row * baseTileHeight - overlapY);
                    int x2 = Math.Min(sourceWidth, (col + 1) * baseTileWidth + overlapX);
                    int y2 = Math.Min(sourceHeight, (row + 1) * baseTileHeight + overlapY);
                    int tileWidth = x2 - x1;
                    int tileHeight = y2 - y1;
                    if (tileWidth < 8 || tileHeight < 8)
                        continue;

                    RunRegion(data, stride, width, height, x1, y1, tileWidth, tileHeight, ratio, quality, candidates);
                }
            }
        }

        private void RunRegion(
            IntPtr data,
            int stride,
            int width,
            int height,
            int sourceX,
            int sourceY,
            int sourceWidth,
            int sourceHeight,
            double ratio,
            DownscaleQuality quality,
            List<Candidate> candidates)
        {
            var tensor = new DenseTensor<float>(new[] { 1, 3, _inputHeight, _inputWidth });
            FillTensorFromBgra(data, stride, width, height, sourceX, sourceY, sourceWidth, sourceHeight, ratio, tensor, quality);

            using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) });
            var regionCandidates = DecodeOutputs(results, sourceWidth, sourceHeight);
            foreach (var candidate in regionCandidates)
            {
                candidates.Add(new Candidate(
                    candidate.X + sourceX,
                    candidate.Y + sourceY,
                    candidate.Width,
                    candidate.Height,
                    candidate.Score));
            }
        }

        private List<Candidate> DecodeOutputs(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results, int sourceWidth, int sourceHeight)
        {
            var tensors = results.ToDictionary(r => r.Name, r => new OutputTensor(r.AsTensor<float>()));
            var candidates = new List<Candidate>();
            float scaleX = sourceWidth / (float)_inputWidth;
            float scaleY = sourceHeight / (float)_inputHeight;

            foreach (int stride in Strides)
            {
                if (!tensors.TryGetValue($"cls_{stride}", out var cls) ||
                    !tensors.TryGetValue($"obj_{stride}", out var obj) ||
                    !tensors.TryGetValue($"bbox_{stride}", out var bbox))
                    continue;

                int cols = _inputWidth / stride;
                int rows = _inputHeight / stride;
                int count = Math.Min(rows * cols, Math.Min(cls.Count, Math.Min(obj.Count, bbox.Count)));

                for (int i = 0; i < count; i++)
                {
                    float clsScore = Math.Clamp(cls.Values[i], 0.0f, 1.0f);
                    float objScore = Math.Clamp(obj.Values[i], 0.0f, 1.0f);
                    float score = MathF.Sqrt(clsScore * objScore);
                    if (score < _options.ConfidenceThreshold)
                        continue;

                    int row = i / cols;
                    int col = i % cols;
                    float cx = (col + bbox.Values[i * 4 + 0]) * stride;
                    float cy = (row + bbox.Values[i * 4 + 1]) * stride;
                    float w = MathF.Exp(bbox.Values[i * 4 + 2]) * stride;
                    float h = MathF.Exp(bbox.Values[i * 4 + 3]) * stride;
                    float x1 = (cx - w / 2.0f) * scaleX;
                    float y1 = (cy - h / 2.0f) * scaleY;
                    AddClampedCandidate(candidates, x1, y1, w * scaleX, h * scaleY, sourceWidth, sourceHeight, score);
                }
            }

            return candidates;
        }

        private static void AddClampedCandidate(List<Candidate> candidates, float x, float y, float width, float height, int imageWidth, int imageHeight, float score)
        {
            float x1 = Math.Clamp(x, 0, imageWidth - 1);
            float y1 = Math.Clamp(y, 0, imageHeight - 1);
            float x2 = Math.Clamp(x + width, 0, imageWidth - 1);
            float y2 = Math.Clamp(y + height, 0, imageHeight - 1);
            float w = x2 - x1;
            float h = y2 - y1;
            if (w < 2 || h < 2)
                return;
            candidates.Add(new Candidate(x1, y1, w, h, score));
        }

        private unsafe void FillTensorFromBgra(
            IntPtr data,
            int stride,
            int width,
            int height,
            int sourceX,
            int sourceY,
            int sourceWidth,
            int sourceHeight,
            double ratio,
            DenseTensor<float> tensor,
            DownscaleQuality quality)
        {
            byte* src = (byte*)data;
            double modelToSourceX = sourceWidth / (double)_inputWidth;
            double modelToSourceY = sourceHeight / (double)_inputHeight;
            double sourceToActual = ratio > 0 && ratio < 1.0 ? 1.0 / ratio : 1.0;

            for (int y = 0; y < _inputHeight; y++)
            {
                double srcY = (sourceY + y * modelToSourceY) * sourceToActual;
                int y0 = Math.Clamp((int)Math.Floor(srcY), 0, height - 1);
                int y1 = Math.Min(y0 + 1, height - 1);
                double fy = srcY - y0;

                for (int x = 0; x < _inputWidth; x++)
                {
                    double srcX = (sourceX + x * modelToSourceX) * sourceToActual;
                    int x0 = Math.Clamp((int)Math.Floor(srcX), 0, width - 1);
                    int x1 = Math.Min(x0 + 1, width - 1);
                    double fx = srcX - x0;

                    (float b, float g, float r) = quality == DownscaleQuality.FastNearest
                        ? ReadPixel(src, stride, x0, y0)
                        : ReadBilinear(src, stride, x0, y0, x1, y1, fx, fy);

                    tensor[0, 0, y, x] = b;
                    tensor[0, 1, y, x] = g;
                    tensor[0, 2, y, x] = r;
                }
            }
        }

        private static unsafe (float B, float G, float R) ReadPixel(byte* src, int stride, int x, int y)
        {
            byte* p = src + y * stride + x * 4;
            return (p[0], p[1], p[2]);
        }

        private static unsafe (float B, float G, float R) ReadBilinear(byte* src, int stride, int x0, int y0, int x1, int y1, double fx, double fy)
        {
            var p00 = ReadPixel(src, stride, x0, y0);
            var p10 = ReadPixel(src, stride, x1, y0);
            var p01 = ReadPixel(src, stride, x0, y1);
            var p11 = ReadPixel(src, stride, x1, y1);
            double wx0 = 1.0 - fx;
            double wx1 = fx;
            double wy0 = 1.0 - fy;
            double wy1 = fy;
            return (
                (float)(p00.B * wx0 * wy0 + p10.B * wx1 * wy0 + p01.B * wx0 * wy1 + p11.B * wx1 * wy1),
                (float)(p00.G * wx0 * wy0 + p10.G * wx1 * wy0 + p01.G * wx0 * wy1 + p11.G * wx1 * wy1),
                (float)(p00.R * wx0 * wy0 + p10.R * wx1 * wy0 + p01.R * wx0 * wy1 + p11.R * wx1 * wy1));
        }

        private static IReadOnlyList<Candidate> ApplyNms(IReadOnlyList<Candidate> candidates, float threshold, int topK)
        {
            var ordered = candidates.OrderByDescending(c => c.Score).Take(Math.Max(1, topK)).ToList();
            var kept = new List<Candidate>();
            while (ordered.Count > 0)
            {
                var current = ordered[0];
                ordered.RemoveAt(0);
                kept.Add(current);
                ordered.RemoveAll(other => IoU(current, other) > threshold);
            }

            return kept;
        }

        private static float IoU(Candidate a, Candidate b)
        {
            float x1 = Math.Max(a.X, b.X);
            float y1 = Math.Max(a.Y, b.Y);
            float x2 = Math.Min(a.X + a.Width, b.X + b.Width);
            float y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);
            float intersection = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
            if (intersection <= 0)
                return 0;
            float union = a.Width * a.Height + b.Width * b.Height - intersection;
            return union <= 0 ? 0 : intersection / union;
        }

        private static int ReadInputDimension(IReadOnlyList<int> dims, int index, int fallback)
        {
            if (dims.Count <= index || dims[index] <= 0)
                return fallback;
            return dims[index];
        }

        private readonly record struct Candidate(float X, float Y, float Width, float Height, float Score);

        private readonly record struct OutputTensor
        {
            public OutputTensor(Tensor<float> tensor)
            {
                int last = tensor.Dimensions.Length == 0 ? 0 : tensor.Dimensions[^1];
                LastDimension = last;
                Values = tensor.ToArray();
                Count = last <= 0 ? 0 : Values.Length / last;
            }

            public float[] Values { get; }

            public int Count { get; }

            public int LastDimension { get; }
        }
    }
}
