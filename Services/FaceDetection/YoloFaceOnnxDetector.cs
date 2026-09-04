using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Media.Imaging;
using FaceShield.Models.Analysis;
using FaceShield.Services.Analysis;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace FaceShield.Services.FaceDetection
{
    public sealed class YoloFaceOnnxDetector : IBgraFaceDetector, IExecutionProviderDiagnostics
    {
        private const int Yolo5FaceAnchorsPerScale = 3;
        private const int Yolo5FaceFeaturesPerAnchor = 16;
        private const int PreprocessCoordinateCacheCapacity = 16;
        private static readonly object StatusLock = new();
        private static string _lastExecutionProviderLabel = "CPU";
        private static string? _lastExecutionProviderError;

        internal string ExecutionProviderLabel { get; }
        internal string? ExecutionProviderError { get; private set; }

        string IExecutionProviderDiagnostics.ExecutionProviderLabel => ExecutionProviderLabel;
        string? IExecutionProviderDiagnostics.ExecutionProviderError => ExecutionProviderError;

        private static readonly (float Width, float Height)[][] Yolo5FaceAnchors =
        {
            new[] { (4f, 5f), (8f, 10f), (13f, 16f) },
            new[] { (23f, 29f), (43f, 55f), (73f, 105f) },
            new[] { (146f, 217f), (231f, 300f), (335f, 433f) }
        };

        private readonly InferenceSession _session;
        private readonly string _inputName;
        private readonly int _inputWidth;
        private readonly int _inputHeight;
        private readonly YoloFaceOnnxDetectorOptions _options;
        private readonly object _inferenceGate = new();
        private readonly DenseTensor<float> _inputTensor;
        private readonly NamedOnnxValue[] _inputValues;
        private readonly List<PreprocessCoordinateCache> _preprocessCoordinateCaches =
            new(PreprocessCoordinateCacheCapacity);
        private TensorPaddingLayout? _initializedTensorPaddingLayout;
        private bool _disposed;

        public YoloFaceOnnxDetector(YoloFaceOnnxDetectorOptions? options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrWhiteSpace(_options.ModelPath))
                throw new InvalidOperationException("YOLO face ONNX model path is required.");
            if (!File.Exists(_options.ModelPath))
                throw new FileNotFoundException("YOLO face ONNX model was not found.", _options.ModelPath);

            // FaceONNX와 달리 YOLO는 별도 factory 경로에서 생성되므로 macOS의
            // bundled ONNX Runtime/libomp 의존성을 동일하게 사전 검증한다.
            if (OperatingSystem.IsMacOS())
                FaceOnnxDetector.EnsureRuntimeAvailable();

            using var sessionOptions = CreateSessionOptions();
            string? gpuProvider = null;
            bool shouldTryGpu = _options.UseGpu &&
                (!OperatingSystem.IsMacOS() || _options.EnableCoreMl);
            if (shouldTryGpu)
                gpuProvider = TryAppendGpuExecutionProvider(sessionOptions);

            string? providerError = shouldTryGpu && gpuProvider == null
                ? "GPU execution provider load failed."
                : null;
            if (providerError != null && GetLastExecutionProviderError() == null)
                UpdateExecutionProviderError(providerError);

            try
            {
                _session = new InferenceSession(_options.ModelPath, sessionOptions);
                ExecutionProviderLabel = gpuProvider == "CoreML"
                    ? "CoreML(default)"
                    : gpuProvider != null
                        ? $"GPU:{gpuProvider}"
                        : shouldTryGpu ? "CPU(가속 실패)" : "CPU";
                ExecutionProviderError = providerError;
                UpdateExecutionProviderLabel(ExecutionProviderLabel);
                if (gpuProvider != null || !shouldTryGpu)
                    UpdateExecutionProviderError(null);
            }
            catch (Exception ex) when (shouldTryGpu)
            {
                using var fallbackOptions = CreateSessionOptions();
                _session = new InferenceSession(_options.ModelPath, fallbackOptions);
                ExecutionProviderLabel = "CPU(가속 실패)";
                ExecutionProviderError = ex.Message;
                UpdateExecutionProviderLabel(ExecutionProviderLabel);
                UpdateExecutionProviderError(ex.Message);
            }

            _inputName = _session.InputMetadata.Keys.First();

            var dims = _session.InputMetadata[_inputName].Dimensions;
            _inputHeight = ResolveInputDimension(dims, 2, 640, _options.InputHeight);
            _inputWidth = ResolveInputDimension(dims, 3, 640, _options.InputWidth);
            _inputTensor = new DenseTensor<float>(new[] { 1, 3, _inputHeight, _inputWidth });
            _inputValues = new[] { NamedOnnxValue.CreateFromTensor(_inputName, _inputTensor) };

            if (_options.DumpDebug)
            {
                string inputs = string.Join("; ", _session.InputMetadata.Select(m => $"{m.Key}[{string.Join("x", m.Value.Dimensions)}]"));
                string outputs = string.Join("; ", _session.OutputMetadata.Select(m => $"{m.Key}[{string.Join("x", m.Value.Dimensions)}]"));
                Debug.WriteLine($"[YoloFaceOnnxMeta] model={Path.GetFileName(_options.ModelPath)}, type={_options.ModelType}, inputs={inputs}, outputs={outputs}");
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
            var candidates = new List<Candidate>();
            long inferMs = 0;
            if (!_options.UseTiling || _options.IncludeFullFrameWhenTiling)
            {
                inferMs += RunRegion(
                    data,
                    stride,
                    width,
                    height,
                    0,
                    0,
                    sourceWidth,
                    sourceHeight,
                    ratio,
                    quality,
                    candidates);
            }

            if (_options.UseTiling)
            {
                inferMs += RunTiles(
                    data,
                    stride,
                    width,
                    height,
                    sourceWidth,
                    sourceHeight,
                    ratio,
                    quality,
                    candidates);
            }
            pre.Stop();

            int rawCount = candidates.Count;
            if (ratio > 0 && ratio < 1.0)
            {
                candidates = candidates.Select(d => new Candidate(
                    d.X / (float)ratio,
                    d.Y / (float)ratio,
                    d.Width / (float)ratio,
                    d.Height / (float)ratio,
                    d.Score,
                    d.Landmarks.HasValue ? d.Landmarks.Value.Scale(1.0f / (float)ratio) : null)).ToList();
            }

            var afterNms = ApplySmallAreaFilter(
                ApplyLowConfidencePositionFilter(
                    ApplyTopSmallLowConfidenceFilter(
                        ApplyAspectRatioFilter(
                            RefineLargeBoxes(
                                ApplyNms(candidates, _options.NmsThreshold, Math.Max(1, _options.MaxDetections)),
                                width,
                                height)),
                        width,
                        height),
                    width,
                    height),
                width,
                height);
            if (_options.DumpDebug)
                WriteCandidateDebug(rawCount, afterNms, sourceWidth, sourceHeight, ratio);

            var final = afterNms
                .Select(d => new FaceDetectionResult
                {
                    Bounds = new Rect(d.X, d.Y, d.Width, d.Height),
                    Confidence = d.Score
                })
                .ToArray();

            if (_options.DumpDebug)
                Debug.WriteLine($"[YoloFaceOnnx] type={_options.ModelType}, totalMs={pre.ElapsedMilliseconds}, inferMs={inferMs}, faces={final.Length}, tiling={_options.UseTiling}");
            return final;
        }

        public void Dispose()
        {
            lock (_inferenceGate)
            {
                if (_disposed)
                    return;

                _disposed = true;
                _session.Dispose();
            }
        }

        public static string GetLastExecutionProviderLabel()
        {
            lock (StatusLock)
            {
                return _lastExecutionProviderLabel;
            }
        }

        public static string? GetLastExecutionProviderError()
        {
            lock (StatusLock)
            {
                return _lastExecutionProviderError;
            }
        }

        private static void UpdateExecutionProviderLabel(string label)
        {
            lock (StatusLock)
            {
                _lastExecutionProviderLabel = label;
            }
        }

        private static void UpdateExecutionProviderError(string? error)
        {
            lock (StatusLock)
            {
                _lastExecutionProviderError = error;
            }
        }

        private List<Candidate> DecodeOutputs(
            IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results,
            int sourceWidth,
            int sourceHeight,
            ResizeTransform resize)
        {
            var tensors = results
                .Select(r => new OutputTensor(r.Name, r.AsTensor<float>()))
                .Where(t => t.Values.Length > 0)
                .OrderByDescending(t => t.Values.Length)
                .ToArray();

            if (_options.ModelType == YoloFaceModelType.Yolo5Face &&
                TryDecodeYolo5FaceFeatureMaps(tensors, sourceWidth, sourceHeight, resize, out var yolo5Candidates))
            {
                return yolo5Candidates;
            }

            foreach (var tensor in tensors)
            {
                if (!TryGetCandidateLayout(tensor, out int count, out int features, out bool channelFirst))
                    continue;

                if (_options.DumpDebug)
                {
                    Debug.WriteLine(
                        $"[YoloFaceOnnxOutput] name={tensor.Name}[{string.Join("x", tensor.Dimensions)}], count={count}, features={features}, channelFirst={channelFirst}");
                }

                return DecodeCandidateTensor(tensor, count, features, channelFirst, sourceWidth, sourceHeight, resize);
            }

            throw new InvalidOperationException("YOLO face output tensor was not recognized. Expected a tensor shaped like [1,N,F] or [1,F,N] with at least 5 features.");
        }

        private bool TryDecodeYolo5FaceFeatureMaps(
            IReadOnlyList<OutputTensor> tensors,
            int sourceWidth,
            int sourceHeight,
            ResizeTransform resize,
            out List<Candidate> candidates)
        {
            candidates = new List<Candidate>();
            var maps = tensors
                .Where(t => TryGetYolo5FaceFeatureMapShape(t, out _, out _, out _))
                .OrderByDescending(t => t.Dimensions[2] * t.Dimensions[3])
                .Take(Yolo5FaceAnchors.Length)
                .ToArray();

            if (maps.Length == 0)
                return false;

            for (int scaleIndex = 0; scaleIndex < maps.Length; scaleIndex++)
            {
                var tensor = maps[scaleIndex];
                if (!TryGetYolo5FaceFeatureMapShape(tensor, out int channels, out int gridHeight, out int gridWidth))
                    continue;

                if (_options.DumpDebug)
                {
                    Debug.WriteLine(
                        $"[YoloFaceOnnxOutput] name={tensor.Name}[{string.Join("x", tensor.Dimensions)}], layout=yolo5-face-feature-map, scale={scaleIndex}, grid={gridWidth}x{gridHeight}, channels={channels}");
                }

                DecodeYolo5FaceFeatureMap(tensor, scaleIndex, gridWidth, gridHeight, sourceWidth, sourceHeight, resize, candidates);
            }

            return true;
        }

        private void DecodeYolo5FaceFeatureMap(
            OutputTensor tensor,
            int scaleIndex,
            int gridWidth,
            int gridHeight,
            int sourceWidth,
            int sourceHeight,
            ResizeTransform resize,
            List<Candidate> candidates)
        {
            var anchors = Yolo5FaceAnchors[Math.Min(scaleIndex, Yolo5FaceAnchors.Length - 1)];
            ReadOnlySpan<float> values = tensor.Values.Span;
            float strideX = _inputWidth / (float)Math.Max(1, gridWidth);
            float strideY = _inputHeight / (float)Math.Max(1, gridHeight);

            for (int anchorIndex = 0; anchorIndex < Yolo5FaceAnchorsPerScale; anchorIndex++)
            {
                var anchor = anchors[anchorIndex];
                for (int y = 0; y < gridHeight; y++)
                {
                    for (int x = 0; x < gridWidth; x++)
                    {
                        float objectness = Sigmoid(ReadYolo5FaceFeature(values, anchorIndex, 4, y, x, gridHeight, gridWidth));
                        if (objectness < _options.ObjectnessThreshold)
                            continue;

                        float classScore = Sigmoid(ReadYolo5FaceFeature(values, anchorIndex, 15, y, x, gridHeight, gridWidth));
                        float score = objectness * classScore;
                        if (score < _options.ConfidenceThreshold)
                            continue;

                        float rawX = Sigmoid(ReadYolo5FaceFeature(values, anchorIndex, 0, y, x, gridHeight, gridWidth));
                        float rawY = Sigmoid(ReadYolo5FaceFeature(values, anchorIndex, 1, y, x, gridHeight, gridWidth));
                        float rawW = Sigmoid(ReadYolo5FaceFeature(values, anchorIndex, 2, y, x, gridHeight, gridWidth));
                        float rawH = Sigmoid(ReadYolo5FaceFeature(values, anchorIndex, 3, y, x, gridHeight, gridWidth));

                        float cx = (rawX * 2.0f - 0.5f + x) * strideX;
                        float cy = (rawY * 2.0f - 0.5f + y) * strideY;
                        float w = MathF.Pow(rawW * 2.0f, 2.0f) * anchor.Width;
                        float h = MathF.Pow(rawH * 2.0f, 2.0f) * anchor.Height;

                        float x1 = (cx - w * 0.5f - resize.PadX) / resize.ScaleX;
                        float y1 = (cy - h * 0.5f - resize.PadY) / resize.ScaleY;
                        float x2 = (cx + w * 0.5f - resize.PadX) / resize.ScaleX;
                        float y2 = (cy + h * 0.5f - resize.PadY) / resize.ScaleY;
                        var landmarks = DecodeYolo5LandmarkBounds(values, anchorIndex, y, x, gridHeight, gridWidth, strideX, strideY, anchor.Width, anchor.Height, resize);
                        AddClampedCandidate(candidates, x1, y1, x2, y2, sourceWidth, sourceHeight, score, landmarks);
                    }
                }
            }
        }

        private long RunTiles(
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
            long inferMs = 0;
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

                    inferMs += RunRegion(
                        data,
                        stride,
                        width,
                        height,
                        x1,
                        y1,
                        tileWidth,
                        tileHeight,
                        ratio,
                        quality,
                        candidates);
                }
            }

            return inferMs;
        }

        private long RunRegion(
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
            List<Candidate> regionCandidates;
            long inferElapsedMs;
            lock (_inferenceGate)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(YoloFaceOnnxDetector));

                var resize = FillTensorFromBgra(
                    data,
                    stride,
                    width,
                    height,
                    sourceX,
                    sourceY,
                    sourceWidth,
                    sourceHeight,
                    ratio,
                    _inputTensor,
                    quality);

                var infer = Stopwatch.StartNew();
                using var results = _session.Run(_inputValues);
                infer.Stop();
                inferElapsedMs = infer.ElapsedMilliseconds;
                regionCandidates = DecodeOutputs(results, sourceWidth, sourceHeight, resize);
            }

            foreach (var candidate in regionCandidates)
            {
                candidates.Add(new Candidate(
                    candidate.X + sourceX,
                    candidate.Y + sourceY,
                    candidate.Width,
                    candidate.Height,
                    candidate.Score,
                    candidate.Landmarks.HasValue ? candidate.Landmarks.Value.Offset(sourceX, sourceY) : null));
            }

            return inferElapsedMs;
        }

        private List<Candidate> DecodeCandidateTensor(
            OutputTensor tensor,
            int count,
            int features,
            bool channelFirst,
            int sourceWidth,
            int sourceHeight,
            ResizeTransform resize)
        {
            var candidates = new List<Candidate>();
            ReadOnlySpan<float> values = tensor.Values.Span;
            for (int i = 0; i < count; i++)
            {
                float objectness = ReadFeature(values, i, 4, count, features, channelFirst);
                if (objectness < _options.ObjectnessThreshold)
                    continue;

                float score = BuildScore(values, i, count, features, channelFirst, objectness);
                if (score < _options.ConfidenceThreshold)
                    continue;

                float cx = ReadFeature(values, i, 0, count, features, channelFirst);
                float cy = ReadFeature(values, i, 1, count, features, channelFirst);
                float w = ReadFeature(values, i, 2, count, features, channelFirst);
                float h = ReadFeature(values, i, 3, count, features, channelFirst);
                if (LooksNormalized(cx, cy, w, h))
                {
                    cx *= _inputWidth;
                    w *= _inputWidth;
                    cy *= _inputHeight;
                    h *= _inputHeight;
                }

                float x1 = (cx - w * 0.5f - resize.PadX) / resize.ScaleX;
                float y1 = (cy - h * 0.5f - resize.PadY) / resize.ScaleY;
                float x2 = (cx + w * 0.5f - resize.PadX) / resize.ScaleX;
                float y2 = (cy + h * 0.5f - resize.PadY) / resize.ScaleY;
                AddClampedCandidate(candidates, x1, y1, x2, y2, sourceWidth, sourceHeight, score);
            }

            return candidates;
        }

        private float BuildScore(
            ReadOnlySpan<float> values,
            int index,
            int count,
            int features,
            bool channelFirst,
            float objectness)
        {
            if (_options.ModelType == YoloFaceModelType.Yolo5Face && features >= 16)
            {
                float classScore = ReadFeature(values, index, features - 1, count, features, channelFirst);
                if (classScore >= 0 && classScore <= 1)
                    return objectness * classScore;
            }

            return objectness;
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
                $"[YoloFaceOnnxDump] model={Path.GetFileName(_options.ModelPath)}, type={_options.ModelType}, input={_inputWidth}x{_inputHeight}, source={sourceWidth}x{sourceHeight}, ratio={ratio:0.###}, rgb={_options.UseRgbInput}, letterbox={_options.UseLetterboxResize}, centerPad={_options.CenterLetterboxPadding}, padValue={_options.LetterboxPaddingValue:0.###}, objectness={_options.ObjectnessThreshold:0.###}, confidence={_options.ConfidenceThreshold:0.###}, nms={_options.NmsThreshold:0.###}, rawAboveThreshold={rawCount}, afterNms={afterNms.Count}, top={top}");
        }

        private static bool TryGetCandidateLayout(OutputTensor tensor, out int count, out int features, out bool channelFirst)
        {
            count = 0;
            features = 0;
            channelFirst = false;
            if (tensor.Dimensions.Length < 2)
                return false;

            var dims = tensor.Dimensions.Where(d => d > 0).ToArray();
            if (dims.Length < 2)
                return false;

            int last = dims[^1];
            int previous = dims.Length >= 2 ? dims[^2] : 0;
            if (last >= 5 && last <= 128)
            {
                features = last;
                count = tensor.Values.Length / features;
                channelFirst = false;
                return count > 0;
            }

            if (previous >= 5 && previous <= 128)
            {
                features = previous;
                count = tensor.Values.Length / features;
                channelFirst = true;
                return count > 0;
            }

            return false;
        }

        private static bool TryGetYolo5FaceFeatureMapShape(OutputTensor tensor, out int channels, out int gridHeight, out int gridWidth)
        {
            channels = 0;
            gridHeight = 0;
            gridWidth = 0;
            if (tensor.Dimensions.Length != 4 || tensor.Dimensions[0] != 1)
                return false;

            channels = tensor.Dimensions[1];
            gridHeight = tensor.Dimensions[2];
            gridWidth = tensor.Dimensions[3];
            return channels == Yolo5FaceAnchorsPerScale * Yolo5FaceFeaturesPerAnchor &&
                gridHeight > 0 &&
                gridWidth > 0;
        }

        private static float ReadYolo5FaceFeature(ReadOnlySpan<float> values, int anchorIndex, int feature, int y, int x, int gridHeight, int gridWidth)
        {
            int channel = anchorIndex * Yolo5FaceFeaturesPerAnchor + feature;
            int offset = ((channel * gridHeight) + y) * gridWidth + x;
            if (offset < 0 || offset >= values.Length)
                return 0;
            return values[offset];
        }

        private static float ReadFeature(ReadOnlySpan<float> values, int index, int feature, int count, int features, bool channelFirst)
        {
            int offset = channelFirst
                ? feature * count + index
                : index * features + feature;
            if (offset < 0 || offset >= values.Length)
                return 0;
            return values[offset];
        }

        private static bool LooksNormalized(float cx, float cy, float w, float h)
        {
            return cx >= 0 && cy >= 0 && w >= 0 && h >= 0 &&
                cx <= 2 && cy <= 2 && w <= 2 && h <= 2;
        }

        private static float Sigmoid(float value)
        {
            if (value >= 0)
            {
                float z = MathF.Exp(-value);
                return 1.0f / (1.0f + z);
            }
            else
            {
                float z = MathF.Exp(value);
                return z / (1.0f + z);
            }
        }

        private static LandmarkBounds? DecodeYolo5LandmarkBounds(
            ReadOnlySpan<float> values,
            int anchorIndex,
            int gridY,
            int gridX,
            int gridHeight,
            int gridWidth,
            float strideX,
            float strideY,
            float anchorWidth,
            float anchorHeight,
            ResizeTransform resize)
        {
            float minX = float.MaxValue;
            float minY = float.MaxValue;
            float maxX = float.MinValue;
            float maxY = float.MinValue;
            int count = 0;
            for (int i = 0; i < 5; i++)
            {
                int feature = 5 + i * 2;
                float rawX = ReadYolo5FaceFeature(values, anchorIndex, feature, gridY, gridX, gridHeight, gridWidth);
                float rawY = ReadYolo5FaceFeature(values, anchorIndex, feature + 1, gridY, gridX, gridHeight, gridWidth);
                float x = (rawX * anchorWidth + gridX * strideX - resize.PadX) / resize.ScaleX;
                float y = (rawY * anchorHeight + gridY * strideY - resize.PadY) / resize.ScaleY;
                if (!float.IsFinite(x) || !float.IsFinite(y))
                    continue;

                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
                count++;
            }

            return count >= 5 && maxX > minX && maxY > minY
                ? new LandmarkBounds(minX, minY, maxX, maxY, count)
                : null;
        }

        private static void AddClampedCandidate(
            List<Candidate> candidates,
            float x1,
            float y1,
            float x2,
            float y2,
            int width,
            int height,
            float score,
            LandmarkBounds? landmarks = null)
        {
            x1 = Math.Clamp(x1, 0, width - 1);
            y1 = Math.Clamp(y1, 0, height - 1);
            x2 = Math.Clamp(x2, 0, width - 1);
            y2 = Math.Clamp(y2, 0, height - 1);
            float w = x2 - x1;
            float h = y2 - y1;
            if (w < 2 || h < 2)
                return;
            candidates.Add(new Candidate(x1, y1, w, h, score, landmarks));
        }

        private unsafe ResizeTransform FillTensorFromBgra(
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

            float padding = _options.LetterboxPaddingValue * _options.InputScale;
            Span<float> values = tensor.Buffer.Span;
            bool hasPadding = padX > 0 ||
                padY > 0 ||
                resizedWidth < _inputWidth ||
                resizedHeight < _inputHeight;
            if (hasPadding)
            {
                var paddingLayout = new TensorPaddingLayout(
                    padX,
                    padY,
                    resizedWidth,
                    resizedHeight,
                    padding);
                if (_initializedTensorPaddingLayout != paddingLayout)
                {
                    values.Fill(padding);
                    _initializedTensorPaddingLayout = paddingLayout;
                }
            }
            else
            {
                // The valid image covers every tensor element, so the pixel loop below
                // fully initializes the buffer. Invalidate the padding state because a
                // later letterboxed run must restore its border before inference.
                _initializedTensorPaddingLayout = null;
            }

            int planeSize = checked(_inputWidth * _inputHeight);
            int redPlaneOffset = _options.UseRgbInput ? 0 : planeSize * 2;
            int greenPlaneOffset = planeSize;
            int bluePlaneOffset = _options.UseRgbInput ? planeSize * 2 : 0;
            float inputScale = _options.InputScale;

            double modelToSourceX = 1.0 / scaleX;
            double modelToSourceY = 1.0 / scaleY;
            double sourceToActual = ratio > 0 && ratio < 1.0 ? 1.0 / ratio : 1.0;
            PreprocessCoordinateCache coordinateCache = GetOrCreatePreprocessCoordinateCache(
                width,
                height,
                sourceX,
                sourceY,
                sourceWidth,
                sourceHeight,
                ratio,
                quality,
                scaleX,
                scaleY,
                resizedWidth,
                resizedHeight,
                padX,
                padY,
                modelToSourceX,
                modelToSourceY,
                sourceToActual);

            for (int y = 0; y < _inputHeight; y++)
            {
                if (y < padY || y >= padY + resizedHeight)
                    continue;

                int y0 = coordinateCache.Y0[y];
                byte* row0 = src + y0 * stride;
                if (quality == DownscaleQuality.FastNearest)
                {
                    for (int x = padX; x < padX + resizedWidth; x++)
                    {
                        byte* pixel = row0 + coordinateCache.X0ByteOffset[x];
                        int pixelOffset = y * _inputWidth + x;
                        values[redPlaneOffset + pixelOffset] = pixel[2] * inputScale;
                        values[greenPlaneOffset + pixelOffset] = pixel[1] * inputScale;
                        values[bluePlaneOffset + pixelOffset] = pixel[0] * inputScale;
                    }
                }
                else
                {
                    byte* row1 = src + coordinateCache.Y1[y] * stride;
                    double wy0 = coordinateCache.Wy0[y];
                    double wy1 = coordinateCache.Wy1[y];
                    for (int x = padX; x < padX + resizedWidth; x++)
                    {
                        (float b, float g, float r) = ReadBilinear(
                            row0,
                            row1,
                            coordinateCache.X0ByteOffset[x],
                            coordinateCache.X1ByteOffset[x],
                            coordinateCache.Wx0[x],
                            coordinateCache.Wx1[x],
                            wy0,
                            wy1);
                        int pixelOffset = y * _inputWidth + x;
                        values[redPlaneOffset + pixelOffset] = r * inputScale;
                        values[greenPlaneOffset + pixelOffset] = g * inputScale;
                        values[bluePlaneOffset + pixelOffset] = b * inputScale;
                    }
                }
            }

            return new ResizeTransform((float)scaleX, (float)scaleY, padX, padY);
        }

        private PreprocessCoordinateCache GetOrCreatePreprocessCoordinateCache(
            int width,
            int height,
            int sourceX,
            int sourceY,
            int sourceWidth,
            int sourceHeight,
            double ratio,
            DownscaleQuality quality,
            double scaleX,
            double scaleY,
            int resizedWidth,
            int resizedHeight,
            int padX,
            int padY,
            double modelToSourceX,
            double modelToSourceY,
            double sourceToActual)
        {
            var key = new PreprocessCoordinateCacheKey(
                width,
                height,
                sourceX,
                sourceY,
                sourceWidth,
                sourceHeight,
                ratio,
                quality,
                _inputWidth,
                _inputHeight,
                _options.UseLetterboxResize,
                _options.CenterLetterboxPadding,
                scaleX,
                scaleY,
                resizedWidth,
                resizedHeight,
                padX,
                padY);
            for (int i = 0; i < _preprocessCoordinateCaches.Count; i++)
            {
                var cached = _preprocessCoordinateCaches[i];
                if (cached.Key != key)
                    continue;

                if (i > 0)
                {
                    _preprocessCoordinateCaches.RemoveAt(i);
                    _preprocessCoordinateCaches.Insert(0, cached);
                }

                return cached;
            }

            var x0ByteOffset = new int[_inputWidth];
            var x1ByteOffset = new int[_inputWidth];
            var wx0 = new double[_inputWidth];
            var wx1 = new double[_inputWidth];
            for (int x = padX; x < padX + resizedWidth; x++)
            {
                double srcX = (sourceX + (x - padX) * modelToSourceX) * sourceToActual;
                int sourceX0 = Math.Clamp((int)Math.Floor(srcX), 0, width - 1);
                double fx = srcX - sourceX0;
                x0ByteOffset[x] = sourceX0 * 4;
                x1ByteOffset[x] = Math.Min(sourceX0 + 1, width - 1) * 4;
                wx0[x] = 1.0 - fx;
                wx1[x] = fx;
            }

            var y0 = new int[_inputHeight];
            var y1 = new int[_inputHeight];
            var wy0 = new double[_inputHeight];
            var wy1 = new double[_inputHeight];
            for (int y = padY; y < padY + resizedHeight; y++)
            {
                double srcY = (sourceY + (y - padY) * modelToSourceY) * sourceToActual;
                int sourceY0 = Math.Clamp((int)Math.Floor(srcY), 0, height - 1);
                double fy = srcY - sourceY0;
                y0[y] = sourceY0;
                y1[y] = Math.Min(sourceY0 + 1, height - 1);
                wy0[y] = 1.0 - fy;
                wy1[y] = fy;
            }

            var created = new PreprocessCoordinateCache(
                key,
                x0ByteOffset,
                x1ByteOffset,
                wx0,
                wx1,
                y0,
                y1,
                wy0,
                wy1);
            if (_preprocessCoordinateCaches.Count >= PreprocessCoordinateCacheCapacity)
                _preprocessCoordinateCaches.RemoveAt(_preprocessCoordinateCaches.Count - 1);
            _preprocessCoordinateCaches.Insert(0, created);
            return created;
        }

        private static unsafe (float B, float G, float R) ReadBilinear(
            byte* row0,
            byte* row1,
            int x0ByteOffset,
            int x1ByteOffset,
            double wx0,
            double wx1,
            double wy0,
            double wy1)
        {
            byte* p00 = row0 + x0ByteOffset;
            byte* p10 = row0 + x1ByteOffset;
            byte* p01 = row1 + x0ByteOffset;
            byte* p11 = row1 + x1ByteOffset;
            return (
                (float)(p00[0] * wx0 * wy0 + p10[0] * wx1 * wy0 + p01[0] * wx0 * wy1 + p11[0] * wx1 * wy1),
                (float)(p00[1] * wx0 * wy0 + p10[1] * wx1 * wy0 + p01[1] * wx0 * wy1 + p11[1] * wx1 * wy1),
                (float)(p00[2] * wx0 * wy0 + p10[2] * wx1 * wy0 + p01[2] * wx0 * wy1 + p11[2] * wx1 * wy1));
        }

        private static IReadOnlyList<Candidate> ApplyNms(IReadOnlyList<Candidate> candidates, float threshold, int maxDetections)
        {
            var ordered = candidates.OrderByDescending(c => c.Score).ToArray();
            var suppressed = new bool[ordered.Length];
            var kept = new List<Candidate>(Math.Min(Math.Max(0, maxDetections), ordered.Length));
            for (int i = 0; i < ordered.Length && kept.Count < maxDetections; i++)
            {
                if (suppressed[i])
                    continue;

                var current = ordered[i];
                kept.Add(current);
                for (int j = i + 1; j < ordered.Length; j++)
                {
                    if (!suppressed[j] && IoU(current, ordered[j]) > threshold)
                        suppressed[j] = true;
                }
            }

            return kept;
        }

        private IReadOnlyList<Candidate> ApplyLowConfidencePositionFilter(IReadOnlyList<Candidate> candidates, int width, int height)
        {
            if (!_options.UseLowConfidencePositionFilter || candidates.Count == 0)
                return candidates;

            float maxConfidence = Math.Clamp(_options.LowConfidencePositionMaxConfidence, 0.0f, 1.0f);
            double minCenterYRatio = Math.Clamp(_options.LowConfidencePositionMinCenterYRatio, 0.0, 1.0);
            double safeHeight = Math.Max(1.0, height);
            var kept = new List<Candidate>(candidates.Count);
            int dropped = 0;
            foreach (var candidate in candidates)
            {
                double centerYRatio = (candidate.Y + candidate.Height * 0.5f) / safeHeight;
                if (candidate.Score <= maxConfidence && centerYRatio >= minCenterYRatio)
                {
                    dropped++;
                    continue;
                }

                kept.Add(candidate);
            }

            if (_options.DumpDebug)
            {
                Trace.WriteLine(
                    $"[YoloLowConfidencePositionFilter] candidates={candidates.Count}, kept={kept.Count}, dropped={dropped}, maxConfidence={maxConfidence:0.###}, minCenterY={minCenterYRatio:0.###}");
            }

            return kept;
        }

        private IReadOnlyList<Candidate> ApplySmallAreaFilter(IReadOnlyList<Candidate> candidates, int width, int height)
        {
            if (!_options.UseSmallAreaFilter || candidates.Count == 0)
                return candidates;

            double maxAreaRatio = Math.Clamp(_options.SmallAreaMaxAreaRatio, 0.0, 1.0);
            if (maxAreaRatio <= 0.0)
                return candidates;

            double frameArea = Math.Max(1.0, width * (double)height);
            var kept = new List<Candidate>(candidates.Count);
            int dropped = 0;
            foreach (var candidate in candidates)
            {
                double areaRatio = Math.Max(0.0, candidate.Width * candidate.Height) / frameArea;
                if (areaRatio <= maxAreaRatio)
                {
                    dropped++;
                    continue;
                }

                kept.Add(candidate);
            }

            if (_options.DumpDebug)
            {
                Trace.WriteLine(
                    $"[YoloSmallAreaFilter] candidates={candidates.Count}, kept={kept.Count}, dropped={dropped}, maxArea={maxAreaRatio:0.####}");
            }

            return kept;
        }

        private IReadOnlyList<Candidate> ApplyAspectRatioFilter(IReadOnlyList<Candidate> candidates)
        {
            if (!_options.UseAspectRatioFilter || candidates.Count == 0)
                return candidates;

            double minAspectRatio = Math.Clamp(_options.MinAspectRatio, 0.0, 10.0);
            double maxAspectRatio = Math.Clamp(_options.MaxAspectRatio, 0.0, 10.0);
            if (minAspectRatio <= 0.0 && maxAspectRatio <= 0.0)
                return candidates;
            if (maxAspectRatio <= 0.0)
                maxAspectRatio = double.MaxValue;
            if (minAspectRatio > maxAspectRatio)
                (minAspectRatio, maxAspectRatio) = (maxAspectRatio, minAspectRatio);

            var kept = new List<Candidate>(candidates.Count);
            int dropped = 0;
            foreach (var candidate in candidates)
            {
                if (!IsAspectRatioAllowed(candidate.Width, candidate.Height, minAspectRatio, maxAspectRatio))
                {
                    dropped++;
                    continue;
                }

                kept.Add(candidate);
            }

            if (_options.DumpDebug)
            {
                Trace.WriteLine(
                    $"[YoloAspectRatioFilter] candidates={candidates.Count}, kept={kept.Count}, dropped={dropped}, minAspect={minAspectRatio:0.###}, maxAspect={maxAspectRatio:0.###}");
            }

            return kept;
        }

        internal static bool IsAspectRatioAllowed(double width, double height, double minAspectRatio, double maxAspectRatio)
        {
            if (minAspectRatio <= 0.0 && maxAspectRatio <= 0.0)
                return true;
            if (maxAspectRatio <= 0.0)
                maxAspectRatio = double.MaxValue;
            if (minAspectRatio > maxAspectRatio)
                (minAspectRatio, maxAspectRatio) = (maxAspectRatio, minAspectRatio);

            double aspectRatio = height > 0.0 ? width / height : 0.0;
            return aspectRatio >= minAspectRatio && aspectRatio <= maxAspectRatio;
        }

        private IReadOnlyList<Candidate> ApplyTopSmallLowConfidenceFilter(IReadOnlyList<Candidate> candidates, int width, int height)
        {
            if (!_options.UseTopSmallLowConfidenceFilter || candidates.Count == 0)
                return candidates;

            double maxCenterYRatio = Math.Clamp(_options.TopSmallLowConfidenceMaxCenterYRatio, 0.0, 1.0);
            double maxAreaRatio = Math.Clamp(_options.TopSmallLowConfidenceMaxAreaRatio, 0.0, 1.0);
            float maxConfidence = Math.Clamp(_options.TopSmallLowConfidenceMaxConfidence, 0.0f, 1.0f);
            if (maxCenterYRatio <= 0.0 || maxAreaRatio <= 0.0 || maxConfidence <= 0.0f)
                return candidates;

            double safeArea = Math.Max(1.0, width * (double)height);
            double safeHeight = Math.Max(1.0, height);
            var kept = new List<Candidate>(candidates.Count);
            int dropped = 0;
            foreach (var candidate in candidates)
            {
                double centerYRatio = (candidate.Y + candidate.Height * 0.5f) / safeHeight;
                double areaRatio = Math.Max(0.0, candidate.Width * candidate.Height) / safeArea;
                if (IsTopSmallLowConfidenceCandidate(
                        candidate.Score,
                        centerYRatio,
                        areaRatio,
                        maxConfidence,
                        maxCenterYRatio,
                        maxAreaRatio))
                {
                    dropped++;
                    continue;
                }

                kept.Add(candidate);
            }

            if (_options.DumpDebug)
            {
                Trace.WriteLine(
                    $"[YoloTopSmallLowConfidenceFilter] candidates={candidates.Count}, kept={kept.Count}, dropped={dropped}, maxConfidence={maxConfidence:0.###}, maxCenterY={maxCenterYRatio:0.###}, maxArea={maxAreaRatio:0.####}");
            }

            return kept;
        }

        internal static bool IsTopSmallLowConfidenceCandidate(
            double confidence,
            double centerYRatio,
            double areaRatio,
            double maxConfidence,
            double maxCenterYRatio,
            double maxAreaRatio)
        {
            if (maxConfidence <= 0.0 || maxCenterYRatio <= 0.0 || maxAreaRatio <= 0.0)
                return false;

            return confidence <= maxConfidence &&
                centerYRatio <= maxCenterYRatio &&
                areaRatio <= maxAreaRatio;
        }

        private IReadOnlyList<Candidate> RefineLargeBoxes(IReadOnlyList<Candidate> candidates, int width, int height)
        {
            bool useScaleRefine =
                _options.LargeBoxMinAreaRatio > 0 &&
                (_options.LargeBoxWidthScale < 0.999f || _options.LargeBoxHeightScale < 0.999f);
            bool useLandmarkRefine =
                _options.UseYolo5LandmarkBoxRefine &&
                _options.Yolo5LandmarkBoxMinAreaRatio > 0;
            if (candidates.Count == 0 ||
                _options.ModelType != YoloFaceModelType.Yolo5Face ||
                (!useScaleRefine && !useLandmarkRefine))
            {
                return candidates;
            }

            double frameArea = Math.Max(1.0, width * (double)height);
            float widthScale = Math.Clamp(_options.LargeBoxWidthScale, 0.50f, 1.0f);
            float heightScale = Math.Clamp(_options.LargeBoxHeightScale, 0.50f, 1.0f);
            var refined = new List<Candidate>(candidates.Count);
            int landmarkEligible = 0;
            int landmarkApplied = 0;
            foreach (var candidate in candidates)
            {
                var current = candidate;
                double areaRatio = candidate.Width * candidate.Height / frameArea;
                if (useLandmarkRefine && areaRatio >= _options.Yolo5LandmarkBoxMinAreaRatio)
                {
                    landmarkEligible++;
                    if (TryRefineFromLandmarks(current, width, height, out var landmarkRefined))
                    {
                        current = landmarkRefined;
                        landmarkApplied++;
                    }
                }

                if (useScaleRefine && areaRatio >= _options.LargeBoxMinAreaRatio)
                    current = ScaleCandidate(current, widthScale, heightScale, width, height);

                refined.Add(current);
            }

            if (_options.DumpDebug && useLandmarkRefine)
            {
                Trace.WriteLine(
                    $"[YoloLandmarkBoxRefine] candidates={candidates.Count}, eligible={landmarkEligible}, applied={landmarkApplied}, minAreaRatio={_options.Yolo5LandmarkBoxMinAreaRatio:0.###}, widthScale={_options.Yolo5LandmarkBoxWidthScale:0.###}, heightScale={_options.Yolo5LandmarkBoxHeightScale:0.###}, centerYOffset={_options.Yolo5LandmarkBoxCenterYOffsetRatio:0.###}");
            }

            return refined;
        }

        private bool TryRefineFromLandmarks(Candidate candidate, int width, int height, out Candidate refined)
        {
            refined = candidate;
            if (!candidate.Landmarks.HasValue)
                return false;

            var landmarks = candidate.Landmarks.Value;
            float landmarkWidth = landmarks.Width;
            float landmarkHeight = landmarks.Height;
            if (landmarkWidth < 4 || landmarkHeight < 4)
                return false;

            float targetWidth = Math.Clamp(
                landmarkWidth * Math.Clamp(_options.Yolo5LandmarkBoxWidthScale, 1.10f, 3.50f),
                candidate.Width * 0.45f,
                candidate.Width * 1.05f);
            float targetHeight = Math.Clamp(
                landmarkHeight * Math.Clamp(_options.Yolo5LandmarkBoxHeightScale, 1.10f, 3.80f),
                candidate.Height * 0.45f,
                candidate.Height * 1.05f);
            float cx = landmarks.CenterX;
            float cy = landmarks.CenterY + targetHeight * Math.Clamp(_options.Yolo5LandmarkBoxCenterYOffsetRatio, -0.35f, 0.35f);
            float x1 = Math.Clamp(cx - targetWidth * 0.5f, 0, Math.Max(0, width - 1));
            float y1 = Math.Clamp(cy - targetHeight * 0.5f, 0, Math.Max(0, height - 1));
            float x2 = Math.Clamp(cx + targetWidth * 0.5f, 0, Math.Max(0, width - 1));
            float y2 = Math.Clamp(cy + targetHeight * 0.5f, 0, Math.Max(0, height - 1));
            float w = Math.Max(0, x2 - x1);
            float h = Math.Max(0, y2 - y1);
            if (w < 2 || h < 2)
                return false;

            var candidateRefined = new Candidate(x1, y1, w, h, candidate.Score, candidate.Landmarks);
            if (IoU(candidate, candidateRefined) < Math.Clamp(_options.Yolo5LandmarkBoxMinOriginalIou, 0.0f, 0.95f))
                return false;

            refined = candidateRefined;
            return true;
        }

        private static Candidate ScaleCandidate(Candidate candidate, float widthScale, float heightScale, int width, int height)
        {
            float cx = candidate.X + candidate.Width * 0.5f;
            float cy = candidate.Y + candidate.Height * 0.5f;
            float scaledWidth = Math.Max(2.0f, candidate.Width * widthScale);
            float scaledHeight = Math.Max(2.0f, candidate.Height * heightScale);
            float x1 = Math.Clamp(cx - scaledWidth * 0.5f, 0, Math.Max(0, width - 1));
            float y1 = Math.Clamp(cy - scaledHeight * 0.5f, 0, Math.Max(0, height - 1));
            float x2 = Math.Clamp(cx + scaledWidth * 0.5f, 0, Math.Max(0, width - 1));
            float y2 = Math.Clamp(cy + scaledHeight * 0.5f, 0, Math.Max(0, height - 1));
            float w = Math.Max(0, x2 - x1);
            float h = Math.Max(0, y2 - y1);
            return w < 2 || h < 2
                ? candidate
                : new Candidate(x1, y1, w, h, candidate.Score, candidate.Landmarks);
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

        private SessionOptions CreateSessionOptions()
        {
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

            return sessionOptions;
        }

        private static int ResolveInputDimension(IReadOnlyList<int> dims, int index, int fallback, int? configured)
        {
            if (dims.Count > index && dims[index] > 0)
                return dims[index];
            return configured.GetValueOrDefault(fallback);
        }

        private static string? TryAppendGpuExecutionProvider(SessionOptions options)
        {
            if (OperatingSystem.IsMacOS())
            {
                if (TryAppendExecutionProvider(options, "AppendExecutionProvider_CoreML", "Microsoft.ML.OnnxRuntime"))
                {
                    UpdateExecutionProviderLabel("CoreML(default)");
                    UpdateExecutionProviderError(null);
                    return "CoreML";
                }

                return null;
            }

            if (OperatingSystem.IsWindows())
            {
                if (TryAppendExecutionProvider(options, "AppendExecutionProvider_DML", "Microsoft.ML.OnnxRuntime.DirectML"))
                {
                    options.EnableMemoryPattern = false;
                    options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                    UpdateExecutionProviderLabel("GPU:DirectML");
                    UpdateExecutionProviderError(null);
                    return "DirectML";
                }

                UpdateExecutionProviderLabel("CPU(DirectML 로드 실패)");
                if (GetLastExecutionProviderError() == null)
                    UpdateExecutionProviderError(BuildDirectMlDiagnostics());
                return null;
            }

            return null;
        }

        private static bool TryAppendExecutionProvider(SessionOptions options, string methodName, string assemblyName)
        {
            TryLoadAssemblyFromBaseDir(assemblyName);
            TryLoadAssembly(assemblyName);

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
                                        : pType.IsEnum
                                            ? Enum.ToObject(pType, 0)
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
                                : pType.IsEnum
                                    ? Enum.ToObject(pType, 0)
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
            string onnxRuntime = Path.Combine(baseDir, "onnxruntime.dll");
            string managed = Path.Combine(baseDir, "Microsoft.ML.OnnxRuntime.dll");
            string sharedProvider = Path.Combine(baseDir, "onnxruntime_providers_shared.dll");

            bool hasManaged = File.Exists(managed);
            bool hasOnnx = File.Exists(onnxRuntime);
            bool hasShared = File.Exists(sharedProvider);

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

        private readonly record struct Candidate(float X, float Y, float Width, float Height, float Score, LandmarkBounds? Landmarks);

        private readonly record struct LandmarkBounds(float MinX, float MinY, float MaxX, float MaxY, int Count)
        {
            public float Width => Math.Max(0, MaxX - MinX);

            public float Height => Math.Max(0, MaxY - MinY);

            public float CenterX => MinX + Width * 0.5f;

            public float CenterY => MinY + Height * 0.5f;

            public LandmarkBounds Offset(float x, float y)
            {
                return new LandmarkBounds(MinX + x, MinY + y, MaxX + x, MaxY + y, Count);
            }

            public LandmarkBounds Scale(float scale)
            {
                return new LandmarkBounds(MinX * scale, MinY * scale, MaxX * scale, MaxY * scale, Count);
            }
        }

        private readonly record struct ResizeTransform(float ScaleX, float ScaleY, float PadX, float PadY);

        private readonly record struct TensorPaddingLayout(
            int PadX,
            int PadY,
            int ResizedWidth,
            int ResizedHeight,
            float PaddingValue);

        private readonly record struct PreprocessCoordinateCacheKey(
            int FrameWidth,
            int FrameHeight,
            int SourceX,
            int SourceY,
            int SourceWidth,
            int SourceHeight,
            double Ratio,
            DownscaleQuality Quality,
            int InputWidth,
            int InputHeight,
            bool UseLetterboxResize,
            bool CenterLetterboxPadding,
            double ScaleX,
            double ScaleY,
            int ResizedWidth,
            int ResizedHeight,
            int PadX,
            int PadY);

        private sealed record PreprocessCoordinateCache(
            PreprocessCoordinateCacheKey Key,
            int[] X0ByteOffset,
            int[] X1ByteOffset,
            double[] Wx0,
            double[] Wx1,
            int[] Y0,
            int[] Y1,
            double[] Wy0,
            double[] Wy1);

        private readonly record struct OutputTensor
        {
            public OutputTensor(string name, Tensor<float> tensor)
            {
                Name = name;
                Dimensions = tensor.Dimensions.ToArray();
                Values = tensor is DenseTensor<float> dense
                    ? dense.Buffer
                    : tensor.ToArray();
            }

            public string Name { get; }

            public int[] Dimensions { get; }

            public ReadOnlyMemory<float> Values { get; }
        }
    }
}
