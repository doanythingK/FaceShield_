param(
    [string]$VideoPath,
    [string]$TemplateCsv = ".tmp\yolo-full-gt\yolo-detection-smoke-template.csv",
    [string]$OutputDir = ".tmp\yolo-full-gt\review-package",
    [double]$PaddingRatio = 0.65,
    [int]$MaxRows = 0,
    [int]$FrameWidth = 0,
    [int]$FrameHeight = 0,
    [string]$FfmpegPath = "",
    [string]$FfprobePath = ""
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

function Resolve-RepoPath {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return ""
    }

    if ([IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return Join-Path $repo $Path
}

function Read-DoubleValue {
    param(
        [object]$Row,
        [string]$Name
    )

    $value = $Row.PSObject.Properties[$Name].Value
    if ([string]::IsNullOrWhiteSpace($value)) {
        return 0.0
    }

    return [double]::Parse($value, [System.Globalization.CultureInfo]::InvariantCulture)
}

function Read-IntValue {
    param(
        [object]$Row,
        [string]$Name
    )

    $value = $Row.PSObject.Properties[$Name].Value
    if ([string]::IsNullOrWhiteSpace($value)) {
        return 0
    }

    return [int]::Parse($value, [System.Globalization.CultureInfo]::InvariantCulture)
}

function Get-VideoSize {
    param([string]$Path)

    $ffprobe = Resolve-Tool -PreferredPath $FfprobePath -ToolName "ffprobe"
    if ($null -eq $ffprobe) {
        return $null
    }

    $sizeText = Invoke-Tool $ffprobe @("-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=s=x:p=0", $Path)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sizeText)) {
        return $null
    }

    $parts = $sizeText.Trim().Split("x")
    if ($parts.Count -ne 2) {
        return $null
    }

    [pscustomobject]@{
        Width = [int]$parts[0]
        Height = [int]$parts[1]
    }
}

function Get-CropRect {
    param(
        [double]$X,
        [double]$Y,
        [double]$W,
        [double]$H,
        [int]$ImageWidth,
        [int]$ImageHeight,
        [double]$Padding
    )

    $padX = $W * $Padding
    $padY = $H * $Padding
    $left = [Math]::Max(0, [Math]::Floor($X - $padX))
    $top = [Math]::Max(0, [Math]::Floor($Y - $padY))
    $right = [Math]::Min($ImageWidth, [Math]::Ceiling($X + $W + $padX))
    $bottom = [Math]::Min($ImageHeight, [Math]::Ceiling($Y + $H + $padY))
    $cropW = [Math]::Max(1, [int]($right - $left))
    $cropH = [Math]::Max(1, [int]($bottom - $top))

    [pscustomobject]@{
        X = [int]$left
        Y = [int]$top
        W = $cropW
        H = $cropH
    }
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

function Resolve-Tool {
    param(
        [string]$PreferredPath,
        [string]$ToolName
    )

    if (-not [string]::IsNullOrWhiteSpace($PreferredPath)) {
        $resolved = Resolve-RepoPath $PreferredPath
        if (-not (Test-Path $resolved)) {
            throw "$ToolName not found: $resolved"
        }

        return [pscustomobject]@{
            Command = $resolved
            ToolName = ""
            UseWsl = $false
        }
    }

    $native = Get-Command $ToolName -ErrorAction SilentlyContinue
    if ($null -ne $native) {
        return [pscustomobject]@{
            Command = $native.Source
            ToolName = ""
            UseWsl = $false
        }
    }

    $wsl = Get-Command wsl.exe -ErrorAction SilentlyContinue
    if ($null -ne $wsl) {
        return [pscustomobject]@{
            Command = $wsl.Source
            ToolName = $ToolName
            UseWsl = $true
        }
    }

    return $null
}

function Invoke-Tool {
    param(
        [object]$Tool,
        [string[]]$Arguments
    )

    if ($Tool.UseWsl) {
        $converted = @()
        foreach ($arg in $Arguments) {
            if ($arg -match '^[A-Za-z]:\\') {
                $converted += Convert-ToWslPath $arg
            }
            else {
                $converted += $arg
            }
        }

        return & $Tool.Command --exec $Tool.ToolName @converted
    }

    return & $Tool.Command @Arguments
}

if ([string]::IsNullOrWhiteSpace($VideoPath)) {
    throw "VideoPath is required."
}

$resolvedVideo = Resolve-RepoPath $VideoPath
$resolvedTemplate = Resolve-RepoPath $TemplateCsv
$resolvedOutputDir = Resolve-RepoPath $OutputDir

if (-not (Test-Path $resolvedVideo)) {
    throw "Video not found: $resolvedVideo"
}

if (-not (Test-Path $resolvedTemplate)) {
    throw "Template CSV not found: $resolvedTemplate"
}

$ffmpeg = Resolve-Tool -PreferredPath $FfmpegPath -ToolName "ffmpeg"
if ($null -eq $ffmpeg) {
    throw "ffmpeg not found on PATH and wsl.exe is unavailable. Pass -FfmpegPath explicitly."
}

if ($FrameWidth -le 0 -or $FrameHeight -le 0) {
    $size = Get-VideoSize $resolvedVideo
    if ($null -eq $size) {
        throw "Could not determine video size. Pass -FrameWidth and -FrameHeight."
    }

    $FrameWidth = $size.Width
    $FrameHeight = $size.Height
}

$rows = @(Import-Csv $resolvedTemplate)
if ($rows.Count -eq 0) {
    throw "Template CSV has no rows: $resolvedTemplate"
}

if ($MaxRows -gt 0) {
    $rows = @($rows | Select-Object -First $MaxRows)
}

New-Item -ItemType Directory -Force -Path $resolvedOutputDir | Out-Null
$cropDir = Join-Path $resolvedOutputDir "crops"
New-Item -ItemType Directory -Force -Path $cropDir | Out-Null

$reviewRows = New-Object System.Collections.Generic.List[object]
$index = 0
foreach ($row in $rows) {
    $frame = Read-IntValue $row "frame"
    $x = Read-DoubleValue $row "x"
    $y = Read-DoubleValue $row "y"
    $w = Read-DoubleValue $row "w"
    $h = Read-DoubleValue $row "h"
    $rect = Get-CropRect -X $x -Y $y -W $w -H $h -ImageWidth $FrameWidth -ImageHeight $FrameHeight -Padding $PaddingRatio
    $sourcePredictionId = $row.sourcePredictionId
    if ([string]::IsNullOrWhiteSpace($sourcePredictionId)) {
        $sourcePredictionId = $index.ToString([System.Globalization.CultureInfo]::InvariantCulture)
    }

    $cropFileName = "frame-{0:D6}-pred-{1}.png" -f $frame, ($sourcePredictionId -replace '[^A-Za-z0-9_.-]', '_')
    $cropPath = Join-Path $cropDir $cropFileName
    $filter = "select=eq(n\,$frame),crop=$($rect.W):$($rect.H):$($rect.X):$($rect.Y)"
    Invoke-Tool $ffmpeg @("-y", "-hide_banner", "-loglevel", "error", "-i", $resolvedVideo, "-vf", $filter, "-frames:v", "1", $cropPath) | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "ffmpeg crop failed for frame $frame with exit code $LASTEXITCODE"
    }

    $reviewRows.Add([pscustomobject]@{
        frame = $row.frame
        gtId = $row.gtId
        label = $row.label
        x = $row.x
        y = $row.y
        w = $row.w
        h = $row.h
        sourcePredictionId = $row.sourcePredictionId
        sourceConfidence = $row.sourceConfidence
        source = $row.source
        cropPath = $cropPath
        cropX = $rect.X
        cropY = $rect.Y
        cropW = $rect.W
        cropH = $rect.H
        reviewStatus = ""
        evidenceNotes = ""
        notes = $row.notes
    }) | Out-Null
    $index++
}

$reviewCsv = Join-Path $resolvedOutputDir "full-gt-review.csv"
$reviewRows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $reviewCsv

Write-Host "[YoloFullGtReviewPackage] wrote rows=$($reviewRows.Count), reviewCsv=$reviewCsv, cropDir=$cropDir"
Write-Host "[YoloFullGtReviewPackage] label instructions: set label=face/nonface and evidenceNotes after reviewing cropPath images; add rows for visible missed faces."
