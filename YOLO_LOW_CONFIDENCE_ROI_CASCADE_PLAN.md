# YOLO 저신뢰도 ROI 보조 검증 설계안

## 문서 목적

YOLO의 빠른 전체 프레임 검출은 유지하면서, 얼굴일 확률이 낮거나 검출 조건이 불리한 후보 좌표만 강한 보조 검출기로 재검증한다.

핵심 목표는 다음과 같다.

- 전체 프레임을 보조 모델로 다시 검출하지 않는다.
- 이미 디코딩한 프레임 버퍼에서 후보 좌표 주변 ROI만 사용한다.
- 보조 모델이 확인한 후보만 교체하거나 복구한다.
- 보조 검증 때문에 YOLO의 정상적인 얼굴이 제거되거나 새로운 오탐이 추가되지 않게 한다.
- 품질 저하 없이 `analysisTotalMs`와 보조 검출 비용을 줄인다.

이 문서는 설계안이다. 현재 문서 작성 시점에는 코드 구현·빌드·실제 영상 검증을 완료한 상태가 아니다.

## 현재 코드와 원하는 방식의 차이

### 현재 YOLO 검출 흐름

1. `AutoMaskGenerator.GeneratePipelinedDetectAll*`가 프레임을 BGRA 버퍼로 디코딩한다.
2. YOLO가 전체 프레임을 검출한다.
3. 면적·통계·위치 필터를 적용한 뒤 `FrameMaskProvider`에 최종 얼굴 좌표와 confidence를 저장한다.
4. 후처리 단계에서 tracking, gap-fill, scene-cut guard, weak cleanup 등을 적용한다.

### 현재 위험 프레임 cascade

`YoloRiskCascadeStep`은 다음 조건으로 위험 프레임을 만든다.

- 낮은 confidence
- 작은 얼굴
- 가장자리 얼굴
- 추적 중간 누락
- 신규 track 진입
- 주기적 전체 검증

현재 cascade는 위험 프레임을 다시 디코딩한 뒤 FaceONNX를 **전체 프레임**에 실행한다. 따라서 좌표만 보조 검증하는 목표와는 다르다.

### 현재 ROI 코드

`FaceTrackRoiRefiner`는 추적 후 보간·lost-fill 후보의 좌표 주변을 잘라 보조 검출기를 실행한다. 이미 다음 동작을 제공한다.

- 후보 좌표에 여백 추가
- 프레임 경계로 ROI 자르기
- 원본 BGRA 버퍼의 stride를 유지한 ROI 포인터 전달
- ROI 검출 좌표를 전체 프레임 좌표로 복원
- 기존 후보와 IoU·중심 위치를 비교해 해당 후보만 교체

이 코드를 YOLO의 원시 저신뢰도 후보 검증에도 재사용하는 방향이 적절하다.

## 목표 처리 흐름

```text
프레임 1회 디코드
      ↓
YOLO 전체 프레임 검출
      ↓
원시 후보 중 위험 후보만 선택
      ↓
후보별 ROI 생성·경계 보정·겹침 병합
      ↓
같은 프레임 버퍼에서 보조 모델을 ROI에만 실행
      ↓
ROI 결과를 전체 좌표로 복원
      ↓
원본 후보와 매칭되는 결과만 교체·복구
      ↓
최종 마스크 저장 및 기존 temporal 후처리
```

프레임을 모아서 나중에 처리하지 않는다. 검출 worker가 동일 프레임 버퍼를 보유하고 있는 동안 ROI 보조 검증까지 끝낸 뒤 결과를 전달하고 버퍼를 반환한다.

## 후보 선택 정책

후보 선택은 최종 필터 결과만 보지 말고, YOLO의 원시 후보와 필터 후 후보를 구분해야 한다.

### ROI 보조 검증 대상

- confidence가 YOLO 저신뢰도 기준 이하인 후보
- 면적 비율이 작은 후보
- 화면 가장자리에 걸린 후보
- 직전·다음 프레임과 geometry 연결이 약한 후보
- 새로 등장했지만 아직 temporal support가 부족한 후보

현재 기준값은 다음 값을 초기값으로 참고한다.

- `YoloRiskLowConfidenceThreshold = 0.45`
- `YoloRiskSmallFaceAreaRatio = 0.0012`
- `YoloRiskEdgeMarginRatio = 0.025`
- `YoloStrongConfirmationFrames = 2`

위 값은 최종 고정값이 아니며, 대표 영상의 누락·오탐 검토 후 조정한다.

### 원시 후보 보존

`FilterFacesByAreaAndStats`에서 이미 제거된 후보는 후단에서 다시 확인할 수 없다. 작은 얼굴을 보조 모델로 살릴 목적이라면 다음 중 하나가 필요하다.

- 필터 적용 전 원시 후보를 일시적으로 ROI 후보 목록에 전달한다.
- 필터가 제거한 후보 중 위험 유형만 별도 목록으로 반환한다.

전체 프레임이나 전체 원시 결과를 장기간 보관하지 않고, 현재 프레임 처리 직후 소비하는 방식으로 메모리를 제한한다.

## ROI 생성과 병합

각 후보 박스를 그대로 자르지 않고 주변 문맥을 포함한다.

1. 후보 박스에 가로·세로 여백을 추가한다.
2. 프레임 경계를 넘지 않도록 clamp한다.
3. 너무 작은 ROI는 최소 크기까지 확장한다.
4. 겹치거나 가까운 ROI는 하나의 union ROI로 병합한다.
5. 한 프레임의 ROI 개수와 전체 면적에 상한을 둔다.

현재 `FaceTrackRoiRefiner`의 `0.90 × 후보 크기`, 최소 `80px` 여백은 초기값으로 참고할 수 있다. 가장자리 후보는 잘린 얼굴을 보완할 수 있도록 프레임 안쪽 방향의 여백을 더 확보한다.

ROI를 후보마다 별도 실행하면 후보 수가 많은 프레임에서 보조 호출 수가 폭증할 수 있다. 따라서 기본 방식은 겹침 병합이며, 병합 후에도 ROI가 과도하게 많으면 해당 프레임만 보수적으로 전체 보조 검증 또는 검증 생략을 선택한다.

## 보조 모델 결과 반영

보조 모델의 confidence를 YOLO confidence와 직접 비교하지 않는다. 모델별 threshold를 별도로 적용한다.

### 기존 후보 교체

다음 조건을 모두 만족할 때만 기존 YOLO 후보의 좌표·confidence를 교체한다.

- 보조 결과가 보조 모델의 최소 신뢰도 이상이다.
- 보조 결과와 원본 후보의 IoU 또는 중심 위치가 허용 범위 안이다.
- 후보가 장면 전환 경계를 넘어온 carry가 아니다.
- 필요한 경우 인접 검증 프레임에서 temporal support가 있다.

### 후보 제거

보조 모델이 못 찾았다는 이유만으로 즉시 제거하지 않는다.

- 저신뢰 후보이고 temporal support가 없을 때만 제거 후보로 둔다.
- 인접 프레임에 강한 동일 얼굴이 있으면 기존 후보를 유지하거나 tracking 후처리에 맡긴다.
- 가장자리·부분 얼굴은 보조 모델 실패만으로 제거하지 않는다.

### 새 얼굴 추가

ROI 안에서 원본 YOLO 후보와 겹치지 않는 보조 결과를 무조건 새 얼굴로 추가하지 않는다. 새 얼굴 추가는 다음 조건 중 하나가 필요하다.

- 높은 보조 confidence와 강한 geometry 일치
- 인접한 두 개 이상의 검증 프레임에서 반복 확인
- 신규 track 진입 정책을 통과

이 제한이 없으면 ROI의 배경 물체가 새로운 오탐 얼굴로 추가될 수 있다.

## 전체 화면 보조 검증이 필요한 경우

좌표 기반 ROI만으로는 원래 후보가 전혀 없는 얼굴을 찾을 수 없다. 따라서 다음 경우에는 제한적인 전체 화면 검증을 유지한다.

- 얼굴 후보가 전혀 없는 프레임
- 긴 tracking gap
- scene-cut 직후 신규 인물 진입
- 주기적인 누락 감시용 global scan

권장 분리는 다음과 같다.

| 상황 | 보조 방식 |
| --- | --- |
| 저신뢰도·작은 얼굴·가장자리 후보 | 후보 ROI만 검증 |
| 후보는 있으나 위치가 불안정한 신규 track | ROI 검증 후 필요 시 global scan |
| 얼굴 후보 자체가 없음 | 제한적 global scan |
| 긴 gap 또는 scene-cut 직후 | global scan 및 기존 temporal guard |
| 높은 confidence 정상 후보 | YOLO 결과만 사용 |

기존 `YoloRiskCascadeStep`의 global scan을 전부 제거하는 것이 아니라, 좌표가 있는 위험 후보는 ROI 방식으로 분리하고 좌표가 없는 누락 감시만 전체 검증으로 남긴다.

## 구현 위치

가장 효율적인 구현 위치는 `GeneratePipelinedDetectAll*`의 YOLO 검출 직후이다.

```text
DetectFacesBgraSmart
    → 원시 후보 기록
    → 위험 ROI 생성
    → 보조 detector ROI 호출
    → 후보 매칭·교체·제거 판단
    → 필터 및 BuildMaskPayload
    → results queue
```

이 위치를 선택하는 이유는 다음과 같다.

- 프레임 BGRA 버퍼가 이미 메모리에 있다.
- 별도 FFmpeg seek와 재디코딩을 피할 수 있다.
- `FrameMaskProvider`에 최종 결과를 쓰기 전에 후보를 정리할 수 있다.
- ROI 결과를 별도 장기 저장하지 않아 메모리 증가를 제한할 수 있다.

병렬 검출에서는 보조 detector를 여러 worker가 동시에 공유하지 않는다. 다음 중 하나를 사용한다.

- worker별 보조 detector session 생성
- 보조 호출만 제한된 semaphore로 직렬화

실행 방식은 메모리와 wall-clock을 비교해 결정한다. `detectMs` 누적값만으로 속도를 판단하지 않고 `totalMs`와 `analysisTotalMs`를 기준으로 한다.

## 보조 모델 선택

첫 구현의 보조 모델은 현재 품질 기준을 가진 FaceONNX가 적절하다.

- FaceONNX: 현재 baseline 및 ROI 코드 재사용 가능
- RetinaFace: 별도 ONNX adapter와 동일 영상 A/B가 먼저 필요
- SCRFD: 기존 FaceShield 품질 게이트 실패 기록이 있어 보조 모델 우선순위에서 제외
- YuNet: 속도 후보이지만 품질 보정 목적의 강한 보조 모델로는 우선순위가 낮음

## 성능 계산과 기대 조건

새 방식의 대략적인 비용은 다음과 같이 본다.

```text
새 분석 시간 ≈ YOLO 전체 검출 시간
             + ROI 개수 × 보조 ROI 검출 시간
             + ROI crop·병합·매칭 비용
```

현재 위험 프레임 전체 재검증과 비교해 다음 조건일 때 유리하다.

```text
보조 ROI 호출 비용 합계 < 위험 프레임 전체 검출 비용
```

보조 모델 입력 크기가 고정이면 ROI 면적이 작아져도 추론 비용이 크게 줄지 않을 수 있다. 따라서 ROI 개수 제한, ROI 병합, 이미 디코딩된 버퍼 재사용이 필수다.

## 로그 추가 항목

다음 항목을 `AutoRunSummary` 또는 별도 ROI summary에 남긴다.

- `roiCandidateCount`
- `roiMergedCount`
- `roiAttemptCount`
- `roiAcceptedCount`
- `roiRejectedCount`
- `roiRemovedCount`
- `roiDetectMs`
- `roiCropMs`
- `roiFallbackGlobalFrames`
- `roiSkippedByBudget`
- 보조 모델 실행 provider

기존 로그와 함께 다음을 비교한다.

- `analysisTotalMs`
- `AutoRunSummary.totalMs`
- `ExportRunSummary.totalMs`
- `finalShortGaps`
- `finalPerFaceShortGaps`
- `finalLargeJumps`
- `finalMissRecovery`
- `finalFpSuppressed`
- `sampleShortGaps`
- `samplePerFaceShortGaps`
- `sampleMissRecovery`
- `droppedVideoPackets`

## 품질 게이트

YOLO 기존 baseline과 비교할 때 다음 조건을 적용한다.

### 필수 무회귀

- 출력 프레임 수 동일
- 출력 FPS·재생시간 동일
- `droppedVideoPackets=0`
- 마지막 프레임 포함 전체 구간 처리
- 장면 전환 carry 잔상 증가 없음
- 대표 검토 프레임의 얼굴 누락 증가 없음

### 채택 조건

- 저신뢰도 후보의 실제 얼굴 누락이 감소하거나 유지된다.
- 오탐이 증가하지 않는다.
- `sampleShortGaps`, `samplePerFaceShortGaps`가 악화되지 않는다.
- ROI 보조 비용을 포함한 `analysisTotalMs`가 허용 범위 안이다.
- ROI 보조가 없는 YOLO baseline보다 품질 또는 속도 중 하나가 명확히 개선된다.

`onlyBaseline`·`onlyOptimized`·IoU 차이는 후보 차이를 찾는 신호로 사용하고, 실제 누락·오탐 판정은 대표 프레임 검토 또는 별도 GT로 확정한다.

## 단계별 구현 순서

1. 원시 YOLO 후보와 필터 후 후보를 구분하는 임시 후보 구조 추가
2. 후보 위험도 판정과 ROI padding·clamp·병합 유틸리티 추가
3. 현재 `FaceTrackRoiRefiner`의 BGRA ROI 호출·좌표 복원 로직 재사용
4. `GeneratePipelinedDetectAll*` 검출 hot path에 ROI 보조 검증 연결
5. 기존 후보 교체·제거·새 후보 추가 정책 구현
6. 좌표 없는 누락용 global scan과 ROI 검증 경로 분리
7. worker별 session 또는 semaphore 방식의 동시성 검증
8. ROI별 로그와 품질 gate 추가
9. 짧은 문제 구간 → 30초 sample → 6분 사본 순서로 실행 검증
10. `dotnet build FaceShield.sln` 및 실제 export 무결성 검증 후 채택 여부 결정

## 채택 전 금지 사항

- 외부 모델 benchmark만으로 기본 detector 변경
- 낮은 confidence 후보를 보조 결과 없이 즉시 삭제
- ROI 결과와 원본 후보의 geometry 확인 없이 새 얼굴 추가
- 한 프레임의 ROI 수 제한 없이 보조 모델 호출
- `detectMs`만 보고 속도 개선으로 판정
- global scan을 제거해 좌표 없는 얼굴 누락 경로를 없애는 변경

## 최종 방향

YOLO 전체 검출을 유지하고, **좌표가 있는 불확실 후보만 FaceONNX ROI로 재검증**하는 것이 1차 구현 방향이다. 좌표가 없는 누락은 기존 tracking·주기적 global scan으로 보완한다.

이 구조가 통과하면 전체 프레임을 다시 읽는 현재 위험 프레임 cascade의 비용을 줄이면서, 작은 얼굴·가장자리 얼굴·일시적인 저신뢰도 후보의 품질을 보완할 수 있다. 실제 성능 향상과 품질 향상은 위 게이트를 동일 영상으로 통과한 뒤에만 확정한다.
