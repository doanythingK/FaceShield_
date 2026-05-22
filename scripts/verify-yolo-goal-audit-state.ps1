param(
    [string]$PlanPath = "AUTO_MOSAIC_QUALITY_SPEED_PLAN.md",
    [string]$AutoMosaicDefaultVerify = "scripts/verify-auto-mosaic-default.ps1",
    [string]$YoloStateVerify = "scripts/verify-yolo-state.ps1"
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
        throw "File not found: $resolved"
    }

    return Get-Content -Raw -Path $resolved
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

    Write-Host "[YoloGoalAuditVerify] pass $Name"
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

    Write-Host "[YoloGoalAuditVerify] pass $Name"
}

$plan = Read-RepoFile $PlanPath
$autoVerify = Read-RepoFile $AutoMosaicDefaultVerify
$yoloState = Read-RepoFile $YoloStateVerify

$marker = "yolo-goal-audit-state: backend=integrated; default=FaceONNX; recommendation=none; representative=pass; extended=fail; extended-export=fail; sample-gt=pass; complete=false; remaining=full-gt-label,gui-smoke,license,10min-full"
Assert-Contains "goal audit marker" $plan $marker

foreach ($token in @(
    "backend=integrated",
    "default=FaceONNX",
    "recommendation=none",
    "representative=pass",
    "extended=fail",
    "extended-export=fail",
    "sample-gt=pass",
    "complete=false",
    "full-gt-label",
    "gui-smoke",
    "license",
    "10min-full")) {
    Assert-Contains "goal audit token $token" $plan $token
}

Assert-Contains "profile verifier recorded" $plan "verify-yolo-profile-state.ps1"
Assert-Contains "representative verifier recorded" $plan "verify-yolo-representative-gate.ps1"
Assert-Contains "representative pass recorded" $plan "SmokeQualityGate passed=True"
Assert-Contains "extended verifier recorded" $plan "verify-yolo-extended-gate.ps1"
Assert-Contains "extended export verifier recorded" $plan "verify-yolo-extended-export-gate.ps1"
Assert-Contains "ten minute runner recorded" $plan "run-yolo-ten-minute-full.ps1"
Assert-Contains "ten minute state verifier recorded" $plan "verify-yolo-ten-minute-state.ps1"
Assert-Contains "ten minute runner marker recorded" $plan "yolo-ten-minute-runner-state: prepared=true"
Assert-Contains "ten minute optimized full run recorded" $plan "full-run=yolo-optimized-only-pass"
Assert-Contains "ten minute auto total recorded" $plan "autoTotalMs=2536529"
Assert-Contains "ten minute export total recorded" $plan "exportTotalMs=1375350"
Assert-Contains "ten minute direct face frames recorded" $plan "directFaceFrames=8063"
Assert-Contains "gt label sample marker recorded" $plan "yolo-gt-label-sample-state:"
Assert-Contains "gt label verifier recorded" $plan "verify-yolo-gt-label-state.ps1"
Assert-Contains "gt label pass sample rows recorded" $plan "passRows=15"
Assert-Contains "gt label pass yolo tp recorded" $plan "passYoloTP=15"
Assert-Contains "gt label pass yolo fp recorded" $plan "passYoloFP=0"
Assert-Contains "gt label fail sample rows recorded" $plan "failRows=26"
Assert-Contains "gt label fail unclear recorded" $plan "failUnclear=1"
Assert-Contains "gt label fail yolo fp recorded" $plan "failYoloFP=10"
Assert-Contains "gt label fail faceonnx fp recorded" $plan "failFaceOnnxFP=14"
Assert-Contains "gt label scope recorded" $plan "sample-crops-not-full-video-gt"
Assert-Contains "extended fail recorded" $plan "SmokeQualityGate passed=False"
Assert-Contains "extended baseline frames recorded" $plan "baselineFrames=83"
Assert-Contains "extended optimized frames recorded" $plan "optimizedFrames=74"
Assert-Contains "extended only baseline recorded" $plan "onlyBaseline=14"
Assert-Contains "extended export baseline direct frames recorded" $plan "directFaceFrames=83"
Assert-Contains "extended export yolo direct frames recorded" $plan "directFaceFrames=74"
Assert-Contains "no final recommendation recorded" $plan "recommendation=none"
Assert-Contains "full gt label still missing" $plan "full-gt-label"
Assert-Contains "gui smoke still missing" $plan "gui-smoke"
Assert-Contains "license still unresolved" $plan "license"
Assert-Contains "ten minute still unresolved" $plan "10min-full"

Assert-Contains "auto verifier exposes yolo state" $autoVerify "RunYoloState"
Assert-Contains "auto verifier exposes representative gate" $autoVerify "RunYoloRepresentativeGate"
Assert-Contains "auto verifier exposes extended gate" $autoVerify "RunYoloExtendedGate"
Assert-Contains "auto verifier exposes extended export gate" $autoVerify "RunYoloExtendedExportGate"
Assert-Contains "auto verifier exposes ten minute state" $autoVerify "RunYoloTenMinuteState"
Assert-Contains "auto verifier exposes ten minute clip requirement" $autoVerify "RequireYoloTenMinuteClip"
Assert-Contains "auto verifier exposes ten minute run requirement" $autoVerify "RequireYoloTenMinuteRun"
Assert-Contains "yolo state exposes representative gate" $yoloState "RunRepresentativeGate"
Assert-Contains "yolo state exposes extended gate" $yoloState "RunExtendedGate"
Assert-Contains "yolo state exposes extended export gate" $yoloState "RunExtendedExportGate"
Assert-Contains "yolo state exposes ten minute state" $yoloState "RunTenMinuteState"
Assert-Contains "yolo state exposes ten minute clip requirement" $yoloState "RequireTenMinuteClip"
Assert-Contains "yolo state exposes ten minute run requirement" $yoloState "RequireTenMinuteRun"

Write-Host "[YoloGoalAuditVerify] all requested checks passed"
