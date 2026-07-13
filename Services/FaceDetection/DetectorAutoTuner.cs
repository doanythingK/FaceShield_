using Avalonia;
using FaceShield.Services.Analysis;
using FaceShield.Services.Video;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FaceShield.Services.FaceDetection
{
    public static class DetectorAutoTuner
    {
        private const double GpuQualityMinBestIou = 0.75;
        private const double GpuPreferenceMinScoreRatio = 1.20;
        private const int QualityProbeFrameLimit = 12;
        private const int PerformanceProbeFrameLimit = 3;

        private readonly record struct AutoTuneKey(
            int Width,
            int Height,
            string SourceIdentity,
            int MaxSessions,
            DownscaleQuality Quality,
            bool UseOrtOptimization,
            bool AllowGpu,
            float? DetectionThreshold,
            float? ConfidenceThreshold,
            float? NmsThreshold,
            int? IntraOpNumThreads,
            int? InterOpNumThreads,
            bool? UseParallelExecution,
            bool? EnablePreprocessParallelism);

        private readonly record struct AutoTuneResult(
            FaceOnnxDetectorOptions Options,
            int Sessions,
            string Label);

        private static readonly ConcurrentDictionary<AutoTuneKey, AutoTuneResult> Cache = new();

        public static bool TryTune(
            string videoPath,
            double downscaleRatio,
            DownscaleQuality downscaleQuality,
            FaceOnnxDetectorOptions baseOptions,
            int maxSessions,
            bool allowGpuAuto,
            CancellationToken cancellationToken,
            out FaceOnnxDetectorOptions tunedOptions,
            out int tunedSessions,
            out string? label)
        {
            try
            {
                return TryTuneCore(
                    videoPath,
                    downscaleRatio,
                    downscaleQuality,
                    baseOptions,
                    maxSessions,
                    allowGpuAuto,
                    cancellationToken,
                    out tunedOptions,
                    out tunedSessions,
                    out label);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                tunedOptions = baseOptions;
                tunedSessions = Math.Max(1, maxSessions);
                label = null;
                Debug.WriteLine($"[AutoTune] skipped after failure: {ex.Message}");
                return false;
            }
        }

        private static bool TryTuneCore(
            string videoPath,
            double downscaleRatio,
            DownscaleQuality downscaleQuality,
            FaceOnnxDetectorOptions baseOptions,
            int maxSessions,
            bool allowGpuAuto,
            CancellationToken cancellationToken,
            out FaceOnnxDetectorOptions tunedOptions,
            out int tunedSessions,
            out string? label)
        {
            tunedOptions = baseOptions;
            tunedSessions = Math.Max(1, maxSessions);
            label = null;

            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(videoPath) || maxSessions < 1)
                return false;

            using var extractor = new FfFrameExtractor(videoPath, enableHardware: true);
            extractor.StartSequentialRead(0);

            PixelSize frameSize = extractor.FrameSize;
            int width = frameSize.Width;
            int height = frameSize.Height;
            if (width <= 0 || height <= 0)
                return false;

            bool useProxy = downscaleRatio > 0 && downscaleRatio < 1.0;
            int targetWidth = useProxy ? Math.Max(1, (int)Math.Round(width * downscaleRatio)) : width;
            int targetHeight = useProxy ? Math.Max(1, (int)Math.Round(height * downscaleRatio)) : height;
            double detectorRatio = useProxy ? 1.0 : downscaleRatio;
            bool allowGpu = allowGpuAuto || baseOptions.UseGpu;
            var key = new AutoTuneKey(
                targetWidth,
                targetHeight,
                BuildCacheSourceIdentity(videoPath, allowGpu),
                maxSessions,
                downscaleQuality,
                baseOptions.UseOrtOptimization,
                allowGpu,
                baseOptions.DetectionThreshold,
                baseOptions.ConfidenceThreshold,
                baseOptions.NmsThreshold,
                baseOptions.IntraOpNumThreads,
                baseOptions.InterOpNumThreads,
                baseOptions.UseParallelExecution,
                baseOptions.EnablePreprocessParallelism);
            if (Cache.TryGetValue(key, out var cached))
            {
                tunedOptions = cached.Options;
                tunedSessions = cached.Sessions;
                label = cached.Label;
                Debug.WriteLine($"[AutoTune] cache hit: {label}");
                return true;
            }

            int stride;
            int frameIndex;
            int size = Math.Max(1, targetWidth * targetHeight * 4);
            var pool = ArrayPool<byte>.Shared;
            byte[] buffer = pool.Rent(size);
            byte[] qualityBuffer = pool.Rent(size);
            var performanceSamples = new List<(byte[] Buffer, int Stride)>(PerformanceProbeFrameLimit);
            var rentedPerformanceBuffers = new List<byte[]>(PerformanceProbeFrameLimit - 1);
            try
            {
                if (!extractor.TryGetNextFrameRawToBuffer(
                        cancellationToken,
                        targetWidth,
                        targetHeight,
                        downscaleQuality == DownscaleQuality.BalancedBilinear,
                        buffer,
                        out frameIndex,
                        out stride))
                {
                    return false;
                }

                Array.Copy(buffer, 0, qualityBuffer, 0, size);
                performanceSamples.Add((buffer, stride));
                int qualityStride = stride;
                var qualityCpuOptions = CloneOptions(
                    baseOptions,
                    baseOptions.IntraOpNumThreads,
                    baseOptions.InterOpNumThreads,
                    useGpu: false,
                    enablePreprocessParallelism: baseOptions.EnablePreprocessParallelism ?? true,
                    useParallelExecution: baseOptions.UseParallelExecution == true);
                TryPromoteQualitySample(
                    extractor,
                    cancellationToken,
                    targetWidth,
                    targetHeight,
                    downscaleQuality == DownscaleQuality.BalancedBilinear,
                    detectorRatio,
                    downscaleQuality,
                    qualityCpuOptions,
                    qualityBuffer,
                    size,
                    ref qualityStride);
                CollectPerformanceSamples(
                    extractor,
                    cancellationToken,
                    targetWidth,
                    targetHeight,
                    downscaleQuality == DownscaleQuality.BalancedBilinear,
                    pool,
                    size,
                    performanceSamples,
                    rentedPerformanceBuffers);

                var candidates = BuildCandidates(baseOptions, maxSessions, allowGpu);
                IReadOnlyList<FaceDetectionResult>? cpuReference = null;
                double bestCpuScore = 0;
                FaceOnnxDetectorOptions? bestCpuOptions = null;
                int bestCpuSessions = Math.Max(1, maxSessions);
                string? bestCpuLabel = null;
                double bestGpuScore = 0;
                FaceOnnxDetectorOptions? bestGpuOptions = null;
                int bestGpuSessions = Math.Max(1, maxSessions);
                string? bestGpuLabel = null;

                unsafe
                {
                    fixed (byte* qualitySrc = qualityBuffer)
                    {
                        foreach (var candidate in candidates)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (candidate.Options.UseGpu)
                            {
                                cpuReference ??= DetectOnce(
                                    (IntPtr)qualitySrc,
                                    qualityStride,
                                    targetWidth,
                                    targetHeight,
                                    detectorRatio,
                                    downscaleQuality,
                                    CloneOptions(
                                        baseOptions,
                                        candidate.Options.IntraOpNumThreads,
                                        candidate.Options.InterOpNumThreads,
                                        useGpu: false,
                                        enablePreprocessParallelism: candidate.Options.EnablePreprocessParallelism ?? true,
                                        useParallelExecution: candidate.Options.UseParallelExecution == true));

                                var gpuReference = DetectOnce(
                                    (IntPtr)qualitySrc,
                                    qualityStride,
                                    targetWidth,
                                    targetHeight,
                                    detectorRatio,
                                    downscaleQuality,
                                    candidate.Options,
                                    out string? gpuProviderLabel);

                                if (!IsActiveGpuProvider(gpuProviderLabel))
                                {
                                    Debug.WriteLine($"[AutoTune] skip {candidate.Label}, GPU provider was not active");
                                    continue;
                                }

                                if (!IsQualityCompatible(cpuReference, gpuReference))
                                {
                                    Debug.WriteLine($"[AutoTune] skip {candidate.Label}, quality gate failed");
                                    continue;
                                }
                            }

                            double score;
                            try
                            {
                                score = MeasureThroughput(
                                    performanceSamples,
                                    targetWidth,
                                    targetHeight,
                                    detectorRatio,
                                    downscaleQuality,
                                    candidate.Options,
                                    candidate.Sessions,
                                    cancellationToken);
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                            {
                                throw;
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[AutoTune] skip {candidate.Label}, benchmark failed: {ex.Message}");
                                continue;
                            }

                            Debug.WriteLine($"[AutoTune] candidate={candidate.Label}, score={score:F2}/s");

                            if (candidate.Options.UseGpu && score > bestGpuScore)
                            {
                                bestGpuScore = score;
                                bestGpuOptions = candidate.Options;
                                bestGpuSessions = candidate.Sessions;
                                bestGpuLabel = candidate.Label;
                            }

                            if (!candidate.Options.UseGpu && score > bestCpuScore)
                            {
                                bestCpuScore = score;
                                bestCpuOptions = candidate.Options;
                                bestCpuSessions = candidate.Sessions;
                                bestCpuLabel = candidate.Label;
                            }
                        }
                    }
                }

                if (bestCpuScore <= 0 || bestCpuOptions == null)
                    return false;

                double bestScore = bestCpuScore;
                var bestOptions = bestCpuOptions;
                int bestSessions = bestCpuSessions;
                string? bestLabel = bestCpuLabel;
                if (allowGpu && bestGpuOptions != null &&
                    bestGpuScore >= bestCpuScore * GpuPreferenceMinScoreRatio)
                {
                    bestScore = bestGpuScore;
                    bestOptions = bestGpuOptions;
                    bestSessions = bestGpuSessions;
                    bestLabel = bestGpuLabel;
                }

                tunedOptions = bestOptions;
                tunedSessions = bestSessions;
                label = bestLabel;
                Cache[key] = new AutoTuneResult(tunedOptions, tunedSessions, label ?? "자동 튜닝");
                Debug.WriteLine($"[AutoTune] {label}, score={bestScore:F2}/s");
                return true;
            }
            finally
            {
                foreach (var rented in rentedPerformanceBuffers)
                    pool.Return(rented);
                pool.Return(buffer);
                pool.Return(qualityBuffer);
            }
        }

        private static void CollectPerformanceSamples(
            FfFrameExtractor extractor,
            CancellationToken cancellationToken,
            int targetWidth,
            int targetHeight,
            bool useBilinear,
            ArrayPool<byte> pool,
            int bufferSize,
            List<(byte[] Buffer, int Stride)> samples,
            List<byte[]> rentedBuffers)
        {
            while (samples.Count < PerformanceProbeFrameLimit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] sample = pool.Rent(bufferSize);
                if (!extractor.TryGetNextFrameRawToBuffer(
                        cancellationToken,
                        targetWidth,
                        targetHeight,
                        useBilinear,
                        sample,
                        out _,
                        out int sampleStride))
                {
                    pool.Return(sample);
                    return;
                }

                rentedBuffers.Add(sample);
                samples.Add((sample, sampleStride));
            }
        }

        private static unsafe void TryPromoteQualitySample(
            FfFrameExtractor extractor,
            CancellationToken cancellationToken,
            int targetWidth,
            int targetHeight,
            bool useBilinear,
            double detectorRatio,
            DownscaleQuality quality,
            FaceOnnxDetectorOptions cpuOptions,
            byte[] qualityBuffer,
            int bufferSize,
            ref int qualityStride)
        {
            fixed (byte* src = qualityBuffer)
            {
                var initialFaces = DetectOnce(
                    (IntPtr)src,
                    qualityStride,
                    targetWidth,
                    targetHeight,
                    detectorRatio,
                    quality,
                    cpuOptions);
                if (initialFaces != null && initialFaces.Count > 0)
                    return;
            }

            var pool = ArrayPool<byte>.Shared;
            byte[] probe = pool.Rent(bufferSize);
            try
            {
                for (int i = 0; i < QualityProbeFrameLimit; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!extractor.TryGetNextFrameRawToBuffer(
                            cancellationToken,
                            targetWidth,
                            targetHeight,
                            useBilinear,
                            probe,
                            out _,
                            out int probeStride))
                    {
                        return;
                    }

                    fixed (byte* src = probe)
                    {
                        var faces = DetectOnce(
                            (IntPtr)src,
                            probeStride,
                            targetWidth,
                            targetHeight,
                            detectorRatio,
                            quality,
                            cpuOptions);
                        if (faces == null || faces.Count == 0)
                            continue;
                    }

                    Array.Copy(probe, 0, qualityBuffer, 0, bufferSize);
                    qualityStride = probeStride;
                    return;
                }
            }
            finally
            {
                pool.Return(probe);
            }
        }

        private static IReadOnlyList<FaceDetectionResult>? DetectOnce(
            IntPtr data,
            int stride,
            int width,
            int height,
            double ratio,
            DownscaleQuality quality,
            FaceOnnxDetectorOptions options)
            => DetectOnce(data, stride, width, height, ratio, quality, options, out _);

        private static IReadOnlyList<FaceDetectionResult>? DetectOnce(
            IntPtr data,
            int stride,
            int width,
            int height,
            double ratio,
            DownscaleQuality quality,
            FaceOnnxDetectorOptions options,
            out string? providerLabel)
        {
            providerLabel = null;
            try
            {
                using var detector = new FaceOnnxDetector(options);
                providerLabel = detector.ExecutionProviderLabel;
                return detector.DetectFacesBgra(data, stride, width, height, ratio, quality);
            }
            catch
            {
                return null;
            }
        }

        private static bool IsQualityCompatible(
            IReadOnlyList<FaceDetectionResult>? reference,
            IReadOnlyList<FaceDetectionResult>? candidate)
        {
            if (reference == null || candidate == null)
                return false;

            if (reference.Count == 0)
                return false;

            if (reference.Count != candidate.Count)
                return false;

            var used = new bool[candidate.Count];
            for (int i = 0; i < reference.Count; i++)
            {
                double best = 0.0;
                int bestIndex = -1;
                for (int j = 0; j < candidate.Count; j++)
                {
                    if (used[j])
                        continue;

                    double iou = IoU(reference[i].Bounds, candidate[j].Bounds);
                    if (iou > best)
                    {
                        best = iou;
                        bestIndex = j;
                    }
                }

                if (bestIndex < 0 || best < GpuQualityMinBestIou)
                    return false;

                used[bestIndex] = true;
            }

            return true;
        }

        private static bool IsActiveGpuProvider(string? label)
            => !string.IsNullOrWhiteSpace(label) &&
                label.StartsWith("GPU:", StringComparison.OrdinalIgnoreCase);

        private static string BuildCacheSourceIdentity(string videoPath, bool requireSourceIdentity)
        {
            if (!requireSourceIdentity)
                return "shared-cpu-performance";

            try
            {
                string fullPath = Path.GetFullPath(videoPath).Trim();
                if (OperatingSystem.IsWindows())
                    fullPath = fullPath.ToUpperInvariant();
                var info = new FileInfo(fullPath);
                return $"{fullPath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            }
            catch
            {
                return $"source:{videoPath.Trim()}";
            }
        }

        private static double IoU(Rect a, Rect b)
        {
            double x0 = Math.Max(a.X, b.X);
            double y0 = Math.Max(a.Y, b.Y);
            double x1 = Math.Min(a.Right, b.Right);
            double y1 = Math.Min(a.Bottom, b.Bottom);
            double intersection = Math.Max(0.0, x1 - x0) * Math.Max(0.0, y1 - y0);
            if (intersection <= 0.0)
                return 0.0;

            double areaA = Math.Max(0.0, a.Width) * Math.Max(0.0, a.Height);
            double areaB = Math.Max(0.0, b.Width) * Math.Max(0.0, b.Height);
            double union = areaA + areaB - intersection;
            return union <= 0.0 ? 0.0 : intersection / union;
        }

        private static double MeasureThroughput(
            IReadOnlyList<(byte[] Buffer, int Stride)> samples,
            int width,
            int height,
            double ratio,
            DownscaleQuality quality,
            FaceOnnxDetectorOptions options,
            int sessions,
            CancellationToken cancellationToken)
        {
            if (samples.Count == 0 || sessions <= 0)
                return 0;

            cancellationToken.ThrowIfCancellationRequested();

            var detectors = new List<FaceOnnxDetector>(sessions);
            try
            {
                for (int i = 0; i < sessions; i++)
                {
                    var detector = new FaceOnnxDetector(options);
                    if (options.UseGpu && !detector.UsesGpuExecutionProvider)
                    {
                        detector.Dispose();
                        DisposeAll(detectors);
                        return 0;
                    }

                    detectors.Add(detector);
                }
            }
            catch
            {
                DisposeAll(detectors);
                return 0;
            }

            try
            {
                foreach (var detector in detectors)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    DetectSamples(detector, samples, width, height, ratio, quality, cancellationToken);
                }

                int iterations = Math.Max(1, options.UseGpu ? 3 : 2);
                var sw = Stopwatch.StartNew();
                Parallel.For(
                    0,
                    sessions,
                    new ParallelOptions { CancellationToken = cancellationToken },
                    i =>
                    {
                        var detector = detectors[i];
                        for (int k = 0; k < iterations; k++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            DetectSamples(detector, samples, width, height, ratio, quality, cancellationToken);
                        }
                    });
                sw.Stop();

                double seconds = Math.Max(0.0001, sw.Elapsed.TotalSeconds);
                return (sessions * iterations * samples.Count) / seconds;
            }
            finally
            {
                DisposeAll(detectors);
            }
        }

        private static unsafe void DetectSamples(
            FaceOnnxDetector detector,
            IReadOnlyList<(byte[] Buffer, int Stride)> samples,
            int width,
            int height,
            double ratio,
            DownscaleQuality quality,
            CancellationToken cancellationToken)
        {
            foreach (var sample in samples)
            {
                cancellationToken.ThrowIfCancellationRequested();
                fixed (byte* src = sample.Buffer)
                    detector.DetectFacesBgra((IntPtr)src, sample.Stride, width, height, ratio, quality);
            }
        }

        private static List<(FaceOnnxDetectorOptions Options, int Sessions, string Label)> BuildCandidates(
            FaceOnnxDetectorOptions baseOptions,
            int maxSessions,
            bool allowGpu)
        {
            int cores = Math.Max(1, Environment.ProcessorCount);
            var candidates = new List<(FaceOnnxDetectorOptions, int, string)>();
            int requestedSessions = Math.Max(1, maxSessions);
            for (int sessions = 1; sessions <= requestedSessions; sessions++)
            {
                var defaultCpuOptions = CloneOptions(
                    baseOptions,
                    baseOptions.IntraOpNumThreads,
                    baseOptions.InterOpNumThreads,
                    useGpu: false,
                    enablePreprocessParallelism: baseOptions.EnablePreprocessParallelism ?? true,
                    useParallelExecution: baseOptions.UseParallelExecution == true);
                candidates.Add((defaultCpuOptions, sessions, $"CPU {sessions}세션/default"));

                int perSession = Math.Max(1, cores / sessions);
                var threadCandidates = new SortedSet<int>
                {
                    perSession,
                    Math.Max(1, perSession / 2),
                    1
                };

                foreach (int threads in threadCandidates)
                {
                    var opts = CloneOptions(
                        baseOptions,
                        threads,
                        interThreads: baseOptions.InterOpNumThreads,
                        useGpu: false,
                        enablePreprocessParallelism: sessions <= 1,
                        useParallelExecution: baseOptions.UseParallelExecution == true);
                    candidates.Add((opts, sessions, $"CPU {sessions}세션/{threads}스레드"));

                    if (baseOptions.UseParallelExecution != false)
                    {
                        int interThreads = Math.Max(1, Math.Min(threads, Math.Max(1, Environment.ProcessorCount / Math.Max(1, sessions))));
                        var parallelOpts = CloneOptions(
                            baseOptions,
                            threads,
                            interThreads,
                            useGpu: false,
                            enablePreprocessParallelism: sessions <= 1,
                            useParallelExecution: true);
                        candidates.Add((parallelOpts, sessions, $"CPU {sessions}세션/{threads}스레드/ORT_PARALLEL({interThreads})"));
                    }
                }

                if (allowGpu || baseOptions.UseGpu)
                {
                    var opts = CloneOptions(
                        baseOptions,
                        perSession,
                        interThreads: baseOptions.InterOpNumThreads,
                        useGpu: true,
                        enablePreprocessParallelism: sessions <= 1,
                        useParallelExecution: false);
                    candidates.Add((opts, sessions, $"GPU {sessions}세션/{perSession}스레드"));
                }
            }

            return candidates;
        }

        private static FaceOnnxDetectorOptions CloneOptions(
            FaceOnnxDetectorOptions source,
            int? intraThreads,
            int? interThreads,
            bool useGpu,
            bool enablePreprocessParallelism,
            bool useParallelExecution)
        {
            return new FaceOnnxDetectorOptions
            {
                UseOrtOptimization = source.UseOrtOptimization,
                UseGpu = useGpu,
                IntraOpNumThreads = intraThreads,
                InterOpNumThreads = interThreads,
                UseParallelExecution = useParallelExecution,
                DetectionThreshold = source.DetectionThreshold,
                ConfidenceThreshold = source.ConfidenceThreshold,
                NmsThreshold = source.NmsThreshold,
                EnablePreprocessParallelism = enablePreprocessParallelism,
                AllowAutoTune = source.AllowAutoTune,
                AllowAutoGpu = source.AllowAutoGpu
            };
        }

        private static void DisposeAll(IEnumerable<IDisposable> disposables)
        {
            foreach (var disposable in disposables)
                disposable.Dispose();
        }
    }
}
