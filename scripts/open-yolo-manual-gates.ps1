param(
    [string]$ReviewIndex = ".tmp\yolo-full-gt\review-package-smoke\review-index.html",
    [string]$FullGtReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-gt-review.csv",
    [string]$FullFrameReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-frame-review.csv",
    [string]$GuiChecklistCsv = ".tmp\yolo-gui-smoke\manual-smoke-checklist.csv",
    [string]$PredictionLog = ".tmp\yolo-ten-minute-detection-smoke\yolo-ten-minute-yolo-only-20260523-022022.log",
    [string]$AiCandidateReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-gt-review-reviewed-candidate.csv",
    [string]$AiCandidateFullFrameReviewCsv = ".tmp\yolo-full-gt\review-package-smoke\full-frame-review-reviewed-candidate.csv",
    [string]$PseudoGtCsv = ".tmp\yolo-pseudo-gt\pseudo-gt-candidates.csv",
    [string]$PseudoGtReviewQueueCsv = ".tmp\yolo-pseudo-gt\pseudo-gt-review-queue.csv",
    [string]$PseudoGtReviewClosureCsv = ".tmp\yolo-pseudo-gt\pseudo-gt-review-closure.csv",
    [string]$PseudoGtReviewClosureSummary = ".tmp\yolo-pseudo-gt\pseudo-gt-review-closure-summary.md",
    [string]$HumanReviewDraftDir = ".tmp\yolo-manual-gates\human-review-draft",
    [string]$PseudoGtReviewDraftDir = ".tmp\yolo-pseudo-gt\review-draft",
    [double]$MinIou = 0.50,
    [int]$MaxMisses = 0,
    [int]$MaxFalsePositives = 0,
    [int]$MaxLowIou = 0,
    [string]$SummaryPath = ".tmp\yolo-manual-gates\manual-gate-summary.md",
    [string]$DashboardPath = ".tmp\yolo-manual-gates\manual-gate-dashboard.html",
    [string]$PendingReportPath = ".tmp\yolo-manual-gates\manual-pending-report.md",
    [string]$GuiEvidenceDir = ".tmp\yolo-gui-smoke\evidence",
    [string]$GuiEvidenceGuidePath = ".tmp\yolo-gui-smoke\gui-smoke-evidence-guide.md",
    [switch]$Open,
    [switch]$OpenDashboard,
    [switch]$OpenApp,
    [switch]$VerifyReady,
    [switch]$VerifyCompleted,
    [switch]$WriteSummary,
    [switch]$PrepareGuiChecklist,
    [switch]$PrepareGuiEvidence
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$manualReadinessVerifier = Join-Path $repo "scripts\verify-yolo-manual-readiness-state.ps1"
$fullGtReviewedVerifier = Join-Path $repo "scripts\verify-yolo-full-gt-reviewed-state.ps1"
$guiSmokeVerifier = Join-Path $repo "scripts\verify-yolo-gui-smoke-state.ps1"
$guiChecklistGenerator = Join-Path $repo "scripts\new-yolo-gui-smoke-checklist.ps1"
$completionFinalizer = Join-Path $repo "scripts\complete-yolo-goal-after-manual-gates.ps1"
$pendingReportWriter = Join-Path $repo "scripts\write-yolo-manual-pending-report.ps1"
$humanReviewDraftWriter = Join-Path $repo "scripts\new-yolo-human-review-draft.ps1"
$pseudoGtReviewDraftWriter = Join-Path $repo "scripts\new-yolo-pseudo-gt-review-draft.ps1"
$guiEvidencePrep = Join-Path $repo "scripts\prepare-yolo-gui-smoke-evidence.ps1"
$pseudoGtReviewClosure = Join-Path $repo "scripts\close-yolo-pseudo-gt-review.ps1"

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

function Convert-ToHtmlText {
    param([string]$Text)

    return [System.Net.WebUtility]::HtmlEncode($Text)
}

function Convert-ToRelativeLink {
    param(
        [string]$BasePath,
        [string]$TargetPath
    )

    $trimChars = [char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $resolvedBaseDir = [IO.Path]::GetFullPath((Split-Path -Parent $BasePath)).TrimEnd($trimChars) + [IO.Path]::DirectorySeparatorChar
    $resolvedTargetPath = [IO.Path]::GetFullPath($TargetPath)
    $baseUri = [Uri]$resolvedBaseDir
    $targetUri = [Uri]$resolvedTargetPath
    return $baseUri.MakeRelativeUri($targetUri).ToString()
}

function Get-CsvValue {
    param(
        [object]$Row,
        [string]$Column
    )

    if ($null -eq $Row.PSObject.Properties[$Column]) {
        return ""
    }

    return [string]$Row.$Column
}

function Get-ManualGateProgress {
    param(
        [string]$FullGtReviewCsvPath,
        [string]$FullFrameReviewCsvPath,
        [string]$GuiChecklistCsvPath
    )

    $reviewRows = @(Import-Csv $FullGtReviewCsvPath)
    $frameRows = @(Import-Csv $FullFrameReviewCsvPath)
    $guiRows = @(Import-Csv $GuiChecklistCsvPath)

    $pendingReviewRows = @($reviewRows | Where-Object {
        [string]::IsNullOrWhiteSpace((Get-CsvValue $_ "label")) -or
        [string]::IsNullOrWhiteSpace((Get-CsvValue $_ "reviewStatus")) -or
        [string]::IsNullOrWhiteSpace((Get-CsvValue $_ "evidenceNotes"))
    })
    $pendingFrameRows = @($frameRows | Where-Object {
        [string]::IsNullOrWhiteSpace((Get-CsvValue $_ "missedFaceCount")) -or
        [string]::IsNullOrWhiteSpace((Get-CsvValue $_ "missedFaceRowsAdded")) -or
        [string]::IsNullOrWhiteSpace((Get-CsvValue $_ "reviewStatus")) -or
        [string]::IsNullOrWhiteSpace((Get-CsvValue $_ "evidenceNotes"))
    })
    $pendingGuiRows = @($guiRows | Where-Object {
        [string]::IsNullOrWhiteSpace((Get-CsvValue $_ "status")) -or
        [string]::IsNullOrWhiteSpace((Get-CsvValue $_ "evidenceType")) -or
        [string]::IsNullOrWhiteSpace((Get-CsvValue $_ "artifactPath")) -or
        [string]::IsNullOrWhiteSpace((Get-CsvValue $_ "evidence"))
    })

    return [pscustomobject]@{
        FullGtReviewRows = $reviewRows.Count
        FullGtPendingRows = $pendingReviewRows.Count
        FullFrameRows = $frameRows.Count
        FullFramePendingRows = $pendingFrameRows.Count
        GuiRows = $guiRows.Count
        GuiPendingRows = $pendingGuiRows.Count
    }
}

function Get-RemainingManualGates {
    param(
        [pscustomobject]$Progress,
        [bool]$PseudoGtReady = $true,
        [bool]$PseudoGtReviewClosureReady = $true
    )

    $remaining = @()
    if ($Progress.FullGtPendingRows -gt 0 -or $Progress.FullFramePendingRows -gt 0) {
        $remaining += "full-gt-label"
    }
    if ($Progress.GuiPendingRows -gt 0) {
        $remaining += "gui-smoke"
    }
    if (-not $PseudoGtReady) {
        $remaining += "pseudo-gt-evidence"
    }
    elseif (-not $PseudoGtReviewClosureReady) {
        $remaining += "pseudo-gt-review-closure"
    }
    if ($remaining.Count -eq 0) {
        $remaining += "none"
    }

    return $remaining
}

function Test-PseudoGtReviewClosureReady {
    param(
        [string]$ClosureCsvPath,
        [string]$ClosureSummaryPath
    )

    if (-not (Test-Path $ClosureCsvPath) -or -not (Test-Path $ClosureSummaryPath)) {
        return $false
    }

    $rows = @(Import-Csv $ClosureCsvPath)
    if ($rows.Count -eq 0) {
        return $false
    }

    return @($rows | Where-Object { (Get-CsvValue $_ "closureStatus") -ne "closed" }).Count -eq 0
}

function Get-PendingGuiSmokeRows {
    param([string]$GuiChecklistCsvPath)

    return @(Import-Csv $GuiChecklistCsvPath | Where-Object {
        [string]::IsNullOrWhiteSpace((Get-CsvValue $_ "status")) -or
        [string]::IsNullOrWhiteSpace((Get-CsvValue $_ "evidenceType")) -or
        [string]::IsNullOrWhiteSpace((Get-CsvValue $_ "artifactPath")) -or
        [string]::IsNullOrWhiteSpace((Get-CsvValue $_ "evidence"))
    })
}

function Write-ManualGateDashboard {
    param(
        [string]$Path,
        [string]$ReviewIndexPath,
        [string]$FullGtReviewCsvPath,
        [string]$FullFrameReviewCsvPath,
        [string]$GuiChecklistCsvPath,
        [string]$AiCandidateReviewCsvPath,
        [string]$AiCandidateFullFrameReviewCsvPath,
        [string]$HumanReviewDraftReportPath,
        [string]$GuiEvidenceGuideResolvedPath,
        [string]$PendingReportResolvedPath,
        [object]$Progress,
        [bool]$PseudoGtReady,
        [bool]$PseudoGtReviewClosureReady,
        [string[]]$Commands
    )

    $resolvedDashboardPath = Resolve-RepoPath $Path
    $dashboardDir = Split-Path -Parent $resolvedDashboardPath
    New-Item -ItemType Directory -Force -Path $dashboardDir | Out-Null

    $links = @(
        [pscustomobject]@{ Label = "Review index"; Path = $ReviewIndexPath },
        [pscustomobject]@{ Label = "Manual pending report"; Path = $PendingReportResolvedPath },
        [pscustomobject]@{ Label = "Full-GT review CSV"; Path = $FullGtReviewCsvPath },
        [pscustomobject]@{ Label = "Full-frame review CSV"; Path = $FullFrameReviewCsvPath },
        [pscustomobject]@{ Label = "GUI smoke checklist CSV"; Path = $GuiChecklistCsvPath }
    )
    if (-not [string]::IsNullOrWhiteSpace($resolvedPseudoGtCsv) -and (Test-Path $resolvedPseudoGtCsv)) {
        $links += [pscustomobject]@{ Label = "Pseudo-GT candidates CSV"; Path = $resolvedPseudoGtCsv }
    }
    if (-not [string]::IsNullOrWhiteSpace($resolvedPseudoGtReviewClosureCsv) -and (Test-Path $resolvedPseudoGtReviewClosureCsv)) {
        $links += [pscustomobject]@{ Label = "Pseudo-GT review closure CSV"; Path = $resolvedPseudoGtReviewClosureCsv }
    }
    if (-not [string]::IsNullOrWhiteSpace($resolvedPseudoGtReviewClosureSummary) -and (Test-Path $resolvedPseudoGtReviewClosureSummary)) {
        $links += [pscustomobject]@{ Label = "Pseudo-GT review closure summary"; Path = $resolvedPseudoGtReviewClosureSummary }
    }
    if (-not [string]::IsNullOrWhiteSpace($AiCandidateReviewCsvPath) -and (Test-Path $AiCandidateReviewCsvPath)) {
        $links += [pscustomobject]@{ Label = "AI candidate full-GT CSV"; Path = $AiCandidateReviewCsvPath }
    }
    if (-not [string]::IsNullOrWhiteSpace($AiCandidateFullFrameReviewCsvPath) -and (Test-Path $AiCandidateFullFrameReviewCsvPath)) {
        $links += [pscustomobject]@{ Label = "AI candidate full-frame CSV"; Path = $AiCandidateFullFrameReviewCsvPath }
    }
    if (-not [string]::IsNullOrWhiteSpace($HumanReviewDraftReportPath) -and (Test-Path $HumanReviewDraftReportPath)) {
        $links += [pscustomobject]@{ Label = "Human review draft report"; Path = $HumanReviewDraftReportPath }
    }
    if (-not [string]::IsNullOrWhiteSpace($GuiEvidenceGuideResolvedPath) -and (Test-Path $GuiEvidenceGuideResolvedPath)) {
        $links += [pscustomobject]@{ Label = "GUI smoke evidence guide"; Path = $GuiEvidenceGuideResolvedPath }
    }

    $builder = New-Object System.Text.StringBuilder
    [void]$builder.AppendLine("<!doctype html>")
    [void]$builder.AppendLine("<html lang=""en"">")
    [void]$builder.AppendLine("<head>")
    [void]$builder.AppendLine("<meta charset=""utf-8"">")
    [void]$builder.AppendLine("<meta name=""viewport"" content=""width=device-width, initial-scale=1"">")
    [void]$builder.AppendLine("<title>YOLO manual gate dashboard</title>")
    [void]$builder.AppendLine("<style>")
    [void]$builder.AppendLine("body{font-family:Segoe UI,Arial,sans-serif;margin:24px;line-height:1.45;color:#111;background:#f7f7f7}h1,h2{margin:0 0 12px}.panel{background:#fff;border:1px solid #ddd;margin:0 0 16px;padding:14px}.links,.progress{display:grid;grid-template-columns:repeat(auto-fit,minmax(240px,1fr));gap:10px}.link,.metric{display:block;border:1px solid #ddd;padding:10px;text-decoration:none;color:#111;background:#fafafa}.metric strong{display:block;font-size:14px}.metric .value{font-size:22px;font-weight:700}.muted{color:#555;font-size:12px;word-break:break-word}ol{margin:0 0 0 22px;padding:0}li{margin:6px 0}pre{white-space:pre-wrap;background:#111;color:#f3f3f3;padding:12px;overflow:auto}.pending{color:#9a3412;font-weight:600}")
    [void]$builder.AppendLine("</style>")
    [void]$builder.AppendLine("</head>")
    [void]$builder.AppendLine("<body>")
    $remainingGates = @(Get-RemainingManualGates -Progress $Progress -PseudoGtReady $PseudoGtReady -PseudoGtReviewClosureReady $PseudoGtReviewClosureReady)
    [void]$builder.AppendLine("<h1>YOLO manual gate dashboard</h1>")
    [void]$builder.AppendLine("<section class=""panel""><h2>Remaining gates</h2><p class=""pending"">$(Convert-ToHtmlText ($remainingGates -join ', '))</p></section>")
    [void]$builder.AppendLine("<section class=""panel""><h2>Progress</h2><div class=""progress"">")
    [void]$builder.AppendLine("<div class=""metric""><strong>Full-GT crop review</strong><span class=""value"">$($Progress.FullGtPendingRows)</span><br><span class=""muted"">pending of $($Progress.FullGtReviewRows), needs label/reviewStatus/evidenceNotes</span></div>")
    [void]$builder.AppendLine("<div class=""metric""><strong>Full-frame review</strong><span class=""value"">$($Progress.FullFramePendingRows)</span><br><span class=""muted"">pending of $($Progress.FullFrameRows), needs missed counts/reviewStatus/evidenceNotes</span></div>")
    [void]$builder.AppendLine("<div class=""metric""><strong>GUI smoke checklist</strong><span class=""value"">$($Progress.GuiPendingRows)</span><br><span class=""muted"">pending of $($Progress.GuiRows), needs status/evidenceType/artifactPath/evidence</span></div>")
    [void]$builder.AppendLine("</div></section>")
    [void]$builder.AppendLine("<section class=""panel""><h2>Pending Preview</h2>")
    [void]$builder.AppendLine("<p class=""muted"">First rows still blocking completion. Full details are in the manual pending report.</p>")
    [void]$builder.AppendLine("<h3>Full-GT crop rows</h3><ol>")
    $pendingReviewRows = @(Import-Csv $FullGtReviewCsvPath | Where-Object {
        [string]::IsNullOrWhiteSpace((Get-CsvValue $_ "label")) -or
        [string]::IsNullOrWhiteSpace((Get-CsvValue $_ "reviewStatus")) -or
        [string]::IsNullOrWhiteSpace((Get-CsvValue $_ "evidenceNotes"))
    } | Select-Object -First 5)
    foreach ($row in $pendingReviewRows) {
        [void]$builder.AppendLine("<li>frame $(Convert-ToHtmlText (Get-CsvValue $row "frame")), pred $(Convert-ToHtmlText (Get-CsvValue $row "sourcePredictionId")): fill label, reviewStatus, evidenceNotes<br><span class=""muted"">$(Convert-ToHtmlText (Get-CsvValue $row "cropPath"))</span></li>")
    }
    if ($pendingReviewRows.Count -eq 0) {
        [void]$builder.AppendLine("<li>No pending crop rows.</li>")
    }
    [void]$builder.AppendLine("</ol><h3>Full-frame rows</h3><ol>")
    $pendingFrameRows = @(Import-Csv $FullFrameReviewCsvPath | Where-Object {
        [string]::IsNullOrWhiteSpace((Get-CsvValue $_ "missedFaceCount")) -or
        [string]::IsNullOrWhiteSpace((Get-CsvValue $_ "missedFaceRowsAdded")) -or
        [string]::IsNullOrWhiteSpace((Get-CsvValue $_ "reviewStatus")) -or
        [string]::IsNullOrWhiteSpace((Get-CsvValue $_ "evidenceNotes"))
    } | Select-Object -First 5)
    foreach ($row in $pendingFrameRows) {
        [void]$builder.AppendLine("<li>frame $(Convert-ToHtmlText (Get-CsvValue $row "frame")): fill missedFaceCount, missedFaceRowsAdded, reviewStatus, evidenceNotes<br><span class=""muted"">$(Convert-ToHtmlText (Get-CsvValue $row "overlayFrameImagePath"))</span></li>")
    }
    if ($pendingFrameRows.Count -eq 0) {
        [void]$builder.AppendLine("<li>No pending full-frame rows.</li>")
    }
    [void]$builder.AppendLine("</ol><h3>GUI smoke rows</h3><ol>")
    $pendingGuiRows = @(Import-Csv $GuiChecklistCsvPath | Where-Object {
        [string]::IsNullOrWhiteSpace((Get-CsvValue $_ "status")) -or
        [string]::IsNullOrWhiteSpace((Get-CsvValue $_ "evidenceType")) -or
        [string]::IsNullOrWhiteSpace((Get-CsvValue $_ "artifactPath")) -or
        [string]::IsNullOrWhiteSpace((Get-CsvValue $_ "evidence"))
    } | Select-Object -First 5)
    foreach ($row in $pendingGuiRows) {
        [void]$builder.AppendLine("<li>$(Convert-ToHtmlText (Get-CsvValue $row "stepId")): fill status, evidenceType, artifactPath, evidence</li>")
    }
    if ($pendingGuiRows.Count -eq 0) {
        [void]$builder.AppendLine("<li>No pending GUI smoke rows.</li>")
    }
    [void]$builder.AppendLine("</ol></section>")
    [void]$builder.AppendLine("<section class=""panel""><h2>AI-Assisted Candidate Reference</h2>")
    [void]$builder.AppendLine("<p class=""muted"">Reference only. These candidate CSVs can speed up visual review, but they are not final GT and do not complete the manual gate.</p>")
    [void]$builder.AppendLine("<ol>")
    if (-not [string]::IsNullOrWhiteSpace($AiCandidateReviewCsvPath) -and (Test-Path $AiCandidateReviewCsvPath)) {
        [void]$builder.AppendLine("<li>full-GT candidate: <span class=""muted"">$(Convert-ToHtmlText $AiCandidateReviewCsvPath)</span></li>")
    }
    if (-not [string]::IsNullOrWhiteSpace($AiCandidateFullFrameReviewCsvPath) -and (Test-Path $AiCandidateFullFrameReviewCsvPath)) {
        [void]$builder.AppendLine("<li>full-frame candidate: <span class=""muted"">$(Convert-ToHtmlText $AiCandidateFullFrameReviewCsvPath)</span></li>")
    }
    [void]$builder.AppendLine("<li>rule: reference-only-not-final-gt</li>")
    [void]$builder.AppendLine("</ol></section>")
    [void]$builder.AppendLine("<section class=""panel""><h2>Pseudo-GT Completion Evidence</h2>")
    [void]$builder.AppendLine("<p class=""muted"">Status: $(Convert-ToHtmlText $pseudoGtStatus). Completion requires published test-only pseudo-GT candidates and review closure before finalizer.</p>")
    [void]$builder.AppendLine("<ol>")
    [void]$builder.AppendLine("<li>$(Convert-ToHtmlText $pseudoGtAction)</li>")
    [void]$builder.AppendLine("<li>Prepare review draft: <span class=""muted"">$(Convert-ToHtmlText $pseudoGtReviewDraftCommand)</span></li>")
    if (Test-Path $resolvedPseudoGtReviewDraftReport) {
        [void]$builder.AppendLine("<li>review draft report: <span class=""muted"">$(Convert-ToHtmlText $resolvedPseudoGtReviewDraftReport)</span></li>")
    }
    [void]$builder.AppendLine("<li>Closure rule: require-all-closed-after-goal-evidence-publish.</li>")
    [void]$builder.AppendLine("</ol></section>")
    [void]$builder.AppendLine("<section class=""panel""><h2>Artifacts</h2><div class=""links"">")
    foreach ($link in $links) {
        $href = Convert-ToRelativeLink -BasePath $resolvedDashboardPath -TargetPath $link.Path
        [void]$builder.AppendLine("<a class=""link"" href=""$(Convert-ToHtmlText $href)""><strong>$(Convert-ToHtmlText $link.Label)</strong><br><span class=""muted"">$(Convert-ToHtmlText $link.Path)</span></a>")
    }
    [void]$builder.AppendLine("</div></section>")
    [void]$builder.AppendLine("<section class=""panel""><h2>Order</h2><ol>")
    [void]$builder.AppendLine("<li>Open the review index and fill full-gt-review.csv crop rows.</li>")
    [void]$builder.AppendLine("<li>Scan full-frame-review.csv rows for missed visible faces and add matching manual missed-face rows when needed.</li>")
    [void]$builder.AppendLine("<li>Run the app, complete all GUI smoke rows, and record preview-track-hold evidence.</li>")
    [void]$builder.AppendLine("<li>Run the strict full-GT verifier, GUI smoke verifier, then the completion finalizer.</li>")
    [void]$builder.AppendLine("</ol></section>")
    [void]$builder.AppendLine("<section class=""panel""><h2>Commands</h2><pre>$(Convert-ToHtmlText ($Commands -join [Environment]::NewLine))</pre></section>")
    [void]$builder.AppendLine("</body>")
    [void]$builder.AppendLine("</html>")

    $builder.ToString() | Set-Content -Encoding UTF8 -Path $resolvedDashboardPath
    return $resolvedDashboardPath
}

function Invoke-RequiredVerifier {
    param(
        [string]$Name,
        [string]$ScriptPath,
        [string[]]$Arguments
    )

    if (-not (Test-Path $ScriptPath)) {
        throw "$Name verifier not found: $ScriptPath"
    }

    Write-Host "[YoloManualGate] start $Name"
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $ScriptPath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE"
    }

    Write-Host "[YoloManualGate] pass $Name"
}

function Ensure-GuiChecklist {
    param([string]$Path)

    $resolved = Resolve-RepoPath $Path
    if (Test-Path $resolved) {
        return Assert-FileNonEmpty "GUI checklist CSV" $Path
    }

    if (-not $PrepareGuiChecklist) {
        throw "GUI checklist CSV not found: $resolved. Pass -PrepareGuiChecklist to create a pending checklist."
    }

    if (-not (Test-Path $guiChecklistGenerator)) {
        throw "GUI checklist generator not found: $guiChecklistGenerator"
    }

    $generatorOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $guiChecklistGenerator -OutputCsv $resolved 2>&1
    if ($LASTEXITCODE -ne 0) {
        $generatorText = ($generatorOutput | Out-String)
        throw "GUI checklist generation failed with exit code $LASTEXITCODE. output=$generatorText"
    }

    Write-Host "[YoloManualGate] preparedGuiChecklist=$resolved"
    return Assert-FileNonEmpty "GUI checklist CSV" $resolved
}

$resolvedReviewIndex = Assert-FileNonEmpty "review index" $ReviewIndex
$resolvedFullGtReviewCsv = Assert-FileNonEmpty "full GT review CSV" $FullGtReviewCsv
$resolvedFullFrameReviewCsv = Assert-FileNonEmpty "full-frame review CSV" $FullFrameReviewCsv
$resolvedGuiChecklistCsv = Ensure-GuiChecklist $GuiChecklistCsv
$resolvedGuiEvidenceGuidePath = Resolve-RepoPath $GuiEvidenceGuidePath
$resolvedPredictionLog = Assert-FileNonEmpty "prediction log" $PredictionLog
$resolvedAiCandidateReviewCsv = Resolve-RepoPath $AiCandidateReviewCsv
$resolvedAiCandidateFullFrameReviewCsv = Resolve-RepoPath $AiCandidateFullFrameReviewCsv
$resolvedPseudoGtCsv = Resolve-RepoPath $PseudoGtCsv
$resolvedPseudoGtReviewQueueCsv = Resolve-RepoPath $PseudoGtReviewQueueCsv
$resolvedPseudoGtReviewClosureCsv = Resolve-RepoPath $PseudoGtReviewClosureCsv
$resolvedPseudoGtReviewClosureSummary = Resolve-RepoPath $PseudoGtReviewClosureSummary
$resolvedPseudoGtReviewDraftDir = Resolve-RepoPath $PseudoGtReviewDraftDir
$resolvedPseudoGtReviewDraftReport = Join-Path $resolvedPseudoGtReviewDraftDir "pseudo-gt-review-draft-report.md"

if ($PrepareGuiEvidence) {
    if (-not (Test-Path $guiEvidencePrep)) {
        throw "GUI smoke evidence prep script not found: $guiEvidencePrep"
    }

    Invoke-RequiredVerifier "gui-smoke-evidence-prep" $guiEvidencePrep @(
        "-ChecklistCsv", $resolvedGuiChecklistCsv,
        "-EvidenceDir", (Resolve-RepoPath $GuiEvidenceDir),
        "-GuidePath", $resolvedGuiEvidenceGuidePath,
        "-UpdateChecklist",
        "-Verify"
    )
}
if (Test-Path $resolvedAiCandidateReviewCsv) {
    Assert-FileNonEmpty "AI-assisted candidate full GT review CSV" $resolvedAiCandidateReviewCsv | Out-Null
}
if (Test-Path $resolvedAiCandidateFullFrameReviewCsv) {
    Assert-FileNonEmpty "AI-assisted candidate full-frame review CSV" $resolvedAiCandidateFullFrameReviewCsv | Out-Null
}
$manualGateProgress = Get-ManualGateProgress `
    -FullGtReviewCsvPath $resolvedFullGtReviewCsv `
    -FullFrameReviewCsvPath $resolvedFullFrameReviewCsv `
    -GuiChecklistCsvPath $resolvedGuiChecklistCsv
$pseudoGtReady = Test-Path $resolvedPseudoGtCsv
$pseudoGtReviewClosureReady = Test-PseudoGtReviewClosureReady `
    -ClosureCsvPath $resolvedPseudoGtReviewClosureCsv `
    -ClosureSummaryPath $resolvedPseudoGtReviewClosureSummary
$remainingGates = @(Get-RemainingManualGates -Progress $manualGateProgress -PseudoGtReady $pseudoGtReady -PseudoGtReviewClosureReady $pseudoGtReviewClosureReady)
$remainingGateText = $remainingGates -join ","
$pendingGuiSmokeRows = @(Get-PendingGuiSmokeRows -GuiChecklistCsvPath $resolvedGuiChecklistCsv)
$nextGuiSmokeRow = $pendingGuiSmokeRows | Select-Object -First 1
$nextGuiStep = if ($null -eq $nextGuiSmokeRow) { "none" } else { Get-CsvValue $nextGuiSmokeRow "stepId" }
$nextGuiEvidenceType = if ($null -eq $nextGuiSmokeRow) { "none" } else { Get-CsvValue $nextGuiSmokeRow "evidenceType" }
$nextGuiArtifactPath = if ($null -eq $nextGuiSmokeRow) { "none" } else { Get-CsvValue $nextGuiSmokeRow "artifactPath" }
$nextGuiEvidenceSetterCommand = if ($null -eq $nextGuiSmokeRow) {
    "none"
}
else {
    "powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\set-yolo-gui-smoke-evidence.ps1 -StepId $nextGuiStep -Evidence `"<replace with observed result for $nextGuiStep>`""
}
$pseudoGtStatus = if ($pseudoGtReady) { "ready" } else { "missing" }
$pseudoGtAction = "run scripts\run-yolo-problem-span-verification.ps1 on a <=30s problem span with tile-face or face-verification evidence and -PublishPseudoGtToGoalEvidence so $PseudoGtCsv exists before pseudo-GT closure and completion finalizer"
$pseudoGtReviewDraftCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\new-yolo-pseudo-gt-review-draft.ps1 -PseudoGtReviewQueueCsv `"$PseudoGtReviewQueueCsv`" -FullFrameReviewCsv `"$FullFrameReviewCsv`" -OutputDir `"$PseudoGtReviewDraftDir`" -Force -Verify"
$completedManualReadinessCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\verify-yolo-manual-readiness-state.ps1 -FullGtReviewCsv `"$FullGtReviewCsv`" -FullFrameReviewCsv `"$FullFrameReviewCsv`" -GuiChecklistCsv `"$GuiChecklistCsv`" -FullGtPredictionLog `"$PredictionLog`" -FullGtMinIou $MinIou -FullGtMaxMisses $MaxMisses -FullGtMaxFalsePositives $MaxFalsePositives -FullGtMaxLowIou $MaxLowIou -AllowCompletedFullGt -AllowCompletedGuiSmoke -AllowQualityGateFailure"
$completedFullGtCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\verify-yolo-full-gt-reviewed-state.ps1 -ReviewCsv `"$FullGtReviewCsv`" -FullFrameReviewCsv `"$FullFrameReviewCsv`" -PredictionLog `"$PredictionLog`" -RequireFullFrameReview -RequireEvidence -RequireArtifacts -MinIou $MinIou -MaxMisses $MaxMisses -MaxFalsePositives $MaxFalsePositives -MaxLowIou $MaxLowIou -AllowQualityGateFailure"
$completedPseudoGtReviewClosureCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\close-yolo-pseudo-gt-review.ps1 -PseudoGtCsv `"$PseudoGtCsv`" -ReviewCsv `"$FullGtReviewCsv`" -FullFrameReviewCsv `"$FullFrameReviewCsv`" -OutputCsv `"$PseudoGtReviewClosureCsv`" -SummaryPath `"$PseudoGtReviewClosureSummary`" -RequireAllClosed"
$completedGuiSmokeCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\verify-yolo-gui-smoke-state.ps1 -ChecklistCsv `"$GuiChecklistCsv`" -RequireManualPass"
    $completedYoloStateCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\verify-yolo-state.ps1 -AllowCompletedFullGt -AllowCompletedGuiSmoke -RequireComplete -FullGtPredictionLog `"$PredictionLog`" -FullGtMinIou $MinIou -FullGtMaxMisses $MaxMisses -FullGtMaxFalsePositives $MaxFalsePositives -FullGtMaxLowIou $MaxLowIou -AllowFullGtQualityGateFailure"
    $completionFinalizerCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\complete-yolo-goal-after-manual-gates.ps1 -FullGtReviewCsv `"$FullGtReviewCsv`" -FullFrameReviewCsv `"$FullFrameReviewCsv`" -GuiChecklistCsv `"$GuiChecklistCsv`" -PredictionLog `"$PredictionLog`" -PseudoGtCsv `"$PseudoGtCsv`" -PseudoGtReviewClosureCsv `"$PseudoGtReviewClosureCsv`" -PseudoGtReviewClosureSummary `"$PseudoGtReviewClosureSummary`" -MinIou $MinIou -MaxMisses $MaxMisses -MaxFalsePositives $MaxFalsePositives -MaxLowIou $MaxLowIou -AllowQualityGateFailure -UpdatePlan -RunYoloState"
    $pendingReportCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\write-yolo-manual-pending-report.ps1 -FullGtReviewCsv `"$FullGtReviewCsv`" -FullFrameReviewCsv `"$FullFrameReviewCsv`" -GuiChecklistCsv `"$GuiChecklistCsv`" -ReviewIndex `"$ReviewIndex`" -OutputPath `"$PendingReportPath`" -Verify"
    $prepareGuiEvidenceCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\prepare-yolo-gui-smoke-evidence.ps1 -ChecklistCsv `"$GuiChecklistCsv`" -EvidenceDir `"$GuiEvidenceDir`" -GuidePath `"$GuiEvidenceGuidePath`" -UpdateChecklist -Verify"
    $humanReviewDraftCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\new-yolo-human-review-draft.ps1 -FullGtReviewCsv `"$FullGtReviewCsv`" -FullFrameReviewCsv `"$FullFrameReviewCsv`" -AiCandidateReviewCsv `"$AiCandidateReviewCsv`" -AiCandidateFullFrameReviewCsv `"$AiCandidateFullFrameReviewCsv`" -OutputDir `"$HumanReviewDraftDir`" -Force -Verify"
$openCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\open-yolo-manual-gates.ps1 -Open"
$openDashboardCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\open-yolo-manual-gates.ps1 -WriteSummary -OpenDashboard"
$openAppCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\open-yolo-manual-gates.ps1 -OpenApp"
$openSmokeManualCommand = "dotnet run --project FaceShield.csproj -- --yolo-smoke --open-manual"
$openSmokeAutoCommand = "dotnet run --project FaceShield.csproj -- --yolo-smoke --open-auto --no-auto-export"
$dashboardCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\open-yolo-manual-gates.ps1 -WriteSummary"
$fullGtAction = "fill label/reviewStatus/evidenceNotes in full-gt-review.csv, fill missedFaceCount/missedFaceRowsAdded/reviewStatus/evidenceNotes in full-frame-review.csv, and add manual missed-face rows when needed"
$guiSmokeAction = "run the Avalonia app, complete every manual-smoke-checklist.csv row with status=pass, evidenceType, artifactPath, and evidence"
$trackHoldAction = "record preview-track-hold evidence showing an already mosaicked target remains covered during short detector misses, while weak one-frame false positives are not kept"
$completionPlanAction = "after full-GT review and GUI smoke evidence are filled, update AUTO_MOSAIC_QUALITY_SPEED_PLAN.md yolo-goal-audit-state to complete=true, remaining=none, completion-audit=pass-complete; AllowQualityGateFailure keeps failed YOLO quality gates as recommendation-none evidence instead of promoting the model"

Write-Host "[YoloManualGate] reviewIndex=$resolvedReviewIndex"
Write-Host "[YoloManualGate] fullGtReviewCsv=$resolvedFullGtReviewCsv"
Write-Host "[YoloManualGate] fullFrameReviewCsv=$resolvedFullFrameReviewCsv"
Write-Host "[YoloManualGate] guiChecklistCsv=$resolvedGuiChecklistCsv"
Write-Host "[YoloManualGate] predictionLog=$resolvedPredictionLog"
Write-Host "[YoloManualGate] aiCandidateFullGtReviewCsv=$resolvedAiCandidateReviewCsv"
Write-Host "[YoloManualGate] aiCandidateFullFrameReviewCsv=$resolvedAiCandidateFullFrameReviewCsv"
Write-Host "[YoloManualGate] aiCandidateRule=reference-only-not-final-gt"
Write-Host "[YoloManualGate] pseudoGtCsv=$resolvedPseudoGtCsv"
Write-Host "[YoloManualGate] pseudoGtStatus=$pseudoGtStatus"
Write-Host "[YoloManualGate] pseudoGtReviewClosureCsv=$resolvedPseudoGtReviewClosureCsv"
Write-Host "[YoloManualGate] pseudoGtReviewClosureSummary=$resolvedPseudoGtReviewClosureSummary"
Write-Host "[YoloManualGate] pseudoGtReviewQueueCsv=$resolvedPseudoGtReviewQueueCsv"
Write-Host "[YoloManualGate] pseudoGtReviewDraftDir=$resolvedPseudoGtReviewDraftDir"
Write-Host "[YoloManualGate] pseudoGtReviewDraftReport=$resolvedPseudoGtReviewDraftReport"
Write-Host "[YoloManualGate] pseudoGtReviewDraftCommand=$pseudoGtReviewDraftCommand"
Write-Host "[YoloManualGate] completedManualReadinessCommand=$completedManualReadinessCommand"
Write-Host "[YoloManualGate] completedFullGtCommand=$completedFullGtCommand"
Write-Host "[YoloManualGate] completedPseudoGtReviewClosureCommand=$completedPseudoGtReviewClosureCommand"
Write-Host "[YoloManualGate] completedGuiSmokeCommand=$completedGuiSmokeCommand"
Write-Host "[YoloManualGate] completedYoloStateCommand=$completedYoloStateCommand"
Write-Host "[YoloManualGate] completionFinalizerCommand=$completionFinalizerCommand"
Write-Host "[YoloManualGate] pendingReportCommand=$pendingReportCommand"
Write-Host "[YoloManualGate] prepareGuiEvidenceCommand=$prepareGuiEvidenceCommand"
Write-Host "[YoloManualGate] humanReviewDraftCommand=$humanReviewDraftCommand"
Write-Host "[YoloManualGate] remaining=$remainingGateText"
Write-Host "[YoloManualGate] progress=fullGtPendingRows=$($manualGateProgress.FullGtPendingRows),fullFramePendingRows=$($manualGateProgress.FullFramePendingRows),guiPendingRows=$($manualGateProgress.GuiPendingRows)"
Write-Host "[YoloManualGate] openCommand=$openCommand"
Write-Host "[YoloManualGate] openDashboardCommand=$openDashboardCommand"
Write-Host "[YoloManualGate] openAppCommand=$openAppCommand"
Write-Host "[YoloManualGate] openSmokeManualCommand=$openSmokeManualCommand"
Write-Host "[YoloManualGate] openSmokeAutoCommand=$openSmokeAutoCommand"
Write-Host "[YoloManualGate] dashboardCommand=$dashboardCommand"
Write-Host "[YoloManualGate] fullGtAction=$fullGtAction"
Write-Host "[YoloManualGate] guiSmokeAction=$guiSmokeAction"
Write-Host "[YoloManualGate] trackHoldAction=$trackHoldAction"
Write-Host "[YoloManualGate] pseudoGtAction=$pseudoGtAction"
Write-Host "[YoloManualGate] completionPlanAction=$completionPlanAction"
Write-Host "[YoloManualGate] nextGuiStep=$nextGuiStep"
Write-Host "[YoloManualGate] nextGuiEvidenceType=$nextGuiEvidenceType"
Write-Host "[YoloManualGate] nextGuiArtifactPath=$nextGuiArtifactPath"
Write-Host "[YoloManualGate] nextGuiEvidenceSetterCommand=$nextGuiEvidenceSetterCommand"

if ($WriteSummary) {
    $resolvedSummaryPath = Resolve-RepoPath $SummaryPath
    $resolvedDashboardPath = Resolve-RepoPath $DashboardPath
    $resolvedPendingReportPath = Resolve-RepoPath $PendingReportPath
    $resolvedHumanReviewDraftDir = Resolve-RepoPath $HumanReviewDraftDir
    $resolvedHumanReviewDraftReportPath = Join-Path $resolvedHumanReviewDraftDir "human-review-draft-report.md"
    $summaryDir = Split-Path -Parent $resolvedSummaryPath
    New-Item -ItemType Directory -Force -Path $summaryDir | Out-Null
    $fullGtReviewComplete = $manualGateProgress.FullGtPendingRows -eq 0 -and $manualGateProgress.FullFramePendingRows -eq 0
    $humanReviewDraftStatus = if ($fullGtReviewComplete) { "skippedHumanReviewDraft=full-gt-complete" } else { "humanReviewDraftCommand=available" }
    $commands = @(
        $openCommand,
        $openDashboardCommand,
        $openAppCommand,
        $openSmokeManualCommand,
        $openSmokeAutoCommand,
        $nextGuiEvidenceSetterCommand,
        $completedFullGtCommand,
        $completedGuiSmokeCommand,
        $completedManualReadinessCommand,
        $completedPseudoGtReviewClosureCommand,
        $completedYoloStateCommand,
        $completionFinalizerCommand,
        $pendingReportCommand,
        $prepareGuiEvidenceCommand,
        $dashboardCommand
    )
    if (-not $fullGtReviewComplete) {
        $commands += $humanReviewDraftCommand
    }
    if (Test-Path $resolvedPseudoGtReviewQueueCsv) {
        $commands += $pseudoGtReviewDraftCommand
    }

    $summary = @(
        "# YOLO Manual Gate Summary",
        "",
        "## Remaining",
        ($remainingGates | ForEach-Object { "- $_" }),
        "",
        "## Artifacts",
        "- reviewIndex: $resolvedReviewIndex",
        "- fullGtReviewCsv: $resolvedFullGtReviewCsv",
        "- fullFrameReviewCsv: $resolvedFullFrameReviewCsv",
        "- guiChecklistCsv: $resolvedGuiChecklistCsv",
        "- predictionLog: $resolvedPredictionLog",
        "- aiCandidateFullGtReviewCsv: $resolvedAiCandidateReviewCsv",
        "- aiCandidateFullFrameReviewCsv: $resolvedAiCandidateFullFrameReviewCsv",
        "- aiCandidateRule: reference-only-not-final-gt",
        "- pseudoGtCsv: $resolvedPseudoGtCsv",
        "- pseudoGtReviewQueueCsv: $resolvedPseudoGtReviewQueueCsv",
        "- pseudoGtStatus: $pseudoGtStatus",
        "- pseudoGtReviewDraftDir: $resolvedPseudoGtReviewDraftDir",
        "- pseudoGtReviewDraftReport: $resolvedPseudoGtReviewDraftReport",
        "- pseudoGtReviewClosureCsv: $resolvedPseudoGtReviewClosureCsv",
        "- pseudoGtReviewClosureSummary: $resolvedPseudoGtReviewClosureSummary",
        "- pseudoGtReviewClosureRule: require-all-closed-after-goal-evidence-publish",
        "- humanReviewDraftReport: $resolvedHumanReviewDraftReportPath",
        "- guiEvidenceGuidePath: $resolvedGuiEvidenceGuidePath",
        "- openSmokeManualCommand: $openSmokeManualCommand",
        "- openSmokeAutoCommand: $openSmokeAutoCommand",
        "- $humanReviewDraftStatus",
        "- pseudoGtReviewDraftCommand: $pseudoGtReviewDraftCommand",
        "- pendingReportPath: $resolvedPendingReportPath",
        "- dashboardPath: $resolvedDashboardPath",
        "",
        "## Progress",
        "- fullGtReviewRows=$($manualGateProgress.FullGtReviewRows)",
        "- fullGtPendingRows=$($manualGateProgress.FullGtPendingRows)",
        "- fullFrameRows=$($manualGateProgress.FullFrameRows)",
        "- fullFramePendingRows=$($manualGateProgress.FullFramePendingRows)",
        "- guiRows=$($manualGateProgress.GuiRows)",
        "- guiPendingRows=$($manualGateProgress.GuiPendingRows)",
        "",
        "## Next GUI Step",
        "- nextGuiStep=$nextGuiStep",
        "- nextGuiEvidenceType=$nextGuiEvidenceType",
        "- nextGuiArtifactPath=$nextGuiArtifactPath",
        "- nextGuiEvidenceSetterCommand=$nextGuiEvidenceSetterCommand",
        "",
        "## Actions",
        "- fullGtAction: $fullGtAction",
        "- guiSmokeAction: $guiSmokeAction",
        "- trackHoldAction: $trackHoldAction",
        "- pseudoGtAction: $pseudoGtAction",
        "- completionPlanAction: $completionPlanAction",
        "",
        "## Required GUI Steps",
        "- open-video",
        "- select-yolo-backend",
        "- download-yolo-model",
        "- run-yolo-auto-detect",
        "- preview-result",
        "- preview-track-hold",
        "- manual-edit",
        "- export",
        "- reopen-state",
        "",
        "## Commands",
        '```powershell',
        $commands,
        '```'
    )
    Set-Content -Encoding UTF8 -Path $resolvedSummaryPath -Value $summary
    if ($fullGtReviewComplete) {
        Write-Host "[YoloManualGate] skippedHumanReviewDraft=full-gt-complete"
    }
    else {
        if (-not (Test-Path $humanReviewDraftWriter)) {
            throw "Human review draft writer not found: $humanReviewDraftWriter"
        }

        Invoke-RequiredVerifier "human-review-draft" $humanReviewDraftWriter @(
            "-FullGtReviewCsv", $resolvedFullGtReviewCsv,
            "-FullFrameReviewCsv", $resolvedFullFrameReviewCsv,
            "-AiCandidateReviewCsv", $resolvedAiCandidateReviewCsv,
            "-AiCandidateFullFrameReviewCsv", $resolvedAiCandidateFullFrameReviewCsv,
            "-OutputDir", $resolvedHumanReviewDraftDir,
            "-Force",
            "-Verify"
        )
    }

    $writtenDashboardPath = Write-ManualGateDashboard `
        -Path $resolvedDashboardPath `
        -ReviewIndexPath $resolvedReviewIndex `
        -FullGtReviewCsvPath $resolvedFullGtReviewCsv `
        -FullFrameReviewCsvPath $resolvedFullFrameReviewCsv `
        -GuiChecklistCsvPath $resolvedGuiChecklistCsv `
        -AiCandidateReviewCsvPath $resolvedAiCandidateReviewCsv `
        -AiCandidateFullFrameReviewCsvPath $resolvedAiCandidateFullFrameReviewCsv `
        -HumanReviewDraftReportPath $resolvedHumanReviewDraftReportPath `
        -GuiEvidenceGuideResolvedPath $resolvedGuiEvidenceGuidePath `
        -PendingReportResolvedPath $resolvedPendingReportPath `
        -Progress $manualGateProgress `
        -PseudoGtReady $pseudoGtReady `
        -PseudoGtReviewClosureReady $pseudoGtReviewClosureReady `
        -Commands $commands
    Write-Host "[YoloManualGate] summaryPath=$resolvedSummaryPath"
    Write-Host "[YoloManualGate] dashboardPath=$writtenDashboardPath"
}

if ($WriteSummary) {
    if (-not (Test-Path $pendingReportWriter)) {
        throw "Pending report writer not found: $pendingReportWriter"
    }

    Invoke-RequiredVerifier "manual-pending-report" $pendingReportWriter @(
        "-FullGtReviewCsv", $resolvedFullGtReviewCsv,
        "-FullFrameReviewCsv", $resolvedFullFrameReviewCsv,
        "-GuiChecklistCsv", $resolvedGuiChecklistCsv,
        "-ReviewIndex", $resolvedReviewIndex,
        "-AiCandidateReviewCsv", $resolvedAiCandidateReviewCsv,
        "-AiCandidateFullFrameReviewCsv", $resolvedAiCandidateFullFrameReviewCsv,
        "-OutputPath", (Resolve-RepoPath $PendingReportPath),
        "-Verify"
    )
}

if ($Open) {
    foreach ($path in @($resolvedReviewIndex, $resolvedFullGtReviewCsv, $resolvedFullFrameReviewCsv, $resolvedGuiChecklistCsv)) {
        Invoke-Item $path
    }

    Write-Host "[YoloManualGate] opened manual review artifacts"
}

if ($OpenDashboard) {
    $resolvedDashboardPath = Resolve-RepoPath $DashboardPath
    if (-not (Test-Path $resolvedDashboardPath)) {
        if (-not $WriteSummary) {
            throw "Dashboard not found: $resolvedDashboardPath. Pass -WriteSummary to create it before opening."
        }
    }

    Assert-FileNonEmpty "manual gate dashboard" $resolvedDashboardPath | Out-Null
    Invoke-Item $resolvedDashboardPath
    Write-Host "[YoloManualGate] openedDashboard=$resolvedDashboardPath"
}

if ($OpenApp) {
    Start-Process -FilePath "dotnet" -ArgumentList @("run", "--project", "FaceShield.csproj") -WorkingDirectory $repo
    Write-Host "[YoloManualGate] opened FaceShield app"
}

if ($VerifyReady) {
    Invoke-RequiredVerifier "manual-readiness-state" $manualReadinessVerifier @(
        "-FullGtReviewCsv", $resolvedFullGtReviewCsv,
        "-FullFrameReviewCsv", $resolvedFullFrameReviewCsv,
        "-GuiChecklistCsv", $resolvedGuiChecklistCsv,
        "-FullGtPredictionLog", $resolvedPredictionLog,
        "-FullGtMinIou", "$MinIou",
        "-FullGtMaxMisses", "$MaxMisses",
        "-FullGtMaxFalsePositives", "$MaxFalsePositives",
        "-FullGtMaxLowIou", "$MaxLowIou",
        "-AllowCompletedFullGt",
        "-AllowQualityGateFailure"
    )
}

if ($VerifyCompleted) {
    Invoke-RequiredVerifier "manual-readiness-completed-state" $manualReadinessVerifier @(
        "-FullGtReviewCsv", $resolvedFullGtReviewCsv,
        "-FullFrameReviewCsv", $resolvedFullFrameReviewCsv,
        "-GuiChecklistCsv", $resolvedGuiChecklistCsv,
        "-FullGtPredictionLog", $resolvedPredictionLog,
        "-FullGtMinIou", "$MinIou",
        "-FullGtMaxMisses", "$MaxMisses",
        "-FullGtMaxFalsePositives", "$MaxFalsePositives",
        "-FullGtMaxLowIou", "$MaxLowIou",
        "-AllowCompletedFullGt",
        "-AllowCompletedGuiSmoke",
        "-AllowQualityGateFailure"
    )
    Invoke-RequiredVerifier "full-gt-reviewed-state" $fullGtReviewedVerifier @(
        "-ReviewCsv", $resolvedFullGtReviewCsv,
        "-FullFrameReviewCsv", $resolvedFullFrameReviewCsv,
        "-PredictionLog", $resolvedPredictionLog,
        "-RequireFullFrameReview",
        "-RequireEvidence",
        "-RequireArtifacts",
        "-MinIou", "$MinIou",
        "-MaxMisses", "$MaxMisses",
        "-MaxFalsePositives", "$MaxFalsePositives",
        "-MaxLowIou", "$MaxLowIou",
        "-AllowQualityGateFailure"
    )
    Invoke-RequiredVerifier "gui-smoke-state" $guiSmokeVerifier @(
        "-ChecklistCsv", $resolvedGuiChecklistCsv,
        "-RequireManualPass"
    )
    if (-not (Test-Path $resolvedPseudoGtCsv)) {
        throw "Pseudo-GT candidate CSV is required before completed manual gates: $resolvedPseudoGtCsv. Run scripts\run-yolo-problem-span-verification.ps1 with tile-face or face-verification evidence and -PublishPseudoGtToGoalEvidence."
    }
    Invoke-RequiredVerifier "pseudo-gt-review-closure" $pseudoGtReviewClosure @(
        "-PseudoGtCsv", $resolvedPseudoGtCsv,
        "-ReviewCsv", $resolvedFullGtReviewCsv,
        "-FullFrameReviewCsv", $resolvedFullFrameReviewCsv,
        "-OutputCsv", $resolvedPseudoGtReviewClosureCsv,
        "-SummaryPath", $resolvedPseudoGtReviewClosureSummary,
        "-RequireAllClosed"
    )
}

Write-Host "[YoloManualGate] all requested checks passed"
