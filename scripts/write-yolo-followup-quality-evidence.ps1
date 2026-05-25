param(
    [string]$Source = ".tmp\srcTest-smoke\yolo-quality-2s.mp4",
    [string]$VideoPath = ".tmp\srcTest-smoke\yolo-quality-2s.mp4",
    [string]$OutputDir = ".tmp\yolo-quality",
    [string]$PredictionLog = "",
    [string]$ChecklistPath = "",
    [string]$TemplateCsv = "",
    [string]$ReviewPackageDir = "",
    [string]$SummaryPath = "",
    [string]$MaskContinuityPath = "",
    [string]$TrimStart = "",
    [int]$TrimSeconds = 0,
    [string]$ClipPath = "",
    [switch]$ForceTrim,
    [switch]$RunSmoke,
    [switch]$ForceRunSmoke,
    [ValidateSet("YoloV8Face", "Yolo5Face")]
    [string]$YoloModelType = "Yolo5Face",
    [string]$YoloModelPath = "",
    [int]$YoloInputSize = 640,
    [double]$YoloObjectnessThreshold = 0.12,
    [double]$YoloConfidenceThreshold = 0.18,
    [double]$YoloNmsThreshold = 0.45,
    [int]$YoloMaxLostFillFrames = 3,
    [int]$ParallelDetectorCount = 2,
    [int]$VideoFrameCount = 60,
    [int]$MaxFullFrameRows = 8,
    [int]$FullFrameScaleWidth = 960,
    [int]$SmokeTimeoutSeconds = 0,
    [switch]$AllowNoDetections,
    [switch]$SkipReviewPackage,
    [switch]$ForceReviewPackage
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

function Resolve-RepoPath {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return ""
    }

    if ([IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return Join-Path $repo $Path
}

function Add-SwitchArg {
    param(
        [System.Collections.Generic.List[string]]$Arguments,
        [string]$Name,
        [bool]$Enabled
    )

    if ($Enabled) {
        $Arguments.Add($Name) | Out-Null
    }
}

function Add-ValueArg {
    param(
        [System.Collections.Generic.List[string]]$Arguments,
        [string]$Name,
        [object]$Value
    )

    $Arguments.Add($Name) | Out-Null
    $Arguments.Add([string]$Value) | Out-Null
}

function Require-File {
    param(
        [string]$Name,
        [string]$Path
    )

    if (-not (Test-Path $Path)) {
        throw "$Name not found: $Path"
    }
}

function Read-MatchValue {
    param(
        [string]$Text,
        [string]$Pattern,
        [string]$Default = "none"
    )

    $match = [regex]::Match($Text, $Pattern)
    if ($match.Success) {
        return $match.Groups[1].Value
    }

    return $Default
}

function Add-FrameListValues {
    param(
        [System.Collections.Generic.SortedSet[int]]$Frames,
        [string]$Text
    )

    if ([string]::IsNullOrWhiteSpace($Text) -or $Text -eq "none") {
        return
    }

    foreach ($value in @($Text -split ",")) {
        if ($value -match '^\d+$') {
            [void]$Frames.Add([int]$value)
        }
    }
}

function Add-FramePairTargetValues {
    param(
        [System.Collections.Generic.SortedSet[int]]$Frames,
        [string]$Text
    )

    if ([string]::IsNullOrWhiteSpace($Text) -or $Text -eq "none") {
        return
    }

    foreach ($value in @($Text -split ",")) {
        if ($value -match '^\d+->(\d+)$') {
            [void]$Frames.Add([int]$Matches[1])
        }
    }
}

function Add-FrameRangeValues {
    param(
        [System.Collections.Generic.SortedSet[int]]$Frames,
        [string]$Text
    )

    if ([string]::IsNullOrWhiteSpace($Text) -or $Text -eq "none") {
        return
    }

    foreach ($value in @($Text -split ",")) {
        if ($value -match '^(\d+)-(\d+)$') {
            $start = [int]$Matches[1]
            $end = [int]$Matches[2]
            for ($frame = $start; $frame -le $end; $frame++) {
                [void]$Frames.Add($frame)
            }
        }
        elseif ($value -match '^\d+$') {
            [void]$Frames.Add([int]$value)
        }
    }
}

function Get-ReviewFrameNumbers {
    param(
        [object[]]$TrackPostLines,
        [object[]]$SceneGuardLines,
        [object[]]$FinalMaskSummaryLines
    )

    $frames = New-Object System.Collections.Generic.SortedSet[int]
    if ($TrackPostLines.Count -gt 0) {
        $trackLine = $TrackPostLines[0].Line
        Add-FrameListValues $frames (Read-MatchValue $trackLine 'lostFrames=(.*?), removedShort=')
    }

    if ($SceneGuardLines.Count -gt 0) {
        $sceneLine = $SceneGuardLines[0].Line
        Add-FramePairTargetValues $frames (Read-MatchValue $sceneLine 'checkedPairs=(.*?), maxDiff=')
        Add-FramePairTargetValues $frames (Read-MatchValue $sceneLine 'cutPairs=(.*?), removed=')
        Add-FrameListValues $frames (Read-MatchValue $sceneLine 'removedFrames=(.*?), threshold=')
    }

    if ($FinalMaskSummaryLines.Count -gt 0) {
        $summaryLine = $FinalMaskSummaryLines[0].Line
        Add-FrameRangeValues $frames (Read-MatchValue $summaryLine 'shortGapRanges=(.*?), largeJumpGaps=')
        Add-FrameRangeValues $frames (Read-MatchValue $summaryLine 'largeJumpRanges=(.*?), isolated=')
        Add-FrameListValues $frames (Read-MatchValue $summaryLine 'isolatedFrames=(.*?), lowConf=')
    }

    return @($frames | ForEach-Object { $_ })
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

function Invoke-NativeCapture {
    param(
        [string]$FilePath,
        [string[]]$Arguments,
        [int]$TimeoutSeconds = 0
    )

    $stdoutPath = [IO.Path]::GetTempFileName()
    $stderrPath = [IO.Path]::GetTempFileName()

    try {
        $process = Start-Process `
            -FilePath $FilePath `
            -ArgumentList $Arguments `
            -NoNewWindow `
            -PassThru `
            -RedirectStandardOutput $stdoutPath `
            -RedirectStandardError $stderrPath

        if ($TimeoutSeconds -gt 0) {
            if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
                try {
                    $process.Kill()
                }
                catch {
                    Write-Warning "Failed to kill timed out process: $($_.Exception.Message)"
                }

                $stdout = if (Test-Path $stdoutPath) { Get-Content -Path $stdoutPath } else { @() }
                $stderr = if (Test-Path $stderrPath) { Get-Content -Path $stderrPath } else { @() }
                return [pscustomobject]@{
                    ExitCode = -1
                    Output = @($stdout + $stderr + "Timed out after $TimeoutSeconds seconds.")
                }
            }
        }
        else {
            $process.WaitForExit()
        }

        $stdoutText = if (Test-Path $stdoutPath) { Get-Content -Path $stdoutPath } else { @() }
        $stderrText = if (Test-Path $stderrPath) { Get-Content -Path $stderrPath } else { @() }
        $process.Refresh()
        $exitCode = if ($null -eq $process.ExitCode) { 0 } else { $process.ExitCode }

        return [pscustomobject]@{
            ExitCode = $exitCode
            Output = @($stdoutText + $stderrText)
        }
    }
    finally {
        Remove-Item -Force -ErrorAction SilentlyContinue -Path $stdoutPath, $stderrPath
    }
}

function Invoke-FfmpegTrim {
    param(
        [string]$InputPath,
        [string]$OutputPath,
        [string]$Start,
        [int]$Seconds
    )

    if ($Seconds -le 0) {
        return
    }

    $outputDir = Split-Path -Parent $OutputPath
    if (-not [string]::IsNullOrWhiteSpace($outputDir)) {
        New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
    }

    $ffmpeg = Get-Command ffmpeg -ErrorAction SilentlyContinue
    if ($null -ne $ffmpeg) {
        $args = @(
            "-y",
            "-hide_banner",
            "-loglevel",
            "error",
            "-ss",
            $Start,
            "-t",
            $Seconds.ToString([System.Globalization.CultureInfo]::InvariantCulture),
            "-i",
            $InputPath,
            "-c",
            "copy",
            $OutputPath
        )
        $result = Invoke-NativeCapture -FilePath $ffmpeg.Source -Arguments $args
        if ($result.ExitCode -eq 0) {
            return
        }

        Write-Warning "Windows ffmpeg trim failed; trying WSL ffmpeg. $($result.Output -join ' ')"
    }

    $wsl = Get-Command wsl.exe -ErrorAction SilentlyContinue
    if ($null -eq $wsl) {
        throw "ffmpeg was not found in Windows PATH and wsl.exe was not available."
    }

    $wslArgs = @(
        "--exec",
        "ffmpeg",
        "-y",
        "-hide_banner",
        "-loglevel",
        "error",
        "-ss",
        $Start,
        "-t",
        $Seconds.ToString([System.Globalization.CultureInfo]::InvariantCulture),
        "-i",
        (Convert-ToWslPath $InputPath),
        "-c",
        "copy",
        (Convert-ToWslPath $OutputPath)
    )
    $wslResult = Invoke-NativeCapture -FilePath $wsl.Source -Arguments $wslArgs
    if ($wslResult.ExitCode -ne 0) {
        throw "ffmpeg trim failed: $($wslResult.Output -join ' ')"
    }
}

function Invoke-PowerShellCapture {
    param(
        [string[]]$Arguments,
        [int]$TimeoutSeconds
    )

    $stdoutPath = [IO.Path]::GetTempFileName()
    $stderrPath = [IO.Path]::GetTempFileName()

    try {
        $process = Start-Process `
            -FilePath "powershell.exe" `
            -ArgumentList $Arguments `
            -NoNewWindow `
            -PassThru `
            -RedirectStandardOutput $stdoutPath `
            -RedirectStandardError $stderrPath

        if ($TimeoutSeconds -gt 0) {
            if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
                try {
                    $process.Kill()
                }
                catch {
                    Write-Warning "Failed to kill timed out process: $($_.Exception.Message)"
                }

                $stdout = if (Test-Path $stdoutPath) { Get-Content -Path $stdoutPath } else { @() }
                $stderr = if (Test-Path $stderrPath) { Get-Content -Path $stderrPath } else { @() }
                return [pscustomobject]@{
                    ExitCode = -1
                    Output = @($stdout + $stderr + "Timed out after $TimeoutSeconds seconds.")
                    TimedOut = $true
                }
            }
        }
        else {
            $process.WaitForExit()
        }

        $stdoutText = if (Test-Path $stdoutPath) { Get-Content -Path $stdoutPath } else { @() }
        $stderrText = if (Test-Path $stderrPath) { Get-Content -Path $stderrPath } else { @() }

        $process.Refresh()
        $exitCode = if ($null -eq $process.ExitCode) { 0 } else { $process.ExitCode }

        return [pscustomobject]@{
            ExitCode = $exitCode
            Output = @($stdoutText + $stderrText)
            TimedOut = $false
        }
    }
    finally {
        Remove-Item -Force -ErrorAction SilentlyContinue -Path $stdoutPath, $stderrPath
    }
}

if ([string]::IsNullOrWhiteSpace($PredictionLog)) {
    $PredictionLog = Join-Path $OutputDir "yolo-quality-2s-dump.log"
}

if ([string]::IsNullOrWhiteSpace($ChecklistPath)) {
    $ChecklistPath = Join-Path $OutputDir "yolo-quality-review-checklist.md"
}

if ([string]::IsNullOrWhiteSpace($TemplateCsv)) {
    $TemplateCsv = Join-Path $OutputDir "yolo-quality-full-gt-template.csv"
}

if ([string]::IsNullOrWhiteSpace($ReviewPackageDir)) {
    $ReviewPackageDir = Join-Path $OutputDir "review-package"
}

if ([string]::IsNullOrWhiteSpace($SummaryPath)) {
    $SummaryPath = Join-Path $OutputDir "yolo-followup-quality-evidence.md"
}

if ([string]::IsNullOrWhiteSpace($MaskContinuityPath)) {
    $MaskContinuityPath = Join-Path $OutputDir "yolo-mask-continuity-report.md"
}

$resolvedOutputDir = Resolve-RepoPath $OutputDir
$resolvedPredictionLog = Resolve-RepoPath $PredictionLog
$resolvedChecklistPath = Resolve-RepoPath $ChecklistPath
$resolvedTemplateCsv = Resolve-RepoPath $TemplateCsv
$resolvedReviewPackageDir = Resolve-RepoPath $ReviewPackageDir
$resolvedSummaryPath = Resolve-RepoPath $SummaryPath
$resolvedMaskContinuityPath = Resolve-RepoPath $MaskContinuityPath
$resolvedVideoPath = Resolve-RepoPath $VideoPath

New-Item -ItemType Directory -Force -Path $resolvedOutputDir | Out-Null

if ($TrimSeconds -gt 0) {
    if ([string]::IsNullOrWhiteSpace($TrimStart)) {
        throw "-TrimStart is required when -TrimSeconds is greater than 0."
    }

    if ([string]::IsNullOrWhiteSpace($ClipPath)) {
        $safeStart = $TrimStart -replace '[^0-9A-Za-z]+', '-'
        $ClipPath = Join-Path $OutputDir "followup-${safeStart}-${TrimSeconds}s.mp4"
    }

    $resolvedClipPath = Resolve-RepoPath $ClipPath
    $resolvedTrimSource = Resolve-RepoPath $Source
    Require-File "trim source video" $resolvedTrimSource

    if ($ForceTrim.IsPresent -or -not (Test-Path $resolvedClipPath)) {
        Invoke-FfmpegTrim `
            -InputPath $resolvedTrimSource `
            -OutputPath $resolvedClipPath `
            -Start $TrimStart `
            -Seconds $TrimSeconds
    }

    $Source = $ClipPath
    $VideoPath = $ClipPath
    $resolvedVideoPath = $resolvedClipPath
}

$smokeScript = Join-Path $repo "scripts\run-srcTest-smoke.ps1"
$checklistScript = Join-Path $repo "scripts\write-yolo-quality-review-checklist.ps1"
$templateScript = Join-Path $repo "scripts\new-yolo-full-gt-template.ps1"
$packageScript = Join-Path $repo "scripts\new-yolo-full-gt-review-package.ps1"
$maskContinuityScript = Join-Path $repo "scripts\write-yolo-mask-continuity-report.ps1"

Require-File "run-srcTest-smoke.ps1" $smokeScript
Require-File "write-yolo-quality-review-checklist.ps1" $checklistScript
Require-File "new-yolo-full-gt-template.ps1" $templateScript
if (-not $SkipReviewPackage) {
    Require-File "new-yolo-full-gt-review-package.ps1" $packageScript
}

$shouldRunSmoke = $RunSmoke.IsPresent -or $ForceRunSmoke.IsPresent -or -not (Test-Path $resolvedPredictionLog)
if ($shouldRunSmoke) {
    if (-not ($RunSmoke.IsPresent -or $ForceRunSmoke.IsPresent)) {
        throw "Prediction log is missing: $resolvedPredictionLog. Re-run with -RunSmoke to generate it."
    }

    $smokeArgs = [System.Collections.Generic.List[string]]::new()
    $smokeArgs.Add("-NoProfile") | Out-Null
    $smokeArgs.Add("-ExecutionPolicy") | Out-Null
    $smokeArgs.Add("Bypass") | Out-Null
    $smokeArgs.Add("-File") | Out-Null
    $smokeArgs.Add($smokeScript) | Out-Null
    Add-ValueArg $smokeArgs "-Source" $Source
    Add-SwitchArg $smokeArgs "-SkipTrim" $true
    Add-SwitchArg $smokeArgs "-SkipBaseline" $true
    Add-SwitchArg $smokeArgs "-SkipExport" $true
    Add-SwitchArg $smokeArgs "-DumpDetections" $true
    Add-ValueArg $smokeArgs "-YoloModelType" $YoloModelType
    Add-ValueArg $smokeArgs "-YoloInputSize" $YoloInputSize
    Add-ValueArg $smokeArgs "-YoloObjectnessThreshold" $YoloObjectnessThreshold.ToString([System.Globalization.CultureInfo]::InvariantCulture)
    Add-ValueArg $smokeArgs "-YoloConfidenceThreshold" $YoloConfidenceThreshold.ToString([System.Globalization.CultureInfo]::InvariantCulture)
    Add-ValueArg $smokeArgs "-YoloNmsThreshold" $YoloNmsThreshold.ToString([System.Globalization.CultureInfo]::InvariantCulture)
    Add-ValueArg $smokeArgs "-YoloMaxLostFillFrames" $YoloMaxLostFillFrames
    Add-ValueArg $smokeArgs "-ParallelDetectorCount" $ParallelDetectorCount
    Add-SwitchArg $smokeArgs "-YoloUseLowConfidencePositionFilter" $true
    Add-SwitchArg $smokeArgs "-YoloUseSmallAreaFilter" $true
    Add-SwitchArg $smokeArgs "-YoloUseAspectRatioFilter" $true

    if (-not [string]::IsNullOrWhiteSpace($YoloModelPath)) {
        Add-ValueArg $smokeArgs "-YoloModelPath" $YoloModelPath
    }

    $smokeResult = Invoke-PowerShellCapture -Arguments $smokeArgs.ToArray() -TimeoutSeconds $SmokeTimeoutSeconds
    $smokeResult.Output | Set-Content -Encoding UTF8 -Path $resolvedPredictionLog
    if ($smokeResult.ExitCode -ne 0) {
        throw "YOLO follow-up smoke failed with exit code $($smokeResult.ExitCode). Log: $resolvedPredictionLog"
    }
}

Require-File "prediction log" $resolvedPredictionLog

$detectionRows = @(Select-String -Path $resolvedPredictionLog -Pattern '^\[SmokeDetection\]' -ErrorAction SilentlyContinue)
$sceneGuard = @(Select-String -Path $resolvedPredictionLog -Pattern '^\[SmokeFaceTrackSceneCutGuard\]' -ErrorAction SilentlyContinue | Select-Object -Last 1)
$trackPost = @(Select-String -Path $resolvedPredictionLog -Pattern '^\[SmokeFaceTrackPost\]' -ErrorAction SilentlyContinue | Select-Object -Last 1)
$autoSummary = @(Select-String -Path $resolvedPredictionLog -Pattern '^\[AutoRunSummary\]' -ErrorAction SilentlyContinue | Select-Object -Last 1)
$finalMaskCleanup = @(Select-String -Path $resolvedPredictionLog -Pattern '^\[(SmokeYoloFinalMaskCleanup|YoloFinalMaskCleanup)\]' -ErrorAction SilentlyContinue | Select-Object -Last 1)
$finalMaskSummary = @(Select-String -Path $resolvedPredictionLog -Pattern '^\[(SmokeFinalMaskSummary|FinalMaskSummary)\]' -ErrorAction SilentlyContinue | Select-Object -Last 1)
$reviewFrameNumbers = Get-ReviewFrameNumbers -TrackPostLines $trackPost -SceneGuardLines $sceneGuard -FinalMaskSummaryLines $finalMaskSummary

if ($detectionRows.Count -eq 0) {
    if (-not $AllowNoDetections.IsPresent) {
        throw "Prediction log contains no [SmokeDetection] rows. Re-run with -AllowNoDetections to record this as no-detection evidence."
    }

    $noDetectionChecklist = @(
        "# YOLO Quality Review Checklist",
        "",
        "- Source log: ``$PredictionLog``",
        "- Detection rows parsed: 0",
        "",
        "## Run Evidence"
    )

    if ($autoSummary.Count -gt 0) {
        $noDetectionChecklist += "- ``$($autoSummary[0].Line)``"
    }
    if ($trackPost.Count -gt 0) {
        $noDetectionChecklist += "- ``$($trackPost[0].Line)``"
    }
    if ($sceneGuard.Count -gt 0) {
        $noDetectionChecklist += "- ``$($sceneGuard[0].Line)``"
    }
    if ($finalMaskCleanup.Count -gt 0) {
        $noDetectionChecklist += "- ``$($finalMaskCleanup[0].Line)``"
    }
    if ($finalMaskSummary.Count -gt 0) {
        $noDetectionChecklist += "- ``$($finalMaskSummary[0].Line)``"
    }

    $noDetectionChecklist += @(
        "",
        "## Review Points",
        "| Issue | Evidence | Decision note |",
        "| --- | --- | --- |",
        "| False positives | no `[SmokeDetection]` rows | Review the source clip if it contains visible faces. If no visible face is present, this is no-false-positive evidence for the sampled span. |",
        "",
        "## Manual Label Rule",
        '- `face`: visible real face covered by the candidate box.',
        '- `nonface`: background/object/body/text/partial artifact that should not be mosaicked.',
        '- `miss`: visible face not covered by any candidate row; add a manual GT row before quality-gate claims.'
    )
    Set-Content -Encoding UTF8 -Path $resolvedChecklistPath -Value ($noDetectionChecklist -join [Environment]::NewLine)
}
else {

    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $checklistScript `
        -LogPath $resolvedPredictionLog `
        -OutputPath $resolvedChecklistPath
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to write YOLO quality checklist."
    }

    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $maskContinuityScript `
        -LogPath $resolvedPredictionLog `
        -OutputPath $resolvedMaskContinuityPath
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to write YOLO mask continuity report."
    }

    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $templateScript `
        -PredictionLog $resolvedPredictionLog `
        -OutputCsv $resolvedTemplateCsv
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to write YOLO full-GT template."
    }

    $reviewIndex = Join-Path $resolvedReviewPackageDir "review-index.html"
    $reviewCsv = Join-Path $resolvedReviewPackageDir "full-gt-review.csv"
    $fullFrameReviewCsv = Join-Path $resolvedReviewPackageDir "full-frame-review.csv"

    if (-not $SkipReviewPackage) {
        Require-File "review video" $resolvedVideoPath

        $reviewIndex = Join-Path $resolvedReviewPackageDir "review-index.html"
        if ($ForceReviewPackage.IsPresent -or -not (Test-Path $reviewIndex)) {
            $reviewVideoFrameCount = $VideoFrameCount
            if ($reviewFrameNumbers.Count -gt 0) {
                $maxRequiredFrame = @($reviewFrameNumbers | Sort-Object -Descending | Select-Object -First 1)[0]
                $reviewVideoFrameCount = [Math]::Max($reviewVideoFrameCount, $maxRequiredFrame + 1)
            }

            $packageArgs = @(
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                $packageScript,
                "-VideoPath",
                $resolvedVideoPath,
                "-TemplateCsv",
                $resolvedTemplateCsv,
                "-OutputDir",
                $resolvedReviewPackageDir,
                "-IncludeFullFrameReview",
                "-VideoFrameCount",
                $reviewVideoFrameCount,
                "-MaxFullFrameRows",
                $MaxFullFrameRows,
                "-FullFrameScaleWidth",
                $FullFrameScaleWidth
            )

            if ($reviewFrameNumbers.Count -gt 0) {
                $packageArgs += "-RequiredFullFrameNumbers"
                $packageArgs += ($reviewFrameNumbers -join ",")
            }

            if ($ForceReviewPackage.IsPresent) {
                $packageArgs += "-Force"
            }

            & powershell.exe @packageArgs
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to write YOLO full-GT review package."
            }
        }
    }
}

$summary = New-Object System.Text.StringBuilder
[void]$summary.AppendLine("# YOLO Follow-Up Quality Evidence")
[void]$summary.AppendLine()
[void]$summary.AppendLine("- Prediction log: ``$PredictionLog``")
[void]$summary.AppendLine("- Checklist: ``$ChecklistPath``")
if ($detectionRows.Count -gt 0) {
    [void]$summary.AppendLine("- Final mask continuity: ``$MaskContinuityPath``")
    [void]$summary.AppendLine("- Full-GT template: ``$TemplateCsv``")
}
if (-not $SkipReviewPackage -and $detectionRows.Count -gt 0) {
    [void]$summary.AppendLine("- Review index: ``$ReviewPackageDir/review-index.html``")
    [void]$summary.AppendLine("- Crop review CSV: ``$ReviewPackageDir/full-gt-review.csv``")
    [void]$summary.AppendLine("- Full-frame review CSV: ``$ReviewPackageDir/full-frame-review.csv``")
    if ($reviewFrameNumbers.Count -gt 0) {
        [void]$summary.AppendLine("- Required full-frame review frames: ``$($reviewFrameNumbers -join ",")``")
    }
}
[void]$summary.AppendLine("- Detection rows: $($detectionRows.Count)")
if ($detectionRows.Count -eq 0) {
    [void]$summary.AppendLine("- No detection rows were found; crop/full-frame package generation was skipped.")
}
if ($autoSummary.Count -gt 0) {
    [void]$summary.AppendLine("- Auto summary: ``$($autoSummary[0].Line)``")
}
if ($trackPost.Count -gt 0) {
    [void]$summary.AppendLine("- Track postprocess: ``$($trackPost[0].Line)``")
}
if ($sceneGuard.Count -gt 0) {
    [void]$summary.AppendLine("- Scene-cut guard: ``$($sceneGuard[0].Line)``")
}
if ($finalMaskCleanup.Count -gt 0) {
    [void]$summary.AppendLine("- Final mask cleanup: ``$($finalMaskCleanup[0].Line)``")
}
if ($finalMaskSummary.Count -gt 0) {
    [void]$summary.AppendLine("- Final mask summary: ``$($finalMaskSummary[0].Line)``")
}
[void]$summary.AppendLine()
[void]$summary.AppendLine("## Manual Review Rule")
[void]$summary.AppendLine('- Use `face` only for a visible real face covered by the candidate box.')
[void]$summary.AppendLine('- Use `nonface` for background/object/body/text/partial artifacts that should not be mosaicked.')
[void]$summary.AppendLine('- Use `miss` by adding manual rows for visible faces not covered by any candidate row.')
[void]$summary.AppendLine("- This package is review evidence only. It does not make YOLO or FaceONNX ground truth.")
[void]$summary.AppendLine()
[void]$summary.AppendLine("## Completion Note")
[void]$summary.AppendLine("- Follow-up completion still requires visual confirmation on the user-reported problem span.")

Set-Content -Encoding UTF8 -Path $resolvedSummaryPath -Value $summary.ToString()

Write-Host "[YoloFollowupQualityEvidence] log=$PredictionLog"
Write-Host "[YoloFollowupQualityEvidence] checklist=$ChecklistPath"
if ($detectionRows.Count -gt 0) {
    Write-Host "[YoloFollowupQualityEvidence] maskContinuity=$MaskContinuityPath"
    Write-Host "[YoloFollowupQualityEvidence] template=$TemplateCsv"
}
if (-not $SkipReviewPackage -and $detectionRows.Count -gt 0) {
    Write-Host "[YoloFollowupQualityEvidence] reviewIndex=$ReviewPackageDir/review-index.html"
}
Write-Host "[YoloFollowupQualityEvidence] summary=$SummaryPath"
Write-Host "[YoloFollowupQualityEvidence] detections=$($detectionRows.Count)"
