from pathlib import Path


def read_exact(p: Path) -> str:
    with p.open("r", encoding="utf-8", newline="") as f:
        return f.read()


def write_exact(p: Path, text: str) -> None:
    with p.open("w", encoding="utf-8", newline="") as f:
        f.write(text)


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = read_exact(p)
    candidates = [(old, new)]
    if "\n" in old:
        candidates.append((old.replace("\n", "\r\n"), new.replace("\n", "\r\n")))
    for old_candidate, new_candidate in candidates:
        count = text.count(old_candidate)
        if count == 1:
            write_exact(p, text.replace(old_candidate, new_candidate, 1))
            return
        if count > 1:
            raise RuntimeError(f"Expected one match in {path}, found {count}")
    raise RuntimeError(f"Patch target not found in {path}: {old[:120]!r}")


write_exact(Path("Services/Workspace/WorkspaceStateMapper.cs"), """using FaceShield.Enums.Workspace;
using System;

namespace FaceShield.Services.Workspace
{
    internal sealed record WorkspaceStateCapture(
        string VideoPath,
        WorkspaceMode Mode,
        int SelectedFrameIndex,
        double ViewStartSeconds,
        double SecondsPerScreen,
        double TimelineExtentSeconds,
        int AutoResumeIndex,
        bool AutoCompleted,
        string? AutoRunSignature,
        string? AutoExecutionSignature,
        bool AutoExportGateRequired,
        bool AutoExportGatePassed,
        string? AutoExportGateFailure,
        bool AutoExportHybridPolicyAvailable,
        bool AutoExportAllowHybridCopy,
        string? AutoExportHybridDisableReasons);

    internal sealed record WorkspaceRestoreState(
        int AutoResumeIndex,
        bool AutoCompleted,
        string? AutoRunSignature,
        string? AutoExecutionSignature,
        WorkspaceAutoExportGateState ExportGateState,
        double SecondsPerScreen,
        double TimelineExtentSeconds,
        double RequestedViewStartSeconds,
        int SelectedFrameIndex);

    internal static class WorkspaceStateMapper
    {
        internal static WorkspaceSnapshot CreateSnapshot(
            WorkspaceStateCapture state,
            DateTimeOffset lastOpened)
        {
            if (state == null)
                throw new ArgumentNullException(nameof(state));

            return new WorkspaceSnapshot(
                state.VideoPath,
                state.Mode,
                state.SelectedFrameIndex,
                state.ViewStartSeconds,
                state.SecondsPerScreen,
                lastOpened,
                state.AutoResumeIndex,
                state.AutoCompleted,
                state.AutoRunSignature,
                state.AutoExportGateRequired,
                state.AutoExportGatePassed,
                state.AutoExportGateFailure,
                state.AutoExportHybridPolicyAvailable,
                state.AutoExportAllowHybridCopy,
                state.AutoExportHybridDisableReasons,
                state.AutoExecutionSignature,
                state.TimelineExtentSeconds);
        }

        internal static WorkspaceRestoreState CreateRestoreState(
            WorkspaceSnapshot snapshot,
            double currentSecondsPerScreen,
            int totalFrames,
            string hybridDisabledReason)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));

            double secondsPerScreen = snapshot.SecondsPerScreen > 0
                ? snapshot.SecondsPerScreen
                : currentSecondsPerScreen;
            int selectedFrameIndex = totalFrames <= 0
                ? -1
                : Math.Clamp(snapshot.SelectedFrameIndex, 0, totalFrames - 1);

            return new WorkspaceRestoreState(
                snapshot.AutoResumeIndex,
                snapshot.AutoCompleted,
                snapshot.AutoRunSignature,
                snapshot.AutoExecutionSignature,
                WorkspaceExportGatePolicy.Restore(snapshot, hybridDisabledReason),
                secondsPerScreen,
                snapshot.TimelineExtentSeconds,
                snapshot.ViewStartSeconds,
                selectedFrameIndex);
        }

        internal static double ClampViewStart(
            double requestedViewStartSeconds,
            double timelineExtentSeconds,
            double secondsPerScreen)
        {
            double maxStart = Math.Max(
                0,
                timelineExtentSeconds - Math.Max(0, secondsPerScreen));
            return Math.Clamp(requestedViewStartSeconds, 0, maxStart);
        }
    }
}
""")

replace_once(
    "ViewModels/Pages/WorkspaceViewModel.cs",
    """        private WorkspaceSnapshot BuildSnapshot()\n        {\n            return new WorkspaceSnapshot(\n                FrameList.VideoPath,\n                Mode,\n                FrameList.SelectedFrameIndex,\n                FrameList.ViewStartSeconds,\n                FrameList.SecondsPerScreen,\n                DateTimeOffset.Now,\n                _autoResumeIndex,\n                _autoCompleted,\n                _autoRunSignature,\n                _autoExportGateRequired,\n                _autoExportGatePassed,\n                _autoExportGateFailure,\n                _autoExportHybridPolicyAvailable,\n                _autoExportAllowHybridCopy,\n                _autoExportHybridDisableReasons,\n                _autoExecutionSignature,\n                FrameList.TimelineExtentSeconds);\n        }\n""",
    """        private WorkspaceSnapshot BuildSnapshot()\n        {\n            var state = new WorkspaceStateCapture(\n                FrameList.VideoPath,\n                Mode,\n                FrameList.SelectedFrameIndex,\n                FrameList.ViewStartSeconds,\n                FrameList.SecondsPerScreen,\n                FrameList.TimelineExtentSeconds,\n                _autoResumeIndex,\n                _autoCompleted,\n                _autoRunSignature,\n                _autoExecutionSignature,\n                _autoExportGateRequired,\n                _autoExportGatePassed,\n                _autoExportGateFailure,\n                _autoExportHybridPolicyAvailable,\n                _autoExportAllowHybridCopy,\n                _autoExportHybridDisableReasons);\n            return WorkspaceStateMapper.CreateSnapshot(state, DateTimeOffset.Now);\n        }\n""")

replace_once(
    "ViewModels/Pages/WorkspaceViewModel.cs",
    """        private void ApplySnapshot(WorkspaceSnapshot snapshot)\n        {\n            if (snapshot == null)\n                return;\n\n            _autoResumeIndex = snapshot.AutoResumeIndex;\n            _autoCompleted = snapshot.AutoCompleted;\n            _autoRunSignature = snapshot.AutoRunSignature;\n            _autoExecutionSignature = snapshot.AutoExecutionSignature;\n            ApplyAutoExportGateState(\n                WorkspaceExportGatePolicy.Restore(\n                    snapshot,\n                    HybridCopyDisabledReason));\n\n            double secondsPerScreen = snapshot.SecondsPerScreen;\n            if (secondsPerScreen <= 0)\n                secondsPerScreen = FrameList.SecondsPerScreen;\n            FrameList.SecondsPerScreen = secondsPerScreen;\n\n            FrameList.RestoreTimelineExtentSeconds(snapshot.TimelineExtentSeconds);\n            double maxStart = Math.Max(0, FrameList.TimelineExtentSeconds - FrameList.SecondsPerScreen);\n            FrameList.ViewStartSeconds = Math.Clamp(snapshot.ViewStartSeconds, 0, maxStart);\n\n            int index;\n            if (FrameList.TotalFrames <= 0)\n                index = -1;\n            else\n                index = Math.Clamp(snapshot.SelectedFrameIndex, 0, FrameList.TotalFrames - 1);\n\n            FrameList.SelectedFrameIndex = index;\n        }\n""",
    """        private void ApplySnapshot(WorkspaceSnapshot snapshot)\n        {\n            if (snapshot == null)\n                return;\n\n            WorkspaceRestoreState state = WorkspaceStateMapper.CreateRestoreState(\n                snapshot,\n                FrameList.SecondsPerScreen,\n                FrameList.TotalFrames,\n                HybridCopyDisabledReason);\n\n            _autoResumeIndex = state.AutoResumeIndex;\n            _autoCompleted = state.AutoCompleted;\n            _autoRunSignature = state.AutoRunSignature;\n            _autoExecutionSignature = state.AutoExecutionSignature;\n            ApplyAutoExportGateState(state.ExportGateState);\n\n            FrameList.SecondsPerScreen = state.SecondsPerScreen;\n            FrameList.RestoreTimelineExtentSeconds(state.TimelineExtentSeconds);\n            FrameList.ViewStartSeconds = WorkspaceStateMapper.ClampViewStart(\n                state.RequestedViewStartSeconds,\n                FrameList.TimelineExtentSeconds,\n                FrameList.SecondsPerScreen);\n            FrameList.SelectedFrameIndex = state.SelectedFrameIndex;\n        }\n""")

print("Phase C1 workspace state mapper extraction applied.")
