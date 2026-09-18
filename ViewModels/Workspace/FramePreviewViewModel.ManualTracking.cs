using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using FaceShield.Services.Video;
using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FaceShield.ViewModels.Workspace;

public partial class FramePreviewViewModel
{
    private string? _manualTrackingVideoPath;
    private int _manualTrackingTotalFrames;
    private CancellationTokenSource? _manualTrackingCts;
    private Task? _manualTrackingTask;
    private readonly object _manualTrackingCommitGate = new();
    private bool _manualTrackingCommitStarted;
    private Func<bool>? _tryBeginManualTrackingLifetime;
    private Action? _endManualTrackingLifetime;
    private Func<Task>? _persistManualTrackingWorkspace;
    private bool _isManualTracking;
    private int _manualTrackingProgress;
    private string? _manualTrackingStatusText;
    private bool _manualTrackingToolPanelHandlerAttached;
    private int _manualTrackingPendingSourceValidationFrame = -1;

    public bool ManualTrackingAvailable =>
        _manualMaskKeyframesEnabled &&
        !string.IsNullOrWhiteSpace(_manualTrackingVideoPath);

    public bool IsManualTracking
    {
        get => _isManualTracking;
        private set
        {
            if (_isManualTracking == value)
                return;
            _isManualTracking = value;
            OnPropertyChanged(nameof(IsManualTracking));
            OnPropertyChanged(nameof(CanTrackForward));
        }
    }

    public int ManualTrackingProgress
    {
        get => _manualTrackingProgress;
        private set
        {
            int bounded = Math.Clamp(value, 0, 100);
            if (_manualTrackingProgress == bounded)
                return;
            _manualTrackingProgress = bounded;
            OnPropertyChanged(nameof(ManualTrackingProgress));
        }
    }

    public string? ManualTrackingStatusText
    {
        get => _manualTrackingStatusText;
        private set
        {
            if (string.Equals(_manualTrackingStatusText, value, StringComparison.Ordinal))
                return;
            _manualTrackingStatusText = value;
            OnPropertyChanged(nameof(ManualTrackingStatusText));
        }
    }

    public bool CanTrackForward =>
        ManualTrackingAvailable &&
        !_disposed &&
        !IsManualTracking &&
        !_isPlaying &&
        !IsFrameLoading &&
        _toolPanel.CanEditWorkspace &&
        _tryBeginManualTrackingLifetime != null &&
        _endManualTrackingLifetime != null &&
        _persistManualTrackingWorkspace != null &&
        _currentFrameIndex >= 0 &&
        _maskBitmap != null;

    internal void ConfigureManualTrackingOwnership(
        Func<bool> tryBeginLifetimeOperation,
        Action endLifetimeOperation,
        Func<Task> persistWorkspaceStateAsync)
    {
        _tryBeginManualTrackingLifetime = tryBeginLifetimeOperation
            ?? throw new ArgumentNullException(nameof(tryBeginLifetimeOperation));
        _endManualTrackingLifetime = endLifetimeOperation
            ?? throw new ArgumentNullException(nameof(endLifetimeOperation));
        _persistManualTrackingWorkspace = persistWorkspaceStateAsync
            ?? throw new ArgumentNullException(nameof(persistWorkspaceStateAsync));
        OnPropertyChanged(nameof(CanTrackForward));
    }

    internal void ConfigureManualTrackingContext(
        string? videoPath,
        int totalFrames)
    {
        _manualTrackingVideoPath = videoPath;
        _manualTrackingTotalFrames = Math.Max(0, totalFrames);
        if (!_manualTrackingToolPanelHandlerAttached)
        {
            _toolPanel.PropertyChanged += OnManualTrackingToolPanelPropertyChanged;
            _manualTrackingToolPanelHandlerAttached = true;
        }
        if (_manualMaskKeyframesEnabled && !string.IsNullOrWhiteSpace(videoPath))
            ManualTrackingShutdownRegistry.Register(this);
        OnPropertyChanged(nameof(ManualTrackingAvailable));
        OnPropertyChanged(nameof(CanTrackForward));
    }

    internal void DetachManualTrackingContext()
    {
        DisposeManualTrackingState();
        OnPropertyChanged(nameof(CanTrackForward));
    }

    internal void NotifyManualTrackingPlaybackStateChanged()
        => OnPropertyChanged(nameof(CanTrackForward));

    internal async Task StopManualTrackingAndWaitAsync()
    {
        CancelManualTrackingCore();
        Task? trackingTask = Volatile.Read(ref _manualTrackingTask);
        if (trackingTask == null)
            return;

        try
        {
            await trackingTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[FramePreview] manual tracking shutdown completed with error: {ex.Message}");
        }
    }

    private void OnManualTrackingToolPanelPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ToolPanelViewModel.IsAutoRunning) &&
            !_toolPanel.IsAutoRunning)
        {
            RefreshCurrentManualMaskAfterAuto();
        }

        if (e.PropertyName == nameof(ToolPanelViewModel.CanEditWorkspace))
            OnPropertyChanged(nameof(CanTrackForward));
    }

    private void RefreshCurrentManualMaskAfterAuto()
    {
        if (!_manualMaskKeyframesEnabled ||
            _disposed ||
            _currentFrameIndex < 0 ||
            _frameBitmap == null ||
            _maskProvider is not FrameMaskProvider provider)
        {
            return;
        }

        WriteableBitmap replacement =
            CreateEditableMask(_currentFrameIndex, _frameBitmap)
            ?? CreateEmptyMask(
                _frameBitmap.PixelSize.Width,
                _frameBitmap.PixelSize.Height);

        _manualMaskKeyframeRefreshInProgress = true;
        try
        {
            MaskBitmap = replacement;
            _maskUndo.Clear();
            _maskDirty = false;
            if (_manualTrackingPendingSourceValidationFrame == _currentFrameIndex)
                _manualTrackingPendingSourceValidationFrame = -1;
            UpdateDetectionRects(_currentFrameIndex);

            if (ManualMaskKeyframeTimeline.InvalidateSegmentIfSourceChanged(
                    provider,
                    _currentFrameIndex))
            {
                ManualTrackingStatusText =
                    "자동 검출로 키프레임 마스크가 변경되어 기존 추적을 적용하지 않습니다. " +
                    "현재 프레임에서 다시 자동 추적하세요.";
            }

            RefreshPreview(force: true);
        }
        finally
        {
            _manualMaskKeyframeRefreshInProgress = false;
        }

        OnPropertyChanged(nameof(CanTrackForward));
    }

    internal void NotifyManualMaskEditedForTracking(int frameIndex)
    {
        if (!_manualMaskKeyframesEnabled || frameIndex < 0)
            return;

        _manualTrackingPendingSourceValidationFrame = frameIndex;
        ManualTrackingStatusText = "마스크가 수정되었습니다. 이 프레임부터 다시 추적할 수 있습니다.";
        OnPropertyChanged(nameof(CanTrackForward));
    }

    private bool RevalidatePendingManualTrackingSource(FrameMaskProvider provider)
    {
        int frameIndex = _manualTrackingPendingSourceValidationFrame;
        if (frameIndex < 0)
            return false;

        if (!provider.HasStoredMask(frameIndex) &&
            !provider.TryGetFaceMaskData(frameIndex, out _))
        {
            return false;
        }

        _manualTrackingPendingSourceValidationFrame = -1;
        bool stale = ManualMaskKeyframeTimeline.InvalidateSegmentIfSourceChanged(
            provider,
            frameIndex);
        if (stale)
        {
            ManualTrackingStatusText =
                "키프레임 마스크가 변경되어 기존 추적을 적용하지 않습니다. " +
                "현재 프레임에서 다시 자동 추적하세요.";
        }
        return stale;
    }

    [RelayCommand]
    private Task TrackForward()
        => RunManualTrackingAsync(maxNextFrames: null);

    [RelayCommand]
    private Task TrackNextFrame()
        => RunManualTrackingAsync(maxNextFrames: 1);

    private async Task RunManualTrackingAsync(int? maxNextFrames)
    {
        if (!CanTrackForward)
            return;

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task operationTask = completion.Task;
        if (Interlocked.CompareExchange(
                ref _manualTrackingTask,
                operationTask,
                null) != null)
        {
            return;
        }

        bool lifetimeStarted = false;
        try
        {
            Func<bool>? tryBegin = _tryBeginManualTrackingLifetime;
            if (tryBegin == null || !tryBegin())
                return;

            lifetimeStarted = true;
            await TrackForwardCoreAsync(maxNextFrames);
        }
        finally
        {
            try
            {
                if (lifetimeStarted)
                    _endManualTrackingLifetime?.Invoke();
            }
            finally
            {
                completion.TrySetResult(true);
                _ = Interlocked.CompareExchange(
                    ref _manualTrackingTask,
                    null,
                    operationTask);
            }
        }
    }

    private async Task TrackForwardCoreAsync(int? maxNextFrames)
    {
        if (!CanTrackForward ||
            _maskProvider is not FrameMaskProvider provider ||
            string.IsNullOrWhiteSpace(_manualTrackingVideoPath) ||
            _maskBitmap == null)
        {
            return;
        }

        int sourceFrame = _currentFrameIndex;
        if (sourceFrame < 0)
            return;

        PersistCurrentMask();
        RevalidatePendingManualTrackingSource(provider);

        bool hasStoredManualSource = provider.TryCloneStoredMask(
            sourceFrame,
            out WriteableBitmap storedManualSource);
        bool sourceWasExplicit =
            hasStoredManualSource ||
            provider.TryGetFaceMaskData(sourceFrame, out _);
        WriteableBitmap? sourceMask = hasStoredManualSource
            ? storedManualSource
            : sourceWasExplicit
                ? provider.GetFinalMask(sourceFrame)
                : CloneBitmap(_maskBitmap);
        if (sourceMask == null)
        {
            ManualTrackingStatusText = "현재 프레임에 추적할 마스크가 없습니다.";
            return;
        }

        CancellationTokenSource? previous = Interlocked.Exchange(
            ref _manualTrackingCts,
            new CancellationTokenSource());
        if (previous != null)
        {
            try { previous.Cancel(); }
            catch (ObjectDisposedException) { }
            previous.Dispose();
        }

        CancellationTokenSource trackingCts = _manualTrackingCts!;
        CancellationToken token = trackingCts.Token;

        int endExclusive = ManualMaskKeyframeTimeline.GetNextExplicitKeyframe(
            provider,
            sourceFrame,
            totalFrames: 0);
        if (maxNextFrames.HasValue)
        {
            int limit = sourceFrame >= int.MaxValue - maxNextFrames.Value - 1
                ? int.MaxValue
                : sourceFrame + maxNextFrames.Value + 1;
            endExclusive = Math.Min(endExclusive, limit);
        }
        int expectedFrames = endExclusive != int.MaxValue
            ? Math.Max(0, endExclusive - sourceFrame - 1)
            : _manualTrackingTotalFrames > sourceFrame
                ? Math.Max(0, _manualTrackingTotalFrames - sourceFrame - 1)
                : 0;

        IsManualTracking = true;
        _toolPanel.IsManualTracking = true;
        ManualTrackingProgress = 0;
        ManualTrackingStatusText = endExclusive != int.MaxValue
            ? $"프레임 {sourceFrame}부터 {endExclusive - 1}까지 추적 중..."
            : $"프레임 {sourceFrame}부터 영상 끝까지 추적 중...";
        OnPropertyChanged(nameof(CanTrackForward));

        bool exactOperationsSuspended = false;
        try
        {
            await StopManualOperationsAndWaitAsync();
            token.ThrowIfCancellationRequested();
            await SuspendExactFrameOperationsAndWaitAsync();
            exactOperationsSuspended = true;
            token.ThrowIfCancellationRequested();

            var progress = new SynchronousTrackingProgress(processed =>
            {
                if (_disposed)
                {
                    try { trackingCts.Cancel(); }
                    catch (ObjectDisposedException) { }
                    return;
                }

                if (expectedFrames <= 0)
                    return;
                int percent = (int)Math.Round(processed * 100.0 / expectedFrames);
                if (endExclusive == int.MaxValue)
                    percent = Math.Min(percent, 99);
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_disposed && ReferenceEquals(_manualTrackingCts, trackingCts))
                        ManualTrackingProgress = percent;
                });
            });

            ManualMaskTrackResult result = await Task.Run(
                () =>
                {
                    ManualMaskTrackResult tracked = ManualMaskTrackingService.TrackForward(
                        _manualTrackingVideoPath!,
                        sourceFrame,
                        endExclusive,
                        sourceMask,
                        progress,
                        token);
                    tracked.Segment.SourceMaskFingerprint =
                        ManualMaskFingerprint.Compute(sourceMask, token);
                    return tracked;
                },
                token);

            token.ThrowIfCancellationRequested();
            if (_disposed || !ReferenceEquals(_manualTrackingCts, trackingCts))
                return;

            if (result.ProcessedFrames > 0)
            {
                Func<Task>? persistWorkspace = _persistManualTrackingWorkspace;
                if (persistWorkspace == null)
                {
                    throw new InvalidOperationException(
                        "수동 추적 workspace 저장 컨텍스트가 구성되지 않았습니다.");
                }

                WriteableBitmap? promotedMask = null;
                if (!sourceWasExplicit)
                    promotedMask = CloneBitmap(sourceMask);

                bool commitStarted = false;
                try
                {
                    lock (_manualTrackingCommitGate)
                    {
                        token.ThrowIfCancellationRequested();
                        if (_disposed || !ReferenceEquals(_manualTrackingCts, trackingCts))
                            return;

                        _manualTrackingCommitStarted = true;
                        commitStarted = true;
                        if (promotedMask != null)
                        {
                            provider.SetMask(sourceFrame, promotedMask);
                            promotedMask = null;
                        }
                    }

                    // If persistence fails, leave the in-memory user/manual source
                    // intact. The old rollback removed the whole frame range, which
                    // can now contain an independent Auto layer. A later save can
                    // safely retry this source without destroying unrelated Auto data.
                    await persistWorkspace().ConfigureAwait(true);

                    ManualMaskKeyframeTimeline.SetTrackSegment(
                        provider,
                        result.Segment);
                }
                finally
                {
                    promotedMask?.Dispose();
                    if (commitStarted)
                    {
                        lock (_manualTrackingCommitGate)
                            _manualTrackingCommitStarted = false;
                    }
                }
            }

            if (result.Segment.StoppedByFailure)
            {
                if (expectedFrames > 0)
                {
                    ManualTrackingProgress = (int)Math.Round(
                        Math.Clamp(result.ProcessedFrames * 100.0 / expectedFrames, 0, 99));
                }

                string stop = result.Segment.StopFrame.HasValue
                    ? $"{result.Segment.StopFrame.Value} 프레임"
                    : "현재 구간";
                ManualTrackingStatusText = result.ProcessedFrames > 0
                    ? $"추적 중단: {stop}에서 {result.Segment.StopReason ?? "신뢰도 부족"}. " +
                      "성공한 구간까지만 저장했습니다. 해당 프레임에서 마스크를 수정한 뒤 다시 추적하세요."
                    : $"추적 실패: {stop}에서 {result.Segment.StopReason ?? "신뢰도 부족"}. " +
                      "추적 결과를 저장하지 않았습니다.";
            }
            else
            {
                ManualTrackingProgress = 100;
                int lastFrame = result.Segment.Components
                    .SelectMany(static component => component.Samples)
                    .Select(static sample => sample.FrameIndex)
                    .DefaultIfEmpty(sourceFrame)
                    .Max();
                ManualTrackingStatusText = result.ProcessedFrames > 0
                    ? $"추적 완료: {sourceFrame} → {lastFrame} 프레임."
                    : "추적할 다음 프레임이 없어 타임라인을 변경하지 않았습니다.";
            }
        }
        catch (OperationCanceledException) when (trackingCts.IsCancellationRequested)
        {
            ManualTrackingStatusText = "수동 마스크 추적이 취소되었습니다.";
        }
        catch (Exception ex)
        {
            ManualTrackingStatusText = $"수동 마스크 추적 실패: {ex.Message}";
        }
        finally
        {
            sourceMask.Dispose();
            if (exactOperationsSuspended && !_disposed)
                ResumeExactFrameOperations();

            if (ReferenceEquals(_manualTrackingCts, trackingCts))
                _manualTrackingCts = null;
            trackingCts.Dispose();
            IsManualTracking = false;
            _toolPanel.IsManualTracking = false;
            OnPropertyChanged(nameof(CanTrackForward));

            if (!_disposed && _currentFrameIndex >= 0)
                OnFrameIndexChanged(_currentFrameIndex);
        }
    }

    [RelayCommand]
    private void CancelManualTracking()
        => CancelManualTrackingCore();

    internal void CancelManualTrackingForShutdown()
        => CancelManualTrackingCore();

    private void CancelManualTrackingCore()
    {
        lock (_manualTrackingCommitGate)
        {
            if (_manualTrackingCommitStarted)
                return;

            CancellationTokenSource? cts = Volatile.Read(ref _manualTrackingCts);
            if (cts == null)
                return;
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    private void DisposeManualTrackingState()
    {
        CancelManualTrackingCore();
        if (_manualMaskKeyframeHandlerAttached)
        {
            PropertyChanged -= OnManualMaskKeyframePropertyChanged;
            _manualMaskKeyframeHandlerAttached = false;
        }
        if (_manualMaskEditHandlerAttached)
        {
            MaskEdited -= NotifyManualMaskEditedForTracking;
            _manualMaskEditHandlerAttached = false;
        }
        if (_manualUndoHandlerAttached)
        {
            _toolPanel.UndoRequested -= OnManualUndoCompleted;
            _manualUndoHandlerAttached = false;
        }
        if (_manualTrackingToolPanelHandlerAttached)
        {
            _toolPanel.PropertyChanged -= OnManualTrackingToolPanelPropertyChanged;
            _manualTrackingToolPanelHandlerAttached = false;
        }
    }

    private sealed class SynchronousTrackingProgress : IProgress<int>
    {
        private readonly Action<int> _report;

        internal SynchronousTrackingProgress(Action<int> report)
        {
            _report = report;
        }

        public void Report(int value) => _report(value);
    }
}