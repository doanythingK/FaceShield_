param(
    [string]$QualityClip = ".tmp/srcTest-smoke/smoke-0600-3s.mp4",
    [string]$AutoTuneClip = ".tmp/srcTest-smoke/smoke-0600-5s.mp4",
    [string]$RoiHitClip = ".tmp/srcTest-smoke/smoke-0900-2s.mp4",
    [switch]$RunExportSmoke,
    [string]$ExportClip = ".tmp/srcTest-smoke/smoke-1200-2s.mp4",
    [switch]$RunMediumAuto,
    [string]$MediumAutoClip = ".tmp/srcTest-smoke/smoke-1200-30s.mp4",
    [switch]$RunMediumExport,
    [string]$MediumExportClip = ".tmp/srcTest-smoke/smoke-1200-30s.mp4",
    [switch]$RunLongAutoTune,
    [string]$LongAutoTuneClip = ".tmp/srcTest-smoke/smoke-1200-30s.mp4",
    [switch]$RunYoloState,
    [switch]$RunYoloRepresentativeGate,
    [switch]$RunYoloExtendedGate,
    [switch]$RunYoloExtendedExportGate,
    [switch]$RunYoloGuiSmokeState,
    [switch]$RequireYoloGuiSmokeManualPass,
    [switch]$RunYoloManualReadinessState,
    [switch]$RunYoloManualGateSummary,
    [switch]$RunYoloReadyForHumanGatesState,
    [string]$YoloManualGateSummaryPath = ".tmp\yolo-manual-gates\manual-gate-summary.md",
    [string]$YoloEvidenceReportPath = ".tmp\yolo-manual-gates\goal-evidence-report.md",
    [switch]$AllowCompletedYoloFullGt,
    [switch]$AllowCompletedYoloGuiSmoke,
    [switch]$RequireYoloComplete,
    [string]$YoloFullGtPredictionCsv = "",
    [string]$YoloFullGtPredictionLog = ".tmp\yolo-ten-minute-detection-smoke\yolo-ten-minute-yolo-only-20260523-022022.log",
    [double]$YoloFullGtMinIou = 0.50,
    [int]$YoloFullGtMaxMisses = 0,
    [int]$YoloFullGtMaxFalsePositives = 0,
    [int]$YoloFullGtMaxLowIou = 0,
    [switch]$AllowFullGtQualityGateFailure,
    [switch]$RunYoloProfileState,
    [switch]$RunYoloConclusionState,
    [switch]$RunYoloDistributionState,
    [switch]$RunYoloCropReview,
    [switch]$RunYoloFullGtReviewedCandidateState,
    [string]$YoloCropReviewPassCsv = ".tmp/yolo-crops/test-0900-yolo5face/crop-review.csv",
    [string]$YoloCropReviewFailCsv = ".tmp/yolo-crops/test-0600-30s-yolo5face/crop-review.csv",
    [string]$YoloRepresentativeQualityClip = ".tmp/srcTest-smoke/smoke-0600-3s.mp4",
    [string]$YoloRepresentativeModelPath = "",
    [string]$YoloExtendedQualityClip = ".tmp/srcTest-smoke/smoke-0600-30s.mp4",
    [string]$YoloExtendedModelPath = "",
    [string]$YoloExtendedExportQualityClip = ".tmp/srcTest-smoke/smoke-0600-30s.mp4",
    [string]$YoloExtendedExportModelPath = ""
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$smoke = Join-Path $repo "scripts\run-srcTest-smoke.ps1"
$trackPostprocessVerify = Join-Path $repo "scripts\verify-face-track-postprocess.ps1"
$faceTrackSceneCutGuardVerify = Join-Path $repo "scripts\verify-face-track-scene-cut-guard.ps1"
$yoloTemporalSmoothingCutBoundaryVerify = Join-Path $repo "scripts\verify-yolo-temporal-smoothing-cut-boundary.ps1"
$autoMaskSparseSceneCutGuardVerify = Join-Path $repo "scripts\verify-automask-sparse-scene-cut-guard.ps1"
$autoMaskSparseMaterializeSceneCutVerify = Join-Path $repo "scripts\verify-automask-sparse-materialize-scene-cut.ps1"
$yoloQualityReviewChecklistVerify = Join-Path $repo "scripts\verify-yolo-quality-review-checklist.ps1"
$yoloFollowupQualityEvidenceVerify = Join-Path $repo "scripts\verify-yolo-followup-quality-evidence.ps1"
$yoloProblemSpanRunnerVerify = Join-Path $repo "scripts\verify-yolo-problem-span-runner-state.ps1"
$yoloPseudoGtEvidenceVerify = Join-Path $repo "scripts\verify-yolo-pseudo-gt-evidence-state.ps1"
$yoloPseudoGtTileInputVerify = Join-Path $repo "scripts\verify-yolo-pseudo-gt-tile-input-state.ps1"
$yoloPseudoGtFaceRunnerVerify = Join-Path $repo "scripts\verify-yolo-pseudo-gt-face-runner-state.ps1"
$yoloPseudoGtFaceVerificationInputVerify = Join-Path $repo "scripts\verify-yolo-pseudo-gt-face-verification-input-state.ps1"
$yoloPseudoGtFaceVerificationRunnerVerify = Join-Path $repo "scripts\verify-yolo-pseudo-gt-face-verification-runner-state.ps1"
$yoloPseudoGtPersonObjectInputVerify = Join-Path $repo "scripts\verify-yolo-pseudo-gt-person-object-input-state.ps1"
$yoloPseudoGtReviewDraftVerify = Join-Path $repo "scripts\verify-yolo-pseudo-gt-review-draft-state.ps1"
$yoloPseudoGtReviewVisualPackageVerify = Join-Path $repo "scripts\verify-yolo-pseudo-gt-review-visual-package-state.ps1"
$yoloPseudoGtReviewDraftApplyVerify = Join-Path $repo "scripts\verify-yolo-pseudo-gt-review-draft-apply-state.ps1"
$yoloPseudoGtReviewClosureVerify = Join-Path $repo "scripts\verify-yolo-pseudo-gt-review-closure-state.ps1"
$yoloPseudoGtSeparationVerify = Join-Path $repo "scripts\verify-yolo-pseudo-gt-separation-state.ps1"
$autoNoDetectionReviewVerify = Join-Path $repo "scripts\verify-auto-no-detection-review-state.ps1"
$yoloDetectionOverlayVideoVerify = Join-Path $repo "scripts\verify-yolo-detection-overlay-video.ps1"
$yoloAspectRatioFilterVerify = Join-Path $repo "scripts\verify-yolo-aspect-ratio-filter.ps1"
$yoloFinalMaskCleanupVerify = Join-Path $repo "scripts\verify-yolo-final-mask-cleanup.ps1"
$yoloStateVerify = Join-Path $repo "scripts\verify-yolo-state.ps1"
$yoloCompletionAuditVerify = Join-Path $repo "scripts\verify-yolo-completion-audit-state.ps1"
$yoloProfileStateVerify = Join-Path $repo "scripts\verify-yolo-profile-state.ps1"
$yoloConclusionStateVerify = Join-Path $repo "scripts\verify-yolo-conclusion-state.ps1"
$yoloDistributionStateVerify = Join-Path $repo "scripts\verify-yolo-distribution-state.ps1"
$yoloCropReviewVerify = Join-Path $repo "scripts\verify-yolo-crop-review.ps1"
$yoloFullGtReviewedCandidateStateVerify = Join-Path $repo "scripts\verify-yolo-full-gt-reviewed-candidate-state.ps1"
$yoloRepresentativeGateVerify = Join-Path $repo "scripts\verify-yolo-representative-gate.ps1"
$yoloExtendedGateVerify = Join-Path $repo "scripts\verify-yolo-extended-gate.ps1"
$yoloExtendedExportGateVerify = Join-Path $repo "scripts\verify-yolo-extended-export-gate.ps1"
$yoloGuiSmokeStateVerify = Join-Path $repo "scripts\verify-yolo-gui-smoke-state.ps1"
$yoloManualReadinessStateVerify = Join-Path $repo "scripts\verify-yolo-manual-readiness-state.ps1"
$yoloManualGateHelper = Join-Path $repo "scripts\open-yolo-manual-gates.ps1"
$yoloReadyForHumanGatesStateVerify = Join-Path $repo "scripts\verify-yolo-ready-for-human-gates-state.ps1"

function Invoke-ScriptStep([string]$Name, [string]$ScriptPath, [string[]]$Arguments) {
    Write-Host "[AutoMosaicVerify] start $Name"
    $oldErrorAction = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $ScriptPath @Arguments 2>&1
        $exitCode = $LASTEXITCODE
        $text = ($output | Out-String)
        Write-Host $text
        if ($exitCode -ne 0) {
            throw "$Name failed with exit code $exitCode"
        }
    }
    finally {
        $ErrorActionPreference = $oldErrorAction
    }

    Write-Host "[AutoMosaicVerify] pass $Name"
    return $text
}

function Invoke-Step([string]$Name, [string[]]$Arguments) {
    return Invoke-ScriptStep $Name $smoke $Arguments
}

function Assert-Contains([string]$Name, [string]$Text, [string]$Pattern) {
    if ($Text -notmatch $Pattern) {
        throw "$Name did not contain expected pattern: $Pattern"
    }
}

if ($RequireYoloComplete) {
    if (-not (Test-Path $yoloCompletionAuditVerify)) {
        throw "YOLO completion audit verifier not found: $yoloCompletionAuditVerify"
    }

    $completeGuardArgs = @(
        "-MinIou", "$YoloFullGtMinIou",
        "-MaxMisses", "$YoloFullGtMaxMisses",
        "-MaxFalsePositives", "$YoloFullGtMaxFalsePositives",
        "-MaxLowIou", "$YoloFullGtMaxLowIou",
        "-RequireComplete"
    )
    if (-not [string]::IsNullOrWhiteSpace($YoloFullGtPredictionCsv)) {
        $completeGuardArgs += @("-PredictionCsv", $YoloFullGtPredictionCsv)
    }
    else {
        $completeGuardArgs += @("-PredictionLog", $YoloFullGtPredictionLog)
    }
    if ($AllowFullGtQualityGateFailure) {
        $completeGuardArgs += "-AllowQualityGateFailure"
    }

    $completeGuardOutput = Invoke-ScriptStep "yolo-require-complete-guard" $yoloCompletionAuditVerify $completeGuardArgs
    Assert-Contains "yolo-require-complete-guard" $completeGuardOutput "\[YoloCompletionAuditVerify\] all requested checks passed"
    $RunYoloState = $true
}

if (-not (Test-Path (Join-Path $repo $QualityClip))) {
    throw "Quality clip not found: $QualityClip"
}

if (-not (Test-Path (Join-Path $repo $AutoTuneClip))) {
    throw "Auto-tune clip not found: $AutoTuneClip"
}

if (-not (Test-Path (Join-Path $repo $RoiHitClip))) {
    throw "ROI-hit clip not found: $RoiHitClip"
}

if ($RunExportSmoke -and -not (Test-Path (Join-Path $repo $ExportClip))) {
    throw "Export clip not found: $ExportClip"
}

if ($RunMediumAuto -and -not (Test-Path (Join-Path $repo $MediumAutoClip))) {
    throw "Medium auto clip not found: $MediumAutoClip"
}

if ($RunMediumExport -and -not (Test-Path (Join-Path $repo $MediumExportClip))) {
    throw "Medium export clip not found: $MediumExportClip"
}

if ($RunYoloCropReview -and -not (Test-Path $yoloCropReviewVerify)) {
    throw "YOLO crop review verifier not found: $yoloCropReviewVerify"
}

if ($RunYoloState -and -not (Test-Path $yoloStateVerify)) {
    throw "YOLO state verifier not found: $yoloStateVerify"
}

if ($RunYoloProfileState -and -not (Test-Path $yoloProfileStateVerify)) {
    throw "YOLO profile state verifier not found: $yoloProfileStateVerify"
}

if ($RunYoloConclusionState -and -not (Test-Path $yoloConclusionStateVerify)) {
    throw "YOLO conclusion state verifier not found: $yoloConclusionStateVerify"
}

if ($RunYoloDistributionState -and -not (Test-Path $yoloDistributionStateVerify)) {
    throw "YOLO distribution state verifier not found: $yoloDistributionStateVerify"
}

if ($RunYoloRepresentativeGate -and -not (Test-Path $yoloRepresentativeGateVerify)) {
    throw "YOLO representative gate verifier not found: $yoloRepresentativeGateVerify"
}

if ($RunYoloExtendedGate -and -not (Test-Path $yoloExtendedGateVerify)) {
    throw "YOLO extended gate verifier not found: $yoloExtendedGateVerify"
}

if ($RunYoloExtendedExportGate -and -not (Test-Path $yoloExtendedExportGateVerify)) {
    throw "YOLO extended export gate verifier not found: $yoloExtendedExportGateVerify"
}

if ($RunYoloGuiSmokeState -and -not (Test-Path $yoloGuiSmokeStateVerify)) {
    throw "YOLO GUI smoke state verifier not found: $yoloGuiSmokeStateVerify"
}

if ($RunYoloManualReadinessState -and -not (Test-Path $yoloManualReadinessStateVerify)) {
    throw "YOLO manual readiness state verifier not found: $yoloManualReadinessStateVerify"
}

if ($RunYoloManualGateSummary -and -not (Test-Path $yoloManualGateHelper)) {
    throw "YOLO manual gate helper not found: $yoloManualGateHelper"
}

if ($RunYoloReadyForHumanGatesState -and -not (Test-Path $yoloReadyForHumanGatesStateVerify)) {
    throw "YOLO ready-for-human-gates state verifier not found: $yoloReadyForHumanGatesStateVerify"
}

if ($RunYoloFullGtReviewedCandidateState -and -not (Test-Path $yoloFullGtReviewedCandidateStateVerify)) {
    throw "YOLO full GT reviewed candidate state verifier not found: $yoloFullGtReviewedCandidateStateVerify"
}

foreach ($requiredVerifier in @($faceTrackSceneCutGuardVerify, $yoloTemporalSmoothingCutBoundaryVerify, $autoMaskSparseSceneCutGuardVerify, $autoMaskSparseMaterializeSceneCutVerify, $yoloQualityReviewChecklistVerify, $yoloFollowupQualityEvidenceVerify, $yoloProblemSpanRunnerVerify, $yoloPseudoGtEvidenceVerify, $yoloPseudoGtTileInputVerify, $yoloPseudoGtFaceRunnerVerify, $yoloPseudoGtFaceVerificationInputVerify, $yoloPseudoGtFaceVerificationRunnerVerify, $yoloPseudoGtPersonObjectInputVerify, $yoloPseudoGtReviewClosureVerify, $yoloPseudoGtSeparationVerify, $autoNoDetectionReviewVerify, $yoloDetectionOverlayVideoVerify, $yoloAspectRatioFilterVerify, $yoloFinalMaskCleanupVerify)) {
    if (-not (Test-Path $requiredVerifier)) {
        throw "Required verifier not found: $requiredVerifier"
    }
}

$trackOutput = Invoke-ScriptStep "track-postprocess-policy" $trackPostprocessVerify @()
Assert-Contains "track-postprocess-policy" $trackOutput "\[FaceTrackPostVerify\]"
Assert-Contains "track-postprocess-policy" $trackOutput "gapFrames=11"
Assert-Contains "track-postprocess-policy" $trackOutput "initialFilled=3"
Assert-Contains "track-postprocess-policy" $trackOutput "lostFrames=33,34,35,88,89"
Assert-Contains "track-postprocess-policy" $trackOutput "removedSparse=3"
Assert-Contains "track-postprocess-policy" $trackOutput "removedUnstableTail=1"
Assert-Contains "track-postprocess-policy" $trackOutput "removedEdgeTail=1"
Assert-Contains "track-postprocess-policy" $trackOutput "largeJumpFilled=False"
Assert-Contains "track-postprocess-policy" $trackOutput "filledFrames=10,11,12,25,30,31,32,33,34,35,50,51,52,55,59,70,71,75,76,77,82,83,84,85,86,87,88,89"

$sceneCutOutput = Invoke-ScriptStep "face-track-scene-cut-guard" $faceTrackSceneCutGuardVerify @()
Assert-Contains "face-track-scene-cut-guard" $sceneCutOutput "\[FaceTrackSceneCutGuardVerify\]"
Assert-Contains "face-track-scene-cut-guard" $sceneCutOutput "checkedPairs=0->2,2->3"
Assert-Contains "face-track-scene-cut-guard" $sceneCutOutput "maxDiff=0.480"
Assert-Contains "face-track-scene-cut-guard" $sceneCutOutput "cutPairs=1->2"
Assert-Contains "face-track-scene-cut-guard" $sceneCutOutput "removedFrames=2"
Assert-Contains "face-track-scene-cut-guard" $sceneCutOutput "hardCutRemoved=True"
Assert-Contains "face-track-scene-cut-guard" $sceneCutOutput "sameSceneKept=True"
Assert-Contains "face-track-scene-cut-guard" $sceneCutOutput "reverseChecked=1"
Assert-Contains "face-track-scene-cut-guard" $sceneCutOutput "reverseRemoved=1"
Assert-Contains "face-track-scene-cut-guard" $sceneCutOutput "reversePairs=1->3"
Assert-Contains "face-track-scene-cut-guard" $sceneCutOutput "directCandidates=3"
Assert-Contains "face-track-scene-cut-guard" $sceneCutOutput "directRemoved=3"
Assert-Contains "face-track-scene-cut-guard" $sceneCutOutput "weakSourceDirectCandidates=0"
Assert-Contains "face-track-scene-cut-guard" $sceneCutOutput "mediumDirectCandidates=3"
Assert-Contains "face-track-scene-cut-guard" $sceneCutOutput "mediumDirectRemoved=3"
Assert-Contains "face-track-scene-cut-guard" $sceneCutOutput "postCutCandidates=4"
Assert-Contains "face-track-scene-cut-guard" $sceneCutOutput "postCutRemoved=4"
Assert-Contains "face-track-scene-cut-guard" $sceneCutOutput "edgePostCutDefaultCandidates=0"
Assert-Contains "face-track-scene-cut-guard" $sceneCutOutput "edgePostCutCandidates=2"
Assert-Contains "face-track-scene-cut-guard" $sceneCutOutput "edgePostCutRemoved=2"
Assert-Contains "face-track-scene-cut-guard" $sceneCutOutput "longPostCutRemoved=7"
Assert-Contains "face-track-scene-cut-guard" $sceneCutOutput "sameStrongContinuationCandidates=3"
Assert-Contains "face-track-scene-cut-guard" $sceneCutOutput "sameStrongContinuationRemoved=4"
Assert-Contains "face-track-scene-cut-guard" $sceneCutOutput "independentStrongContinuationCandidates=0"
Assert-Contains "face-track-scene-cut-guard" $sceneCutOutput "delayedPostCutCandidates=2"
Assert-Contains "face-track-scene-cut-guard" $sceneCutOutput "delayedPostCutRemoved=2"
Assert-Contains "face-track-scene-cut-guard" $sceneCutOutput "noSourcePostCutCandidates=0"
Assert-Contains "face-track-scene-cut-guard" $sceneCutOutput "probeCandidates=1"
Assert-Contains "face-track-scene-cut-guard" $sceneCutOutput "probeCutPairs=100->101"
Assert-Contains "face-track-scene-cut-guard" $sceneCutOutput "probeRemoved=0"
Assert-Contains "face-track-scene-cut-guard" $sceneCutOutput "diffCacheCalls=1"

$smoothingCutOutput = Invoke-ScriptStep "yolo-temporal-smoothing-cut-boundary" $yoloTemporalSmoothingCutBoundaryVerify @()
Assert-Contains "yolo-temporal-smoothing-cut-boundary" $smoothingCutOutput "\[YoloTemporalSmoothingCutBoundaryVerify\]"
Assert-Contains "yolo-temporal-smoothing-cut-boundary" $smoothingCutOutput "blocked=10,11,12,30"
Assert-Contains "yolo-temporal-smoothing-cut-boundary" $smoothingCutOutput "prevBlocked=True"
Assert-Contains "yolo-temporal-smoothing-cut-boundary" $smoothingCutOutput "nextSameScene=True"
Assert-Contains "yolo-temporal-smoothing-cut-boundary" $smoothingCutOutput "nextBlocked=True"

$sparseDecisionOutput = Invoke-ScriptStep "automask-sparse-scene-cut-guard" $autoMaskSparseSceneCutGuardVerify @()
Assert-Contains "automask-sparse-scene-cut-guard" $sparseDecisionOutput "\[AutoMaskSparseSceneCutGuardVerify\]"
Assert-Contains "automask-sparse-scene-cut-guard" $sparseDecisionOutput "hardCutStops=True"
Assert-Contains "automask-sparse-scene-cut-guard" $sparseDecisionOutput "sameSceneStops=False"
Assert-Contains "automask-sparse-scene-cut-guard" $sparseDecisionOutput "nonYoloStops=False"

$sparseMaterializeOutput = Invoke-ScriptStep "automask-sparse-materialize-scene-cut" $autoMaskSparseMaterializeSceneCutVerify @()
Assert-Contains "automask-sparse-materialize-scene-cut" $sparseMaterializeOutput "\[AutoMaskSparseMaterializeSceneCutVerify\]"
Assert-Contains "automask-sparse-materialize-scene-cut" $sparseMaterializeOutput "yoloHardCutInterpolated=0"
Assert-Contains "automask-sparse-materialize-scene-cut" $sparseMaterializeOutput "yoloSceneCutStops=1"
Assert-Contains "automask-sparse-materialize-scene-cut" $sparseMaterializeOutput "yoloSceneCutTransitions=0->5"
Assert-Contains "automask-sparse-materialize-scene-cut" $sparseMaterializeOutput "cutBeforePositiveInterpolated=0"
Assert-Contains "automask-sparse-materialize-scene-cut" $sparseMaterializeOutput "faceOnnxInterpolated=4"

$reviewChecklistOutput = Invoke-ScriptStep "yolo-quality-review-checklist" $yoloQualityReviewChecklistVerify @()
Assert-Contains "yolo-quality-review-checklist" $reviewChecklistOutput "\[YoloQualityReviewChecklistVerify\] all requested checks passed"

$followupEvidenceOutput = Invoke-ScriptStep "yolo-followup-quality-evidence" $yoloFollowupQualityEvidenceVerify @()
Assert-Contains "yolo-followup-quality-evidence" $followupEvidenceOutput "\[YoloFollowupQualityEvidenceVerify\] all requested checks passed"

$problemSpanRunnerOutput = Invoke-ScriptStep "yolo-problem-span-runner" $yoloProblemSpanRunnerVerify @()
Assert-Contains "yolo-problem-span-runner" $problemSpanRunnerOutput "\[YoloProblemSpanRunnerVerify\] all requested checks passed"

$pseudoGtEvidenceOutput = Invoke-ScriptStep "yolo-pseudo-gt-evidence" $yoloPseudoGtEvidenceVerify @()
Assert-Contains "yolo-pseudo-gt-evidence" $pseudoGtEvidenceOutput "\[YoloPseudoGtEvidenceVerify\] all requested checks passed"

$pseudoGtTileInputOutput = Invoke-ScriptStep "yolo-pseudo-gt-tile-input" $yoloPseudoGtTileInputVerify @()
Assert-Contains "yolo-pseudo-gt-tile-input" $pseudoGtTileInputOutput "\[YoloPseudoGtTileInputVerify\] all requested checks passed"

$pseudoGtFaceRunnerOutput = Invoke-ScriptStep "yolo-pseudo-gt-face-runner" $yoloPseudoGtFaceRunnerVerify @()
Assert-Contains "yolo-pseudo-gt-face-runner" $pseudoGtFaceRunnerOutput "\[YoloPseudoGtFaceRunnerVerify\] all requested checks passed"

$pseudoGtFaceVerificationInputOutput = Invoke-ScriptStep "yolo-pseudo-gt-face-verification-input" $yoloPseudoGtFaceVerificationInputVerify @()
Assert-Contains "yolo-pseudo-gt-face-verification-input" $pseudoGtFaceVerificationInputOutput "\[YoloPseudoGtFaceVerificationInputVerify\] all requested checks passed"

$pseudoGtFaceVerificationRunnerOutput = Invoke-ScriptStep "yolo-pseudo-gt-face-verification-runner" $yoloPseudoGtFaceVerificationRunnerVerify @()
Assert-Contains "yolo-pseudo-gt-face-verification-runner" $pseudoGtFaceVerificationRunnerOutput "\[YoloPseudoGtFaceVerificationRunnerVerify\] all requested checks passed"

$pseudoGtPersonObjectInputOutput = Invoke-ScriptStep "yolo-pseudo-gt-person-object-input" $yoloPseudoGtPersonObjectInputVerify @()
Assert-Contains "yolo-pseudo-gt-person-object-input" $pseudoGtPersonObjectInputOutput "\[YoloPseudoGtPersonObjectInputVerify\] all requested checks passed"

$pseudoGtReviewDraftOutput = Invoke-ScriptStep "yolo-pseudo-gt-review-draft" $yoloPseudoGtReviewDraftVerify @()
Assert-Contains "yolo-pseudo-gt-review-draft" $pseudoGtReviewDraftOutput "\[YoloPseudoGtReviewDraftVerify\] all requested checks passed"

$pseudoGtReviewVisualPackageOutput = Invoke-ScriptStep "yolo-pseudo-gt-review-visual-package" $yoloPseudoGtReviewVisualPackageVerify @()
Assert-Contains "yolo-pseudo-gt-review-visual-package" $pseudoGtReviewVisualPackageOutput "\[YoloPseudoGtReviewVisualVerify\] all requested checks passed"

$pseudoGtReviewDraftApplyOutput = Invoke-ScriptStep "yolo-pseudo-gt-review-draft-apply" $yoloPseudoGtReviewDraftApplyVerify @()
Assert-Contains "yolo-pseudo-gt-review-draft-apply" $pseudoGtReviewDraftApplyOutput "\[YoloPseudoGtReviewDraftApplyVerify\] all requested checks passed"

$pseudoGtReviewClosureOutput = Invoke-ScriptStep "yolo-pseudo-gt-review-closure" $yoloPseudoGtReviewClosureVerify @()
Assert-Contains "yolo-pseudo-gt-review-closure" $pseudoGtReviewClosureOutput "\[YoloPseudoGtReviewClosureVerify\] all requested checks passed"

$pseudoGtSeparationOutput = Invoke-ScriptStep "yolo-pseudo-gt-separation" $yoloPseudoGtSeparationVerify @()
Assert-Contains "yolo-pseudo-gt-separation" $pseudoGtSeparationOutput "\[YoloPseudoGtSeparationVerify\] all requested checks passed"
Assert-Contains "yolo-pseudo-gt-separation" $pseudoGtSeparationOutput "runtime source has no pseudo-GT references"

$autoNoDetectionReviewOutput = Invoke-ScriptStep "auto-no-detection-review" $autoNoDetectionReviewVerify @()
Assert-Contains "auto-no-detection-review" $autoNoDetectionReviewOutput "\[AutoNoDetectionReviewVerify\] all requested checks passed"

$detectionOverlayOutput = Invoke-ScriptStep "yolo-detection-overlay-video" $yoloDetectionOverlayVideoVerify @()
Assert-Contains "yolo-detection-overlay-video" $detectionOverlayOutput "\[YoloDetectionOverlayVideoVerify\] all requested checks passed"
Assert-Contains "yolo-detection-overlay-video" $detectionOverlayOutput "yolo-detection-overlay\.mp4"

$aspectRatioFilterOutput = Invoke-ScriptStep "yolo-aspect-ratio-filter" $yoloAspectRatioFilterVerify @()
Assert-Contains "yolo-aspect-ratio-filter" $aspectRatioFilterOutput "\[YoloAspectRatioFilterVerify\] all requested checks passed"

$finalMaskCleanupOutput = Invoke-ScriptStep "yolo-final-mask-cleanup" $yoloFinalMaskCleanupVerify @()
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "\[YoloFinalMaskCleanupVerify\]"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "removedWeakIsolated=44"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "removedWeakUnsupported=3"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "removedMediumUnsupported=3"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "removedWeakShortClusters=5"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "removedWeakTextureClusters=5"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "removedWeakTinyClusters=5"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "removedTinyShortClusters=4"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "removedTinyIsolated=1"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "removedTopEdgeWeakClusters=3"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "removedTopEdgeLargeDuplicates=1"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "removedUpperWeakClusters=8"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "removedLowerWeakClusters=3"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "removedAspectOutliers=3"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "gapFilled=5"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "gapFrames=11,31,32,33,111"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "supportedWeakGapFilled=2"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "unsupportedWeakGapFilled=0"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "extendedGapFilled=5"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "extendedGapFrames=301,302,303,304,305"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "mixedFrameGapFilled=1"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "gapCutRemoved=2"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "gapCutAfterRemoved=1"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "postSceneCleanupRemoved=2"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "sceneCutCarryRemoved=17"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "sceneCutCarryFrames=1001,1002,1003,1004,1005,1006,1007,1008,2001,2002,2003,2004,2005,2006,2007,2008,2009"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "sceneCutCarryRemovedUnsupportedStrong=2"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "sceneCutCarryRemovedUnsupportedStrongFrames=1006,2009"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "sceneCutCarryBlockedFrames=1001,1002,1003,1004,1005,1006,1007,1008,2001,2002,2003,2004,2005,2006,2007,2008,2009,2010,2011"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "sceneCutCarryRefillBlocked=0"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "emptyPostCutRemovedUnsupportedStrong=2"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "emptyPostCutRefillBlocked=0"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "stickyStrongCarryRemoved=5"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "stickyStrongCarryRemovedUnsupportedStrong=5"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "driftingStrongCarryRemoved=5"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "areaChangedStrongCarryProtected=3"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "sameCenterAreaChangedStrongCarryRemoved=5"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "partialSceneCarryRefillBlocked=3"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "partialSceneCarryBlockedFrames=3101,3102,3103"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "sceneCarryAnchorRefillBlocked=1"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "sceneCarryAnchorBlockedFrames=3209"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "storedCleanupRefillBlocked=3"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "storedCleanupBlockedFrames=3301,3302,3303"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "storedSceneCarryRefillBlocked=3"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "storedSceneCarryBlockedFrames=3311,3312,3313"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "remainingFrames=20,30,50,59,60,61,90,91,92,93,96"

$qualityOutput = Invoke-Step "quality-gate-all-frame-parallel" @(
    "-SkipTrim",
    "-Source", $QualityClip,
    "-SkipExport",
    "-OptimizedCpuOnly",
    "-OptimizedNoTracking",
    "-ParallelDetectorCount", "2",
    "-MinAvgIou", "0.99",
    "-MinBestIou", "0.99"
)
Assert-Contains "quality-gate-all-frame-parallel" $qualityOutput "\[SmokeQualityGate\] passed=True"
Assert-Contains "quality-gate-all-frame-parallel" $qualityOutput "avgBestIou=1\.000"
Assert-Contains "quality-gate-all-frame-parallel" $qualityOutput "minBestIou=1\.000"

$roiHitOutput = Invoke-Step "roi-refiner-hit-representative" @(
    "-SkipTrim",
    "-Source", $RoiHitClip,
    "-SkipBaseline",
    "-SkipExport",
    "-OptimizedCpuOnly",
    "-OptimizedNoTracking",
    "-ParallelDetectorCount", "2"
)
Assert-Contains "roi-refiner-hit-representative" $roiHitOutput "\[SmokeFaceTrackRoiRefine\].*attempts=1[0-9]"
Assert-Contains "roi-refiner-hit-representative" $roiHitOutput "\[SmokeFaceTrackRoiRefine\].*hits=[1-9]"
Assert-Contains "roi-refiner-hit-representative" $roiHitOutput "\[SmokeFaceTrackRoiRefine\].*seeks=[1-9]"
Assert-Contains "roi-refiner-hit-representative" $roiHitOutput "\[SmokeFaceTrackRoiRefine\].*decoded=[1-9][0-9]*"

if ($RunExportSmoke) {
    $exportOutput = Invoke-Step "direct-face-export-smoke" @(
        "-SkipTrim",
        "-Source", $ExportClip,
        "-SkipBaseline",
        "-OptimizedCpuOnly",
        "-OptimizedNoTracking",
        "-ParallelDetectorCount", "2"
    )
    Assert-Contains "direct-face-export-smoke" $exportOutput "\[ExportRunSummary\].*bitmapMaskFrames=0"
    Assert-Contains "direct-face-export-smoke" $exportOutput "\[ExportRunSummary\].*directFaceFrames=[1-9][0-9]*"
    Assert-Contains "direct-face-export-smoke" $exportOutput "\[Smoke\].*output="
}

if ($RunMediumAuto) {
    $mediumOutput = Invoke-Step "medium-auto-track-roi" @(
        "-SkipTrim",
        "-Source", $MediumAutoClip,
        "-SkipBaseline",
        "-SkipExport",
        "-OptimizedCpuOnly",
        "-OptimizedNoTracking",
        "-ParallelDetectorCount", "2"
    )
    Assert-Contains "medium-auto-track-roi" $mediumOutput "processed=899"
    Assert-Contains "medium-auto-track-roi" $mediumOutput "detects=899"
    Assert-Contains "medium-auto-track-roi" $mediumOutput "interpolated=0"
    Assert-Contains "medium-auto-track-roi" $mediumOutput "\[SmokeFaceTrackPost\].*filled=[1-9][0-9]*"
    Assert-Contains "medium-auto-track-roi" $mediumOutput "\[SmokeFaceTrackPost\].*lostFilled=[1-9][0-9]*"
    Assert-Contains "medium-auto-track-roi" $mediumOutput "\[SmokeFaceTrackPost\].*removedShort=[1-9][0-9]*"
    Assert-Contains "medium-auto-track-roi" $mediumOutput "\[SmokeFaceTrackRoiRefine\].*attempts=32"
    Assert-Contains "medium-auto-track-roi" $mediumOutput "\[SmokeFaceTrackRoiRefine\].*hits=[1-9][0-9]*"
    Assert-Contains "medium-auto-track-roi" $mediumOutput "\[SmokeFaceTrackRoiRefine\].*seeks=[1-9]"
    Assert-Contains "medium-auto-track-roi" $mediumOutput "\[SmokeFaceTrackRoiRefine\].*decoded=[1-9][0-9]*"
}

if ($RunMediumExport) {
    $mediumExportOutput = Invoke-Step "medium-auto-export" @(
        "-SkipTrim",
        "-Source", $MediumExportClip,
        "-SkipBaseline",
        "-OptimizedCpuOnly",
        "-OptimizedNoTracking",
        "-ParallelDetectorCount", "2"
    )
    Assert-Contains "medium-auto-export" $mediumExportOutput "processed=899"
    Assert-Contains "medium-auto-export" $mediumExportOutput "detects=899"
    Assert-Contains "medium-auto-export" $mediumExportOutput "\[SmokeFaceTrackPost\].*filled=[1-9][0-9]*"
    Assert-Contains "medium-auto-export" $mediumExportOutput "\[SmokeFaceTrackPost\].*lostFilled=[1-9][0-9]*"
    Assert-Contains "medium-auto-export" $mediumExportOutput "\[SmokeFaceTrackRoiRefine\].*attempts=32"
    Assert-Contains "medium-auto-export" $mediumExportOutput "\[SmokeFaceTrackRoiRefine\].*hits=[1-9][0-9]*"
    Assert-Contains "medium-auto-export" $mediumExportOutput "\[ExportRunSummary\].*frames=902"
    Assert-Contains "medium-auto-export" $mediumExportOutput "\[ExportRunSummary\].*bitmapMaskFrames=0"
    Assert-Contains "medium-auto-export" $mediumExportOutput "\[ExportRunSummary\].*directFaceFrames=[1-9][0-9]*"
    Assert-Contains "medium-auto-export" $mediumExportOutput "\[Smoke\].*output="
}

$shortTuneOutput = Invoke-Step "default-autotune-provider-short" @(
    "-SkipTrim",
    "-Source", $AutoTuneClip,
    "-SkipBaseline",
    "-SkipExport",
    "-UseAutoTune"
)
Assert-Contains "default-autotune-provider-short" $shortTuneOutput "\[SmokeTune\].*tuned="
Assert-Contains "default-autotune-provider-short" $shortTuneOutput "detector=FaceOnnxDetector/(CPU|GPU:DirectML)"
Assert-Contains "default-autotune-gpu-short" $shortTuneOutput "mode=pipe-parallel"
Assert-Contains "default-autotune-gpu-short" $shortTuneOutput "detects=150"
Assert-Contains "default-autotune-gpu-short" $shortTuneOutput "interpolated=0"

if ($RunLongAutoTune) {
    if (-not (Test-Path (Join-Path $repo $LongAutoTuneClip))) {
        throw "Long auto-tune clip not found: $LongAutoTuneClip"
    }

    $longTuneOutput = Invoke-Step "default-autotune-provider-long" @(
        "-SkipTrim",
        "-Source", $LongAutoTuneClip,
        "-SkipBaseline",
        "-SkipExport",
        "-UseAutoTune"
    )
    Assert-Contains "default-autotune-provider-long" $longTuneOutput "\[SmokeTune\].*tuned="
    Assert-Contains "default-autotune-provider-long" $longTuneOutput "detector=FaceOnnxDetector/(CPU|GPU:DirectML)"
    Assert-Contains "default-autotune-gpu-long" $longTuneOutput "mode=pipe-parallel"
    Assert-Contains "default-autotune-gpu-long" $longTuneOutput "detects=899"
    Assert-Contains "default-autotune-gpu-long" $longTuneOutput "interpolated=0"
}

if ($RunYoloCropReview) {
    $yoloReviewOutput = Invoke-ScriptStep "yolo-crop-review" $yoloCropReviewVerify @(
        "-PassReviewCsv", $YoloCropReviewPassCsv,
        "-FailReviewCsv", $YoloCropReviewFailCsv
    )
    Assert-Contains "yolo-crop-review" $yoloReviewOutput "\[YoloCropReviewVerify\] all requested checks passed"
}

if ($RunYoloFullGtReviewedCandidateState -and -not $RunYoloState) {
    $candidateOutput = Invoke-ScriptStep "yolo-full-gt-reviewed-candidate-state" $yoloFullGtReviewedCandidateStateVerify @()
    Assert-Contains "yolo-full-gt-reviewed-candidate-state" $candidateOutput "\[YoloFullGtReviewedCandidateVerify\] all requested checks passed"
}

if ($RunYoloState) {
    $yoloStateArgs = @(
        "-YoloCropReviewPassCsv", $YoloCropReviewPassCsv,
        "-YoloCropReviewFailCsv", $YoloCropReviewFailCsv,
        "-RepresentativeQualityClip", $YoloRepresentativeQualityClip,
        "-ExtendedQualityClip", $YoloExtendedQualityClip,
        "-ExtendedExportQualityClip", $YoloExtendedExportQualityClip
    )
    if (-not [string]::IsNullOrWhiteSpace($YoloRepresentativeModelPath)) {
        $yoloStateArgs += @("-RepresentativeYoloModelPath", $YoloRepresentativeModelPath)
    }
    if (-not [string]::IsNullOrWhiteSpace($YoloExtendedModelPath)) {
        $yoloStateArgs += @("-ExtendedYoloModelPath", $YoloExtendedModelPath)
    }
    if (-not [string]::IsNullOrWhiteSpace($YoloExtendedExportModelPath)) {
        $yoloStateArgs += @("-ExtendedExportYoloModelPath", $YoloExtendedExportModelPath)
    }
    if ($RunYoloRepresentativeGate) {
        $yoloStateArgs += "-RunRepresentativeGate"
    }
    if ($RunYoloExtendedGate) {
        $yoloStateArgs += "-RunExtendedGate"
    }
    if ($RunYoloExtendedExportGate) {
        $yoloStateArgs += "-RunExtendedExportGate"
    }
    if ($RunYoloGuiSmokeState) {
        $yoloStateArgs += "-RunGuiSmokeState"
    }
    if ($RequireYoloGuiSmokeManualPass) {
        $yoloStateArgs += "-RequireGuiSmokeManualPass"
    }
    if ($AllowCompletedYoloFullGt) {
        $yoloStateArgs += "-AllowCompletedFullGt"
    }
    if ($AllowCompletedYoloGuiSmoke) {
        $yoloStateArgs += "-AllowCompletedGuiSmoke"
    }
    if ($RequireYoloComplete) {
        $yoloStateArgs += "-RequireComplete"
    }
    if ($AllowFullGtQualityGateFailure) {
        $yoloStateArgs += "-AllowFullGtQualityGateFailure"
    }
    if (-not [string]::IsNullOrWhiteSpace($YoloFullGtPredictionCsv)) {
        $yoloStateArgs += @("-FullGtPredictionCsv", $YoloFullGtPredictionCsv)
    }
    else {
        $yoloStateArgs += @("-FullGtPredictionLog", $YoloFullGtPredictionLog)
    }
    $yoloStateArgs += @(
        "-FullGtMinIou", "$YoloFullGtMinIou",
        "-FullGtMaxMisses", "$YoloFullGtMaxMisses",
        "-FullGtMaxFalsePositives", "$YoloFullGtMaxFalsePositives",
        "-FullGtMaxLowIou", "$YoloFullGtMaxLowIou"
    )

    $yoloStateOutput = Invoke-ScriptStep "yolo-state" $yoloStateVerify $yoloStateArgs
    Assert-Contains "yolo-state" $yoloStateOutput "\[YoloStateVerify\] all requested checks passed"
}

if ($RunYoloProfileState) {
    $yoloProfileOutput = Invoke-ScriptStep "yolo-profile-state" $yoloProfileStateVerify @()
    Assert-Contains "yolo-profile-state" $yoloProfileOutput "\[YoloProfileVerify\] all requested checks passed"
}

if ($RunYoloConclusionState) {
    $yoloConclusionOutput = Invoke-ScriptStep "yolo-conclusion-state" $yoloConclusionStateVerify @()
    Assert-Contains "yolo-conclusion-state" $yoloConclusionOutput "\[YoloConclusionVerify\] all requested checks passed"
}

if ($RunYoloDistributionState) {
    $yoloDistributionOutput = Invoke-ScriptStep "yolo-distribution-state" $yoloDistributionStateVerify @()
    Assert-Contains "yolo-distribution-state" $yoloDistributionOutput "\[YoloDistributionVerify\] all requested checks passed"
}

if ($RunYoloRepresentativeGate -and -not $RunYoloState) {
    $yoloRepresentativeOutput = Invoke-ScriptStep "yolo-representative-gate" $yoloRepresentativeGateVerify @(
        "-QualityClip", $YoloRepresentativeQualityClip,
        "-YoloModelPath", $YoloRepresentativeModelPath
    )
    Assert-Contains "yolo-representative-gate" $yoloRepresentativeOutput "\[YoloRepresentativeGateVerify\] all requested checks passed"
}

if ($RunYoloExtendedGate -and -not $RunYoloState) {
    $yoloExtendedOutput = Invoke-ScriptStep "yolo-extended-gate" $yoloExtendedGateVerify @(
        "-QualityClip", $YoloExtendedQualityClip,
        "-YoloModelPath", $YoloExtendedModelPath
    )
    Assert-Contains "yolo-extended-gate" $yoloExtendedOutput "\[YoloExtendedGateVerify\] all requested checks passed"
}

if ($RunYoloExtendedExportGate -and -not $RunYoloState) {
    $yoloExtendedExportOutput = Invoke-ScriptStep "yolo-extended-export-gate" $yoloExtendedExportGateVerify @(
        "-QualityClip", $YoloExtendedExportQualityClip,
        "-YoloModelPath", $YoloExtendedExportModelPath
    )
    Assert-Contains "yolo-extended-export-gate" $yoloExtendedExportOutput "\[YoloExtendedExportGateVerify\] all requested checks passed"
}

if ($RunYoloGuiSmokeState -and -not $RunYoloState) {
    $guiSmokeArgs = @()
    if ($RequireYoloGuiSmokeManualPass) {
        $guiSmokeArgs += "-RequireManualPass"
    }

    $yoloGuiSmokeOutput = Invoke-ScriptStep "yolo-gui-smoke-state" $yoloGuiSmokeStateVerify $guiSmokeArgs
    Assert-Contains "yolo-gui-smoke-state" $yoloGuiSmokeOutput "\[YoloGuiSmokeVerify\] all requested checks passed"
}

if ($RunYoloManualReadinessState -and -not $RunYoloState) {
    $manualReadinessArgs = @()
    if ($AllowCompletedYoloFullGt) {
        $manualReadinessArgs += "-AllowCompletedFullGt"
    }
    if ($AllowCompletedYoloGuiSmoke) {
        $manualReadinessArgs += "-AllowCompletedGuiSmoke"
    }
    if ($AllowFullGtQualityGateFailure) {
        $manualReadinessArgs += "-AllowQualityGateFailure"
    }
    if (-not [string]::IsNullOrWhiteSpace($YoloFullGtPredictionCsv)) {
        $manualReadinessArgs += @("-FullGtPredictionCsv", $YoloFullGtPredictionCsv)
    }
    else {
        $manualReadinessArgs += @("-FullGtPredictionLog", $YoloFullGtPredictionLog)
    }
    $manualReadinessArgs += @(
        "-FullGtMinIou", "$YoloFullGtMinIou",
        "-FullGtMaxMisses", "$YoloFullGtMaxMisses",
        "-FullGtMaxFalsePositives", "$YoloFullGtMaxFalsePositives",
        "-FullGtMaxLowIou", "$YoloFullGtMaxLowIou"
    )

    $yoloManualReadinessOutput = Invoke-ScriptStep "yolo-manual-readiness-state" $yoloManualReadinessStateVerify $manualReadinessArgs
    Assert-Contains "yolo-manual-readiness-state" $yoloManualReadinessOutput "\[YoloManualReadinessVerify\] all requested checks passed"
}

if ($RunYoloManualGateSummary -and -not $RunYoloState) {
    $summaryOutput = Invoke-ScriptStep "yolo-manual-gate-summary" $yoloManualGateHelper @(
        "-WriteSummary",
        "-SummaryPath", $YoloManualGateSummaryPath
    )
    Assert-Contains "yolo-manual-gate-summary" $summaryOutput "\[YoloManualGate\] summaryPath="
    Assert-Contains "yolo-manual-gate-summary" $summaryOutput "\[YoloManualGate\] all requested checks passed"
}

if ($RunYoloReadyForHumanGatesState) {
    $readyOutput = Invoke-ScriptStep "yolo-ready-for-human-gates-state" $yoloReadyForHumanGatesStateVerify @(
        "-ManualGateSummary", $YoloManualGateSummaryPath,
        "-EvidenceReport", $YoloEvidenceReportPath
    )
    Assert-Contains "yolo-ready-for-human-gates-state" $readyOutput "\[YoloReadyForHumanGatesVerify\] ready=true, remaining=gui-smoke"
    Assert-Contains "yolo-ready-for-human-gates-state" $readyOutput "\[YoloReadyForHumanGatesVerify\] all requested checks passed"
}

Write-Host "[AutoMosaicVerify] all requested checks passed"
