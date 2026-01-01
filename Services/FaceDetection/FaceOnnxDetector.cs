// FILE: Services/FaceDetection/FaceOnnxDetector.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
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
        private static readonly object _statusLock = new();
        private static string _lastExecutionProviderLabel = "CPU";
        private static string? _lastExecutionProviderError;

        public FaceOnnxDetector()
        {
            _detector = new FaceDetector(); // 확실함
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
            using var temp = new FaceDetector();
            float detection = temp.DetectionThreshold;
            float confidence = temp.ConfidenceThreshold;
            float nms = temp.NmsThreshold;

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
                // Fallback to CPU if GPU/provider initialization fails.
                _detector = new FaceDetector(detection, confidence, nms);
                UpdateExecutionProviderLabel("CPU(가속 실패)");
                UpdateExecutionProviderError(ex.Message);
            }
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

            if (TryAppendExecutionProvider(options, "AppendExecutionProvider_DML", "Microsoft.ML.OnnxRuntime.DirectML"))
            {
                UpdateExecutionProviderLabel("GPU:DirectML");
                UpdateExecutionProviderError(null);
                return "DirectML";
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                UpdateExecutionProviderLabel("CPU(DirectML 미지원)");
                UpdateExecutionProviderError(null);
            }

            if (TryAppendExecutionProvider(options, "AppendExecutionProvider_CUDA", "Microsoft.ML.OnnxRuntime.Gpu"))
                return "CUDA";

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
                                object arg = parameters[1].ParameterType == typeof(uint) ? 0u : 0;
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
                        object arg = parameters[0].ParameterType == typeof(uint) ? 0u : 0;
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
