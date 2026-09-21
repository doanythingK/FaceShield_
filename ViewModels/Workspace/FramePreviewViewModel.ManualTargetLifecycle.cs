using FaceShield.Services.Video;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace FaceShield.ViewModels.Workspace;

public partial class FramePreviewViewModel
{
    private bool _manualTargetAutoLifecycleAttached;

    // A workspace can export without ever attaching WorkspaceView. Install
    // this guard at the session coordinator rather than only in the view.
    internal void InstallManualTargetExportGuard()
        => _toolPanel.ManualTargetExportGuard = TryCommitPendingManualTargetEdit;

    // The global Auto refresh rebuilds the legacy editor and clears its Undo
    // stack. A selected target owns different pixels and a different stack.
    // Attach after the legacy handlers so target restoration/archiving runs last.
    private void AttachManualTargetAutoLifecycle()
    {
        if (_manualTargetAutoLifecycleAttached)
            return;
        _toolPanel.PropertyChanged += OnManualTargetAutoLifecycleChanged;
        // Direct programmatic frame reloads do not always pass through the
        // view's pointer/keyboard tunnel. Preserve the latest pushed snapshot
        // at the actual brush/Undo completion boundary as an additional guard.
        MaskEdited += OnManualTargetEditCompletedArchiveUndo;
        _toolPanel.UndoRequested += OnManualTargetUndoCompletedArchiveUndo;
        _manualTargetAutoLifecycleAttached = true;
    }

    private void OnManualTargetEditCompletedArchiveUndo(int frameIndex)
    {
        if (!_disposed && _selectedManualTarget != null &&
            frameIndex == _currentFrameIndex)
            PreserveManualTargetUndo();
    }

    private void OnManualTargetUndoCompletedArchiveUndo()
    {
        if (!_disposed && _selectedManualTarget != null)
            PreserveManualTargetUndo(clearWhenEmpty: true);
    }

    private void OnManualTargetAutoLifecycleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed)
        {
            _toolPanel.PropertyChanged -= OnManualTargetAutoLifecycleChanged;
            MaskEdited -= OnManualTargetEditCompletedArchiveUndo;
            _toolPanel.UndoRequested -= OnManualTargetUndoCompletedArchiveUndo;
            _manualTargetAutoLifecycleAttached = false;
            return;
        }
        if (e.PropertyName != nameof(ToolPanelViewModel.IsAutoRunning) ||
            _manualTargets == null || _selectedManualTarget == null)
            return;

        if (_toolPanel.IsAutoRunning)
        {
            // The toolbar's commit guard normally saves before Auto begins;
            // this also protects programmatic Auto transitions after attachment.
            if (TryCommitPendingManualTargetEdit())
                PreserveManualTargetUndo();
            return;
        }

        // The legacy handler runs earlier and may have replaced the editor
        // with a global mask. Never overwrite a dirty target after save failure.
        if (_maskDirty || _isDrawing || _frameBitmap == null || _currentFrameIndex < 0)
            return;
        ReplaceEditorWithSelectedTarget();
        RestoreManualTargetUndo();
        ComposeTargetPreview();
    }

    // The workspace restores target data first, then the UI restores only an ID
    // that is still present. Do not use the normal user-switch gate during
    // startup: video initialization can finish after the view is attached.
    internal void RestorePersistedManualTargetSelection()
    {
        if (_manualTargets == null || string.IsNullOrWhiteSpace(_manualTargetsVideoPath) ||
            _disposed)
            return;
        AttachManualTargetAutoLifecycle();
        if (_maskDirty || _isDrawing)
            return;

        Guid? saved = ManualOverlayTargetSelectionStore.Load(_manualTargetsVideoPath);
        if (!saved.HasValue || _selectedManualTarget?.Id == saved.Value)
            return;
        ManualTargetChoice? choice = ManualTargetChoices.FirstOrDefault(item => item.Id == saved.Value);
        if (choice == null)
            return;

        // The optional saved ID can differ from the workspace's first target.
        // Treat this as a real selection change so the first target's in-memory
        // Undo stack is not lost when its editor is replaced.
        PreserveManualTargetUndo();
        _manualTargets.SelectTarget(choice.Id);
        _selectedManualTarget = choice;
        OnPropertyChanged(nameof(SelectedManualTarget));
        OnPropertyChanged(nameof(HasSelectedManualTarget));
        ReplaceEditorWithSelectedTarget();
        RestoreManualTargetUndo();
        OnPropertyChanged(nameof(CanTrackSelectedManualTarget));
    }

    internal void PersistManualTargetSelection()
    {
        if (_manualTargets == null || _selectedManualTarget == null ||
            string.IsNullOrWhiteSpace(_manualTargetsVideoPath) || _disposed)
            return;
        // Selection metadata is optional UI state; an unwritable selection file
        // must never prevent mask editing or overwrite a valid target document.
        try
        {
            ManualOverlayTargetSelectionStore.Save(_manualTargetsVideoPath, _selectedManualTarget.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Debug.WriteLine($"[ManualTarget] Could not persist selected face: {ex.Message}");
        }
    }

    // Write only selected target alpha, never its editable raster into the
    // provider's legacy/global manual layer. The throwing form is used where
    // navigation can show its own error and roll back the frame selection.
    internal void CommitPendingManualTargetEdit()
    {
        if (_manualTargets == null || _selectedManualTarget == null || _disposed)
            return;
        CommitSelectedManualTargetEdit();
    }

    internal void ReportManualTargetFailure(string operation, Exception ex)
    {
        Debug.WriteLine($"[ManualTarget] {operation} failed: {ex}");
        ManualTrackingStatusText =
            $"{operation} 실패: {ex.Message} 현재 작업 화면의 편집 상태를 확인한 뒤 다시 시도하세요.";
    }

    // ToolPanel.Save invokes its guard BEFORE WorkspaceViewModel.OnSaveRequested
    // calls the legacy PersistCurrentMask. A failed target write must block that
    // event, preserve the dirty bitmap and expose a reason to the user.
    internal bool TryCommitPendingManualTargetEdit()
    {
        try
        {
            CommitPendingManualTargetEdit();
            return true;
        }
        catch (Exception ex)
        {
            ReportManualTargetFailure("수동 얼굴 마스크 저장", ex);
            return false;
        }
    }
}
