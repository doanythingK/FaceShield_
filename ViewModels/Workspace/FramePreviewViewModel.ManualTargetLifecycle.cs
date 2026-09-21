using FaceShield.Services.Video;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace FaceShield.ViewModels.Workspace;

public partial class FramePreviewViewModel
{
    internal int ManualTargetEditableFrameIndex => _currentFrameIndex;

    // The workspace restores target data first, then the UI restores only an ID
    // that is still present. Do not use the normal user-switch gate during
    // startup: video initialization can finish after the view is attached.
    internal void RestorePersistedManualTargetSelection()
    {
        if (_manualTargets == null || string.IsNullOrWhiteSpace(_manualTargetsVideoPath) ||
            _disposed || _maskDirty || _isDrawing)
            return;

        Guid? saved = ManualOverlayTargetSelectionStore.Load(_manualTargetsVideoPath);
        if (!saved.HasValue || _selectedManualTarget?.Id == saved.Value)
            return;
        ManualTargetChoice? choice = ManualTargetChoices.FirstOrDefault(item => item.Id == saved.Value);
        if (choice == null)
            return;

        _manualTargets.SelectTarget(choice.Id);
        _selectedManualTarget = choice;
        OnPropertyChanged(nameof(SelectedManualTarget));
        OnPropertyChanged(nameof(HasSelectedManualTarget));
        ReplaceEditorWithSelectedTarget();
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
            Debug.WriteLine($"[ManualTarget] Target edit remains pending after save failure: {ex}");
            ManualTrackingStatusText =
                $"수동 얼굴 마스크 저장 실패: {ex.Message} 편집 내용은 현재 화면에 남아 있습니다. 저장 경로를 확인한 뒤 다시 시도하세요.";
            return false;
        }
    }
}
