param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

function Resolve-RepoPath {
    param([string]$Path)

    if ([IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return Join-Path $repo $Path
}

function Read-RequiredFile {
    param([string]$Path)

    $resolved = Resolve-RepoPath $Path
    if (-not (Test-Path $resolved)) {
        throw "Required file not found: $resolved"
    }

    return Get-Content -Raw -Path $resolved
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

    Write-Host "[YoloPseudoGtSeparationVerify] pass $Name"
}

function Assert-NotContains {
    param(
        [string]$Name,
        [string]$Text,
        [string]$Pattern
    )

    if ($Text -match $Pattern) {
        throw "$Name unexpectedly matched pattern: $Pattern"
    }

    Write-Host "[YoloPseudoGtSeparationVerify] pass $Name"
}

$runtimeRoots = @(
    "Services",
    "Views",
    "ViewModels",
    "Models",
    "Controls",
    "Converters",
    "Enums"
)

$runtimeFiles = [System.Collections.Generic.List[IO.FileInfo]]::new()
foreach ($root in $runtimeRoots) {
    $resolvedRoot = Resolve-RepoPath $root
    if (-not (Test-Path $resolvedRoot)) {
        throw "Runtime root not found: $resolvedRoot"
    }

    Get-ChildItem -Path $resolvedRoot -Recurse -File -Include *.cs,*.axaml |
        ForEach-Object { $runtimeFiles.Add($_) | Out-Null }
}

foreach ($path in @("Program.cs", "App.axaml.cs", "FaceShield.csproj")) {
    $runtimeFiles.Add((Get-Item (Resolve-RepoPath $path))) | Out-Null
}

if ($runtimeFiles.Count -eq 0) {
    throw "No runtime source files were found."
}

$forbiddenRuntimePattern = "(?i)pseudo\s*-?\s*gt|pseudoGt|PseudoGt|new-yolo-pseudo|close-yolo-pseudo|tileFaceCsv|faceVerificationCsv|personObjectCsv"
$violations = [System.Collections.Generic.List[string]]::new()
foreach ($file in $runtimeFiles) {
    $text = Get-Content -Raw -Path $file.FullName
    if ($text -match $forbiddenRuntimePattern) {
        $relative = [IO.Path]::GetRelativePath($repo, $file.FullName)
        $violations.Add($relative) | Out-Null
    }
}

if ($violations.Count -gt 0) {
    throw "Pseudo-GT test-only terms leaked into runtime files: $($violations -join ', ')"
}

Write-Host "[YoloPseudoGtSeparationVerify] pass runtime source has no pseudo-GT references, files=$($runtimeFiles.Count)"

$postProcessPipeline = Read-RequiredFile "Services/Analysis/AutoMaskPostProcessPipeline.cs"
$temporalPostProcessor = Read-RequiredFile "Services/Analysis/AutoMaskTemporalPostProcessor.cs"
$roiRefiner = Read-RequiredFile "Services/Analysis/FaceTrackRoiRefiner.cs"
$sceneCutGuard = Read-RequiredFile "Services/Analysis/FaceTrackSceneCutGuard.cs"
$pseudoGtEvidence = Read-RequiredFile "scripts/new-yolo-pseudo-gt-evidence.ps1"
$pseudoGtTileInput = Read-RequiredFile "scripts/new-yolo-pseudo-gt-tile-input.ps1"
$pseudoGtFaceVerificationInput = Read-RequiredFile "scripts/new-yolo-pseudo-gt-face-verification-input.ps1"
$pseudoGtPersonObjectInput = Read-RequiredFile "scripts/new-yolo-pseudo-gt-person-object-input.ps1"
$pseudoGtReviewDraft = Read-RequiredFile "scripts/new-yolo-pseudo-gt-review-draft.ps1"
$pseudoGtReviewDraftApply = Read-RequiredFile "scripts/apply-yolo-pseudo-gt-review-draft.ps1"
$pseudoGtClosure = Read-RequiredFile "scripts/close-yolo-pseudo-gt-review.ps1"
$problemSpanGuide = Read-RequiredFile "YOLO_PROBLEM_SPAN_VERIFICATION.md"

Assert-NotContains "postprocess pipeline does not know pseudo-GT" $postProcessPipeline $forbiddenRuntimePattern
Assert-Contains "postprocess pipeline owns runtime temporal stage" $postProcessPipeline "AutoMaskTemporalPostProcessor"
Assert-Contains "postprocess pipeline owns runtime ROI stage" $postProcessPipeline "AutoMaskRoiRefineStep"
Assert-Contains "postprocess pipeline owns runtime scene-cut stage" $postProcessPipeline "YoloSceneCutPostProcessor"
Assert-Contains "postprocess pipeline owns final mask cleanup" $postProcessPipeline "YoloFinalMaskPostProcessor"
Assert-NotContains "temporal postprocessor does not know pseudo-GT" $temporalPostProcessor $forbiddenRuntimePattern
Assert-NotContains "ROI refiner does not know pseudo-GT" $roiRefiner $forbiddenRuntimePattern
Assert-NotContains "scene-cut guard does not know pseudo-GT" $sceneCutGuard $forbiddenRuntimePattern

Assert-Contains "pseudo-GT evidence script records test-only boundary" $pseudoGtEvidence "test-only evidence"
Assert-Contains "pseudo-GT evidence script keeps review CSV final" $pseudoGtEvidence "final face/nonface/miss must be copied into the review CSV"
Assert-Contains "pseudo-GT evidence script treats person object as auxiliary" $pseudoGtEvidence "person/object support is auxiliary only"
Assert-Contains "pseudo-GT evidence script marks auxiliary role as priority only" $pseudoGtEvidence "priority-only-not-face-evidence"
Assert-Contains "pseudo-GT tile script records runtime separation" $pseudoGtTileInput "not part of the app runtime path"
Assert-Contains "pseudo-GT face verification input script records runtime separation" $pseudoGtFaceVerificationInput "not part of the app runtime path"
Assert-Contains "pseudo-GT person/object input script records runtime separation" $pseudoGtPersonObjectInput "not part of the app runtime path"
Assert-Contains "pseudo-GT review draft records test-only boundary" $pseudoGtReviewDraft "test-only review preparation"
Assert-Contains "pseudo-GT review draft keeps final labels human-owned" $pseudoGtReviewDraft "does not finalize face/nonface/miss labels"
Assert-Contains "pseudo-GT review draft apply records test-only boundary" $pseudoGtReviewDraftApply "test-only merge helper"
Assert-Contains "pseudo-GT review draft apply keeps final labels human-owned" $pseudoGtReviewDraftApply "does not infer labels from suggestedLabel"
Assert-Contains "pseudo-GT review draft apply keeps review CSV ownership" $pseudoGtReviewDraftApply "review CSV-owned"
Assert-Contains "pseudo-GT closure script records test-only boundary" $pseudoGtClosure "The app runtime path does not read this file"
Assert-Contains "problem span guide records runtime separation" $problemSpanGuide "runtime pipeline"
Assert-Contains "problem span guide keeps pseudo-GT out of detector path" $problemSpanGuide "test-only evidence pipeline"

Write-Host "[YoloPseudoGtSeparationVerify] all requested checks passed"
