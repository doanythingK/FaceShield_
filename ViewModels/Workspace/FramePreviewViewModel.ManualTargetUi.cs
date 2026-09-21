using System;
using System.Linq;

namespace FaceShield.ViewModels.Workspace;

public partial class FramePreviewViewModel
{
    // The editor keeps each face's bitmap separate. This refresh is also used
    // after a selection change, when there is no subsequent frame-load event.
    internal void RefreshManualTargetPreview() => ComposeTargetPreview();

    // Do not advertise the existing global-timeline export as supporting
    // target-owned tracking before the export coordinator is connected.
    internal bool HasTargetOwnedCorrections => _manualTargets?.Snapshot()
        .Any(target => target.Keyframes.Count > 0) == true;
}
