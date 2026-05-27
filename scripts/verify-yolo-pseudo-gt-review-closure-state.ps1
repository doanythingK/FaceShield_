param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$script = Join-Path $repo "scripts\close-yolo-pseudo-gt-review.ps1"
$guide = Join-Path $repo "YOLO_PROBLEM_SPAN_VERIFICATION.md"
$work = Join-Path $repo ".tmp\yolo-pseudo-gt-review-closure-verify"
$pseudoCsv = Join-Path $work "pseudo-gt-candidates.csv"
$reviewCsv = Join-Path $work "full-gt-review.csv"
$fullFrameCsv = Join-Path $work "full-frame-review.csv"
$outputCsv = Join-Path $work "pseudo-gt-review-closure.csv"
$summaryPath = Join-Path $work "pseudo-gt-review-closure-summary.md"

function Assert-File {
    param([string]$Name, [string]$Path)

    if (-not (Test-Path $Path)) {
        throw "$Name not found: $Path"
    }
}

function Assert-Contains {
    param(
        [string]$Name,
        [string]$Text,
        [string]$Pattern
    )

    if ($Text -notmatch $Pattern) {
        throw "$Name missing pattern: $Pattern"
    }

    Write-Host "[YoloPseudoGtReviewClosureVerify] pass $Name"
}

Assert-File "review closure script" $script

New-Item -ItemType Directory -Force -Path $work | Out-Null

@(
    [pscustomobject]@{
        candidateId = "base-2-0"
        frame = 2
        candidateType = "supportedFaceCandidate"
        source = "base-yolo"
        basePredictionId = "0"
        tileDetectionId = "tile-face-2"
        verificationId = "verify-face-2"
        x = 10
        y = 20
        w = 50
        h = 60
        baseFaceConfidence = 0.41
        tileFaceConfidence = 0.92
        tileSupportCount = 3
        faceVerificationConfidence = 0.89
        faceVerificationDistance = 0.21
        personConfidence = 0
        personUpperOverlap = 0
        bestIou = 0.9
        centerDistanceRatio = 0.1
        fpProbability = 0.1
        missProbability = 0
        pseudoGtReason = "supported"
        reviewLabel = ""
        reviewStatus = "pending-human"
        evidenceNotes = ""
    },
    [pscustomobject]@{
        candidateId = "base-6-0"
        frame = 6
        candidateType = "falsePositiveCandidate"
        source = "base-yolo"
        basePredictionId = "0"
        tileDetectionId = ""
        verificationId = ""
        x = 500
        y = 440
        w = 180
        h = 180
        baseFaceConfidence = 0.22
        tileFaceConfidence = 0
        tileSupportCount = 0
        faceVerificationConfidence = 0
        faceVerificationDistance = 1
        personConfidence = 0.72
        personUpperOverlap = 0.62
        bestIou = 0
        centerDistanceRatio = 99
        fpProbability = 0.85
        missProbability = 0.1
        pseudoGtReason = "unsupported"
        reviewLabel = ""
        reviewStatus = "pending-human"
        evidenceNotes = ""
    },
    [pscustomObject]@{
        candidateId = "miss-7-tile-face-7"
        frame = 7
        candidateType = "missCandidate"
        source = "tile-face"
        basePredictionId = ""
        tileDetectionId = "tile-face-7"
        verificationId = ""
        x = 60
        y = 80
        w = 24
        h = 28
        baseFaceConfidence = 0
        tileFaceConfidence = 0.87
        tileSupportCount = 2
        faceVerificationConfidence = 0
        faceVerificationDistance = 1
        personConfidence = 0
        personUpperOverlap = 0
        bestIou = 0
        centerDistanceRatio = 99
        fpProbability = 0
        missProbability = 0.82
        pseudoGtReason = "miss"
        reviewLabel = ""
        reviewStatus = "pending-human"
        evidenceNotes = ""
    }
) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $pseudoCsv

@(
    [pscustomobject]@{ frame = 2; gtId = ""; label = "face"; x = 10; y = 20; w = 50; h = 60; sourcePredictionId = "0"; sourceConfidence = 0.41; source = "smoke-detection-log"; cropPath = ""; cropX = 0; cropY = 0; cropW = 80; cropH = 90; reviewStatus = "pass"; evidenceNotes = "visible face"; notes = "" },
    [pscustomobject]@{ frame = 6; gtId = ""; label = "nonface"; x = 500; y = 440; w = 180; h = 180; sourcePredictionId = "0"; sourceConfidence = 0.22; source = "smoke-detection-log"; cropPath = ""; cropX = 0; cropY = 0; cropW = 220; cropH = 220; reviewStatus = "pass"; evidenceNotes = "background artifact"; notes = "" },
    [pscustomobject]@{ frame = 7; gtId = "manual-miss-7"; label = "face"; x = 61; y = 81; w = 24; h = 28; sourcePredictionId = ""; sourceConfidence = ""; source = "manual-miss"; cropPath = ""; cropX = 0; cropY = 0; cropW = 80; cropH = 90; reviewStatus = "pass"; evidenceNotes = "visible missed face"; notes = "" }
) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $reviewCsv

@(
    [pscustomobject]@{ frame = 7; frameImagePath = ""; overlayFrameImagePath = ""; detectedCandidateCount = 0; candidateSummary = ""; missedFaceCount = 1; missedFaceRowsAdded = 1; reviewStatus = "pass"; evidenceNotes = "manual miss added"; notes = "" }
) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $fullFrameCsv

$output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script `
    -PseudoGtCsv $pseudoCsv `
    -ReviewCsv $reviewCsv `
    -FullFrameReviewCsv $fullFrameCsv `
    -OutputCsv $outputCsv `
    -SummaryPath $summaryPath `
    -RequireAllClosed 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "close-yolo-pseudo-gt-review.ps1 failed: $($output | Out-String)"
}

Assert-File "closure output CSV" $outputCsv
Assert-File "closure summary" $summaryPath

$rows = @(Import-Csv $outputCsv)
if ($rows.Count -ne 3) {
    throw "Expected 3 closure rows, actual=$($rows.Count)"
}

if (@($rows | Where-Object { $_.closureStatus -ne "closed" }).Count -ne 0) {
    throw "Expected all closure rows to be closed."
}

foreach ($column in @("candidateId", "candidateType", "expectedReviewLabel", "closureStatus", "reviewLabel", "reviewStatus", "reviewIou", "closureReason")) {
    if ($null -eq $rows[0].PSObject.Properties[$column]) {
        throw "Missing closure column: $column"
    }
}

$scriptText = Get-Content -Raw -Path $script
$summaryText = Get-Content -Raw -Path $summaryPath
$guideText = Get-Content -Raw -Path $guide

Assert-Contains "script matches source prediction ids" $scriptText "sourcePredictionId"
Assert-Contains "script supports manual miss iou matching" $scriptText "PreferManualMiss"
Assert-Contains "script enforces require all closed" $scriptText "RequireAllClosed"
Assert-Contains "summary records closed count" $summaryText "closed=3"
Assert-Contains "summary records no unreviewed rows" $summaryText "unreviewed=0"
Assert-Contains "summary records miss closure" $summaryText "missClosed=1"
Assert-Contains "guide documents review closure" $guideText "close-yolo-pseudo-gt-review\.ps1"

Write-Host "[YoloPseudoGtReviewClosureVerify] all requested checks passed"
