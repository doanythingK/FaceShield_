using Avalonia;
using FaceShield.Services.Analysis;
using FaceShield.Services.Video;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace FaceShield.Services.FaceDetection;

internal static class DetectorAutoTuner
{
    private readonly record struct AutoTuneKey(
        int Width,
        int Height,
        int MaxSessions,
        bool UseOrtOptimization,
        bool AllowGpu);

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

        if (string.IsNullOrWhiteSpace(videoPath))
            return false;

        if (maxSessions < 1)
            return false;

        bool allowGpu = allowGpuAuto;
        if (FaceOnnxDetector.GetGpuAvailability() == false)
            allowGpu = false;

        using var extractor = new FfFrameExtractor(videoPath, enableHardware: true);
        extractor.StartSequentialRead(0);

        PixelSize frameSize = extractor.FrameSize;
        int width = frameSize.Width;
        int height = frameSize.Height;
        if (width <= 0 || height <= 0)
            return false;

        var key = new AutoTuneKey(width, height, maxSessions, baseOptions.UseOrtOptimization, allowGpu);
        if (Cache.TryGetValue(key, out var cached))
        {
            tunedOptions = cached.Options;
            tunedSessions = cached.Sessions;
            label = cached.Label;
            return true;
        }

        int stride;
        int frameIndex;
        int size = Math.Max(1, width * height * 4);
        var pool = System.Buffers.ArrayPool<byte>.Shared;
        var buffer = pool.Rent(size);
        try
        {
            if (!extractor.TryGetNextFrameRawToBuffer(
                    default,
                    width,
                    height,
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
                            width,
                            height,
                            downscaleRatio,
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
            foreach (var d in detectors)
                d.Dispose();
            return 0;
        }

        try
        {
            // warmup
            foreach (var d in detectors)
                d.DetectFacesBgra(data, stride, width, height, ratio, quality);

            int iterations = options.UseGpu ? 3 : 2;
            if (iterations < 1) iterations = 1;

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
            foreach (var d in detectors)
                d.Dispose();
        }
    }

    private static List<(FaceOnnxDetectorOptions Options, int Sessions, string Label)> BuildCandidates(
        FaceOnnxDetectorOptions baseOptions,
        int maxSessions,
        bool allowGpu)
    {
        int cores = Math.Max(1, Environment.ProcessorCount);
        var candidates = new List<(FaceOnnxDetectorOptions, int, string)>();

        int[] sessionCandidates = maxSessions <= 1
            ? new[] { 1 }
            : new[] { 1, maxSessions };

        foreach (int sessions in sessionCandidates)
        {
            int perSession = Math.Max(1, cores / sessions);
            int[] threadCandidates = { perSession, Math.Max(1, perSession / 2) };

            foreach (int threads in threadCandidates)
            {
                var opts = CloneOptions(baseOptions, threads, baseOptions.UseGpu, sessions <= 1);
                candidates.Add((opts, sessions, $"CPU {sessions}세션/{threads}스레드"));
            }

            if (allowGpu)
            {
                var gpuOpts = CloneOptions(baseOptions, perSession, useGpu: true, enablePreprocessParallelism: sessions <= 1);
                candidates.Add((gpuOpts, sessions, $"GPU {sessions}세션/{perSession}스레드"));
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
}
