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
- `scripts/verify-face-track-postprocess.ps1`
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
- 현재 작업공간과 로컬 NuGet cache에는 바로 붙일 수 있는 별도 face/yolo/opencv detector 모델 또는 패키지가 확인되지 않았다. 네트워크나 별도 모델 파일 없이 새 detector backend를 구현하면 임의 추정 구현이 되므로 기본 경로에는 넣지 않는다.
- 따라서 단기 개선은 `FaceOnnxDetector` 유지 + DirectML/CoreML/CPU provider + pipeline 최적화가 맞고, 실제 모델 교체는 `IBgraFaceDetector` 구현을 새로 추가하는 backend 확장으로 진행해야 한다.
- 자동 실행 경로에서 `_detectorFactoryOptions`가 `FaceDetectorFactoryOptions.ForOnnx(...)`로 다시 고정되던 부분을 제거했다. 이제 자동 튜닝은 `FaceOnnx` backend에서만 적용하고, factory option 자체는 유지하므로 새 backend를 추가했을 때 자동 모자이크 경로가 다시 FaceONNX로 되돌아가지 않는다.
- `ScrfdOnnxDetector` backend를 추가했다. 이 backend는 insightface 계열 SCRFD ONNX 출력(score/bbox stride map)을 해석하는 외부 모델 경로 기반 `IBgraFaceDetector` 구현이다. 기본값으로는 사용하지 않으며, 모델 파일이 없으면 명확히 실패한다.
- `scripts/run-srcTest-smoke.ps1 -ScrfdModelPath <model.onnx>`를 추가했다. baseline은 기존 FaceONNX로 유지하고 optimized case만 SCRFD backend로 실행해 같은 quality gate에서 A/B 비교할 수 있다. 현재 저장소에는 SCRFD 모델 파일이 없으므로 실제 SCRFD 품질/속도 수치는 아직 없다.
- `scripts/inspect-onnx-outputs.ps1`를 추가했다. 외부 ONNX 모델의 input/output 이름, shape, 값 범위를 확인해 score/bbox/kps 출력 규약을 decoder 구현 전에 검증한다.
- SCRFD 전처리 옵션으로 `UseLetterboxResize`, `UseRgbInput`, `MultiplyBboxByStride`를 추가했다. smoke script에서는 `-ScrfdStretchInput`, `-ScrfdUseBgr`로 letterbox/stretch와 RGB/BGR 조합을 비교할 수 있다.

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
- 외부 SCRFD detector backend는 추가됐지만, 실제 모델 파일이 없어 SCRFD A/B 실행 결과는 아직 없다.
- GUI에서 열기, preview, 자동 검출, 수동 수정, export 전체 흐름을 직접 smoke하지 않았다.
- macOS 실기기에서 자동 모드 시작과 ONNX/libomp runtime load를 확인하지 않았다.

따라서 현재 상태는 최상 품질 기본 경로와 측정 가능한 고속 경로를 구현한 단계이며, goal 완료로 처리하지 않는다.

추가 gate 검증:

- 6분 3초 클립에서 CPU single baseline과 CPU `pipe-parallel(2)` all-frame 경로를 `MinAvgIou=0.99`, `MinBestIou=0.99`로 비교했고 `SmokeQualityGate passed=True`를 확인했다.
- 같은 클립에서 `DetectEveryNFrames=2` sparse 경로는 `onlyBaseline=27`, `onlyOptimized=87`이 발생해 `SmokeQualityGate passed=False`가 되었고, 스크립트가 exit code 2로 종료되는 것을 확인했다.
- `scripts/verify-auto-mosaic-default.ps1` 기본 실행이 통과했다. 이 실행에서 품질 gate는 `avgBestIou=1.000`, `minBestIou=1.000`, `passed=True`였고, auto tune 짧은 검증은 `GPU 2세션/8스레드`, `FaceOnnxDetector/GPU:DirectML`, `processed=150`, `detects=150`, `interpolated=0`, `faceMaskFrames=8`, `totalMs=64,236ms`로 완료했다.

## 2026-05-12 실제 사용 확인 상태
사용자가 현재 앱 상태를 실제 영상에서 확인한 결과는 다음과 같다.

- 큰 얼굴과 일반적인 얼굴 검출, 모자이크는 적당히 동작한다.
- 작은 얼굴은 모자이크가 잘 안 되는 구간이 있다.
- 사람이 아닌 물건이 얼굴로 오검출되어 모자이크되는 경우가 있다.
- 모자이크가 얼굴을 따라 트래킹되는 느낌이 부족하고, 중간에 깜박거리는 구간이 있다.
- export 시간이 여전히 오래 걸린다.

이 관찰은 기존 smoke gate의 한계를 보여준다. 기존 gate는 짧은 클립에서 CPU baseline과 optimized 경로의 프레임/박스 일치 여부를 보는 데는 유효했지만, 실제 사용 품질 관점의 작은 얼굴, 오탐 물체, track continuity, flicker, 긴 export 시간을 충분히 대표하지 못했다.

현재 우선순위는 다음으로 조정한다.

1. 작은 얼굴 미탐 구간을 frame index와 화면 위치로 수집한다.
2. 물건 오탐 구간을 frame index와 객체 종류로 수집한다.
3. 깜박임 구간을 `이전 얼굴 있음 -> 현재 없음 -> 다음 얼굴 있음` 패턴과 실제 영상 확인 결과로 분리한다.
4. track continuity를 강화한다. 단순 프레임별 face rect dictionary만으로는 부족하므로 얼굴별 track id, 짧은 gap 보간, 박스 smoothing, 오탐 track 제거가 필요하다.
5. 작은 얼굴 대응은 threshold 완화만으로 처리하지 않는다. threshold 완화는 물건 오탐을 늘릴 수 있으므로, ROI 재검출, 2차 verifier/refiner, 또는 작은 얼굴에 강한 detector backend를 비교해야 한다.
6. export 병목은 `[ExportRunSummary]`의 `maskMs`, `swsToBgraMs`, `swsToEncMs`, `encodeMs`, `totalMs`를 실제 긴 영상에서 확인한 뒤 큰 항목부터 줄인다.

추가 방향: 확정 track 중심으로 모자이크를 유지한다.

- 한 번 얼굴로 확정된 대상은 detector가 잠깐 놓쳐도 영상에서 사라질 때까지 가능한 한 블러를 유지한다.
- 단, 처음 1프레임만 오검출된 물건을 끝까지 블러하면 안 되므로 모든 후보를 바로 확정하지 않는다.
- `Tentative`: 새 후보. 1프레임 검출만으로는 확정하지 않는다.
- `Confirmed`: 일정 프레임 동안 반복 검출되거나 자연스러운 이동/크기 변화가 확인된 얼굴 track. 짧은 미탐 구간은 보간한다.
- `Lost`: 확정 track이 잠깐 사라진 상태. 일정 프레임까지는 예측/보간으로 블러를 유지한다.
- `Ended`: 화면 밖 이동, 긴 미탐, scene cut, 비정상적인 크기/위치 변화로 종료된 track.
- 반쪽 얼굴/가장자리 얼굴은 검출 증거가 짧아도 놓치면 안 되므로 `Tentative` 상태에서 바로 버리지 않는다.
- 작은 물건 오탐은 confidence가 높아도 짧은 단발 track이면 제거한다.
- 화면 중앙의 작은 후보라도 3프레임 이상 자연스럽게 이어지면 바로 제거하지 않는다. 사람이 뒤돌면서 얼굴이 반만 보이는 경우도 이 범위에 포함한다.
- 향후 구현은 `FaceTrackState` 또는 `FaceTrackLifecycle` 형태로 확장해, frame dictionary 후처리가 아니라 track lifecycle 기준으로 최종 face rect를 만들도록 한다.

다음 구현 후보:

- `FaceTrack`, `FaceTrackBuilder`, `FaceTrackInterpolator`를 추가해 frame 단위 결과를 track 단위로 재구성한다.
- 짧은 no-face gap은 앞뒤 track이 같은 얼굴일 때만 보간한다.
- 일정 길이 이하의 단발 오검출 track은 제거하거나 이상 후보로 표시한다.
- 작은 얼굴 후보는 낮은 confidence라도 바로 버리지 않고 track 후보로 유지한 뒤, 연속성으로 확정한다.
- 확정된 track은 짧은 detector 미탐에도 `Lost` 상태로 유지하고, 영상에서 사라질 때까지 보간/예측 블러를 지속한다.
- 물건 오탐은 confidence만으로 구분하기 어렵기 때문에, box 크기/비율/움직임/지속시간 기반 필터와 2차 verifier를 검토한다.
- export는 face rect만 있는 프레임에서 direct blur 경로를 유지하되, 변환/마스크/인코딩 시간 중 실제 병목을 먼저 측정한다.

완료 기준을 다음처럼 보강한다.

- 작은 얼굴이 포함된 대표 구간에서 미탐 frame 수를 기존보다 줄인다.
- 물건 오탐 frame 수를 줄인다.
- 같은 얼굴 track의 짧은 깜박임을 줄이고, 모자이크 박스 이동이 프레임 사이에 자연스럽게 이어진다.
- export는 동일 품질 조건에서 `ExportRunSummary.totalMs` 또는 주요 병목 항목이 감소한다.
- 위 항목은 짧은 smoke가 아니라 실제 문제 영상의 대표 구간 여러 개에서 확인한다.

## 2026-05-12 track 후처리 1차 구현
실제 사용 확인에서 나온 깜박임과 단발 오검출 문제를 줄이기 위해 frame 단위 보정 로직 일부를 track 단위 후처리로 분리했다.

추가/변경 파일:

- `Services/Analysis/FaceTrack.cs`
- `Services/Analysis/FaceTrackBuilder.cs`
- `Services/Analysis/FaceTrackInterpolator.cs`
- `ViewModels/Pages/WorkspaceViewModel.cs`
- `scripts/run-srcTest-smoke.ps1`

구현 내용:

- `FaceTrackBuilder`가 frame별 face rect를 IoU, 중심점 이동량, 면적 변화율 기준으로 같은 얼굴 track으로 묶는다.
- `FaceTrackInterpolator`가 같은 track의 짧은 no-face gap만 보간한다.
- 확정 track은 마지막 검출 뒤 detector가 잠깐 놓쳐도 최대 3프레임까지 이동량 기반으로 예측해 블러를 유지한다.
- 확정 track lost-fill이 발생한 frame index를 `FilledLostFrameIndices`와 `lostFrames=` 로그로 남겨, 이후 ROI verifier/refiner를 해당 frame 주변에만 붙일 수 있게 했다.
- `FaceTrackRoiRefiner`를 추가해 gap-fill/lost-fill 후보 프레임만 FFmpeg raw BGRA로 다시 읽고, 예측 박스 주변 ROI crop만 detector에 넣어 재검출한다. ROI 검출 결과가 예측 박스와 충분히 가까울 때만 기존 예측 박스를 교체한다.
- ROI 재검출은 전역 detector threshold를 바꾸지 않고, ROI 전용 CPU FaceONNX detector에서만 `DetectionThreshold/ConfidenceThreshold`를 최대 `0.12`까지 낮춰 더 민감하게 확인한다. 전역 자동 검출의 사용자 기준값 `0.2/0.25/0.7`은 유지한다.
- ROI 후보가 가까운 frame에 몰린 경우 매 후보마다 seek하지 않고 한 번의 sequential read로 이어 읽도록 최적화했다. 후보 frame 간격이 `12`프레임보다 크면 다시 seek해 긴 구간을 불필요하게 디코드하지 않는다.
- 긴 구간에서 ROI 후보 상한을 적용할 때 입력 순서 편향이 생기지 않도록, 후보를 frame index 기준으로 먼저 정렬한 뒤 `maxCandidates`를 적용한다.
- confidence가 낮고 지속 길이가 짧은 단발 track은 오검출 후보로 보고 제거한다.
- confidence가 높더라도 1~2개 검출만 가진 작은 단발 track은 물건 오탐 가능성이 높으므로 제거한다.
- 작은 track이 3개 이상 검출로 이어지면 중앙의 반쪽 얼굴 가능성을 고려해 즉시 제거하지 않는다.
- 단, 화면 가장자리에 닿은 작은 단발 후보는 반쪽 얼굴 가능성이 있으므로 보존한다.
- 수동 mask가 있는 frame은 자동 track 보정 대상에서 제외한다.
- 기존 `WorkspaceViewModel.ApplyAutoTemporalFixes()`는 track 후처리 호출로 축소했다.
- smoke harness도 자동 검출 후 `FaceTrackInterpolator`를 적용하고 `[SmokeFaceTrackPost]` 로그를 남기도록 맞췄다.
- synthetic 검증 스크립트에서 짧은 gap fill, 확정 track lost-fill, low-confidence 단발 제거, 1~2개 검출짜리 작은 오탐 제거, 제거된 track의 보간 재생성 차단, 3프레임 이상 중앙 반쪽 얼굴 후보 보존을 직접 확인하도록 했다.
- smoke harness도 `FaceTrackRoiRefiner`를 호출해 ROI refiner 경로를 console gate에서 검증한다.

검증:

- `dotnet build FaceShield.sln` 성공. 기존 FFmpeg.AutoGen obsolete warning 7개만 발생했다.
- `.tmp/srcTest-smoke/smoke-0600-3s.mp4` 단일 optimized CPU all-frame 검증에서 `[SmokeFaceTrackPost] tracks=3, filled=0, removedShort=0, rewritten=8`을 확인했다.
- `scripts/verify-auto-mosaic-default.ps1` 기본 실행이 통과했다.
- 품질 gate는 baseline과 optimized 모두 track 후처리 적용 후 `baselineFrames=8`, `optimizedFrames=8`, `onlyBaseline=0`, `onlyOptimized=0`, `avgBestIou=1.000`, `minBestIou=1.000`, `passed=True`였다.
- auto tune 짧은 검증은 `FaceOnnxDetector/GPU:DirectML`, `mode=pipe-parallel`, `detects=150`, `interpolated=0`으로 통과했고, 후처리 로그는 `tracks=3`, `filled=0`, `removedShort=0`, `rewritten=8`이었다.
- `scripts/verify-face-track-postprocess.ps1` 실행 결과 `tracks=6`, `filled=1`, `gapFrames=11`, `lostFilled=3`, `lostFrames=33,34,35`, `removedShort=3`, `rewritten=13`, `filledFrames=10,11,12,25,30,31,32,33,34,35,50,51,52`를 확인했다. frame 11은 gap-fill ROI 후보, frame 25는 화면 가장자리 반쪽 얼굴 후보 보존 케이스, frame 30~35는 확정 track lost-fill 케이스, frame 50~52는 중앙에서 3프레임 이상 이어지는 작은 반쪽 얼굴 후보 보존 케이스다.
- `scripts/verify-auto-mosaic-default.ps1` 통합 검증에서 ROI refiner가 민감한 ROI 전용 detector로 `attempts=8`, `hits=0` 실행됐고, baseline/optimized 모두 같은 결과를 유지했다. 이 샘플에서는 ROI crop이 추가 얼굴을 찾지는 못했지만, gap-fill 5프레임과 lost-fill 3프레임을 모두 재검출 대상으로 확인하면서 품질 gate를 깨지 않는 것은 확인했다.
- ROI hit 대표 구간 `.tmp/srcTest-smoke/smoke-0900-2s.mp4`를 `scripts/verify-auto-mosaic-default.ps1`에 추가했다. 이 gate는 `FaceTrackRoiRefiner`가 실제 구간에서 `attempts=11`, `hits=5`를 내는지 확인한다. ROI seek 최적화 후 단독 실행에서는 `seeks=4`, `decoded=26`, `elapsedMs=9,455`였다.
- 선택형 export smoke gate `-RunExportSmoke`를 `scripts/verify-auto-mosaic-default.ps1`에 추가했다. `.tmp/srcTest-smoke/smoke-1200-2s.mp4`에서 자동 검출 후 export까지 실행하고 `[ExportRunSummary]`의 `bitmapMaskFrames=0`, `directFaceFrames>0`, output 생성 로그를 확인한다.
- `.tmp/srcTest-smoke/smoke-1200-30s.mp4` 30초 중간 길이 검증에서 `processed=899`, `detects=899`, `interpolated=0`, `totalMs=357,398ms`, `regular=614`, `small=2037`, `rejected=2111`, `statsRejected=131`을 확인했다. track 후처리는 `tracks=244`, `filled=431`, `lostFilled=104`, `removedShort=77`, `rewritten=778`이었고, ROI refiner는 상한 32개 후보에서 `attempts=32`, `hits=22`, `seeks=4`, `decoded=77`, `elapsedMs=14,599`였다.
- 위 30초 검증을 재현할 수 있도록 `scripts/verify-auto-mosaic-default.ps1`에 선택형 `-RunMediumAuto` gate를 추가했다. 이 gate는 `processed=899`, `detects=899`, `interpolated=0`, track 보정 발생, ROI `attempts=32`, ROI `hits>0`을 확인한다.

남은 한계:

- 실제 영상 smoke 구간에서는 gap 보간과 단발 제거가 발생하지 않았다. synthetic 검증에서는 기능 자체를 확인했지만, 실제 문제 구간에서 개선 수치는 아직 확인하지 못했다.
- 작은 얼굴 미탐 자체를 줄이는 detector/backend는 아직 구현하지 않았다. ROI refiner의 기본 경로는 gap-fill/lost-fill 후보까지 확장했지만, 현재 기본 샘플에서는 `hits=0`이라 실제 미탐 감소 효과는 아직 확인되지 않았다.
- 긴 export 병목 감소는 이번 변경의 대상이 아니며, 실제 긴 구간 `ExportRunSummary` 기준으로 계속 측정해야 한다.

## 2026-05-12 작은 얼굴 필터 보강
작은 얼굴 미탐 가능성을 줄이기 위해 `AutoMaskGenerator`의 face size filter를 보수적으로 완화했다.

참고 기준:

- 사용자가 2026-05-11 집 테스트에서 사용한 detector threshold는 `DetectionThreshold=0.2`, `ConfidenceThreshold=0.25`, `NmsThreshold=0.7`이다.
- 이 값들은 `HomePageViewModel.BuildDetectorOptions()`에서 `FaceOnnxDetectorOptions`로 전달되고, `DetectorAutoTuner.CloneOptions()`도 threshold 값을 유지한다.
- 이번 변경은 위 detector threshold를 직접 바꾸지 않고, detector가 반환한 후보를 `AutoMaskGenerator` 필터와 `FaceTrackInterpolator` 후처리에서 어떻게 살리거나 제거할지 조정한 것이다.

기존 한계:

- 기존 `MinFaceAreaRatio=0.00075`는 3840x2160 영상 기준 약 6,220px, 정사각형 환산 약 79x79px 미만 얼굴 후보를 버릴 수 있다.
- 단순히 면적 기준만 낮추면 물건 오검출이 늘어날 수 있다.

변경 내용:

- 일반 face 후보 기준은 유지한다.
- 작은 face 후보는 `MinSmallFaceAreaRatio=0.00025` 이상이면서 confidence가 `0.72` 이상일 때만 살린다.
- 작은 face 후보는 confidence가 높아도 skin/edge/luma variance 기반 픽셀 통계 검사를 강제로 통과해야 한다.
- 일반 후보의 기존 high-confidence stats bypass 경로는 유지해 큰/일반 얼굴 처리 비용과 기존 결과 변화를 줄였다.
- `[AutoRunSummary]`의 `roi=` 요약에 `regular`, `small`, `rejected`, `statsRejected` filter 통계를 남기도록 했다.

검증:

- `dotnet build FaceShield.sln` 성공. 기존 FFmpeg.AutoGen obsolete warning 7개만 발생했다.
- `scripts/verify-auto-mosaic-default.ps1` 기본 실행이 통과했다.
- 품질 gate는 `baselineFrames=8`, `optimizedFrames=8`, `onlyBaseline=0`, `onlyOptimized=0`, `avgBestIou=1.000`, `minBestIou=1.000`, `passed=True`였다.
- auto tune 짧은 검증은 `FaceOnnxDetector/GPU:DirectML`, `mode=pipe-parallel`, `detects=150`, `interpolated=0`으로 통과했다.
- 계측 추가 후 `.tmp/srcTest-smoke/smoke-0600-3s.mp4` 단일 optimized CPU all-frame 검증에서 `filter=regular=8, small=0, rejected=0, statsRejected=0` 로그를 확인했다.
- `.tmp/srcTest-smoke/smoke-0900-2s.mp4` 단일 optimized CPU all-frame 검증에서 `filter=regular=84, small=0, rejected=0, statsRejected=0`, track 후처리 `tracks=5`, `filled=5`, `removedShort=0`, `rewritten=51`을 확인했다. 실제 영상 구간에서 gap 보간이 발생한 대표 후보로 둔다.
- `.tmp/srcTest-smoke/smoke-1200-2s.mp4` 단일 optimized CPU all-frame 검증에서 `filter=regular=28, small=0, rejected=12, statsRejected=0`, track 후처리 `tracks=1`, `filled=0`, `removedShort=0`, `rewritten=28`을 확인했다.
- `.tmp/srcTest-smoke/smoke-1500-2s.mp4` 단일 optimized CPU all-frame 검증에서 `filter=regular=0, small=2, rejected=8, statsRejected=3`, track 후처리 `tracks=2`, `filled=0`, `removedShort=0`, `rewritten=2`를 확인했다. 작은 얼굴 후보가 실제로 유지되는 대표 후보로 둔다.
- 이후 같은 구간을 `DetectionThreshold=0.2`, `ConfidenceThreshold=0.25`, `NmsThreshold=0.7`, `-DumpDetections`로 재검증했다. 작은 후보는 frame 2/3/4의 빨간 수건/물건 오탐으로 확인되어 작은 단발 track 제거 기준을 추가했다.
- 제거된 작은 track은 gap 보간에서 다시 살아나지 않도록 보정했다. 재실행 결과 `filter=regular=0, small=5, rejected=13, statsRejected=4`, `tracks=4`, `filled=0`, `removedShort=4`, `rewritten=1`, `faceMaskFrames=1`이었다.
- 중앙의 빨간 수건/물건 오탐은 제거됐고, 남은 1개는 frame 56의 화면 오른쪽 가장자리 작은 후보다. 프레임 이미지로 육안 확인한 결과 화면 끝에 걸린 반쪽 얼굴 후보로 보여 현재 정책대로 보존한다. 이후 다른 구간에서 가장자리 물체 오탐이 반복되면 가장자리 후보용 verifier 또는 더 엄격한 edge partial-face 검증이 필요하다.
- 작은 track 제거 기준은 `1~2개 검출`로 제한했다. 사람이 뒤돌면서 얼굴이 반만 보이는 경우처럼 중앙에서 3프레임 이상 이어지는 작은 후보는 `Tentative`로 남겨 이후 lifecycle/ROI 재검증 대상이 되게 한다.

남은 한계:

- 이 변경은 detector가 이미 반환한 작은 얼굴 후보를 덜 버리는 보강이다. detector 자체가 후보를 반환하지 못하는 작은 얼굴은 ROI 재검출, verifier/refiner, 또는 새 detector backend가 필요하다.
- 작은 얼굴 후보가 유지되는 대표 구간은 확인했지만, 실제 육안 기준 미탐 frame 수 감소는 아직 확인하지 못했다.
- 가장자리 작은 후보는 반쪽 얼굴 보호를 위해 보수적으로 살린다. 이 정책은 가장자리 물체 오탐을 남길 수 있으므로 실제 영상 확인 결과에 따라 별도 verifier가 필요하다.

## 2026-05-12 auto tune provider 선택 보강
threshold를 `DetectionThreshold=0.2`, `ConfidenceThreshold=0.25`, `NmsThreshold=0.7`로 낮춘 뒤, 짧은 auto tune 샘플에서 GPU가 선택됐지만 실제 5초 검출 wall-clock이 CPU 병렬 경로보다 느려지는 케이스를 확인했다.

변경 내용:

- `DetectorAutoTuner`의 provider 후보 측정을 단일 프레임이 아니라 최대 3개 연속 프레임 기준으로 바꿨다.
- GPU는 quality gate를 통과하더라도 CPU 최고 후보보다 충분히 빠를 때만 선택하도록 `GpuPreferenceMinScoreRatio`를 `1.20`으로 조정했다.
- CPU/GPU 후보 점수를 분리해 계산한다. 이전 구조에서는 GPU가 일단 전체 최고점이 되면 CPU 대비 승격 margin을 사실상 우회할 수 있었다.
- CPU 후보에 고정 thread 수뿐 아니라 기본 ORT thread 설정 후보(`CPU <n>세션/default`)를 추가했다. 수동 smoke의 기본 CPU 경로와 같은 후보를 auto-tune에서도 비교하기 위함이다.
- `scripts/verify-auto-mosaic-default.ps1`는 더 이상 GPU 선택을 고정 요구하지 않는다. CPU/GPU 중 auto tune이 선택한 provider가 `FaceOnnxDetector/CPU` 또는 `FaceOnnxDetector/GPU:DirectML`로 정상 동작하고, `pipe-parallel`, 전 프레임 검출, `interpolated=0` 조건을 만족하는지 확인한다.
- `scripts/verify-auto-mosaic-default.ps1`가 `scripts/verify-face-track-postprocess.ps1`를 먼저 실행하도록 묶었다. 기본 검증 한 번으로 track gap 보간, 작은 오탐 제거, 반쪽 얼굴 후보 보존 정책까지 같이 확인한다.
- `scripts/verify-auto-mosaic-default.ps1 -RunMediumExport`를 추가했다. 30초 대표 구간에서 자동 검출 후 export까지 수행하고 `processed=899`, ROI hit, `ExportRunSummary.frames=902`, `bitmapMaskFrames=0`, `directFaceFrames>0`, output 생성을 assertion한다.
- `scripts/run-srcTest-smoke.ps1`는 매 실행마다 고유 harness 폴더를 사용한다. 이전 smoke 프로세스가 남아 있어도 고정 `SmokeHarness.exe` 파일 잠금 때문에 다음 검증이 실패하는 상황을 줄인다.

검증:

- `dotnet build FaceShield.sln` 성공. 기존 FFmpeg.AutoGen obsolete warning 7개만 발생했다.
- `git diff --check` 통과.
- `.tmp/srcTest-smoke/smoke-0600-5s.mp4`에서 `-UseAutoTune` 재실행 결과, tuner가 `CPU 2세션/4스레드`를 선택했다.
- 같은 검증에서 `FaceOnnxDetector/CPU`, `mode=pipe-parallel`, `processed=150`, `detects=150`, `interpolated=0`, `faceMaskFrames=16`, `totalMs=80,877ms`를 확인했다.
- 직전 GPU 선택 경로의 같은 5초 검증은 `FaceOnnxDetector/GPU:DirectML`, `totalMs=107,471ms`였으므로, 이 샘플에서는 느린 GPU 고정 선택을 줄였다.
- 이후 `scripts/verify-auto-mosaic-default.ps1` 전체 기본 검증이 다시 통과했다. 같은 5초 검증에서 최신 실행은 `FaceOnnxDetector/GPU:DirectML`, `totalMs=59,534ms`로 통과했다. provider 선택은 실행 시점의 장비 부하에 따라 CPU/GPU가 달라질 수 있으므로, gate는 provider 고정이 아니라 품질 유지와 전 프레임 병렬 경로 진입을 확인한다.
- 고유 harness 폴더 변경 후 `.tmp/srcTest-smoke/smoke-1500-2s.mp4` 짧은 smoke도 통과했다. 결과는 `FaceOnnxDetector/CPU`, `mode=pipe-parallel`, `processed=59`, `faceMaskFrames=1`, `removedShort=4`, `totalMs=21,827ms`였다.
- 후처리 정책 gate를 통합한 뒤 `scripts/verify-auto-mosaic-default.ps1`를 다시 실행했고 전체 통과했다. 최신 통합 실행에서 `track-postprocess-policy`는 `filled=1`, `lostFilled=3`, `lostFrames=33,34,35`, `removedShort=3`, `filledFrames=10,11,12,25,30,31,32,33,34,35,50,51,52`였다. 품질 gate는 lost-fill 적용 후에도 `baselineFrames=19`, `optimizedFrames=19`, `avgBestIou=1.000`, `minBestIou=1.000`으로 통과했고, CPU 병렬 경로는 `totalMs=34,381ms`로 CPU single baseline `61,097ms`보다 빨랐다. 품질 gate의 실제 lost-fill frame도 baseline/optimized 모두 `lostFrames=6,7,8`로 일치했다. auto tune gate는 `FaceOnnxDetector/GPU:DirectML`, `processed=150`, `interpolated=0`, `lostFilled=3`, `lostFrames=6,7,8`, `totalMs=64,020ms`였다.
- `scripts/verify-auto-mosaic-default.ps1 -RunExportSmoke -RunMediumAuto -RunLongAutoTune`가 통과했다. 해당 실행에서 export smoke는 `bitmapMaskFrames=0`, `directFaceFrames=31`, `totalMs=12,299ms`였고, 30초 medium CPU gate는 `processed=899`, `detects=899`, `filled=431`, `lostFilled=104`, `removedShort=77`, ROI `attempts=32`, `hits=22`, `totalMs=397,825ms`였다.
- 같은 verifier 실행에서 short auto-tune은 `FaceOnnxDetector/GPU:DirectML`, `processed=150`, `detects=150`, `interpolated=0`, `totalMs=84,769ms`였고, long auto-tune은 `FaceOnnxDetector/GPU:DirectML`, `processed=899`, `detects=899`, `interpolated=0`, ROI `attempts=32`, `hits=22`, `totalMs=430,952ms`였다.
- GPU 승격 margin과 CPU default 후보 추가 후 `.tmp/srcTest-smoke/smoke-0600-5s.mp4 -UseAutoTune`은 다시 `FaceOnnxDetector/GPU:DirectML`, `processed=150`, `detects=150`, `interpolated=0`, `totalMs=60,447ms`로 완료됐다. 같은 변경 직후 30초 long auto-tune은 `CPU 2세션/4스레드`를 선택했고 `processed=899`, `detects=899`, `interpolated=0`, ROI `attempts=32`, `hits=22`, `totalMs=438,618ms`였다. provider 선택은 부하에 따라 흔들릴 수 있어 gate는 provider 고정보다 전 프레임 처리/품질/병렬 경로를 확인한다.
- 30초 대표 구간 export 포함 smoke도 실행했다. `.tmp/srcTest-smoke/smoke-1200-30s.mp4`에서 자동 검출은 `processed=899`, `detectMs=762,418`, `totalMs=382,985ms`, track 후처리는 `filled=431`, `lostFilled=104`, `removedShort=77`, ROI는 `attempts=32`, `hits=22`, `elapsedMs=17,773`이었다. 이어진 export는 `frames=902`, `bitmapMaskFrames=0`, `directFaceFrames=778`, `swsToBgraMs=15,927`, `maskMs=47,715`, `swsToEncMs=24,851`, `encodeMs=4,361`, `totalMs=148,317ms`였다. 이 구간에서는 export보다 detector가 더 큰 병목이다.
- `-RunMediumExport` 추가 후 `scripts/verify-auto-mosaic-default.ps1` 기본 실행도 다시 통과했다. 최신 실행에서 품질 gate는 `baselineFrames=19`, `optimizedFrames=19`, `avgBestIou=1.000`, CPU 병렬 `totalMs=34,870ms`였고, ROI-hit 대표 gate는 `attempts=11`, `hits=5`, `elapsedMs=9,134`였다. auto-tune은 `CPU 2세션/default`, `provider=CPU`, `processed=150`, `detects=150`, `interpolated=0`, `totalMs=52,773ms`로 통과했다.
- `scripts/verify-auto-mosaic-default.ps1 -RunMediumExport`도 실제 통과했다. 이 실행에서 품질 gate는 CPU single `totalMs=54,409ms`, CPU 병렬 `totalMs=34,181ms`, `avgBestIou=1.000`이었다. ROI-hit 대표 gate는 `attempts=11`, `hits=5`, `elapsedMs=9,288`이었다. `medium-auto-export`는 자동 검출 `processed=899`, `detectMs=629,598`, `totalMs=316,366ms`, track `filled=431`, `lostFilled=104`, `removedShort=77`, ROI `attempts=32`, `hits=22`, `elapsedMs=13,718`을 확인했고, export는 `frames=902`, `bitmapMaskFrames=0`, `directFaceFrames=778`, `maskMs=39,891`, `swsToEncMs=22,251`, `encodeMs=4,160`, `totalMs=127,750ms`로 통과했다. 마지막 short auto-tune gate는 `CPU 2세션/default`, `processed=150`, `detects=150`, `interpolated=0`, `totalMs=53,885ms`였다.
- `MaskedVideoExporter.ApplyFaceRectsAndBlur()`에 단일 얼굴 fast path를 추가했다. 얼굴이 1개인 frame은 기존 ellipse alpha/soft edge 계산은 유지하되, per-pixel shape list 순회와 radius map 생성/조회 비용을 건너뛴다. 적용 후 `scripts/verify-auto-mosaic-default.ps1 -RunExportSmoke`가 다시 통과했다. 이 실행에서 direct face export smoke는 `frames=61`, `bitmapMaskFrames=0`, `directFaceFrames=31`, `maskMs=713`, `swsToEncMs=1,089`, `encodeMs=479`, `totalMs=7,066ms`였고, short auto-tune gate는 `CPU 2세션/default`, `processed=150`, `detects=150`, `interpolated=0`, `totalMs=61,999ms`였다.
- 전체 원본 `srcTest/260102_jp_10.mp4`는 `3840x2160`, `30000/1001fps`, `duration=1067.599867`, `nb_frames=31996`, 파일 크기 약 `2.3GB`다. 30초 대표 구간 자동 검출+export 수치를 단순 환산하면 전체 원본 풀런은 몇 시간 단위가 될 수 있다.
- detector 호출 수를 줄이는 `sparse-pipe-parallel`도 품질 gate에서 확인했다. `.tmp/srcTest-smoke/smoke-0600-3s.mp4`에 `-OptimizedDetectEvery 2`를 적용하면 `detects=45`, `interpolated=9`, `detectMs=32,705`, `totalMs=16,820ms`로 매우 빨라졌지만, FaceONNX all-frame baseline 대비 `baselineFrames=19`, `optimizedFrames=22`, `onlyBaseline=3`, `onlyOptimized=6`, `avgBestIou=0.930`, `minBestIou=0.627`, `passed=False`였다. 따라서 `DetectEveryNFrames=2` sparse tracking은 현재 품질 최우선 기본값으로 승격하지 않는다.

남은 한계:

- auto tune 측정 프레임 수와 후보 수를 늘렸기 때문에 자동 시작 전 튜닝 시간이 조금 늘 수 있다.
- 장기 영상에서 CPU/GPU 우위가 실행 시점 부하에 따라 바뀌므로, 전체 영상 기준 최종 선택 정책은 더 긴 대표 구간으로 계속 검증해야 한다.

## 2026-05-12 completion audit
목표를 구체 deliverable로 나누면 다음과 같다.

| 요구/완료 기준 | 현재 증거 | 판정 |
| --- | --- | --- |
| 기본 품질을 희생하지 않는 자동 모자이크 | `DownscaleRatio=1.0`, `DetectEveryNFrames=1` 경로를 유지한다. `scripts/verify-auto-mosaic-default.ps1` 품질 gate에서 baseline/optimized 모두 `baselineFrames=19`, `optimizedFrames=19`, `avgBestIou=1.000`, `minBestIou=1.000`으로 통과했다. | 충족 |
| 처리 속도 개선 | 같은 6분 3초 클립 gate에서 CPU single baseline `totalMs=66,771ms`, CPU 병렬 `totalMs=36,138ms`를 확인했다. 최신 강한 verifier에서 3초 품질 gate는 CPU single `totalMs=70,544ms`, CPU 병렬 `totalMs=41,973ms`로 통과했고, 30초 medium CPU gate는 `processed=899`, `totalMs=397,825ms`였다. auto tune은 CPU/GPU 후보를 모두 비교하고 `pipe-parallel` 경로로 통과한다. `DetectEveryNFrames=2` sparse는 `totalMs=16,820ms`까지 줄었지만 품질 gate 실패로 기본 승격하지 않는다. | 부분 충족 |
| 작은 얼굴 후보 보존 | `MinSmallFaceAreaRatio=0.00025`, `SmallFaceConfidenceMin=0.72`, stats gate를 추가했고 15분 2초 구간에서 작은 후보 유지/오탐 제거를 확인했다. | 부분 충족 |
| 작은 얼굴 detector 미탐 자체 감소 | 현재 변경은 detector가 반환한 후보를 덜 버리는 방식이다. detector가 후보를 반환하지 못한 얼굴을 새로 찾는 기본 `FaceTrackRoiRefiner` 경로는 gap-fill/lost-fill 후보까지 추가했고 ROI 전용 threshold도 더 민감하게 낮췄다. 9분 2초 대표 구간에서는 `attempts=11`, `hits=5`로 실제 ROI 보정 hit를 확인했다. 다만 작은 얼굴 전용 새 detector backend는 없다. | 부분 충족 |
| 물건 오탐 감소 | 15분 2초 구간에서 빨간 수건/물건 오탐은 `removedShort=4`로 제거됐고, synthetic gate에서 1~2개 검출짜리 작은 오탐 제거와 보간 재생성 차단을 확인했다. | 부분 충족 |
| 반쪽 얼굴/뒤도는 얼굴 보존 | synthetic gate에서 가장자리 반쪽 얼굴 후보와 중앙 3프레임 작은 후보 보존을 확인했다. 15분 2초 frame 56의 오른쪽 가장자리 후보도 프레임 이미지로 확인해 보존했다. | 부분 충족 |
| 확정 track 유지 | 확정 track lost-fill을 추가했고 synthetic gate에서 `lostFilled=3`, `lostFrames=33,34,35`, 실제 9분 2초 smoke에서 `lostFilled=10`을 확인했다. | 부분 충족 |
| 오탐 잔상 방지 | lost-fill은 3개 이상 검출된 강한 track만, 최대 3프레임만 적용한다. 작은 track은 lost-fill 대상에서 제외한다. | 부분 충족 |
| detector/backend 교체 가능성 | `FaceDetectorBackend.ScrfdOnnx`, `ScrfdOnnxDetectorOptions`, `ScrfdOnnxDetector`를 추가했고 `FaceDetectorFactory`에서 생성 가능하다. `scripts/run-srcTest-smoke.ps1 -ScrfdModelPath <model.onnx>`로 FaceONNX baseline 대비 SCRFD optimized A/B를 실행할 수 있다. `.tmp`에 받은 SCRFD 500M/10G 후보는 실행됐지만 quality gate를 통과하지 못해 기본 승격하지 않는다. | 부분 충족 |
| ROI 재검출/2차 verifier | `FaceTrackRoiRefiner`가 track gap-fill/lost-fill 후보만 raw BGRA로 다시 읽어 ROI crop 재검출을 수행한다. 전역 threshold는 유지하고 ROI 전용 CPU detector만 `0.12/0.12`로 더 민감하게 돌린다. 기본 품질 gate에서는 `attempts=8`, `hits=0`으로 품질 유지가 확인됐고, 9분 2초 ROI-hit 대표 gate에서는 `attempts=11`, `hits=5`로 실제 보정 hit가 확인됐다. 후보 frame 정렬/sequential read 최적화 후 해당 구간은 `seeks=4`, `decoded=26`, `elapsedMs=9,455`로 계측됐다. 강한 2차 모델 verifier는 아직 없다. | 부분 충족 |
| export 병목 개선 | direct face rect export와 summary는 유지된다. 단일 얼굴 direct blur fast path 추가 후 최신 `scripts/verify-auto-mosaic-default.ps1 -RunExportSmoke`에서 `.tmp/srcTest-smoke/smoke-1200-2s.mp4` export가 `bitmapMaskFrames=0`, `directFaceFrames=31`, `swsToBgraMs=552`, `maskMs=713`, `swsToEncMs=1,089`, `encodeMs=479`, `totalMs=7,066ms`로 완료됐다. `scripts/verify-auto-mosaic-default.ps1 -RunMediumExport`로 재현 가능한 30초 대표 구간 export gate도 통과했고, `frames=902`, `bitmapMaskFrames=0`, `directFaceFrames=778`, `maskMs=39,891`, `swsToEncMs=22,251`, `encodeMs=4,160`, `totalMs=127,750ms`였다. 전체 17분 원본 export 병목은 아직 없다. | 부분 충족 |
| 실제 `srcTest` 대표 구간 검증 | 원본 `srcTest/260102_jp_10.mp4`는 `3840x2160`, `duration=1067.599867`, `nb_frames=31996`로 확인했다. 6분/9분/12분/15분 짧은 clip smoke와 12분 30초 자동 검출 smoke가 있다. 최신 30초 검증에서는 `processed=899`, `detects=899`, `filled=431`, `lostFilled=104`, `removedShort=77`, ROI `attempts=32`, `hits=22`, CPU medium `totalMs=397,825ms`, long auto-tune `totalMs=430,952ms` 및 변경 후 long auto-tune 직접 smoke `totalMs=438,618ms`를 확인했다. 30초 export 포함 smoke는 자동 검출 `totalMs=382,985ms`, export `totalMs=148,317ms`로 완료됐다. 전체 17분 원본 end-to-end 자동 검출 + export 풀런은 아직 없다. | 부분 충족 |
| GUI smoke | shell harness 검증은 통과했다. Avalonia GUI에서 open, preview, auto detect, manual edit, export 전체 흐름은 직접 확인하지 않았다. | 미완료 |
| 빌드/정적 gate | `dotnet build FaceShield.sln` 성공, `git diff --check` 통과, `scripts/verify-auto-mosaic-default.ps1 -RunExportSmoke -RunMediumAuto -RunLongAutoTune` 통과, `scripts/verify-auto-mosaic-default.ps1 -RunMediumExport` 통과. 최신 강한 verifier에는 track policy, 품질 gate, ROI-hit 대표 gate, direct face export smoke, medium 30초 gate, medium 30초 export gate, short auto-tune gate, long auto-tune gate가 포함된다. SCRFD/YuNet backend와 YuNet tiling 실험, auto-tune CPU/GPU 선택 보정 후 `dotnet build FaceShield.sln`은 7개 기존 FFmpeg obsolete warning만 남기고 성공했고, `git diff --check`도 통과했다. 최신 `-RunExportSmoke`는 단일 얼굴 fast path 추가 후 다시 통과했다. 품질 gate는 `baselineFrames=19`, `optimizedFrames=19`, `avgBestIou=1.000`, `minBestIou=1.000`, CPU 병렬 `totalMs=32,437ms`였고, direct export smoke는 `bitmapMaskFrames=0`, `directFaceFrames=31`, `maskMs=713`, `totalMs=7,066ms`였다. 최신 `-RunMediumExport` verifier는 자동 검출 `totalMs=316,366ms`, export `totalMs=127,750ms`로 통과했다. | 충족 |

현재 결론:

- 목표를 완료로 처리할 수 없다.
- 지금까지의 변경은 기본 품질 유지, 병렬 처리 속도, track continuity, 짧은 오탐 제거, 반쪽 얼굴 후보 보존을 보강한 단계다.
- 다음으로 실제 목표에 더 직접적으로 남은 작업은 SCRFD decoder/전처리 추가 보정 또는 다른 detector 후보 A/B, 전체 17분 원본 또는 그에 준하는 긴 구간 end-to-end 자동 검출 + export 검증, Avalonia GUI smoke다. `FaceTrackRoiRefiner`의 실제 hit 대표 구간은 9분 2초 clip에서 확보했지만, 강한 2차 모델 verifier의 실제 모델 검증은 아직 없다.

## 2026-05-12 SCRFD 외부 모델 A/B 1차
외부 모델 후보는 Hugging Face `RuteNL/SCRFD-face-detection-ONNX`의 `500m.onnx`와 `10g_bnkps.onnx`를 `.tmp/models/`에만 내려받아 테스트했다. 해당 모델 카드는 Apache-2.0으로 표시되지만, upstream pretrained model 출처가 InsightFace이므로 배포/상용 사용은 별도 확인이 필요하다. 모델 파일은 repo에 포함하지 않는다.

추가/변경 파일:

- `Services/FaceDetection/ScrfdOnnxDetector.cs`
- `Services/FaceDetection/ScrfdOnnxDetectorOptions.cs`
- `Services/FaceDetection/YuNetOnnxDetector.cs`
- `Services/FaceDetection/YuNetOnnxDetectorOptions.cs`
- `Services/FaceDetection/FaceDetectorBackend.cs`
- `Services/FaceDetection/FaceDetectorFactory.cs`
- `Services/FaceDetection/FaceDetectorFactoryOptions.cs`
- `scripts/run-srcTest-smoke.ps1`
- `scripts/inspect-onnx-outputs.ps1`

검증:

- `scripts/inspect-onnx-outputs.ps1 -ModelPath .tmp/models/scrfd_500m.onnx` 결과 input은 `input.1=1x3x640x640`, output은 `score_8/16/32`, `bbox_8/16/32` 구조였다.
- `scrfd_500m.onnx` 단독 optimized smoke는 `.tmp/srcTest-smoke/smoke-0600-3s.mp4`에서 실행됐다. `ConfidenceThreshold=0.25` 기준 `totalMs=13,174ms`, `faceMaskFrames=23`이었으나 FaceONNX baseline과 비교하면 `baselineFrames=19`, `optimizedFrames=23`, `onlyBaseline=13`, `onlyOptimized=17`, `avgBestIou=0.001`, `passed=False`였다.
- `scrfd_500m.onnx`를 `ConfidenceThreshold=0.5`로 올리면 `totalMs=11,166ms`까지 줄었지만 `faceMaskFrames=0`으로 전부 미탐이었다.
- `scrfd_10g_bnkps.onnx` 단독 optimized smoke는 `totalMs=27,819ms`, `faceMaskFrames=19`로 baseline frame 수와 같았다.
- 그러나 `scrfd_10g_bnkps.onnx` A/B gate는 `baselineFrames=19`, `optimizedFrames=19`, `onlyBaseline=16`, `onlyOptimized=16`, `avgBestIou=0.010`, `passed=False`였다.
- insightface 방식에 맞춘 letterbox + RGB 전처리를 추가로 적용했지만, `scrfd_10g_bnkps.onnx`는 `faceMaskFrames=2`, `onlyBaseline=17`, `avgBestIou=0.290`, `passed=False`로 더 나빠졌다.
- stretch + BGR 조합도 `scrfd_10g_bnkps.onnx`에서 `baselineFrames=19`, `optimizedFrames=19`, `onlyBaseline=16`, `onlyOptimized=16`, `avgBestIou=0.010`, `passed=False`로 기존 stretch + RGB와 동일하게 실패했다.

판정:

- SCRFD backend는 실제 모델 로드와 자동 모자이크 파이프라인 실행까지 가능하다.
- 현재 decoder/전처리 조합 또는 후보 모델은 FaceONNX baseline 품질 gate를 통과하지 못한다. letterbox/RGB, stretch/RGB, stretch/BGR 중 통과한 조합은 없다.
- 속도만 보면 SCRFD 500M은 매우 빠르지만 품질이 부족하고, SCRFD 10G는 일부 프레임 수는 맞지만 좌표/구간 일치가 부족하다.
- 따라서 기본 detector 교체는 보류한다. 다음 후보는 letterbox 입력, RGB/BGR 입력 옵션, bbox decode 방식별 A/B, 또는 다른 모델 계열(YuNet/RetinaFace/YOLO-face) 비교다.

## 2026-05-12 YuNet 외부 모델 A/B 1차
OpenCV Zoo의 `face_detection_yunet_2023mar.onnx`를 `.tmp/models/`에만 내려받아 테스트했다. OpenCV Zoo README 기준 YuNet은 MIT License이며, 2023-March 모델은 WIDER Face 기준 AP hard 0.7503으로 공개되어 있다. 모델 파일은 repo에 포함하지 않는다.

구현:

- `FaceDetectorBackend.YuNetOnnx`를 추가했다.
- `YuNetOnnxDetector`를 추가해 OpenCV `FaceDetectorYN`의 공개 postprocess와 같은 방식으로 `cls_8/16/32`, `obj_8/16/32`, `bbox_8/16/32`를 decode한다.
- `scripts/run-srcTest-smoke.ps1 -YuNetModelPath <model.onnx>`로 FaceONNX baseline 대비 YuNet optimized A/B를 실행할 수 있다.
- `-YuNetUseTiling`, `-YuNetTileOnly`, `-YuNetTileColumns`, `-YuNetTileRows`, `-YuNetTileOverlapRatio` 옵션을 추가해 4K 원본을 640 입력 하나로만 압축하지 않는 tile/multi-region 실험도 실행할 수 있게 했다.

검증:

- `scripts/inspect-onnx-outputs.ps1 -ModelPath .tmp/models/face_detection_yunet_2023mar.onnx` 결과 input은 `input=1x3x640x640`, output은 `cls_8/16/32`, `obj_8/16/32`, `bbox_8/16/32`, `kps_8/16/32` 12개였다.
- `.tmp/srcTest-smoke/smoke-0600-3s.mp4` YuNet 단독 optimized smoke는 `totalMs=9,825ms`, `detectMs=15,890ms`, `faceMaskFrames=33`, ROI refiner `attempts=7`, `hits=7`로 매우 빨랐다.
- 같은 구간 FaceONNX baseline 대비 A/B gate는 `baselineFrames=19`, `optimizedFrames=33`, `onlyBaseline=4`, `onlyOptimized=18`, `avgBestIou=0.277`, `passed=False`였다.
- `ConfidenceThreshold=0.6`은 오탐을 줄였지만 `faceMaskFrames=0`으로 전부 미탐이었다.
- `-YuNetUseTiling` 단독 optimized smoke는 `totalMs=41,791ms`, `detectMs=82,467ms`, `faceMaskFrames=51`, ROI refiner `attempts=28`, `hits=27`이었다.
- 같은 tiling 설정의 FaceONNX baseline 대비 A/B gate는 `baselineFrames=19`, `optimizedFrames=51`, `onlyBaseline=4`, `onlyOptimized=36`, `avgBestIou=0.272`, `minBestIou=0.000`, `passed=False`였다.
- full frame 입력을 빼고 tile만 돌리는 `-YuNetUseTiling -YuNetTileOnly` A/B gate도 실행했다. 결과는 `totalMs=34,976ms`, `detectMs=69,111ms`, `faceMaskFrames=38`, ROI refiner `attempts=25`, `hits=24`였고, FaceONNX baseline 대비 `baselineFrames=19`, `optimizedFrames=38`, `onlyBaseline=5`, `onlyOptimized=24`, `avgBestIou=0.163`, `minBestIou=0.000`, `passed=False`였다.

판정:

- YuNet backend도 실제 모델 로드와 자동 모자이크 파이프라인 실행까지 가능하다.
- 속도는 현재 후보 중 가장 좋지만, 4K 원본을 640 고정 입력으로 줄이는 구조와 현재 threshold 조합에서는 FaceONNX baseline 품질 gate를 통과하지 못한다.
- tiling은 작은 얼굴 recall을 늘리는 대신 오탐 후보를 크게 늘리고 단일 YuNet 대비 속도 이점도 줄었다. tile-only는 full+tile보다 빠르고 오탐 frame 수는 줄었지만 baseline과의 좌표/구간 일치가 더 나빴다. 현재 2x2 tiling 설정은 기본 detector 교체 후보가 아니다.
- 기본 detector 교체는 보류한다. YuNet은 빠른 1차 후보/ROI verifier 후보로 남기되, 기본 승격 전에는 threshold curve, tile-only/full+tile 비교, 오탐 필터, 더 적합한 대체 모델(RetinaFace/YOLO-face 등)을 별도로 검증해야 한다.
