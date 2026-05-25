param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$work = Join-Path $repo ".tmp\yolo-quality-review-checklist-verify"
$log = Join-Path $work "synthetic-yolo.log"
$out = Join-Path $work "review-checklist.md"
$script = Join-Path $repo "scripts\write-yolo-quality-review-checklist.ps1"

function Assert-Contains {
    param(
        [string]$Name,
        [string]$Text,
        [string]$Pattern
    )

    if ($Text -notmatch $Pattern) {
        throw "$Name did not contain expected pattern: $Pattern"
    }
}

New-Item -ItemType Directory -Force -Path $work | Out-Null

@'
[AutoRunSummary] runId=synthetic, detector=YoloFaceOnnxDetector/CPU, mode=pipe-parallel, totalFrames=12, processed=12, decoded=12, detects=12, interpolated=0, readMs=0, decodeMs=10, detectMs=20, maskMs=0, totalMs=30, downscale=1.000, quality=BalancedBilinear, tracking=True, everyN=1, parallel=2, roi=regular=3, small=0, rejected=1, statsRejected=0
[SmokeFaceTrackPost] label=synthetic-yolo, tracks=1, filled=1, lostFilled=2, lostFrames=4,5, removedShort=1, removedSparse=1, removedEdgeTail=1, removedLower=0, rewritten=6
[SmokeFaceTrackSceneCutGuard] label=synthetic-yolo, directCandidates=1, postCutCandidates=2, checked=2, checkedPairs=2->3,5->6, maxDiff=0.410, cutPairs=5->6, removed=1, removedFrames=6, threshold=0.320, elapsedMs=3, error=none
[AutoMaskSparsePipe] done decoded=12, detects=3, interpolated=2, sparseSceneCuts=1, sparseSceneCutPairs=6->9, decodeMs=10, detectMs=20, totalMs=30, filter=regular=2, small=0, rejected=1, statsRejected=0
[SmokeDetection] label=synthetic-yolo, frame=2, index=0, x=10.0, y=20.0, w=50.0, h=60.0, area=3000.0, conf=0.410, cx=0.055, cy=0.120, areaRatio=0.002000, aspectRatio=0.833
[SmokeDetection] label=synthetic-yolo, frame=6, index=0, x=500.0, y=400.0, w=20.0, h=22.0, area=440.0, conf=0.220, cx=0.398, cy=0.571, areaRatio=0.000210, aspectRatio=0.909
[SmokeDetectionSummary] label=synthetic-yolo, frames=2, detections=2, frameRange=2-6, confMin=0.220, confAvg=0.315, confMax=0.410, areaRatioMin=0.000210, areaRatioAvg=0.001105, areaRatioMax=0.002000, aspectRatioMin=0.833, aspectRatioAvg=0.871, aspectRatioMax=0.909
'@ | Set-Content -Encoding UTF8 -Path $log

$output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script -LogPath $log -OutputPath $out 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "write-yolo-quality-review-checklist.ps1 failed: $($output | Out-String)"
}

if (-not (Test-Path $out)) {
    throw "Checklist was not created: $out"
}

$text = Get-Content -Raw -Path $out
Assert-Contains "title" $text "# YOLO Quality Review Checklist"
Assert-Contains "detection rows" $text "Detection rows parsed: 2"
Assert-Contains "flicker frames" $text "lostFrames=4,5"
Assert-Contains "edge tail review" $text "removedEdgeTail=1"
Assert-Contains "direct scene cut candidates" $text "directCandidates=1"
Assert-Contains "post-cut scene cut candidates" $text "postCutCandidates=2"
Assert-Contains "scene cut pairs" $text "checkedPairs=2->3,5->6"
Assert-Contains "scene cut max diff" $text "maxDiff=0.410"
Assert-Contains "scene cut cut pairs" $text "cutPairs=5->6"
Assert-Contains "removed frames" $text "removedFrames=6"
Assert-Contains "sparse cut pairs" $text "sparseSceneCutPairs=6->9"
Assert-Contains "low confidence table" $text "\| 6 \| 0 \| 0\.220"
Assert-Contains "small area table" $text "0\.000210"
Assert-Contains "aspect table" $text "0\.909"
Assert-Contains "label rule model ground truth" $text "do not treat YOLO or FaceONNX as ground truth"
Assert-Contains "label rule visible face" $text '\- `face`: visible real face'
Assert-Contains "label rule nonface" $text '\- `nonface`: background/object/body/text/partial artifact'
Assert-Contains "label rule miss" $text '\- `miss`: visible face not covered'

Write-Host "[YoloQualityReviewChecklistVerify] all requested checks passed"
