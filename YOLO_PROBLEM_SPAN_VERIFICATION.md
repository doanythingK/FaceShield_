# YOLO Problem-Span Verification

이 문서는 YOLO 자동 모자이크의 남은 문제를 전체 영상이 아니라 짧은 문제 구간으로 확인하기 위한 절차다.

## 목적

- 깜박임: 한 번 잡힌 얼굴이 짧은 detector miss 때문에 빠지는지 확인한다.
- 화면전환 잔상: 컷 이후 이전 장면의 모자이크가 남는지 확인한다.
- 오탐: 얼굴이 아닌 영역의 후보를 `face`/`nonface`/`miss` 기준으로 분리한다.
- FaceONNX 기본 경로는 별도 회귀 게이트로만 확인하고, YOLO 문제 구간 검증과 섞지 않는다.

## 짧은 구간 생성 및 실행

긴 원본 영상을 그대로 smoke로 돌리지 않는다. 문제 구간 시작 시각과 길이를 정해서 30초 이하로 자른다.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./scripts/run-yolo-problem-span-verification.ps1 `
  -Source "srcTest/260102_jp_10.mp4" `
  -TrimStart "00:09:00" `
  -TrimSeconds 2 `
  -OutputDir ".tmp/yolo-problem-span-0900" `
  -YoloModelType Yolo5Face `
  -YoloInputSize 640 `
  -YoloObjectnessThreshold 0.12 `
  -YoloConfidenceThreshold 0.18 `
  -YoloNmsThreshold 0.45 `
  -ParallelDetectorCount 2 `
  -MaxFullFrameRows 24
```

`-TrimStart`와 `-TrimSeconds`는 실제 문제 구간에 맞게 바꾼다. `-AllowLongSmokeSource`는 사용하지 않는다.
기본 실행은 review package 생성을 생략한다. crop/full-frame HTML 검토가 필요하면 `-WithReviewPackage`를 추가한다.

## 산출물

`-OutputDir` 아래에서 다음 파일을 확인한다.

- `yolo-followup-quality-evidence.md`: 실행 summary, scene-cut 제거 결과, 최종 mask summary
- `yolo-mask-continuity-report.md`: 짧은 gap, isolated mask, low-confidence mask 목록
- `yolo-quality-review-checklist.md`: 깜박임/잔상/오탐 review frame 목록
- `yolo-quality-full-gt-template.csv`: 필요 시 수동 `face`/`nonface`/`miss` 라벨 입력용

review package가 필요하면 `scripts/run-yolo-problem-span-verification.ps1`에 `-WithReviewPackage`를 붙여 다시 실행한다. 그러면 `review-package/review-index.html`에서 crop/full-frame overlay를 확인한다.
`SmokeDetection` 로그의 `conf`는 threshold 경계 후보가 반올림 때문에 잘못 분류되지 않도록 6자리 정밀도로 기록한다.

부분 시각 확인은 참고 증거로만 취급한다. 일부 overlay를 확인해서 후보가 실제 얼굴인지 설명할 수는 있지만, 전체 오탐/미탐 게이트를 닫으려면 `review-package/full-gt-review.csv`와 `review-package/full-frame-review.csv`에 crop/full-frame row가 채워져 있어야 한다. 특히 edge 또는 top-edge weak 후보는 보호해야 할 부분 얼굴일 수 있으므로, 실제 얼굴을 덮지 않는다는 시각 근거 없이 자동 오탐으로 단정하지 않는다.

## 판정 기준

### 깜박임

통과 근거:

- `Final mask summary` 또는 `SmokeFinalMaskSummary`에서 `shortGaps=0`
- `isolated=0`
- `reviewRequired=True`이면 `reviewReasons`에서 `short-gap`, `large-jump-gap`, `isolated-mask`가 있는지 먼저 확인한다. 이 값은 통과/실패 단정이 아니라 어느 frame group을 먼저 봐야 하는지 알려주는 triage 근거다.
- cleanup이 제거한 얼굴이 다시 채워지지 않았다는 근거가 필요하면 `Final mask gap fill`에서 `blockedByCleanup=...`, `cleanupBlockedFrames=...`를 확인한다. 현재 YOLO 경로는 제거된 얼굴 위치를 per-face block으로 넘기므로, 같은 프레임의 unrelated face gap은 채워질 수 있다. 따라서 `frames=none`은 전체 gap 미보정 근거이고, `blockedByCleanup`은 같은 제거 얼굴을 재생성하지 않았다는 근거다.
- `lostFrames`가 있더라도 해당 full-frame overlay에서 대상 얼굴이 계속 덮여 있음

실패 근거:

- 실제 얼굴이 있는 구간에서 `shortGaps`가 남음
- review overlay에서 얼굴이 1-2프레임 빠짐
- gap fill 이후 scene-cut guard가 정상 얼굴 구간을 잘못 지움
- cleanup으로 지운 프레임이 후속 gap fill에서 같은 프레임에 다시 생성됨

### 화면전환 잔상

통과 근거:

- `Scene-cut guard`에 `cutPairs=...`와 `removedFrames=...`가 남음
- 후보가 많은 구간에서도 `directChecked=...`가 0으로 고정되지 않는다. `directSkipped=...`가 있으면 직접 source->target 비교가 예산 한도까지 수행되고 나머지만 생략된 것이다.
- 컷 직후 같은 위치의 약한 tail이 함께 제거됨
- 컷 이후 final gap fill이 `blockedByCut=...`/`cutBlockedFrames=...` 또는 `blockedBySceneCarry=...`/`sceneCarryBlockedFrames=...`를 남기면, 후속 anti-flicker fill이 확인된 전환 구간의 잔상을 다시 만들지 않았다는 근거로 본다. `blockedByCleanup=...`/`cleanupBlockedFrames=...`는 cleanup이 제거한 같은 얼굴 위치를 재생성하지 않았다는 근거다.
- `reviewReasons`에 `large-jump-gap`이 있으면 화면전환 또는 box jump 후보를 우선 확인한다.
- review overlay에서 다음 장면에 이전 장면의 모자이크가 남지 않음

실패 근거:

- `maxDiff`가 threshold 이상인데 `removedFrames=none`
- 컷 직후 1-5프레임 동안 같은 위치의 약한 box가 남음
- `removedFrames` 이후 final cleanup/gap fill이 같은 위치의 mask를 다시 만든 흔적이 있음. 같은 프레임의 다른 얼굴 보정은 실패 근거로 보지 않고 crop/full-frame overlay에서 위치가 겹치는지 확인한다.

### 오탐

자동 오탐으로 단정하지 않는다. 다음 기준으로 라벨링한다.

- `face`: 후보 box가 실제 보이는 얼굴 또는 보호해야 할 부분 얼굴을 덮음
- `nonface`: 배경, 물체, 자막, 몸통 등 얼굴이 아닌 영역
- `miss`: 보이는 얼굴이 후보 box에 덮이지 않음. CSV에 수동 row를 추가한다.

자동 삭제 후보로 볼 수 있는 근거:

- `weak unsupported`
- `very-low-confidence short cluster`
- `tiny isolated non-edge`
- scene-cut 이후 같은 위치의 weak tail
- `reviewReasons`의 `weak-non-edge`, `upper-weak`, `lower-weak`, `aspect-outlier`, `tiny-weak`, `tiny-short`

삭제하지 말고 review 대상으로 남겨야 하는 근거:

- edge에 닿은 부분 얼굴
- 3프레임 이상 안정적으로 이어지는 작은 후보
- confidence가 낮아도 주변 frame에 strong continuation이 있는 후보

## 회귀 확인

YOLO 문제 구간 수정 후에는 다음을 실행한다.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./scripts/verify-auto-mosaic-default.ps1
dotnet build FaceShield.sln
```

`verify-auto-mosaic-default.ps1`는 FaceONNX/default 회귀와 YOLO 후처리 verifier를 같이 확인한다. FFmpeg hardware decode setup warning은 fallback 후 검증이 통과하면 실패 근거로 보지 않는다.

## 완료 조건

goal 완료로 볼 수 있는 최소 증거:

- 문제 구간 30초 이하 clip 또는 기존 run log 기반 evidence bundle이 있음
- 깜박임, 화면전환 잔상, 오탐 후보가 각각 checklist/review artifact에서 확인됨
- `dotnet build FaceShield.sln` 통과
- FaceONNX/default regression gate 통과
- 문서에 최종 evidence path와 남은 risk가 기록됨

현재 상태에서는 기존 짧은 샘플 evidence와 verifier는 통과했지만, 사용자가 실제로 본 문제 구간의 visual confirmation은 아직 별도 증거가 필요하다.

## Resume/Retry State Guard

사용자 debug output에는 export cancel 이후 `processed=1`인 재개 실행이 기존 mask state 전체에 대해 track postprocess를 다시 수행한 흔적이 있었다. 이 경우 재개 지점 이후에 남아 있던 예전 YOLO face-rect mask가 새 detector 결과를 가로막고, 이전 실행의 화면전환 잔상 또는 오탐이 다시 export될 수 있다.

현재 GUI 자동 감지 경로는 detector를 시작하기 전에 다음을 보장한다.

- fresh run: 기존 auto face-rect mask 전체를 지운다.
- resume run: resume frame 이후의 stale auto face-rect mask를 먼저 지운다.
- manual bitmap mask는 별도 저장 경로라서 이 stale face-rect reset 대상이 아니다.
- stale reset이 발생하면 `[AutoMaskResumeReset] start=... removedStaleFaceMasks=...`가 debug output에 남는다.

검증 스크립트: `scripts/verify-auto-resume-mask-reset.ps1`

## Current Focused Evidence

- 2026-05-27 current HEAD short-span run: `.tmp/yolo-goal-current-perface-0900/yolo-followup-quality-evidence.md`
- 2026-05-27 current HEAD review-package run: `.tmp/yolo-goal-current-perface-0900-review/yolo-followup-quality-evidence.md`
- Source span: `srcTest/260102_jp_10.mp4`, `TrimStart=00:09:00`, `TrimSeconds=2`
- Detector/provider: `YoloFaceOnnxDetector/GPU:DirectML`
- Processed frames: `60`
- Final rows: `96`
- Review package: `.tmp/yolo-goal-current-perface-0900-review/review-package/review-index.html`
- Review rows: `96` crop rows, `32` full-frame rows
- Required full-frame review frames: `4,6,7,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,24,25,26,32`
- Flicker/carry triage: `shortGaps=0`, `largeJumpGaps=0`, `isolated=0`, `protectedSceneCarry=0`, `reviewRequired=False`, `reviewReasons=none`
- Cleanup evidence: `removedUpperWeakClusters=3`, `removedFrames=33,34,35`
- Scene-cut evidence: `directChecked=74`, `directSkipped=0`, `cutPairs=none`, `removedFrames=none`
- False-positive triage: `weakNonEdge=0`, `upperWeak=0`, `lowerWeak=0`, `aspectBad=0`, `tinyWeak=0`, `tinyShort=0`; residual `edgeWeak/topEdgeWeak` frames are `4,6,7,17,20,24,25,32` and remain visual review targets rather than automatic non-face labels.
- Partial overlay observation: frames `4`, `24`, and `32` show the residual edge/top-edge boxes covering a visible partial background face and/or the foreground face. This is reference evidence only; the crop/full-frame CSV rows are still unreviewed GT rows.

## Wrapper Smoke

`scripts/run-yolo-problem-span-verification.ps1` 자체는 `.tmp/srcTest-smoke/smoke-0900-2s.mp4`의 2초 구간으로 확인했다.

- Output: `.tmp/yolo-problem-span-wrapper-smoke/yolo-followup-quality-evidence.md`
- Detection rows: `96`
- Scene-cut evidence on this no-hard-cut wrapper sample after the direct-check budget/priority update: `directChecked=74`, `directSkipped=0`, `maxDiff=0.206`, `cutPairs=none`, `removedFrames=none`, `threshold=0.150`. This confirms dense spans still get bounded direct source->target checks, while the lower direct threshold and higher budget do not delete this no-hard-cut sample.
- Post-scene final gap-fill now also carries cut pairs found by the initial final gap-fill scene-cut guard, so a gap-fill that was removed across a transition cannot be re-created by the later post-scene gap-fill pass.
- Final gap-fill now rejects weak geometry-risk anchors (`edge`, `tiny`, `upper/lower weak`, aspect outlier) while keeping strong edge anchors eligible. This reduces YOLO false positives from becoming temporal gap-fill seeds without disabling high-confidence edge faces.
- Post-scene cleanup now also purges residual YOLO carry masks after known cut pairs when the post-cut box still matches the pre-cut position and remains at or below `0.98` confidence. For adjacent cut pairs it scans the normal 5-frame carry purge window, then continues through an 8-frame weak-carry window for matching boxes at or below `0.78` confidence. The later anti-flicker gap-fill block window is also 8 frames, so a transition residue removed or flagged near a cut is not recreated a few frames later. For direct source-to-target cut pairs it starts at `source+1` and continues through the confirmed target plus the carry window, so transition frames between `source` and `target` cannot keep an old blur. This is implemented in `YoloFinalMaskPostProcessor.RemoveSceneCutCarryRemnants` and shared by the GUI path and smoke harness. Removed carry frames and the extended carry block window are passed into the final gap-fill blocked-frame list, so the anti-flicker pass cannot recreate the same scene-transition residue.
- Deterministic verifier evidence: `scripts/verify-yolo-final-mask-cleanup.ps1` now includes `sceneCutCarryRemoved=17`, `sceneCutCarryFrames=1001,1002,1003,1004,1005,1006,1007,1008,2001,2002,2003,2004,2005,2006,2007,2008,2009`, extended `sceneCutCarryBlockedFrames=1001,1002,1003,1004,1005,1006,1007,1008,2001,2002,2003,2004,2005,2006,2007,2008,2009,2010,2011`, `emptyPostCutRemovedUnsupportedStrong=2`, and `partialSceneCarryRefillBlocked=1`.
- Final cleanup evidence: `removedUpperWeakClusters=3`, `removedFrames=33,34,35`
- Final gap-fill evidence: `filled=0`, `frames=none`, `blockedByCleanup=3`, `cleanupBlockedFrames=33,34,35`
- Final mask summary: `shortGaps=0`, `largeJumpGaps=0`, `isolated=0`, `lowConf=7`, `weakNonEdgeFrames=none`, `upperWeakFrames=none`

## 2026-05-27 Top-Edge Fastcheck

Current short evidence after the top-edge cleanup/logging pass:

- Output: `.tmp/yolo-followup-current-topedge-fastcheck/yolo-followup-quality-evidence.md`
- Review package: `.tmp/yolo-followup-current-topedge-fastcheck/review-package/review-index.html`
- Detection rows: `96`
- Cleanup evidence: `removedTopEdgeWeakClusters=0`, `removedUpperWeakClusters=3`, `removedFrames=33,34,35`
- Final post-scene gap-fill evidence: `filled=0`, `frames=none`, `blockedByCleanup=3`, `cleanupBlockedFrames=33,34,35`
- Final mask summary: `shortGaps=0`, `largeJumpGaps=0`, `isolated=0`, `weakNonEdge=0`, `upperWeak=0`, `lowerWeak=0`, `aspectBad=0`, `tinyWeak=0`, `tinyShort=0`
- Remaining review targets: `lowConf=7`, `edgeWeak=8`, `topEdgeWeak=8`, frames `4,6,7,17,20,24,25,32`
- Visual overlay review:
  - Frames `4,6,7`: the top-edge weak box covers a visible partial background face.
  - Frames `17,20,24,25,32`: the large foreground face is covered, and the top-edge weak box covers the same visible partial background face.

These remaining edge/top-edge weak candidates are not automatically false positives. In this 2-second sample they are visible face candidates, so tightening the default cleanup further would risk removing protectable partial faces. The user-reported problem span still needs its own focused visual confirmation before the follow-up goal can be marked complete.
