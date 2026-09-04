using FaceShield.Enums.Workspace;
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
