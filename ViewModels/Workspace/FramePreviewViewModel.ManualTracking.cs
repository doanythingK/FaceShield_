using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using FaceShield.Services.Video;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FaceShield.ViewModels.Workspace;

public partial class FramePreviewViewModel
{
    private string? _manualTrackingVideoPath;
    private int _manualTrackingTotalFrames;
    private CancellationTokenSource? _manualTrackingCts;
    private bool _isManualTracking;
    private int _manualTrackingProgress;
    private string? _manualTrackingStatusText;

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
        _toolPanel.CanEditWorkspace &&
        _currentFrameIndex >= 0 &&
        _maskBitmap != null;

    internal void ConfigureManualTrackingContext(
        string? videoPath,
        int totalFrames)
    {
        _manualTrackingVideoPath = videoPath;
        _manualTrackingTotalFrames = Math.Max(0, totalFrames);
        OnPropertyChanged(nameof(ManualTrackingAvailable));
        OnPropertyChanged(nameof(CanTrackForward));
    }

    internal void NotifyManualMaskEditedForTracking(int frameIndex)
    {
        if (!_manualMaskKeyframesEnabled ||
            _maskProvider is not FrameMaskProvider provider ||
            frameIndex < 0)
        {
            return;
        }

        ManualMaskKeyframeTimeline.NotifyExplicitKeyframeChanged(provider, frameIndex);
        ManualTrackingStatusText = "마스크가 수정되었습니다. 이 프레임부터 다시 추적할 수 있습니다.";
        OnPropertyChanged(nameof(CanTrackForward));
    }

    [RelayCommand]
    private async Task TrackForward()
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

        // Tracking must always have an explicit source keyframe. If the visible mask
        // was inherited from an earlier keyframe, promote the current visible mask to
        // a new keyframe before starting the tracker.
        if (!provider.HasStoredMask(sourceFrame) &&
            !provider.TryGetFaceMaskData(sourceFrame, out _))
        {
            provider.SetMask(sourceFrame, CloneBitmap(_maskBitmap));
            ManualMaskKeyframeTimeline.NotifyExplicitKeyframeChanged(provider, sourceFrame);
        }

        WriteableBitmap? sourceMask = provider.GetFinalMask(sourceFrame);
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
            _manualTrackingTotalFrames);
        int expectedFrames = endExclusive != int.MaxValue
            ? Math.Max(0, endExclusive - sourceFrame - 1)
            : 0;

        IsManualTracking = true;
        _toolPanel.IsManualTracking = true;
        ManualTrackingProgress = 0;
        ManualTrackingStatusText = expectedFrames > 0
            ? $"프레임 {sourceFrame}부터 {endExclusive - 1}까지 추적 중..."
            : $"프레임 {sourceFrame}부터 영상 끝까지 추적 중...";
        OnPropertyChanged(nameof(CanTrackForward));

        bool exactOperationsSuspended = false;
        try
        {
            await StopManualOperationsAndWaitAsync();
            await SuspendExactFrameOperationsAndWaitAsync();
            exactOperationsSuspended = true;

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
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_disposed && ReferenceEquals(_manualTrackingCts, trackingCts))
                        ManualTrackingProgress = percent;
                });
            });

            ManualMaskTrackResult result = await Task.Run(
                () => ManualMaskTrackingService.TrackForward(
                    _manualTrackingVideoPath!,
                    sourceFrame,
                    endExclusive,
                    sourceMask,
                    progress,
                    token),
                token);

            token.ThrowIfCancellationRequested();
            if (_disposed || !ReferenceEquals(_manualTrackingCts, trackingCts))
                return;

            ManualMaskKeyframeTimeline.SetTrackSegment(provider, result.Segment);
            ManualTrackingProgress = 100;
            if (result.Segment.StoppedByFailure)
            {
                string stop = result.Segment.StopFrame.HasValue
                    ? $"{result.Segment.StopFrame.Value} 프레임"
                    : "현재 구간";
                ManualTrackingStatusText =
                    $"추적 중단: {stop}에서 {result.Segment.StopReason ?? "신뢰도 부족"}. " +
                    "해당 프레임에서 마스크를 수정한 뒤 다시 추적하세요.";
            }
            else
            {
                int lastFrame = result.Segment.Samples.Count > 0
                    ? result.Segment.Samples[^1].FrameIndex
                    : sourceFrame;
                ManualTrackingStatusText =
                    $"추적 완료: {sourceFrame} → {lastFrame} 프레임.";
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
    {
        CancellationTokenSource? cts = Volatile.Read(ref _manualTrackingCts);
        if (cts == null)
            return;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { }
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
