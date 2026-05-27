param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$script = Join-Path $repo "scripts\new-yolo-pseudo-gt-person-object-input.ps1"
$guide = Join-Path $repo "YOLO_PROBLEM_SPAN_VERIFICATION.md"
$work = Join-Path $repo ".tmp\yolo-pseudo-gt-person-object-input-verify"
$outDir = Join-Path $work "out"
$externalGenerator = Join-Path $work "external-person-object.ps1"
$externalCsv = Join-Path $work "external-person-object.csv"
$badExternalGenerator = Join-Path $work "bad-external-person-object.ps1"
$badExternalCsv = Join-Path $work "bad-external-person-object.csv"
$badBoundsExternalGenerator = Join-Path $work "bad-bounds-external-person-object.ps1"
$badBoundsExternalCsv = Join-Path $work "bad-bounds-external-person-object.csv"

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

    Write-Host "[YoloPseudoGtPersonObjectInputVerify] pass $Name"
}

Assert-File "pseudo-GT person object input script" $script
Assert-File "problem-span guide" $guide

New-Item -ItemType Directory -Force -Path $work | Out-Null

@'
param(
    [Parameter(Mandatory = $true)]
    [string]$ManifestCsv,
    [Parameter(Mandatory = $true)]
    [string]$OutputCsv
)

$rows = @(Import-Csv $ManifestCsv)
if ($rows.Count -lt 1) {
    throw "manifest has no rows"
}

$rows | ForEach-Object {
    [pscustomobject]@{
        frame = $_.frame
        detectionId = "person-$($_.frame)"
        x = 40
        y = 20
        w = 120
        h = 180
        confidence = 0.82
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
        detectionId = "bad-outside-manifest"
        x = 40
        y = 20
        w = 120
        h = 180
        confidence = 0.82
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
        detectionId = "bad-outside-frame"
        x = 400
        y = 20
        w = 120
        h = 180
        confidence = 0.82
    }
) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $OutputCsv
'@ | Set-Content -Encoding UTF8 -Path $badBoundsExternalGenerator

$template = "-NoProfile -ExecutionPolicy Bypass -File `"$externalGenerator`" -ManifestCsv `"{manifest}`" -OutputCsv `"{output}`""
$output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script `
    -Frames "1,3-4" `
    -OutputDir $outDir `
    -FrameWidth 320 `
    -FrameHeight 240 `
    -ScaleWidth 640 `
    -SkipImageExtraction `
    -ExternalCommand "powershell.exe" `
    -ExternalArgumentsTemplate $template `
    -ExternalOutputCsv $externalCsv 2>&1

if ($LASTEXITCODE -ne 0) {
    throw "new-yolo-pseudo-gt-person-object-input.ps1 failed: $($output | Out-String)"
}

$previousErrorActionPreference = $ErrorActionPreference
try {
    $ErrorActionPreference = "Continue"
    $tooManyFramesOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script `
        -Frames "1-3" `
        -OutputDir (Join-Path $work "too-many") `
        -FrameWidth 320 `
        -FrameHeight 240 `
        -MaxFrames 2 `
        -SkipImageExtraction 2>&1
    $tooManyFramesExitCode = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $previousErrorActionPreference
}

if ($tooManyFramesExitCode -eq 0 -or (($tooManyFramesOutput | Out-String) -notmatch "limited to 2 frames")) {
    throw "Expected person/object input to reject oversized pseudo-GT frame sets by default."
}

$badTemplate = "-NoProfile -ExecutionPolicy Bypass -File `"$badExternalGenerator`" -ManifestCsv `"{manifest}`" -OutputCsv `"{output}`""
try {
    $ErrorActionPreference = "Continue"
    $badExternalOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script `
        -Frames "1" `
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
    throw "Expected person/object input to reject external CSV rows outside the manifest."
}

$badBoundsTemplate = "-NoProfile -ExecutionPolicy Bypass -File `"$badBoundsExternalGenerator`" -ManifestCsv `"{manifest}`" -OutputCsv `"{output}`""
try {
    $ErrorActionPreference = "Continue"
    $badBoundsExternalOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script `
        -Frames "1" `
        -OutputDir (Join-Path $work "bad-bounds-external") `
        -FrameWidth 320 `
        -FrameHeight 240 `
        -SkipImageExtraction `
        -ExternalCommand "powershell.exe" `
        -ExternalArgumentsTemplate $badBoundsTemplate `
        -ExternalOutputCsv $badBoundsExternalCsv 2>&1
    $badBoundsExternalExitCode = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $previousErrorActionPreference
}

if ($badBoundsExternalExitCode -eq 0 -or (($badBoundsExternalOutput | Out-String) -notmatch "outside the manifest frame bounds")) {
    throw "Expected person/object input to reject external CSV rows outside frame bounds."
}

$manifest = Join-Path $outDir "person-object-manifest.csv"
$summary = Join-Path $outDir "person-object-input-summary.md"

Assert-File "person object manifest" $manifest
Assert-File "person object input summary" $summary
Assert-File "external person object CSV" $externalCsv

$rows = @(Import-Csv $manifest)
if ($rows.Count -ne 3) {
    throw "Expected three manifest rows, actual=$($rows.Count)"
}

foreach ($column in @("frame", "frameWidth", "frameHeight", "scaleWidth", "coordinateSpace", "frameImagePath", "frameRelativePath")) {
    if ($null -eq $rows[0].PSObject.Properties[$column]) {
        throw "Missing manifest column: $column"
    }
}

if ($rows[0].coordinateSpace -ne "original-frame") {
    throw "Expected person/object manifest to keep original-frame coordinate space."
}

$externalRows = @(Import-Csv $externalCsv)
if ($externalRows.Count -ne 3) {
    throw "Expected three external person/object rows, actual=$($externalRows.Count)"
}

$scriptText = Get-Content -Raw -Path $script
$guideText = Get-Content -Raw -Path $guide
$summaryText = Get-Content -Raw -Path $summary

Assert-Contains "script supports frame ranges" $scriptText "Get-FrameList"
Assert-Contains "script limits large frame sets" $scriptText "MaxFrames"
Assert-Contains "script has explicit large frame override" $scriptText "AllowLargeFrameSet"
Assert-Contains "script writes person object manifest" $scriptText "person-object-manifest\.csv"
Assert-Contains "script extracts full frames" $scriptText "Invoke-FfmpegFrameExtraction"
Assert-Contains "script supports external command hook" $scriptText "ExternalCommand"
Assert-Contains "script requires external output csv" $scriptText "ExternalOutputCsv is required"
Assert-Contains "script validates external output against manifest" $scriptText "outside the manifest"
Assert-Contains "script validates external output against frame bounds" $scriptText "outside the manifest frame bounds"
Assert-Contains "script records runtime separation" $summaryText "not part of the app runtime path"
Assert-Contains "summary records auxiliary-only rule" $summaryText "auxiliary evidence only"
Assert-Contains "summary records frame bound rule" $summaryText "original-frame manifest bounds"
Assert-Contains "summary records frame count" $summaryText "frameCount=3"
Assert-Contains "summary records max frames" $summaryText "maxFrames=900"
Assert-Contains "summary records frame count" $summaryText "framesPrepared=3"
Assert-Contains "summary records external command" $summaryText "externalCommandUsed=True"
Assert-Contains "guide documents person object input" $guideText "new-yolo-pseudo-gt-person-object-input\.ps1"

Write-Host "[YoloPseudoGtPersonObjectInputVerify] all requested checks passed"
