<#
.SYNOPSIS
    YOLO 후처리 토글 조합별 A/B 비교를 한 번에 실행합니다.

.DESCRIPTION
    동일한 조건에서 baseline(후처리 OFF)과 프리셋별 토글 조합을 별도 실행해
    .log를 수집하고 `compare-yolo-postprocess-runs.ps1`로 요약 비교를 출력합니다.
    기본 동작은 오탐/미탐 고도화 실험용으로 프레임 구간 필터까지 지원합니다.

.PARAMETER Source
    입력 비디오 경로.

.PARAMETER Start
    실행 시작 지점(예: 00:01:00).

.PARAMETER Seconds
    실행 길이(초).

.PARAMETER StartFrame
    비교 통계 계산용 프레임 시작(0 기반).

.PARAMETER EndFrameExclusive
    비교 통계 계산용 프레임 종료(미포함).

.PARAMETER Presets
    비교할 프리셋 이름 목록.

.PARAMETER LogRoot
    실행별 로그 저장 디렉터리.

.PARAMETER SkipExport
    실행 시 인코딩을 건너뛰어 로그/후처리 결과만 수집.

.PARAMETER SummaryFile
    오프셋 기반 비교 결과 집계 JSON 파일 경로.
    지정하지 않으면 `<LogRoot>\compare-summary.json`으로 저장됩니다.

.NOTES
    내부 비교는 `scripts/run-srcTest-smoke.ps1`를 호출합니다.
#>
param(
    [string] $Source = "srcTest/260102_jp_10.mp4",
    [string] $Start = "00:01:00",
    [int] $Seconds = 30,
    [int] $StartFrame = -1,
    [int] $EndFrameExclusive = -1,
    [switch] $RequireFrameFilter,
    [string[]] $Presets = @("off", "weak", "gap", "scene", "smooth", "roi", "full"),
    [string] $LogRoot = ".tmp/yolo-postprocess-presets",
    [switch] $SkipExport,
    [string] $SummaryFile = "",
    [string] $YoloModelPath = "",
    [ValidateSet("YoloV8Face", "Yolo5Face")]
    [string] $YoloModelType = "YoloV8Face",
    [int] $YoloInputSize = 640,
    [double] $YoloObjectnessThreshold = 0.25,
    [double] $YoloConfidenceThreshold = 0.35,
    [double] $YoloNmsThreshold = 0.45,
    [switch] $UseAutoTune,
    [switch] $OptimizedNoTracking,
    [int] $OptimizedDetectEvery = 1,
    [int] $ParallelDetectorCount = 2,
    [double] $DetectionThreshold = 0.2,
    [double] $ConfidenceThreshold = 0.25,
    [double] $NmsThreshold = 0.7,
    [double] $YoloLowConfidencePositionMaxConfidence = 0.50,
    [double] $YoloLowConfidencePositionMinCenterYRatio = 0.08,
    [double] $YoloSmallAreaMaxAreaRatio = 0.0035,
    [double] $YoloMinAspectRatio = 0.35,
    [double] $YoloMaxAspectRatio = 1.65,
    [int] $YoloMaxLostFillFrames = 0,
    [int] $YoloMaxInitialFillFrames = 3,
    [int] $YoloDropShortTrackMaxDetections = 1,
    [double] $YoloShortTrackMaxConfidence = 0.18,
    [double] $YoloLowerFrameTrackMaxConfidence = 0.50,
    [int] $AllowedWeakFaceIncrease = 0,
    [int] $MinDetectGain = 0,
    [int] $MaxExportMsDelta = 0,
    [int] $AllowedShortGapIncrease = 0,
    [int] $AllowedLargeJumpIncrease = 0,
    [double] $FalsePositiveScorePenaltyPerFrame = 6.0,
    [double] $MissedDetectScorePenaltyPerFace = 1.5,
    [double] $MissDetectGainScoreRewardPerFace = 0.6,
    [double] $SceneCutCarryPenaltyPerFrame = 4.0,
    [double] $ExportMsPenaltyPerMs = 0.02,
    [double] $ShortGapPenaltyPerFrame = 7.5,
    [double] $LargeJumpPenaltyPerFrame = 7.5,
    [double] $ReviewRequiredPenalty = 999
)

$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot ".." )).Path
$runSmoke = Join-Path $repo 'scripts/run-srcTest-smoke.ps1'
$compareScript = Join-Path $repo 'scripts/compare-yolo-postprocess-runs.ps1'

function Invoke-Run {
    param(
        [string]$Name,
        [hashtable]$Switches,
        [string]$LogPath,
        [bool]$SkipBase = $true,
        [bool]$SkipOptimized = $false
    )

    Write-Host "[PostprocessPresetRun] preset=$Name run"

    $args = @(
        '-Source', $Source,
        '-Start', $Start,
        '-Seconds', "$Seconds",
        '-OptimizedDetectEvery', "$OptimizedDetectEvery",
        '-ParallelDetectorCount', "$ParallelDetectorCount",
        '-YoloInputSize', "$YoloInputSize",
        '-YoloObjectnessThreshold', $YoloObjectnessThreshold.ToString([System.Globalization.CultureInfo]::InvariantCulture),
        '-YoloConfidenceThreshold', $YoloConfidenceThreshold.ToString([System.Globalization.CultureInfo]::InvariantCulture),
        '-YoloNmsThreshold', $YoloNmsThreshold.ToString([System.Globalization.CultureInfo]::InvariantCulture),
        '-YoloLargeBoxMinAreaRatio', '0',
        '-YoloLowConfidencePositionMaxConfidence', $YoloLowConfidencePositionMaxConfidence.ToString([System.Globalization.CultureInfo]::InvariantCulture),
        '-YoloLowConfidencePositionMinCenterYRatio', $YoloLowConfidencePositionMinCenterYRatio.ToString([System.Globalization.CultureInfo]::InvariantCulture),
        '-YoloSmallAreaMaxAreaRatio', $YoloSmallAreaMaxAreaRatio.ToString([System.Globalization.CultureInfo]::InvariantCulture),
        '-YoloMinAspectRatio', $YoloMinAspectRatio.ToString([System.Globalization.CultureInfo]::InvariantCulture),
        '-YoloMaxAspectRatio', $YoloMaxAspectRatio.ToString([System.Globalization.CultureInfo]::InvariantCulture),
        '-YoloMaxLostFillFrames', "$YoloMaxLostFillFrames",
        '-YoloMaxInitialFillFrames', "$YoloMaxInitialFillFrames",
        '-YoloDropShortTrackMaxDetections', "$YoloDropShortTrackMaxDetections",
        '-YoloShortTrackMaxConfidence', $YoloShortTrackMaxConfidence.ToString([System.Globalization.CultureInfo]::InvariantCulture),
        '-YoloLowerFrameTrackMaxConfidence', $YoloLowerFrameTrackMaxConfidence.ToString([System.Globalization.CultureInfo]::InvariantCulture),
        '-DetectionThreshold', $DetectionThreshold.ToString([System.Globalization.CultureInfo]::InvariantCulture),
        '-ConfidenceThreshold', $ConfidenceThreshold.ToString([System.Globalization.CultureInfo]::InvariantCulture),
        '-NmsThreshold', $NmsThreshold.ToString([System.Globalization.CultureInfo]::InvariantCulture),
        '-LogFile', $LogPath
    )

    $args += '-YoloModelType', $YoloModelType
    if (-not [string]::IsNullOrWhiteSpace($YoloModelPath)) {
        $args += '-YoloModelPath', $YoloModelPath
    }

    if ($SkipBase) { $args += '-SkipBaseline' }
    if ($SkipOptimized) { $args += '-SkipOptimized' }
    if ($UseAutoTune.IsPresent) { $args += '-UseAutoTune' }
    if ($OptimizedNoTracking.IsPresent) { $args += '-OptimizedNoTracking' }
    if ($SkipExport.IsPresent) { $args += '-SkipExport' }

    foreach ($key in $Switches.Keys) {
        if ($Switches[$key]) {
            $args += "-$key"
        }
    }

    $logDir = Split-Path -Path $LogPath -Parent
    if (-not [string]::IsNullOrWhiteSpace($logDir) -and -not (Test-Path $logDir)) {
        New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    }

    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runSmoke @args
    if ($LASTEXITCODE -ne 0) {
        throw "run-srcTest-smoke failed for preset=$Name (exit=$LASTEXITCODE)"
    }
}

function Invoke-Compare {
    param(
        [string]$BaselineLog,
        [string]$TargetLog,
        [string]$PresetName,
        [string]$ResultPath
    )

    Write-Host "[PostprocessPresetCompare] preset=$PresetName compare"

    $compareArgs = @(
        '-RunALog', $BaselineLog,
        '-RunBLog', $TargetLog,
        '-RunAId', 'auto-*',
        '-RunBId', 'auto-*',
        '-JsonOutput'
    )

    if ($RequireFrameFilter.IsPresent) {
        $compareArgs += '-RequireFrameFilter'
    }
    if ($StartFrame -ge 0) { $compareArgs += @('-StartFrame', "$StartFrame") }
    if ($EndFrameExclusive -ge 0) { $compareArgs += @('-EndFrameExclusive', "$EndFrameExclusive") }

    $compareOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $compareScript @compareArgs | Out-String
    if ($LASTEXITCODE -ne 0) {
        throw "compare-yolo-postprocess-runs failed for preset=$PresetName (exit=$LASTEXITCODE)"
    }

    $compareJson = $compareOutput | ConvertFrom-Json
    Set-Content -Path $ResultPath -Value $compareOutput -Encoding UTF8
    return $compareJson
}

function Get-WeakFaceScore {
    param(
        [Parameter(Mandatory = $true)]
        [object] $FinalSummary
    )

    $keys = @(
        'isolated',
        'lowConf',
        'weakNonEdge',
        'edgeWeak',
        'topEdgeWeak',
        'topEdgeLarge',
        'upperWeak',
        'lowerWeak',
        'aspectBad',
        'tinyWeak',
        'tinyShort'
    )

    $score = 0
    foreach ($key in $keys) {
        $value = Read-JsonValue -Container $FinalSummary -Key $key
        if ($null -ne $value) {
            $score += [int]$value
        }
    }

    return $score
}

function Read-NumericValue {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Container,
        [Parameter(Mandatory = $true)]
        [string] $Key,
        [double] $Default = 0
    )

    $value = Read-JsonValue -Container $Container -Key $Key
    if ($null -eq $value) {
        return $Default
    }

    if ($value -is [int] -or $value -is [long] -or $value -is [float] -or $value -is [double]) {
        return [double]$value
    }

    if ($value -is [bool]) {
        return [double]($value ? 1 : 0)
    }

    try {
        return [double]$value
    }
    catch {
        return $Default
    }
}

function Read-BoolValue {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Container,
        [Parameter(Mandatory = $true)]
        [string] $Key,
        [bool] $Default = $false
    )

    $value = Read-JsonValue -Container $Container -Key $Key
    if ($null -eq $value) {
        return $Default
    }

    if ($value -is [bool]) {
        return $value
    }

    $text = "$value"
    if ([string]::IsNullOrWhiteSpace($text)) {
        return $Default
    }

    if ($text -match '^(true|1)$') {
        return $true
    }

    if ($text -match '^(false|0)$') {
        return $false
    }

    return $Default
}

function Build-ScenarioDecision {
    param(
        [double] $BaselineWeakFace,
        [double] $TargetWeakFace,
        [double] $BaselineDetect,
        [double] $TargetDetect,
        [double] $BaselineSceneCutRemoved,
        [double] $TargetSceneCutRemoved,
        [double] $BaselineExportMs,
        [double] $TargetExportMs,
        [double] $BaselineShortGaps,
        [double] $TargetShortGaps,
        [double] $BaselineLargeJumpGaps,
        [double] $TargetLargeJumpGaps,
        [bool] $BaselineReviewRequired,
        [bool] $TargetReviewRequired,
        [double] $FalsePositivePenaltyPerFrame,
        [double] $MissedDetectPenaltyPerFace,
        [double] $MissDetectGainRewardPerFace,
        [double] $SceneCutPenaltyPerFrame,
        [double] $ExportPenaltyPerMs,
        [double] $ShortGapPenaltyPerFrame,
        [double] $LargeJumpPenaltyPerFrame,
        [double] $ReviewPenalty
    )

    $weakFaceDelta = $TargetWeakFace - $BaselineWeakFace
    $detectDelta = $TargetDetect - $BaselineDetect
    $sceneCutDelta = $TargetSceneCutRemoved - $BaselineSceneCutRemoved
    $exportDelta = $TargetExportMs - $BaselineExportMs
    $shortGapDelta = $TargetShortGaps - $BaselineShortGaps
    $largeJumpDelta = $TargetLargeJumpGaps - $BaselineLargeJumpGaps

    $passesFalsePositive = $weakFaceDelta -le $AllowedWeakFaceIncrease
    $passesMissFill = $detectDelta -ge $MinDetectGain
    $passesTransition = -not $TargetReviewRequired
    $passesSceneCutCarry = $sceneCutDelta -le 0
    $passesExport = $exportDelta -le $MaxExportMsDelta
    $passesShortGap = $shortGapDelta -le $AllowedShortGapIncrease
    $passesLargeJump = $largeJumpDelta -le $AllowedLargeJumpIncrease

    $passed = $passesFalsePositive -and $passesMissFill -and $passesTransition -and $passesSceneCutCarry -and $passesExport -and $passesShortGap -and $passesLargeJump
    $weakPenalty = [Math]::Max(0, $weakFaceDelta) * $FalsePositivePenaltyPerFrame
    $detectLossPenalty = [Math]::Max(0, -$detectDelta) * $MissedDetectPenaltyPerFace
    $detectGainReward = [Math]::Max(0, $detectDelta) * $MissDetectGainRewardPerFace
    $scenePenalty = [Math]::Max(0, $sceneCutDelta) * $SceneCutPenaltyPerFrame
    $exportPenalty = [Math]::Max(0, $exportDelta) * $ExportPenaltyPerMs
    $shortGapPenalty = [Math]::Max(0, $shortGapDelta) * $ShortGapPenaltyPerFrame
    $largeJumpPenalty = [Math]::Max(0, $largeJumpDelta) * $LargeJumpPenaltyPerFrame
    $reviewPenalty = if ($TargetReviewRequired) { $ReviewPenalty } else { 0 }
    $score = [Math]::Round($weakPenalty + $detectLossPenalty + $scenePenalty + $exportPenalty + $shortGapPenalty + $largeJumpPenalty + $reviewPenalty - $detectGainReward, 4)
    $normalized = [Math]::Round($score, 4)
    $reason = @(
        if (-not $passesFalsePositive) { "weakFaceDelta=+$weakFaceDelta" } else { $null }
        if (-not $passesMissFill) { "detectDelta=$detectDelta" } else { $null }
        if (-not $passesTransition) { "reviewRequired=$TargetReviewRequired" } else { $null }
        if (-not $passesSceneCutCarry) { "sceneCutRemovedDelta=+$sceneCutDelta" } else { $null }
        if (-not $passesExport) { "exportMsDelta=+$exportDelta" } else { $null }
        if (-not $passesShortGap) { "shortGapsDelta=+$shortGapDelta" } else { $null }
        if (-not $passesLargeJump) { "largeJumpGapsDelta=+$largeJumpDelta" } else { $null }
    ) | Where-Object { $null -ne $_ }

    return [pscustomobject]@{
        WeakFaceDelta = $weakFaceDelta
        DetectDelta = $detectDelta
        SceneCutRemovedDelta = $sceneCutDelta
        ExportMsDelta = $exportDelta
        ShortGapsDelta = $shortGapDelta
        LargeJumpGapsDelta = $largeJumpDelta
        BaselineReviewRequired = $BaselineReviewRequired
        TargetReviewRequired = $TargetReviewRequired
        PassFalsePositive = $passesFalsePositive
        PassMissFill = $passesMissFill
        PassTransition = $passesTransition
        PassSceneCarry = $passesSceneCutCarry
        PassExport = $passesExport
        PassShortGaps = $passesShortGap
        PassLargeJumpGaps = $passesLargeJump
        WeakPenalty = $weakPenalty
        DetectLossPenalty = $detectLossPenalty
        DetectGainReward = $detectGainReward
        SceneCutPenalty = $scenePenalty
        ExportPenalty = $exportPenalty
        ShortGapPenalty = $shortGapPenalty
        LargeJumpPenalty = $largeJumpPenalty
        ReviewPenalty = $reviewPenalty
        CompositeScore = $score
        NormalizedScore = $normalized
        Passed = $passed
        Reasons = if ($reason.Count -eq 0) { "pass" } else { $reason -join ";" }
    }
}

function Read-JsonValue {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Container,
        [Parameter(Mandatory = $true)]
        [string] $Key
    )

    if ($null -eq $Container) {
        return $null
    }

    if ($Container -is [hashtable] -and $Container.ContainsKey($Key)) {
        return $Container[$Key]
    }

    $property = $Container.PSObject.Properties[$Key]
    if ($null -ne $property) {
        return $property.Value
    }

    return $null
}

$presetsByName = @{
    off = @{
        YoloEnablePostProcessing = $false
        YoloEnableRoiPostProcess = $false
        YoloEnableWeakIsolatedCleanup = $false
        YoloEnableGapFill = $false
        YoloEnableSceneCutCarryCleanup = $false
        YoloEnableTemporalSmoothing = $false
    }
    weak = @{
        YoloEnablePostProcessing = $true
        YoloEnableWeakIsolatedCleanup = $true
        YoloEnableRoiPostProcess = $false
        YoloEnableGapFill = $false
        YoloEnableSceneCutCarryCleanup = $false
        YoloEnableTemporalSmoothing = $false
    }
    gap = @{
        YoloEnablePostProcessing = $true
        YoloEnableGapFill = $true
        YoloEnableRoiPostProcess = $false
        YoloEnableWeakIsolatedCleanup = $false
        YoloEnableSceneCutCarryCleanup = $false
        YoloEnableTemporalSmoothing = $false
    }
    scene = @{
        YoloEnablePostProcessing = $true
        YoloEnableSceneCutCarryCleanup = $true
        YoloEnableRoiPostProcess = $false
        YoloEnableWeakIsolatedCleanup = $false
        YoloEnableGapFill = $false
        YoloEnableTemporalSmoothing = $false
    }
    smooth = @{
        YoloEnablePostProcessing = $true
        YoloEnableTemporalSmoothing = $true
        YoloEnableRoiPostProcess = $false
        YoloEnableWeakIsolatedCleanup = $false
        YoloEnableGapFill = $false
        YoloEnableSceneCutCarryCleanup = $false
    }
    roi = @{
        YoloEnablePostProcessing = $true
        YoloEnableRoiPostProcess = $true
        YoloEnableWeakIsolatedCleanup = $false
        YoloEnableGapFill = $false
        YoloEnableSceneCutCarryCleanup = $false
        YoloEnableTemporalSmoothing = $false
    }
    full = @{
        YoloEnablePostProcessing = $true
        YoloEnableRoiPostProcess = $true
        YoloEnableWeakIsolatedCleanup = $true
        YoloEnableGapFill = $true
        YoloEnableSceneCutCarryCleanup = $true
        YoloEnableTemporalSmoothing = $true
    }
}

New-Item -ItemType Directory -Force -Path $LogRoot | Out-Null

$normalizedPresets = @($Presets | ForEach-Object { $_.ToLowerInvariant().Trim() })

$required = @('off') + $normalizedPresets
$seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
$summary = New-Object 'System.Collections.Generic.List[psobject]'
foreach ($name in $required) {
    if (-not $presetsByName.ContainsKey($name)) {
        throw "unknown preset name: $name. available: $($presetsByName.Keys -join ', ')"
    }

    if (-not $seen.Add($name)) {
        continue
    }

    $logPath = Join-Path $LogRoot ("${name}.log")
    Write-Host "[PostprocessPresetRun] write log=$logPath"
    Invoke-Run -Name $name -Switches $presetsByName[$name] -LogPath $logPath -SkipBase $true -SkipOptimized $false
}

$baselineLog = Join-Path $LogRoot 'off.log'
if (-not (Test-Path $baselineLog)) {
    throw "baseline log not found: $baselineLog"
}

$presetsToCompare = @($normalizedPresets | Where-Object { $_ -ne 'off' })
foreach ($name in $presetsToCompare) {
    $normalized = $name
    $targetLog = Join-Path $LogRoot ("${normalized}.log")
    if (-not (Test-Path $targetLog)) {
        throw "preset log not found: $targetLog"
    }

    $resultPath = Join-Path $LogRoot ("compare-off-vs-${normalized}.json")
    $compare = Invoke-Compare -BaselineLog $baselineLog -TargetLog $targetLog -PresetName $name -ResultPath $resultPath
    $baselineRun = Read-JsonValue -Container $compare -Key 'RunA'
    $targetRun = Read-JsonValue -Container $compare -Key 'RunB'
    $baselineSummary = Read-JsonValue -Container $baselineRun -Key 'RunSummary'
    $targetSummary = Read-JsonValue -Container $targetRun -Key 'RunSummary'
    $baselineExport = Read-JsonValue -Container $baselineRun -Key 'Export'
    $targetExport = Read-JsonValue -Container $targetRun -Key 'Export'
    $baselineSceneReset = Read-JsonValue -Container $baselineRun -Key 'SceneCutReset'
    $targetSceneReset = Read-JsonValue -Container $targetRun -Key 'SceneCutReset'
    $baselineFinal = Read-JsonValue -Container $baselineRun -Key 'FinalSummary'
    $targetFinal = Read-JsonValue -Container $targetRun -Key 'FinalSummary'

    $baselineWeakFace = Get-WeakFaceScore -FinalSummary $baselineFinal
    $targetWeakFace = Get-WeakFaceScore -FinalSummary $targetFinal
    $baselineDetects = Read-NumericValue -Container $baselineSummary -Key 'detects'
    $targetDetects = Read-NumericValue -Container $targetSummary -Key 'detects'
    $baselineSceneCutRemoved = Read-NumericValue -Container $baselineSceneReset -Key 'removed'
    $targetSceneCutRemoved = Read-NumericValue -Container $targetSceneReset -Key 'removed'
    $baselineExportMs = Read-NumericValue -Container $baselineExport -Key 'totalMs'
    $targetExportMs = Read-NumericValue -Container $targetExport -Key 'totalMs'
    $baselineShortGaps = Read-NumericValue -Container $baselineFinal -Key 'shortGaps'
    $targetShortGaps = Read-NumericValue -Container $targetFinal -Key 'shortGaps'
    $baselineLargeJumpGaps = Read-NumericValue -Container $baselineFinal -Key 'largeJumpGaps'
    $targetLargeJumpGaps = Read-NumericValue -Container $targetFinal -Key 'largeJumpGaps'
    $baselineReview = Read-BoolValue -Container $baselineFinal -Key 'reviewRequired'
    $targetReview = Read-BoolValue -Container $targetFinal -Key 'reviewRequired'
    $decision = Build-ScenarioDecision `
        -BaselineWeakFace $baselineWeakFace `
        -TargetWeakFace $targetWeakFace `
        -BaselineDetect $baselineDetects `
        -TargetDetect $targetDetects `
        -BaselineSceneCutRemoved $baselineSceneCutRemoved `
        -TargetSceneCutRemoved $targetSceneCutRemoved `
        -BaselineExportMs $baselineExportMs `
        -TargetExportMs $targetExportMs `
        -BaselineShortGaps $baselineShortGaps `
        -TargetShortGaps $targetShortGaps `
        -BaselineLargeJumpGaps $baselineLargeJumpGaps `
        -TargetLargeJumpGaps $targetLargeJumpGaps `
        -BaselineReviewRequired $baselineReview `
        -TargetReviewRequired $targetReview `
        -FalsePositivePenaltyPerFrame $FalsePositiveScorePenaltyPerFrame `
        -MissedDetectPenaltyPerFace $MissedDetectScorePenaltyPerFace `
        -MissDetectGainRewardPerFace $MissDetectGainScoreRewardPerFace `
        -SceneCutPenaltyPerFrame $SceneCutCarryPenaltyPerFrame `
        -ExportPenaltyPerMs $ExportMsPenaltyPerMs `
        -ShortGapPenaltyPerFrame $ShortGapPenaltyPerFrame `
        -LargeJumpPenaltyPerFrame $LargeJumpPenaltyPerFrame `
        -ReviewPenalty $ReviewRequiredPenalty

    $summary.Add([pscustomobject]@{
        Preset = $normalized
        BaselineRunId = Read-JsonValue -Container $compare.RunA -Key 'RunId'
        TargetRunId = Read-JsonValue -Container $compare.RunB -Key 'RunId'
        WeakFaceCountBaseline = $baselineWeakFace
        WeakFaceCountTarget = $targetWeakFace
        WeakFaceCountDelta = $decision.WeakFaceDelta
        DetectDelta = $decision.DetectDelta
        ShortGapDelta = $decision.ShortGapsDelta
        LargeJumpGapsDelta = $decision.LargeJumpGapsDelta
        ShortGapsCountBaseline = $baselineShortGaps
        ShortGapsCountTarget = $targetShortGaps
        LargeJumpGapsCountBaseline = $baselineLargeJumpGaps
        LargeJumpGapsCountTarget = $targetLargeJumpGaps
        RunMsDelta = if ($null -ne $baselineSummary -and $null -ne $targetSummary -and $baselineSummary.ContainsKey('totalMs') -and $targetSummary.ContainsKey('totalMs')) { $targetSummary.totalMs - $baselineSummary.totalMs } else { $null }
        ExportMsDelta = $decision.ExportMsDelta
        ReviewRequired = $targetReview
        SceneCutRemovedDelta = $decision.SceneCutRemovedDelta
        CompositeScore = $decision.CompositeScore
        WeakPenalty = $decision.WeakPenalty
        DetectLossPenalty = $decision.DetectLossPenalty
        DetectGainReward = $decision.DetectGainReward
        SceneCutPenalty = $decision.SceneCutPenalty
        ExportPenalty = $decision.ExportPenalty
        ShortGapPenalty = $decision.ShortGapPenalty
        LargeJumpPenalty = $decision.LargeJumpPenalty
        ReviewPenalty = $decision.ReviewPenalty
        PassFalsePositive = $decision.PassFalsePositive
        PassMissFill = $decision.PassMissFill
        PassTransition = $decision.PassTransition
        PassSceneCarry = $decision.PassSceneCarry
        PassExport = $decision.PassExport
        PassShortGaps = $decision.PassShortGaps
        PassLargeJumpGaps = $decision.PassLargeJumpGaps
        Passed = $decision.Passed
        DecisionReasons = $decision.Reasons
        ResultPath = $resultPath
    })
    Write-Host "[PostprocessPresetCompare] result=$resultPath"
}

if ($summary.Count -gt 0) {
    $summaryPath = if ([string]::IsNullOrWhiteSpace($SummaryFile)) { Join-Path $LogRoot "compare-summary.json" } else { $SummaryFile }
    $summarySorted = $summary | Sort-Object @{ Expression = { [double]$_.CompositeScore }; Descending = $false }, @{ Expression = { [double]$_.WeakFaceCountDelta }; Descending = $false }, @{ Expression = { [double]$_.ShortGapDelta }; Descending = $false }, @{ Expression = { [double]$_.LargeJumpGapsDelta }; Descending = $false }, @{ Expression = { [double]$_.ExportMsDelta }; Descending = $false }, @{ Expression = { [double]$_.DetectDelta }; Descending = $true }
    $summarySorted | ConvertTo-Json -Depth 8 | Set-Content -Path $summaryPath -Encoding UTF8
    Write-Host "[PostprocessPresetCompare] summary=$summaryPath"
    Write-Host "[PostprocessPresetCompare] score top3="
    foreach ($entry in ($summarySorted | Select-Object -First 3)) {
        Write-Host "[PostprocessPresetCompare] rank preset=$($entry.Preset) score=$($entry.CompositeScore) weakFaceDelta=$($entry.WeakFaceCountDelta) shortGapDelta=$($entry.ShortGapDelta) largeJumpDelta=$($entry.LargeJumpGapsDelta) detectDelta=$($entry.DetectDelta) sceneCutDelta=$($entry.SceneCutRemovedDelta) exportMsDelta=$($entry.ExportMsDelta) review=$($entry.ReviewRequired)"
    }

    $passed = @($summarySorted | Where-Object { $_.Passed })
    if ($passed.Count -gt 0) {
        Write-Host "[PostprocessPresetCompare] passed preset count=$($passed.Count)"
        foreach ($entry in $passed) {
            Write-Host "[PostprocessPresetCompare] pass preset=$($entry.Preset) score=$($entry.CompositeScore) weakFaceDelta=$($entry.WeakFaceCountDelta) shortGapDelta=$($entry.ShortGapDelta) largeJumpDelta=$($entry.LargeJumpGapsDelta) detectDelta=$($entry.DetectDelta) sceneCutDelta=$($entry.SceneCutRemovedDelta) exportMsDelta=$($entry.ExportMsDelta) review=$($entry.ReviewRequired) reasons=$($entry.DecisionReasons)"
        }
    }
    else {
        Write-Host "[PostprocessPresetCompare] pass preset none (criteria: weakFace<=+$AllowedWeakFaceIncrease, shortGaps<=+$AllowedShortGapIncrease, largeJumpGaps<=+$AllowedLargeJumpIncrease, detect>=$MinDetectGain, no review-required, sceneCutRemovedDelta<=0, exportMsDelta<=$MaxExportMsDelta)"
    }
}

Write-Host "[PostprocessPresetCompare] done"
Write-Host "[PostprocessPresetCompare] logs=$LogRoot"
