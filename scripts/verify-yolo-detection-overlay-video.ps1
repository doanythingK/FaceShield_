param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$script = Join-Path $repo "scripts\new-yolo-detection-overlay-video.ps1"
$video = Join-Path $repo ".tmp\srcTest-smoke\smoke-0900-2s.mp4"
$log = Join-Path $repo ".tmp\yolo-followup-current-0900-default\yolo-quality-2s-dump.log"
$output = Join-Path $repo ".tmp\yolo-followup-current-0900-expanded\yolo-detection-overlay.mp4"

function Assert-Contains {
    param(
        [string]$Name,
        [string]$Text,
        [string]$Expected
    )

    if (-not $Text.Contains($Expected)) {
        throw "$Name missing text: $Expected"
    }

    Write-Host "[YoloDetectionOverlayVideoVerify] pass $Name"
}

if (-not (Test-Path $script)) {
    throw "Overlay script not found: $script"
}

$scriptText = Get-Content -Raw -Path $script
Assert-Contains "script parses SmokeDetection rows" $scriptText "[SmokeDetection]"
Assert-Contains "script draws per-frame boxes" $scriptText "drawbox"
Assert-Contains "script labels confidence" $scriptText "drawtext"
Assert-Contains "script uses frame enable expression" $scriptText "eq(n"
Assert-Contains "script supports wsl ffmpeg fallback" $scriptText "wsl.exe"

if (-not (Test-Path $video)) {
    throw "Required sample video not found: $video"
}
if (-not (Test-Path $log)) {
    throw "Required prediction log not found: $log"
}

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script `
    -VideoPath $video `
    -PredictionLog $log `
    -OutputPath $output `
    -ScaleWidth 640
if ($LASTEXITCODE -ne 0) {
    throw "Overlay script failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path $output)) {
    throw "Overlay output not found: $output"
}

$info = Get-Item $output
if ($info.Length -le 0) {
    throw "Overlay output is empty: $output"
}

Write-Host "[YoloDetectionOverlayVideoVerify] pass output path=$output, bytes=$($info.Length)"
Write-Host "[YoloDetectionOverlayVideoVerify] all requested checks passed"
