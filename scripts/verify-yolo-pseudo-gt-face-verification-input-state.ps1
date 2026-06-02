param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$script = Join-Path $repo "scripts\new-yolo-pseudo-gt-face-verification-input.ps1"
$guide = Join-Path $repo "YOLO_PROBLEM_SPAN_VERIFICATION.md"
$work = Join-Path $repo ".tmp\yolo-pseudo-gt-face-verification-input-verify"
$outDir = Join-Path $work "out"
$baseLog = Join-Path $work "base-yolo.log"
$multiBaseLog = Join-Path $work "multi-base-yolo.log"
$externalGenerator = Join-Path $work "external-verifier.ps1"
$externalCsv = Join-Path $work "external-face-verification.csv"
$badExternalGenerator = Join-Path $work "bad-external-verifier.ps1"
$badExternalCsv = Join-Path $work "bad-external-face-verification.csv"
$badCropExternalGenerator = Join-Path $work "bad-crop-external-verifier.ps1"
$badCropExternalCsv = Join-Path $work "bad-crop-external-face-verification.csv"
$badAmbiguousExternalGenerator = Join-Path $work "bad-ambiguous-external-verifier.ps1"
$badAmbiguousExternalCsv = Join-Path $work "bad-ambiguous-external-face-verification.csv"
$multiExternalGenerator = Join-Path $work "multi-external-verifier.ps1"
$multiExternalCsv = Join-Path $work "multi-external-face-verification.csv"

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
[SmokeDetection] label=synthetic-yolo, frame=7, index=0, x=20.0, y=30.0, w=30.0, h=34.0, area=1020.0, conf=0.710, cx=0.100, cy=0.120, areaRatio=0.001020, aspectRatio=0.882
[SmokeDetection] label=synthetic-yolo, frame=7, index=1, x=180.0, y=90.0, w=40.0, h=44.0, area=1760.0, conf=0.650, cx=0.600, cy=0.420, areaRatio=0.001760, aspectRatio=0.909
'@ | Set-Content -Encoding UTF8 -Path $multiBaseLog

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
        x = ([double]$_.x - [double]$_.cropX)
        y = ([double]$_.y - [double]$_.cropY)
        w = $_.w
        h = $_.h
        faceVerificationConfidence = 0.91
        faceVerificationDistance = 0.18
    }
} | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $OutputCsv
'@ | Set-Content -Encoding UTF8 -Path $externalGenerator

@'
param(
    [Parameter(Mandatory = $true)]
    [string]$ManifestCsv,
    [Parameter(Mandatory = $true)]
    [string]$OutputCsv
)

@(
    [pscustomobject]@{
        frame = 999
        verificationId = "bad-outside-manifest"
        x = 10
        y = 10
        w = 20
        h = 20
        faceVerificationConfidence = 0.91
        faceVerificationDistance = 0.18
    }
) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $OutputCsv
'@ | Set-Content -Encoding UTF8 -Path $badExternalGenerator

@'
param(
    [Parameter(Mandatory = $true)]
    [string]$ManifestCsv,
    [Parameter(Mandatory = $true)]
    [string]$OutputCsv
)

$manifest = @(Import-Csv $ManifestCsv)
@(
    [pscustomobject]@{
        frame = $manifest[0].frame
        candidateId = $manifest[0].candidateId
        verificationId = "bad-outside-crop"
        x = 300
        y = 220
        w = 20
        h = 20
        faceVerificationConfidence = 0.91
        faceVerificationDistance = 0.18
    }
) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $OutputCsv
'@ | Set-Content -Encoding UTF8 -Path $badCropExternalGenerator

@'
param(
    [Parameter(Mandatory = $true)]
    [string]$ManifestCsv,
    [Parameter(Mandatory = $true)]
    [string]$OutputCsv
)

$manifest = @(Import-Csv $ManifestCsv)
$first = $manifest[0]
@(
    [pscustomobject]@{
        frame = $first.frame
        verificationId = "bad-ambiguous-crop"
        x = $first.x
        y = $first.y
        w = $first.w
        h = $first.h
        faceVerificationConfidence = 0.91
        faceVerificationDistance = 0.18
    }
) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $OutputCsv
'@ | Set-Content -Encoding UTF8 -Path $badAmbiguousExternalGenerator

@'
param(
    [Parameter(Mandatory = $true)]
    [string]$ManifestCsv,
    [Parameter(Mandatory = $true)]
    [string]$OutputCsv
)

$manifest = @(Import-Csv $ManifestCsv)
$manifest | ForEach-Object {
    [pscustomobject]@{
        frame = $_.frame
        candidateId = $_.candidateId
        verificationId = "verify-$($_.frame)-$($_.basePredictionId)"
        x = $_.x
        y = $_.y
        w = $_.w
        h = $_.h
        faceVerificationConfidence = 0.91
        faceVerificationDistance = 0.18
    }
} | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $OutputCsv
'@ | Set-Content -Encoding UTF8 -Path $multiExternalGenerator

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
    -ExternalOutputCsv $externalCsv `
    -ExternalOutputCoordinateSpace CropImage 2>&1

if ($LASTEXITCODE -ne 0) {
    throw "new-yolo-pseudo-gt-face-verification-input.ps1 failed: $($output | Out-String)"
}

$previousErrorActionPreference = $ErrorActionPreference
try {
    $ErrorActionPreference = "Continue"
    $tooManyFramesOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script `
        -BasePredictionLog $baseLog `
        -OutputDir (Join-Path $work "too-many") `
        -FrameWidth 320 `
        -FrameHeight 240 `
        -MaxFrames 1 `
        -SkipImageExtraction 2>&1
    $tooManyFramesExitCode = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $previousErrorActionPreference
}

if ($tooManyFramesExitCode -eq 0 -or (($tooManyFramesOutput | Out-String) -notmatch "limited to 1 frames")) {
    throw "Expected face verification input to reject oversized pseudo-GT frame sets by default."
}

$badTemplate = "-NoProfile -ExecutionPolicy Bypass -File `"$badExternalGenerator`" -ManifestCsv `"{manifest}`" -OutputCsv `"{output}`""
try {
    $ErrorActionPreference = "Continue"
    $badExternalOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script `
        -BasePredictionLog $baseLog `
        -OutputDir (Join-Path $work "bad-external") `
        -FrameWidth 320 `
        -FrameHeight 240 `
        -SkipImageExtraction `
        -ExternalCommand "powershell.exe" `
        -ExternalArgumentsTemplate $badTemplate `
        -ExternalOutputCsv $badExternalCsv 2>&1
    $badExternalExitCode = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $previousErrorActionPreference
}

if ($badExternalExitCode -eq 0 -or (($badExternalOutput | Out-String) -notmatch "outside the manifest")) {
    throw "Expected face verification input to reject external CSV rows outside the manifest."
}

$badCropTemplate = "-NoProfile -ExecutionPolicy Bypass -File `"$badCropExternalGenerator`" -ManifestCsv `"{manifest}`" -OutputCsv `"{output}`""
try {
    $ErrorActionPreference = "Continue"
    $badCropExternalOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script `
        -BasePredictionLog $baseLog `
        -OutputDir (Join-Path $work "bad-crop-external") `
        -FrameWidth 320 `
        -FrameHeight 240 `
        -SkipImageExtraction `
        -ExternalCommand "powershell.exe" `
        -ExternalArgumentsTemplate $badCropTemplate `
        -ExternalOutputCsv $badCropExternalCsv 2>&1
    $badCropExternalExitCode = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $previousErrorActionPreference
}

if ($badCropExternalExitCode -eq 0 -or (($badCropExternalOutput | Out-String) -notmatch "outside the manifest crop")) {
    throw "Expected face verification input to reject external CSV rows outside the manifest crop."
}

$badAmbiguousTemplate = "-NoProfile -ExecutionPolicy Bypass -File `"$badAmbiguousExternalGenerator`" -ManifestCsv `"{manifest}`" -OutputCsv `"{output}`""
try {
    $ErrorActionPreference = "Continue"
    $badAmbiguousExternalOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script `
        -BasePredictionLog $multiBaseLog `
        -OutputDir (Join-Path $work "bad-ambiguous-external") `
        -FrameWidth 320 `
        -FrameHeight 240 `
        -SkipImageExtraction `
        -ExternalCommand "powershell.exe" `
        -ExternalArgumentsTemplate $badAmbiguousTemplate `
        -ExternalOutputCsv $badAmbiguousExternalCsv 2>&1
    $badAmbiguousExternalExitCode = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $previousErrorActionPreference
}

if ($badAmbiguousExternalExitCode -eq 0 -or (($badAmbiguousExternalOutput | Out-String) -notmatch "must include candidateId/sourceCandidateId/basePredictionId")) {
    throw "Expected face verification input to require candidate id for multi-crop frames."
}

$multiTemplate = "-NoProfile -ExecutionPolicy Bypass -File `"$multiExternalGenerator`" -ManifestCsv `"{manifest}`" -OutputCsv `"{output}`""
$multiOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script `
    -BasePredictionLog $multiBaseLog `
    -OutputDir (Join-Path $work "multi-external") `
    -FrameWidth 320 `
    -FrameHeight 240 `
    -SkipImageExtraction `
    -ExternalCommand "powershell.exe" `
    -ExternalArgumentsTemplate $multiTemplate `
    -ExternalOutputCsv $multiExternalCsv 2>&1

if ($LASTEXITCODE -ne 0) {
    throw "Expected candidate-bound multi-crop face verification to pass: $($multiOutput | Out-String)"
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

foreach ($column in @("frame", "candidateId", "basePredictionId", "x", "y", "w", "h", "baseFaceConfidence", "cropX", "cropY", "cropW", "cropH", "cropImageW", "cropImageH", "frameWidth", "frameHeight", "cropImagePath", "cropRelativePath")) {
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

$secondExternalRow = $externalRows[1]
$secondManifestRow = $rows[1]
$normalizedX = [double]::Parse($secondExternalRow.x, [System.Globalization.CultureInfo]::InvariantCulture)
$normalizedY = [double]::Parse($secondExternalRow.y, [System.Globalization.CultureInfo]::InvariantCulture)
$expectedX = [double]::Parse($secondManifestRow.x, [System.Globalization.CultureInfo]::InvariantCulture)
$expectedY = [double]::Parse($secondManifestRow.y, [System.Globalization.CultureInfo]::InvariantCulture)
if ([Math]::Abs($normalizedX - $expectedX) -gt 0.001 -or
    [Math]::Abs($normalizedY - $expectedY) -gt 0.001) {
    throw "Expected CropImage external face verification output to be normalized to original frame coordinates."
}

if ($secondExternalRow.inputCoordinateSpace -ne "CropImage" -or $secondExternalRow.normalizedCoordinateSpace -ne "Frame") {
    throw "Expected normalized face verification output to preserve coordinate-space audit columns."
}

$scriptText = Get-Content -Raw -Path $script
$guideText = Get-Content -Raw -Path $guide
$summaryText = Get-Content -Raw -Path $summary

Assert-Contains "script accepts base prediction log" $scriptText "BasePredictionLog"
Assert-Contains "script accepts base prediction csv" $scriptText "BasePredictionCsv"
Assert-Contains "script limits large frame sets" $scriptText "MaxFrames"
Assert-Contains "script has explicit large frame override" $scriptText "AllowLargeFrameSet"
Assert-Contains "script writes face verification manifest" $scriptText "face-verification-manifest\.csv"
Assert-Contains "script extracts candidate crops" $scriptText "Invoke-FfmpegCropExtraction"
Assert-Contains "script supports external command hook" $scriptText "ExternalCommand"
Assert-Contains "script requires external output csv" $scriptText "ExternalOutputCsv is required"
Assert-Contains "script defaults external timeout" $scriptText '\[int\]\$ExternalTimeoutSeconds\s*=\s*180'
Assert-Contains "script supports external output coordinate space" $scriptText "ExternalOutputCoordinateSpace"
Assert-Contains "script normalizes crop image coordinates" $scriptText "Convert-ExternalFaceVerificationCsvCoordinateSpace"
Assert-Contains "script validates external output against manifest" $scriptText "outside the manifest"
Assert-Contains "script validates external output against manifest crop" $scriptText "outside the manifest crop"
Assert-Contains "script validates optional external candidate id" $scriptText "candidateId[\s\S]*sourceCandidateId[\s\S]*basePredictionId"
Assert-Contains "script requires candidate id for multi-crop frame" $scriptText "must include candidateId/sourceCandidateId/basePredictionId"
Assert-Contains "script records runtime separation" $summaryText "not part of the app runtime path"
Assert-Contains "summary records crop count" $summaryText "crops=2"
Assert-Contains "summary records frame count" $summaryText "frameCount=2"
Assert-Contains "summary records max frames" $summaryText "maxFrames=900"
Assert-Contains "summary records external command" $summaryText "externalCommandUsed=True"
Assert-Contains "summary records external coordinate space" $summaryText "externalOutputCoordinateSpace=CropImage"
Assert-Contains "summary records face verification output fields" $summaryText "faceVerificationConfidence"
Assert-Contains "summary records manifest crop validation" $summaryText "manifest crops"
Assert-Contains "summary records multi-crop binding rule" $summaryText "multiple manifest crops"
Assert-Contains "guide documents face verification input" $guideText "new-yolo-pseudo-gt-face-verification-input\.ps1"

Write-Host "[YoloPseudoGtFaceVerificationInputVerify] all requested checks passed"
