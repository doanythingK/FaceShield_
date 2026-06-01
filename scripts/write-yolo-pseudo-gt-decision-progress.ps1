param(
    [string]$DecisionCsv = ".tmp\yolo-pseudo-gt\review-visual\pseudo-gt-review-decision-sheet.csv",
    [string]$FrameDecisionCsv = ".tmp\yolo-pseudo-gt\review-visual\pseudo-gt-full-frame-review-decision-sheet.csv",
    [string]$OutputSummary = ".tmp\yolo-pseudo-gt\review-visual\pseudo-gt-decision-progress.md",
    [string]$ReviewIndex = ".tmp\yolo-pseudo-gt\review-visual\pseudo-gt-review-visual-index.html",
    [string]$DraftReviewCsv = ".tmp\yolo-pseudo-gt\review-draft\pseudo-gt-full-gt-review-draft.csv",
    [string]$DraftFullFrameReviewCsv = ".tmp\yolo-pseudo-gt\review-draft\pseudo-gt-full-frame-review-draft.csv",
    [string]$ReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-gt-review.csv",
    [string]$FullFrameReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-frame-review.csv",
    [string]$PseudoGtCsv = ".tmp\yolo-pseudo-gt\pseudo-gt-candidates.csv",
    [switch]$RequireComplete
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

function Resolve-RepoPath {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return ""
    }

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

    if ($null -eq $Row -or $null -eq $Row.PSObject.Properties[$Column]) {
        return ""
    }

    return [string]$Row.$Column
}

function Test-ReviewedStatus {
    param([string]$Status)

    if ([string]::IsNullOrWhiteSpace($Status)) {
        return $false
    }

    return $Status.Trim().ToLowerInvariant() -in @("pass", "reviewed", "complete", "completed", "closed", "done")
}

function Normalize-Label {
    param([string]$Label)

    if ([string]::IsNullOrWhiteSpace($Label)) {
        return ""
    }

    $normalized = $Label.Trim().ToLowerInvariant()
    if ($normalized -in @("face", "actualface", "true", "1")) {
        return "face"
    }

    if ($normalized -in @("nonface", "notface", "false", "0", "object", "background")) {
        return "nonface"
    }

    if ($normalized -eq "miss") {
        return "miss"
    }

    return $normalized
}

function Get-ExpectedLabels {
    param([string]$CandidateType)

    switch ($CandidateType) {
        "supportedFaceCandidate" { return @("face") }
        "falsePositiveCandidate" { return @("nonface") }
        "missCandidate" { return @("face", "miss") }
        default { return @() }
    }
}

function Test-DecisionReady {
    param([object]$Row)

    return -not [string]::IsNullOrWhiteSpace((Get-CsvValue $Row "label")) -and
        (Test-ReviewedStatus (Get-CsvValue $Row "reviewStatus")) -and
        -not [string]::IsNullOrWhiteSpace((Get-CsvValue $Row "evidenceNotes"))
}

function Test-FrameDecisionReady {
    param([object]$Row)

    return -not [string]::IsNullOrWhiteSpace((Get-CsvValue $Row "missedFaceCount")) -and
        -not [string]::IsNullOrWhiteSpace((Get-CsvValue $Row "missedFaceRowsAdded")) -and
        (Test-ReviewedStatus (Get-CsvValue $Row "reviewStatus")) -and
        -not [string]::IsNullOrWhiteSpace((Get-CsvValue $Row "evidenceNotes"))
}

function Get-RowId {
    param([object]$Row)

    $candidateId = Get-CsvValue $Row "pseudoGt_candidateId"
    if ([string]::IsNullOrWhiteSpace($candidateId)) {
        $candidateId = Get-CsvValue $Row "candidateId"
    }

    return "frame=$(Get-CsvValue $Row "frame"),candidate=$candidateId"
}

$decisionPath = Resolve-RepoPath $DecisionCsv
$frameDecisionPath = Resolve-RepoPath $FrameDecisionCsv
$summaryPath = Resolve-RepoPath $OutputSummary

if (-not (Test-Path $decisionPath)) {
    throw "Pseudo-GT decision CSV not found: $decisionPath"
}
if (-not (Test-Path $frameDecisionPath)) {
    throw "Pseudo-GT full-frame decision CSV not found: $frameDecisionPath"
}

$decisionRows = @(Import-Csv $decisionPath)
$frameDecisionRows = @(Import-Csv $frameDecisionPath)
if ($decisionRows.Count -eq 0) {
    throw "Pseudo-GT decision CSV has no rows: $decisionPath"
}

$readyRows = @($decisionRows | Where-Object { Test-DecisionReady $_ })
$pendingRows = @($decisionRows | Where-Object { -not (Test-DecisionReady $_) })
$invalidLabelRows = @()
foreach ($row in $readyRows) {
    $expected = @(Get-ExpectedLabels (Get-CsvValue $row "pseudoGt_candidateType"))
    if ($expected.Count -eq 0) {
        continue
    }

    $label = Normalize-Label (Get-CsvValue $row "label")
    if ($label -notin $expected) {
        $invalidLabelRows += $row
    }
}

$frameReadyRows = @($frameDecisionRows | Where-Object { Test-FrameDecisionReady $_ })
$framePendingRows = @($frameDecisionRows | Where-Object { -not (Test-FrameDecisionReady $_) })

$candidateTypeCounts = [ordered]@{}
foreach ($row in $decisionRows) {
    $type = Get-CsvValue $row "pseudoGt_candidateType"
    if ([string]::IsNullOrWhiteSpace($type)) {
        $type = "unknown"
    }
    if (-not $candidateTypeCounts.Contains($type)) {
        $candidateTypeCounts[$type] = 0
    }
    $candidateTypeCounts[$type]++
}

$nextPending = @($pendingRows | Select-Object -First 10 | ForEach-Object { "- $(Get-RowId $_), type=$(Get-CsvValue $_ "pseudoGt_candidateType"), suggested=$(Get-CsvValue $_ "suggestedLabel")" })
$invalidLabels = @($invalidLabelRows | Select-Object -First 10 | ForEach-Object { "- $(Get-RowId $_), type=$(Get-CsvValue $_ "pseudoGt_candidateType"), label=$(Get-CsvValue $_ "label"), expected=$(@(Get-ExpectedLabels (Get-CsvValue $_ "pseudoGt_candidateType")) -join "|")" })
$pendingFrames = @($framePendingRows | Select-Object -First 10 | ForEach-Object { "- frame=$(Get-CsvValue $_ "frame"), pseudoGtMissCandidateCount=$(Get-CsvValue $_ "pseudoGtMissCandidateCount"), pseudoGtMissCandidateIds=$(Get-CsvValue $_ "pseudoGtMissCandidateIds")" })

$summaryParent = Split-Path -Parent $summaryPath
if (-not [string]::IsNullOrWhiteSpace($summaryParent)) {
    New-Item -ItemType Directory -Force -Path $summaryParent | Out-Null
}

$applyCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./scripts/apply-yolo-pseudo-gt-review-draft.ps1 -DraftReviewCsv `"$DraftReviewCsv`" -DraftFullFrameReviewCsv `"$DraftFullFrameReviewCsv`" -DecisionCsv `"$DecisionCsv`" -FrameDecisionCsv `"$FrameDecisionCsv`" -ReviewCsv `"$ReviewCsv`" -FullFrameReviewCsv `"$FullFrameReviewCsv`" -InPlace -Verify"
$closeCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./scripts/close-yolo-pseudo-gt-review.ps1 -PseudoGtCsv `"$PseudoGtCsv`" -ReviewCsv `"$ReviewCsv`" -FullFrameReviewCsv `"$FullFrameReviewCsv`" -OutputCsv `".tmp/yolo-pseudo-gt/pseudo-gt-review-closure.csv`" -SummaryPath `".tmp/yolo-pseudo-gt/pseudo-gt-review-closure-summary.md`" -RequireAllClosed"

$lines = @(
    "# YOLO Pseudo-GT Decision Progress",
    "",
    "This is a test-only human review progress report. It does not infer final face/nonface/miss labels.",
    "",
    "## Inputs",
    "- decisionCsv: $decisionPath",
    "- frameDecisionCsv: $frameDecisionPath",
    "- reviewIndex: $(Resolve-RepoPath $ReviewIndex)",
    "",
    "## Candidate Decision Counts",
    "- candidateRows: $($decisionRows.Count)",
    "- readyCandidateRows: $($readyRows.Count)",
    "- pendingCandidateRows: $($pendingRows.Count)",
    "- invalidCandidateLabelRows: $($invalidLabelRows.Count)",
    "",
    "## Full-Frame Decision Counts",
    "- frameDecisionRows: $($frameDecisionRows.Count)",
    "- readyFrameDecisionRows: $($frameReadyRows.Count)",
    "- pendingFrameDecisionRows: $($framePendingRows.Count)",
    "",
    "## Candidate Types"
)
foreach ($entry in $candidateTypeCounts.GetEnumerator()) {
    $lines += "- $($entry.Key): $($entry.Value)"
}

$lines += @(
    "",
    "## Next Pending Candidate Rows"
)
if ($nextPending.Count -eq 0) {
    $lines += "- none"
}
else {
    $lines += $nextPending
}

$lines += @(
    "",
    "## Invalid Candidate Labels",
    "These rows are filled but do not match the closure rule for the pseudo-GT candidate type."
)
if ($invalidLabels.Count -eq 0) {
    $lines += "- none"
}
else {
    $lines += $invalidLabels
}

$lines += @(
    "",
    "## Pending Full-Frame Rows"
)
if ($pendingFrames.Count -eq 0) {
    $lines += "- none"
}
else {
    $lines += $pendingFrames
}

$lines += @(
    "",
    "## Next Commands",
    "Run these only after human review fills the decision sheets.",
    "",
    '```powershell',
    $applyCommand,
    '```',
    "",
    '```powershell',
    $closeCommand,
    '```'
)

$lines | Set-Content -Encoding UTF8 -Path $summaryPath

$isComplete = $pendingRows.Count -eq 0 -and $invalidLabelRows.Count -eq 0 -and $framePendingRows.Count -eq 0
if ($RequireComplete -and -not $isComplete) {
    throw "Pseudo-GT decision review is incomplete: pendingCandidateRows=$($pendingRows.Count), invalidCandidateLabelRows=$($invalidLabelRows.Count), pendingFrameDecisionRows=$($framePendingRows.Count)"
}

Write-Host "[YoloPseudoGtDecisionProgress] summary=$summaryPath"
Write-Host "[YoloPseudoGtDecisionProgress] candidateRows=$($decisionRows.Count),ready=$($readyRows.Count),pending=$($pendingRows.Count),invalidLabels=$($invalidLabelRows.Count)"
Write-Host "[YoloPseudoGtDecisionProgress] frameRows=$($frameDecisionRows.Count),ready=$($frameReadyRows.Count),pending=$($framePendingRows.Count)"
