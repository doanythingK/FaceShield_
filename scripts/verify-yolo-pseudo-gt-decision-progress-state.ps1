param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$script = Join-Path $repo "scripts\write-yolo-pseudo-gt-decision-progress.ps1"

function Assert-Contains {
    param(
        [string]$Name,
        [string]$Text,
        [string]$Expected
    )

    if (-not $Text.Contains($Expected)) {
        throw "$Name missing text: $Expected"
    }

    Write-Host "[YoloPseudoGtDecisionProgressVerify] pass $Name"
}

function Assert-FileNonEmpty {
    param(
        [string]$Name,
        [string]$Path
    )

    if (-not (Test-Path $Path)) {
        throw "$Name not found: $Path"
    }
    if ((Get-Item $Path).Length -le 0) {
        throw "$Name is empty: $Path"
    }

    Write-Host "[YoloPseudoGtDecisionProgressVerify] pass $Name"
}

if (-not (Test-Path $script)) {
    throw "Decision progress script not found: $script"
}

$scriptText = Get-Content -Raw -Path $script
Assert-Contains "script is test-only" $scriptText "test-only human review progress report"
Assert-Contains "script does not infer labels" $scriptText "does not infer final face/nonface/miss labels"
Assert-Contains "script checks candidate decision fields" $scriptText "label"
Assert-Contains "script checks candidate evidence notes" $scriptText "evidenceNotes"
Assert-Contains "script checks full-frame decision fields" $scriptText "missedFaceRowsAdded"
Assert-Contains "script has require complete guard" $scriptText "RequireComplete"
Assert-Contains "script prints apply command" $scriptText "apply-yolo-pseudo-gt-review-draft.ps1"
Assert-Contains "script prints close command" $scriptText "close-yolo-pseudo-gt-review.ps1"

$work = Join-Path $repo ".tmp\yolo-pseudo-gt-decision-progress-state"
if (Test-Path $work) {
    Remove-Item -Recurse -Force -Path $work
}
New-Item -ItemType Directory -Force -Path $work | Out-Null

$decisionCsv = Join-Path $work "pseudo-gt-review-decision-sheet.csv"
$frameDecisionCsv = Join-Path $work "pseudo-gt-full-frame-review-decision-sheet.csv"

@(
    [pscustomobject]@{
        reviewRank = "1"
        frame = "10"
        pseudoGt_candidateId = "base-10"
        pseudoGt_candidateType = "supportedFaceCandidate"
        suggestedLabel = "face"
        label = "face"
        reviewStatus = "pass"
        evidenceNotes = "Human confirmed visible face in crop and overlay."
        visualReviewStatus = "visual-ready-test-only"
        cropPath = "crop-10.png"
        visualOverlayPath = "overlay-10.png"
        baseFaceConfidence = "0.9"
        tileFaceConfidence = "0.8"
        tileSupportCount = "2"
        faceVerificationConfidence = "0.7"
        faceVerificationDistance = "0.1"
        personConfidence = "0"
        personUpperOverlap = "0"
        fpProbability = "0"
        missProbability = "0"
        pseudoGtReason = "supported"
    },
    [pscustomobject]@{
        reviewRank = "2"
        frame = "11"
        pseudoGt_candidateId = "base-11"
        pseudoGt_candidateType = "falsePositiveCandidate"
        suggestedLabel = "nonface"
        label = ""
        reviewStatus = ""
        evidenceNotes = ""
        visualReviewStatus = "visual-ready-test-only"
        cropPath = "crop-11.png"
        visualOverlayPath = "overlay-11.png"
        baseFaceConfidence = "0.2"
        tileFaceConfidence = "0"
        tileSupportCount = "0"
        faceVerificationConfidence = "0"
        faceVerificationDistance = "1"
        personConfidence = "0"
        personUpperOverlap = "0"
        fpProbability = "0.8"
        missProbability = "0"
        pseudoGtReason = "unsupported"
    },
    [pscustomobject]@{
        reviewRank = "3"
        frame = "12"
        pseudoGt_candidateId = "miss-12"
        pseudoGt_candidateType = "missCandidate"
        suggestedLabel = "miss"
        label = "nonface"
        reviewStatus = "pass"
        evidenceNotes = "Human intentionally entered a label that should fail closure compatibility."
        visualReviewStatus = "visual-ready-test-only"
        cropPath = "crop-12.png"
        visualOverlayPath = "overlay-12.png"
        baseFaceConfidence = "0"
        tileFaceConfidence = "0.7"
        tileSupportCount = "2"
        faceVerificationConfidence = "0"
        faceVerificationDistance = "1"
        personConfidence = "0"
        personUpperOverlap = "0"
        fpProbability = "0"
        missProbability = "0.7"
        pseudoGtReason = "miss"
    }
) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $decisionCsv

@(
    [pscustomobject]@{
        frame = "12"
        missedFaceCount = ""
        missedFaceRowsAdded = ""
        reviewStatus = ""
        evidenceNotes = ""
        pseudoGtMissCandidateCount = "1"
        pseudoGtMissCandidateIds = "miss-12"
    }
) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $frameDecisionCsv

$summary = Join-Path $work "progress.md"
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script `
    -DecisionCsv $decisionCsv `
    -FrameDecisionCsv $frameDecisionCsv `
    -OutputSummary $summary
if ($LASTEXITCODE -ne 0) {
    throw "Decision progress pending selftest failed with exit code $LASTEXITCODE"
}
Assert-FileNonEmpty "pending progress summary" $summary
$summaryText = Get-Content -Raw -Path $summary
Assert-Contains "summary records candidate rows" $summaryText "candidateRows: 3"
Assert-Contains "summary records ready candidate rows" $summaryText "readyCandidateRows: 2"
Assert-Contains "summary records pending candidate rows" $summaryText "pendingCandidateRows: 1"
Assert-Contains "summary records invalid label rows" $summaryText "invalidCandidateLabelRows: 1"
Assert-Contains "summary records pending frame rows" $summaryText "pendingFrameDecisionRows: 1"
Assert-Contains "summary records next pending candidate" $summaryText "candidate=base-11"
Assert-Contains "summary records invalid miss label" $summaryText "candidate=miss-12"
Assert-Contains "summary records next apply command" $summaryText "apply-yolo-pseudo-gt-review-draft.ps1"
Assert-Contains "summary records next close command" $summaryText "close-yolo-pseudo-gt-review.ps1"

$oldErrorAction = $ErrorActionPreference
try {
    $ErrorActionPreference = "Continue"
    $requireOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script `
        -DecisionCsv $decisionCsv `
        -FrameDecisionCsv $frameDecisionCsv `
        -OutputSummary (Join-Path $work "require-fail.md") `
        -RequireComplete 2>&1
    $requireExit = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $oldErrorAction
}
if ($requireExit -eq 0) {
    throw "Decision progress RequireComplete unexpectedly passed with pending/invalid rows."
}
Assert-Contains "require complete explains pending rows" ($requireOutput | Out-String) "pendingCandidateRows=1"

@(
    [pscustomobject]@{
        reviewRank = "1"
        frame = "10"
        pseudoGt_candidateId = "base-10"
        pseudoGt_candidateType = "supportedFaceCandidate"
        suggestedLabel = "face"
        label = "face"
        reviewStatus = "pass"
        evidenceNotes = "Human confirmed visible face in crop and overlay."
    },
    [pscustomobject]@{
        reviewRank = "2"
        frame = "11"
        pseudoGt_candidateId = "base-11"
        pseudoGt_candidateType = "falsePositiveCandidate"
        suggestedLabel = "nonface"
        label = "nonface"
        reviewStatus = "pass"
        evidenceNotes = "Human confirmed this crop is not a face."
    },
    [pscustomobject]@{
        reviewRank = "3"
        frame = "12"
        pseudoGt_candidateId = "miss-12"
        pseudoGt_candidateType = "missCandidate"
        suggestedLabel = "miss"
        label = "miss"
        reviewStatus = "pass"
        evidenceNotes = "Human confirmed a missed visible face."
    }
) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $decisionCsv

@(
    [pscustomobject]@{
        frame = "12"
        missedFaceCount = "1"
        missedFaceRowsAdded = "1"
        reviewStatus = "pass"
        evidenceNotes = "Human completed the full-frame missed-face scan."
        pseudoGtMissCandidateCount = "1"
        pseudoGtMissCandidateIds = "miss-12"
    }
) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $frameDecisionCsv

$completeSummary = Join-Path $work "complete-progress.md"
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script `
    -DecisionCsv $decisionCsv `
    -FrameDecisionCsv $frameDecisionCsv `
    -OutputSummary $completeSummary `
    -RequireComplete
if ($LASTEXITCODE -ne 0) {
    throw "Decision progress complete selftest failed with exit code $LASTEXITCODE"
}
Assert-FileNonEmpty "complete progress summary" $completeSummary
$completeText = Get-Content -Raw -Path $completeSummary
Assert-Contains "complete summary records no pending candidates" $completeText "pendingCandidateRows: 0"
Assert-Contains "complete summary records no invalid labels" $completeText "invalidCandidateLabelRows: 0"
Assert-Contains "complete summary records no pending frames" $completeText "pendingFrameDecisionRows: 0"

Write-Host "[YoloPseudoGtDecisionProgressVerify] all requested checks passed"
