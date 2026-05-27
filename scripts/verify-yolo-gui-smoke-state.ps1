param(
    [string]$ChecklistCsv = ".tmp\yolo-gui-smoke\manual-smoke-checklist.csv",
    [string]$HomeViewModel = "ViewModels\Pages\HomePageViewModel.cs",
    [string]$HomeView = "Views\Pages\HomePageView.axaml",
    [string]$MainWindowViewModel = "ViewModels\MainWindowViewModel.cs",
    [string]$AppCodeBehind = "App.axaml.cs",
    [string]$AppStartupOptions = "Models\AppStartupOptions.cs",
    [string]$WorkspaceViewModel = "ViewModels\Pages\WorkspaceViewModel.cs",
    [string]$FramePreviewViewModel = "ViewModels\Workspace\FramePreviewViewModel.cs",
    [string]$ToolPanelViewModel = "ViewModels\Workspace\ToolPanelViewModel.cs",
    [string]$StateStore = "Services\Workspace\WorkspaceStateStore.cs",
    [string]$FrameMaskProvider = "Services\Video\FrameMaskProvider.cs",
    [string]$ExactFrameProvider = "Services\Video\Session\ExactFrameProvider.cs",
    [string]$TimelineController = "Services\Video\Session\TimelineController.cs",
    [string]$VideoSession = "Services\Video\Session\VideoSession.cs",
    [string]$TimelineFrameStrip = "Controls\TimelineFrameStrip.cs",
    [string]$TimelineThumbnailProvider = "Services\Video\TimelineThumbnailProvider.cs",
    [switch]$RequireManualPass,
    [switch]$AllowPartialManualPass,
    [switch]$SelfTest
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
        throw "Required file not found: $resolved"
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

    Write-Host "[YoloGuiSmokeVerify] pass $Name"
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

    Write-Host "[YoloGuiSmokeVerify] pass $Name"
}

function Get-AllowedArtifactExtensions {
    param([string]$EvidenceType)

    switch ($EvidenceType) {
        "screenshot" { return @(".png", ".jpg", ".jpeg", ".webp", ".bmp") }
        "screenshot-or-recording" { return @(".png", ".jpg", ".jpeg", ".webp", ".bmp", ".mp4", ".mov", ".mkv", ".avi", ".webm") }
        "recording" { return @(".mp4", ".mov", ".mkv", ".avi", ".webm") }
        "screenshot-or-log" { return @(".png", ".jpg", ".jpeg", ".webp", ".bmp", ".log", ".txt") }
        "output-file" { return @(".mp4", ".mov", ".mkv", ".avi", ".webm") }
        default { throw "Unsupported evidenceType: $EvidenceType" }
    }
}

function Assert-ArtifactMatchesEvidenceType {
    param(
        [string]$Step,
        [string]$EvidenceType,
        [string]$ArtifactPath
    )

    $artifactInfo = Get-Item $ArtifactPath
    if ($artifactInfo -isnot [IO.FileInfo]) {
        throw "Manual GUI smoke artifactPath is not a file: $Step artifactPath=$ArtifactPath"
    }

    if ($artifactInfo.Length -le 0) {
        throw "Manual GUI smoke artifactPath is empty: $Step artifactPath=$ArtifactPath"
    }

    $extension = $artifactInfo.Extension.ToLowerInvariant()
    $allowed = Get-AllowedArtifactExtensions $EvidenceType
    if ($extension -notin $allowed) {
        throw "Manual GUI smoke artifactPath extension does not match evidenceType: $Step evidenceType=$EvidenceType extension=$extension allowed=$($allowed -join ',')"
    }
}

function Assert-Throws {
    param(
        [string]$Name,
        [scriptblock]$Action,
        [string]$ExpectedText
    )

    try {
        & $Action
    }
    catch {
        $message = $_.Exception.Message
        if (-not [string]::IsNullOrWhiteSpace($ExpectedText) -and $message -notlike "*$ExpectedText*") {
            throw "$Name threw an unexpected error. Expected text '$ExpectedText', actual '$message'"
        }

        Write-Host "[YoloGuiSmokeVerify] pass negative selftest $Name"
        return
    }

    throw "$Name did not throw"
}

function Assert-ManualChecklist {
    param(
        [string]$Path,
        [string[]]$Steps,
        [hashtable]$EvidenceTypes,
        [switch]$AllowPartial
    )

    $resolvedChecklist = Resolve-RepoPath $Path
    if (-not (Test-Path $resolvedChecklist)) {
        throw "Manual GUI smoke checklist not found: $resolvedChecklist"
    }

    $rows = @(Import-Csv $resolvedChecklist)
    foreach ($step in $Steps) {
        $row = $rows | Where-Object { $_.stepId -eq $step } | Select-Object -First 1
        if ($null -eq $row) {
            throw "Manual GUI smoke checklist missing step: $step"
        }

        foreach ($column in @("status", "evidenceType", "artifactPath", "evidence", "notes")) {
            if ($null -eq $row.PSObject.Properties[$column]) {
                throw "Manual GUI smoke checklist row missing column '$column': $step"
            }
        }

        $status = $row.status.Trim().ToLowerInvariant()
        if ([string]::IsNullOrWhiteSpace($status) -and $AllowPartial) {
            continue
        }

        if ($status -ne "pass") {
            throw "Manual GUI smoke step is not pass: $step status=$($row.status)"
        }

        $evidenceType = $row.evidenceType.Trim().ToLowerInvariant()
        if ($evidenceType -notin $EvidenceTypes[$step]) {
            throw "Manual GUI smoke step has unsupported evidenceType: $step evidenceType=$($row.evidenceType)"
        }

        if ([string]::IsNullOrWhiteSpace($row.evidence)) {
            throw "Manual GUI smoke step is missing evidence: $step"
        }

        if ([string]::IsNullOrWhiteSpace($row.artifactPath)) {
            throw "Manual GUI smoke step is missing artifactPath: $step"
        }

        $artifactPath = Resolve-RepoPath $row.artifactPath
        if (-not (Test-Path $artifactPath)) {
            throw "Manual GUI smoke artifactPath does not exist: $step artifactPath=$artifactPath"
        }

        Assert-ArtifactMatchesEvidenceType $step $evidenceType $artifactPath
    }
}

$homeText = Read-RepoFile $HomeViewModel
$homeViewText = Read-RepoFile $HomeView
$mainWindowText = Read-RepoFile $MainWindowViewModel
$appText = Read-RepoFile $AppCodeBehind
$startupOptionsText = Read-RepoFile $AppStartupOptions
$workspaceText = Read-RepoFile $WorkspaceViewModel
$framePreviewText = Read-RepoFile $FramePreviewViewModel
$toolPanelText = Read-RepoFile $ToolPanelViewModel
$stateText = Read-RepoFile $StateStore
$frameMaskProviderText = Read-RepoFile $FrameMaskProvider
$exactFrameProviderText = Read-RepoFile $ExactFrameProvider
$timelineControllerText = Read-RepoFile $TimelineController
$videoSessionText = Read-RepoFile $VideoSession
$timelineFrameStripText = Read-RepoFile $TimelineFrameStrip
$timelineThumbnailProviderText = Read-RepoFile $TimelineThumbnailProvider

Assert-Match "home exposes open workflow" $homeText "StartWorkspace|OpenVideo|PickVideo"
Assert-Match "home exposes yolo backend selector" $homeText "YOLO Face ONNX[\s\S]*FaceDetectorBackend\.YoloFaceOnnx"
Assert-Match "home exposes yolo model path" $homeText "AutoYoloModelPath"
Assert-Match "home view exposes yolo picker" $homeViewText "PickYoloModel_Click"
Assert-Match "home view exposes yolo downloader in detector row" $homeViewText "SelectedItem=""\{Binding SelectedAutoDetectorBackendOption\}""[\s\S]*DownloadYoloModelCommand[\s\S]*IsVisible=""\{Binding IsYoloDetectorSelected\}"""
Assert-Match "home view binds yolo download progress" $homeViewText "YoloModelDownloadProgress"
Assert-Match "home view widens yolo input numeric control" $homeViewText 'NumericUpDown\s+Width="128"[\s\S]*AutoYoloInputSize'
Assert-Match "home view widens yolo tile numeric controls" $homeViewText 'NumericUpDown\s+Width="92"[\s\S]*AutoYoloTileColumns[\s\S]*NumericUpDown\s+Width="92"[\s\S]*AutoYoloTileRows'
Assert-Match "home downloads yolo model" $homeText "DownloadYoloModelAsync[\s\S]*YoloModelDownloadStatus"
Assert-Match "home stores downloaded yolo model outside repo" $homeText "GetYoloModelDownloadDirectory[\s\S]*LocalApplicationData[\s\S]*FaceShield"
Assert-Match "home applies startup smoke options" $homeText "ApplyStartupOptions[\s\S]*SelectedAutoDetectorBackendOption[\s\S]*SelectedYoloModelTypeOption[\s\S]*AutoYoloModelPath[\s\S]*SelectedVideoPath"
Assert-Match "home applies startup auto export option" $homeText "ApplyStartupOptions[\s\S]*AutoExportAfter"
Assert-Match "app forwards desktop startup args" $appText "new MainWindowViewModel\(desktop\.Args\)"
Assert-Match "main window supports startup manual open" $mainWindowText "OpenStartupWorkspaceAsync[\s\S]*OpenManualWorkspaceCommand\.ExecuteAsync"
Assert-Match "main window supports startup auto open" $mainWindowText "OpenStartupWorkspaceAsync[\s\S]*OpenAutoWorkspaceCommand\.ExecuteAsync"
Assert-Match "main window applies startup frame after workspace open" $mainWindowText "_startupFrameIndex[\s\S]*CurrentPage\s+is\s+WorkspaceViewModel[\s\S]*SelectedFrameIndex\s*=\s*Math\.Clamp"
Assert-Contains "startup options support yolo smoke preset" $startupOptionsText "--yolo-smoke"
Assert-Contains "startup options use srcTest smoke video" $startupOptionsText "srcTest/260102_jp_10.mp4"
Assert-Contains "startup options use local yolo smoke model" $startupOptionsText ".tmp/models/YoloV5Face.onnx"
Assert-Contains "startup options support auto open" $startupOptionsText "--open-auto"
Assert-Contains "startup options support no auto export" $startupOptionsText "--no-auto-export"
Assert-Contains "startup options support frame selection" $startupOptionsText "--frame"
Assert-Match "workspace runs auto detect" $workspaceText "RunAutoMaskAsync|RunAutoAsync"
Assert-Match "workspace creates yolo detector factory" $workspaceText "FaceDetectorFactory"
Assert-Match "workspace exports video" $workspaceText "VideoExportService[\s\S]*Export"
Assert-Match "workspace persists state" $workspaceText "PersistWorkspaceState"
Assert-Match "workspace marks detection complete before export cancel path" $workspaceText '_autoCompleted\s*=\s*true;\s*_autoResumeIndex\s*=\s*0;[\s\S]*if\s*\(exportAfter\)[\s\S]*SaveVideoAsync'
Assert-Match "workspace keeps completed detection state when export is canceled" $workspaceText 'if\s*\(!exported\)[\s\S]*PersistWorkspaceState\(includePreviewMask:\s*false\)[\s\S]*return\s+false;'
Assert-Match "workspace preserves completed detection state on export cancel exception" $workspaceText 'bool\s+detectionCompleted\s*=\s*false;[\s\S]*detectionCompleted\s*=\s*true;[\s\S]*catch\s*\(OperationCanceledException\)[\s\S]*_autoCompleted\s*=\s*detectionCompleted;[\s\S]*if\s*\(detectionCompleted\)[\s\S]*_autoResumeIndex\s*=\s*0;'
Assert-Match "workspace clears stale resume face masks before auto rerun" $workspaceText 'ResetAutoFaceMasksForRun\(lastProcessed\);[\s\S]*RemoveFaceMasksFrom\(startFrameIndex\)[\s\S]*AutoMaskResumeReset'
Assert-Match "frame mask provider supports resume-range face reset" $frameMaskProviderText 'public\s+int\s+RemoveFaceMasksFrom\(int\s+startFrameIndex\)[\s\S]*frameIndex\s*<\s*startFrameIndex[\s\S]*_faceMasks\.TryRemove'
Assert-Match "workspace loads initial selected frame after session init on UI thread" $workspaceText '_sessionInitialized\s*&&\s*FrameList\.SelectedFrameIndex\s*>=\s*0[\s\S]*Dispatcher\.UIThread\.Post[\s\S]*FramePreview\.OnFrameIndexChanged\(FrameList\.SelectedFrameIndex\)'
Assert-Match "workspace state restores face rect masks" $stateText 'FaceMasks[\s\S]*SetFaceRects'
Assert-Match "workspace state saves face rect masks" $stateText 'GetFaceMaskEntries\(\)[\s\S]*FaceMasks'
Assert-Match "frame preview uses sequential playback reader" $framePreviewText 'RunSequentialPlaybackAsync[\s\S]*FfFrameExtractor\(videoPath\)[\s\S]*StartSequentialRead\(startFrameIndex\)[\s\S]*TryGetNextFrame\(ct,\s*out\s+var\s+frame,\s*out\s+int\s+frameIndex\)'
Assert-Match "frame preview avoids exact-frame cancellation flood while playing" $framePreviewText 'if\s*\(_isPlaying\)[\s\S]*return;[\s\S]*int\s+stamp\s*=\s*Interlocked\.Increment\(ref\s+_changeStamp\)'
Assert-Match "frame preview applies playback frames on UI thread" $framePreviewText 'Dispatcher\.UIThread\.InvokeAsync\(\(\)\s*=>[\s\S]*runId\s*!=\s*_playbackRunId[\s\S]*ApplyPlaybackFrame\(frame,\s*frameIndex\)[\s\S]*onFrameAdvanced\(frameIndex\)'
Assert-Match "exact frame provider returns null instead of cancellation exception" $exactFrameProviderText 'ct\.IsCancellationRequested[\s\S]*return\s+null[\s\S]*Task\.Run\(\(\)\s*=>\s*_extractor\.GetFrameByIndex\(frameIndex\)\)'
Assert-Match "timeline controller uses request ids instead of canceling every preview frame" $timelineControllerText '_exactRequestId[\s\S]*Interlocked\.Increment\(ref\s+_exactRequestId\)[\s\S]*Volatile\.Read\(ref\s+_exactRequestId\)'
Assert-Match "video session defaults to lazy thumbnail preload" $videoSessionText 'eagerThumbnailCount\s*=\s*0[\s\S]*done\s*<\s*preloadLimit'
Assert-Match "timeline render avoids synchronous thumbnail decode" $timelineFrameStripText 'TryGetCachedThumbnail\(frame[\s\S]*RequestThumbnail\(provider,\s*frame\)'
Assert-Match "timeline requests missing thumbnails off render path" $timelineFrameStripText 'private\s+void\s+RequestThumbnail[\s\S]*Task\.Run[\s\S]*provider\.GetThumbnail\(frame\)'
Assert-Contains "thumbnail provider exposes cache-only lookup" $timelineThumbnailProviderText "TryGetCachedThumbnail"
Assert-Match "tool panel supports manual mode" $toolPanelText "SetManual|EditMode\.Manual"
Assert-Match "tool panel supports brush" $toolPanelText "SetBrush|EditMode\.Brush"
Assert-Match "tool panel supports eraser" $toolPanelText "SetEraser|EditMode\.Eraser"
Assert-Match "state stores yolo model path" $stateText "YoloModelPath"
Assert-Match "state stores yolo5 profile" $stateText "Yolo5ModelPath"
Assert-Match "state stores yolo v8 profile" $stateText "YoloV8ModelPath"

$checklistScript = Join-Path $repo "scripts\new-yolo-gui-smoke-checklist.ps1"
if (-not (Test-Path $checklistScript)) {
    throw "GUI smoke checklist script not found: $checklistScript"
}
$evidencePrepScript = Join-Path $repo "scripts\prepare-yolo-gui-smoke-evidence.ps1"
if (-not (Test-Path $evidencePrepScript)) {
    throw "GUI smoke evidence prep script not found: $evidencePrepScript"
}
$evidencePrepText = Get-Content -Raw -Path $evidencePrepScript
Assert-Contains "checklist evidence prep keeps manual status blank" $evidencePrepText "does not set status=pass or evidence"
Assert-Contains "checklist evidence prep records track hold recording" $evidencePrepText "preview-track-hold.mp4"
Assert-Contains "checklist evidence prep records setter command section" $evidencePrepText "Evidence Setter Commands"
Assert-Contains "checklist evidence prep records open-video setter" $evidencePrepText "-StepId open-video"
Assert-Contains "checklist evidence prep records track-hold setter" $evidencePrepText "-StepId preview-track-hold"
Assert-Contains "checklist evidence prep records reopen-state setter" $evidencePrepText "-StepId reopen-state"

$evidenceSetScript = Join-Path $repo "scripts\set-yolo-gui-smoke-evidence.ps1"
if (-not (Test-Path $evidenceSetScript)) {
    throw "GUI smoke evidence setter script not found: $evidenceSetScript"
}
$evidenceSetText = Get-Content -Raw -Path $evidenceSetScript
Assert-Contains "checklist evidence setter validates artifacts" $evidenceSetText "Artifact does not exist"
Assert-Contains "checklist evidence setter rejects wrong artifact type" $evidenceSetText "extension does not match evidenceType"
Assert-Contains "checklist evidence setter requires evidence text" $evidenceSetText "Evidence text is required"

$requiredSteps = @(
    "open-video",
    "select-yolo-backend",
    "download-yolo-model",
    "run-yolo-auto-detect",
    "preview-result",
    "preview-track-hold",
    "manual-edit",
    "export",
    "reopen-state"
)

$requiredEvidenceTypes = @{
    "open-video" = @("screenshot-or-recording")
    "select-yolo-backend" = @("screenshot")
    "download-yolo-model" = @("screenshot-or-log")
    "run-yolo-auto-detect" = @("screenshot-or-log")
    "preview-result" = @("screenshot-or-recording")
    "preview-track-hold" = @("recording")
    "manual-edit" = @("screenshot-or-recording")
    "export" = @("output-file")
    "reopen-state" = @("screenshot-or-recording")
}

if ($SelfTest) {
    $selfTestRelativeDir = ".tmp\yolo-gui-smoke\verifier-selftest"
    $selfTestDir = Join-Path $repo $selfTestRelativeDir
    New-Item -ItemType Directory -Force -Path $selfTestDir | Out-Null

    $selfTestArtifacts = @{
        "open-video" = "open-video.png"
        "select-yolo-backend" = "select-yolo-backend.png"
        "download-yolo-model" = "download-yolo-model.log"
        "run-yolo-auto-detect" = "run-yolo-auto-detect.log"
        "preview-result" = "preview-result.mp4"
        "preview-track-hold" = "preview-track-hold.mp4"
        "manual-edit" = "manual-edit.png"
        "export" = "export.mp4"
        "reopen-state" = "reopen-state.png"
    }

    $selfTestRows = foreach ($step in $requiredSteps) {
        $artifactPath = Join-Path $selfTestDir $selfTestArtifacts[$step]
        Set-Content -Encoding UTF8 -Path $artifactPath -Value "yolo gui smoke selftest artifact for $step"

        [pscustomobject]@{
            stepId = $step
            status = "pass"
            evidenceType = $requiredEvidenceTypes[$step][0]
            artifactPath = Join-Path $selfTestRelativeDir $selfTestArtifacts[$step]
            evidence = "selftest evidence for $step"
            notes = "selftest"
        }
    }

    $selfTestChecklist = Join-Path $selfTestDir "manual-smoke-checklist.csv"
    $selfTestRows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $selfTestChecklist

    $wrongExportTypeRows = @($selfTestRows | ForEach-Object { $_.PSObject.Copy() })
    $wrongExportArtifact = Join-Path $selfTestDir "export.txt"
    Set-Content -Encoding UTF8 -Path $wrongExportArtifact -Value "not a video output"
    ($wrongExportTypeRows | Where-Object { $_.stepId -eq "export" } | Select-Object -First 1).artifactPath = Join-Path $selfTestRelativeDir "export.txt"
    $wrongExportTypeChecklist = Join-Path $selfTestDir "manual-smoke-checklist-wrong-export-type.csv"
    $wrongExportTypeRows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $wrongExportTypeChecklist

    $missingArtifactRows = @($selfTestRows | ForEach-Object { $_.PSObject.Copy() })
    ($missingArtifactRows | Where-Object { $_.stepId -eq "preview-result" } | Select-Object -First 1).artifactPath = Join-Path $selfTestRelativeDir "missing-preview.mp4"
    $missingArtifactChecklist = Join-Path $selfTestDir "manual-smoke-checklist-missing-artifact.csv"
    $missingArtifactRows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $missingArtifactChecklist

    $failStatusRows = @($selfTestRows | ForEach-Object { $_.PSObject.Copy() })
    ($failStatusRows | Where-Object { $_.stepId -eq "manual-edit" } | Select-Object -First 1).status = "fail"
    $failStatusChecklist = Join-Path $selfTestDir "manual-smoke-checklist-fail-status.csv"
    $failStatusRows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $failStatusChecklist

    Assert-Throws "manual export wrong artifact type" {
        Assert-ManualChecklist -Path $wrongExportTypeChecklist -Steps $requiredSteps -EvidenceTypes $requiredEvidenceTypes
    } "extension does not match evidenceType"

    Assert-Throws "manual missing artifact" {
        Assert-ManualChecklist -Path $missingArtifactChecklist -Steps $requiredSteps -EvidenceTypes $requiredEvidenceTypes
    } "artifactPath does not exist"

    Assert-Throws "manual fail status" {
        Assert-ManualChecklist -Path $failStatusChecklist -Steps $requiredSteps -EvidenceTypes $requiredEvidenceTypes
    } "step is not pass"

    $noClobberChecklist = Join-Path $selfTestDir "manual-smoke-checklist-no-clobber.csv"
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $checklistScript -OutputCsv $noClobberChecklist -Force | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "GUI checklist no-clobber fixture creation failed with exit code $LASTEXITCODE"
    }

    $oldErrorAction = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $noClobberOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $checklistScript -OutputCsv $noClobberChecklist 2>&1
        $noClobberExitCode = $LASTEXITCODE
        $noClobberText = ($noClobberOutput | Out-String)
    }
    finally {
        $ErrorActionPreference = $oldErrorAction
    }
    if ($noClobberExitCode -eq 0 -or $noClobberText -notlike "*Pass -Force to overwrite it.*") {
        throw "GUI checklist no-clobber selftest failed. exitCode=$noClobberExitCode output=$noClobberText"
    }
    Write-Host "[YoloGuiSmokeVerify] pass checklist no-clobber selftest"

    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $checklistScript -OutputCsv $noClobberChecklist -Force | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "GUI checklist force overwrite selftest failed with exit code $LASTEXITCODE"
    }
    Write-Host "[YoloGuiSmokeVerify] pass checklist force overwrite selftest"

    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $evidenceSetScript -SelfTest | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "GUI evidence setter selftest failed with exit code $LASTEXITCODE"
    }
    Write-Host "[YoloGuiSmokeVerify] pass checklist evidence setter selftest"

    $ChecklistCsv = $selfTestChecklist
    $RequireManualPass = $true
    Write-Host "[YoloGuiSmokeVerify] selftest checklist=$selfTestChecklist"
}

$checklistScriptText = Get-Content -Raw -Path $checklistScript
Assert-Contains "checklist has evidence type column" $checklistScriptText "evidenceType"
Assert-Contains "checklist has artifact path column" $checklistScriptText "artifactPath"
Assert-Contains "checklist has model download step" $checklistScriptText "download-yolo-model"
Assert-Contains "checklist has anti-flicker tracking step" $checklistScriptText "preview-track-hold"
Assert-Contains "checklist records detector miss tracking hold" $checklistScriptText "briefly missed by the detector"
Assert-Contains "checklist requires output file evidence" $checklistScriptText "output-file"

if ($RequireManualPass) {
    Assert-ManualChecklist -Path $ChecklistCsv -Steps $requiredSteps -EvidenceTypes $requiredEvidenceTypes
    Write-Host "[YoloGuiSmokeVerify] pass manual checklist"
    if ($SelfTest) {
        Write-Host "[YoloGuiSmokeVerify] pass negative selftests=3"
    }
}
elseif ($AllowPartialManualPass) {
    Assert-ManualChecklist -Path $ChecklistCsv -Steps $requiredSteps -EvidenceTypes $requiredEvidenceTypes -AllowPartial
    Write-Host "[YoloGuiSmokeVerify] pass partial manual checklist"
}
else {
    Write-Host "[YoloGuiSmokeVerify] manual checklist not required in this run"
}

Write-Host "[YoloGuiSmokeVerify] all requested checks passed"
