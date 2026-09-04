from pathlib import Path


def read_exact(path: str) -> str:
    with Path(path).open('r', encoding='utf-8', newline='') as f:
        return f.read()


def write_exact(path: str, text: str) -> None:
    p = Path(path)
    p.parent.mkdir(parents=True, exist_ok=True)
    with p.open('w', encoding='utf-8', newline='') as f:
        f.write(text)


def replace_once(path: str, old: str, new: str) -> None:
    text = read_exact(path)
    candidates = [(old, new)]
    if '\n' in old:
        candidates.append((old.replace('\n', '\r\n'), new.replace('\n', '\r\n')))
    for before, after in candidates:
        count = text.count(before)
        if count == 1:
            write_exact(path, text.replace(before, after, 1))
            return
        if count > 1:
            raise RuntimeError(f'Expected one match in {path}, found {count}: {old[:120]!r}')
    raise RuntimeError(f'Patch target not found in {path}: {old[:160]!r}')


def replace_between(path: str, start_marker: str, end_marker: str, replacement: str) -> None:
    text = read_exact(path)
    sep = '\r\n' if '\r\n' in text else '\n'
    start_key = start_marker.replace('\n', sep)
    end_key = end_marker.replace('\n', sep)
    start = text.find(start_key)
    if start < 0:
        raise RuntimeError(f'Start marker not found in {path}: {start_marker!r}')
    end = text.find(end_key, start)
    if end < 0:
        raise RuntimeError(f'End marker not found in {path}: {end_marker!r}')
    write_exact(path, text[:start] + replacement.replace('\n', sep) + text[end:])


write_exact('Services/Analysis/AutoMaskGenerationStrategyPlanner.cs', '''using FaceShield.Services.FaceDetection;
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
''')

replace_between(
    'Services/Analysis/AutoMaskGenerator.cs',
    '''                    bool canPipeline = _options.DetectEveryNFrames <= 1;\n''',
    '''                    Debug.WriteLine("[AutoMask] mode=sequential");\n                    GenerateSequential(videoPath, progress, ct, effectiveStartFrameIndex, totalFrames, onFrameProcessed);\n''',
    '''                    AutoMaskGenerationStrategyKind strategy =\n                        AutoMaskGenerationStrategyPlanner.Resolve(\n                            _options,\n                            _detector,\n                            _detectorFactory);\n\n                    switch (strategy)\n                    {\n                        case AutoMaskGenerationStrategyKind.PipelinedParallel:\n                        {\n                            var primary = (IBgraFaceDetector)_detector;\n                            using var pool = AutoMaskGenerationStrategyPlanner.CreateCompatibleDetectorPool(\n                                primary,\n                                _detectorFactory!,\n                                _options.ParallelDetectorCount);\n                            if (pool.Detectors.Count > 1)\n                            {\n                                Debug.WriteLine($"[AutoMask] mode=pipe-parallel({pool.Detectors.Count})");\n                                GeneratePipelinedDetectAllParallel(\n                                    videoPath,\n                                    pool.Detectors,\n                                    progress,\n                                    ct,\n                                    effectiveStartFrameIndex,\n                                    totalFrames,\n                                    onFrameProcessed);\n                                return;\n                            }\n\n                            Debug.WriteLine("[AutoMask] mode=pipe-single");\n                            GeneratePipelinedDetectAll(\n                                videoPath,\n                                primary,\n                                progress,\n                                ct,\n                                effectiveStartFrameIndex,\n                                totalFrames,\n                                onFrameProcessed);\n                            return;\n                        }\n\n                        case AutoMaskGenerationStrategyKind.PipelinedSingle:\n                            Debug.WriteLine("[AutoMask] mode=pipe-single");\n                            GeneratePipelinedDetectAll(\n                                videoPath,\n                                (IBgraFaceDetector)_detector,\n                                progress,\n                                ct,\n                                effectiveStartFrameIndex,\n                                totalFrames,\n                                onFrameProcessed);\n                            return;\n\n                        case AutoMaskGenerationStrategyKind.SparsePipelinedParallel:\n                        {\n                            var primary = (IBgraFaceDetector)_detector;\n                            using var pool = AutoMaskGenerationStrategyPlanner.CreateCompatibleDetectorPool(\n                                primary,\n                                _detectorFactory!,\n                                _options.ParallelDetectorCount);\n                            Debug.WriteLine($"[AutoMask] mode=sparse-pipe-parallel({pool.Detectors.Count})");\n                            GenerateSparsePipelinedTrackingParallel(\n                                videoPath,\n                                pool.Detectors,\n                                progress,\n                                ct,\n                                effectiveStartFrameIndex,\n                                totalFrames,\n                                onFrameProcessed);\n                            return;\n                        }\n\n                        default:\n                            break;\n                    }\n\n''')

replace_once(
    'Services/Analysis/AutoMaskGenerator.cs',
    '''                    Debug.WriteLine("[AutoMask] mode=sequential");\n                    GenerateSequential(videoPath, progress, ct, effectiveStartFrameIndex, totalFrames, onFrameProcessed);\n''',
    '''                    Debug.WriteLine("[AutoMask] mode=sequential");\n                    GenerateSequential(videoPath, progress, ct, effectiveStartFrameIndex, totalFrames, onFrameProcessed);\n''')

replace_once(
    'Services/Analysis/AutoMaskGenerator.cs',
    '''            bool sparsePipelineAvailable = _options.UseTracking &&\n                _options.DetectEveryNFrames > 1 &&\n                _detector is IBgraFaceDetector &&\n                _detectorFactory != null &&\n                _options.ParallelDetectorCount >= 1;\n''',
    '''            bool sparsePipelineAvailable =\n                AutoMaskGenerationStrategyPlanner.Resolve(\n                    _options,\n                    _detector,\n                    _detectorFactory) ==\n                AutoMaskGenerationStrategyKind.SparsePipelinedParallel;\n''')

replace_between(
    'Services/Analysis/AutoMaskGenerator.cs',
    '''        private static bool TryAddCompatibleParallelDetector(\n''',
    '''        private void GenerateSequential(\n''',
    '')

print('Phase C7 strategy planner extraction applied.')
