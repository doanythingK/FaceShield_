param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$script = Join-Path $repo "scripts\new-yolo-pseudo-gt-evidence.ps1"
$guide = Join-Path $repo "YOLO_PROBLEM_SPAN_VERIFICATION.md"
$work = Join-Path $repo ".tmp\yolo-pseudo-gt-verify"
$baseLog = Join-Path $work "base-yolo.log"
$tileCsv = Join-Path $work "tile-face.csv"
$verificationCsv = Join-Path $work "face-verification.csv"
$personCsv = Join-Path $work "person-object.csv"
$badVerificationCsv = Join-Path $work "bad-face-verification.csv"
$emptyBaseLog = Join-Path $work "empty-base-yolo.log"
$outputCsv = Join-Path $work "pseudo-gt-candidates.csv"
$summaryPath = Join-Path $work "pseudo-gt-summary.md"
$reviewQueueCsv = Join-Path $work "pseudo-gt-review-queue.csv"
$emptyBaseOutputCsv = Join-Path $work "empty-base-pseudo-gt-candidates.csv"
$emptyBaseSummaryPath = Join-Path $work "empty-base-pseudo-gt-summary.md"
$emptyBaseReviewQueueCsv = Join-Path $work "empty-base-pseudo-gt-review-queue.csv"
$badOutputCsv = Join-Path $work "bad-pseudo-gt-candidates.csv"
$badSummaryPath = Join-Path $work "bad-pseudo-gt-summary.md"

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

    Write-Host "[YoloPseudoGtEvidenceVerify] pass $Name"
}

function Assert-Column {
    param(
        [object]$Row,
        [string]$Name
    )

    if ($null -eq $Row.PSObject.Properties[$Name]) {
        throw "Missing output column: $Name"
    }

    Write-Host "[YoloPseudoGtEvidenceVerify] pass output column $Name"
}

Assert-File "pseudo-GT evidence script" $script
Assert-File "problem-span guide" $guide

New-Item -ItemType Directory -Force -Path $work | Out-Null

@'
[SmokeDetection] label=synthetic-yolo, frame=1, index=0, x=100.0, y=100.0, w=50.0, h=60.0, area=3000.0, conf=0.820, cx=0.125, cy=0.150, areaRatio=0.003000, aspectRatio=0.833
[SmokeDetection] label=synthetic-yolo, frame=2, index=0, x=400.0, y=300.0, w=40.0, h=45.0, area=1800.0, conf=0.210, cx=0.420, cy=0.340, areaRatio=0.001800, aspectRatio=0.889
[SmokeDetection] label=synthetic-yolo, frame=5, index=0, x=500.0, y=500.0, w=200.0, h=200.0, area=40000.0, conf=0.550, cx=0.600, cy=0.600, areaRatio=0.040000, aspectRatio=1.000
[SmokeDetection] label=synthetic-yolo, frame=8, index=0, x=730.0, y=220.0, w=60.0, h=60.0, area=3600.0, conf=0.330, cx=0.760, cy=0.260, areaRatio=0.003600, aspectRatio=1.000
[SmokeDetection] label=synthetic-yolo, frame=10, index=0, x=300.0, y=200.0, w=60.0, h=60.0, area=3600.0, conf=0.300, cx=0.330, cy=0.240, areaRatio=0.003600, aspectRatio=1.000
[SmokeDetection] label=synthetic-yolo, frame=11, index=0, x=700.0, y=0.0, w=190.0, h=190.0, area=36100.0, conf=0.420, cx=0.795, cy=0.095, areaRatio=0.055000, aspectRatio=1.000
[SmokeDetection] label=synthetic-yolo, frame=13, index=0, x=40.0, y=160.0, w=44.0, h=46.0, area=2024.0, conf=0.370, cx=0.070, cy=0.300, areaRatio=0.002024, aspectRatio=0.957
'@ | Set-Content -Encoding UTF8 -Path $baseLog

@'
[AutoRunSummary] runId=empty-base, detector=YoloFaceOnnxDetector/CPU, mode=pipe-parallel, totalFrames=12, startFrame=0, processed=12, decoded=12, detects=12, interpolated=0, readMs=0, decodeMs=10, detectMs=20, maskMs=0, totalMs=30, downscale=1.000, quality=BalancedBilinear, tracking=True, everyN=1, parallel=2, roi=regular=0, small=0, rejected=0, statsRejected=0
'@ | Set-Content -Encoding UTF8 -Path $emptyBaseLog

@(
    [pscustomobject]@{ frame = 1; detectionId = "tile-face-1"; x = 101.0; y = 102.0; w = 51.0; h = 58.0; confidence = 0.910; tileSupportCount = 3; evidenceModel = "heavy-tile-face-v1"; evidenceRunner = "tile-runner-local" },
    [pscustomobject]@{ frame = 2; detectionId = "tile-face-2"; x = 103.0; y = 103.0; w = 50.0; h = 58.0; confidence = 0.900; tileSupportCount = 2; evidenceModel = "heavy-tile-face-v1"; evidenceRunner = "tile-runner-local" },
    [pscustomobject]@{ frame = 3; detectionId = "tile-face-3"; x = 45.0; y = 60.0; w = 24.0; h = 28.0; confidence = 0.870; tileSupportCount = 2; evidenceModel = "heavy-tile-face-v1"; evidenceRunner = "tile-runner-local" },
    [pscustomobject]@{ frame = 7; detectionId = "tile-low-quality-7"; x = 720.0; y = 240.0; w = 44.0; h = 44.0; confidence = 0.120; tileSupportCount = 1; evidenceModel = "heavy-tile-face-v1"; evidenceRunner = "tile-runner-local" },
    [pscustomobject]@{ frame = 8; detectionId = "tile-single-support-8"; x = 740.0; y = 240.0; w = 44.0; h = 44.0; confidence = 0.930; tileSupportCount = 1; evidenceModel = "heavy-tile-face-v1"; evidenceRunner = "tile-runner-local" }
) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $tileCsv

@(
    [pscustomobject]@{ frame = 1; candidateId = "base-1-1-0"; basePredictionId = "1-0"; verificationId = "verify-face-1"; x = 102.0; y = 101.0; w = 49.0; h = 59.0; faceVerificationConfidence = 0.880; faceVerificationDistance = 0.220; evidenceModel = "arcface-local-v1"; evidenceRunner = "face-verify-runner" },
    [pscustomobject]@{ frame = 3; verificationId = "verify-tile-face-3-primary"; x = 46.0; y = 61.0; w = 24.0; h = 28.0; faceVerificationConfidence = 0.910; faceVerificationDistance = 0.170; evidenceModel = "arcface-local-v1"; evidenceRunner = "face-verify-runner" },
    [pscustomobject]@{ frame = 3; verificationId = "verify-tile-face-3-secondary"; x = 47.0; y = 62.0; w = 24.0; h = 28.0; faceVerificationConfidence = 0.900; faceVerificationDistance = 0.180; evidenceModel = "arcface-local-v1"; evidenceRunner = "face-verify-runner" },
    [pscustomobject]@{ frame = 4; verificationId = "verify-face-4"; x = 210.0; y = 120.0; w = 32.0; h = 36.0; faceVerificationConfidence = 0.910; faceVerificationDistance = 0.180; evidenceModel = "arcface-local-v1"; evidenceRunner = "face-verify-runner" },
    [pscustomobject]@{ frame = 5; verificationId = "verify-small-face-5"; x = 575.0; y = 575.0; w = 50.0; h = 50.0; faceVerificationConfidence = 0.930; faceVerificationDistance = 0.150; evidenceModel = "arcface-local-v1"; evidenceRunner = "face-verify-runner" },
    [pscustomobject]@{ frame = 6; verificationId = "verify-low-quality-6"; x = 700.0; y = 220.0; w = 60.0; h = 60.0; faceVerificationConfidence = 0.120; faceVerificationDistance = 0.990; evidenceModel = "arcface-local-v1"; evidenceRunner = "face-verify-runner" },
    [pscustomobject]@{ frame = 8; verificationId = "verify-high-conf-bad-distance-8"; x = 730.0; y = 220.0; w = 60.0; h = 60.0; faceVerificationConfidence = 0.990; faceVerificationDistance = 0.990; evidenceModel = "arcface-local-v1"; evidenceRunner = "face-verify-runner" },
    [pscustomobject]@{ frame = 9; verificationId = "verify-low-conf-good-distance-9"; x = 760.0; y = 220.0; w = 60.0; h = 60.0; faceVerificationConfidence = 0.120; faceVerificationDistance = 0.120; evidenceModel = "arcface-local-v1"; evidenceRunner = "face-verify-runner" },
    [pscustomobject]@{ frame = 10; verificationId = "verify-low-base-primary-10"; x = 301.0; y = 201.0; w = 60.0; h = 60.0; faceVerificationConfidence = 0.910; faceVerificationDistance = 0.180; evidenceModel = "arcface-local-v1"; evidenceRunner = "face-verify-runner" },
    [pscustomobject]@{ frame = 10; verificationId = "verify-low-base-secondary-10"; x = 302.0; y = 202.0; w = 60.0; h = 60.0; faceVerificationConfidence = 0.890; faceVerificationDistance = 0.190; evidenceModel = "arcface-local-v1"; evidenceRunner = "face-verify-runner" },
    [pscustomobject]@{ frame = 12; verificationId = "verify-low-base-primary-10"; x = 303.0; y = 203.0; w = 60.0; h = 60.0; faceVerificationConfidence = 0.900; faceVerificationDistance = 0.180; evidenceModel = "arcface-local-v1"; evidenceRunner = "face-verify-runner" },
    [pscustomobject]@{ frame = 13; candidateId = "base-13-99-0"; basePredictionId = "13-99"; verificationId = "verify-wrong-bound-base-13"; x = 41.0; y = 161.0; w = 44.0; h = 46.0; faceVerificationConfidence = 0.930; faceVerificationDistance = 0.120; evidenceModel = "arcface-local-v1"; evidenceRunner = "face-verify-runner" }
) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $verificationCsv

@(
    [pscustomobject]@{ frame = 2; detectionId = "car-2"; x = 398.0; y = 298.0; w = 48.0; h = 58.0; confidence = 0.990; class = "car"; evidenceModel = "heavy-person-object-v1"; evidenceRunner = "person-object-runner" },
    [pscustomobject]@{ frame = 2; detectionId = "person-2"; x = 390.0; y = 280.0; w = 90.0; h = 190.0; confidence = 0.760; class = "person"; evidenceModel = "heavy-person-object-v1"; evidenceRunner = "person-object-runner" },
    [pscustomobject]@{ frame = 3; detectionId = "person-3"; x = 40.0; y = 50.0; w = 80.0; h = 180.0; confidence = 0.770; class = "person"; evidenceModel = "heavy-person-object-v1"; evidenceRunner = "person-object-runner" },
    [pscustomobject]@{ frame = 4; detectionId = "person-low-confidence-4"; x = 205.0; y = 110.0; w = 80.0; h = 180.0; confidence = 0.120; class = "person"; evidenceModel = "heavy-person-object-v1"; evidenceRunner = "person-object-runner" }
) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $personCsv

$output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script `
    -BasePredictionLog $baseLog `
    -TileFaceCsv $tileCsv `
    -FaceVerificationCsv $verificationCsv `
    -PersonObjectCsv $personCsv `
    -OutputCsv $outputCsv `
    -SummaryPath $summaryPath `
    -ReviewQueueCsv $reviewQueueCsv 2>&1

if ($LASTEXITCODE -ne 0) {
    throw "new-yolo-pseudo-gt-evidence.ps1 failed: $($output | Out-String)"
}

Assert-File "pseudo-GT output CSV" $outputCsv
Assert-File "pseudo-GT summary" $summaryPath
Assert-File "pseudo-GT review queue CSV" $reviewQueueCsv

$scriptText = Get-Content -Raw -Path $script
$guideText = Get-Content -Raw -Path $guide
$summaryText = Get-Content -Raw -Path $summaryPath
$rows = @(Import-Csv $outputCsv)
$reviewQueueRows = @(Import-Csv $reviewQueueCsv)

if ($rows.Count -ne 13) {
    throw "Expected 13 pseudo-GT rows, actual=$($rows.Count)"
}

if ($reviewQueueRows.Count -ne 13) {
    throw "Expected 13 pseudo-GT review queue rows, actual=$($reviewQueueRows.Count)"
}

$first = $rows[0]
foreach ($column in @(
        "candidateId",
        "candidateType",
        "source",
        "tileDetectionId",
        "verificationId",
        "baseFaceConfidence",
        "tileFaceConfidence",
        "tileSupportCount",
        "faceVerificationConfidence",
        "faceVerificationDistance",
        "personConfidence",
        "personUpperOverlap",
        "personObjectClass",
        "tileEvidenceModel",
        "tileEvidenceRunner",
        "faceVerificationEvidenceModel",
        "faceVerificationEvidenceRunner",
        "faceVerificationSourceCandidateId",
        "faceVerificationSourceBasePredictionId",
        "personObjectEvidenceModel",
        "personObjectEvidenceRunner",
        "auxiliarySignalRole",
        "supportFrameCount",
        "supportRowCount",
        "supportSources",
        "supportEvidenceIds",
        "bestIou",
        "centerDistanceRatio",
        "areaChangeRatio",
        "centerXRatio",
        "centerYRatio",
        "baseAreaRatio",
        "aspectRatio",
        "geometryTag",
        "fpProbability",
        "missProbability",
        "pseudoGtReason",
        "reviewLabel",
        "reviewStatus",
        "evidenceNotes")) {
    Assert-Column $first $column
}

$queueFirst = $reviewQueueRows[0]
foreach ($column in @(
        "reviewRank",
        "candidateId",
        "candidateType",
        "expectedReviewLabel",
        "source",
        "basePredictionId",
        "tileDetectionId",
        "verificationId",
        "x",
        "y",
        "w",
        "h",
        "reviewPriorityScore",
        "auxiliaryPriorityBoost",
        "geometryPriorityBoost",
        "dominantProbability",
        "reviewPriorityReason",
        "fpProbability",
        "missProbability",
        "personConfidence",
        "personUpperOverlap",
        "personObjectClass",
        "tileEvidenceModel",
        "tileEvidenceRunner",
        "faceVerificationEvidenceModel",
        "faceVerificationEvidenceRunner",
        "faceVerificationSourceCandidateId",
        "faceVerificationSourceBasePredictionId",
        "personObjectEvidenceModel",
        "personObjectEvidenceRunner",
        "auxiliarySignalRole",
        "supportRowCount",
        "supportEvidenceIds",
        "areaChangeRatio",
        "centerXRatio",
        "centerYRatio",
        "baseAreaRatio",
        "aspectRatio",
        "geometryTag",
        "reviewStatus",
        "evidenceNotes")) {
    Assert-Column $queueFirst $column
}

if ($queueFirst.candidateType -eq "supportedFaceCandidate") {
    throw "Expected review queue to prioritize miss/false-positive candidates before supported face candidates."
}

if ([string]::IsNullOrWhiteSpace($queueFirst.x) -or [string]::IsNullOrWhiteSpace($queueFirst.y) -or [string]::IsNullOrWhiteSpace($queueFirst.w) -or [string]::IsNullOrWhiteSpace($queueFirst.h)) {
    throw "Expected review queue to preserve candidate geometry columns."
}

if ($queueFirst.candidateType -eq "missCandidate" -and [string]::IsNullOrWhiteSpace($queueFirst.tileDetectionId) -and [string]::IsNullOrWhiteSpace($queueFirst.verificationId)) {
    throw "Expected miss review queue row to preserve tile or verification source id."
}

$falsePositiveQueueRow = @($reviewQueueRows | Where-Object { $_.candidateType -eq "falsePositiveCandidate" })[0]
if ($null -eq $falsePositiveQueueRow) {
    throw "Expected falsePositiveCandidate in review queue."
}

$falsePositiveCandidateRow = @($rows | Where-Object { $_.candidateType -eq "falsePositiveCandidate" })[0]
if ([double]::Parse($falsePositiveCandidateRow.fpProbability, [System.Globalization.CultureInfo]::InvariantCulture) -lt 0.78) {
    throw "Expected person/object support to avoid lowering falsePositiveCandidate probability."
}

if ($falsePositiveCandidateRow.personObjectClass -ne "person") {
    throw "Expected non-person object class rows to be ignored for person support."
}

if ($falsePositiveCandidateRow.personObjectEvidenceModel -ne "heavy-person-object-v1" -or $falsePositiveCandidateRow.personObjectEvidenceRunner -ne "person-object-runner") {
    throw "Expected falsePositiveCandidate to preserve person/object model and runner provenance."
}

if ([double]::Parse($falsePositiveCandidateRow.personConfidence, [System.Globalization.CultureInfo]::InvariantCulture) -ge 0.99) {
    throw "Expected non-person object confidence to stay out of personConfidence."
}

if ([double]::Parse($falsePositiveQueueRow.auxiliaryPriorityBoost, [System.Globalization.CultureInfo]::InvariantCulture) -le 0) {
    throw "Expected person/object support to raise falsePositiveCandidate review priority."
}

if ($falsePositiveCandidateRow.auxiliarySignalRole -ne "priority-only-not-face-evidence") {
    throw "Expected candidate CSV to record person/object support as priority-only, not face evidence."
}

if ($falsePositiveQueueRow.auxiliarySignalRole -ne "priority-only-not-face-evidence") {
    throw "Expected review queue to preserve priority-only person/object role."
}

if ($falsePositiveQueueRow.reviewPriorityReason -notmatch "auxiliary person/object support raises review priority") {
    throw "Expected review priority reason to explain auxiliary person/object boost."
}

if ($falsePositiveQueueRow.expectedReviewLabel -ne "nonface") {
    throw "Expected falsePositiveCandidate queue row to tell the reviewer to close it as nonface after visual confirmation."
}

if ([string]::IsNullOrWhiteSpace($falsePositiveQueueRow.evidenceNotes)) {
    throw "Expected review queue to preserve candidate evidenceNotes for human review."
}

$topEdgeLargeCandidate = @($rows | Where-Object { $_.candidateType -eq "falsePositiveCandidate" -and $_.basePredictionId -eq "11-0" })[0]
if ($null -eq $topEdgeLargeCandidate) {
    throw "Expected top-edge large YOLO geometry to remain a falsePositiveCandidate for review."
}

if ($topEdgeLargeCandidate.geometryTag -ne "top-edge-large-review") {
    throw "Expected pseudo-GT candidate CSV to tag top-edge large base YOLO geometry for review."
}

if ($topEdgeLargeCandidate.centerYRatio -ne "0.095" -or $topEdgeLargeCandidate.baseAreaRatio -ne "0.055" -or $topEdgeLargeCandidate.aspectRatio -ne "1") {
    throw "Expected pseudo-GT candidate CSV to preserve normalized top-edge geometry evidence."
}

$topEdgeLargeQueueRow = @($reviewQueueRows | Where-Object { $_.basePredictionId -eq "11-0" })[0]
if ($null -eq $topEdgeLargeQueueRow) {
    throw "Expected top-edge large YOLO geometry to appear in the review queue."
}

if ($topEdgeLargeQueueRow.geometryTag -ne "top-edge-large-review") {
    throw "Expected review queue to preserve top-edge large geometry tag."
}

if ([double]::Parse($topEdgeLargeQueueRow.geometryPriorityBoost, [System.Globalization.CultureInfo]::InvariantCulture) -le 0) {
    throw "Expected top-edge large geometry to raise review priority without deciding face/nonface."
}

if ($topEdgeLargeQueueRow.reviewPriorityReason -notmatch "geometry tag 'top-edge-large-review' raises review priority but does not decide face/nonface") {
    throw "Expected review priority reason to explain geometry review boost."
}

$missQueueRow = @($reviewQueueRows | Where-Object { $_.candidateType -eq "missCandidate" })[0]
if ($null -eq $missQueueRow -or $missQueueRow.expectedReviewLabel -ne "miss") {
    throw "Expected missCandidate queue row to tell the reviewer to close it as miss after visual confirmation."
}

$lowConfidencePersonMiss = @($rows | Where-Object { $_.candidateType -eq "missCandidate" -and $_.verificationId -eq "verify-face-4" })[0]
if ($null -eq $lowConfidencePersonMiss) {
    throw "Expected verification-only missCandidate for low-confidence person/object auxiliary test."
}

if ([double]::Parse($lowConfidencePersonMiss.personConfidence, [System.Globalization.CultureInfo]::InvariantCulture) -ne 0) {
    throw "Expected low-confidence person/object support to stay out of personConfidence."
}

if ([double]::Parse($lowConfidencePersonMiss.personUpperOverlap, [System.Globalization.CultureInfo]::InvariantCulture) -ne 0) {
    throw "Expected low-confidence person/object support to stay out of personUpperOverlap."
}

$lowConfidencePersonQueue = @($reviewQueueRows | Where-Object { $_.verificationId -eq "verify-face-4" })[0]
if ($null -eq $lowConfidencePersonQueue) {
    throw "Expected review queue row for low-confidence person/object auxiliary test."
}

if ([double]::Parse($lowConfidencePersonQueue.auxiliaryPriorityBoost, [System.Globalization.CultureInfo]::InvariantCulture) -ne 0) {
    throw "Expected low-confidence person/object support not to raise review priority."
}

if (-not [string]::IsNullOrWhiteSpace($lowConfidencePersonMiss.auxiliarySignalRole)) {
    throw "Expected low-confidence person/object support not to set an auxiliary signal role."
}

$personSupportedMiss = @($rows | Where-Object { $_.candidateType -eq "missCandidate" -and $_.tileDetectionId -eq "tile-face-3" })[0]
if ($null -eq $personSupportedMiss) {
    throw "Expected tile-only missCandidate for person/object auxiliary priority test."
}

if ([double]::Parse($personSupportedMiss.personUpperOverlap, [System.Globalization.CultureInfo]::InvariantCulture) -le 0) {
    throw "Expected high-confidence person/object support to remain recorded as auxiliary evidence."
}

if ($personSupportedMiss.tileEvidenceModel -ne "heavy-tile-face-v1" -or $personSupportedMiss.tileEvidenceRunner -ne "tile-runner-local") {
    throw "Expected missCandidate to preserve tile model and runner provenance."
}

$personSupportedMissProbability = [double]::Parse($personSupportedMiss.missProbability, [System.Globalization.CultureInfo]::InvariantCulture)
if ([Math]::Abs($personSupportedMissProbability - 0.841) -gt 0.0001) {
    throw "Expected person/object support not to inflate missProbability; actual=$personSupportedMissProbability."
}

$personSupportedQueue = @($reviewQueueRows | Where-Object { $_.tileDetectionId -eq "tile-face-3" })[0]
if ($null -eq $personSupportedQueue) {
    throw "Expected review queue row for person/object auxiliary priority test."
}

if ([double]::Parse($personSupportedQueue.auxiliaryPriorityBoost, [System.Globalization.CultureInfo]::InvariantCulture) -le 0) {
    throw "Expected high-confidence person/object support to raise review priority as auxiliary evidence."
}

if ($personSupportedMiss.auxiliarySignalRole -ne "priority-only-not-face-evidence") {
    throw "Expected high-confidence person/object support to record a priority-only role on miss candidates."
}

if ($personSupportedQueue.auxiliarySignalRole -ne "priority-only-not-face-evidence") {
    throw "Expected review queue to preserve priority-only role on miss candidates."
}

if (@($rows | Where-Object { $_.candidateType -eq "supportedFaceCandidate" }).Count -ne 2) {
    throw "Expected two supportedFaceCandidate rows."
}

if (@($rows | Where-Object { $_.candidateType -eq "falsePositiveCandidate" }).Count -ne 5) {
    throw "Expected five falsePositiveCandidate rows."
}

if (@($rows | Where-Object { $_.candidateType -eq "missCandidate" }).Count -ne 6) {
    throw "Expected six missCandidate rows."
}

if (@($rows | Where-Object { $_.candidateType -eq "missCandidate" -and $_.source -eq "face-verification" -and $_.verificationId -eq "verify-face-4" }).Count -ne 1) {
    throw "Expected one verification-only missCandidate."
}

if (@($rows | Where-Object { $_.candidateType -eq "missCandidate" -and $_.frame -eq "3" -and $_.tileDetectionId -eq "tile-face-3" }).Count -ne 1) {
    throw "Expected multiple verification rows near one unmatched tile face to stay collapsed to one tile missCandidate."
}

if (@($rows | Where-Object { $_.candidateType -eq "missCandidate" -and $_.verificationId -eq "verify-tile-face-3-secondary" }).Count -ne 0) {
    throw "Expected secondary verification near an already-emitted tile missCandidate not to create a duplicate missCandidate."
}

if (@($rows | Where-Object { $_.candidateType -eq "missCandidate" -and $_.verificationId -eq "verify-low-base-secondary-10" }).Count -ne 0) {
    throw "Expected secondary verification evidence near an already-detected low-confidence base YOLO row not to become a missCandidate."
}

if (@($rows | Where-Object { $_.candidateType -eq "supportedFaceCandidate" -and $_.basePredictionId -eq "10-0" }).Count -ne 1) {
    throw "Expected low-confidence base YOLO row with high-quality verification support to remain a supportedFaceCandidate."
}

if (@($rows | Where-Object { $_.candidateType -eq "missCandidate" -and $_.frame -eq "12" -and $_.verificationId -eq "verify-low-base-primary-10" }).Count -ne 1) {
    throw "Expected repeated verification IDs on different frames to stay frame-scoped and preserve the later missCandidate."
}

if (@($rows | Where-Object { $_.verificationId -eq "verify-low-quality-6" }).Count -ne 0) {
    throw "Expected low-confidence/high-distance verification-only row to be ignored instead of becoming a missCandidate."
}

if (@($rows | Where-Object { $_.verificationId -eq "verify-high-conf-bad-distance-8" }).Count -ne 0) {
    throw "Expected high-confidence but high-distance verification-only row to be ignored for high-precision pseudo-GT."
}

if (@($rows | Where-Object { $_.verificationId -eq "verify-low-conf-good-distance-9" }).Count -ne 0) {
    throw "Expected low-confidence but low-distance verification-only row to be ignored for high-precision pseudo-GT."
}

if (@($rows | Where-Object { $_.candidateType -eq "supportedFaceCandidate" -and $_.verificationId -eq "verify-wrong-bound-base-13" }).Count -ne 0) {
    throw "Expected source-bound face verification row not to support a different same-frame base candidate."
}

$wrongBoundBase = @($rows | Where-Object { $_.candidateType -eq "falsePositiveCandidate" -and $_.basePredictionId -eq "13-0" })[0]
if ($null -eq $wrongBoundBase) {
    throw "Expected wrong-bound same-frame base candidate to remain a falsePositiveCandidate."
}

$wrongBoundMiss = @($rows | Where-Object { $_.candidateType -eq "missCandidate" -and $_.verificationId -eq "verify-wrong-bound-base-13" })[0]
if ($null -eq $wrongBoundMiss) {
    throw "Expected wrong-bound face verification row to remain a separate missCandidate for review."
}

if ($wrongBoundMiss.faceVerificationSourceCandidateId -ne "base-13-99-0" -or $wrongBoundMiss.faceVerificationSourceBasePredictionId -ne "13-99") {
    throw "Expected missCandidate to preserve source-bound face verification ids."
}

if (@($rows | Where-Object { $_.tileDetectionId -eq "tile-low-quality-7" }).Count -ne 0) {
    throw "Expected low-confidence tile row to be ignored instead of becoming pseudo-GT support or miss evidence."
}

if (@($rows | Where-Object { $_.tileDetectionId -eq "tile-single-support-8" }).Count -ne 0) {
    throw "Expected high-confidence but single-support tile row to be ignored for overlap-based pseudo-GT."
}

$weakOnlyComparison = @($rows | Where-Object { $_.candidateType -eq "falsePositiveCandidate" -and $_.basePredictionId -eq "8-0" })[0]
if ($null -eq $weakOnlyComparison) {
    throw "Expected weak-only same-frame high-precision evidence to keep the base row as a falsePositiveCandidate."
}

if ($weakOnlyComparison.pseudoGtReason -notmatch "lacks tile/verification face support") {
    throw "Expected weak-only same-frame pseudo-GT rows to stay out of comparison evidence."
}

if ([double]::Parse($weakOnlyComparison.bestIou, [System.Globalization.CultureInfo]::InvariantCulture) -ne 0) {
    throw "Expected weak-only comparison evidence to leave bestIou at 0."
}

$largeGeometryMismatch = @($rows | Where-Object { $_.candidateType -eq "falsePositiveCandidate" -and $_.basePredictionId -eq "5-0" })[0]
if ($null -eq $largeGeometryMismatch) {
    throw "Expected center-aligned large YOLO box to remain a falsePositiveCandidate when area ratio is too different."
}

if ([double]::Parse($largeGeometryMismatch.areaChangeRatio, [System.Globalization.CultureInfo]::InvariantCulture) -lt 15.0) {
    throw "Expected unsupported geometry mismatch to preserve nearest high-precision areaChangeRatio evidence."
}

if ($largeGeometryMismatch.pseudoGtReason -notmatch "same-frame high-precision face evidence but fails IoU/center/area support thresholds") {
    throw "Expected unsupported geometry mismatch reason to explain failed support thresholds."
}

if (@($rows | Where-Object { $_.candidateType -eq "missCandidate" -and $_.verificationId -eq "verify-small-face-5" }).Count -ne 1) {
    throw "Expected center-aligned small verification face to remain a missCandidate when base YOLO geometry is too large."
}

$supportedRow = @($rows | Where-Object { $_.candidateType -eq "supportedFaceCandidate" -and $_.source -eq "base-yolo" })[0]
if ([int]$supportedRow.supportFrameCount -lt 2) {
    throw "Expected supported row to record repeated support frames."
}

if ([int]$supportedRow.supportRowCount -lt 3) {
    throw "Expected supported row to record repeated tile/verification support rows."
}

$supportedCenterDistance = [double]::Parse($supportedRow.centerDistanceRatio, [System.Globalization.CultureInfo]::InvariantCulture)
if ($supportedCenterDistance -gt 0.028) {
    throw "Expected supported row to preserve the best tile/verification centerDistanceRatio."
}

if ($supportedRow.supportSources -notmatch "tile" -or $supportedRow.supportSources -notmatch "verification") {
    throw "Expected supported row to record tile and verification support sources."
}

if ($supportedRow.supportEvidenceIds -notmatch "tile-face:1:tile-face-1" -or
    $supportedRow.supportEvidenceIds -notmatch "tile-face:2:tile-face-2" -or
    $supportedRow.supportEvidenceIds -notmatch "face-verification:1:verify-face-1") {
    throw "Expected supported row to preserve concrete tile/verification support evidence ids."
}

if ($supportedRow.tileEvidenceModel -ne "heavy-tile-face-v1" -or $supportedRow.faceVerificationEvidenceModel -ne "arcface-local-v1") {
    throw "Expected supported row to preserve tile and face-verification model provenance."
}

if ($supportedRow.tileEvidenceRunner -ne "tile-runner-local" -or $supportedRow.faceVerificationEvidenceRunner -ne "face-verify-runner") {
    throw "Expected supported row to preserve tile and face-verification runner provenance."
}

if ($supportedRow.faceVerificationSourceCandidateId -ne "base-1-1-0" -or $supportedRow.faceVerificationSourceBasePredictionId -ne "1-0") {
    throw "Expected supported row to preserve source-bound face verification ids."
}

$supportedQueueRow = @($reviewQueueRows | Where-Object { $_.candidateType -eq "supportedFaceCandidate" -and $_.basePredictionId -eq "1-0" })[0]
if ($null -eq $supportedQueueRow -or $supportedQueueRow.supportEvidenceIds -ne $supportedRow.supportEvidenceIds) {
    throw "Expected review queue to preserve supportEvidenceIds for supported candidates."
}

if ($supportedQueueRow.tileEvidenceModel -ne $supportedRow.tileEvidenceModel -or $supportedQueueRow.faceVerificationEvidenceRunner -ne $supportedRow.faceVerificationEvidenceRunner) {
    throw "Expected review queue to preserve pseudo-GT model and runner provenance."
}

if (@($rows | Where-Object { $_.reviewStatus -ne "pending-human" }).Count -ne 0) {
    throw "Pseudo-GT rows must remain pending-human."
}

$emptyBaseOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script `
    -BasePredictionLog $emptyBaseLog `
    -TileFaceCsv $tileCsv `
    -OutputCsv $emptyBaseOutputCsv `
    -SummaryPath $emptyBaseSummaryPath `
    -ReviewQueueCsv $emptyBaseReviewQueueCsv 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "new-yolo-pseudo-gt-evidence.ps1 empty-base run failed: $($emptyBaseOutput | Out-String)"
}

Assert-File "empty-base pseudo-GT output CSV" $emptyBaseOutputCsv
Assert-File "empty-base pseudo-GT summary" $emptyBaseSummaryPath
Assert-File "empty-base pseudo-GT review queue CSV" $emptyBaseReviewQueueCsv

$emptyBaseRows = @(Import-Csv $emptyBaseOutputCsv)
$emptyBaseSummaryText = Get-Content -Raw -Path $emptyBaseSummaryPath
if ($emptyBaseRows.Count -ne 3) {
    throw "Expected 3 empty-base pseudo-GT rows, actual=$($emptyBaseRows.Count)"
}
if (@($emptyBaseRows | Where-Object { $_.candidateType -ne "missCandidate" }).Count -ne 0) {
    throw "Expected no-base pseudo-GT rows to all be missCandidate."
}
if ($emptyBaseSummaryText -notmatch "baseRows=0" -or $emptyBaseSummaryText -notmatch "missCandidate=3") {
    throw "Expected empty-base pseudo-GT summary to record baseRows=0 and missCandidate=3."
}
Write-Host "[YoloPseudoGtEvidenceVerify] pass no-base tile rows become miss candidates"

@(
    [pscustomobject]@{ frame = 1; verificationId = "bad-verify-face-1"; x = 102.0; y = 101.0; w = 49.0; h = 59.0; faceVerificationConfidence = 0.920 }
) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $badVerificationCsv

$previousErrorActionPreference = $ErrorActionPreference
$ErrorActionPreference = "Continue"
try {
    $badOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script `
        -BasePredictionLog $baseLog `
        -FaceVerificationCsv $badVerificationCsv `
        -OutputCsv $badOutputCsv `
        -SummaryPath $badSummaryPath 2>&1
    $badExitCode = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $previousErrorActionPreference
}

if ($badExitCode -eq 0) {
    throw "Expected malformed face verification CSV to fail."
}

if (($badOutput | Out-String) -notmatch "face-verification CSV row 0 missing required value: faceVerificationDistance") {
    throw "Expected malformed face verification CSV to report missing faceVerificationDistance. Output: $($badOutput | Out-String)"
}
Write-Host "[YoloPseudoGtEvidenceVerify] pass malformed face verification CSV rejected"

Assert-Contains "script accepts base prediction log" $scriptText "BasePredictionLog"
Assert-Contains "script accepts tile face CSV" $scriptText "TileFaceCsv"
Assert-Contains "script accepts face verification CSV" $scriptText "FaceVerificationCsv"
Assert-Contains "script accepts person object CSV" $scriptText "PersonObjectCsv"
Assert-Contains "script filters person object class labels" $scriptText "Test-PersonClassLabel"
Assert-Contains "script filters low-confidence person object support" $scriptText 'candidate\.Confidence -lt \$MinPersonObjectConfidence'
Assert-Contains "script validates required input columns" $scriptText "strict-required-columns"
Assert-Contains "script requires face verification distance" $scriptText "faceVerificationDistance"
Assert-Contains "script requires face verification confidence and distance support" $scriptText 'Candidate\.Confidence -ge \$MinVerificationConfidence -and[\s\S]*Candidate\.VerificationDistance -le \$MaxVerificationDistance'
Assert-Contains "script calculates IoU" $scriptText "function Get-Iou"
Assert-Contains "script calculates center distance" $scriptText "Get-CenterDistanceRatio"
Assert-Contains "script checks support area ratio" $scriptText "MaxSupportAreaChangeRatio"
Assert-Contains "script records support area ratio" $scriptText "areaChangeRatio"
Assert-Contains "script guards source-bound face verification matches" $scriptText "Test-SourceBindingAllowsMatch"
Assert-Contains "script writes source-bound face verification ids" $scriptText "faceVerificationSourceCandidateId"
Assert-Contains "script records unsupported nearest high-quality comparison" $scriptText "Find-BestComparison"
Assert-Contains "script filters weak comparison evidence" $scriptText 'Find-BestComparison[\s\S]*Test-FaceEvidenceMetricSupport \$candidate'
Assert-Contains "script records best geometry support" $scriptText "Get-MinMatchProperty"
Assert-Contains "script records temporal support" $scriptText "supportFrameCount"
Assert-Contains "script records repeated support rows" $scriptText "supportRowCount"
Assert-Contains "script records concrete support evidence ids" $scriptText "supportEvidenceIds"
Assert-Contains "script scopes matched pseudo gt ids by frame" $scriptText "Get-DetectionMatchKey"
Assert-Contains "script writes review queue csv" $scriptText "ReviewQueueCsv"
Assert-Contains "script supports no-base miss evidence" $scriptText 'baseRows=\$\(\$baseRows.Count\)'
Assert-Contains "script records review priority score" $scriptText "reviewPriorityScore"
Assert-Contains "script records auxiliary priority boost" $scriptText "auxiliaryPriorityBoost"
Assert-Contains "script records auxiliary signal role" $scriptText "auxiliarySignalRole"
Assert-Contains "script reads optional evidence model provenance" $scriptText "evidenceModel"
Assert-Contains "script reads optional evidence runner provenance" $scriptText "evidenceRunner"
Assert-Contains "script writes tile model provenance" $scriptText "tileEvidenceModel"
Assert-Contains "script writes face verification runner provenance" $scriptText "faceVerificationEvidenceRunner"
Assert-Contains "script summarizes source-bound face verification rows" $scriptText "sourceBoundFaceVerificationRows"
Assert-Contains "script marks person object as priority-only" $scriptText "priority-only-not-face-evidence"
Assert-Contains "script treats auxiliary boost as non-final" $scriptText "auxiliary person/object support raises review priority but does not decide face/nonface"
if ($scriptText -match 'missProbability = \[Math\]::Min\(0\.98,[^\r\n]*personUpperOverlap') {
    throw "Expected person/object support to stay out of missProbability formulas."
}
Write-Host "[YoloPseudoGtEvidenceVerify] pass script keeps person object out of miss probability"
Assert-Contains "script records expected review label in queue" $scriptText "expectedReviewLabel"
Assert-Contains "script preserves queue evidence notes" $scriptText 'evidenceNotes = \$row\.evidenceNotes'
Assert-Contains "script preserves review queue geometry" $scriptText "basePredictionId[\s\S]*tileDetectionId[\s\S]*verificationId[\s\S]*x ="
Assert-Contains "script records verification-only misses" $scriptText "test-only high-quality face verification was not matched by base YOLO"
Assert-Contains "script filters weak verification-only misses" $scriptText 'Test-VerificationMetricSupport \$verification'
Assert-Contains "script filters weak tile evidence" $scriptText "MinTileFaceConfidence"
Assert-Contains "script requires repeated tile support" $scriptText 'TileSupportCount -ge \$MinTileSupportCount'
Assert-Contains "script skips weak tile-only misses" $scriptText 'Test-TileFaceMetricSupport \$tile'
Assert-Contains "script treats person object as auxiliary" $scriptText "person/object support is auxiliary only"
Assert-Contains "script does not finalize labels" $scriptText "final face/nonface/miss must be copied into the review CSV"
Assert-Contains "summary records test-only boundary" $summaryText "test-only evidence"
Assert-Contains "summary records supported count" $summaryText "supportedFaceCandidate=2"
Assert-Contains "summary records false positive count" $summaryText "falsePositiveCandidate=5"
Assert-Contains "summary records miss count" $summaryText "missCandidate=6"
Assert-Contains "summary records model provenance count" $summaryText "modelProvenanceRows="
Assert-Contains "summary records runner provenance count" $summaryText "runnerProvenanceRows="
Assert-Contains "summary records source-bound face verification count" $summaryText "sourceBoundFaceVerificationRows="
Assert-Contains "summary records geometry tagged row count" $summaryText "geometryTaggedRows=1"
Assert-Contains "summary records geometry tags" $summaryText "geometryTags=top-edge-large-review"
Assert-Contains "summary records provenance field contract" $summaryText "evidenceProvenance=optional-evidenceModel/evidenceRunner"
Assert-Contains "summary records review queue path" $summaryText "reviewQueue="
Assert-Contains "summary records strict input validation" $summaryText "inputValidation=strict-required-columns"
Assert-Contains "summary records top review candidates" $summaryText "topReviewCandidates="
Assert-Contains "summary records temporal support window" $summaryText "temporalSupportWindowFrames=2"
Assert-Contains "summary records tile confidence threshold" $summaryText "minTileFaceConfidence=0.55"
Assert-Contains "summary records tile support threshold" $summaryText "minTileSupportCount=2"
Assert-Contains "summary records person object confidence threshold" $summaryText "minPersonObjectConfidence=0.5"
Assert-Contains "summary records support area ratio" $summaryText "maxSupportAreaChangeRatio=3"
Assert-Contains "guide documents high-quality verification" $guideText "face verification/face detection"
Assert-Contains "guide documents runtime separation" $guideText "pseudo-GT"
Assert-Contains "guide documents tile confidence threshold" $guideText "MinTileFaceConfidence"
Assert-Contains "guide documents tile support threshold" $guideText "MinTileSupportCount"
Assert-Contains "guide documents pseudo gt output fields" $guideText "faceVerificationConfidence[\s\S]*faceVerificationDistance"
Assert-Contains "guide documents optional pseudo gt model provenance" $guideText "evidenceModel[\s\S]*evidenceRunner"
Assert-Contains "guide documents area ratio evidence" $guideText "areaChangeRatio"
Assert-Contains "guide documents no-detection pseudo gt miss evidence" $guideText "AllowNoDetections[\s\S]*WithPseudoGtTileInput[\s\S]*missCandidate"

Write-Host "[YoloPseudoGtEvidenceVerify] all requested checks passed"
