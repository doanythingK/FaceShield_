namespace FaceShield.ViewModels.Workspace;

public partial class FramePreviewViewModel
{
    // A failed target-owned save must roll the timeline back to the frame
    // whose editable bitmap is still on screen, not to a stale new selection.
    internal int ManualTargetEditableFrameIndex =>
        _manualTargets != null && _selectedManualTarget != null &&
        _maskBitmap != null && _frameBitmap != null
            ? _currentFrameIndex
            : -1;

    // A face selection can change without a subsequent exact frame seek.
    internal void RefreshManualTargetPreview() => ComposeTargetPreview();
}
