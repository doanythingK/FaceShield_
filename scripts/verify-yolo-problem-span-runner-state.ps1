param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$runnerPath = Join-Path $repo "scripts\run-yolo-problem-span-verification.ps1"
$guidePath = Join-Path $repo "YOLO_PROBLEM_SPAN_VERIFICATION.md"
$planPath = Join-Path $repo "AUTO_MOSAIC_QUALITY_SPEED_PLAN.md"
$smokePath = Join-Path $repo "YOLO_GUI_SMOKE_RESULT.md"

function Assert-File {
    param([string]$Name, [string]$Path)

    if (-not (Test-Path $Path)) {
        throw "$Name not found: $Path"
    }
}

function Assert-Match {
    param([string]$Name, [string]$Text, [string]$Pattern)

    if ($Text -notmatch $Pattern) {
        throw "$Name missing pattern: $Pattern"
    }

    Write-Host "[YoloProblemSpanRunnerVerify] pass $Name"
}

Assert-File "problem-span runner" $runnerPath
Assert-File "problem-span guide" $guidePath
Assert-File "auto mosaic plan" $planPath
Assert-File "yolo smoke result" $smokePath

$runner = Get-Content -Raw -Path $runnerPath
$guide = Get-Content -Raw -Path $guidePath
$plan = Get-Content -Raw -Path $planPath
$smoke = Get-Content -Raw -Path $smokePath

Assert-Match "runner requires source" $runner '\[Parameter\(Mandatory\s*=\s*\$true\)\]\s*\r?\n\s*\[string\]\$Source'
Assert-Match "runner requires trim start" $runner '\[Parameter\(Mandatory\s*=\s*\$true\)\]\s*\r?\n\s*\[string\]\$TrimStart'
Assert-Match "runner requires bounded trim seconds" $runner '\[Parameter\(Mandatory\s*=\s*\$true\)\]\s*\r?\n\s*\[ValidateRange\(1,\s*30\)\]\s*\r?\n\s*\[int\]\$TrimSeconds'
Assert-Match "runner defaults to yolo5" $runner '\[ValidateSet\("YoloV8Face",\s*"Yolo5Face"\)\][\s\S]*\$YoloModelType\s*=\s*"Yolo5Face"'
Assert-Match "runner uses followup wrapper" $runner 'write-yolo-followup-quality-evidence\.ps1[\s\S]*-RunSmoke[\s\S]*-TrimStart[\s\S]*-TrimSeconds[\s\S]*-OutputDir'
Assert-Match "runner forwards yolo thresholds" $runner '-YoloObjectnessThreshold[\s\S]*-YoloConfidenceThreshold[\s\S]*-YoloNmsThreshold'
Assert-Match "runner skips review package by default" $runner 'if\s*\(-not\s*\$WithReviewPackage\.IsPresent\)[\s\S]*-SkipReviewPackage'
Assert-Match "runner supports forced rerun" $runner 'if\s*\(\$Force\.IsPresent\)[\s\S]*-ForceTrim[\s\S]*-ForceRunSmoke'

if ($runner -match "AllowLongSmokeSource") {
    throw "runner should not expose or forward AllowLongSmokeSource"
}
Write-Host "[YoloProblemSpanRunnerVerify] pass runner does not expose long source override"

Assert-Match "guide uses runner" $guide 'scripts/run-yolo-problem-span-verification\.ps1[\s\S]*-TrimStart[\s\S]*-TrimSeconds'
Assert-Match "guide says no full video smoke override" $guide '-AllowLongSmokeSource'
Assert-Match "guide records wrapper smoke evidence" $guide 'Wrapper Smoke[\s\S]*yolo-problem-span-wrapper-smoke[\s\S]*Detection rows:\s*`96`[\s\S]*removedFrames=33,34,35[\s\S]*blockedByCleanup=3[\s\S]*cleanupBlockedFrames=33,34,35'
Assert-Match "guide documents cleanup-block pass criteria" $guide 'blockedByCleanup=\.\.\.[\s\S]*cleanupBlockedFrames=\.\.\.[\s\S]*후속 anti-flicker fill'
Assert-Match "guide documents high-confidence scene carry cleanup" $guide 'at or below `0\.95` confidence[\s\S]*RemoveSceneCutCarryRemnants[\s\S]*anti-flicker pass cannot recreate the same scene-transition residue'
Assert-Match "plan links runner" $plan 'scripts/run-yolo-problem-span-verification\.ps1[\s\S]*caps the problem span at 30 seconds'
Assert-Match "smoke result links runner" $smoke 'scripts/run-yolo-problem-span-verification\.ps1[\s\S]*short-span entrypoint'
Assert-Match "smoke result records current high-confidence validation" $smoke 'Current validation after the high-confidence scene-carry and medium-risk anchor update[\s\S]*verify-yolo-final-mask-cleanup\.ps1[\s\S]*mediumRiskyAnchorGapFilled=0[\s\S]*mediumRiskyAnchorSuppressed=1[\s\S]*supportedMediumRiskyAnchorGapFilled=2[\s\S]*dotnet build FaceShield\.sln[\s\S]*SmokeQualityGate passed=True[\s\S]*avgBestIou=1\.000[\s\S]*largeJumpGaps=0'

Write-Host "[YoloProblemSpanRunnerVerify] all requested checks passed"
