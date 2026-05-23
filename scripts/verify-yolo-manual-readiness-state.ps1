param(
    [string]$PlanPath = "AUTO_MOSAIC_QUALITY_SPEED_PLAN.md",
    [string]$FullGtReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-gt-review.csv",
    [string]$FullFrameReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-frame-review.csv",
    [string]$ReviewIndex = ".tmp\yolo-full-gt\review-package-smoke\review-index.html",
    [string]$CandidateReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-gt-review-reviewed-candidate.csv",
    [string]$CandidateFullFrameReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-frame-review-reviewed-candidate.csv",
    [string]$GuiChecklistCsv = ".tmp\yolo-gui-smoke\manual-smoke-checklist.csv",
    [string]$TenMinuteOutputPath = ".tmp\srcTest-smoke\smoke-0200-600s_blur.mp4",
    [string]$TenMinuteLogPath = ".tmp\yolo-ten-minute\yolo-ten-minute-20260523-000044.log",
    [string]$IncompleteBaselineFullLogPath = ".tmp\yolo-ten-minute-baseline-full\yolo-ten-minute-baseline-only-20260523-032108.log"
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

    Write-Host "[YoloManualReadinessVerify] pass $Name"
}

function Assert-FileNonEmpty {
    param(
        [string]$Name,
        [string]$Path
    )

    $resolved = Resolve-RepoPath $Path
    if (-not (Test-Path $resolved)) {
        throw "$Name not found: $resolved"
    }

    $item = Get-Item $resolved
    if ($item -isnot [IO.FileInfo]) {
        throw "$Name is not a file: $resolved"
    }

    if ($item.Length -le 0) {
        throw "$Name is empty: $resolved"
    }

    Write-Host "[YoloManualReadinessVerify] pass $Name"
    return $resolved
}

function Assert-PathColumnFiles {
    param(
        [object[]]$Rows,
        [string]$ColumnName,
        [bool]$Required
    )

    foreach ($row in $Rows) {
        $value = if ($null -ne $row.PSObject.Properties[$ColumnName]) { $row.$ColumnName } else { "" }
        if ([string]::IsNullOrWhiteSpace($value)) {
            if ($Required) {
                throw "$ColumnName is required at frame=$($row.frame)"
            }

            continue
        }

        Assert-FileNonEmpty "$ColumnName artifact frame=$($row.frame)" $value | Out-Null
    }
}

function Assert-ManualFullGtPackage {
    $reviewCsv = Assert-FileNonEmpty "full GT review CSV" $FullGtReviewCsv
    $frameCsv = Assert-FileNonEmpty "full-frame review CSV" $FullFrameReviewCsv
    Assert-FileNonEmpty "full GT review index" $ReviewIndex | Out-Null

    $reviewRows = @(Import-Csv $reviewCsv)
    if ($reviewRows.Count -lt 1) {
        throw "full GT review CSV has no rows"
    }

    $unblankLabels = @($reviewRows | Where-Object {
        $null -ne $_.PSObject.Properties["label"] -and -not [string]::IsNullOrWhiteSpace($_.label)
    })
    if ($unblankLabels.Count -ne 0) {
        throw "manual full GT review CSV should remain unreviewed until a human fills labels: $($unblankLabels.Count) labeled rows"
    }

    Assert-PathColumnFiles $reviewRows "cropPath" $true
    Write-Host "[YoloManualReadinessVerify] pass full GT review rows=$($reviewRows.Count), labels=pending"

    $frameRows = @(Import-Csv $frameCsv)
    if ($frameRows.Count -lt 1) {
        throw "full-frame review CSV has no rows"
    }

    $reviewedFrameRows = @($frameRows | Where-Object {
        $null -ne $_.PSObject.Properties["missedFaceCount"] -and -not [string]::IsNullOrWhiteSpace($_.missedFaceCount)
    })
    if ($reviewedFrameRows.Count -ne 0) {
        throw "manual full-frame review CSV should remain pending until a human fills missedFaceCount: $($reviewedFrameRows.Count) reviewed rows"
    }

    Assert-PathColumnFiles $frameRows "frameImagePath" $true
    Assert-PathColumnFiles $frameRows "overlayFrameImagePath" $false
    Write-Host "[YoloManualReadinessVerify] pass full-frame review rows=$($frameRows.Count), status=pending"
}

function Assert-AiCandidatePackage {
    $candidateCsv = Assert-FileNonEmpty "AI candidate full GT review CSV" $CandidateReviewCsv
    $candidateFrameCsv = Assert-FileNonEmpty "AI candidate full-frame review CSV" $CandidateFullFrameReviewCsv

    $candidateRows = @(Import-Csv $candidateCsv)
    if ($candidateRows.Count -lt 1) {
        throw "AI candidate review CSV has no rows"
    }

    foreach ($row in $candidateRows) {
        if ([string]::IsNullOrWhiteSpace($row.label)) {
            throw "AI candidate review row missing label at frame=$($row.frame), sourcePredictionId=$($row.sourcePredictionId)"
        }

        if ($row.reviewStatus -ne "ai-candidate") {
            throw "AI candidate reviewStatus should be ai-candidate at frame=$($row.frame), actual=$($row.reviewStatus)"
        }
    }

    Assert-PathColumnFiles $candidateRows "cropPath" $true
    Write-Host "[YoloManualReadinessVerify] pass AI candidate review rows=$($candidateRows.Count)"

    $candidateFrameRows = @(Import-Csv $candidateFrameCsv)
    if ($candidateFrameRows.Count -lt 1) {
        throw "AI candidate full-frame review CSV has no rows"
    }

    foreach ($row in $candidateFrameRows) {
        if ([string]::IsNullOrWhiteSpace($row.missedFaceCount)) {
            throw "AI candidate full-frame row missing missedFaceCount at frame=$($row.frame)"
        }

        if ($row.reviewStatus -ne "ai-candidate") {
            throw "AI candidate full-frame reviewStatus should be ai-candidate at frame=$($row.frame), actual=$($row.reviewStatus)"
        }
    }

    Assert-PathColumnFiles $candidateFrameRows "frameImagePath" $true
    Write-Host "[YoloManualReadinessVerify] pass AI candidate full-frame rows=$($candidateFrameRows.Count)"
}

function Assert-GuiChecklistReady {
    $checklist = Assert-FileNonEmpty "GUI manual checklist" $GuiChecklistCsv
    $rows = @(Import-Csv $checklist)
    $requiredSteps = @(
        "open-video",
        "select-yolo-backend",
        "run-yolo-auto-detect",
        "preview-result",
        "manual-edit",
        "export",
        "reopen-state"
    )

    foreach ($step in $requiredSteps) {
        $row = $rows | Where-Object { $_.stepId -eq $step } | Select-Object -First 1
        if ($null -eq $row) {
            throw "GUI manual checklist missing step: $step"
        }

        foreach ($column in @("status", "evidenceType", "artifactPath", "evidence", "notes")) {
            if ($null -eq $row.PSObject.Properties[$column]) {
                throw "GUI manual checklist row missing column '$column': $step"
            }
        }
    }

    $filledStatuses = @($rows | Where-Object { -not [string]::IsNullOrWhiteSpace($_.status) })
    if ($filledStatuses.Count -ne 0) {
        throw "GUI manual checklist should remain pending until real GUI smoke is performed: $($filledStatuses.Count) filled statuses"
    }

    Write-Host "[YoloManualReadinessVerify] pass GUI manual checklist rows=$($rows.Count), status=pending"
}

function Assert-TenMinuteArtifactsReady {
    Assert-FileNonEmpty "10-minute YOLO output" $TenMinuteOutputPath | Out-Null
    $tenMinuteLog = Assert-FileNonEmpty "10-minute YOLO log" $TenMinuteLogPath
    $baselineLog = Assert-FileNonEmpty "incomplete FaceONNX baseline full log" $IncompleteBaselineFullLogPath

    $tenMinuteText = Get-Content -Raw -Path $tenMinuteLog
    Assert-Contains "10-minute log has yolo detector" $tenMinuteText "detector=YoloFaceOnnxDetector"
    Assert-Contains "10-minute log has export summary" $tenMinuteText "[ExportRunSummary]"
    Assert-Contains "10-minute log has smoke output marker" $tenMinuteText "[Smoke] label=optimized-track-1-scale-1-cpu-yolo"

    $baselineText = Get-Content -Raw -Path $baselineLog
    Assert-Contains "baseline full attempt has baseline-only mode" $baselineText "baselineOnly=True"
    Assert-Contains "baseline full attempt has pipe-single mode" $baselineText "[AutoMask] mode=pipe-single"
    if ($baselineText.Contains("[YoloTenMinuteFull] complete")) {
        throw "incomplete baseline full log unexpectedly has complete marker"
    }

    Write-Host "[YoloManualReadinessVerify] pass 10-minute artifacts ready"
}

$planPathResolved = Assert-FileNonEmpty "plan document" $PlanPath
$plan = Get-Content -Raw -Path $planPathResolved
Assert-Contains "plan keeps goal incomplete" $plan "complete=false"
Assert-Contains "plan keeps full GT pending" $plan "full-gt-label"
Assert-Contains "plan keeps GUI smoke pending" $plan "gui-smoke"
Assert-Contains "plan records ten minute full not required after extended fail" $plan "ten-minute-full=not-required-after-extended-fail"

Assert-ManualFullGtPackage
Assert-AiCandidatePackage
Assert-GuiChecklistReady
Assert-TenMinuteArtifactsReady

Write-Host "[YoloManualReadinessVerify] all requested checks passed"
