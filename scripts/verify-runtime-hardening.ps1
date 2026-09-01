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
$autoMask = Read-RepoFile "Services/Analysis/AutoMaskGenerator.cs"
$thumbnailProvider = Read-RepoFile "Services/Video/TimelineThumbnailProvider.cs"
$videoSession = Read-RepoFile "Services/Video/Session/VideoSession.cs"
$timelineController = Read-RepoFile "Services/Video/Session/TimelineController.cs"
$exactProvider = Read-RepoFile "Services/Video/Session/ExactFrameProvider.cs"
$frameList = Read-RepoFile "ViewModels/Workspace/FrameListViewModel.cs"
$timelineStrip = Read-RepoFile "Controls/TimelineFrameStrip.cs"
$frameListView = Read-RepoFile "Views/Workspace/FrameListView.axaml"
$app = Read-RepoFile "App.axaml.cs"
$home = Read-RepoFile "ViewModels/Pages/HomePageViewModel.cs"
$exportService = Read-RepoFile "Services/Video/VideoExportService.cs"

Assert-Match "playback checks decoder error before normal eof" $framePreview 'SequentialDecodeError[\s\S]*SequentialReachedEndOfStream[\s\S]*onPlaybackFailed'
Assert-NotMatch "playback does not convert any non-cancel stop into natural eof" $framePreview 'endedNaturally\s*\|=\s*!ct\.IsCancellationRequested'
Assert-NotMatch "playback exceptions are not marked natural" $framePreview 'catch\s*\(Exception\s+ex\)[\s\S]{0,300}endedNaturally\s*=\s*true'

Assert-Match "workspace loader attempts backup generation" $workspaceStore 'TryLoadStateFile\(_stateBackupFile\)[\s\S]*TryLoadWorkspacePayload[\s\S]*backupState'
Assert-NotMatch "unreadable masks are not deleted while loading" $workspaceStore 'LoadMask[\s\S]{0,1400}File\.Delete\(path\)'

Assert-Match "timeline cache has a hard entry bound" $thumbnailProvider '_maxCacheEntries[\s\S]*TrimCacheIfNeeded'
Assert-Match "timeline thumbnails expose direct timestamp-seek extraction" $thumbnailProvider 'GetTimelineThumbnailAtTimestampScaled\('
Assert-NotMatch "timeline thumbnails do not invoke exact ordinal extraction" $thumbnailProvider 'GetFrameByIndexScaled\('
Assert-Match "timeline thumbnail extractor seeks by presentation time" $extractor 'GetTimelineThumbnailAtTimestampScaled[\s\S]{0,9000}SeekMainDecoder\(targetPts\)'
Assert-Match "timeline viewport requests pass cancellation to thumbnail decode" ($thumbnailProvider + $timelineStrip) 'GetThumbnailAtTime\([\s\S]{0,240}token'
Assert-NotMatch "timeline controller does not issue uncancellable exact requests" $timelineController 'CancellationToken\.None'
Assert-Match "exact provider cancels lifetime before decoder ownership wait" $exactProvider '_lifetimeCts\.Cancel\(\)[\s\S]{0,300}_decodeGate\.Wait\(\)'
Assert-Match "timeline UI prefers cached decoded PTS mapping" ($frameList + $timelineStrip) 'TryGetFrameTimestampSeconds[\s\S]*TryGetFrameIndexAtTimestamp'
Assert-Match "timeline UI binds source duration explicitly" $frameListView 'TotalDurationSeconds="\{Binding TotalDurationSeconds\}"'
Assert-NotMatch "frame list does not materialize per-frame view models" $frameList 'Enumerable\.Range|FrameItemViewModel'
Assert-NotMatch "timeline control no longer depends on per-frame item collection" $timelineStrip 'ItemsProperty|FrameItemViewModel'
Assert-Match "video session shares one extractor for exact and thumbnail providers" $videoSession 'ExactFrameProvider\(_extractor, ownsExtractor: false\)[\s\S]*TimelineThumbnailProvider\([\s\S]{0,300}_extractor'
Assert-NotMatch "frame list no longer creates a duplicate thumbnail provider" $frameList 'new\s+TimelineThumbnailProvider'
Assert-Match "workspace attaches session-owned thumbnail provider" $workspace 'FrameList\.SetThumbnailProvider\(session\.ThumbnailProvider\)'
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
Assert-NotMatch "home status does not consume global decoder diagnostics" $home 'FfFrameExtractor\.GetLastDecode'
Assert-Match "dispatcher classifies recoverable UI exceptions explicitly" $app 'IsRecoverableUiException\(e\.Exception\)[\s\S]{0,200}e\.Handled\s*=\s*true'
Assert-NotMatch "dispatcher does not blanket swallow logged UI exceptions" $app 'HandleUnhandledException\(e\.Exception\);\s*e\.Handled\s*=\s*true'

Assert-Match "workspace defers resource disposal until active operations drain" $workspace '_activeLifetimeOperations[\s\S]*_disposeRequested[\s\S]*ScheduleOwnedResourceDispose'
Assert-Match "auto run enters workspace lifetime gate" $workspace 'RunAutoAsync[\s\S]{0,1000}TryBeginLifetimeOperation'
Assert-Match "export enters workspace lifetime gate" $workspace 'SaveVideoAsync[\s\S]{0,1200}TryBeginLifetimeOperation'

Assert-Match "workspace carries explicit overwrite policy" $workspace 'allowOutputOverwrite'
Assert-Match "save as unique path never falls back to original after numeric exhaustion" $workspace 'Guid\.NewGuid\(\)[\s\S]*고유한 내보내기 파일명'
Assert-Match "export non-overwrite commit uses atomic move semantics" $exportService '!allowOutputOverwrite[\s\S]*File\.Move\(stagedOutputPath,\s*finalOutputPath,\s*overwrite:\s*false\)'

Write-Host "Runtime hardening verification passed."
