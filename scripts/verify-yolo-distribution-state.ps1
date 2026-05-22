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

    Write-Host "[YoloDistributionVerify] pass $Name"
}

Assert-Contains "distribution marker" "distribution=no-bundled-yolo-model"
Assert-Contains "yolov8 source" "lindevs/yolov8-face"
Assert-Contains "yolov8 mit marker" "MIT license"
Assert-Contains "yolov8 upstream caveat" "upstream YOLOv8 weight/license"
Assert-Contains "yolo5 source" "hayashiLin/deepfacelivemodels"
Assert-Contains "yolo5 gpl marker" "gpl-3.0"
Assert-Contains "no repo bundling" "no-bundled-yolo-model"
Assert-Contains "faceonnx remains default" "default=FaceONNX"

$trackedFiles = & git -C $repo ls-files
if ($LASTEXITCODE -ne 0) {
    throw "git ls-files failed with exit code $LASTEXITCODE"
}

$blockedPatterns = @(
    "^\.tmp/models/",
    "(^|/)YoloV5Face\.onnx$",
    "(^|/)yolov8[nslmx]?-face-lindevs\.onnx$"
)

foreach ($file in $trackedFiles) {
    foreach ($pattern in $blockedPatterns) {
        if ($file -match $pattern) {
            throw "YOLO model file is tracked: $file"
        }
    }
}

Write-Host "[YoloDistributionVerify] pass no tracked yolo model files"
Write-Host "[YoloDistributionVerify] all requested checks passed"
