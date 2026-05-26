param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$work = Join-Path $repo ".tmp\yolo-followup-quality-evidence-verify"
$log = Join-Path $work "synthetic-yolo.log"
$outDir = Join-Path $work "out"
$script = Join-Path $repo "scripts\write-yolo-followup-quality-evidence.ps1"
$plan = Join-Path $repo "AUTO_MOSAIC_QUALITY_SPEED_PLAN.md"
$smokeResult = Join-Path $repo "YOLO_GUI_SMOKE_RESULT.md"

function Assert-Contains {
    param(
        [string]$Name,
        [string]$Text,
        [string]$Pattern
    )

    if ($Text -notmatch $Pattern) {
        throw "$Name did not contain expected pattern: $Pattern"
    }

    Write-Host "[YoloFollowupQualityEvidenceVerify] pass $Name"
}

if (-not (Test-Path $script)) {
    throw "Script not found: $script"
}

New-Item -ItemType Directory -Force -Path $work | Out-Null

@'
[AutoRunSummary] runId=synthetic, detector=YoloFaceOnnxDetector/CPU, mode=pipe-parallel, totalFrames=12, startFrame=0, processed=12, decoded=12, detects=12, interpolated=0, readMs=0, decodeMs=10, detectMs=20, maskMs=0, totalMs=30, downscale=1.000, quality=BalancedBilinear, tracking=True, everyN=1, parallel=2, roi=regular=3, small=0, rejected=1, statsRejected=0
[SmokeFaceTrackPost] label=synthetic-yolo, tracks=1, filled=1, lostFilled=2, lostFrames=4,5, removedShort=1, removedSparse=1, removedUnstableTail=1, removedEdgeTail=1, removedLower=0, rewritten=6
[SmokeFaceTrackSceneCutGuard] label=synthetic-yolo, directCandidates=1, postCutCandidates=2, checked=2, checkedPairs=2->3,5->6, maxDiff=0.410, cutPairs=5->6, removed=1, removedFrames=6, threshold=0.320, elapsedMs=3, error=none
[SmokeYoloStrongCarrySceneCutProbe] label=synthetic-yolo, candidates=1, checked=1, directChecked=1, directSkipped=0, checkedPairs=13->14, maxDiff=0.520, cutPairs=13->14, threshold=0.150, elapsedMs=2, error=none
[SmokeYoloFinalMaskCleanup] label=synthetic-yolo, removedWeakIsolated=4, removedWeakUnsupported=1, removedMediumUnsupported=0, removedWeakShortClusters=0, removedWeakTinyClusters=0, removedTinyShortClusters=2, removedTinyIsolated=1, removedTopEdgeWeakClusters=0, removedUpperWeakClusters=0, removedLowerWeakClusters=0, removedAspectOutliers=0, removedFrames=8,10,11,12
[SmokeYoloFinalMaskGapFill] label=synthetic-yolo, filled=1, frames=5, blockedByCut=1, cutBlockedFrames=7, blockedByCleanup=1, cleanupBlockedFrames=8, blockedBySceneCarry=0, sceneCarryBlockedFrames=none
[SmokeYoloFinalMaskGapFillSceneCutGuard] label=synthetic-yolo, candidates=2, checked=2, checkedPairs=4->5,5->6, maxDiff=0.410, cutPairs=4->5, removed=1, removedFrames=5, threshold=0.320, elapsedMs=2, error=none
[SmokeYoloSceneCutCarryCleanup] label=synthetic-yolo, removed=3, removedFrames=6,7,13, removedUnsupportedStrong=1, removedUnsupportedStrongFrames=13, protectedStrong=2, protectedStrongFrames=14,15, blockedFrames=6,7,8,9,10,11,12,13, purgeFrames=5, blockFrames=8, extendedWeakMaxConfidence=0.78
[SmokeYoloFinalMaskPostSceneGapFill] label=synthetic-yolo, filled=0, frames=none, blockedByCut=1, cutBlockedFrames=7, blockedByCleanup=1, cleanupBlockedFrames=8, blockedBySceneCarry=2, sceneCarryBlockedFrames=6,7
[SmokeYoloFinalMaskPostSceneGapFillSceneCutGuard] label=synthetic-yolo, candidates=0, checked=0, checkedPairs=none, maxDiff=0.000, cutPairs=none, removed=0, removedFrames=none, threshold=0.000, elapsedMs=0, error=none
[SmokeYoloSceneCutCarryCleanup] stage=post-gap-fill label=synthetic-yolo, removed=2, removedFrames=18,19, removedUnsupportedStrong=1, removedUnsupportedStrongFrames=19, protectedStrong=1, protectedStrongFrames=20, blockedFrames=18,19,20,21,22,23,24,25, purgeFrames=5, blockFrames=8, extendedWeakMaxConfidence=0.78
[SmokeFinalMaskSummary] label=synthetic-yolo, frames=2, rows=2, frameRange=2-6, shortGaps=1, shortGapRanges=3-5, largeJumpGaps=1, largeJumpRanges=3-5, isolated=2, isolatedFrames=2,6, lowConf=1, lowConfFrames=2, weakNonEdge=1, weakNonEdgeFrames=2, edgeWeak=1, edgeWeakFrames=10, topEdgeWeak=1, topEdgeWeakFrames=10, upperWeak=1, upperWeakFrames=2, lowerWeak=1, lowerWeakFrames=6, aspectBad=1, aspectBadFrames=9, tinyWeak=1, tinyWeakFrames=2, tinyShort=1, tinyShortFrames=2, protectedSceneCarry=2, protectedSceneCarryFrames=14,15, reviewRequired=True, reviewReasons=short-gap,large-jump-gap,isolated-mask,low-confidence-review,weak-non-edge,edge-weak-review,top-edge-weak-review,upper-weak,lower-weak,aspect-outlier,tiny-weak,tiny-short,scene-carry-protected
[SmokeDetection] label=synthetic-yolo, frame=2, index=0, x=10.0, y=20.0, w=50.0, h=60.0, area=3000.0, conf=0.410, cx=0.055, cy=0.120, areaRatio=0.002000, aspectRatio=0.833
[SmokeDetection] label=synthetic-yolo, frame=6, index=0, x=500.0, y=440.0, w=180.0, h=180.0, area=32400.0, conf=0.220, cx=0.461, cy=0.736, areaRatio=0.015625, aspectRatio=1.000
[SmokeDetection] label=synthetic-yolo, frame=9, index=0, x=700.0, y=120.0, w=8.0, h=40.0, area=320.0, conf=0.180, cx=0.550, cy=0.222, areaRatio=0.000154, aspectRatio=0.200
[SmokeDetection] label=synthetic-yolo, frame=10, index=0, x=610.0, y=0.0, w=34.0, h=30.0, area=1020.0, conf=0.310, cx=0.490, cy=0.021, areaRatio=0.000492, aspectRatio=1.133
[SmokeDetectionSummary] label=synthetic-yolo, frames=4, detections=4, frameRange=2-10, confMin=0.180, confAvg=0.280, confMax=0.410, areaRatioMin=0.000210, areaRatioAvg=0.000748, areaRatioMax=0.002000, aspectRatioMin=0.833, aspectRatioAvg=0.959, aspectRatioMax=1.133
'@ | Set-Content -Encoding UTF8 -Path $log

$output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script `
    -PredictionLog $log `
    -OutputDir $outDir `
    -SkipReviewPackage 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "write-yolo-followup-quality-evidence.ps1 failed: $($output | Out-String)"
}

$checklist = Join-Path $outDir "yolo-quality-review-checklist.md"
$continuity = Join-Path $outDir "yolo-mask-continuity-report.md"
$template = Join-Path $outDir "yolo-quality-full-gt-template.csv"
$summary = Join-Path $outDir "yolo-followup-quality-evidence.md"

foreach ($required in @($checklist, $continuity, $template, $summary)) {
    if (-not (Test-Path $required)) {
        throw "Expected output was not created: $required"
    }
}

$scriptText = Get-Content -Raw -Path $script
$checklistText = Get-Content -Raw -Path $checklist
$continuityText = Get-Content -Raw -Path $continuity
$summaryText = Get-Content -Raw -Path $summary
$planText = Get-Content -Raw -Path $plan
$smokeText = Get-Content -Raw -Path $smokeResult

Assert-Contains "script can run smoke" $scriptText "RunSmoke"
Assert-Contains "script loads yolo model resolver" $scriptText "resolve-yolo-model-path\.ps1"
Assert-Contains "script resolves default yolo model for smoke" $scriptText 'Resolve-YoloModelPath[\s\S]*-Require[\s\S]*Add-ValueArg\s+\$smokeArgs\s+"-YoloModelPath"\s+\$resolvedYoloModelPath'
Assert-Contains "script passes skip trim" $scriptText 'Add-SwitchArg\s+\$smokeArgs\s+"-SkipTrim"'
Assert-Contains "script avoids direct native stderr failure" $scriptText "Invoke-PowerShellCapture"
Assert-Contains "script captures stderr through files" $scriptText "RedirectStandardError"
Assert-Contains "script supports smoke timeout" $scriptText "SmokeTimeoutSeconds"
Assert-Contains "script supports max smoke source seconds" $scriptText "MaxSmokeSourceSeconds"
Assert-Contains "script supports explicit long smoke override" $scriptText "AllowLongSmokeSource"
Assert-Contains "script checks smoke source duration" $scriptText "Assert-SmokeSourceScope[\s\S]*Get-VideoDurationSeconds"
Assert-Contains "script uses ffprobe metadata for duration" $scriptText "ffprobe[\s\S]*format=duration"
Assert-Contains "script blocks long smoke source by default" $scriptText "RunSmoke source is too long"
Assert-Contains "script supports no-detection evidence" $scriptText "AllowNoDetections"
Assert-Contains "script supports trim start" $scriptText "TrimStart"
Assert-Contains "script supports trim seconds" $scriptText "TrimSeconds"
Assert-Contains "script supports wsl ffmpeg trim" $scriptText "Convert-ToWslPath[\s\S]*wsl\.exe[\s\S]*ffmpeg"
Assert-Contains "script handles blank process exit code" $scriptText '\$exitCode\s*=\s*if\s*\(\$null\s+-eq\s+\$process\.ExitCode\)\s*\{\s*0\s*\}'
Assert-Contains "script enables dumped detections" $scriptText "DumpDetections"
Assert-Contains "script enables aspect filter" $scriptText "YoloUseAspectRatioFilter"
Assert-Contains "script parses final mask cleanup" $scriptText "SmokeYoloFinalMaskCleanup|YoloFinalMaskCleanup"
Assert-Contains "script parses strong carry scene-cut probe" $scriptText "SmokeYoloStrongCarrySceneCutProbe|YoloStrongCarrySceneCutProbe"
Assert-Contains "script parses scene-cut carry cleanup" $scriptText "SmokeYoloSceneCutCarryCleanup|YoloSceneCutCarryCleanup"
Assert-Contains "script preserves all scene-cut carry cleanup lines" $scriptText 'foreach\s*\(\$carryCleanup\s+in\s+\$sceneCutCarryCleanup\)'
Assert-Contains "script parses final mask post-scene cleanup" $scriptText "SmokeYoloFinalMaskPostSceneCleanup|YoloFinalMaskPostSceneCleanup"
Assert-Contains "script parses final mask gap fill" $scriptText "SmokeYoloFinalMaskPostSceneGapFill|YoloFinalMaskPostSceneGapFill|SmokeYoloFinalMaskGapFill|YoloFinalMaskGapFill"
Assert-Contains "script parses final mask gap-fill scene guard" $scriptText "SmokeYoloFinalMaskPostSceneGapFillSceneCutGuard|YoloFinalMaskPostSceneGapFillSceneCutGuard|SmokeYoloFinalMaskGapFillSceneCutGuard|YoloFinalMaskGapFillSceneCutGuard"
Assert-Contains "script parses final mask summary" $scriptText "SmokeFinalMaskSummary|FinalMaskSummary"
Assert-Contains "script writes review package" $scriptText "new-yolo-full-gt-review-package\.ps1"
Assert-Contains "script writes final mask continuity report" $scriptText "write-yolo-mask-continuity-report\.ps1"
Assert-Contains "script can write detection overlay video" $scriptText "WithDetectionOverlayVideo[\s\S]*new-yolo-detection-overlay-video\.ps1"
Assert-Contains "script can write review contact sheet" $scriptText "WithReviewContactSheet[\s\S]*new-yolo-review-contact-sheet\.ps1[\s\S]*Review contact sheet"
Assert-Contains "script can write no-detection contact sheet" $scriptText "Get-SampledReviewFrameNumbers[\s\S]*No detection rows were found[\s\S]*Sampled no-detection review frames"
Assert-Contains "script marks no-detection review required" $scriptText "No-detection review:[\s\S]*no-detection-frame-scan"
Assert-Contains "script writes detection overlay summary" $scriptText "Detection overlay video"
Assert-Contains "script reuses existing review package" $scriptText "ForceReviewPackage[\s\S]*review-index\.html"
Assert-Contains "script derives required full-frame review frames" $scriptText "Get-ReviewFrameNumbers[\s\S]*lostFrames=.*removedShort[\s\S]*checkedPairs=.*maxDiff"
Assert-Contains "script derives review frames from strong carry probe" $scriptText "StrongCarryProbeLines[\s\S]*checkedPairs=.*maxDiff[\s\S]*cutPairs=.*threshold"
Assert-Contains "script derives protected carry review frames" $scriptText "SceneCutCarryCleanupLines[\s\S]*removedUnsupportedStrongFrames=.*protectedStrong[\s\S]*protectedStrongFrames=.*blockedFrames"
Assert-Contains "script derives review frames from final summary gaps" $scriptText "FinalMaskSummaryLines[\s\S]*shortGapRanges=.*largeJumpGaps[\s\S]*largeJumpRanges=.*isolated="
Assert-Contains "script derives review frames from final summary residuals" $scriptText "lowConfFrames=.*weakNonEdge[\s\S]*weakNonEdgeFrames=.*edgeWeak[\s\S]*edgeWeakFrames=.*topEdgeWeak[\s\S]*topEdgeWeakFrames=.*upperWeak[\s\S]*upperWeakFrames=.*lowerWeak[\s\S]*lowerWeakFrames=.*aspectBad[\s\S]*aspectBadFrames=.*tinyWeak[\s\S]*tinyWeakFrames=.*tinyShort[\s\S]*tinyShortFrames=.*protectedSceneCarry[\s\S]*protectedSceneCarryFrames="
Assert-Contains "script derives review frames from low-confidence detections" $scriptText "LowConfidenceReviewThreshold[\s\S]*Add-LowConfidenceDetectionFrames[\s\S]*DetectionRows[\s\S]*conf="
Assert-Contains "script passes required full-frame review frames" $scriptText "RequiredFullFrameNumbers[\s\S]*reviewFrameNumbers"
Assert-Contains "script expands review video frame count for required frames" $scriptText "reviewVideoFrameCount[\s\S]*maxRequiredFrame\s*\+\s*1"
Assert-Contains "script writes summary" $scriptText "YOLO Follow-Up Quality Evidence"
Assert-Contains "checklist has scene-cut evidence" $checklistText "directCandidates=1"
Assert-Contains "checklist has post-cut scene-cut evidence" $checklistText "postCutCandidates=2"
Assert-Contains "checklist has strong carry scene-cut probe evidence" $checklistText "Strong carry scene-cut probe[\s\S]*probeCandidates=1[\s\S]*probeCutPairs=13->14"
Assert-Contains "checklist has false-positive label rule" $checklistText "do not treat YOLO or FaceONNX as ground truth"
Assert-Contains "continuity says rows are final masks" $continuityText 'final `FrameMaskProvider` face rectangles'
Assert-Contains "continuity reports short gaps" $continuityText "Short empty gaps: 2"
Assert-Contains "continuity reports isolated masks" $continuityText "Isolated final mask frames: 2"
Assert-Contains "continuity reports low confidence masks" $continuityText "Low-confidence final masks: 3"
Assert-Contains "continuity reports weak non-edge masks" $continuityText "Weak non-edge final masks: 2"
Assert-Contains "continuity reports weak edge masks" $continuityText "Weak edge final masks: 2"
Assert-Contains "continuity reports top-edge weak masks" $continuityText "Top-edge weak final masks: 1"
Assert-Contains "continuity reports upper-frame weak non-edge masks" $continuityText "Upper-frame weak non-edge final masks: 0"
Assert-Contains "continuity reports lower-frame weak non-edge masks" $continuityText "Lower-frame weak non-edge final masks: 1"
Assert-Contains "continuity reports aspect outliers" $continuityText "Aspect-ratio outlier final masks: 1"
Assert-Contains "continuity reports gap diagnostics" $continuityText "AreaChange[\s\S]*CenterShift[\s\S]*Review hint"
Assert-Contains "continuity reports large jump hint" $continuityText "large box jump; review before fill"
Assert-Contains "continuity includes weak non-edge table" $continuityText "Weak Non-Edge Final Masks[\s\S]*weak non-edge; review false positive"
Assert-Contains "continuity includes weak edge table" $continuityText "Weak Edge Final Masks[\s\S]*top-edge weak candidate; review partial face vs edge false positive"
Assert-Contains "continuity includes lower weak table" $continuityText "Lower Weak-To-Medium Non-Edge Final Masks[\s\S]*0\.220"
Assert-Contains "continuity includes aspect outlier table" $continuityText "Aspect-Ratio Outlier Final Masks[\s\S]*too narrow for YOLO face profile"
Assert-Contains "summary records detection rows" $summaryText "Detection rows: 4"
Assert-Contains "summary records auto start frame" $summaryText "startFrame=0"
Assert-Contains "summary links final mask continuity report" $summaryText "Final mask continuity"
Assert-Contains "summary records final mask summary" $summaryText "Final mask summary"
Assert-Contains "summary records final mask cleanup" $summaryText "Final mask cleanup"
Assert-Contains "summary records scene-cut carry cleanup" $summaryText "Scene-cut carry cleanup[\s\S]*removed=3[\s\S]*removedFrames=6,7,13[\s\S]*removedUnsupportedStrong=1[\s\S]*removedUnsupportedStrongFrames=13[\s\S]*protectedStrong=2[\s\S]*protectedStrongFrames=14,15"
Assert-Contains "summary records post-gap-fill scene-cut carry cleanup" $summaryText "Scene-cut carry cleanup[\s\S]*stage=post-gap-fill[\s\S]*removed=2[\s\S]*removedFrames=18,19[\s\S]*removedUnsupportedStrongFrames=19[\s\S]*protectedStrongFrames=20"
Assert-Contains "summary records strong carry scene-cut probe" $summaryText "Strong carry scene-cut probe[\s\S]*candidates=1[\s\S]*checkedPairs=13->14[\s\S]*cutPairs=13->14"
Assert-Contains "summary records extended scene-carry block window" $summaryText "blockFrames=8"
Assert-Contains "summary records extended weak carry confidence" $summaryText "extendedWeakMaxConfidence=0.78"
Assert-Contains "summary records final mask post-scene cleanup" $scriptText "Final mask post-scene cleanup"
Assert-Contains "summary records tiny short cluster cleanup" $summaryText "removedTinyShortClusters=2"
Assert-Contains "summary records tiny isolated cleanup" $summaryText "removedTinyIsolated=1"
Assert-Contains "summary records final mask gap fill" $summaryText "Final mask gap fill"
Assert-Contains "summary records blocked final mask gap fill" $summaryText "blockedByCut=1"
Assert-Contains "summary records cleanup-blocked final mask gap fill" $summaryText "blockedByCleanup=1"
Assert-Contains "summary records scene-carry-blocked final mask gap fill" $summaryText "blockedBySceneCarry=2"
Assert-Contains "summary records final mask gap-fill scene guard" $summaryText "Final mask gap-fill scene-cut guard"
Assert-Contains "summary records large jump summary" $summaryText "largeJumpGaps=1"
Assert-Contains "summary records weak final mask evidence" $summaryText "weakNonEdge=1"
Assert-Contains "summary records edge weak final mask evidence" $summaryText "edgeWeak=1"
Assert-Contains "summary records top-edge weak final mask evidence" $summaryText "topEdgeWeak=1"
Assert-Contains "summary records lower weak final mask evidence" $summaryText "lowerWeak=1"
Assert-Contains "summary records aspect outlier final mask evidence" $summaryText "aspectBad=1"
Assert-Contains "summary records tiny weak final mask evidence" $summaryText "tinyWeak=1"
Assert-Contains "summary records tiny short final mask evidence" $summaryText "tinyShort=1"
Assert-Contains "summary records protected scene-carry evidence" $summaryText "protectedSceneCarry=2[\s\S]*protectedSceneCarryFrames=14,15"
Assert-Contains "summary records final mask review reasons" $summaryText "reviewRequired=True[\s\S]*reviewReasons=short-gap,large-jump-gap,isolated-mask,low-confidence-review,weak-non-edge,edge-weak-review,top-edge-weak-review,upper-weak,lower-weak,aspect-outlier,tiny-weak,tiny-short,scene-carry-protected"
Assert-Contains "summary records required full-frame review frames when packaged" $scriptText "Required full-frame review frames"
Assert-Contains "summary includes protected carry review frame output" $summaryText "Required full-frame review frames:[\s\S]*14[\s\S]*15"
Assert-Contains "summary includes post-gap protected carry review frame output" $summaryText "Required full-frame review frames:[\s\S]*19[\s\S]*20"
Assert-Contains "summary preserves face label markdown" $summaryText 'Use `face` only'
Assert-Contains "summary preserves nonface label markdown" $summaryText 'Use `nonface` for'
Assert-Contains "summary records partial visual review boundary" $summaryText "Partial Visual Review Rule"
Assert-Contains "summary keeps assistant observations reference-only" $summaryText "Assistant/AI overlay observations are reference evidence only"
Assert-Contains "summary blocks subset review from closing gate" $summaryText "reviewed subset can explain why a candidate was kept or removed, but it cannot close the full false-positive/miss gate"
Assert-Contains "summary protects partial top-edge faces" $summaryText "Do not convert edge or top-edge weak candidates to automatic false positives"
Assert-Contains "summary records pending visual completion" $summaryText "visual confirmation"
Assert-Contains "plan records follow-up incomplete" $planText "yolo-followup-quality-state:[\s\S]*complete=false"
Assert-Contains "plan records problem-video pending" $planText "problem-video-visual-confirmation=pending"
Assert-Contains "smoke result records follow-up package" $smokeText "yolo-quality/review-package/review-index.html"

Write-Host "[YoloFollowupQualityEvidenceVerify] all requested checks passed"
