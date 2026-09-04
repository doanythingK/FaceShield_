using Avalonia;
using FaceShield.Services.Video;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace FaceShield.Services.Analysis;

internal sealed class DetectionResult
{
    public int Index { get; init; }
    public Rect[] Bounds { get; init; } = Array.Empty<Rect>();
    public PixelSize Size { get; init; }
    public float? MinConfidence { get; init; }
    public float[] Confidences { get; init; } = Array.Empty<float>();
    public double[] FrameSignature { get; init; } = Array.Empty<double>();
}

internal readonly record struct SparseSceneCutTransition(
    int SourceFrameIndex,
    int NextFrameIndex);

internal readonly record struct SparseMaterializeResult(
    int Interpolated,
    int SceneCutStops,
    IReadOnlyList<SparseSceneCutTransition> SceneCutTransitions);

internal static class SparseTrackingMaterializer
{
    private const double SparseSceneCutDifferenceThreshold = 0.32;
    private const double OffModeSparseSceneCutDifferenceThreshold = 0.18;

    internal static SparseMaterializeResult Materialize(
        ConcurrentDictionary<int, DetectionResult> results,
        FrameMaskProvider.SparseFaceMaskWorkingCopy maskProvider,
        AutoMaskOptions options,
        int start,
        int endExclusive,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (results == null || results.Count == 0)
        {
            return new SparseMaterializeResult(
                0,
                0,
                Array.Empty<SparseSceneCutTransition>());
        }

        if (endExclusive <= start)
        {
            return new SparseMaterializeResult(
                0,
                0,
                Array.Empty<SparseSceneCutTransition>());
        }

        cancellationToken.ThrowIfCancellationRequested();
        int[] keys = results.Keys.ToArray();
        Array.Sort(keys);

        int materialized = 0;
        int sceneCutStops = 0;
        var sceneCutTransitions = new List<SparseSceneCutTransition>();
        int maxBridgeFrames = Math.Max(1, options.DetectEveryNFrames * 2);
        bool guardSceneCuts =
            options.UseTracking &&
            options.ProcessingMode != AutoMaskProcessingMode.Raw;
        double sceneCutThreshold =
            options.FilterProfile == FaceFilterProfile.Yolo &&
            !options.EnablePostProcessing
                ? OffModeSparseSceneCutDifferenceThreshold
                : SparseSceneCutDifferenceThreshold;

        for (int i = 0; i < keys.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int key = keys[i];
            if (key < start || key >= endExclusive)
                continue;

            if (!results.TryGetValue(key, out DetectionResult? current) ||
                current.Bounds.Length == 0)
            {
                continue;
            }

            if (!maskProvider.HasEntry(key))
            {
                cancellationToken.ThrowIfCancellationRequested();
                maskProvider.SetFaceRects(
                    key,
                    current.Bounds,
                    current.Size,
                    current.MinConfidence,
                    current.Confidences);
            }

            DetectionResult? nextPositive = FindNextPositiveResult(
                results,
                keys,
                i + 1,
                endExclusive,
                key,
                maxBridgeFrames,
                cancellationToken);
            bool canBridge =
                nextPositive != null &&
                CanBridgeSparseResults(
                    current,
                    nextPositive,
                    maxBridgeFrames,
                    guardSceneCuts,
                    sceneCutThreshold,
                    cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            int nextKey =
                FindNextDetectionKey(keys, i + 1, endExclusive);
            bool stopAtSceneCut = false;
            if (nextKey >= 0 &&
                results.TryGetValue(
                    nextKey,
                    out DetectionResult? nextDetection) &&
                ShouldStopSparseSceneCarry(
                    key,
                    nextKey,
                    maxBridgeFrames,
                    guardSceneCuts,
                    current.FrameSignature,
                    nextDetection.FrameSignature,
                    sceneCutThreshold))
            {
                stopAtSceneCut = true;
                sceneCutStops++;
                sceneCutTransitions.Add(
                    new SparseSceneCutTransition(key, nextKey));
            }

            int interval = Math.Max(1, options.DetectEveryNFrames);
            int segmentEnd = stopAtSceneCut
                ? key + 1
                : canBridge
                    ? nextPositive!.Index
                    : nextKey >= 0
                        ? Math.Min(nextKey, key + interval)
                        : Math.Min(endExclusive, key + interval);

            if (segmentEnd <= key + 1)
                continue;

            for (int frame = key + 1; frame < segmentEnd; frame++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (maskProvider.HasEntry(frame))
                    continue;

                DetectionResult payload = canBridge
                    ? InterpolateSparseResult(
                        current,
                        nextPositive!,
                        frame,
                        cancellationToken)
                    : current;

                if (payload.Bounds.Length == 0)
                    continue;

                cancellationToken.ThrowIfCancellationRequested();
                maskProvider.SetFaceRects(
                    frame,
                    payload.Bounds,
                    payload.Size,
                    payload.MinConfidence,
                    payload.Confidences);
                materialized++;
            }
        }

        return new SparseMaterializeResult(
            materialized,
            sceneCutStops,
            sceneCutTransitions.ToArray());
    }

    internal static string FormatSceneCutTransitions(
        IReadOnlyList<SparseSceneCutTransition> transitions)
    {
        if (transitions.Count == 0)
            return "none";

        const int maxTransitions = 16;
        var selected = transitions
            .Take(maxTransitions)
            .Select(static x => string.Create(
                CultureInfo.InvariantCulture,
                $"{x.SourceFrameIndex}->{x.NextFrameIndex}"));
        string text = string.Join(",", selected);

        return transitions.Count > maxTransitions
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{text},+{transitions.Count - maxTransitions}")
            : text;
    }

    private static int FindNextDetectionKey(
        int[] keys,
        int startIndex,
        int endExclusive)
    {
        if (startIndex < 0 || startIndex >= keys.Length)
            return -1;

        int key = keys[startIndex];
        return key >= endExclusive ? -1 : key;
    }

    private static DetectionResult? FindNextPositiveResult(
        ConcurrentDictionary<int, DetectionResult> results,
        int[] keys,
        int startIndex,
        int endExclusive,
        int currentKey,
        int maxBridgeFrames,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        for (int i = startIndex; i < keys.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int key = keys[i];
            if (key >= endExclusive)
                return null;
            if (!results.TryGetValue(key, out DetectionResult? result))
                continue;
            if (key - currentKey > maxBridgeFrames)
                return null;
            if (result.Bounds.Length > 0)
                return result;
        }

        return null;
    }

    private static bool CanBridgeSparseResults(
        DetectionResult current,
        DetectionResult next,
        int maxBridgeFrames,
        bool guardSceneCuts,
        double sceneCutThreshold,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (next.Index <= current.Index ||
            next.Index - current.Index > maxBridgeFrames)
        {
            return false;
        }

        if (current.Bounds.Length == 0 || next.Bounds.Length == 0)
            return false;

        if (guardSceneCuts &&
            IsSparseSceneCut(current, next, sceneCutThreshold))
        {
            return false;
        }

        int matches = 0;
        var used = new bool[next.Bounds.Length];
        for (int i = 0; i < current.Bounds.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int match = SparseTrackingMath.FindBestMatch(
                current.Bounds[i],
                next.Bounds,
                used);
            if (match < 0)
                continue;

            used[match] = true;
            matches++;
        }

        return matches > 0;
    }

    private static DetectionResult InterpolateSparseResult(
        DetectionResult current,
        DetectionResult next,
        int frameIndex,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (next.Index <= current.Index)
            return current;

        double t =
            (frameIndex - current.Index) /
            (double)(next.Index - current.Index);
        t = Math.Clamp(t, 0.0, 1.0);

        var bounds = new List<Rect>(current.Bounds.Length);
        var confidences = new List<float>(current.Bounds.Length);
        var used = new bool[next.Bounds.Length];

        for (int i = 0; i < current.Bounds.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Rect from = current.Bounds[i];
            int match = SparseTrackingMath.FindBestMatch(
                from,
                next.Bounds,
                used);
            if (match < 0)
                continue;

            used[match] = true;
            Rect to = next.Bounds[match];
            bounds.Add(SparseTrackingMath.LerpRect(from, to, t));
            confidences.Add(
                SparseTrackingMath.LerpConfidence(
                    current.Confidences,
                    next.Confidences,
                    i,
                    match,
                    t));
        }

        return new DetectionResult
        {
            Index = frameIndex,
            Bounds = bounds.ToArray(),
            Size =
                current.Size.Width > 0 && current.Size.Height > 0
                    ? current.Size
                    : next.Size,
            MinConfidence =
                SparseTrackingMath.MinConfidence(confidences),
            Confidences = confidences.ToArray()
        };
    }

    private static bool IsSparseSceneCut(
        DetectionResult current,
        DetectionResult next,
        double sceneCutThreshold)
    {
        return SparseTrackingMath.SignatureDifference(
            current.FrameSignature,
            next.FrameSignature) >= sceneCutThreshold;
    }

    private static bool ShouldStopSparseSceneCarry(
        int currentKey,
        int nextKey,
        int maxBridgeFrames,
        bool guardSceneCuts,
        IReadOnlyList<double> currentSignature,
        IReadOnlyList<double> nextSignature,
        double sceneCutThreshold)
    {
        return guardSceneCuts &&
            nextKey >= 0 &&
            nextKey > currentKey &&
            nextKey - currentKey <= maxBridgeFrames &&
            SparseTrackingMath.SignatureDifference(
                currentSignature,
                nextSignature) >= sceneCutThreshold;
    }
}
