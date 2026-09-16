using FaceShield.Services.Video;
using System;
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
        if (!_manualMaskKeyframesEnabled ||
            _currentFrameIndex < 0 ||
            _maskBitmap == null ||
            _maskProvider is not FrameMaskProvider provider)
        {
            return;
        }

        // Undo can return a tracked/inherited frame exactly to the effective mask it
        // had before editing. Do not persist that no-op as a new explicit keyframe,
        // otherwise the existing track would be cut at this frame.
        if (ManualMaskKeyframeTimeline.TryCloneEffectiveKeyframeMask(
                provider,
                _currentFrameIndex,
                out var baseline))
        {
            using (baseline)
            {
                if (baseline.PixelSize.Width == _maskBitmap.PixelSize.Width &&
                    baseline.PixelSize.Height == _maskBitmap.PixelSize.Height &&
                    string.Equals(
                        ManualMaskFingerprint.Compute(baseline),
                        ManualMaskFingerprint.Compute(_maskBitmap),
                        StringComparison.Ordinal))
                {
                    _maskDirty = false;
                    ManualTrackingStatusText =
                        "되돌리기로 원래 마스크 상태가 복원되어 기존 추적을 유지합니다.";
                    OnPropertyChanged(nameof(CanTrackForward));
                    return;
                }
            }
        }

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
            if (ManualMaskKeyframeTimeline.InvalidateSegmentIfSourceChanged(
                    provider,
                    _currentFrameIndex))
            {
                ManualTrackingStatusText =
                    "키프레임 마스크가 변경되어 기존 추적을 적용하지 않습니다. " +
                    "현재 프레임에서 다시 자동 추적하세요.";
            }
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
