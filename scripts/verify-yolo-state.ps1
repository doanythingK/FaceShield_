param(
    [string]$YoloCropReviewPassCsv = ".tmp/yolo-crops/test-0900-yolo5face/crop-review.csv",
    [string]$YoloCropReviewFailCsv = ".tmp/yolo-crops/test-0600-30s-yolo5face/crop-review.csv",
    [switch]$RunRepresentativeGate,
    [string]$RepresentativeQualityClip = ".tmp/srcTest-smoke/smoke-0600-3s.mp4",
    [string]$RepresentativeYoloModelPath = "",
    [switch]$RunExtendedGate,
    [string]$ExtendedQualityClip = ".tmp/srcTest-smoke/smoke-0600-30s.mp4",
    [string]$ExtendedYoloModelPath = "",
    [switch]$RunExtendedExportGate,
    [string]$ExtendedExportQualityClip = ".tmp/srcTest-smoke/smoke-0600-30s.mp4",
    [string]$ExtendedExportYoloModelPath = "",
    [switch]$RunTenMinuteState,
    [switch]$RequireTenMinuteClip,
    [switch]$RequireTenMinuteRun,
    [switch]$RequireTenMinuteBaselineOnlyRun,
    [switch]$RequireTenMinuteIncompleteBaselineFullAttempt,
    [switch]$RequireTenMinuteFaceOnnxOptimizedOnlyRun,
    [switch]$RequireTenMinutePartialSpeedCompareRun,
    [switch]$RunGuiSmokeState,
    [switch]$RequireGuiSmokeManualPass,
    [switch]$AllowCompletedFullGt,
    [switch]$AllowCompletedGuiSmoke,
    [string]$FullGtPredictionCsv = "",
    [string]$FullGtPredictionLog = ".tmp\yolo-ten-minute-detection-smoke\yolo-ten-minute-yolo-only-20260523-022022.log",
    [double]$FullGtMinIou = 0.50,
    [int]$FullGtMaxMisses = 0,
    [int]$FullGtMaxFalsePositives = 0,
    [int]$FullGtMaxLowIou = 0
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

function Invoke-YoloVerify {
    param(
        [string]$Name,
        [string]$ScriptPath,
        [string[]]$Arguments
    )

    if (-not (Test-Path $ScriptPath)) {
        throw "$Name verifier not found: $ScriptPath"
    }

    Write-Host "[YoloStateVerify] start $Name"
    $oldErrorAction = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $ScriptPath @Arguments 2>&1
        $exitCode = $LASTEXITCODE
        $text = ($output | Out-String)
        Write-Host $text
    }
    finally {
        $ErrorActionPreference = $oldErrorAction
    }

    if ($exitCode -ne 0) {
        throw "$Name failed with exit code $exitCode"
    }

    Write-Host "[YoloStateVerify] pass $Name"
}

$profileStateVerify = Join-Path $repo "scripts\verify-yolo-profile-state.ps1"
$cropReviewVerify = Join-Path $repo "scripts\verify-yolo-crop-review.ps1"
$gtLabelStateVerify = Join-Path $repo "scripts\verify-yolo-gt-label-state.ps1"
$fullGtLabelStateVerify = Join-Path $repo "scripts\verify-yolo-full-gt-label-state.ps1"
$fullGtTemplateStateVerify = Join-Path $repo "scripts\verify-yolo-full-gt-template-state.ps1"
$fullGtReviewPackageStateVerify = Join-Path $repo "scripts\verify-yolo-full-gt-review-package-state.ps1"
$fullGtReviewedStateVerify = Join-Path $repo "scripts\verify-yolo-full-gt-reviewed-state.ps1"
$fullGtReviewedCandidateStateVerify = Join-Path $repo "scripts\verify-yolo-full-gt-reviewed-candidate-state.ps1"
$conclusionStateVerify = Join-Path $repo "scripts\verify-yolo-conclusion-state.ps1"
$distributionStateVerify = Join-Path $repo "scripts\verify-yolo-distribution-state.ps1"
$goalAuditStateVerify = Join-Path $repo "scripts\verify-yolo-goal-audit-state.ps1"
$manualReadinessStateVerify = Join-Path $repo "scripts\verify-yolo-manual-readiness-state.ps1"
$manualGateHelperStateVerify = Join-Path $repo "scripts\verify-yolo-manual-gate-helper-state.ps1"
$guiSmokeStateVerify = Join-Path $repo "scripts\verify-yolo-gui-smoke-state.ps1"
$representativeGateVerify = Join-Path $repo "scripts\verify-yolo-representative-gate.ps1"
$extendedGateVerify = Join-Path $repo "scripts\verify-yolo-extended-gate.ps1"
$extendedExportGateVerify = Join-Path $repo "scripts\verify-yolo-extended-export-gate.ps1"
$tenMinuteStateVerify = Join-Path $repo "scripts\verify-yolo-ten-minute-state.ps1"

Invoke-YoloVerify "profile-state" $profileStateVerify @()
Invoke-YoloVerify "crop-review" $cropReviewVerify @(
    "-PassReviewCsv", $YoloCropReviewPassCsv,
    "-FailReviewCsv", $YoloCropReviewFailCsv
)
Invoke-YoloVerify "gt-label-state" $gtLabelStateVerify @(
    "-PassReviewCsv", $YoloCropReviewPassCsv,
    "-FailReviewCsv", $YoloCropReviewFailCsv
)
Invoke-YoloVerify "full-gt-label-state" $fullGtLabelStateVerify @(
    "-SelfTest"
)
Invoke-YoloVerify "full-gt-template-state" $fullGtTemplateStateVerify @()
Invoke-YoloVerify "full-gt-review-package-state" $fullGtReviewPackageStateVerify @()
Invoke-YoloVerify "full-gt-reviewed-state" $fullGtReviewedStateVerify @(
    "-SelfTest"
)
Invoke-YoloVerify "full-gt-reviewed-candidate-state" $fullGtReviewedCandidateStateVerify @()
Invoke-YoloVerify "conclusion-state" $conclusionStateVerify @()
Invoke-YoloVerify "distribution-state" $distributionStateVerify @()
Invoke-YoloVerify "goal-audit-state" $goalAuditStateVerify @()
Invoke-YoloVerify "manual-gate-helper-state" $manualGateHelperStateVerify @()
$manualReadinessArgs = @()
if ($AllowCompletedFullGt) {
    $manualReadinessArgs += "-AllowCompletedFullGt"
}
if ($AllowCompletedGuiSmoke) {
    $manualReadinessArgs += "-AllowCompletedGuiSmoke"
}
if (-not [string]::IsNullOrWhiteSpace($FullGtPredictionCsv)) {
    $manualReadinessArgs += @("-FullGtPredictionCsv", $FullGtPredictionCsv)
}
else {
    $manualReadinessArgs += @("-FullGtPredictionLog", $FullGtPredictionLog)
}
$manualReadinessArgs += @(
    "-FullGtMinIou", "$FullGtMinIou",
    "-FullGtMaxMisses", "$FullGtMaxMisses",
    "-FullGtMaxFalsePositives", "$FullGtMaxFalsePositives",
    "-FullGtMaxLowIou", "$FullGtMaxLowIou"
)
Invoke-YoloVerify "manual-readiness-state" $manualReadinessStateVerify $manualReadinessArgs
if ($RunRepresentativeGate) {
    Invoke-YoloVerify "representative-gate" $representativeGateVerify @(
        "-QualityClip", $RepresentativeQualityClip,
        "-YoloModelPath", $RepresentativeYoloModelPath
    )
}
if ($RunExtendedGate) {
    Invoke-YoloVerify "extended-gate" $extendedGateVerify @(
        "-QualityClip", $ExtendedQualityClip,
        "-YoloModelPath", $ExtendedYoloModelPath
    )
}
if ($RunExtendedExportGate) {
    Invoke-YoloVerify "extended-export-gate" $extendedExportGateVerify @(
        "-QualityClip", $ExtendedExportQualityClip,
        "-YoloModelPath", $ExtendedExportYoloModelPath
    )
}
if ($RunTenMinuteState) {
    $tenMinuteArgs = @()
    if ($RequireTenMinuteClip) {
        $tenMinuteArgs += "-RequireClip"
    }
    if ($RequireTenMinuteRun) {
        $tenMinuteArgs += "-RequireRun"
    }
    if ($RequireTenMinuteBaselineOnlyRun) {
        $tenMinuteArgs += "-RequireBaselineOnlyRun"
    }
    if ($RequireTenMinuteIncompleteBaselineFullAttempt) {
        $tenMinuteArgs += "-RequireIncompleteBaselineFullAttempt"
    }
    if ($RequireTenMinuteFaceOnnxOptimizedOnlyRun) {
        $tenMinuteArgs += "-RequireFaceOnnxOptimizedOnlyRun"
    }
    if ($RequireTenMinutePartialSpeedCompareRun) {
        $tenMinuteArgs += "-RequirePartialSpeedCompareRun"
    }

    Invoke-YoloVerify "ten-minute-state" $tenMinuteStateVerify $tenMinuteArgs
}
if ($RunGuiSmokeState) {
    $guiSmokeArgs = @()
    if ($RequireGuiSmokeManualPass) {
        $guiSmokeArgs += "-RequireManualPass"
    }

    Invoke-YoloVerify "gui-smoke-state" $guiSmokeStateVerify $guiSmokeArgs
}

Write-Host "[YoloStateVerify] all requested checks passed"
