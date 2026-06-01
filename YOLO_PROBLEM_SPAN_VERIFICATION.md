# YOLO Problem-Span Verification

이 문서는 YOLO 자동 모자이크의 남은 문제를 전체 영상이 아니라 짧은 문제 구간으로 확인하기 위한 절차다.

## 목적

- 깜박임: 한 번 잡힌 얼굴이 짧은 detector miss 때문에 빠지는지 확인한다.
- 화면전환 잔상: 컷 이후 이전 장면의 모자이크가 남는지 확인한다.
- 오탐: 얼굴이 아닌 영역의 후보를 `face`/`nonface`/`miss` 기준으로 분리한다.
- FaceONNX 기본 경로는 별도 회귀 게이트로만 확인하고, YOLO 문제 구간 검증과 섞지 않는다.

## Current Quality Goal

현재 목표는 YOLO 자동 모자이크에서 남은 깜박임, 화면전환 잔상, 오탐/미탐 의심을 짧은 문제 구간 기준으로 더 정확히 검증하고 줄이는 것이다.

- 앱 기본 런타임 경로와 별도로, 테스트/고도화 전용 high-precision pseudo-GT 검출 파이프라인을 둔다.
- 기본 YOLO 런타임 경로는 빠른 자동 모자이크용으로 유지하고, pseudo-GT 경로는 느려도 되는 검증 보조 단계로만 사용한다.
- 문제 구간 검증은 전체 원본 영상이 아니라 30초 이하 clip, frame dump, 기존 run log/evidence bundle을 기준으로 진행한다.
- 작은 얼굴 미탐 검증을 위해 frame을 tile/overlap으로 나누고, tile을 확대해 고정밀 face 검출을 수행한다.
- 테스트/고도화 단계에서는 기본 YOLO 후보를 고품질 face verification/face detection 모델로 재검증한다.
- 고품질 검증 모델은 앱 기본 런타임에 포함하지 않고, 짧은 문제 구간의 evidence 생성과 오탐/미탐 후보 분류에만 사용한다.
- 필요하면 무거운 face/person/object 모델을 로컬 경로로 받아 보조 검증에 사용한다. 모델 파일은 커밋하지 않는다.
- 기본 YOLO 결과와 고정밀 tile 검출/face verification 결과를 비교해 `missCandidate`, `falsePositiveCandidate`, `supportedFaceCandidate`를 기록한다.
- YOLO 후보와 고품질 검증 결과의 IoU, 중심 거리, 면적 변화율, 반복 support를 비교해 후보 유형을 나눈다.
- person/object 검출은 얼굴 정답으로 단정하지 않고, 오탐/미탐 후보 우선순위를 높이는 보조 신호로만 사용한다.
- 후보별 `baseFaceConfidence`, `tileFaceConfidence`, `tileSupportCount`, `faceVerificationConfidence`, `faceVerificationDistance`, `personConfidence`, `personUpperOverlap`, `personObjectClass`, `auxiliarySignalRole`, `supportFrameCount`, `supportRowCount`, `bestIou`, `centerDistanceRatio`, `areaChangeRatio`, `fpProbability`, `missProbability`, `pseudoGtReason`을 evidence log/CSV에 남긴다.
- 최종 `face`/`nonface`/`miss` 확정은 review CSV 라벨로 닫는다.
- 기존 FaceONNX 기본 경로와 앱 기본 YOLO 런타임 성능 경로는 회귀시키지 않는다.

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
검출 박스가 짧은 clip 위에서 시간 순서대로 어떻게 이어지는지 확인하려면 `-WithDetectionOverlayVideo`를 추가한다. 이 경우 `yolo-detection-overlay.mp4`가 함께 생성된다.
review frame을 한 장 이미지로 빠르게 훑어보려면 `-WithReviewContactSheet`를 추가한다. 이 옵션은 `yolo-detection-overlay.mp4`와 `yolo-review-contact-sheet.png`를 함께 생성한다.
고품질 검증용 tile manifest까지 같은 run에서 만들려면 `-WithPseudoGtTileInput`을 추가한다. 이미지만 바로 만들지 않고 manifest만 확인하려면 `-PseudoGtTileSkipImageExtraction`을 함께 쓴다.
기본 YOLO 후보 박스를 고품질 face verification 모델로 재검증할 crop manifest까지 같은 run에서 만들려면 `-WithPseudoGtFaceVerificationInput`을 추가한다. 이미지만 바로 만들지 않고 manifest만 확인하려면 `-PseudoGtFaceVerificationSkipImageExtraction`을 함께 쓴다.
사람/사물 보조 검증용 full-frame manifest까지 같은 run에서 만들려면 `-WithPseudoGtPersonObjectInput`을 추가한다. 이미지만 바로 만들지 않고 manifest만 확인하려면 `-PseudoGtPersonObjectSkipImageExtraction`을 함께 쓴다.
test-only manifest 스크립트도 기본적으로 큰 frame set을 막는다. 독립 실행 시 `MaxFrames=900`을 넘는 `-Frames`/base prediction frame set은 거부되며, 이는 30fps 기준 30초 문제 구간 상한에 맞춘 안전장치다. `-AllowLargeFrameSet`은 명시적 로컬 감사 목적 외에는 사용하지 않는다.
problem-span runner와 follow-up evidence wrapper는 이 상한을 `-PseudoGtMaxFrames`로 manifest 생성 단계에 전달한다. 기본값은 `900`이며, 일반 검증에서는 이 값을 늘리지 않는다.

고품질 검증 모델을 별도 로컬 runner로 실행했다면, 그 산출 CSV를 problem-span runner에 붙여 test-only pseudo-GT evidence를 만들 수 있다. 이 CSV들은 기본 앱 런타임 입력이 아니며, review 후보 우선순위용 증거로만 사용한다.

먼저 `new-yolo-pseudo-gt-tile-input.ps1`로 짧은 clip의 review frame을 tile/overlap manifest로 만들고, 필요하면 로컬 외부 모델 runner를 호출한다. 모델 파일과 runner 출력은 `.tmp` 또는 로컬 경로에 두며 커밋하지 않는다.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./scripts/new-yolo-pseudo-gt-tile-input.ps1 `
  -VideoPath ".tmp/yolo-problem-span-0900/followup-00-09-00-2s.mp4" `
  -Frames "4,6,7,17,20,24,25,32" `
  -OutputDir ".tmp/local-heavy-model/tile-input" `
  -TileColumns 3 `
  -TileRows 3 `
  -TileOverlapRatio 0.25 `
  -TileScale 2.0 `
  -ExternalCommand "powershell.exe" `
  -ExternalArgumentsTemplate "-NoProfile -ExecutionPolicy Bypass -File C:\local-models\run-heavy-face.ps1 -ManifestCsv `"{manifest}`" -OutputCsv `"{output}`"" `
  -ExternalOutputCoordinateSpace TileImage `
  -ExternalOutputCsv ".tmp/local-heavy-model/tile-face.csv"
```

외부 runner는 manifest의 `tileImagePath`, `frame`, `tileIndex`, `tileX`, `tileY`, `tileW`, `tileH`, `tileScale`, `tileImageW`, `tileImageH`를 읽어 `frame,tileIndex,detectionId,x,y,w,h,confidence,tileSupportCount` CSV를 만들어야 한다. `tileImagePath`는 `TileScale`이 적용된 확대 tile 이미지다. 외부 runner가 원본 frame 좌표계로 출력하면 기본값 `Frame`을 쓰고, 확대 tile 이미지 좌표로 출력하면 `-ExternalOutputCoordinateSpace TileImage`, 확대 전 tile-local 좌표로 출력하면 `TileOriginal`을 지정한다. 이 경우 script가 evidence 입력 전에 원본 frame 좌표계로 정규화하고 `inputCoordinateSpace`, `normalizedCoordinateSpace=Frame`을 남긴다. `tileIndex`는 `sourceTileIndex` 또는 `manifestTileIndex` 이름으로도 받을 수 있다. 외부 tile-face 출력은 같은 frame이라는 이유만으로 통과하지 않고, 정규화 뒤 검출 중심점이 해당 manifest tile 안에 있어야 한다. face verification 모델을 따로 실행했다면 `frame,verificationId,x,y,w,h,faceVerificationConfidence,faceVerificationDistance` CSV를 만든다. `new-yolo-pseudo-gt-evidence.ps1`는 이 필수 evidence 컬럼이 빠진 CSV를 거부하므로, 누락된 confidence/distance/geometry 기본값으로 오탐/미탐 후보가 조용히 분류되지 않는다.

기본 YOLO 후보 자체를 고품질 face verification 모델에 넣을 때는 `new-yolo-pseudo-gt-face-verification-input.ps1`가 만든 `face-verification-manifest.csv`를 사용한다. 이 manifest는 `cropImagePath`, `candidateId`, `basePredictionId`, 원본 YOLO box, 확장 crop 좌표를 담고, 외부 runner는 `frame,verificationId,x,y,w,h,faceVerificationConfidence,faceVerificationDistance` CSV를 만들어야 한다.
같은 frame에 manifest crop이 여러 개 있으면 외부 runner는 `candidateId`, `sourceCandidateId`, `basePredictionId` 중 하나를 출력해야 하며, 그 값은 manifest의 후보와 일치해야 한다. 외부 runner가 원본 frame 좌표계로 출력하면 기본값 `Frame`을 쓰고, crop 이미지 좌표로 출력하면 `-ExternalOutputCoordinateSpace CropImage`, 확대 전 crop-local 좌표로 출력하면 `CropOriginal`을 지정한다. 이 경우 script가 evidence 입력 전에 원본 frame 좌표계로 정규화하고 `inputCoordinateSpace`, `normalizedCoordinateSpace=Frame`을 남긴다. 또한 외부 face verification 출력은 같은 frame이라는 이유만으로 통과하지 않고, 정규화 뒤 검출 중심점이 해당 frame의 matching manifest crop 안에 있어야 한다. 이 검증은 test-only evidence에 엉뚱한 같은-frame 검출이 섞여 오탐/미탐 후보를 왜곡하지 않게 하기 위한 것이다.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./scripts/new-yolo-pseudo-gt-face-verification-input.ps1 `
  -VideoPath ".tmp/yolo-problem-span-0900/followup-00-09-00-2s.mp4" `
  -BasePredictionLog ".tmp/yolo-problem-span-0900/yolo-quality-2s-dump.log" `
  -OutputDir ".tmp/local-heavy-model/face-verification-input" `
  -CropPaddingRatio 0.35 `
  -ExternalCommand "powershell.exe" `
  -ExternalArgumentsTemplate "-NoProfile -ExecutionPolicy Bypass -File C:\local-models\run-face-verifier.ps1 -ManifestCsv `"{manifest}`" -OutputCsv `"{output}`"" `
  -ExternalOutputCoordinateSpace CropImage `
  -ExternalOutputCsv ".tmp/local-heavy-model/face-verification.csv"
```

사람/사물 보조 신호는 `new-yolo-pseudo-gt-person-object-input.ps1`가 만든 `person-object-manifest.csv`를 사용한다. 이 manifest는 `frameImagePath`, `frame`, `frameWidth`, `frameHeight`, `scaleWidth`, `scaledFrameWidth`, `scaledFrameHeight`를 담는다. 외부 runner는 기본값으로 원본 frame 좌표계 기준 `frame,detectionId,x,y,w,h,confidence` CSV를 만들어야 하며, 검출 중심점은 해당 frame의 manifest bounds 안에 있어야 한다. 외부 runner가 `ScaleWidth`로 만든 스케일된 frame 이미지 좌표계로 출력하면 `-ExternalOutputCoordinateSpace ScaledFrame`을 지정한다. 이 경우 script가 evidence 입력 전에 원본 frame 좌표계로 정규화하고 `inputCoordinateSpace`, `normalizedCoordinateSpace=Frame`을 남긴다. 외부 runner가 `class`/`label`/`name`/`category`를 제공하면 `person`/`human`/`people`/`man`/`woman` 계열만 `personConfidence`, `personUpperOverlap` 계산에 사용하고, `car` 같은 non-person object row는 person 보조 신호에서 제외한다. class가 없는 기존 CSV는 호환을 위해 그대로 보조 후보로 받는다. 단, confidence가 `MinPersonObjectConfidence` 미만인 row는 보조 우선순위에도 쓰지 않는다. 이 값은 얼굴 정답이 아니라 후보 우선순위 보조 신호로만 사용한다.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./scripts/new-yolo-pseudo-gt-person-object-input.ps1 `
  -VideoPath ".tmp/yolo-problem-span-0900/followup-00-09-00-2s.mp4" `
  -Frames "4,6,7,17,20,24,25,32" `
  -OutputDir ".tmp/local-heavy-model/person-object-input" `
  -ScaleWidth 960 `
  -ExternalCommand "powershell.exe" `
  -ExternalArgumentsTemplate "-NoProfile -ExecutionPolicy Bypass -File C:\local-models\run-heavy-person-object.ps1 -ManifestCsv `"{manifest}`" -OutputCsv `"{output}`"" `
  -ExternalOutputCoordinateSpace ScaledFrame `
  -ExternalOutputCsv ".tmp/local-heavy-model/person-object.csv"
```

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./scripts/run-yolo-problem-span-verification.ps1 `
  -Source "srcTest/260102_jp_10.mp4" `
  -TrimStart "00:09:00" `
  -TrimSeconds 2 `
  -OutputDir ".tmp/yolo-problem-span-0900-pseudo-gt" `
  -WithPseudoGtTileInput `
  -PseudoGtTileColumns 3 `
  -PseudoGtTileRows 3 `
  -PseudoGtTileOverlapRatio 0.25 `
  -PseudoGtTileScale 2.0 `
  -PseudoGtTileExternalCommand "powershell.exe" `
  -PseudoGtTileExternalArgumentsTemplate "-NoProfile -ExecutionPolicy Bypass -File C:\local-models\run-heavy-face-tile.ps1 -ManifestCsv `"{manifest}`" -OutputCsv `"{output}`"" `
  -PseudoGtTileExternalOutputCoordinateSpace TileImage `
  -PseudoGtTileExternalOutputCsv ".tmp/local-heavy-model/tile-face.csv" `
  -WithPseudoGtFaceVerificationInput `
  -PseudoGtFaceVerificationExternalCommand "powershell.exe" `
  -PseudoGtFaceVerificationExternalArgumentsTemplate "-NoProfile -ExecutionPolicy Bypass -File C:\local-models\run-face-verifier.ps1 -ManifestCsv `"{manifest}`" -OutputCsv `"{output}`"" `
  -PseudoGtFaceVerificationExternalOutputCoordinateSpace CropImage `
  -PseudoGtFaceVerificationExternalOutputCsv ".tmp/local-heavy-model/face-verification.csv" `
  -WithPseudoGtPersonObjectInput `
  -PseudoGtPersonObjectScaleWidth 960 `
  -PseudoGtPersonObjectExternalCommand "powershell.exe" `
  -PseudoGtPersonObjectExternalArgumentsTemplate "-NoProfile -ExecutionPolicy Bypass -File C:\local-models\run-heavy-person-object.ps1 -ManifestCsv `"{manifest}`" -OutputCsv `"{output}`"" `
  -PseudoGtPersonObjectExternalOutputCoordinateSpace ScaledFrame `
  -PseudoGtPersonObjectExternalOutputCsv ".tmp/local-heavy-model/person-object.csv" `
  -PublishPseudoGtToGoalEvidence
```

`PseudoGtTileExternalCommand`를 쓰면 problem-span runner가 tile manifest 생성 뒤 외부 고정밀 tile face runner를 실행하고, `PseudoGtTileExternalOutputCsv`를 곧바로 `PseudoGtTileFaceCsv`로 연결해 `pseudo-gt-candidates.csv` 생성에 사용한다. 이미 외부 모델을 따로 실행해 둔 경우에는 `PseudoGtTileFaceCsv`만 직접 넘겨도 된다.
`PseudoGtPersonObjectExternalCommand`를 쓰면 runner가 full-frame person/object manifest 생성 뒤 외부 보조 runner를 실행하고, `PseudoGtPersonObjectExternalOutputCsv`를 곧바로 `PseudoGtPersonObjectCsv`로 연결한다. 외부 runner가 스케일된 frame 이미지 좌표로 출력하면 `PseudoGtPersonObjectExternalOutputCoordinateSpace ScaledFrame`을 지정해야 한다.

`-PublishPseudoGtToGoalEvidence`를 붙이면 problem-span run의 pseudo-GT 후보를 completion gate가 읽는 `.tmp/yolo-pseudo-gt/pseudo-gt-candidates.csv`, `.tmp/yolo-pseudo-gt/pseudo-gt-summary.md`, `.tmp/yolo-pseudo-gt/pseudo-gt-review-queue.csv`로 발행한다. 이 스위치는 tile-face 또는 face-verification CSV/외부 runner가 있을 때만 허용된다. person/object 결과만으로는 얼굴 정답을 만들 수 없으므로 goal evidence 발행 조건이 아니다.

`PseudoGtTileFaceCsv`와 `PseudoGtFaceVerificationCsv` 중 하나 이상이 있으면 `pseudo-gt-candidates.csv`와 `pseudo-gt-summary.md`가 생성된다. tile 없이 face verification만 잡은 얼굴도 기본 YOLO와 매칭되지 않으면 `missCandidate`로 남긴다. 단, tile face row는 `MinTileFaceConfidence` 이상이고 `MinTileSupportCount` 이상의 반복 tile support가 있을 때만 support/miss/temporal/comparison evidence로 쓰고, face verification row는 `MinVerificationConfidence` 이상이면서 `MaxVerificationDistance` 이하일 때만 support/miss/comparison 후보가 된다. 기본 YOLO 박스와 고품질 face evidence는 IoU 또는 중심 거리로 매칭하되, 중심만 맞고 크기 차이가 큰 후보는 geometry support로 보지 않는다. 이렇게 큰 YOLO 박스 안의 작은 얼굴처럼 과대 박스/미탐이 섞인 경우가 `supportedFaceCandidate`로 묻히지 않고 review queue에 남는다. 약한 tile/verification row는 같은 frame에 있어도 nearest comparison 근거로 기록하지 않는다. `PseudoGtPersonObjectCsv`는 선택 입력이며 얼굴 정답으로 쓰지 않고 우선순위 보조 신호로만 쓴다.
`pseudo-gt-review-queue.csv`는 같은 후보를 `fpProbability`/`missProbability` 기준으로 정렬해 먼저 볼 frame/candidate를 알려준다. queue에는 사람이 라벨을 옮길 때 참고할 `expectedReviewLabel`과 candidate `evidenceNotes`도 포함하지만, 이 값도 참고 증거이며 최종 판정은 review CSV 라벨로만 닫는다. `expectedReviewLabel`은 `supportedFaceCandidate=face`, `falsePositiveCandidate=nonface`, `missCandidate=miss`로 안내한다.
기본 YOLO 검출이 0개인 구간에서도 `-AllowNoDetections -WithPseudoGtTileInput`과 tile 외부 runner를 함께 쓰면 sampled frame tile 결과가 `missCandidate`로 기록된다. 이때 `-WithPseudoGtPersonObjectInput`과 person/object 외부 runner를 함께 쓰면 같은 sampled frame의 person/object 보조 신호도 `personConfidence`, `personUpperOverlap`, `auxiliaryPriorityBoost`, `auxiliarySignalRole=priority-only-not-face-evidence`로 연결된다. 다만 `MinPersonObjectConfidence` 미만 person/object row는 review 우선순위를 올리지 않는다. person/object row는 `missProbability`를 직접 올리지 않고 `auxiliaryPriorityBoost`로만 review 순서를 올린다. 이 경로는 작은 얼굴 미탐 검증용 test-only 증거이며, person/object 결과는 얼굴 정답이 아니라 review 우선순위 보조 신호이고 실제 `miss` 확정은 review CSV 라벨로 닫는다.

review CSV를 사람이 채운 뒤에는 pseudo-GT 후보가 실제 라벨로 닫혔는지 별도 closure summary로 확인한다.
closure CSV는 candidate의 confidence, tile/verification, person/object 보조 신호, `auxiliarySignalRole`, 반복 support, IoU/center-distance evidence를 보존하므로 최종 `face`/`nonface`/`miss` 라벨 근거를 나중에 다시 확인할 수 있다.
`-PublishPseudoGtToGoalEvidence`로 goal evidence에 발행한 경우에는 completion gate 기본 경로인 `.tmp/yolo-pseudo-gt/`의 후보 CSV를 닫아야 한다. problem-span 출력 폴더만 검증하는 임시 실행이면 아래 경로들을 해당 run의 `-OutputDir` 아래 파일로 바꿔서 실행한다.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./scripts/close-yolo-pseudo-gt-review.ps1 `
  -PseudoGtCsv ".tmp/yolo-pseudo-gt/pseudo-gt-candidates.csv" `
  -ReviewCsv ".tmp/yolo-full-gt/review-package-smoke/full-gt-review.csv" `
  -FullFrameReviewCsv ".tmp/yolo-full-gt/review-package-smoke/full-frame-review.csv" `
  -OutputCsv ".tmp/yolo-pseudo-gt/pseudo-gt-review-closure.csv" `
  -SummaryPath ".tmp/yolo-pseudo-gt/pseudo-gt-review-closure-summary.md" `
  -RequireAllClosed
```

`supportedFaceCandidate`는 완료된 `reviewStatus`의 `face`, `falsePositiveCandidate`는 완료된 `reviewStatus`의 `nonface`, `missCandidate`는 수동 추가한 `face` 또는 `miss` row로 닫힌다. `missCandidate`는 같은 frame의 manual row와 IoU로 매칭하며, `full-frame-review.csv`가 있으면 해당 frame의 missed-face scan도 완료 상태이고 `missedFaceRowsAdded > 0`이어야 닫힌다.
full-GT review package에서도 detection crop row는 `face`/`nonface`로 닫고, full-frame scan에서 추가하는 manual missed-face row는 `miss` 라벨을 사용할 수 있다. `miss` 라벨은 `sourcePredictionId`가 없는 manual missed-face row에만 유효하다.

## 산출물

`-OutputDir` 아래에서 다음 파일을 확인한다.

- `yolo-followup-quality-evidence.md`: 실행 summary, scene-cut 제거 결과, 최종 mask summary
- `yolo-mask-continuity-report.md`: 짧은 gap, per-face short gap, isolated mask, low-confidence mask 목록
- `yolo-quality-review-checklist.md`: 깜박임/잔상/오탐 review frame 목록
- `yolo-quality-full-gt-template.csv`: 필요 시 수동 `face`/`nonface`/`miss` 라벨 입력용
- `yolo-detection-overlay.mp4`: `-WithDetectionOverlayVideo` 사용 시 생성되는 연속 검출 overlay 영상
- `yolo-review-contact-sheet.png`: `-WithReviewContactSheet` 사용 시 생성되는 frame-number-labeled review contact sheet
- `pseudo-gt-tile-input/tile-manifest.csv`: `-WithPseudoGtTileInput` 사용 시 생성되는 test-only 고품질 모델 입력 manifest
- `pseudo-gt-tile-input/tile-input-summary.md`: tile frame/overlap/해상도/외부 runner 연결 요약
- `pseudo-gt-face-verification-input/face-verification-manifest.csv`: `-WithPseudoGtFaceVerificationInput` 사용 시 생성되는 base YOLO 후보 crop manifest
- `pseudo-gt-face-verification-input/face-verification-input-summary.md`: crop padding/해상도/외부 face verification runner 연결 요약
- `pseudo-gt-person-object-input/person-object-manifest.csv`: `-WithPseudoGtPersonObjectInput` 사용 시 생성되는 full-frame person/object 보조 검증 manifest
- `pseudo-gt-person-object-input/person-object-input-summary.md`: frame/해상도/외부 person/object runner 연결 요약
- `pseudo-gt-candidates.csv`: test-only high-precision tile/verification 결과를 기본 YOLO 후보와 비교한 후보 CSV
- `pseudo-gt-review-queue.csv`: `falsePositiveCandidate`/`missCandidate` 우선 확인용 review queue CSV. 사람이 후보를 바로 추적할 수 있도록 `expectedReviewLabel`, `basePredictionId`, `tileDetectionId`, `verificationId`, `x/y/w/h`, IoU/center-distance/support/probability 근거와 `evidenceNotes`를 함께 보존한다. person/object overlap은 class가 person 계열인 경우에만 `auxiliaryPriorityBoost`로 review 우선순위만 올리며, `auxiliarySignalRole=priority-only-not-face-evidence`로 face/nonface/miss 결론이 아님을 명시한다.
- `pseudo-gt-summary.md`: pseudo-GT 후보 수와 입력 row count 요약
- `pseudo-gt-review-closure.csv`: review CSV 라벨로 pseudo-GT 후보가 닫혔는지 확인한 결과
- `pseudo-gt-review-closure-summary.md`: 닫힌 후보, 미검토 후보, 라벨 불일치 후보 수 요약

review package가 필요하면 `scripts/run-yolo-problem-span-verification.ps1`에 `-WithReviewPackage`를 붙여 다시 실행한다. 그러면 `review-package/review-index.html`에서 crop/full-frame overlay를 확인한다.
연속 재생에서 깜박임이나 화면전환 잔상을 먼저 빠르게 보려면 `-WithDetectionOverlayVideo`를 함께 사용한다. 단, 이 overlay 영상도 참고 증거이며 최종 오탐/미탐 판정은 CSV review row로 닫는다.
정지 이미지로 오탐 후보를 빠르게 분류하려면 `-WithReviewContactSheet`를 함께 사용한다. 검출이 0개인 구간에서는 같은 옵션이 짧은 source clip에서 샘플 frame contact sheet를 만들어 미탐 여부를 확인하게 한다. 이 contact sheet도 참고 증거이며, 최종 판정은 review CSV로 닫는다.
`SmokeDetection` 로그의 `conf`는 threshold 경계 후보가 반올림 때문에 잘못 분류되지 않도록 6자리 정밀도로 기록한다.

부분 시각 확인은 참고 증거로만 취급한다. 일부 overlay를 확인해서 후보가 실제 얼굴인지 설명할 수는 있지만, 전체 오탐/미탐 게이트를 닫으려면 `review-package/full-gt-review.csv`와 `review-package/full-frame-review.csv`에 crop/full-frame row가 채워져 있어야 한다. 특히 edge 또는 top-edge weak 후보는 보호해야 할 부분 얼굴일 수 있으므로, 실제 얼굴을 덮지 않는다는 시각 근거 없이 자동 오탐으로 단정하지 않는다.

## Test-Only High-Precision Pseudo-GT Direction

오탐/미탐 고도화 단계에서는 앱 기본 런타임 경로와 별도로, 느려도 더 강한 테스트 전용 pseudo-GT 검출을 사용한다. 이 경로는 배포 기본값이나 실시간 자동 모자이크 속도 목표가 아니라, 짧은 문제 구간에서 사람이 리뷰하기 전 후보 우선순위를 높이는 검증 보조 단계다.

현재 자동 모자이크 후처리는 runtime pipeline과 temporal/ROI/scene-cut 단계가 분리되어 있으므로, pseudo-GT도 기본 detector 실행에 직접 섞지 않는다. 별도 test-only evidence pipeline으로 기본 YOLO 결과를 읽고, 고정밀 tile/person/object 보조 결과와 비교해 review CSV 초안을 보강한다.

- 기본 YOLO 결과와 별도로, 테스트 전용 고정밀 검출을 같은 짧은 clip/frame에 실행한다.
- tile/face verification/person-object manifest 생성은 기본 `MaxFrames=900` 상한으로 full-video frame sweep을 막고, 문제 구간 frame set만 받는다.
- 작은 얼굴 미탐을 줄이기 위해 frame을 tile/overlap으로 나누고, tile을 모델 입력 크기로 확대해서 검출한다.
- 기본 YOLO 후보는 고품질 face verification/face detection 모델로 재검증한다.
- 이 고품질 검증 모델은 앱 기본 런타임에는 포함하지 않고, 짧은 문제 구간의 evidence 생성과 오탐/미탐 후보 분류에만 사용한다.
- 필요하면 무거운 face/person/object 모델을 로컬 경로로 받아 사용한다. 모델 파일은 커밋하지 않고, 기본 앱 실행 경로에도 넣지 않는다.
- 기본 YOLO에는 없고 고정밀 tile 검출/face verification에는 있는 후보를 `missCandidate`로 기록한다.
- 기본 YOLO에는 있는데 고정밀 face verification/tile/person/object/context 근거가 약한 후보를 `falsePositiveCandidate`로 기록한다.
- 기본 YOLO와 고정밀 tile 검출 또는 face verification이 같은 위치를 반복 지지하면 `supportedFaceCandidate`로 기록한다.
- YOLO 후보와 고품질 검증 결과의 IoU, 중심 거리, 면적 변화율, 반복 support를 비교해 후보 유형을 나눈다.
- person/object 결과는 얼굴 정답이 아니므로 단독으로 `face`/`nonface`/`miss` 확정에 쓰지 않는다. 사람 검출은 얼굴 미탐 후보나 오탐 후보의 우선순위를 높이는 보조 신호로만 사용한다.
- 최종 확정은 여전히 review CSV의 `face`/`nonface`/`miss` 라벨로 닫는다.

예상 로그/CSV 필드:

- `baseFaceConfidence`
- `tileFaceConfidence`
- `tileSupportCount`
- `faceVerificationConfidence`
- `faceVerificationDistance`
- `personConfidence`
- `personUpperOverlap`
- `personObjectClass`
- `supportFrameCount`
- `supportRowCount`
- `supportSources`
- `bestIou`
- `centerDistanceRatio`
- `areaChangeRatio`
- `fpProbability`
- `missProbability`
- `pseudoGtReason`
- `expectedReviewLabel`
- `evidenceNotes`

## 판정 기준

### 깜박임

통과 근거:

- `Final mask summary` 또는 `SmokeFinalMaskSummary`에서 `shortGaps=0`
- 여러 얼굴이 동시에 있는 구간은 `perFaceShortGaps=0`도 같이 확인한다. 이 값은 다른 얼굴 마스크가 같은 프레임에 남아 있어도 특정 얼굴만 1-3프레임 빠지는 깜박임 후보를 잡기 위한 것이다.
- `isolated=0`
- `reviewRequired=True`이면 `reviewReasons`에서 `short-gap`, `per-face-short-gap`, `large-jump-gap`, `isolated-mask`가 있는지 먼저 확인한다. 이 값은 통과/실패 단정이 아니라 어느 frame group을 먼저 봐야 하는지 알려주는 triage 근거다.
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
- 컷 이후 final gap fill이 `blockedByCut=...`/`cutBlockedFrames=...` 또는 `blockedBySceneCarry=...`/`sceneCarryBlockedFrames=...`를 남기면, 후속 anti-flicker fill이 확인된 전환 구간의 잔상을 다시 만들지 않았다는 근거로 본다. scene-carry blocked frame이 stable gap 안에 하나라도 있거나, scene-carry blocked face가 gap 내부 보간 후보와 매칭되면 해당 gap 전체를 채우지 않는다. scene-carry blocked frame이 gap anchor인 경우도 차단되므로, 컷 window 안의 protected 후보가 바로 다음 빈 프레임으로 이전 장면 모자이크를 연장할 수 없다. stored/manual mask가 있는 프레임도 block 판단에는 포함되므로, fill 대상에서 제외된 stored frame 주변으로 전환 잔상이나 cleanup 제거 후보가 다시 퍼지는지 확인할 수 있다. `blockedByCleanup=...`/`cleanupBlockedFrames=...`는 cleanup이 제거한 같은 얼굴 위치를 재생성하지 않았다는 근거다.
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
- resume prompt는 저장된 자동 실행 signature가 현재 detector/YOLO 설정, threshold, tiling, tracking/downscale 설정과 같을 때만 표시한다. 설정이 바뀌면 stale partial run을 이어가지 않고 fresh run으로 전환한다.
- manual bitmap mask는 별도 저장 경로라서 이 stale face-rect reset 대상이 아니다.
- stale reset이 발생하면 `[AutoMaskResumeReset] start=... removedStaleFaceMasks=...`가 debug output에 남는다.
- 설정 mismatch로 partial run을 버리면 `[AutoMaskResumeReset] reason=settings-changed resumeIndex=...`가 debug output에 남는다.
- `[AutoRunSummary]`에는 `startFrame=...`이 함께 남아 `processed=1` 같은 짧은 실행이 새 전체 실행인지, 부분 재개인지 바로 구분할 수 있다.

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
- Flicker/carry triage: `shortGaps=0`, `largeJumpGaps=0`, `isolated=0`, `protectedSceneCarry=0`; remaining low-confidence edge/top-edge rows now keep `reviewRequired=True` with review-only reasons until visual labeling is complete.
- Cleanup evidence: `removedUpperWeakClusters=3`, `removedFrames=33,34,35`
- Scene-cut evidence: `directChecked=74`, `directSkipped=0`, `cutPairs=none`, `removedFrames=none`
- False-positive triage: `weakNonEdge=0`, `upperWeak=0`, `lowerWeak=0`, `aspectBad=0`, `tinyWeak=0`, `tinyShort=0`; residual `edgeWeak/topEdgeWeak` frames are `4,6,7,17,20,24,25,32` and remain visual review targets rather than automatic non-face labels.
- Partial overlay observation: frames `4`, `24`, and `32` show the residual edge/top-edge boxes covering a visible partial background face and/or the foreground face. This is reference evidence only; the crop/full-frame CSV rows are still unreviewed GT rows.

## Wrapper Smoke

`scripts/run-yolo-problem-span-verification.ps1` 자체는 `.tmp/srcTest-smoke/smoke-0900-2s.mp4`의 2초 구간으로 확인했다.

- Output: `.tmp/yolo-problem-span-wrapper-smoke/yolo-followup-quality-evidence.md`
- Detection rows: `96`
- Overlay option smoke: `.tmp/yolo-problem-span-overlay-smoke/yolo-followup-quality-evidence.md`
- Detection overlay video: `.tmp/yolo-problem-span-overlay-smoke/yolo-detection-overlay.mp4` (`689K`, generated from the same 2-second sample)
- Scene-cut evidence on this no-hard-cut wrapper sample after the direct-check budget/priority update: `directChecked=74`, `directSkipped=0`, `maxDiff=0.206`, `cutPairs=none`, `removedFrames=none`, `threshold=0.150`. This confirms dense spans still get bounded direct source->target checks, while the lower direct threshold and higher budget do not delete this no-hard-cut sample.
- Post-scene final gap-fill now also carries cut pairs found by the initial final gap-fill scene-cut guard, so a gap-fill that was removed across a transition cannot be re-created by the later post-scene gap-fill pass.
- Final gap-fill now rejects weak geometry-risk anchors (`edge`, `tiny`, `upper/lower weak`, aspect outlier) while keeping strong edge anchors eligible. This reduces YOLO false positives from becoming temporal gap-fill seeds without disabling high-confidence edge faces.
- Post-scene cleanup now also purges residual YOLO carry masks after known cut pairs when the post-cut box still matches the pre-cut position and remains at or below `0.98` confidence. For adjacent cut pairs it scans the normal 5-frame carry purge window, then continues through an 8-frame weak-carry window for matching boxes at or below `0.78` confidence. The later anti-flicker gap-fill block window is also 8 frames, so a transition residue removed or flagged near a cut is not recreated a few frames later. For direct source-to-target cut pairs it starts at `source+1` and continues through the confirmed target plus the carry window, so transition frames between `source` and `target` cannot keep an old blur. If the cut-start frame no longer has a mask, the carry reference lookup now checks up to 5 frames before the cut-start frame. This is implemented in `YoloFinalMaskPostProcessor.RemoveSceneCutCarryRemnants` and shared by the GUI path and smoke harness. Removed carry frames and the extended carry block window are passed into the final gap-fill blocked-frame list, so the anti-flicker pass cannot recreate the same scene-transition residue.
- Deterministic verifier evidence: `scripts/verify-yolo-final-mask-cleanup.ps1` now includes `sceneCutCarryRemoved=17`, `sceneCutCarryFrames=1001,1002,1003,1004,1005,1006,1007,1008,2001,2002,2003,2004,2005,2006,2007,2008,2009`, extended `sceneCutCarryBlockedFrames=1001,1002,1003,1004,1005,1006,1007,1008,2001,2002,2003,2004,2005,2006,2007,2008,2009,2010,2011`, `emptyPostCutRemovedUnsupportedStrong=2`, `partialSceneCarryRefillBlocked=3` with `partialSceneCarryBlockedFrames=3101,3102,3103`, `partialSceneCarryFaceRefillBlocked=3` with `partialSceneCarryFaceBlockedFrames=3111,3112,3113`, `sceneCarryAnchorRefillBlocked=1` with `sceneCarryAnchorBlockedFrames=3209`, `storedCleanupRefillBlocked=3` with `storedCleanupBlockedFrames=3301,3302,3303`, and `storedSceneCarryRefillBlocked=3` with `storedSceneCarryBlockedFrames=3311,3312,3313`.
- Final cleanup evidence: `removedUpperWeakClusters=3`, `removedFrames=33,34,35`
- Final gap-fill evidence: `filled=0`, `frames=none`, `blockedByCleanup=3`, `cleanupBlockedFrames=33,34,35`
- Final mask summary: `shortGaps=0`, `largeJumpGaps=0`, `isolated=0`, `lowConf=7`, `weakNonEdgeFrames=none`, `upperWeakFrames=none`, `reviewRequired=True`, `reviewReasons=low-confidence-review,edge-weak-review,top-edge-weak-review`

Latest post stored-gap-block short-span evidence:

- Evidence summary: `.tmp/yolo-problem-span-storedblock-0900/yolo-followup-quality-evidence.md`
- Overlay video: `.tmp/yolo-problem-span-storedblock-0900/yolo-detection-overlay.mp4`
- Source: existing 2-second `.tmp/srcTest-smoke/smoke-0900-2s.mp4` clip, not the full source video
- Run summary: `detector=YoloFaceOnnxDetector/GPU:DirectML`, `processed=60`, `detections=96`
- Flicker/carry triage: `shortGaps=0`, `largeJumpGaps=0`, `isolated=0`, `protectedSceneCarry=0`
- False-positive triage: `weakNonEdge=0`, `upperWeak=0`, `lowerWeak=0`, `aspectBad=0`, `tinyWeak=0`, `tinyShort=0`
- Scene-cut guard on this no-cut sample: `cutPairs=none`, `removedFrames=none`, `maxDiff=0.206`
- Remaining review state: `reviewRequired=True`, `reviewReasons=low-confidence-review,edge-weak-review,top-edge-weak-review`

Latest extended-lookback short-span evidence:

- Evidence summary: `.tmp/yolo-problem-span-lookback5-0900/yolo-followup-quality-evidence.md`
- Overlay video: `.tmp/yolo-problem-span-lookback5-0900/yolo-detection-overlay.mp4`
- Source: existing 2-second `.tmp/srcTest-smoke/smoke-0900-2s.mp4` clip, not the full source video
- Code change covered: YOLO post-cut carry source lookback is now 5 frames in the GUI path, smoke harness, and scene-carry cleanup defaults
- Synthetic guard proof: `scripts/verify-face-track-scene-cut-guard.ps1` passed with `extendedLookbackPostCutCandidates=2`, `extendedLookbackPostCutRemoved=2`
- Run summary: `detector=YoloFaceOnnxDetector/GPU:DirectML`, `processed=60`, `detections=96`
- Flicker/carry triage: `shortGaps=0`, `largeJumpGaps=0`, `isolated=0`, `protectedSceneCarry=0`
- False-positive triage: `weakNonEdge=0`, `upperWeak=0`, `lowerWeak=0`, `aspectBad=0`, `tinyWeak=0`, `tinyShort=0`
- Scene-cut guard on this no-cut sample: `cutPairs=none`, `removedFrames=none`, `maxDiff=0.206`
- Remaining review state: `reviewRequired=True`, `reviewReasons=low-confidence-review,edge-weak-review,top-edge-weak-review`
- Visual overlay spot-check: top-region crops from `.tmp/yolo-problem-span-lookback5-0900/review-frames/overlay-top-004.png`, `overlay-top-006.png`, `overlay-top-007.png`, `overlay-top-017.png`, `overlay-top-020.png`, `overlay-top-024.png`, `overlay-top-025.png`, and `overlay-top-032.png` were checked. Frames `4`, `6`, and `7` show the residual top-edge weak box over a visible partial background face; frames `17`, `20`, `24`, `25`, and `32` show a visible partial background face and/or the foreground face. No confirmed non-face false positive was found in these residual review rows.
- Decision: do not tighten the default edge/top-edge cleanup based on this 2-second sample. These rows remain `reviewRequired=True` until the crop/full-frame GT CSV rows are filled, because automatically deleting them would risk creating misses on partial faces.
- Full review package generated for this same lookback5 sample:
  - Summary: `.tmp/yolo-problem-span-lookback5-0900-review/yolo-followup-quality-evidence.md`
  - Review index: `.tmp/yolo-problem-span-lookback5-0900-review/review-package/review-index.html`
  - Crop review CSV: `.tmp/yolo-problem-span-lookback5-0900-review/review-package/full-gt-review.csv` with `96` candidate rows
  - Full-frame review CSV: `.tmp/yolo-problem-span-lookback5-0900-review/review-package/full-frame-review.csv` with `24` frame rows
  - Detection overlay video: `.tmp/yolo-problem-span-lookback5-0900-review/yolo-detection-overlay.mp4`
  - Required full-frame review frames: `4,6,7,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,24,25,26,32`

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

## 2026-05-27 No-Detection / Small-Face Probe

Focused 2-second probe at `00:06:00`:

- Command path: `scripts/run-yolo-problem-span-verification.ps1 -Source srcTest/260102_jp_10.mp4 -TrimStart 00:06:00 -TrimSeconds 2 -AllowNoDetections`
- Evidence summary: `.tmp/yolo-problem-span-0600-review/yolo-followup-quality-evidence.md`
- Contact sheet: `.tmp/yolo-problem-span-0600-review/no-detection-contact-sheet.png`
- Result: `Detection rows: 0`; final mask summary stayed `frames=0`, `rows=0`, `shortGaps=0`, `largeJumpGaps=0`, `isolated=0`, `weakNonEdge=0`, `tinyShort=0`, and `reviewRequired=False`.
- Visual observation: the sampled frames do not contain a large foreground face, but do contain very small/background face-like regions. Treat this as small-face miss evidence, not as false-positive evidence.
- Tiling comparison on the same 2-second clip: `.tmp/yolo-tiling-0600-sweep.csv` and `.tmp/yolo-tiling-0600-sweep.log`. The non-tiling sweep produced `FaceMaskFrames=15`; tiling produced `FaceMaskFrames=16`, but also left `weakNonEdge=8`, `tinyShort=5`, `reviewRequired=True`, and raised CPU `totalMs` to `42006`. Do not enable tiling as a default fix from this evidence alone; it needs a separate small-face profile gate.
- Large/landmark box refinement comparison on the 09:00 2-second clip: `.tmp/yolo-landmark-refine-0900-sweep.csv`. Landmark refine reduced quality against the current reference (`AvgBestIou` from `0.801` to `0.473`, `MinBestIou` from `0.625` to `0.205`, `AvgBaselineCoverage` from `0.925` to `0.502`), so it should not be enabled by default for large-box cleanup.

## 2026-05-27 Strong Carry Independence Guard

Synthetic scene-cut carry cleanup now distinguishes a real post-cut strong face from same-size post-cut carry residue:

- Code path: `YoloFinalMaskPostProcessor.RemoveSceneCutCarryRemnants`
- Rule: a high-confidence carry-like candidate is protected only when later strong matching support also shows independent scale change away from the pre-cut reference. Motion-only same-size support is treated as possible scene-transition residue and is removed.
- Verifier: `scripts/verify-yolo-final-mask-cleanup.ps1`
- Evidence: `stickyStrongCarryRemoved=5`, `stickyStrongCarryRemovedUnsupportedStrong=5`, `driftingStrongCarryRemoved=5`, `areaChangedStrongCarryProtected=3`
- Meaning: same-position and same-size drifting high-confidence blur residue after a confirmed cut is no longer protected only because it repeats for several frames. A post-cut strong candidate with scale change can still remain for visual review.

## 2026-05-27 Strong Carry Scene-Cut Probe

The GUI and smoke paths now add a high-confidence post-cut carry probe before final scene-carry cleanup:

- Code paths: `WorkspaceViewModel.ProbeYoloStrongCarrySceneCuts`, `FaceTrackSceneCutGuard.BuildWeakPostCutCarryCandidates`, and `FaceTrackSceneCutGuard.Apply(removeCandidates: false)`.
- Rule: YOLO candidates from `0.78` through `0.995` confidence that still match a stronger pre-cut source can be used to collect frame-difference cut evidence, but this probe does not delete the candidate immediately.
- GUI order: the same strong-carry probe now runs once before temporal smoothing and once after smoothing. Pre-smooth probe cut pairs are passed into temporal smoothing, so smoothing cannot average boxes across a cut that was discovered only by the high-confidence carry probe.
- Gap-fill order: the GUI path now performs the first YOLO weak/isolated cleanup with `fillStableGaps: false`. Stable gap fill is deferred until after pre/post scene-cut probes and scene-carry cleanup have produced blocked cut pairs, blocked cleanup faces, and scene-carry blocked frames. This prevents the anti-flicker fill from bridging a cut before the cut evidence exists.
- Post-gap-fill guard: if the deferred final gap-fill pass discovers an additional cut through `YoloFinalMaskGapFillSceneCutGuard`, the GUI path now reruns scene-carry cleanup with the combined cut pairs and logs it as `[YoloSceneCutCarryCleanup] stage=post-gap-fill ...`. This covers residue around a cut that is only exposed by the anti-flicker fill candidate itself.
- Final deletion still happens in `YoloFinalMaskPostProcessor.RemoveSceneCutCarryRemnants`, so same-position strong residue after a confirmed cut can be removed while independently moving/scaling strong post-cut faces remain protected for review.
- Weak carry candidate generation no longer treats every later strong confidence box as independent proof of a real post-cut face. A strong continuation protects the candidate only when it has enough center shift or area change from the pre-cut source; same-position/same-size strong continuation remains checkable as scene-cut carry residue.
- Verifier: `scripts/verify-face-track-scene-cut-guard.ps1`
- Evidence: `probeCandidates=1`, `probeCutPairs=100->101`, `probeRemoved=0`
- Meaning: a high-confidence transition ghost no longer needs to be weak enough for the older `0.78` post-cut candidate path before the cut pair can be discovered, but the app still avoids treating confidence alone as ground truth.
- Evidence export: `scripts/write-yolo-followup-quality-evidence.ps1` and `scripts/write-yolo-quality-review-checklist.ps1` preserve `[SmokeYoloStrongCarrySceneCutProbe]` / `[YoloStrongCarrySceneCutProbe]` lines and add probe target frames to required full-frame review candidates.
- Carry-cleanup evidence export: when both the initial scene-carry cleanup and `stage=post-gap-fill` cleanup appear in one run, `scripts/write-yolo-followup-quality-evidence.ps1` keeps all cleanup lines and adds frames from each line's removed/protected carry fields to the required full-frame review candidates.

Focused 2-second `00:09:00` evidence after the probe export update:

- Summary: `.tmp/yolo-problem-span-strongprobe-0900/yolo-followup-quality-evidence.md`
- Checklist: `.tmp/yolo-problem-span-strongprobe-0900/yolo-quality-review-checklist.md`
- Detection overlay: `.tmp/yolo-problem-span-strongprobe-0900/yolo-detection-overlay.mp4`
- Detector: `YoloFaceOnnxDetector/GPU:DirectML`
- Detection rows: `96`
- Strong carry probe: `candidates=44`, `checked=44`, `directChecked=43`, `directSkipped=0`, `maxDiff=0.216`, `cutPairs=none`
- Final summary: `shortGaps=0`, `largeJumpGaps=0`, `isolated=0`, `weakNonEdge=0`, `upperWeak=0`, `lowerWeak=0`, `aspectBad=0`, `tinyWeak=0`, `tinyShort=0`, `protectedSceneCarry=0`
- Remaining review state: `reviewRequired=True`, `reviewReasons=low-confidence-review,edge-weak-review,top-edge-weak-review`
- Visual overlay contact sheets:
  - Edge/top review: `.tmp/yolo-problem-span-strongprobe-0900/visual-review/edge-top-review-contact.png`
  - Full review: `.tmp/yolo-problem-span-strongprobe-0900/visual-review/full-review-contact.png`
- Visual observation: frames `4,6,7,17,20,24,25,32` still show visible partial background and/or foreground faces inside the residual edge/top-edge boxes. These are not confirmed non-face false positives from this evidence, so the default cleanup should not delete them automatically.
- This run used a 2-second focused trim, not the full source video.

## 2026-05-27 Review Contact Sheet Smoke

The problem-span wrapper can now generate a review contact sheet directly:

- Option: `-WithReviewContactSheet`
- Smoke summary: `.tmp/yolo-problem-span-contactsheet-smoke/yolo-followup-quality-evidence.md`
- Detection overlay: `.tmp/yolo-problem-span-contactsheet-smoke/yolo-detection-overlay.mp4`
- Contact sheet: `.tmp/yolo-problem-span-contactsheet-smoke/yolo-review-contact-sheet.png`
- Required full-frame review frames: `4,6,7,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,24,25,26,27,28,29,30,31,32`
- Final summary: `shortGaps=0`, `largeJumpGaps=0`, `isolated=0`, `weakNonEdge=0`, `upperWeak=0`, `lowerWeak=0`, `aspectBad=0`, `tinyWeak=0`, `tinyShort=0`, `protectedSceneCarry=0`, `reviewRequired=True`
- This smoke used a 2-second focused `00:09:00` trim, not the full source video.

For no-detection spans, the same `-WithReviewContactSheet` option samples frames from the short source clip and records `Sampled no-detection review frames` in `yolo-followup-quality-evidence.md`.
Even if `[SmokeFinalMaskSummary]` reports `reviewRequired=False` because there are no final mask rows, the wrapper summary records `No-detection review: reviewRequired=True, reviewReasons=no-detection-frame-scan` so visible-face misses are not accidentally treated as clean.
GUI 자동 검토 목록도 YOLO no-detection 전체 구간을 조용히 통과시키지 않고, 샘플 frame을 `얼굴 없음` 검토 대상으로 올린다. 이때 debug log는 `[AutoMaskNoDetectionReview]`로 남는다.
YOLO가 일부 얼굴만 잡고 face coverage가 낮은 구간도 긴 no-face span을 그대로 통과시키지 않는다. 전체 frame 대비 face frame이 20% 이하이고, 검출 구간 사이에 긴 no-face run이 있으면 최대 12개 frame을 `얼굴 없음` 검토 대상으로 샘플링하며 debug log는 `[AutoMaskSparseNoFaceReview]`로 남는다. 이 경로는 미탐 확정이 아니라 GUI 수동 검토 우선순위 지정이다.
GUI 자동 검토 목록의 `신뢰도 낮음` 기준은 선택된 detector backend의 threshold를 따른다. YOLO 선택 중에는 FaceONNX threshold가 아니라 `YoloFaceOnnxDetectorOptions.ConfidenceThreshold + LowConfidenceMargin`을 사용한다.
Sparse tracking materialize는 다음 detection key가 멀리 떨어진 경우에도 이전 얼굴을 그 key 직전까지 길게 끌고 가지 않는다. bridge 가능한 positive detection이 없으면 fallback carry를 `DetectEveryNFrames` 구간으로 제한하며, verifier는 `farNextInterpolated=4`와 frame `5..19` no materialized faces를 확인한다.

No-detection contact-sheet smoke:

- Smoke summary: `.tmp/yolo-problem-span-nodetect-contactsheet-smoke/yolo-followup-quality-evidence.md`
- Contact sheet: `.tmp/yolo-problem-span-nodetect-contactsheet-smoke/yolo-review-contact-sheet.png`
- Detection rows: `0`
- Sampled no-detection review frames: `0,5,11,16,21,27,32,38,43,48,54,59`
- This smoke used a 2-second focused `00:06:00` trim, not the full source video.
