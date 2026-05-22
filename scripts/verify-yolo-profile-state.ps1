param(
    [string]$HomeViewModel = "ViewModels/Pages/HomePageViewModel.cs",
    [string]$WorkspaceViewModel = "ViewModels/Pages/WorkspaceViewModel.cs",
    [string]$AutoMaskGenerator = "Services/Analysis/AutoMaskGenerator.cs",
    [string]$WorkspaceStateStore = "Services/Workspace/WorkspaceStateStore.cs",
    [string]$YoloOptions = "Services/FaceDetection/YoloFaceOnnxDetectorOptions.cs",
    [string]$BackendEnum = "Services/FaceDetection/FaceDetectorBackend.cs",
    [string]$YoloModelEnum = "Services/FaceDetection/YoloFaceModelType.cs"
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

function Read-RepoFile {
    param([string]$Path)

    $resolved = if ([IO.Path]::IsPathRooted($Path)) { $Path } else { Join-Path $repo $Path }
    if (-not (Test-Path $resolved)) {
        throw "Required source file not found: $resolved"
    }

    return Get-Content -Raw -Path $resolved
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

    Write-Host "[YoloProfileVerify] pass $Name"
}

$homeText = Read-RepoFile $HomeViewModel
$workspaceText = Read-RepoFile $WorkspaceViewModel
$autoMaskGeneratorText = Read-RepoFile $AutoMaskGenerator
$state = Read-RepoFile $WorkspaceStateStore
$options = Read-RepoFile $YoloOptions
$backend = Read-RepoFile $BackendEnum
$modelEnum = Read-RepoFile $YoloModelEnum

Assert-Match "backend enum exposes yolo" $backend "YoloFaceOnnx\s*=\s*3"
Assert-Match "yolo model enum exposes v8" $modelEnum "YoloV8Face\s*=\s*0"
Assert-Match "yolo model enum exposes yolo5" $modelEnum "Yolo5Face\s*=\s*1"

Assert-Match "settings version is v5" $homeText "CurrentAutoSettingsVersion\s*=\s*5"
Assert-Match "home detector selector keeps faceonnx" $homeText "new AutoDetectorBackendOption\(""FaceONNX"",\s*FaceDetectorBackend\.FaceOnnx\)"
Assert-Match "home detector selector exposes yolo" $homeText "new AutoDetectorBackendOption\(""YOLO Face ONNX"",\s*FaceDetectorBackend\.YoloFaceOnnx\)"
Assert-Match "home yolo selector exposes v8" $homeText "new YoloModelTypeOption\(""YOLOv8-Face"",\s*YoloFaceModelType\.YoloV8Face\)"
Assert-Match "home yolo selector exposes yolo5" $homeText "new YoloModelTypeOption\(""YOLO5Face"",\s*YoloFaceModelType\.Yolo5Face\)"

foreach ($field in @(
    "_yoloV8Profile",
    "_yolo5Profile",
    "StoreCurrentYoloProfile",
    "CaptureCurrentYoloProfile",
    "ApplyYoloProfile",
    "ReadSavedYoloProfile")) {
    Assert-Match "home has $field" $homeText ([regex]::Escape($field))
}

foreach ($property in @(
    "ModelPath",
    "ObjectnessThreshold",
    "ConfidenceThreshold",
    "NmsThreshold",
    "InputSize",
    "UseTiling",
    "TileOnly",
    "TileColumns",
    "TileRows",
    "TileOverlapRatio")) {
    Assert-Match "yolo profile property $property" $homeText "public\s+.*\s+$property\s*\{"
}

foreach ($prefix in @("YoloV8", "Yolo5")) {
    foreach ($suffix in @(
        "ModelPath",
        "ObjectnessThreshold",
        "ConfidenceThreshold",
        "NmsThreshold",
        "InputSize",
        "UseTiling",
        "TileOnly",
        "TileColumns",
        "TileRows",
        "TileOverlapRatio")) {
        Assert-Match "state has $prefix$suffix" $state "public\s+.*\s+$prefix$suffix\s*\{"
        Assert-Match "home persists $prefix$suffix" $homeText "$prefix$suffix\s*=\s*yolo"
    }
}

Assert-Match "legacy active yolo profile remains persisted" $state "public\s+.*\s+YoloModelPath\s*\{"
Assert-Match "home persists active legacy profile" $homeText "YoloModelPath\s*=\s*activeYoloProfile\.ModelPath"
Assert-Match "home migrates saved yolo5 profile" $homeText "ReadSavedYoloProfile\(saved,\s*YoloFaceModelType\.Yolo5Face"
Assert-Match "home migrates saved yolo v8 profile" $homeText "ReadSavedYoloProfile\(saved,\s*YoloFaceModelType\.YoloV8Face"

Assert-Match "auto options choose yolo filter profile" $homeText "FilterProfile\s*=\s*IsYoloDetectorSelected\s*\?\s*FaceFilterProfile\.Yolo\s*:\s*FaceFilterProfile\.FaceOnnx"
Assert-Match "factory creates yolo detector options" $homeText "FaceDetectorFactoryOptions\.ForYoloFaceOnnx\(new YoloFaceOnnxDetectorOptions"
Assert-Match "factory falls back to faceonnx options" $homeText "return FaceDetectorFactoryOptions\.ForOnnx\(faceOnnxOptions\)"
Assert-Match "faceonnx thresholds stay separate" $homeText "DetectionThreshold\s*=\s*\(float\)Math\.Clamp\(AutoDetectionThreshold"
Assert-Match "yolo objectness comes from yolo property" $homeText "ObjectnessThreshold\s*=\s*\(float\)Math\.Clamp\(AutoYoloObjectnessThreshold"
Assert-Match "yolo confidence comes from yolo property" $homeText "ConfidenceThreshold\s*=\s*\(float\)Math\.Clamp\(AutoYoloConfidenceThreshold"
Assert-Match "yolo nms comes from yolo property" $homeText "NmsThreshold\s*=\s*\(float\)Math\.Clamp\(AutoYoloNmsThreshold"
Assert-Match "yolo tiling comes from yolo property" $homeText "UseTiling\s*=\s*AutoYoloUseTiling"
Assert-Match "yolo tile-only maps to full-frame switch" $homeText "IncludeFullFrameWhenTiling\s*=\s*!AutoYoloTileOnly"

Assert-Match "workspace autotune guarded to faceonnx backend" $workspaceText "_detectorFactoryOptions\.Backend\s*==\s*FaceDetectorBackend\.FaceOnnx[\s\S]*DetectorAutoTuner\.TryTune"
Assert-Match "workspace autotune updates faceonnx options only" $workspaceText "detectorFactoryOptions\s*=\s*detectorFactoryOptions\.WithFaceOnnxOptions\(detectorOptions\)"
Assert-Match "workspace keeps configured filter profile in run options" $workspaceText "FilterProfile\s*=\s*_autoOptions\.FilterProfile"
Assert-Match "workspace yolo track profile exists" $workspaceText "if\s*\(profile\s*==\s*FaceFilterProfile\.Yolo\)[\s\S]*UnstableTailMaxConfidence\s*=\s*0\.40f[\s\S]*LowerFrameTrackMaxConfidence\s*=\s*0\.50f"
Assert-Match "workspace faceonnx track profile remains default branch" $workspaceText "return\s+new\s+FaceTrackPostProcessOptions[\s\S]*WeakConfidence\s*=\s*TemporalConfidenceWeak[\s\S]*StrongConfidence\s*=\s*TemporalConfidenceStrong"

Assert-Match "automask yolo filter profile exists" $autoMaskGeneratorText "if\s*\(profile\s*==\s*FaceFilterProfile\.Yolo\)[\s\S]*MinSmallFaceAreaRatio\s*\*\s*0\.70[\s\S]*2\.7[\s\S]*0\.30f[\s\S]*UseStatsFilter:\s*false"
Assert-Match "automask faceonnx default uses stats filter" $autoMaskGeneratorText "return\s+new\s+FaceFilterSettings[\s\S]*MaxFaceAspectRatio[\s\S]*SmallFaceConfidenceMin[\s\S]*UseStatsFilter:\s*true"

foreach ($property in @(
    "ObjectnessThreshold",
    "ConfidenceThreshold",
    "NmsThreshold",
    "UseTiling",
    "IncludeFullFrameWhenTiling",
    "TileColumns",
    "TileRows",
    "TileOverlapRatio",
    "UseLowConfidencePositionFilter",
    "LowConfidencePositionMaxConfidence",
    "LowConfidencePositionMinCenterYRatio",
    "UseSmallAreaFilter",
    "SmallAreaMaxAreaRatio")) {
    Assert-Match "yolo options expose $property" $options "public\s+.*\s+$property\s*\{"
}

Write-Host "[YoloProfileVerify] all requested checks passed"
