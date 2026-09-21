using FaceShield.Services.Video;
using System;

namespace FaceShield.ViewModels.Workspace;

public partial class FramePreviewViewModel
{
    /// <summary>
    /// An explicitly absent face is a zero-alpha correction for ONLY the
    /// selected target. It terminates this target's earlier tracking interval;
    /// other manual targets and Auto/legacy masks are not modified.
    /// </summary>
    internal bool MarkSelectedManualTargetAbsentAtCurrentFrame()
    {
        if (!CanSwitchManualTarget() || _manualTargets == null ||
            _selectedManualTarget == null || _frameBitmap == null ||
            _maskBitmap == null || _currentFrameIndex < 0)
            return false;

        // Save the current editable pixels before replacing them. Failure
        // leaves the existing bitmap/Undo in place and prevents the operation.
        CommitSelectedManualTargetEdit();

        // This is a user edit, not a background refresh: Undo should restore
        // the mask that was visible immediately before the absence correction.
        PushUndoSnapshot(_maskBitmap);
        PreserveManualTargetUndo();

        int width = _frameBitmap.PixelSize.Width;
        int height = _frameBitmap.PixelSize.Height;
        var absent = new ManualOverlayStoredKeyframe(
            _currentFrameIndex, width, height,
            new byte[ManualOverlayStateStore.CheckedPixelCount(width, height)]);
        ManualOverlayTargetEditCommitter.Commit(
            _manualTargetsVideoPath
                ?? throw new InvalidOperationException("Manual target video path is missing."),
            _manualTargets, _selectedManualTarget.Id, absent);

        // Publish the empty editor only AFTER durable target publication. The
        // archive survives ReplaceEditorWithSelectedTarget's shared Undo clear.
        _maskDirty = false;
        _manualTrackingPendingSourceValidationFrame = -1;
        ReplaceEditorWithSelectedTarget();
        RestoreManualTargetUndo();
        OnPropertyChanged(nameof(CanTrackSelectedManualTarget));
        ComposeTargetPreview();
        return true;
    }
}
