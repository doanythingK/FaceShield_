param(
    [string]$PlanPath = "AUTO_MOSAIC_QUALITY_SPEED_PLAN.md"
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$resolvedPlanPath = if ([IO.Path]::IsPathRooted($PlanPath)) { $PlanPath } else { Join-Path $repo $PlanPath }
if (-not (Test-Path $resolvedPlanPath)) {
    throw "Plan file not found: $resolvedPlanPath"
}

$plan = Get-Content -Raw -Path $resolvedPlanPath

function Assert-Contains {
    param(
        [string]$Name,
        [string]$Text
    )

    if (-not $plan.Contains($Text)) {
        throw "$Name missing text: $Text"
    }

    Write-Host "[YoloConclusionVerify] pass $Name"
}

function Assert-Match {
    param(
        [string]$Name,
        [string]$Pattern
    )

    if ($plan -notmatch $Pattern) {
        throw "$Name missing pattern: $Pattern"
    }

    Write-Host "[YoloConclusionVerify] pass $Name"
}

$marker = "yolo-conclusion-state: no-final-yolo-recommendation; default=FaceONNX; ab-gate-not-ground-truth; required=label-gui-10min; distribution=no-bundled-yolo-model; axes=model,decode,preprocess,post-filter,track,roi,tiling,small-face,box-refine,speed"
Assert-Contains "conclusion marker" $marker

foreach ($candidate in @(
    "YOLOv8n 640",
    "YOLOv8m 640",
    "YOLOv8s 640",
    "YOLOv8l 640",
    "YOLO5Face 0.12/0.18/0.45",
    "selective tiling",
    "low-confidence position filter",
    "small-area filter",
    "FaceONNX ROI verifier",
    "large-box/landmark box refine")) {
    Assert-Contains "classification row $candidate" $candidate
}

foreach ($token in @(
    "no-final-yolo-recommendation",
    "default=FaceONNX",
    "ab-gate-not-ground-truth",
    "label-gui-10min",
    "no-bundled-yolo-model",
    "model",
    "decode",
    "preprocess",
    "post-filter",
    "track",
    "roi",
    "tiling",
    "small-face",
    "box-refine",
    "speed")) {
    Assert-Contains "classification token $token" $token
}

Assert-Match "faceonnx remains default" "FaceONNX"
Assert-Match "yolov8s rejected" "YOLOv8s 640[\s\S]*StrictGatePassed=False"
Assert-Match "yolov8l rejected" "YOLOv8l 640[\s\S]*64"
Assert-Match "small area filter rejected" "small-area filter[\s\S]*optimizedFrames=5"
Assert-Match "roi verifier rejected" "FaceONNX ROI verifier[\s\S]*boxCountDiffFrames"
Assert-Match "decode not primary failure" "decode[\s\S]*feature-map"

Write-Host "[YoloConclusionVerify] all requested checks passed"
