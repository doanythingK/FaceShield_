using FaceShield.Services.Video;
using System.ComponentModel;

namespace FaceShield.ViewModels.Workspace;

public partial class FramePreviewViewModel
{
    private bool _manualMaskKeyframesEnabled;
    private bool _manualMaskKeyframeRefreshInProgress;
    private bool _manualMaskKeyframeHandlerAttached;

    internal void ConfigureManualMaskKeyframes(bool enabled)
    {
        _manualMaskKeyframesEnabled = enabled;

        if (_maskProvider is not FrameMaskProvider provider)
            return;

        ManualMaskKeyframeTimeline.Configure(provider, enabled);

        if (!_manualMaskKeyframeHandlerAttached)
        {
            PropertyChanged += OnManualMaskKeyframePropertyChanged;
            _manualMaskKeyframeHandlerAttached = true;
        }

        if (enabled)
            ApplyInheritedManualMaskIfNeeded();
    }

    private void OnManualMaskKeyframePropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MaskBitmap))
            ApplyInheritedManualMaskIfNeeded();
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

        // Exact stored masks and single-frame automatic face masks are explicit
        // keyframes/overrides and must remain authoritative for this frame.
        if (provider.HasStoredMask(_currentFrameIndex) ||
            provider.TryGetFaceMaskData(_currentFrameIndex, out _))
        {
            return;
        }

        if (!ManualMaskKeyframeTimeline.TryCloneEffectiveStoredMask(
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
