param(
    [string]$ChecklistCsv = ".tmp\yolo-gui-smoke\manual-smoke-checklist.csv",
    [string]$StepId,
    [string]$ArtifactPath,
    [string]$Evidence,
    [switch]$Force,
    [switch]$SelfTest
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

function Get-AllowedArtifactExtensions {
    param([string]$EvidenceType)

    switch ($EvidenceType) {
        "screenshot" { return @(".png", ".jpg", ".jpeg", ".webp", ".bmp") }
        "screenshot-or-recording" { return @(".png", ".jpg", ".jpeg", ".webp", ".bmp", ".mp4", ".mov", ".mkv", ".avi", ".webm") }
        "recording" { return @(".mp4", ".mov", ".mkv", ".avi", ".webm") }
        "screenshot-or-log" { return @(".png", ".jpg", ".jpeg", ".webp", ".bmp", ".log", ".txt") }
        "output-file" { return @(".mp4", ".mov", ".mkv", ".avi", ".webm") }
        default { throw "Unsupported evidenceType: $EvidenceType" }
    }
}

function Assert-Artifact {
    param(
        [string]$Step,
        [string]$EvidenceType,
        [string]$Path
    )

    $resolved = Resolve-RepoPath $Path
    if (-not (Test-Path $resolved)) {
        throw "Artifact does not exist: step=$Step artifactPath=$resolved"
    }

    $item = Get-Item $resolved
    if ($item -isnot [IO.FileInfo]) {
        throw "Artifact is not a file: step=$Step artifactPath=$resolved"
    }

    if ($item.Length -le 0) {
        throw "Artifact is empty: step=$Step artifactPath=$resolved"
    }

    $extension = $item.Extension.ToLowerInvariant()
    $allowed = Get-AllowedArtifactExtensions $EvidenceType
    if ($extension -notin $allowed) {
        throw "Artifact extension does not match evidenceType: step=$Step evidenceType=$EvidenceType extension=$extension allowed=$($allowed -join ',')"
    }

    return Get-RepoRelativePath $resolved
}

function Set-GuiSmokeEvidence {
    param(
        [string]$CsvPath,
        [string]$Step,
        [string]$Artifact,
        [string]$EvidenceText,
        [bool]$AllowOverwrite
    )

    $resolvedChecklist = Resolve-RepoPath $CsvPath
    if (-not (Test-Path $resolvedChecklist)) {
        throw "Checklist not found: $resolvedChecklist"
    }

    if ([string]::IsNullOrWhiteSpace($EvidenceText)) {
        throw "Evidence text is required for step=$Step"
    }

    $rows = @(Import-Csv $resolvedChecklist)
    $row = $rows | Where-Object { $_.stepId -eq $Step } | Select-Object -First 1
    if ($null -eq $row) {
        throw "Checklist step not found: $Step"
    }

    foreach ($column in @("status", "evidenceType", "artifactPath", "evidence", "notes")) {
        if ($null -eq $row.PSObject.Properties[$column]) {
            throw "Checklist row missing column '$column': $Step"
        }
    }

    if (-not $AllowOverwrite -and $row.status.Trim().ToLowerInvariant() -eq "pass") {
        throw "Checklist step is already pass. Use -Force to overwrite: $Step"
    }

    $artifactCandidate = if ([string]::IsNullOrWhiteSpace($Artifact)) { $row.artifactPath } else { $Artifact }
    if ([string]::IsNullOrWhiteSpace($artifactCandidate)) {
        throw "ArtifactPath is required for step=$Step"
    }

    $artifactRelativePath = Assert-Artifact $Step $row.evidenceType.Trim().ToLowerInvariant() $artifactCandidate

    $row.status = "pass"
    $row.artifactPath = $artifactRelativePath
    $row.evidence = $EvidenceText.Trim()

    $rows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $resolvedChecklist

    Write-Host "[YoloGuiSmokeEvidenceSet] step=$Step status=pass artifactPath=$artifactRelativePath"
}

function Assert-Throws {
    param(
        [string]$Name,
        [scriptblock]$Action,
        [string]$ExpectedText
    )

    try {
        & $Action
    }
    catch {
        $message = $_.Exception.Message
        if (-not [string]::IsNullOrWhiteSpace($ExpectedText) -and $message -notlike "*$ExpectedText*") {
            throw "$Name threw an unexpected error. Expected text '$ExpectedText', actual '$message'"
        }

        Write-Host "[YoloGuiSmokeEvidenceSet] pass negative selftest $Name"
        return
    }

    throw "$Name did not throw"
}

if ($SelfTest) {
    $selfTestDir = Join-Path $repo ".tmp\yolo-gui-smoke\evidence-set-selftest"
    New-Item -ItemType Directory -Force -Path $selfTestDir | Out-Null

    $artifact = Join-Path $selfTestDir "preview-track-hold.mp4"
    Set-Content -Encoding UTF8 -Path $artifact -Value "selftest video artifact"

    $checklist = Join-Path $selfTestDir "manual-smoke-checklist.csv"
    @(
        [pscustomobject]@{
            stepId = "preview-track-hold"
            status = ""
            evidenceType = "recording"
            artifactPath = Get-RepoRelativePath $artifact
            evidence = ""
            notes = "selftest"
        }
    ) | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $checklist

    Set-GuiSmokeEvidence `
        -CsvPath $checklist `
        -Step "preview-track-hold" `
        -Artifact "" `
        -EvidenceText "selftest verified track hold recording" `
        -AllowOverwrite $false

    $updated = @(Import-Csv $checklist)
    if ($updated[0].status -ne "pass" -or $updated[0].evidence -notlike "*track hold*") {
        throw "Selftest failed to update checklist row"
    }
    Write-Host "[YoloGuiSmokeEvidenceSet] pass selftest update row"

    Assert-Throws "missing artifact" {
        Set-GuiSmokeEvidence `
            -CsvPath $checklist `
            -Step "preview-track-hold" `
            -Artifact (Join-Path $selfTestDir "missing.mp4") `
            -EvidenceText "missing artifact should fail" `
            -AllowOverwrite $true
    } "Artifact does not exist"

    $wrongArtifact = Join-Path $selfTestDir "preview-track-hold.txt"
    Set-Content -Encoding UTF8 -Path $wrongArtifact -Value "not a recording"
    Assert-Throws "wrong artifact type" {
        Set-GuiSmokeEvidence `
            -CsvPath $checklist `
            -Step "preview-track-hold" `
            -Artifact $wrongArtifact `
            -EvidenceText "wrong artifact type should fail" `
            -AllowOverwrite $true
    } "extension does not match evidenceType"

    Write-Host "[YoloGuiSmokeEvidenceSet] all requested checks passed"
    return
}

if ([string]::IsNullOrWhiteSpace($StepId)) {
    throw "StepId is required unless -SelfTest is used."
}

if ([string]::IsNullOrWhiteSpace($Evidence)) {
    throw "Evidence is required unless -SelfTest is used."
}

Set-GuiSmokeEvidence `
    -CsvPath $ChecklistCsv `
    -Step $StepId `
    -Artifact $ArtifactPath `
    -EvidenceText $Evidence `
    -AllowOverwrite $Force.IsPresent
