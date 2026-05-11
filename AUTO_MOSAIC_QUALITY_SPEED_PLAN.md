# 자동 검출 모자이크 품질/속도 개선 설계 기획

## 목적
현재 자동 검출 후 모자이크 처리의 핵심 문제는 두 가지다.

- 결과 품질: 얼굴 미탐, 오탐, 박스 튐, 프레임 간 모자이크 흔들림이 결과물 품질을 떨어뜨린다.
- 처리 시간: 자동 검출, 프레임 변환, 마스크/블러, export/encode 전체 시간이 길다.

이 문서는 모델 교체까지 포함해 품질을 유지하거나 높이면서 시간을 줄이기 위한 구현 계획이다. 단순히 검출 간격을 늘리거나 더 약한 모델로 바꾸는 방식은 품질 저하 가능성이 크므로 기본 전략에서 제외한다.

## 현재 확인한 프로젝트 구조
FaceShield는 .NET 8 Avalonia 데스크톱 앱이며 솔루션은 단일 프로젝트 `FaceShield.csproj`로 구성되어 있다. 현재 핵심 진입점은 다음과 같다.

- `ViewModels/Pages/HomePageViewModel.cs`: 홈 자동 실행, 자동 완료 후 저장, 예상 시간/상태 표시.
- `ViewModels/Pages/WorkspaceViewModel.cs`: 자동 검출 실행, 자동 튜닝, 후처리, export 연결.
- `Services/Analysis/AutoMaskGenerator.cs`: 자동 검출 파이프라인, sparse pipeline, tracking, ROI, 진행 로그.
- `Services/Analysis/AutoMaskOptions.cs`: downscale, tracking, 검출 간격, 병렬 detector 수 옵션.
- `Services/FaceDetection/IBgraFaceDetector.cs`: BGRA 기반 검출기 교체용 인터페이스.
- `Services/FaceDetection/FaceDetectorFactory.cs`: 검출기 생성 팩토리.
- `Services/FaceDetection/FaceDetectorBackend.cs`: 현재 `FaceOnnx`만 등록된 backend enum.
- `Services/FaceDetection/FaceOnnxDetector.cs`: 현재 기본 얼굴 검출 구현.
- `Services/FaceDetection/DetectorAutoTuner.cs`: 자동 실행 시작 시 ONNX 실행 옵션/세션 수 측정.
- `Services/Video/VideoExportService.cs`: export, 색상 변환, direct face rect blur, bitmap mask blur, encode.
- `Services/Video/MaskedVideoExporter.cs`: 얼굴 영역 직접 블러 및 마스크 블러 적용.
- `Services/Video/Session/ExactFrameProvider.cs`: 프레임 정확 조회와 preview 안정성.

현재 구조상 모델 교체는 `IBgraFaceDetector`, `FaceDetectorFactory`, `FaceDetectorBackend`, `FaceDetectorFactoryOptions`를 확장하는 방식이 맞다. `AutoMaskGenerator`나 `WorkspaceViewModel`에 새 모델을 직접 박으면 이후 비교와 롤백이 어려워진다.

## 현재 상태 요약
이미 들어간 기반 작업:

- `FaceOnnxDetector`가 `IBgraFaceDetector`를 구현한다.
- `AutoMaskGenerator`가 BGRA/raw 기반 파이프라인과 sparse 병렬 파이프라인을 지원한다.
- `WorkspaceViewModel.RunAutoCoreAsync()`에서 `DetectorAutoTuner`를 `Task.Run`으로 실행해 UI thread 정지를 줄인다.
- `VideoExportService`는 자동 face rect가 있는 프레임에서 전체 bitmap mask 대신 `ApplyFaceRectsAndBlur()` direct blur 경로를 쓸 수 있다.
- export 로그에 `bitmapMaskFrames`, `directFaceFrames`, `swsToBgraMs`, `maskMs`, `swsToEncMs`, `encodeMs`, `totalMs`가 남는다.
- 홈 자동 진행 상태는 export 단계에서도 숨기지 않는 방향으로 수정되어 있다.

남은 한계:

- `FaceDetectorBackend`는 아직 `FaceOnnx` 하나뿐이다.
- 실제 10분 문제 영상 기준으로 자동 검출 시간과 export 시간이 분리 측정되어 있지 않다.
- 검출 품질 평가는 로그만으로 부족하고, 미탐/오탐/박스 튐 구간을 샘플로 수집해야 한다.
- 트래킹은 프레임 재사용과 smoothing 중심이라, 얼굴별 track identity를 안정적으로 유지하는 구조까지는 아니다.
- 모델 후보별 배포 크기, native/provider 안정성, Windows/macOS 지원 여부가 아직 확정되지 않았다.

## 성능/품질 목표
정량 목표는 실제 샘플 측정 후 확정한다. 1차 목표는 다음 기준으로 잡는다.

- 자동 검출: 10분 영상에서 검출 단계가 export보다 과도하게 길면 detector/backend 교체와 pipeline 개선을 우선한다.
- export: `maskMs`, `swsToBgraMs`, `swsToEncMs`, `encodeMs` 중 가장 큰 항목부터 줄인다.
- 품질: 얼굴 미탐이 늘어나는 옵션은 기본값으로 쓰지 않는다.
- 안정성: DirectML/CoreML/provider 자동 확장은 검증된 경로만 기본값으로 둔다.
- UX: 예상 시간, 진행 상태, 취소 가능성은 성능 개선 과정에서도 유지한다.

## 측정 먼저 할 항목
실제 문제 영상으로 다음 로그를 반드시 확보한다.

```text
[AutoTune] ...
[AutoMask] frames=..., readMs=..., detectMs=..., maskMs=..., totalMs=...
[AutoMaskPipe] frames=..., decodeMs=..., detectMs=..., totalMs=...
[AutoMaskSparsePipe] detects=..., decoded=..., decodeMs=..., detectMs=..., totalMs=...
[OnnxPerf] calls=..., preMs=..., inferMs=..., totalMs=...
[Export] done frames=..., bitmapMaskFrames=..., directFaceFrames=..., swsToBgraMs=..., maskMs=..., swsToEncMs=..., encodeMs=..., totalMs=...
```

기록 포맷:

| 샘플 | 해상도/FPS | 길이 | 옵션 | 자동 검출 total | detectMs | export total | maskMs | encodeMs | 품질 메모 |
| --- | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | --- |
| 문제 영상 A | 확인 필요 | 확인 필요 | 현재 기본값 | 확인 필요 | 확인 필요 | 확인 필요 | 확인 필요 | 확인 필요 | 미탐/오탐 구간 기록 |

측정 전까지 "확실히 빨라졌다" 또는 "모델 교체만 하면 해결된다"는 결론은 내리지 않는다.

## 모델 교체 전략
모델 교체는 한 번에 기본 모델을 갈아엎지 않고, backend를 추가한 뒤 동일 샘플에서 비교한다.

### 후보군
1. 현재 `FaceONNX`
   - 기준선으로 유지한다.
   - 기존 품질/배포 안정성을 비교 기준으로 삼는다.

2. SCRFD 계열 ONNX
   - 작은 얼굴과 다양한 각도에서 품질이 좋은 후보로 검토한다.
   - ONNX Runtime CPU/DirectML/CoreML provider에서 입력/후처리 구현이 필요하다.
   - NMS, anchor/grid decode, threshold 튜닝을 직접 구현해야 할 수 있다.

3. RetinaFace 계열 ONNX
   - 정확도 후보로 검토한다.
   - 속도가 느릴 수 있으므로 1차 전체 검출보다는 의심 구간 재검출용 2차 모델 후보로 둔다.

4. BlazeFace/MediaPipe 계열
   - 빠른 1차 검출 후보로 검토한다.
   - 영상 속 작은 얼굴, 측면 얼굴, 마스크/가림 상황에서 품질 검증이 필요하다.

5. YOLO face 계열 ONNX
   - 배치 처리, GPU/provider 활용 가능성을 검토한다.
   - 얼굴 박스만 필요한 현재 구조와 잘 맞지만, 모델 파일 출처와 라이선스 확인이 필요하다.

### 권장 구조
단일 모델 교체보다 2단계 구조를 우선 검토한다.

1. 빠른 1차 detector
   - 전체 프레임 또는 sparse frame에서 빠르게 얼굴 후보를 찾는다.
   - 낮은 confidence 후보도 일단 track 후보로 남긴다.

2. 강한 2차 verifier/refiner
   - 미탐 가능성이 높은 구간, 갑자기 얼굴이 사라진 구간, 박스가 크게 튄 구간만 재검출한다.
   - 모든 프레임에 강한 모델을 돌리지 않는다.

3. track 기반 보정
   - 얼굴별 track id를 유지한다.
   - 짧은 미탐 구간은 이전/다음 track으로 보간한다.
   - scene cut 또는 급격한 움직임에서는 보간을 끊는다.

## 구현 단계
### 1단계: 측정/진단 모드 강화
목표는 병목을 검출, 디코딩, 변환, 블러, 인코딩으로 분리하는 것이다.

- `AutoMaskGenerator`에 run summary 객체를 추가한다.
- `WorkspaceViewModel`에서 자동 검출 완료 시 summary를 로그와 UI 상태에 남긴다.
- export summary와 auto summary를 같은 run id로 묶는다.
- 홈 자동 저장 경로에서도 동일한 run id를 남긴다.
- 미탐/오탐 확인용으로 "이상 후보 프레임" 목록을 저장하거나 export 전 workspace에서 확인 가능하게 한다.

산출물:

- `Services/Analysis/AutoMaskRunSummary.cs`
- `Services/Video/ExportRunSummary.cs`
- 로그 예: `[AutoRunSummary] runId=..., autoMs=..., exportMs=..., detector=..., options=...`

### 2단계: detector backend 확장 지점 정리
목표는 새 모델을 안전하게 붙이고 동일 옵션으로 비교하는 것이다.

- `FaceDetectorBackend`에 새 후보 enum을 추가한다.
- `FaceDetectorFactoryOptions`에 backend별 options를 분리한다.
- 모든 detector는 `IBgraFaceDetector`를 구현한다.
- detector별 입력 크기, threshold, NMS, provider 옵션을 명시한다.
- backend 선택은 임시로 개발용 설정 또는 내부 옵션으로만 열고, 검증 전 사용자 기본값으로 노출하지 않는다.

예상 변경 파일:

- `Services/FaceDetection/FaceDetectorBackend.cs`
- `Services/FaceDetection/FaceDetectorFactory.cs`
- `Services/FaceDetection/FaceDetectorFactoryOptions.cs`
- `Services/FaceDetection/*DetectorOptions.cs`
- `Services/FaceDetection/*Detector.cs`

### 3단계: 모델 후보 A/B 벤치
목표는 같은 영상, 같은 옵션, 같은 export 경로에서 후보를 비교하는 것이다.

비교 항목:

- 검출 total time
- frame당 detector latency
- 미탐 구간 수
- 오탐 구간 수
- 얼굴 박스 jitter
- 작은 얼굴 검출률
- DirectML/CoreML/CPU fallback 안정성
- 배포 파일 크기와 native 의존성

판정:

- 빠르지만 미탐이 늘면 기본 detector로 쓰지 않는다.
- 정확하지만 느리면 2차 verifier/refiner로 제한한다.
- provider 초기화 실패나 장비별 편차가 크면 기본값에서 제외한다.

### 4단계: track-first 자동 모자이크
목표는 `DetectEveryNFrames > 1`에서도 얼굴이 끊기거나 튀지 않게 만드는 것이다.

- `FaceTrack` 모델을 추가한다.
- 검출 결과를 frame 단위 dictionary만으로 보지 않고 track 단위로 관리한다.
- IoU, 중심점 거리, 크기 변화율로 같은 얼굴 여부를 판단한다.
- 짧은 미탐 구간은 이전/다음 검출 박스로 보간한다.
- 갑작스러운 scene cut, 화면 전환, 큰 위치 변화에서는 track을 끊는다.
- 수동 편집 프레임은 자동 smoothing/track 보정 대상에서 제외한다.

예상 추가 파일:

- `Services/Analysis/FaceTrack.cs`
- `Services/Analysis/FaceTrackBuilder.cs`
- `Services/Analysis/FaceTrackInterpolator.cs`

### 5단계: ROI 재검출과 품질 게이트
목표는 전체 프레임 재검출 없이 미탐 가능성이 높은 구간만 보강하는 것이다.

- 얼굴이 갑자기 사라진 구간을 `suspicious gap`으로 표시한다.
- 이전 track 주변 ROI를 확대해서 재검출한다.
- confidence가 낮거나 박스가 튄 프레임만 강한 detector로 재검출한다.
- 재검출 결과가 기존 track과 충돌하면 더 안정적인 쪽을 선택한다.
- 보정 전/후 face rect를 로그로 비교한다.

품질 게이트:

- 같은 얼굴 track이 3~5프레임 이하로 사라졌다가 돌아오면 보간 또는 ROI 재검출을 시도한다.
- 박스 면적이 직전 대비 비정상적으로 커지거나 작아지면 후보로만 두고 즉시 반영하지 않는다.
- 얼굴 수가 갑자기 크게 변하면 scene cut 여부를 먼저 판단한다.

### 6단계: export 경로 추가 최적화
목표는 자동 face rect 경로를 최대한 bitmap mask 경로로 떨어뜨리지 않는 것이다.

- 자동 face rect만 있는 프레임은 계속 `ApplyFaceRectsAndBlur()` 경로를 사용한다.
- 수동 mask가 있는 프레임만 `ApplyMaskAndBlur()`를 사용한다.
- face rect가 없는 프레임은 BGRA 변환 없이 decode frame을 바로 encode한다.
- `swsToBgraMs`와 `swsToEncMs`가 크면 변환 재사용 또는 encoder pixel format 조정을 검토한다.
- `encodeMs`가 병목이면 preset/profile/CRF 또는 하드웨어 인코딩을 별도 실험한다.

주의:

- 화질 손실을 일으키는 인코딩 옵션 변경은 기본값으로 바로 넣지 않는다.
- 오디오 sync와 frame count exactness는 유지해야 한다.

### 7단계: UX 유지
성능 개선 중에도 사용자가 느끼는 상태 표시는 유지한다.

- 자동 검출 중 예상 시간 표시 유지.
- export 중 예상 시간 또는 단계 표시 유지.
- 자동 튜닝 중 UI thread block 금지.
- 취소 시 정상 취소로 처리하고 오류처럼 보이지 않게 한다.
- 홈 자동 저장과 워크스페이스 자동 실행의 상태 표시를 별도로 확인한다.

## 기본값 정책
검증 전:

- 기본 detector는 현재 안정적인 `FaceOnnx` 유지.
- 새 backend는 내부 옵션 또는 개발 설정으로만 선택.
- GPU/provider는 검증 전까지 기본값으로 승격하지 않는다.
- 검출 간격 증가나 threshold 완화는 기본 품질 개선책으로 쓰지 않는다.

검증 후:

- 품질이 같은데 빠른 detector는 기본 1차 detector 후보로 승격한다.
- 느리지만 미탐을 줄이는 detector는 2차 verifier/refiner로 사용한다.
- 검증된 GPU/provider는 기본 후보로 승격하되, 장비별 실패는 CPU fallback과 상태 로그로 명확히 남긴다.

## 검증 계획
최소 검증:

- `dotnet build FaceShield.sln`
- 짧은 샘플 영상 open, preview, 자동 검출, workspace 보정, export 확인.
- 문제 10분 영상 자동 저장 경로 실행.
- Windows `win-x64` publish output native DLL 확인.
- macOS ARM64는 모델/native 변경이 있으면 별도 publish와 실행 확인.

품질 검증:

- 미탐이 발생한 구간을 frame index로 기록.
- 오탐이 발생한 구간을 frame index로 기록.
- 박스 튐이 눈에 띄는 구간을 frame index로 기록.
- 기존 FaceONNX 결과와 새 backend 결과를 같은 frame에서 비교.

성능 검증:

- 자동 검출 total.
- export total.
- detector latency.
- `maskMs`, `encodeMs`, `swsToBgraMs`, `swsToEncMs`.
- UI 멈춤 여부.
- ETA/status 표시 유지 여부.

## 작업 우선순위
1. 실제 문제 영상 기준 baseline 로그 확보.
2. run summary 저장 구조 추가.
3. `FaceDetectorBackend` 확장 구조 정리.
4. 새 detector 후보 1개를 proof-of-concept로 추가.
5. 같은 영상에서 FaceONNX와 후보 detector 비교.
6. track-first 보정 추가.
7. ROI 재검출/2차 verifier 추가.
8. export 병목별 최적화.
9. 기본값 승격 여부 결정.

## 리스크
- 새 모델의 라이선스나 배포 가능 여부가 불명확하면 제품 기본값으로 쓸 수 없다.
- DirectML/CoreML provider는 장비별로 초기화 실패 또는 느린 fallback이 생길 수 있다.
- 검출 속도만 개선하고 track 보정을 하지 않으면 결과물은 더 불안정해질 수 있다.
- 하드웨어 인코딩은 속도는 빨라질 수 있지만 화질, 호환성, 배포 의존성 리스크가 있다.
- 측정 없이 여러 최적화를 한 번에 넣으면 어떤 변경이 품질/속도에 영향을 줬는지 알 수 없다.

## 결론
이번 문제는 "검출 모델만 가벼운 것으로 교체"하는 식으로 처리하면 품질 저하 가능성이 크다. 현재 프로젝트에는 이미 detector factory, BGRA detector interface, sparse pipeline, direct face rect export, export timing log가 있으므로 이 기반을 살려야 한다.

가장 현실적인 방향은 다음 순서다.

1. 실제 문제 영상 기준으로 병목을 수치화한다.
2. backend 교체 구조를 확장해 새 detector 후보를 안전하게 붙인다.
3. 빠른 1차 검출과 강한 2차 재검출을 분리한다.
4. track-first 보정으로 미탐과 박스 튐을 줄인다.
5. export 병목은 로그 항목별로 줄인다.

이 순서로 가야 품질을 희생하지 않고 자동 검출 후 모자이크 처리 시간을 줄일 수 있다.

## 2026-05-11 1차 구현 기록
작업 브랜치: `plan/auto-mosaic-quality-speed`

반영 내용:

- `Services/Analysis/AutoMaskGenerator.cs`의 sparse tracking materialize 경로를 수정했다.
- 기존에는 검출 키프레임 사이 중간 프레임에 마지막 얼굴 박스를 단순 복사했다.
- 수정 후에는 다음 검출 키프레임의 얼굴 박스와 IoU, 중심점 거리, 면적 변화율로 같은 얼굴 후보를 매칭한다.
- 매칭 가능한 경우 중간 프레임의 얼굴 박스를 선형 보간한다.
- 짧은 검출 실패 gap은 앞뒤 긍정 검출이 같은 얼굴로 판단될 때 보간으로 채운다.
- gap 보간 중 매칭되지 않은 박스는 복사하지 않아 오탐/잔상 유지 위험을 줄인다.
- 매칭할 수 없는 경우에는 기존처럼 현재 키프레임 박스를 제한된 구간에만 사용한다.
- 마지막 검출 박스를 영상 끝까지 무제한 복사하지 않고, 다음 검출 구간 또는 검출 간격 안에서만 materialize한다.
- `[AutoMaskSparsePipe] done` 로그에 `interpolated` 값을 추가해 sparse tracking으로 채워진 프레임 수를 확인할 수 있게 했다.

목적:

- `DetectEveryNFrames > 1`과 tracking 조합에서 검출 횟수를 늘리지 않고도 박스 계단 현상과 프레임 간 흔들림을 줄인다.
- 얼굴이 사라진 뒤 마지막 박스가 과도하게 유지되는 품질 리스크를 줄인다.
- 추가 detector 호출 없이 중간 프레임을 보정하므로 속도 비용은 낮게 유지한다.

아직 확실하지 않은 점:

- 실제 문제 영상에서 품질 개선 정도와 처리 시간 변화는 아직 측정하지 않았다.
- 다음 단계에서는 실제 샘플 로그의 `detectMs`, `interpolated`, `totalMs`, export timing을 비교해야 한다.

## 2026-05-11 2차 구현 기록
반영 내용:

- `Services/Analysis/AutoMaskRunSummary.cs`를 추가했다.
- `AutoMaskGenerator`가 자동 검출 완료 시 `LastRunSummary`를 보관하고 `[AutoRunSummary]` 로그를 남기도록 했다.
- summary에는 mode, totalFrames, processed, decoded, detects, interpolated, read/decode/detect/mask/total ms, downscale, tracking, detect interval, parallel detector count, ROI summary가 포함된다.

목적:

- 실제 문제 영상 실행 후 자동 검출 병목을 export 로그와 분리해서 확인한다.
- `sequential`, `pipe-single`, `pipe-parallel`, `sparse-pipe-parallel` 경로별 처리 시간을 같은 포맷으로 비교한다.
- 향후 모델 교체나 ROI 재검출을 적용할 때 품질/속도 비교 기준선을 확보한다.

아직 확실하지 않은 점:

- 실제 문제 영상 로그가 없으므로 어떤 경로가 최종 병목인지는 아직 알 수 없다.

## 2026-05-11 3차 구현 기록
반영 내용:

- `Services/FaceDetection/DetectorAutoTuner.cs` 내부 후보 측정 루프에 `CancellationToken`을 연결했다.
- `WorkspaceViewModel.RunAutoCoreAsync()`에서 자동 튜닝 호출 시 자동 실행 취소 토큰을 전달하도록 했다.
- 튜닝 시작 전, 샘플 프레임 로딩, 후보 측정 전, warm-up/측정 루프 내부에서 취소를 확인한다.

목적:

- 자동 실행 초반 튜닝 중 취소했을 때 UI가 오래 멈춘 것처럼 보이는 위험을 줄인다.
- 성능 튜닝을 유지하면서도 사용자가 취소를 누르면 더 빠르게 정상 취소 경로로 빠진다.

아직 확실하지 않은 점:

- 각 detector inference 호출 자체는 외부 라이브러리 호출이므로 호출 중간을 강제로 중단할 수는 없다.
- 취소 반응성 개선 정도는 실제 영상과 장비에서 확인해야 한다.

## 2026-05-11 4차 구현 기록
반영 내용:

- 초기 실험에서는 신규/초기 자동 모자이크 기본값을 `추적 사용=true`, `2프레임마다 검출`로 조정했다.
- 이후 실제 `srcTest` 6분 구간 smoke에서 `DetectEveryNFrames=2`가 baseline-only/optimized-only 프레임 차이를 만들 수 있음을 확인했다.
- 최종 기본값은 품질 우선 기준에 맞춰 `DetectEveryNFrames=1`, `DownscaleRatio=1.0`, `ParallelDetectorCount=2`로 정리했다.
- 기존 저장 설정이 있으면 저장된 사용자 설정이 우선 적용된다.
- `Services/Workspace/WorkspaceStateStore.cs`의 자동 설정 기본값도 최종 기본값과 맞췄다.
- 구버전 저장 설정에는 `SettingsVersion`이 없으므로, 이전 실험 기본값인 `DetectEveryNFrames=2`나 downscale 값이 기본처럼 남지 않도록 legacy 설정은 안전 기본값으로 마이그레이션한다.

목적:

- 기본 경로에서는 모든 프레임 검출을 유지해 baseline과 같은 품질을 목표로 한다.
- 속도 개선은 검출 간격 증가나 다운스케일이 아니라 `pipe-parallel` 병렬 검출 경로로 가져간다.

아직 확실하지 않은 점:

- 전체 영상의 다양한 얼굴/장면 구간에서도 baseline과 완전 일치하는지는 추가 검증이 필요하다.

## 2026-05-11 5차 구현 기록
반영 내용:

- `UseTracking=true`, `DetectEveryNFrames > 1`이면 병렬 세션 수가 1로 튜닝되더라도 sparse pipeline을 타도록 수정했다.
- 기존 조건은 `ParallelDetectorCount > 1`일 때만 sparse pipeline을 사용해서, 자동 튜닝 결과가 1세션이면 sequential tracking으로 떨어질 수 있었다.

목적:

- 사용자가 `DetectEveryNFrames > 1` 추적 옵션을 선택했을 때 sparse materialize 보간 로직을 안정적으로 적용한다.
- 장비에 따라 1세션이 가장 빠르게 튜닝되어도 품질 보정 경로를 잃지 않게 한다.

## 2026-05-11 6차 구현 기록
반영 내용:

- `Services/Video/ExportRunSummary.cs`를 추가했다.
- `VideoExportService`가 export 완료 시 `LastExportSummary`를 보관하고 `[ExportRunSummary]` 로그를 남기도록 했다.
- `WorkspaceViewModel.SaveVideoAsync()`에서 export summary를 workspace 로그에도 연결했다.
- `AutoMaskRunSummary`와 `ExportRunSummary`에 `runId`를 추가해 자동 검출과 export 로그를 같은 실행 단위로 묶을 수 있게 했다.
- `AutoMaskRunSummary`에 detector 이름을 추가해 모델/backend 교체 후 같은 로그 포맷으로 기준선과 후보를 비교할 수 있게 했다.
- `WorkspaceViewModel.RunAutoCoreAsync()`는 자동 실행마다 `auto-{guid}` 형식의 run id를 만들고, 자동 검출 후 export까지 같은 값을 전달한다.
- 수동 export는 `export-{guid}` 형식의 run id를 남겨 자동 실행 로그와 구분한다.
- 블러 대상이 없어 remux-copy로 조기 종료되는 export 경로도 `[ExportRunSummary]`를 남기도록 했다.

목적:

- 자동 검출 summary와 export summary를 같은 테스트 실행에서 비교한다.
- `swsToBgraMs`, `maskMs`, `swsToEncMs`, `encodeMs`, `totalMs`를 구조화해 다음 최적화 우선순위를 잡는다.
- 홈 자동 저장 경로처럼 자동 검출 직후 export가 이어지는 경우에도 `[AutoRunSummary]`와 `[ExportRunSummary]`를 `runId`로 정확히 매칭한다.
- 얼굴이 검출되지 않는 테스트 구간에서도 export 단계가 누락되지 않았는지 확인할 수 있다.
- 실제 6분 구간 smoke에서 sparse face rect가 있는 상태로 hybrid copy export가 `Invalid argument`를 발생시키는 것을 확인했다.
- encoder 패킷 timestamp rescale을 보강했지만 같은 출력 스트림에서 재인코딩 패킷과 원본 패킷을 섞는 경계 실패가 계속되어, 블러 대상이 있는 경우의 hybrid copy는 비활성화했다.
- 블러 대상이 전혀 없는 remux-copy 고속 경로는 유지한다.

## 2026-05-11 srcTest smoke 시도
대상:

- `srcTest/260102_jp_10.mp4`
- 확인된 메타데이터: 3840x2160, 약 29.97fps, 약 1067.6초, 약 31996프레임

시도:

- 전체 영상은 오래 걸리므로 10초 구간을 잘라 smoke 실행을 시도했다.
- WSL 임시 harness에서는 FFmpeg native load가 실패했다. 시스템 `libavcodec.so.60`은 로드되지만 `FFmpeg.AutoGen 8.0.0` 바인딩과 런타임 FFmpeg ABI가 맞지 않아 `avcodec_version()` 단계에서 중단됐다.
- Windows `dotnet.exe`로 repo의 Windows native DLL 경로를 이용한 smoke도 시도했으나, 현재 WSL interop에서 `UtilBindVsockAnyPort` 오류로 실행이 시작되지 않았다.

결론:

- 최초 WSL 단독 실행에서는 FFmpeg ABI 문제와 Windows interop 문제로 smoke를 완료하지 못했다.
- 이후 Windows PowerShell harness와 WSL ffmpeg 클립 생성을 조합해 `srcTest` 실제 짧은 구간 smoke를 완료했다.
- 실제 영상 기준 `[AutoRunSummary]`, `[ExportRunSummary]`, baseline/optimized face rect 비교 로그를 확보했다.
- 아직 전체 17분 영상 기준 최종 수치는 확보하지 않았다.

추가 산출물:

- `scripts/run-srcTest-smoke.ps1`
- Windows PowerShell에서 `.\scripts\run-srcTest-smoke.ps1 -Start 00:02:00 -Seconds 10` 형태로 실행하면 10초 클립을 만들고 기준선(`baseline-all-frames`)과 개선 경로(`optimized-track-2`)의 자동 검출/export summary를 출력한다.
- 기준선 실행을 생략하려면 `-SkipBaseline`을 붙인다.
- 전제: Windows `dotnet`과 `ffmpeg` CLI가 PATH에 있어야 한다.
- 스크립트가 생성하는 C# harness는 임시 프로젝트로 `dotnet build` 검증을 통과했다.
- Windows PATH에 `ffmpeg`가 없으면 `-SkipTrim -Source <이미 만든 짧은 클립>`으로 실행할 수 있게 했다.
- 3초 클립 smoke 1회 결과 해당 구간에서는 얼굴이 0개 검출됐다. 이 결과는 속도 경로 확인에는 유효하지만 품질 비교 샘플로는 부족하다.

실제 6분 구간 3초 클립 smoke:

| 케이스 | 경로 | 검출 호출 | 보간 | faceMaskFrames | 자동 검출 total | detectMs | export total | directFaceFrames |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| baseline-all-frames | `pipe-single` | 90 | 0 | 8 | 73,731ms | 73,348ms | 7,814ms | 8 |
| optimized-track-2 | `sparse-pipe-parallel` | 45 | 4 | 8 | 26,209ms | 25,857ms | 7,978ms | 8 |
| optimized-track-2-parallel-2 | `sparse-pipe-parallel` | 45 | 4 | 8 | 20,808ms | 40,478ms | 9,369ms | 8 |
| optimized-track-3-parallel-2 | `sparse-pipe-parallel` | 30 | 6 | 9 | 12,759ms | 23,227ms | 8,559ms | 9 |
| optimized-all-1-parallel-2 | `pipe-parallel` | 90 | 0 | 8 | 36,819ms | 72,635ms | 7,061ms | 8 |
| optimized-track-1-parallel-2 | `pipe-parallel` | 90 | 0 | 8 | 33,638ms | 66,588ms | 6,604ms | 8 |
| optimized-track-2-scale-0.75 | `sparse-pipe-parallel` | 45 | 4 | 8 | 19,948ms | 19,614ms | 8,553ms | 8 |
| optimized-track-2-scale-0.5 | `sparse-pipe-parallel` | 45 | 3 | 6 | 14,196ms | 13,933ms | 9,131ms | 6 |

해석:

- 같은 92프레임 클립에서 최적화 경로는 검출 호출을 90회에서 45회로 줄였다.
- faceMaskFrames는 둘 다 8개로 같고, 최적화 경로는 중간 프레임 4개를 보간했다.
- 자동 검출 total은 약 73.7초에서 약 26.2초로 줄었다.
- 원본 해상도 유지 상태에서 parallel detector 2개를 쓰면 faceMaskFrames 8개를 유지하면서 자동 검출 total이 약 20.8초까지 줄었다. `detectMs` 합계는 병렬 thread 누적 시간이라 wall-clock인 `totalMs`를 우선 판단한다.
- 다만 `DetectEveryNFrames=2` 비교에서는 baseline-only frame 27, optimized-only frame 87이 발생했다. 공통 프레임 IoU는 높았지만 프레임 단위 완전 일치는 아니므로 품질 우선 기본값으로는 부적절하다.
- 모든 프레임 검출을 유지한 `parallel=2` 경로는 baseline과 `baselineFrames=8`, `optimizedFrames=8`, `common=8`, `onlyBaseline=0`, `onlyOptimized=0`, `avgBestIou=1.000`, `minBestIou=1.000`으로 완전 일치했다.
- 앱 기본 조합인 `UseTracking=true + DetectEveryNFrames=1 + parallel=2`도 `pipe-parallel`로 진입했고, baseline과 프레임/박스가 완전 일치했다.
- 따라서 기본 품질 경로는 `DetectEveryNFrames=1 + parallel pipeline`으로 둔다.
- `DetectEveryNFrames=3`은 이 짧은 구간에서 더 빨랐지만, 빠른 움직임/장면 전환에서 보간 의존도가 커지는 품질 리스크가 있으므로 기본값으로 적용하지 않는다.
- 0.75 다운스케일은 faceMaskFrames 8개를 유지하면서 자동 검출 total을 약 19.9초까지 줄였다.
- 0.5 다운스케일은 더 빠르지만 faceMaskFrames가 6개로 줄어 품질 손실이 확인되어 기본값 후보에서 제외했다.
- export는 direct face rect 경로를 사용했고, 두 케이스 모두 완료됐다.
- 이 smoke는 짧은 구간 기준이므로 전체 17분 영상의 최종 수치로 일반화하면 안 된다.

기본값 판단:

- 신규/초기 자동 모자이크 downscale 기본값은 `1.0 + BalancedBilinear`로 유지한다.
- parallel session 기본값은 기존처럼 `2`를 유지한다. 실제 smoke에서 원본 해상도 품질을 유지한 채 wall-clock 개선이 확인됐고, 자동 튜너가 장비별로 더 나은 세션 수를 고를 수 있다.
- `DetectEveryNFrames` 기본값은 `1`로 유지한다. `2`와 `3`은 추가 품질 검증 전까지 사용자 선택/실험 옵션으로만 둔다.
- `UseTracking=true`라도 `DetectEveryNFrames=1`이면 모든 프레임 검출이므로 parallel all-frame pipeline을 사용할 수 있게 했다.
- `AutoSettingsState.SettingsVersion=3`을 사용해 구버전 저장 설정은 품질 우선 기본값으로 보정한다.
- `0.75`는 한 구간 smoke에서 속도 이득과 동일 faceMaskFrames를 보였지만, 작은 얼굴/측면 얼굴/먼 얼굴 품질을 전체 영상에서 보장하지 못하므로 기본값으로 적용하지 않는다.
- `0.5`는 속도상 유리하지만 실제 smoke에서 검출 프레임 수가 줄었으므로 기본값으로 적용하지 않았다.

실제 6분 구간 5초 클립 추가 smoke:

| 케이스 | 경로 | 검출 호출 | faceMaskFrames | 자동 검출 total | export total | 비교 |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| baseline-all-frames | `pipe-single` | 150 | 8 | 100,254ms | 9,930ms | 기준 |
| optimized-track-1-parallel-2 | `pipe-parallel` | 150 | 8 | 53,724ms | 10,648ms | `onlyBaseline=0`, `onlyOptimized=0`, `avgBestIou=1.000`, `minBestIou=1.000` |
| script-default-track-1-parallel-2 | `pipe-parallel` | 150 | 8 | 56,451ms | 11,121ms | baseline 비교 생략, 기본 스크립트 경로 확인 |

해석:

- 5초/150프레임 클립에서도 모든 프레임 검출을 유지한 parallel pipeline은 baseline과 프레임/박스가 완전히 일치했다.
- 자동 검출 wall-clock은 약 100.3초에서 53.7초로 줄었다.
- smoke 스크립트 기본값도 `DownscaleRatio=1.0`, `DetectEveryNFrames=1`, `ParallelDetectorCount=2`로 맞췄고, 기본 실행이 `pipe-parallel`로 진입하는 것을 확인했다.
- `[AutoRunSummary]`의 detector 표기에 ONNX Runtime provider를 포함해 `FaceOnnxDetector/CPU`, `FaceOnnxDetector/GPU:DirectML`처럼 실제 가속 경로를 확인할 수 있게 했다.
- export는 큰 병목이 아니며, 현재 병목은 여전히 자동 검출 detector 실행이다.

실제 9분 구간 2초 클립 추가 smoke:

| 케이스 | 경로 | 검출 호출 | faceMaskFrames | 자동 검출 total | export total | 비고 |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| script-default-track-1-parallel-2-cpu | `pipe-parallel` | 60 | 51 | 24,220ms | 12,828ms | `FaceOnnxDetector/CPU`, 얼굴 다수 구간 |
| script-default-track-1-parallel-2-gpu | `pipe-parallel` | 60 | 51 | 19,650ms | 13,101ms | `FaceOnnxDetector/GPU:DirectML`, 기본 스크립트 경로 |

해석:

- 다른 시간대에서도 기본 경로는 원본 해상도, 모든 프레임 검출, parallel pipeline으로 진입했다.
- 해당 구간은 faceMaskFrames가 51개라 6분 구간보다 얼굴 검출이 훨씬 많은 장면으로 보이며, export는 direct face rect 51프레임으로 완료됐다.
- 같은 9분 구간에서 DirectML은 CPU 대비 자동 검출 wall-clock을 약 24.2초에서 19.7초로 줄였고, faceMaskFrames는 51개로 유지됐다.
- 앱 자동 설정 버전을 `3`으로 올려 legacy 저장 설정은 Windows/macOS에서 GPU 사용 기본값을 다시 적용한다. GPU 초기화 실패 시 기존 detector fallback 로직이 CPU로 내려간다.
- legacy 저장 설정은 `DetectEveryNFrames=1`, `DownscaleRatio=1.0`, GPU 기본값뿐 아니라 `ParallelSessionCount`도 최소 2로 보정한다.
- direct face blur의 alpha/radius map 사전 계산 최적화도 시도했지만, 같은 smoke에서 `maskMs`가 약 5.1초에서 8.5초로 악화되어 반영하지 않았다.
- smoke 스크립트는 `-SkipTrim`으로 여러 클립을 테스트할 때 출력 파일이 덮이지 않도록 입력 클립 이름 기반으로 export 파일명을 만든다.
- smoke 스크립트에 `-SkipExport`를 추가했다. 긴 구간에서는 자동 검출/face rect 비교만 먼저 확인하고, export는 대표 구간에서 따로 검증할 수 있다.
- smoke 스크립트에 `-UseAutoTune`를 추가했다. 실제 `DetectorAutoTuner.TryTune` 경로를 호출해 튜닝 라벨/세션/provider를 확인할 수 있다.
- smoke 스크립트에 품질 gate를 추가했다. baseline 비교 실행 시 프레임 누락/추가, 박스 수 차이, `avgBestIou`, `minBestIou` 기준을 검사하고 실패하면 exit code 2로 종료한다.
- `scripts/verify-native-publish.ps1`를 추가했다. `win-x64`와 `osx-arm64` publish를 실행하고 각 runtime의 필수 native 파일을 검사한다. macOS 검증에서는 Windows `onnxruntime.dll`/`onnxruntime_providers_shared.dll`이 섞이면 실패하고, `libomp.dylib` 누락은 경고로 표시한다.
- `scripts/verify-auto-mosaic-default.ps1`를 추가했다. 기본 실행은 6분 3초 클립에서 CPU single baseline 대비 CPU `pipe-parallel(2)` all-frame 품질 gate를 검사하고, 6분 5초 클립에서 `UseAutoTune` 기본 경로가 `GPU:DirectML`, `pipe-parallel(2)`, 전 프레임 검출로 동작하는지 확인한다. `-RunLongAutoTune`을 붙이면 12분 30초 클립까지 확장 검증한다.

뒤쪽 시간대 추가 smoke:

| 구간 | 경로 | 검출 호출 | faceMaskFrames | 자동 검출 total | export total | 비고 |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| 12:00 2초 | `GPU:DirectML` / `pipe-parallel` | 60 | 28 | 25,842ms | 10,057ms | direct face rect export |
| 15:00 2초 | `GPU:DirectML` / `pipe-parallel` | 59 | 0 | 18,315ms | 193ms | 얼굴 없음, remux-copy |

해석:

- 12분 구간도 원본 해상도, 모든 프레임 검출, DirectML, parallel pipeline으로 동작했고 얼굴 마스크 28프레임을 export했다.
- 15분 구간은 검출 프레임이 없어 blur 대상이 없었고, remux-copy 경로가 `[ExportRunSummary]`를 남기며 빠르게 종료됐다.

12분 구간 baseline 비교:

| 케이스 | provider / 경로 | 검출 호출 | faceMaskFrames | 자동 검출 total | export total |
| --- | --- | ---: | ---: | ---: | ---: |
| baseline-all-frames | `CPU` / `pipe-single` | 60 | 28 | 27,579ms | 7,206ms |
| optimized-track-1-gpu | `GPU:DirectML` / `pipe-parallel` | 60 | 28 | 20,839ms | 7,746ms |

비교 결과:

- `baselineFrames=28`, `optimizedFrames=28`, `common=28`, `onlyBaseline=0`, `onlyOptimized=0`, `boxCountDiffFrames=0`
- `avgBestIou=0.916`, `minBestIou=0.846`
- DirectML provider의 floating point 차이로 박스 좌표가 완전 동일하지는 않지만, 동일 프레임/동일 박스 수를 유지했고 IoU도 높은 편이다.
- 자동 검출 wall-clock은 같은 12분 구간에서 약 27.6초에서 20.8초로 줄었다.
- `DetectorAutoTuner`에 GPU 후보 quality gate를 추가했다. 튜닝 샘플에서 CPU 기준과 박스 수가 다르거나 최소 IoU가 `0.75` 미만이면 GPU 후보는 속도가 빨라도 선택하지 않는다.
- 12분 2초 클립에서 `-UseAutoTune` smoke를 실행했고, 튜너가 `GPU 2세션/8스레드`를 선택한 뒤 `FaceOnnxDetector/GPU:DirectML`, `pipe-parallel(2)`로 자동 검출을 완료했다.
- 튜너 캐시 키에 `IntraOpNumThreads`와 `EnablePreprocessParallelism`을 포함해, 사용자가 thread/preprocess 설정을 바꿨을 때 이전 튜닝 결과가 잘못 재사용되지 않게 했다.
- 짧은 튜닝 샘플에서 `1세션`이 선택되면 긴 구간에서 오히려 느려지는 실측이 있어, 튜너는 사용자가 선택한 병렬 세션 수를 낮추지 않고 해당 세션 수 안에서 thread/provider만 고르게 했다.
- 보정 후 `-UseAutoTune` smoke에서 다시 `GPU 2세션/8스레드`, `pipe-parallel(2)`가 선택되는 것을 확인했다.
- 6분 5초 클립에서도 `-UseAutoTune -SkipBaseline -SkipExport`를 다시 실행했고, 튜너가 `GPU 2세션/8스레드`를 선택한 뒤 `FaceOnnxDetector/GPU:DirectML`, `pipe-parallel(2)`로 자동 검출을 완료했다.
- 해당 5초 클립의 추가 결과는 `processed=150`, `detects=150`, `interpolated=0`, `faceMaskFrames=8`, `totalMs=66,709ms`였다. export는 의도적으로 생략했다.
- 12분 30초 클립에서 `-UseAutoTune -SkipBaseline -SkipExport`를 실행했을 때 튜너가 CPU `ORT_PARALLEL`을 고르는 케이스를 확인했다. 이 경로는 `FaceOnnxDetector/CPU`, `pipe-parallel(2)`, `processed=899`, `faceMaskFrames=309`, `totalMs=499,482ms`로 기존 GPU 기본 경로보다 느렸다.
- 원인은 짧은 튜닝 샘플 throughput이 장기 구간 wall-clock과 어긋나는 경우였다. GPU 후보가 quality gate를 통과하고 CPU 최고 점수의 75% 이상이면 GPU를 유지하도록 튜너 정책을 보정했다.
- 보정 후 같은 12분 30초 클립에서 튜너가 `GPU 2세션/8스레드`를 선택했고, `FaceOnnxDetector/GPU:DirectML`, `pipe-parallel(2)`, `processed=899`, `detects=899`, `interpolated=0`, `faceMaskFrames=309`, `totalMs=384,784ms`로 완료했다.
- smoke script의 `[SmokeTune]` provider 표기가 CPU 선택 시 이전 GPU 시도 상태를 보여줄 수 있어, CPU 선택이면 provider를 `CPU`로 출력하도록 고쳤다.

12분 구간 10초 기본 GPU smoke:

| 구간 | provider / 경로 | 검출 호출 | faceMaskFrames | 자동 검출 total | export total |
| --- | --- | ---: | ---: | ---: | ---: |
| 12:00 10초 | `GPU:DirectML` / `pipe-parallel` | 300 | 127 | 109,771ms | 30,639ms |
| 12:00 10초 | `GPU:DirectML` / `pipe-single` + ROI | 300 | 129 | 138,857ms | 34,996ms |
| 12:00 30초 | `GPU:DirectML` / `pipe-parallel` | 899 | 309 | 338,306ms | skipped |
| 06:00 30초 | `GPU:DirectML` / `pipe-parallel` | 899 | 44 | 377,052ms | skipped |

해석:

- 2초 샘플보다 긴 300프레임 구간에서도 원본 해상도, 모든 프레임 검출, DirectML, parallel pipeline이 완료됐다.
- `interpolated=0`이므로 기본 품질 경로는 프레임 스킵/보간 없이 전 프레임 검출이다.
- export는 direct face rect 127프레임을 처리했고 완료됐다.
- `parallel=1`의 ROI 단일 파이프도 측정했지만 자동 검출 wall-clock이 109.8초에서 138.9초로 느려졌고 faceMaskFrames도 127에서 129로 달라졌다.
- 따라서 기본 속도/품질 경로는 ROI 단일 파이프가 아니라 `parallel=2` all-frame pipeline으로 유지한다.
- 12분/6분 30초, 각 899프레임 구간도 export 없이 자동 검출을 완료했고, `interpolated=0`으로 전 프레임 검출을 유지했다.

전체 영상 예상:

- 원본 `srcTest/260102_jp_10.mp4`는 3840x2160, 29.97fps, 1,067.6초, 31,996프레임이다.
- 12분 30초 구간은 899프레임 자동 검출에 338,306ms, 6분 30초 구간은 899프레임 자동 검출에 377,052ms가 걸렸다.
- 단순 환산 시 전체 전 프레임 자동 검출은 약 3.3~3.7시간 범위로 예상된다. export 시간은 별도다.
- 따라서 최상 품질 기본값은 전 프레임 검출을 유지하되, 전체 영상 실사용 속도 개선은 향후 새 detector backend 또는 품질 검증된 sparse/refiner 구조가 필요하다.
- 현재 검증된 기본값은 품질 우선 경로이고, `DetectEveryNFrames > 1` 또는 downscale은 전체 품질 근거가 부족하므로 기본값으로 승격하지 않는다.

모델 교체 가능성 확인:

- 현재 `FaceONNX` NuGet 패키지는 별도 `.onnx` 파일을 프로젝트에 노출하지 않고 DLL 내부 리소스로 모델을 포함하는 구조다.
- XML 문서 기준 `FaceONNX.FaceDetector`의 공개 생성자는 threshold와 `SessionOptions` 중심이며, 임의 모델 경로를 주입하는 생성자는 확인되지 않았다.
- DLL 문자열 기준 현재 detector 모델 리소스는 `deploy_dpe_220_v4_slim.onnx`로 보인다.
- 따라서 단기 개선은 `FaceOnnxDetector` 유지 + DirectML/CoreML/CPU provider + pipeline 최적화가 맞고, 실제 모델 교체는 `IBgraFaceDetector` 구현을 새로 추가하는 backend 확장으로 진행해야 한다.
- 자동 실행 경로에서 `_detectorFactoryOptions`가 `FaceDetectorFactoryOptions.ForOnnx(...)`로 다시 고정되던 부분을 제거했다. 이제 자동 튜닝은 `FaceOnnx` backend에서만 적용하고, factory option 자체는 유지하므로 새 backend를 추가했을 때 자동 모자이크 경로가 다시 FaceONNX로 되돌아가지 않는다.

## 2026-05-11 완료 감사
목표를 다음 deliverable로 나눠 확인한다.

| 요구 | 현재 증거 | 상태 |
| --- | --- | --- |
| 자동 모자이크 품질 저하 방지 | 기본값을 `DownscaleRatio=1.0`, `DetectEveryNFrames=1`, `interpolated=0` 경로로 유지했고, 6분 3초/5초 및 12분 2초 비교에서 baseline 대비 누락 프레임 0개를 확인했다. | 부분 충족 |
| 자동 검출 속도 개선 | CPU single baseline 대비 `pipe-parallel(2)`와 DirectML 기본 경로에서 3초/5초/2초 샘플의 wall-clock 감소를 확인했다. | 부분 충족 |
| 실제 `srcTest` 영상 기반 검증 | `srcTest/260102_jp_10.mp4`에서 여러 짧은 클립과 30초 자동 검출 smoke를 실행했다. 전체 17분 풀런은 아직 수행하지 않았다. | 미완료 |
| 모델 교체 가능성 검토 | 현재 FaceONNX 모델은 DLL 내부 리소스 구조라 임의 모델 경로 교체가 불가능하고, 새 모델은 `IBgraFaceDetector` backend 추가가 필요하다고 확인했다. 자동 실행 경로가 `_detectorFactoryOptions`를 보존하도록 수정해 backend 교체 지점은 막히지 않게 했다. | 설계 충족, 구현 미완료 |
| 병목 측정 가능성 | `[AutoRunSummary]`, `[ExportRunSummary]`, `runId`, provider 표기를 추가했다. | 충족 |
| 자동 튜닝 안정성 | cancellation token, GPU quality gate, legacy 설정 migration, 병렬 세션 유지 정책을 반영했고 `-UseAutoTune` smoke에서 `GPU 2세션/8스레드`가 유지됨을 확인했다. | 충족 |
| export 경로 개선 | direct face rect summary와 no-blur remux-copy summary를 추가했다. hybrid copy는 실제 smoke에서 `Invalid argument`가 발생해 비활성화했다. | 부분 충족 |
| 품질 검토 UX | 자동 이상 프레임은 전체 no-face 프레임이 아니라 앞뒤 얼굴 사이의 짧은 no-face gap, 낮은 confidence, flicker 중심으로 좁혔다. | 충족 |
| Windows 배포 검증 | `scripts/verify-native-publish.ps1 -RuntimeIdentifier win-x64`가 성공했고, publish 폴더에 `FaceShield.exe`, `DirectML.dll`, `onnxruntime.dll`, `FaceONNX.dll`, FFmpeg DLL들이 포함된 것을 확인했다. | 충족 |
| macOS 배포 검증 | Windows에서 `scripts/verify-native-publish.ps1 -RuntimeIdentifier osx-arm64` cross-publish가 성공했고, `FaceShield`, `libonnxruntime.dylib`, FFmpeg dylib들이 포함된 것을 확인했다. Windows DirectML package가 osx publish에 섞여 `NETSDK1152`가 나던 문제를 package condition 수정으로 해결했고, Windows `onnxruntime.dll`/`onnxruntime_providers_shared.dll` 부재도 검증했다. 다만 현재 publish에는 `libomp.dylib`가 포함되지 않으므로 warning을 남기며, 실제 Mac 런타임 확인은 남아 있다. | 부분 충족 |

완료로 볼 수 없는 항목:

- 전체 17분 원본 영상 end-to-end 자동 검출 + export 풀런이 아직 없다.
- 새 detector backend나 실제 모델 A/B 구현은 아직 없다.
- GUI에서 열기, preview, 자동 검출, 수동 수정, export 전체 흐름을 직접 smoke하지 않았다.
- macOS 실기기에서 자동 모드 시작과 ONNX/libomp runtime load를 확인하지 않았다.

따라서 현재 상태는 최상 품질 기본 경로와 측정 가능한 고속 경로를 구현한 단계이며, goal 완료로 처리하지 않는다.

추가 gate 검증:

- 6분 3초 클립에서 CPU single baseline과 CPU `pipe-parallel(2)` all-frame 경로를 `MinAvgIou=0.99`, `MinBestIou=0.99`로 비교했고 `SmokeQualityGate passed=True`를 확인했다.
- 같은 클립에서 `DetectEveryNFrames=2` sparse 경로는 `onlyBaseline=27`, `onlyOptimized=87`이 발생해 `SmokeQualityGate passed=False`가 되었고, 스크립트가 exit code 2로 종료되는 것을 확인했다.
- `scripts/verify-auto-mosaic-default.ps1` 기본 실행이 통과했다. 이 실행에서 품질 gate는 `avgBestIou=1.000`, `minBestIou=1.000`, `passed=True`였고, auto tune 짧은 검증은 `GPU 2세션/8스레드`, `FaceOnnxDetector/GPU:DirectML`, `processed=150`, `detects=150`, `interpolated=0`, `faceMaskFrames=8`, `totalMs=64,236ms`로 완료했다.
