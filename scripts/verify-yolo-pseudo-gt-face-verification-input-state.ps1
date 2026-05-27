param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$script = Join-Path $repo "scripts\new-yolo-pseudo-gt-face-verification-input.ps1"
$guide = Join-Path $repo "YOLO_PROBLEM_SPAN_VERIFICATION.md"
$work = Join-Path $repo ".tmp\yolo-pseudo-gt-face-verification-input-verify"
$outDir = Join-Path $work "out"
$baseLog = Join-Path $work "base-yolo.log"
$externalGenerator = Join-Path $work "external-verifier.ps1"
$externalCsv = Join-Path $work "external-face-verification.csv"

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

    Write-Host "[YoloPseudoGtFaceVerificationInputVerify] pass $Name"
}

Assert-File "pseudo-GT face verification input script" $script
Assert-File "problem-span guide" $guide

New-Item -ItemType Directory -Force -Path $work | Out-Null

@'
[SmokeDetection] label=synthetic-yolo, frame=1, index=0, x=10.0, y=12.0, w=20.0, h=24.0, area=480.0, conf=0.720, cx=0.100, cy=0.120, areaRatio=0.000480, aspectRatio=0.833
[SmokeDetection] label=synthetic-yolo, frame=2, index=0, x=260.0, y=180.0, w=50.0, h=60.0, area=3000.0, conf=0.420, cx=0.700, cy=0.700, areaRatio=0.003000, aspectRatio=0.833
'@ | Set-Content -Encoding UTF8 -Path $baseLog

@'
param(
    [Parameter(Mandatory = $true)]
    [string]$ManifestCsv,
    [Parameter(Mandatory = $true)]
    [string]$OutputCsv
)

$rows = @(Import-Csv $ManifestCsv)
if ($rows.Count -ne 2) {
    throw "expected two verification input rows"
}

$rows | ForEach-Object {
    [pscustomobject]@{
        frame = $_.frame
        verificationId = "verify-$($_.frame)-$($_.basePredictionId)"
        x = $_.x
        y = $_.y
        w = $_.w
        h = $_.h
        faceVerificationConfidence = 0.91
        faceVerificationDistance = 0.18
    }
} | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $OutputCsv
'@ | Set-Content -Encoding UTF8 -Path $externalGenerator

$template = "-NoProfile -ExecutionPolicy Bypass -File `"$externalGenerator`" -ManifestCsv `"{manifest}`" -OutputCsv `"{output}`""
$output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script `
    -BasePredictionLog $baseLog `
    -OutputDir $outDir `
    -FrameWidth 320 `
    -FrameHeight 240 `
    -CropPaddingRatio 0.50 `
    -MinCropSize 32 `
    -SkipImageExtraction `
    -ExternalCommand "powershell.exe" `
    -ExternalArgumentsTemplate $template `
    -ExternalOutputCsv $externalCsv 2>&1

if ($LASTEXITCODE -ne 0) {
    throw "new-yolo-pseudo-gt-face-verification-input.ps1 failed: $($output | Out-String)"
}

$manifest = Join-Path $outDir "face-verification-manifest.csv"
$summary = Join-Path $outDir "face-verification-input-summary.md"

Assert-File "face verification manifest" $manifest
Assert-File "face verification input summary" $summary
Assert-File "external face verification CSV" $externalCsv

$rows = @(Import-Csv $manifest)
if ($rows.Count -ne 2) {
    throw "Expected two manifest rows, actual=$($rows.Count)"
}

foreach ($column in @("frame", "candidateId", "basePredictionId", "x", "y", "w", "h", "baseFaceConfidence", "cropX", "cropY", "cropW", "cropH", "frameWidth", "frameHeight", "cropImagePath", "cropRelativePath")) {
    if ($null -eq $rows[0].PSObject.Properties[$column]) {
        throw "Missing manifest column: $column"
    }
}

if ([int]$rows[0].cropX -ne 0 -or [int]$rows[0].cropY -ne 0) {
    throw "Expected first crop to clamp to the top-left frame boundary."
}

if ([int]$rows[1].cropX + [int]$rows[1].cropW -gt 320 -or [int]$rows[1].cropY + [int]$rows[1].cropH -gt 240) {
    throw "Expected second crop to clamp to frame bounds."
}

$externalRows = @(Import-Csv $externalCsv)
if ($externalRows.Count -ne 2) {
    throw "Expected two external verification rows, actual=$($externalRows.Count)"
}

$scriptText = Get-Content -Raw -Path $script
$guideText = Get-Content -Raw -Path $guide
$summaryText = Get-Content -Raw -Path $summary

Assert-Contains "script accepts base prediction log" $scriptText "BasePredictionLog"
Assert-Contains "script accepts base prediction csv" $scriptText "BasePredictionCsv"
Assert-Contains "script writes face verification manifest" $scriptText "face-verification-manifest\.csv"
Assert-Contains "script extracts candidate crops" $scriptText "Invoke-FfmpegCropExtraction"
Assert-Contains "script supports external command hook" $scriptText "ExternalCommand"
Assert-Contains "script requires external output csv" $scriptText "ExternalOutputCsv is required"
Assert-Contains "script records runtime separation" $summaryText "not part of the app runtime path"
Assert-Contains "summary records crop count" $summaryText "crops=2"
Assert-Contains "summary records external command" $summaryText "externalCommandUsed=True"
Assert-Contains "summary records face verification output fields" $summaryText "faceVerificationConfidence"
Assert-Contains "guide documents face verification input" $guideText "new-yolo-pseudo-gt-face-verification-input\.ps1"

Write-Host "[YoloPseudoGtFaceVerificationInputVerify] all requested checks passed"
