param(
    [string]$FullGtReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-gt-review.csv",
    [string]$FullFrameReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-frame-review.csv",
    [string]$GuiChecklistCsv = ".tmp\yolo-gui-smoke\manual-smoke-checklist.csv",
    [string]$ReviewIndex = ".tmp\yolo-full-gt\review-package-smoke\review-index.html",
    [string]$AiCandidateReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-gt-review-reviewed-candidate.csv",
    [string]$AiCandidateFullFrameReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-frame-review-reviewed-candidate.csv",
    [string]$OutputPath = ".tmp\yolo-manual-gates\manual-pending-report.md",
    [int]$MaxRows = 12,
    [switch]$Verify
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

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

function Count-Filled {
    param(
        [object[]]$Rows,
        [string]$Column
    )

    return @($Rows | Where-Object {
        $null -ne $_.PSObject.Properties[$Column] -and -not [string]::IsNullOrWhiteSpace($_.$Column)
    }).Count
}

function Escape-Cell {
    param([string]$Value)

    if ($null -eq $Value) {
        return ""
    }

    return ($Value -replace "\|", "\|") -replace "`r?`n", " "
}

function Get-Value {
    param(
        [object]$Row,
        [string]$Column
    )

    if ($null -eq $Row.PSObject.Properties[$Column]) {
        return ""
    }

    return [string]$Row.$Column
}

function Assert-ReportContains {
    param(
        [string]$Name,
        [string]$Text,
        [string]$Expected
    )

    if (-not $Text.Contains($Expected)) {
        throw "$Name missing text: $Expected"
    }

    Write-Host "[YoloManualPendingReport] pass $Name"
}

$reviewCsv = Assert-FileNonEmpty "full GT review CSV" $FullGtReviewCsv
$frameCsv = Assert-FileNonEmpty "full-frame review CSV" $FullFrameReviewCsv
$guiCsv = Assert-FileNonEmpty "GUI checklist CSV" $GuiChecklistCsv
$reviewIndexPath = Assert-FileNonEmpty "review index" $ReviewIndex
$candidateReviewPath = Resolve-RepoPath $AiCandidateReviewCsv
$candidateFramePath = Resolve-RepoPath $AiCandidateFullFrameReviewCsv
$hasCandidateReview = Test-Path $candidateReviewPath
$hasCandidateFrameReview = Test-Path $candidateFramePath
if ($hasCandidateReview) {
    Assert-FileNonEmpty "AI-assisted candidate full GT review CSV" $candidateReviewPath | Out-Null
}
if ($hasCandidateFrameReview) {
    Assert-FileNonEmpty "AI-assisted candidate full-frame review CSV" $candidateFramePath | Out-Null
}

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

$pendingReviewRows = @($reviewRows | Where-Object {
    [string]::IsNullOrWhiteSpace((Get-Value $_ "label")) -or
    [string]::IsNullOrWhiteSpace((Get-Value $_ "reviewStatus")) -or
    [string]::IsNullOrWhiteSpace((Get-Value $_ "evidenceNotes"))
})
$pendingFrameRows = @($frameRows | Where-Object {
    [string]::IsNullOrWhiteSpace((Get-Value $_ "missedFaceCount")) -or
    [string]::IsNullOrWhiteSpace((Get-Value $_ "missedFaceRowsAdded")) -or
    [string]::IsNullOrWhiteSpace((Get-Value $_ "reviewStatus")) -or
    [string]::IsNullOrWhiteSpace((Get-Value $_ "evidenceNotes"))
})
$pendingGuiRows = @($guiRows | Where-Object {
    [string]::IsNullOrWhiteSpace((Get-Value $_ "status")) -or
    [string]::IsNullOrWhiteSpace((Get-Value $_ "evidenceType")) -or
    [string]::IsNullOrWhiteSpace((Get-Value $_ "artifactPath")) -or
    [string]::IsNullOrWhiteSpace((Get-Value $_ "evidence"))
})

$reviewedRows = Count-Filled $reviewRows "label"
$frameReviewedRows = Count-Filled $frameRows "missedFaceCount"
$guiStatusRows = Count-Filled $guiRows "status"
$max = [Math]::Max(1, $MaxRows)

$resolvedOutput = Resolve-RepoPath $OutputPath
$outputDir = Split-Path -Parent $resolvedOutput
if (-not [string]::IsNullOrWhiteSpace($outputDir)) {
    New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
}

$lines = @(
    "# YOLO Manual Pending Report",
    "",
    "This report lists the current manual review rows still blocking YOLO goal completion.",
    "",
    "## Input Rules",
    "- full-gt-review.csv label: use face for a real visible face, nonface for detector false positives. The verifier also accepts actualface/true/1 and false/0, but the review package should use face/nonface for clarity.",
    "- full-gt-review.csv reviewStatus: use pass after the crop row has been reviewed, and fill evidenceNotes with the visual reason.",
    "- full-frame-review.csv missedFaceCount: enter the number of visible faces in that frame that are not covered by detection crop rows.",
    "- full-frame-review.csv missedFaceRowsAdded: enter the number of manual missed-face rows added to full-gt-review.csv for that frame; it must match missedFaceCount.",
    "- GUI checklist status: use pass only after the manual step is actually verified. preview-track-hold requires evidenceType=recording and a non-empty video artifact.",
    "",
    "## Summary",
    "- fullGtReviewRows=$($reviewRows.Count)",
    "- fullGtReviewedRows=$reviewedRows",
    "- fullGtPendingRows=$($pendingReviewRows.Count)",
    "- fullFrameRows=$($frameRows.Count)",
    "- fullFrameReviewedRows=$frameReviewedRows",
    "- fullFramePendingRows=$($pendingFrameRows.Count)",
    "- guiRows=$($guiRows.Count)",
    "- guiStatusRows=$guiStatusRows",
    "- guiPendingRows=$($pendingGuiRows.Count)",
    "- reviewIndex=$reviewIndexPath",
    "",
    "## Full-GT Crop Rows",
    "",
    "| frame | sourcePredictionId | confidence | cropPath | missing |",
    "| --- | --- | --- | --- | --- |"
)

foreach ($row in ($pendingReviewRows | Select-Object -First $max)) {
    $missing = @()
    foreach ($column in @("label", "reviewStatus", "evidenceNotes")) {
        if ([string]::IsNullOrWhiteSpace((Get-Value $row $column))) {
            $missing += $column
        }
    }

    $lines += "| $(Escape-Cell (Get-Value $row "frame")) | $(Escape-Cell (Get-Value $row "sourcePredictionId")) | $(Escape-Cell (Get-Value $row "sourceConfidence")) | $(Escape-Cell (Get-Value $row "cropPath")) | $(Escape-Cell ($missing -join ",")) |"
}

if ($pendingReviewRows.Count -gt $max) {
    $lines += ""
    $lines += "_Showing first $max of $($pendingReviewRows.Count) pending crop rows._"
}

$lines += @(
    "",
    "## Full-Frame Review Rows",
    "",
    "| frame | detectedCandidateCount | frameImagePath | overlayFrameImagePath | missing |",
    "| --- | --- | --- | --- | --- |"
)

foreach ($row in ($pendingFrameRows | Select-Object -First $max)) {
    $missing = @()
    foreach ($column in @("missedFaceCount", "missedFaceRowsAdded", "reviewStatus", "evidenceNotes")) {
        if ([string]::IsNullOrWhiteSpace((Get-Value $row $column))) {
            $missing += $column
        }
    }

    $lines += "| $(Escape-Cell (Get-Value $row "frame")) | $(Escape-Cell (Get-Value $row "detectedCandidateCount")) | $(Escape-Cell (Get-Value $row "frameImagePath")) | $(Escape-Cell (Get-Value $row "overlayFrameImagePath")) | $(Escape-Cell ($missing -join ",")) |"
}

if ($pendingFrameRows.Count -gt $max) {
    $lines += ""
    $lines += "_Showing first $max of $($pendingFrameRows.Count) pending full-frame rows._"
}

$lines += @(
    "",
    "## GUI Smoke Steps",
    "",
    "| stepId | evidenceType | artifactPath | missing |",
    "| --- | --- | --- | --- |"
)

foreach ($row in ($pendingGuiRows | Select-Object -First $max)) {
    $missing = @()
    foreach ($column in @("status", "evidenceType", "artifactPath", "evidence")) {
        if ([string]::IsNullOrWhiteSpace((Get-Value $row $column))) {
            $missing += $column
        }
    }

    $lines += "| $(Escape-Cell (Get-Value $row "stepId")) | $(Escape-Cell (Get-Value $row "evidenceType")) | $(Escape-Cell (Get-Value $row "artifactPath")) | $(Escape-Cell ($missing -join ",")) |"
}

if ($pendingGuiRows.Count -gt $max) {
    $lines += ""
    $lines += "_Showing first $max of $($pendingGuiRows.Count) pending GUI rows._"
}

$candidateReviewStatus = if ($hasCandidateReview) { "available" } else { "missing" }
$candidateFrameStatus = if ($hasCandidateFrameReview) { "available" } else { "missing" }
$lines += @(
    "",
    "## AI-Assisted Candidate Reference",
    "",
    "These files are review accelerators only. They are not final GT and must not be used to mark the YOLO goal complete until the human review CSV and GUI checklist are filled.",
    "",
    "- aiCandidateFullGtReviewCsv=$candidateReviewPath",
    "- aiCandidateFullGtReviewStatus=$candidateReviewStatus",
    "- aiCandidateFullFrameReviewCsv=$candidateFramePath",
    "- aiCandidateFullFrameReviewStatus=$candidateFrameStatus",
    "- aiCandidateRule=reference-only-not-final-gt"
)

$lines += @(
    "",
    "## Completion Commands",
    "",
    '```powershell',
    'powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\verify-yolo-full-gt-reviewed-state.ps1 -ReviewCsv ".tmp\yolo-full-gt\review-package-smoke\full-gt-review.csv" -FullFrameReviewCsv ".tmp\yolo-full-gt\review-package-smoke\full-frame-review.csv" -PredictionLog ".tmp\yolo-ten-minute-detection-smoke\yolo-ten-minute-yolo-only-20260523-022022.log" -RequireFullFrameReview -RequireEvidence -RequireArtifacts -MinIou 0.5 -MaxMisses 0 -MaxFalsePositives 0 -MaxLowIou 0 -AllowQualityGateFailure',
    'powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\verify-yolo-gui-smoke-state.ps1 -ChecklistCsv ".tmp\yolo-gui-smoke\manual-smoke-checklist.csv" -RequireManualPass',
    'powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\complete-yolo-goal-after-manual-gates.ps1 -AllowQualityGateFailure -UpdatePlan -RunYoloState',
    '```'
)

$lines | Set-Content -Encoding UTF8 -Path $resolvedOutput
$report = Get-Content -Raw -Path $resolvedOutput

foreach ($required in @(
    "YOLO Manual Pending Report",
    "full-gt-review.csv label",
    "missedFaceRowsAdded",
    "GUI checklist status",
    "fullGtPendingRows=$($pendingReviewRows.Count)",
    "fullFramePendingRows=$($pendingFrameRows.Count)",
    "guiPendingRows=$($pendingGuiRows.Count)",
    "preview-track-hold",
    "AI-Assisted Candidate Reference",
    "reference-only-not-final-gt",
    "AllowQualityGateFailure",
    "complete-yolo-goal-after-manual-gates.ps1")) {
    Assert-ReportContains "report token $required" $report $required
}

if ($Verify) {
    if ($pendingReviewRows.Count -eq 0 -and $pendingFrameRows.Count -eq 0 -and $pendingGuiRows.Count -eq 0) {
        Assert-ReportContains "report has no pending manual rows" $report "fullGtPendingRows=0"
    }
    else {
        Assert-ReportContains "report keeps pending full GT rows" $report "fullGtPendingRows="
        Assert-ReportContains "report keeps pending full-frame rows" $report "fullFramePendingRows="
        Assert-ReportContains "report keeps pending GUI rows" $report "guiPendingRows="
    }
}

Write-Host "[YoloManualPendingReport] wrote output=$resolvedOutput"
Write-Host "[YoloManualPendingReport] fullGtPendingRows=$($pendingReviewRows.Count), fullFramePendingRows=$($pendingFrameRows.Count), guiPendingRows=$($pendingGuiRows.Count)"
Write-Host "[YoloManualPendingReport] all requested checks passed"
