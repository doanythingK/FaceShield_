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
$badTileExternalGenerator = Join-Path $work "bad-tile-external-generator.ps1"
$badTileExternalCsv = Join-Path $work "bad-tile-external-tile-face.csv"

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
        tileIndex = $first.tileIndex
        detectionId = "external-tile-face-$($first.frame)-$($first.tileIndex)"
        x = 0
        y = 0
        w = 60
        h = 70
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
        tileIndex = 0
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
        tileIndex = $first.tileIndex
        detectionId = "bad-outside-tile"
        x = 280
        y = 180
        w = 16
        h = 16
        confidence = 0.91
        tileSupportCount = 1
    }
) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $OutputCsv
'@ | Set-Content -Encoding UTF8 -Path $badTileExternalGenerator

$template = "-NoProfile -ExecutionPolicy Bypass -File `"$externalGenerator`" -ManifestCsv `"{manifest}`" -OutputCsv `"{output}`""
$output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script `
    -Frames "1,3-4" `
    -OutputDir $outDir `
    -TileColumns 3 `
    -TileRows 2 `
    -TileOverlapRatio 0.25 `
    -TileScale 2.5 `
    -FrameWidth 300 `
    -FrameHeight 200 `
    -SkipImageExtraction `
    -ExternalCommand "powershell.exe" `
    -ExternalArgumentsTemplate $template `
    -ExternalOutputCsv $externalCsv `
    -ExternalOutputCoordinateSpace TileImage 2>&1

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

$badTileTemplate = "-NoProfile -ExecutionPolicy Bypass -File `"$badTileExternalGenerator`" -ManifestCsv `"{manifest}`" -OutputCsv `"{output}`""
try {
    $ErrorActionPreference = "Continue"
    $badTileExternalOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script `
        -Frames "1" `
        -OutputDir (Join-Path $work "bad-tile-external") `
        -FrameWidth 300 `
        -FrameHeight 200 `
        -SkipImageExtraction `
        -ExternalCommand "powershell.exe" `
        -ExternalArgumentsTemplate $badTileTemplate `
        -ExternalOutputCsv $badTileExternalCsv 2>&1
    $badTileExternalExitCode = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $previousErrorActionPreference
}

if ($badTileExternalExitCode -eq 0 -or (($badTileExternalOutput | Out-String) -notmatch "outside the manifest tile")) {
    throw "Expected tile input to reject external CSV rows outside the manifest tile."
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

foreach ($column in @("frame", "tileIndex", "tileX", "tileY", "tileW", "tileH", "tileScale", "tileImageW", "tileImageH", "frameWidth", "frameHeight", "tileImagePath", "tileRelativePath")) {
    if ($null -eq $manifestRows[0].PSObject.Properties[$column]) {
        throw "Missing manifest column: $column"
    }
}

$firstManifestRow = $manifestRows[0]
if ([double]::Parse($firstManifestRow.tileScale, [System.Globalization.CultureInfo]::InvariantCulture) -ne 2.5) {
    throw "Expected manifest to record tileScale=2.5."
}

$tileW = [double]::Parse($firstManifestRow.tileW, [System.Globalization.CultureInfo]::InvariantCulture)
$tileH = [double]::Parse($firstManifestRow.tileH, [System.Globalization.CultureInfo]::InvariantCulture)
$tileImageW = [double]::Parse($firstManifestRow.tileImageW, [System.Globalization.CultureInfo]::InvariantCulture)
$tileImageH = [double]::Parse($firstManifestRow.tileImageH, [System.Globalization.CultureInfo]::InvariantCulture)
if ($tileImageW -le $tileW -or $tileImageH -le $tileH) {
    throw "Expected tile input manifest to describe enlarged tile images."
}

$externalRows = @(Import-Csv $externalCsv)
if ($externalRows.Count -ne 1) {
    throw "Expected one external output row, actual=$($externalRows.Count)"
}

$externalRow = $externalRows[0]
$normalizedX = [double]::Parse($externalRow.x, [System.Globalization.CultureInfo]::InvariantCulture)
$normalizedY = [double]::Parse($externalRow.y, [System.Globalization.CultureInfo]::InvariantCulture)
$normalizedW = [double]::Parse($externalRow.w, [System.Globalization.CultureInfo]::InvariantCulture)
$normalizedH = [double]::Parse($externalRow.h, [System.Globalization.CultureInfo]::InvariantCulture)
$expectedX = [double]::Parse($firstManifestRow.tileX, [System.Globalization.CultureInfo]::InvariantCulture)
$expectedY = [double]::Parse($firstManifestRow.tileY, [System.Globalization.CultureInfo]::InvariantCulture)
$expectedW = 60.0 * ($tileW / $tileImageW)
$expectedH = 70.0 * ($tileH / $tileImageH)
if ([Math]::Abs($normalizedX - $expectedX) -gt 0.001 -or
    [Math]::Abs($normalizedY - $expectedY) -gt 0.001 -or
    [Math]::Abs($normalizedW - $expectedW) -gt 0.001 -or
    [Math]::Abs($normalizedH - $expectedH) -gt 0.001) {
    throw "Expected TileImage external output to be normalized to original frame coordinates."
}

if ($externalRow.inputCoordinateSpace -ne "TileImage" -or $externalRow.normalizedCoordinateSpace -ne "Frame") {
    throw "Expected normalized external output to preserve coordinate-space audit columns."
}

$scriptText = Get-Content -Raw -Path $script
$guideText = Get-Content -Raw -Path $guide
$summaryText = Get-Content -Raw -Path $summary

Assert-Contains "script supports frame ranges" $scriptText "Get-FrameList"
Assert-Contains "script limits large frame sets" $scriptText "MaxFrames"
Assert-Contains "script has explicit large frame override" $scriptText "AllowLargeFrameSet"
Assert-Contains "script builds overlap tile starts" $scriptText "Get-TileStarts"
Assert-Contains "script supports tile scaling" $scriptText "TileScale"
Assert-Contains "script extracts enlarged tiles" $scriptText 'scale=\$\(\$row\.tileImageW\):\$\(\$row\.tileImageH\)'
Assert-Contains "script supports external output coordinate space" $scriptText "ExternalOutputCoordinateSpace"
Assert-Contains "script normalizes tile image coordinates" $scriptText "Convert-ExternalTileFaceCsvCoordinateSpace"
Assert-Contains "script writes manifest" $scriptText "tile-manifest\.csv"
Assert-Contains "script supports ffmpeg extraction" $scriptText "Invoke-FfmpegTileExtraction"
Assert-Contains "script supports wsl ffmpeg fallback" $scriptText "wsl\.exe"
Assert-Contains "script supports external command hook" $scriptText "ExternalCommand"
Assert-Contains "script requires external output csv" $scriptText "ExternalOutputCsv is required"
Assert-Contains "script defaults external timeout" $scriptText '\[int\]\$ExternalTimeoutSeconds\s*=\s*180'
Assert-Contains "script validates external output against manifest" $scriptText "outside the manifest"
Assert-Contains "script requires external tile index" $scriptText "tileIndex/sourceTileIndex/manifestTileIndex"
Assert-Contains "script validates external output against manifest tile" $scriptText "outside the manifest tile"
Assert-Contains "script records runtime separation" $summaryText "not part of the app runtime path"
Assert-Contains "summary records frame count" $summaryText "frameCount=3"
Assert-Contains "summary records max frames" $summaryText "maxFrames=900"
Assert-Contains "summary records tile scale" $summaryText "tileScale=2.5"
Assert-Contains "summary records tile count" $summaryText "tiles=18"
Assert-Contains "summary records external command" $summaryText "externalCommandUsed=True"
Assert-Contains "summary records external coordinate space" $summaryText "externalOutputCoordinateSpace=TileImage"
Assert-Contains "summary records tile output binding" $summaryText "tileIndex/sourceTileIndex/manifestTileIndex"
Assert-Contains "guide documents tile pseudo gt" $guideText "PseudoGtTileFaceCsv"

Write-Host "[YoloPseudoGtTileInputVerify] all requested checks passed"
