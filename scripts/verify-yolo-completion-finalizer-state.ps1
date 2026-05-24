param(
    [string]$PlanPath = "AUTO_MOSAIC_QUALITY_SPEED_PLAN.md",
    [string]$FullGtReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-gt-review.csv",
    [string]$FullFrameReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-frame-review.csv",
    [string]$GuiChecklistCsv = ".tmp\yolo-gui-smoke\manual-smoke-checklist.csv",
    [string]$PredictionLog = ".tmp\yolo-ten-minute-detection-smoke\yolo-ten-minute-yolo-only-20260523-022022.log",
    [string]$ManualGateSummary = ".tmp\yolo-manual-gates\manual-gate-summary.md"
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$finalizer = Join-Path $repo "scripts\complete-yolo-goal-after-manual-gates.ps1"

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

function Assert-Contains {
    param(
        [string]$Name,
        [string]$Text,
        [string]$Expected
    )

    if (-not $Text.Contains($Expected)) {
        throw "$Name missing text: $Expected"
    }

    Write-Host "[YoloCompletionFinalizerStateVerify] pass $Name"
}

function Get-FilledCount {
    param(
        [object[]]$Rows,
        [string]$Column
    )

    return @($Rows | Where-Object {
        $null -ne $_.PSObject.Properties[$Column] -and -not [string]::IsNullOrWhiteSpace($_.$Column)
    }).Count
}

if (-not (Test-Path $finalizer)) {
    throw "completion finalizer not found: $finalizer"
}

$finalizerText = Get-Content -Raw -Path $finalizer
Assert-Contains "finalizer updates goal marker" $finalizerText "Update-GoalAuditMarker"
Assert-Contains "finalizer requires strict full GT" $finalizerText "full-gt-reviewed-state"
Assert-Contains "finalizer requires GUI smoke" $finalizerText "gui-smoke-state"
Assert-Contains "finalizer runs completion audit" $finalizerText "completion-audit-complete"
Assert-Contains "finalizer writes evidence report" $finalizerText "goal-evidence-report-complete"
Assert-Contains "finalizer has selftest" $finalizerText "SelfTest"

Write-Host "[YoloCompletionFinalizerStateVerify] start finalizer selftest"
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $finalizer -SelfTest
if ($LASTEXITCODE -ne 0) {
    throw "Finalizer selftest failed with exit code $LASTEXITCODE"
}
Write-Host "[YoloCompletionFinalizerStateVerify] pass finalizer selftest"

$planPath = Assert-FileNonEmpty "plan document" $PlanPath
$reviewCsv = Assert-FileNonEmpty "full GT review CSV" $FullGtReviewCsv
$frameCsv = Assert-FileNonEmpty "full-frame review CSV" $FullFrameReviewCsv
$guiCsv = Assert-FileNonEmpty "GUI checklist CSV" $GuiChecklistCsv
$predictionLogPath = Assert-FileNonEmpty "prediction log" $PredictionLog
$summaryPath = Assert-FileNonEmpty "manual gate summary" $ManualGateSummary

$reviewRows = @(Import-Csv $reviewCsv)
$frameRows = @(Import-Csv $frameCsv)
$guiRows = @(Import-Csv $guiCsv)

if ($reviewRows.Count -eq 0) {
    throw "Full GT review CSV has no rows: $reviewCsv"
}
if ($frameRows.Count -eq 0) {
    throw "Full-frame review CSV has no rows: $frameCsv"
}
if ($guiRows.Count -eq 0) {
    throw "GUI checklist CSV has no rows: $guiCsv"
}

$reviewedRows = Get-FilledCount $reviewRows "label"
$frameReviewedRows = Get-FilledCount $frameRows "missedFaceCount"
$guiStatusRows = Get-FilledCount $guiRows "status"
$hasPendingEvidence = $reviewedRows -lt $reviewRows.Count -or
    $frameReviewedRows -lt $frameRows.Count -or
    $guiStatusRows -lt $guiRows.Count

$oldErrorAction = $ErrorActionPreference
try {
    $ErrorActionPreference = "Continue"
    $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $finalizer `
        -PlanPath $planPath `
        -FullGtReviewCsv $reviewCsv `
        -FullFrameReviewCsv $frameCsv `
        -GuiChecklistCsv $guiCsv `
        -PredictionLog $predictionLogPath `
        -ManualGateSummary $summaryPath 2>&1
    $exitCode = $LASTEXITCODE
    $text = ($output | Out-String)
    Write-Host $text
}
finally {
    $ErrorActionPreference = $oldErrorAction
}

if ($hasPendingEvidence) {
    if ($exitCode -eq 0) {
        throw "Finalizer unexpectedly passed with pending manual evidence."
    }

    if ($text -notmatch "unreviewed rows|manual checklist|status|evidence|full-gt-reviewed-state|gui-smoke-state") {
        throw "Finalizer pending-evidence failure reason was unexpected. output=$text"
    }

    Write-Host "[YoloCompletionFinalizerStateVerify] pass current pending evidence blocks finalizer"
    Write-Host "[YoloCompletionFinalizerStateVerify] finalizable=false, reviewedRows=$reviewedRows/$($reviewRows.Count), fullFrameRows=$frameReviewedRows/$($frameRows.Count), guiRows=$guiStatusRows/$($guiRows.Count)"
    Write-Host "[YoloCompletionFinalizerStateVerify] all requested checks passed"
    exit 0
}

if ($exitCode -ne 0) {
    throw "Manual evidence appears filled, but finalizer still failed with exit code $exitCode. output=$text"
}

Write-Host "[YoloCompletionFinalizerStateVerify] finalizable=true, reviewedRows=$reviewedRows/$($reviewRows.Count), fullFrameRows=$frameReviewedRows/$($frameRows.Count), guiRows=$guiStatusRows/$($guiRows.Count)"
Write-Host "[YoloCompletionFinalizerStateVerify] all requested checks passed"
