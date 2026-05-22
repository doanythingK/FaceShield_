param(
    [string]$ChecklistCsv = ".tmp\yolo-gui-smoke\manual-smoke-checklist.csv",
    [string]$HomeViewModel = "ViewModels\Pages\HomePageViewModel.cs",
    [string]$HomeView = "Views\Pages\HomePageView.axaml",
    [string]$WorkspaceViewModel = "ViewModels\Pages\WorkspaceViewModel.cs",
    [string]$ToolPanelViewModel = "ViewModels\Workspace\ToolPanelViewModel.cs",
    [string]$StateStore = "Services\Workspace\WorkspaceStateStore.cs",
    [switch]$RequireManualPass
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

$homeText = Read-RepoFile $HomeViewModel
$homeViewText = Read-RepoFile $HomeView
$workspaceText = Read-RepoFile $WorkspaceViewModel
$toolPanelText = Read-RepoFile $ToolPanelViewModel
$stateText = Read-RepoFile $StateStore

Assert-Match "home exposes open workflow" $homeText "StartWorkspace|OpenVideo|PickVideo"
Assert-Match "home exposes yolo backend selector" $homeText "YOLO Face ONNX[\s\S]*FaceDetectorBackend\.YoloFaceOnnx"
Assert-Match "home exposes yolo model path" $homeText "AutoYoloModelPath"
Assert-Match "home view exposes yolo picker" $homeViewText "PickYoloModel_Click"
Assert-Match "workspace runs auto detect" $workspaceText "RunAutoMaskAsync|RunAutoAsync"
Assert-Match "workspace creates yolo detector factory" $workspaceText "FaceDetectorFactory"
Assert-Match "workspace exports video" $workspaceText "VideoExportService[\s\S]*Export"
Assert-Match "workspace persists state" $workspaceText "PersistWorkspaceState"
Assert-Match "tool panel supports manual mode" $toolPanelText "SetManual|EditMode\.Manual"
Assert-Match "tool panel supports brush" $toolPanelText "SetBrush|EditMode\.Brush"
Assert-Match "tool panel supports eraser" $toolPanelText "SetEraser|EditMode\.Eraser"
Assert-Match "state stores yolo model path" $stateText "YoloModelPath"
Assert-Match "state stores yolo5 profile" $stateText "Yolo5ModelPath"
Assert-Match "state stores yolo v8 profile" $stateText "YoloV8ModelPath"

$requiredSteps = @(
    "open-video",
    "select-yolo-backend",
    "run-yolo-auto-detect",
    "preview-result",
    "manual-edit",
    "export",
    "reopen-state"
)

if ($RequireManualPass) {
    $resolvedChecklist = Resolve-RepoPath $ChecklistCsv
    if (-not (Test-Path $resolvedChecklist)) {
        throw "Manual GUI smoke checklist not found: $resolvedChecklist"
    }

    $rows = @(Import-Csv $resolvedChecklist)
    foreach ($step in $requiredSteps) {
        $row = $rows | Where-Object { $_.stepId -eq $step } | Select-Object -First 1
        if ($null -eq $row) {
            throw "Manual GUI smoke checklist missing step: $step"
        }

        if ($row.status.Trim().ToLowerInvariant() -ne "pass") {
            throw "Manual GUI smoke step is not pass: $step status=$($row.status)"
        }

        if ([string]::IsNullOrWhiteSpace($row.evidence)) {
            throw "Manual GUI smoke step is missing evidence: $step"
        }
    }

    Write-Host "[YoloGuiSmokeVerify] pass manual checklist"
}
else {
    Write-Host "[YoloGuiSmokeVerify] manual checklist not required in this run"
}

Write-Host "[YoloGuiSmokeVerify] all requested checks passed"
