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
    [switch]$RunYoloPseudoGtState,
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
$autoMaskDefaultFilterStabilityVerify = Join-Path $repo "scripts\verify-automask-default-filter-stability.ps1"
$autoResumeMaskResetVerify = Join-Path $repo "scripts\verify-auto-resume-mask-reset.ps1"
$autoMaskResumeEofVerify = Join-Path $repo "scripts\verify-automask-resume-eof.ps1"
$autoProcessingModeMigrationVerify = Join-Path $repo "scripts\verify-auto-processing-mode-migration.ps1"
$blurRenderConsistencyVerify = Join-Path $repo "scripts\verify-blur-render-consistency.ps1"
$bgraIntegralRangeVerify = Join-Path $repo "scripts\verify-bgra-integral-range.ps1"
$swsFrameColorFidelityVerify = Join-Path $repo "scripts\verify-sws-frame-color-fidelity.ps1"
$tenBitEncoderFallbackVerify = Join-Path $repo "scripts\verify-ten-bit-encoder-fallback.ps1"
$exportProgressCompletionVerify = Join-Path $repo "scripts\verify-export-progress-completion.ps1"
$videoFieldFidelityPolicyVerify = Join-Path $repo "scripts\verify-video-field-fidelity-policy.ps1"
$encodedPresentationGapsVerify = Join-Path $repo "scripts\verify-encoded-presentation-gaps.ps1"
$vfrMaskOrdinalVerify = Join-Path $repo "scripts\verify-vfr-mask-ordinal.ps1"
$vfrFrameExtractorOrdinalVerify = Join-Path $repo "scripts\verify-vfr-frame-extractor-ordinal.ps1"
$detectorAutoTunerSessionRangeVerify = Join-Path $repo "scripts\verify-detector-auto-tuner-session-range.ps1"
$detectorAutoTunerSafetyVerify = Join-Path $repo "scripts\verify-detector-auto-tuner-safety.ps1"
$detectorAutoTunerOverheadVerify = Join-Path $repo "scripts\verify-detector-auto-tuner-overhead.ps1"
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

function Assert-AutoTuneModeMatchesSessions(
    [string]$Name,
    [string]$Text,
    [int]$MaxSessions
) {
    $match = [regex]::Match($Text, '\[SmokeTune\][^\r\n]*sessions=(\d+)')
    if (-not $match.Success) {
        throw "$Name did not report the selected session count"
    }

    $sessions = [int]$match.Groups[1].Value
    if ($sessions -lt 1 -or $sessions -gt $MaxSessions) {
        throw "$Name selected sessions=$sessions outside 1..$MaxSessions"
    }

    $expectedMode = if ($sessions -eq 1) { "pipe-single" } else { "pipe-parallel" }
    Assert-Contains $Name $Text "\[AutoRunSummary\].*mode=$expectedMode"
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

foreach ($requiredVerifier in @($faceTrackSceneCutGuardVerify, $yoloTemporalSmoothingCutBoundaryVerify, $autoMaskSparseSceneCutGuardVerify, $autoMaskSparseMaterializeSceneCutVerify, $autoMaskDefaultFilterStabilityVerify, $autoResumeMaskResetVerify, $autoMaskResumeEofVerify, $autoProcessingModeMigrationVerify, $blurRenderConsistencyVerify, $bgraIntegralRangeVerify, $swsFrameColorFidelityVerify, $encodedPresentationGapsVerify, $vfrMaskOrdinalVerify, $vfrFrameExtractorOrdinalVerify, $detectorAutoTunerSessionRangeVerify, $detectorAutoTunerSafetyVerify, $detectorAutoTunerOverheadVerify, $autoNoDetectionReviewVerify, $yoloDetectionOverlayVideoVerify, $yoloAspectRatioFilterVerify, $yoloFinalMaskCleanupVerify)) {
    if (-not (Test-Path $requiredVerifier)) {
        throw "Required verifier not found: $requiredVerifier"
    }
}

if ($RunYoloPseudoGtState) {
    foreach ($requiredVerifier in @($yoloQualityReviewChecklistVerify, $yoloFollowupQualityEvidenceVerify, $yoloProblemSpanRunnerVerify, $yoloPseudoGtEvidenceVerify, $yoloPseudoGtTileInputVerify, $yoloPseudoGtFaceRunnerVerify, $yoloPseudoGtFaceVerificationInputVerify, $yoloPseudoGtFaceVerificationRunnerVerify, $yoloPseudoGtPersonObjectInputVerify, $yoloPseudoGtReviewDraftVerify, $yoloPseudoGtReviewVisualPackageVerify, $yoloPseudoGtReviewDraftApplyVerify, $yoloPseudoGtReviewClosureVerify, $yoloPseudoGtSeparationVerify)) {
        if (-not (Test-Path $requiredVerifier)) {
            throw "Required YOLO pseudo-GT verifier not found: $requiredVerifier"
        }
    }
}

$trackOutput = Invoke-ScriptStep "track-postprocess-policy" $trackPostprocessVerify @()
Assert-Contains "track-postprocess-policy" $trackOutput "\[FaceTrackPostVerify\]"
Assert-Contains "track-postprocess-policy" $trackOutput "gapFrames=11"
Assert-Contains "track-postprocess-policy" $trackOutput "initialFilled=3"
Assert-Contains "track-postprocess-policy" $trackOutput "lostFrames=33,34,35,88,89,90,98,99"
Assert-Contains "track-postprocess-policy" $trackOutput "removedSparse=3"
Assert-Contains "track-postprocess-policy" $trackOutput "removedUnstableTail=1"
Assert-Contains "track-postprocess-policy" $trackOutput "removedEdgeTail=1"
Assert-Contains "track-postprocess-policy" $trackOutput "largeJumpFilled=False"
Assert-Contains "track-postprocess-policy" $trackOutput "faceOnnxContinuity=True"
Assert-Contains "track-postprocess-policy" $trackOutput "confirmedHold=True"
Assert-Contains "track-postprocess-policy" $trackOutput "filledFrames=10,11,12,25,30,31,32,33,34,35,50,51,52,55,59,70,71,75,76,77,82,83,84,85,86,87,88,89,90,95,96,97,98,99"

$defaultFilterOutput = Invoke-ScriptStep "automask-default-filter-stability" $autoMaskDefaultFilterStabilityVerify @()
Assert-Contains "automask-default-filter-stability" $defaultFilterOutput "policies=6 geometry=3 pixelPolicies=2"
Assert-Contains "automask-default-filter-stability" $defaultFilterOutput "runtimePaths=7 resumeSignature=v5"

$autoResumeOutput = Invoke-ScriptStep "auto-resume-mask-reset" $autoResumeMaskResetVerify @()
Assert-Contains "auto-resume-mask-reset" $autoResumeOutput "resetCases=5"
Assert-Contains "auto-resume-mask-reset" $autoResumeOutput "precision=True culture=True provider=True source=True sessions=True models=True backends=True riskIntent=True timelinePrompt=True providerPool=True snapshot=True json=True"

$autoResumeEofOutput = Invoke-ScriptStep "automask-resume-eof" $autoMaskResumeEofVerify @("-SourcePath", $QualityClip)
Assert-Contains "automask-resume-eof" $autoResumeEofOutput "trackedResume=full-replay firstCallbackCancel=zero misalignedSparse=full-replay"
Assert-Contains "automask-resume-eof" $autoResumeEofOutput "sparseCancelWatermark=10 sparseFaultWatermark=0 sparseOutOfOrderClean=true sparseResumeEquivalent=true"
Assert-Contains "automask-resume-eof" $autoResumeEofOutput "metadataMismatch=zero\+under\+over staleResume=full-replay"

$processingModeMigrationOutput = Invoke-ScriptStep "auto-processing-mode-migration" $autoProcessingModeMigrationVerify @()
Assert-Contains "auto-processing-mode-migration" $processingModeMigrationOutput "validModesPreserved=8 validQualitiesPreserved=4 postprocessPreserved=7"
Assert-Contains "auto-processing-mode-migration" $processingModeMigrationOutput "upgradeRoundTrip=true modeSemantics=4 isolatedNoUserStateAccess=true"

$blurRenderOutput = Invoke-ScriptStep "blur-render-consistency" $blurRenderConsistencyVerify @()
Assert-Contains "blur-render-consistency" $blurRenderOutput "radiusPolicy=true radiusMapHeight=true previewExportMatch=true overlapOrderIndependent=true stateless=true crossThreadCacheReset=true"

$bgraIntegralOutput = Invoke-ScriptStep "bgra-integral-range" $bgraIntegralRangeVerify @()
Assert-Contains "bgra-integral-range" $bgraIntegralOutput "storage=uint32 dci4kFull=2256076800 wrapCases=10000 maxWindow=81 checkedSize=true"

$swsColorOutput = Invoke-ScriptStep "sws-frame-color-fidelity" $swsFrameColorFidelityVerify @()
Assert-Contains "sws-frame-color-fidelity" $swsColorOutput "legacyCalls=0 dynamicContexts=3 metadataFirstConversions=4 framePropertiesReset=true nativeRoundTrip=true hdrSideDataReplacement=true"

$tenBitEncoderOutput = Invoke-ScriptStep "ten-bit-encoder-fallback" $tenBitEncoderFallbackVerify @()
Assert-Contains "ten-bit-encoder-fallback" $tenBitEncoderOutput "software=libx264,libx265 .*libx265TenBitOpen=true"

$exportProgressOutput = Invoke-ScriptStep "export-progress-completion" $exportProgressCompletionVerify @()
Assert-Contains "export-progress-completion" $exportProgressOutput "processingMax=99 committed=100 unknownTotal=100"

$videoFieldOutput = Invoke-ScriptStep "video-field-fidelity-policy" $videoFieldFidelityPolicyVerify @()
Assert-Contains "video-field-fidelity-policy" $videoFieldOutput "fieldOrders=6 .*interlacedFailClosed=true nonLeftChromaSoftware=true"

$encodedPresentationOutput = Invoke-ScriptStep "encoded-presentation-gaps" $encodedPresentationGapsVerify @()
Assert-Contains "encoded-presentation-gaps" $encodedPresentationOutput "gapCases=14 durationPolicies=7"

$vfrMaskOrdinalOutput = Invoke-ScriptStep "vfr-mask-ordinal" $vfrMaskOrdinalVerify @()
Assert-Contains "vfr-mask-ordinal" $vfrMaskOrdinalOutput "coverage-cases=3"

$vfrFrameExtractorOutput = Invoke-ScriptStep "vfr-frame-extractor-ordinal" $vfrFrameExtractorOrdinalVerify @()
Assert-Contains "vfr-frame-extractor-ordinal" $vfrFrameExtractorOutput "source-guards=true actual=false"

$autoTunerOutput = Invoke-ScriptStep "detector-autotune-session-range" $detectorAutoTunerSessionRangeVerify @()
Assert-Contains "detector-autotune-session-range" $autoTunerOutput "cpuSessions=1,2,3,4"
Assert-Contains "detector-autotune-session-range" $autoTunerOutput "gpuSessions=1,2,3,4"
Assert-Contains "detector-autotune-session-range" $autoTunerOutput "roiPolicies=6"
Assert-Contains "detector-autotune-session-range" $autoTunerOutput "fullFramePaths=2"

$autoTunerSafetyOutput = Invoke-ScriptStep "detector-autotune-safety" $detectorAutoTunerSafetyVerify @()
Assert-Contains "detector-autotune-safety" $autoTunerSafetyOutput "qualityCases=5"
Assert-Contains "detector-autotune-safety" $autoTunerSafetyOutput "providerCases=4"
Assert-Contains "detector-autotune-safety" $autoTunerSafetyOutput "fallback=True cancellation=True"

$autoTunerOverheadOutput = Invoke-ScriptStep "detector-autotune-overhead" $detectorAutoTunerOverheadVerify @()
Assert-Contains "detector-autotune-overhead" $autoTunerOverheadOutput "plans=4"
Assert-Contains "detector-autotune-overhead" $autoTunerOverheadOutput "qualitySessionReuse=True"
Assert-Contains "detector-autotune-overhead" $autoTunerOverheadOutput "warmupSampleLimit=True"

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
Assert-Contains "automask-sparse-scene-cut-guard" $sparseDecisionOutput "guardDisabledStops=False"
Assert-Contains "automask-sparse-scene-cut-guard" $sparseDecisionOutput "strictThresholdStops=False"

$sparseMaterializeOutput = Invoke-ScriptStep "automask-sparse-materialize-scene-cut" $autoMaskSparseMaterializeSceneCutVerify @()
Assert-Contains "automask-sparse-materialize-scene-cut" $sparseMaterializeOutput "\[AutoMaskSparseMaterializeSceneCutVerify\]"
Assert-Contains "automask-sparse-materialize-scene-cut" $sparseMaterializeOutput "yoloHardCutInterpolated=0"
Assert-Contains "automask-sparse-materialize-scene-cut" $sparseMaterializeOutput "yoloSceneCutStops=1"
Assert-Contains "automask-sparse-materialize-scene-cut" $sparseMaterializeOutput "yoloSceneCutTransitions=0->5"
Assert-Contains "automask-sparse-materialize-scene-cut" $sparseMaterializeOutput "cutBeforePositiveInterpolated=0"
Assert-Contains "automask-sparse-materialize-scene-cut" $sparseMaterializeOutput "faceOnnxInterpolated=4"

if ($RunYoloPseudoGtState) {
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
}

$autoNoDetectionReviewOutput = Invoke-ScriptStep "auto-no-detection-review" $autoNoDetectionReviewVerify @()
Assert-Contains "auto-no-detection-review" $autoNoDetectionReviewOutput "\[AutoNoDetectionReviewVerify\] all requested checks passed"

$detectionOverlayOutput = Invoke-ScriptStep "yolo-detection-overlay-video" $yoloDetectionOverlayVideoVerify @()
Assert-Contains "yolo-detection-overlay-video" $detectionOverlayOutput "\[YoloDetectionOverlayVideoVerify\] all requested checks passed"
Assert-Contains "yolo-detection-overlay-video" $detectionOverlayOutput "actual=false"

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
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "extendedGapFilled=8"
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "extendedGapFrames=301,302,303,304,305,306,307,308"
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
Assert-Contains "yolo-final-mask-cleanup" $finalMaskCleanupOutput "areaChangedStrongCarryProtected=2"
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
    "-ProcessingMode", "Legacy",
    "-ParallelDetectorCount", "2",
    "-MinAvgIou", "0.99",
    "-MinBestIou", "0.99",
    "-AllowFrameMismatch"
)
Assert-Contains "quality-gate-all-frame-parallel" $qualityOutput "\[SmokeQualityGate\] passed=True"
Assert-Contains "quality-gate-all-frame-parallel" $qualityOutput "onlyBaseline=0"
Assert-Contains "quality-gate-all-frame-parallel" $qualityOutput "boxCountDiffFrames=0"
Assert-Contains "quality-gate-all-frame-parallel" $qualityOutput "avgBestIou=1\.000"
Assert-Contains "quality-gate-all-frame-parallel" $qualityOutput "minBestIou=1\.000"

$roiHitOutput = Invoke-Step "roi-refiner-hit-representative" @(
    "-SkipTrim",
    "-Source", $RoiHitClip,
    "-SkipBaseline",
    "-SkipExport",
    "-OptimizedCpuOnly",
    "-OptimizedNoTracking",
    "-ProcessingMode", "Legacy",
    "-ParallelDetectorCount", "2"
)
Assert-Contains "roi-refiner-hit-representative" $roiHitOutput "\[SmokeFaceTrackRoiRefine\].*attempts=[1-9][0-9]*"
Assert-Contains "roi-refiner-hit-representative" $roiHitOutput "\[SmokeFaceTrackRoiRefine\].*hits=[1-9][0-9]*"
Assert-Contains "roi-refiner-hit-representative" $roiHitOutput "\[SmokeFaceTrackRoiRefine\].*seeks=[1-9][0-9]*"
Assert-Contains "roi-refiner-hit-representative" $roiHitOutput "\[SmokeFaceTrackRoiRefine\].*decoded=[1-9][0-9]*"

if ($RunExportSmoke) {
    $exportOutput = Invoke-Step "direct-face-export-smoke" @(
        "-SkipTrim",
        "-Source", $ExportClip,
        "-SkipBaseline",
        "-OptimizedCpuOnly",
        "-OptimizedNoTracking",
        "-ProcessingMode", "Legacy",
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
        "-ProcessingMode", "Legacy",
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
        "-ProcessingMode", "Legacy",
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
Assert-Contains "default-autotune-provider-short" $shortTuneOutput "\[AutoRunSummary\].*tracking=True.*post=False.*processingMode=Tracked"
Assert-AutoTuneModeMatchesSessions "default-autotune-provider-short" $shortTuneOutput 2
Assert-Contains "default-autotune-provider-short" $shortTuneOutput "\[AutoRunFrameCountAdjusted\].*reported=150, actual=148.*decoded=148"
Assert-Contains "default-autotune-provider-short" $shortTuneOutput "\[AutoRunSummary\].*totalFrames=148.*processed=148.*decoded=148.*decodeEof=true.*detects=148"
Assert-Contains "default-autotune-provider-short" $shortTuneOutput "interpolated=0"

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
    Assert-AutoTuneModeMatchesSessions "default-autotune-provider-long" $longTuneOutput 2
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
