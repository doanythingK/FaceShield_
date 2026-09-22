using System.Threading.Tasks;

namespace FaceShield.ViewModels.Pages;

public partial class WorkspaceViewModel
{
    private bool _manualTrackingOwnershipConfigured;

    internal void ConfigureManualTrackingOwnership()
    {
        if (_manualTrackingOwnershipConfigured)
            return;

        _manualTrackingOwnershipConfigured = true;
        FramePreview.ConfigureManualTrackingOwnership(
            _operationLifetime.TryBeginExclusiveProcessing,
            _operationLifetime.EndExclusiveProcessing,
            PersistManualTrackingWorkspaceAsync);
        _operationLifetime.AdmissionClosed +=
            FramePreview.CancelManualTrackingForShutdown;
    }

    private async Task PersistManualTrackingWorkspaceAsync()
    {
        if (_workspacePersistence == null)
            return;

        FramePreview.PersistCurrentMask();
        // Capture scalar state inside the same ordering gate as the provider mask
        // snapshot and request publication, just like ordinary workspace saves.
        await _workspacePersistence.QueueSaveAsync(BuildSnapshot).ConfigureAwait(false);

        // QueueSaveAsync uses latest-wins and an older request can complete after
        // being skipped as stale. Do not publish tracking metadata until the current
        // persistence tail has actually drained, so the promoted source keyframe is
        // durable in either this snapshot or a newer one.
        await _workspacePersistence.FlushAsync().ConfigureAwait(false);
    }
}
