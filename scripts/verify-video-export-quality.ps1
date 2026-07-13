<#
.SYNOPSIS
    Verifies that an exported video preserves source format quality and timeline.

.DESCRIPTION
    Uses ffprobe to compare video, audio, color, pixel-format, chapters, container
    metadata, and packet timelines. Uses ffmpeg to fully decode the output and reject corrupt media.
    The command exits with a non-zero status when a required check fails.

.PARAMETER SourcePath
    Original video path.

.PARAMETER OutputPath
    Exported video path.

.PARAMETER ReportPath
    Optional JSON report path. Relative paths are resolved from the repository root.

.PARAMETER MaxTimelineDeltaSeconds
    Maximum stream/format duration delta. Zero derives half of one source frame plus
    one millisecond.

.PARAMETER MaxAvSkewDeltaSeconds
    Maximum change in audio/video start or end skew. The default is 10 milliseconds.

.PARAMETER SkipDecodeCheck
    Skips the full ffmpeg decode integrity check. Metadata and packet checks still run.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\verify-video-export-quality.ps1 `
        -SourcePath D:\video\source.mp4 -OutputPath D:\video\source_blur.mp4 `
        -ReportPath .tmp\quality-gates\source_blur.json
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePath,
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,
    [string]$ReportPath = "",
    [string]$FfprobePath = "",
    [string]$FfmpegPath = "",
    [ValidateRange(0, 10)]
    [double]$MaxTimelineDeltaSeconds = 0,
    [ValidateRange(0, 1)]
    [double]$MaxAvSkewDeltaSeconds = 0.010,
    [ValidateRange(0, 10)]
    [double]$MaxFpsDelta = 0.001,
    [ValidateRange(0, 1000)]
    [int]$MaxFrameCountDelta = 0,
    [ValidateRange(1, 3600)]
    [int]$PacketTailSeconds = 120,
    [switch]$SkipDecodeCheck
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$checks = [System.Collections.Generic.List[object]]::new()

function Resolve-RepoPath {
    param(
        [string]$Path,
        [bool]$MustExist = $true
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return ""
    }

    $candidate = if ([IO.Path]::IsPathRooted($Path)) { $Path } else { Join-Path $repo $Path }
    if ($MustExist) {
        return (Resolve-Path $candidate).Path
    }

    return [IO.Path]::GetFullPath($candidate)
}

function Resolve-Tool {
    param(
        [string]$PreferredPath,
        [string]$ToolName
    )

    if (-not [string]::IsNullOrWhiteSpace($PreferredPath)) {
        return Resolve-RepoPath $PreferredPath
    }

    $command = Get-Command $ToolName -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        throw "$ToolName was not found. Add it to PATH or pass -${ToolName}Path."
    }

    return $command.Source
}

function Invoke-ProbeJson {
    param(
        [string]$Tool,
        [string[]]$Arguments
    )

    $output = & $Tool @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "ffprobe failed with exit code ${exitCode}: $($output | Out-String)"
    }

    $text = $output -join "`n"
    if ([string]::IsNullOrWhiteSpace($text)) {
        throw "ffprobe returned no JSON output."
    }

    return $text | ConvertFrom-Json
}

function Get-StreamPacketSignature {
    param(
        [string]$Tool,
        [string]$Path,
        [string]$StreamSpecifier
    )

    $output = & $Tool @(
        "-v", "error",
        "-select_streams", $StreamSpecifier,
        "-show_packets",
        "-show_data_hash", "sha256",
        "-show_entries", "packet=pts_time,dts_time,duration_time,size,data_hash,flags",
        "-of", "compact=p=0:nk=0",
        $Path
    ) 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "ffprobe packet query failed: $($output | Out-String)"
    }

    $packetLines = @($output | ForEach-Object { [string]$_ })
    $text = $packetLines -join "`n"
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hashBytes = $sha256.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($text))
        $hash = [BitConverter]::ToString($hashBytes).Replace("-", "").ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }

    return [pscustomobject]@{
        count = $packetLines.Count
        value = $hash
    }
}

function Get-PropertyText {
    param(
        [object]$Object,
        [string]$Name,
        [string]$Default = ""
    )

    if ($null -eq $Object) {
        return $Default
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or [string]::IsNullOrWhiteSpace([string]$property.Value)) {
        return $Default
    }

    return [string]$property.Value
}

function Test-AuxiliaryVideoStream {
    param([object]$Stream)

    $disposition = $Stream.disposition
    return (Get-PropertyText $disposition "attached_pic" "0") -eq "1" -or
        (Get-PropertyText $disposition "timed_thumbnails" "0") -eq "1" -or
        (Get-PropertyText $disposition "still_image" "0") -eq "1"
}

function Get-PrimaryVideoStream {
    param([object[]]$Streams)

    $regularStreams = @($Streams | Where-Object { -not (Test-AuxiliaryVideoStream $_) })
    $defaultStream = $regularStreams | Where-Object {
        (Get-PropertyText $_.disposition "default" "0") -eq "1"
    } | Select-Object -First 1
    if ($null -ne $defaultStream) {
        return $defaultStream
    }

    return $regularStreams | Select-Object -First 1
}

function Convert-ToDouble {
    param(
        [object]$Value,
        [double]$Default = [double]::NaN
    )

    if ($null -eq $Value) {
        return $Default
    }

    $parsed = 0.0
    if ([double]::TryParse(
        [string]$Value,
        [System.Globalization.NumberStyles]::Float,
        [System.Globalization.CultureInfo]::InvariantCulture,
        [ref]$parsed)) {
        return $parsed
    }

    return $Default
}

function Convert-ToInt64 {
    param(
        [object]$Value,
        [long]$Default = -1
    )

    if ($null -eq $Value) {
        return $Default
    }

    $parsed = 0L
    if ([long]::TryParse([string]$Value, [ref]$parsed)) {
        return $parsed
    }

    return $Default
}

function Convert-RationalToDouble {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value) -or $Value -eq "N/A") {
        return [double]::NaN
    }

    $separator = if ($Value.Contains("/")) { "/" } elseif ($Value.Contains(":")) { ":" } else { "" }
    if ([string]::IsNullOrEmpty($separator)) {
        return Convert-ToDouble $Value
    }

    $parts = $Value.Split([char]$separator)
    if ($parts.Count -ne 2) {
        return [double]::NaN
    }

    $numerator = Convert-ToDouble $parts[0]
    $denominator = Convert-ToDouble $parts[1]
    if ([double]::IsNaN($numerator) -or [double]::IsNaN($denominator) -or $denominator -eq 0) {
        return [double]::NaN
    }

    return $numerator / $denominator
}

function Format-Number {
    param(
        [double]$Value,
        [string]$Format = "0.######"
    )

    if ([double]::IsNaN($Value) -or [double]::IsInfinity($Value)) {
        return "unknown"
    }

    return $Value.ToString($Format, [System.Globalization.CultureInfo]::InvariantCulture)
}

function Add-Check {
    param(
        [string]$Name,
        [bool]$Passed,
        [string]$Source,
        [string]$Output,
        [string]$Requirement
    )

    $status = if ($Passed) { "PASS" } else { "FAIL" }
    $checks.Add([pscustomobject]@{
        name = $Name
        passed = $Passed
        source = $Source
        output = $Output
        requirement = $Requirement
    })
    Write-Host "[ExportQualityGate] $status $Name source=$Source output=$Output requirement=$Requirement"
}

function Add-CopiedStreamChecks {
    param(
        [string]$Prefix,
        [object]$SourceStream,
        [object]$OutputStream,
        [string]$SourcePath,
        [string]$OutputPath,
        [string]$ProbeTool
    )

    $sourceCodec = Get-PropertyText $SourceStream "codec_name" "unknown"
    $outputCodec = Get-PropertyText $OutputStream "codec_name" "unknown"
    Add-Check "$Prefix-codec" ($sourceCodec -eq $outputCodec) `
        $sourceCodec $outputCodec "equal (stream copy)"

    foreach ($name in @(
        "default", "dub", "original", "comment", "lyrics", "karaoke", "forced",
        "hearing_impaired", "visual_impaired", "clean_effects", "attached_pic",
        "timed_thumbnails", "non_diegetic", "captions", "descriptions", "metadata",
        "dependent", "still_image")) {
        $sourceValue = Get-PropertyText $SourceStream.disposition $name "0"
        $outputValue = Get-PropertyText $OutputStream.disposition $name "0"
        Add-Check "$Prefix-disposition-$($name.Replace('_', '-'))" `
            ($sourceValue -eq $outputValue) $sourceValue $outputValue "equal"
    }

    foreach ($name in @("language", "title", "filename", "mimetype")) {
        $sourceValue = Get-PropertyText $SourceStream.tags $name ""
        $outputValue = Get-PropertyText $OutputStream.tags $name ""
        Add-Check "$Prefix-tag-$name" `
            ([string]::IsNullOrWhiteSpace($sourceValue) -or $sourceValue -eq $outputValue) `
            $sourceValue $outputValue "preserve when present"
    }

    $sourceExtradataHash = Get-PropertyText $SourceStream "extradata_hash" ""
    $outputExtradataHash = Get-PropertyText $OutputStream "extradata_hash" ""
    $mustMatchExtradata =
        -not [string]::IsNullOrWhiteSpace($sourceExtradataHash) -and $sourceCodec -ne "mov_text"
    Add-Check "$Prefix-extradata-hash" `
        (-not $mustMatchExtradata -or $sourceExtradataHash -eq $outputExtradataHash) `
        $sourceExtradataHash $outputExtradataHash "equal when source is known; mov_text muxer headers may differ"

    $sourceIndex = Get-PropertyText $SourceStream "index" "-1"
    $outputIndex = Get-PropertyText $OutputStream "index" "-1"
    $sourcePackets = Get-StreamPacketSignature $ProbeTool $SourcePath $sourceIndex
    $outputPackets = Get-StreamPacketSignature $ProbeTool $OutputPath $outputIndex
    Add-Check "$Prefix-packet-count" ($sourcePackets.count -eq $outputPackets.count) `
        ([string]$sourcePackets.count) ([string]$outputPackets.count) "equal"
    Add-Check "$Prefix-packet-timeline-payload" ($sourcePackets.value -eq $outputPackets.value) `
        $sourcePackets.value $outputPackets.value "PTS/DTS/duration/size/payload hash/flags equal"
}

function Get-MediaProbe {
    param(
        [string]$Tool,
        [string]$Path
    )

    return Invoke-ProbeJson $Tool @(
        "-v", "error",
        "-count_frames",
        "-show_streams",
        "-show_data_hash", "sha256",
        "-show_chapters",
        "-show_format",
        "-of", "json",
        $Path
    )
}

function Get-VideoProbeFrames {
    param(
        [string]$Tool,
        [string]$Path,
        [string]$StreamSpecifier
    )

    $probe = Invoke-ProbeJson $Tool @(
        "-v", "error",
        "-select_streams", $StreamSpecifier,
        "-read_intervals", "%+#32",
        "-show_frames",
        "-of", "json",
        $Path
    )
    $frames = @($probe.frames)
    return @($frames | Select-Object -First 16)
}

function Get-FrameSideDataSignature {
    param(
        [object[]]$Frames,
        [string]$SideDataType,
        [string[]]$Fields
    )

    if ($null -eq $Frames -or $Frames.Count -eq 0) {
        return "none"
    }

    foreach ($frame in $Frames) {
        foreach ($sideData in @($frame.side_data_list)) {
            if ((Get-PropertyText $sideData "side_data_type" "") -ne $SideDataType) {
                continue
            }

            $values = foreach ($field in $Fields) {
                "$field=$(Get-PropertyText $sideData $field 'unknown')"
            }
            return $values -join "|"
        }
    }

    return "none"
}

function Get-FrameInterlaceSignature {
    param([object[]]$Frames)

    if ($null -eq $Frames -or $Frames.Count -eq 0) {
        return "none"
    }

    $values = foreach ($frame in $Frames) {
        $interlaced = Get-PropertyText $frame "interlaced_frame" "unknown"
        $topFieldFirst = Get-PropertyText $frame "top_field_first" "unknown"
        "${interlaced}/${topFieldFirst}"
    }
    return $values -join ","
}

function Test-HasInterlacedFrame {
    param([object[]]$Frames)

    foreach ($frame in @($Frames)) {
        if ((Get-PropertyText $frame "interlaced_frame" "0") -eq "1") {
            return $true
        }
    }
    return $false
}

function Get-PixelFormatMap {
    param([string]$Tool)

    $json = Invoke-ProbeJson $Tool @("-v", "error", "-show_pixel_formats", "-of", "json")
    $map = @{}
    foreach ($format in @($json.pixel_formats)) {
        $components = @($format.components)
        $bitDepth = if ($components.Count -gt 0) {
            [int](Get-PropertyText $components[0] "bit_depth" "0")
        } else {
            0
        }

        $log2ChromaW = Convert-ToInt64 (Get-PropertyText $format "log2_chroma_w" "0") 0
        $log2ChromaH = Convert-ToInt64 (Get-PropertyText $format "log2_chroma_h" "0") 0
        $name = Get-PropertyText $format "name"
        if (-not [string]::IsNullOrWhiteSpace($name)) {
            $map[$name] = [pscustomobject]@{
                name = $name
                components = [int](Get-PropertyText $format "nb_components" "0")
                bitDepth = $bitDepth
                log2ChromaW = [int]$log2ChromaW
                log2ChromaH = [int]$log2ChromaH
            }
        }
    }

    return $map
}

function Get-FrameRate {
    param([object]$Stream)

    $rate = Convert-RationalToDouble (Get-PropertyText $Stream "avg_frame_rate")
    if ([double]::IsNaN($rate) -or $rate -le 0) {
        $rate = Convert-RationalToDouble (Get-PropertyText $Stream "r_frame_rate")
    }

    return $rate
}

function Get-FrameCount {
    param([object]$Stream)

    $count = Convert-ToInt64 (Get-PropertyText $Stream "nb_read_frames")
    if ($count -lt 0) {
        $count = Convert-ToInt64 (Get-PropertyText $Stream "nb_frames")
    }

    return $count
}

function Get-StreamDuration {
    param(
        [object]$Stream,
        [object]$Format
    )

    $duration = Convert-ToDouble (Get-PropertyText $Stream "duration")
    if ([double]::IsNaN($duration)) {
        $duration = Convert-ToDouble (Get-PropertyText $Format "duration")
    }

    return $duration
}

function Get-StreamStart {
    param(
        [object]$Stream,
        [object]$Format
    )

    $start = Convert-ToDouble (Get-PropertyText $Stream "start_time")
    if ([double]::IsNaN($start)) {
        $start = Convert-ToDouble (Get-PropertyText $Format "start_time") 0
    }

    return $start
}

function Get-VideoBitDepth {
    param(
        [object]$Stream,
        [hashtable]$PixelFormats
    )

    $pixelFormatName = Get-PropertyText $Stream "pix_fmt"
    if ($PixelFormats.ContainsKey($pixelFormatName)) {
        $depth = [int]$PixelFormats[$pixelFormatName].bitDepth
        if ($depth -gt 0) {
            return $depth
        }
    }

    return [int](Convert-ToInt64 (Get-PropertyText $Stream "bits_per_raw_sample") 0)
}

function Get-PacketEnd {
    param(
        [string]$Tool,
        [string]$Path,
        [string]$StreamSelector,
        [object]$Stream,
        [object]$Format,
        [int]$TailSeconds
    )

    $streamStart = Get-StreamStart $Stream $Format
    $streamDuration = Get-StreamDuration $Stream $Format
    $expectedEnd = if ([double]::IsNaN($streamDuration)) { $streamStart } else { $streamStart + $streamDuration }
    $probeArguments = @("-v", "error")
    if (-not [double]::IsNaN($streamDuration)) {
        $readStart = [Math]::Max(0.0, $expectedEnd - $TailSeconds)
        $readEnd = $expectedEnd + 1.0
        $readStartText = $readStart.ToString("0.######", [System.Globalization.CultureInfo]::InvariantCulture)
        $readEndText = $readEnd.ToString("0.######", [System.Globalization.CultureInfo]::InvariantCulture)
        $probeArguments += @("-read_intervals", "${readStartText}%${readEndText}")
    }
    $probeArguments += @(
        "-select_streams", $StreamSelector,
        "-show_packets",
        "-show_entries", "packet=pts_time,dts_time,duration_time",
        "-of", "json",
        $Path
    )
    $json = Invoke-ProbeJson $Tool $probeArguments

    $end = [double]::NaN
    foreach ($packet in @($json.packets)) {
        $timestamp = Convert-ToDouble (Get-PropertyText $packet "pts_time")
        if ([double]::IsNaN($timestamp)) {
            $timestamp = Convert-ToDouble (Get-PropertyText $packet "dts_time")
        }
        if ([double]::IsNaN($timestamp)) {
            continue
        }

        $duration = Convert-ToDouble (Get-PropertyText $packet "duration_time") 0
        $packetEnd = $timestamp + [Math]::Max(0.0, $duration)
        if ([double]::IsNaN($end) -or $packetEnd -gt $end) {
            $end = $packetEnd
        }
    }

    if ([double]::IsNaN($end) -and -not [double]::IsNaN($streamDuration)) {
        return [pscustomobject]@{ value = $expectedEnd; evidence = "stream-duration" }
    }

    return [pscustomobject]@{ value = $end; evidence = "last-packet" }
}

function Test-KnownMetadata {
    param([string]$Value)

    return -not [string]::IsNullOrWhiteSpace($Value) -and
        $Value -notin @("unknown", "unspecified", "reserved", "N/A")
}

function Get-VideoRotation {
    param([object]$Video)

    foreach ($sideData in @($Video.side_data_list)) {
        $rotationText = Get-PropertyText $sideData "rotation" ""
        if (-not [string]::IsNullOrWhiteSpace($rotationText)) {
            $rotation = Convert-ToDouble $rotationText
            if (-not [double]::IsNaN($rotation)) {
                return [Math]::Round($rotation, 6)
            }
        }
    }

    if ($null -ne $Video.tags) {
        $tagRotation = Get-PropertyText $Video.tags "rotate" ""
        if (-not [string]::IsNullOrWhiteSpace($tagRotation)) {
            $rotation = Convert-ToDouble $tagRotation
            if (-not [double]::IsNaN($rotation)) {
                return [Math]::Round($rotation, 6)
            }
        }
    }

    return 0.0
}

$source = Resolve-RepoPath $SourcePath
$output = Resolve-RepoPath $OutputPath
$ffprobe = Resolve-Tool $FfprobePath "ffprobe"
$ffmpeg = Resolve-Tool $FfmpegPath "ffmpeg"

Write-Host "[ExportQualityGate] source=$source"
Write-Host "[ExportQualityGate] output=$output"

$pixelFormats = Get-PixelFormatMap $ffprobe
$sourceProbe = Get-MediaProbe $ffprobe $source
$outputProbe = Get-MediaProbe $ffprobe $output
$sourceVideos = @($sourceProbe.streams | Where-Object { (Get-PropertyText $_ "codec_type") -eq "video" })
$outputVideos = @($outputProbe.streams | Where-Object { (Get-PropertyText $_ "codec_type") -eq "video" })
$sourceAudios = @($sourceProbe.streams | Where-Object { (Get-PropertyText $_ "codec_type") -eq "audio" })
$outputAudios = @($outputProbe.streams | Where-Object { (Get-PropertyText $_ "codec_type") -eq "audio" })
$sourceVideo = Get-PrimaryVideoStream $sourceVideos
$outputVideo = Get-PrimaryVideoStream $outputVideos
if ($null -eq $sourceVideo -or $null -eq $outputVideo) {
    throw "A source and output primary video stream are required."
}
$sourcePrimaryVideoIndex = Get-PropertyText $sourceVideo "index" "-1"
$outputPrimaryVideoIndex = Get-PropertyText $outputVideo "index" "-1"
$sourceVideoProbeFrames = @(Get-VideoProbeFrames $ffprobe $source $sourcePrimaryVideoIndex)
$outputVideoProbeFrames = @(Get-VideoProbeFrames $ffprobe $output $outputPrimaryVideoIndex)
$sourceAdditionalVideos = @($sourceVideos | Where-Object {
    (Get-PropertyText $_ "index" "-1") -ne $sourcePrimaryVideoIndex
})
$outputAdditionalVideos = @($outputVideos | Where-Object {
    (Get-PropertyText $_ "index" "-1") -ne $outputPrimaryVideoIndex
})
$preservedStreamTypes = @("subtitle", "data", "attachment")
$sourceChapters = @($sourceProbe.chapters)
$outputChapters = @($outputProbe.chapters)

Add-Check "video-stream-count" ($sourceVideos.Count -eq $outputVideos.Count -and $sourceVideos.Count -gt 0) `
    ([string]$sourceVideos.Count) ([string]$outputVideos.Count) "equal and at least one"
Add-Check "audio-stream-count" ($sourceAudios.Count -eq $outputAudios.Count) `
    ([string]$sourceAudios.Count) ([string]$outputAudios.Count) "equal"
foreach ($streamType in $preservedStreamTypes) {
    $sourceTypedStreams = @($sourceProbe.streams | Where-Object {
        (Get-PropertyText $_ "codec_type") -eq $streamType
    })
    $outputTypedStreams = @($outputProbe.streams | Where-Object {
        (Get-PropertyText $_ "codec_type") -eq $streamType
    })
    Add-Check "$streamType-stream-count" ($sourceTypedStreams.Count -eq $outputTypedStreams.Count) `
        ([string]$sourceTypedStreams.Count) ([string]$outputTypedStreams.Count) "equal"

    $typedPairCount = [Math]::Min($sourceTypedStreams.Count, $outputTypedStreams.Count)
    for ($i = 0; $i -lt $typedPairCount; $i++) {
        Add-CopiedStreamChecks `
            -Prefix "$streamType[$i]" `
            -SourceStream $sourceTypedStreams[$i] `
            -OutputStream $outputTypedStreams[$i] `
            -SourcePath $source `
            -OutputPath $output `
            -ProbeTool $ffprobe
    }
}

$additionalVideoPairCount = [Math]::Min($sourceAdditionalVideos.Count, $outputAdditionalVideos.Count)
for ($i = 0; $i -lt $additionalVideoPairCount; $i++) {
    Add-CopiedStreamChecks `
        -Prefix "additional-video[$i]" `
        -SourceStream $sourceAdditionalVideos[$i] `
        -OutputStream $outputAdditionalVideos[$i] `
        -SourcePath $source `
        -OutputPath $output `
        -ProbeTool $ffprobe
}

foreach ($tagName in @(
    "title", "artist", "album", "album_artist", "composer", "comment", "description",
    "synopsis", "genre", "date", "creation_time", "copyright", "location", "publisher",
    "show", "episode_id", "network")) {
    $sourceTag = Get-PropertyText $sourceProbe.format.tags $tagName ""
    $outputTag = Get-PropertyText $outputProbe.format.tags $tagName ""
    Add-Check "container-tag-$($tagName.Replace('_', '-'))" `
        ([string]::IsNullOrWhiteSpace($sourceTag) -or $sourceTag -eq $outputTag) `
        $sourceTag $outputTag "preserve when present"
}

$sourceWidth = [int](Get-PropertyText $sourceVideo "width" "0")
$sourceHeight = [int](Get-PropertyText $sourceVideo "height" "0")
$outputWidth = [int](Get-PropertyText $outputVideo "width" "0")
$outputHeight = [int](Get-PropertyText $outputVideo "height" "0")
Add-Check "resolution" ($sourceWidth -eq $outputWidth -and $sourceHeight -eq $outputHeight) `
    "${sourceWidth}x${sourceHeight}" "${outputWidth}x${outputHeight}" "equal"

$sourceSarText = Get-PropertyText $sourceVideo "sample_aspect_ratio" "unknown"
$outputSarText = Get-PropertyText $outputVideo "sample_aspect_ratio" "unknown"
$sourceSar = Convert-RationalToDouble $sourceSarText
$outputSar = Convert-RationalToDouble $outputSarText
$sarPassed = if ([double]::IsNaN($sourceSar)) {
    [double]::IsNaN($outputSar) -or [Math]::Abs(1.0 - $outputSar) -le 0.000001
} else {
    -not [double]::IsNaN($outputSar) -and [Math]::Abs($sourceSar - $outputSar) -le 0.000001
}
Add-Check "sample-aspect-ratio" $sarPassed $sourceSarText $outputSarText `
    "equal; an unspecified source SAR may become explicit 1:1"

$sourceRotation = Get-VideoRotation $sourceVideo
$outputRotation = Get-VideoRotation $outputVideo
Add-Check "video-display-rotation" ([Math]::Abs($sourceRotation - $outputRotation) -le 0.000001) `
    (Format-Number $sourceRotation) (Format-Number $outputRotation) "equal"

$sourceFps = Get-FrameRate $sourceVideo
$outputFps = Get-FrameRate $outputVideo
$fpsDelta = [Math]::Abs($sourceFps - $outputFps)
$fpsPassed = -not [double]::IsNaN($fpsDelta) -and $fpsDelta -le $MaxFpsDelta
Add-Check "frame-rate" $fpsPassed (Format-Number $sourceFps) (Format-Number $outputFps) `
    "delta <= $($MaxFpsDelta.ToString([System.Globalization.CultureInfo]::InvariantCulture))"

$sourceFrameCount = Get-FrameCount $sourceVideo
$outputFrameCount = Get-FrameCount $outputVideo
$frameDelta = if ($sourceFrameCount -ge 0 -and $outputFrameCount -ge 0) {
    [Math]::Abs($sourceFrameCount - $outputFrameCount)
} else {
    [long]::MaxValue
}
Add-Check "frame-count" ($frameDelta -le $MaxFrameCountDelta) ([string]$sourceFrameCount) ([string]$outputFrameCount) `
    "delta <= $MaxFrameCountDelta; -1 means unverifiable"

$sourcePixFmt = Get-PropertyText $sourceVideo "pix_fmt" "unknown"
$outputPixFmt = Get-PropertyText $outputVideo "pix_fmt" "unknown"
$sourcePixelInfo = if ($pixelFormats.ContainsKey($sourcePixFmt)) { $pixelFormats[$sourcePixFmt] } else { $null }
$outputPixelInfo = if ($pixelFormats.ContainsKey($outputPixFmt)) { $pixelFormats[$outputPixFmt] } else { $null }
$sourceBitDepth = Get-VideoBitDepth $sourceVideo $pixelFormats
$outputBitDepth = Get-VideoBitDepth $outputVideo $pixelFormats
Add-Check "video-bit-depth" ($sourceBitDepth -gt 0 -and $outputBitDepth -ge $sourceBitDepth) `
    "${sourcePixFmt}/${sourceBitDepth}-bit" "${outputPixFmt}/${outputBitDepth}-bit" "output >= source"

$chromaKnown = $null -ne $sourcePixelInfo -and $null -ne $outputPixelInfo
$chromaPassed = $chromaKnown -and
    $outputPixelInfo.components -ge $sourcePixelInfo.components -and
    $outputPixelInfo.log2ChromaW -le $sourcePixelInfo.log2ChromaW -and
    $outputPixelInfo.log2ChromaH -le $sourcePixelInfo.log2ChromaH
$sourceChroma = if ($null -eq $sourcePixelInfo) {
    "$sourcePixFmt/unknown"
} else {
    "$sourcePixFmt/components=$($sourcePixelInfo.components),subsample=$($sourcePixelInfo.log2ChromaW):$($sourcePixelInfo.log2ChromaH)"
}
$outputChroma = if ($null -eq $outputPixelInfo) {
    "$outputPixFmt/unknown"
} else {
    "$outputPixFmt/components=$($outputPixelInfo.components),subsample=$($outputPixelInfo.log2ChromaW):$($outputPixelInfo.log2ChromaH)"
}
Add-Check "chroma-sampling" $chromaPassed $sourceChroma $outputChroma `
    "component count must not decrease; log2 chroma subsampling must not increase"

$sourceFrameInterlace = Get-FrameInterlaceSignature $sourceVideoProbeFrames
$outputFrameInterlace = Get-FrameInterlaceSignature $outputVideoProbeFrames
$sourceHasInterlacedFrame = Test-HasInterlacedFrame $sourceVideoProbeFrames
$outputHasInterlacedFrame = Test-HasInterlacedFrame $outputVideoProbeFrames

$sourceChromaLocation = Get-PropertyText $sourceVideo "chroma_location" "unknown"
$outputChromaLocation = Get-PropertyText $outputVideo "chroma_location" "unknown"
$sourceChromaKnown = Test-KnownMetadata $sourceChromaLocation
Add-Check "video-chroma-location" `
    (-not $sourceChromaKnown -or $sourceChromaLocation -eq $outputChromaLocation) `
    $sourceChromaLocation $outputChromaLocation `
    $(if ($sourceChromaKnown) { "preserve source value" } else { "source unspecified; reported only" })

$sourceFieldOrder = Get-PropertyText $sourceVideo "field_order" "unknown"
$outputFieldOrder = Get-PropertyText $outputVideo "field_order" "unknown"
$sourceFieldOrderKnown = Test-KnownMetadata $sourceFieldOrder
$outputFieldOrderKnown = Test-KnownMetadata $outputFieldOrder
$fieldOrderPassed = if ($sourceFieldOrder -eq "progressive") {
    $outputFieldOrder -eq "progressive" -or
        (-not $outputFieldOrderKnown -and -not $outputHasInterlacedFrame)
} else {
    -not $sourceFieldOrderKnown -or $sourceFieldOrder -eq $outputFieldOrder
}
Add-Check "video-field-order" $fieldOrderPassed $sourceFieldOrder $outputFieldOrder `
    "preserve interlaced order; progressive may be unspecified only when decoded frames remain progressive"

Add-Check "video-frame-interlace-signature" `
    (-not $sourceHasInterlacedFrame -or $sourceFrameInterlace -eq $outputFrameInterlace) `
    $sourceFrameInterlace $outputFrameInterlace `
    "preserve interlaced_frame/top_field_first for the first decoded frames when interlaced"

foreach ($field in @("color_range", "color_space", "color_transfer", "color_primaries")) {
    $sourceColor = Get-PropertyText $sourceVideo $field "unknown"
    $outputColor = Get-PropertyText $outputVideo $field "unknown"
    $sourceKnown = Test-KnownMetadata $sourceColor
    $passed = -not $sourceKnown -or $sourceColor -eq $outputColor
    $requirement = if ($sourceKnown) { "preserve source value" } else { "source unspecified; reported only" }
    Add-Check "video-$($field.Replace('_', '-'))" $passed $sourceColor $outputColor $requirement
}

$masteringFields = @(
    "red_x", "red_y", "green_x", "green_y", "blue_x", "blue_y",
    "white_point_x", "white_point_y", "min_luminance", "max_luminance"
)
$sourceTransfer = Get-PropertyText $sourceVideo "color_transfer" "unknown"
$sourceUsesHdrTransfer = $sourceTransfer -in @("smpte2084", "arib-std-b67")
Add-Check "video-hdr-frame-probe" `
    (-not $sourceUsesHdrTransfer -or $sourceVideoProbeFrames.Count -gt 0) `
    ([string]$sourceVideoProbeFrames.Count) ([string]$outputVideoProbeFrames.Count) `
    "an HDR source must yield decoded frames for side-data verification"

$sourceMastering = Get-FrameSideDataSignature `
    $sourceVideoProbeFrames "Mastering display metadata" $masteringFields
$outputMastering = Get-FrameSideDataSignature `
    $outputVideoProbeFrames "Mastering display metadata" $masteringFields
Add-Check "video-mastering-display-metadata" `
    ($sourceMastering -eq "none" -or $sourceMastering -eq $outputMastering) `
    $sourceMastering $outputMastering "preserve when present"

$contentLightFields = @("max_content", "max_average")
$sourceContentLight = Get-FrameSideDataSignature `
    $sourceVideoProbeFrames "Content light level metadata" $contentLightFields
$outputContentLight = Get-FrameSideDataSignature `
    $outputVideoProbeFrames "Content light level metadata" $contentLightFields
Add-Check "video-content-light-level-metadata" `
    ($sourceContentLight -eq "none" -or $sourceContentLight -eq $outputContentLight) `
    $sourceContentLight $outputContentLight "preserve when present"

$sourceDuration = Get-StreamDuration $sourceVideo $sourceProbe.format
$outputDuration = Get-StreamDuration $outputVideo $outputProbe.format
$sourceFormatDuration = Convert-ToDouble (Get-PropertyText $sourceProbe.format "duration")
$outputFormatDuration = Convert-ToDouble (Get-PropertyText $outputProbe.format "duration")
$derivedTimelineDelta = if (-not [double]::IsNaN($sourceFps) -and $sourceFps -gt 0) {
    (0.5 / $sourceFps) + 0.001
} else {
    0.018
}
$timelineLimit = if ($MaxTimelineDeltaSeconds -gt 0) { $MaxTimelineDeltaSeconds } else { $derivedTimelineDelta }

Add-Check "chapter-count" ($sourceChapters.Count -eq $outputChapters.Count) `
    ([string]$sourceChapters.Count) ([string]$outputChapters.Count) "equal"
$chapterPairCount = [Math]::Min($sourceChapters.Count, $outputChapters.Count)
for ($i = 0; $i -lt $chapterPairCount; $i++) {
    $sourceChapter = $sourceChapters[$i]
    $outputChapter = $outputChapters[$i]
    $sourceChapterStart = Convert-ToDouble (Get-PropertyText $sourceChapter "start_time")
    $outputChapterStart = Convert-ToDouble (Get-PropertyText $outputChapter "start_time")
    $sourceChapterEnd = Convert-ToDouble (Get-PropertyText $sourceChapter "end_time")
    $outputChapterEnd = Convert-ToDouble (Get-PropertyText $outputChapter "end_time")
    $chapterStartDelta = [Math]::Abs($sourceChapterStart - $outputChapterStart)
    $chapterEndDelta = [Math]::Abs($sourceChapterEnd - $outputChapterEnd)
    $sourceChapterTitle = Get-PropertyText $sourceChapter.tags "title" ""
    $outputChapterTitle = Get-PropertyText $outputChapter.tags "title" ""
    Add-Check "chapter[$i]-start" `
        (-not [double]::IsNaN($chapterStartDelta) -and $chapterStartDelta -le $timelineLimit) `
        (Format-Number $sourceChapterStart) (Format-Number $outputChapterStart) `
        "delta <= $(Format-Number $timelineLimit) seconds"
    Add-Check "chapter[$i]-end" `
        (-not [double]::IsNaN($chapterEndDelta) -and $chapterEndDelta -le $timelineLimit) `
        (Format-Number $sourceChapterEnd) (Format-Number $outputChapterEnd) `
        "delta <= $(Format-Number $timelineLimit) seconds"
    Add-Check "chapter[$i]-title" `
        ([string]::IsNullOrWhiteSpace($sourceChapterTitle) -or $sourceChapterTitle -eq $outputChapterTitle) `
        $sourceChapterTitle $outputChapterTitle "preserve when present"
}

$videoDurationDelta = [Math]::Abs($sourceDuration - $outputDuration)
Add-Check "video-duration" (-not [double]::IsNaN($videoDurationDelta) -and $videoDurationDelta -le $timelineLimit) `
    (Format-Number $sourceDuration) (Format-Number $outputDuration) "delta <= $(Format-Number $timelineLimit) seconds"
$formatDurationDelta = [Math]::Abs($sourceFormatDuration - $outputFormatDuration)
Add-Check "container-duration" (-not [double]::IsNaN($formatDurationDelta) -and $formatDurationDelta -le $timelineLimit) `
    (Format-Number $sourceFormatDuration) (Format-Number $outputFormatDuration) "delta <= $(Format-Number $timelineLimit) seconds"

$sourceVideoStart = Get-StreamStart $sourceVideo $sourceProbe.format
$outputVideoStart = Get-StreamStart $outputVideo $outputProbe.format
$sourceVideoEnd = Get-PacketEnd $ffprobe $source $sourcePrimaryVideoIndex $sourceVideo $sourceProbe.format $PacketTailSeconds
$outputVideoEnd = Get-PacketEnd $ffprobe $output $outputPrimaryVideoIndex $outputVideo $outputProbe.format $PacketTailSeconds
$sourceVideoSpan = $sourceVideoEnd.value - $sourceVideoStart
$outputVideoSpan = $outputVideoEnd.value - $outputVideoStart
$videoSpanDelta = [Math]::Abs($sourceVideoSpan - $outputVideoSpan)
Add-Check "video-packet-span" (-not [double]::IsNaN($videoSpanDelta) -and $videoSpanDelta -le $timelineLimit) `
    "$(Format-Number $sourceVideoSpan)/$($sourceVideoEnd.evidence)" `
    "$(Format-Number $outputVideoSpan)/$($outputVideoEnd.evidence)" "delta <= $(Format-Number $timelineLimit) seconds"

$audioPairCount = [Math]::Min($sourceAudios.Count, $outputAudios.Count)
for ($i = 0; $i -lt $audioPairCount; $i++) {
    $sourceAudio = $sourceAudios[$i]
    $outputAudio = $outputAudios[$i]
    $prefix = "audio[$i]"
    $sourceCodec = Get-PropertyText $sourceAudio "codec_name" "unknown"
    $outputCodec = Get-PropertyText $outputAudio "codec_name" "unknown"
    Add-Check "$prefix-codec" ($sourceCodec -eq $outputCodec) $sourceCodec $outputCodec "equal (no lossy codec substitution)"

    foreach ($field in @("sample_rate", "channels", "channel_layout")) {
        $sourceValue = Get-PropertyText $sourceAudio $field "unknown"
        $outputValue = Get-PropertyText $outputAudio $field "unknown"
        Add-Check "$prefix-$($field.Replace('_', '-'))" ($sourceValue -eq $outputValue) `
            $sourceValue $outputValue "equal"
    }

    $sourceAudioDepth = [int](Convert-ToInt64 (Get-PropertyText $sourceAudio "bits_per_raw_sample") 0)
    if ($sourceAudioDepth -le 0) {
        $sourceAudioDepth = [int](Convert-ToInt64 (Get-PropertyText $sourceAudio "bits_per_sample") 0)
    }
    $outputAudioDepth = [int](Convert-ToInt64 (Get-PropertyText $outputAudio "bits_per_raw_sample") 0)
    if ($outputAudioDepth -le 0) {
        $outputAudioDepth = [int](Convert-ToInt64 (Get-PropertyText $outputAudio "bits_per_sample") 0)
    }
    $audioDepthPassed = $sourceAudioDepth -le 0 -or $outputAudioDepth -ge $sourceAudioDepth
    Add-Check "$prefix-bit-depth" $audioDepthPassed ([string]$sourceAudioDepth) ([string]$outputAudioDepth) `
        "output >= source when source depth is known; 0 means unspecified"

    $sourceAudioDuration = Get-StreamDuration $sourceAudio $sourceProbe.format
    $outputAudioDuration = Get-StreamDuration $outputAudio $outputProbe.format
    $audioDurationDelta = [Math]::Abs($sourceAudioDuration - $outputAudioDuration)
    Add-Check "$prefix-duration" (-not [double]::IsNaN($audioDurationDelta) -and $audioDurationDelta -le $timelineLimit) `
        (Format-Number $sourceAudioDuration) (Format-Number $outputAudioDuration) "delta <= $(Format-Number $timelineLimit) seconds"

    $sourceAudioStart = Get-StreamStart $sourceAudio $sourceProbe.format
    $outputAudioStart = Get-StreamStart $outputAudio $outputProbe.format
    $sourceAudioEnd = Get-PacketEnd $ffprobe $source "a:$i" $sourceAudio $sourceProbe.format $PacketTailSeconds
    $outputAudioEnd = Get-PacketEnd $ffprobe $output "a:$i" $outputAudio $outputProbe.format $PacketTailSeconds
    $sourceStartSkew = $sourceAudioStart - $sourceVideoStart
    $outputStartSkew = $outputAudioStart - $outputVideoStart
    $startSkewDelta = [Math]::Abs($sourceStartSkew - $outputStartSkew)
    Add-Check "$prefix-av-start-skew" (-not [double]::IsNaN($startSkewDelta) -and $startSkewDelta -le $MaxAvSkewDeltaSeconds) `
        (Format-Number $sourceStartSkew) (Format-Number $outputStartSkew) "delta <= $(Format-Number $MaxAvSkewDeltaSeconds) seconds"

    $sourceEndSkew = $sourceAudioEnd.value - $sourceVideoEnd.value
    $outputEndSkew = $outputAudioEnd.value - $outputVideoEnd.value
    $endSkewDelta = [Math]::Abs($sourceEndSkew - $outputEndSkew)
    Add-Check "$prefix-av-end-skew" (-not [double]::IsNaN($endSkewDelta) -and $endSkewDelta -le $MaxAvSkewDeltaSeconds) `
        "$(Format-Number $sourceEndSkew)/$($sourceAudioEnd.evidence)" `
        "$(Format-Number $outputEndSkew)/$($outputAudioEnd.evidence)" "delta <= $(Format-Number $MaxAvSkewDeltaSeconds) seconds"
}

if ($SkipDecodeCheck) {
    Add-Check "output-full-decode" $true "not-applicable" "skipped" "metadata-only run requested"
} else {
    $decodeOutput = & $ffmpeg @(
        "-hide_banner", "-v", "error", "-xerror",
        "-i", $output,
        "-map", "0:$outputPrimaryVideoIndex",
        "-map", "0:a?",
        "-f", "null", "-"
    ) 2>&1
    $decodeExitCode = $LASTEXITCODE
    $decodeMessage = if ($decodeExitCode -eq 0) { "decoded successfully" } else { ($decodeOutput | Out-String).Trim() }
    Add-Check "output-full-decode" ($decodeExitCode -eq 0) "not-applicable" $decodeMessage "ffmpeg exit code 0"
}

$failures = @($checks | Where-Object { -not $_.passed })
$status = if ($failures.Count -eq 0) { "passed" } else { "failed" }
$report = [pscustomobject]@{
    schemaVersion = 1
    generatedAtUtc = [DateTime]::UtcNow.ToString("o")
    status = $status
    sourcePath = $source
    outputPath = $output
    tools = [pscustomobject]@{
        ffprobe = $ffprobe
        ffmpeg = $ffmpeg
    }
    thresholds = [pscustomobject]@{
        maxTimelineDeltaSeconds = $timelineLimit
        maxAvSkewDeltaSeconds = $MaxAvSkewDeltaSeconds
        maxFpsDelta = $MaxFpsDelta
        maxFrameCountDelta = $MaxFrameCountDelta
    }
    source = [pscustomobject]@{
        resolution = "${sourceWidth}x${sourceHeight}"
        sampleAspectRatio = $sourceSarText
        framesPerSecond = Format-Number $sourceFps
        frameCount = $sourceFrameCount
        pixelFormat = $sourcePixFmt
        bitDepth = $sourceBitDepth
        chromaLocation = Get-PropertyText $sourceVideo "chroma_location" "unknown"
        fieldOrder = Get-PropertyText $sourceVideo "field_order" "unknown"
        frameInterlaceSignature = $sourceFrameInterlace
        videoDurationSeconds = Format-Number $sourceDuration
        videoPacketEndSeconds = Format-Number $sourceVideoEnd.value
        audioStreamCount = $sourceAudios.Count
    }
    output = [pscustomobject]@{
        resolution = "${outputWidth}x${outputHeight}"
        sampleAspectRatio = $outputSarText
        framesPerSecond = Format-Number $outputFps
        frameCount = $outputFrameCount
        pixelFormat = $outputPixFmt
        bitDepth = $outputBitDepth
        chromaLocation = Get-PropertyText $outputVideo "chroma_location" "unknown"
        fieldOrder = Get-PropertyText $outputVideo "field_order" "unknown"
        frameInterlaceSignature = $outputFrameInterlace
        videoDurationSeconds = Format-Number $outputDuration
        videoPacketEndSeconds = Format-Number $outputVideoEnd.value
        audioStreamCount = $outputAudios.Count
    }
    checks = @($checks)
}

if (-not [string]::IsNullOrWhiteSpace($ReportPath)) {
    $resolvedReport = Resolve-RepoPath $ReportPath $false
    $reportDirectory = Split-Path -Parent $resolvedReport
    if (-not [string]::IsNullOrWhiteSpace($reportDirectory)) {
        New-Item -ItemType Directory -Force -Path $reportDirectory | Out-Null
    }
    $report | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 $resolvedReport
    Write-Host "[ExportQualityGate] report=$resolvedReport"
}

if ($failures.Count -gt 0) {
    throw "Export quality gate failed: $($failures.Count) check(s) failed: $(($failures.name) -join ', ')"
}

Write-Host "[ExportQualityGate] PASS checks=$($checks.Count)"
