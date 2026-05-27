param(
    [string]$PlanPath = "AUTO_MOSAIC_QUALITY_SPEED_PLAN.md",
    [string]$FullGtReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-gt-review.csv",
    [string]$FullFrameReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-frame-review.csv",
    [string]$ReviewIndex = ".tmp\yolo-full-gt\review-package-smoke\review-index.html",
    [string]$CandidateReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-gt-review-reviewed-candidate.csv",
    [string]$CandidateFullFrameReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-frame-review-reviewed-candidate.csv",
    [string]$GuiChecklistCsv = ".tmp\yolo-gui-smoke\manual-smoke-checklist.csv",
    [string]$GuiSmokeVerifier = "scripts\verify-yolo-gui-smoke-state.ps1",
    [string]$FullGtReviewedVerifier = "scripts\verify-yolo-full-gt-reviewed-state.ps1",
    [string]$FullGtPredictionCsv = "",
    [string]$FullGtPredictionLog = ".tmp\yolo-ten-minute-detection-smoke\yolo-ten-minute-yolo-only-20260523-022022.log",
    [double]$FullGtMinIou = 0.50,
    [int]$FullGtMaxMisses = 0,
    [int]$FullGtMaxFalsePositives = 0,
    [int]$FullGtMaxLowIou = 0,
    [switch]$AllowQualityGateFailure,
    [switch]$AllowCompletedFullGt,
    [switch]$AllowCompletedGuiSmoke
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
            $source = if ($null -ne $row.PSObject.Properties["source"]) { $row.source } else { "" }
            $sourcePredictionId = if ($null -ne $row.PSObject.Properties["sourcePredictionId"]) { $row.sourcePredictionId } else { "" }
            if ($ColumnName -eq "cropPath" -and $source -eq "manual-missed" -and [string]::IsNullOrWhiteSpace($sourcePredictionId)) {
                continue
            }

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
    if ($unblankLabels.Count -ne 0 -and -not $AllowCompletedFullGt) {
        throw "manual full GT review CSV should remain unreviewed until a human fills labels: $($unblankLabels.Count) labeled rows"
    }
    if ($unblankLabels.Count -ne 0 -and $unblankLabels.Count -ne $reviewRows.Count) {
        throw "manual full GT review CSV is partially labeled: labeled=$($unblankLabels.Count), rows=$($reviewRows.Count)"
    }

    Assert-PathColumnFiles $reviewRows "cropPath" $true
    $labelState = if ($unblankLabels.Count -eq 0) { "pending" } else { "completed" }
    Write-Host "[YoloManualReadinessVerify] pass full GT review rows=$($reviewRows.Count), labels=$labelState"

    $frameRows = @(Import-Csv $frameCsv)
    if ($frameRows.Count -lt 1) {
        throw "full-frame review CSV has no rows"
    }

    $reviewedFrameRows = @($frameRows | Where-Object {
        $null -ne $_.PSObject.Properties["missedFaceCount"] -and -not [string]::IsNullOrWhiteSpace($_.missedFaceCount)
    })
    if ($reviewedFrameRows.Count -ne 0 -and -not $AllowCompletedFullGt) {
        throw "manual full-frame review CSV should remain pending until a human fills missedFaceCount: $($reviewedFrameRows.Count) reviewed rows"
    }
    if ($reviewedFrameRows.Count -ne 0 -and $reviewedFrameRows.Count -ne $frameRows.Count) {
        throw "manual full-frame review CSV is partially reviewed: reviewed=$($reviewedFrameRows.Count), rows=$($frameRows.Count)"
    }

    Assert-PathColumnFiles $frameRows "frameImagePath" $true
    Assert-PathColumnFiles $frameRows "overlayFrameImagePath" $false
    $frameState = if ($reviewedFrameRows.Count -eq 0) { "pending" } else { "completed" }
    Write-Host "[YoloManualReadinessVerify] pass full-frame review rows=$($frameRows.Count), status=$frameState"

    if ($labelState -eq "completed" -and $frameState -eq "completed") {
        $reviewedVerifierPath = Assert-FileNonEmpty "full GT reviewed verifier" $FullGtReviewedVerifier
        $reviewedArgs = @(
            "-ReviewCsv", $reviewCsv,
            "-FullFrameReviewCsv", $frameCsv,
            "-MinIou", "$FullGtMinIou",
            "-MaxMisses", "$FullGtMaxMisses",
            "-MaxFalsePositives", "$FullGtMaxFalsePositives",
            "-MaxLowIou", "$FullGtMaxLowIou",
            "-RequireEvidence",
            "-RequireFullFrameReview",
            "-RequireArtifacts"
        )
        if ($AllowQualityGateFailure) {
            $reviewedArgs += "-AllowQualityGateFailure"
        }

        if (-not [string]::IsNullOrWhiteSpace($FullGtPredictionCsv)) {
            $reviewedArgs += @("-PredictionCsv", $FullGtPredictionCsv)
        }
        else {
            $reviewedArgs += @("-PredictionLog", $FullGtPredictionLog)
        }

        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $reviewedVerifierPath @reviewedArgs
        if ($LASTEXITCODE -ne 0) {
            throw "Full GT reviewed verifier failed with exit code $LASTEXITCODE"
        }

        Write-Host "[YoloManualReadinessVerify] pass completed full GT reviewed gate"
    }
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
        "download-yolo-model",
        "run-yolo-auto-detect",
        "preview-result",
        "preview-track-hold",
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
    if ($AllowCompletedGuiSmoke) {
        $guiSmokeVerifierPath = Assert-FileNonEmpty "GUI smoke verifier" $GuiSmokeVerifier
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $guiSmokeVerifierPath -ChecklistCsv $checklist -RequireManualPass
        if ($LASTEXITCODE -ne 0) {
            throw "GUI smoke verifier failed with exit code $LASTEXITCODE"
        }
    }
    elseif ($filledStatuses.Count -ne 0) {
        $guiSmokeVerifierPath = Assert-FileNonEmpty "GUI smoke verifier" $GuiSmokeVerifier
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $guiSmokeVerifierPath -ChecklistCsv $checklist -AllowPartialManualPass
        if ($LASTEXITCODE -ne 0) {
            throw "GUI smoke partial verifier failed with exit code $LASTEXITCODE"
        }
    }

    $statusState = if ($filledStatuses.Count -eq 0) {
        "pending"
    }
    elseif ($filledStatuses.Count -eq $rows.Count) {
        "completed"
    }
    else {
        "partial"
    }
    Write-Host "[YoloManualReadinessVerify] pass GUI manual checklist rows=$($rows.Count), status=$statusState"
}

$planPathResolved = Assert-FileNonEmpty "plan document" $PlanPath
$plan = Get-Content -Raw -Path $planPathResolved
Assert-Contains "plan keeps goal incomplete" $plan "complete=false"
if ($plan.Contains("full-gt-reviewed=pass")) {
    Assert-Contains "plan records full GT reviewed" $plan "full-gt-reviewed=pass"
}
else {
    Assert-Contains "plan keeps full GT pending" $plan "full-gt-label"
}
Assert-Contains "plan keeps GUI smoke pending" $plan "gui-smoke"
Assert-Contains "plan records ten minute full not required after extended fail" $plan "ten-minute-full=not-required-after-extended-fail"
Assert-Contains "plan records short-span-only cleanup" $plan "short-span-only-goal=pass"

Assert-ManualFullGtPackage
Assert-AiCandidatePackage
Assert-GuiChecklistReady

Write-Host "[YoloManualReadinessVerify] all requested checks passed"
