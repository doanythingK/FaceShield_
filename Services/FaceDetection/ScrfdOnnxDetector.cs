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
    public sealed class ScrfdOnnxDetector : IBgraFaceDetector
    {
        private static readonly int[] CandidateStrides = { 8, 16, 32, 64, 128 };
        private readonly InferenceSession _session;
        private readonly string _inputName;
        private readonly int _inputWidth;
        private readonly int _inputHeight;
        private readonly ScrfdOnnxDetectorOptions _options;

        internal string ExecutionProviderLabel { get; }

        public ScrfdOnnxDetector(ScrfdOnnxDetectorOptions? options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrWhiteSpace(_options.ModelPath))
                throw new InvalidOperationException("SCRFD ONNX model path is required.");
            if (!File.Exists(_options.ModelPath))
                throw new FileNotFoundException("SCRFD ONNX model was not found.", _options.ModelPath);

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

            bool usesGpuExecutionProvider = _options.UseGpu && TryAppendGpuExecutionProvider(sessionOptions);
            ExecutionProviderLabel = usesGpuExecutionProvider ? "GPU:DirectML" : "CPU";

            _session = new InferenceSession(_options.ModelPath, sessionOptions);
            _inputName = _session.InputMetadata.Keys.First();

            var dims = _session.InputMetadata[_inputName].Dimensions;
            _inputHeight = _options.InputHeight.GetValueOrDefault(ReadInputDimension(dims, 2, 640));
            _inputWidth = _options.InputWidth.GetValueOrDefault(ReadInputDimension(dims, 3, 640));

            if (_options.DumpDebug)
            {
                string inputs = string.Join("; ", _session.InputMetadata.Select(m => $"{m.Key}[{string.Join("x", m.Value.Dimensions)}]"));
                string outputs = string.Join("; ", _session.OutputMetadata.Select(m => $"{m.Key}[{string.Join("x", m.Value.Dimensions)}]"));
                Debug.WriteLine($"[ScrfdOnnxMeta] model={Path.GetFileName(_options.ModelPath)}, inputs={inputs}, outputs={outputs}");
            }
        }

        public IReadOnlyList<FaceDetectionResult> DetectFaces(WriteableBitmap frame)
        {
            if (frame == null)
                return Array.Empty<FaceDetectionResult>();

            using var fb = frame.Lock();
            return DetectFacesBgra(
                fb.Address,
                fb.RowBytes,
                frame.PixelSize.Width,
                frame.PixelSize.Height,
                1.0,
                DownscaleQuality.BalancedBilinear);
        }

        public IReadOnlyList<FaceDetectionResult> DetectFacesDownscaled(
            WriteableBitmap frame,
            double ratio,
            DownscaleQuality quality)
        {
            if (frame == null)
                return Array.Empty<FaceDetectionResult>();

            using var fb = frame.Lock();
            return DetectFacesBgra(
                fb.Address,
                fb.RowBytes,
                frame.PixelSize.Width,
                frame.PixelSize.Height,
                ratio,
                quality);
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
            var tensor = new DenseTensor<float>(new[] { 1, 3, _inputHeight, _inputWidth });
            var resize = FillTensorFromBgra(data, stride, width, height, sourceWidth, sourceHeight, ratio, tensor, quality);
            pre.Stop();

            var infer = Stopwatch.StartNew();
            using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) });
            infer.Stop();

            var detections = DecodeScrfdOutputs(results, sourceWidth, sourceHeight, resize);
            int rawCount = detections.Count;
            if (ratio > 0 && ratio < 1.0)
                detections = detections.Select(d => new Candidate(
                    d.X / (float)ratio,
                    d.Y / (float)ratio,
                    d.Width / (float)ratio,
                    d.Height / (float)ratio,
                    d.Score)).ToList();

            var afterNms = ApplyNms(detections, _options.NmsThreshold);
            if (_options.DumpDebug)
                WriteCandidateDebug(rawCount, afterNms, sourceWidth, sourceHeight, ratio);

            var final = afterNms
                .Select(d => new FaceDetectionResult
                {
                    Bounds = new Rect(d.X, d.Y, d.Width, d.Height),
                    Confidence = d.Score
                })
                .ToArray();

            Debug.WriteLine($"[ScrfdOnnx] preMs={pre.ElapsedMilliseconds}, inferMs={infer.ElapsedMilliseconds}, faces={final.Length}");
            return final;
        }

        public void Dispose()
        {
            _session.Dispose();
        }

        private List<Candidate> DecodeScrfdOutputs(
            IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results,
            int sourceWidth,
            int sourceHeight,
            ResizeTransform resize)
        {
            var tensors = results
                .Select(r => new OutputTensor(r.Name, r.AsTensor<float>()))
                .ToArray();

            var scores = tensors.Where(t => t.LastDimension == 1 || t.LastDimension == 2).ToArray();
            var boxes = tensors.Where(t => t.LastDimension == 4).ToArray();
            if (scores.Length == 0 || boxes.Length == 0)
                throw new InvalidOperationException("SCRFD output tensors were not recognized. Expected score tensors with last dimension 1/2 and bbox tensors with last dimension 4.");

            var pairs = PairScoreAndBoxTensors(scores, boxes);
            var candidates = new List<Candidate>();

            foreach (var pair in pairs)
            {
                var scoreValues = pair.Score.Values;
                var boxValues = pair.Box.Values;
                int count = Math.Min(pair.Score.Count, pair.Box.Count);
                int stride = GuessStride(count, sourceWidth, sourceHeight);
                int featureWidth = Math.Max(1, (int)Math.Ceiling(_inputWidth / (double)stride));
                int featureHeight = Math.Max(1, (int)Math.Ceiling(_inputHeight / (double)stride));
                int anchorsPerPoint = Math.Max(1, count / Math.Max(1, featureWidth * featureHeight));

                if (_options.DumpDebug)
                {
                    Debug.WriteLine(
                        $"[ScrfdOnnxPair] score={pair.Score.Name}[{string.Join("x", pair.Score.Dimensions)}], box={pair.Box.Name}[{string.Join("x", pair.Box.Dimensions)}], count={count}, stride={stride}, feature={featureWidth}x{featureHeight}, anchorsPerPoint={anchorsPerPoint}");
                }

                for (int i = 0; i < count; i++)
                {
                    float score = ReadScore(scoreValues, pair.Score.LastDimension, i);
                    if (score < _options.ConfidenceThreshold)
                        continue;

                    int pointIndex = i / anchorsPerPoint;
                    int anchorX = pointIndex % featureWidth;
                    int anchorY = pointIndex / featureWidth;
                    float centerX = (anchorX + _options.AnchorCenterOffset) * stride;
                    float centerY = (anchorY + _options.AnchorCenterOffset) * stride;

                    float bboxScale = _options.MultiplyBboxByStride ? stride : 1.0f;
                    float left = ReadBox(boxValues, i, 0) * bboxScale;
                    float top = ReadBox(boxValues, i, 1) * bboxScale;
                    float right = ReadBox(boxValues, i, 2) * bboxScale;
                    float bottom = ReadBox(boxValues, i, 3) * bboxScale;

                    float x1 = (centerX - left - resize.PadX) / resize.ScaleX;
                    float y1 = (centerY - top - resize.PadY) / resize.ScaleY;
                    float x2 = (centerX + right - resize.PadX) / resize.ScaleX;
                    float y2 = (centerY + bottom - resize.PadY) / resize.ScaleY;
                    AddClampedCandidate(candidates, x1, y1, x2, y2, sourceWidth, sourceHeight, score);
                }
            }

            return candidates;
        }

        private void WriteCandidateDebug(
            int rawCount,
            IReadOnlyList<Candidate> afterNms,
            int sourceWidth,
            int sourceHeight,
            double ratio)
        {
            string top = "none";
            if (afterNms.Count > 0)
            {
                top = string.Join(
                    "; ",
                    afterNms
                        .OrderByDescending(c => c.Score)
                        .Take(Math.Max(1, _options.DebugCandidateLimit))
                        .Select(c =>
                        {
                            double areaRatio = c.Width * c.Height / Math.Max(1.0, sourceWidth * (double)sourceHeight);
                            double aspect = c.Height > 0 ? c.Width / c.Height : 0.0;
                            return $"x={c.X:0.0},y={c.Y:0.0},w={c.Width:0.0},h={c.Height:0.0},areaRatio={areaRatio:0.######},aspect={aspect:0.###},conf={c.Score:0.###}";
                        }));
            }

            Debug.WriteLine(
                $"[ScrfdOnnxDump] model={Path.GetFileName(_options.ModelPath)}, input={_inputWidth}x{_inputHeight}, source={sourceWidth}x{sourceHeight}, ratio={ratio:0.###}, rgb={_options.UseRgbInput}, letterbox={_options.UseLetterboxResize}, centerPad={_options.CenterLetterboxPadding}, padValue={_options.LetterboxPaddingValue:0.###}, mean={_options.InputMean:0.###}, std={_options.InputStd:0.###}, anchorOffset={_options.AnchorCenterOffset:0.###}, multiplyStride={_options.MultiplyBboxByStride}, rawAboveThreshold={rawCount}, afterNms={afterNms.Count}, top={top}");
        }

        private static (OutputTensor Score, OutputTensor Box)[] PairScoreAndBoxTensors(OutputTensor[] scores, OutputTensor[] boxes)
        {
            var orderedScores = scores.OrderByDescending(s => s.Count).ToArray();
            var orderedBoxes = boxes.OrderByDescending(b => b.Count).ToArray();
            int count = Math.Min(orderedScores.Length, orderedBoxes.Length);
            var pairs = new (OutputTensor Score, OutputTensor Box)[count];
            for (int i = 0; i < count; i++)
                pairs[i] = (orderedScores[i], orderedBoxes[i]);
            return pairs;
        }

        private int GuessStride(int outputCount, int sourceWidth, int sourceHeight)
        {
            int bestStride = 8;
            int bestDistance = int.MaxValue;
            foreach (int stride in CandidateStrides)
            {
                int fw = Math.Max(1, (int)Math.Ceiling(_inputWidth / (double)stride));
                int fh = Math.Max(1, (int)Math.Ceiling(_inputHeight / (double)stride));
                int expected = fw * fh * 2;
                int distance = Math.Abs(expected - outputCount);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestStride = stride;
                }
            }

            return bestStride;
        }

        private static float ReadScore(float[] values, int lastDimension, int index)
        {
            if (lastDimension == 2)
                return values[index * 2 + 1];
            return values[index];
        }

        private static float ReadBox(float[] values, int index, int offset)
        {
            return values[index * 4 + offset];
        }

        private static void AddClampedCandidate(List<Candidate> candidates, float x1, float y1, float x2, float y2, int width, int height, float score)
        {
            x1 = Math.Clamp(x1, 0, width - 1);
            y1 = Math.Clamp(y1, 0, height - 1);
            x2 = Math.Clamp(x2, 0, width - 1);
            y2 = Math.Clamp(y2, 0, height - 1);
            float w = x2 - x1;
            float h = y2 - y1;
            if (w < 2 || h < 2)
                return;
            candidates.Add(new Candidate(x1, y1, w, h, score));
        }

        private unsafe ResizeTransform FillTensorFromBgra(
            IntPtr data,
            int stride,
            int width,
            int height,
            int sourceWidth,
            int sourceHeight,
            double ratio,
            DenseTensor<float> tensor,
            DownscaleQuality quality)
        {
            byte* src = (byte*)data;
            double scaleX;
            double scaleY;
            int resizedWidth;
            int resizedHeight;
            int padX = 0;
            int padY = 0;
            if (_options.UseLetterboxResize)
            {
                double scale = Math.Min(_inputWidth / (double)sourceWidth, _inputHeight / (double)sourceHeight);
                scaleX = scale;
                scaleY = scale;
                resizedWidth = Math.Max(1, Math.Min(_inputWidth, (int)Math.Round(sourceWidth * scale)));
                resizedHeight = Math.Max(1, Math.Min(_inputHeight, (int)Math.Round(sourceHeight * scale)));
                if (_options.CenterLetterboxPadding)
                {
                    padX = Math.Max(0, (_inputWidth - resizedWidth) / 2);
                    padY = Math.Max(0, (_inputHeight - resizedHeight) / 2);
                }
            }
            else
            {
                scaleX = _inputWidth / (double)sourceWidth;
                scaleY = _inputHeight / (double)sourceHeight;
                resizedWidth = _inputWidth;
                resizedHeight = _inputHeight;
            }

            float inputStd = Math.Abs(_options.InputStd) > 0.0001f ? _options.InputStd : 1.0f;
            float padding = (_options.LetterboxPaddingValue - _options.InputMean) / inputStd;
            for (int c = 0; c < 3; c++)
            {
                for (int y = 0; y < _inputHeight; y++)
                {
                    for (int x = 0; x < _inputWidth; x++)
                        tensor[0, c, y, x] = padding;
                }
            }

            double modelToSourceX = 1.0 / scaleX;
            double modelToSourceY = 1.0 / scaleY;
            double sourceToActual = ratio > 0 && ratio < 1.0 ? 1.0 / ratio : 1.0;

            for (int y = 0; y < _inputHeight; y++)
            {
                if (y < padY || y >= padY + resizedHeight)
                    continue;

                double srcY = (y - padY) * modelToSourceY * sourceToActual;
                int y0 = Math.Clamp((int)Math.Floor(srcY), 0, height - 1);
                int y1 = Math.Min(y0 + 1, height - 1);
                double fy = srcY - y0;

                for (int x = 0; x < _inputWidth; x++)
                {
                    if (x < padX || x >= padX + resizedWidth)
                        continue;

                    double srcX = (x - padX) * modelToSourceX * sourceToActual;
                    int x0 = Math.Clamp((int)Math.Floor(srcX), 0, width - 1);
                    int x1 = Math.Min(x0 + 1, width - 1);
                    double fx = srcX - x0;

                    (float b, float g, float r) = quality == DownscaleQuality.FastNearest
                        ? ReadPixel(src, stride, x0, y0)
                        : ReadBilinear(src, stride, x0, y0, x1, y1, fx, fy);

                    if (_options.UseRgbInput)
                    {
                        tensor[0, 0, y, x] = (r - _options.InputMean) / inputStd;
                        tensor[0, 1, y, x] = (g - _options.InputMean) / inputStd;
                        tensor[0, 2, y, x] = (b - _options.InputMean) / inputStd;
                    }
                    else
                    {
                        tensor[0, 0, y, x] = (b - _options.InputMean) / inputStd;
                        tensor[0, 1, y, x] = (g - _options.InputMean) / inputStd;
                        tensor[0, 2, y, x] = (r - _options.InputMean) / inputStd;
                    }
                }
            }

            return new ResizeTransform((float)scaleX, (float)scaleY, padX, padY);
        }

        private static unsafe (float B, float G, float R) ReadPixel(byte* src, int stride, int x, int y)
        {
            byte* p = src + y * stride + x * 4;
            return (p[0], p[1], p[2]);
        }

        private static unsafe (float B, float G, float R) ReadBilinear(
            byte* src,
            int stride,
            int x0,
            int y0,
            int x1,
            int y1,
            double fx,
            double fy)
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

        private static IReadOnlyList<Candidate> ApplyNms(IReadOnlyList<Candidate> candidates, float threshold)
        {
            var ordered = candidates.OrderByDescending(c => c.Score).ToList();
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

        private static bool TryAppendGpuExecutionProvider(SessionOptions options)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var method = typeof(SessionOptions).GetMethods()
                        .FirstOrDefault(m => m.Name == "AppendExecutionProvider_DML");
                    if (method == null)
                        return false;

                    var parameters = method.GetParameters();
                    if (parameters.Length == 0)
                        method.Invoke(options, Array.Empty<object>());
                    else if (parameters.Length == 1 && parameters[0].ParameterType == typeof(int))
                        method.Invoke(options, new object[] { 0 });
                    else if (parameters.Length == 1 && parameters[0].ParameterType == typeof(uint))
                        method.Invoke(options, new object[] { 0u });
                    else
                        return false;

                    return true;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private readonly record struct Candidate(float X, float Y, float Width, float Height, float Score);

        private readonly record struct ResizeTransform(float ScaleX, float ScaleY, float PadX, float PadY);

        private readonly record struct OutputTensor
        {
            public OutputTensor(string name, Tensor<float> tensor)
            {
                Name = name;
                Dimensions = tensor.Dimensions.ToArray();
                Values = tensor.ToArray();
            }

            public string Name { get; }

            public int[] Dimensions { get; }

            public float[] Values { get; }

            public int LastDimension => Dimensions.Length == 0 ? 0 : Dimensions[^1];

            public int Count
            {
                get
                {
                    if (LastDimension <= 0)
                        return 0;
                    return Values.Length / LastDimension;
                }
            }
        }
    }
}
