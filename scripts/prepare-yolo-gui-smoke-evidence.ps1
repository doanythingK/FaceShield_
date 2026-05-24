param(
    [string]$ChecklistCsv = ".tmp\yolo-gui-smoke\manual-smoke-checklist.csv",
    [string]$EvidenceDir = ".tmp\yolo-gui-smoke\evidence",
    [string]$GuidePath = ".tmp\yolo-gui-smoke\gui-smoke-evidence-guide.md",
    [switch]$UpdateChecklist,
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

function Get-RepoRelativePath {
    param([string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    $repoRoot = [IO.Path]::GetFullPath($repo)
    if (-not $repoRoot.EndsWith([IO.Path]::DirectorySeparatorChar)) {
        $repoRoot += [IO.Path]::DirectorySeparatorChar
    }

    if ($fullPath.StartsWith($repoRoot, [StringComparison]::OrdinalIgnoreCase)) {
        return $fullPath.Substring($repoRoot.Length).Replace("\", "/")
    }

    return $fullPath
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

    Write-Host "[YoloGuiSmokeEvidencePrep] pass $Name"
}

$steps = @(
    [pscustomobject]@{
        stepId = "open-video"
        fileName = "open-video.png"
        evidenceType = "screenshot-or-recording"
        note = "Open a short srcTest video from Home and capture the Workspace preview frames."
    },
    [pscustomobject]@{
        stepId = "select-yolo-backend"
        fileName = "select-yolo-backend.png"
        evidenceType = "screenshot"
        note = "Select YOLO Face ONNX and a YOLO profile, with FaceONNX thresholds still separate."
    },
    [pscustomobject]@{
        stepId = "download-yolo-model"
        fileName = "download-yolo-model.log"
        evidenceType = "screenshot-or-log"
        note = "Use the YOLO download button or confirm an existing model and AutoYoloModelPath."
    },
    [pscustomobject]@{
        stepId = "run-yolo-auto-detect"
        fileName = "run-yolo-auto-detect.log"
        evidenceType = "screenshot-or-log"
        note = "Run YOLO automatic mosaic and capture the completed status or log."
    },
    [pscustomobject]@{
        stepId = "preview-result"
        fileName = "preview-result.mp4"
        evidenceType = "screenshot-or-recording"
        note = "Scrub or play preview frames and capture masked faces without obvious flicker."
    },
    [pscustomobject]@{
        stepId = "preview-track-hold"
        fileName = "preview-track-hold.mp4"
        evidenceType = "recording"
        note = "Record a previously masked face staying covered during short detector misses."
    },
    [pscustomobject]@{
        stepId = "manual-edit"
        fileName = "manual-edit.png"
        evidenceType = "screenshot-or-recording"
        note = "Capture manual, brush, eraser, or undo editing reflected in preview."
    },
    [pscustomobject]@{
        stepId = "export"
        fileName = "export.mp4"
        evidenceType = "output-file"
        note = "Use the actual exported YOLO workspace video file."
    },
    [pscustomobject]@{
        stepId = "reopen-state"
        fileName = "reopen-state.png"
        evidenceType = "screenshot-or-recording"
        note = "Reopen the same workspace and capture restored YOLO model, profile, and mask state."
    }
)

$checklistPath = Resolve-RepoPath $ChecklistCsv
if (-not (Test-Path $checklistPath)) {
    throw "GUI smoke checklist not found: $checklistPath"
}

$resolvedEvidenceDir = Resolve-RepoPath $EvidenceDir
$resolvedGuidePath = Resolve-RepoPath $GuidePath
$guideDir = Split-Path -Parent $resolvedGuidePath
New-Item -ItemType Directory -Force -Path $resolvedEvidenceDir | Out-Null
if (-not [string]::IsNullOrWhiteSpace($guideDir)) {
    New-Item -ItemType Directory -Force -Path $guideDir | Out-Null
}

$rows = @(Import-Csv $checklistPath)
foreach ($step in $steps) {
    $row = $rows | Where-Object { $_.stepId -eq $step.stepId } | Select-Object -First 1
    if ($null -eq $row) {
        throw "GUI smoke checklist missing step: $($step.stepId)"
    }

    foreach ($column in @("status", "evidenceType", "artifactPath", "evidence", "notes")) {
        if ($null -eq $row.PSObject.Properties[$column]) {
            throw "GUI smoke checklist row missing column '$column': $($step.stepId)"
        }
    }

    if ($UpdateChecklist -and [string]::IsNullOrWhiteSpace($row.artifactPath)) {
        $artifactPath = Join-Path $resolvedEvidenceDir $step.fileName
        $row.artifactPath = Get-RepoRelativePath $artifactPath
    }
}

if ($UpdateChecklist) {
    # This intentionally does not set status=pass or evidence. Manual verification must fill those fields.
    $rows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $checklistPath
}

$artifactLines = foreach ($step in $steps) {
    $artifactPath = Get-RepoRelativePath (Join-Path $resolvedEvidenceDir $step.fileName)
    "| $($step.stepId) | $($step.evidenceType) | $artifactPath | $($step.note) |"
}

$setterCommands = foreach ($step in $steps) {
    "powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\set-yolo-gui-smoke-evidence.ps1 -StepId $($step.stepId) -Evidence `"<replace with observed result for $($step.stepId)>`""
}

$guide = @(
    "# YOLO GUI Smoke Evidence Guide",
    "",
    "This guide prepares paths only. It does not mark any GUI smoke step as passed.",
    "",
    "## Commands",
    "",
    '```powershell',
    "powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\open-yolo-manual-gates.ps1 -OpenApp",
    "powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\set-yolo-gui-smoke-evidence.ps1 -StepId preview-track-hold -Evidence `"Recorded track hold with no off/on flicker during short detector miss.`"",
    "powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\verify-yolo-gui-smoke-state.ps1 -ChecklistCsv `"$ChecklistCsv`" -RequireManualPass",
    '```',
    "",
    "## Artifact Paths",
    "",
    "| stepId | evidenceType | artifactPath | required evidence |",
    "| --- | --- | --- | --- |"
) + $artifactLines + @(
    "",
    "## Evidence Setter Commands",
    "",
    "Run one command after capturing the matching artifact. Replace the evidence text with the observed result.",
    "",
    '```powershell',
    $setterCommands,
    '```',
    "",
    "## Completion Rule",
    "",
    "- Fill status=pass only after manually verifying the step in the Avalonia app.",
    "- You can use set-yolo-gui-smoke-evidence.ps1 after each capture to validate the artifact and fill one checklist row.",
    "- Fill evidence with the observed result, not just the file name.",
    "- preview-track-hold must be a non-empty video recording that shows no off/on mosaic flicker during short detector misses.",
    "- export must point to the actual exported video file.",
    "- Run the verifier command above after filling all rows."
)

Set-Content -Encoding UTF8 -Path $resolvedGuidePath -Value $guide

if ($Verify) {
    $guideText = Get-Content -Raw -Path $resolvedGuidePath
    Assert-Contains "guide records no auto pass" $guideText "does not mark any GUI smoke step as passed"
    Assert-Contains "guide records preview track hold" $guideText "preview-track-hold"
    Assert-Contains "guide records evidence setter" $guideText "set-yolo-gui-smoke-evidence.ps1"
    Assert-Contains "guide records all-step setter section" $guideText "Evidence Setter Commands"
    Assert-Contains "guide records open-video setter" $guideText "-StepId open-video"
    Assert-Contains "guide records track-hold setter" $guideText "-StepId preview-track-hold"
    Assert-Contains "guide records reopen-state setter" $guideText "-StepId reopen-state"
    Assert-Contains "guide records manual verifier" $guideText "verify-yolo-gui-smoke-state.ps1"
    Assert-Contains "guide records export output" $guideText "export must point to the actual exported video file"

	    if ($UpdateChecklist) {
	        $updatedRows = @(Import-Csv $checklistPath)
	        foreach ($step in $steps) {
	            $row = $updatedRows | Where-Object { $_.stepId -eq $step.stepId } | Select-Object -First 1
	            if ([string]::IsNullOrWhiteSpace($row.artifactPath)) {
	                throw "GUI smoke checklist artifactPath was not prepared: $($step.stepId)"
	            }
	            if ($row.status.Trim().ToLowerInvariant() -eq "pass" -or -not [string]::IsNullOrWhiteSpace($row.evidence)) {
	                continue
	            }
	        }

	        Write-Host "[YoloGuiSmokeEvidencePrep] pass checklist artifact paths prepared without changing final evidence"
	    }
}

Write-Host "[YoloGuiSmokeEvidencePrep] evidenceDir=$resolvedEvidenceDir"
Write-Host "[YoloGuiSmokeEvidencePrep] guidePath=$resolvedGuidePath"
Write-Host "[YoloGuiSmokeEvidencePrep] checklist=$checklistPath"
