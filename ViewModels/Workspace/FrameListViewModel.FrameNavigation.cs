using CommunityToolkit.Mvvm.Input;
using System;

namespace FaceShield.ViewModels.Workspace;

public partial class FrameListViewModel
{
    // User-directed frame navigation must respect the active workspace operation.
    private bool CanNavigateToFrame(int frameIndex)
        => !_disposed && IsPlaybackEnabled &&
           _canUserNavigate?.Invoke() != false &&
           frameIndex >= 0 && TotalFrames > 0;

    [RelayCommand]
    private void SeekToFrame(int frameIndex)
    {
        if (!CanNavigateToFrame(frameIndex))
            return;

        if (IsPlaying)
            NotifyPlaybackStopped();

        int target = IsTotalFramesEstimated
            ? frameIndex
            : Math.Clamp(frameIndex, 0, TotalFrames - 1);
        if (SelectedFrameIndex == target)
        {
            // A click on the same ordinal retries an earlier failed frame load.
            SelectedFrameIndexChanged?.Invoke(target);
            return;
        }
        SelectedFrameIndex = target;
        TimelineNavigationStatus = null;
    }

    [RelayCommand]
    private void PreviousFrame()
        => StepFrame(-1);

    [RelayCommand]
    private void NextFrame()
        => StepFrame(1);

    private void StepFrame(int delta)
    {
        if (!CanNavigateToFrame(SelectedFrameIndex))
            return;

        if (IsPlaying)
            NotifyPlaybackStopped();

        long candidate = (long)SelectedFrameIndex + delta;
        int target = (int)Math.Clamp(candidate, 0L, int.MaxValue);
        if (!IsTotalFramesEstimated)
            target = Math.Min(target, TotalFrames - 1);
        if (target == SelectedFrameIndex)
            return;

        SelectedFrameIndex = target;
        TimelineNavigationStatus = null;
    }

    private string? _timelineNavigationStatus;
    public string? TimelineNavigationStatus
    {
        get => _timelineNavigationStatus;
        set
        {
            if (string.Equals(_timelineNavigationStatus, value, StringComparison.Ordinal))
                return;
            _timelineNavigationStatus = value;
            OnPropertyChanged(nameof(TimelineNavigationStatus));
        }
    }
}
