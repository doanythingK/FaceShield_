using Avalonia.Threading;
using FaceShield.Services.Analysis;
using FaceShield.Services.Video;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FaceShield.ViewModels.Workspace;

public sealed class IssueReviewCoordinator : IDisposable
{
    private const int SuspiciousNoFaceMaxGap = 8;
    private const int NoDetectionReviewSampleCount = 12;
    private const int SparseNoFaceReviewSampleCount = 12;
    private const double SparseNoFaceReviewMaxCoverageRatio = 0.20;
    private const int IssueTimeResolveBudget = 96;
    private const int IssueTimeCacheProbeBudget = 192;

    private readonly FrameMaskProvider _maskProvider;
    private readonly Func<bool> _tryBeginLifetimeOperation;
    private readonly Action _endLifetimeOperation;
    private readonly ObservableCollection<IssueEntryViewModel> _noFaceIssueEntries = new();
    private readonly ObservableCollection<IssueEntryViewModel> _lowConfidenceIssueEntries = new();
    private readonly ObservableCollection<IssueEntryViewModel> _flickerIssueEntries = new();
    private HashSet<int> _noFaceIssueSet = new();
    private HashSet<int> _lowConfidenceIssueSet = new();
    private HashSet<int> _flickerIssueSet = new();
    private int[] _anomalies = Array.Empty<int>();
    private CancellationTokenSource? _timeCts;
    private bool _disposed;

    public IssueReviewCoordinator(
        FrameMaskProvider maskProvider,
        Func<bool> tryBeginLifetimeOperation,
        Action endLifetimeOperation)
    {
        _maskProvider = maskProvider ?? throw new ArgumentNullException(nameof(maskProvider));
        _tryBeginLifetimeOperation = tryBeginLifetimeOperation ?? throw new ArgumentNullException(nameof(tryBeginLifetimeOperation));
        _endLifetimeOperation = endLifetimeOperation ?? throw new ArgumentNullException(nameof(endLifetimeOperation));
    }

    public ObservableCollection<IssueEntryViewModel> NoFaceIssueEntries => _noFaceIssueEntries;
    public ObservableCollection<IssueEntryViewModel> LowConfidenceIssueEntries => _lowConfidenceIssueEntries;
    public ObservableCollection<IssueEntryViewModel> FlickerIssueEntries => _flickerIssueEntries;

    public event Action? StateChanged;

    public async Task BuildAsync(
        int totalFrames,
        float lowConfidenceCutoff,
        FaceFilterProfile filterProfile,
        TimelineThumbnailProvider? timestampProvider,
        int anchorFrame,
        double fps,
        double secondsPerScreen,
        bool hideResolved,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        IssueAnalysisResult result = totalFrames <= 0
            ? IssueAnalysisResult.Empty
            : await Task.Run(
                () => Analyze(totalFrames, lowConfidenceCutoff, filterProfile, cancellationToken),
                cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyResult(result, timestampProvider, hideResolved);
            StateChanged?.Invoke();
        });

        if (totalFrames <= 0)
        {
            CancelTimeRefresh();
            return;
        }

        RefreshTimes(timestampProvider, anchorFrame, fps, secondsPerScreen);
    }

    private IssueAnalysisResult Analyze(
        int totalFrames,
        float lowConfidenceCutoff,
        FaceFilterProfile filterProfile,
        CancellationToken cancellationToken)
    {
        var noFace = new List<int>();
        var lowConfidence = new List<int>();
        var flicker = new List<int>();
        var faceFrames = new SortedSet<int>();

        foreach (var entry in _maskProvider.GetFaceMaskEntries())
        {
            cancellationToken.ThrowIfCancellationRequested();
            int frameIndex = entry.Key;
            if (frameIndex < 0 || frameIndex >= totalFrames)
                continue;

            var data = entry.Value;
            if (data.Faces.Count == 0)
                continue;

            faceFrames.Add(frameIndex);
            if (data.MinConfidence.HasValue && data.MinConfidence.Value < lowConfidenceCutoff)
                lowConfidence.Add(frameIndex);
        }

        int[] faceFrameIndices = faceFrames.ToArray();
        if (filterProfile == FaceFilterProfile.Yolo)
        {
            if (faceFrameIndices.Length == 0)
                AddNoDetectionReviewFrames(totalFrames, noFace, cancellationToken);
            else
            {
                AddSuspiciousNoFaceGaps(faceFrameIndices, totalFrames, noFace, cancellationToken);
                AddSparseNoFaceReviewFrames(faceFrameIndices, totalFrames, noFace, cancellationToken);
            }
        }
        else
        {
            AddSuspiciousNoFaceGaps(faceFrameIndices, totalFrames, noFace, cancellationToken);
        }

        for (int i = 1; i < faceFrameIndices.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int previous = faceFrameIndices[i - 1];
            int current = faceFrameIndices[i];
            if (current - previous == 2)
                flicker.Add(previous + 1);
        }

        noFace.Sort();
        lowConfidence.Sort();
        flicker.Sort();
        return new IssueAnalysisResult(noFace.ToArray(), lowConfidence.ToArray(), flicker.ToArray());
    }

    private void ApplyResult(
        IssueAnalysisResult result,
        TimelineThumbnailProvider? timestampProvider,
        bool hideResolved)
    {
        _noFaceIssueSet = new HashSet<int>(result.NoFaceFrames);
        _lowConfidenceIssueSet = new HashSet<int>(result.LowConfidenceFrames);
        _flickerIssueSet = new HashSet<int>(result.FlickerFrames);
        _anomalies = MergeSortedFrames(
            MergeSortedFrames(result.NoFaceFrames, result.LowConfidenceFrames),
            result.FlickerFrames);

        ResetIssueList(_noFaceIssueEntries, result.NoFaceFrames, "얼굴 없음", timestampProvider, hideResolved);
        ResetIssueList(_lowConfidenceIssueEntries, result.LowConfidenceFrames, "신뢰도 낮음", timestampProvider, hideResolved);
        ResetIssueList(_flickerIssueEntries, result.FlickerFrames, "연속 끊김", timestampProvider, hideResolved);
    }

    public IssueReviewStateSnapshot CreateStateSnapshot()
    {
        return new IssueReviewStateSnapshot(
            _noFaceIssueSet.OrderBy(static x => x).ToArray(),
            _lowConfidenceIssueSet.OrderBy(static x => x).ToArray(),
            _flickerIssueSet.OrderBy(static x => x).ToArray(),
            (int[])_anomalies.Clone());
    }

    public bool TryGetAdjacentAnomaly(int currentFrame, bool forward, out int targetFrame)
    {
        targetFrame = -1;
        if (_anomalies.Length == 0)
            return false;

        int idx = Array.BinarySearch(_anomalies, currentFrame);
        if (forward)
        {
            idx = idx >= 0 ? idx + 1 : ~idx;
            if (idx >= _anomalies.Length)
                idx = 0;
        }
        else
        {
            idx = idx >= 0 ? idx - 1 : ~idx - 1;
            if (idx < 0)
                idx = _anomalies.Length - 1;
        }

        targetFrame = _anomalies[idx];
        return true;
    }

    public bool TryGetFirstAnomaly(out int targetFrame)
    {
        targetFrame = _anomalies.Length > 0 ? _anomalies[0] : -1;
        return targetFrame >= 0;
    }

    public void ResolveIssueForFrame(int frameIndex)
    {
        bool changed = _noFaceIssueSet.Remove(frameIndex);
        changed |= _lowConfidenceIssueSet.Remove(frameIndex);
        changed |= _flickerIssueSet.Remove(frameIndex);
        if (!changed)
            return;

        RemoveIssueEntry(_noFaceIssueEntries, frameIndex);
        RemoveIssueEntry(_lowConfidenceIssueEntries, frameIndex);
        RemoveIssueEntry(_flickerIssueEntries, frameIndex);
        _anomalies = MergeSortedFrames(
            MergeSortedFrames(
                _noFaceIssueSet.OrderBy(static x => x).ToArray(),
                _lowConfidenceIssueSet.OrderBy(static x => x).ToArray()),
            _flickerIssueSet.OrderBy(static x => x).ToArray());
        StateChanged?.Invoke();
    }

    public void SetHideResolved(bool hideResolved)
    {
        SetIssueVisibility(_noFaceIssueEntries, hideResolved);
        SetIssueVisibility(_lowConfidenceIssueEntries, hideResolved);
        SetIssueVisibility(_flickerIssueEntries, hideResolved);
    }

    public void RefreshTimes(
        TimelineThumbnailProvider? provider,
        int anchorFrame,
        double fps,
        double secondsPerScreen)
    {
        if (_disposed || provider == null || provider.OperationsSuspended)
            return;

        double safeFps = double.IsFinite(fps) && fps > 0 ? fps : 30.0;
        double safeSpan = double.IsFinite(secondsPerScreen) && secondsPerScreen > 0
            ? secondsPerScreen
            : 10.0;
        int nearFrameDistance = (int)Math.Clamp(
            Math.Ceiling(safeFps * safeSpan * 2.0),
            120,
            50_000);

        var cts = new CancellationTokenSource();
        CancellationTokenSource? previous = Interlocked.Exchange(ref _timeCts, cts);
        if (previous != null)
        {
            try { previous.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        _ = RefreshTimesAsync(provider, cts, anchorFrame, nearFrameDistance);
    }

    private async Task RefreshTimesAsync(
        TimelineThumbnailProvider provider,
        CancellationTokenSource cts,
        int anchorFrame,
        int nearFrameDistance)
    {
        if (!_tryBeginLifetimeOperation())
        {
            Interlocked.CompareExchange(ref _timeCts, null, cts);
            cts.Dispose();
            return;
        }

        try
        {
            IssueEntryViewModel[] entries = await Dispatcher.UIThread.InvokeAsync(
                () => CollectIssueEntriesNearAnchor(anchorFrame, IssueTimeCacheProbeBudget));
            if (entries.Length == 0)
                return;

            CancellationToken token = cts.Token;
            Dictionary<int, double> resolved = await Task.Run(() =>
            {
                var times = new Dictionary<int, double>();
                var unresolved = new HashSet<int>();
                foreach (int frameIndex in entries.Select(static entry => entry.FrameIndex).Distinct())
                {
                    token.ThrowIfCancellationRequested();
                    if (provider.TryGetFrameTimestampSeconds(frameIndex, out double timestampSeconds))
                        times[frameIndex] = timestampSeconds;
                    else
                        unresolved.Add(frameIndex);
                }

                if (provider.OperationsSuspended || unresolved.Count == 0)
                    return times;

                int effectiveAnchor = anchorFrame >= 0 ? anchorFrame : unresolved.Min();
                int[] candidates = unresolved
                    .Where(frameIndex => frameIndex == effectiveAnchor || Math.Abs((long)frameIndex - effectiveAnchor) <= nearFrameDistance)
                    .OrderBy(frameIndex => Math.Abs((long)frameIndex - effectiveAnchor))
                    .Take(IssueTimeResolveBudget)
                    .OrderBy(static frameIndex => frameIndex)
                    .ToArray();

                foreach (int frameIndex in candidates)
                {
                    token.ThrowIfCancellationRequested();
                    if (provider.OperationsSuspended)
                        break;
                    if (provider.TryResolveFrameTimestampSeconds(frameIndex, token, out double timestampSeconds))
                        times[frameIndex] = timestampSeconds;
                }

                return times;
            }, token).ConfigureAwait(false);

            token.ThrowIfCancellationRequested();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested || !ReferenceEquals(_timeCts, cts))
                    return;

                foreach (IssueEntryViewModel entry in entries)
                {
                    if (resolved.TryGetValue(entry.FrameIndex, out double timestampSeconds))
                        entry.TimeText = FormatIssueTime(timestampSeconds);
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Interlocked.CompareExchange(ref _timeCts, null, cts);
            cts.Dispose();
            _endLifetimeOperation();
        }
    }

    public void CancelTimeRefresh()
    {
        CancellationTokenSource? cts = Interlocked.Exchange(ref _timeCts, null);
        if (cts == null)
            return;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void ResetIssueList(
        ObservableCollection<IssueEntryViewModel> target,
        IReadOnlyList<int> frames,
        string label,
        TimelineThumbnailProvider? provider,
        bool hideResolved)
    {
        target.Clear();
        for (int i = 0; i < frames.Count; i++)
        {
            string timeText = "--:--.--";
            if (provider?.TryGetFrameTimestampSeconds(frames[i], out double timestampSeconds) == true)
                timeText = FormatIssueTime(timestampSeconds);

            var entry = new IssueEntryViewModel(frames[i], label, timeText)
            {
                HideResolved = hideResolved
            };
            entry.Resolved += OnIssueResolved;
            target.Add(entry);
        }
    }

    private void OnIssueResolved(IssueEntryViewModel entry)
        => ResolveIssueForFrame(entry.FrameIndex);

    private IssueEntryViewModel[] CollectIssueEntriesNearAnchor(int anchorFrame, int maxEntries)
    {
        if (maxEntries <= 0)
            return Array.Empty<IssueEntryViewModel>();

        int safeAnchor = Math.Max(0, anchorFrame);
        int perCollectionBudget = Math.Max(1, (maxEntries + 2) / 3);
        var candidates = new List<IssueEntryViewModel>(Math.Min(maxEntries * 2, 512));
        AddIssueEntriesNearAnchor(_noFaceIssueEntries, safeAnchor, perCollectionBudget, candidates);
        AddIssueEntriesNearAnchor(_lowConfidenceIssueEntries, safeAnchor, perCollectionBudget, candidates);
        AddIssueEntriesNearAnchor(_flickerIssueEntries, safeAnchor, perCollectionBudget, candidates);

        return candidates
            .OrderBy(entry => Math.Abs((long)entry.FrameIndex - safeAnchor))
            .ThenBy(static entry => entry.FrameIndex)
            .Take(maxEntries)
            .ToArray();
    }

    private static void AddIssueEntriesNearAnchor(
        ObservableCollection<IssueEntryViewModel> source,
        int anchorFrame,
        int maxEntries,
        List<IssueEntryViewModel> target)
    {
        if (source.Count == 0 || maxEntries <= 0)
            return;

        int low = 0;
        int high = source.Count;
        while (low < high)
        {
            int mid = low + ((high - low) / 2);
            if (source[mid].FrameIndex < anchorFrame)
                low = mid + 1;
            else
                high = mid;
        }

        int left = low - 1;
        int right = low;
        int added = 0;
        while (added < maxEntries && (left >= 0 || right < source.Count))
        {
            if (left < 0)
                target.Add(source[right++]);
            else if (right >= source.Count)
                target.Add(source[left--]);
            else
            {
                long leftDistance = Math.Abs((long)source[left].FrameIndex - anchorFrame);
                long rightDistance = Math.Abs((long)source[right].FrameIndex - anchorFrame);
                if (leftDistance <= rightDistance)
                    target.Add(source[left--]);
                else
                    target.Add(source[right++]);
            }
            added++;
        }
    }

    private static void RemoveIssueEntry(ObservableCollection<IssueEntryViewModel> target, int frameIndex)
    {
        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (target[i].FrameIndex == frameIndex)
                target.RemoveAt(i);
        }
    }

    private static void SetIssueVisibility(ObservableCollection<IssueEntryViewModel> entries, bool hideResolved)
    {
        for (int i = 0; i < entries.Count; i++)
            entries[i].HideResolved = hideResolved;
    }

    private static string FormatIssueTime(double timestampSeconds)
    {
        if (!double.IsFinite(timestampSeconds) || timestampSeconds < 0)
            return "--:--.--";
        TimeSpan time = TimeSpan.FromSeconds(timestampSeconds);
        long minutes = (long)Math.Floor(time.TotalMinutes);
        int hundredths = time.Milliseconds / 10;
        return $"{minutes:D2}:{time.Seconds:D2}.{hundredths:D2}";
    }

    private static void AddSuspiciousNoFaceGaps(
        IReadOnlyList<int> faceFrames,
        int totalFrames,
        List<int> noFace,
        CancellationToken cancellationToken)
    {
        if (faceFrames.Count < 2 || totalFrames <= 0)
            return;
        for (int i = 1; i < faceFrames.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int previous = faceFrames[i - 1];
            int current = faceFrames[i];
            int length = current - previous - 1;
            if (length <= 0 || length > SuspiciousNoFaceMaxGap)
                continue;
            for (int frame = previous + 1; frame < Math.Min(current, totalFrames); frame++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                noFace.Add(frame);
            }
        }
    }

    private static void AddNoDetectionReviewFrames(
        int totalFrames,
        List<int> noFace,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (totalFrames <= 0)
            return;
        int sampleCount = Math.Min(NoDetectionReviewSampleCount, totalFrames);
        if (sampleCount <= 0)
            return;

        var frames = new SortedSet<int>();
        if (sampleCount == 1)
            frames.Add(0);
        else
        {
            for (int i = 0; i < sampleCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int frame = (int)Math.Round(i * (totalFrames - 1) / (double)(sampleCount - 1));
                frames.Add(Math.Clamp(frame, 0, totalFrames - 1));
            }
        }
        noFace.AddRange(frames);
        System.Diagnostics.Debug.WriteLine($"[AutoMaskNoDetectionReview] frames={string.Join(",", frames)} totalFrames={totalFrames}");
    }

    private static void AddSparseNoFaceReviewFrames(
        IReadOnlyList<int> faceFrames,
        int totalFrames,
        List<int> noFace,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int faceFrameCount = faceFrames.Count;
        if (totalFrames <= 0 || faceFrameCount <= 0)
            return;
        double coverage = faceFrameCount / (double)totalFrames;
        if (coverage > SparseNoFaceReviewMaxCoverageRatio || faceFrameCount < 2)
            return;

        var candidateFrames = new List<int>();
        for (int i = 1; i < faceFrames.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int previous = faceFrames[i - 1];
            int current = faceFrames[i];
            int length = current - previous - 1;
            if (length <= SuspiciousNoFaceMaxGap)
                continue;
            int start = previous + 1;
            candidateFrames.Add(start);
            if (length > 2)
                candidateFrames.Add(start + length / 2);
            candidateFrames.Add(current - 1);
        }

        if (candidateFrames.Count == 0)
            return;
        var frames = new SortedSet<int>(noFace);
        int sampleCount = Math.Min(SparseNoFaceReviewSampleCount, candidateFrames.Count);
        if (sampleCount == 1)
            frames.Add(candidateFrames[0]);
        else
        {
            for (int sample = 0; sample < sampleCount; sample++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int index = (int)Math.Round(sample * (candidateFrames.Count - 1) / (double)(sampleCount - 1));
                frames.Add(Math.Clamp(candidateFrames[index], 0, totalFrames - 1));
            }
        }
        noFace.Clear();
        noFace.AddRange(frames);
        System.Diagnostics.Debug.WriteLine($"[AutoMaskSparseNoFaceReview] faceFrames={faceFrameCount} totalFrames={totalFrames} coverage={coverage:0.000} frames={string.Join(",", frames)}");
    }

    private static int[] MergeSortedFrames(IReadOnlyList<int> first, IReadOnlyList<int> second)
    {
        if (first.Count == 0)
            return CopyFrames(second);
        if (second.Count == 0)
            return CopyFrames(first);
        var merged = new int[first.Count + second.Count];
        int i = 0, j = 0, k = 0;
        while (i < first.Count && j < second.Count)
        {
            int a = first[i];
            int b = second[j];
            if (a == b)
            {
                merged[k++] = a;
                i++;
                j++;
            }
            else if (a < b)
                merged[k++] = first[i++];
            else
                merged[k++] = second[j++];
        }
        while (i < first.Count)
            merged[k++] = first[i++];
        while (j < second.Count)
            merged[k++] = second[j++];
        if (k == merged.Length)
            return merged;
        var trimmed = new int[k];
        Array.Copy(merged, trimmed, k);
        return trimmed;
    }

    private static int[] CopyFrames(IReadOnlyList<int> source)
    {
        if (source is int[] arr)
            return (int[])arr.Clone();
        var copy = new int[source.Count];
        for (int i = 0; i < source.Count; i++)
            copy[i] = source[i];
        return copy;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        CancelTimeRefresh();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(IssueReviewCoordinator));
    }

    private sealed record IssueAnalysisResult(
        int[] NoFaceFrames,
        int[] LowConfidenceFrames,
        int[] FlickerFrames)
    {
        internal static IssueAnalysisResult Empty { get; } = new(
            Array.Empty<int>(),
            Array.Empty<int>(),
            Array.Empty<int>());
    }
}

public sealed record IssueReviewStateSnapshot(
    int[] NoFaceFrames,
    int[] LowConfidenceFrames,
    int[] FlickerFrames,
    int[] Anomalies);
