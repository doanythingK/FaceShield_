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

    private Task PersistManualTrackingWorkspaceAsync()
    {
        if (_workspacePersistence == null)
            return Task.CompletedTask;

        FramePreview.PersistCurrentMask();
        var snapshot = BuildSnapshot();
        return _workspacePersistence.QueueSaveAsync(snapshot);
    }
}
