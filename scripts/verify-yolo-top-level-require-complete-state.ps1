param(
    [string]$AutoMosaicDefaultVerify = "scripts\verify-auto-mosaic-default.ps1"
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

function Assert-Contains {
    param(
        [string]$Name,
        [string]$Text,
        [string]$Expected
    )

    if (-not $Text.Contains($Expected)) {
        throw "$Name missing text: $Expected"
    }

    Write-Host "[YoloTopLevelRequireCompleteVerify] pass $Name"
}

function Assert-NotContains {
    param(
        [string]$Name,
        [string]$Text,
        [string]$Unexpected
    )

    if ($Text.Contains($Unexpected)) {
        throw "$Name contained unexpected text: $Unexpected"
    }

    Write-Host "[YoloTopLevelRequireCompleteVerify] pass $Name"
}

$autoVerify = Resolve-RepoPath $AutoMosaicDefaultVerify
if (-not (Test-Path $autoVerify)) {
    throw "Auto mosaic default verifier not found: $autoVerify"
}

$autoVerifyText = Get-Content -Raw -Path $autoVerify
Assert-Contains "auto verifier exposes require complete switch" $autoVerifyText "RequireYoloComplete"
Assert-Contains "auto verifier runs require complete guard" $autoVerifyText "yolo-require-complete-guard"
Assert-Contains "auto verifier uses completion audit" $autoVerifyText "verify-yolo-completion-audit-state.ps1"
Assert-Contains "auto verifier promotes require complete to yolo state" $autoVerifyText '$RunYoloState = $true'

$oldErrorAction = $ErrorActionPreference
try {
    $ErrorActionPreference = "Continue"
    $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $autoVerify -RequireYoloComplete 2>&1
    $exitCode = $LASTEXITCODE
    $text = ($output | Out-String)
    Write-Host $text
}
finally {
    $ErrorActionPreference = $oldErrorAction
}

if ($exitCode -eq 0) {
    throw "Top-level RequireYoloComplete unexpectedly passed with pending goal marker."
}

Assert-Contains "negative output starts require complete guard" $text "[AutoMosaicVerify] start yolo-require-complete-guard"
Assert-Contains "negative output fails on incomplete marker" $text "goal marked complete missing text: complete=true"
Assert-Contains "negative output reports guard failure" $text "yolo-require-complete-guard failed with exit code 1"
Assert-NotContains "negative output does not run faceonnx quality gate" $text "[AutoMosaicVerify] start quality-gate-all-frame-parallel"
Assert-NotContains "negative output does not run yolo state wrapper" $text "[AutoMosaicVerify] start yolo-state"

Write-Host "[YoloTopLevelRequireCompleteVerify] all requested checks passed"
