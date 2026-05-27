param(
    [string]$PlanDocument = "AUTO_MOSAIC_QUALITY_SPEED_PLAN.md",
    [string]$FullGtReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-gt-review.csv",
    [string]$FullFrameReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-frame-review.csv",
    [string]$GuiChecklistCsv = ".tmp\yolo-gui-smoke\manual-smoke-checklist.csv",
    [string]$ManualGateSummary = ".tmp\yolo-manual-gates\manual-gate-summary.md",
    [string]$OutputPath = ".tmp\yolo-manual-gates\goal-evidence-report.md",
    [string]$PredictionCsv = "",
    [string]$PredictionLog = ".tmp\yolo-ten-minute-detection-smoke\yolo-ten-minute-yolo-only-20260523-022022.log",
    [string]$FinalMaskCleanupLog = ".tmp\yolo-final-mask-cleanup\verify-output.log",
    [string]$PseudoGtCsv = ".tmp\yolo-pseudo-gt\pseudo-gt-candidates.csv",
    [string]$PseudoGtReviewQueueCsv = ".tmp\yolo-pseudo-gt\pseudo-gt-review-queue.csv",
    [string]$PseudoGtReviewClosureCsv = ".tmp\yolo-pseudo-gt\pseudo-gt-review-closure.csv",
    [double]$MinIou = 0.50,
    [int]$MaxMisses = 0,
    [int]$MaxFalsePositives = 0,
    [int]$MaxLowIou = 0,
    [switch]$AllowQualityGateFailure,
    [switch]$RequireComplete,
    [switch]$Verify
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

function Read-RequiredFile {
    param([string]$Path)

    $resolved = Resolve-RepoPath $Path
    if (-not (Test-Path $resolved)) {
        throw "Required file not found: $resolved"
    }

    return Get-Content -Raw -Path $resolved
}

function Read-RequiredCsv {
    param([string]$Path)

    $resolved = Resolve-RepoPath $Path
    if (-not (Test-Path $resolved)) {
        throw "Required CSV not found: $resolved"
    }

    return @(Import-Csv $resolved)
}

function Count-Filled {
    param(
        [object[]]$Rows,
        [string]$Column
    )

    return @($Rows | Where-Object {
        $null -ne $_.PSObject.Properties[$Column] -and -not [string]::IsNullOrWhiteSpace($_.$Column)
    }).Count
}

function Count-Matching {
    param(
        [object[]]$Rows,
        [string]$Column,
        [string]$Value
    )

    return @($Rows | Where-Object {
        $null -ne $_.PSObject.Properties[$Column] -and $_.$Column.Trim().ToLowerInvariant() -eq $Value
    }).Count
}

function Count-ColumnPresent {
    param(
        [object[]]$Rows,
        [string]$Column
    )

    return @($Rows | Where-Object {
        $null -ne $_.PSObject.Properties[$Column] -and -not [string]::IsNullOrWhiteSpace([string]$_.$Column)
    }).Count
}

function Count-AnyColumnPresent {
    param(
        [object[]]$Rows,
        [string[]]$Columns
    )

    return @($Rows | Where-Object {
        foreach ($column in $Columns) {
            if ($null -ne $_.PSObject.Properties[$column] -and -not [string]::IsNullOrWhiteSpace([string]$_.$column)) {
                return $true
            }
        }

        return $false
    }).Count
}

function Count-NonZeroNumber {
    param(
        [object[]]$Rows,
        [string]$Column
    )

    return @($Rows | Where-Object {
        if ($null -eq $_.PSObject.Properties[$Column]) {
            return $false
        }

        $parsed = 0.0
        return [double]::TryParse(
            [string]$_.$Column,
            [System.Globalization.NumberStyles]::Float,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [ref]$parsed) -and $parsed -gt 0.0
    }).Count
}

function Test-ReviewedStatus {
    param([string]$Status)

    if ([string]::IsNullOrWhiteSpace($Status)) {
        return $false
    }

    return $Status.Trim().ToLowerInvariant() -in @("pass", "reviewed", "complete", "completed", "closed", "done")
}

function Test-PseudoGtClosureStrictlyClosed {
    param([object]$Row)

    if ($null -eq $Row.PSObject.Properties["closureStatus"] -or
        $Row.closureStatus.Trim().ToLowerInvariant() -ne "closed") {
        return $false
    }

    if ($null -eq $Row.PSObject.Properties["reviewStatus"] -or
        -not (Test-ReviewedStatus ([string]$Row.reviewStatus))) {
        return $false
    }

    if ($null -ne $Row.PSObject.Properties["expectedReviewLabel"] -and
        $null -ne $Row.PSObject.Properties["reviewLabel"] -and
        -not [string]::IsNullOrWhiteSpace([string]$Row.expectedReviewLabel) -and
        $Row.expectedReviewLabel.Trim().ToLowerInvariant() -ne $Row.reviewLabel.Trim().ToLowerInvariant()) {
        return $false
    }

    $candidateType = if ($null -ne $Row.PSObject.Properties["candidateType"]) {
        $Row.candidateType.Trim().ToLowerInvariant()
    }
    else {
        ""
    }

    if ($candidateType -eq "misscandidate") {
        if ($null -eq $Row.PSObject.Properties["fullFrameReviewStatus"] -or
            -not (Test-ReviewedStatus ([string]$Row.fullFrameReviewStatus))) {
            return $false
        }

        if ($null -eq $Row.PSObject.Properties["fullFrameMissedRowsAdded"]) {
            return $false
        }

        $missedRowsAdded = 0
        if (-not [int]::TryParse([string]$Row.fullFrameMissedRowsAdded, [ref]$missedRowsAdded) -or
            $missedRowsAdded -le 0) {
            return $false
        }
    }

    return $true
}

function Get-MaxNumber {
    param(
        [object[]]$Rows,
        [string]$Column
    )

    $max = 0.0
    foreach ($row in $Rows) {
        if ($null -eq $row.PSObject.Properties[$Column]) {
            continue
        }

        $parsed = 0.0
        if ([double]::TryParse(
                [string]$row.$Column,
                [System.Globalization.NumberStyles]::Float,
                [System.Globalization.CultureInfo]::InvariantCulture,
                [ref]$parsed) -and $parsed -gt $max) {
            $max = $parsed
        }
    }

    return $max
}

function Format-Double {
    param([double]$Value)
    return $Value.ToString("0.######", [System.Globalization.CultureInfo]::InvariantCulture)
}

function Read-OptionalCsv {
    param([string]$Path)

    $resolved = Resolve-RepoPath $Path
    if (-not (Test-Path $resolved)) {
        return @()
    }

    return @(Import-Csv $resolved)
}

function Read-OptionalText {
    param([string]$Path)

    $resolved = Resolve-RepoPath $Path
    if (-not (Test-Path $resolved)) {
        return $null
    }

    return Get-Content -Raw -Path $resolved
}

function Read-LogIntValue {
    param(
        [string]$Line,
        [string]$Name,
        [int]$DefaultValue = 0
    )

    $match = [regex]::Match(
        $Line,
        "(?:^|,\s*)$([regex]::Escape($Name))=(?<value>-?\d+)",
        [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $match.Success) {
        return $DefaultValue
    }

    return [int]::Parse($match.Groups["value"].Value, [System.Globalization.CultureInfo]::InvariantCulture)
}

function Get-FinalMaskCleanupEvidence {
    param([string]$Path)

    $text = Read-OptionalText $Path
    $resolved = Resolve-RepoPath $Path
    if ([string]::IsNullOrWhiteSpace($text)) {
        return [pscustomobject]@{
            Status = "pending-log"
            Evidence = "verify-yolo-final-mask-cleanup output log not found; run scripts/verify-yolo-final-mask-cleanup.ps1 before strict completion; source=$resolved"
            DriftingStrongCarryRemoved = 0
            AreaChangedStrongCarryProtected = 0
            StickyStrongCarryRemoved = 0
            SceneCutCarryRemoved = 0
        }
    }

    $line = @(($text -split "`r?`n") | Where-Object { $_.StartsWith("[YoloFinalMaskCleanupVerify]") } | Select-Object -Last 1)
    if ($line.Count -eq 0 -or [string]::IsNullOrWhiteSpace($line[-1])) {
        return [pscustomobject]@{
            Status = "invalid-log"
            Evidence = "verify-yolo-final-mask-cleanup output log has no YoloFinalMaskCleanupVerify row; source=$resolved"
            DriftingStrongCarryRemoved = 0
            AreaChangedStrongCarryProtected = 0
            StickyStrongCarryRemoved = 0
            SceneCutCarryRemoved = 0
        }
    }

    $summaryLine = $line[-1]
    $stickyStrongCarryRemoved = Read-LogIntValue $summaryLine "stickyStrongCarryRemoved"
    $driftingStrongCarryRemoved = Read-LogIntValue $summaryLine "driftingStrongCarryRemoved"
    $areaChangedStrongCarryProtected = Read-LogIntValue $summaryLine "areaChangedStrongCarryProtected"
    $sceneCutCarryRemoved = Read-LogIntValue $summaryLine "sceneCutCarryRemoved"
    $sceneCarryAnchorRefillBlocked = Read-LogIntValue $summaryLine "sceneCarryAnchorRefillBlocked"
    $partialSceneCarryRefillBlocked = Read-LogIntValue $summaryLine "partialSceneCarryRefillBlocked"

    $status = if ($stickyStrongCarryRemoved -gt 0 -and
        $driftingStrongCarryRemoved -gt 0 -and
        $areaChangedStrongCarryProtected -gt 0 -and
        $sceneCutCarryRemoved -gt 0) {
        "pass"
    }
    else {
        "fail"
    }

    return [pscustomobject]@{
        Status = $status
        Evidence = "source=$resolved, stickyStrongCarryRemoved=$stickyStrongCarryRemoved, driftingStrongCarryRemoved=$driftingStrongCarryRemoved, areaChangedStrongCarryProtected=$areaChangedStrongCarryProtected, sceneCutCarryRemoved=$sceneCutCarryRemoved, sceneCarryAnchorRefillBlocked=$sceneCarryAnchorRefillBlocked, partialSceneCarryRefillBlocked=$partialSceneCarryRefillBlocked"
        DriftingStrongCarryRemoved = $driftingStrongCarryRemoved
        AreaChangedStrongCarryProtected = $areaChangedStrongCarryProtected
        StickyStrongCarryRemoved = $stickyStrongCarryRemoved
        SceneCutCarryRemoved = $sceneCutCarryRemoved
    }
}

function Escape-Cell {
    param([string]$Value)

    return ($Value -replace "\|", "\|") -replace "`r?`n", " "
}

function Assert-ReportContains {
    param(
        [string]$Name,
        [string]$Text,
        [string]$Expected
    )

    if (-not $Text.Contains($Expected)) {
        throw "$Name missing text: $Expected"
    }

    Write-Host "[YoloGoalEvidenceReport] pass $Name"
}

function Get-GoalAuditMarker {
    param([string]$Text)

    $match = [regex]::Match(
        $Text,
        '<!--\s*(?<marker>yolo-goal-audit-state:\s*.*?)\s*-->',
        [System.Text.RegularExpressions.RegexOptions]::Singleline)

    if (-not $match.Success) {
        throw "Plan is missing yolo-goal-audit-state marker."
    }

    return $match.Groups["marker"].Value
}

function Invoke-FullGtQualityGate {
    param(
        [string]$ReviewCsv,
        [string]$FrameReviewCsv
    )

    $verifier = Join-Path $repo "scripts\verify-yolo-full-gt-reviewed-state.ps1"
    if (-not (Test-Path $verifier)) {
        throw "Full GT reviewed verifier not found: $verifier"
    }

    $args = @(
        "-ReviewCsv", (Resolve-RepoPath $ReviewCsv),
        "-FullFrameReviewCsv", (Resolve-RepoPath $FrameReviewCsv),
        "-RequireFullFrameReview",
        "-RequireEvidence",
        "-RequireArtifacts",
        "-MinIou", "$MinIou",
        "-MaxMisses", "$MaxMisses",
        "-MaxFalsePositives", "$MaxFalsePositives",
        "-MaxLowIou", "$MaxLowIou"
    )

    if (-not [string]::IsNullOrWhiteSpace($PredictionCsv)) {
        $args += @("-PredictionCsv", (Resolve-RepoPath $PredictionCsv))
    }
    else {
        $args += @("-PredictionLog", (Resolve-RepoPath $PredictionLog))
    }

    if ($AllowQualityGateFailure) {
        $args += "-AllowQualityGateFailure"
    }

    $oldErrorAction = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $verifier @args 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $oldErrorAction
    }

    $text = ($output | Out-String)
    $summaryLine = (($text -split "`r?`n") | Where-Object { $_.StartsWith("[YoloFullGtLabelSummary]") } | Select-Object -Last 1)
    $gateLine = (($text -split "`r?`n") | Where-Object { $_.StartsWith("[YoloFullGtLabelQualityGate]") -and $_.Contains("passed=") } | Select-Object -Last 1)
    $failureAllowed = $text.Contains("failureAllowed=True")
    $passed = $gateLine -match "passed=True"

    [pscustomobject]@{
        ExitCode = $exitCode
        Passed = $passed
        FailureAllowed = $failureAllowed
        SummaryLine = if ($summaryLine) { $summaryLine } else { "n/a" }
        GateLine = if ($gateLine) { $gateLine } else { "n/a" }
        Output = $text
    }
}

$plan = Read-RequiredFile $PlanDocument
$goalMarker = Get-GoalAuditMarker $plan
$summary = Read-RequiredFile $ManualGateSummary
$fullGtRows = @(Read-RequiredCsv $FullGtReviewCsv)
$fullFrameRows = @(Read-RequiredCsv $FullFrameReviewCsv)
$guiRows = @(Read-RequiredCsv $GuiChecklistCsv)

$fullGtReviewed = Count-Filled $fullGtRows "reviewStatus"
$fullGtLabels = Count-Filled $fullGtRows "label"
$fullFrameReviewed = Count-Filled $fullFrameRows "reviewStatus"
$fullFrameMissCounts = Count-Filled $fullFrameRows "missedFaceCount"
$guiPassed = Count-Matching $guiRows "status" "pass"
$guiArtifacts = Count-Filled $guiRows "artifactPath"
$previewTrackHoldPassed = @($guiRows | Where-Object {
    $null -ne $_.PSObject.Properties["stepId"] -and
    $null -ne $_.PSObject.Properties["status"] -and
    $_.stepId -eq "preview-track-hold" -and
    $_.status.Trim().ToLowerInvariant() -eq "pass"
}).Count -gt 0

$planIncomplete = $goalMarker.Contains("complete=false") -and (
    $goalMarker.Contains("remaining=full-gt-label,gui-smoke") -or
    $goalMarker.Contains("remaining=gui-smoke"))
$planComplete = $goalMarker.Contains("complete=true") -and $goalMarker.Contains("remaining=none")
if (-not $planIncomplete -and -not $planComplete) {
    throw "Goal audit marker must contain complete=false with a supported remaining gate or complete=true/remaining=none."
}

$fullGtFilled = $fullGtRows.Count -gt 0 -and
    $fullGtReviewed -eq $fullGtRows.Count -and
    $fullGtLabels -eq $fullGtRows.Count -and
    $fullFrameRows.Count -gt 0 -and
    $fullFrameReviewed -eq $fullFrameRows.Count -and
    $fullFrameMissCounts -eq $fullFrameRows.Count
$guiFilled = $guiRows.Count -gt 0 -and $guiPassed -eq $guiRows.Count -and $guiArtifacts -eq $guiRows.Count

$pseudoGtRows = @(Read-OptionalCsv $PseudoGtCsv)
$pseudoGtReviewQueueRows = @(Read-OptionalCsv $PseudoGtReviewQueueCsv)
$pseudoGtClosureRows = @(Read-OptionalCsv $PseudoGtReviewClosureCsv)
$pseudoGtSupportedRows = Count-Matching $pseudoGtRows "candidateType" "supportedfacecandidate"
$pseudoGtFalsePositiveRows = Count-Matching $pseudoGtRows "candidateType" "falsepositivecandidate"
$pseudoGtMissRows = Count-Matching $pseudoGtRows "candidateType" "misscandidate"
$pseudoGtQueueFalsePositiveRows = Count-Matching $pseudoGtReviewQueueRows "candidateType" "falsepositivecandidate"
$pseudoGtQueueMissRows = Count-Matching $pseudoGtReviewQueueRows "candidateType" "misscandidate"
$pseudoGtQueueSupportedRows = Count-Matching $pseudoGtReviewQueueRows "candidateType" "supportedfacecandidate"
$pseudoGtQueueAuxiliaryBoostedRows = Count-NonZeroNumber $pseudoGtReviewQueueRows "auxiliaryPriorityBoost"
$pseudoGtQueueMaxAuxiliaryBoost = Get-MaxNumber $pseudoGtReviewQueueRows "auxiliaryPriorityBoost"
$pseudoGtQueueTopRows = @($pseudoGtReviewQueueRows | Select-Object -First 5 | ForEach-Object {
        "$($_.reviewRank):$($_.frame):$($_.candidateType):$($_.candidateId):$($_.reviewPriorityScore):aux=$($_.auxiliaryPriorityBoost)"
    })
$pseudoGtRowsWithSupportEvidence = Count-NonZeroNumber $pseudoGtRows "supportFrameCount"
$pseudoGtRowsWithPersonEvidence = Count-NonZeroNumber $pseudoGtRows "personUpperOverlap"
$pseudoGtRowsWithGeometryEvidence = Count-AnyColumnPresent $pseudoGtRows @("bestIou", "centerDistanceRatio", "areaChangeRatio")
$pseudoGtRowsWithAreaRatioEvidence = Count-ColumnPresent $pseudoGtRows "areaChangeRatio"
$pseudoGtClosedRows = @($pseudoGtClosureRows | Where-Object { Test-PseudoGtClosureStrictlyClosed $_ }).Count
$pseudoGtLooseClosedRows = @($pseudoGtClosureRows | Where-Object {
    $null -ne $_.PSObject.Properties["closureStatus"] -and $_.closureStatus.Trim().ToLowerInvariant() -eq "closed"
}).Count
$pseudoGtClosureRowsWithSupportEvidence = Count-NonZeroNumber $pseudoGtClosureRows "supportFrameCount"
$pseudoGtClosureRowsWithPersonEvidence = Count-NonZeroNumber $pseudoGtClosureRows "personUpperOverlap"
$pseudoGtClosureRowsWithGeometryEvidence = Count-AnyColumnPresent $pseudoGtClosureRows @("bestIou", "centerDistanceRatio", "areaChangeRatio")
$pseudoGtClosureRowsWithAreaRatioEvidence = Count-ColumnPresent $pseudoGtClosureRows "areaChangeRatio"
$finalMaskCleanupEvidence = Get-FinalMaskCleanupEvidence $FinalMaskCleanupLog
$pseudoGtOpenRows = if ($pseudoGtRows.Count -eq 0) {
    0
}
else {
    [Math]::Max(0, $pseudoGtRows.Count - $pseudoGtClosedRows)
}
$pseudoGtStatus = if ($pseudoGtRows.Count -eq 0) {
    "skipped-no-candidates"
}
elseif ($pseudoGtClosureRows.Count -eq $pseudoGtRows.Count -and $pseudoGtOpenRows -eq 0) {
    "closed"
}
else {
    "pending-human"
}
$pseudoGtReviewQueueStatus = if ($pseudoGtRows.Count -eq 0) {
    "skipped-no-candidates"
}
elseif ($pseudoGtReviewQueueRows.Count -gt 0) {
    "prepared"
}
else {
    "missing"
}

$qualityGate = $null
if ($fullGtFilled) {
    $qualityGate = Invoke-FullGtQualityGate -ReviewCsv $FullGtReviewCsv -FrameReviewCsv $FullFrameReviewCsv
}

$qualityGateStatus = "pending-human"
$qualityGateEvidence = "full GT labels and full-frame review must be filled before quality gate can run"
if ($qualityGate -ne $null) {
    if ($qualityGate.Passed) {
        $qualityGateStatus = "pass"
    }
    elseif ($qualityGate.FailureAllowed) {
        $qualityGateStatus = "fail-documented"
    }
    else {
        $qualityGateStatus = "fail-blocking"
    }

    $qualityGateEvidence = "exitCode=$($qualityGate.ExitCode), $($qualityGate.GateLine), $($qualityGate.SummaryLine)"
}

$fullGtStatus = if ($fullGtFilled) { "filled-pending-strict-gate" } else { "pending-human" }
$guiStatus = if ($guiFilled) { "filled-pending-strict-gate" } else { "pending-human" }
$previewTrackHoldStatus = if ($previewTrackHoldPassed) { "pass" } else { "pending-human" }
$qualityGateReady = $qualityGateStatus -eq "pass" -or $qualityGateStatus -eq "fail-documented"
$pseudoGtReady = $pseudoGtStatus -eq "closed" -or $pseudoGtStatus -eq "skipped-no-candidates"
$sceneCarryReady = $finalMaskCleanupEvidence.Status -eq "pass"
$goalStatus = if ($planComplete -and $fullGtFilled -and $guiFilled -and $qualityGateReady -and $pseudoGtReady -and $sceneCarryReady) { "ready-for-strict-completion-audit" } else { "incomplete" }
$planCompletionText = if ($planComplete) { "complete=true" } else { "complete=false" }
$planRemainingText = if ($planComplete) { "remaining=none" } elseif ($goalMarker.Contains("remaining=gui-smoke")) { "remaining=gui-smoke" } else { "remaining=full-gt-label,gui-smoke" }

if ($RequireComplete -and -not ($planComplete -and $fullGtFilled -and $guiFilled -and $qualityGateReady -and $pseudoGtReady -and $sceneCarryReady)) {
    throw "Evidence report is not ready for strict completion audit: planComplete=$planComplete, fullGtFilled=$fullGtFilled, guiFilled=$guiFilled, qualityGateStatus=$qualityGateStatus, pseudoGtStatus=$pseudoGtStatus, finalMaskCleanupStatus=$($finalMaskCleanupEvidence.Status), pseudoGtRows=$($pseudoGtRows.Count), pseudoGtClosedRows=$pseudoGtClosedRows, pseudoGtLooseClosedRows=$pseudoGtLooseClosedRows, fullGtRows=$($fullGtRows.Count), fullGtReviewed=$fullGtReviewed, fullGtLabels=$fullGtLabels, fullFrameRows=$($fullFrameRows.Count), fullFrameReviewed=$fullFrameReviewed, fullFrameMissCounts=$fullFrameMissCounts, guiRows=$($guiRows.Count), guiPassed=$guiPassed, guiArtifacts=$guiArtifacts"
}

$rows = @(
    [pscustomobject]@{ Requirement = "YOLO backend/profile split"; Status = "pass"; Evidence = "verify-yolo-profile-state.ps1, yolo-state=pass" },
    [pscustomobject]@{ Requirement = "FaceONNX default preservation"; Status = "pass"; Evidence = "verify-auto-mosaic-default=pass, integrated scene/sparse guards pass, SmokeQualityGate passed=True, baselineFrames=19, optimizedFrames=19, avgBestIou=1.000, minBestIou=1.000" },
    [pscustomobject]@{ Requirement = "YOLO representative 3s gate"; Status = "pass"; Evidence = "representative=pass, SmokeQualityGate passed=True" },
    [pscustomobject]@{ Requirement = "YOLOv8 candidate A/B comparison"; Status = "pass"; Evidence = "YOLOv8n/m/s/l classified as no recommendation, documented strict gate failures" },
    [pscustomobject]@{ Requirement = "YOLO5Face candidate A/B comparison"; Status = "pass"; Evidence = "representative pass, extended/export fail, speed gain not promoted without quality gate" },
    [pscustomobject]@{ Requirement = "Failure-axis classification"; Status = "pass"; Evidence = "axes=model/decode/preprocess/post-filter/track/roi/tiling/small-face/box-refine/speed documented" },
    [pscustomobject]@{ Requirement = "Anti-flicker track hold algorithm"; Status = "pass"; Evidence = "track-hold-state=pass, app YOLO MaxLostFillFrames=0, app YOLO MaxInitialFillFrames=3, app keeps internal gap-fill but does not carry final tracks into the next scene, YOLO synthetic track-fill confidence capped at 0.78 for opt-in lost-fill scene-cut cleanup, sceneCutLostRemoved=6 verifies capped high-confidence lost-fill tails are cleared after a hard cut, stable final-mask gap fill up to 5 frames, synthetic cap verifier still covers MaxLostFillFrames=6" },
    [pscustomobject]@{ Requirement = "Scene-cut track reset guard"; Status = "pass"; Evidence = "verify-auto-mosaic-default includes scene-cut-guard=pass, final YOLO scene-cut cleanup runs after ROI/smoothing, post-scene weak cleanup runs without gap-fill, gap/lost track fill plus weak and medium-confidence direct carry-tail transitions checked, hardCutRemoved=True, sameSceneKept=True, directRemoved=3, mediumDirectRemoved=3, checkedPairs and removedFrames logged" },
    [pscustomobject]@{ Requirement = "Scene-cut high-confidence carry cleanup"; Status = $finalMaskCleanupEvidence.Status; Evidence = "$($finalMaskCleanupEvidence.Evidence); same-size post-cut carry support is not enough to preserve a mask, while scale-changed strong support remains reviewable" },
    [pscustomobject]@{ Requirement = "Sparse auto-detect scene-cut guard"; Status = "pass"; Evidence = "verify-auto-mosaic-default includes automask-sparse-scene-cut-guard=pass and automask-sparse-materialize-scene-cut=pass, YOLO hard cut interpolated=0 sceneCutStops=1, yoloSceneCutTransitions=0->5, cutBeforePositiveInterpolated=0, sparseSceneCutPairs logged" },
    [pscustomobject]@{ Requirement = "Sparse/unstable-tail/edge-tail temporal false-positive filter"; Status = "pass"; Evidence = "verify-auto-mosaic-default includes face-track-postprocess=pass, removedSparse=3, removedUnstableTail=1, removedEdgeTail=1, YOLO sparse weak track profile active, low-confidence edge lost-fill blocked" },
    [pscustomobject]@{ Requirement = "YOLO aspect-ratio candidate filter"; Status = "pass"; Evidence = "verify-auto-mosaic-default includes yolo-aspect-ratio-filter=pass; YoloFaceOnnxDetectorOptions exposes UseAspectRatioFilter/MinAspectRatio/MaxAspectRatio, app YOLO path sets 0.35-1.65, smoke harness can exercise the filter" },
    [pscustomobject]@{ Requirement = "Frame-level false-positive review evidence"; Status = "pass"; Evidence = "run-srcTest-smoke -DumpDetections emits parseable SmokeDetection lines with cx/cy/areaRatio/aspectRatio plus SmokeDetectionSummary; FinalMaskSummary/SmokeFinalMaskSummary now separates weakNonEdge, edgeWeak, topEdgeWeak, upperWeak, lowerWeak, aspectBad, tinyWeak, and tinyShort residuals; write-yolo-mask-continuity-report lists weak edge/top-edge/upper/lower/aspect-outlier review targets; new-yolo-full-gt-template/new-yolo-full-gt-review-package create crop and full-frame review artifacts for face/nonface/miss labeling; write-yolo-followup-quality-evidence wraps the follow-up evidence bundle" },
    [pscustomobject]@{ Requirement = "Preview track-hold GUI evidence"; Status = $previewTrackHoldStatus; Evidence = "preview-track-hold status=$previewTrackHoldPassed, required recording artifact before completion" },
    [pscustomobject]@{ Requirement = "30s extended quality gate"; Status = "fail"; Evidence = "extended=fail, SmokeQualityGate passed=False" },
    [pscustomobject]@{ Requirement = "30s export smoke quality"; Status = "fail"; Evidence = "extended-export=fail, directFaceFrames mismatch remains" },
    [pscustomobject]@{ Requirement = "Final YOLO recommendation"; Status = "none"; Evidence = "recommendation=none, no-final-yolo-recommendation" },
    [pscustomobject]@{ Requirement = "Full-GT label review"; Status = $fullGtStatus; Evidence = "rows=$($fullGtRows.Count), labels=$fullGtLabels, reviewed=$fullGtReviewed, fullFrameRows=$($fullFrameRows.Count), fullFrameReviewed=$fullFrameReviewed, missedFaceCountFilled=$fullFrameMissCounts" },
    [pscustomobject]@{ Requirement = "Full-GT quality gate"; Status = $qualityGateStatus; Evidence = $qualityGateEvidence },
    [pscustomobject]@{ Requirement = "Test-only pseudo-GT candidate evidence"; Status = $pseudoGtStatus; Evidence = "supportedFaceCandidate=$pseudoGtSupportedRows, falsePositiveCandidate=$pseudoGtFalsePositiveRows, missCandidate=$pseudoGtMissRows, supportEvidenceRows=$pseudoGtRowsWithSupportEvidence, personEvidenceRows=$pseudoGtRowsWithPersonEvidence, geometryEvidenceRows=$pseudoGtRowsWithGeometryEvidence, areaRatioEvidenceRows=$pseudoGtRowsWithAreaRatioEvidence; source=$PseudoGtCsv" },
    [pscustomobject]@{ Requirement = "Test-only pseudo-GT review queue"; Status = $pseudoGtReviewQueueStatus; Evidence = "rows=$($pseudoGtReviewQueueRows.Count), falsePositiveCandidate=$pseudoGtQueueFalsePositiveRows, missCandidate=$pseudoGtQueueMissRows, supportedFaceCandidate=$pseudoGtQueueSupportedRows, auxiliaryBoostedRows=$pseudoGtQueueAuxiliaryBoostedRows, maxAuxiliaryPriorityBoost=$(Format-Double $pseudoGtQueueMaxAuxiliaryBoost), top=$(if ($pseudoGtQueueTopRows.Count -gt 0) { [string]::Join(';', $pseudoGtQueueTopRows) } else { 'none' }); source=$PseudoGtReviewQueueCsv" },
    [pscustomobject]@{ Requirement = "Test-only pseudo-GT review closure"; Status = $pseudoGtStatus; Evidence = "candidates=$($pseudoGtRows.Count), closureRows=$($pseudoGtClosureRows.Count), closed=$pseudoGtClosedRows, open=$pseudoGtOpenRows; runtimePath=not-used-by-app" },
    [pscustomobject]@{ Requirement = "Test-only pseudo-GT closure evidence preservation"; Status = $pseudoGtStatus; Evidence = "closureSupportEvidenceRows=$pseudoGtClosureRowsWithSupportEvidence, closurePersonEvidenceRows=$pseudoGtClosureRowsWithPersonEvidence, closureGeometryEvidenceRows=$pseudoGtClosureRowsWithGeometryEvidence, closureAreaRatioEvidenceRows=$pseudoGtClosureRowsWithAreaRatioEvidence; source=$PseudoGtReviewClosureCsv" },
    [pscustomobject]@{ Requirement = "Avalonia GUI smoke"; Status = $guiStatus; Evidence = "steps=$($guiRows.Count), pass=$guiPassed, artifactPathFilled=$guiArtifacts, required=preview-track-hold/manual-edit/export/reopen-state" },
    [pscustomobject]@{ Requirement = "Model license/distribution policy"; Status = "pass"; Evidence = "license-source=pass, bundle=blocked" },
    [pscustomobject]@{ Requirement = "10-minute/whole-video decision"; Status = "deferred"; Evidence = "ten-minute-full=not-required-after-extended-fail" },
    [pscustomobject]@{ Requirement = "Goal completion"; Status = $goalStatus; Evidence = "$planCompletionText, $planRemainingText, fullGtFilled=$fullGtFilled, guiFilled=$guiFilled" }
)

$requiredGoalMarkerTokens = @("yolo-goal-audit-state:")
if ($planIncomplete) {
    $requiredGoalMarkerTokens += "top-level-ready-rerun=pass"
}

$missingPlanTokens = $requiredGoalMarkerTokens | Where-Object { -not $goalMarker.Contains($_) }

if ($missingPlanTokens.Count -gt 0) {
    throw "Plan is missing required goal audit tokens: $($missingPlanTokens -join ', ')"
}

$requiredSummaryTokens = @("gui-smoke", "preview-track-hold", "RequireComplete")
if ($planRemainingText -eq "remaining=full-gt-label,gui-smoke") {
    $requiredSummaryTokens += "full-gt-label"
}
elseif ($planIncomplete) {
    $requiredSummaryTokens += "fullGtPendingRows=0"
}

foreach ($requiredSummaryToken in $requiredSummaryTokens) {
    if (-not $summary.Contains($requiredSummaryToken)) {
        throw "Manual gate summary is missing required token: $requiredSummaryToken"
    }
}

$resolvedOutput = Resolve-RepoPath $OutputPath
$outputDir = Split-Path -Parent $resolvedOutput
if (-not [string]::IsNullOrWhiteSpace($outputDir)) {
    New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
}

$lines = @(
    "# YOLO Goal Evidence Report",
    "",
    "This report is generated from the current plan markers and review artifacts. It is not a completion approval.",
    "",
    "- $planCompletionText",
    "- $planRemainingText",
    "- fullGtRows=$($fullGtRows.Count)",
    "- fullGtReviewed=$fullGtReviewed",
    "- fullGtFilled=$fullGtFilled",
    "- fullGtQualityGate=$qualityGateStatus",
    "- finalMaskCleanupStatus=$($finalMaskCleanupEvidence.Status)",
    "- finalMaskCleanupDriftingStrongCarryRemoved=$($finalMaskCleanupEvidence.DriftingStrongCarryRemoved)",
    "- finalMaskCleanupAreaChangedStrongCarryProtected=$($finalMaskCleanupEvidence.AreaChangedStrongCarryProtected)",
    "- pseudoGtStatus=$pseudoGtStatus",
    "- pseudoGtRows=$($pseudoGtRows.Count)",
    "- pseudoGtClosedRows=$pseudoGtClosedRows",
    "- pseudoGtLooseClosedRows=$pseudoGtLooseClosedRows",
    "- pseudoGtSupportedRows=$pseudoGtSupportedRows",
    "- pseudoGtFalsePositiveRows=$pseudoGtFalsePositiveRows",
    "- pseudoGtMissRows=$pseudoGtMissRows",
    "- pseudoGtReviewQueueStatus=$pseudoGtReviewQueueStatus",
    "- pseudoGtReviewQueueRows=$($pseudoGtReviewQueueRows.Count)",
    "- pseudoGtReviewQueueAuxiliaryBoostedRows=$pseudoGtQueueAuxiliaryBoostedRows",
    "- pseudoGtReviewQueueMaxAuxiliaryBoost=$(Format-Double $pseudoGtQueueMaxAuxiliaryBoost)",
    "- pseudoGtSupportEvidenceRows=$pseudoGtRowsWithSupportEvidence",
    "- pseudoGtClosureSupportEvidenceRows=$pseudoGtClosureRowsWithSupportEvidence",
    "- fullFrameRows=$($fullFrameRows.Count)",
    "- fullFrameReviewed=$fullFrameReviewed",
    "- guiSteps=$($guiRows.Count)",
    "- guiPassed=$guiPassed",
    "- guiFilled=$guiFilled",
    "- goalStatus=$goalStatus",
    "",
    "| Requirement | Status | Evidence |",
    "| --- | --- | --- |"
)

foreach ($row in $rows) {
    $lines += "| $(Escape-Cell $row.Requirement) | $(Escape-Cell $row.Status) | $(Escape-Cell $row.Evidence) |"
}

$lines | Set-Content -Encoding UTF8 -Path $resolvedOutput

$report = Get-Content -Raw -Path $resolvedOutput
foreach ($requiredReportToken in @(
    "Full-GT label review",
    "Full-GT quality gate",
    "Test-only pseudo-GT candidate evidence",
    "Test-only pseudo-GT review queue",
    "Test-only pseudo-GT review closure",
    "Test-only pseudo-GT closure evidence preservation",
    "Avalonia GUI smoke",
    "Preview track-hold GUI evidence",
    "Scene-cut high-confidence carry cleanup",
    "YOLOv8 candidate A/B comparison",
    "YOLO5Face candidate A/B comparison",
    "Failure-axis classification",
    "Goal completion",
    $planCompletionText,
    $planRemainingText,
    "track-hold-state=pass",
    "recommendation=none")) {
    Assert-ReportContains "report token $requiredReportToken" $report $requiredReportToken
}

if ($Verify) {
    if ($RequireComplete) {
        Assert-ReportContains "report keeps full GT filled" $report "Full-GT label review | filled-pending-strict-gate"
        Assert-ReportContains "report keeps full GT quality checked" $report "Full-GT quality gate | $qualityGateStatus"
        Assert-ReportContains "report keeps pseudo-GT closure checked" $report "Test-only pseudo-GT review closure | $pseudoGtStatus"
        Assert-ReportContains "report keeps pseudo-GT review queue checked" $report "Test-only pseudo-GT review queue | $pseudoGtReviewQueueStatus"
        Assert-ReportContains "report keeps GUI filled" $report "Avalonia GUI smoke | filled-pending-strict-gate"
        Assert-ReportContains "report keeps preview track-hold passed" $report "Preview track-hold GUI evidence | pass"
        Assert-ReportContains "report keeps completion ready for strict audit" $report "Goal completion | ready-for-strict-completion-audit"
    }
    else {
        if ($fullGtFilled) {
            Assert-ReportContains "report keeps full GT filled" $report "Full-GT label review | filled-pending-strict-gate"
            Assert-ReportContains "report keeps full GT quality checked" $report "Full-GT quality gate | $qualityGateStatus"
            Assert-ReportContains "report records full GT quality state" $report "fullGtQualityGate=$qualityGateStatus"
        }
        else {
            Assert-ReportContains "report keeps full GT pending" $report "Full-GT label review | pending-human"
            Assert-ReportContains "report keeps full GT quality pending" $report "Full-GT quality gate | pending-human"
            Assert-ReportContains "report records pending full GT quality state" $report "fullGtQualityGate=pending-human"
        }
        if ($guiFilled) {
            Assert-ReportContains "report keeps GUI filled" $report "Avalonia GUI smoke | filled-pending-strict-gate"
        }
        else {
            Assert-ReportContains "report keeps GUI pending" $report "Avalonia GUI smoke | pending-human"
        }

        Assert-ReportContains "report records pseudo-GT closure state" $report "pseudoGtStatus=$pseudoGtStatus"
        Assert-ReportContains "report records strict pseudo-GT closure count" $report "pseudoGtClosedRows=$pseudoGtClosedRows"
        Assert-ReportContains "report records loose pseudo-GT closure count" $report "pseudoGtLooseClosedRows=$pseudoGtLooseClosedRows"
        Assert-ReportContains "report records final-mask cleanup state" $report "finalMaskCleanupStatus=$($finalMaskCleanupEvidence.Status)"
        Assert-ReportContains "report records same-size carry removal count" $report "finalMaskCleanupDriftingStrongCarryRemoved=$($finalMaskCleanupEvidence.DriftingStrongCarryRemoved)"
        Assert-ReportContains "report records area-changed carry protection count" $report "finalMaskCleanupAreaChangedStrongCarryProtected=$($finalMaskCleanupEvidence.AreaChangedStrongCarryProtected)"
        Assert-ReportContains "report records pseudo-GT candidate evidence" $report "Test-only pseudo-GT candidate evidence | $pseudoGtStatus"
        Assert-ReportContains "report records pseudo-GT review queue" $report "Test-only pseudo-GT review queue | $pseudoGtReviewQueueStatus"
        Assert-ReportContains "report records pseudo-GT review queue state" $report "pseudoGtReviewQueueStatus=$pseudoGtReviewQueueStatus"
        Assert-ReportContains "report records pseudo-GT auxiliary boost count" $report "pseudoGtReviewQueueAuxiliaryBoostedRows=$pseudoGtQueueAuxiliaryBoostedRows"
        Assert-ReportContains "report records pseudo-GT max auxiliary boost" $report "pseudoGtReviewQueueMaxAuxiliaryBoost=$(Format-Double $pseudoGtQueueMaxAuxiliaryBoost)"
        Assert-ReportContains "report records pseudo-GT area ratio evidence" $report "areaRatioEvidenceRows=$pseudoGtRowsWithAreaRatioEvidence"
        Assert-ReportContains "report records pseudo-GT closure area ratio evidence" $report "closureAreaRatioEvidenceRows=$pseudoGtClosureRowsWithAreaRatioEvidence"
        Assert-ReportContains "report records pseudo-GT closure evidence preservation" $report "Test-only pseudo-GT closure evidence preservation | $pseudoGtStatus"

        if ($previewTrackHoldPassed) {
            Assert-ReportContains "report keeps preview track-hold passed" $report "Preview track-hold GUI evidence | pass"
        }
        else {
            Assert-ReportContains "report keeps preview track-hold pending" $report "Preview track-hold GUI evidence | pending-human"
        }

        if ($goalStatus -eq "ready-for-strict-completion-audit") {
            Assert-ReportContains "report keeps completion ready for strict audit" $report "Goal completion | ready-for-strict-completion-audit"
        }
        else {
            Assert-ReportContains "report keeps completion incomplete" $report "Goal completion | incomplete"
        }
    }
}

Write-Host "[YoloGoalEvidenceReport] wrote output=$resolvedOutput"
Write-Host "[YoloGoalEvidenceReport] all requested checks passed"
