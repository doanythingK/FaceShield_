using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace FaceShield.ViewModels.Workspace;

public partial class IssueEntryViewModel : ObservableObject
{
    public int FrameIndex { get; }
    public string Label { get; }
    public string TimeText { get; }

    [ObservableProperty]
    private bool isResolved;

    private bool _hideResolved;

    public event Action<IssueEntryViewModel>? Resolved;

    public IssueEntryViewModel(int frameIndex, string label, string timeText)
    {
        FrameIndex = frameIndex;
        Label = label;
        TimeText = timeText;
    }

    public bool HideResolved
    {
        get => _hideResolved;
        set
        {
            if (_hideResolved == value)
                return;
            _hideResolved = value;
            OnPropertyChanged(nameof(HideResolved));
            OnPropertyChanged(nameof(IsVisible));
        }
    }

    public bool IsVisible => !_hideResolved || !IsResolved;

    partial void OnIsResolvedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsVisible));
        if (value)
            Resolved?.Invoke(this);
    }
}
