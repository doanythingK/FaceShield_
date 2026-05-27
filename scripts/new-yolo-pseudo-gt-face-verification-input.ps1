param(
    [string]$VideoPath = "",
    [string]$BasePredictionCsv = "",
    [string]$BasePredictionLog = "",
    [string]$Frames = "",
    [string]$OutputDir = ".tmp\yolo-pseudo-gt\face-verification-input",
    [double]$CropPaddingRatio = 0.35,
    [int]$MinCropSize = 32,
    [int]$MaxFrames = 900,
    [switch]$AllowLargeFrameSet,
    [int]$FrameWidth = 0,
    [int]$FrameHeight = 0,
    [int]$MaxCrops = 0,
    [switch]$SkipImageExtraction,
    [string]$ExternalCommand = "",
    [string]$ExternalArgumentsTemplate = "",
    [string]$ExternalOutputCsv = "",
    [int]$ExternalTimeoutSeconds = 0
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

function Convert-ToWslPath {
    param([string]$Path)

    if ($Path -match '^([A-Za-z]):\\(.*)$') {
        $drive = $matches[1].ToLowerInvariant()
        $rest = $matches[2] -replace '\\', '/'
        return "/mnt/$drive/$rest"
    }

    return $Path -replace '\\', '/'
}

function Invoke-NativeCapture {
    param(
        [string]$FilePath,
        [string[]]$Arguments,
        [int]$TimeoutSeconds = 0
    )

    $stdoutPath = [IO.Path]::GetTempFileName()
    $stderrPath = [IO.Path]::GetTempFileName()

    try {
        $process = Start-Process `
            -FilePath $FilePath `
            -ArgumentList $Arguments `
            -NoNewWindow `
            -PassThru `
            -RedirectStandardOutput $stdoutPath `
            -RedirectStandardError $stderrPath

        if ($TimeoutSeconds -gt 0) {
            if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
                try {
                    $process.Kill()
                }
                catch {
                    Write-Warning "Failed to kill timed out process: $($_.Exception.Message)"
                }

                $stdout = if (Test-Path $stdoutPath) { Get-Content -Path $stdoutPath } else { @() }
                $stderr = if (Test-Path $stderrPath) { Get-Content -Path $stderrPath } else { @() }
                return [pscustomobject]@{
                    ExitCode = -1
                    Output = @($stdout + $stderr + "Timed out after $TimeoutSeconds seconds.")
                }
            }
        }
        else {
            $process.WaitForExit()
        }

        $stdoutText = if (Test-Path $stdoutPath) { Get-Content -Path $stdoutPath } else { @() }
        $stderrText = if (Test-Path $stderrPath) { Get-Content -Path $stderrPath } else { @() }
        $process.Refresh()
        $exitCode = if ($null -eq $process.ExitCode) { 0 } else { $process.ExitCode }

        return [pscustomobject]@{
            ExitCode = $exitCode
            Output = @($stdoutText + $stderrText)
        }
    }
    finally {
        Remove-Item -Force -ErrorAction SilentlyContinue -Path $stdoutPath, $stderrPath
    }
}

function Get-PropertyValue {
    param(
        [object]$Row,
        [string[]]$Names,
        [object]$Default = ""
    )

    foreach ($name in $Names) {
        $property = $Row.PSObject.Properties[$name]
        if ($null -ne $property -and -not [string]::IsNullOrWhiteSpace([string]$property.Value)) {
            return $property.Value
        }
    }

    return $Default
}

function Read-DoubleValue {
    param(
        [object]$Row,
        [string[]]$Names,
        [double]$Default = 0.0
    )

    $value = Get-PropertyValue $Row $Names ""
    if ([string]::IsNullOrWhiteSpace([string]$value)) {
        return $Default
    }

    $parsed = 0.0
    if ([double]::TryParse(
            [string]$value,
            [System.Globalization.NumberStyles]::Float,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [ref]$parsed)) {
        return $parsed
    }

    return $Default
}

function Read-IntValue {
    param(
        [object]$Row,
        [string[]]$Names,
        [int]$Default = 0
    )

    $value = Get-PropertyValue $Row $Names ""
    if ([string]::IsNullOrWhiteSpace([string]$value)) {
        return $Default
    }

    $parsed = 0
    if ([int]::TryParse([string]$value, [ref]$parsed)) {
        return $parsed
    }

    return $Default
}

function Get-FrameSet {
    param([string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return $null
    }

    $values = [System.Collections.Generic.HashSet[int]]::new()
    foreach ($token in @($Text -split ",")) {
        $trimmed = $token.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed)) {
            continue
        }

        if ($trimmed -match '^(\d+)-(\d+)$') {
            $start = [int]$Matches[1]
            $end = [int]$Matches[2]
            if ($end -lt $start) {
                throw "Invalid frame range: $trimmed"
            }

            for ($frame = $start; $frame -le $end; $frame++) {
                [void]$values.Add($frame)
            }
        }
        elseif ($trimmed -match '^\d+$') {
            [void]$values.Add([int]$trimmed)
        }
        else {
            throw "Invalid frame token: $trimmed"
        }
    }

    return $values
}

function New-BaseRow {
    param(
        [int]$Frame,
        [string]$Id,
        [double]$X,
        [double]$Y,
        [double]$W,
        [double]$H,
        [double]$Confidence
    )

    [pscustomobject]@{
        Frame = $Frame
        Id = $Id
        X = $X
        Y = $Y
        W = $W
        H = $H
        Confidence = $Confidence
    }
}

function Read-BasePredictionCsvRows {
    param([string]$Path)

    $resolved = Resolve-RepoPath $Path
    if (-not (Test-Path $resolved)) {
        throw "Base prediction CSV not found: $resolved"
    }

    $rows = [System.Collections.Generic.List[object]]::new()
    $index = 0
    foreach ($row in (Import-Csv $resolved)) {
        $frame = Read-IntValue $row @("frame", "Frame")
        $id = [string](Get-PropertyValue $row @("predictionId", "detectionId", "id", "Id") "")
        if ([string]::IsNullOrWhiteSpace($id)) {
            $id = "$frame-$index"
        }

        $rows.Add((New-BaseRow `
                    -Frame $frame `
                    -Id $id `
                    -X (Read-DoubleValue $row @("x", "X")) `
                    -Y (Read-DoubleValue $row @("y", "Y")) `
                    -W (Read-DoubleValue $row @("w", "W", "width", "Width")) `
                    -H (Read-DoubleValue $row @("h", "H", "height", "Height")) `
                    -Confidence (Read-DoubleValue $row @("confidence", "conf", "baseFaceConfidence", "Confidence")))) | Out-Null
        $index++
    }

    return @($rows)
}

function Read-BasePredictionLogRows {
    param([string]$Path)

    $resolved = Resolve-RepoPath $Path
    if (-not (Test-Path $resolved)) {
        throw "Base prediction log not found: $resolved"
    }

    $rows = [System.Collections.Generic.List[object]]::new()
    $pattern = '\[SmokeDetection\].*frame=(\d+), index=(\d+), x=([0-9.\-]+), y=([0-9.\-]+), w=([0-9.\-]+), h=([0-9.\-]+), area=[0-9.\-]+, conf=([0-9.\-]+)'
    foreach ($line in (Get-Content -Path $resolved)) {
        $match = [regex]::Match($line, $pattern)
        if (-not $match.Success) {
            continue
        }

        $frame = [int]$match.Groups[1].Value
        $index = $match.Groups[2].Value
        $rows.Add((New-BaseRow `
                    -Frame $frame `
                    -Id "$frame-$index" `
                    -X ([double]::Parse($match.Groups[3].Value, [System.Globalization.CultureInfo]::InvariantCulture)) `
                    -Y ([double]::Parse($match.Groups[4].Value, [System.Globalization.CultureInfo]::InvariantCulture)) `
                    -W ([double]::Parse($match.Groups[5].Value, [System.Globalization.CultureInfo]::InvariantCulture)) `
                    -H ([double]::Parse($match.Groups[6].Value, [System.Globalization.CultureInfo]::InvariantCulture)) `
                    -Confidence ([double]::Parse($match.Groups[7].Value, [System.Globalization.CultureInfo]::InvariantCulture)))) | Out-Null
    }

    return @($rows)
}

function Get-VideoFrameSize {
    param([string]$SourceVideo)

    if ([string]::IsNullOrWhiteSpace($SourceVideo) -or -not (Test-Path $SourceVideo)) {
        return $null
    }

    $ffprobe = Get-Command ffprobe -ErrorAction SilentlyContinue
    $wsl = Get-Command wsl.exe -ErrorAction SilentlyContinue
    $arguments = @(
        "-v",
        "error",
        "-select_streams",
        "v:0",
        "-show_entries",
        "stream=width,height",
        "-of",
        "csv=s=x:p=0",
        $SourceVideo
    )

    if ($null -ne $ffprobe) {
        $result = Invoke-NativeCapture -FilePath $ffprobe.Source -Arguments $arguments
        if ($result.ExitCode -eq 0 -and ($result.Output -join "`n") -match '(\d+)x(\d+)') {
            return [pscustomobject]@{ Width = [int]$Matches[1]; Height = [int]$Matches[2] }
        }
    }

    if ($null -ne $wsl) {
        $wslArguments = @(
            "--exec",
            "ffprobe",
            "-v",
            "error",
            "-select_streams",
            "v:0",
            "-show_entries",
            "stream=width,height",
            "-of",
            "csv=s=x:p=0",
            (Convert-ToWslPath $SourceVideo)
        )
        $result = Invoke-NativeCapture -FilePath $wsl.Source -Arguments $wslArguments
        if ($result.ExitCode -eq 0 -and ($result.Output -join "`n") -match '(\d+)x(\d+)') {
            return [pscustomobject]@{ Width = [int]$Matches[1]; Height = [int]$Matches[2] }
        }
    }

    return $null
}

function Get-RelativePathCompat {
    param(
        [string]$BasePath,
        [string]$TargetPath
    )

    $baseUriPath = if ($BasePath.EndsWith([IO.Path]::DirectorySeparatorChar)) { $BasePath } else { $BasePath + [IO.Path]::DirectorySeparatorChar }
    $baseUri = [Uri]::new($baseUriPath)
    $targetUri = [Uri]::new($TargetPath)
    return [Uri]::UnescapeDataString($baseUri.MakeRelativeUri($targetUri).ToString()).Replace('/', [IO.Path]::DirectorySeparatorChar)
}

function Split-ArgumentString {
    param([string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return @()
    }

    $matches = [regex]::Matches($Text, '"([^"]*)"|''([^'']*)''|(\S+)')
    $args = [System.Collections.Generic.List[string]]::new()
    foreach ($match in $matches) {
        if ($match.Groups[1].Success) {
            $args.Add($match.Groups[1].Value) | Out-Null
        }
        elseif ($match.Groups[2].Success) {
            $args.Add($match.Groups[2].Value) | Out-Null
        }
        elseif ($match.Groups[3].Success) {
            $args.Add($match.Groups[3].Value) | Out-Null
        }
    }

    return @($args)
}

function Get-RequiredCsvValue {
    param(
        [object]$Row,
        [string[]]$Names,
        [string]$Source,
        [int]$Index
    )

    foreach ($name in $Names) {
        $property = $Row.PSObject.Properties[$name]
        if ($null -ne $property -and -not [string]::IsNullOrWhiteSpace([string]$property.Value)) {
            return $property.Value
        }
    }

    throw "$Source CSV row $Index missing required value: $([string]::Join('/', $Names))"
}

function Read-RequiredDoubleValue {
    param(
        [object]$Row,
        [string[]]$Names,
        [string]$Source,
        [int]$Index
    )

    $value = Get-RequiredCsvValue $Row $Names $Source $Index
    $parsed = 0.0
    if ([double]::TryParse(
            [string]$value,
            [System.Globalization.NumberStyles]::Float,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [ref]$parsed)) {
        return $parsed
    }

    throw "$Source CSV row $Index has invalid numeric value for $([string]::Join('/', $Names)): $value"
}

function Read-RequiredIntValue {
    param(
        [object]$Row,
        [string[]]$Names,
        [string]$Source,
        [int]$Index
    )

    $value = Get-RequiredCsvValue $Row $Names $Source $Index
    $parsed = 0
    if ([int]::TryParse([string]$value, [ref]$parsed)) {
        return $parsed
    }

    throw "$Source CSV row $Index has invalid integer value for $([string]::Join('/', $Names)): $value"
}

function Assert-ExternalFaceVerificationCsv {
    param(
        [string]$Path,
        [object[]]$ManifestRows
    )

    $manifestFrames = [System.Collections.Generic.HashSet[int]]::new()
    foreach ($row in $ManifestRows) {
        [void]$manifestFrames.Add([int]$row.frame)
    }

    $rows = @(Import-Csv $Path)
    $index = 0
    foreach ($row in $rows) {
        $frame = Read-RequiredIntValue $row @("frame", "Frame") "face-verification" $index
        if (-not $manifestFrames.Contains($frame)) {
            throw "face-verification CSV row $index references frame $frame outside the manifest."
        }

        $width = Read-RequiredDoubleValue $row @("w", "W", "width", "Width") "face-verification" $index
        $height = Read-RequiredDoubleValue $row @("h", "H", "height", "Height") "face-verification" $index
        if ($width -le 0 -or $height -le 0) {
            throw "face-verification CSV row $index has non-positive geometry: w=$width, h=$height"
        }

        [void](Read-RequiredDoubleValue $row @("x", "X") "face-verification" $index)
        [void](Read-RequiredDoubleValue $row @("y", "Y") "face-verification" $index)
        [void](Read-RequiredDoubleValue $row @("faceVerificationConfidence", "confidence", "conf", "Confidence") "face-verification" $index)
        [void](Read-RequiredDoubleValue $row @("faceVerificationDistance", "verificationDistance", "distance", "FaceVerificationDistance") "face-verification" $index)
        $index++
    }
}

function Invoke-FfmpegCropExtraction {
    param(
        [string]$SourceVideo,
        [object[]]$Rows
    )

    if ([string]::IsNullOrWhiteSpace($SourceVideo)) {
        throw "VideoPath is required unless -SkipImageExtraction is used."
    }

    if (-not (Test-Path $SourceVideo)) {
        throw "VideoPath not found: $SourceVideo"
    }

    $ffmpeg = Get-Command ffmpeg -ErrorAction SilentlyContinue
    $wsl = Get-Command wsl.exe -ErrorAction SilentlyContinue

    if ($null -eq $ffmpeg -and $null -eq $wsl) {
        throw "ffmpeg was not found in Windows PATH and wsl.exe was not available."
    }

    foreach ($row in $Rows) {
        $filter = "select='eq(n\,$($row.frame))',crop=$($row.cropW):$($row.cropH):$($row.cropX):$($row.cropY)"
        $args = @(
            "-y",
            "-hide_banner",
            "-loglevel",
            "error",
            "-i",
            $SourceVideo,
            "-vf",
            $filter,
            "-frames:v",
            "1",
            $row.cropImagePath
        )

        if ($null -ne $ffmpeg) {
            $result = Invoke-NativeCapture -FilePath $ffmpeg.Source -Arguments $args
            if ($result.ExitCode -eq 0) {
                continue
            }

            if ($null -eq $wsl) {
                throw "ffmpeg face verification crop extraction failed: $($result.Output -join ' ')"
            }
        }

        $wslArgs = @(
            "--exec",
            "ffmpeg",
            "-y",
            "-hide_banner",
            "-loglevel",
            "error",
            "-i",
            (Convert-ToWslPath $SourceVideo),
            "-vf",
            $filter,
            "-frames:v",
            "1",
            (Convert-ToWslPath $row.cropImagePath)
        )
        $wslResult = Invoke-NativeCapture -FilePath $wsl.Source -Arguments $wslArgs
        if ($wslResult.ExitCode -ne 0) {
            throw "WSL ffmpeg face verification crop extraction failed: $($wslResult.Output -join ' ')"
        }
    }
}

function Invoke-ExternalModel {
    param(
        [string]$Command,
        [string]$ArgumentsTemplate,
        [string]$ManifestPath,
        [string]$CropsDir,
        [string]$OutputPath,
        [string]$Video,
        [int]$TimeoutSeconds
    )

    if ([string]::IsNullOrWhiteSpace($Command)) {
        return
    }

    if ([string]::IsNullOrWhiteSpace($OutputPath)) {
        throw "ExternalOutputCsv is required when ExternalCommand is used."
    }

    $resolvedCommand = Resolve-RepoPath $Command
    if (-not (Test-Path $resolvedCommand)) {
        $commandInfo = Get-Command $Command -ErrorAction SilentlyContinue
        if ($null -eq $commandInfo) {
            throw "ExternalCommand not found: $Command"
        }

        $resolvedCommand = $commandInfo.Source
    }

    $template = if ([string]::IsNullOrWhiteSpace($ArgumentsTemplate)) {
        "-ManifestCsv `"{manifest}`" -OutputCsv `"{output}`""
    }
    else {
        $ArgumentsTemplate
    }

    $argumentText = $template.
        Replace("{manifest}", $ManifestPath).
        Replace("{cropsDir}", $CropsDir).
        Replace("{output}", $OutputPath).
        Replace("{video}", $Video)

    $args = Split-ArgumentString $argumentText
    $result = Invoke-NativeCapture -FilePath $resolvedCommand -Arguments $args -TimeoutSeconds $TimeoutSeconds
    if ($result.ExitCode -ne 0) {
        throw "External high-quality face verification command failed with exit code $($result.ExitCode): $($result.Output -join ' ')"
    }

    if (-not (Test-Path $OutputPath)) {
        throw "External high-quality face verification command did not create output CSV: $OutputPath"
    }

    Assert-ExternalFaceVerificationCsv -Path $OutputPath -ManifestRows @(Import-Csv $ManifestPath)
}

function Format-Double {
    param([double]$Value)
    return $Value.ToString("0.######", [System.Globalization.CultureInfo]::InvariantCulture)
}

if ($CropPaddingRatio -lt 0.0 -or $CropPaddingRatio -gt 2.0) {
    throw "CropPaddingRatio must be between 0.0 and 2.0."
}

if ($MinCropSize -lt 1) {
    throw "MinCropSize must be at least 1."
}

if ($MaxFrames -lt 1) {
    throw "MaxFrames must be at least 1."
}

if ([string]::IsNullOrWhiteSpace($BasePredictionCsv) -and [string]::IsNullOrWhiteSpace($BasePredictionLog)) {
    throw "BasePredictionCsv or BasePredictionLog is required."
}

$resolvedOutputDir = Resolve-RepoPath $OutputDir
$cropsDir = Join-Path $resolvedOutputDir "crops"
$manifestPath = Join-Path $resolvedOutputDir "face-verification-manifest.csv"
$summaryPath = Join-Path $resolvedOutputDir "face-verification-input-summary.md"
$resolvedVideoPath = Resolve-RepoPath $VideoPath
$resolvedExternalOutputCsv = Resolve-RepoPath $ExternalOutputCsv

if ($FrameWidth -lt 1 -or $FrameHeight -lt 1) {
    $frameSize = Get-VideoFrameSize -SourceVideo $resolvedVideoPath
    if ($null -eq $frameSize) {
        throw "FrameWidth and FrameHeight must be provided when video dimensions cannot be read."
    }

    $FrameWidth = $frameSize.Width
    $FrameHeight = $frameSize.Height
}

$baseRows = if (-not [string]::IsNullOrWhiteSpace($BasePredictionCsv)) {
    @(Read-BasePredictionCsvRows $BasePredictionCsv)
}
else {
    @(Read-BasePredictionLogRows $BasePredictionLog)
}

$frameSet = Get-FrameSet $Frames
if ($null -ne $frameSet) {
    $baseRows = @($baseRows | Where-Object { $frameSet.Contains([int]$_.Frame) })
}

$distinctFrameCount = @($baseRows | Select-Object -ExpandProperty Frame -Unique).Count
if (-not $AllowLargeFrameSet.IsPresent -and $distinctFrameCount -gt $MaxFrames) {
    throw "Pseudo-GT face verification input is limited to $MaxFrames frames by default. Use a <=30s problem span, a smaller frame list, or pass -AllowLargeFrameSet only for an intentional local audit."
}

if ($MaxCrops -gt 0) {
    $baseRows = @($baseRows | Sort-Object Frame, Id | Select-Object -First $MaxCrops)
}

if ($baseRows.Count -eq 0) {
    throw "No base YOLO rows were found for face verification input."
}

New-Item -ItemType Directory -Force -Path $resolvedOutputDir | Out-Null
New-Item -ItemType Directory -Force -Path $cropsDir | Out-Null

$manifestRows = [System.Collections.Generic.List[object]]::new()
foreach ($row in @($baseRows | Sort-Object Frame, Id)) {
    $padX = $row.W * $CropPaddingRatio
    $padY = $row.H * $CropPaddingRatio
    $cropX = [int][Math]::Floor($row.X - $padX)
    $cropY = [int][Math]::Floor($row.Y - $padY)
    $cropRight = [int][Math]::Ceiling($row.X + $row.W + $padX)
    $cropBottom = [int][Math]::Ceiling($row.Y + $row.H + $padY)

    $cropX = [Math]::Max(0, $cropX)
    $cropY = [Math]::Max(0, $cropY)
    $cropRight = [Math]::Min($FrameWidth, [Math]::Max($cropRight, $cropX + $MinCropSize))
    $cropBottom = [Math]::Min($FrameHeight, [Math]::Max($cropBottom, $cropY + $MinCropSize))
    if ($cropRight -le $cropX) {
        $cropX = [Math]::Max(0, [Math]::Min($FrameWidth - 1, $cropX))
        $cropRight = [Math]::Min($FrameWidth, $cropX + 1)
    }
    if ($cropBottom -le $cropY) {
        $cropY = [Math]::Max(0, [Math]::Min($FrameHeight - 1, $cropY))
        $cropBottom = [Math]::Min($FrameHeight, $cropY + 1)
    }

    $cropW = [Math]::Max(1, $cropRight - $cropX)
    $cropH = [Math]::Max(1, $cropBottom - $cropY)
    $safeId = ([string]$row.Id) -replace '[^0-9A-Za-z_.-]+', '-'
    $cropImagePath = Join-Path $cropsDir ("frame-{0:D06}-candidate-{1}.png" -f $row.Frame, $safeId)

    $manifestRows.Add([pscustomobject]@{
            frame = $row.Frame
            candidateId = "base-$($row.Frame)-$($row.Id)"
            basePredictionId = $row.Id
            x = Format-Double $row.X
            y = Format-Double $row.Y
            w = Format-Double $row.W
            h = Format-Double $row.H
            baseFaceConfidence = Format-Double $row.Confidence
            cropX = $cropX
            cropY = $cropY
            cropW = $cropW
            cropH = $cropH
            frameWidth = $FrameWidth
            frameHeight = $FrameHeight
            cropImagePath = $cropImagePath
            cropRelativePath = Get-RelativePathCompat $resolvedOutputDir $cropImagePath
        }) | Out-Null
}

$manifestRows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $manifestPath

if (-not $SkipImageExtraction.IsPresent) {
    Invoke-FfmpegCropExtraction -SourceVideo $resolvedVideoPath -Rows @($manifestRows)
}

if (-not [string]::IsNullOrWhiteSpace($ExternalCommand)) {
    Invoke-ExternalModel `
        -Command $ExternalCommand `
        -ArgumentsTemplate $ExternalArgumentsTemplate `
        -ManifestPath $manifestPath `
        -CropsDir $cropsDir `
        -OutputPath $resolvedExternalOutputCsv `
        -Video $resolvedVideoPath `
        -TimeoutSeconds $ExternalTimeoutSeconds
}

$summary = @(
    "# YOLO Pseudo-GT Face Verification Input",
    "",
    "This is test-only input for a local high-quality face verification/face detection model. It is not part of the app runtime path.",
    "",
    "- baseRows=$($baseRows.Count)",
    "- crops=$($manifestRows.Count)",
    "- frames=$Frames",
    "- frameCount=$distinctFrameCount",
    "- maxFrames=$MaxFrames",
    "- largeFrameSetAllowed=$($AllowLargeFrameSet.IsPresent)",
    "- cropPaddingRatio=$($CropPaddingRatio.ToString('0.###', [System.Globalization.CultureInfo]::InvariantCulture))",
    "- minCropSize=$MinCropSize",
    "- frameWidth=$FrameWidth",
    "- frameHeight=$FrameHeight",
    "- manifest=$manifestPath",
    "- imageExtractionSkipped=$($SkipImageExtraction.IsPresent)",
    "- externalCommandUsed=$(-not [string]::IsNullOrWhiteSpace($ExternalCommand))",
    "- externalOutputCsv=$ExternalOutputCsv",
    "",
    "External model output should be converted to FaceVerificationCsv fields before running new-yolo-pseudo-gt-evidence.ps1: frame, verificationId, x, y, w, h, faceVerificationConfidence, faceVerificationDistance.",
    "Final face/nonface/miss decisions still require review CSV labels."
)

$summary | Set-Content -Encoding UTF8 -Path $summaryPath

Write-Host "[YoloPseudoGtFaceVerificationInput] rows=$($manifestRows.Count), manifest=$manifestPath, skipImageExtraction=$($SkipImageExtraction.IsPresent)"
if (-not [string]::IsNullOrWhiteSpace($ExternalCommand)) {
    Write-Host "[YoloPseudoGtFaceVerificationInput] externalOutput=$ExternalOutputCsv"
}
