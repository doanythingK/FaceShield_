using CommunityToolkit.Mvvm.Input;
using System;

namespace FaceShield.ViewModels.Workspace;

public partial class FrameListViewModel
{
    // These controls navigate ordinal frames, never seconds * average FPS. The
    // ordinary selection event still drives the manual player's exact frame load.
    [RelayCommand]
    private void PreviousFrame()
        => StepFrame(-1);

    [RelayCommand]
    private void NextFrame()
        => StepFrame(1);

    private void StepFrame(int delta)
    {
        if (_disposed || !IsPlaybackEnabled ||
            _canUserNavigate?.Invoke() == false ||
            SelectedFrameIndex < 0 || TotalFrames <= 0)
        {
            return;
        }

        if (IsPlaying)
            NotifyPlaybackStopped();

        long candidate = (long)SelectedFrameIndex + delta;
        int target = (int)Math.Clamp(candidate, 0L, int.MaxValue);
        if (!IsTotalFramesEstimated)
            target = Math.Min(target, TotalFrames - 1);
        if (target == SelectedFrameIndex)
            return;

        SelectedFrameIndex = target;
    }

    // Selection failures must be visible rather than silently leaving the old frame.
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
