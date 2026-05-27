param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$runnerPath = Join-Path $repo "scripts\run-yolo-problem-span-verification.ps1"
$srcSmokeHarnessPath = Join-Path $repo "scripts\run-srcTest-smoke.ps1"
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
Assert-File "srcTest smoke harness" $srcSmokeHarnessPath
Assert-File "problem-span guide" $guidePath
Assert-File "auto mosaic plan" $planPath
Assert-File "yolo smoke result" $smokePath

$runner = Get-Content -Raw -Path $runnerPath
$srcSmokeHarness = Get-Content -Raw -Path $srcSmokeHarnessPath
$guide = Get-Content -Raw -Path $guidePath
$plan = Get-Content -Raw -Path $planPath
$smoke = Get-Content -Raw -Path $smokePath

Assert-Match "runner requires source" $runner '\[Parameter\(Mandatory\s*=\s*\$true\)\]\s*\r?\n\s*\[string\]\$Source'
Assert-Match "runner requires trim start" $runner '\[Parameter\(Mandatory\s*=\s*\$true\)\]\s*\r?\n\s*\[string\]\$TrimStart'
Assert-Match "runner requires bounded trim seconds" $runner '\[Parameter\(Mandatory\s*=\s*\$true\)\]\s*\r?\n\s*\[ValidateRange\(1,\s*30\)\]\s*\r?\n\s*\[int\]\$TrimSeconds'
Assert-Match "runner defaults to yolo5" $runner '\[ValidateSet\("YoloV8Face",\s*"Yolo5Face"\)\][\s\S]*\$YoloModelType\s*=\s*"Yolo5Face"'
Assert-Match "runner uses followup wrapper" $runner 'write-yolo-followup-quality-evidence\.ps1[\s\S]*-RunSmoke[\s\S]*-TrimStart[\s\S]*-TrimSeconds[\s\S]*-OutputDir'
Assert-Match "runner forwards yolo thresholds" $runner '-YoloObjectnessThreshold[\s\S]*-YoloConfidenceThreshold[\s\S]*-YoloNmsThreshold'
Assert-Match "runner forwards pseudo gt tile face csv" $runner '\[string\]\$PseudoGtTileFaceCsv[\s\S]*-PseudoGtTileFaceCsv'
Assert-Match "runner forwards pseudo gt face verification csv" $runner '\[string\]\$PseudoGtFaceVerificationCsv[\s\S]*-PseudoGtFaceVerificationCsv'
Assert-Match "runner forwards pseudo gt person object csv" $runner '\[string\]\$PseudoGtPersonObjectCsv[\s\S]*-PseudoGtPersonObjectCsv'
Assert-Match "runner supports no-detection evidence" $runner '\[switch\]\$AllowNoDetections[\s\S]*-AllowNoDetections'
Assert-Match "runner skips review package by default" $runner 'if\s*\(-not\s*\$WithReviewPackage\.IsPresent\)[\s\S]*-SkipReviewPackage'
Assert-Match "runner supports detection overlay video" $runner '\[switch\]\$WithDetectionOverlayVideo[\s\S]*-WithDetectionOverlayVideo'
Assert-Match "runner supports review contact sheet" $runner '\[switch\]\$WithReviewContactSheet[\s\S]*-WithReviewContactSheet'
Assert-Match "runner supports pseudo gt tile input manifest" $runner '\[switch\]\$WithPseudoGtTileInput[\s\S]*-WithPseudoGtTileInput[\s\S]*-PseudoGtTileColumns[\s\S]*-PseudoGtTileRows[\s\S]*-PseudoGtTileOverlapRatio'
Assert-Match "runner supports pseudo gt tile manifest without image extraction" $runner '\[switch\]\$PseudoGtTileSkipImageExtraction[\s\S]*-PseudoGtTileSkipImageExtraction'
Assert-Match "runner supports pseudo gt face verification input manifest" $runner '\[switch\]\$WithPseudoGtFaceVerificationInput[\s\S]*-WithPseudoGtFaceVerificationInput[\s\S]*-PseudoGtFaceVerificationCropPaddingRatio'
Assert-Match "runner supports pseudo gt face verification manifest without image extraction" $runner '\[switch\]\$PseudoGtFaceVerificationSkipImageExtraction[\s\S]*-PseudoGtFaceVerificationSkipImageExtraction'
Assert-Match "runner supports pseudo gt face verification external command" $runner 'PseudoGtFaceVerificationExternalCommand[\s\S]*-PseudoGtFaceVerificationExternalCommand[\s\S]*PseudoGtFaceVerificationExternalOutputCsv[\s\S]*-PseudoGtFaceVerificationExternalOutputCsv'
Assert-Match "runner supports pseudo gt person object input manifest" $runner '\[switch\]\$WithPseudoGtPersonObjectInput[\s\S]*-WithPseudoGtPersonObjectInput[\s\S]*-PseudoGtPersonObjectScaleWidth'
Assert-Match "runner supports pseudo gt person object manifest without image extraction" $runner '\[switch\]\$PseudoGtPersonObjectSkipImageExtraction[\s\S]*-PseudoGtPersonObjectSkipImageExtraction'
Assert-Match "runner supports pseudo gt person object external command" $runner 'PseudoGtPersonObjectExternalCommand[\s\S]*-PseudoGtPersonObjectExternalCommand[\s\S]*PseudoGtPersonObjectExternalOutputCsv[\s\S]*-PseudoGtPersonObjectExternalOutputCsv'
Assert-Match "runner supports forced rerun" $runner 'if\s*\(\$Force\.IsPresent\)[\s\S]*-ForceTrim[\s\S]*-ForceRunSmoke'
Assert-Match "srcTest smoke cleans generated harness by default" $srcSmokeHarness '\[switch\]\$KeepHarness[\s\S]*finally\s*\{[\s\S]*-not\s+\$KeepHarness\.IsPresent[\s\S]*Remove-Item\s+-Recurse\s+-Force\s+-Path\s+\$harness'

if ($runner -match "AllowLongSmokeSource") {
    throw "runner should not expose or forward AllowLongSmokeSource"
}
Write-Host "[YoloProblemSpanRunnerVerify] pass runner does not expose long source override"

Assert-Match "guide uses runner" $guide 'scripts/run-yolo-problem-span-verification\.ps1[\s\S]*-TrimStart[\s\S]*-TrimSeconds'
Assert-Match "guide documents detection overlay option" $guide '-WithDetectionOverlayVideo[\s\S]*yolo-detection-overlay\.mp4'
Assert-Match "guide documents contact sheet option" $guide '-WithReviewContactSheet[\s\S]*yolo-review-contact-sheet\.png'
Assert-Match "guide documents pseudo gt evidence" $guide 'Pseudo-GT[\s\S]*faceVerificationConfidence[\s\S]*faceVerificationDistance'
Assert-Match "guide documents pseudo gt tile input" $guide 'WithPseudoGtTileInput[\s\S]*new-yolo-pseudo-gt-tile-input\.ps1[\s\S]*-TileColumns[\s\S]*-ExternalCommand[\s\S]*tileImagePath[\s\S]*tileSupportCount'
Assert-Match "guide documents pseudo gt face verification input" $guide 'WithPseudoGtFaceVerificationInput[\s\S]*new-yolo-pseudo-gt-face-verification-input\.ps1[\s\S]*face-verification-manifest\.csv[\s\S]*faceVerificationConfidence'
Assert-Match "guide documents pseudo gt person object input" $guide 'WithPseudoGtPersonObjectInput[\s\S]*new-yolo-pseudo-gt-person-object-input\.ps1[\s\S]*person-object-manifest\.csv[\s\S]*personUpperOverlap'
Assert-Match "guide documents pseudo gt frame-set cap" $guide 'MaxFrames=900[\s\S]*-AllowLargeFrameSet'
Assert-Match "guide says no full video smoke override" $guide '-AllowLongSmokeSource'
Assert-Match "guide records wrapper smoke evidence" $guide 'Wrapper Smoke[\s\S]*yolo-problem-span-wrapper-smoke[\s\S]*Detection rows:\s*`96`[\s\S]*removedFrames=33,34,35[\s\S]*blockedByCleanup=3[\s\S]*cleanupBlockedFrames=33,34,35'
Assert-Match "guide documents cleanup-block pass criteria" $guide 'blockedByCleanup=\.\.\.[\s\S]*cleanupBlockedFrames=\.\.\.[\s\S]*후속 anti-flicker fill'
Assert-Match "guide documents high-confidence scene carry cleanup" $guide 'at or below `0\.98` confidence[\s\S]*RemoveSceneCutCarryRemnants[\s\S]*anti-flicker pass cannot recreate the same scene-transition residue'
Assert-Match "plan links runner" $plan 'scripts/run-yolo-problem-span-verification\.ps1[\s\S]*caps the problem span at 30 seconds'
Assert-Match "smoke result links runner" $smoke 'scripts/run-yolo-problem-span-verification\.ps1[\s\S]*short-span entrypoint'
Assert-Match "smoke result records current high-confidence validation" $smoke 'Current validation after the high-confidence scene-carry, medium-risk anchor, per-face cleanup block, inward-initial-fill, 8-frame scene-carry block, and extended weak carry cleanup update[\s\S]*scripts/verify-yolo-final-mask-cleanup\.ps1[\s\S]*mediumRiskyAnchorGapFilled=0[\s\S]*mediumRiskyAnchorSuppressed=1[\s\S]*supportedMediumRiskyAnchorGapFilled=2[\s\S]*partialCleanupBlockedGapFilled=0[\s\S]*partialSceneCarryRefillBlocked=3[\s\S]*partialSceneCarryBlockedFrames=3101,3102,3103[\s\S]*sceneCutCarryRemoved=17[\s\S]*emptyPostCutRemovedUnsupportedStrong=2[\s\S]*sceneCutCarryBlockedFrames=1001,1002,1003,1004,1005,1006,1007,1008,2001,2002,2003,2004,2005,2006,2007,2008,2009,2010,2011[\s\S]*Windows `dotnet build C:\\testdev\\FaceShield\.sln` passed[\s\S]*SmokeQualityGate passed=True[\s\S]*avgBestIou=1\.000[\s\S]*minBestIou=1\.000'
Assert-Match "smoke result records current review package evidence" $smoke 'Current short problem-span review package[\s\S]*\.tmp/yolo-goal-current-perface-0900-review/yolo-followup-quality-evidence\.md[\s\S]*review-package/review-index\.html[\s\S]*96` crop review rows[\s\S]*32` full-frame review rows[\s\S]*residual edge/top-edge rows now keep `reviewRequired=True`[\s\S]*frames `4`, `24`, and `32`'
Assert-Match "guide records current review package evidence" $guide 'current HEAD review-package run[\s\S]*\.tmp/yolo-goal-current-perface-0900-review/yolo-followup-quality-evidence\.md[\s\S]*Review rows:\s*`96` crop rows,\s*`32` full-frame rows[\s\S]*Partial overlay observation: frames `4`, `24`, and `32`'

Write-Host "[YoloProblemSpanRunnerVerify] all requested checks passed"
