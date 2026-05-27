param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$providerPath = Join-Path $repo "Services\Video\FrameMaskProvider.cs"
$workspacePath = Join-Path $repo "ViewModels\Pages\WorkspaceViewModel.cs"

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

$provider = Get-Content -Raw -Path $providerPath
$workspace = Get-Content -Raw -Path $workspacePath

Assert-Contains "provider exposes ranged face-mask cleanup" $provider "public int RemoveFaceMasksFrom(int startFrameIndex)"
Assert-Contains "provider preserves earlier face masks" $provider "if (frameIndex < startFrameIndex)"
Assert-Contains "provider removes matching future face masks" $provider "_faceMasks.TryRemove(frameIndex, out _)"
Assert-Contains "workspace resets masks before generator run" $workspace "ResetAutoFaceMasksForRun(lastProcessed);"
Assert-Contains "fresh run clears all face masks" $workspace "_maskProvider.ClearFaceMasks();"
Assert-Contains "resume run clears stale future face masks" $workspace "_maskProvider.RemoveFaceMasksFrom(startFrameIndex)"
Assert-Contains "resume reset is logged" $workspace "[AutoMaskResumeReset]"
Assert-Contains "workspace tracks detection completion across export cancel" $workspace "bool detectionCompleted = false;"
Assert-Contains "workspace marks detection completion before export" $workspace "detectionCompleted = true;"
Assert-Contains "workspace preserves completed state on export cancel" $workspace "_autoCompleted = detectionCompleted;"
Assert-Contains "workspace clears resume index after completed cancel" $workspace "if (detectionCompleted)"

Write-Host "[AutoResumeMaskResetVerify] all requested checks passed"
