param(
    [string]$PlanPath = "AUTO_MOSAIC_QUALITY_SPEED_PLAN.md",
    [string]$ThresholdSweepScript = "scripts/run-yolo-threshold-sweep.ps1",
    [string]$ReviewFilterSweepScript = "scripts/run-yolo-review-filter-sweep.ps1",
    [string]$ReviewFilterCandidateScript = "scripts/find-yolo-review-filter-candidates.ps1"
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

function Resolve-RepoPath {
    param([string]$Path)

    if ([IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return Join-Path $repo $Path
}

function Read-RepoFile {
    param([string]$Path)

    $resolved = Resolve-RepoPath $Path
    if (-not (Test-Path $resolved)) {
        throw "File not found: $resolved"
    }

    return Get-Content -Raw -Path $resolved
}

function Assert-Contains {
    param(
        [string]$Name,
        [string]$Text,
        [string]$Expected
    )

    if (-not $Text.Contains($Expected)) {
        throw "$Name missing text: $Expected"
    }

    Write-Host "[YoloSweepVerify] pass $Name"
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

    Write-Host "[YoloSweepVerify] pass $Name"
}

$plan = Read-RepoFile $PlanPath
$thresholdSweep = Read-RepoFile $ThresholdSweepScript
$reviewFilterSweep = Read-RepoFile $ReviewFilterSweepScript
$reviewFilterCandidates = Read-RepoFile $ReviewFilterCandidateScript

Assert-Contains "plan records sweep marker" $plan "yolo-sweep-harness-state:"
Assert-Contains "plan records threshold sweep script" $plan "run-yolo-threshold-sweep.ps1"
Assert-Contains "plan records review filter sweep script" $plan "run-yolo-review-filter-sweep.ps1"
Assert-Contains "plan records review filter candidate script" $plan "find-yolo-review-filter-candidates.ps1"
Assert-Contains "plan records objectness sweep" $plan "9분 2초 YOLO5Face objectness sweep"
Assert-Contains "plan records confidence sweep" $plan "9분 2초 YOLO5Face confidence sweep"
Assert-Contains "plan records tiling sweep" $plan "6분 3초 YOLO5Face tiling mode sweep"
Assert-Contains "plan records roi sweep" $plan "FaceONNX ROI verifier sweep"
Assert-Contains "plan records box refine sweep" $plan "box refine smoke"
Assert-Contains "plan records low confidence filter failure" $plan "low-confidence position filter"
Assert-Contains "plan records small area filter failure" $plan "small-area filter"
Assert-Contains "plan records yolo v8s rejection" $plan "YOLOv8s 640"
Assert-Contains "plan records yolo v8l rejection" $plan "YOLOv8l 640"
Assert-Contains "plan records no recommendation" $plan "추천 후보 없음"

foreach ($token in @(
    "ObjectnessThresholds",
    "ConfidenceThresholds",
    "NmsThresholds",
    "IncludeTiling",
    "IncludeTileOnly",
    "IncludeLargeBoxScale",
    "IncludeLandmarkBoxRefine",
    "IncludeFaceOnnxRoiRefine",
    "IncludeTrackPostProcess",
    "StrictGatePassed",
    "AvgBaselineCoverage",
    "MinBaselineCoverage",
    "FaceOnnxRoiAttempts",
    "FaceOnnxRoiHits",
    "FaceOnnxRoiElapsedMs",
    "Export-Csv")) {
    Assert-Contains "threshold sweep exposes $token" $thresholdSweep $token
}

Assert-Match "threshold sweep resolves model path" $thresholdSweep "Resolve-YoloModelPath[\s\S]*-Require"
Assert-Match "threshold sweep calls smoke harness" $thresholdSweep "run-srcTest-smoke\.ps1[\s\S]*YoloObjectnessThreshold[\s\S]*YoloConfidenceThreshold[\s\S]*YoloNmsThreshold"
Assert-Match "threshold sweep keeps collection thresholds open" $thresholdSweep 'MinAvgIou",\s*"0"[\s\S]*MinBestIou",\s*"0"'
Assert-Match "threshold sweep supports tiling args" $thresholdSweep "YoloUseTiling[\s\S]*YoloTileColumns[\s\S]*YoloTileRows[\s\S]*YoloTileOverlapRatio"
Assert-Match "threshold sweep supports roi refine args" $thresholdSweep "YoloUseFaceOnnxRoiRefine[\s\S]*YoloFaceOnnxRoiMinAreaRatio[\s\S]*YoloFaceOnnxRoiMaxCandidates"
Assert-Match "threshold sweep supports track args" $thresholdSweep "YoloDropShortTrackMaxDetections[\s\S]*YoloShortTrackMaxConfidence[\s\S]*YoloLowerFrameTrackMaxConfidence"

foreach ($token in @(
    "MaxConfidences",
    "MinCenterYRatios",
    "IncludeSmallAreaFilter",
    "SmallAreaMaxAreaRatios",
    "IncludeUnfilteredBaseline",
    "StrictGatePassed",
    "YoloUseLowConfidencePositionFilter",
    "YoloUseSmallAreaFilter",
    "Export-Csv")) {
    Assert-Contains "review filter sweep exposes $token" $reviewFilterSweep $token
}

Assert-Match "review filter sweep calls smoke harness" $reviewFilterSweep "run-srcTest-smoke\.ps1[\s\S]*YoloUseLowConfidencePositionFilter[\s\S]*YoloUseSmallAreaFilter"
Assert-Match "review filter candidates use reviewed labels" $reviewFilterCandidates 'Verdict\s+-eq\s+"Face"[\s\S]*Verdict\s+-eq\s+"NonFace"'
Assert-Contains "review filter candidates export rule metrics" $reviewFilterCandidates "DroppedNonFace"
Assert-Contains "review filter candidates export face loss metrics" $reviewFilterCandidates "DroppedFace"

Write-Host "[YoloSweepVerify] all requested checks passed"
