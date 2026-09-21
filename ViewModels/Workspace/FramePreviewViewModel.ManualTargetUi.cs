using System;
using System.Linq;

namespace FaceShield.ViewModels.Workspace;

public partial class FramePreviewViewModel
{
    // Selection changes need a composite refresh even without a frame seek.
    internal void RefreshManualTargetPreview() => ComposeTargetPreview();

    internal bool HasTargetOwnedCorrections => _manualTargets?.Snapshot()
        .Any(target => target.Keyframes.Count > 0) == true;

    internal bool CanExportLegacyMask()
    {
        if (!HasTargetOwnedCorrections)
            return true;
        ManualTrackingStatusText =
            "대상별 마스크를 누락하는 기존 영상 내보내기는 차단했습니다. " +
            "얼굴별 키프레임과 추적 상태는 별도로 저장되어 있습니다. " +
            "대상별 내보내기 연결 후 영상으로 저장할 수 있습니다.";
        return false;
    }
}
