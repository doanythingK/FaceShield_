param(
    [string]$ProjectFile = "FaceShield.csproj",
    [string]$GitIgnore = ".gitignore",
    [string]$HomeViewModel = "ViewModels/Pages/HomePageViewModel.cs",
    [string]$HomeView = "Views/Pages/HomePageView.axaml",
    [string]$HomeViewCodeBehind = "Views/Pages/HomePageView.axaml.cs",
    [string]$WorkspaceViewModel = "ViewModels/Pages/WorkspaceViewModel.cs",
    [string]$AutoMaskGenerator = "Services/Analysis/AutoMaskGenerator.cs",
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
    [string]$SmoothingCutBoundaryVerifier = "scripts/verify-yolo-temporal-smoothing-cut-boundary.ps1",
    [string]$AutoMosaicDefaultVerifier = "scripts/verify-auto-mosaic-default.ps1"
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

$projectText = Read-RepoFile $ProjectFile
$gitIgnoreText = Read-RepoFile $GitIgnore
$homeText = Read-RepoFile $HomeViewModel
$homeViewText = Read-RepoFile $HomeView
$homeViewCodeBehindText = Read-RepoFile $HomeViewCodeBehind
$workspaceText = Read-RepoFile $WorkspaceViewModel
$autoMaskGeneratorText = Read-RepoFile $AutoMaskGenerator
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
$smoothingCutBoundaryVerifyText = Read-RepoFile $SmoothingCutBoundaryVerifier
$autoMosaicDefaultVerifyText = Read-RepoFile $AutoMosaicDefaultVerifier

Assert-Match "backend enum exposes yolo" $backend "YoloFaceOnnx\s*=\s*3"
Assert-Match "yolo model enum exposes v8" $modelEnum "YoloV8Face\s*=\s*0"
Assert-Match "yolo model enum exposes yolo5" $modelEnum "Yolo5Face\s*=\s*1"

Assert-Match "settings version is v6" $homeText "CurrentAutoSettingsVersion\s*=\s*6"
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
Assert-Match "home reads saved yolo5 auto pipeline profile" $homeText "ResolveSavedYoloDownscaleRatio\(saved\.Yolo5DownscaleRatio[\s\S]*ResolveSavedYoloDownscaleQuality\(saved\.Yolo5DownscaleQuality[\s\S]*Yolo5AutoTrackingEnabled[\s\S]*Yolo5AutoDetectEveryNFrames[\s\S]*Yolo5ParallelSessionCount"
Assert-Match "home reads saved yolov8 auto pipeline profile" $homeText "ResolveSavedYoloDownscaleRatio\(saved\.YoloV8DownscaleRatio[\s\S]*ResolveSavedYoloDownscaleQuality\(saved\.YoloV8DownscaleQuality[\s\S]*YoloV8AutoTrackingEnabled[\s\S]*YoloV8AutoDetectEveryNFrames[\s\S]*YoloV8ParallelSessionCount"
Assert-Match "home suppresses tracking restart while applying yolo profile" $homeText "OnAutoTrackingEnabledChanged[\s\S]*if\s*\(_isApplyingYoloProfile\)"
Assert-Match "home suppresses detect interval restart while applying yolo profile" $homeText "OnAutoDetectEveryNFramesChanged[\s\S]*if\s*\(_isApplyingYoloProfile\)"
Assert-Match "home suppresses downscale restart while applying yolo profile" $homeText "OnSelectedDownscaleOptionChanged[\s\S]*if\s*\(_isApplyingYoloProfile\)"
Assert-Match "home suppresses downscale quality restart while applying yolo profile" $homeText "OnSelectedDownscaleQualityOptionChanged[\s\S]*if\s*\(_isApplyingYoloProfile\)"
Assert-Match "home suppresses parallel restart while applying yolo profile" $homeText "OnSelectedParallelSessionCountChanged[\s\S]*if\s*\(_isApplyingYoloProfile\)"

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
Assert-Match "workspace tracking toggle gates temporal fixes" $workspaceText "private\s+FaceTrackPostProcessResult\s+ApplyAutoTemporalFixes\(\)[\s\S]*if\s*\(!_autoOptions\.UseTracking\)[\s\S]*return\s+FaceTrackPostProcessResult\.Empty"
Assert-Match "workspace tracking toggle gates temporal smoothing" $workspaceText "if\s*\(_autoOptions\.UseTracking\)\s*\{[\s\S]*ApplyAutoTemporalSmoothing\(_autoOptions\.FilterProfile\s*==\s*FaceFilterProfile\.Yolo[\s\S]*yoloPreSmoothCutPairs[\s\S]*Array\.Empty<string>\(\)\);[\s\S]*\}"
Assert-Match "workspace smoothing does not search across long scene gaps" $workspaceText "TemporalSmoothSearchWindowFrames\s*=\s*2[\s\S]*BuildTemporalSmoothingCutStarts\(blockedCutPairs\)[\s\S]*FindNearestTemporalFaces\(facesByFrame,\s*i,\s*-1,\s*TemporalSmoothSearchWindowFrames,\s*blockedCutStarts\)[\s\S]*FindNearestTemporalFaces\(facesByFrame,\s*i,\s*1,\s*TemporalSmoothSearchWindowFrames,\s*blockedCutStarts\)[\s\S]*searched\s*>\s*maxDistanceFrames"
Assert-Match "workspace smoothing blocks pre-smooth yolo scene cuts" $workspaceText "yoloPreSmoothCutPairs[\s\S]*preSmoothGuard\.CutFramePairs[\s\S]*ApplyAutoTemporalSmoothing\(_autoOptions\.FilterProfile\s*==\s*FaceFilterProfile\.Yolo[\s\S]*BuildTemporalSmoothingCutStarts[\s\S]*Split\(""->""[\s\S]*IsBlockedTemporalSmoothingStep"
Assert-Match "smoothing cut boundary verifier covers blocked direct pairs" $smoothingCutBoundaryVerifyText "10->13[\s\S]*AssertSet[\s\S]*13,\s*12[\s\S]*12,\s*13[\s\S]*InvokeFindNearest\(facesByFrame,\s*13,\s*-1,\s*2,\s*blocked\)"
Assert-Match "workspace yolo track profile exists" $workspaceText "if\s*\(profile\s*==\s*FaceFilterProfile\.Yolo\)[\s\S]*MaxLostFillFrames\s*=\s*0[\s\S]*MaxInitialFillFrames\s*=\s*3[\s\S]*MaxConfirmedTrackHoldFrames\s*=\s*SuspiciousNoFaceMaxGap[\s\S]*AllowSmallTrackLostFill\s*=\s*true[\s\S]*StrongConfidence\s*=\s*0\.58f[\s\S]*DropShortTrackMaxDetections\s*=\s*2[\s\S]*DropShortSmallTrackMaxDetections\s*=\s*3[\s\S]*ShortTrackMaxConfidence\s*=\s*0\.48f[\s\S]*DropSparseTrackMaxDetections\s*=\s*3[\s\S]*DropSparseTrackMinSpanFrames\s*=\s*8[\s\S]*DropSparseTrackMaxDensity\s*=\s*0\.42[\s\S]*SparseTrackMaxConfidence\s*=\s*0\.56f[\s\S]*EdgeTailMaxConfidence\s*=\s*0\.50f[\s\S]*EdgeLostFillMaxConfidence\s*=\s*0\.60f[\s\S]*UnstableTailMaxConfidence\s*=\s*0\.40f[\s\S]*LowerFrameTrackMaxConfidence\s*=\s*0\.50f"
Assert-Match "workspace logs sparse temporal removals" $workspaceText "removedSparse=\{result\.RemovedSparseFaces\}"
Assert-Match "workspace logs edge-tail temporal removals" $workspaceText "removedEdgeTail=\{result\.RemovedEdgeTailFaces\}"
Assert-Match "workspace logs final yolo mask summary" $workspaceText "\[FinalMaskSummary\][\s\S]*profile=Yolo[\s\S]*shortGaps=\{shortGapCount\}[\s\S]*largeJumpGaps=\{largeJumpGapRanges\.Count\}[\s\S]*largeJumpRanges=\{FormatTextList\(largeJumpGapRanges\)\}[\s\S]*isolatedFrames=\{FormatFrameList\(isolatedFrames\)\}[\s\S]*lowConf=\{lowConfidenceRows\}[\s\S]*lowConfFrames=\{FormatFrameList\(lowConfidenceFrames[\s\S]*weakNonEdge=\{weakNonEdgeRows\}[\s\S]*weakNonEdgeFrames=\{FormatFrameList\(weakNonEdgeFrames[\s\S]*edgeWeak=\{edgeWeakRows\}[\s\S]*edgeWeakFrames=\{FormatFrameList\(edgeWeakFrames[\s\S]*topEdgeWeak=\{topEdgeWeakRows\}[\s\S]*topEdgeWeakFrames=\{FormatFrameList\(topEdgeWeakFrames[\s\S]*upperWeak=\{upperWeakRows\}[\s\S]*upperWeakFrames=\{FormatFrameList\(upperWeakFrames[\s\S]*lowerWeak=\{lowerWeakRows\}[\s\S]*lowerWeakFrames=\{FormatFrameList\(lowerWeakFrames[\s\S]*aspectBad=\{aspectBadRows\}[\s\S]*aspectBadFrames=\{FormatFrameList\(aspectBadFrames[\s\S]*tinyWeak=\{tinyWeakRows\}[\s\S]*tinyWeakFrames=\{FormatFrameList\(tinyWeakFrames[\s\S]*tinyShort=\{tinyShortRows\}[\s\S]*tinyShortFrames=\{FormatFrameList\(tinyShortFrames"
Assert-Match "workspace final summary reports tiny short residuals" $workspaceText "YoloFinalMaskTinyShortConfidenceMax\s*=\s*0\.62f[\s\S]*YoloFinalMaskTinyShortAreaRatio\s*=\s*0\.0009[\s\S]*tinyShortRows\+\+"
Assert-Match "workspace final summary detects large jumps" $workspaceText "FinalMaskLargeJumpAreaChangeRatio\s*=\s*4\.0[\s\S]*FinalMaskLargeJumpCenterShift\s*=\s*0\.20[\s\S]*TryGetBestFinalMaskFace[\s\S]*GetFinalMaskAreaChange[\s\S]*GetFinalMaskCenterShift"
Assert-Match "workspace final mask summary is yolo-only" $workspaceText "private\s+void\s+LogFinalMaskSummary\(\)[\s\S]*_autoOptions\.FilterProfile\s*!=\s*FaceFilterProfile\.Yolo[\s\S]*return;"
Assert-Match "workspace removes weak isolated final yolo masks" $workspaceText "_autoOptions\.FilterProfile\s*==\s*FaceFilterProfile\.Yolo[\s\S]*RemoveYoloWeakIsolatedFinalMasks\(FrameList\.VideoPath,\s*token\)[\s\S]*YoloFinalMaskPostProcessor[\s\S]*YoloFinalMaskCleanup"
Assert-Match "workspace logs yolo final cleanup reasons" $workspaceText "removedWeakUnsupported=\{cleanup\.RemovedWeakUnsupportedFaces\}[\s\S]*removedWeakShortClusters=\{cleanup\.RemovedWeakShortClusterFaces\}[\s\S]*removedWeakTinyClusters=\{cleanup\.RemovedWeakTinyClusterFaces\}[\s\S]*removedTinyShortClusters=\{cleanup\.RemovedTinyShortClusterFaces\}[\s\S]*removedTinyIsolated=\{cleanup\.RemovedTinyIsolatedFaces\}[\s\S]*removedUpperWeakClusters=\{cleanup\.RemovedUpperWeakClusterFaces\}[\s\S]*removedLowerWeakClusters=\{cleanup\.RemovedLowerWeakClusterFaces\}[\s\S]*removedAspectOutliers=\{cleanup\.RemovedAspectOutlierClusterFaces\}"
Assert-Match "workspace passes yolo cut pairs to final gap fill" $workspaceText "postSmoothGuard\.CutFramePairs[\s\S]*RemoveYoloWeakIsolatedFinalMasks\([\s\S]*blockedCutFramePairs:\s*yoloPreSmoothCutPairs\.Concat\(yoloPostSmoothCutPairs\)\.Distinct\(\)\.ToArray\(\)[\s\S]*bool\s+fillStableGaps\s*=\s*true[\s\S]*blockedCutFramePairs"
Assert-Match "workspace fills stable yolo final gaps" $workspaceText "YoloFinalMaskStableGapMaxFrames\s*=\s*5[\s\S]*FillYoloStableFinalMaskGaps[\s\S]*FillShortStableGaps\([\s\S]*_maskProvider[\s\S]*MaxGapFrames\s*=\s*YoloFinalMaskStableGapMaxFrames[\s\S]*YoloFinalMaskGapFill[\s\S]*blockedByCut=\{gapFill\.BlockedCutGapFaces\}"
Assert-Match "workspace blocks final gap fill across known yolo scene cuts" $workspaceText "FillYoloStableFinalMaskGaps[\s\S]*BlockedCutFramePairs\s*=\s*blockedCutFramePairs\s*\?\?\s*Array\.Empty<string>\(\)"
Assert-Match "workspace guards stable yolo final gaps across scene cuts" $workspaceText "FillYoloStableFinalMaskGaps[\s\S]*gapFill\.CutGuardFacesInfo[\s\S]*FaceTrackSceneCutGuard\(\)\.Apply[\s\S]*YoloFinalMaskGapFillSceneCutGuard"
Assert-Match "workspace broadens yolo scene cut carry candidates conservatively" $workspaceText "YoloSceneCutDirectCarryMaxConfidence\s*=\s*0\.90f[\s\S]*YoloSceneCutDirectCarryMinSourceConfidence\s*=\s*0\.58f[\s\S]*YoloSceneCutPostCutCarryMaxConfidence\s*=\s*0\.78f[\s\S]*YoloSceneCutDifferenceThreshold\s*=\s*0\.15[\s\S]*YoloSceneCutDirectDifferenceThreshold\s*=\s*0\.15[\s\S]*YoloSceneCutDirectDifferenceMaxCandidates\s*=\s*24[\s\S]*YoloSceneCutMatchingTailMaxFrames\s*=\s*5[\s\S]*YoloSceneCutMatchingTailMaxConfidence\s*=\s*0\.90f[\s\S]*YoloSceneCutCandidateMatchMinIou\s*=\s*0\.55[\s\S]*YoloSceneCutCandidateMatchMaxCenterShiftRatio\s*=\s*0\.45[\s\S]*YoloSceneCutCandidateMatchMaxAreaChangeRatio\s*=\s*2\.0[\s\S]*YoloSceneCutPostCutLookbackFrames\s*=\s*3[\s\S]*BuildWeakTrackTransitionCandidates[\s\S]*maxTargetConfidence:\s*YoloSceneCutDirectCarryMaxConfidence[\s\S]*minConfidenceDrop:\s*0\.02f[\s\S]*maxPostCutCarryFrames:\s*5[\s\S]*minSourceConfidence:\s*YoloSceneCutDirectCarryMinSourceConfidence[\s\S]*BuildWeakPostCutCarryCandidates[\s\S]*maxTargetConfidence:\s*YoloSceneCutPostCutCarryMaxConfidence[\s\S]*maxCarryFrames:\s*5[\s\S]*sourceLookbackFrames:\s*YoloSceneCutPostCutLookbackFrames[\s\S]*includeEdgeCandidates:\s*true[\s\S]*directDifferenceThreshold\s*=\s*candidates\.Length\s*<=\s*YoloSceneCutDirectDifferenceMaxCandidates[\s\S]*removeMatchingTailFrames:\s*YoloSceneCutMatchingTailMaxFrames[\s\S]*removeMatchingTailMaxConfidence:\s*YoloSceneCutMatchingTailMaxConfidence[\s\S]*candidateMatchMinIou:\s*YoloSceneCutCandidateMatchMinIou"
Assert-Match "final mask cleanup removes weak isolated non-edge masks" $finalMaskPostProcessorText "RemoveWeakIsolatedMasks[\s\S]*NeighborWindowFrames[\s\S]*WeakConfidenceMax[\s\S]*TouchesFrameEdge[\s\S]*RemoveFaceMask"
Assert-Match "final mask cleanup uses per-face temporal neighbors" $finalMaskPostProcessorText "HasMatchingTemporalNeighbor[\s\S]*HasMatchingFace[\s\S]*NeighborMinIou[\s\S]*NeighborMaxCenterShiftRatio[\s\S]*NeighborMaxAreaChangeRatio"
Assert-Match "final mask cleanup does not keep unrelated weak boxes by frame adjacency only" $finalMaskPostProcessorText "confidence\s*<=\s*options\.WeakConfidenceMax[\s\S]*!TouchesFrameEdge[\s\S]*hasMatchingNeighbor\s*=\s*HasMatchingTemporalNeighbor[\s\S]*removeUnsupported\s*=\s*!hasMatchingNeighbor"
Assert-Match "final mask cleanup removes weak short temporal clusters without strong continuation" $finalMaskPostProcessorText "IsWeakShortTemporalCluster[\s\S]*WeakClusterMaxFrames[\s\S]*WeakClusterMaxConfidence[\s\S]*HasStrongAdjacentContinuation"
Assert-Match "final mask cleanup removes weak tiny temporal clusters only without strong continuation" $finalMaskPostProcessorText "IsWeakTinyTemporalCluster[\s\S]*TinyClusterMaxAreaRatio[\s\S]*HasStrongAdjacentContinuation[\s\S]*TinyClusterMaxFrames"
Assert-Match "final mask cleanup removes medium-confidence tiny isolated non-edge masks" $finalMaskPostProcessorText "IsTinyFace\([\s\S]*options\.TinyIsolatedMaxAreaRatio[\s\S]*!hasMatchingNeighbor[\s\S]*confidence\s*<=\s*options\.TinyIsolatedMaxConfidence"
Assert-Match "final mask cleanup removes medium-confidence tiny short clusters" $finalMaskPostProcessorText "IsTinyShortTemporalCluster[\s\S]*TinyShortClusterMaxFrames[\s\S]*TinyShortClusterMaxConfidence[\s\S]*TinyShortClusterMaxAreaRatio"
Assert-Match "final mask cleanup removes weak aspect outlier clusters" $finalMaskPostProcessorText "IsAspectOutlierTemporalCluster[\s\S]*AspectOutlierClusterMaxFrames[\s\S]*AspectOutlierClusterMaxConfidence[\s\S]*AspectOutlierMinRatio[\s\S]*AspectOutlierMaxRatio"
Assert-Match "final mask cleanup removes upper weak non-edge clusters" $finalMaskPostProcessorText "IsUpperWeakTemporalCluster[\s\S]*UpperWeakClusterMaxFrames[\s\S]*UpperWeakClusterMaxConfidence[\s\S]*UpperWeakClusterMaxCenterYRatio[\s\S]*UpperWeakClusterMaxAreaRatio"
Assert-Match "final mask cleanup removes lower weak non-edge clusters" $finalMaskPostProcessorText "IsLowerWeakTemporalCluster[\s\S]*LowerWeakClusterMaxFrames[\s\S]*LowerWeakClusterMaxConfidence[\s\S]*LowerWeakClusterMinCenterYRatio[\s\S]*LowerWeakClusterMinAreaRatio[\s\S]*LowerWeakClusterMaxAreaRatio"
Assert-Match "final mask cleanup fills stable strong gaps" $finalMaskPostProcessorText "FillShortStableGaps[\s\S]*MaxGapFrames[\s\S]*MinAnchorConfidence[\s\S]*TryFindStableGapMatch[\s\S]*SetFaceRects[\s\S]*FilledFacesInfo[\s\S]*CutGuardFacesInfo"
Assert-Match "final mask gap fill blocks known scene cuts" $finalMaskPostProcessorText "blockedCutGapFaces[\s\S]*blockedCutFrames[\s\S]*CrossesBlockedCut[\s\S]*BlockedCutFramePairs[\s\S]*TryParseFramePair[\s\S]*BlockedCutGapFaces[\s\S]*BlockedCutFrameIndices"
Assert-Match "final mask cleanup exposes supported weak gap options" $finalMaskPostProcessorText "SupportedAnchorMinConfidence[\s\S]*SupportedAnchorNeighborWindowFrames"
Assert-Match "final mask cleanup checks supported weak gap anchors" $finalMaskPostProcessorText "IsGapAnchorEligible[\s\S]*HasSupportedGapAnchorNeighbor[\s\S]*HasSupportedGapAnchorFace"
Assert-Match "final mask gap fill seeds mixed frames" $finalMaskPostProcessorText "CreateFillEntry[\s\S]*TryGetFaceMaskData\(frameIndex[\s\S]*new\s+List<Rect>\(existing\.Faces\)"
Assert-Match "final mask gap fill dedupes against existing frame faces" $finalMaskPostProcessorText "HasMatchingFace\(fill\.Faces,\s*interpolated"
Assert-Match "workspace yolo scene cut guard is gated to yolo" $workspaceText "FaceFilterProfile\.Yolo[\s\S]*RemoveYoloTrackFillAcrossSceneCuts"
Assert-Match "workspace yolo scene cut guard uses shared service" $workspaceText "new\s+FaceTrackSceneCutGuard\(\)[\s\S]*BuildWeakTrackTransitionCandidates[\s\S]*BuildWeakPostCutCarryCandidates[\s\S]*trackPost\.FilledGapFacesInfo[\s\S]*trackPost\.FilledLostFacesInfo[\s\S]*trackPost\.FilledInitialFacesInfo[\s\S]*guard\.Apply"
Assert-Match "workspace yolo scene cut guard logs pre and post smooth stages" $workspaceText "RemoveYoloTrackFillAcrossSceneCuts\(FrameList\.VideoPath,\s*trackPost,\s*token,\s*""pre-smooth""\)[\s\S]*RemoveYoloTrackFillAcrossSceneCuts\(FrameList\.VideoPath,\s*trackPost,\s*token,\s*""post-smooth""\)[\s\S]*stage=\{stage\}"
Assert-Match "scene cut guard builds weak direct candidates" $sceneCutGuardText "BuildWeakTrackTransitionCandidates[\s\S]*FaceTrackBuilder\(\)\.Build[\s\S]*maxTargetConfidence[\s\S]*minSourceConfidence[\s\S]*FaceTrackFilledFace"
Assert-Match "scene cut guard builds weak post-cut carry candidates" $sceneCutGuardText "BuildWeakPostCutCarryCandidates[\s\S]*maxCarryFrames[\s\S]*sourceLookbackFrames[\s\S]*includeEdgeCandidates[\s\S]*BuildWeakCarryRun[\s\S]*HasStrongContinuation[\s\S]*lookbackFrames[\s\S]*sourceFrame[\s\S]*HasMatchingSourceFace[\s\S]*maxTargetConfidence[\s\S]*FaceTrackFilledFace"
Assert-Match "scene cut guard checks weak carry tails" $sceneCutGuardText "maxPostCutCarryFrames[\s\S]*carryEndFrame[\s\S]*lastAddedIndex[\s\S]*FaceTrackFilledFace"
Assert-Match "scene cut guard caches frame differences" $sceneCutGuardText "differenceByPair[\s\S]*TryGetValue\(pair,[\s\S]*frameDifferenceProvider\(pair\.Source,\s*pair\.Target\)[\s\S]*differenceByPair\[pair\]"
Assert-Match "scene cut guard skips exact duplicate candidates" $sceneCutGuardText "DeduplicateCandidates[\s\S]*GetCandidateKey[\s\S]*SourceFrameIndex[\s\S]*FrameIndex[\s\S]*Math\.Round\(candidate\.Bounds"
Assert-Match "scene cut guard removes track fill" $sceneCutGuardText "DefaultDifferenceThreshold\s*=\s*0\.32[\s\S]*DefaultDirectDifferenceThreshold\s*=\s*0\.36[\s\S]*TryGetNextFrameRawToBuffer[\s\S]*RemoveFaceCandidate"
Assert-Match "scene cut guard can remove weak matching post-cut tail" $sceneCutGuardText "RemoveWeakMatchingTail[\s\S]*maxTailFrames[\s\S]*maxTailConfidence[\s\S]*TryRemoveWeakMatchingFace"
Assert-Match "scene cut guard exposes deterministic verifier path" $sceneCutGuardText "Func<int,\s*int,\s*double>\s+frameDifferenceProvider[\s\S]*GetMaxFrameDifference[\s\S]*RemoveFaceCandidate"
Assert-Match "track postprocess exposes synthetic fill confidence cap" $trackBuilderText "SyntheticFillConfidenceMax\s*\{\s*get;\s*init;\s*\}\s*=\s*1\.0f"
Assert-Match "track interpolation caps synthetic fill confidence" $trackInterpolatorText "ClampSyntheticFillConfidence[\s\S]*SyntheticFillConfidenceMax[\s\S]*Math\.Clamp\(sourceConfidence"
Assert-Match "workspace caps yolo synthetic track fill confidence" $workspaceText "SyntheticFillConfidenceMax\s*=\s*YoloSceneCutPostCutCarryMaxConfidence"
Assert-Match "scene cut guard scans adjacent frame differences" $sceneCutGuardText "for\s*\(int\s+frame\s*=\s*sourceFrame;\s*frame\s*<\s*targetFrame;\s*frame\+\+\)[\s\S]*FormatFramePair\(maxSource,\s*maxTarget\)"
Assert-Match "scene cut guard checks cumulative frame difference" $sceneCutGuardText "directDifferenceThreshold[\s\S]*GetFrameDifference\(\s*sourceFrame,\s*targetFrame[\s\S]*TryGetFramePairDifference[\s\S]*FormatFramePair\(sourceFrame,\s*targetFrame\)"
Assert-Match "scene cut guard exposes frame evidence" $sceneCutGuardText "RemovedFrameIndices[\s\S]*CheckedFramePairs[\s\S]*MaxDifference[\s\S]*CutFramePairs"
Assert-Match "workspace logs scene cut frame evidence" $workspaceText "checkedPairs=\{FormatTextList\(result\.CheckedFramePairs\)\}[\s\S]*maxDiff=\{result\.MaxDifference[\s\S]*cutPairs=\{FormatTextList\(result\.CutFramePairs\)\}[\s\S]*removedFrames=\{FormatFrameList\(result\.RemovedFrameIndices\)\}"
Assert-Match "workspace faceonnx track profile remains default branch" $workspaceText "return\s+new\s+FaceTrackPostProcessOptions[\s\S]*WeakConfidence\s*=\s*TemporalConfidenceWeak[\s\S]*StrongConfidence\s*=\s*TemporalConfidenceStrong"
Assert-Match "workspace refreshes preview after track postprocess" $workspaceText "var\s+trackPost\s*=\s*ApplyAutoTemporalFixes\(\);[\s\S]*RefineAutoFacesWithRoi[\s\S]*ApplyAutoTemporalSmoothing\([\s\S]*RefreshAutoPreviewAfterPostProcess\(exportAfter\)"
Assert-Match "workspace roi refine includes initial fill candidates" $workspaceText "private\s+void\s+RefineAutoFacesWithRoi[\s\S]*trackPost\.FilledGapFacesInfo[\s\S]*trackPost\.FilledLostFacesInfo[\s\S]*trackPost\.FilledInitialFacesInfo[\s\S]*new\s+FaceTrackRoiRefiner\(\)\.Apply"
Assert-Match "workspace applies yolo scene cut before and after temporal smoothing" $workspaceText "var\s+trackPost\s*=\s*ApplyAutoTemporalFixes\(\);[\s\S]*RefineAutoFacesWithRoi[\s\S]*RemoveYoloWeakIsolatedFinalMasks\(FrameList\.VideoPath,\s*token\);[\s\S]*var\s+preSmoothGuard\s*=\s*RemoveYoloTrackFillAcrossSceneCuts\(FrameList\.VideoPath,\s*trackPost,\s*token,\s*""pre-smooth""\);[\s\S]*yoloPreSmoothCutPairs\s*=\s*preSmoothGuard\.CutFramePairs;[\s\S]*ApplyAutoTemporalSmoothing\([\s\S]*yoloPreSmoothCutPairs[\s\S]*var\s+postSmoothGuard\s*=\s*RemoveYoloTrackFillAcrossSceneCuts\(FrameList\.VideoPath,\s*trackPost,\s*token,\s*""post-smooth""\);[\s\S]*yoloPostSmoothCutPairs\s*=\s*postSmoothGuard\.CutFramePairs;[\s\S]*blockedCutFramePairs:\s*yoloPreSmoothCutPairs\.Concat\(yoloPostSmoothCutPairs\)\.Distinct\(\)\.ToArray\(\)[\s\S]*LogFinalMaskSummary"
Assert-Match "default verifier runs smoothing cut boundary harness" $autoMosaicDefaultVerifyText "verify-yolo-temporal-smoothing-cut-boundary\.ps1[\s\S]*Invoke-ScriptStep\s+""yolo-temporal-smoothing-cut-boundary""[\s\S]*prevBlocked=True[\s\S]*nextBlocked=True"
Assert-Match "workspace temporal smoothing does not materialize empty frames" $workspaceText "if\s*\(hasStored\[i\]\s*\|\|\s*facesByFrame\[i\]\s*==\s*null\)\s*continue;[\s\S]*if\s*\(hasStored\[i\]\s*\|\|\s*facesByFrame\[i\]\s*==\s*null\s*\|\|\s*facesByFrame\[i\]!\.Count\s*==\s*0\)\s*continue;"
Assert-Match "roi refiner only replaces existing matching faces" $roiRefinerText "TryGetFaceMaskData\(candidate\.FrameIndex,[\s\S]*FindSimilarFaceIndex\(faces,\s*candidate\.Bounds\)[\s\S]*if\s*\(replaceIndex\s*<\s*0\)\s*return\s+false;[\s\S]*faces\[replaceIndex\]\s*=\s*refined\.Bounds"

Assert-Match "automask yolo filter profile exists" $autoMaskGeneratorText "if\s*\(profile\s*==\s*FaceFilterProfile\.Yolo\)[\s\S]*MinSmallFaceAreaRatio\s*\*\s*0\.70[\s\S]*2\.7[\s\S]*0\.30f[\s\S]*UseStatsFilter:\s*false"
Assert-Match "automask faceonnx default uses stats filter" $autoMaskGeneratorText "return\s+new\s+FaceFilterSettings[\s\S]*MaxFaceAspectRatio[\s\S]*SmallFaceConfidenceMin[\s\S]*UseStatsFilter:\s*true"
Assert-Match "automask yolo detector summary includes provider" $autoMaskGeneratorText "_detector\s+is\s+YoloFaceOnnxDetector[\s\S]*YoloFaceOnnxDetector\.GetLastExecutionProviderLabel\(\)[\s\S]*YoloFaceOnnxDetector\.GetLastExecutionProviderError\(\)"
Assert-Match "automask sparse scene cut guard is yolo-only" $autoMaskGeneratorText "guardSceneCuts\s*=\s*_options\.FilterProfile\s*==\s*FaceFilterProfile\.Yolo[\s\S]*IsSparseSceneCut"
Assert-Match "automask sparse scene cut guard blocks materialization" $autoMaskGeneratorText "stopAtSceneCut[\s\S]*sceneCutStops\+\+[\s\S]*\?\s*key\s*\+\s*1"
Assert-Match "automask sparse scene cut summary logs count" $autoMaskGeneratorText "sparseSceneCuts=\{materialized\.SceneCutStops\}"
Assert-Match "automask sparse scene cut summary logs pairs" $autoMaskGeneratorText "sparseSceneCutPairs=\{FormatSparseSceneCutTransitions\(materialized\.SceneCutTransitions\)\}"
Assert-Match "automask sparse scene cut decision is isolated" $autoMaskGeneratorText "ShouldStopSparseSceneCarry[\s\S]*guardSceneCuts[\s\S]*ComputeSignatureDifference"
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
Assert-Match "yolo gpu failure falls back to cpu session" $yoloDetectorText "catch\s*\(Exception\s+ex\)\s*when\s*\(_options\.UseGpu\)[\s\S]*new\s+InferenceSession\(_options\.ModelPath,\s*CreateSessionOptions\(\)\)"
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
    "DropShortTrackMaxDetections\s*=\s*yoloDropShortTrackMaxDetections",
    "DropSparseTrackMaxDetections\s*=\s*3",
    "DropSparseTrackMinSpanFrames\s*=\s*8",
    "DropSparseTrackMaxDensity\s*=\s*0\.42",
    "EdgeTailMaxConfidence\s*=\s*0\.50f",
    "EdgeLostFillMaxConfidence\s*=\s*0\.60f",
    "SyntheticFillConfidenceMax\s*=\s*yoloSceneCutPostCutCarryMaxConfidence",
    "MaxConfirmedTrackHoldFrames\s*=\s*8",
    "AllowSmallTrackLostFill\s*=\s*true",
    "ShortTrackMaxConfidence\s*=\s*yoloShortTrackMaxConfidence",
    "SparseTrackMaxConfidence\s*=\s*0\.56f",
    "LowerFrameTrackMaxConfidence\s*=\s*yoloLowerFrameTrackMaxConfidence")) {
    Assert-Match "smoke harness maps $assignment" $smokeHarnessText $assignment
}

Assert-Match "home enables yolo aspect ratio filter" $homeText "UseAspectRatioFilter\s*=\s*true[\s\S]*MinAspectRatio\s*=\s*0\.35[\s\S]*MaxAspectRatio\s*=\s*1\.65"
Assert-Match "home leaves risky top small low-confidence filter opt-in" $options "public\s+bool\s+UseTopSmallLowConfidenceFilter\s*\{\s*get;\s*init;\s*\}\s*=\s*false;"

Assert-Match "smoke harness supports yolo roi refine switch" $smokeHarnessText "if\s*\(useYolo\s*&&\s*yoloUseFaceOnnxRoiRefine\)"
Assert-Match "smoke harness logs scene cut guard" $smokeHarnessText "new\s+FaceTrackSceneCutGuard\(\)[\s\S]*BuildWeakTrackTransitionCandidates[\s\S]*BuildWeakPostCutCarryCandidates[\s\S]*trackPost\.FilledGapFacesInfo[\s\S]*trackPost\.FilledLostFacesInfo[\s\S]*trackPost\.FilledInitialFacesInfo[\s\S]*SmokeFaceTrackSceneCutGuard[\s\S]*postCutCandidates"
Assert-Match "smoke harness matches yolo scene cut threshold" $smokeHarnessText "yoloSceneCutDirectCarryMaxConfidence\s*=\s*0\.90f[\s\S]*yoloSceneCutDirectCarryMinSourceConfidence\s*=\s*0\.58f[\s\S]*yoloSceneCutPostCutCarryMaxConfidence\s*=\s*0\.78f[\s\S]*yoloSceneCutDifferenceThreshold\s*=\s*0\.15[\s\S]*yoloSceneCutDirectDifferenceThreshold\s*=\s*0\.15[\s\S]*yoloSceneCutDirectDifferenceMaxCandidates\s*=\s*24[\s\S]*yoloSceneCutMatchingTailMaxFrames\s*=\s*5[\s\S]*yoloSceneCutMatchingTailMaxConfidence\s*=\s*0\.90f[\s\S]*yoloSceneCutCandidateMatchMinIou\s*=\s*0\.55[\s\S]*yoloSceneCutCandidateMatchMaxCenterShiftRatio\s*=\s*0\.45[\s\S]*yoloSceneCutCandidateMatchMaxAreaChangeRatio\s*=\s*2\.0[\s\S]*yoloSceneCutPostCutLookbackFrames\s*=\s*3[\s\S]*minConfidenceDrop:\s*0\.02f[\s\S]*maxPostCutCarryFrames:\s*5[\s\S]*minSourceConfidence:\s*yoloSceneCutDirectCarryMinSourceConfidence[\s\S]*maxCarryFrames:\s*5[\s\S]*sourceLookbackFrames:\s*yoloSceneCutPostCutLookbackFrames[\s\S]*sceneCutDirectDifferenceThreshold\s*=\s*sceneCutCandidates\.Length\s*<=\s*yoloSceneCutDirectDifferenceMaxCandidates[\s\S]*differenceThreshold:\s*yoloSceneCutDifferenceThreshold[\s\S]*directDifferenceThreshold:\s*sceneCutDirectDifferenceThreshold[\s\S]*removeMatchingTailFrames:\s*yoloSceneCutMatchingTailMaxFrames[\s\S]*removeMatchingTailMaxConfidence:\s*yoloSceneCutMatchingTailMaxConfidence[\s\S]*candidateMatchMinIou:\s*yoloSceneCutCandidateMatchMinIou"
Assert-Match "smoke harness logs initial fill count" $smokeHarnessText "SmokeFaceTrackPost[\s\S]*initialFilled=\{trackPost\.FilledInitialFaces\}"
Assert-Match "smoke harness logs direct scene cut candidates" $smokeHarnessText "directCandidates=\{directCandidates\.Count\}"
Assert-Match "smoke harness logs scene cut frame evidence" $smokeHarnessText "checkedPairs=\{FormatTextValues\(sceneCut\.CheckedFramePairs\)\}[\s\S]*maxDiff=\{sceneCut\.MaxDifference[\s\S]*cutPairs=\{FormatTextValues\(sceneCut\.CutFramePairs\)\}[\s\S]*removedFrames=\{FormatFrames\(sceneCut\.RemovedFrameIndices\)\}"
Assert-Match "smoke harness logs final yolo mask summary" $smokeHarnessText "if\s*\(useYolo\)[\s\S]*LogFinalMaskSummary\(label,\s*maskProvider\)[\s\S]*SmokeFinalMaskSummary[\s\S]*shortGaps=\{shortGapCount\}[\s\S]*largeJumpGaps=\{largeJumpGapRanges\.Count\}[\s\S]*largeJumpRanges=\{FormatTextValues\(largeJumpGapRanges\)\}[\s\S]*isolatedFrames=\{FormatFrames\(isolatedFrames\)\}[\s\S]*lowConfFrames=\{FormatFrames\(lowConfidenceFrames[\s\S]*weakNonEdge=\{weakNonEdgeRows\}[\s\S]*weakNonEdgeFrames=\{FormatFrames\(weakNonEdgeFrames[\s\S]*edgeWeak=\{edgeWeakRows\}[\s\S]*edgeWeakFrames=\{FormatFrames\(edgeWeakFrames[\s\S]*topEdgeWeak=\{topEdgeWeakRows\}[\s\S]*topEdgeWeakFrames=\{FormatFrames\(topEdgeWeakFrames[\s\S]*upperWeak=\{upperWeakRows\}[\s\S]*upperWeakFrames=\{FormatFrames\(upperWeakFrames[\s\S]*lowerWeak=\{lowerWeakRows\}[\s\S]*lowerWeakFrames=\{FormatFrames\(lowerWeakFrames[\s\S]*aspectBad=\{aspectBadRows\}[\s\S]*aspectBadFrames=\{FormatFrames\(aspectBadFrames[\s\S]*tinyWeak=\{tinyWeakRows\}[\s\S]*tinyWeakFrames=\{FormatFrames\(tinyWeakFrames[\s\S]*tinyShort=\{tinyShortRows\}[\s\S]*tinyShortFrames=\{FormatFrames\(tinyShortFrames"
Assert-Match "smoke harness final summary detects large jumps" $smokeHarnessText "largeJumpAreaChangeRatio\s*=\s*4\.0[\s\S]*largeJumpCenterShift\s*=\s*0\.20[\s\S]*TryGetBestFinalMaskFace[\s\S]*GetFinalMaskAreaChange[\s\S]*GetFinalMaskCenterShift"
Assert-Match "smoke harness removes weak isolated final yolo masks" $smokeHarnessText "YoloFinalMaskPostProcessor[\s\S]*RemoveWeakIsolatedMasks\(maskProvider\)[\s\S]*SmokeYoloFinalMaskCleanup"
Assert-Match "smoke harness logs yolo final cleanup reasons" $smokeHarnessText "removedWeakUnsupported=\{cleanup\.RemovedWeakUnsupportedFaces\}[\s\S]*removedWeakShortClusters=\{cleanup\.RemovedWeakShortClusterFaces\}[\s\S]*removedWeakTinyClusters=\{cleanup\.RemovedWeakTinyClusterFaces\}[\s\S]*removedTinyShortClusters=\{cleanup\.RemovedTinyShortClusterFaces\}[\s\S]*removedTinyIsolated=\{cleanup\.RemovedTinyIsolatedFaces\}[\s\S]*removedUpperWeakClusters=\{cleanup\.RemovedUpperWeakClusterFaces\}[\s\S]*removedLowerWeakClusters=\{cleanup\.RemovedLowerWeakClusterFaces\}[\s\S]*removedAspectOutliers=\{cleanup\.RemovedAspectOutlierClusterFaces\}"
Assert-Match "smoke harness logs yolo final gap fill" $smokeHarnessText "yoloFinalMaskStableGapMaxFrames\s*=\s*5[\s\S]*FillShortStableGaps\([\s\S]*maskProvider[\s\S]*MaxGapFrames\s*=\s*yoloFinalMaskStableGapMaxFrames[\s\S]*SmokeYoloFinalMaskGapFill[\s\S]*blockedByCut=\{gapFill\.BlockedCutGapFaces\}"
Assert-Match "smoke harness guards yolo final gap fill across scene cuts" $smokeHarnessText "FillShortStableGaps\([\s\S]*maskProvider[\s\S]*gapFill\.CutGuardFacesInfo[\s\S]*FaceTrackSceneCutGuard\(\)\.Apply[\s\S]*SmokeYoloFinalMaskGapFillSceneCutGuard"
Assert-Match "smoke harness cleans weak remnants after scene guard" $smokeHarnessText "SmokeFaceTrackSceneCutGuard[\s\S]*postProcessor\.RemoveWeakIsolatedMasks\(maskProvider\)[\s\S]*SmokeYoloFinalMaskPostSceneCleanup"
Assert-Match "smoke harness detection dump logs normalized geometry" $smokeHarnessText "SmokeDetection[\s\S]*cx=\{centerX:F3\}[\s\S]*cy=\{centerY:F3\}[\s\S]*areaRatio=\{areaRatio:F6\}[\s\S]*aspectRatio=\{aspectRatio:F3\}"
Assert-Match "smoke harness detection dump logs summary" $smokeHarnessText "SmokeDetectionSummary[\s\S]*frames=\{frameCount\}[\s\S]*detections=\{detectionCount\}[\s\S]*areaRatioAvg=\{avgAreaRatio:F6\}[\s\S]*aspectRatioAvg=\{avgAspectRatio:F3\}"
Assert-Match "smoke harness builds yolo roi candidates" $smokeHarnessText "BuildLargeFaceRoiCandidates"
Assert-Match "smoke harness logs yolo roi refine" $smokeHarnessText "SmokeYoloFaceOnnxRoiRefine"
Assert-Match "smoke harness creates faceonnx roi detector" $smokeHarnessText "new FaceOnnxDetector\(CreateRoiRefinerDetectorOptions"

Write-Host "[YoloProfileVerify] all requested checks passed"
