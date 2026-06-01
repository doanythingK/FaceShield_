param(
    [string]$RunnerScript = "scripts\invoke-yolo-pseudo-gt-face-verification-runner.ps1"
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

    Write-Host "[YoloPseudoGtFaceVerificationRunnerVerify] pass $Name"
}

Assert-File "pseudo-GT face verification runner" $runnerPath
$text = Get-Content -Raw -Path $runnerPath

Assert-Contains "runner is external test-only script" $text "YoloPseudoGtFaceVerificationRunner"
Assert-Contains "runner accepts face verification manifest" $text '\[Parameter\(Mandatory\s*=\s*\$true\)\][\s\S]*\[string\]\$ManifestCsv'
Assert-Contains "runner writes face verification csv" $text '\[Parameter\(Mandatory\s*=\s*\$true\)\][\s\S]*\[string\]\$OutputCsv'
Assert-Contains "runner requires local model path" $text '\[Parameter\(Mandatory\s*=\s*\$true\)\][\s\S]*\[string\]\$ModelPath'
Assert-Contains "runner supports scrfd and yunet" $text 'ValidateSet\("Scrfd",\s*"YuNet"\)'
Assert-Contains "runner compiles temp project under tmp" $text "\.tmp\\yolo-pseudo-gt-face-verification-runner"
Assert-Contains "runner references app detectors instead of runtime pipeline" $text "ProjectReference[\s\S]*FaceShield\.csproj"
Assert-Contains "runner uses SCRFD detector" $text "new ScrfdOnnxDetector"
Assert-Contains "runner uses YuNet detector" $text "new YuNetOnnxDetector"
Assert-Contains "runner reads crop image path" $text "cropImagePath"
Assert-Contains "runner reads base candidate geometry" $text "baseFaceConfidence|basePredictionId[\s\S]*baseX[\s\S]*baseY[\s\S]*baseW[\s\S]*baseH"
Assert-Contains "runner converts crop detections to frame coordinates" $text "cropX \+ face\.Bounds\.X[\s\S]*cropY \+ face\.Bounds\.Y"
Assert-Contains "runner computes face verification distance" $text "CenterDistanceRatio"
Assert-Contains "runner scores by confidence geometry and distance" $text "face\.Confidence \+ iou - distance"
Assert-Contains "runner emits face verification csv contract" $text "frame,candidateId,basePredictionId,verificationId,x,y,w,h,faceVerificationConfidence,faceVerificationDistance"
Assert-Contains "runner records evidence model provenance" $text "EvidenceModel"
Assert-Contains "runner records evidence runner provenance" $text "EvidenceRunner"
Assert-Contains "runner stays out of default runtime pipeline" $text "IBgraFaceDetector detector = CreateDetector"

$problemSpanRunner = Get-Content -Raw -Path (Join-Path $repo "scripts\run-yolo-problem-span-verification.ps1")
Assert-Contains "problem-span runner can call external face verification runner" $problemSpanRunner "PseudoGtFaceVerificationExternalCommand[\s\S]*PseudoGtFaceVerificationExternalArgumentsTemplate[\s\S]*PseudoGtFaceVerificationExternalOutputCsv"

$guide = Get-Content -Raw -Path (Join-Path $repo "YOLO_PROBLEM_SPAN_VERIFICATION.md")
Assert-Contains "guide documents external face verification runner path" $guide "invoke-yolo-pseudo-gt-face-verification-runner\.ps1"

Write-Host "[YoloPseudoGtFaceVerificationRunnerVerify] all requested checks passed"
