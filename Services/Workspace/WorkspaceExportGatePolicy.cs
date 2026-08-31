using FaceShield.Services.Analysis;
using System;

namespace FaceShield.Services.Workspace;

internal sealed record WorkspaceAutoExportGateState(
    bool Required,
    bool Passed,
    string? Failure,
    AutoMaskRunSummary? CompletedRunSummary,
    bool HybridPolicyAvailable,
    bool AllowHybridCopy,
    string HybridDisableReasons);

internal static class WorkspaceExportGatePolicy
{
    internal static string? GetRequiredYoloCascadeFailure(
        AutoMaskOptions options,
        AutoMaskRunSummary? summary)
    {
        if (!options.EnableYoloRiskCascade ||
            options.FilterProfile != FaceFilterProfile.Yolo)
        {
            return null;
        }

        if (summary == null)
            return "yolo-risk-cascade-summary-missing";
        if (!summary.YoloRiskCascadeEnabled)
            return "yolo-risk-cascade-not-executed";
        if (summary.YoloTimelineFrameCount <= 0 ||
            summary.YoloPtsTimingFrameCount !=
                summary.YoloTimelineFrameCount ||
            summary.YoloUnalignedTimelineFrameCount != 0)
        {
            return "yolo-risk-cascade-incomplete-pts-coverage";
        }

        if (summary.YoloUnalignedRiskFrameCount != 0)
            return "yolo-risk-cascade-unaligned-risk-frames";
        if (!string.Equals(
                summary.YoloCascadeError,
                "none",
                StringComparison.OrdinalIgnoreCase))
        {
            return "yolo-risk-cascade-error";
        }

        if (summary.YoloSecondaryAttemptCount +
                summary.YoloProtectedStoredMaskFrameCount !=
            summary.YoloRiskFrameCount)
        {
            return "yolo-risk-cascade-incomplete-coverage";
        }

        return null;
    }

    internal static WorkspaceAutoExportGateState Begin(
        string hybridDisabledReason)
    {
        return new WorkspaceAutoExportGateState(
            Required: true,
            Passed: false,
            Failure: "auto-run-incomplete",
            CompletedRunSummary: null,
            HybridPolicyAvailable: false,
            AllowHybridCopy: false,
            HybridDisableReasons: hybridDisabledReason);
    }

    internal static WorkspaceAutoExportGateState Complete(
        AutoMaskOptions options,
        AutoMaskRunSummary? summary,
        string hybridDisabledReason)
    {
        string? failure = summary == null
            ? "auto-run-summary-missing"
            : GetRequiredYoloCascadeFailure(options, summary);

        return new WorkspaceAutoExportGateState(
            Required: true,
            Passed: failure == null,
            Failure: failure,
            CompletedRunSummary: summary,
            HybridPolicyAvailable: true,
            AllowHybridCopy: false,
            HybridDisableReasons: hybridDisabledReason);
    }

    internal static WorkspaceAutoExportGateState Restore(
        WorkspaceSnapshot snapshot,
        string hybridDisabledReason)
    {
        bool required = snapshot.AutoExportGateRequired;
        bool passed = snapshot.AutoExportGatePassed;
        string? failure = snapshot.AutoExportGateFailure;

        bool legacyIncompleteRun =
            !required &&
            !passed &&
            snapshot.AutoResumeIndex > 0 &&
            !snapshot.AutoCompleted;
        bool legacyYoloCascadeEvidenceMissing =
            !required &&
            !passed &&
            !string.IsNullOrWhiteSpace(snapshot.AutoRunSignature) &&
            snapshot.AutoRunSignature.Contains(
                "profile=Yolo",
                StringComparison.OrdinalIgnoreCase) &&
            snapshot.AutoRunSignature.Contains(
                "riskCascade=True",
                StringComparison.OrdinalIgnoreCase);

        if (legacyIncompleteRun ||
            legacyYoloCascadeEvidenceMissing)
        {
            required = true;
            passed = false;
            failure = legacyIncompleteRun
                ? "legacy-auto-run-incomplete"
                : "legacy-cascade-evidence-missing";
        }

        return new WorkspaceAutoExportGateState(
            Required: required,
            Passed: passed,
            Failure: failure,
            CompletedRunSummary: null,
            HybridPolicyAvailable:
                snapshot.AutoExportHybridPolicyAvailable,
            AllowHybridCopy: false,
            HybridDisableReasons: hybridDisabledReason);
    }
}
