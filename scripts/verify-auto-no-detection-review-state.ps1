param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$workspacePath = Join-Path $repo "ViewModels\Pages\WorkspaceViewModel.cs"
$guidePath = Join-Path $repo "YOLO_PROBLEM_SPAN_VERIFICATION.md"

function Assert-Contains {
    param(
        [string]$Name,
        [string]$Text,
        [string]$Expected
    )

    if (-not $Text.Contains($Expected)) {
        throw "$Name missing text: $Expected"
    }

    Write-Host "[AutoNoDetectionReviewVerify] pass $Name"
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

    Write-Host "[AutoNoDetectionReviewVerify] pass $Name"
}

if (-not (Test-Path $workspacePath)) {
    throw "WorkspaceViewModel not found: $workspacePath"
}
if (-not (Test-Path $guidePath)) {
    throw "YOLO problem-span guide not found: $guidePath"
}

$workspace = Get-Content -Raw -Path $workspacePath
$guide = Get-Content -Raw -Path $guidePath

Assert-Contains "workspace limits no-detection review sample count" $workspace "private const int NoDetectionReviewSampleCount = 12;"
Assert-Contains "workspace limits sparse no-face review sample count" $workspace "private const int SparseNoFaceReviewSampleCount = 12;"
Assert-Contains "workspace limits sparse no-face review coverage" $workspace "private const double SparseNoFaceReviewMaxCoverageRatio = 0.20;"
Assert-Match "workspace flags complete no-detection for yolo" $workspace "if\s*\(\s*faceFrameCount\s*==\s*0\s*\)[\s\r\n]*AddNoDetectionReviewFrames\(total, noFace\)"
Assert-Match "workspace samples sparse yolo no-face spans" $workspace "AddSuspiciousNoFaceGaps\(hasFace, noFace\);[\s\r\n]*AddSparseNoFaceReviewFrames\(hasFace, faceFrameCount, noFace\);"
Assert-Match "workspace preserves normal suspicious no-face gap review" $workspace "else\s*\{[\s\r\n]*AddSuspiciousNoFaceGaps\(hasFace, noFace\);[\s\r\n]*\}"
Assert-Contains "workspace samples no-detection frames" $workspace "private static void AddNoDetectionReviewFrames(int totalFrames, List<int> noFace)"
Assert-Contains "workspace writes sampled no-detection frames to no-face list" $workspace "noFace.AddRange(frames);"
Assert-Contains "workspace logs no-detection review frames" $workspace "[AutoMaskNoDetectionReview]"
Assert-Contains "workspace samples sparse no-face frames" $workspace "private static void AddSparseNoFaceReviewFrames(bool[] hasFace, int faceFrameCount, List<int> noFace)"
Assert-Contains "workspace logs sparse no-face review frames" $workspace "[AutoMaskSparseNoFaceReview]"
Assert-Contains "workspace still exposes no-face issue entries" $workspace 'ResetIssueList(_noFaceIssueEntries, noFaceFrames, "얼굴 없음");'
Assert-Contains "guide documents gui no-detection review" $guide "GUI 자동 검토 목록도 YOLO no-detection 전체 구간을 조용히 통과시키지 않고"
Assert-Contains "guide documents no-detection debug log" $guide "[AutoMaskNoDetectionReview]"
Assert-Contains "guide documents sparse no-face review" $guide "YOLO가 일부 얼굴만 잡고 face coverage가 낮은 구간"
Assert-Contains "guide documents sparse no-face debug log" $guide "[AutoMaskSparseNoFaceReview]"

Write-Host "[AutoNoDetectionReviewVerify] all requested checks passed"
