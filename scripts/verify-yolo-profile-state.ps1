param(
    [string]$HomeViewModel = "ViewModels/Pages/HomePageViewModel.cs",
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

foreach ($property in @(
    "ObjectnessThreshold",
    "ConfidenceThreshold",
    "NmsThreshold",
    "UseTiling",
    "IncludeFullFrameWhenTiling",
    "TileColumns",
    "TileRows",
    "TileOverlapRatio")) {
    Assert-Match "yolo options expose $property" $options "public\s+.*\s+$property\s*\{"
}

Write-Host "[YoloProfileVerify] all requested checks passed"
