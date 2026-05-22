param(
    [string]$PlanPath = "AUTO_MOSAIC_QUALITY_SPEED_PLAN.md",
    [string]$RunnerPath = "scripts\run-yolo-ten-minute-full.ps1",
    [string]$PartialSpeedCompareRunnerPath = "scripts\run-yolo-partial-speed-compare.ps1",
    [string]$SourcePath = "srcTest\260102_jp_10.mp4",
    [string]$ClipPath = ".tmp\srcTest-smoke\smoke-0200-600s.mp4",
    [string]$LogPath = ".tmp\yolo-ten-minute\yolo-ten-minute-20260523-000044.log",
    [string]$OutputPath = ".tmp\srcTest-smoke\smoke-0200-600s_blur.mp4",
    [string]$BaselineOnlyLogPath = "",
    [string]$BaselineOnlyLogDir = ".tmp\yolo-ten-minute-baseline-smoke",
    [string]$BaselineOnlyLogPattern = "yolo-ten-minute-baseline-only-*.log",
    [int]$BaselineOnlyMinFrames = 90,
    [string]$IncompleteBaselineFullLogPath = "",
    [string]$IncompleteBaselineFullLogDir = ".tmp\yolo-ten-minute-baseline-full",
    [string]$IncompleteBaselineFullLogPattern = "yolo-ten-minute-baseline-only-*.log",
    [int]$IncompleteBaselineFullMinProgressFrames = 240,
    [int]$IncompleteBaselineFullMaxProgressFrames = 1000,
    [string]$FaceOnnxOptimizedOnlyLogPath = "",
    [string]$FaceOnnxOptimizedOnlyLogDir = ".tmp\yolo-ten-minute-faceonnx-optimized-smoke",
    [string]$FaceOnnxOptimizedOnlyLogPattern = "yolo-ten-minute-faceonnx-optimized-only-*.log",
    [int]$FaceOnnxOptimizedOnlyMinFrames = 90,
    [string]$PartialSpeedYoloLogDir = ".tmp\yolo-partial-speed\yolo",
    [string]$PartialSpeedFaceOnnxLogDir = ".tmp\yolo-partial-speed\faceonnx-optimized",
    [int]$PartialSpeedMinFrames = 90,
    [int]$ExpectedOutputWidth = 3840,
    [int]$ExpectedOutputHeight = 2160,
    [int]$ExpectedOutputMinFrames = 17980,
    [double]$ExpectedOutputMinDurationSeconds = 599.0,
    [double]$ExpectedOutputMaxDurationSeconds = 601.0,
    [switch]$RequireClip,
    [switch]$RequireRun,
    [switch]$RequireBaselineOnlyRun,
    [switch]$RequireIncompleteBaselineFullAttempt,
    [switch]$RequireFaceOnnxOptimizedOnlyRun,
    [switch]$RequirePartialSpeedCompareRun
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$resolvedPlanPath = if ([IO.Path]::IsPathRooted($PlanPath)) { $PlanPath } else { Join-Path $repo $PlanPath }
$resolvedRunnerPath = if ([IO.Path]::IsPathRooted($RunnerPath)) { $RunnerPath } else { Join-Path $repo $RunnerPath }
$resolvedPartialSpeedCompareRunnerPath = if ([IO.Path]::IsPathRooted($PartialSpeedCompareRunnerPath)) { $PartialSpeedCompareRunnerPath } else { Join-Path $repo $PartialSpeedCompareRunnerPath }
$resolvedSourcePath = if ([IO.Path]::IsPathRooted($SourcePath)) { $SourcePath } else { Join-Path $repo $SourcePath }
$resolvedClipPath = if ([IO.Path]::IsPathRooted($ClipPath)) { $ClipPath } else { Join-Path $repo $ClipPath }
$resolvedLogPath = if ([IO.Path]::IsPathRooted($LogPath)) { $LogPath } else { Join-Path $repo $LogPath }
$resolvedOutputPath = if ([IO.Path]::IsPathRooted($OutputPath)) { $OutputPath } else { Join-Path $repo $OutputPath }
$resolvedBaselineOnlyLogDir = if ([IO.Path]::IsPathRooted($BaselineOnlyLogDir)) { $BaselineOnlyLogDir } else { Join-Path $repo $BaselineOnlyLogDir }
$resolvedIncompleteBaselineFullLogDir = if ([IO.Path]::IsPathRooted($IncompleteBaselineFullLogDir)) { $IncompleteBaselineFullLogDir } else { Join-Path $repo $IncompleteBaselineFullLogDir }
$resolvedFaceOnnxOptimizedOnlyLogDir = if ([IO.Path]::IsPathRooted($FaceOnnxOptimizedOnlyLogDir)) { $FaceOnnxOptimizedOnlyLogDir } else { Join-Path $repo $FaceOnnxOptimizedOnlyLogDir }
$resolvedPartialSpeedYoloLogDir = if ([IO.Path]::IsPathRooted($PartialSpeedYoloLogDir)) { $PartialSpeedYoloLogDir } else { Join-Path $repo $PartialSpeedYoloLogDir }
$resolvedPartialSpeedFaceOnnxLogDir = if ([IO.Path]::IsPathRooted($PartialSpeedFaceOnnxLogDir)) { $PartialSpeedFaceOnnxLogDir } else { Join-Path $repo $PartialSpeedFaceOnnxLogDir }

foreach ($required in @($resolvedPlanPath, $resolvedRunnerPath, $resolvedPartialSpeedCompareRunnerPath, $resolvedSourcePath)) {
    if (-not (Test-Path $required)) {
        throw "Required file not found: $required"
    }
}

$plan = Get-Content -Raw -Path $resolvedPlanPath
$runner = Get-Content -Raw -Path $resolvedRunnerPath
$smokePath = Join-Path $repo "scripts\run-srcTest-smoke.ps1"
if (-not (Test-Path $smokePath)) {
    throw "Smoke script not found: $smokePath"
}
$smoke = Get-Content -Raw -Path $smokePath

function Assert-Contains {
    param(
        [string]$Name,
        [string]$Text,
        [string]$Expected
    )

    if (-not $Text.Contains($Expected)) {
        throw "$Name missing text: $Expected"
    }

    Write-Host "[YoloTenMinuteStateVerify] pass $Name"
}

function Assert-Match {
    param(
        [string]$Name,
        [string]$Text,
        [string]$Pattern
    )

    if ($Text -notmatch $Pattern) {
        throw "$Name missing pattern: $Pattern"
    }

    Write-Host "[YoloTenMinuteStateVerify] pass $Name"
}

function Assert-NotContains {
    param(
        [string]$Name,
        [string]$Text,
        [string]$Unexpected
    )

    if ($Text.Contains($Unexpected)) {
        throw "$Name contains unexpected text: $Unexpected"
    }

    Write-Host "[YoloTenMinuteStateVerify] pass $Name"
}

function Resolve-LatestLog {
    param(
        [string]$LogPath,
        [string]$LogDir,
        [string]$Pattern
    )

    if (-not [string]::IsNullOrWhiteSpace($LogPath)) {
        return if ([IO.Path]::IsPathRooted($LogPath)) { $LogPath } else { Join-Path $repo $LogPath }
    }

    if (-not (Test-Path $LogDir)) {
        throw "Log directory not found: $LogDir"
    }

    $latest = Get-ChildItem -Path $LogDir -Filter $Pattern -File |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($null -eq $latest) {
        throw "No log found in $LogDir with pattern $Pattern"
    }

    return $latest.FullName
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

function Resolve-Ffprobe {
    $native = Get-Command ffprobe -ErrorAction SilentlyContinue
    if ($null -ne $native) {
        return [pscustomobject]@{
            Command = $native.Source
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

    return $null
}

function Invoke-FfprobeJson {
    param([string]$VideoPath)

    $tool = Resolve-Ffprobe
    if ($null -eq $tool) {
        throw "ffprobe not found on PATH and wsl.exe is unavailable."
    }

    $probePath = if ($tool.UseWsl) { Convert-ToWslPath $VideoPath } else { $VideoPath }
    $arguments = @(
        "-v", "error",
        "-select_streams", "v:0",
        "-show_entries", "stream=width,height,avg_frame_rate,nb_frames,duration",
        "-show_entries", "format=duration,size",
        "-of", "json",
        $probePath
    )

    $json = if ($tool.UseWsl) {
        & $tool.Command --exec ffprobe @arguments
    }
    else {
        & $tool.Command @arguments
    }

    if ($LASTEXITCODE -ne 0) {
        throw "ffprobe failed with exit code $LASTEXITCODE for $VideoPath"
    }

    return ($json | Out-String | ConvertFrom-Json)
}

function Assert-VideoProbe {
    param([string]$VideoPath)

    $probe = Invoke-FfprobeJson $VideoPath
    if ($null -eq $probe.streams -or $probe.streams.Count -lt 1) {
        throw "ffprobe did not return a video stream for $VideoPath"
    }

    $stream = @($probe.streams)[0]
    $width = [int]$stream.width
    $height = [int]$stream.height
    if ($width -ne $ExpectedOutputWidth -or $height -ne $ExpectedOutputHeight) {
        throw "10-minute output dimensions mismatch: ${width}x${height}, expected ${ExpectedOutputWidth}x${ExpectedOutputHeight}"
    }

    $frames = 0
    if ($null -ne $stream.nb_frames -and -not [string]::IsNullOrWhiteSpace([string]$stream.nb_frames)) {
        $frames = [int]$stream.nb_frames
    }
    if ($frames -lt $ExpectedOutputMinFrames) {
        throw "10-minute output frame count $frames is below expected minimum $ExpectedOutputMinFrames"
    }

    $durationValue = if ($null -ne $stream.duration -and -not [string]::IsNullOrWhiteSpace([string]$stream.duration)) {
        [string]$stream.duration
    }
    else {
        [string]$probe.format.duration
    }

    $duration = [double]::Parse($durationValue, [System.Globalization.CultureInfo]::InvariantCulture)
    if ($duration -lt $ExpectedOutputMinDurationSeconds -or $duration -gt $ExpectedOutputMaxDurationSeconds) {
        throw "10-minute output duration $duration is outside expected range $ExpectedOutputMinDurationSeconds-$ExpectedOutputMaxDurationSeconds"
    }

    Write-Host "[YoloTenMinuteStateVerify] pass ten minute output probe width=$width, height=$height, frames=$frames, duration=$duration"
}

function Get-MaxProgressFrames {
    param([string]$Text)

    $maxFrames = 0
    foreach ($match in [regex]::Matches($Text, "\[AutoMaskPipe\] frames=(\d+)")) {
        $frames = [int]$match.Groups[1].Value
        $maxFrames = [Math]::Max($maxFrames, $frames)
    }

    return $maxFrames
}

Assert-Contains "plan runner marker" $plan "yolo-ten-minute-runner-state: prepared=true"
Assert-Contains "plan records runner path" $plan "scripts/run-yolo-ten-minute-full.ps1"
Assert-Contains "plan records ten minute clip" $plan ".tmp/srcTest-smoke/smoke-0200-600s.mp4"
Assert-Contains "plan records optimized full run" $plan "full-run=yolo-optimized-only-pass"
Assert-Contains "plan records baseline-only runner support" $plan "baseline-only-runner=short-smoke-pass"
Assert-Contains "plan records incomplete baseline-only full attempt" $plan "baseline-only-full=attempted-incomplete-slow"
Assert-Contains "plan records incomplete baseline-only full progress" $plan "baseline-only-full-progress=240frames-no-complete"
Assert-Contains "plan records baseline-only log pattern" $plan "baseline-only-log-pattern=.tmp/yolo-ten-minute-baseline-smoke/yolo-ten-minute-baseline-only-*.log"
Assert-Contains "plan records faceonnx optimized-only runner support" $plan "faceonnx-optimized-only-runner=short-smoke-pass"
Assert-Contains "plan records faceonnx optimized-only log pattern" $plan "faceonnx-optimized-only-log-pattern=.tmp/yolo-ten-minute-faceonnx-optimized-smoke/yolo-ten-minute-faceonnx-optimized-only-*.log"
Assert-Contains "plan records partial speed compare runner support" $plan "partial-speed-compare=short-smoke-pass"
Assert-Contains "plan records partial speed yolo total" $plan "partial-yolo-totalMs=20720"
Assert-Contains "plan records partial speed faceonnx total" $plan "partial-faceonnx-totalMs=34039"
Assert-Contains "plan records partial speed ratio" $plan "partial-faceonnx-yolo-ratio=1.643"
Assert-Contains "plan records ten minute log" $plan ".tmp/yolo-ten-minute/yolo-ten-minute-20260523-000044.log"
Assert-Contains "plan records ten minute auto total" $plan "autoTotalMs=2536529"
Assert-Contains "plan records ten minute export total" $plan "exportTotalMs=1375350"
Assert-Contains "plan records ten minute direct face frames" $plan "directFaceFrames=8063"
Assert-Contains "plan keeps ten minute incomplete" $plan "미완료: 10min-full"

Assert-Match "runner default is 600 seconds" $runner '\[int\]\$Seconds\s*=\s*600'
Assert-Contains "runner uses yolo5face profile" $runner '[string]$YoloModelType = "Yolo5Face"'
Assert-Match "runner uses objectness 0.12" $runner '\[double\]\$YoloObjectnessThreshold\s*=\s*0\.12'
Assert-Match "runner uses confidence 0.18" $runner '\[double\]\$YoloConfidenceThreshold\s*=\s*0\.18'
Assert-Match "runner uses nms 0.45" $runner '\[double\]\$YoloNmsThreshold\s*=\s*0\.45'
Assert-Contains "runner can include baseline" $runner "[switch]`$RunBaseline"
Assert-Contains "runner can run baseline only" $runner "[switch]`$BaselineOnly"
Assert-Contains "runner can run faceonnx optimized only" $runner "[switch]`$FaceOnnxOptimizedOnly"
Assert-Contains "runner can skip export" $runner "[switch]`$SkipExport"
Assert-Contains "runner can dump detections" $runner "[switch]`$DumpDetections"
Assert-Contains "runner can dump compare details" $runner "[switch]`$DumpCompareDetails"
Assert-Contains "runner can dump compare overlays" $runner "[switch]`$DumpCompareOverlays"
Assert-Contains "runner can dump compare crops" $runner "[switch]`$DumpCompareCrops"
Assert-Contains "runner can allow quality failure" $runner "[switch]`$AllowQualityFailure"
Assert-Contains "runner names baseline-only logs" $runner 'yolo-ten-minute-$modeName-$timestamp.log'
Assert-Contains "runner names faceonnx optimized-only mode" $runner "faceonnx-optimized-only"
Assert-Contains "runner records faceonnx optimized-only flag" $runner "faceOnnxOptimizedOnly="
Assert-Contains "runner streams log lines" $runner "ForEach-Object"
Assert-Contains "runner writes incremental log" $runner "Add-Content -Encoding UTF8 -Path `$logPath"
Assert-Contains "partial speed compare runner calls ten minute runner" (Get-Content -Raw -Path $resolvedPartialSpeedCompareRunnerPath) "run-yolo-ten-minute-full.ps1"
Assert-Contains "partial speed compare runner reports summary" (Get-Content -Raw -Path $resolvedPartialSpeedCompareRunnerPath) "[YoloPartialSpeedCompare] summary"
Assert-Contains "smoke can skip optimized case" $smoke "[switch]`$SkipOptimized"
Assert-Contains "smoke guards empty run selection" $smoke "[SmokeRun] no cases selected"

$sourceInfo = Get-Item $resolvedSourcePath
if ($sourceInfo.Length -lt 2000000000) {
    throw "Source video is smaller than expected for srcTest full input: $($sourceInfo.Length)"
}
Write-Host "[YoloTenMinuteStateVerify] pass source video size"

if ($RequireClip) {
    if (-not (Test-Path $resolvedClipPath)) {
        throw "Required 10-minute clip not found: $resolvedClipPath"
    }

    $clipInfo = Get-Item $resolvedClipPath
    if ($clipInfo.Length -lt 1000000000) {
        throw "10-minute clip is smaller than expected: $($clipInfo.Length)"
    }

    Write-Host "[YoloTenMinuteStateVerify] pass ten minute clip prepared"
}

if ($RequireRun) {
    foreach ($required in @($resolvedLogPath, $resolvedOutputPath)) {
        if (-not (Test-Path $required)) {
            throw "Required 10-minute run artifact not found: $required"
        }
    }

    $log = Get-Content -Raw -Path $resolvedLogPath
    Assert-Contains "run log has yolo detector summary" $log "detector=YoloFaceOnnxDetector"
    Assert-Contains "run log has frame count" $log "totalFrames=17984"
    Assert-Contains "run log has processed count" $log "processed=17982"
    Assert-Contains "run log has yolo auto total" $log "totalMs=2536529"
    Assert-Contains "run log has track post summary" $log "tracks=2644, filled=5492, lostFilled=1762"
    Assert-Contains "run log has roi summary" $log "attempts=32, hits=6"
    Assert-Contains "run log has export summary" $log "frames=17984, bitmapMaskFrames=0, directFaceFrames=8063"
    Assert-Contains "run log has export total" $log "totalMs=1375350"

    $outputInfo = Get-Item $resolvedOutputPath
    if ($outputInfo.Length -lt 1000000000) {
        throw "10-minute output is smaller than expected: $($outputInfo.Length)"
    }

    Assert-VideoProbe $resolvedOutputPath
    Write-Host "[YoloTenMinuteStateVerify] pass ten minute run artifacts"
}

if ($RequireBaselineOnlyRun) {
    $resolvedBaselineOnlyLogPath = Resolve-LatestLog $BaselineOnlyLogPath $resolvedBaselineOnlyLogDir $BaselineOnlyLogPattern
    if (-not (Test-Path $resolvedBaselineOnlyLogPath)) {
        throw "Required baseline-only run log not found: $resolvedBaselineOnlyLogPath"
    }

    $baselineLog = Get-Content -Raw -Path $resolvedBaselineOnlyLogPath
    Assert-Contains "baseline-only log has baseline label" $baselineLog "label=baseline-all-frames"
    Assert-Contains "baseline-only log has faceonnx detector" $baselineLog "detector=FaceOnnxDetector/CPU"
    Assert-Contains "baseline-only log completed" $baselineLog "[YoloTenMinuteFull] complete exitCode=0"
    Assert-NotContains "baseline-only log has no optimized case" $baselineLog "optimized-"
    Assert-NotContains "baseline-only log has no yolo detector" $baselineLog "detector=YoloFaceOnnxDetector"

    $match = [regex]::Match($baselineLog, "totalFrames=(\d+)")
    if (-not $match.Success) {
        throw "baseline-only log missing totalFrames"
    }

    $frames = [int]$match.Groups[1].Value
    if ($frames -lt $BaselineOnlyMinFrames) {
        throw "baseline-only totalFrames $frames is below expected minimum $BaselineOnlyMinFrames"
    }

    Write-Host "[YoloTenMinuteStateVerify] pass baseline-only run artifacts"
}

if ($RequireIncompleteBaselineFullAttempt) {
    $resolvedIncompleteBaselineFullLogPath = Resolve-LatestLog $IncompleteBaselineFullLogPath $resolvedIncompleteBaselineFullLogDir $IncompleteBaselineFullLogPattern
    if (-not (Test-Path $resolvedIncompleteBaselineFullLogPath)) {
        throw "Required incomplete baseline full attempt log not found: $resolvedIncompleteBaselineFullLogPath"
    }

    $incompleteLog = Get-Content -Raw -Path $resolvedIncompleteBaselineFullLogPath
    Assert-Contains "incomplete baseline-full log has baseline-only mode" $incompleteLog "baselineOnly=True"
    Assert-Contains "incomplete baseline-full log has faceonnx detector path" $incompleteLog "label=baseline-all-frames"
    Assert-Contains "incomplete baseline-full log has pipe-single mode" $incompleteLog "[AutoMask] mode=pipe-single"
    Assert-NotContains "incomplete baseline-full log is not complete" $incompleteLog "[YoloTenMinuteFull] complete"
    Assert-NotContains "incomplete baseline-full log has no yolo detector" $incompleteLog "detector=YoloFaceOnnxDetector"

    $maxFrames = Get-MaxProgressFrames $incompleteLog
    if ($maxFrames -lt $IncompleteBaselineFullMinProgressFrames) {
        throw "incomplete baseline-full max progress frames $maxFrames is below expected minimum $IncompleteBaselineFullMinProgressFrames"
    }

    if ($maxFrames -gt $IncompleteBaselineFullMaxProgressFrames) {
        throw "incomplete baseline-full max progress frames $maxFrames is above expected maximum $IncompleteBaselineFullMaxProgressFrames"
    }

    Write-Host "[YoloTenMinuteStateVerify] pass incomplete baseline-full attempt maxProgressFrames=$maxFrames"
}

if ($RequireFaceOnnxOptimizedOnlyRun) {
    $resolvedFaceOnnxOptimizedOnlyLogPath = Resolve-LatestLog $FaceOnnxOptimizedOnlyLogPath $resolvedFaceOnnxOptimizedOnlyLogDir $FaceOnnxOptimizedOnlyLogPattern
    if (-not (Test-Path $resolvedFaceOnnxOptimizedOnlyLogPath)) {
        throw "Required FaceONNX optimized-only run log not found: $resolvedFaceOnnxOptimizedOnlyLogPath"
    }

    $faceOnnxOptimizedLog = Get-Content -Raw -Path $resolvedFaceOnnxOptimizedOnlyLogPath
    Assert-Contains "faceonnx optimized-only log has flag" $faceOnnxOptimizedLog "faceOnnxOptimizedOnly=True"
    Assert-Contains "faceonnx optimized-only log has faceonnx detector" $faceOnnxOptimizedLog "detector=FaceOnnxDetector/CPU"
    Assert-Contains "faceonnx optimized-only log has parallel mode" $faceOnnxOptimizedLog "mode=pipe-parallel"
    Assert-Contains "faceonnx optimized-only log completed" $faceOnnxOptimizedLog "[YoloTenMinuteFull] complete exitCode=0"
    Assert-NotContains "faceonnx optimized-only log has no baseline case" $faceOnnxOptimizedLog "label=baseline-all-frames"
    Assert-NotContains "faceonnx optimized-only log has no yolo detector" $faceOnnxOptimizedLog "detector=YoloFaceOnnxDetector"

    $match = [regex]::Match($faceOnnxOptimizedLog, "totalFrames=(\d+)")
    if (-not $match.Success) {
        throw "FaceONNX optimized-only log missing totalFrames"
    }

    $frames = [int]$match.Groups[1].Value
    if ($frames -lt $FaceOnnxOptimizedOnlyMinFrames) {
        throw "FaceONNX optimized-only totalFrames $frames is below expected minimum $FaceOnnxOptimizedOnlyMinFrames"
    }

    Write-Host "[YoloTenMinuteStateVerify] pass faceonnx optimized-only run artifacts"
}

if ($RequirePartialSpeedCompareRun) {
    $partialYoloLog = Resolve-LatestLog "" $resolvedPartialSpeedYoloLogDir "yolo-ten-minute-yolo-only-*.log"
    $partialFaceOnnxLog = Resolve-LatestLog "" $resolvedPartialSpeedFaceOnnxLogDir "yolo-ten-minute-faceonnx-optimized-only-*.log"
    $partialYoloText = Get-Content -Raw -Path $partialYoloLog
    $partialFaceOnnxText = Get-Content -Raw -Path $partialFaceOnnxLog

    Assert-Contains "partial speed yolo log has yolo detector" $partialYoloText "detector=YoloFaceOnnxDetector"
    Assert-Contains "partial speed yolo log completed" $partialYoloText "[YoloTenMinuteFull] complete exitCode=0"
    Assert-Contains "partial speed faceonnx log has faceonnx detector" $partialFaceOnnxText "detector=FaceOnnxDetector/CPU"
    Assert-Contains "partial speed faceonnx log completed" $partialFaceOnnxText "[YoloTenMinuteFull] complete exitCode=0"

    foreach ($entry in @(
        @{ Name = "partial speed yolo"; Text = $partialYoloText },
        @{ Name = "partial speed faceonnx"; Text = $partialFaceOnnxText }
    )) {
        $match = [regex]::Match($entry.Text, "totalFrames=(\d+)")
        if (-not $match.Success) {
            throw "$($entry.Name) log missing totalFrames"
        }

        $frames = [int]$match.Groups[1].Value
        if ($frames -lt $PartialSpeedMinFrames) {
            throw "$($entry.Name) totalFrames $frames is below expected minimum $PartialSpeedMinFrames"
        }
    }

    Write-Host "[YoloTenMinuteStateVerify] pass partial speed compare artifacts"
}

Write-Host "[YoloTenMinuteStateVerify] all requested checks passed"
