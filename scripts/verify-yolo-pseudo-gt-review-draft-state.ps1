param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$scriptPath = Join-Path $repo "scripts\new-yolo-pseudo-gt-review-draft.ps1"

function Assert-Contains {
    param(
        [string]$Name,
        [string]$Text,
        [string]$Expected
    )

    if (-not $Text.Contains($Expected)) {
        throw "$Name missing text: $Expected"
    }

    Write-Host "[YoloPseudoGtReviewDraftVerify] pass $Name"
}

if (-not (Test-Path $scriptPath)) {
    throw "Pseudo-GT review draft script not found: $scriptPath"
}

$scriptText = Get-Content -Raw -Path $scriptPath
Assert-Contains "script records test-only boundary" $scriptText "test-only review preparation"
Assert-Contains "script keeps final labels blank" $scriptText 'label = ""'
Assert-Contains "script keeps final review status blank" $scriptText 'reviewStatus = ""'
Assert-Contains "script keeps final evidence notes blank" $scriptText 'evidenceNotes = ""'
Assert-Contains "script preserves source id for base candidates" $scriptText 'basePredictionId'
Assert-Contains "script keeps miss source id blank" $scriptText 'missCandidate'
Assert-Contains "script writes full gt draft csv" $scriptText "pseudo-gt-full-gt-review-draft.csv"
Assert-Contains "script writes full frame draft csv" $scriptText "pseudo-gt-full-frame-review-draft.csv"
Assert-Contains "script documents source id plus iou closure" $scriptText "source id plus IoU geometry"
Assert-Contains "script documents miss full-frame evidence" $scriptText "missed-face evidence"

$selfTestDir = Join-Path $repo ".tmp\yolo-pseudo-gt-review-draft-state"
New-Item -ItemType Directory -Force -Path $selfTestDir | Out-Null

$queueCsv = Join-Path $selfTestDir "pseudo-gt-review-queue.csv"
@(
    [pscustomobject]@{
        reviewRank = "1"
        frame = "10"
        candidateId = "base-10-0"
        candidateType = "supportedFaceCandidate"
        expectedReviewLabel = "face"
        source = "base-yolo"
        basePredictionId = "10-0"
        tileDetectionId = "tile-10-0"
        verificationId = "verify-10-0"
        x = "100"
        y = "120"
        w = "80"
        h = "90"
        reviewPriorityScore = "0.95"
        baseFaceConfidence = "0.70"
        tileFaceConfidence = "0.91"
        tileSupportCount = "3"
        faceVerificationConfidence = "0.88"
        faceVerificationDistance = "0.05"
        personConfidence = "0"
        personUpperOverlap = "0"
        fpProbability = "0.02"
        missProbability = "0"
        reviewPriorityReason = "supported face"
        pseudoGtReason = "base supported by high precision evidence"
        reviewStatus = "pending-human"
        evidenceNotes = "candidate only"
    },
    [pscustomobject]@{
        reviewRank = "2"
        frame = "11"
        candidateId = "base-11-1"
        candidateType = "falsePositiveCandidate"
        expectedReviewLabel = "nonface"
        source = "base-yolo"
        basePredictionId = "11-1"
        tileDetectionId = ""
        verificationId = ""
        x = "200"
        y = "20"
        w = "100"
        h = "120"
        reviewPriorityScore = "0.80"
        baseFaceConfidence = "0.52"
        tileFaceConfidence = "0"
        tileSupportCount = "0"
        faceVerificationConfidence = "0"
        faceVerificationDistance = "1"
        personConfidence = "0"
        personUpperOverlap = "0"
        fpProbability = "0.80"
        missProbability = "0"
        reviewPriorityReason = "unsupported base candidate"
        pseudoGtReason = "base lacks high precision support"
        reviewStatus = "pending-human"
        evidenceNotes = "candidate only"
    },
    [pscustomobject]@{
        reviewRank = "3"
        frame = "12"
        candidateId = "miss-12-face-0"
        candidateType = "missCandidate"
        expectedReviewLabel = "miss"
        source = "tile-face"
        basePredictionId = ""
        tileDetectionId = "tile-12-0"
        verificationId = ""
        x = "300"
        y = "140"
        w = "50"
        h = "60"
        reviewPriorityScore = "0.70"
        baseFaceConfidence = "0"
        tileFaceConfidence = "0.81"
        tileSupportCount = "2"
        faceVerificationConfidence = "0"
        faceVerificationDistance = "1"
        personConfidence = "0"
        personUpperOverlap = "0"
        fpProbability = "0"
        missProbability = "0.70"
        reviewPriorityReason = "tile miss"
        pseudoGtReason = "tile face was not matched by base YOLO"
        reviewStatus = "pending-human"
        evidenceNotes = "candidate only"
    }
) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $queueCsv

$frameCsv = Join-Path $selfTestDir "full-frame-review.csv"
@(
    [pscustomobject]@{
        frame = "12"
        frameImagePath = "frame-12.png"
        overlayFrameImagePath = "overlay-12.png"
        detectedCandidateCount = "2"
        candidateSummary = "synthetic"
        missedFaceCount = ""
        missedFaceRowsAdded = ""
        reviewStatus = ""
        evidenceNotes = ""
    }
) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $frameCsv

$outputDir = Join-Path $selfTestDir "draft"
if (Test-Path $outputDir) {
    Remove-Item -Recurse -Force -Path $outputDir
}

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $scriptPath `
    -PseudoGtReviewQueueCsv $queueCsv `
    -FullFrameReviewCsv $frameCsv `
    -OutputDir $outputDir `
    -Force `
    -Verify
if ($LASTEXITCODE -ne 0) {
    throw "Pseudo-GT review draft selftest failed with exit code $LASTEXITCODE"
}

$draftReviewCsv = Join-Path $outputDir "pseudo-gt-full-gt-review-draft.csv"
$draftFrameCsv = Join-Path $outputDir "pseudo-gt-full-frame-review-draft.csv"
$reportPath = Join-Path $outputDir "pseudo-gt-review-draft-report.md"
foreach ($path in @($draftReviewCsv, $draftFrameCsv, $reportPath)) {
    if (-not (Test-Path $path)) {
        throw "Expected draft output not found: $path"
    }
}

$draftRows = @(Import-Csv $draftReviewCsv)
$draftFrameRows = @(Import-Csv $draftFrameCsv)
if ($draftRows.Count -ne 3) {
    throw "Expected three draft review rows, actual=$($draftRows.Count)"
}
if ($draftFrameRows.Count -ne 1) {
    throw "Expected one miss frame draft row, actual=$($draftFrameRows.Count)"
}

$baseRows = @($draftRows | Where-Object { $_.pseudoGt_candidateType -ne "missCandidate" })
$missRows = @($draftRows | Where-Object { $_.pseudoGt_candidateType -eq "missCandidate" })
if (@($baseRows | Where-Object { [string]::IsNullOrWhiteSpace($_.sourcePredictionId) }).Count -ne 0) {
    throw "Base pseudo-GT draft rows did not preserve sourcePredictionId."
}
if (@($missRows | Where-Object { -not [string]::IsNullOrWhiteSpace($_.sourcePredictionId) }).Count -ne 0) {
    throw "Miss pseudo-GT draft rows unexpectedly filled sourcePredictionId."
}
if (@($draftRows | Where-Object {
            -not [string]::IsNullOrWhiteSpace($_.label) -or
            -not [string]::IsNullOrWhiteSpace($_.reviewStatus) -or
            -not [string]::IsNullOrWhiteSpace($_.evidenceNotes)
        }).Count -ne 0) {
    throw "Pseudo-GT review draft filled final review fields."
}

$reportText = Get-Content -Raw -Path $reportPath
Assert-Contains "report records review draft" $reportText "YOLO Pseudo-GT Review Draft"
Assert-Contains "report records test only rule" $reportText "test-only-reference-not-final-gt"
Assert-Contains "report records source id geometry rule" $reportText "source id plus IoU geometry"
Assert-Contains "report records miss frame draft" $reportText "Miss Frame Draft Rows"

Write-Host "[YoloPseudoGtReviewDraftVerify] all requested checks passed"
