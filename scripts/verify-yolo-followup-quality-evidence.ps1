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
[AutoRunSummary] runId=synthetic, detector=YoloFaceOnnxDetector/CPU, mode=pipe-parallel, totalFrames=12, processed=12, decoded=12, detects=12, interpolated=0, readMs=0, decodeMs=10, detectMs=20, maskMs=0, totalMs=30, downscale=1.000, quality=BalancedBilinear, tracking=True, everyN=1, parallel=2, roi=regular=3, small=0, rejected=1, statsRejected=0
[SmokeFaceTrackPost] label=synthetic-yolo, tracks=1, filled=1, lostFilled=2, lostFrames=4,5, removedShort=1, removedSparse=1, removedEdgeTail=1, removedLower=0, rewritten=6
[SmokeFaceTrackSceneCutGuard] label=synthetic-yolo, directCandidates=1, checked=2, checkedPairs=2->3,5->6, maxDiff=0.410, cutPairs=5->6, removed=1, removedFrames=6, threshold=0.320, elapsedMs=3, error=none
[SmokeYoloFinalMaskCleanup] label=synthetic-yolo, removedWeakIsolated=4, removedWeakUnsupported=1, removedWeakShortClusters=0, removedWeakTinyClusters=0, removedTinyShortClusters=2, removedTinyIsolated=1, removedUpperWeakClusters=0, removedFrames=8,10,11,12
[SmokeYoloFinalMaskGapFill] label=synthetic-yolo, filled=1, frames=5
[SmokeYoloFinalMaskGapFillSceneCutGuard] label=synthetic-yolo, candidates=2, checked=2, checkedPairs=4->5,5->6, maxDiff=0.410, cutPairs=4->5, removed=1, removedFrames=5, threshold=0.320, elapsedMs=2, error=none
[SmokeFinalMaskSummary] label=synthetic-yolo, frames=2, rows=2, frameRange=2-6, shortGaps=1, shortGapRanges=3-5, largeJumpGaps=1, largeJumpRanges=3-5, isolated=2, isolatedFrames=2,6, lowConf=1, lowConfFrames=2, weakNonEdge=1, weakNonEdgeFrames=2, tinyWeak=1, tinyWeakFrames=2, tinyShort=1, tinyShortFrames=2
[SmokeDetection] label=synthetic-yolo, frame=2, index=0, x=10.0, y=20.0, w=50.0, h=60.0, area=3000.0, conf=0.410, cx=0.055, cy=0.120, areaRatio=0.002000, aspectRatio=0.833
[SmokeDetection] label=synthetic-yolo, frame=6, index=0, x=500.0, y=400.0, w=20.0, h=22.0, area=440.0, conf=0.220, cx=0.398, cy=0.571, areaRatio=0.000210, aspectRatio=0.909
[SmokeDetection] label=synthetic-yolo, frame=9, index=0, x=700.0, y=120.0, w=24.0, h=25.0, area=600.0, conf=0.180, cx=0.556, cy=0.190, areaRatio=0.000290, aspectRatio=0.960
[SmokeDetection] label=synthetic-yolo, frame=10, index=0, x=610.0, y=35.0, w=34.0, h=30.0, area=1020.0, conf=0.310, cx=0.490, cy=0.070, areaRatio=0.000492, aspectRatio=1.133
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
Assert-Contains "script parses final mask post-scene cleanup" $scriptText "SmokeYoloFinalMaskPostSceneCleanup"
Assert-Contains "script parses final mask gap fill" $scriptText "SmokeYoloFinalMaskGapFill|YoloFinalMaskGapFill"
Assert-Contains "script parses final mask gap-fill scene guard" $scriptText "SmokeYoloFinalMaskGapFillSceneCutGuard|YoloFinalMaskGapFillSceneCutGuard"
Assert-Contains "script parses final mask summary" $scriptText "SmokeFinalMaskSummary|FinalMaskSummary"
Assert-Contains "script writes review package" $scriptText "new-yolo-full-gt-review-package\.ps1"
Assert-Contains "script writes final mask continuity report" $scriptText "write-yolo-mask-continuity-report\.ps1"
Assert-Contains "script reuses existing review package" $scriptText "ForceReviewPackage[\s\S]*review-index\.html"
Assert-Contains "script derives required full-frame review frames" $scriptText "Get-ReviewFrameNumbers[\s\S]*lostFrames=.*removedShort[\s\S]*checkedPairs=.*maxDiff"
Assert-Contains "script derives review frames from final summary gaps" $scriptText "FinalMaskSummaryLines[\s\S]*shortGapRanges=.*largeJumpGaps[\s\S]*largeJumpRanges=.*isolated="
Assert-Contains "script derives review frames from final summary residuals" $scriptText "lowConfFrames=.*weakNonEdge[\s\S]*weakNonEdgeFrames=.*tinyWeak[\s\S]*tinyWeakFrames=.*tinyShort[\s\S]*tinyShortFrames="
Assert-Contains "script derives review frames from low-confidence detections" $scriptText "LowConfidenceReviewThreshold[\s\S]*Add-LowConfidenceDetectionFrames[\s\S]*DetectionRows[\s\S]*conf="
Assert-Contains "script passes required full-frame review frames" $scriptText "RequiredFullFrameNumbers[\s\S]*reviewFrameNumbers"
Assert-Contains "script expands review video frame count for required frames" $scriptText "reviewVideoFrameCount[\s\S]*maxRequiredFrame\s*\+\s*1"
Assert-Contains "script writes summary" $scriptText "YOLO Follow-Up Quality Evidence"
Assert-Contains "checklist has scene-cut evidence" $checklistText "directCandidates=1"
Assert-Contains "checklist has false-positive label rule" $checklistText "do not treat YOLO or FaceONNX as ground truth"
Assert-Contains "continuity says rows are final masks" $continuityText 'final `FrameMaskProvider` face rectangles'
Assert-Contains "continuity reports short gaps" $continuityText "Short empty gaps: 2"
Assert-Contains "continuity reports isolated masks" $continuityText "Isolated final mask frames: 2"
Assert-Contains "continuity reports low confidence masks" $continuityText "Low-confidence final masks: 3"
Assert-Contains "continuity reports weak non-edge masks" $continuityText "Weak non-edge final masks: 3"
Assert-Contains "continuity reports upper-frame weak non-edge masks" $continuityText "Upper-frame weak non-edge final masks: 1"
Assert-Contains "continuity reports gap diagnostics" $continuityText "AreaChange[\s\S]*CenterShift[\s\S]*Review hint"
Assert-Contains "continuity reports large jump hint" $continuityText "large box jump; review before fill"
Assert-Contains "continuity includes weak non-edge table" $continuityText "Weak Non-Edge Final Masks[\s\S]*upper-frame weak non-edge; review false positive vs small face"
Assert-Contains "summary records detection rows" $summaryText "Detection rows: 4"
Assert-Contains "summary links final mask continuity report" $summaryText "Final mask continuity"
Assert-Contains "summary records final mask summary" $summaryText "Final mask summary"
Assert-Contains "summary records final mask cleanup" $summaryText "Final mask cleanup"
Assert-Contains "summary records final mask post-scene cleanup" $scriptText "Final mask post-scene cleanup"
Assert-Contains "summary records tiny short cluster cleanup" $summaryText "removedTinyShortClusters=2"
Assert-Contains "summary records tiny isolated cleanup" $summaryText "removedTinyIsolated=1"
Assert-Contains "summary records final mask gap fill" $summaryText "Final mask gap fill"
Assert-Contains "summary records final mask gap-fill scene guard" $summaryText "Final mask gap-fill scene-cut guard"
Assert-Contains "summary records large jump summary" $summaryText "largeJumpGaps=1"
Assert-Contains "summary records weak final mask evidence" $summaryText "weakNonEdge=1"
Assert-Contains "summary records tiny weak final mask evidence" $summaryText "tinyWeak=1"
Assert-Contains "summary records tiny short final mask evidence" $summaryText "tinyShort=1"
Assert-Contains "summary records required full-frame review frames when packaged" $scriptText "Required full-frame review frames"
Assert-Contains "summary preserves face label markdown" $summaryText 'Use `face` only'
Assert-Contains "summary preserves nonface label markdown" $summaryText 'Use `nonface` for'
Assert-Contains "summary records pending visual completion" $summaryText "visual confirmation"
Assert-Contains "plan records follow-up incomplete" $planText "yolo-followup-quality-state:[\s\S]*complete=false"
Assert-Contains "plan records problem-video pending" $planText "problem-video-visual-confirmation=pending"
Assert-Contains "smoke result records follow-up package" $smokeText "yolo-quality/review-package/review-index.html"

Write-Host "[YoloFollowupQualityEvidenceVerify] all requested checks passed"
