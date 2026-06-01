param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$scriptPath = Join-Path $repo "scripts\new-yolo-pseudo-gt-review-visual-package.ps1"
$work = Join-Path $repo ".tmp\yolo-pseudo-gt-review-visual-package-state"

function Assert-Contains {
    param(
        [string]$Name,
        [string]$Text,
        [string]$Expected
    )

    if (-not $Text.Contains($Expected)) {
        throw "$Name missing text: $Expected"
    }

    Write-Host "[YoloPseudoGtReviewVisualVerify] pass $Name"
}

function Assert-FileNonEmpty {
    param(
        [string]$Name,
        [string]$Path
    )

    if (-not (Test-Path $Path)) {
        throw "$Name not found: $Path"
    }

    $item = Get-Item $Path
    if ($item.Length -le 0) {
        throw "$Name is empty: $Path"
    }

    Write-Host "[YoloPseudoGtReviewVisualVerify] pass $Name"
}

function Resolve-Tool {
    param([string]$ToolName)

    $native = Get-Command $ToolName -ErrorAction SilentlyContinue
    if ($null -ne $native) {
        return [pscustomobject]@{
            Command = $native.Source
            ToolName = ""
            UseWsl = $false
        }
    }

    $wsl = Get-Command wsl.exe -ErrorAction SilentlyContinue
    if ($null -ne $wsl) {
        return [pscustomobject]@{
            Command = $wsl.Source
            ToolName = $ToolName
            UseWsl = $true
        }
    }

    return $null
}

function Convert-ToWslPath {
    param([string]$Path)

    if ($Path -match '^([A-Za-z]):\\(.*)$') {
        $drive = $matches[1].ToLowerInvariant()
        $rest = $matches[2] -replace '\\', '/'
        return "/mnt/$drive/$rest"
    }

    return $Path -replace '\\', '/'
}

function Invoke-Tool {
    param(
        [object]$Tool,
        [string[]]$Arguments
    )

    if ($Tool.UseWsl) {
        $converted = @()
        foreach ($arg in $Arguments) {
            if ($arg -match '^[A-Za-z]:\\') {
                $converted += Convert-ToWslPath $arg
            }
            else {
                $converted += $arg
            }
        }

        return & $Tool.Command --exec $Tool.ToolName @converted
    }

    return & $Tool.Command @Arguments
}

if (-not (Test-Path $scriptPath)) {
    throw "Script not found: $scriptPath"
}

$scriptText = Get-Content -Raw -Path $scriptPath
Assert-Contains "script records test-only visual boundary" $scriptText "test-only visual review evidence"
Assert-Contains "script keeps final labels human owned" $scriptText "does not finalize face/nonface/miss labels"
Assert-Contains "script rejects suggestedLabel inference" $scriptText "does not infer labels from suggestedLabel"
Assert-Contains "script writes visual draft csv" $scriptText "pseudo-gt-full-gt-review-visual-draft.csv"
Assert-Contains "script writes compact decision sheet" $scriptText "pseudo-gt-review-decision-sheet.csv"
Assert-Contains "script writes compact full-frame decision sheet" $scriptText "pseudo-gt-full-frame-review-decision-sheet.csv"
Assert-Contains "script writes crop path" $scriptText "cropPath"
Assert-Contains "script writes overlay path" $scriptText "visualOverlayPath"
Assert-Contains "script records missing visual frames" $scriptText "missingVisualFrames"
Assert-Contains "script records missing visual recovery command" $scriptText "suggestedVideoRerunCommand"
Assert-Contains "script uses per-source frame visual size" $scriptText "Get-VisualSourceSize"
Assert-Contains "script clamps overlay box to source frame" $scriptText "Get-ClampedBoxRect"
Assert-Contains "script documents decision sheet apply path" $scriptText "-DecisionCsv and -FrameDecisionCsv"

if (Test-Path $work) {
    Remove-Item -Recurse -Force -Path $work
}
New-Item -ItemType Directory -Force -Path $work | Out-Null

$frameDir = Join-Path $work "frames"
New-Item -ItemType Directory -Force -Path $frameDir | Out-Null
$ffmpeg = Resolve-Tool "ffmpeg"
if ($null -eq $ffmpeg) {
    throw "ffmpeg not found for pseudo-GT visual package self-test"
}

foreach ($frame in @(4, 17, 20)) {
    $framePath = Join-Path $frameDir ("frame-{0:D6}.png" -f $frame)
    Invoke-Tool $ffmpeg @(
        "-y",
        "-hide_banner",
        "-loglevel",
        "error",
        "-f",
        "lavfi",
        "-i",
        "color=c=gray:s=320x180",
        "-frames:v",
        "1",
        $framePath
    ) | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $framePath)) {
        throw "ffmpeg frame fixture failed for frame $frame"
    }
}

$draftCsv = Join-Path $work "pseudo-gt-full-gt-review-draft.csv"
$frameDraftCsv = Join-Path $work "pseudo-gt-full-frame-review-draft.csv"
$outputDir = Join-Path $work "visual"

@(
    [pscustomobject]@{
        frame = "17"
        gtId = ""
        label = ""
        x = "20"
        y = "20"
        w = "60"
        h = "70"
        sourcePredictionId = "17-1"
        sourceConfidence = "0.45"
        source = "pseudo-gt-review-queue"
        cropPath = ""
        cropX = ""
        cropY = ""
        cropW = ""
        cropH = ""
        reviewStatus = ""
        evidenceNotes = ""
        notes = "visual fixture false positive"
        pseudoGt_candidateId = "fp-17"
        pseudoGt_candidateType = "falsePositiveCandidate"
        pseudoGt_tileFaceConfidence = "0.05"
        pseudoGt_tileSupportCount = "0"
        pseudoGt_faceVerificationConfidence = "0.01"
        pseudoGt_faceVerificationDistance = "0.92"
        pseudoGt_personConfidence = "0.10"
        pseudoGt_personUpperOverlap = "0.00"
        pseudoGt_fpProbability = "0.90"
        pseudoGt_missProbability = "0.00"
        pseudoGt_pseudoGtReason = "base unsupported by tile and verifier"
        suggestedLabel = "nonface"
        candidateRule = "test-only-reference-not-final-gt"
        humanReviewInstruction = "fixture"
    },
    [pscustomobject]@{
        frame = "20"
        gtId = ""
        label = ""
        x = "80"
        y = "30"
        w = "55"
        h = "65"
        sourcePredictionId = "20-1"
        sourceConfidence = "0.82"
        source = "pseudo-gt-review-queue"
        cropPath = ""
        cropX = ""
        cropY = ""
        cropW = ""
        cropH = ""
        reviewStatus = ""
        evidenceNotes = ""
        notes = "visual fixture supported"
        pseudoGt_candidateId = "support-20"
        pseudoGt_candidateType = "supportedFaceCandidate"
        pseudoGt_tileFaceConfidence = "0.88"
        pseudoGt_tileSupportCount = "3"
        pseudoGt_faceVerificationConfidence = "0.91"
        pseudoGt_faceVerificationDistance = "0.08"
        pseudoGt_personConfidence = "0.40"
        pseudoGt_personUpperOverlap = "0.22"
        pseudoGt_fpProbability = "0.02"
        pseudoGt_missProbability = "0.00"
        pseudoGt_pseudoGtReason = "tile and verifier support base candidate"
        suggestedLabel = "face"
        candidateRule = "test-only-reference-not-final-gt"
        humanReviewInstruction = "fixture"
    },
    [pscustomobject]@{
        frame = "4"
        gtId = "miss-4"
        label = ""
        x = "180"
        y = "40"
        w = "42"
        h = "48"
        sourcePredictionId = ""
        sourceConfidence = ""
        source = "pseudo-gt-review-queue"
        cropPath = ""
        cropX = ""
        cropY = ""
        cropW = ""
        cropH = ""
        reviewStatus = ""
        evidenceNotes = ""
        notes = "visual fixture miss"
        pseudoGt_candidateId = "miss-4"
        pseudoGt_candidateType = "missCandidate"
        pseudoGt_tileFaceConfidence = "0.76"
        pseudoGt_tileSupportCount = "2"
        pseudoGt_faceVerificationConfidence = "0.84"
        pseudoGt_faceVerificationDistance = "0.11"
        pseudoGt_personConfidence = "0.50"
        pseudoGt_personUpperOverlap = "0.30"
        pseudoGt_fpProbability = "0.00"
        pseudoGt_missProbability = "0.85"
        pseudoGt_pseudoGtReason = "tile face appears without base candidate"
        suggestedLabel = "miss"
        candidateRule = "test-only-reference-not-final-gt"
        humanReviewInstruction = "fixture"
    }
) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $draftCsv

@(
    [pscustomobject]@{
        frame = "4"
        frameImagePath = (Join-Path $frameDir "frame-000004.png")
        overlayFrameImagePath = ""
        detectedCandidateCount = "0"
        candidateSummary = "miss fixture"
        missedFaceCount = ""
        missedFaceRowsAdded = ""
        reviewStatus = ""
        evidenceNotes = ""
        pseudoGtMissCandidateCount = "1"
        pseudoGtMissCandidateIds = "miss-4"
        suggestedMissedFaceRowsAdded = "1"
        candidateRule = "test-only-reference-not-final-gt"
        humanReviewInstruction = "fixture"
    }
) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $frameDraftCsv

$output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $scriptPath `
    -DraftReviewCsv $draftCsv `
    -DraftFullFrameReviewCsv $frameDraftCsv `
    -FrameSourceDir $frameDir `
    -OutputDir $outputDir `
    -FrameWidth 320 `
    -FrameHeight 180 `
    -Force `
    -Verify `
    -RequireAllVisuals 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "new-yolo-pseudo-gt-review-visual-package.ps1 failed: $($output | Out-String)"
}

$text = ($output | Out-String)
Assert-Contains "script selftest completed" $text "[YoloPseudoGtReviewVisual] all requested checks passed"

$visualDraftCsv = Join-Path $outputDir "pseudo-gt-full-gt-review-visual-draft.csv"
$visualFrameCsv = Join-Path $outputDir "pseudo-gt-full-frame-review-visual-draft.csv"
$decisionCsv = Join-Path $outputDir "pseudo-gt-review-decision-sheet.csv"
$frameDecisionCsv = Join-Path $outputDir "pseudo-gt-full-frame-review-decision-sheet.csv"
$indexHtml = Join-Path $outputDir "pseudo-gt-review-visual-index.html"
$reportPath = Join-Path $outputDir "pseudo-gt-review-visual-report.md"
Assert-FileNonEmpty "visual draft CSV" $visualDraftCsv
Assert-FileNonEmpty "visual frame draft CSV" $visualFrameCsv
Assert-FileNonEmpty "decision sheet CSV" $decisionCsv
Assert-FileNonEmpty "full-frame decision sheet CSV" $frameDecisionCsv
Assert-FileNonEmpty "visual index HTML" $indexHtml
Assert-FileNonEmpty "visual report" $reportPath

$rows = @(Import-Csv $visualDraftCsv)
$decisionRows = @(Import-Csv $decisionCsv)
$frameDecisionRows = @(Import-Csv $frameDecisionCsv)
if ($rows.Count -ne 3) {
    throw "Expected 3 visual draft rows, got $($rows.Count)"
}
if ($decisionRows.Count -ne 3) {
    throw "Expected 3 decision sheet rows, got $($decisionRows.Count)"
}
if ($frameDecisionRows.Count -ne 1) {
    throw "Expected 1 full-frame decision sheet row, got $($frameDecisionRows.Count)"
}

$finalFilled = @($rows | Where-Object {
    -not [string]::IsNullOrWhiteSpace($_.label) -or
    -not [string]::IsNullOrWhiteSpace($_.reviewStatus) -or
    -not [string]::IsNullOrWhiteSpace($_.evidenceNotes)
})
if ($finalFilled.Count -ne 0) {
    throw "Visual package filled final review fields unexpectedly: $($finalFilled.Count)"
}
Write-Host "[YoloPseudoGtReviewVisualVerify] pass final review fields stay blank"

$filledDecisionRows = @($decisionRows | Where-Object {
    -not [string]::IsNullOrWhiteSpace($_.label) -or
    -not [string]::IsNullOrWhiteSpace($_.reviewStatus) -or
    -not [string]::IsNullOrWhiteSpace($_.evidenceNotes)
})
if ($filledDecisionRows.Count -ne 0) {
    throw "Decision sheet filled final review fields unexpectedly: $($filledDecisionRows.Count)"
}
$filledFrameDecisionRows = @($frameDecisionRows | Where-Object {
    -not [string]::IsNullOrWhiteSpace($_.missedFaceCount) -or
    -not [string]::IsNullOrWhiteSpace($_.missedFaceRowsAdded) -or
    -not [string]::IsNullOrWhiteSpace($_.reviewStatus) -or
    -not [string]::IsNullOrWhiteSpace($_.evidenceNotes)
})
if ($filledFrameDecisionRows.Count -ne 0) {
    throw "Full-frame decision sheet filled final review fields unexpectedly: $($filledFrameDecisionRows.Count)"
}
Write-Host "[YoloPseudoGtReviewVisualVerify] pass decision sheets keep final fields blank"

foreach ($row in $rows) {
    Assert-FileNonEmpty "crop for frame $($row.frame)" $row.cropPath
    Assert-FileNonEmpty "overlay for frame $($row.frame)" $row.visualOverlayPath
    if ($row.visualReviewStatus -ne "visual-ready-test-only") {
        throw "Unexpected visualReviewStatus for frame $($row.frame): $($row.visualReviewStatus)"
    }
}

$indexText = Get-Content -Raw -Path $indexHtml
Assert-Contains "index shows false positives" $indexText "falsePositiveCandidate"
Assert-Contains "index shows supported faces" $indexText "supportedFaceCandidate"
Assert-Contains "index shows miss candidates" $indexText "missCandidate"
Assert-Contains "index warns suggested label is not final" $indexText "Do not copy"
Assert-Contains "index links decision sheet" $indexText "Decision sheet"

$reportText = Get-Content -Raw -Path $reportPath
Assert-Contains "report records zero missing visuals" $reportText "missingVisualRows: 0"
Assert-Contains "report records final fields blank" $reportText "finalFilledRows: 0"
Assert-Contains "report records decision sheet" $reportText "decisionCsv"

$missingFrameDir = Join-Path $work "missing-frames"
New-Item -ItemType Directory -Force -Path $missingFrameDir | Out-Null
$missingOutputDir = Join-Path $work "visual-missing"
$missingOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $scriptPath `
    -DraftReviewCsv $draftCsv `
    -DraftFullFrameReviewCsv $frameDraftCsv `
    -FrameSourceDir $missingFrameDir `
    -OutputDir $missingOutputDir `
    -FrameWidth 320 `
    -FrameHeight 180 `
    -Force `
    -Verify 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "new-yolo-pseudo-gt-review-visual-package.ps1 missing-visual run failed: $($missingOutput | Out-String)"
}
$missingReportPath = Join-Path $missingOutputDir "pseudo-gt-review-visual-report.md"
Assert-FileNonEmpty "missing visual report" $missingReportPath
$missingReportText = Get-Content -Raw -Path $missingReportPath
Assert-Contains "missing report records missing visual count" $missingReportText "missingVisualRows: 2"
Assert-Contains "missing report records missing visual frames" $missingReportText "missingVisualFrames: 17,20"
Assert-Contains "missing report records missing visual candidate ids" $missingReportText "missingVisualCandidateIds: fp-17,support-20"
Assert-Contains "missing report records short clip recovery rule" $missingReportText "rerun with the short problem clip"
Assert-Contains "missing report records video rerun command" $missingReportText "suggestedVideoRerunCommand"
Assert-Contains "missing report records video placeholder" $missingReportText "<short problem clip path>"

$clampFrameDir = Join-Path $work "clamp-frames"
New-Item -ItemType Directory -Force -Path $clampFrameDir | Out-Null
$clampFramePath = Join-Path $clampFrameDir "frame-000008.png"
Invoke-Tool $ffmpeg @(
    "-y",
    "-hide_banner",
    "-loglevel",
    "error",
    "-f",
    "lavfi",
    "-i",
    "testsrc2=size=320x180:rate=1:duration=1",
    "-frames:v",
    "1",
    $clampFramePath
) | Out-Null
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $clampFramePath)) {
    throw "ffmpeg clamp source frame fixture failed"
}

$clampDraftCsv = Join-Path $work "clamp-pseudo-gt-review-draft.csv"
@(
    [pscustomobject]@{
        frame = 8
        x = -20
        y = -15
        w = 1195
        h = 1082
        pseudoGt_candidateId = "clamp-out-of-frame"
        pseudoGt_candidateType = "falsePositiveCandidate"
        suggestedLabel = "nonface"
        label = ""
        reviewStatus = ""
        evidenceNotes = ""
    },
    [pscustomobject]@{
        frame = 8
        x = 500
        y = 250
        w = 50
        h = 50
        pseudoGt_candidateId = "clamp-beyond-frame"
        pseudoGt_candidateType = "falsePositiveCandidate"
        suggestedLabel = "nonface"
        label = ""
        reviewStatus = ""
        evidenceNotes = ""
    }
) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $clampDraftCsv

$clampOutputDir = Join-Path $work "visual-clamp"
$clampOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $scriptPath `
    -DraftReviewCsv $clampDraftCsv `
    -DraftFullFrameReviewCsv $frameDraftCsv `
    -FrameSourceDir $clampFrameDir `
    -OutputDir $clampOutputDir `
    -FrameWidth 1920 `
    -FrameHeight 1080 `
    -Force `
    -Verify `
    -RequireAllVisuals 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "new-yolo-pseudo-gt-review-visual-package.ps1 clamp run failed: $($clampOutput | Out-String)"
}
$clampDraftOut = Join-Path $clampOutputDir "pseudo-gt-full-gt-review-visual-draft.csv"
Assert-FileNonEmpty "clamp visual draft CSV" $clampDraftOut
$clampRows = @(Import-Csv $clampDraftOut)
if ($clampRows.Count -ne 2) {
    throw "Expected two clamp visual rows, got $($clampRows.Count)"
}
foreach ($clampRow in $clampRows) {
    Assert-FileNonEmpty "clamp crop" $clampRow.cropPath
    Assert-FileNonEmpty "clamp overlay" $clampRow.visualOverlayPath
    if ([int]$clampRow.cropW -gt 320 -or [int]$clampRow.cropH -gt 180) {
        throw "Expected clamp crop to use source frame size 320x180, got candidate=$($clampRow.pseudoGt_candidateId), cropW=$($clampRow.cropW), cropH=$($clampRow.cropH)"
    }
}
Write-Host "[YoloPseudoGtReviewVisualVerify] pass source-size clamp for out-of-frame crops"

$videoPath = Join-Path $work "source-video.mp4"
Invoke-Tool $ffmpeg @(
    "-y",
    "-hide_banner",
    "-loglevel",
    "error",
    "-f",
    "lavfi",
    "-i",
    "testsrc2=size=320x180:rate=10:duration=3",
    "-pix_fmt",
    "yuv420p",
    $videoPath
) | Out-Null
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $videoPath)) {
    throw "ffmpeg source video fixture failed"
}

$videoFallbackOutputDir = Join-Path $work "visual-video-fallback"
$videoFallbackOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $scriptPath `
    -DraftReviewCsv $draftCsv `
    -DraftFullFrameReviewCsv $frameDraftCsv `
    -FrameSourceDir $missingFrameDir `
    -OutputDir $videoFallbackOutputDir `
    -VideoPath $videoPath `
    -Force `
    -Verify `
    -RequireAllVisuals 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "new-yolo-pseudo-gt-review-visual-package.ps1 video fallback run failed: $($videoFallbackOutput | Out-String)"
}
$videoFallbackText = ($videoFallbackOutput | Out-String)
Assert-Contains "video fallback selftest completed" $videoFallbackText "[YoloPseudoGtReviewVisual] all requested checks passed"
$videoFallbackReportPath = Join-Path $videoFallbackOutputDir "pseudo-gt-review-visual-report.md"
$videoFallbackDraftCsv = Join-Path $videoFallbackOutputDir "pseudo-gt-full-gt-review-visual-draft.csv"
Assert-FileNonEmpty "video fallback report" $videoFallbackReportPath
Assert-FileNonEmpty "video fallback visual draft CSV" $videoFallbackDraftCsv
$videoFallbackReportText = Get-Content -Raw -Path $videoFallbackReportPath
Assert-Contains "video fallback report records zero missing visuals" $videoFallbackReportText "missingVisualRows: 0"
Assert-Contains "video fallback report records concrete video path" $videoFallbackReportText $videoPath
$videoFallbackRows = @(Import-Csv $videoFallbackDraftCsv)
foreach ($row in $videoFallbackRows) {
    Assert-FileNonEmpty "video fallback crop for frame $($row.frame)" $row.cropPath
    Assert-FileNonEmpty "video fallback overlay for frame $($row.frame)" $row.visualOverlayPath
    if ($row.visualReviewStatus -ne "visual-ready-test-only") {
        throw "Unexpected video fallback visualReviewStatus for frame $($row.frame): $($row.visualReviewStatus)"
    }
}
Write-Host "[YoloPseudoGtReviewVisualVerify] pass video fallback fills missing visuals"

Write-Host "[YoloPseudoGtReviewVisualVerify] all requested checks passed"
