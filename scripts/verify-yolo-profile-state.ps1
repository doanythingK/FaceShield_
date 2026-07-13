param(
    [string]$ProjectFile = "FaceShield.csproj",
    [string]$GitIgnore = ".gitignore",
    [string]$HomeViewModel = "ViewModels/Pages/HomePageViewModel.cs",
    [string]$HomeView = "Views/Pages/HomePageView.axaml",
    [string]$HomeViewCodeBehind = "Views/Pages/HomePageView.axaml.cs",
    [string]$WorkspaceViewModel = "ViewModels/Pages/WorkspaceViewModel.cs",
    [string]$AutoMaskRunSummary = "Services/Analysis/AutoMaskRunSummary.cs",
    [string]$AutoMaskGenerator = "Services/Analysis/AutoMaskGenerator.cs",
    [string]$AutoMaskPostProcessPipeline = "Services/Analysis/AutoMaskPostProcessPipeline.cs",
    [string]$AutoMaskTemporalPostProcessor = "Services/Analysis/AutoMaskTemporalPostProcessor.cs",
    [string]$AutoMaskRoiRefineStep = "Services/Analysis/AutoMaskRoiRefineStep.cs",
    [string]$YoloSceneCutPostProcessor = "Services/Analysis/YoloSceneCutPostProcessor.cs",
    [string]$TrackBuilder = "Services/Analysis/FaceTrackBuilder.cs",
    [string]$TrackInterpolator = "Services/Analysis/FaceTrackInterpolator.cs",
    [string]$SceneCutGuard = "Services/Analysis/FaceTrackSceneCutGuard.cs",
    [string]$FinalMaskPostProcessor = "Services/Analysis/YoloFinalMaskPostProcessor.cs",
    [string]$RoiRefiner = "Services/Analysis/FaceTrackRoiRefiner.cs",
    [string]$WorkspaceStateStore = "Services/Workspace/WorkspaceStateStore.cs",
    [string]$YoloOptions = "Services/FaceDetection/YoloFaceOnnxDetectorOptions.cs",
    [string]$YoloDetector = "Services/FaceDetection/YoloFaceOnnxDetector.cs",
    [string]$BackendEnum = "Services/FaceDetection/FaceDetectorBackend.cs",
    [string]$YoloModelEnum = "Services/FaceDetection/YoloFaceModelType.cs",
    [string]$YoloModelResolver = "scripts/resolve-yolo-model-path.ps1",
    [string]$SmokeHarness = "scripts/run-srcTest-smoke.ps1",
    [string]$FinalMaskCleanupVerifier = "scripts/verify-yolo-final-mask-cleanup.ps1",
    [string]$SmoothingCutBoundaryVerifier = "scripts/verify-yolo-temporal-smoothing-cut-boundary.ps1",
    [string]$AutoMosaicDefaultVerifier = "scripts/verify-auto-mosaic-default.ps1",
    [string]$YoloGuiSmokeResult = "YOLO_GUI_SMOKE_RESULT.md",
    [string]$AutoMosaicQualityPlan = "AUTO_MOSAIC_QUALITY_SPEED_PLAN.md"
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

function Assert-NotMatch {
    param(
        [string]$Name,
        [string]$Text,
        [string]$Pattern
    )

    if ($Text -match $Pattern) {
        throw "$Name unexpected pattern: $Pattern"
    }

    Write-Host "[YoloProfileVerify] pass $Name"
}

$projectText = Read-RepoFile $ProjectFile
$gitIgnoreText = Read-RepoFile $GitIgnore
$homeText = Read-RepoFile $HomeViewModel
$homeViewText = Read-RepoFile $HomeView
$homeViewCodeBehindText = Read-RepoFile $HomeViewCodeBehind
$workspaceText = Read-RepoFile $WorkspaceViewModel
$autoMaskRunSummaryText = Read-RepoFile $AutoMaskRunSummary
$autoMaskGeneratorText = Read-RepoFile $AutoMaskGenerator
$autoMaskPostProcessPipelineText = Read-RepoFile $AutoMaskPostProcessPipeline
$autoMaskTemporalPostProcessorText = Read-RepoFile $AutoMaskTemporalPostProcessor
$autoMaskRoiRefineStepText = Read-RepoFile $AutoMaskRoiRefineStep
$yoloSceneCutPostProcessorText = Read-RepoFile $YoloSceneCutPostProcessor
$trackBuilderText = Read-RepoFile $TrackBuilder
$trackInterpolatorText = Read-RepoFile $TrackInterpolator
$sceneCutGuardText = Read-RepoFile $SceneCutGuard
$finalMaskPostProcessorText = Read-RepoFile $FinalMaskPostProcessor
$roiRefinerText = Read-RepoFile $RoiRefiner
$state = Read-RepoFile $WorkspaceStateStore
$options = Read-RepoFile $YoloOptions
$yoloDetectorText = Read-RepoFile $YoloDetector
$backend = Read-RepoFile $BackendEnum
$modelEnum = Read-RepoFile $YoloModelEnum
$yoloModelResolverText = Read-RepoFile $YoloModelResolver
$smokeHarnessText = Read-RepoFile $SmokeHarness
$finalMaskCleanupVerifyText = Read-RepoFile $FinalMaskCleanupVerifier
$smoothingCutBoundaryVerifyText = Read-RepoFile $SmoothingCutBoundaryVerifier
$autoMosaicDefaultVerifyText = Read-RepoFile $AutoMosaicDefaultVerifier
$yoloGuiSmokeResultText = Read-RepoFile $YoloGuiSmokeResult
$autoMosaicQualityPlanText = Read-RepoFile $AutoMosaicQualityPlan

$postProcessCompatibilityText = @(
    $autoMaskPostProcessPipelineText
    $autoMaskTemporalPostProcessorText
    $autoMaskRoiRefineStepText
    $yoloSceneCutPostProcessorText
) -join "`n"
$postProcessCompatibilityText = $postProcessCompatibilityText `
    -replace '\bvideoPath\b', 'FrameList.VideoPath' `
    -replace '\bcancellationToken\b', 'token' `
    -replace '\bmaskProvider\b', '_maskProvider' `
    -replace '\bprofile\b', '_autoOptions.FilterProfile' `
    -replace '\buseTracking\b', '_autoOptions.UseTracking' `
    -replace '\btotalFrames\b', 'FrameList.TotalFrames' `
    -replace '\bApplyTemporalFixes\b', 'ApplyAutoTemporalFixes' `
    -replace '\bApplyTemporalSmoothing\b', 'ApplyAutoTemporalSmoothing' `
    -replace '\bRemoveTrackFillAcrossSceneCuts\b', 'RemoveYoloTrackFillAcrossSceneCuts' `
    -replace '\bProbeStrongCarrySceneCuts\b', 'ProbeYoloStrongCarrySceneCuts' `
    -replace '\bAutoMaskPostProcessLog\.', '' `
    -replace '_options', '_autoOptions'
$workspaceText = "$workspaceText`n$postProcessCompatibilityText"

Assert-Match "backend enum exposes yolo" $backend "YoloFaceOnnx\s*=\s*3"
Assert-Match "yolo model enum exposes v8" $modelEnum "YoloV8Face\s*=\s*0"
Assert-Match "yolo model enum exposes yolo5" $modelEnum "Yolo5Face\s*=\s*1"

Assert-Match "settings version is v10" $homeText "CurrentAutoSettingsVersion\s*=\s*10"
Assert-Match "home processing mode list defaults to tracked" $homeText "AutoProcessingModeOptions[\s\S]*new AutoProcessingModeOption\([^\r\n]*AutoMaskProcessingMode\.Tracked\)"
Assert-Match "home constructor selects first processing mode" $homeText "selectedAutoProcessingModeOption\s*=\s*AutoProcessingModeOptions\[0\]"
Assert-Match "saved processing mode defaults to tracked" $state "ProcessingMode\s*\{\s*get;\s*set;\s*\}\s*=\s*\(int\)AutoMaskProcessingMode\.Tracked"
Assert-Match "smoke harness defaults to tracked" $smokeHarnessText '\[string\]\$ProcessingMode\s*=\s*"Tracked"'
Assert-Match "smoke generated harness falls back to tracked" $smokeHarnessText "args\.Length\s*>\s*90[\s\S]*AutoMaskProcessingMode\.Tracked"
Assert-Match "home detector selector keeps faceonnx" $homeText "new AutoDetectorBackendOption\(""FaceONNX"",\s*FaceDetectorBackend\.FaceOnnx\)"
Assert-Match "home detector selector exposes yolo" $homeText "new AutoDetectorBackendOption\(""YOLO Face ONNX"",\s*FaceDetectorBackend\.YoloFaceOnnx\)"
Assert-Match "home yolo selector exposes v8" $homeText "new YoloModelTypeOption\(""YOLOv8-Face"",\s*YoloFaceModelType\.YoloV8Face\)"
Assert-Match "home yolo selector exposes yolo5" $homeText "new YoloModelTypeOption\(""YOLO5Face"",\s*YoloFaceModelType\.Yolo5Face\)"
Assert-Match "home view binds detector backend selector" $homeViewText "ItemsSource=""\{Binding AutoDetectorBackendOptions\}""[\s\S]*SelectedItem=""\{Binding SelectedAutoDetectorBackendOption\}"""
Assert-Match "home view shows yolo panel only for yolo backend" $homeViewText "IsVisible=""\{Binding IsYoloDetectorSelected\}""[\s\S]*YOLO"
Assert-Match "home view binds yolo model selector" $homeViewText "ItemsSource=""\{Binding YoloModelTypeOptions\}""[\s\S]*SelectedItem=""\{Binding SelectedYoloModelTypeOption\}"""
Assert-Match "home view binds yolo model path" $homeViewText "Text=""\{Binding AutoYoloModelPath,\s*Mode=TwoWay\}"""
Assert-Match "home view exposes yolo model picker" $homeViewText "Click=""PickYoloModel_Click"""
Assert-Match "home view exposes yolo model download button in detector row" $homeViewText "SelectedItem=""\{Binding SelectedAutoDetectorBackendOption\}""[\s\S]*Command=""\{Binding DownloadYoloModelCommand\}""[\s\S]*IsVisible=""\{Binding IsYoloDetectorSelected\}"""
Assert-Match "home view binds yolo download progress" $homeViewText "Value=""\{Binding YoloModelDownloadProgress\}"""
Assert-Match "home view binds yolo download status" $homeViewText "Text=""\{Binding YoloModelDownloadStatus\}"""
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
Assert-Match "project copies solution-local yolo models" $projectText ([regex]::Escape('None Update="Models\Yolo\*.onnx"'))
Assert-Match "project copies yolo models to output" $projectText "<CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>"
Assert-Match "project copies yolo models to publish" $projectText "<CopyToPublishDirectory>PreserveNewest</CopyToPublishDirectory>"
Assert-Match "gitignore excludes solution-local yolo models" $gitIgnoreText ([regex]::Escape("Models/Yolo/*.onnx"))
Assert-Match "home defines solution-local yolo folder" $homeText "DefaultYoloModelDirectory\s*=\s*""Models/Yolo"""
Assert-Match "home defines yolo5 default model names" $homeText "DefaultYolo5FaceModelFileNames[\s\S]*YoloV5Face\.onnx[\s\S]*Yolo5Face\.onnx"
Assert-Match "home defines yolov8 default model names" $homeText "DefaultYoloV8FaceModelFileNames[\s\S]*yolov8n-face-lindevs\.onnx[\s\S]*yolov8l-face-lindevs\.onnx"
Assert-Match "home default profile resolves solution-local model" $homeText "ModelPath\s*=\s*ResolveDefaultYoloModelPath\(modelType\)"
Assert-Match "home resolves selected or default yolo model path" $homeText "ResolveSelectedYoloModelPath[\s\S]*NormalizeYoloModelPath\(AutoYoloModelPath\)[\s\S]*ResolveDefaultYoloModelPath\(modelType\)"
Assert-Match "home scans default yolo model directories" $homeText "EnumerateDefaultYoloModelDirectories[\s\S]*AppContext\.BaseDirectory[\s\S]*Directory\.GetCurrentDirectory\(\)"
Assert-Match "home scans downloaded yolo model directory" $homeText "GetYoloModelDownloadDirectory[\s\S]*LocalApplicationData[\s\S]*Models[\s\S]*Yolo"
Assert-Match "home downloads yolo model command" $homeText "DownloadYoloModelAsync[\s\S]*HttpCompletionOption\.ResponseHeadersRead"
Assert-Match "home downloads yolo5 model" $homeText "YoloV5Face\.onnx[\s\S]*huggingface\.co/hayashiLin/deepfacelivemodels/resolve/main/YoloV5Face\.onnx"
Assert-Match "home downloads yolov8n model" $homeText "yolov8n-face-lindevs\.onnx[\s\S]*github\.com/lindevs/yolov8-face/releases/download/1\.0\.1/yolov8n-face-lindevs\.onnx"
Assert-Match "home download does not block faceonnx default" $homeText "CanDownloadYoloModel\s*=>\s*IsYoloDetectorSelected\s*&&\s*!IsYoloModelDownloading"
Assert-Match "home yolo status reads yolo provider" $homeText "IsYoloDetectorSelected[\s\S]*YoloFaceOnnxDetector\.GetLastExecutionProviderLabel\(\)[\s\S]*YoloFaceOnnxDetector\.GetLastExecutionProviderError\(\)"
Assert-Match "factory uses resolved yolo model path" $homeText "var\s+yoloModelPath\s*=\s*ResolveSelectedYoloModelPath\(yoloModelType\)[\s\S]*ModelPath\s*=\s*yoloModelPath"
Assert-Match "yolo readiness points to solution-local folder" $homeText "DefaultYoloModelDirectory"
Assert-Match "script yolo model resolver exists" $yoloModelResolverText "function\s+Resolve-YoloModelPath"
Assert-Match "script resolver prefers solution-local model folder" $yoloModelResolverText "Models\\Yolo"
Assert-Match "script resolver keeps tmp model fallback" $yoloModelResolverText "\.tmp\\models"
Assert-Match "smoke harness uses yolo model resolver" $smokeHarnessText "Resolve-YoloModelPath[\s\S]*YoloModelType"
Assert-Match "smoke harness keeps faceonnx default without yolo path" $smokeHarnessText '\[string\]::IsNullOrWhiteSpace\(\$YoloModelPath\)[\s\S]*""'

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
        "TileOverlapRatio",
        "DownscaleRatio",
        "DownscaleQuality",
        "AutoTrackingEnabled",
        "AutoDetectEveryNFrames",
        "ParallelSessionCount")) {
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
        "TileOverlapRatio",
        "DownscaleRatio",
        "DownscaleQuality",
        "AutoTrackingEnabled",
        "AutoDetectEveryNFrames",
        "ParallelSessionCount")) {
        Assert-Match "state has $prefix$suffix" $state "public\s+.*\s+$prefix$suffix\s*\{"
        Assert-Match "home persists $prefix$suffix" $homeText "$prefix$suffix\s*=\s*(?:\(int\)\s*)?yolo"
    }
}

Assert-Match "legacy active yolo profile remains persisted" $state "public\s+.*\s+YoloModelPath\s*\{"
Assert-Match "home persists active legacy profile" $homeText "YoloModelPath\s*=\s*activeYoloProfile\.ModelPath"
Assert-Match "home migrates saved yolo5 profile" $homeText "ReadSavedYoloProfile\(saved,\s*YoloFaceModelType\.Yolo5Face"
Assert-Match "home migrates saved yolo v8 profile" $homeText "ReadSavedYoloProfile\(saved,\s*YoloFaceModelType\.YoloV8Face"
Assert-Match "home captures yolo auto pipeline profile" $homeText "CaptureCurrentYoloProfile\(\)[\s\S]*DownscaleRatio\s*=\s*SelectedDownscaleOption\?\.Ratio[\s\S]*AutoTrackingEnabled\s*=\s*AutoTrackingEnabled[\s\S]*AutoDetectEveryNFrames\s*=\s*Math\.Max\(1,\s*AutoDetectEveryNFrames\)[\s\S]*ParallelSessionCount\s*=\s*Math\.Max\(1,\s*SelectedParallelSessionCount\)"
Assert-Match "home applies yolo auto pipeline profile" $homeText "ApplyYoloProfile\(YoloProfileState\s+profile\)[\s\S]*SelectedDownscaleOption\s*=\s*downscale[\s\S]*SelectedDownscaleQualityOption\s*=\s*quality[\s\S]*AutoTrackingEnabled\s*=\s*profile\.AutoTrackingEnabled[\s\S]*AutoDetectEveryNFrames\s*=\s*Math\.Max\(1,\s*profile\.AutoDetectEveryNFrames\)[\s\S]*SelectedParallelSessionCount\s*=\s*Math\.Max\(1,\s*profile\.ParallelSessionCount\)"
Assert-Match "home reads saved yolo5 auto pipeline profile" $homeText "ResolveSavedYoloDownscaleRatio\(saved\.Yolo5DownscaleRatio[\s\S]*ResolveSavedYoloDownscaleQuality\([\s\S]*saved\.Yolo5DownscaleQuality[\s\S]*Yolo5AutoTrackingEnabled[\s\S]*Yolo5AutoDetectEveryNFrames[\s\S]*Yolo5ParallelSessionCount"
Assert-Match "home reads saved yolov8 auto pipeline profile" $homeText "ResolveSavedYoloDownscaleRatio\(saved\.YoloV8DownscaleRatio[\s\S]*ResolveSavedYoloDownscaleQuality\([\s\S]*saved\.YoloV8DownscaleQuality[\s\S]*YoloV8AutoTrackingEnabled[\s\S]*YoloV8AutoDetectEveryNFrames[\s\S]*YoloV8ParallelSessionCount"
Assert-Match "home suppresses tracking restart while applying yolo profile" $homeText "OnAutoTrackingEnabledChanged[\s\S]*if\s*\(_isApplyingYoloProfile\)"
Assert-Match "home suppresses detect interval restart while applying yolo profile" $homeText "OnAutoDetectEveryNFramesChanged[\s\S]*if\s*\(_isApplyingYoloProfile\)"
Assert-Match "home suppresses downscale restart while applying yolo profile" $homeText "OnSelectedDownscaleOptionChanged[\s\S]*if\s*\(_isApplyingYoloProfile\)"
Assert-Match "home suppresses downscale quality restart while applying yolo profile" $homeText "OnSelectedDownscaleQualityOptionChanged[\s\S]*if\s*\(_isApplyingYoloProfile\)"
Assert-Match "home suppresses parallel restart while applying yolo profile" $homeText "OnSelectedParallelSessionCountChanged[\s\S]*if\s*\(_isApplyingYoloProfile\)"

Assert-Match "auto options choose yolo filter profile" $homeText "FilterProfile\s*=\s*IsYoloDetectorSelected\s*\?\s*FaceFilterProfile\.Yolo\s*:\s*FaceFilterProfile\.FaceOnnx"
Assert-Match "factory creates yolo detector options" $homeText "FaceDetectorFactoryOptions\.ForYoloFaceOnnx\(\s*new YoloFaceOnnxDetectorOptions"
Assert-Match "factory falls back to faceonnx options" $homeText "return FaceDetectorFactoryOptions\.ForOnnx\(faceOnnxOptions\)"
Assert-Match "faceonnx thresholds stay separate" $homeText "DetectionThreshold\s*=\s*\(float\)Math\.Clamp\(AutoDetectionThreshold"
Assert-Match "yolo objectness comes from yolo property" $homeText "ObjectnessThreshold\s*=\s*\(float\)Math\.Clamp\(AutoYoloObjectnessThreshold"
Assert-Match "yolo confidence comes from yolo property" $homeText "ConfidenceThreshold\s*=\s*\(float\)Math\.Clamp\(AutoYoloConfidenceThreshold"
Assert-Match "yolo nms comes from yolo property" $homeText "NmsThreshold\s*=\s*\(float\)Math\.Clamp\(AutoYoloNmsThreshold"
Assert-Match "home enables conservative top small low-confidence yolo filter" $homeText "UseTopSmallLowConfidenceFilter\s*=\s*true[\s\S]*TopSmallLowConfidenceMaxCenterYRatio\s*=\s*0\.07[\s\S]*TopSmallLowConfidenceMaxAreaRatio\s*=\s*0\.0045[\s\S]*TopSmallLowConfidenceMaxConfidence\s*=\s*0\.55f"
Assert-Match "yolo tiling comes from yolo property" $homeText "UseTiling\s*=\s*AutoYoloUseTiling"
Assert-Match "yolo tile-only maps to full-frame switch" $homeText "IncludeFullFrameWhenTiling\s*=\s*!AutoYoloTileOnly"

Assert-Match "workspace autotune guarded to faceonnx backend" $workspaceText "_detectorFactoryOptions\.Backend\s*==\s*FaceDetectorBackend\.FaceOnnx[\s\S]*DetectorAutoTuner\.TryTune"
Assert-Match "workspace autotune updates faceonnx options only" $workspaceText "detectorFactoryOptions\s*=\s*detectorFactoryOptions\.WithFaceOnnxOptions\(detectorOptions\)"
Assert-Match "workspace keeps configured filter profile in run options" $workspaceText "FilterProfile\s*=\s*effectiveAutoOptions\.FilterProfile"
Assert-Match "workspace uses yolo threshold for yolo low-confidence review" $workspaceText "GetLowConfidenceCutoff\(\)[\s\S]*_detectorFactoryOptions\.Backend\s+switch[\s\S]*FaceDetectorBackend\.YoloFaceOnnx[\s\S]*YoloFaceOnnxOptions\.ConfidenceThreshold"
Assert-Match "workspace keeps faceonnx threshold for faceonnx low-confidence review" $workspaceText "GetLowConfidenceCutoff\(\)[\s\S]*FaceDetectorBackend\.FaceOnnx[\s\S]*FaceOnnxOptions\.ConfidenceThreshold\s*\?\?\s*defaults\.Confidence"
Assert-Match "workspace supports alternate backend thresholds for low-confidence review" $workspaceText "GetLowConfidenceCutoff\(\)[\s\S]*FaceDetectorBackend\.ScrfdOnnx[\s\S]*ScrfdOnnxOptions\.ConfidenceThreshold[\s\S]*FaceDetectorBackend\.YuNetOnnx[\s\S]*YuNetOnnxOptions\.ConfidenceThreshold"
Assert-Match "auto summary logs start frame" $autoMaskRunSummaryText "int\s+StartFrameIndex[\s\S]*startFrame=\{StartFrameIndex\}"
Assert-Match "auto generator passes start frame into summaries" $autoMaskGeneratorText "new\s+AutoMaskRunSummary\([\s\S]*totalFrames,\s*start,[\s\S]*processed[\s\S]*new\s+AutoMaskRunSummary\([\s\S]*totalFrames,\s*start,[\s\S]*decoded"
Assert-Match "workspace requires every analysis frame" $workspaceText "AutoDetectionCompletionTailToleranceFrames\s*=\s*0"
Assert-Match "workspace skips postprocess for incomplete auto runs" $workspaceText "if\s*\(!IsAutoDetectionRunComplete\(generator\.LastRunSummary,\s*lastProcessed,\s*FrameList\.TotalFrames\)\)[\s\S]*?AutoMaskPostProcessSkipped[\s\S]*?return\s+false;"
Assert-Match "workspace resolves summary last frame" $workspaceText "AutoMaskRunSummary\?\s+summary[\s\S]*?summaryLastFrame\s*=\s*runSummary\.StartFrameIndex\s*\+\s*runSummary\.ProcessedFrames\s*-\s*1"
Assert-Match "workspace tracking toggle gates temporal fixes" $workspaceText "FaceTrackPostProcessResult\s+ApplyAutoTemporalFixes\([\s\S]*if\s*\(!_autoOptions\.UseTracking\)[\s\S]*return\s+FaceTrackPostProcessResult\.Empty"
Assert-Match "workspace tracking toggle gates temporal smoothing" $workspaceText "_autoOptions\.UseTracking[\s\S]*ApplyAutoTemporalSmoothing\("
Assert-Match "workspace smoothing does not search across long scene gaps" $workspaceText "TemporalSmoothSearchWindowFrames\s*=\s*2[\s\S]*BuildTemporalSmoothingCutStarts\(blockedCutPairs\)[\s\S]*FindNearestTemporalFaces\(facesByFrame,\s*i,\s*-1,\s*TemporalSmoothSearchWindowFrames,\s*blockedCutStarts\)[\s\S]*FindNearestTemporalFaces\(facesByFrame,\s*i,\s*1,\s*TemporalSmoothSearchWindowFrames,\s*blockedCutStarts\)[\s\S]*searched\s*>\s*maxDistanceFrames"
Assert-Match "workspace smoothing blocks pre-smooth yolo scene cuts" $workspaceText "yoloPreSmoothCutPairs[\s\S]*preSmoothGuard\.CutFramePairs[\s\S]*yoloPreSmoothStrongCarryProbeCutPairs[\s\S]*preSmoothStrongCarryProbe\.CutFramePairs[\s\S]*ApplyAutoTemporalSmoothing\([\s\S]*_autoOptions\.FilterProfile\s*==\s*FaceFilterProfile\.Yolo[\s\S]*CombineCutFramePairs\(yoloPreSmoothCutPairs,\s*yoloPreSmoothStrongCarryProbeCutPairs\)[\s\S]*BuildTemporalSmoothingCutStarts[\s\S]*Split\(""->""[\s\S]*IsBlockedTemporalSmoothingStep"
Assert-Match "smoothing cut boundary verifier covers blocked direct pairs" $smoothingCutBoundaryVerifyText "10->13[\s\S]*AssertSet[\s\S]*13,\s*12[\s\S]*12,\s*13[\s\S]*InvokeFindNearest\(facesByFrame,\s*13,\s*-1,\s*2,\s*blocked\)"
Assert-Match "workspace yolo track profile exists" $workspaceText "YoloConfirmedTrackHoldFrames\s*=\s*5[\s\S]*if\s*\([^)]*FaceFilterProfile\.Yolo\)[\s\S]*MaxLostFillFrames\s*=\s*0[\s\S]*MaxInitialFillFrames\s*=\s*3[\s\S]*InitialFillRequiresInwardMotion\s*=\s*true[\s\S]*MaxConfirmedTrackHoldFrames\s*=\s*YoloConfirmedTrackHoldFrames[\s\S]*AllowSmallTrackLostFill\s*=\s*true[\s\S]*StrongConfidence\s*=\s*0\.58f[\s\S]*DropShortTrackMaxDetections\s*=\s*2[\s\S]*DropShortSmallTrackMaxDetections\s*=\s*3[\s\S]*ShortTrackMaxConfidence\s*=\s*0\.48f[\s\S]*DropSparseTrackMaxDetections\s*=\s*3[\s\S]*DropSparseTrackMinSpanFrames\s*=\s*8[\s\S]*DropSparseTrackMaxDensity\s*=\s*0\.42[\s\S]*SparseTrackMaxConfidence\s*=\s*0\.56f[\s\S]*EdgeTailMaxConfidence\s*=\s*0\.50f[\s\S]*EdgeLostFillMaxConfidence\s*=\s*0\.60f[\s\S]*MaxConfirmedTrackBridgeCenterShiftRatio\s*=\s*1\.20[\s\S]*UnstableTailMaxConfidence\s*=\s*0\.45f[\s\S]*LowerFrameTrackMaxConfidence\s*=\s*0\.50f"
Assert-Match "workspace logs sparse temporal removals" $workspaceText "removedSparse=\{result\.RemovedSparseFaces\}"
Assert-Match "workspace logs unstable-tail temporal removals" $workspaceText "removedUnstableTail=\{result\.RemovedUnstableTailFaces\}"
Assert-Match "workspace logs edge-tail temporal removals" $workspaceText "removedEdgeTail=\{result\.RemovedEdgeTailFaces\}"
Assert-Match "track postprocess result exposes unstable-tail removals" $trackInterpolatorText "TrimUnstableLowConfidenceTails[\s\S]*RemovedUnstableTailFaces"
Assert-Match "workspace logs final yolo mask summary" $workspaceText "\[FinalMaskSummary\][\s\S]*profile=Yolo[\s\S]*shortGaps=\{shortGapCount\}[\s\S]*largeJumpGaps=\{largeJumpGapRanges\.Count\}[\s\S]*largeJumpRanges=\{FormatTextList\(largeJumpGapRanges\)\}[\s\S]*isolatedFrames=\{FormatFrameList\(isolatedFrames\)\}[\s\S]*lowConf=\{lowConfidenceRows\}[\s\S]*lowConfFrames=\{FormatFrameList\(lowConfidenceFrames[\s\S]*weakNonEdge=\{weakNonEdgeRows\}[\s\S]*weakNonEdgeFrames=\{FormatFrameList\(weakNonEdgeFrames[\s\S]*edgeWeak=\{edgeWeakRows\}[\s\S]*edgeWeakFrames=\{FormatFrameList\(edgeWeakFrames[\s\S]*topEdgeWeak=\{topEdgeWeakRows\}[\s\S]*topEdgeWeakFrames=\{FormatFrameList\(topEdgeWeakFrames[\s\S]*topEdgeLarge=\{topEdgeLargeRows\}[\s\S]*topEdgeLargeFrames=\{FormatFrameList\(topEdgeLargeFrames[\s\S]*upperWeak=\{upperWeakRows\}[\s\S]*upperWeakFrames=\{FormatFrameList\(upperWeakFrames[\s\S]*lowerWeak=\{lowerWeakRows\}[\s\S]*lowerWeakFrames=\{FormatFrameList\(lowerWeakFrames[\s\S]*aspectBad=\{aspectBadRows\}[\s\S]*aspectBadFrames=\{FormatFrameList\(aspectBadFrames[\s\S]*tinyWeak=\{tinyWeakRows\}[\s\S]*tinyWeakFrames=\{FormatFrameList\(tinyWeakFrames[\s\S]*tinyShort=\{tinyShortRows\}[\s\S]*tinyShortFrames=\{FormatFrameList\(tinyShortFrames[\s\S]*protectedSceneCarry=\{protectedSceneCarryFrames\.Length\}[\s\S]*protectedSceneCarryFrames=\{FormatFrameList\(protectedSceneCarryFrames\)\}[\s\S]*reviewRequired=\{reviewReasons\.Count > 0\}[\s\S]*reviewReasons=\{FormatTextList\(reviewReasons\)\}"
Assert-Match "workspace final mask summary builds review reasons" $workspaceText "BuildFinalMaskReviewReasons\([\s\S]*short-gap[\s\S]*large-jump-gap[\s\S]*isolated-mask[\s\S]*low-confidence-review[\s\S]*weak-non-edge[\s\S]*edge-weak-review[\s\S]*top-edge-weak-review[\s\S]*top-edge-large-review[\s\S]*upper-weak[\s\S]*lower-weak[\s\S]*aspect-outlier[\s\S]*tiny-weak[\s\S]*tiny-short[\s\S]*scene-carry-protected"
Assert-Match "workspace final summary reports tiny short residuals" $workspaceText "YoloFinalMaskTinyShortConfidenceMax\s*=\s*0\.62f[\s\S]*YoloFinalMaskTinyShortAreaRatio\s*=\s*0\.0009[\s\S]*tinyShortRows\+\+"
Assert-Match "workspace final summary detects large jumps" $workspaceText "FinalMaskLargeJumpAreaChangeRatio\s*=\s*4\.0[\s\S]*FinalMaskLargeJumpCenterShift\s*=\s*0\.20[\s\S]*TryGetBestFinalMaskFace[\s\S]*GetFinalMaskAreaChange[\s\S]*GetFinalMaskCenterShift"
Assert-Match "workspace final mask summary covers generic and yolo profiles" $autoMaskPostProcessPipelineText "private\s+AutoMaskPostProcessFinalSummary\s+LogFinalMaskSummary\([\s\S]*_options\.FilterProfile\s*!=\s*FaceFilterProfile\.Yolo[\s\S]*genericEvidenceLogLine[\s\S]*profile=Yolo"
Assert-Match "workspace removes weak isolated final yolo masks before unblocked gap fill" $workspaceText "_autoOptions\.FilterProfile\s*==\s*FaceFilterProfile\.Yolo[\s\S]*RemoveYoloWeakIsolatedFinalMasks\([\s\S]*FrameList\.VideoPath,[\s\S]*token,[\s\S]*fillStableGaps:\s*false[\s\S]*YoloFinalMaskPostProcessor[\s\S]*YoloFinalMaskCleanup"
Assert-Match "workspace logs yolo final cleanup reasons" $workspaceText "removedWeakUnsupported=\{cleanup\.RemovedWeakUnsupportedFaces\}[\s\S]*removedMediumUnsupported=\{cleanup\.RemovedMediumUnsupportedFaces\}[\s\S]*removedWeakShortClusters=\{cleanup\.RemovedWeakShortClusterFaces\}[\s\S]*removedWeakTextureClusters=\{cleanup\.RemovedWeakTextureClusterFaces\}[\s\S]*removedWeakTinyClusters=\{cleanup\.RemovedWeakTinyClusterFaces\}[\s\S]*removedTinyShortClusters=\{cleanup\.RemovedTinyShortClusterFaces\}[\s\S]*removedTinyIsolated=\{cleanup\.RemovedTinyIsolatedFaces\}[\s\S]*removedTopEdgeWeakClusters=\{cleanup\.RemovedTopEdgeWeakClusterFaces\}[\s\S]*removedTopEdgeLargeDuplicates=\{cleanup\.RemovedTopEdgeLargeDuplicateFaces\}[\s\S]*removedUpperWeakClusters=\{cleanup\.RemovedUpperWeakClusterFaces\}[\s\S]*removedLowerWeakClusters=\{cleanup\.RemovedLowerWeakClusterFaces\}[\s\S]*removedAspectOutliers=\{cleanup\.RemovedAspectOutlierClusterFaces\}"
Assert-Match "workspace has configurable yolo cleanup log label" $workspaceText 'string\s+logLabel\s*=\s*"YoloFinalMaskCleanup"[\s\S]*\$"\[\{logLabel\}\]'
Assert-Match "workspace labels post-scene yolo cleanup separately" $workspaceText "logLabel:\s*""YoloFinalMaskPostSceneCleanup"""
Assert-Match "workspace has configurable yolo gap-fill log labels" $workspaceText 'string\s+gapFillLogLabel\s*=\s*"YoloFinalMaskGapFill"[\s\S]*string\s+gapFillSceneCutGuardLogLabel\s*=\s*"YoloFinalMaskGapFillSceneCutGuard"[\s\S]*\$\"\[\{gapFillLogLabel\}\][\s\S]*\$\"\[\{gapFillSceneCutGuardLogLabel\}\]'
Assert-Match "workspace labels post-scene yolo gap fill separately" $workspaceText 'gapFillLogLabel:\s*"YoloFinalMaskPostSceneGapFill"[\s\S]*gapFillSceneCutGuardLogLabel:\s*"YoloFinalMaskPostSceneGapFillSceneCutGuard"'
Assert-Match "workspace cleanup logger can emit no-removal rows" $workspaceText "bool\s+logWhenNoRemovals\s*=\s*false[\s\S]*cleanup\.RemovedWeakIsolatedFaces\s*>\s*0\s*\|\|\s*logWhenNoRemovals"
Assert-Match "workspace logs post-scene yolo cleanup even when no removals" $workspaceText "logLabel:\s*""YoloFinalMaskPostSceneCleanup""[\s\S]*logWhenNoRemovals:\s*true"
Assert-Match "workspace combines every yolo scene-cut source" $autoMaskPostProcessPipelineText "yoloCutPairs\s*=\s*CombineCutFramePairs\(\s*yoloPreSmoothCutPairs,\s*yoloPreSmoothStrongCarryProbeCutPairs,\s*yoloPostSmoothCutPairs,\s*yoloCleanupPass\.CutFramePairs,\s*yoloStrongCarryProbeCutPairs\)"
Assert-Match "workspace passes yolo cut pairs to enabled final gap fill" $autoMaskPostProcessPipelineText "RemoveYoloWeakIsolatedFinalMasks\([\s\S]*?fillStableGaps:\s*enableGapFill,[\s\S]*?blockedCutFramePairs:\s*yoloCutPairs"
Assert-Match "workspace final gap fill helper accepts cut-pair blockers" $autoMaskPostProcessPipelineText "bool\s+fillStableGaps\s*=\s*true,[\s\S]*?IReadOnlyCollection<string>\?\s*blockedCutFramePairs"
Assert-Match "workspace defaults stable yolo gaps to eight frames" $autoMaskPostProcessPipelineText "YoloFinalMaskStableGapMaxFrames\s*=\s*8[\s\S]*gapFillMaxGapFrames\s*=\s*YoloFinalMaskStableGapMaxFrames"
Assert-Match "workspace applies stable yolo gap blockers" $autoMaskPostProcessPipelineText "FillShortStableGaps\(\s*_maskProvider,[\s\S]*MaxGapFrames\s*=\s*gapFillMaxGapFrames[\s\S]*BlockedSceneCarryFrameIndices\s*=\s*sceneCarryBlockedFrameIndices"
Assert-Match "workspace logs stable yolo gap decisions" $autoMaskPostProcessPipelineText "gapFillLogLabel[\s\S]*blockedByCut=\{gapFill\.BlockedCutGapFaces\}[\s\S]*blockedByCleanup=\{gapFill\.BlockedCleanupGapFrames\}[\s\S]*blockedBySceneCarry=\{gapFill\.BlockedSceneCarryGapFrames\}[\s\S]*suppressedWeakGeometryAnchors=\{gapFill\.SuppressedWeakGeometryAnchorChecks\}[\s\S]*suppressedRiskyGeometryAnchors=\{gapFill\.SuppressedRiskyGeometryAnchorChecks\}[\s\S]*unsupportedWeakAnchors=\{gapFill\.UnsupportedWeakAnchorChecks\}"
Assert-Match "workspace blocks final gap fill across known yolo scene cuts" $workspaceText "FillYoloStableFinalMaskGaps[\s\S]*BlockedCutFramePairs\s*=\s*blockedCutFramePairs\s*\?\?\s*Array\.Empty<string>\(\)"
Assert-Match "workspace guards initial yolo gap fill when scene cleanup is enabled" $autoMaskPostProcessPipelineText "yoloCleanupPass\s*=\s*RemoveYoloWeakIsolatedFinalMasks\([\s\S]*?fillStableGaps:\s*enableGapFill,[\s\S]*?skipSceneCutGuard:\s*!enableSceneCutCleanup"
Assert-Match "workspace carries cleanup and scene-cut blockers into post-scene gap fill" $autoMaskPostProcessPipelineText "postSceneCleanupPass\s*=\s*enableWeakIsolationCleanup[\s\S]*?blockedCutFramePairs:\s*yoloCutPairs,[\s\S]*?additionalBlockedFaces:\s*CombineFaceInfos\(\s*yoloCleanupPass\.RemovedFacesInfo,\s*yoloCarryCleanup\.RemovedFacesInfo\)[\s\S]*?sceneCarryBlockedFaces:\s*yoloCarryCleanup\.RemovedFacesInfo,[\s\S]*?sceneCarryBlockedFrameIndices:\s*yoloSceneCutBlockedFrames"
Assert-Match "workspace merges current cleanup removals with prior gap blockers" $autoMaskPostProcessPipelineText "CombineFaceInfos\(additionalBlockedFaces,\s*cleanup\.RemovedFacesInfo\)"
Assert-Match "workspace reruns scene-cut carry cleanup for post-gap-fill cuts" $autoMaskPostProcessPipelineText "postSceneCleanupPass\.CutFramePairs\.Count\s*>\s*0[\s\S]*postGapFillCutPairs\s*=\s*CombineCutFramePairs\(yoloCutPairs,\s*postSceneCleanupPass\.CutFramePairs\)[\s\S]*RemoveSceneCutCarryRemnants\([\s\S]*postGapFillCutPairs[\s\S]*BuildSceneCutCarryBlockedFrames\([\s\S]*postGapFillCutPairs[\s\S]*stage=post-gap-fill"
Assert-Match "workspace cleans weak yolo remnants after post-gap-fill carry cleanup without refilling" $workspaceText "stage=post-gap-fill[\s\S]*RemoveYoloWeakIsolatedFinalMasks\([\s\S]*fillStableGaps:\s*false[\s\S]*logLabel:\s*""YoloFinalMaskPostGapFillCleanup"""
Assert-Match "workspace blocks final gap fill from cleanup removals" $workspaceText "FillYoloStableFinalMaskGaps[\s\S]*BlockedFrameIndices\s*=\s*blockedFrameIndices\s*\?\?\s*Array\.Empty<int>\(\)[\s\S]*BlockedFaces\s*=\s*blockedFaces\s*\?\?\s*Array\.Empty<FaceTrackFilledFace>\(\)"
Assert-Match "workspace guards stable yolo final gaps across scene cuts" $workspaceText "FillYoloStableFinalMaskGaps[\s\S]*gapFill\.CutGuardFacesInfo[\s\S]*FaceTrackSceneCutGuard\(\)\.Apply[\s\S]*gapFillSceneCutGuardLogLabel"
Assert-Match "workspace broadens yolo scene cut carry candidates conservatively" $workspaceText "YoloSceneCutDirectCarryMaxConfidence\s*=\s*0\.98f[\s\S]*YoloSceneCutDirectCarryMinSourceConfidence\s*=\s*0\.58f[\s\S]*YoloSceneCutPostCutCarryMaxConfidence\s*=\s*0\.78f[\s\S]*YoloSceneCutDifferenceThreshold\s*=\s*0\.15[\s\S]*YoloSceneCutDirectDifferenceThreshold\s*=\s*0\.20[\s\S]*YoloSceneCutDirectDifferenceMaxCandidates\s*=\s*240[\s\S]*YoloSceneCutCarryProbeFrames\s*=\s*SuspiciousNoFaceMaxGap[\s\S]*YoloSceneCutMatchingTailMaxFrames\s*=\s*SuspiciousNoFaceMaxGap[\s\S]*YoloSceneCutMatchingTailMaxConfidence\s*=\s*0\.98f[\s\S]*YoloSceneCutCandidateMatchMinIou\s*=\s*0\.55[\s\S]*YoloSceneCutCandidateMatchMaxCenterShiftRatio\s*=\s*0\.80[\s\S]*YoloSceneCutCandidateMatchMaxAreaChangeRatio\s*=\s*3\.5[\s\S]*YoloSceneCutPostCutLookbackFrames\s*=\s*5[\s\S]*BuildWeakTrackTransitionCandidates[\s\S]*maxTargetConfidence:\s*YoloSceneCutDirectCarryMaxConfidence[\s\S]*minConfidenceDrop:\s*0\.0f[\s\S]*maxPostCutCarryFrames:\s*YoloSceneCutCarryProbeFrames[\s\S]*minSourceConfidence:\s*YoloSceneCutDirectCarryMinSourceConfidence[\s\S]*BuildWeakPostCutCarryCandidates[\s\S]*maxTargetConfidence:\s*YoloSceneCutPostCutCarryMaxConfidence[\s\S]*maxCarryFrames:\s*YoloSceneCutCarryProbeFrames[\s\S]*sourceLookbackFrames:\s*YoloSceneCutPostCutLookbackFrames[\s\S]*includeEdgeCandidates:\s*true[\s\S]*var\s+candidates\s*=\s*directCandidates[\s\S]*\.Concat\(postCutCandidates\)[\s\S]*\.Concat\(trackPost\.FilledGapFacesInfo\)[\s\S]*directDifferenceThreshold:\s*YoloSceneCutDirectDifferenceThreshold[\s\S]*directDifferenceMaxChecks:\s*YoloSceneCutDirectDifferenceMaxCandidates[\s\S]*removeMatchingTailFrames:\s*YoloSceneCutMatchingTailMaxFrames[\s\S]*removeMatchingTailMaxConfidence:\s*YoloSceneCutMatchingTailMaxConfidence[\s\S]*candidateMatchMinIou:\s*YoloSceneCutCandidateMatchMinIou"
Assert-Match "workspace probes strong yolo scene carry without immediate removal" $workspaceText "YoloSceneCutStrongCarryProbeMaxConfidence\s*=\s*1\.0f[\s\S]*YoloSceneCutStrongCarryProbeMinConfidence\s*=\s*0\.78f[\s\S]*YoloSceneCutStrongCarryProbeMinSourceConfidence\s*=\s*0\.58f[\s\S]*ProbeYoloStrongCarrySceneCuts[\s\S]*string\s+stage[\s\S]*BuildWeakPostCutCarryCandidates\([\s\S]*maxTargetConfidence:\s*YoloSceneCutStrongCarryProbeMaxConfidence[\s\S]*minSourceConfidence:\s*YoloSceneCutStrongCarryProbeMinSourceConfidence[\s\S]*minTargetConfidence:\s*YoloSceneCutStrongCarryProbeMinConfidence[\s\S]*removeCandidates:\s*false[\s\S]*YoloStrongCarrySceneCutProbe[\s\S]*stage=\{stage\}"
Assert-Match "workspace strong carry probe includes independent continuation evidence" $workspaceText "ProbeYoloStrongCarrySceneCuts[\s\S]*BuildWeakPostCutCarryCandidates\([\s\S]*includeIndependentStrongContinuation:\s*true"
Assert-Match "scene cut guard supports probe-only mode" $sceneCutGuardText "BuildWeakPostCutCarryCandidates[\s\S]*minSourceConfidence[\s\S]*minTargetConfidence[\s\S]*removeCandidates\s*=\s*true[\s\S]*if\s*\(!removeCandidates\)[\s\S]*continue;"
Assert-Match "docs record yolo direct scene cut budget" $yoloGuiSmokeResultText "direct source->target frame difference[\s\S]*cap direct source->target checks at 240"
Assert-Match "plan records yolo direct scene cut budget" $autoMosaicQualityPlanText "YoloSceneCutDirectDifferenceMaxCandidates=240[\s\S]*cap expensive direct source-to-target frame comparisons at 240"
Assert-Match "docs record yolo lost-fill disabled by default" $yoloGuiSmokeResultText "MaxLostFillFrames=0[\s\S]*MaxInitialFillFrames=3"
Assert-NotMatch "docs do not claim yolo lost-fill default is two frames" $yoloGuiSmokeResultText "MaxLostFillFrames=2"
Assert-Match "plan records yolo direct threshold" $autoMosaicQualityPlanText "source->target>=0\.20"
Assert-Match "plan records yolo adjacent and direct thresholds" $autoMosaicQualityPlanText "0\.15[\s\S]*adjacent-pair checks[\s\S]*0\.20[\s\S]*direct source-to-target checks"
Assert-Match "workspace purges residual yolo carry masks after scene cuts" $workspaceText "YoloSceneCutCarryPurgeFrames\s*=\s*15[\s\S]*YoloSceneCutCarryBlockFrames\s*=\s*15[\s\S]*YoloSceneCutExtendedWeakCarryMaxConfidence\s*=\s*0\.78f[\s\S]*YoloSceneCutCarryPurgeMaxConfidence\s*=\s*0\.98f[\s\S]*RemoveSceneCutCarryRemnants\([\s\S]*new\s+YoloSceneCutCarryCleanupOptions[\s\S]*ExtendedWeakCarryFrames\s*=\s*YoloSceneCutCarryBlockFrames[\s\S]*SourceLookbackFrames\s*=\s*YoloSceneCutPostCutLookbackFrames[\s\S]*ExtendedWeakMaxConfidence\s*=\s*YoloSceneCutExtendedWeakCarryMaxConfidence[\s\S]*BuildSceneCutCarryBlockedFrames\([\s\S]*YoloSceneCutCarryBlockFrames[\s\S]*YoloSceneCutCarryCleanup[\s\S]*removedUnsupportedStrong=\{yoloCarryCleanup\.RemovedUnsupportedStrongCarryLikeFaces\}[\s\S]*removedUnsupportedStrongFrames=\{FormatFrameList\(yoloCarryCleanup\.RemovedUnsupportedStrongCarryLikeFrameIndices\)\}[\s\S]*protectedStrong=\{yoloCarryCleanup\.ProtectedStrongCarryLikeFaces\}[\s\S]*protectedStrongFrames=\{FormatFrameList\(yoloCarryCleanup\.ProtectedStrongCarryLikeFrameIndices\)\}[\s\S]*blockedFrames=\{FormatFrameList\(yoloSceneCutBlockedFrames\)\}[\s\S]*blockFrames=\{YoloSceneCutCarryBlockFrames\}[\s\S]*extendedWeakMaxConfidence=\{YoloSceneCutExtendedWeakCarryMaxConfidence"
Assert-Match "final mask postprocessor owns scene-cut carry cleanup" $finalMaskPostProcessorText "RemoveSceneCutCarryRemnants[\s\S]*YoloSceneCutCarryCleanupOptions[\s\S]*TryParseFramePair[\s\S]*IsSceneCutCarryMatch[\s\S]*YoloSceneCutCarryCleanupResult"
Assert-Match "final mask postprocessor reports protected strong scene carry candidates" $finalMaskPostProcessorText "protectedFaceKeys[\s\S]*GetCarryReviewKey[\s\S]*ProtectedStrongCarryLikeFaces[\s\S]*ProtectedStrongCarryLikeFrameIndices"
Assert-Match "final mask postprocessor removes unsupported strong scene carry candidates" $finalMaskPostProcessorText "IsUnsupportedStrongCarryLikeFace[\s\S]*StrongCarrySupportLookaheadFrames[\s\S]*StrongCarrySupportMinFrames[\s\S]*StrongCarrySupportMinConfidence[\s\S]*RemovedUnsupportedStrongCarryLikeFaces[\s\S]*RemovedUnsupportedStrongCarryLikeFrameIndices"
Assert-Match "final mask postprocessor requires area and center change for strong carry protection" $finalMaskPostProcessorText "hasIndependentCenterShift[\s\S]*hasIndependentAreaChange[\s\S]*StrongCarryProtectionRequiresAreaChange[\s\S]*StrongCarryProtectionRequiresCenterShift[\s\S]*return hasIndependentAreaChange && hasIndependentCenterShift"
Assert-Match "final mask postprocessor exposes extended weak carry options" $finalMaskPostProcessorText "ExtendedWeakCarryFrames\s*\{\s*get;\s*init;\s*\}\s*=\s*8[\s\S]*ExtendedWeakMaxConfidence\s*\{\s*get;\s*init;\s*\}\s*=\s*0\.78f"
Assert-Match "final mask postprocessor applies extended weak carry cleanup only after purge window" $finalMaskPostProcessorText "purgeLastTargetFrame[\s\S]*weakLastTargetFrame[\s\S]*frameIndex\s*<=\s*purgeLastTargetFrame[\s\S]*options\.ExtendedWeakMaxConfidence"
Assert-Match "final mask scene carry cleanup uses extended source lookback" $finalMaskPostProcessorText "GetSceneCutCarryReferences[\s\S]*cutStartFrame\s*-\s*lookbackFrames[\s\S]*SourceLookbackFrames\s*\{\s*get;\s*init;\s*\}\s*=\s*5"
Assert-Match "final mask cleanup verifies scene-cut carry cleanup behavior" $finalMaskCleanupVerifyText "RemoveSceneCutCarryRemnants[\s\S]*unsupported strong carry frame 1006[\s\S]*Expected scene-cut carry cleanup to remove unsupported strong carry-like frames 1006 and 2009[\s\S]*Expected scene-cut carry cleanup not to recreate removed carry masks[\s\S]*Expected unsupported high-confidence post-cut carry frames 3002 and 3006 to be removed when there is not enough continuation support"
Assert-Match "final mask cleanup verifies same-size drifting carry removal and scale-changed support protection" $finalMaskCleanupVerifyText "driftingStrongCarryCleanup\.RemovedFaces\s*!=\s*5[\s\S]*areaChangedStrongCarryCleanup\.ProtectedStrongCarryLikeFaces\s*<\s*2[\s\S]*sameCenterAreaChangedStrongCarryCleanup\.RemovedFaces\s*!=\s*5[\s\S]*driftingStrongCarryRemoved=\{driftingStrongCarryCleanup\.RemovedFaces\}[\s\S]*areaChangedStrongCarryProtected=\{areaChangedStrongCarryCleanup\.ProtectedStrongCarryLikeFaces\}[\s\S]*sameCenterAreaChangedStrongCarryRemoved=\{sameCenterAreaChangedStrongCarryCleanup\.RemovedFaces\}"
Assert-Match "final mask cleanup removes weak isolated non-edge masks" $finalMaskPostProcessorText "RemoveWeakIsolatedMasks[\s\S]*NeighborWindowFrames[\s\S]*WeakConfidenceMax[\s\S]*TouchesFrameEdge[\s\S]*RemoveFaceMask"
Assert-Match "final mask cleanup uses per-face temporal neighbors" $finalMaskPostProcessorText "HasMatchingTemporalNeighbor[\s\S]*HasMatchingFace[\s\S]*NeighborMinIou[\s\S]*NeighborMaxCenterShiftRatio[\s\S]*NeighborMaxAreaChangeRatio"
Assert-Match "final mask cleanup does not keep unrelated weak boxes by frame adjacency only" $finalMaskPostProcessorText "confidence\s*<=\s*options\.WeakConfidenceMax[\s\S]*!TouchesFrameEdge[\s\S]*hasMatchingNeighbor\s*=\s*HasMatchingTemporalNeighbor[\s\S]*removeUnsupported\s*=\s*!hasMatchingNeighbor"
Assert-Match "final mask cleanup removes weak short temporal clusters without strong continuation" $finalMaskPostProcessorText "IsWeakShortTemporalCluster[\s\S]*WeakClusterMaxFrames[\s\S]*WeakClusterMaxConfidence[\s\S]*HasStrongAdjacentContinuation"
Assert-Match "final mask cleanup removes three-frame weak short clusters conservatively" $finalMaskPostProcessorText "WeakClusterMaxFrames\s*\{\s*get;\s*init;\s*\}\s*=\s*3[\s\S]*WeakClusterMaxConfidence\s*\{\s*get;\s*init;\s*\}\s*=\s*0\.48f"
Assert-Match "final mask cleanup verifies three-frame weak short cluster policy" $finalMaskCleanupVerifyText "Expected three-frame weak non-edge cluster frame \{frame\} to be removed[\s\S]*Expected weak three-frame cluster with strong continuation to remain"
Assert-Match "final mask cleanup removes weak tiny temporal clusters only without strong continuation" $finalMaskPostProcessorText "IsWeakTinyTemporalCluster[\s\S]*TinyClusterMaxAreaRatio[\s\S]*HasStrongAdjacentContinuation[\s\S]*TinyClusterMaxFrames"
Assert-Match "final mask cleanup removes medium-confidence tiny isolated non-edge masks" $finalMaskPostProcessorText "IsTinyFace\([\s\S]*options\.TinyIsolatedMaxAreaRatio[\s\S]*!hasMatchingNeighbor[\s\S]*confidence\s*<=\s*options\.TinyIsolatedMaxConfidence"
Assert-Match "final mask cleanup removes medium-confidence tiny short clusters" $finalMaskPostProcessorText "IsTinyShortTemporalCluster[\s\S]*TinyShortClusterMaxFrames[\s\S]*TinyShortClusterMaxConfidence[\s\S]*TinyShortClusterMaxAreaRatio"
Assert-Match "final mask cleanup removes weak aspect outlier clusters" $finalMaskPostProcessorText "IsAspectOutlierTemporalCluster[\s\S]*AspectOutlierClusterMaxFrames[\s\S]*AspectOutlierClusterMaxConfidence[\s\S]*AspectOutlierMinRatio[\s\S]*AspectOutlierMaxRatio"
Assert-Match "final mask cleanup removes upper weak non-edge clusters" $finalMaskPostProcessorText "IsUpperWeakTemporalCluster[\s\S]*UpperWeakClusterMaxFrames[\s\S]*UpperWeakClusterMaxConfidence[\s\S]*UpperWeakStrongContinuationMinConfidence[\s\S]*UpperWeakClusterMaxCenterYRatio[\s\S]*UpperWeakClusterMaxAreaRatio"
Assert-Match "final mask cleanup removes top-edge weak clusters conservatively" $finalMaskPostProcessorText "IsTopEdgeWeakTemporalCluster[\s\S]*TopEdgeWeakClusterMaxFrames[\s\S]*TopEdgeWeakClusterMaxConfidence[\s\S]*TopEdgeWeakStrongContinuationMinConfidence[\s\S]*visited\.Count\s*==\s*1[\s\S]*IsTopEdgeWeakIsolatedFace[\s\S]*return\s+visited\.Count\s*<=\s*options\.TopEdgeWeakClusterMaxFrames[\s\S]*IsTopEdgeWeakFace[\s\S]*TouchesFrameTopEdge"
Assert-Match "final mask cleanup removes top-edge large duplicates only with smaller support" $finalMaskPostProcessorText "IsTopEdgeLargeDuplicateFace[\s\S]*TopEdgeLargeDuplicateMaxConfidence[\s\S]*IsTopEdgeLargeFace[\s\S]*TopEdgeLargeDuplicateMinSupportConfidence[\s\S]*IsTopEdgeLargeDuplicateSupportFace[\s\S]*TopEdgeLargeDuplicateMinSupportContainment"
Assert-Match "final mask cleanup removes lower weak non-edge clusters" $finalMaskPostProcessorText "IsLowerWeakTemporalCluster[\s\S]*LowerWeakClusterMaxFrames[\s\S]*LowerWeakClusterMaxConfidence[\s\S]*LowerWeakClusterMinCenterYRatio[\s\S]*LowerWeakClusterMinAreaRatio[\s\S]*LowerWeakClusterMaxAreaRatio"
Assert-Match "final mask cleanup fills stable strong gaps" $finalMaskPostProcessorText "FillShortStableGaps[\s\S]*MaxGapFrames[\s\S]*MinAnchorConfidence[\s\S]*TryFindStableGapMatch[\s\S]*SetFaceRects[\s\S]*FilledFacesInfo[\s\S]*CutGuardFacesInfo"
Assert-Match "final mask gap fill blocks known scene cuts" $finalMaskPostProcessorText "blockedCutGapFaces[\s\S]*blockedCutFrames[\s\S]*TryGetCrossedCutRange[\s\S]*BlockedCutFramePairs[\s\S]*TryParseFramePair[\s\S]*BlockedCutGapFaces[\s\S]*BlockedCutFrameIndices"
Assert-Match "final mask gap fill blocks whole cleanup-contaminated gaps" $finalMaskPostProcessorText "blockedFrames\s*=\s*new\s+HashSet<int>\(options\.BlockedFrameIndices\)[\s\S]*allGapFrameIndices\s*=\s*GetGapFrameIndices\(previousFrame,\s*nextFrame\)[\s\S]*fillableGapFrameIndices\s*=\s*allGapFrameIndices[\s\S]*hasCleanupBlockedFrame\s*=\s*allGapFrameIndices\.Any\(blockedFrames\.Contains\)[\s\S]*blockedCleanupFrames\.Add\(frameIndex\)[\s\S]*interpolatedGapFaces[\s\S]*foreach\s*\(var interpolatedGapFace in interpolatedGapFaces\)[\s\S]*BlockedCleanupGapFrames[\s\S]*BlockedCleanupFrameIndices"
Assert-Match "final mask gap fill blocks scene-carry-contaminated frames and anchors" $finalMaskPostProcessorText "blockedSceneCarryFrames\s*=\s*new\s+HashSet<int>\(options\.BlockedSceneCarryFrameIndices\)[\s\S]*blockedSceneCarryFacesByFrame\s*=\s*BuildBlockedFacesByFrame\(options\.BlockedSceneCarryFaces\)[\s\S]*blockedSceneCarryFrames\.Contains\(previousFrame\)[\s\S]*blockedSceneCarryFrames\.Contains\(nextFrame\)[\s\S]*hasSceneCarryBlockedFrame\s*=\s*allGapFrameIndices\.Any\(blockedSceneCarryFrames\.Contains\)[\s\S]*HasMatchingBlockedFace\([\s\S]*blockedSceneCarryFacesByFrame[\s\S]*hasSceneCarryBlockedFace\s*=\s*true[\s\S]*blockedSceneCarryGapFrames\.Add\(frameIndex\)[\s\S]*BlockedSceneCarryGapFrames[\s\S]*BlockedSceneCarryFrameIndices"
Assert-Match "final mask postprocessor exposes scene-cut carry blocked windows" $finalMaskPostProcessorText "BuildSceneCutCarryBlockedFrames[\s\S]*referenceFrame\s*=\s*Math\.Min\(sourceFrame,\s*targetFrame\)[\s\S]*confirmedTargetFrame\s*=\s*Math\.Max\(sourceFrame,\s*targetFrame\)[\s\S]*lastTargetFrame\s*=\s*confirmedTargetFrame\s*\+\s*maxCarryFrames\s*-\s*1[\s\S]*frames\.Add\(frameIndex\)"
Assert-Match "final mask cleanup exposes supported weak gap options" $finalMaskPostProcessorText "SupportedAnchorMinConfidence[\s\S]*SupportedAnchorNeighborWindowFrames"
Assert-Match "final mask gap fill exposes long center-shift options" $finalMaskPostProcessorText "LongGapMinFrames[\s\S]*LongGapMaxCenterShiftRatio"
Assert-Match "final mask gap fill limits long center-shift gaps" $finalMaskPostProcessorText "gap\s*>=\s*options\.LongGapMinFrames[\s\S]*centerShift\s*>\s*options\.LongGapMaxCenterShiftRatio"
Assert-Match "final mask cleanup checks supported weak gap anchors" $finalMaskPostProcessorText "IsGapAnchorEligible[\s\S]*HasSupportedGapAnchorNeighbor[\s\S]*HasSupportedGapAnchorFace"
Assert-Match "final mask gap fill suppresses weak geometry-risk anchors" $finalMaskPostProcessorText "IsSuppressedWeakGeometryGapAnchor[\s\S]*WeakGeometryAnchorMaxConfidence[\s\S]*TouchesFrameEdge[\s\S]*IsTinyGapAnchorFace[\s\S]*IsUpperWeakGapAnchorFace[\s\S]*IsLowerWeakGapAnchorFace[\s\S]*IsAspectOutlierGapAnchorFace"
Assert-Match "final mask gap fill exposes medium risky anchor support threshold" $finalMaskPostProcessorText "RiskyGeometryAnchorSupportMaxConfidence\s*\{\s*get;\s*init;\s*\}\s*=\s*0\.72f"
Assert-Match "final mask gap fill requires support for medium risky geometry anchors" $finalMaskPostProcessorText "confidence\s*<=\s*options\.RiskyGeometryAnchorSupportMaxConfidence[\s\S]*IsRiskyGeometryGapAnchor[\s\S]*HasSupportedGapAnchorNeighbor"
Assert-Match "final mask gap fill reports suppressed anchor checks" $finalMaskPostProcessorText "SuppressedWeakGeometryAnchorChecks[\s\S]*SuppressedRiskyGeometryAnchorChecks[\s\S]*UnsupportedWeakAnchorChecks"
Assert-Match "final mask gap fill rejects weak edge anchors" $finalMaskCleanupVerifyText "weakEdgeAnchorGapFill[\s\S]*Expected weak edge anchors not to create final-mask gap fills"
Assert-Match "final mask gap fill keeps strong edge anchors eligible" $finalMaskCleanupVerifyText "strongEdgeAnchorGapFill[\s\S]*Expected strong edge anchors to remain eligible"
Assert-Match "final mask cleanup verifies medium risky geometry anchor policy" $finalMaskCleanupVerifyText "mediumRiskyAnchorGapFill[\s\S]*Expected medium-confidence risky geometry anchors without third support not to create final-mask gap fills[\s\S]*SuppressedRiskyGeometryAnchorChecks[\s\S]*supportedMediumRiskyAnchorGapFill[\s\S]*Expected medium-confidence risky geometry anchors with third support to remain eligible"
Assert-Match "final mask cleanup verifies whole-gap cleanup and scene-carry gap blocks" $finalMaskCleanupVerifyText "partialBlockedFrameGapFill[\s\S]*Expected a cleanup-blocked frame inside a stable gap to block the whole gap[\s\S]*partialPostCutWindowGapFill[\s\S]*Expected a scene-carry blocked frame inside a stable gap to suppress the whole gap[\s\S]*partialSceneCarryFaceBlockedGapFill[\s\S]*Expected a matching scene-carry blocked face inside a stable gap to suppress the whole gap"
Assert-Match "final mask gap fill seeds mixed frames" $finalMaskPostProcessorText "CreateFillEntry[\s\S]*TryGetFaceMaskData\(frameIndex[\s\S]*new\s+List<Rect>\(existing\.Faces\)"
Assert-Match "final mask gap fill dedupes against existing frame faces" $finalMaskPostProcessorText "HasMatchingFace\(fill\.Faces,\s*interpolated"
Assert-Match "workspace yolo scene cut guard is gated to yolo" $workspaceText "FaceFilterProfile\.Yolo[\s\S]*RemoveYoloTrackFillAcrossSceneCuts"
Assert-Match "workspace yolo scene cut guard uses shared service" $workspaceText "new\s+FaceTrackSceneCutGuard\(\)[\s\S]*BuildWeakTrackTransitionCandidates[\s\S]*BuildWeakPostCutCarryCandidates[\s\S]*trackPost\.FilledGapFacesInfo[\s\S]*trackPost\.FilledLostFacesInfo[\s\S]*trackPost\.FilledInitialFacesInfo[\s\S]*guard\.Apply"
Assert-Match "workspace yolo scene cut guard logs pre and post smooth stages" $workspaceText "RemoveYoloTrackFillAcrossSceneCuts\([\s\S]*trackPost[\s\S]*""pre-smooth""[\s\S]*RemoveYoloTrackFillAcrossSceneCuts\([\s\S]*trackPost[\s\S]*""post-smooth""[\s\S]*stage=\{stage\}"
Assert-Match "scene cut guard builds weak direct candidates" $sceneCutGuardText "BuildWeakTrackTransitionCandidates[\s\S]*FaceTrackBuilder\(\)\.Build[\s\S]*maxTargetConfidence[\s\S]*minSourceConfidence[\s\S]*FaceTrackFilledFace"
Assert-Match "scene cut guard builds weak post-cut carry candidates" $sceneCutGuardText "BuildWeakPostCutCarryCandidates[\s\S]*maxCarryFrames[\s\S]*sourceLookbackFrames[\s\S]*includeEdgeCandidates[\s\S]*BuildWeakCarryRun[\s\S]*lookbackFrames[\s\S]*sourceFrame[\s\S]*TryFindMatchingSourceFace[\s\S]*HasIndependentStrongContinuation[\s\S]*maxTargetConfidence[\s\S]*FaceTrackFilledFace"
Assert-Match "scene cut guard protects only independent strong continuation" $sceneCutGuardText "StrongContinuationIndependentCenterShiftRatio\s*=\s*0\.22[\s\S]*StrongContinuationIndependentAreaChangeRatio\s*=\s*1\.65[\s\S]*HasIndependentStrongContinuation[\s\S]*GetNormalizedCenterShift\(sourceFace[\s\S]*GetAreaRatio\(sourceFace"
Assert-Match "scene cut guard checks weak carry tails" $sceneCutGuardText "maxPostCutCarryFrames[\s\S]*carryEndFrame[\s\S]*lastAddedIndex[\s\S]*FaceTrackFilledFace"
Assert-Match "scene cut guard caches frame differences" $sceneCutGuardText "differenceByPair[\s\S]*TryGetValue\(pair,[\s\S]*frameDifferenceProvider\(pair\.Source,\s*pair\.Target\)[\s\S]*differenceByPair\[pair\]"
Assert-Match "scene cut guard skips exact duplicate candidates" $sceneCutGuardText "DeduplicateCandidates[\s\S]*GetCandidateKey[\s\S]*SourceFrameIndex[\s\S]*FrameIndex[\s\S]*Math\.Round\(candidate\.Bounds"
Assert-Match "scene cut guard removes track fill" $sceneCutGuardText "DefaultDifferenceThreshold\s*=\s*0\.32[\s\S]*DefaultDirectDifferenceThreshold\s*=\s*0\.36[\s\S]*TryGetNextFrameRawToBuffer[\s\S]*RemoveFaceCandidate"
Assert-Match "scene cut guard can remove weak matching post-cut tail" $sceneCutGuardText "RemoveWeakMatchingTail[\s\S]*maxTailFrames[\s\S]*maxTailConfidence[\s\S]*TryRemoveWeakMatchingFace"
Assert-Match "scene cut guard exposes deterministic verifier path" $sceneCutGuardText "Func<int,\s*int,\s*double>\s+frameDifferenceProvider[\s\S]*GetMaxFrameDifference[\s\S]*RemoveFaceCandidate"
Assert-Match "track postprocess exposes synthetic fill confidence cap" $trackBuilderText "SyntheticFillConfidenceMax\s*\{\s*get;\s*init;\s*\}\s*=\s*1\.0f"
Assert-Match "track postprocess exposes inward initial-fill gate" $trackBuilderText "InitialFillRequiresInwardMotion\s*\{\s*get;\s*init;\s*\}\s*=\s*false"
Assert-Match "track interpolator gates initial fill by inward edge motion" $trackInterpolatorText "InitialFillRequiresInwardMotion[\s\S]*IsMovingInwardFromTouchedEdge[\s\S]*return\s+touchedEdge\s*&&\s*inward"
Assert-Match "track interpolator protects confirmed small tracks" $trackInterpolatorText "CouldBePartialFace[\s\S]*IsConfirmedSmallTrack[\s\S]*ConfirmedTrackMinDetections[\s\S]*StrongConfidence[\s\S]*IsSmallTrack"
Assert-Match "track postprocess reports blocked initial fill" $trackInterpolatorText "blockedInitialFillTracks\+\+[\s\S]*BlockedInitialFillTracks"
Assert-Match "track interpolation caps synthetic fill confidence" $trackInterpolatorText "ClampSyntheticFillConfidence[\s\S]*SyntheticFillConfidenceMax[\s\S]*Math\.Clamp\(sourceConfidence"
Assert-Match "workspace caps yolo synthetic track fill confidence" $workspaceText "SyntheticFillConfidenceMax\s*=\s*(YoloSceneCutPostCutCarryMaxConfidence|YoloSyntheticFillConfidenceMax)"
Assert-Match "workspace logs blocked initial fill" $workspaceText "blockedInitialFill=\{result\.BlockedInitialFillTracks\}"
Assert-Match "scene cut guard scans adjacent frame differences" $sceneCutGuardText "for\s*\(int\s+frame\s*=\s*sourceFrame;\s*frame\s*<\s*targetFrame;\s*frame\+\+\)[\s\S]*FormatFramePair\(maxSource,\s*maxTarget\)"
Assert-Match "scene cut guard checks cumulative frame difference" $sceneCutGuardText "directDifferenceThreshold[\s\S]*GetFrameDifference\(\s*sourceFrame,\s*targetFrame[\s\S]*TryGetFramePairDifference[\s\S]*FormatFramePair\(sourceFrame,\s*targetFrame\)"
Assert-Match "scene cut guard reports direct difference budget" $sceneCutGuardText "directDifferenceMaxChecks[\s\S]*directDifferenceBudget[\s\S]*directDifferenceChecks[\s\S]*directDifferenceSkipped[\s\S]*DirectDifferenceChecks[\s\S]*DirectDifferenceSkipped"
Assert-Match "scene cut guard saves direct budget when adjacent cut is enough" $sceneCutGuardText "bool\s+isCut\s*=\s*difference\s*>=\s*differenceThreshold[\s\S]*if\s*\(!isCut\s*&&[\s\S]*directDifferenceChecks\s*<\s*directDifferenceBudget"
Assert-Match "scene cut guard reuses cached direct pair without spending budget" $sceneCutGuardText "differenceByPair\.TryGetValue\(directPair,[\s\S]*directDifferenceChecks\s*<\s*directDifferenceBudget[\s\S]*if\s*\(!differenceByPair\.ContainsKey\(directPair\)\)[\s\S]*directDifferenceChecks\+\+"
Assert-Match "scene cut guard caches direct endpoint samples during sequential read" $sceneCutGuardText "BuildDirectDifferenceFrameSet[\s\S]*PrecomputeFrameDifferences\([\s\S]*directSampleFrames[\s\S]*CaptureFrameSample[\s\S]*frameSamples\.TryGetValue\(sourceFrame[\s\S]*frameSamples\.TryGetValue\(targetFrame[\s\S]*ComputeFrameDifference"
Assert-Match "scene cut guard exposes frame evidence" $sceneCutGuardText "RemovedFrameIndices[\s\S]*CheckedFramePairs[\s\S]*MaxDifference[\s\S]*CutFramePairs"
Assert-Match "workspace logs scene cut frame evidence" $workspaceText "directChecked=\{result\.DirectDifferenceChecks\}[\s\S]*directSkipped=\{result\.DirectDifferenceSkipped\}[\s\S]*checkedPairs=\{FormatTextList\(result\.CheckedFramePairs\)\}[\s\S]*maxDiff=\{result\.MaxDifference[\s\S]*cutPairs=\{FormatTextList\(result\.CutFramePairs\)\}[\s\S]*removedFrames=\{FormatFrameList\(result\.RemovedFrameIndices\)\}"
Assert-Match "workspace faceonnx track profile remains default branch" $workspaceText "return\s+new\s+FaceTrackPostProcessOptions[\s\S]*WeakConfidence\s*=\s*TemporalConfidenceWeak[\s\S]*StrongConfidence\s*=\s*TemporalConfidenceStrong"
Assert-Match "auto generator runs postprocess pipeline" $autoMaskGeneratorText "new\s+AutoMaskPostProcessPipeline\([\s\S]*postProcess\.Apply\("
Assert-Match "workspace refreshes preview after completed postprocess" $workspaceText "CompleteAutoExportGate\(runOptions,\s*generator\.LastRunSummary\);\s*RefreshAutoPreviewAfterPostProcess\(exportAfter\)"
Assert-Match "workspace roi refine includes initial fill candidates" $workspaceText "AutoMaskRoiRefineStep[\s\S]*trackPost\.FilledGapFacesInfo[\s\S]*trackPost\.FilledLostFacesInfo[\s\S]*trackPost\.FilledInitialFacesInfo[\s\S]*new\s+FaceTrackRoiRefiner\(\)\.Apply"
Assert-Match "workspace applies yolo scene cut before and after temporal smoothing" $autoMaskPostProcessPipelineText "preSmoothGuard\s*=\s*yoloSceneCutPostProcessor\.RemoveTrackFillAcrossSceneCuts\([\s\S]*?""pre-smooth""[\s\S]*?yoloPreSmoothCutPairs\s*=\s*NormalizeCutFramePairs\(preSmoothGuard\.CutFramePairs\)[\s\S]*?ApplyTemporalSmoothing\([\s\S]*?CombineCutFramePairs\(yoloPreSmoothCutPairs,\s*yoloPreSmoothStrongCarryProbeCutPairs\)[\s\S]*?postSmoothGuard\s*=\s*yoloSceneCutPostProcessor\.RemoveTrackFillAcrossSceneCuts\([\s\S]*?""post-smooth""[\s\S]*?yoloPostSmoothCutPairs\s*=\s*NormalizeCutFramePairs\(postSmoothGuard\.CutFramePairs\)[\s\S]*?yoloCutPairs\s*=\s*CombineCutFramePairs\([\s\S]*?LogFinalMaskSummary"
Assert-Match "default verifier runs smoothing cut boundary harness" $autoMosaicDefaultVerifyText "verify-yolo-temporal-smoothing-cut-boundary\.ps1[\s\S]*Invoke-ScriptStep\s+""yolo-temporal-smoothing-cut-boundary""[\s\S]*prevBlocked=True[\s\S]*nextBlocked=True"
Assert-Match "workspace temporal smoothing does not materialize empty frames" $workspaceText "if\s*\(hasStored\[i\]\s*\|\|\s*facesByFrame\[i\]\s*==\s*null\)\s*continue;[\s\S]*if\s*\(hasStored\[i\]\s*\|\|\s*facesByFrame\[i\]\s*==\s*null\s*\|\|\s*facesByFrame\[i\]!\.Count\s*==\s*0[\s\S]*\)\s*continue;"
Assert-Match "roi refiner only replaces existing matching faces" $roiRefinerText "TryGetFaceMaskData\(candidate\.FrameIndex,[\s\S]*FindSimilarFaceIndex\(faces,\s*candidate\.Bounds\)[\s\S]*if\s*\(replaceIndex\s*<\s*0\)\s*return\s+false;[\s\S]*faces\[replaceIndex\]\s*=\s*refined\.Bounds"

Assert-Match "automask yolo filter profile supports precision defaults" $autoMaskGeneratorText "if\s*\(profile\s*==\s*FaceFilterProfile\.Yolo\)[\s\S]*OffModeYoloMinSmallFaceAreaRatio[\s\S]*MinSmallFaceAreaRatio\s*\*\s*0\.70[\s\S]*OffModeYoloSmallFaceConfidenceMin[\s\S]*0\.30f[\s\S]*OffModeYoloMaxFaceAspectRatio[\s\S]*2\.7[\s\S]*useStatsFilter\s*=\s*usePrecisionMode[\s\S]*UseStatsFilter:\s*useStatsFilter"
Assert-Match "automask reserves precision filtering for legacy compatibility" $autoMaskGeneratorText "usePrecisionMode\s*=\s*options\.ProcessingMode\s*==\s*AutoMaskProcessingMode\.Legacy\s*&&[\s\S]*!options\.EnablePostProcessing[\s\S]*GetFaceFilterSettings\([\s\S]*usePrecisionMode\)"
Assert-Match "automask resolves one filter policy for every runtime path" $autoMaskGeneratorText "_faceFilterSettings\s*=\s*GetRuntimeFaceFilterSettings\(_options\)[\s\S]*FilterFacesByAreaAndStats\([\s\S]*_faceFilterSettings"
Assert-Match "automask tracked faceonnx disables frame stats" $autoMaskGeneratorText "disableFrameStats\s*=\s*options\.FilterProfile\s*==\s*FaceFilterProfile\.FaceOnnx\s*&&[\s\S]*options\.ProcessingMode\s*==\s*AutoMaskProcessingMode\.Tracked\s*&&[\s\S]*!options\.EnablePostProcessing[\s\S]*settings\s+with\s*\{\s*UseStatsFilter\s*=\s*false\s*\}"
Assert-Match "automask base faceonnx policy retains stats" $autoMaskGeneratorText "return\s+new\s+FaceFilterSettings\([\s\S]*MaxFaceAspectRatio,[\s\S]*SmallFaceConfidenceMin,[\s\S]*UseStatsFilter:\s*true\s*\);"
Assert-Match "automask yolo detector summary includes provider" $autoMaskGeneratorText "_detector\s+is\s+YoloFaceOnnxDetector[\s\S]*YoloFaceOnnxDetector\.GetLastExecutionProviderLabel\(\)[\s\S]*YoloFaceOnnxDetector\.GetLastExecutionProviderError\(\)"
Assert-Match "automask sparse scene cut guard covers tracked profiles" $autoMaskGeneratorText "collectSceneCutBoundaries\s*=\s*_options\.UseTracking\s*&&\s*_options\.ProcessingMode\s*!=\s*AutoMaskProcessingMode\.Raw\s*&&\s*\(_options\.FilterProfile\s*==\s*FaceFilterProfile\.Yolo\s*\|\|\s*_options\.ProcessingMode\s*!=\s*AutoMaskProcessingMode\.Legacy\)[\s\S]*IsSparseSceneCut"
Assert-Match "automask sparse scene cut guard blocks materialization" $autoMaskGeneratorText "stopAtSceneCut[\s\S]*sceneCutStops\+\+[\s\S]*\?\s*key\s*\+\s*1"
Assert-Match "automask sparse scene cut summary logs count" $autoMaskGeneratorText "sparseSceneCuts=\{materialized\.SceneCutStops\}"
Assert-Match "automask sparse scene cut summary logs pairs" $autoMaskGeneratorText "sparseSceneCutPairs=\{FormatSparseSceneCutTransitions\(materialized\.SceneCutTransitions\)\}"
Assert-Match "automask sparse scene cut decision is isolated" $autoMaskGeneratorText "ShouldStopSparseSceneCarry[\s\S]*guardSceneCuts[\s\S]*ComputeSignatureDifference"
Assert-Match "automask sparse fallback carry caps at detect interval" $autoMaskGeneratorText "canBridge[\s\S]*nextPositive!\.Index[\s\S]*Math\.Min\(nextKey,\s*key\s*\+\s*Math\.Max\(1,\s*_options\.DetectEveryNFrames\)\)"
Assert-Match "automask sparse materialize result exposes scene cut count" $autoMaskGeneratorText "SparseMaterializeResult\([\s\S]*int\s+Interpolated,[\s\S]*int\s+SceneCutStops"
Assert-Match "automask sparse materialize result exposes scene cut transitions" $autoMaskGeneratorText "SparseSceneCutTransition\(int\s+SourceFrameIndex,\s*int\s+NextFrameIndex\)[\s\S]*SceneCutTransitions"

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
    "SmallAreaMaxAreaRatio",
    "UseAspectRatioFilter",
    "MinAspectRatio",
    "MaxAspectRatio",
    "UseTopSmallLowConfidenceFilter",
    "TopSmallLowConfidenceMaxCenterYRatio",
    "TopSmallLowConfidenceMaxAreaRatio",
    "TopSmallLowConfidenceMaxConfidence")) {
    Assert-Match "yolo options expose $property" $options "public\s+.*\s+$property\s*\{"
}

foreach ($method in @(
    "CreateSessionOptions",
    "ResolveInputDimension",
    "GetLastExecutionProviderLabel",
    "GetLastExecutionProviderError",
    "RunTiles",
    "ApplyLowConfidencePositionFilter",
    "ApplySmallAreaFilter",
    "ApplyAspectRatioFilter",
    "IsAspectRatioAllowed",
    "ApplyTopSmallLowConfidenceFilter",
    "IsTopSmallLowConfidenceCandidate",
    "RefineLargeBoxes",
    "TryRefineFromLandmarks")) {
    Assert-Match "yolo detector uses $method" $yoloDetectorText ([regex]::Escape($method))
}

Assert-Match "yolo fixed input size prefers model metadata" $yoloDetectorText "ResolveInputDimension\(dims,\s*2,\s*640,\s*_options\.InputHeight\)[\s\S]*ResolveInputDimension\(dims,\s*3,\s*640,\s*_options\.InputWidth\)[\s\S]*dims\[index\]\s*>\s*0[\s\S]*return\s+dims\[index\]"
Assert-Match "yolo directml provider is loaded like faceonnx" $yoloDetectorText "TryAppendExecutionProvider\(options,\s*""AppendExecutionProvider_DML"",\s*""Microsoft\.ML\.OnnxRuntime\.DirectML""\)"
Assert-Match "yolo gpu failure falls back to cpu session" $yoloDetectorText "catch\s*\(Exception\s+ex\)\s*when\s*\(shouldTryGpu\)[\s\S]*fallbackOptions\s*=\s*CreateSessionOptions\(\)[\s\S]*new\s+InferenceSession\(_options\.ModelPath,\s*fallbackOptions\)"
Assert-Match "yolo directml diagnostics recorded" $yoloDetectorText "BuildDirectMlDiagnostics[\s\S]*onnxruntime_providers_shared\.dll"

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
    "Yolo5LandmarkBoxMinOriginalIou",
    "UseAspectRatioFilter",
    "MinAspectRatio",
    "MaxAspectRatio",
    "UseTopSmallLowConfidenceFilter",
    "TopSmallLowConfidenceMaxCenterYRatio",
    "TopSmallLowConfidenceMaxAreaRatio",
    "TopSmallLowConfidenceMaxConfidence")) {
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
    "YoloUseAspectRatioFilter",
    "YoloMinAspectRatio",
    "YoloMaxAspectRatio",
    "YoloMaxLostFillFrames",
    "YoloMaxInitialFillFrames",
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
    "UseAspectRatioFilter\s*=\s*yoloUseAspectRatioFilter",
    "MinAspectRatio\s*=\s*yoloMinAspectRatio",
    "MaxAspectRatio\s*=\s*yoloMaxAspectRatio",
    "MaxLostFillFrames\s*=\s*yoloMaxLostFillFrames",
    "MaxInitialFillFrames\s*=\s*yoloMaxInitialFillFrames",
    "InitialFillRequiresInwardMotion\s*=\s*true",
    "DropShortTrackMaxDetections\s*=\s*yoloDropShortTrackMaxDetections",
    "DropShortSmallTrackMaxDetections\s*=\s*3",
    "DropSparseTrackMaxDetections\s*=\s*3",
    "DropSparseTrackMinSpanFrames\s*=\s*8",
    "DropSparseTrackMaxDensity\s*=\s*0\.42",
    "EdgeTailMaxConfidence\s*=\s*0\.50f",
    "EdgeLostFillMaxConfidence\s*=\s*0\.60f",
    "SyntheticFillConfidenceMax\s*=\s*yoloSceneCutPostCutCarryMaxConfidence",
    "MaxConfirmedTrackHoldFrames\s*=\s*5",
    "MaxConfirmedTrackBridgeCenterShiftRatio\s*=\s*1\.20",
    "AllowSmallTrackLostFill\s*=\s*true",
    "ShortTrackMaxConfidence\s*=\s*yoloShortTrackMaxConfidence",
    "SparseTrackMaxConfidence\s*=\s*0\.56f",
    "LowerFrameTrackMaxConfidence\s*=\s*yoloLowerFrameTrackMaxConfidence")) {
    Assert-Match "smoke harness maps $assignment" $smokeHarnessText $assignment
}

Assert-Match "home enables yolo aspect ratio filter" $homeText "UseAspectRatioFilter\s*=\s*true[\s\S]*MinAspectRatio\s*=\s*0\.35[\s\S]*MaxAspectRatio\s*=\s*1\.65"
Assert-Match "yolo options leave top small low-confidence filter opt-in by default" $options "public\s+bool\s+UseTopSmallLowConfidenceFilter\s*\{\s*get;\s*init;\s*\}\s*=\s*false;"

Assert-Match "smoke harness supports yolo roi refine switch" $smokeHarnessText "if\s*\(useYolo\s*&&\s*yoloUseFaceOnnxRoiRefine\)"
Assert-Match "smoke harness logs scene cut guard" $smokeHarnessText "new\s+FaceTrackSceneCutGuard\(\)[\s\S]*BuildWeakTrackTransitionCandidates[\s\S]*BuildWeakPostCutCarryCandidates[\s\S]*var\s+sceneCutCandidates\s*=\s*directCandidates[\s\S]*\.Concat\(postCutCandidates\)[\s\S]*\.Concat\(trackPost\.FilledGapFacesInfo\)[\s\S]*trackPost\.FilledLostFacesInfo[\s\S]*trackPost\.FilledInitialFacesInfo[\s\S]*SmokeFaceTrackSceneCutGuard[\s\S]*postCutCandidates"
Assert-Match "smoke harness matches yolo scene cut threshold" $smokeHarnessText "yoloSceneCutDirectCarryMaxConfidence\s*=\s*0\.98f[\s\S]*yoloSceneCutDirectCarryMinSourceConfidence\s*=\s*0\.58f[\s\S]*yoloSceneCutPostCutCarryMaxConfidence\s*=\s*0\.78f[\s\S]*yoloSceneCutDifferenceThreshold\s*=\s*0\.15[\s\S]*yoloSceneCutDirectDifferenceThreshold\s*=\s*0\.20[\s\S]*yoloSceneCutDirectDifferenceMaxCandidates\s*=\s*240[\s\S]*yoloSceneCutCarryPurgeFrames\s*=\s*15[\s\S]*yoloSceneCutCarryBlockFrames\s*=\s*15[\s\S]*yoloSceneCutCarryProbeFrames\s*=\s*8[\s\S]*yoloSceneCutMatchingTailMaxFrames\s*=\s*8[\s\S]*yoloSceneCutMatchingTailMaxConfidence\s*=\s*0\.98f[\s\S]*yoloSceneCutCandidateMatchMinIou\s*=\s*0\.55[\s\S]*yoloSceneCutCandidateMatchMaxCenterShiftRatio\s*=\s*0\.80[\s\S]*yoloSceneCutCandidateMatchMaxAreaChangeRatio\s*=\s*3\.5[\s\S]*yoloSceneCutPostCutLookbackFrames\s*=\s*5[\s\S]*minConfidenceDrop:\s*0\.0f[\s\S]*maxPostCutCarryFrames:\s*yoloSceneCutCarryProbeFrames[\s\S]*minSourceConfidence:\s*yoloSceneCutDirectCarryMinSourceConfidence[\s\S]*maxCarryFrames:\s*yoloSceneCutCarryProbeFrames[\s\S]*sourceLookbackFrames:\s*yoloSceneCutPostCutLookbackFrames[\s\S]*differenceThreshold:\s*yoloSceneCutDifferenceThreshold[\s\S]*directDifferenceThreshold:\s*yoloSceneCutDirectDifferenceThreshold[\s\S]*directDifferenceMaxChecks:\s*yoloSceneCutDirectDifferenceMaxCandidates[\s\S]*removeMatchingTailFrames:\s*yoloSceneCutMatchingTailMaxFrames[\s\S]*removeMatchingTailMaxConfidence:\s*yoloSceneCutMatchingTailMaxConfidence[\s\S]*SourceLookbackFrames\s*=\s*yoloSceneCutPostCutLookbackFrames[\s\S]*candidateMatchMinIou:\s*yoloSceneCutCandidateMatchMinIou"
Assert-Match "smoke harness logs initial fill count" $smokeHarnessText "SmokeFaceTrackPost[\s\S]*initialFilled=\{trackPost\.FilledInitialFaces\}"
Assert-Match "smoke harness logs blocked initial fill count" $smokeHarnessText "SmokeFaceTrackPost[\s\S]*blockedInitialFill=\{trackPost\.BlockedInitialFillTracks\}"
Assert-Match "smoke harness logs unstable-tail temporal removals" $smokeHarnessText "removedUnstableTail=\{trackPost\.RemovedUnstableTailFaces\}"
Assert-Match "smoke harness logs direct scene cut candidates" $smokeHarnessText "directCandidates=\{directCandidates\.Count\}"
Assert-Match "smoke harness logs scene cut frame evidence" $smokeHarnessText "directChecked=\{sceneCut\.DirectDifferenceChecks\}[\s\S]*directSkipped=\{sceneCut\.DirectDifferenceSkipped\}[\s\S]*checkedPairs=\{FormatTextValues\(sceneCut\.CheckedFramePairs\)\}[\s\S]*maxDiff=\{sceneCut\.MaxDifference[\s\S]*cutPairs=\{FormatTextValues\(sceneCut\.CutFramePairs\)\}[\s\S]*removedFrames=\{FormatFrames\(sceneCut\.RemovedFrameIndices\)\}"
Assert-Match "smoke harness logs strong carry scene cut probe" $smokeHarnessText "strongCarryProbeCandidates[\s\S]*BuildWeakPostCutCarryCandidates\([\s\S]*maxTargetConfidence:\s*yoloSceneCutStrongCarryProbeMaxConfidence[\s\S]*minSourceConfidence:\s*yoloSceneCutStrongCarryProbeMinSourceConfidence[\s\S]*minTargetConfidence:\s*yoloSceneCutStrongCarryProbeMinConfidence[\s\S]*removeCandidates:\s*false[\s\S]*SmokeYoloStrongCarrySceneCutProbe"
Assert-Match "smoke harness strong carry probe includes independent continuation evidence" $smokeHarnessText "strongCarryProbeCandidates[\s\S]*BuildWeakPostCutCarryCandidates\([\s\S]*includeIndependentStrongContinuation:\s*true"
Assert-Match "smoke harness logs final yolo mask summary" $smokeHarnessText "if\s*\(useYolo\)[\s\S]*LogFinalMaskSummary\(label,\s*maskProvider,\s*protectedSceneCarryFrameIndices\)[\s\S]*SmokeFinalMaskSummary[\s\S]*shortGaps=\{shortGapCount\}[\s\S]*perFaceShortGaps=\{perFaceShortGapRanges\.Count\}[\s\S]*perFaceShortGapRanges=\{FormatTextValues\(perFaceShortGapRanges\)\}[\s\S]*largeJumpGaps=\{largeJumpGapRanges\.Count\}[\s\S]*largeJumpRanges=\{FormatTextValues\(largeJumpGapRanges\)\}[\s\S]*isolatedFrames=\{FormatFrames\(isolatedFrames\)\}[\s\S]*lowConfFrames=\{FormatFrames\(lowConfidenceFrames[\s\S]*weakNonEdge=\{weakNonEdgeRows\}[\s\S]*weakNonEdgeFrames=\{FormatFrames\(weakNonEdgeFrames[\s\S]*edgeWeak=\{edgeWeakRows\}[\s\S]*edgeWeakFrames=\{FormatFrames\(edgeWeakFrames[\s\S]*topEdgeWeak=\{topEdgeWeakRows\}[\s\S]*topEdgeWeakFrames=\{FormatFrames\(topEdgeWeakFrames[\s\S]*topEdgeLarge=\{topEdgeLargeRows\}[\s\S]*topEdgeLargeFrames=\{FormatFrames\(topEdgeLargeFrames[\s\S]*upperWeak=\{upperWeakRows\}[\s\S]*upperWeakFrames=\{FormatFrames\(upperWeakFrames[\s\S]*lowerWeak=\{lowerWeakRows\}[\s\S]*lowerWeakFrames=\{FormatFrames\(lowerWeakFrames[\s\S]*aspectBad=\{aspectBadRows\}[\s\S]*aspectBadFrames=\{FormatFrames\(aspectBadFrames[\s\S]*tinyWeak=\{tinyWeakRows\}[\s\S]*tinyWeakFrames=\{FormatFrames\(tinyWeakFrames[\s\S]*tinyShort=\{tinyShortRows\}[\s\S]*tinyShortFrames=\{FormatFrames\(tinyShortFrames[\s\S]*protectedSceneCarry=\{protectedSceneCarryFrames\.Length\}[\s\S]*protectedSceneCarryFrames=\{FormatFrames\(protectedSceneCarryFrames\)\}[\s\S]*reviewRequired=\{reviewReasons\.Count > 0\}[\s\S]*reviewReasons=\{FormatTextValues\(reviewReasons\)\}"
Assert-Match "smoke harness final mask summary builds review reasons" $smokeHarnessText "BuildFinalMaskReviewReasons\([\s\S]*short-gap[\s\S]*per-face-short-gap[\s\S]*large-jump-gap[\s\S]*isolated-mask[\s\S]*low-confidence-review[\s\S]*weak-non-edge[\s\S]*edge-weak-review[\s\S]*top-edge-weak-review[\s\S]*top-edge-large-review[\s\S]*upper-weak[\s\S]*lower-weak[\s\S]*aspect-outlier[\s\S]*tiny-weak[\s\S]*tiny-short[\s\S]*scene-carry-protected"
Assert-Match "smoke harness final summary detects per-face flicker gaps" $smokeHarnessText "FindPerFaceShortGapRanges\([\s\S]*IsSameFinalMaskFace"
Assert-Match "smoke harness final summary detects large jumps" $smokeHarnessText "largeJumpAreaChangeRatio\s*=\s*4\.0[\s\S]*largeJumpCenterShift\s*=\s*0\.20[\s\S]*TryGetBestFinalMaskFace[\s\S]*GetFinalMaskAreaChange[\s\S]*GetFinalMaskCenterShift"
Assert-Match "smoke harness removes weak isolated final yolo masks" $smokeHarnessText "YoloFinalMaskPostProcessor[\s\S]*RemoveWeakIsolatedMasks\(maskProvider\)[\s\S]*SmokeYoloFinalMaskCleanup"
Assert-Match "smoke harness logs yolo final cleanup reasons" $smokeHarnessText "removedWeakUnsupported=\{cleanup\.RemovedWeakUnsupportedFaces\}[\s\S]*removedMediumUnsupported=\{cleanup\.RemovedMediumUnsupportedFaces\}[\s\S]*removedWeakShortClusters=\{cleanup\.RemovedWeakShortClusterFaces\}[\s\S]*removedWeakTextureClusters=\{cleanup\.RemovedWeakTextureClusterFaces\}[\s\S]*removedWeakTinyClusters=\{cleanup\.RemovedWeakTinyClusterFaces\}[\s\S]*removedTinyShortClusters=\{cleanup\.RemovedTinyShortClusterFaces\}[\s\S]*removedTinyIsolated=\{cleanup\.RemovedTinyIsolatedFaces\}[\s\S]*removedTopEdgeWeakClusters=\{cleanup\.RemovedTopEdgeWeakClusterFaces\}[\s\S]*removedTopEdgeLargeDuplicates=\{cleanup\.RemovedTopEdgeLargeDuplicateFaces\}[\s\S]*removedUpperWeakClusters=\{cleanup\.RemovedUpperWeakClusterFaces\}[\s\S]*removedLowerWeakClusters=\{cleanup\.RemovedLowerWeakClusterFaces\}[\s\S]*removedAspectOutliers=\{cleanup\.RemovedAspectOutlierClusterFaces\}"
Assert-Match "smoke harness defers yolo final gap fill until scene-cut blockers exist" $smokeHarnessText "SmokeYoloFinalMaskGapFill[\s\S]*deferredUntilSceneCutGuard=True[\s\S]*SmokeFaceTrackSceneCutGuard[\s\S]*SmokeYoloSceneCutCarryCleanup[\s\S]*SmokeYoloFinalMaskPostSceneGapFill"
Assert-Match "smoke harness blocks final gap fill from cleanup removals and scene-cut carry windows" $smokeHarnessText "cleanupBlockedFaces\s*=\s*cleanup\.RemovedFacesInfo[\s\S]*sceneCutBlockedFrameIndices\s*=\s*YoloFinalMaskPostProcessor\.BuildSceneCutCarryBlockedFrames[\s\S]*postSceneBlockedFaces\s*=\s*cleanupBlockedFaces[\s\S]*Concat\(sceneCutCarryCleanup\.RemovedFacesInfo\)[\s\S]*Concat\(postSceneCleanup\.RemovedFacesInfo\)[\s\S]*BlockedFaces\s*=\s*postSceneBlockedFaces[\s\S]*BlockedSceneCarryFaces\s*=\s*sceneCutCarryCleanup\.RemovedFacesInfo[\s\S]*BlockedSceneCarryFrameIndices\s*=\s*sceneCutBlockedFrameIndices"
Assert-Match "smoke harness guards yolo post-scene final gap fill across scene cuts" $smokeHarnessText "FillShortStableGaps\([\s\S]*maskProvider[\s\S]*postSceneGapFill\.CutGuardFacesInfo[\s\S]*FaceTrackSceneCutGuard\(\)\.Apply[\s\S]*SmokeYoloFinalMaskPostSceneGapFillSceneCutGuard"
Assert-Match "smoke harness cleans weak remnants after scene guard" $smokeHarnessText "yoloSceneCutCarryPurgeFrames\s*=\s*15[\s\S]*yoloSceneCutCarryBlockFrames\s*=\s*15[\s\S]*yoloSceneCutExtendedWeakCarryMaxConfidence\s*=\s*0\.78f[\s\S]*SmokeFaceTrackSceneCutGuard[\s\S]*sceneCutPairs\s*=\s*sceneCut\.CutFramePairs[\s\S]*RemoveSceneCutCarryRemnants[\s\S]*sceneCutPairs[\s\S]*MaxCarryFrames\s*=\s*yoloSceneCutCarryPurgeFrames[\s\S]*ExtendedWeakCarryFrames\s*=\s*yoloSceneCutCarryBlockFrames[\s\S]*ExtendedWeakMaxConfidence\s*=\s*yoloSceneCutExtendedWeakCarryMaxConfidence[\s\S]*postProcessor\.RemoveWeakIsolatedMasks\(maskProvider\)[\s\S]*sceneCutBlockedFrameIndices\s*=\s*YoloFinalMaskPostProcessor\.BuildSceneCutCarryBlockedFrames[\s\S]*sceneCutPairs[\s\S]*yoloSceneCutCarryBlockFrames[\s\S]*SmokeYoloSceneCutCarryCleanup[\s\S]*removedUnsupportedStrong=\{sceneCutCarryCleanup\.RemovedUnsupportedStrongCarryLikeFaces\}[\s\S]*removedUnsupportedStrongFrames=\{FormatFrames\(sceneCutCarryCleanup\.RemovedUnsupportedStrongCarryLikeFrameIndices\)\}[\s\S]*protectedStrong=\{sceneCutCarryCleanup\.ProtectedStrongCarryLikeFaces\}[\s\S]*protectedStrongFrames=\{FormatFrames\(sceneCutCarryCleanup\.ProtectedStrongCarryLikeFrameIndices\)\}[\s\S]*blockedFrames=\{FormatFrames\(sceneCutBlockedFrameIndices\)\}[\s\S]*purgeFrames=\{yoloSceneCutCarryPurgeFrames\}[\s\S]*blockFrames=\{yoloSceneCutCarryBlockFrames\}[\s\S]*extendedWeakMaxConfidence=\{yoloSceneCutExtendedWeakCarryMaxConfidence[\s\S]*SmokeYoloFinalMaskPostSceneCleanup"
Assert-Match "smoke harness refills after scene guard with blocked cut pairs" $smokeHarnessText "SmokeYoloFinalMaskPostSceneCleanup[\s\S]*FillShortStableGaps\([\s\S]*BlockedCutFramePairs\s*=\s*sceneCutPairs[\s\S]*BlockedFrameIndices\s*=\s*postSceneBlockedFrameIndices[\s\S]*BlockedFaces\s*=\s*postSceneBlockedFaces[\s\S]*BlockedSceneCarryFaces\s*=\s*sceneCutCarryCleanup\.RemovedFacesInfo[\s\S]*BlockedSceneCarryFrameIndices\s*=\s*sceneCutBlockedFrameIndices[\s\S]*SmokeYoloFinalMaskPostSceneGapFill[\s\S]*blockedByCut=\{postSceneGapFill\.BlockedCutGapFaces\}[\s\S]*blockedByCleanup=\{postSceneGapFill\.BlockedCleanupGapFrames\}[\s\S]*blockedBySceneCarry=\{postSceneGapFill\.BlockedSceneCarryGapFrames\}[\s\S]*suppressedWeakGeometryAnchors=\{postSceneGapFill\.SuppressedWeakGeometryAnchorChecks\}[\s\S]*suppressedRiskyGeometryAnchors=\{postSceneGapFill\.SuppressedRiskyGeometryAnchorChecks\}[\s\S]*unsupportedWeakAnchors=\{postSceneGapFill\.UnsupportedWeakAnchorChecks\}[\s\S]*SmokeYoloFinalMaskPostSceneGapFillSceneCutGuard"
Assert-Match "smoke harness reruns scene-cut carry cleanup for post-gap-fill cuts" $smokeHarnessText "postSceneGapFillGuard\.CutFramePairs\.Count\s*>\s*0[\s\S]*postGapFillSceneCutPairs\s*=\s*sceneCutPairs[\s\S]*Concat\(postSceneGapFillGuard\.CutFramePairs\)[\s\S]*RemoveSceneCutCarryRemnants\([\s\S]*postGapFillSceneCutPairs[\s\S]*BuildSceneCutCarryBlockedFrames\([\s\S]*postGapFillSceneCutPairs[\s\S]*SmokeYoloSceneCutCarryCleanup[\s\S]*stage=post-gap-fill"
Assert-Match "smoke harness cleans weak yolo remnants after post-gap-fill carry cleanup" $smokeHarnessText "SmokeYoloSceneCutCarryCleanup[\s\S]*stage=post-gap-fill[\s\S]*postProcessor\.RemoveWeakIsolatedMasks\(maskProvider\)[\s\S]*SmokeYoloFinalMaskPostGapFillCleanup"
Assert-Match "docs record medium risky anchor support rule" $yoloGuiSmokeResultText "medium-confidence geometry-risk anchors[\s\S]*RiskyGeometryAnchorSupportMaxConfidence=0\.72[\s\S]*third matching temporal support"
Assert-Match "plan records medium risky anchor support rule" $autoMosaicQualityPlanText "medium-confidence geometry-risk anchors[\s\S]*third matching temporal support[\s\S]*suppressedRiskyGeometryAnchors[\s\S]*mediumRiskyAnchorGapFilled=0[\s\S]*mediumRiskyAnchorSuppressed=1[\s\S]*supportedMediumRiskyAnchorGapFilled=2"
Assert-Match "smoke harness detection dump logs normalized geometry" $smokeHarnessText "SmokeDetection[\s\S]*conf=\{conf:F6\}[\s\S]*cx=\{centerX:F3\}[\s\S]*cy=\{centerY:F3\}[\s\S]*areaRatio=\{areaRatio:F6\}[\s\S]*aspectRatio=\{aspectRatio:F3\}"
Assert-Match "smoke harness detection dump logs summary" $smokeHarnessText "SmokeDetectionSummary[\s\S]*frames=\{frameCount\}[\s\S]*detections=\{detectionCount\}[\s\S]*areaRatioAvg=\{avgAreaRatio:F6\}[\s\S]*aspectRatioAvg=\{avgAspectRatio:F3\}"
Assert-Match "smoke harness builds yolo roi candidates" $smokeHarnessText "BuildLargeFaceRoiCandidates"
Assert-Match "smoke harness logs yolo roi refine" $smokeHarnessText "SmokeYoloFaceOnnxRoiRefine"
Assert-Match "smoke harness creates faceonnx roi detector" $smokeHarnessText "new FaceOnnxDetector\(CreateRoiRefinerDetectorOptions"

Write-Host "[YoloProfileVerify] all requested checks passed"
