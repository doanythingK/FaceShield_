param(
    [string]$ReviewIndex = ".tmp\yolo-full-gt\review-package-smoke\review-index.html",
    [string]$FullGtReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-gt-review.csv",
    [string]$FullFrameReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-frame-review.csv",
    [string]$GuiChecklistCsv = ".tmp\yolo-gui-smoke\manual-smoke-checklist.csv",
    [string]$PredictionLog = ".tmp\yolo-ten-minute-detection-smoke\yolo-ten-minute-yolo-only-20260523-022022.log",
    [double]$MinIou = 0.50,
    [int]$MaxMisses = 0,
    [int]$MaxFalsePositives = 0,
    [int]$MaxLowIou = 0,
    [switch]$Open,
    [switch]$VerifyReady,
    [switch]$VerifyCompleted
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$manualReadinessVerifier = Join-Path $repo "scripts\verify-yolo-manual-readiness-state.ps1"
$fullGtReviewedVerifier = Join-Path $repo "scripts\verify-yolo-full-gt-reviewed-state.ps1"
$guiSmokeVerifier = Join-Path $repo "scripts\verify-yolo-gui-smoke-state.ps1"

function Resolve-RepoPath {
    param([string]$Path)

    if ([IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return Join-Path $repo $Path
}

function Assert-FileNonEmpty {
    param(
        [string]$Name,
        [string]$Path
    )

    $resolved = Resolve-RepoPath $Path
    if (-not (Test-Path $resolved)) {
        throw "$Name not found: $resolved"
    }

    $item = Get-Item $resolved
    if ($item -isnot [IO.FileInfo]) {
        throw "$Name is not a file: $resolved"
    }

    if ($item.Length -le 0) {
        throw "$Name is empty: $resolved"
    }

    return $resolved
}

function Invoke-RequiredVerifier {
    param(
        [string]$Name,
        [string]$ScriptPath,
        [string[]]$Arguments
    )

    if (-not (Test-Path $ScriptPath)) {
        throw "$Name verifier not found: $ScriptPath"
    }

    Write-Host "[YoloManualGate] start $Name"
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $ScriptPath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE"
    }

    Write-Host "[YoloManualGate] pass $Name"
}

$resolvedReviewIndex = Assert-FileNonEmpty "review index" $ReviewIndex
$resolvedFullGtReviewCsv = Assert-FileNonEmpty "full GT review CSV" $FullGtReviewCsv
$resolvedFullFrameReviewCsv = Assert-FileNonEmpty "full-frame review CSV" $FullFrameReviewCsv
$resolvedGuiChecklistCsv = Assert-FileNonEmpty "GUI checklist CSV" $GuiChecklistCsv
$resolvedPredictionLog = Assert-FileNonEmpty "prediction log" $PredictionLog

Write-Host "[YoloManualGate] reviewIndex=$resolvedReviewIndex"
Write-Host "[YoloManualGate] fullGtReviewCsv=$resolvedFullGtReviewCsv"
Write-Host "[YoloManualGate] fullFrameReviewCsv=$resolvedFullFrameReviewCsv"
Write-Host "[YoloManualGate] guiChecklistCsv=$resolvedGuiChecklistCsv"
Write-Host "[YoloManualGate] predictionLog=$resolvedPredictionLog"
Write-Host "[YoloManualGate] completedFullGtCommand=powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\verify-yolo-full-gt-reviewed-state.ps1 -ReviewCsv `"$FullGtReviewCsv`" -FullFrameReviewCsv `"$FullFrameReviewCsv`" -PredictionLog `"$PredictionLog`" -RequireFullFrameReview -RequireEvidence -RequireArtifacts -MinIou $MinIou -MaxMisses $MaxMisses -MaxFalsePositives $MaxFalsePositives -MaxLowIou $MaxLowIou"
Write-Host "[YoloManualGate] completedGuiSmokeCommand=powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\verify-yolo-gui-smoke-state.ps1 -ChecklistCsv `"$GuiChecklistCsv`" -RequireManualPass"

if ($Open) {
    foreach ($path in @($resolvedReviewIndex, $resolvedFullGtReviewCsv, $resolvedFullFrameReviewCsv, $resolvedGuiChecklistCsv)) {
        Invoke-Item $path
    }

    Write-Host "[YoloManualGate] opened manual review artifacts"
}

if ($VerifyReady) {
    Invoke-RequiredVerifier "manual-readiness-state" $manualReadinessVerifier @()
}

if ($VerifyCompleted) {
    Invoke-RequiredVerifier "full-gt-reviewed-state" $fullGtReviewedVerifier @(
        "-ReviewCsv", $resolvedFullGtReviewCsv,
        "-FullFrameReviewCsv", $resolvedFullFrameReviewCsv,
        "-PredictionLog", $resolvedPredictionLog,
        "-RequireFullFrameReview",
        "-RequireEvidence",
        "-RequireArtifacts",
        "-MinIou", "$MinIou",
        "-MaxMisses", "$MaxMisses",
        "-MaxFalsePositives", "$MaxFalsePositives",
        "-MaxLowIou", "$MaxLowIou"
    )
    Invoke-RequiredVerifier "gui-smoke-state" $guiSmokeVerifier @(
        "-ChecklistCsv", $resolvedGuiChecklistCsv,
        "-RequireManualPass"
    )
}

Write-Host "[YoloManualGate] all requested checks passed"
