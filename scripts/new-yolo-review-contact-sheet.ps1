param(
    [Parameter(Mandatory = $true)]
    [string]$VideoPath,
    [Parameter(Mandatory = $true)]
    [string]$Frames,
    [string]$OutputPath = ".tmp\yolo-quality\yolo-review-contact-sheet.png",
    [int]$ScaleWidth = 360,
    [int]$Columns = 5,
    [int]$MaxFrames = 40,
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

$resolvedVideo = Resolve-RepoPath $VideoPath
$resolvedOutput = Resolve-RepoPath $OutputPath
$outputDir = Split-Path -Parent $resolvedOutput
if (-not [string]::IsNullOrWhiteSpace($outputDir)) {
    New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
}

if (-not (Test-Path $resolvedVideo)) {
    throw "Video not found: $resolvedVideo"
}

$frameNumbers = @($Frames -split "," |
    ForEach-Object { $_.Trim() } |
    Where-Object { $_ -match '^\d+$' } |
    ForEach-Object { [int]$_ } |
    Sort-Object -Unique |
    Select-Object -First $MaxFrames)

if ($frameNumbers.Count -eq 0) {
    throw "No frame numbers were provided for contact sheet generation."
}

$columnsToUse = [Math]::Max(1, $Columns)
$rowsToUse = [Math]::Max(1, [int][Math]::Ceiling($frameNumbers.Count / [double]$columnsToUse))
$selectExpr = @($frameNumbers | ForEach-Object { "eq(n\,$_)" }) -join "+"
$filters = @("select='$selectExpr'")
if ($ScaleWidth -gt 0) {
    $filters += "scale=$($ScaleWidth):-2"
}
$filters += "tile=${columnsToUse}x${rowsToUse}:padding=6:margin=6"
$filterGraph = $filters -join ","

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
    "-frames:v",
    "1",
    "-update",
    "1",
    $outputArg
) | Out-Null

if ($LASTEXITCODE -ne 0 -or -not (Test-Path $resolvedOutput)) {
    throw "Failed to write review contact sheet: $resolvedOutput"
}

$info = Get-Item $resolvedOutput
if ($info.Length -le 0) {
    throw "Review contact sheet is empty: $resolvedOutput"
}

Write-Host "[YoloReviewContactSheet] wrote path=$resolvedOutput, frames=$($frameNumbers -join ","), bytes=$($info.Length)"
