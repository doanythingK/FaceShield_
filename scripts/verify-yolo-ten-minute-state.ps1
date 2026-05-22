param(
    [string]$PlanPath = "AUTO_MOSAIC_QUALITY_SPEED_PLAN.md",
    [string]$RunnerPath = "scripts\run-yolo-ten-minute-full.ps1",
    [string]$SourcePath = "srcTest\260102_jp_10.mp4",
    [string]$ClipPath = ".tmp\srcTest-smoke\smoke-0200-600s.mp4",
    [string]$LogPath = ".tmp\yolo-ten-minute\yolo-ten-minute-20260523-000044.log",
    [string]$OutputPath = ".tmp\srcTest-smoke\smoke-0200-600s_blur.mp4",
    [switch]$RequireClip,
    [switch]$RequireRun
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$resolvedPlanPath = if ([IO.Path]::IsPathRooted($PlanPath)) { $PlanPath } else { Join-Path $repo $PlanPath }
$resolvedRunnerPath = if ([IO.Path]::IsPathRooted($RunnerPath)) { $RunnerPath } else { Join-Path $repo $RunnerPath }
$resolvedSourcePath = if ([IO.Path]::IsPathRooted($SourcePath)) { $SourcePath } else { Join-Path $repo $SourcePath }
$resolvedClipPath = if ([IO.Path]::IsPathRooted($ClipPath)) { $ClipPath } else { Join-Path $repo $ClipPath }
$resolvedLogPath = if ([IO.Path]::IsPathRooted($LogPath)) { $LogPath } else { Join-Path $repo $LogPath }
$resolvedOutputPath = if ([IO.Path]::IsPathRooted($OutputPath)) { $OutputPath } else { Join-Path $repo $OutputPath }

foreach ($required in @($resolvedPlanPath, $resolvedRunnerPath, $resolvedSourcePath)) {
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

Assert-Contains "plan runner marker" $plan "yolo-ten-minute-runner-state: prepared=true"
Assert-Contains "plan records runner path" $plan "scripts/run-yolo-ten-minute-full.ps1"
Assert-Contains "plan records ten minute clip" $plan ".tmp/srcTest-smoke/smoke-0200-600s.mp4"
Assert-Contains "plan records optimized full run" $plan "full-run=yolo-optimized-only-pass"
Assert-Contains "plan records baseline-only runner support" $plan "baseline-only-runner=short-smoke-pass"
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
Assert-Contains "runner can skip export" $runner "[switch]`$SkipExport"
Assert-Contains "runner can allow quality failure" $runner "[switch]`$AllowQualityFailure"
Assert-Contains "runner streams log lines" $runner "ForEach-Object"
Assert-Contains "runner writes incremental log" $runner "Add-Content -Encoding UTF8 -Path `$logPath"
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

    Write-Host "[YoloTenMinuteStateVerify] pass ten minute run artifacts"
}

Write-Host "[YoloTenMinuteStateVerify] all requested checks passed"
