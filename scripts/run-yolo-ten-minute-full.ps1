param(
    [string]$Source = "srcTest\260102_jp_10.mp4",
    [string]$Start = "00:02:00",
    [int]$Seconds = 600,
    [string]$Clip = ".tmp\srcTest-smoke\smoke-0200-600s.mp4",
    [string]$YoloModelPath = ".tmp\models\YoloV5Face.onnx",
    [string]$YoloModelType = "Yolo5Face",
    [int]$YoloInputSize = 640,
    [double]$YoloObjectnessThreshold = 0.12,
    [double]$YoloConfidenceThreshold = 0.18,
    [double]$YoloNmsThreshold = 0.45,
    [int]$ParallelDetectorCount = 2,
    [switch]$PrepareClipOnly,
    [switch]$SkipClipPrepare,
    [switch]$RunBaseline,
    [switch]$BaselineOnly,
    [switch]$SkipExport,
    [switch]$AllowQualityFailure,
    [string]$LogDir = ".tmp\yolo-ten-minute"
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$smoke = Join-Path $repo "scripts\run-srcTest-smoke.ps1"
$sourcePath = if ([IO.Path]::IsPathRooted($Source)) { $Source } else { Join-Path $repo $Source }
$clipPath = if ([IO.Path]::IsPathRooted($Clip)) { $Clip } else { Join-Path $repo $Clip }
$modelPath = if ([IO.Path]::IsPathRooted($YoloModelPath)) { $YoloModelPath } else { Join-Path $repo $YoloModelPath }
$logRoot = if ([IO.Path]::IsPathRooted($LogDir)) { $LogDir } else { Join-Path $repo $LogDir }

if (-not (Test-Path $smoke)) {
    throw "Smoke script not found: $smoke"
}

if (-not (Test-Path $sourcePath)) {
    throw "Source video not found: $sourcePath"
}

if (-not $BaselineOnly -and -not (Test-Path $modelPath)) {
    throw "YOLO model not found: $modelPath"
}

$clipDirectory = [IO.Path]::GetDirectoryName($clipPath)
if ([string]::IsNullOrWhiteSpace($clipDirectory)) {
    throw "Could not resolve clip directory from path: $clipPath"
}

New-Item -ItemType Directory -Force -Path $clipDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $logRoot | Out-Null

if (-not $SkipClipPrepare -and -not (Test-Path $clipPath)) {
    $ffmpeg = Get-Command ffmpeg -ErrorAction SilentlyContinue
    if ($null -eq $ffmpeg) {
        throw "ffmpeg not found on PATH. Prepare $clipPath manually or rerun with -SkipClipPrepare after creating the clip."
    }

    Write-Host "[YoloTenMinuteFull] prepare clip source=$sourcePath, start=$Start, seconds=$Seconds, output=$clipPath"
    & ffmpeg -y -hide_banner -loglevel error -ss $Start -t $Seconds -i $sourcePath -c copy $clipPath
    if ($LASTEXITCODE -ne 0) {
        throw "ffmpeg clip creation failed with exit code $LASTEXITCODE"
    }
}

if (-not (Test-Path $clipPath)) {
    throw "10-minute clip not found: $clipPath"
}

$clipInfo = Get-Item $clipPath
Write-Host "[YoloTenMinuteFull] clip ready path=$clipPath, bytes=$($clipInfo.Length)"

if ($PrepareClipOnly) {
    Write-Host "[YoloTenMinuteFull] prepare-only complete"
    exit 0
}

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$logPath = Join-Path $logRoot "yolo-ten-minute-$timestamp.log"

$arguments = @(
    "-SkipTrim",
    "-Source", $Clip,
    "-OptimizedCpuOnly",
    "-ParallelDetectorCount", "$ParallelDetectorCount"
)

if (-not $BaselineOnly) {
    $arguments += @(
    "-YoloModelPath", $YoloModelPath,
    "-YoloModelType", $YoloModelType,
    "-YoloInputSize", "$YoloInputSize",
    "-YoloObjectnessThreshold", "$YoloObjectnessThreshold",
    "-YoloConfidenceThreshold", "$YoloConfidenceThreshold",
    "-YoloNmsThreshold", "$YoloNmsThreshold"
    )
}

if (-not $RunBaseline -and -not $BaselineOnly) {
    $arguments += "-SkipBaseline"
}

if ($BaselineOnly) {
    $arguments += "-SkipOptimized"
}

if ($SkipExport) {
    $arguments += "-SkipExport"
}

$baselineEnabled = $RunBaseline.IsPresent -or $BaselineOnly.IsPresent
Write-Host "[YoloTenMinuteFull] start smoke log=$logPath, baseline=$baselineEnabled, baselineOnly=$($BaselineOnly.IsPresent), export=$(-not $SkipExport.IsPresent)"
$oldErrorAction = $ErrorActionPreference
try {
    $ErrorActionPreference = "Continue"
    if (Test-Path $logPath) {
        Remove-Item -Force -Path $logPath
    }

    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $smoke @arguments 2>&1 | ForEach-Object {
        $line = $_.ToString()
        Add-Content -Encoding UTF8 -Path $logPath -Value $line
        Write-Host $line
    }
    $exitCode = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $oldErrorAction
}

if ($exitCode -ne 0 -and -not $AllowQualityFailure) {
    throw "10-minute YOLO smoke failed with exit code $exitCode. Log: $logPath"
}

Write-Host "[YoloTenMinuteFull] complete exitCode=$exitCode, log=$logPath"
