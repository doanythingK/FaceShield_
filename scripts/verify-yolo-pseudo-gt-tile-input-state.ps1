param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$script = Join-Path $repo "scripts\new-yolo-pseudo-gt-tile-input.ps1"
$guide = Join-Path $repo "YOLO_PROBLEM_SPAN_VERIFICATION.md"
$work = Join-Path $repo ".tmp\yolo-pseudo-gt-tile-input-verify"
$outDir = Join-Path $work "out"
$externalGenerator = Join-Path $work "external-generator.ps1"
$externalCsv = Join-Path $work "external-tile-face.csv"
$badExternalGenerator = Join-Path $work "bad-external-generator.ps1"
$badExternalCsv = Join-Path $work "bad-external-tile-face.csv"

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

    Write-Host "[YoloPseudoGtTileInputVerify] pass $Name"
}

Assert-File "pseudo-GT tile input script" $script
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

$first = $rows[0]
@(
    [pscustomobject]@{
        frame = $first.frame
        detectionId = "external-tile-face-$($first.frame)-$($first.tileIndex)"
        x = $first.tileX
        y = $first.tileY
        w = 24
        h = 28
        confidence = 0.91
        tileSupportCount = 1
    }
) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $OutputCsv
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
        x = 0
        y = 0
        w = 24
        h = 28
        confidence = 0.91
        tileSupportCount = 1
    }
) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $OutputCsv
'@ | Set-Content -Encoding UTF8 -Path $badExternalGenerator

$template = "-NoProfile -ExecutionPolicy Bypass -File `"$externalGenerator`" -ManifestCsv `"{manifest}`" -OutputCsv `"{output}`""
$output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script `
    -Frames "1,3-4" `
    -OutputDir $outDir `
    -TileColumns 3 `
    -TileRows 2 `
    -TileOverlapRatio 0.25 `
    -FrameWidth 300 `
    -FrameHeight 200 `
    -SkipImageExtraction `
    -ExternalCommand "powershell.exe" `
    -ExternalArgumentsTemplate $template `
    -ExternalOutputCsv $externalCsv 2>&1

if ($LASTEXITCODE -ne 0) {
    throw "new-yolo-pseudo-gt-tile-input.ps1 failed: $($output | Out-String)"
}

$previousErrorActionPreference = $ErrorActionPreference
try {
    $ErrorActionPreference = "Continue"
    $tooManyFramesOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script `
        -Frames "1-3" `
        -OutputDir (Join-Path $work "too-many") `
        -FrameWidth 300 `
        -FrameHeight 200 `
        -MaxFrames 2 `
        -SkipImageExtraction 2>&1
    $tooManyFramesExitCode = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $previousErrorActionPreference
}

if ($tooManyFramesExitCode -eq 0 -or (($tooManyFramesOutput | Out-String) -notmatch "limited to 2 frames")) {
    throw "Expected tile input to reject oversized pseudo-GT frame sets by default."
}

$badTemplate = "-NoProfile -ExecutionPolicy Bypass -File `"$badExternalGenerator`" -ManifestCsv `"{manifest}`" -OutputCsv `"{output}`""
try {
    $ErrorActionPreference = "Continue"
    $badExternalOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script `
        -Frames "1" `
        -OutputDir (Join-Path $work "bad-external") `
        -FrameWidth 300 `
        -FrameHeight 200 `
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
    throw "Expected tile input to reject external CSV rows outside the manifest."
}

$manifest = Join-Path $outDir "tile-manifest.csv"
$summary = Join-Path $outDir "tile-input-summary.md"

Assert-File "tile manifest" $manifest
Assert-File "tile input summary" $summary
Assert-File "external output CSV" $externalCsv

$manifestRows = @(Import-Csv $manifest)
if ($manifestRows.Count -ne 18) {
    throw "Expected 18 tile manifest rows, actual=$($manifestRows.Count)"
}

foreach ($column in @("frame", "tileIndex", "tileX", "tileY", "tileW", "tileH", "frameWidth", "frameHeight", "tileImagePath", "tileRelativePath")) {
    if ($null -eq $manifestRows[0].PSObject.Properties[$column]) {
        throw "Missing manifest column: $column"
    }
}

$externalRows = @(Import-Csv $externalCsv)
if ($externalRows.Count -ne 1) {
    throw "Expected one external output row, actual=$($externalRows.Count)"
}

$scriptText = Get-Content -Raw -Path $script
$guideText = Get-Content -Raw -Path $guide
$summaryText = Get-Content -Raw -Path $summary

Assert-Contains "script supports frame ranges" $scriptText "Get-FrameList"
Assert-Contains "script limits large frame sets" $scriptText "MaxFrames"
Assert-Contains "script has explicit large frame override" $scriptText "AllowLargeFrameSet"
Assert-Contains "script builds overlap tile starts" $scriptText "Get-TileStarts"
Assert-Contains "script writes manifest" $scriptText "tile-manifest\.csv"
Assert-Contains "script supports ffmpeg extraction" $scriptText "Invoke-FfmpegTileExtraction"
Assert-Contains "script supports wsl ffmpeg fallback" $scriptText "wsl\.exe"
Assert-Contains "script supports external command hook" $scriptText "ExternalCommand"
Assert-Contains "script requires external output csv" $scriptText "ExternalOutputCsv is required"
Assert-Contains "script validates external output against manifest" $scriptText "outside the manifest"
Assert-Contains "script records runtime separation" $summaryText "not part of the app runtime path"
Assert-Contains "summary records frame count" $summaryText "frameCount=3"
Assert-Contains "summary records max frames" $summaryText "maxFrames=900"
Assert-Contains "summary records tile count" $summaryText "tiles=18"
Assert-Contains "summary records external command" $summaryText "externalCommandUsed=True"
Assert-Contains "guide documents tile pseudo gt" $guideText "PseudoGtTileFaceCsv"

Write-Host "[YoloPseudoGtTileInputVerify] all requested checks passed"
