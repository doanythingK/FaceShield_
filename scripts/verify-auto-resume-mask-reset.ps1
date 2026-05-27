param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$providerPath = Join-Path $repo "Services\Video\FrameMaskProvider.cs"
$workspacePath = Join-Path $repo "ViewModels\Pages\WorkspaceViewModel.cs"
$stateStorePath = Join-Path $repo "Services\Workspace\WorkspaceStateStore.cs"

function Assert-Contains {
    param(
        [string]$Name,
        [string]$Text,
        [string]$Expected
    )

    if (-not $Text.Contains($Expected)) {
        throw "$Name missing text: $Expected"
    }

    Write-Host "[AutoResumeMaskResetVerify] pass $Name"
}

if (-not (Test-Path $providerPath)) {
    throw "FrameMaskProvider not found: $providerPath"
}
if (-not (Test-Path $workspacePath)) {
    throw "WorkspaceViewModel not found: $workspacePath"
}
if (-not (Test-Path $stateStorePath)) {
    throw "WorkspaceStateStore not found: $stateStorePath"
}

$provider = Get-Content -Raw -Path $providerPath
$workspace = Get-Content -Raw -Path $workspacePath
$stateStore = Get-Content -Raw -Path $stateStorePath

Assert-Contains "provider exposes ranged face-mask cleanup" $provider "public int RemoveFaceMasksFrom(int startFrameIndex)"
Assert-Contains "provider preserves earlier face masks" $provider "if (frameIndex < startFrameIndex)"
Assert-Contains "provider removes matching future face masks" $provider "_faceMasks.TryRemove(frameIndex, out _)"
Assert-Contains "workspace resets masks before generator run" $workspace "ResetAutoFaceMasksForRun(lastProcessed);"
Assert-Contains "fresh run clears all face masks" $workspace "_maskProvider.ClearFaceMasks();"
Assert-Contains "resume run clears stale future face masks" $workspace "_maskProvider.RemoveFaceMasksFrom(startFrameIndex)"
Assert-Contains "resume reset is logged" $workspace "[AutoMaskResumeReset]"
Assert-Contains "workspace stores auto run signature" $workspace "_autoRunSignature"
Assert-Contains "workspace gates resume prompt by signature" $workspace "IsAutoResumeSignatureCurrent(BuildAutoRunSignature"
Assert-Contains "workspace resets stale resume on settings change" $workspace "ResetStaleAutoResumeIfSettingsChanged(runSignature);"
Assert-Contains "workspace logs settings-changed resume reset" $workspace "reason=settings-changed"
Assert-Contains "workspace signs yolo detector settings" $workspace "AppendYoloSignature"
Assert-Contains "workspace signs faceonnx detector settings" $workspace "AppendFaceOnnxSignature"
Assert-Contains "workspace tracks detection completion across export cancel" $workspace "bool detectionCompleted = false;"
Assert-Contains "workspace marks detection completion before export" $workspace "detectionCompleted = true;"
Assert-Contains "workspace preserves completed state on export cancel" $workspace "_autoCompleted = detectionCompleted;"
Assert-Contains "workspace clears resume index after completed cancel" $workspace "if (detectionCompleted)"
Assert-Contains "state store persists auto run signature" $stateStore "AutoRunSignature"

Write-Host "[AutoResumeMaskResetVerify] all requested checks passed"
