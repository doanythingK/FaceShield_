<#
.SYNOPSIS
    YOLO 후처리 단계별 옵션 전환 결과를 2개 run 로그로 비교합니다.

.DESCRIPTION
    AutoMaskPostProcess runId, 단계별 timing, 최종 마스크 품질 신호, export summary를 비교해
    단계별 오탐/미탐·carry 처리/시간 영향을 정량 점검할 수 있도록 출력합니다.

.PARAMETER RunALog
    비교 기준이 될 첫 번째 로그 파일 경로.

.PARAMETER RunBLog
    비교 대상 두 번째 로그 파일 경로.

.PARAMETER RunAId
    첫 번째 로그에서 특정 runId를 강제 선택할 경우 지정합니다.
    와일드카드(`*`)를 사용해 접두/접미 패턴 매칭도 가능합니다.

.PARAMETER RunBId
    두 번째 로그에서 특정 runId를 강제 선택할 경우 지정합니다.
    와일드카드(`*`)를 사용해 접두/접미 패턴 매칭도 가능합니다.

.PARAMETER RequireFrameFilter
    -StartFrame/-EndFrameExclusive가 모두 지정되어 있을 때만 필터를 적용할지 강제하는 스위치.

.PARAMETER StartFrame
    scene-cut reset 증거 필터의 시작 프레임(포함, 0 기반).

.PARAMETER EndFrameExclusive
    scene-cut reset 증거 필터의 종료 프레임(미포함, 0 기반).

.PARAMETER WindowSeconds
    frameId 기반 범위를 지정하지 않을 때 자동 구간 분석용 초 단위 길이.
    -StartFrame 또는 -EndFrameExclusive가 모두 지정되지 않은 경우에만 사용됩니다.

.PARAMETER WindowFrameRate
    WindowSeconds를 프레임 범위로 계산할 때 사용하는 fps.
    기본값은 30입니다.

    이 스크립트는 30초 이하 구간의 segment 분석에도 활용할 수 있습니다.
    예) 프레임레이트 30fps, 30초 구간: -StartFrame 0 -EndFrameExclusive 900

.PARAMETER JsonOutput
    결과를 JSON 형식으로 출력합니다.

.PARAMETER MaxWeakScoreDelta
    품질 게이트: 오탐 proxy(weak face 점수) 허용 증가량(기본 0).

.PARAMETER MinMissRecoveryDelta
    품질 게이트: 미탐 보완 proxy(Track lost-filled + interpolated) 최소 개선량(기본 0).

.PARAMETER MinPostGapFillRemovalRateDelta
    품질 게이트: 컷캐리 post-gap-fill 제거율(비율) 최소 개선량(기본 0.00).

.PARAMETER MaxRunTotalMsDelta
    속도 게이트: AutoMask run totalMs 허용 증가량(기본 0).

.PARAMETER MaxExportTotalMsDelta
    속도 게이트: Export totalMs 허용 증가량(기본 0).

.PARAMETER AllowReviewRequired
    대상 비교군에 reviewRequired=true가 발생해도 게이트 통과를 허용할지 지정합니다.

.EXAMPLE
    ./compare-yolo-postprocess-runs.ps1 .\runA.log .\runB.log -RunAId auto-a -RunBId auto-b

.EXAMPLE
    ./compare-yolo-postprocess-runs.ps1 .\runA.log .\runB.log -StartFrame 1200 -EndFrameExclusive 1800 -RequireFrameFilter

.EXAMPLE
    ./compare-yolo-postprocess-runs.ps1 .\run-off.log .\run-scene.log -RunAId auto-off -RunBId auto-scene `
      -StartFrame 300 -EndFrameExclusive 900 -RequireFrameFilter

.EXAMPLE
    ./scripts/run-srcTest-smoke.ps1 -Source srcTest/260102_jp_10.mp4 -Start 00:01:00 -Seconds 30 -LogFile .\logs\off.log
    ./scripts/run-srcTest-smoke.ps1 -Source srcTest/260102_jp_10.mp4 -Start 00:01:00 -Seconds 30 -YoloEnableGapFill -LogFile .\logs\gapfill.log
    ./scripts/compare-yolo-postprocess-runs.ps1 .\logs\off.log .\logs\gapfill.log -RunAId auto-* -RunBId auto-* -StartFrame 0 -EndFrameExclusive 900 -RequireFrameFilter
#>
param(
    [Parameter(Mandatory = $true)]
    [string] $RunALog,
    [Parameter(Mandatory = $true)]
    [string] $RunBLog,
    [Parameter(Mandatory = $false)]
    [string] $RunAId = "",
    [Parameter(Mandatory = $false)]
    [string] $RunBId = "",
    [Parameter(Mandatory = $false)]
    [switch] $RequireFrameFilter,
    [Parameter(Mandatory = $false)]
    [int] $StartFrame = -1,
    [Parameter(Mandatory = $false)]
    [int] $EndFrameExclusive = -1,
    [Parameter(Mandatory = $false)]
    [double] $WindowSeconds = 0,
    [Parameter(Mandatory = $false)]
    [double] $WindowFrameRate = 30.0,
    [Parameter(Mandatory = $false)]
    [switch] $JsonOutput,
    [Parameter(Mandatory = $false)]
    [int] $MaxWeakScoreDelta = 0,
    [Parameter(Mandatory = $false)]
    [int] $MinMissRecoveryDelta = 0,
    [Parameter(Mandatory = $false)]
    [double] $MinPostGapFillRemovalRateDelta = 0.0,
    [Parameter(Mandatory = $false)]
    [int] $MaxRunTotalMsDelta = 0,
    [Parameter(Mandatory = $false)]
    [int] $MaxExportTotalMsDelta = 0,
    [Parameter(Mandatory = $false)]
    [switch] $AllowReviewRequired
)

$ErrorActionPreference = "Stop"

foreach ($path in @($RunALog, $RunBLog)) {
    if (-not (Test-Path $path)) {
        throw "log not found: $path"
    }
}

if ($RequireFrameFilter.IsPresent -and ($StartFrame -lt 0 -or $EndFrameExclusive -lt 0)) {
    throw "RequireFrameFilter is set but StartFrame and EndFrameExclusive must both be specified"
}

if ($WindowSeconds -gt 0) {
    if ($WindowFrameRate -le 0) {
        throw "WindowSeconds is set, but WindowFrameRate must be greater than 0."
    }

    if ($StartFrame -lt 0 -and $EndFrameExclusive -lt 0) {
        $StartFrame = 0
        $EndFrameExclusive = [int][math]::Ceiling($WindowSeconds * $WindowFrameRate)
    }
    elseif ($StartFrame -ge 0 -and $EndFrameExclusive -lt 0) {
        $EndFrameExclusive = $StartFrame + [int][math]::Ceiling($WindowSeconds * $WindowFrameRate)
    }
}

function Read-RunInfo {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,
        [string] $TargetRunId
    )

    $lines = Read-LogLines -Path $Path

    $targetIdPattern = if ([string]::IsNullOrWhiteSpace($TargetRunId)) { $null } else { $TargetRunId.Trim() }
    $targetId = if ($null -eq $targetIdPattern) { $null } else { $targetIdPattern }
    $targetMatchesWildcard = if ($null -eq $targetIdPattern) { $false } else { $targetIdPattern -match '[\*\?]' }
    $resolvedTargetRunId = $null
    $runIdFromStart = $null
    $collectingTargetRun = $false
    $foundTargetRun = $false
    $encounteredRunIds = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)

    $timingsByPhase = [ordered]@{}
    $postOptions = @{}
    $runSummary = [ordered]@{}
    $faceTrackSummary = [ordered]@{}
    $sceneCarrySummary = [ordered]@{}
    $finalSummary = [ordered]@{}
    $exportSummary = [ordered]@{}
    $sceneCutReset = [ordered]@{ resetCount = 0; removed = 0; clearRanges = @() }
    $sceneCutControlSummary = [ordered]@{
        preSmoothCutPairs = 0;
        preSmoothCutWindows = 0;
        preSmoothStrongPairs = 0;
        preSmoothStrongWindows = 0;
        postSmoothCutPairs = 0;
        postSmoothCutWindows = 0;
        postSmoothStrongPairs = 0;
        postSmoothStrongWindows = 0;
        finalCutPairs = 0;
        finalCutWindows = 0;
        finalPreCutWindows = 0;
        finalPreStrongWindows = 0;
        finalPostCutWindows = 0;
        finalPostStrongWindows = 0;
        postGapFillCarryPairs = 0;
        postGapFillCarryWindows = 0;
    }

    foreach ($line in $lines) {
        $lineRunId = $null

        if ($line -match '^\[AutoMaskPostProcess\] start ') {
            if ($line -match 'runId=(\S+)') {
                $runIdFromStart = $matches[1]
                $encounteredRunIds.Add($runIdFromStart) | Out-Null
                if ($null -eq $targetIdPattern) {
                    $targetId = $runIdFromStart
                    $resolvedTargetRunId = $runIdFromStart
                    $collectingTargetRun = $true
                    $foundTargetRun = $true
                } else {
                    if ($targetMatchesWildcard) {
                        if (-not $foundTargetRun -and ($runIdFromStart -like $targetIdPattern)) {
                            $resolvedTargetRunId = $runIdFromStart
                            $foundTargetRun = $true
                            $collectingTargetRun = $true
                        } else {
                            $collectingTargetRun = ($runIdFromStart -eq $resolvedTargetRunId)
                        }
                    } else {
                        $collectingTargetRun = ($runIdFromStart -eq $targetIdPattern)
                        if ($collectingTargetRun) {
                            $resolvedTargetRunId = $runIdFromStart
                            $foundTargetRun = $true
                        }
                    }
                }

                if (-not $collectingTargetRun) { continue }
            }
            if (-not $collectingTargetRun) { continue }

            if ($line -match 'post=(True|False|true|false)') { $postOptions.post = [bool]::Parse($matches[1]) }
            if ($line -match 'roi=(True|False|true|false)') { $postOptions.roi = [bool]::Parse($matches[1]) }
            if ($line -match 'weakIso=(True|False|true|false)') { $postOptions.weakIso = [bool]::Parse($matches[1]) }
            if ($line -match 'gapFill=(True|False|true|false)') { $postOptions.gapFill = [bool]::Parse($matches[1]) }
            if ($line -match 'scene=(True|False|true|false)') { $postOptions.scene = [bool]::Parse($matches[1]) }
            if ($line -match 'smooth=(True|False|true|false)') { $postOptions.smooth = [bool]::Parse($matches[1]) }
            if ($line -match 'tracking=(True|False|true|false)') { $postOptions.tracking = [bool]::Parse($matches[1]) }
            if ($line -match 'everyN=(\d+)') { $postOptions.everyN = [int]$matches[1] }
            if ($line -match 'runMissRecovery=(True|False|true|false)') { $postOptions.runMissRecovery = [bool]::Parse($matches[1]) }
            if ($line -match 'runTrackPost=(True|False|true|false)') { $postOptions.runTrackPost = [bool]::Parse($matches[1]) }
            if ($line -match 'profile=([^ ]+)') { $postOptions.profile = $matches[1] }
            if ($line -match 'totalFrames=(\d+)') { $postOptions.totalFrames = [int]$matches[1] }
            continue
        }

        if ($line -match 'runId=([^ ]+)') {
            $lineRunId = $matches[1]
            $encounteredRunIds.Add($lineRunId) | Out-Null
            if ($null -eq $targetIdPattern) {
                $targetId = $lineRunId
                $resolvedTargetRunId = $lineRunId
                $collectingTargetRun = $true
                $foundTargetRun = $true
            }
            elseif ($targetMatchesWildcard) {
                if ($lineRunId -like $targetIdPattern -and -not $foundTargetRun) {
                    $resolvedTargetRunId = $lineRunId
                    $collectingTargetRun = $true
                    $foundTargetRun = $true
                } else {
                    $collectingTargetRun = ($lineRunId -eq $resolvedTargetRunId)
                }
            }
            else {
                $collectingTargetRun = ($lineRunId -eq $targetIdPattern)
                if ($collectingTargetRun) {
                    $resolvedTargetRunId = $lineRunId
                    $foundTargetRun = $true
                } elseif ($lineRunId -ne $resolvedTargetRunId) {
                    $collectingTargetRun = $false
                }
            }
        }

        if (-not $collectingTargetRun) {
            continue
        }

        if ($line -match '^\[AutoMaskPostProcessTiming\] runId=(\S+) phase=([^ ]+) run=(True|False|true|false) elapsedMs=(\d+)') {
            if ($matches[1] -ne $resolvedTargetRunId) { continue }

            $phase = $matches[2]
            $elapsed = [long]$matches[4]
            $timingsByPhase[$phase] = $elapsed
            continue
        }

        if ($line -match '^\[AutoRunSummary\] runId=([^,]+), .* detector=([^,]+), mode=([^,]+), totalFrames=(\d+), startFrame=(\d+), processed=(\d+), decoded=(\d+), detects=(\d+), interpolated=(\d+), readMs=(\d+), decodeMs=(\d+), detectMs=(\d+), maskMs=(\d+), totalMs=(\d+), .* tracking=(True|False|true|false), everyN=(\d+)') {
            if ($matches[1].Trim() -ne $resolvedTargetRunId) { continue }
            $runSummary = [ordered]@{
                detector = $matches[2];
                mode = $matches[3];
                totalFrames = [int]$matches[4];
                startFrame = [int]$matches[5];
                processed = [int]$matches[6];
                decoded = [int]$matches[7];
                detects = [int]$matches[8];
                interpolated = [int]$matches[9];
                readMs = [long]$matches[10];
                decodeMs = [long]$matches[11];
                detectMs = [long]$matches[12];
                maskMs = [long]$matches[13];
                totalMs = [long]$matches[14];
                tracking = [bool]::Parse($matches[15]);
                everyN = [int]$matches[16];
            }
            continue
        }

        if ($line -match '^\[FaceTrackPost\] .* tracks=(\d+), filled=(\d+), lostFilled=(\d+), fillInitial=(\d+), lostFrames=([^,]+), removedShort=(\d+), removedSparse=(\d+), removedUnstableTail=(\d+), removedEdgeTail=(\d+), removedLower=(\d+), rewritten=(\d+)') {
            $faceTrackSummary = [ordered]@{
                tracks = [int]$matches[1];
                filled = [int]$matches[2];
                lostFilled = [int]$matches[3];
                initialFilled = [int]$matches[4];
                removedShort = [int]$matches[6];
                removedSparse = [int]$matches[7];
                removedUnstableTail = [int]$matches[8];
                removedEdgeTail = [int]$matches[9];
                removedLower = [int]$matches[10];
                rewritten = [int]$matches[11];
            }
            continue
        }

        if ($line -match '^\[SmokeFaceTrackPost\] label=([^,]+), tracks=(\d+), filled=(\d+), lostFilled=(\d+), initialFilled=(\d+), blockedInitialFill=(\d+), lostFrames=([^,]+), removedShort=(\d+), removedSparse=(\d+), removedUnstableTail=(\d+), removedEdgeTail=(\d+), removedLower=(\d+), rewritten=(\d+)') {
            $faceTrackSummary = [ordered]@{
                tracks = [int]$matches[2];
                filled = [int]$matches[3];
                lostFilled = [int]$matches[4];
                initialFilled = [int]$matches[5];
                removedShort = [int]$matches[7];
                removedSparse = [int]$matches[8];
                removedUnstableTail = [int]$matches[9];
                removedEdgeTail = [int]$matches[10];
                removedLower = [int]$matches[11];
                rewritten = [int]$matches[12];
            }
            continue
        }

        if ($line -match '^\[YoloSceneCutCarryCleanup\] .* removed=(\d+), removedFrames=([^,]+), removedUnsupportedStrong=(\d+), removedUnsupportedStrongFrames=([^,]+), protectedStrong=(\d+), protectedStrongFrames=([^,]+), blockedFrames=([^,]+), purgeFrames=(\d+), blockFrames=(\d+), extendedWeakMaxConfidence=([0-9.]+)') {
            $sceneCarrySummary = [ordered]@{
                removed = [int]$matches[1];
                removedFrames = $matches[2];
                removedUnsupportedStrong = [int]$matches[3];
                removedUnsupportedStrongFrames = $matches[4];
                protectedStrong = [int]$matches[5];
                protectedStrongFrames = $matches[6];
                blockedFrames = $matches[7];
                purgeFrames = [int]$matches[8];
                blockFrames = [int]$matches[9];
                extendedWeakMaxConfidence = [float]$matches[10];
            }
            continue
        }

        if ($line -match '^\[YoloSceneCutRebuild\] runId=(\S+) stage=pre-smooth action=plan preCutPairs=(\S+) preCutWindows=([^ ]+) preStrongPairs=(\S+) preStrongWindows=([^ ]+) rebuildWindowFrames=(\d+)') {
            if ($matches[1].Trim() -ne $resolvedTargetRunId) { continue }
            $sceneCutControlSummary.preSmoothCutPairs = Parse-IntOrZero $matches[2]
            $sceneCutControlSummary.preSmoothCutWindows = Count-TextListValues $matches[3]
            $sceneCutControlSummary.preSmoothStrongPairs = Parse-IntOrZero $matches[4]
            $sceneCutControlSummary.preSmoothStrongWindows = Count-TextListValues $matches[5]
            continue
        }

        if ($line -match '^\[YoloSceneCutRebuild\] runId=(\S+) stage=post-smooth action=plan postCutPairs=(\S+) postCutWindows=([^ ]+) postStrongPairs=(\S+) postStrongWindows=([^ ]+) rebuildWindowFrames=(\d+)') {
            if ($matches[1].Trim() -ne $resolvedTargetRunId) { continue }
            $sceneCutControlSummary.postSmoothCutPairs = Parse-IntOrZero $matches[2]
            $sceneCutControlSummary.postSmoothCutWindows = Count-TextListValues $matches[3]
            $sceneCutControlSummary.postSmoothStrongPairs = Parse-IntOrZero $matches[4]
            $sceneCutControlSummary.postSmoothStrongWindows = Count-TextListValues $matches[5]
            continue
        }

        if ($line -match '^\[YoloSceneCutRebuild\] runId=(\S+) stage=final action=cleanup carryCutPairs=(\S+) carryWindows=([^ ]+) preWindowSources=pre:(\S+) preStrong:([^ ]+) postWindowSources=post:(\S+) postStrong:([^ ]+) purgeFrames=(\d+) blockFrames=(\d+) maxConfidence=([0-9.]+) extendedWeakMaxConfidence=([0-9.]+)') {
            if ($matches[1].Trim() -ne $resolvedTargetRunId) { continue }
            $sceneCutControlSummary.finalCutPairs = Parse-IntOrZero $matches[2]
            $sceneCutControlSummary.finalCutWindows = Count-TextListValues $matches[3]
            $sceneCutControlSummary.finalPreCutWindows = Count-TextListValues $matches[4]
            $sceneCutControlSummary.finalPreStrongWindows = Count-TextListValues $matches[5]
            $sceneCutControlSummary.finalPostCutWindows = Count-TextListValues $matches[6]
            $sceneCutControlSummary.finalPostStrongWindows = Count-TextListValues $matches[7]
            continue
        }

        if ($line -match '^\[YoloSceneCutRebuild\] runId=(\S+) stage=post-gap-fill action=cleanup carryCutPairs=(\S+) carryWindows=([^ ]+) purgeFrames=(\d+) blockFrames=(\d+) maxConfidence=([0-9.]+) extendedWeakMaxConfidence=([0-9.]+)') {
            if ($matches[1].Trim() -ne $resolvedTargetRunId) { continue }
            $sceneCutControlSummary.postGapFillCarryPairs = Parse-IntOrZero $matches[2]
            $sceneCutControlSummary.postGapFillCarryWindows = Count-TextListValues $matches[3]
            continue
        }

        if ($line -match '^\[(?:FinalMaskSummary|SmokeFinalMaskSummary)\] .* reviewRequired=(True|False|true|false) reviewReasons=([^\]]+)') {
            $finalSummary.reviewRequired = [bool]::Parse($matches[1])
            $finalSummary.reviewReasons = $matches[2]
            continue
        }

        if ($line -match '^\[(?:FinalMaskSummary|SmokeFinalMaskSummary)\] .* isolated=(\d+), isolatedFrames=([^,]+), lowConf=(\d+), lowConfFrames=([^,]+), weakNonEdge=(\d+), weakNonEdgeFrames=([^,]+), edgeWeak=(\d+), edgeWeakFrames=([^,]+), topEdgeWeak=(\d+), topEdgeWeakFrames=([^,]+), topEdgeLarge=(\d+), topEdgeLargeFrames=([^,]+), upperWeak=(\d+), upperWeakFrames=([^,]+), lowerWeak=(\d+), lowerWeakFrames=([^,]+), aspectBad=(\d+), aspectBadFrames=([^,]+), tinyWeak=(\d+), tinyWeakFrames=([^,]+), tinyShort=(\d+), tinyShortFrames=([^,]+)') {
            $finalSummary.isolated = [int]$matches[1]
            $finalSummary.lowConf = [int]$matches[3]
            $finalSummary.weakNonEdge = [int]$matches[5]
            $finalSummary.edgeWeak = [int]$matches[7]
            $finalSummary.topEdgeWeak = [int]$matches[9]
            $finalSummary.topEdgeLarge = [int]$matches[11]
            $finalSummary.upperWeak = [int]$matches[13]
            $finalSummary.lowerWeak = [int]$matches[15]
            $finalSummary.aspectBad = [int]$matches[17]
            $finalSummary.tinyWeak = [int]$matches[19]
            $finalSummary.tinyShort = [int]$matches[21]
            continue
        }

        if ($line -match '^\[(?:FinalMaskSummary|SmokeFinalMaskSummary)\] .* shortGaps=(\d+)') {
            $finalSummary.shortGaps = [int]$matches[1]
            continue
        }

        if ($line -match '^\[(?:FinalMaskSummary|SmokeFinalMaskSummary)\] .* largeJumpGaps=(\d+)') {
            $finalSummary.largeJumpGaps = [int]$matches[1]
            continue
        }
        if ($line -match '^\[(?:FinalMaskSummary|SmokeFinalMaskSummary\)] .* postGapFillCarryPairs=(\d+), postGapFillRemoved=(\d+), postGapFillProtected=(\d+), postGapFillRemovalRate=([0-9.]+), postGapFillProtectedRate=([0-9.]+)') {
            $finalSummary.postGapFillCarryPairs = [int]$matches[1]
            $finalSummary.postGapFillRemoved = [int]$matches[2]
            $finalSummary.postGapFillProtected = [int]$matches[3]
            $finalSummary.postGapFillRemovalRate = [double]$matches[4]
            $finalSummary.postGapFillProtectedRate = [double]$matches[5]
            continue
        }

        if ($line -match '^\[(?:FinalMaskSummary|SmokeFinalMaskSummary\)] .* postGapFillCarryPairs=(\d+), postGapFillRemoved=(\d+), postGapFillProtected=(\d+)') {
            $finalSummary.postGapFillCarryPairs = [int]$matches[1]
            $finalSummary.postGapFillRemoved = [int]$matches[2]
            $finalSummary.postGapFillProtected = [int]$matches[3]
            continue
        }

        if ($line -match '^\[(?:FinalMaskSummary|SmokeFinalMaskSummary)\] .* perFaceShortGaps=(\d+)') {
            $finalSummary.perFaceShortGaps = [int]$matches[1]
            continue
        }

        if ($line -match '^\[ExportRunSummary\] runId=([^,]+), mode=([^,]+), frames=(\d+), bitmapMaskFrames=(\d+), directFaceFrames=(\d+), swsToBgraMs=(\d+), maskMs=(\d+), swsToEncMs=(\d+), encodeMs=(\d+), totalMs=(\d+), hybridCopyAttempted=(True|False|true|false), hybridCopyUsed=(True|False|true|false), forceSoftwareEncoder=(True|False|true|false), forceSafeEncoding=(True|False|true|false), forceAudioTranscode=(True|False|true|false), forceH264Fallback=(True|False|true|false)(?:, hybridWindowExpectedEncodedFrames=(\d+), hybridWindowEncodedFrames=(\d+), hybridWindowFrameShortfall=(\d+), sampleWindowSourceFrames=(\d+), sampleWindowProducedFrames=(\d+), sampleWindowFrameShortfall=(\d+))?') {
            if ($matches[1].Trim() -ne $resolvedTargetRunId) { continue }
            $exportSummary = [ordered]@{
                runId = $matches[1].Trim();
                exportMode = $matches[2];
                frames = [int]$matches[3];
                bitmapMaskFrames = [int]$matches[4];
                directFaceFrames = [int]$matches[5];
                swsToBgraMs = [long]$matches[6];
                maskMs = [long]$matches[7];
                swsToEncMs = [long]$matches[8];
                encodeMs = [long]$matches[9];
                totalMs = [long]$matches[10];
                hybridCopyAttempted = [bool]::Parse($matches[11]);
                hybridCopyUsed = [bool]::Parse($matches[12]);
                forceSoftwareEncoder = [bool]::Parse($matches[13]);
                forceSafeEncoding = [bool]::Parse($matches[14]);
                forceAudioTranscode = [bool]::Parse($matches[15]);
                forceH264Fallback = [bool]::Parse($matches[16]);
                hybridWindowExpectedEncodedFrames = if ($null -ne $matches[17]) { [int]$matches[17] } else { 0 };
                hybridWindowEncodedFrames = if ($null -ne $matches[18]) { [int]$matches[18] } else { 0 };
                hybridWindowFrameShortfall = if ($null -ne $matches[19]) { [int]$matches[19] } else { 0 };
                sampleWindowSourceFrames = if ($null -ne $matches[20]) { [int]$matches[20] } else { 0 };
                sampleWindowProducedFrames = if ($null -ne $matches[21]) { [int]$matches[21] } else { 0 };
                sampleWindowFrameShortfall = if ($null -ne $matches[22]) { [int]$matches[22] } else { 0 };
            }
            continue
        }

        if ($collectingTargetRun -and ($line -match '^\[AutoMask\] scene-cut reset idx=(\d+) clearFrom=(\d+) clearTo=(\d+) removed=(\d+)')) {
            $idx = [int]$matches[1]
            if ($StartFrame -ge 0 -and $EndFrameExclusive -ge 0 -and ($idx -lt $StartFrame -or $idx -ge $EndFrameExclusive)) { continue }
            $sceneCutReset.resetCount++
            $sceneCutReset.removed += [int]$matches[4]
            $sceneCutReset.clearRanges += "$($matches[2])-$($matches[3])"
        }
    }

    if (-not $foundTargetRun) {
        $knownRunIds = [string]::Join(", ", $encounteredRunIds)
        if ($targetMatchesWildcard) {
            throw "target run id pattern '$targetIdPattern' not found in log: $Path. known runs: $knownRunIds"
        }
        throw "target run id '$targetId' not found in log: $Path. known runs: $knownRunIds"
    }

    return [pscustomobject]@{
        Path = (Resolve-Path $Path).Path;
        RunId = if ($null -ne $resolvedTargetRunId) { $resolvedTargetRunId } else { if ($null -ne $targetId) { $targetId } else { if ($null -ne $runIdFromStart) { $runIdFromStart } else { "none" } } };
        StartOptions = $postOptions;
        Timings = $timingsByPhase;
        RunSummary = $runSummary;
        FaceTrack = $faceTrackSummary;
        SceneCarry = $sceneCarrySummary;
        SceneCutControl = $sceneCutControlSummary;
        FinalSummary = $finalSummary;
        Export = $exportSummary;
        SceneCutReset = $sceneCutReset;
    }
}

function Read-LogLines {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    $encoding = [System.Text.Encoding]::UTF8
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) {
        $encoding = [System.Text.Encoding]::Unicode
    }
    elseif ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFE -and $bytes[1] -eq 0xFF) {
        $encoding = [System.Text.Encoding]::BigEndianUnicode
    }
    elseif ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        $encoding = [System.Text.Encoding]::UTF8
    }

    return [System.IO.File]::ReadAllLines($Path, $encoding)
}

function Parse-IntOrZero {
    param([Parameter(Mandatory = $true)][string] $Text)

    if ([string]::IsNullOrWhiteSpace($Text) -or $Text -eq "none") { return 0 }
    $value = 0
    if ([int]::TryParse($Text, [ref]$value)) {
        return $value
    }
    return 0
}

function Count-TextListValues {
    param([Parameter(Mandatory = $true)][string] $ListText)

    if ([string]::IsNullOrWhiteSpace($ListText) -or $ListText -eq "none") { return 0 }
    $tokens = $ListText -split '[,|]'
    return @($tokens | Where-Object { -not [string]::IsNullOrWhiteSpace($_.Trim()) }).Count
}

function Format-FrameRange {
    param(
        [int]$StartFrame,
        [int]$EndFrameExclusive
    )

    if ($StartFrame -lt 0 -and $EndFrameExclusive -lt 0) {
        return "all"
    }

    return "${StartFrame}-$EndFrameExclusive"
}

function Get-WeakFaceScore {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable] $Summary
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
    foreach ($k in $keys) {
        if ($Summary.ContainsKey($k)) {
            $score += [int]$Summary[$k]
        }
    }
    return $score
}

function Format-ExportMeta {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable] $Export
    )

    if (-not $Export.ContainsKey('hybridCopyAttempted')) {
        return "mode=n/a"
    }

    return "mode={0}, hybridAttempted={1}, hybridUsed={2}, forceSafe={3}, softEncoder={4}, h264Fallback={5}, audioTranscode={6}" -f @(
        $Export.exportMode,
        $Export.hybridCopyAttempted,
        $Export.hybridCopyUsed,
        $Export.forceSafeEncoding,
        $Export.forceSoftwareEncoder,
        $Export.forceH264Fallback,
        $Export.forceAudioTranscode
    )
}

function Format-ExportIntegrity {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable] $Export
    )

    if (-not $Export.ContainsKey('hybridWindowExpectedEncodedFrames')) {
        return "integrity=n/a"
    }

    return "hybridWindow={0}-{1} shortfall={2}, sampleWindow={3}-{4} shortfall={5}" -f @(
        $Export.hybridWindowExpectedEncodedFrames,
        $Export.hybridWindowEncodedFrames,
        $Export.hybridWindowFrameShortfall,
        $Export.sampleWindowSourceFrames,
        $Export.sampleWindowProducedFrames,
        $Export.sampleWindowFrameShortfall
    )
}

$runA = Read-RunInfo -Path $RunALog -TargetRunId $RunAId
$runB = Read-RunInfo -Path $RunBLog -TargetRunId $RunBId

$allPhases = @($runA.Timings.Keys + $runB.Timings.Keys | Sort-Object -Unique)

$startFrameDisplay = $StartFrame
$endFrameDisplay = $EndFrameExclusive
$frameMode = if ($RequireFrameFilter.IsPresent) { "filtered" } else { "full" }
$frameRangeText = Format-FrameRange -StartFrame $startFrameDisplay -EndFrameExclusive $endFrameDisplay

Write-Host "[YoloPostRunCompare] frameMode=$frameMode span=$frameRangeText runA=$($runA.RunId) runB=$($runB.RunId)"
Write-Host "[YoloPostRunCompare] logA=$($runA.Path)"
Write-Host "[YoloPostRunCompare] logB=$($runB.Path)"

Write-Host ""
Write-Host "== post-process options =="
Write-Host ("A post={0} roi={1} weakIso={2} gapFill={3} scene={4} smooth={5} runTrackPost={6} runMissRecovery={7}" -f `
    $runA.StartOptions.post,$runA.StartOptions.roi,$runA.StartOptions.weakIso,$runA.StartOptions.gapFill,$runA.StartOptions.scene,$runA.StartOptions.smooth,$runA.StartOptions.runTrackPost,$runA.StartOptions.runMissRecovery)
Write-Host ("B post={0} roi={1} weakIso={2} gapFill={3} scene={4} smooth={5} runTrackPost={6} runMissRecovery={7}" -f `
    $runB.StartOptions.post,$runB.StartOptions.roi,$runB.StartOptions.weakIso,$runB.StartOptions.gapFill,$runB.StartOptions.scene,$runB.StartOptions.smooth,$runB.StartOptions.runTrackPost,$runB.StartOptions.runMissRecovery)

Write-Host ""
Write-Host "== timing summary =="
foreach ($phase in $allPhases) {
    $a = if ($runA.Timings.ContainsKey($phase)) { $runA.Timings[$phase] } else { $null }
    $b = if ($runB.Timings.ContainsKey($phase)) { $runB.Timings[$phase] } else { $null }
    $delta = if ($null -ne $a -and $null -ne $b) { $b - $a } else { $null }
    $deltaText = if ($null -eq $delta) { "n/a" } else { if ($delta -gt 0) { "+$delta" } else { "$delta" } }
    Write-Host ([string]::Format("{0,-28} A={1,8}ms B={2,8}ms Δ={3}", $phase, ($a ?? "-").ToString(), ($b ?? "-").ToString(), $deltaText))
}

Write-Host ""
Write-Host "== run summary (auto detect/export pipeline) =="
if ($runA.RunSummary.Count -gt 0) {
    Write-Host ("A detector={0} mode={1} totalFrames={2} processed={3} detects={4} interpolated={5} totalMs={6}" -f `
        $runA.RunSummary.detector, $runA.RunSummary.mode, $runA.RunSummary.totalFrames, $runA.RunSummary.processed, $runA.RunSummary.detects, $runA.RunSummary.interpolated, $runA.RunSummary.totalMs)
}
if ($runB.RunSummary.Count -gt 0) {
    Write-Host ("B detector={0} mode={1} totalFrames={2} processed={3} detects={4} interpolated={5} totalMs={6}" -f `
        $runB.RunSummary.detector, $runB.RunSummary.mode, $runB.RunSummary.totalFrames, $runB.RunSummary.processed, $runB.RunSummary.detects, $runB.RunSummary.interpolated, $runB.RunSummary.totalMs)
}
if ($runA.RunSummary.Count -gt 0 -and $runB.RunSummary.Count -gt 0) {
    $deltaProcessed = $runB.RunSummary.processed - $runA.RunSummary.processed
    $deltaDetects = $runB.RunSummary.detects - $runA.RunSummary.detects
    $deltaInterpolated = $runB.RunSummary.interpolated - $runA.RunSummary.interpolated
    $deltaRunMs = $runB.RunSummary.totalMs - $runA.RunSummary.totalMs
    Write-Host ("autoRunDelta processed={0} detects={1} interpolated={2} totalMs={3}" -f $deltaProcessed, $deltaDetects, $deltaInterpolated, ($deltaRunMs > 0 ? "+$deltaRunMs" : "$deltaRunMs"))
}

Write-Host ""
Write-Host "== quality / carry signal summary =="
$aReview = if ($runA.FinalSummary.ContainsKey('reviewRequired')) { $runA.FinalSummary.reviewRequired } else { $false }
$bReview = if ($runB.FinalSummary.ContainsKey('reviewRequired')) { $runB.FinalSummary.reviewRequired } else { $false }
Write-Host ("A reviewRequired={0} reasons={1}" -f $aReview, $(if ($runA.FinalSummary.ContainsKey('reviewReasons')) { $runA.FinalSummary.reviewReasons } else { "n/a" }))
Write-Host ("B reviewRequired={0} reasons={1}" -f $bReview, $(if ($runB.FinalSummary.ContainsKey('reviewReasons')) { $runB.FinalSummary.reviewReasons } else { "n/a" }))

Write-Host "-- weak-mask categories --"
foreach ($k in 'isolated','lowConf','weakNonEdge','edgeWeak','topEdgeWeak','topEdgeLarge','upperWeak','lowerWeak','aspectBad','tinyWeak','tinyShort','shortGaps','largeJumpGaps','perFaceShortGaps') {
    $a = if ($runA.FinalSummary.ContainsKey($k)) { $runA.FinalSummary[$k] } else { 0 }
    $b = if ($runB.FinalSummary.ContainsKey($k)) { $runB.FinalSummary[$k] } else { 0 }
    $delta = $b - $a
    Write-Host ([string]::Format("{0,-16} A={1,4} B={2,4} Δ={3,5}", $k, $a, $b, $delta))
}

if ($runA.FaceTrack.Count -gt 0 -or $runB.FaceTrack.Count -gt 0) {
    Write-Host ""
    Write-Host "== track post fill summary =="
    Write-Host ("A tracks={0} fillGap={1} fillLost={2} filledInitial={3} rewritten={4}" -f `
        $(if ($runA.FaceTrack.ContainsKey('tracks')) { $runA.FaceTrack.tracks } else { "n/a" }),`
        $(if ($runA.FaceTrack.ContainsKey('filled')) { $runA.FaceTrack.filled } else { "n/a" }),`
        $(if ($runA.FaceTrack.ContainsKey('lostFilled')) { $runA.FaceTrack.lostFilled } else { "n/a" }),`
        $(if ($runA.FaceTrack.ContainsKey('initialFilled')) { $runA.FaceTrack.initialFilled } else { "n/a" }),`
        $(if ($runA.FaceTrack.ContainsKey('rewritten')) { $runA.FaceTrack.rewritten } else { "n/a" }))
    Write-Host ("B tracks={0} fillGap={1} fillLost={2} filledInitial={3} rewritten={4}" -f `
        $(if ($runB.FaceTrack.ContainsKey('tracks')) { $runB.FaceTrack.tracks } else { "n/a" }),`
        $(if ($runB.FaceTrack.ContainsKey('filled')) { $runB.FaceTrack.filled } else { "n/a" }),`
        $(if ($runB.FaceTrack.ContainsKey('lostFilled')) { $runB.FaceTrack.lostFilled } else { "n/a" }),`
        $(if ($runB.FaceTrack.ContainsKey('initialFilled')) { $runB.FaceTrack.initialFilled } else { "n/a" }),`
        $(if ($runB.FaceTrack.ContainsKey('rewritten')) { $runB.FaceTrack.rewritten } else { "n/a" }))
}

Write-Host ""
Write-Host "== scene-cut carry control summary =="
Write-Host ("A removed={0} removedUnsupportedStrong={1} protectedStrong={2}" -f $runA.SceneCarry.removed, $runA.SceneCarry.removedUnsupportedStrong, $runA.SceneCarry.protectedStrong)
Write-Host ("B removed={0} removedUnsupportedStrong={1} protectedStrong={2}" -f $runB.SceneCarry.removed, $runB.SceneCarry.removedUnsupportedStrong, $runB.SceneCarry.protectedStrong)
Write-Host ("A postGapFillCarryPairs={0}, removed={1}, protected={2}, removalRate={3:P1}, protectedRate={4:P1}" -f `
    $(if ($runA.FinalSummary.ContainsKey('postGapFillCarryPairs')) { $runA.FinalSummary.postGapFillCarryPairs } else { "n/a" }), `
    $(if ($runA.FinalSummary.ContainsKey('postGapFillRemoved')) { $runA.FinalSummary.postGapFillRemoved } else { "n/a" }), `
    $(if ($runA.FinalSummary.ContainsKey('postGapFillProtected')) { $runA.FinalSummary.postGapFillProtected } else { "n/a" }), `
    $(if ($runA.FinalSummary.ContainsKey('postGapFillRemovalRate')) { [double]$runA.FinalSummary.postGapFillRemovalRate } else { 0 }), `
    $(if ($runA.FinalSummary.ContainsKey('postGapFillProtectedRate')) { [double]$runA.FinalSummary.postGapFillProtectedRate } else { 0 }))
Write-Host ("B postGapFillCarryPairs={0}, removed={1}, protected={2}, removalRate={3:P1}, protectedRate={4:P1}" -f `
    $(if ($runB.FinalSummary.ContainsKey('postGapFillCarryPairs')) { $runB.FinalSummary.postGapFillCarryPairs } else { "n/a" }), `
    $(if ($runB.FinalSummary.ContainsKey('postGapFillRemoved')) { $runB.FinalSummary.postGapFillRemoved } else { "n/a" }), `
    $(if ($runB.FinalSummary.ContainsKey('postGapFillProtected')) { $runB.FinalSummary.postGapFillProtected } else { "n/a" }), `
    $(if ($runB.FinalSummary.ContainsKey('postGapFillRemovalRate')) { [double]$runB.FinalSummary.postGapFillRemovalRate } else { 0 }), `
    $(if ($runB.FinalSummary.ContainsKey('postGapFillProtectedRate')) { [double]$runB.FinalSummary.postGapFillProtectedRate } else { 0 }))

Write-Host ""
Write-Host "== scene-cut rebuild control summary =="
Write-Host ("A pre-smooth cuts={0}, windows={1} / strongCuts={2}, windows={3}" -f `
    $runA.SceneCutControl.preSmoothCutPairs, $runA.SceneCutControl.preSmoothCutWindows, $runA.SceneCutControl.preSmoothStrongPairs, $runA.SceneCutControl.preSmoothStrongWindows)
Write-Host ("B pre-smooth cuts={0}, windows={1} / strongCuts={2}, windows={3}" -f `
    $runB.SceneCutControl.preSmoothCutPairs, $runB.SceneCutControl.preSmoothCutWindows, $runB.SceneCutControl.preSmoothStrongPairs, $runB.SceneCutControl.preSmoothStrongWindows)
Write-Host ("A post-smooth cuts={0}, windows={1} / strongCuts={2}, windows={3}" -f `
    $runA.SceneCutControl.postSmoothCutPairs, $runA.SceneCutControl.postSmoothCutWindows, $runA.SceneCutControl.postSmoothStrongPairs, $runA.SceneCutControl.postSmoothStrongWindows)
Write-Host ("B post-smooth cuts={0}, windows={1} / strongCuts={2}, windows={3}" -f `
    $runB.SceneCutControl.postSmoothCutPairs, $runB.SceneCutControl.postSmoothCutWindows, $runB.SceneCutControl.postSmoothStrongPairs, $runB.SceneCutControl.postSmoothStrongWindows)
Write-Host ("A final carry cuts={0}, carryWindows={1}, preWindows={2}/{3}, postWindows={4}/{5}" -f `
    $runA.SceneCutControl.finalCutPairs, $runA.SceneCutControl.finalCutWindows, $runA.SceneCutControl.finalPreCutWindows, $runA.SceneCutControl.finalPreStrongWindows, $runA.SceneCutControl.finalPostCutWindows, $runA.SceneCutControl.finalPostStrongWindows)
Write-Host ("B final carry cuts={0}, carryWindows={1}, preWindows={2}/{3}, postWindows={4}/{5}" -f `
    $runB.SceneCutControl.finalCutPairs, $runB.SceneCutControl.finalCutWindows, $runB.SceneCutControl.finalPreCutWindows, $runB.SceneCutControl.finalPreStrongWindows, $runB.SceneCutControl.finalPostCutWindows, $runB.SceneCutControl.finalPostStrongWindows)
Write-Host ("A post-gap-fill carry cuts={0}, windows={1}" -f $runA.SceneCutControl.postGapFillCarryPairs, $runA.SceneCutControl.postGapFillCarryWindows)
Write-Host ("B post-gap-fill carry cuts={0}, windows={1}" -f $runB.SceneCutControl.postGapFillCarryPairs, $runB.SceneCutControl.postGapFillCarryWindows)

if ($runA.SceneCutReset.removed -gt 0 -or $runB.SceneCutReset.removed -gt 0) {
    Write-Host ""
    Write-Host "== off-mode scene-cut reset evidence =="
    Write-Host ("A sceneCutResetCount={0} removedFrames={1}" -f $runA.SceneCutReset.resetCount, $runA.SceneCutReset.removed)
    Write-Host ("B sceneCutResetCount={0} removedFrames={1}" -f $runB.SceneCutReset.resetCount, $runB.SceneCutReset.removed)
    Write-Host ("A clearRanges={0}" -f ($runA.SceneCutReset.clearRanges.Count -gt 0 ? ($runA.SceneCutReset.clearRanges -join ", ") : "n/a"))
    Write-Host ("B clearRanges={0}" -f ($runB.SceneCutReset.clearRanges.Count -gt 0 ? ($runB.SceneCutReset.clearRanges -join ", ") : "n/a"))
}

Write-Host ""
Write-Host "== export summary =="
if ($runA.Export.Count -gt 0) {
    Write-Host ("A exportMs={0}, maskFrames={1}, bitmapMasks={2}, directFaces={3}, {4}, {5}" -f $runA.Export.totalMs, $runA.Export.frames, $runA.Export.bitmapMaskFrames, $runA.Export.directFaceFrames, (Format-ExportMeta -Export $runA.Export), (Format-ExportIntegrity -Export $runA.Export))
}
if ($runB.Export.Count -gt 0) {
    Write-Host ("B exportMs={0}, maskFrames={1}, bitmapMasks={2}, directFaces={3}, {4}, {5}" -f $runB.Export.totalMs, $runB.Export.frames, $runB.Export.bitmapMaskFrames, $runB.Export.directFaceFrames, (Format-ExportMeta -Export $runB.Export), (Format-ExportIntegrity -Export $runB.Export))
}

$exportDelta = if (($runA.Export.ContainsKey('totalMs')) -and ($runB.Export.ContainsKey('totalMs'))) { $runB.Export.totalMs - $runA.Export.totalMs } else { $null }
if ($null -ne $exportDelta) {
    Write-Host ("exportDeltaMs={0}" -f ($exportDelta > 0 ? "+$exportDelta" : "$exportDelta"))
}

Write-Host ""
Write-Host "== quick decision hints =="
$aWeakScore = Get-WeakFaceScore -Summary $runA.FinalSummary
$bWeakScore = Get-WeakFaceScore -Summary $runB.FinalSummary
$aTrackFilled = if ($runA.FaceTrack.ContainsKey('lostFilled')) { [int]$runA.FaceTrack.lostFilled } else { 0 }
$bTrackFilled = if ($runB.FaceTrack.ContainsKey('lostFilled')) { [int]$runB.FaceTrack.lostFilled } else { 0 }
$aInterpolated = if ($runA.RunSummary.ContainsKey('interpolated')) { [int]$runA.RunSummary.interpolated } else { 0 }
$bInterpolated = if ($runB.RunSummary.ContainsKey('interpolated')) { [int]$runB.RunSummary.interpolated } else { 0 }
$aExport = if ($runA.Export.ContainsKey('totalMs')) { [long]$runA.Export.totalMs } else { 0 }
$bExport = if ($runB.Export.ContainsKey('totalMs')) { [long]$runB.Export.totalMs } else { 0 }

$weakDelta = $bWeakScore - $aWeakScore
$trackDelta = $bTrackFilled - $aTrackFilled
$interpDelta = $bInterpolated - $aInterpolated
$exportDeltaForHint = $bExport - $aExport
$sceneCutDelta = $runB.SceneCutReset.removed - $runA.SceneCutReset.removed
$shortGapDelta = (if ($runB.FinalSummary.ContainsKey('shortGaps')) { [int]$runB.FinalSummary.shortGaps } else { 0 }) - (if ($runA.FinalSummary.ContainsKey('shortGaps')) { [int]$runA.FinalSummary.shortGaps } else { 0 })
$largeJumpGapDelta = (if ($runB.FinalSummary.ContainsKey('largeJumpGaps')) { [int]$runB.FinalSummary.largeJumpGaps } else { 0 }) - (if ($runA.FinalSummary.ContainsKey('largeJumpGaps')) { [int]$runA.FinalSummary.largeJumpGaps } else { 0 })
$postGapFillCarryPairDelta = (if ($runB.FinalSummary.ContainsKey('postGapFillCarryPairs')) { [int]$runB.FinalSummary.postGapFillCarryPairs } else { 0 }) - (if ($runA.FinalSummary.ContainsKey('postGapFillCarryPairs')) { [int]$runA.FinalSummary.postGapFillCarryPairs } else { 0 })
$postGapFillRemovedDelta = (if ($runB.FinalSummary.ContainsKey('postGapFillRemoved')) { [int]$runB.FinalSummary.postGapFillRemoved } else { 0 }) - (if ($runA.FinalSummary.ContainsKey('postGapFillRemoved')) { [int]$runA.FinalSummary.postGapFillRemoved } else { 0 })
$postGapFillProtectedDelta = (if ($runB.FinalSummary.ContainsKey('postGapFillProtected')) { [int]$runB.FinalSummary.postGapFillProtected } else { 0 }) - (if ($runA.FinalSummary.ContainsKey('postGapFillProtected')) { [int]$runA.FinalSummary.postGapFillProtected } else { 0 })
$postGapFillRemovalRateDelta = (if ($runB.FinalSummary.ContainsKey('postGapFillRemovalRate')) { [double]$runB.FinalSummary.postGapFillRemovalRate } else { 0.0 }) - (if ($runA.FinalSummary.ContainsKey('postGapFillRemovalRate')) { [double]$runA.FinalSummary.postGapFillRemovalRate } else { 0.0 })
$postGapFillProtectedRateDelta = (if ($runB.FinalSummary.ContainsKey('postGapFillProtectedRate')) { [double]$runB.FinalSummary.postGapFillProtectedRate } else { 0.0 }) - (if ($runA.FinalSummary.ContainsKey('postGapFillProtectedRate')) { [double]$runA.FinalSummary.postGapFillProtectedRate } else { 0.0 })
$preSmoothCutPairsDelta = $runB.SceneCutControl.preSmoothCutPairs - $runA.SceneCutControl.preSmoothCutPairs
$preSmoothStrongPairsDelta = $runB.SceneCutControl.preSmoothStrongPairs - $runA.SceneCutControl.preSmoothStrongPairs
$postSmoothCutPairsDelta = $runB.SceneCutControl.postSmoothCutPairs - $runA.SceneCutControl.postSmoothCutPairs
$postSmoothStrongPairsDelta = $runB.SceneCutControl.postSmoothStrongPairs - $runA.SceneCutControl.postSmoothStrongPairs
$finalCarryCutPairsDelta = $runB.SceneCutControl.finalCutPairs - $runA.SceneCutControl.finalCutPairs
$preSmoothCutWindowsDelta = $runB.SceneCutControl.preSmoothCutWindows - $runA.SceneCutControl.preSmoothCutWindows
$preSmoothStrongWindowsDelta = $runB.SceneCutControl.preSmoothStrongWindows - $runA.SceneCutControl.preSmoothStrongWindows
$postSmoothCutWindowsDelta = $runB.SceneCutControl.postSmoothCutWindows - $runA.SceneCutControl.postSmoothCutWindows
$postSmoothStrongWindowsDelta = $runB.SceneCutControl.postSmoothStrongWindows - $runA.SceneCutControl.postSmoothStrongWindows
$finalCarryWindowsDelta = $runB.SceneCutControl.finalCutWindows - $runA.SceneCutControl.finalCutWindows
$finalPreWindowDelta =
    ($runB.SceneCutControl.finalPreCutWindows + $runB.SceneCutControl.finalPreStrongWindows) -
    ($runA.SceneCutControl.finalPreCutWindows + $runA.SceneCutControl.finalPreStrongWindows)
$finalPostWindowDelta =
    ($runB.SceneCutControl.finalPostCutWindows + $runB.SceneCutControl.finalPostStrongWindows) -
    ($runA.SceneCutControl.finalPostCutWindows + $runA.SceneCutControl.finalPostStrongWindows)
$finalPostGapFillCutPairsDelta = $runB.SceneCutControl.postGapFillCarryPairs - $runA.SceneCutControl.postGapFillCarryPairs
$finalPostGapFillWindowsDelta =
    $runB.SceneCutControl.postGapFillCarryWindows - $runA.SceneCutControl.postGapFillCarryWindows
$runAHybridWindowShortfall = if ($runA.Export.ContainsKey('hybridWindowFrameShortfall')) { [int]$runA.Export.hybridWindowFrameShortfall } else { 0 }
$runBHybridWindowShortfall = if ($runB.Export.ContainsKey('hybridWindowFrameShortfall')) { [int]$runB.Export.hybridWindowFrameShortfall } else { 0 }
$runASampleWindowShortfall = if ($runA.Export.ContainsKey('sampleWindowFrameShortfall')) { [int]$runA.Export.sampleWindowFrameShortfall } else { 0 }
$runBSampleWindowShortfall = if ($runB.Export.ContainsKey('sampleWindowFrameShortfall')) { [int]$runB.Export.sampleWindowFrameShortfall } else { 0 }

Write-Host ("오탐 후보 proxy: A={0} B={1} Δ={2}" -f $aWeakScore, $bWeakScore, $weakDelta)
Write-Host ("미탐 보완 proxy (track lost-filled): A={0} B={1} Δ={2}" -f $aTrackFilled, $bTrackFilled, $trackDelta)
Write-Host ("미탐 보완 proxy (interpolated): A={0} B={1} Δ={2}" -f $aInterpolated, $bInterpolated, $interpDelta)
Write-Host ("장면전환 carry reset 제거량: A={0} B={1} Δ={2}" -f $runA.SceneCutReset.removed, $runB.SceneCutReset.removed, $sceneCutDelta)
Write-Host ("단기 미탐 갭 보정 횟수: A={0} B={1} Δ={2}" -f $(if ($runA.FinalSummary.ContainsKey('shortGaps')) { $runA.FinalSummary.shortGaps } else { 0 }), $(if ($runB.FinalSummary.ContainsKey('shortGaps')) { $runB.FinalSummary.shortGaps } else { 0 }), $shortGapDelta)
Write-Host ("큰 점프 갭 보정 횟수: A={0} B={1} Δ={2}" -f $(if ($runA.FinalSummary.ContainsKey('largeJumpGaps')) { $runA.FinalSummary.largeJumpGaps } else { 0 }), $(if ($runB.FinalSummary.ContainsKey('largeJumpGaps')) { $runB.FinalSummary.largeJumpGaps } else { 0 }), $largeJumpGapDelta)
Write-Host ("컷 전환 post-gap-fill carry pairs: A={0} B={1} Δ={2}" -f $(if ($runA.FinalSummary.ContainsKey('postGapFillCarryPairs')) { $runA.FinalSummary.postGapFillCarryPairs } else { 0 }), $(if ($runB.FinalSummary.ContainsKey('postGapFillCarryPairs')) { $runB.FinalSummary.postGapFillCarryPairs } else { 0 }), $postGapFillCarryPairDelta)
Write-Host ("컷 전환 post-gap-fill carry removed: A={0} B={1} Δ={2}" -f $(if ($runA.FinalSummary.ContainsKey('postGapFillRemoved')) { $runA.FinalSummary.postGapFillRemoved } else { 0 }), $(if ($runB.FinalSummary.ContainsKey('postGapFillRemoved')) { $runB.FinalSummary.postGapFillRemoved } else { 0 }), $postGapFillRemovedDelta)
Write-Host ("컷 전환 post-gap-fill carry protected: A={0} B={1} Δ={2}" -f $(if ($runA.FinalSummary.ContainsKey('postGapFillProtected')) { $runA.FinalSummary.postGapFillProtected } else { 0 }), $(if ($runB.FinalSummary.ContainsKey('postGapFillProtected')) { $runB.FinalSummary.postGapFillProtected } else { 0 }), $postGapFillProtectedDelta)
Write-Host ("컷 전환 post-gap-fill carry 제거율: A={0:P1} B={1:P1} Δ={2:P1}" -f $(if ($runA.FinalSummary.ContainsKey('postGapFillRemovalRate')) { [double]$runA.FinalSummary.postGapFillRemovalRate } else { 0.0 }, $(if ($runB.FinalSummary.ContainsKey('postGapFillRemovalRate')) { [double]$runB.FinalSummary.postGapFillRemovalRate } else { 0.0 }), $postGapFillRemovalRateDelta)
Write-Host ("컷 전환 post-gap-fill carry 보존율: A={0:P1} B={1:P1} Δ={2:P1}" -f $(if ($runA.FinalSummary.ContainsKey('postGapFillProtectedRate')) { [double]$runA.FinalSummary.postGapFillProtectedRate } else { 0.0 }, $(if ($runB.FinalSummary.ContainsKey('postGapFillProtectedRate')) { [double]$runB.FinalSummary.postGapFillProtectedRate } else { 0.0 }), $postGapFillProtectedRateDelta)
Write-Host ("장면전환 pre-smooth 큐브: pre컷 {0}->{1} (Δ {2}), strong컷 {3}->{4} (Δ {5})" -f $runA.SceneCutControl.preSmoothCutPairs, $runB.SceneCutControl.preSmoothCutPairs, $preSmoothCutPairsDelta, $runA.SceneCutControl.preSmoothStrongPairs, $runB.SceneCutControl.preSmoothStrongPairs, $preSmoothStrongPairsDelta)
Write-Host ("장면전환 post-smooth 큐브: pre컷 {0}->{1} (Δ {2}), strong컷 {3}->{4} (Δ {5})" -f $runA.SceneCutControl.postSmoothCutPairs, $runB.SceneCutControl.postSmoothCutPairs, $postSmoothCutPairsDelta, $runA.SceneCutControl.postSmoothStrongPairs, $runB.SceneCutControl.postSmoothStrongPairs, $postSmoothStrongPairsDelta)
Write-Host ("장면전환 final carry 컷: A={0} B={1} Δ={2}" -f $runA.SceneCutControl.finalCutPairs, $runB.SceneCutControl.finalCutPairs, $finalCarryCutPairsDelta)
Write-Host ("장면전환 final carry 윈도우: A={0} B={1} Δ={2}" -f $runA.SceneCutControl.finalCutWindows, $runB.SceneCutControl.finalCutWindows, $finalCarryWindowsDelta)
Write-Host ("장면전환 pre-smooth 윈도우: A={0}->{1} (Δ {2}), strong pre-smooth 윈도우: A={3}->{4} (Δ {5})" -f $runA.SceneCutControl.preSmoothCutWindows, $runB.SceneCutControl.preSmoothCutWindows, $preSmoothCutWindowsDelta, $runA.SceneCutControl.preSmoothStrongWindows, $runB.SceneCutControl.preSmoothStrongWindows, $preSmoothStrongWindowsDelta)
Write-Host ("장면전환 post-smooth 윈도우: A={0}->{1} (Δ {2}), strong post-smooth 윈도우: A={3}->{4} (Δ {5})" -f $runA.SceneCutControl.postSmoothCutWindows, $runB.SceneCutControl.postSmoothCutWindows, $postSmoothCutWindowsDelta, $runA.SceneCutControl.postSmoothStrongWindows, $runB.SceneCutControl.postSmoothStrongWindows, $postSmoothStrongWindowsDelta)
Write-Host ("장면전환 final pre/post 윈도우: A={0}->{1} (Δ {2}) / A={3}->{4} (Δ {5})" -f 
    ($runA.SceneCutControl.finalPreCutWindows + $runA.SceneCutControl.finalPreStrongWindows),
    ($runB.SceneCutControl.finalPreCutWindows + $runB.SceneCutControl.finalPreStrongWindows),
    $finalPreWindowDelta,
    ($runA.SceneCutControl.finalPostCutWindows + $runA.SceneCutControl.finalPostStrongWindows),
    ($runB.SceneCutControl.finalPostCutWindows + $runB.SceneCutControl.finalPostStrongWindows),
    $finalPostWindowDelta)
Write-Host ("장면전환 post-gap-fill carry 컷: A={0} B={1} Δ={2}" -f $runA.SceneCutControl.postGapFillCarryPairs, $runB.SceneCutControl.postGapFillCarryPairs, $finalPostGapFillCutPairsDelta)
Write-Host ("장면전환 post-gap-fill carry 윈도우: A={0} B={1} Δ={2}" -f $runA.SceneCutControl.postGapFillCarryWindows, $runB.SceneCutControl.postGapFillCarryWindows, $finalPostGapFillWindowsDelta)
Write-Host ("하이브리드 윈도우 누락: A={0} B={1} Δ={2}" -f $runAHybridWindowShortfall, $runBHybridWindowShortfall, ($runBHybridWindowShortfall - $runAHybridWindowShortfall))
Write-Host ("샘플 구간 누락: A={0} B={1} Δ={2}" -f $runASampleWindowShortfall, $runBSampleWindowShortfall, ($runBSampleWindowShortfall - $runASampleWindowShortfall))
Write-Host ("익스포트 시간: A={0} B={1} Δ={2}" -f $aExport, $bExport, $exportDeltaForHint)

$runMsDelta = if (($runA.RunSummary.ContainsKey('totalMs')) -and ($runB.RunSummary.ContainsKey('totalMs'))) {
    [int]($runB.RunSummary.totalMs - $runA.RunSummary.totalMs)
} else {
    $null
}
$missRecoveryProxyA = $aTrackFilled + $aInterpolated
$missRecoveryProxyB = $bTrackFilled + $bInterpolated
$missRecoveryDelta = $missRecoveryProxyB - $missRecoveryProxyA
$aPostGapFillRemovalRate = if ($runA.FinalSummary.ContainsKey('postGapFillRemovalRate')) { [double]$runA.FinalSummary.postGapFillRemovalRate } else { 0.0 }
$bPostGapFillRemovalRate = if ($runB.FinalSummary.ContainsKey('postGapFillRemovalRate')) { [double]$runB.FinalSummary.postGapFillRemovalRate } else { 0.0 }
$postGapFillRemovalRateDelta = $bPostGapFillRemovalRate - $aPostGapFillRemovalRate
$bReview = if ($runB.FinalSummary.ContainsKey('reviewRequired')) { [bool]$runB.FinalSummary.reviewRequired } else { $false }

$passWeakScore = $weakDelta -le $MaxWeakScoreDelta
$passMissRecovery = $missRecoveryDelta -ge $MinMissRecoveryDelta
$passCutCarry = $postGapFillRemovalRateDelta -ge $MinPostGapFillRemovalRateDelta
$passRunSpeed = if ($null -eq $runMsDelta) { $true } else { $runMsDelta -le $MaxRunTotalMsDelta }
$passExportSpeed = if ($null -eq $exportDeltaForHint) { $true } else { $exportDeltaForHint -le $MaxExportTotalMsDelta }
$passReview = if ($AllowReviewRequired.IsPresent) { $true } else { -not $bReview }
$passOverall = $passWeakScore -and $passMissRecovery -and $passCutCarry -and $passRunSpeed -and $passExportSpeed -and $passReview
$passLabel = if ($passOverall) { "PASS" } else { "REVIEW" }
$reviewText = if ($passReview) { "PASS" } else { "REVIEW_REQUIRED" }

$passReasons = [System.Collections.Generic.List[string]]::new()
if (-not $passWeakScore) { $passReasons.Add("weakScoreΔ=$weakDelta > $MaxWeakScoreDelta") }
if (-not $passMissRecovery) { $passReasons.Add("missRecoveryΔ=$missRecoveryDelta < $MinMissRecoveryDelta") }
if (-not $passCutCarry) { $passReasons.Add("postGapFillRemovalRateΔ=$('{0:P2}' -f $postGapFillRemovalRateDelta) < $('{0:P2}' -f $MinPostGapFillRemovalRateDelta)") }
if (-not $passRunSpeed) { $passReasons.Add("runMsΔ=$runMsDelta > $MaxRunTotalMsDelta") }
if (-not $passExportSpeed) { $passReasons.Add("exportMsΔ=$exportDeltaForHint > $MaxExportTotalMsDelta") }
if (-not $passReview) { $passReasons.Add("reviewRequired=true") }

Write-Host ""
Write-Host "== 운영형 판단 (30초/문제구간 기준) =="
Write-Host ("결론: {0}" -f $passLabel)
Write-Host ("오탐 proxy: A={0}, B={1}, Δ={2}, 기준≤{3}" -f $aWeakScore, $bWeakScore, $weakDelta, $MaxWeakScoreDelta)
Write-Host ("미탐 보완 proxy: A={0}, B={1}, Δ={2}, 기준≥{3}" -f $missRecoveryProxyA, $missRecoveryProxyB, $missRecoveryDelta, $MinMissRecoveryDelta)
Write-Host ("컷캐리 제거율: A={0:P2}, B={1:P2}, Δ={2:P2}, 기준≥{3:P2}" -f $aPostGapFillRemovalRate, $bPostGapFillRemovalRate, $postGapFillRemovalRateDelta, $MinPostGapFillRemovalRateDelta)
Write-Host ("속도(run/export): runΔ={0}, 기준≤{1}, exportΔ={2}, 기준≤{3}" -f (if ($null -ne $runMsDelta) { $runMsDelta } else { "n/a" }), $MaxRunTotalMsDelta, $exportDeltaForHint, $MaxExportTotalMsDelta)
Write-Host ("심사 플래그: reviewRequired=$bReview -> {0}" -f $reviewText)
Write-Host ("판정 사유: {0}" -f ($(if ($passReasons.Count -eq 0) { "pass" } else { $passReasons -join '; ' })))

$quickDecision = [pscustomobject]@{
    Passed = $passOverall;
    WeakFaceDelta = $weakDelta;
    MissRecoveryProxyA = $missRecoveryProxyA;
    MissRecoveryProxyB = $missRecoveryProxyB;
    MissRecoveryDelta = $missRecoveryDelta;
    PostGapFillRemovalRateA = $aPostGapFillRemovalRate;
    PostGapFillRemovalRateB = $bPostGapFillRemovalRate;
    PostGapFillRemovalRateDelta = $postGapFillRemovalRateDelta;
    RunTotalMsDelta = $runMsDelta;
    ExportTotalMsDelta = $exportDeltaForHint;
    RunTotalMsGate = $MaxRunTotalMsDelta;
    ExportTotalMsGate = $MaxExportTotalMsDelta;
    WeakScoreGate = $MaxWeakScoreDelta;
    MissRecoveryGate = $MinMissRecoveryDelta;
    PostGapFillRemovalRateGate = $MinPostGapFillRemovalRateDelta;
    PassWeakScore = $passWeakScore;
    PassMissRecovery = $passMissRecovery;
    PassCutCarry = $passCutCarry;
    PassRunSpeed = $passRunSpeed;
    PassExportSpeed = $passExportSpeed;
    PassReview = $passReview;
    ReviewRequired = $bReview;
    Reasons = if ($passReasons.Count -eq 0) { "pass" } else { $passReasons };
}

if ($JsonOutput.IsPresent) {
    [pscustomobject]@{
        RunA = $runA;
        RunB = $runB;
        Phases = $allPhases;
        QuickDecision = $quickDecision;
    } | ConvertTo-Json -Depth 8
    exit 0
}
