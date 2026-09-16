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
            PersistWorkspaceStateImmediate);
        _operationLifetime.AdmissionClosed +=
            FramePreview.CancelManualTrackingForShutdown;
    }
}
