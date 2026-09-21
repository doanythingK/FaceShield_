using FaceShield.Services.Video;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

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

        FrameMaskProvider? provider = _maskProvider as FrameMaskProvider;
        if (provider != null)
            ManualMaskKeyframeTimeline.Configure(provider, enabled, videoPath);

        if (!enabled)
        {
            DetachManualTrackingContext();
            OnPropertyChanged(nameof(ManualTrackingAvailable));
            OnPropertyChanged(nameof(CanTrackForward));
            return;
        }

        ConfigureManualTrackingContext(videoPath, totalFrames);
        if (provider == null)
            return;

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

        // Target edits have their own baseline, source fingerprint and Undo
        // persistence handler. Never compare an individual face to the global
        // legacy bitmap or clear its dirty flag before its own handler saves it.
        if (HasSelectedManualTarget)
        {
            _manualTrackingPendingSourceValidationFrame = -1;
            return;
        }

        using (var baseline = ManualMaskEditorLayer.CreateEditableMask(
                   provider, _currentFrameIndex, _maskBitmap.PixelSize))
        {
            if (string.Equals(
                    ManualMaskFingerprint.Compute(baseline),
                    ManualMaskFingerprint.Compute(_maskBitmap),
                    StringComparison.Ordinal))
            {
                _maskDirty = false;
                _manualTrackingPendingSourceValidationFrame = -1;
                ManualTrackingStatusText =
                    "되돌리기로 원래 마스크 상태가 복원되어 기존 추적을 유지합니다.";
                OnPropertyChanged(nameof(CanTrackForward));
                return;
            }
        }

        NotifyManualMaskEditedForTracking(_currentFrameIndex);
    }

    private void OnManualMaskKeyframePropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (_disposed)
        {
            CancelManualTrackingCore();
            var pending = new List<Task>(3);
            Task? trackingTask = Volatile.Read(ref _manualTrackingTask);
            Task? loadTask = Volatile.Read(ref _manualFrameLoadTask);
            Task? playbackTask = Volatile.Read(ref _playbackTask);
            if (trackingTask != null)
                pending.Add(trackingTask);
            if (loadTask != null)
                pending.Add(loadTask);
            if (playbackTask != null)
                pending.Add(playbackTask);

            if (pending.Count > 0 && _session != null)
            {
                try
                {
                    _session.DeferDisposeUntil(Task.WhenAll(pending));
                }
                catch (ObjectDisposedException)
                {
                }
            }

            DisposeManualTrackingState();
            return;
        }

        if (e.PropertyName == nameof(IsFrameLoading))
        {
            OnPropertyChanged(nameof(CanTrackForward));
            return;
        }

        if (e.PropertyName == nameof(MaskBitmap))
        {
            ApplyInheritedManualMaskIfNeeded();
            OnPropertyChanged(nameof(CanTrackForward));
        }
    }

    private void ApplyInheritedManualMaskIfNeeded()
    {
        if (!_manualMaskKeyframesEnabled ||
            HasSelectedManualTarget ||
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

        RevalidatePendingManualTrackingSource(provider);

        bool stored = provider.HasStoredMask(_currentFrameIndex);
        bool automatic = provider.TryGetFaceMaskData(_currentFrameIndex, out _);
        if (stored || (automatic && provider.GetStoredMaskFrameIndices().Length == 0))
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

        if (!ManualMaskKeyframeTimeline.TryCloneEffectiveManualMask(
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
