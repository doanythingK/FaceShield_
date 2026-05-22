param(
    [string]$HomeViewModel = "ViewModels/Pages/HomePageViewModel.cs",
    [string]$HomeView = "Views/Pages/HomePageView.axaml",
    [string]$HomeViewCodeBehind = "Views/Pages/HomePageView.axaml.cs",
    [string]$WorkspaceViewModel = "ViewModels/Pages/WorkspaceViewModel.cs",
    [string]$AutoMaskGenerator = "Services/Analysis/AutoMaskGenerator.cs",
    [string]$WorkspaceStateStore = "Services/Workspace/WorkspaceStateStore.cs",
    [string]$YoloOptions = "Services/FaceDetection/YoloFaceOnnxDetectorOptions.cs",
    [string]$YoloDetector = "Services/FaceDetection/YoloFaceOnnxDetector.cs",
    [string]$BackendEnum = "Services/FaceDetection/FaceDetectorBackend.cs",
    [string]$YoloModelEnum = "Services/FaceDetection/YoloFaceModelType.cs",
    [string]$SmokeHarness = "scripts/run-srcTest-smoke.ps1"
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
$homeViewText = Read-RepoFile $HomeView
$homeViewCodeBehindText = Read-RepoFile $HomeViewCodeBehind
$workspaceText = Read-RepoFile $WorkspaceViewModel
$autoMaskGeneratorText = Read-RepoFile $AutoMaskGenerator
$state = Read-RepoFile $WorkspaceStateStore
$options = Read-RepoFile $YoloOptions
$yoloDetectorText = Read-RepoFile $YoloDetector
$backend = Read-RepoFile $BackendEnum
$modelEnum = Read-RepoFile $YoloModelEnum
$smokeHarnessText = Read-RepoFile $SmokeHarness

Assert-Match "backend enum exposes yolo" $backend "YoloFaceOnnx\s*=\s*3"
Assert-Match "yolo model enum exposes v8" $modelEnum "YoloV8Face\s*=\s*0"
Assert-Match "yolo model enum exposes yolo5" $modelEnum "Yolo5Face\s*=\s*1"

Assert-Match "settings version is v5" $homeText "CurrentAutoSettingsVersion\s*=\s*5"
Assert-Match "home detector selector keeps faceonnx" $homeText "new AutoDetectorBackendOption\(""FaceONNX"",\s*FaceDetectorBackend\.FaceOnnx\)"
Assert-Match "home detector selector exposes yolo" $homeText "new AutoDetectorBackendOption\(""YOLO Face ONNX"",\s*FaceDetectorBackend\.YoloFaceOnnx\)"
Assert-Match "home yolo selector exposes v8" $homeText "new YoloModelTypeOption\(""YOLOv8-Face"",\s*YoloFaceModelType\.YoloV8Face\)"
Assert-Match "home yolo selector exposes yolo5" $homeText "new YoloModelTypeOption\(""YOLO5Face"",\s*YoloFaceModelType\.Yolo5Face\)"
Assert-Match "home view binds detector backend selector" $homeViewText "ItemsSource=""\{Binding AutoDetectorBackendOptions\}""[\s\S]*SelectedItem=""\{Binding SelectedAutoDetectorBackendOption\}"""
Assert-Match "home view shows yolo panel only for yolo backend" $homeViewText "IsVisible=""\{Binding IsYoloDetectorSelected\}""[\s\S]*YOLO"
Assert-Match "home view binds yolo model selector" $homeViewText "ItemsSource=""\{Binding YoloModelTypeOptions\}""[\s\S]*SelectedItem=""\{Binding SelectedYoloModelTypeOption\}"""
Assert-Match "home view binds yolo model path" $homeViewText "Text=""\{Binding AutoYoloModelPath,\s*Mode=TwoWay\}"""
Assert-Match "home view exposes yolo model picker" $homeViewText "Click=""PickYoloModel_Click"""
Assert-Match "home view code-behind calls yolo picker" $homeViewCodeBehindText "PickYoloModel_Click[\s\S]*PickYoloModelAsync"
Assert-Match "home view binds yolo input size" $homeViewText "Value=""\{Binding AutoYoloInputSize,\s*Mode=TwoWay\}"""
Assert-Match "home view binds yolo tiling toggle" $homeViewText "IsChecked=""\{Binding AutoYoloUseTiling\}"""
Assert-Match "home view binds yolo tile-only toggle" $homeViewText "IsChecked=""\{Binding AutoYoloTileOnly\}"""
Assert-Match "home view binds yolo tile columns" $homeViewText "Value=""\{Binding AutoYoloTileColumns,\s*Mode=TwoWay\}"""
Assert-Match "home view binds yolo tile rows" $homeViewText "Value=""\{Binding AutoYoloTileRows,\s*Mode=TwoWay\}"""
Assert-Match "home view binds yolo tile overlap" $homeViewText "Value=""\{Binding AutoYoloTileOverlapRatio,\s*Mode=TwoWay\}"""
Assert-Match "home view binds yolo objectness slider" $homeViewText "Value=""\{Binding AutoYoloObjectnessThreshold,\s*Mode=TwoWay\}"""
Assert-Match "home view binds yolo confidence slider" $homeViewText "Value=""\{Binding AutoYoloConfidenceThreshold,\s*Mode=TwoWay\}"""
Assert-Match "home view binds yolo nms slider" $homeViewText "Value=""\{Binding AutoYoloNmsThreshold,\s*Mode=TwoWay\}"""
Assert-Match "home view keeps faceonnx threshold panels separate" $homeViewText "IsVisible=""\{Binding IsFaceOnnxDetectorSelected\}""[\s\S]*AutoDetectionThreshold[\s\S]*AutoNmsThreshold"

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
    "LargeBoxWidthScale",
    "LargeBoxHeightScale",
    "LargeBoxMinAreaRatio",
    "UseYolo5LandmarkBoxRefine",
    "Yolo5LandmarkBoxMinAreaRatio",
    "Yolo5LandmarkBoxWidthScale",
    "Yolo5LandmarkBoxHeightScale",
    "Yolo5LandmarkBoxCenterYOffsetRatio",
    "Yolo5LandmarkBoxMinOriginalIou",
    "UseTiling",
    "IncludeFullFrameWhenTiling",
    "TileColumns",
    "TileRows",
    "TileOverlapRatio",
    "UseLetterboxResize",
    "CenterLetterboxPadding",
    "LetterboxPaddingValue",
    "UseRgbInput",
    "UseLowConfidencePositionFilter",
    "LowConfidencePositionMaxConfidence",
    "LowConfidencePositionMinCenterYRatio",
    "UseSmallAreaFilter",
    "SmallAreaMaxAreaRatio")) {
    Assert-Match "yolo options expose $property" $options "public\s+.*\s+$property\s*\{"
}

foreach ($method in @(
    "RunTiles",
    "ApplyLowConfidencePositionFilter",
    "ApplySmallAreaFilter",
    "RefineLargeBoxes",
    "TryRefineFromLandmarks")) {
    Assert-Match "yolo detector uses $method" $yoloDetectorText ([regex]::Escape($method))
}

foreach ($optionUse in @(
    "UseLetterboxResize",
    "CenterLetterboxPadding",
    "LetterboxPaddingValue",
    "UseRgbInput",
    "LargeBoxWidthScale",
    "LargeBoxHeightScale",
    "LargeBoxMinAreaRatio",
    "UseYolo5LandmarkBoxRefine",
    "Yolo5LandmarkBoxMinAreaRatio",
    "Yolo5LandmarkBoxWidthScale",
    "Yolo5LandmarkBoxHeightScale",
    "Yolo5LandmarkBoxCenterYOffsetRatio",
    "Yolo5LandmarkBoxMinOriginalIou")) {
    Assert-Match "yolo detector reads $optionUse" $yoloDetectorText "_options\.$optionUse"
}

foreach ($parameter in @(
    "YoloLargeBoxWidthScale",
    "YoloLargeBoxHeightScale",
    "YoloLargeBoxMinAreaRatio",
    "YoloUseLandmarkBoxRefine",
    "YoloLandmarkBoxMinAreaRatio",
    "YoloLandmarkBoxWidthScale",
    "YoloLandmarkBoxHeightScale",
    "YoloLandmarkBoxCenterYOffsetRatio",
    "YoloLandmarkBoxMinOriginalIou",
    "YoloUseFaceOnnxRoiRefine",
    "YoloFaceOnnxRoiMinAreaRatio",
    "YoloFaceOnnxRoiMaxCandidates",
    "YoloUseLowConfidencePositionFilter",
    "YoloLowConfidencePositionMaxConfidence",
    "YoloLowConfidencePositionMinCenterYRatio",
    "YoloUseSmallAreaFilter",
    "YoloSmallAreaMaxAreaRatio",
    "YoloDropShortTrackMaxDetections",
    "YoloShortTrackMaxConfidence",
    "YoloLowerFrameTrackMaxConfidence")) {
    Assert-Match "smoke harness exposes $parameter" $smokeHarnessText "\`$$parameter"
}

foreach ($assignment in @(
    "LargeBoxWidthScale\s*=\s*yoloLargeBoxWidthScale",
    "LargeBoxHeightScale\s*=\s*yoloLargeBoxHeightScale",
    "LargeBoxMinAreaRatio\s*=\s*yoloLargeBoxMinAreaRatio",
    "UseYolo5LandmarkBoxRefine\s*=\s*yoloUseLandmarkBoxRefine",
    "Yolo5LandmarkBoxMinAreaRatio\s*=\s*yoloLandmarkBoxMinAreaRatio",
    "Yolo5LandmarkBoxWidthScale\s*=\s*yoloLandmarkBoxWidthScale",
    "Yolo5LandmarkBoxHeightScale\s*=\s*yoloLandmarkBoxHeightScale",
    "Yolo5LandmarkBoxCenterYOffsetRatio\s*=\s*yoloLandmarkBoxCenterYOffsetRatio",
    "Yolo5LandmarkBoxMinOriginalIou\s*=\s*yoloLandmarkBoxMinOriginalIou",
    "UseLowConfidencePositionFilter\s*=\s*yoloUseLowConfidencePositionFilter",
    "LowConfidencePositionMaxConfidence\s*=\s*yoloLowConfidencePositionMaxConfidence",
    "LowConfidencePositionMinCenterYRatio\s*=\s*yoloLowConfidencePositionMinCenterYRatio",
    "UseSmallAreaFilter\s*=\s*yoloUseSmallAreaFilter",
    "SmallAreaMaxAreaRatio\s*=\s*yoloSmallAreaMaxAreaRatio",
    "DropShortTrackMaxDetections\s*=\s*yoloDropShortTrackMaxDetections",
    "ShortTrackMaxConfidence\s*=\s*yoloShortTrackMaxConfidence",
    "LowerFrameTrackMaxConfidence\s*=\s*yoloLowerFrameTrackMaxConfidence")) {
    Assert-Match "smoke harness maps $assignment" $smokeHarnessText $assignment
}

Assert-Match "smoke harness supports yolo roi refine switch" $smokeHarnessText "if\s*\(useYolo\s*&&\s*yoloUseFaceOnnxRoiRefine\)"
Assert-Match "smoke harness builds yolo roi candidates" $smokeHarnessText "BuildLargeFaceRoiCandidates"
Assert-Match "smoke harness logs yolo roi refine" $smokeHarnessText "SmokeYoloFaceOnnxRoiRefine"
Assert-Match "smoke harness creates faceonnx roi detector" $smokeHarnessText "new FaceOnnxDetector\(CreateRoiRefinerDetectorOptions"

Write-Host "[YoloProfileVerify] all requested checks passed"
