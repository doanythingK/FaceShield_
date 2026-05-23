param(
    [string]$ManualGateHelper = "scripts\open-yolo-manual-gates.ps1",
    [string]$FullGtReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-gt-review.csv",
    [string]$FullFrameReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-frame-review.csv",
    [string]$GuiChecklistCsv = ".tmp\yolo-gui-smoke\manual-smoke-checklist.csv",
    [string]$CompletedFixtureReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-gt-review-reviewed-candidate.csv",
    [string]$CompletedFixtureFullFrameReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-frame-review-reviewed-candidate.csv",
    [string]$CompletedFixtureGuiChecklistCsv = ".tmp\yolo-manual-gate-helper\completed-fixture\manual-smoke-checklist.csv"
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

    return $resolved
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

    Write-Host "[YoloManualGateHelperVerify] pass $Name"
}

function Invoke-Helper {
    param([string[]]$Arguments)

    $oldErrorAction = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $helperPath @Arguments 2>&1
        $exitCode = $LASTEXITCODE
        $text = ($output | Out-String)
        Write-Host $text
    }
    finally {
        $ErrorActionPreference = $oldErrorAction
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        Text = $text
    }
}

function Get-ColumnFillState {
    param(
        [object[]]$Rows,
        [string]$Column
    )

    if ($Rows.Count -eq 0) {
        return "empty"
    }

    $filled = @($Rows | Where-Object {
        $null -ne $_.PSObject.Properties[$Column] -and -not [string]::IsNullOrWhiteSpace($_.$Column)
    })

    if ($filled.Count -eq 0) {
        return "pending"
    }

    if ($filled.Count -eq $Rows.Count) {
        return "completed"
    }

    return "partial"
}

function New-CompletedGuiChecklistFixture {
    param([string]$OutputCsv)

    $outputPath = Resolve-RepoPath $OutputCsv
    $outputDir = Split-Path -Parent $outputPath
    New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

    $relativeDir = ".tmp\yolo-manual-gate-helper\completed-fixture"
    $artifactMap = @{
        "open-video" = "open-video.png"
        "select-yolo-backend" = "select-yolo-backend.png"
        "run-yolo-auto-detect" = "run-yolo-auto-detect.log"
        "preview-result" = "preview-result.mp4"
        "manual-edit" = "manual-edit.png"
        "export" = "export.mp4"
        "reopen-state" = "reopen-state.png"
    }
    $evidenceTypeMap = @{
        "open-video" = "screenshot-or-recording"
        "select-yolo-backend" = "screenshot"
        "run-yolo-auto-detect" = "screenshot-or-log"
        "preview-result" = "screenshot-or-recording"
        "manual-edit" = "screenshot-or-recording"
        "export" = "output-file"
        "reopen-state" = "screenshot-or-recording"
    }

    $rows = foreach ($step in @(
        "open-video",
        "select-yolo-backend",
        "run-yolo-auto-detect",
        "preview-result",
        "manual-edit",
        "export",
        "reopen-state"
    )) {
        $artifactRelative = Join-Path $relativeDir $artifactMap[$step]
        $artifactPath = Resolve-RepoPath $artifactRelative
        Set-Content -Encoding UTF8 -Path $artifactPath -Value "manual gate helper completed fixture artifact for $step"

        [pscustomobject]@{
            stepId = $step
            status = "pass"
            evidenceType = $evidenceTypeMap[$step]
            artifactPath = $artifactRelative
            evidence = "manual gate helper completed fixture evidence for $step"
            notes = "fixture only; not real human GUI smoke evidence"
        }
    }

    $rows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $outputPath
    return $outputPath
}

$helperPath = Assert-FileNonEmpty "manual gate helper" $ManualGateHelper
$reviewCsv = Assert-FileNonEmpty "full GT review CSV" $FullGtReviewCsv
$frameCsv = Assert-FileNonEmpty "full-frame review CSV" $FullFrameReviewCsv
$guiCsv = Assert-FileNonEmpty "GUI checklist CSV" $GuiChecklistCsv
$completedReviewCsv = Assert-FileNonEmpty "completed fixture full GT review CSV" $CompletedFixtureReviewCsv
$completedFrameCsv = Assert-FileNonEmpty "completed fixture full-frame review CSV" $CompletedFixtureFullFrameReviewCsv

$helperText = Get-Content -Raw -Path $helperPath
Assert-Contains "helper prints completed manual readiness command" $helperText "completedManualReadinessCommand"
Assert-Contains "helper supports ready verification" $helperText "VerifyReady"
Assert-Contains "helper supports completed verification" $helperText "VerifyCompleted"
Assert-Contains "helper completed path allows full GT" $helperText "AllowCompletedFullGt"
Assert-Contains "helper completed path allows GUI smoke" $helperText "AllowCompletedGuiSmoke"
Assert-Contains "helper completed path runs full GT verifier" $helperText "full-gt-reviewed-state"
Assert-Contains "helper completed path runs GUI verifier" $helperText "gui-smoke-state"

$reviewRows = @(Import-Csv $reviewCsv)
$frameRows = @(Import-Csv $frameCsv)
$guiRows = @(Import-Csv $guiCsv)

$reviewState = Get-ColumnFillState $reviewRows "label"
$frameState = Get-ColumnFillState $frameRows "missedFaceCount"
$guiState = Get-ColumnFillState $guiRows "status"
$allPending = $reviewState -eq "pending" -and $frameState -eq "pending" -and $guiState -eq "pending"
$allCompleted = $reviewState -eq "completed" -and $frameState -eq "completed" -and $guiState -eq "completed"

Write-Host "[YoloManualGateHelperVerify] state review=$reviewState, fullFrame=$frameState, gui=$guiState"

$ready = Invoke-Helper @("-VerifyReady")
if ($ready.ExitCode -ne 0) {
    throw "Manual gate helper -VerifyReady failed with exit code $($ready.ExitCode)"
}
Assert-Contains "ready output includes completed readiness command" $ready.Text "completedManualReadinessCommand"
Assert-Contains "ready output passes" $ready.Text "[YoloManualGate] all requested checks passed"

$completed = Invoke-Helper @("-VerifyCompleted")
if ($allCompleted) {
    if ($completed.ExitCode -ne 0) {
        throw "Manual gate helper -VerifyCompleted failed on completed manual files with exit code $($completed.ExitCode)"
    }

    Assert-Contains "completed output runs manual readiness" $completed.Text "manual-readiness-completed-state"
    Assert-Contains "completed output runs full GT reviewed verifier" $completed.Text "full-gt-reviewed-state"
    Assert-Contains "completed output runs GUI smoke verifier" $completed.Text "gui-smoke-state"
    Assert-Contains "completed output passes" $completed.Text "[YoloManualGate] all requested checks passed"
}
elseif ($allPending) {
    if ($completed.ExitCode -eq 0) {
        throw "Manual gate helper -VerifyCompleted unexpectedly passed on pending manual files"
    }

    Assert-Contains "pending completed check runs manual readiness" $completed.Text "manual-readiness-completed-state"
    Assert-Contains "pending completed check runs full GT reviewed verifier" $completed.Text "full-gt-reviewed-state"
    Assert-Contains "pending completed check fails on unreviewed full GT rows" $completed.Text "Review CSV has unreviewed rows"
}
else {
    if ($completed.ExitCode -eq 0) {
        throw "Manual gate helper -VerifyCompleted unexpectedly passed on partial manual files"
    }

    Assert-Contains "partial completed check runs manual readiness" $completed.Text "manual-readiness-completed-state"
}

$completedGuiCsv = New-CompletedGuiChecklistFixture -OutputCsv $CompletedFixtureGuiChecklistCsv
$completedFixture = Invoke-Helper @(
    "-FullGtReviewCsv", $completedReviewCsv,
    "-FullFrameReviewCsv", $completedFrameCsv,
    "-GuiChecklistCsv", $completedGuiCsv,
    "-MaxFalsePositives", "12",
    "-VerifyCompleted"
)
if ($completedFixture.ExitCode -ne 0) {
    throw "Manual gate helper completed fixture failed with exit code $($completedFixture.ExitCode)"
}
Assert-Contains "completed fixture runs manual readiness" $completedFixture.Text "manual-readiness-completed-state"
Assert-Contains "completed fixture passes manual readiness" $completedFixture.Text "pass manual-readiness-completed-state"
Assert-Contains "completed fixture runs full GT reviewed verifier" $completedFixture.Text "full-gt-reviewed-state"
Assert-Contains "completed fixture runs GUI smoke verifier" $completedFixture.Text "gui-smoke-state"
Assert-Contains "completed fixture passes helper" $completedFixture.Text "[YoloManualGate] all requested checks passed"

Write-Host "[YoloManualGateHelperVerify] all requested checks passed"
