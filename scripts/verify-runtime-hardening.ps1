param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
)

$ErrorActionPreference = "Stop"
$Failures = [System.Collections.Generic.List[string]]::new()

function Read-RepoFile([string]$relativePath) {
    $path = Join-Path $RepoRoot $relativePath
    if (-not (Test-Path $path)) {
        throw "Required file not found: $relativePath"
    }
    return Get-Content -Raw -Path $path
}

function Assert-Match([string]$label, [string]$text, [string]$pattern) {
    if ($text -notmatch $pattern) {
        $script:Failures.Add($label)
    }
}

function Assert-NotMatch([string]$label, [string]$text, [string]$pattern) {
    if ($text -match $pattern) {
        $script:Failures.Add($label)
    }
}

$framePreview = Read-RepoFile "ViewModels/Workspace/FramePreviewViewModel.cs"
$framePreviewView = Read-RepoFile "Views/Workspace/FramePreviewView.axaml.cs"
$toolPanelView = Read-RepoFile "Views/Workspace/ToolPanelView.axaml"
$workspace = Read-RepoFile "ViewModels/Pages/WorkspaceViewModel.cs"
$workspaceLifetime = Read-RepoFile "ViewModels/Workspace/WorkspaceOperationLifetime.cs"
$autoCoordinator = Read-RepoFile "ViewModels/Workspace/AutoMaskRunCoordinator.cs"
$exportCoordinator = Read-RepoFile "ViewModels/Workspace/WorkspaceExportCoordinator.cs"
$sessionCoordinator = Read-RepoFile "ViewModels/Workspace/WorkspaceSessionPlaybackCoordinator.cs"
$workspaceStore = Read-RepoFile "Services/Workspace/WorkspaceStateStore.cs"
$extractor = Read-RepoFile "Services/Video/FfFrameExtractor.cs"
$metadataReader = Read-RepoFile "Services/Video/VideoMetadataReader.cs"
$autoMask = Read-RepoFile "Services/Analysis/AutoMaskGenerator.cs"
$frameAnalyzer = Read-RepoFile "Services/Analysis/FrameAnalyzer.cs"
$autoPostProcess = Read-RepoFile "Services/Analysis/AutoMaskPostProcessPipeline.cs"
$roiStep = Read-RepoFile "Services/Analysis/AutoMaskRoiRefineStep.cs"
$roiRefiner = Read-RepoFile "Services/Analysis/FaceTrackRoiRefiner.cs"
$thumbnailProvider = Read-RepoFile "Services/Video/TimelineThumbnailProvider.cs"
$videoSession = Read-RepoFile "Services/Video/Session/VideoSession.cs"
$manualPlayer = Read-RepoFile "Services/Video/Session/ManualFramePlayer.cs"
$timelineController = Read-RepoFile "Services/Video/Session/TimelineController.cs"
$exactProvider = Read-RepoFile "Services/Video/Session/ExactFrameProvider.cs"
$frameList = Read-RepoFile "ViewModels/Workspace/FrameListViewModel.cs"
$timelineStrip = Read-RepoFile "Controls/TimelineFrameStrip.cs"
$frameListView = Read-RepoFile "Views/Workspace/FrameListView.axaml"
$app = Read-RepoFile "App.axaml.cs"
$homeViewModel = Read-RepoFile "ViewModels/Pages/HomePageViewModel.cs"
$homeView = Read-RepoFile "Views/Pages/HomePageView.axaml"
$exportService = Read-RepoFile "Services/Video/VideoExportService.cs"
$exportStaging = Read-RepoFile "Services/Video/VideoExportStagingPolicy.cs"
$exportFidelity = Read-RepoFile "Services/Video/VideoExportFidelityPolicy.cs"
$exportQualityPreset = Read-RepoFile "Services/Video/VideoExportQualityPreset.cs"
$hdrProbe = Read-RepoFile "Services/Video/VideoHdrProbePolicy.cs"
$videoIoInterrupt = Read-RepoFile "Services/Video/VideoIoInterruptGuard.cs"
$temporalPostProcessor = Read-RepoFile "Services/Analysis/AutoMaskTemporalPostProcessor.cs"
$trackInterpolator = Read-RepoFile "Services/Analysis/FaceTrackInterpolator.cs"
$trackBuilder = Read-RepoFile "Services/Analysis/FaceTrackBuilder.cs"
$yoloFinalPostProcessor = Read-RepoFile "Services/Analysis/YoloFinalMaskPostProcessor.cs"
$yoloRiskCascade = Read-RepoFile "Services/Analysis/YoloRiskCascadeStep.cs"
$frameMaskProvider = Read-RepoFile "Services/Video/FrameMaskProvider.cs"
$sceneCutGuard = Read-RepoFile "Services/Analysis/FaceTrackSceneCutGuard.cs"
$yoloSceneCutPostProcessor = Read-RepoFile "Services/Analysis/YoloSceneCutPostProcessor.cs"
$toolPanel = Read-RepoFile "ViewModels/Workspace/ToolPanelViewModel.cs"
$faceOnnxDetector = Read-RepoFile "Services/FaceDetection/FaceOnnxDetector.cs"
$yoloOnnxDetector = Read-RepoFile "Services/FaceDetection/YoloFaceOnnxDetector.cs"

# Native I/O and cancellation boundaries.
Assert-Match "export input uses cancellable native io guard" ($exportService + $videoIoInterrupt) 'VideoIoInterruptGuard[\s\S]{0,1000}Begin\(cancellationToken\)[\s\S]{0,1800}avformat_open_input[\s\S]{0,1200}avformat_find_stream_info'
Assert-Match "HDR probe accepts cancellation and uses native io guard" ($hdrProbe + $videoIoInterrupt) 'ProbeVideoHdrMetadata\([\s\S]{0,220}CancellationToken\s+cancellationToken[\s\S]{0,1400}VideoIoInterruptGuard[\s\S]{0,1800}avformat_open_input'
Assert-Match "extractor wraps shared AVIO interrupt guard" $extractor 'private\s+void\s+ConfigureIoInterrupt\([\s\S]{0,500}ioInterrupt\.Configure\(format\)[\s\S]{0,500}private\s+VideoIoInterruptGuard\.InterruptScope\s+BeginIoInterrupt\([\s\S]{0,500}return\s+ioInterrupt\.Begin\(cancellationToken\)'
Assert-NotMatch "extractor no longer owns duplicate AVIO callback" $extractor '_ioInterruptHandle|_ioInterruptRequested|HandleIoInterrupt|IoInterruptCallback'
Assert-Match "ffmpeg input installs interrupt callback before open" $extractor 'avformat_alloc_context\(\)[\s\S]{0,900}ConfigureIoInterrupt\(_fmt\)[\s\S]{0,900}BeginIoInterrupt\(cancellationToken\)[\s\S]{0,900}avformat_open_input'
Assert-Match "ffmpeg seeks use cancellable native io scope" $extractor 'SeekMainDecoder\([\s\S]{0,1200}BeginIoInterrupt\(cancellationToken\)[\s\S]{0,800}av_seek_frame'
Assert-Match "metadata reader checks cancellation after open and stream info" $metadataReader 'avformat_open_input[\s\S]{0,500}ThrowIfCancellationRequested\(\)[\s\S]{0,900}avformat_find_stream_info[\s\S]{0,500}ThrowIfCancellationRequested\(\)'
Assert-Match "auto metadata probe uses shared cancellable reader" $autoMask 'ThrowIfCancellationRequested\(\)[\s\S]{0,500}VideoMetadataReader\.Read\(videoPath,\s*ct\)'
Assert-NotMatch "auto mask does not perform raw metadata open" $autoMask 'avformat_(open_input|find_stream_info)'
Assert-Match "frame analyzer metadata probe uses shared cancellable reader" $frameAnalyzer 'VideoMetadataReader\.Read\(videoPath,\s*ct\)'
Assert-NotMatch "frame analyzer does not perform raw metadata open" $frameAnalyzer 'avformat_(open_input|find_stream_info)'

# Sparse temporal/post-processing invariants.
Assert-Match "tracked stabilizer is sparse and cancellable" $temporalPostProcessor 'ApplyTrackedBoxStabilization\([\s\S]{0,300}CancellationToken\s+cancellationToken[\s\S]{0,2200}GetFaceMaskFrameIndices\(\)[\s\S]{0,2400}ThrowIfCancellationRequested\(\)'
Assert-NotMatch "tracked stabilizer does not allocate total-frame arrays" $temporalPostProcessor 'ApplyTrackedBoxStabilization\([\s\S]{0,3500}new\s+(List<Rect>\?\[|List<float>\?\[|PixelSize\[|bool\[)totalFrames\]'
Assert-Match "temporal smoothing is sparse and cancellable" $temporalPostProcessor 'ApplyTemporalSmoothing\([\s\S]{0,320}CancellationToken\s+cancellationToken\s*=\s*default[\s\S]{0,1800}Dictionary<int,\s*List<Rect>>[\s\S]{0,2400}ThrowIfCancellationRequested\(\)'
Assert-NotMatch "temporal smoothing does not allocate total-frame arrays" $temporalPostProcessor 'ApplyTemporalSmoothing\([\s\S]{0,4500}new\s+(List<Rect>\?\[|List<float>\?\[|PixelSize\[|bool\[)totalFrames\]'
Assert-NotMatch "temporal smoothing does not scan every declared frame" $temporalPostProcessor 'ApplyTemporalSmoothing\([\s\S]{0,6500}for\s*\(int\s+i\s*=\s*(0|1);\s*i\s*<\s*totalFrames'
Assert-Match "face track interpolator accepts cancellation" ($trackInterpolator + $trackBuilder + $autoPostProcess) 'FaceTrackInterpolator[\s\S]*CancellationToken\s+cancellationToken\s*=\s*default[\s\S]*builder\.Build\(detectionsByFrame,\s*options,\s*cancellationToken\)'
Assert-Match "face track builder checks cancellation" $trackBuilder 'Build\([\s\S]{0,300}CancellationToken\s+cancellationToken[\s\S]{0,1800}cancellationToken\.ThrowIfCancellationRequested\(\)'
Assert-Match "auto postprocess propagates cancellation to temporal smoothing" $autoPostProcess 'ApplyTemporalSmoothing\([\s\S]{0,500}Array\.Empty<string>\(\),\s*cancellationToken\)'
Assert-Match "auto ROI pipeline propagates cancellation" $autoPostProcess 'AutoMaskRoiRefineStep\(\)\.Apply\([\s\S]{0,700}useFaceOnnxRoiDetector,\s*cancellationToken\)'
Assert-Match "auto ROI step forwards cancellation to refiner" $roiStep 'CancellationToken\s+cancellationToken\s*=\s*default[\s\S]{0,1500}FaceTrackRoiRefiner\(\)\.Apply\([\s\S]{0,500}cancellationToken:\s*cancellationToken'
Assert-Match "ROI refiner checks cancellation around synchronous inference" $roiRefiner 'ThrowIfCancellationRequested\(\);[\s\S]{0,350}DetectFacesBgra\([\s\S]{0,350}ThrowIfCancellationRequested\(\);'

# Export fidelity contract follows the selectable source-relative presets.
Assert-Match "RGB H264 uses compatibility contract" $exportFidelity 'CanEncodeCompatibleX264Rgb'
Assert-NotMatch "RGB path does not claim lossless compatibility" ($exportService + $exportFidelity) 'CanEncodeLosslessX264Rgb|losslessX264RgbConfigured'
Assert-Match "known-source bitrate delegates to preset policy" $exportFidelity 'ApplyBitrateMultiplier\(\s*boundedSourceBitrate,\s*qualityPreset\)'
Assert-Match "size-priority bitrate remains source-relative 1.00x" $exportQualityPreset 'VideoExportQualityPreset\.SizePriority\s*=>\s*1\.00'
Assert-Match "balanced bitrate remains source-relative 1.20x" $exportQualityPreset 'VideoExportQualityPreset\.Balanced\s*=>\s*1\.20'
Assert-Match "quality-priority bitrate remains bounded at 1.40x" $exportQualityPreset 'VideoExportQualityPreset\.QualityPriority\s*=>\s*1\.40'

# Extractor lifetime and decoded-timeline safety.
Assert-Match "extractor frame size is protected by lifetime lock" $extractor 'public\s+PixelSize\s+FrameSize[\s\S]{0,260}lock\s*\(_sync\)[\s\S]{0,160}ThrowIfDisposedLocked\(\)[\s\S]{0,160}_dec->width'
Assert-Match "extractor dispose marks disposed under lifetime lock" $extractor 'public\s+void\s+Dispose\(\)[\s\S]{0,240}lock\s*\(_sync\)[\s\S]{0,160}if\s*\(_disposed\)[\s\S]{0,120}_disposed\s*=\s*true'
Assert-Match "extractor decode checks disposed after acquiring lock" $extractor 'GetFrameByIndex\([\s\S]{0,420}lock\s*\(_sync\)[\s\S]{0,120}ThrowIfDisposedLocked\(\)'
Assert-Match "raw buffer dimensions are read under lifetime lock" $extractor 'TryGetNextFrameRawToBuffer\([\s\S]{0,700}lock\s*\(_sync\)[\s\S]{0,180}ThrowIfDisposedLocked\(\)[\s\S]{0,240}_dec->width'
Assert-Match "single video can use full global PTS frame budget" $extractor 'MaxCachedTimelineFramesPerVideo\s*=\s*1_000_000[\s\S]{0,120}MaxCachedTimelineFramesTotal\s*=\s*1_000_000'
Assert-Match "decoded timeline extent fails closed for unsafe exact timestamp seek" $extractor 'TryGetDecodedTimelineExtentSeconds\([\s\S]{0,700}!_decodedFrameTimeline\.SupportsExactTimestampSeek'
Assert-Match "decoded PTS cache tracks live owners" $extractor 'LiveOwnerCount[\s\S]*ReleaseDecodedFrameTimeline'
Assert-Match "decoded PTS cache enforces resident budget" $extractor 'GetResidentTimelineFrameCountLocked\(\)\s*>?=\s*MaxCachedTimelineFramesTotal'

# Auto pipeline staging and cancellation semantics.
Assert-Match "risk cascade and postprocess use one staged provider" $autoMask 'CreateSnapshot\(\s*out\s+long\s+providerVersion,\s*ct\)[\s\S]*YoloRiskCascadeStep\(\)\.Apply\(\s*workingProvider,[\s\S]*AutoMaskPostProcessPipeline\(\s*workingProvider,'
Assert-Match "staged auto state commits only after postprocess succeeds" $autoMask 'postProcess\.Apply\([\s\S]{0,700}ThrowIfCancellationRequested\(\)[\s\S]{0,320}CommitFaceMasksFrom\([\s\S]{0,260}providerVersion'
Assert-Match "stored-mask writes serialize both stores" $frameMaskProvider 'public\s+void\s+SetMask\([\s\S]{0,300}lock\s*\(_stateGate\)[\s\S]{0,700}_masks[\s\S]{0,300}_faceMasks'
Assert-Match "face-rect writes serialize through state gate" $frameMaskProvider 'public\s+void\s+SetFaceRects\([\s\S]{0,400}lock\s*\(_stateGate\)[\s\S]{0,300}SetFaceRectsLocked\('
Assert-Match "mask provider staged commit validates live version" $frameMaskProvider 'CommitFaceMasksFrom\([\s\S]{0,280}expectedVersion[\s\S]{0,700}_version\s*!=\s*expectedVersion[\s\S]{0,600}_faceMasks\.Clear\(\)'
Assert-Match "mask provider staged commit preserves manual masks" $frameMaskProvider 'CommitFaceMasksFrom\([\s\S]{0,1200}_masks\.ContainsKey\(entry\.Key\)[\s\S]{0,220}continue'
Assert-Match "bitmap snapshot copy checks cancellation per row" $frameMaskProvider 'CloneBitmap\([\s\S]{0,140}CancellationToken\s+cancellationToken[\s\S]{0,900}for\s*\(int\s+y[\s\S]{0,140}ThrowIfCancellationRequested\(\)'
Assert-Match "risk cascade separates cancellation from ordinary failure" $yoloRiskCascade 'catch\s*\(OperationCanceledException\)[\s\S]{0,80}throw;[\s\S]{0,120}catch\s*\(Exception\s+ex\)'
Assert-Match "risk cascade checks cancellation before accepted face commit" $yoloRiskCascade 'if\s*\(acceptedOnFrame\s*<=\s*0\)[\s\S]{0,220}ThrowIfCancellationRequested\(\)[\s\S]{0,280}SetFaceRects\('
Assert-Match "auto run enters workspace lifetime gate" $autoCoordinator 'RunAsync\([\s\S]{0,500}!_tryBeginLifetimeOperation\(\)'
Assert-Match "auto cancellation persists state and returns false" $autoCoordinator 'catch\s*\(OperationCanceledException\)[\s\S]{0,180}Completed\s*=\s*autoAnalysisCompleted[\s\S]{0,180}ResumeIndex\s*=\s*0[\s\S]{0,220}_persistWorkspaceState\(!exportAfter\)[\s\S]{0,120}return\s+false'
Assert-Match "auto completion checks cancellation after anomaly review" $autoCoordinator 'await\s+BuildAnomaliesAsync\(token\);[\s\S]{0,180}token\.ThrowIfCancellationRequested\(\);[\s\S]{0,220}Completed\s*=\s*true[\s\S]{0,100}ResumeIndex\s*=\s*0'

# Workspace operation ownership and export boundaries.
Assert-Match "workspace lifetime closes admission before disposal" $workspaceLifetime 'RequestDispose\([\s\S]{0,700}_admissionClosed\s*=\s*true;[\s\S]{0,120}_disposeRequested\s*=\s*true'
Assert-Match "workspace lifetime waits for active operations to drain" $workspaceLifetime 'End\(\)[\s\S]{0,800}_disposeRequested[\s\S]{0,120}_activeOperations\s*==\s*0[\s\S]{0,400}_onOperationsDrained\(\)'
Assert-Match "workspace dispose cancels operations before owned resource disposal" $workspace 'RequestDispose\(out\s+bool\s+disposeNow\)[\s\S]{0,220}CancelOwnedOperations\(\)[\s\S]{0,180}ScheduleOwnedResourceDispose\(\)'
Assert-Match "export enters workspace lifetime gate" $exportCoordinator 'ExportAsync\([\s\S]{0,700}!_tryBeginLifetimeOperation\(\)'
Assert-Match "export cancellation is normal false result" $exportCoordinator 'catch\s*\(OperationCanceledException\)[\s\S]{0,80}return\s+false'
Assert-Match "workspace carries explicit overwrite policy" $exportCoordinator 'allowOutputOverwrite'
Assert-Match "save-as unique path never falls back to original" $workspace 'Guid\.NewGuid\(\)[\s\S]*고유한 내보내기 파일명'
Assert-Match "export non-overwrite commit uses atomic move" $exportStaging 'if\s*\(!allowOverwrite\)[\s\S]{0,500}File\.Move\(stagedOutputPath,\s*finalOutputPath,\s*overwrite:\s*false\)'

# Manual player lifetime, session ownership, cancellation, and UI editability.
Assert-Match "manual player blocks new operations while closing" $manualPlayer 'EnterOperation\(\)[\s\S]{0,500}_closing\s*\|\|[\s\S]{0,180}ObjectDisposedException'
Assert-Match "manual player counts operations before decode gate wait" $manualPlayer 'using\s+OperationLease\s+operation\s*=\s*EnterOperation\(\);[\s\S]{0,180}_decodeGate\.Wait\(cancellationToken\)'
Assert-Match "manual player async disposal drains operations before resource disposal" $manualPlayer 'StopAndWaitAsync\(\)[\s\S]{0,180}Task\s+drainTask\s*=\s*BeginClose\(\);[\s\S]{0,160}await\s+drainTask\.ConfigureAwait\(false\);[\s\S]{0,100}DisposeResources\(\)'
Assert-Match "manual player disposes extractor before decode gate" $manualPlayer '_extractor\.Dispose\(\);[\s\S]{0,80}_decodeGate\.Dispose\(\);'
Assert-Match "manual player can invalidate uncommitted sequential position" $manualPlayer 'InvalidateSequentialPosition\(\)[\s\S]{0,500}_decodeGate\.Wait\(\)[\s\S]{0,350}_hasSequentialPosition\s*=\s*false'
Assert-Match "video session asynchronously disposes manual player before shared session resources" $videoSession 'DisposeAsync\(\)[\s\S]{0,500}await\s+ManualPlayer\.DisposeAsync\(\)\.ConfigureAwait\(false\)[\s\S]{0,220}DisposeSessionResources\(\)'
Assert-Match "manual session enables dedicated player" $sessionCoordinator 'enableManualPlayer:\s*_mode\s*==\s*WorkspaceMode\.Manual'
Assert-Match "async session initialization links caller cancellation" $sessionCoordinator 'EnsureInitializedAsync\([\s\S]{0,800}CreateLinkedTokenSource\(cancellationToken\)[\s\S]{0,1800}sessionCts\.Token'
Assert-Match "workspace attaches session-owned thumbnail provider" $sessionCoordinator '_frameList\.SetThumbnailProvider\(session\.ThumbnailProvider\)'
Assert-Match "session readiness gates playback before adoption" $sessionCoordinator '_frameList\.SetPlaybackEnabled\(false\)[\s\S]*AdoptSession\([\s\S]*_initialized\s*=\s*true;[\s\S]{0,260}_frameList\.SetPlaybackEnabled\(true\)'
Assert-Match "session readiness gates workspace edit commands" $sessionCoordinator '_framePreview\.SetSessionReady\(false\)[\s\S]*AdoptSession\([\s\S]*_framePreview\.SetSessionReady\(true\)'
Assert-Match "preview playback requires an adopted session" $framePreview 'StartPlayback\([\s\S]{0,900}var\s+playbackSession\s*=\s*_session;[\s\S]{0,220}playbackSession\s*==\s*null'
Assert-Match "manual shutdown waits playback then frame load" $framePreview 'StopManualOperationsAndWaitAsync\(\)[\s\S]{0,180}StopPlaybackAndWaitAsync\(\)[\s\S]{0,180}CancelManualFrameLoadAndWaitAsync\(\)'
Assert-Match "manual playback disposes and invalidates unaccepted decoded frame" $framePreview 'RunManualSequentialPlaybackAsync\([\s\S]*if\s*\(!frameAccepted\)[\s\S]{0,180}frame\?\.Dispose\(\);[\s\S]{0,180}TryInvalidateManualSequentialPosition\(player\)'
Assert-Match "general playback disposes unaccepted decoded frame" $framePreview 'RunSequentialPlaybackAsync\([\s\S]*finally[\s\S]{0,160}!frameAccepted[\s\S]{0,120}frame\?\.Dispose\(\)'
Assert-Match "manual loading cleanup is generation guarded" $framePreview 'ClearManualFrameLoadingState\(int\s+generation\)[\s\S]{0,300}generation\s*!=\s*_manualFrameLoadGeneration[\s\S]{0,220}IsFrameLoading\s*=\s*false'
Assert-Match "manual fallback propagates cancellation" ($framePreview + $timelineController) 'TryLoadExactFallbackAsync\([\s\S]{0,220}CancellationToken\s+cancellationToken\s*=\s*default[\s\S]*GetExactNowAsync\([\s\S]{0,120}cancellationToken[\s\S]*CreateLinkedTokenSource\([\s\S]{0,120}cancellationToken'
Assert-Match "preview edit gate blocks playback and loading" $framePreview 'CanMutateCurrentMask\(\)[\s\S]{0,260}_toolPanel\.CanEditWorkspace[\s\S]{0,120}!_isPlaying[\s\S]{0,120}!IsFrameLoading'
Assert-Match "pointer mutation methods use shared edit gate" $framePreview 'OnPointerPressed\([\s\S]{0,160}!CanMutateCurrentMask\(\)[\s\S]*OnPointerMoved\([\s\S]{0,180}!CanMutateCurrentMask\(\)[\s\S]*OnPointerReleased\([\s\S]{0,180}!CanMutateCurrentMask\(\)'
Assert-Match "manual pointer capture stays on input layer" $framePreviewView 'CapturePointerToInputLayer\(sender,\s*e\)[\s\S]*sender\s+is\s+IInputElement\s+inputLayer[\s\S]{0,120}e\.Pointer\.Capture\(inputLayer\)'
Assert-NotMatch "manual pointer capture does not target parent user control" $framePreviewView 'e\.Pointer\.Capture\(this\)'
Assert-Match "workspace exposes blur-radius control" $toolPanelView 'Minimum="\{Binding MinBlurRadius\}"[\s\S]{0,180}Maximum="\{Binding MaxBlurRadius\}"[\s\S]{0,180}Value="\{Binding BlurRadius, Mode=TwoWay\}"'
Assert-Match "workspace edit gate blocks unready session auto and export" $toolPanel 'CanEditWorkspace\s*=>[\s\S]{0,120}IsSessionReady\s*&&\s*!IsExportRunning\s*&&\s*!IsAutoRunning'
Assert-Match "workspace back normalizes logical playback state" $workspace 'GoBack\(\)[\s\S]{0,420}FrameList\.IsPlaying[\s\S]{0,160}FrameList\.NotifyPlaybackStopped\(\)[\s\S]{0,180}StopManualOperationsAndWaitAsync\(\)'

# Timeline, diagnostics, and persistence hardening.
Assert-Match "timeline cache has hard entry bound" $thumbnailProvider '_maxCacheEntries[\s\S]*TrimCacheIfNeeded'
Assert-Match "timeline thumbnails expose direct timestamp seek" $thumbnailProvider 'GetTimelineThumbnailAtTimestampScaled\('
Assert-NotMatch "timeline thumbnails do not invoke exact ordinal extraction" $thumbnailProvider 'GetFrameByIndexScaled\('
Assert-Match "exact provider cancels lifetime before decoder ownership wait" $exactProvider '_lifetimeCts\.Cancel\(\)[\s\S]{0,300}_decodeGate\.Wait\(\)'
Assert-Match "timeline UI binds navigable extent" $frameListView 'TotalDurationSeconds="\{Binding TimelineExtentSeconds\}"'
Assert-Match "timeline UI binds render invalidation version" $frameListView 'RenderVersion="\{Binding TimelineRenderVersion\}"'
Assert-Match "timeline render version participates in AffectsRender" $timelineStrip 'AffectsRender<TimelineFrameStrip>[\s\S]{0,500}RenderVersionProperty'
Assert-NotMatch "frame list does not materialize per-frame view models" $frameList 'Enumerable\.Range|FrameItemViewModel'
Assert-NotMatch "timeline control does not depend on per-frame item collection" $timelineStrip 'ItemsProperty|FrameItemViewModel'
Assert-Match "video session shares extractor for exact and thumbnails" $videoSession 'ExactFrameProvider\(_extractor,\s*ownsExtractor:\s*false\)[\s\S]*TimelineThumbnailProvider\([\s\S]{0,300}_extractor'
Assert-NotMatch "frame list does not create duplicate thumbnail provider" $frameList 'new\s+TimelineThumbnailProvider'
Assert-Match "workspace state persists timeline extent" $workspaceStore 'TimelineExtentSeconds[\s\S]*TimelineExtentSeconds\s*=\s*snapshot\.TimelineExtentSeconds'
Assert-Match "extractor exposes instance hardware failure state" $extractor 'public\s+bool\s+HardwareTransferFailed'
Assert-NotMatch "extractor does not expose process-global last decode diagnostics" $extractor 'GetLastDecode(Status|Error|Diagnostics)|_lastDecode(Status|Error|Diagnostics)'
Assert-NotMatch "home status does not consume global decoder diagnostics" $homeViewModel 'FfFrameExtractor\.GetLastDecode'
Assert-Match "active auto coordinator exposes instance provider diagnostics" ($autoCoordinator + $faceOnnxDetector + $yoloOnnxDetector) 'ExecutionProviderLabel[\s\S]*ExecutionProviderError[\s\S]*FaceOnnxDetector[\s\S]*YoloFaceOnnxDetector'
Assert-Match "dispatcher classifies recoverable UI exceptions explicitly" $app 'IsRecoverableUiException\(e\.Exception\)[\s\S]{0,200}e\.Handled\s*=\s*true'
Assert-NotMatch "dispatcher does not blanket swallow logged UI exceptions" $app 'HandleUnhandledException\(e\.Exception\);\s*e\.Handled\s*=\s*true'

$legacyThumbnailCache = Join-Path $RepoRoot "Services/Video/Session/ThumbnailCache.cs"
if (Test-Path $legacyThumbnailCache) {
    $Failures.Add("legacy ThumbnailCache.cs still exists")
}

if ($Failures.Count -gt 0) {
    $details = ($Failures | ForEach-Object { " - $_" }) -join [Environment]::NewLine
    throw "Runtime hardening verification failed:`n$details"
}

Write-Host "Runtime hardening verification passed."
