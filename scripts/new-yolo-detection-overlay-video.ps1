param(
    [Parameter(Mandatory = $true)]
    [string]$VideoPath,
    [Parameter(Mandatory = $true)]
    [string]$PredictionLog,
    [string]$OutputPath = ".tmp\yolo-quality\yolo-detection-overlay.mp4",
    [int]$ScaleWidth = 960,
    [string]$BoxColor = "lime",
    [int]$BoxThickness = 4,
    [string]$FfmpegPath = ""
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

function Resolve-RepoPath {
    param([string]$Path)

    if ([IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return Join-Path $repo $Path
}

function Convert-ToWslPath {
    param([string]$Path)

    if ($Path -match '^([A-Za-z]):\\(.*)$') {
        $drive = $matches[1].ToLowerInvariant()
        $rest = $matches[2] -replace '\\', '/'
        return "/mnt/$drive/$rest"
    }

    return $Path -replace '\\', '/'
}

function Resolve-Ffmpeg {
    if (-not [string]::IsNullOrWhiteSpace($FfmpegPath)) {
        return [pscustomobject]@{
            Command = $FfmpegPath
            UseWsl = $false
        }
    }

    $ffmpeg = Get-Command ffmpeg -ErrorAction SilentlyContinue
    if ($null -ne $ffmpeg) {
        return [pscustomobject]@{
            Command = $ffmpeg.Source
            UseWsl = $false
        }
    }

    $wsl = Get-Command wsl.exe -ErrorAction SilentlyContinue
    if ($null -ne $wsl) {
        return [pscustomobject]@{
            Command = $wsl.Source
            UseWsl = $true
        }
    }

    throw "ffmpeg was not found in PATH and wsl.exe was not available."
}

function Invoke-Ffmpeg {
    param(
        [object]$Tool,
        [string[]]$Arguments
    )

    if ($Tool.UseWsl) {
        return & $Tool.Command @("--exec", "ffmpeg") @Arguments
    }

    return & $Tool.Command @Arguments
}

function Format-Number {
    param([double]$Value)

    return $Value.ToString("0.###", [System.Globalization.CultureInfo]::InvariantCulture)
}

function Escape-DrawText {
    param([string]$Text)

    return $Text.Replace("\", "\\").Replace(":", "\:").Replace("'", "\'")
}

$resolvedVideo = Resolve-RepoPath $VideoPath
$resolvedLog = Resolve-RepoPath $PredictionLog
$resolvedOutput = Resolve-RepoPath $OutputPath
$outputDir = Split-Path -Parent $resolvedOutput
if (-not [string]::IsNullOrWhiteSpace($outputDir)) {
    New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
}

if (-not (Test-Path $resolvedVideo)) {
    throw "Video not found: $resolvedVideo"
}
if (-not (Test-Path $resolvedLog)) {
    throw "Prediction log not found: $resolvedLog"
}

$pattern = '\[SmokeDetection\].*frame=(\d+), index=(\d+), x=([0-9.\-]+), y=([0-9.\-]+), w=([0-9.\-]+), h=([0-9.\-]+), area=[0-9.\-]+, conf=([0-9.\-]+)'
$detections = New-Object System.Collections.Generic.List[object]
foreach ($line in (Get-Content -Path $resolvedLog)) {
    $match = [regex]::Match($line, $pattern)
    if (-not $match.Success) {
        continue
    }

    $detections.Add([pscustomobject]@{
        Frame = [int]$match.Groups[1].Value
        Index = [int]$match.Groups[2].Value
        X = [double]::Parse($match.Groups[3].Value, [System.Globalization.CultureInfo]::InvariantCulture)
        Y = [double]::Parse($match.Groups[4].Value, [System.Globalization.CultureInfo]::InvariantCulture)
        W = [double]::Parse($match.Groups[5].Value, [System.Globalization.CultureInfo]::InvariantCulture)
        H = [double]::Parse($match.Groups[6].Value, [System.Globalization.CultureInfo]::InvariantCulture)
        Confidence = [double]::Parse($match.Groups[7].Value, [System.Globalization.CultureInfo]::InvariantCulture)
    }) | Out-Null
}

if ($detections.Count -eq 0) {
    throw "Prediction log contains no [SmokeDetection] rows: $resolvedLog"
}

$filters = New-Object System.Collections.Generic.List[string]
foreach ($row in $detections) {
    $enable = "eq(n\,$($row.Frame))"
    $label = Escape-DrawText ("f{0} p{1} c{2:0.00}" -f $row.Frame, $row.Index, $row.Confidence)
    $filters.Add(("drawbox=x={0}:y={1}:w={2}:h={3}:color={4}:t={5}:enable='{6}'" -f
            (Format-Number $row.X),
            (Format-Number $row.Y),
            (Format-Number $row.W),
            (Format-Number $row.H),
            $BoxColor,
            $BoxThickness,
            $enable)) | Out-Null
    $filters.Add(("drawtext=text='{0}':x={1}:y={2}:fontcolor={3}:fontsize=28:box=1:boxcolor=black@0.55:enable='{4}'" -f
            $label,
            (Format-Number $row.X),
            (Format-Number ([Math]::Max(0.0, $row.Y - 34.0))),
            $BoxColor,
            $enable)) | Out-Null
}

if ($ScaleWidth -gt 0) {
    $filters.Add("scale=$($ScaleWidth):-2") | Out-Null
}

$filterGraph = $filters.ToArray() -join ","
$tool = Resolve-Ffmpeg
$inputArg = if ($tool.UseWsl) { Convert-ToWslPath $resolvedVideo } else { $resolvedVideo }
$outputArg = if ($tool.UseWsl) { Convert-ToWslPath $resolvedOutput } else { $resolvedOutput }

Invoke-Ffmpeg $tool @(
    "-y",
    "-hide_banner",
    "-loglevel",
    "error",
    "-i",
    $inputArg,
    "-vf",
    $filterGraph,
    "-c:v",
    "libx264",
    "-preset",
    "veryfast",
    "-crf",
    "23",
    "-pix_fmt",
    "yuv420p",
    "-an",
    $outputArg
) | Out-Null

if ($LASTEXITCODE -ne 0 -or -not (Test-Path $resolvedOutput)) {
    throw "Failed to write detection overlay video: $resolvedOutput"
}

$info = Get-Item $resolvedOutput
if ($info.Length -le 0) {
    throw "Detection overlay video is empty: $resolvedOutput"
}

Write-Host "[YoloDetectionOverlayVideo] wrote path=$resolvedOutput, detections=$($detections.Count), bytes=$($info.Length)"
