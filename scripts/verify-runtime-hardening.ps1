param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
)

$ErrorActionPreference = "Stop"

function Read-RepoFile([string]$relativePath) {
    $path = Join-Path $RepoRoot $relativePath
    if (-not (Test-Path $path)) {
        throw "Required file not found: $relativePath"
    }
    return Get-Content -Raw -Path $path
}

function Assert-Match([string]$label, [string]$text, [string]$pattern) {
    if ($text -notmatch $pattern) {
        throw "FAILED: $label"
    }
}

function Assert-NotMatch([string]$label, [string]$text, [string]$pattern) {
    if ($text -match $pattern) {
        throw "FAILED: $label"
    }
}

$framePreview = Read-RepoFile "ViewModels/Workspace/FramePreviewViewModel.cs"
$workspace = Read-RepoFile "ViewModels/Pages/WorkspaceViewModel.cs"
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
$timelineController = Read-RepoFile "Services/Video/Session/TimelineController.cs"
$exactProvider = Read-RepoFile "Services/Video/Session/ExactFrameProvider.cs"
$frameList = Read-RepoFile "ViewModels/Workspace/FrameListViewModel.cs"
$timelineStrip = Read-RepoFile "Controls/TimelineFrameStrip.cs"
$frameListView = Read-RepoFile "Views/Workspace/FrameListView.axaml"
$app = Read-RepoFile "App.axaml.cs"
$homeViewModel = Read-RepoFile "ViewModels/Pages/HomePageViewModel.cs"
$homeView = Read-RepoFile "Views/Pages/HomePageView.axaml"
$exportService = Read-RepoFile "Services/Video/VideoExportService.cs"
$hdrProbe = Read-RepoFile "Services/Video/VideoHdrProbePolicy.cs"
$videoIoInterrupt = Read-RepoFile "Services/Video/VideoIoInterruptGuard.cs"
$temporalPostProcessor = Read-RepoFile "Services/Analysis/AutoMaskTemporalPostProcessor.cs"
$trackInterpolator = Read-RepoFile "Services/Analysis/FaceTrackInterpolator.cs"
$trackBuilder = Read-RepoFile "Services/Analysis/FaceTrackBuilder.cs"
$exportFidelity = Read-RepoFile "Services/Video/VideoExportFidelityPolicy.cs"

Assert-Match "export input uses cancellable native io guard" ($exportService + $videoIoInterrupt) 'VideoIoInterruptGuard[\s\S]{0,1000}Begin\(cancellationToken\)[\s\S]{0,1800}avformat_open_input[\s\S]{0,1200}avformat_find_stream_info'
Assert-Match "HDR probe accepts cancellation and uses native io guard" ($hdrProbe + $videoIoInterrupt) 'ProbeVideoHdrMetadata\([\s\S]{0,220}CancellationToken\s+cancellationToken[\s\S]{0,1400}VideoIoInterruptGuard[\s\S]{0,1800}avformat_open_input'
Assert-Match "tracked stabilizer is sparse and cancellable" $temporalPostProcessor 'ApplyTrackedBoxStabilization\([\s\S]{0,300}CancellationToken\s+cancellationToken[\s\S]{0,2200}GetFaceMaskFrameIndices\(\)[\s\S]{0,2400}ThrowIfCancellationRequested\(\)'
Assert-NotMatch "tracked stabilizer does not allocate total-frame arrays" $temporalPostProcessor 'ApplyTrackedBoxStabilization\([\s\S]{0,3500}new\s+(List<Rect>\?\[|List<float>\?\[|PixelSize\[|bool\[)totalFrames\]'
Assert-Match "face track interpolator is sparse and cancellable" ($trackInterpolator + $trackBuilder + $temporalPostProcessor + $autoPostProcess) 'FaceTrackInterpolator[\s\S]*CancellationToken\s+cancellationToken\s*=\s*default[\s\S]*SparseFrameMap[\s\S]*builder\.Build\(detectionsByFrame,\s*options,\s*cancellationToken\)[\s\S]*ApplyTemporalFixes\([\s\S]*CancellationToken\s+cancellationToken[\s\S]*cancellationToken:\s*cancellationToken'
Assert-NotMatch "face track interpolator does not allocate total-frame arrays" $trackInterpolator 'new\s+(List<Rect>\?\[|List<float>\?\[|PixelSize\[|bool\[)totalFrames\]'
Assert-NotMatch "face track interpolator does not scan every video frame when rewriting" $trackInterpolator 'for\s*\(int\s+frameIndex\s*=\s*firstMutableFrame;\s*frameIndex\s*<\s*facesByFrame\.Length'
Assert-Match "face track builder checks cancellation while matching tracks" $trackBuilder 'Build\([\s\S]{0,300}CancellationToken\s+cancellationToken[\s\S]{0,1800}cancellationToken\.ThrowIfCancellationRequested\(\)'
Assert-Match "tracked stabilizer blocks the current scene-cut start" $temporalPostProcessor 'blockedSceneCutStarts\?\.Contains\(frameIndex\)'
Assert-NotMatch "tracked stabilizer does not shift scene cut by one frame" $temporalPostProcessor 'blockedSceneCutStarts\?\.Contains\(frameIndex\s*-\s*1\)'
Assert-Match "RGB H264 uses compatibility contract instead of lossless contract" $exportFidelity 'CanEncodeCompatibleX264Rgb'
Assert-NotMatch "RGB quality path no longer claims lossless compatibility" ($exportService + $exportFidelity) 'CanEncodeLosslessX264Rgb|losslessX264RgbConfigured'
Assert-Match "hardware bitrate uses bounded source-relative guardrail" $exportFidelity 'boundedSourceBitrate\s*\*\s*5L\s*/\s*4L'
Assert-Match "single video can use full global PTS frame budget" $extractor 'MaxCachedTimelineFramesPerVideo\s*=\s*1_000_000[\s\S]{0,120}MaxCachedTimelineFramesTotal\s*=\s*1_000_000'
Assert-Match "decoded timeline extent fails closed when exact timestamp seek is unsafe" $extractor 'TryGetDecodedTimelineExtentSeconds\([\s\S]{0,700}!_decodedFrameTimeline\.SupportsExactTimestampSeek'

Assert-Match "playback checks decoder error before normal eof" $framePreview 'SequentialDecodeError[\s\S]*SequentialReachedEndOfStream[\s\S]*onPlaybackFailed'
Assert-NotMatch "playback does not convert any non-cancel stop into natural eof" $framePreview 'endedNaturally\s*\|=\s*!ct\.IsCancellationRequested'
Assert-NotMatch "playback exceptions are not marked natural" $framePreview 'catch\s*\(Exception\s+ex\)[\s\S]{0,300}endedNaturally\s*=\s*true'

Assert-Match "workspace loader attempts backup generation" $workspaceStore 'TryLoadStateFile\(_stateBackupFile\)[\s\S]*TryLoadWorkspacePayload[\s\S]*backupState'
Assert-NotMatch "unreadable masks are not deleted while loading" $workspaceStore 'LoadMask[\s\S]{0,1400}File\.Delete\(path\)'

Assert-Match "timeline cache has a hard entry bound" $thumbnailProvider '_maxCacheEntries[\s\S]*TrimCacheIfNeeded'
Assert-Match "timeline thumbnails expose direct timestamp-seek extraction" $thumbnailProvider 'GetTimelineThumbnailAtTimestampScaled\('
Assert-NotMatch "timeline thumbnails do not invoke exact ordinal extraction" $thumbnailProvider 'GetFrameByIndexScaled\('
Assert-Match "timeline thumbnail extractor seeks by presentation time" $extractor 'GetTimelineThumbnailAtTimestampScaled[\s\S]{0,9000}SeekMainDecoder\(targetPts,\s*cancellationToken\)'
Assert-Match "timeline viewport requests pass cancellation to thumbnail decode" ($thumbnailProvider + $timelineStrip) 'GetThumbnailAtTime\([\s\S]{0,240}token'
Assert-NotMatch "timeline controller does not issue uncancellable exact requests" $timelineController 'CancellationToken\.None'
Assert-Match "exact provider cancels lifetime before decoder ownership wait" $exactProvider '_lifetimeCts\.Cancel\(\)[\s\S]{0,300}_decodeGate\.Wait\(\)'
Assert-Match "timeline UI prefers cached decoded PTS mapping" ($frameList + $timelineStrip) 'TryGetFrameTimestampSeconds[\s\S]*TryGetFrameIndexAtTimestamp'
Assert-Match "timeline UI binds navigable timeline extent" $frameListView 'TotalDurationSeconds="\{Binding TimelineExtentSeconds\}"'
Assert-Match "timeline UI binds explicit render invalidation version" $frameListView 'RenderVersion="\{Binding TimelineRenderVersion\}"'
Assert-Match "timeline render version participates in AffectsRender" $timelineStrip 'AffectsRender<TimelineFrameStrip>[\s\S]{0,500}RenderVersionProperty'
Assert-Match "frame list models unknown duration explicitly" $frameList 'IsDurationKnown[\s\S]{0,2500}TimelineExtentSeconds'
Assert-NotMatch "unknown duration is not synthesized from average fps" $frameList 'TotalDurationSeconds\s*=\s*[^;]*(TotalFrames\s*/\s*Fps|fpsValue)'
Assert-Match "timestamp thumbnails share decoded timeline origin" $extractor 'GetTimelineThumbnailAtTimestampScaled[\s\S]{0,2200}TryGetTimelineOriginPresentationTimestamp'
Assert-NotMatch "timestamp thumbnails do not use stream start_time as timeline origin" $extractor 'GetTimelineThumbnailAtTimestampScaled[\s\S]{0,1800}stream->start_time'
Assert-Match "timestamp thumbnail checks cancellation after packet read" $extractor 'ReadFrameInterruptibly\([\s\S]{0,180}_fmt[\s\S]{0,180}packet[\s\S]{0,180}cancellationToken[\s\S]{0,1400}cancellationToken\.IsCancellationRequested[\s\S]{0,1400}avcodec_send_packet\(_dec, packet\)'
Assert-Match "blocking frame reads use the shared AVIO interrupt guard" ($extractor + $videoIoInterrupt) 'VideoIoInterruptGuard[\s\S]*ConfigureIoInterrupt\([\s\S]*_ioInterrupt\.Configure\(format\)[\s\S]*BeginIoInterrupt\([\s\S]*_ioInterrupt\.Begin\(cancellationToken\)'
Assert-NotMatch "extractor no longer owns a duplicate AVIO interrupt callback" $extractor '_ioInterruptHandle|_ioInterruptRequested|HandleIoInterrupt|IoInterruptCallback'
Assert-Match "ffmpeg input open installs interrupt callback before open" $extractor 'avformat_alloc_context\(\)[\s\S]{0,900}ConfigureIoInterrupt\(_fmt\)[\s\S]{0,900}BeginIoInterrupt\(cancellationToken\)[\s\S]{0,900}avformat_open_input'
Assert-Match "shared metadata reader installs interrupt callback before open" $metadataReader 'avformat_alloc_context\(\)[\s\S]{0,1800}interrupt_callback\.callback\s*=\s*InterruptCallback[\s\S]{0,1800}avformat_open_input'
Assert-Match "shared metadata reader registers cancellation for native io" $metadataReader 'CancellationTokenRegistration\s+registration[\s\S]{0,1800}cancellationToken\.Register\([\s\S]{0,900}Volatile\.Write\(ref\s+interrupt\.Requested,\s*1\)'
Assert-Match "shared metadata reader checks cancellation after open and stream info" $metadataReader 'avformat_open_input[\s\S]{0,500}ThrowIfCancellationRequested\(\)[\s\S]{0,900}avformat_find_stream_info[\s\S]{0,500}ThrowIfCancellationRequested\(\)'
Assert-Match "auto metadata probe uses shared cancellable reader" $autoMask 'ThrowIfCancellationRequested\(\)[\s\S]{0,500}VideoMetadataReader\.Read\(videoPath,\s*ct\)'
Assert-NotMatch "auto mask no longer performs raw metadata open" $autoMask 'avformat_(open_input|find_stream_info)'
Assert-Match "frame analyzer metadata probe uses shared cancellable reader" $frameAnalyzer 'VideoMetadataReader\.Read\(videoPath,\s*ct\)'
Assert-Match "frame analyzer prefers decoded PTS over average FPS" $frameAnalyzer 'TryGetCachedFrameTimestampSeconds\([\s\S]{0,240}decodedTimestampSec[\s\S]{0,240}\?\s*decodedTimestampSec\s*:\s*idx\s*/\s*fps'
Assert-NotMatch "frame analyzer no longer assigns average FPS time unconditionally" $frameAnalyzer 'TimestampSec\s*=\s*idx\s*/\s*fps'
Assert-NotMatch "frame analyzer no longer performs raw metadata open" $frameAnalyzer 'avformat_(open_input|find_stream_info)'
Assert-Match "frame list metadata probe accepts workspace cancellation" $frameList 'FrameListViewModel\([\s\S]{0,300}CancellationToken\s+cancellationToken[\s\S]{0,900}VideoMetadataReader\.Read\(path,\s*cancellationToken\)'
Assert-NotMatch "frame list no longer performs raw metadata open" $frameList 'avformat_(open_input|find_stream_info)'
Assert-Match "ffmpeg stream info uses cancellable native io scope" $extractor 'BeginIoInterrupt\(cancellationToken\)[\s\S]{0,800}avformat_find_stream_info\(_fmt'
Assert-Match "ffmpeg seeks use cancellable native io scope" $extractor 'SeekMainDecoder\([\s\S]{0,1200}BeginIoInterrupt\(cancellationToken\)[\s\S]{0,800}av_seek_frame'
Assert-Match "ordinal input open and stream info use cancellation" $extractor 'EnsureOrdinalDecoderInitialized\([\s\S]{0,2200}BeginIoInterrupt\(cancellationToken\)[\s\S]{0,1000}avformat_open_input[\s\S]{0,1400}avformat_find_stream_info'
Assert-Match "frame ordinal cancellation is not recorded as permanent decoder failure" $extractor 'EnsureDecodedFrameTimelineThrough\([\s\S]{0,7000}catch\s*\(OperationCanceledException\)\s*when\s*\(cancellationToken\.IsCancellationRequested\)[\s\S]{0,300}DisposeOrdinalDecoder\(\);[\s\S]{0,200}catch\s*\(Exception\s+ex\)[\s\S]{0,160}_ordinalDecoderFailed\s*=\s*true'
Assert-Match "timestamp ordinal cancellation is not recorded as permanent decoder failure" $extractor 'EnsureDecodedFrameTimelineThroughTimestamp\([\s\S]{0,7000}catch\s*\(OperationCanceledException\)\s*when\s*\(cancellationToken\.IsCancellationRequested\)[\s\S]{0,300}DisposeOrdinalDecoder\(\);[\s\S]{0,200}catch\s*\(Exception\s+ex\)[\s\S]{0,160}_ordinalDecoderFailed\s*=\s*true'
Assert-Match "auto ROI pipeline propagates cancellation" $autoPostProcess 'AutoMaskRoiRefineStep\(\)\.Apply\([\s\S]{0,700}useFaceOnnxRoiDetector,\s*cancellationToken\)'
Assert-Match "auto ROI step forwards cancellation to refiner" $roiStep 'CancellationToken\s+cancellationToken\s*=\s*default[\s\S]{0,1500}FaceTrackRoiRefiner\(\)\.Apply\([\s\S]{0,500}cancellationToken:\s*cancellationToken'
Assert-Match "ROI refiner cancels ffmpeg open seek and read" $roiRefiner 'new FfFrameExtractor\([\s\S]{0,300}cancellationToken:\s*cancellationToken[\s\S]{0,1800}StartSequentialRead\([\s\S]{0,200}cancellationToken[\s\S]{0,1200}TryGetNextFrameRawToBuffer\(\s*cancellationToken'
Assert-NotMatch "ROI refiner does not use uncancellable frame reads" $roiRefiner 'TryGetNextFrameRawToBuffer\(\s*CancellationToken\.None'
Assert-Match "ROI refiner checks cancellation immediately around synchronous detector inference" $roiRefiner 'ThrowIfCancellationRequested\(\);[\s\S]{0,350}DetectFacesBgra\([\s\S]{0,350}ThrowIfCancellationRequested\(\);'
Assert-Match "playback restart waits for the prior playback task" $framePreview 'StartPlaybackAfterPreviousAsync[\s\S]{0,1400}await previousPlaybackTask\.ConfigureAwait\(false\)[\s\S]{0,1400}RunSequentialPlaybackAsync'
Assert-Match "playback passes cancellation into ffmpeg initialization" $framePreview 'new FfFrameExtractor\([\s\S]{0,300}cancellationToken:\s*ct[\s\S]{0,300}StartSequentialRead\(startFrameIndex,\s*ct\)'
Assert-Match "issue list resolves display time from decoded PTS" $workspace 'ResetIssueList[\s\S]{0,1800}TryGetFrameTimestampSeconds[\s\S]{0,9000}TryResolveFrameTimestampSeconds'
Assert-Match "issue time refresh does not start while timeline operations are suspended" $workspace 'RefreshIssueTimesInBackground\([\s\S]{0,800}provider\.OperationsSuspended'
Assert-Match "auto resumes issue time refresh after timeline operations resume" $workspace 'FrameList\.ResumeTimelineOperations\(\)[\s\S]{0,500}RefreshIssueTimesInBackground\(FrameList\.SelectedFrameIndex\)'
Assert-Match "issue time uncached resolution is bounded near the selected frame" $workspace 'IssueTimeResolveBudget\s*=\s*96[\s\S]*nearFrameDistance[\s\S]*Take\(IssueTimeResolveBudget\)'
Assert-Match "issue time cache probing is bounded around the navigation anchor" $workspace 'IssueTimeCacheProbeBudget\s*=\s*192[\s\S]*CollectIssueEntriesNearAnchor\([\s\S]{0,300}IssueTimeCacheProbeBudget[\s\S]*AddIssueEntriesNearAnchor'
Assert-Match "auto anomaly navigation refreshes nearby issue timestamps" $workspace 'JumpAutoAnomaly\([\s\S]{0,1400}RefreshIssueTimesInBackground\(targetFrame\)[\s\S]{0,1200}ReviewAutoAnomalies\([\s\S]{0,500}RefreshIssueTimesInBackground\(targetFrame\)'
Assert-NotMatch "background issue refresh does not concatenate every issue collection" $workspace 'RefreshIssueTimesAsync\([\s\S]{0,1800}_noFaceIssueEntries\s*\.Concat\('
Assert-NotMatch "issue list no longer formats time from frame index divided by fps" $workspace 'FormatFrameTime\(|frameIndex\s*/\s*framesPerSecond'
Assert-Match "issue warmup scan is bounded around the center anchor" $timelineStrip 'neighborCount\s*=\s*64[\s\S]{0,800}FindFirstIndexAtOrAfter\(frames, anchorFrame\)'
Assert-Match "decoded PTS cache tracks live owners" $extractor 'LiveOwnerCount[\s\S]*ReleaseDecodedFrameTimeline'
Assert-Match "decoded PTS cache enforces global resident budget" $extractor 'GetResidentTimelineFrameCountLocked\(\)\s*>?=\s*MaxCachedTimelineFramesTotal'
Assert-NotMatch "frame list does not materialize per-frame view models" $frameList 'Enumerable\.Range|FrameItemViewModel'
Assert-NotMatch "timeline control no longer depends on per-frame item collection" $timelineStrip 'ItemsProperty|FrameItemViewModel'
Assert-Match "video session shares one extractor for exact and thumbnail providers" $videoSession 'ExactFrameProvider\(_extractor, ownsExtractor: false\)[\s\S]*TimelineThumbnailProvider\([\s\S]{0,300}_extractor'
Assert-NotMatch "frame list no longer creates a duplicate thumbnail provider" $frameList 'new\s+TimelineThumbnailProvider'
Assert-Match "workspace attaches session-owned thumbnail provider" $workspace 'FrameList\.SetThumbnailProvider\(session\.ThumbnailProvider\)'
Assert-Match "workspace initialization passes cancellation into metadata and session open" $workspace 'initializationToken\.ThrowIfCancellationRequested\(\)[\s\S]{0,500}new FrameListViewModel\([\s\S]{0,200}initializationToken[\s\S]{0,500}InitializeSession\(loadProgress,\s*initializationToken\)'
Assert-Match "async session initialization links external cancellation" $workspace 'EnsureSessionInitializedAsync\([\s\S]{0,600}CreateLinkedTokenSource\([\s\S]{0,120}cancellationToken[\s\S]{0,1500}cancellationToken\.IsCancellationRequested[\s\S]{0,100}throw'
Assert-Match "home workspace loading owns a cancellable token" $homeViewModel '_workspaceLoadCts[\s\S]*BeginWorkspaceLoad\([\s\S]*GetOrCreateWorkspace\([\s\S]{0,800}loadCts\.Token'
Assert-Match "home workspace loading exposes cancellation command" ($homeViewModel + $homeView) 'CancelWorkspaceLoading[\s\S]*CancelWorkspaceLoadingCommand'
Assert-NotMatch "legacy thumbnail cache is no longer wired into video session" ($videoSession + $timelineController) '\bThumbnailCache\b'

$legacyThumbnailCache = Join-Path $RepoRoot "Services/Video/Session/ThumbnailCache.cs"
if (Test-Path $legacyThumbnailCache) {
    throw "FAILED: legacy ThumbnailCache.cs still exists"
}

Assert-Match "extractor exposes instance hardware failure state" $extractor 'public\s+bool\s+HardwareTransferFailed'
Assert-Match "sequential decode error uses instance detail" $extractor 'string\?\s+detail\s*=\s*_decodeError'
Assert-NotMatch "sequential decode error does not consume global last decoder error" $extractor 'string\?\s+detail\s*=\s*GetLastDecodeError\(\)'
Assert-Match "auto mask fallback checks the current extractor" $autoMask 'IsHardwareTransferFailure\(extractor\)'
Assert-NotMatch "extractor does not expose process-global last decode diagnostics" $extractor 'GetLastDecode(Status|Error|Diagnostics)|_lastDecode(Status|Error|Diagnostics)'
Assert-Match "static hardware callback routes diagnostics to extractor owner" $extractor '_hwOwnerByDecoder[\s\S]*owner\?\.UpdateDecodeStatus'
Assert-NotMatch "home status does not consume global decoder diagnostics" $homeViewModel 'FfFrameExtractor\.GetLastDecode'
Assert-Match "dispatcher classifies recoverable UI exceptions explicitly" $app 'IsRecoverableUiException\(e\.Exception\)[\s\S]{0,200}e\.Handled\s*=\s*true'
Assert-NotMatch "dispatcher does not blanket swallow logged UI exceptions" $app 'HandleUnhandledException\(e\.Exception\);\s*e\.Handled\s*=\s*true'

Assert-Match "workspace defers resource disposal until active operations drain" $workspace '_activeLifetimeOperations[\s\S]*_disposeRequested[\s\S]*ScheduleOwnedResourceDispose'
Assert-Match "auto run enters workspace lifetime gate" $workspace 'RunAutoAsync[\s\S]{0,1000}TryBeginLifetimeOperation'
Assert-Match "export enters workspace lifetime gate" $workspace 'SaveVideoAsync[\s\S]{0,1200}TryBeginLifetimeOperation'

Assert-Match "workspace carries explicit overwrite policy" $workspace 'allowOutputOverwrite'
Assert-Match "save as unique path never falls back to original after numeric exhaustion" $workspace 'Guid\.NewGuid\(\)[\s\S]*고유한 내보내기 파일명'
Assert-Match "export non-overwrite commit uses atomic move semantics" $exportService '!allowOutputOverwrite[\s\S]*File\.Move\(stagedOutputPath,\s*finalOutputPath,\s*overwrite:\s*false\)'

Write-Host "Runtime hardening verification passed."
