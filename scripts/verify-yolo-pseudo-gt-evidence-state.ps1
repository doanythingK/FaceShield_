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
'@ | Set-Content -Encoding UTF8 -Path $baseLog

@'
[AutoRunSummary] runId=empty-base, detector=YoloFaceOnnxDetector/CPU, mode=pipe-parallel, totalFrames=12, startFrame=0, processed=12, decoded=12, detects=12, interpolated=0, readMs=0, decodeMs=10, detectMs=20, maskMs=0, totalMs=30, downscale=1.000, quality=BalancedBilinear, tracking=True, everyN=1, parallel=2, roi=regular=0, small=0, rejected=0, statsRejected=0
'@ | Set-Content -Encoding UTF8 -Path $emptyBaseLog

@(
    [pscustomobject]@{ frame = 1; detectionId = "tile-face-1"; x = 101.0; y = 102.0; w = 51.0; h = 58.0; confidence = 0.910; tileSupportCount = 3 },
    [pscustomobject]@{ frame = 2; detectionId = "tile-face-2"; x = 103.0; y = 103.0; w = 50.0; h = 58.0; confidence = 0.900; tileSupportCount = 2 },
    [pscustomobject]@{ frame = 3; detectionId = "tile-face-3"; x = 45.0; y = 60.0; w = 24.0; h = 28.0; confidence = 0.870; tileSupportCount = 2 }
) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $tileCsv

@(
    [pscustomobject]@{ frame = 1; verificationId = "verify-face-1"; x = 102.0; y = 101.0; w = 49.0; h = 59.0; faceVerificationConfidence = 0.880; faceVerificationDistance = 0.220 },
    [pscustomobject]@{ frame = 4; verificationId = "verify-face-4"; x = 210.0; y = 120.0; w = 32.0; h = 36.0; faceVerificationConfidence = 0.910; faceVerificationDistance = 0.180 },
    [pscustomobject]@{ frame = 5; verificationId = "verify-small-face-5"; x = 575.0; y = 575.0; w = 50.0; h = 50.0; faceVerificationConfidence = 0.930; faceVerificationDistance = 0.150 }
) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $verificationCsv

@(
    [pscustomobject]@{ frame = 2; detectionId = "person-2"; x = 390.0; y = 280.0; w = 90.0; h = 190.0; confidence = 0.760 }
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

if ($rows.Count -ne 7) {
    throw "Expected 7 pseudo-GT rows, actual=$($rows.Count)"
}

if ($reviewQueueRows.Count -ne 7) {
    throw "Expected 7 pseudo-GT review queue rows, actual=$($reviewQueueRows.Count)"
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
        "supportFrameCount",
        "supportRowCount",
        "supportSources",
        "bestIou",
        "centerDistanceRatio",
        "areaChangeRatio",
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
        "basePredictionId",
        "tileDetectionId",
        "verificationId",
        "x",
        "y",
        "w",
        "h",
        "reviewPriorityScore",
        "auxiliaryPriorityBoost",
        "dominantProbability",
        "reviewPriorityReason",
        "fpProbability",
        "missProbability",
        "supportRowCount",
        "areaChangeRatio",
        "reviewStatus")) {
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

if ([double]::Parse($falsePositiveQueueRow.auxiliaryPriorityBoost, [System.Globalization.CultureInfo]::InvariantCulture) -le 0) {
    throw "Expected person/object support to raise falsePositiveCandidate review priority."
}

if ($falsePositiveQueueRow.reviewPriorityReason -notmatch "auxiliary person/object support raises review priority") {
    throw "Expected review priority reason to explain auxiliary person/object boost."
}

if (@($rows | Where-Object { $_.candidateType -eq "supportedFaceCandidate" }).Count -ne 1) {
    throw "Expected one supportedFaceCandidate."
}

if (@($rows | Where-Object { $_.candidateType -eq "falsePositiveCandidate" }).Count -ne 2) {
    throw "Expected two falsePositiveCandidate rows."
}

if (@($rows | Where-Object { $_.candidateType -eq "missCandidate" }).Count -ne 4) {
    throw "Expected four missCandidate rows."
}

if (@($rows | Where-Object { $_.candidateType -eq "missCandidate" -and $_.source -eq "face-verification" -and $_.verificationId -eq "verify-face-4" }).Count -ne 1) {
    throw "Expected one verification-only missCandidate."
}

$largeGeometryMismatch = @($rows | Where-Object { $_.candidateType -eq "falsePositiveCandidate" -and $_.basePredictionId -eq "5-0" })[0]
if ($null -eq $largeGeometryMismatch) {
    throw "Expected center-aligned large YOLO box to remain a falsePositiveCandidate when area ratio is too different."
}

if ($largeGeometryMismatch.areaChangeRatio -ne "99") {
    throw "Expected unsupported geometry mismatch to preserve sentinel areaChangeRatio=99."
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
Assert-Contains "script validates required input columns" $scriptText "strict-required-columns"
Assert-Contains "script requires face verification distance" $scriptText "faceVerificationDistance"
Assert-Contains "script calculates IoU" $scriptText "function Get-Iou"
Assert-Contains "script calculates center distance" $scriptText "Get-CenterDistanceRatio"
Assert-Contains "script checks support area ratio" $scriptText "MaxSupportAreaChangeRatio"
Assert-Contains "script records support area ratio" $scriptText "areaChangeRatio"
Assert-Contains "script records best geometry support" $scriptText "Get-MinMatchProperty"
Assert-Contains "script records temporal support" $scriptText "supportFrameCount"
Assert-Contains "script records repeated support rows" $scriptText "supportRowCount"
Assert-Contains "script writes review queue csv" $scriptText "ReviewQueueCsv"
Assert-Contains "script supports no-base miss evidence" $scriptText 'baseRows=\$\(\$baseRows.Count\)'
Assert-Contains "script records review priority score" $scriptText "reviewPriorityScore"
Assert-Contains "script records auxiliary priority boost" $scriptText "auxiliaryPriorityBoost"
Assert-Contains "script treats auxiliary boost as non-final" $scriptText "auxiliary person/object support raises review priority but does not decide face/nonface"
Assert-Contains "script preserves review queue geometry" $scriptText "basePredictionId[\s\S]*tileDetectionId[\s\S]*verificationId[\s\S]*x ="
Assert-Contains "script records verification-only misses" $scriptText "test-only high-quality face verification was not matched by base YOLO"
Assert-Contains "script treats person object as auxiliary" $scriptText "person/object support is auxiliary only"
Assert-Contains "script does not finalize labels" $scriptText "final face/nonface/miss must be copied into the review CSV"
Assert-Contains "summary records test-only boundary" $summaryText "test-only evidence"
Assert-Contains "summary records supported count" $summaryText "supportedFaceCandidate=1"
Assert-Contains "summary records false positive count" $summaryText "falsePositiveCandidate=2"
Assert-Contains "summary records miss count" $summaryText "missCandidate=4"
Assert-Contains "summary records review queue path" $summaryText "reviewQueue="
Assert-Contains "summary records strict input validation" $summaryText "inputValidation=strict-required-columns"
Assert-Contains "summary records top review candidates" $summaryText "topReviewCandidates="
Assert-Contains "summary records temporal support window" $summaryText "temporalSupportWindowFrames=2"
Assert-Contains "summary records support area ratio" $summaryText "maxSupportAreaChangeRatio=3"
Assert-Contains "guide documents high-quality verification" $guideText "face verification/face detection"
Assert-Contains "guide documents runtime separation" $guideText "pseudo-GT"
Assert-Contains "guide documents pseudo gt output fields" $guideText "faceVerificationConfidence[\s\S]*faceVerificationDistance"
Assert-Contains "guide documents area ratio evidence" $guideText "areaChangeRatio"
Assert-Contains "guide documents no-detection pseudo gt miss evidence" $guideText "AllowNoDetections[\s\S]*WithPseudoGtTileInput[\s\S]*missCandidate"

Write-Host "[YoloPseudoGtEvidenceVerify] all requested checks passed"
