using Avalonia;
using FaceShield.Services.Analysis;
using FaceShield.Services.Video;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace FaceShield.Services.FaceDetection
{
    public static class DetectorAutoTuner
    {
        private const double GpuQualityMinBestIou = 0.75;
        private const double GpuPreferenceMinScoreRatio = 0.75;
        private const int QualityProbeFrameLimit = 12;

        private readonly record struct AutoTuneKey(
            int Width,
            int Height,
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
                return true;
            }

            int stride;
            int frameIndex;
            int size = Math.Max(1, targetWidth * targetHeight * 4);
            var pool = ArrayPool<byte>.Shared;
            byte[] buffer = pool.Rent(size);
            byte[] qualityBuffer = pool.Rent(size);
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

                var candidates = BuildCandidates(baseOptions, maxSessions, allowGpu);
                IReadOnlyList<FaceDetectionResult>? cpuReference = null;
                double bestScore = 0;
                FaceOnnxDetectorOptions bestOptions = baseOptions;
                int bestSessions = Math.Max(1, maxSessions);
                string? bestLabel = null;
                double bestGpuScore = 0;
                FaceOnnxDetectorOptions? bestGpuOptions = null;
                int bestGpuSessions = Math.Max(1, maxSessions);
                string? bestGpuLabel = null;

                unsafe
                {
                    fixed (byte* src = buffer)
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
                                    candidate.Options);

                                if (!IsQualityCompatible(cpuReference, gpuReference))
                                {
                                    Debug.WriteLine($"[AutoTune] skip {candidate.Label}, quality gate failed");
                                    continue;
                                }
                            }

                            double score = MeasureThroughput(
                                (IntPtr)src,
                                stride,
                                targetWidth,
                                targetHeight,
                                detectorRatio,
                                downscaleQuality,
                                candidate.Options,
                                candidate.Sessions,
                                cancellationToken);

                            if (candidate.Options.UseGpu && score > bestGpuScore)
                            {
                                bestGpuScore = score;
                                bestGpuOptions = candidate.Options;
                                bestGpuSessions = candidate.Sessions;
                                bestGpuLabel = candidate.Label;
                            }

                            if (score > bestScore)
                            {
                                bestScore = score;
                                bestOptions = candidate.Options;
                                bestSessions = candidate.Sessions;
                                bestLabel = candidate.Label;
                            }
                        }
                    }
                }

                if (bestScore <= 0)
                    return false;

                if (allowGpu && bestGpuOptions != null &&
                    bestGpuScore >= bestScore * GpuPreferenceMinScoreRatio)
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
                pool.Return(buffer);
                pool.Return(qualityBuffer);
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
        {
            try
            {
                using var detector = new FaceOnnxDetector(options);
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
                return candidate.Count == 0;

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
            IntPtr data,
            int stride,
            int width,
            int height,
            double ratio,
            DownscaleQuality quality,
            FaceOnnxDetectorOptions options,
            int sessions,
            CancellationToken cancellationToken)
        {
            if (data == IntPtr.Zero || sessions <= 0)
                return 0;

            cancellationToken.ThrowIfCancellationRequested();

            var detectors = new List<FaceOnnxDetector>(sessions);
            try
            {
                for (int i = 0; i < sessions; i++)
                    detectors.Add(new FaceOnnxDetector(options));
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
                    detector.DetectFacesBgra(data, stride, width, height, ratio, quality);
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
                            detector.DetectFacesBgra(data, stride, width, height, ratio, quality);
                        }
                    });
                sw.Stop();

                double seconds = Math.Max(0.0001, sw.Elapsed.TotalSeconds);
                return (sessions * iterations) / seconds;
            }
            finally
            {
                DisposeAll(detectors);
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
            for (int sessions = requestedSessions; sessions <= requestedSessions; sessions++)
            {
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
