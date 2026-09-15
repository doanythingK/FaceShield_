using FaceShield.Services.Video;
using System.ComponentModel;

namespace FaceShield.ViewModels.Workspace;

public partial class FramePreviewViewModel
{
    private bool _manualMaskKeyframesEnabled;
    private bool _manualMaskKeyframeRefreshInProgress;
    private bool _manualMaskKeyframeHandlerAttached;
    private bool _manualMaskEditHandlerAttached;
    private bool _manualUndoHandlerAttached;

    internal void ConfigureManualMaskKeyframes(
        bool enabled,
        string? videoPath = null,
        int totalFrames = 0)
    {
        _manualMaskKeyframesEnabled = enabled;
        ConfigureManualTrackingContext(videoPath, totalFrames);

        if (_maskProvider is not FrameMaskProvider provider)
            return;

        ManualMaskKeyframeTimeline.Configure(provider, enabled, videoPath);

        if (!_manualMaskKeyframeHandlerAttached)
        {
            PropertyChanged += OnManualMaskKeyframePropertyChanged;
            _manualMaskKeyframeHandlerAttached = true;
        }

        if (!_manualMaskEditHandlerAttached)
        {
            MaskEdited += NotifyManualMaskEditedForTracking;
            _manualMaskEditHandlerAttached = true;
        }

        if (!_manualUndoHandlerAttached)
        {
            // WorkspaceViewModel subscribes its Undo handler during construction.
            // This subscription is attached later from the view, so it observes the
            // already-restored mask and can invalidate tracking based on that result.
            _toolPanel.UndoRequested += OnManualUndoCompleted;
            _manualUndoHandlerAttached = true;
        }

        if (enabled)
            ApplyInheritedManualMaskIfNeeded();
    }

    private void OnManualUndoCompleted()
    {
        if (!_manualMaskKeyframesEnabled || _currentFrameIndex < 0)
            return;
        NotifyManualMaskEditedForTracking(_currentFrameIndex);
    }

    private void OnManualMaskKeyframePropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MaskBitmap))
        {
            ApplyInheritedManualMaskIfNeeded();
            OnPropertyChanged(nameof(CanTrackForward));
        }
    }

    private void ApplyInheritedManualMaskIfNeeded()
    {
        if (!_manualMaskKeyframesEnabled ||
            _manualMaskKeyframeRefreshInProgress ||
            _disposed ||
            _maskDirty ||
            _isDrawing ||
            _currentFrameIndex < 0 ||
            _frameBitmap == null ||
            _maskProvider is not FrameMaskProvider provider)
        {
            return;
        }

        // An exact entry is already a keyframe and remains authoritative. Revalidate
        // any stored tracking segment here because single-frame Auto can replace an
        // exact keyframe without going through the brush MaskEdited event.
        if (provider.HasStoredMask(_currentFrameIndex) ||
            provider.TryGetFaceMaskData(_currentFrameIndex, out _))
        {
            ManualMaskKeyframeTimeline.InvalidateSegmentIfSourceChanged(
                provider,
                _currentFrameIndex);
            return;
        }

        if (!ManualMaskKeyframeTimeline.TryCloneEffectiveKeyframeMask(
                provider,
                _currentFrameIndex,
                out var inherited))
        {
            return;
        }

        if (inherited.PixelSize.Width != _frameBitmap.PixelSize.Width ||
            inherited.PixelSize.Height != _frameBitmap.PixelSize.Height)
        {
            inherited.Dispose();
            return;
        }

        _manualMaskKeyframeRefreshInProgress = true;
        try
        {
            MaskBitmap = inherited;
            _maskUndo.Clear();
            _maskDirty = false;
            RefreshPreview(force: true);
        }
        finally
        {
            _manualMaskKeyframeRefreshInProgress = false;
        }
    }
}
