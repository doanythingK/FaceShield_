param(
    [string]$RunnerScript = "scripts\invoke-yolo-pseudo-gt-face-runner.ps1"
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$runnerPath = Join-Path $repo $RunnerScript

function Assert-File {
    param([string]$Name, [string]$Path)

    if (-not (Test-Path $Path)) {
        throw "$Name not found: $Path"
    }
}

function Assert-Contains {
    param(
        [string]$Name,
        [string]$Text,
        [string]$Pattern
    )

    if ($Text -notmatch $Pattern) {
        throw "$Name missing pattern: $Pattern"
    }

    Write-Host "[YoloPseudoGtFaceRunnerVerify] pass $Name"
}

Assert-File "pseudo-GT face runner" $runnerPath
$text = Get-Content -Raw -Path $runnerPath

Assert-Contains "runner is external test-only script" $text "YoloPseudoGtFaceRunner"
Assert-Contains "runner accepts tile manifest" $text '\[Parameter\(Mandatory\s*=\s*\$true\)\][\s\S]*\[string\]\$ManifestCsv'
Assert-Contains "runner writes external tile face csv" $text '\[Parameter\(Mandatory\s*=\s*\$true\)\][\s\S]*\[string\]\$OutputCsv'
Assert-Contains "runner requires local model path" $text '\[Parameter\(Mandatory\s*=\s*\$true\)\][\s\S]*\[string\]\$ModelPath'
Assert-Contains "runner supports scrfd and yunet" $text 'ValidateSet\("Scrfd",\s*"YuNet"\)'
Assert-Contains "runner keeps models outside repo output" $text "ModelPath not found"
Assert-Contains "runner compiles temp project under tmp" $text "\.tmp\\yolo-pseudo-gt-face-runner"
Assert-Contains "runner references app detectors instead of runtime pipeline" $text "ProjectReference[\s\S]*FaceShield\.csproj"
Assert-Contains "runner uses SCRFD detector" $text "new ScrfdOnnxDetector"
Assert-Contains "runner uses YuNet detector" $text "new YuNetOnnxDetector"
Assert-Contains "runner reads tile image path" $text "tileImagePath"
Assert-Contains "runner converts tile image coordinates to frame coordinates" $text "tileX \+ face\.Bounds\.X / tileScale[\s\S]*tileY \+ face\.Bounds\.Y / tileScale"
Assert-Contains "runner emits frame coordinate csv contract" $text "frame,tileIndex,detectionId,x,y,w,h,confidence,tileSupportCount,evidenceModel,evidenceRunner"
Assert-Contains "runner records evidence model provenance" $text "EvidenceModel"
Assert-Contains "runner records evidence runner provenance" $text "EvidenceRunner"
Assert-Contains "runner computes repeated tile support" $text "TileSupportCount[\s\S]*Iou\(candidate, other\)"
Assert-Contains "runner stays out of default runtime pipeline" $text "IBgraFaceDetector detector = CreateDetector"

$problemSpanRunner = Get-Content -Raw -Path (Join-Path $repo "scripts\run-yolo-problem-span-verification.ps1")
Assert-Contains "problem-span runner can call external tile runner" $problemSpanRunner "PseudoGtTileExternalCommand[\s\S]*PseudoGtTileExternalArgumentsTemplate[\s\S]*PseudoGtTileExternalOutputCsv"

$guide = Get-Content -Raw -Path (Join-Path $repo "YOLO_PROBLEM_SPAN_VERIFICATION.md")
Assert-Contains "guide documents external tile runner path" $guide "PseudoGtTileExternalCommand"

Write-Host "[YoloPseudoGtFaceRunnerVerify] all requested checks passed"
