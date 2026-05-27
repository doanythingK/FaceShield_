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
$outputCsv = Join-Path $work "pseudo-gt-candidates.csv"
$summaryPath = Join-Path $work "pseudo-gt-summary.md"

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
'@ | Set-Content -Encoding UTF8 -Path $baseLog

@(
    [pscustomobject]@{ frame = 1; detectionId = "tile-face-1"; x = 101.0; y = 102.0; w = 51.0; h = 58.0; confidence = 0.910; tileSupportCount = 3 },
    [pscustomobject]@{ frame = 3; detectionId = "tile-face-3"; x = 45.0; y = 60.0; w = 24.0; h = 28.0; confidence = 0.870; tileSupportCount = 2 }
) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $tileCsv

@(
    [pscustomobject]@{ frame = 1; verificationId = "verify-face-1"; x = 102.0; y = 101.0; w = 49.0; h = 59.0; faceVerificationConfidence = 0.880; faceVerificationDistance = 0.220 }
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
    -SummaryPath $summaryPath 2>&1

if ($LASTEXITCODE -ne 0) {
    throw "new-yolo-pseudo-gt-evidence.ps1 failed: $($output | Out-String)"
}

Assert-File "pseudo-GT output CSV" $outputCsv
Assert-File "pseudo-GT summary" $summaryPath

$scriptText = Get-Content -Raw -Path $script
$guideText = Get-Content -Raw -Path $guide
$summaryText = Get-Content -Raw -Path $summaryPath
$rows = @(Import-Csv $outputCsv)

if ($rows.Count -ne 3) {
    throw "Expected 3 pseudo-GT rows, actual=$($rows.Count)"
}

$first = $rows[0]
foreach ($column in @(
        "candidateId",
        "candidateType",
        "baseFaceConfidence",
        "tileFaceConfidence",
        "tileSupportCount",
        "faceVerificationConfidence",
        "faceVerificationDistance",
        "personConfidence",
        "personUpperOverlap",
        "fpProbability",
        "missProbability",
        "pseudoGtReason",
        "reviewLabel",
        "reviewStatus",
        "evidenceNotes")) {
    Assert-Column $first $column
}

if (@($rows | Where-Object { $_.candidateType -eq "supportedFaceCandidate" }).Count -ne 1) {
    throw "Expected one supportedFaceCandidate."
}

if (@($rows | Where-Object { $_.candidateType -eq "falsePositiveCandidate" }).Count -ne 1) {
    throw "Expected one falsePositiveCandidate."
}

if (@($rows | Where-Object { $_.candidateType -eq "missCandidate" }).Count -ne 1) {
    throw "Expected one missCandidate."
}

if (@($rows | Where-Object { $_.reviewStatus -ne "pending-human" }).Count -ne 0) {
    throw "Pseudo-GT rows must remain pending-human."
}

Assert-Contains "script accepts base prediction log" $scriptText "BasePredictionLog"
Assert-Contains "script accepts tile face CSV" $scriptText "TileFaceCsv"
Assert-Contains "script accepts face verification CSV" $scriptText "FaceVerificationCsv"
Assert-Contains "script accepts person object CSV" $scriptText "PersonObjectCsv"
Assert-Contains "script calculates IoU" $scriptText "function Get-Iou"
Assert-Contains "script calculates center distance" $scriptText "Get-CenterDistanceRatio"
Assert-Contains "script treats person object as auxiliary" $scriptText "person/object support is auxiliary only"
Assert-Contains "script does not finalize labels" $scriptText "final face/nonface/miss must be copied into the review CSV"
Assert-Contains "summary records test-only boundary" $summaryText "test-only evidence"
Assert-Contains "summary records supported count" $summaryText "supportedFaceCandidate=1"
Assert-Contains "summary records false positive count" $summaryText "falsePositiveCandidate=1"
Assert-Contains "summary records miss count" $summaryText "missCandidate=1"
Assert-Contains "guide documents high-quality verification" $guideText "face verification/face detection"
Assert-Contains "guide documents runtime separation" $guideText "pseudo-GT"
Assert-Contains "guide documents pseudo gt output fields" $guideText "faceVerificationConfidence[\s\S]*faceVerificationDistance"

Write-Host "[YoloPseudoGtEvidenceVerify] all requested checks passed"
