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
    [double] $YoloLowerFrameTrackMaxConfidence = 0.50
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
    $summary.Add([pscustomobject]@{
        Preset = $normalized
        BaselineRunId = Read-JsonValue -Container $compare.RunA -Key 'RunId'
        TargetRunId = Read-JsonValue -Container $compare.RunB -Key 'RunId'
        WeakFaceCountBaseline = Get-WeakFaceScore -FinalSummary (Read-JsonValue -Container $compare.RunA -Key 'FinalSummary')
        WeakFaceCountTarget = Get-WeakFaceScore -FinalSummary (Read-JsonValue -Container $compare.RunB -Key 'FinalSummary')
        WeakFaceCountDelta = (Get-WeakFaceScore -FinalSummary (Read-JsonValue -Container $compare.RunB -Key 'FinalSummary')) - (Get-WeakFaceScore -FinalSummary (Read-JsonValue -Container $compare.RunA -Key 'FinalSummary'))
        DetectDelta = if ($null -ne (Read-JsonValue -Container (Read-JsonValue -Container $compare.RunA -Key 'RunSummary') -Key 'detects') -and $null -ne (Read-JsonValue -Container (Read-JsonValue -Container $compare.RunB -Key 'RunSummary') -Key 'detects')) { (Read-JsonValue -Container (Read-JsonValue -Container $compare.RunB -Key 'RunSummary') -Key 'detects') - (Read-JsonValue -Container (Read-JsonValue -Container $compare.RunA -Key 'RunSummary') -Key 'detects') } else { $null }
        RunMsDelta = if ($null -ne (Read-JsonValue -Container (Read-JsonValue -Container $compare.RunA -Key 'RunSummary') -Key 'totalMs') -and $null -ne (Read-JsonValue -Container (Read-JsonValue -Container $compare.RunB -Key 'RunSummary') -Key 'totalMs')) { (Read-JsonValue -Container (Read-JsonValue -Container $compare.RunB -Key 'RunSummary') -Key 'totalMs') - (Read-JsonValue -Container (Read-JsonValue -Container $compare.RunA -Key 'RunSummary') -Key 'totalMs') } else { $null }
        ExportMsDelta = if ($null -ne (Read-JsonValue -Container (Read-JsonValue -Container $compare.RunA -Key 'Export') -Key 'totalMs') -and $null -ne (Read-JsonValue -Container (Read-JsonValue -Container $compare.RunB -Key 'Export') -Key 'totalMs')) { (Read-JsonValue -Container (Read-JsonValue -Container $compare.RunB -Key 'Export') -Key 'totalMs') - (Read-JsonValue -Container (Read-JsonValue -Container $compare.RunA -Key 'Export') -Key 'totalMs') } else { $null }
        ReviewRequired = Read-JsonValue -Container (Read-JsonValue -Container $compare.RunB -Key 'FinalSummary') -Key 'reviewRequired'
        ResultPath = $resultPath
    })
    Write-Host "[PostprocessPresetCompare] result=$resultPath"
}

if ($summary.Count -gt 0) {
    $summaryPath = if ([string]::IsNullOrWhiteSpace($SummaryFile)) { Join-Path $LogRoot "compare-summary.json" } else { $SummaryFile }
    $summary | ConvertTo-Json -Depth 8 | Set-Content -Path $summaryPath -Encoding UTF8
    Write-Host "[PostprocessPresetCompare] summary=$summaryPath"
}

Write-Host "[PostprocessPresetCompare] done"
Write-Host "[PostprocessPresetCompare] logs=$LogRoot"
