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
$exportService = Read-RepoFile "Services/Video/VideoExportService.cs"

Assert-Match "playback checks decoder error before normal eof" $framePreview 'SequentialDecodeError[\s\S]*SequentialReachedEndOfStream[\s\S]*onPlaybackFailed'
Assert-NotMatch "playback does not convert any non-cancel stop into natural eof" $framePreview 'endedNaturally\s*\|=\s*!ct\.IsCancellationRequested'
Assert-NotMatch "playback exceptions are not marked natural" $framePreview 'catch\s*\(Exception\s+ex\)[\s\S]{0,300}endedNaturally\s*=\s*true'

Assert-Match "workspace loader attempts backup generation" $workspaceStore 'TryLoadStateFile\(_stateBackupFile\)[\s\S]*TryLoadWorkspacePayload[\s\S]*backupState'
Assert-NotMatch "unreadable masks are not deleted while loading" $workspaceStore 'LoadMask[\s\S]{0,1400}File\.Delete\(path\)'

Assert-Match "timeline cache has a hard entry bound" $thumbnailProvider '_maxCacheEntries[\s\S]*TrimCacheIfNeeded'
Assert-Match "timeline thumbnails use bounded timestamp-seek extraction" $thumbnailProvider 'GetTimelineThumbnailByFrameIndexScaled\('
Assert-NotMatch "timeline thumbnails do not invoke exact ordinal extraction" $thumbnailProvider 'GetFrameByIndexScaled\('
Assert-Match "timeline thumbnail extractor seeks by presentation time" $extractor 'GetTimelineThumbnailByFrameIndexScaled[\s\S]{0,9000}SeekMainDecoder\(targetPts\)'
Assert-NotMatch "timeline thumbnail selection is not average-fps ordinal seek" $thumbnailProvider 'frameIndex\s*/\s*_fps'
Assert-NotMatch "legacy thumbnail cache is no longer wired into video session" ($videoSession + $timelineController) '\bThumbnailCache\b'

$legacyThumbnailCache = Join-Path $RepoRoot "Services/Video/Session/ThumbnailCache.cs"
if (Test-Path $legacyThumbnailCache) {
    throw "FAILED: legacy ThumbnailCache.cs still exists"
}

Assert-Match "extractor exposes instance hardware failure state" $extractor 'public\s+bool\s+HardwareTransferFailed'
Assert-Match "sequential decode error uses instance detail" $extractor 'string\?\s+detail\s*=\s*_decodeError'
Assert-NotMatch "sequential decode error does not consume global last decoder error" $extractor 'string\?\s+detail\s*=\s*GetLastDecodeError\(\)'
Assert-Match "auto mask fallback checks the current extractor" $autoMask 'IsHardwareTransferFailure\(extractor\)'

Assert-Match "workspace defers resource disposal until active operations drain" $workspace '_activeLifetimeOperations[\s\S]*_disposeRequested[\s\S]*ScheduleOwnedResourceDispose'
Assert-Match "auto run enters workspace lifetime gate" $workspace 'RunAutoAsync[\s\S]{0,1000}TryBeginLifetimeOperation'
Assert-Match "export enters workspace lifetime gate" $workspace 'SaveVideoAsync[\s\S]{0,1200}TryBeginLifetimeOperation'

Assert-Match "workspace carries explicit overwrite policy" $workspace 'allowOutputOverwrite'
Assert-Match "save as unique path never falls back to original after numeric exhaustion" $workspace 'Guid\.NewGuid\(\)[\s\S]*고유한 내보내기 파일명'
Assert-Match "export non-overwrite commit uses atomic move semantics" $exportService '!allowOutputOverwrite[\s\S]*File\.Move\(stagedOutputPath,\s*finalOutputPath,\s*overwrite:\s*false\)'

Write-Host "Runtime hardening verification passed."
