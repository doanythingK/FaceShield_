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
Assert-Match "workspace only flags complete no-detection for yolo" $workspace "if\s*\(\s*_autoOptions\.FilterProfile\s*==\s*FaceFilterProfile\.Yolo\s*&&\s*!hasFace\.Any\(static x => x\)\s*\)[\s\S]*AddNoDetectionReviewFrames\(total, noFace\)"
Assert-Match "workspace preserves normal suspicious no-face gap review" $workspace "else\s*[\r\n\s]*AddSuspiciousNoFaceGaps\(hasFace, noFace\);"
Assert-Contains "workspace samples no-detection frames" $workspace "private static void AddNoDetectionReviewFrames(int totalFrames, List<int> noFace)"
Assert-Contains "workspace writes sampled no-detection frames to no-face list" $workspace "noFace.AddRange(frames);"
Assert-Contains "workspace logs no-detection review frames" $workspace "[AutoMaskNoDetectionReview]"
Assert-Contains "workspace still exposes no-face issue entries" $workspace 'ResetIssueList(_noFaceIssueEntries, noFaceFrames, "얼굴 없음");'
Assert-Contains "guide documents gui no-detection review" $guide "GUI 자동 검토 목록도 YOLO no-detection 전체 구간을 조용히 통과시키지 않고"
Assert-Contains "guide documents no-detection debug log" $guide "[AutoMaskNoDetectionReview]"

Write-Host "[AutoNoDetectionReviewVerify] all requested checks passed"
