using Avalonia;
using FaceShield.Services.Analysis;
using FaceShield.Services.Video;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace FaceShield.Services.FaceDetection
{
    internal static class DetectorAutoTuner
    {
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
            int? InterOpNumThreads);

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
            out FaceOnnxDetectorOptions tunedOptions,
            out int tunedSessions,
            out string? label)
        {
            tunedOptions = baseOptions;
            tunedSessions = Math.Max(1, maxSessions);
            label = null;

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
                baseOptions.InterOpNumThreads);
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
            try
            {
                if (!extractor.TryGetNextFrameRawToBuffer(
                        default,
                        targetWidth,
                        targetHeight,
                        downscaleQuality == DownscaleQuality.BalancedBilinear,
                        buffer,
                        out frameIndex,
                        out stride))
                {
                    return false;
                }

                var candidates = BuildCandidates(baseOptions, maxSessions, allowGpu);
                double bestScore = 0;
                FaceOnnxDetectorOptions bestOptions = baseOptions;
                int bestSessions = Math.Max(1, maxSessions);
                string? bestLabel = null;

                unsafe
                {
                    fixed (byte* src = buffer)
                    {
                        foreach (var candidate in candidates)
                        {
                            double score = MeasureThroughput(
                                (IntPtr)src,
                                stride,
                                targetWidth,
                                targetHeight,
                                detectorRatio,
                                downscaleQuality,
                                candidate.Options,
                                candidate.Sessions);

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
            }
        }

        private static double MeasureThroughput(
            IntPtr data,
            int stride,
            int width,
            int height,
            double ratio,
            DownscaleQuality quality,
            FaceOnnxDetectorOptions options,
            int sessions)
        {
            if (data == IntPtr.Zero || sessions <= 0)
                return 0;

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
                    detector.DetectFacesBgra(data, stride, width, height, ratio, quality);

                int iterations = Math.Max(1, options.UseGpu ? 3 : 2);
                var sw = Stopwatch.StartNew();
                Parallel.For(0, sessions, i =>
                {
                    var detector = detectors[i];
                    for (int k = 0; k < iterations; k++)
                        detector.DetectFacesBgra(data, stride, width, height, ratio, quality);
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
            for (int sessions = 1; sessions <= Math.Max(1, maxSessions); sessions++)
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
                    var opts = CloneOptions(baseOptions, threads, useGpu: false, sessions <= 1);
                    candidates.Add((opts, sessions, $"CPU {sessions}세션/{threads}스레드"));
                }

                if (allowGpu || baseOptions.UseGpu)
                {
                    var opts = CloneOptions(baseOptions, perSession, useGpu: true, enablePreprocessParallelism: sessions <= 1);
                    candidates.Add((opts, sessions, $"GPU {sessions}세션/{perSession}스레드"));
                }
            }

            return candidates;
        }

        private static FaceOnnxDetectorOptions CloneOptions(
            FaceOnnxDetectorOptions source,
            int? intraThreads,
            bool useGpu,
            bool enablePreprocessParallelism)
        {
            return new FaceOnnxDetectorOptions
            {
                UseOrtOptimization = source.UseOrtOptimization,
                UseGpu = useGpu,
                IntraOpNumThreads = intraThreads,
                InterOpNumThreads = source.InterOpNumThreads,
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
