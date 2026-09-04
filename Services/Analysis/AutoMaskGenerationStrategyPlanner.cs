using FaceShield.Services.FaceDetection;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace FaceShield.Services.Analysis;

internal enum AutoMaskGenerationStrategyKind
{
    Sequential,
    PipelinedSingle,
    PipelinedParallel,
    SparsePipelinedParallel
}

internal static class AutoMaskGenerationStrategyPlanner
{
    internal static AutoMaskGenerationStrategyKind Resolve(
        AutoMaskOptions options,
        IFaceDetector detector,
        IFaceDetectorFactory? detectorFactory)
    {
        if (options == null)
            throw new ArgumentNullException(nameof(options));
        if (detector == null)
            throw new ArgumentNullException(nameof(detector));

        if (options.DetectEveryNFrames <= 1 && detector is IBgraFaceDetector)
        {
            return detectorFactory != null && options.ParallelDetectorCount > 1
                ? AutoMaskGenerationStrategyKind.PipelinedParallel
                : AutoMaskGenerationStrategyKind.PipelinedSingle;
        }

        if (options.UseTracking &&
            options.DetectEveryNFrames > 1 &&
            detector is IBgraFaceDetector &&
            detectorFactory != null &&
            options.ParallelDetectorCount >= 1)
        {
            return AutoMaskGenerationStrategyKind.SparsePipelinedParallel;
        }

        return AutoMaskGenerationStrategyKind.Sequential;
    }

    internal static AutoMaskDetectorPool CreateCompatibleDetectorPool(
        IBgraFaceDetector primary,
        IFaceDetectorFactory factory,
        int requestedCount)
    {
        if (primary == null)
            throw new ArgumentNullException(nameof(primary));
        if (factory == null)
            throw new ArgumentNullException(nameof(factory));

        int targetCount = Math.Max(1, requestedCount);
        var detectors = new List<IBgraFaceDetector>(targetCount)
        {
            primary
        };

        for (int i = 1; i < targetCount; i++)
        {
            IFaceDetector candidate = factory.CreateDetector();
            if (candidate is not IBgraFaceDetector bgraCandidate)
            {
                candidate.Dispose();
                break;
            }

            if (!DetectorExecutionProviderIdentity.AreCompatible(primary, candidate))
            {
                Debug.WriteLine(
                    $"[AutoMask] parallel detector provider mismatch; expected={DetectorExecutionProviderIdentity.GetCanonicalLabel(primary)}, actual={DetectorExecutionProviderIdentity.GetCanonicalLabel(candidate)}, usingSessions={detectors.Count}");
                candidate.Dispose();
                break;
            }

            detectors.Add(bgraCandidate);
        }

        return new AutoMaskDetectorPool(detectors);
    }
}

internal sealed class AutoMaskDetectorPool : IDisposable
{
    private readonly IReadOnlyList<IBgraFaceDetector> _detectors;
    private bool _disposed;

    internal AutoMaskDetectorPool(IReadOnlyList<IBgraFaceDetector> detectors)
    {
        _detectors = detectors ?? throw new ArgumentNullException(nameof(detectors));
        if (_detectors.Count == 0)
            throw new ArgumentException("At least one detector is required.", nameof(detectors));
    }

    internal IReadOnlyList<IBgraFaceDetector> Detectors => _detectors;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        // Index 0 is the primary detector owned by AutoMaskGenerator's caller.
        for (int i = 1; i < _detectors.Count; i++)
            _detectors[i].Dispose();
    }
}
