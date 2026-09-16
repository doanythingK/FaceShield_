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
            _operationLifetime.TryBegin,
            _operationLifetime.End,
            PersistManualTrackingWorkspaceAsync);
        _operationLifetime.AdmissionClosed +=
            FramePreview.CancelManualTrackingForShutdown;
    }

    private async Task PersistManualTrackingWorkspaceAsync()
    {
        if (_workspacePersistence == null)
            return;

        FramePreview.PersistCurrentMask();
        var snapshot = BuildSnapshot();
        await _workspacePersistence.QueueSaveAsync(snapshot).ConfigureAwait(false);

        // QueueSaveAsync uses latest-wins and an older request can complete after
        // being skipped as stale. Do not publish tracking metadata until the current
        // persistence tail has actually drained, so the promoted source keyframe is
        // durable in either this snapshot or a newer one.
        await _workspacePersistence.FlushAsync().ConfigureAwait(false);
    }
}
