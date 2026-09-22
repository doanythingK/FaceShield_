using System;

namespace FaceShield.ViewModels.Workspace;

public partial class FramePreviewViewModel
{
    private Action<int>? _onConfirmedManualTargetEof;

    // The owning WorkspaceViewModel updates its timeline on the UI thread.
    // A correction boundary, interrupted decode or tracking failure must
    // never call this callback or turn a metadata estimate into an exact count.
    internal void ConfigureConfirmedManualTargetEof(Action<int> onConfirmedEof)
        => _onConfirmedManualTargetEof = onConfirmedEof
            ?? throw new ArgumentNullException(nameof(onConfirmedEof));
}
