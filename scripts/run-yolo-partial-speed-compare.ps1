param(
    [string]$Source = "srcTest\260102_jp_10.mp4",
    [string]$Start = "00:02:00",
    [int]$Seconds = 3,
    [string]$Clip = ".tmp\srcTest-smoke\smoke-0200-partial-speed-3s.mp4",
    [string]$YoloModelPath = ".tmp\models\YoloV5Face.onnx",
    [string]$YoloModelType = "Yolo5Face",
    [int]$YoloInputSize = 640,
    [double]$YoloObjectnessThreshold = 0.12,
    [double]$YoloConfidenceThreshold = 0.18,
    [double]$YoloNmsThreshold = 0.45,
    [int]$ParallelDetectorCount = 2,
    [string]$LogDir = ".tmp\yolo-partial-speed",
    [switch]$ForcePrepare
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$runner = Join-Path $repo "scripts\run-yolo-ten-minute-full.ps1"
$clipPath = if ([IO.Path]::IsPathRooted($Clip)) { $Clip } else { Join-Path $repo $Clip }
$logRoot = if ([IO.Path]::IsPathRooted($LogDir)) { $LogDir } else { Join-Path $repo $LogDir }
$yoloLogDir = Join-Path $logRoot "yolo"
$faceOnnxLogDir = Join-Path $logRoot "faceonnx-optimized"

if (-not (Test-Path $runner)) {
    throw "Runner not found: $runner"
}

if ($ForcePrepare -and (Test-Path $clipPath)) {
    Remove-Item -Force -Path $clipPath
}

New-Item -ItemType Directory -Force -Path $logRoot | Out-Null
New-Item -ItemType Directory -Force -Path $yoloLogDir | Out-Null
New-Item -ItemType Directory -Force -Path $faceOnnxLogDir | Out-Null

function Invoke-Step {
    param(
        [string]$Name,
        [string[]]$Arguments
    )

    Write-Host "[YoloPartialSpeedCompare] start $Name"
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE"
    }
}

function Resolve-LatestLog {
    param(
        [string]$Directory,
        [string]$Pattern
    )

    $latest = Get-ChildItem -Path $Directory -Filter $Pattern -File |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($null -eq $latest) {
        throw "No log found in $Directory with pattern $Pattern"
    }

    return $latest.FullName
}

function Read-AutoSummary {
    param(
        [string]$Path,
        [string]$ExpectedDetector
    )

    $text = Get-Content -Raw -Path $Path
    $lines = @($text -split "`r?`n" | Where-Object {
        $_.Contains("[AutoRunSummary]") -and $_.Contains("detector=$ExpectedDetector")
    })

    if ($lines.Count -eq 0) {
        throw ("Log missing AutoRunSummary for detector={0}: {1}" -f $ExpectedDetector, $Path)
    }

    $line = $lines[-1]
    $totalFrames = [regex]::Match($line, "totalFrames=(\d+)")
    $processed = [regex]::Match($line, "processed=(\d+)")
    $detects = [regex]::Match($line, "detects=(\d+)")
    $totalMs = [regex]::Match($line, "totalMs=(\d+)")

    foreach ($match in @($totalFrames, $processed, $detects, $totalMs)) {
        if (-not $match.Success) {
            throw "AutoRunSummary missing required metric in line: $line"
        }
    }

    [pscustomobject]@{
        Detector = $ExpectedDetector
        TotalFrames = [int]$totalFrames.Groups[1].Value
        Processed = [int]$processed.Groups[1].Value
        Detects = [int]$detects.Groups[1].Value
        TotalMs = [int]$totalMs.Groups[1].Value
        LogPath = $Path
    }
}

Invoke-Step "prepare-partial-clip" @(
    "-Source", $Source,
    "-Start", $Start,
    "-Seconds", "$Seconds",
    "-Clip", $Clip,
    "-PrepareClipOnly",
    "-LogDir", $logRoot
)

Invoke-Step "yolo-optimized-only" @(
    "-Source", $Clip,
    "-Clip", $Clip,
    "-SkipClipPrepare",
    "-SkipExport",
    "-YoloModelPath", $YoloModelPath,
    "-YoloModelType", $YoloModelType,
    "-YoloInputSize", "$YoloInputSize",
    "-YoloObjectnessThreshold", "$YoloObjectnessThreshold",
    "-YoloConfidenceThreshold", "$YoloConfidenceThreshold",
    "-YoloNmsThreshold", "$YoloNmsThreshold",
    "-ParallelDetectorCount", "$ParallelDetectorCount",
    "-LogDir", $yoloLogDir
)

Invoke-Step "faceonnx-optimized-only" @(
    "-Source", $Clip,
    "-Clip", $Clip,
    "-SkipClipPrepare",
    "-SkipExport",
    "-FaceOnnxOptimizedOnly",
    "-ParallelDetectorCount", "$ParallelDetectorCount",
    "-LogDir", $faceOnnxLogDir
)

$yoloLog = Resolve-LatestLog -Directory $yoloLogDir -Pattern "yolo-ten-minute-yolo-only-*.log"
$faceOnnxLog = Resolve-LatestLog -Directory $faceOnnxLogDir -Pattern "yolo-ten-minute-faceonnx-optimized-only-*.log"
$yolo = Read-AutoSummary -Path $yoloLog -ExpectedDetector "YoloFaceOnnxDetector"
$faceOnnx = Read-AutoSummary -Path $faceOnnxLog -ExpectedDetector "FaceOnnxDetector/CPU"

$ratio = if ($yolo.TotalMs -gt 0) {
    [Math]::Round($faceOnnx.TotalMs / [double]$yolo.TotalMs, 3)
} else {
    0.0
}

Write-Host "[YoloPartialSpeedCompare] summary clip=$clipPath, seconds=$Seconds, yoloTotalMs=$($yolo.TotalMs), faceOnnxOptimizedTotalMs=$($faceOnnx.TotalMs), faceOnnxVsYoloRatio=$ratio, yoloFrames=$($yolo.TotalFrames), faceOnnxFrames=$($faceOnnx.TotalFrames), yoloLog=$($yolo.LogPath), faceOnnxLog=$($faceOnnx.LogPath)"
