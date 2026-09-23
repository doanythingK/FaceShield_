using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace FaceShield.ViewModels.Workspace;

public partial class FramePreviewViewModel
{
    private bool _directDisposeDrainInstalled;

    // Installed by the session coordinator before any session is initialized.
    // Unlike the legacy keyframe handler, this covers Auto mode, initial frame
    // loading, and a preview with no manually selected face or mask provider.
    internal void InstallDirectDisposeDrain()
    {
        if (_directDisposeDrainInstalled)
            return;
        _directDisposeDrainInstalled = true;
        PropertyChanged += OnDirectDisposePropertyChanged;
    }

    private void OnDirectDisposePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Dispose always calls SetPreviewBitmap(null), which raises this event
    // even when the previous preview was already null. It runs after _disposed
    // and cancellation, but before _session is cleared or session.Dispose().
        if (!_disposed || e.PropertyName != nameof(PreviewBitmap))
            return;

        PropertyChanged -= OnDirectDisposePropertyChanged;
        _directDisposeDrainInstalled = false;
        CancelManualTrackingCore();

        var pending = new List<Task>(3);
        Task? tracking = Volatile.Read(ref _manualTrackingTask);
        Task? load = Volatile.Read(ref _manualFrameLoadTask);
        Task? playback = Volatile.Read(ref _playbackTask);
        if (tracking != null) pending.Add(tracking);
        if (load != null) pending.Add(load);
        if (playback != null) pending.Add(playback);

        if (pending.Count == 0 || _session == null)
            return;
        try
        {
            // No UI-thread wait: VideoSession closes admission immediately
            // and releases FFmpeg only after all captured tasks have drained.
            _session.DeferDisposeUntil(Task.WhenAll(pending));
        }
        catch (ObjectDisposedException ex)
        {
            Debug.WriteLine($"[FramePreview] session already closing during direct disposal: {ex.Message}");
        }
    }
}
