param(
    [string]$ChecklistCsv = ".tmp\yolo-gui-smoke\manual-smoke-checklist.csv",
    [string]$HomeViewModel = "ViewModels\Pages\HomePageViewModel.cs",
    [string]$HomeView = "Views\Pages\HomePageView.axaml",
    [string]$WorkspaceViewModel = "ViewModels\Pages\WorkspaceViewModel.cs",
    [string]$ToolPanelViewModel = "ViewModels\Workspace\ToolPanelViewModel.cs",
    [string]$StateStore = "Services\Workspace\WorkspaceStateStore.cs",
    [switch]$RequireManualPass,
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
        [hashtable]$EvidenceTypes
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

        if ($row.status.Trim().ToLowerInvariant() -ne "pass") {
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
$workspaceText = Read-RepoFile $WorkspaceViewModel
$toolPanelText = Read-RepoFile $ToolPanelViewModel
$stateText = Read-RepoFile $StateStore

Assert-Match "home exposes open workflow" $homeText "StartWorkspace|OpenVideo|PickVideo"
Assert-Match "home exposes yolo backend selector" $homeText "YOLO Face ONNX[\s\S]*FaceDetectorBackend\.YoloFaceOnnx"
Assert-Match "home exposes yolo model path" $homeText "AutoYoloModelPath"
Assert-Match "home view exposes yolo picker" $homeViewText "PickYoloModel_Click"
Assert-Match "home view exposes yolo downloader" $homeViewText "DownloadYoloModelCommand"
Assert-Match "home view binds yolo download progress" $homeViewText "YoloModelDownloadProgress"
Assert-Match "home downloads yolo model" $homeText "DownloadYoloModelAsync[\s\S]*YoloModelDownloadStatus"
Assert-Match "home stores downloaded yolo model outside repo" $homeText "GetYoloModelDownloadDirectory[\s\S]*LocalApplicationData[\s\S]*FaceShield"
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
    "download-yolo-model",
    "run-yolo-auto-detect",
    "preview-result",
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

    $ChecklistCsv = $selfTestChecklist
    $RequireManualPass = $true
    Write-Host "[YoloGuiSmokeVerify] selftest checklist=$selfTestChecklist"
}

$checklistScript = Join-Path $repo "scripts\new-yolo-gui-smoke-checklist.ps1"
if (-not (Test-Path $checklistScript)) {
    throw "GUI smoke checklist script not found: $checklistScript"
}

$checklistScriptText = Get-Content -Raw -Path $checklistScript
Assert-Contains "checklist has evidence type column" $checklistScriptText "evidenceType"
Assert-Contains "checklist has artifact path column" $checklistScriptText "artifactPath"
Assert-Contains "checklist has model download step" $checklistScriptText "download-yolo-model"
Assert-Contains "checklist requires output file evidence" $checklistScriptText "output-file"

if ($RequireManualPass) {
    Assert-ManualChecklist -Path $ChecklistCsv -Steps $requiredSteps -EvidenceTypes $requiredEvidenceTypes
    Write-Host "[YoloGuiSmokeVerify] pass manual checklist"
    if ($SelfTest) {
        Write-Host "[YoloGuiSmokeVerify] pass negative selftests=3"
    }
}
else {
    Write-Host "[YoloGuiSmokeVerify] manual checklist not required in this run"
}

Write-Host "[YoloGuiSmokeVerify] all requested checks passed"
