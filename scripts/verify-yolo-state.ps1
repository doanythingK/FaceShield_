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
    [switch]$RunGuiSmokeState,
    [switch]$RequireGuiSmokeManualPass,
    [string]$GuiChecklistCsv = ".tmp\yolo-gui-smoke\manual-smoke-checklist.csv",
    [string]$GuiEvidenceDir = ".tmp\yolo-gui-smoke\evidence",
    [string]$GuiEvidenceGuidePath = ".tmp\yolo-gui-smoke\gui-smoke-evidence-guide.md",
    [switch]$AllowCompletedFullGt,
    [switch]$AllowCompletedGuiSmoke,
    [switch]$RequireComplete,
    [string]$FullGtPredictionCsv = "",
    [string]$FullGtPredictionLog = ".tmp\yolo-ten-minute-detection-smoke\yolo-ten-minute-yolo-only-20260523-022022.log",
    [string]$FullGtReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-gt-review.csv",
    [string]$FullFrameReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-frame-review.csv",
    [double]$FullGtMinIou = 0.50,
    [int]$FullGtMaxMisses = 0,
    [int]$FullGtMaxFalsePositives = 0,
    [int]$FullGtMaxLowIou = 0,
    [switch]$AllowFullGtQualityGateFailure,
    [switch]$IgnoreCompletedGoalAuditMarker,
    [switch]$SkipCompletionAuditState
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

function New-CompletionAuditArgs {
    $args = @(
        "-MinIou", "$FullGtMinIou",
        "-MaxMisses", "$FullGtMaxMisses",
        "-MaxFalsePositives", "$FullGtMaxFalsePositives",
        "-MaxLowIou", "$FullGtMaxLowIou"
    )

    if (-not [string]::IsNullOrWhiteSpace($FullGtPredictionCsv)) {
        $args += @("-PredictionCsv", $FullGtPredictionCsv)
    }
    else {
        $args += @("-PredictionLog", $FullGtPredictionLog)
    }

    if ($requireCompleteForCurrentState) {
        $args += "-RequireComplete"
    }
    if ($AllowFullGtQualityGateFailure -or $allowFullGtQualityFailureForCurrentState) {
        $args += "-AllowQualityGateFailure"
    }

    return $args
}

function Resolve-RepoPath {
    param([string]$Path)

    if ([IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return Join-Path $repo $Path
}

function Get-CsvValue {
    param(
        [object]$Row,
        [string]$Column
    )

    if ($null -eq $Row.PSObject.Properties[$Column]) {
        return ""
    }

    return [string]$Row.$Column
}

function Test-CompletedFullGtReview {
    $reviewPath = Resolve-RepoPath $FullGtReviewCsv
    $framePath = Resolve-RepoPath $FullFrameReviewCsv
    if (-not (Test-Path $reviewPath) -or -not (Test-Path $framePath)) {
        return $false
    }

    $reviewRows = @(Import-Csv $reviewPath)
    $frameRows = @(Import-Csv $framePath)
    if ($reviewRows.Count -eq 0 -or $frameRows.Count -eq 0) {
        return $false
    }

    $labeledRows = @($reviewRows | Where-Object {
        -not [string]::IsNullOrWhiteSpace((Get-CsvValue $_ "label"))
    })
    $reviewedFrameRows = @($frameRows | Where-Object {
        -not [string]::IsNullOrWhiteSpace((Get-CsvValue $_ "missedFaceCount"))
    })

    return $labeledRows.Count -eq $reviewRows.Count -and
        $reviewedFrameRows.Count -eq $frameRows.Count
}

function Test-DocumentedFullGtQualityFailureAllowed {
    $planPath = Resolve-RepoPath "AUTO_MOSAIC_QUALITY_SPEED_PLAN.md"
    if (-not (Test-Path $planPath)) {
        return $false
    }

    $plan = Get-Content -Raw -Path $planPath
    return $plan.Contains("full-gt-quality-failure-allowed=pass") -or
        $plan.Contains("fullGtQualityGate=fail-documented")
}

function Test-YoloGoalAuditAlreadyComplete {
    $planPath = Resolve-RepoPath "AUTO_MOSAIC_QUALITY_SPEED_PLAN.md"
    if (-not (Test-Path $planPath)) {
        return $false
    }

    $plan = Get-Content -Raw -Path $planPath
    $matches = [regex]::Matches($plan, "<!--\s*yolo-goal-audit-state:[\s\S]*?-->")
    if ($matches.Count -eq 0) {
        return $false
    }

    $marker = $matches[$matches.Count - 1].Value
    return $marker.Contains("complete=true") -and $marker.Contains("remaining=none")
}

$profileStateVerify = Join-Path $repo "scripts\verify-yolo-profile-state.ps1"
$startupSmokeStateVerify = Join-Path $repo "scripts\verify-yolo-startup-smoke-state.ps1"
$trackHoldStateVerify = Join-Path $repo "scripts\verify-yolo-track-hold-state.ps1"
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
$topLevelRequireCompleteStateVerify = Join-Path $repo "scripts\verify-yolo-top-level-require-complete-state.ps1"
$completionAuditStateVerify = Join-Path $repo "scripts\verify-yolo-completion-audit-state.ps1"
$completionFinalizerStateVerify = Join-Path $repo "scripts\verify-yolo-completion-finalizer-state.ps1"
$guiEvidencePrep = Join-Path $repo "scripts\prepare-yolo-gui-smoke-evidence.ps1"
$manualPendingReportWriter = Join-Path $repo "scripts\write-yolo-manual-pending-report.ps1"
$manualReadinessStateVerify = Join-Path $repo "scripts\verify-yolo-manual-readiness-state.ps1"
$manualGateHelperStateVerify = Join-Path $repo "scripts\verify-yolo-manual-gate-helper-state.ps1"
$guiSmokeStateVerify = Join-Path $repo "scripts\verify-yolo-gui-smoke-state.ps1"
$representativeGateVerify = Join-Path $repo "scripts\verify-yolo-representative-gate.ps1"
$extendedGateVerify = Join-Path $repo "scripts\verify-yolo-extended-gate.ps1"
$extendedExportGateVerify = Join-Path $repo "scripts\verify-yolo-extended-export-gate.ps1"

$fullGtReviewAlreadyCompleted = Test-CompletedFullGtReview
$allowCompletedFullGtForCurrentState = $AllowCompletedFullGt -or $fullGtReviewAlreadyCompleted
$allowFullGtQualityFailureForCurrentState = $AllowFullGtQualityGateFailure -or
    ($fullGtReviewAlreadyCompleted -and (Test-DocumentedFullGtQualityFailureAllowed))
$requireCompleteForCurrentState = $RequireComplete -or
    ((-not $IgnoreCompletedGoalAuditMarker) -and (Test-YoloGoalAuditAlreadyComplete))

$completionAuditAlreadyRan = $false
if ($RequireComplete) {
    Invoke-YoloVerify "completion-audit-require-complete-guard" $completionAuditStateVerify (New-CompletionAuditArgs)
    $completionAuditAlreadyRan = $true
}

Invoke-YoloVerify "profile-state" $profileStateVerify @()
Invoke-YoloVerify "startup-smoke-state" $startupSmokeStateVerify @()
Invoke-YoloVerify "track-hold-state" $trackHoldStateVerify @()
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
Invoke-YoloVerify "top-level-require-complete-state" $topLevelRequireCompleteStateVerify @()
Invoke-YoloVerify "manual-gate-helper-state" $manualGateHelperStateVerify @()
$manualReadinessArgs = @()
if ($allowCompletedFullGtForCurrentState) {
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
    "-FullGtMaxLowIou", "$FullGtMaxLowIou",
    "-FullGtReviewCsv", $FullGtReviewCsv,
    "-FullFrameReviewCsv", $FullFrameReviewCsv
)
if ($allowFullGtQualityFailureForCurrentState) {
    $manualReadinessArgs += "-AllowQualityGateFailure"
}
Invoke-YoloVerify "manual-readiness-state" $manualReadinessStateVerify $manualReadinessArgs
if (-not $completionAuditAlreadyRan -and -not $SkipCompletionAuditState) {
    Invoke-YoloVerify "completion-audit-state" $completionAuditStateVerify (New-CompletionAuditArgs)
}
Invoke-YoloVerify "completion-audit-selftest" $completionAuditStateVerify @(
    "-SelfTest"
)
$completionFinalizerArgs = @(
    "-MinIou", "$FullGtMinIou",
    "-MaxMisses", "$FullGtMaxMisses",
    "-MaxFalsePositives", "$FullGtMaxFalsePositives",
    "-MaxLowIou", "$FullGtMaxLowIou"
)
if ($allowFullGtQualityFailureForCurrentState) {
    $completionFinalizerArgs += "-AllowQualityGateFailure"
}
Invoke-YoloVerify "completion-finalizer-state" $completionFinalizerStateVerify $completionFinalizerArgs
Invoke-YoloVerify "gui-smoke-evidence-prep" $guiEvidencePrep @(
    "-ChecklistCsv", $GuiChecklistCsv,
    "-EvidenceDir", $GuiEvidenceDir,
    "-GuidePath", $GuiEvidenceGuidePath,
    "-UpdateChecklist",
    "-Verify"
)
Invoke-YoloVerify "manual-pending-report" $manualPendingReportWriter @(
    "-GuiChecklistCsv", $GuiChecklistCsv,
    "-Verify"
)
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
if ($RunGuiSmokeState) {
    $guiSmokeArgs = @()
    if ($RequireGuiSmokeManualPass) {
        $guiSmokeArgs += "-RequireManualPass"
    }

    Invoke-YoloVerify "gui-smoke-state" $guiSmokeStateVerify $guiSmokeArgs
}

Write-Host "[YoloStateVerify] all requested checks passed"
