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
- `scrfd_500m.onnx`를 `ConfidenceThreshold=0.5`로 올리면 `totalMs=11,166ms`까지 줄었지만 `faceMaskFrames=0`이었다. 알려진 얼굴 구간에서 최종 마스크가 0프레임이므로 현재 pipeline 결과로는 불합격이다.
- `scrfd_10g_bnkps.onnx` 단독 optimized smoke는 `totalMs=27,819ms`, `faceMaskFrames=19`로 baseline frame 수와 같았다.
- 그러나 `scrfd_10g_bnkps.onnx` A/B gate는 `baselineFrames=19`, `optimizedFrames=19`, `onlyBaseline=16`, `onlyOptimized=16`, `avgBestIou=0.010`, `passed=False`였다.
- insightface 방식에 맞춘 letterbox + RGB 전처리를 추가로 적용했지만, `scrfd_10g_bnkps.onnx`는 `faceMaskFrames=2`, `onlyBaseline=17`, `avgBestIou=0.290`, `passed=False`로 더 나빠졌다.
- stretch + BGR 조합도 `scrfd_10g_bnkps.onnx`에서 `baselineFrames=19`, `optimizedFrames=19`, `onlyBaseline=16`, `onlyOptimized=16`, `avgBestIou=0.010`, `passed=False`로 기존 stretch + RGB와 동일하게 실패했다.

판정:

- SCRFD backend는 실제 모델 로드와 자동 모자이크 파이프라인 실행까지 가능하다.
- 현재 decoder/전처리 조합 또는 후보 모델은 FaceONNX baseline-diff gate를 통과하지 못한다. letterbox/RGB, stretch/RGB, stretch/BGR 중 통과한 조합은 없다.
- 이 결과만으로 SCRFD 모델 자체가 실제 얼굴 검출 기준에서 틀렸다고 단정하지 않는다. 다만 `avgBestIou=0.001~0.010` 수준의 좌표 불일치와 `faceMaskFrames=0` 결과가 있어 현재 adapter/전처리/후처리 조합은 추천할 수 없다.
- 속도만 보면 SCRFD 500M은 매우 빠르지만 현재 pipeline 출력은 기존 동작과 크게 다르고, SCRFD 10G는 일부 프레임 수는 맞지만 좌표/구간 일치가 부족하다.
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
- `ConfidenceThreshold=0.6`은 YuNet-only 후보를 줄였지만 `faceMaskFrames=0`이었다. 알려진 얼굴 구간에서 최종 마스크가 0프레임이므로 현재 pipeline 결과로는 불합격이다.
- `-YuNetUseTiling` 단독 optimized smoke는 `totalMs=41,791ms`, `detectMs=82,467ms`, `faceMaskFrames=51`, ROI refiner `attempts=28`, `hits=27`이었다.
- 같은 tiling 설정의 FaceONNX baseline 대비 A/B gate는 `baselineFrames=19`, `optimizedFrames=51`, `onlyBaseline=4`, `onlyOptimized=36`, `avgBestIou=0.272`, `minBestIou=0.000`, `passed=False`였다.
- full frame 입력을 빼고 tile만 돌리는 `-YuNetUseTiling -YuNetTileOnly` A/B gate도 실행했다. 결과는 `totalMs=34,976ms`, `detectMs=69,111ms`, `faceMaskFrames=38`, ROI refiner `attempts=25`, `hits=24`였고, FaceONNX baseline 대비 `baselineFrames=19`, `optimizedFrames=38`, `onlyBaseline=5`, `onlyOptimized=24`, `avgBestIou=0.163`, `minBestIou=0.000`, `passed=False`였다.

판정:

- YuNet backend도 실제 모델 로드와 자동 모자이크 파이프라인 실행까지 가능하다.
- 속도는 현재 후보 중 가장 좋지만, 4K 원본을 640 고정 입력으로 줄이는 구조와 현재 threshold 조합에서는 FaceONNX baseline 품질 gate를 통과하지 못한다.
- tiling은 작은 얼굴 후보를 늘리는 대신 YuNet-only 후보도 크게 늘리고 단일 YuNet 대비 속도 이점도 줄었다. tile-only는 full+tile보다 빠르고 YuNet-only frame 수는 줄었지만 baseline과의 좌표/구간 일치가 더 나빴다. 현재 2x2 tiling 설정은 기본 detector 교체 후보가 아니다.
- 기본 detector 교체는 보류한다. YuNet은 빠른 1차 후보/ROI verifier 후보로 남기되, 기본 승격 전에는 threshold curve, tile-only/full+tile 비교, 오탐 필터, 더 적합한 대체 모델(RetinaFace/YOLO-face 등)을 별도로 검증해야 한다.

## 2026-05-12 현재 환경 재검증 계획
기존 SCRFD/YuNet A/B와 auto tune 검증은 GPU가 없는 노트북 환경에서 수행된 결과가 섞여 있다. 따라서 그 결과는 CPU-only 저사양 기준의 참고값으로 보고, 현재 목표 환경에서 다시 검증한다.

커뮤니케이션/문구 기준:

- 사용자에게 보이는 문구, 문서 기록, 검증 결과 설명, UI 문구에서는 반말을 절대 사용하지 않는다.
- 모든 설명은 존댓말 또는 중립적인 기술 문체로 작성한다.
- 급한 작업 메모라도 사용자를 향한 표현에는 반말, 명령조, 비하 표현을 남기지 않는다.
- Git 관련 작업은 별도 확인을 받는다. 특히 `push`, `pull`, 작업 취소, 되돌리기처럼 원격/브랜치/작업 상태에 영향을 주는 작업은 사용자 확인 후 진행한다.
- 그 외 목표 범위 안의 코드 구현, 코드 수정, 문서 수정, 로컬 테스트, smoke 실행, 검증 스크립트 실행은 매번 되묻지 않고 자율적으로 진행한다.
- 자율 진행한 작업은 결과와 근거를 문서와 최종 보고에 남긴다.
- 실행 환경의 권한 시스템 때문에 도구 승인 프롬프트가 필요한 경우가 있을 수 있지만, 작업 판단 자체는 위 기준에 따라 자율 진행한다.

이번 라운드의 목표는 최상 검증 품질과 빠른 처리 속도를 동시에 달성하는 것이다. 우선순위는 품질을 먼저 통과시키고, 통과한 후보들 중에서 가장 빠른 설정을 찾는 방식으로 둔다.

- 얼굴 미탐은 허용하지 않는다. 작은 얼굴, 먼 얼굴, 반쪽 얼굴, 고개가 돌아간 얼굴도 노출되면 실패로 본다.
- 얼굴이 아닌 물건 오탐도 허용하지 않는다. 불필요한 모자이크는 영상 품질을 떨어뜨리므로 실패로 본다.
- 같은 얼굴의 모자이크가 중간에 사라졌다 나타나는 깜박임도 실패로 본다.
- 모자이크 박스가 얼굴을 따라 자연스럽게 이어지지 않고 튀거나 흔들리면 실패로 본다.
- 한 번 사람 얼굴로 확정된 track은 화면에서 실제로 사라지거나 scene cut/큰 위치 변화로 종료 판정되기 전까지 모자이크가 유지되어야 한다.
- 확정 track이 detector 미탐 때문에 1~몇 프레임 비어도 즉시 모자이크를 제거하지 않는다. 이전 이동 방향과 크기 변화로 예측/보간해 유지하고, ROI 재검출로 확인한다.
- 확정 track 종료는 긴 미탐, 화면 밖 이동, scene cut, 비정상적인 위치/크기 변화 같은 명확한 조건이 있을 때만 허용한다.
- 속도 개선도 핵심 목표다. 다만 속도는 위 품질 조건을 만족한 후보끼리 비교한다. 빠르지만 미탐, 오탐, 깜박임이 생기는 설정은 기본값이나 추천값으로 쓰지 않는다.
- 최종 후보는 `미탐 0`, `오탐 0`, `깜박임 0`, `박스 튐 최소화`를 만족하면서 `[AutoRunSummary].totalMs`와 `[ExportRunSummary].totalMs`가 가장 낮은 조합이어야 한다.

따라서 자동 gate의 수치만으로 완료를 판단하지 않는다. `avgBestIou`, `minBestIou`, `faceMaskFrames`, `removedShort`, `lostFilled` 같은 로그는 후보를 좁히는 근거일 뿐이며, 대표 구간의 육안 확인에서 미탐/오탐/깜박임이 없어야 통과로 본다.

FaceONNX baseline은 기존 앱 동작 보존을 위한 회귀 기준이지 실제 정답 라벨이 아니다. A/B 로그의 `onlyBaseline`은 FaceONNX에만 있는 후보 frame, `onlyOptimized`는 YOLO/SCRFD/YuNet 등 optimized detector에만 있는 후보 frame을 뜻한다. 이 값은 detector 간 차이를 찾는 신호일 뿐이며, 곧바로 실제 `미탐` 또는 `오탐`으로 판정하지 않는다.

실제 오탐/미탐 판정 기준은 다음과 같이 분리한다.

- 실제 미탐: 라벨된 GT 또는 대표 frame overlay 육안 확인에서 사람 얼굴이 보이는데 detector 결과가 없거나 모자이크가 유지되지 않는 경우.
- 실제 오탐: 라벨된 GT 또는 대표 frame overlay 육안 확인에서 얼굴이 아닌 손/물체/배경인데 detector가 얼굴로 잡아 모자이크 대상이 되는 경우.
- baseline-diff: `onlyBaseline`, `onlyOptimized`, `boxCountDiff`, `lowIou`처럼 FaceONNX와 optimized detector의 후보 수나 박스 정의가 다른 경우.

따라서 YOLO가 FaceONNX보다 빠르고 `onlyOptimized`를 만들더라도, 그 후보가 실제 얼굴이면 recall 개선 가능성으로 따로 기록한다. 반대로 `onlyBaseline`도 FaceONNX가 맞고 YOLO가 틀렸다는 뜻으로 단정하지 않는다. 추천 여부는 먼저 baseline-diff gate로 기존 동작 변화 폭을 확인하고, 그 다음 representative overlay 또는 GT 기준으로 실제 얼굴/비얼굴 여부를 확인해 판단한다.

이번 재검증에서는 YuNet을 우선 제외한다.

제외 이유:

- YuNet은 속도는 빠르지만 기존 A/B에서 FaceONNX-only/YuNet-only frame과 baseline 좌표 불일치가 컸다.
- tiling을 켜면 작은 얼굴 후보는 늘지만 YuNet-only 후보도 크게 늘고 속도 이점이 줄었다.
- 현재 문제의 핵심은 실제 작은 얼굴 누락, 실제 물건 오검출, track 깜박임, 긴 export 시간이므로 YuNet을 계속 튜닝하기보다 FaceONNX baseline과 SCRFD 후보를 먼저 현재 환경에서 다시 비교한다.

현재 환경 검증 대상:

1. `FaceONNX`
   - 현재 기본 detector이자 안정 baseline이다.
   - CPU/GPU auto tune 결과를 모두 기록한다.
   - track 후처리, ROI refiner, 작은 얼굴 filter가 켜진 현재 기본 경로를 기준으로 둔다.

2. `SCRFD`
   - 작은 얼굴과 미탐 감소 가능성이 있는 후보로 다시 검증한다.
   - 기존 노트북 검증에서 실패한 `500M`, `10G` 결과는 폐기하지 않되, 현재 환경에서 같은 clip과 같은 quality gate로 다시 확인한다.
   - 가능하면 `2.5G` 계열도 추가 후보로 검토한다.

이번 검증에서 고정할 기본 조건:

- `DownscaleRatio=1.0`
- `DetectEveryNFrames=1`
- `UseTracking=true`
- `ParallelDetectorCount`는 `2`와 `4`를 모두 측정한다.
- threshold는 현재 사용자 기준값 `DetectionThreshold=0.2`, `ConfidenceThreshold=0.25`, `NmsThreshold=0.7`을 시작점으로 사용한다. 이 값은 고정 결론이 아니다.
- FaceONNX와 SCRFD 각각에 대해 실제 누락 0, 실제 오검출 0, 깜박임 0에 가장 가까운 threshold 조합을 찾고, 검증된 조합을 기본값 후보로 문서화한다.
- 품질 비교 전에는 sparse 검출, downscale, threshold 완화로 속도를 얻지 않는다.

검증 지표:

- 작은 얼굴 미탐 frame 수
- 물건 오탐 frame 수
- 모자이크 깜박임 frame 수
- 박스 튐/흔들림 구간 수
- `faceMaskFrames`
- track 후처리 로그: `tracks`, `filled`, `lostFilled`, `removedShort`, `rewritten`
- ROI refiner 로그: `attempts`, `hits`, `seeks`, `decoded`, `elapsedMs`
- `[AutoRunSummary] totalMs`, `detectMs`, provider
- `[ExportRunSummary] totalMs`, `maskMs`, `swsToBgraMs`, `swsToEncMs`, `encodeMs`
- threshold sweep 결과: `DetectionThreshold`, `ConfidenceThreshold`, `NmsThreshold`별 미탐/오탐/깜박임/속도 변화
- 육안 확인 결과: 모자이크 깜박임, 박스 튐, 작은 얼굴 누락, 물건 오탐

진행 순서:

1. 현재 기본 `FaceONNX`로 대표 clip들을 다시 실행한다.
2. 같은 clip에서 `ParallelDetectorCount=2`와 `4`를 비교한다. 판단은 `detectMs`가 아니라 wall-clock인 `totalMs`를 우선한다.
3. SCRFD 후보 모델을 같은 clip에서 실행한다.
4. SCRFD가 FaceONNX 대비 작은 얼굴 미탐을 줄이는지 먼저 본다.
5. FaceONNX와 SCRFD 각각에 대해 threshold sweep을 실행한다.
6. threshold sweep은 detection/confidence를 낮춰 미탐을 줄이는 방향과, confidence/NMS를 올려 오탐을 줄이는 방향을 모두 포함한다.
7. threshold 후보마다 작은 얼굴 미탐, 물건 오탐, 깜박임, track 보정 로그, 속도를 기록한다.
8. SCRFD가 오탐을 늘리면 threshold, NMS, 후처리 필터 조합을 조정한다.
9. FaceONNX와 SCRFD 중 하나를 기본값으로 바로 교체하지 않고, `Balanced/Accurate` 같은 내부 모드 후보로 둔다.
10. representative clip에서 통과한 뒤에만 더 긴 구간과 export 포함 smoke를 실행한다.

판정 기준:

- SCRFD가 FaceONNX보다 실제 작은 얼굴 누락을 줄이고, 실제 물건 오검출과 모자이크 깜박임을 만들지 않을 때만 다음 후보로 유지한다.
- SCRFD가 빠르더라도 실제 누락/오검출/깜박임/박스 튐이 생기면 기본 승격하지 않는다.
- threshold 기본값은 하드코딩된 현재 값이 아니라, 현재 환경 대표 구간에서 가장 좋은 품질/속도 균형을 보인 검증값으로 정한다.
- 최종 문서에는 detector별 추천 threshold와 근거를 남긴다. 예: `FaceONNX 기본 후보: detection=?, confidence=?, nms=?`, `SCRFD 후보: confidence=?, nms=?`.
- FaceONNX가 현재 환경에서도 가장 안정적이면 기본 detector는 유지하고, SCRFD는 정확도 우선 실험 옵션으로 남긴다.
- YuNet은 이번 라운드에서는 제외하고, FaceONNX/SCRFD 비교가 끝난 뒤 fast mode 후보로 다시 볼지 결정한다.

### 2026-05-13 현재 환경 1차 실행 기록

대상 clip:

- `.tmp/srcTest-smoke/current-0030-2s.mp4`
- 원본: `/mnt/d/WorkSpace/src/260102_two4.mp4`의 00:00:30부터 2초 구간
- 공통 조건: `DownscaleRatio=1.0`, `DetectEveryNFrames=1`, `UseTracking=true`, `DetectionThreshold=0.2`, `ConfidenceThreshold=0.25`, `NmsThreshold=0.7`, export 포함

코드 변경:

- `Services/Analysis/FaceTrackInterpolator.cs`
- 확정 track lost-fill 조건에서 `IsSmallTrack(track, options)` 제외 조건을 제거했다.
- 목적은 한 번 사람 얼굴로 확정된 작은 얼굴 track이 detector 미탐 1~몇 프레임 때문에 바로 끊기지 않게 하는 것이다.
- 짧은 단발/저신뢰 track 제거 로직은 유지하므로, 1~2프레임짜리 작은 물건 오탐을 확정 track처럼 끝까지 유지하는 변경은 아니다.

FaceONNX 병렬 2 재검증:

- 변경 전: `faceMaskFrames=16`, `onlyBaseline=58,59`, `passed=False`
- 변경 후: `faceMaskFrames=19`, `onlyBaseline=none`, `onlyOptimized=none`
- 후처리 로그: `tracks=3`, `lostFilled=3`, `lostFrames=58,59,60`, `removedShort=1`, `rewritten=19`
- `[AutoRunSummary]`: `detector=FaceOnnxDetector/CPU`, `mode=pipe-parallel`, `parallel=2`, `detectMs=32629`, `totalMs=16732`
- `[ExportRunSummary]`: `directFaceFrames=19`, `totalMs=2897`
- `[SmokeQualityGate]`: `passed=False`, `frameMatchOk=True`, `iouOk=False`, `avgBestIou=0.755`, `minBestIou=0.560`
- 판단: 작은 얼굴 끝부분 깜박임/누락은 보정됐지만, baseline 대비 box 위치/크기 차이가 커서 최상 검증 품질 통과는 아니다.

FaceONNX 병렬 4 재검증:

- `faceMaskFrames=19`, `onlyBaseline=none`, `onlyOptimized=none`
- 후처리 로그: `tracks=3`, `lostFilled=3`, `lostFrames=58,59,60`, `removedShort=1`, `rewritten=19`
- `[AutoRunSummary]`: `detector=FaceOnnxDetector/CPU`, `mode=pipe-parallel`, `parallel=4`, `detectMs=73995`, `totalMs=19601`
- `[ExportRunSummary]`: `directFaceFrames=19`, `totalMs=2879`
- `[SmokeQualityGate]`: `passed=False`, `frameMatchOk=True`, `iouOk=False`, `avgBestIou=0.755`, `minBestIou=0.560`
- 판단: 이 clip과 현재 PC에서는 4스레드가 2스레드보다 느리다. 4스레드가 항상 빠르다고 볼 근거는 아직 없다.

SCRFD 500M 재검증:

- 모델: `.tmp/models/scrfd_500m.onnx`
- RGB/letterbox 기본 입력 결과: `faceMaskFrames=0`
- `[AutoRunSummary]`: `detector=ScrfdOnnxDetector`, `parallel=2`, `detectMs=5714`, `totalMs=5105`
- filter 로그: `regular=0`, `small=0`, `rejected=54`, `statsRejected=3`
- `[SmokeQualityGate]`: `passed=False`, `onlyBaseline=37,38,44,45,46,47,48,49,50,51,52,53,54,55,56,57,58,59,60`
- BGR 입력 결과도 `faceMaskFrames=0`
- BGR `[AutoRunSummary]`: `detectMs=5393`, `totalMs=3889`
- 판단: 추론 속도는 빠르지만 현재 decode/filter 조합에서는 최종 마스크가 0프레임이므로 현재 pipeline 기준 불합격이다. 이 결과만으로 SCRFD 500M 모델 자체가 실제 정답 기준에서 틀렸다고 단정하지 않는다.

SCRFD 10G 재검증:

- 모델: `.tmp/models/scrfd_10g_bnkps.onnx`
- RGB/letterbox 기본 입력 결과: `faceMaskFrames=0`
- `[AutoRunSummary]`: `detector=ScrfdOnnxDetector`, `parallel=2`, `detectMs=16131`, `totalMs=8539`
- filter 로그: `regular=0`, `small=0`, `rejected=26`, `statsRejected=0`
- stretch 입력 결과도 `faceMaskFrames=0`
- stretch `[AutoRunSummary]`: `detectMs=16897`, `totalMs=8841`
- 판단: 500M보다 느리고 현재 설정에서는 역시 최종 마스크가 0프레임이다. SCRFD는 모델 성능 평가 이전에 현재 SCRFD decode, 좌표 변환, 후처리 필터 호환성을 먼저 확인해야 한다.

1차 결론:

- 현재 기본 FaceONNX는 작은 얼굴 끝부분 누락을 후처리로 복구할 수 있음을 확인했다.
- `ParallelDetectorCount=2`가 이 clip에서는 `4`보다 빠르다.
- SCRFD 500M/10G는 원시 detector가 후보를 일부 반환하지만, 최종 필터를 통과하지 못해 모자이크가 0프레임이다.
- 따라서 SCRFD를 기본값 후보로 판단하기 전에 raw box 좌표, aspect ratio, area ratio, confidence 분포, `MultiplyBboxByStride`, letterbox/stretced 입력, 필터 기준을 먼저 계측해야 한다.
- threshold 기본값은 아직 확정하지 않는다. 현재 `0.2/0.25/0.7`은 시작점일 뿐이며, FaceONNX와 SCRFD 각각 별도의 sweep과 육안 확인이 필요하다.

### 2026-05-13 `260101_oneday6.mp4` 3초 구간 A/B

사용자 지정 테스트 원본:

- `D:\WorkSpace\src\260101_oneday6.mp4`
- 길이: 약 608.7초
- 검증 clip: `.tmp/srcTest-smoke/oneday6-0030-3s.mp4`
- 생성 조건: 원본 00:00:30부터 3초 구간
- 공통 조건: `DownscaleRatio=1.0`, `DetectEveryNFrames=1`, `UseTracking=true`, `DetectionThreshold=0.2`, `ConfidenceThreshold=0.25`, `NmsThreshold=0.7`, export 포함
- 이번 라운드에서 YuNet은 실행하지 않았다.

FaceONNX baseline:

- `[AutoRunSummary]`: `detector=FaceOnnxDetector/CPU`, `mode=pipe-single`, `totalFrames=91`, `processed=90`, `detects=90`, `parallel=1`
- 대표 실행값: `detectMs=32055`, `totalMs=33123`
- track 보정: `tracks=6`, `filled=8`, `lostFilled=3`, `lostFrames=44,45,46`, `removedShort=2`, `rewritten=19`
- ROI refiner: `attempts=11`, `hits=0`, `seeks=2`, `decoded=17`
- `faceMaskFrames=19`
- `[ExportRunSummary]`: `frames=91`, `directFaceFrames=19`, `maskMs=34`, `totalMs=5122`

FaceONNX parallel 2:

- `[AutoRunSummary]`: `detector=FaceOnnxDetector/CPU`, `mode=pipe-parallel`, `parallel=2`, `totalFrames=91`, `processed=90`, `detects=90`, `detectMs=59832`, `totalMs=30995`
- track 보정: `tracks=6`, `filled=8`, `lostFilled=3`, `lostFrames=44,45,46`, `removedShort=2`, `rewritten=19`
- ROI refiner: `attempts=11`, `hits=0`, `seeks=2`, `decoded=17`, `elapsedMs=2557`
- `faceMaskFrames=19`
- `[ExportRunSummary]`: `frames=91`, `directFaceFrames=19`, `maskMs=26`, `totalMs=4952`
- `[SmokeCompare]`: `baselineFrames=19`, `optimizedFrames=19`, `onlyBaseline=0`, `onlyOptimized=0`, `avgBestIou=1.000`, `minBestIou=1.000`
- `[SmokeQualityGate]`: `passed=True`
- 판단: baseline과 프레임/박스가 완전히 일치했다. 이 구간에서는 baseline보다 wall-clock이 조금 빠르다.

FaceONNX parallel 4:

- `[AutoRunSummary]`: `detector=FaceOnnxDetector/CPU`, `mode=pipe-parallel`, `parallel=4`, `totalFrames=91`, `processed=90`, `detects=90`, `detectMs=113143`, `totalMs=30003`
- track 보정: `tracks=6`, `filled=8`, `lostFilled=3`, `lostFrames=44,45,46`, `removedShort=2`, `rewritten=19`
- ROI refiner: `attempts=11`, `hits=0`, `seeks=2`, `decoded=17`, `elapsedMs=2480`
- `faceMaskFrames=19`
- `[ExportRunSummary]`: `frames=91`, `directFaceFrames=19`, `maskMs=27`, `totalMs=5001`
- `[SmokeCompare]`: `baselineFrames=19`, `optimizedFrames=19`, `onlyBaseline=0`, `onlyOptimized=0`, `avgBestIou=1.000`, `minBestIou=1.000`
- `[SmokeQualityGate]`: `passed=True`
- 판단: 이 clip에서는 4스레드가 AutoRunSummary wall-clock 기준으로 2스레드보다 조금 빠르다. 다만 export totalMs는 2스레드가 조금 낮다.

SCRFD 500M parallel 2:

- 모델: `.tmp/models/scrfd_500m.onnx`
- `[AutoRunSummary]`: `detector=ScrfdOnnxDetector`, `mode=pipe-parallel`, `parallel=2`, `totalFrames=91`, `processed=90`, `detects=90`, `detectMs=10657`, `totalMs=7843`
- filter 로그: `regular=5`, `small=0`, `rejected=4`, `statsRejected=8`
- track 보정: `tracks=3`, `filled=6`, `lostFilled=0`, `lostFrames=none`, `removedShort=2`, `rewritten=9`
- ROI refiner: `attempts=6`, `hits=6`, `seeks=1`, `decoded=7`, `elapsedMs=1053`
- `faceMaskFrames=9`
- `[ExportRunSummary]`: `frames=91`, `directFaceFrames=9`, `maskMs=4`, `totalMs=5229`
- `[SmokeCompare]`: `baselineFrames=19`, `optimizedFrames=9`, `common=6`, `onlyBaseline=13`, `onlyOptimized=3`, `avgBestIou=0.000`, `minBestIou=0.000`
- `[SmokeCompareFrames]`: `onlyBaseline=0,20,33,34,35,36,37,41,42,43,44,45,46`, `onlyOptimized=11,12,13`
- `[SmokeQualityGate]`: `passed=False`
- 판단: 미탐이 많고 optimized-only frame도 있어 오탐 위험이 있다. 품질 조건 실패다.

SCRFD 500M parallel 4:

- `[AutoRunSummary]`: `detector=ScrfdOnnxDetector`, `mode=pipe-parallel`, `parallel=4`, `totalFrames=91`, `processed=90`, `detects=90`, `detectMs=16163`, `totalMs=11396`
- filter 로그: `regular=5`, `small=0`, `rejected=4`, `statsRejected=8`
- track 보정: `tracks=3`, `filled=6`, `lostFilled=0`, `lostFrames=none`, `removedShort=2`, `rewritten=9`
- ROI refiner: `attempts=6`, `hits=6`, `seeks=1`, `decoded=7`, `elapsedMs=1036`
- `faceMaskFrames=9`
- `[ExportRunSummary]`: `frames=91`, `directFaceFrames=9`, `maskMs=5`, `totalMs=4739`
- `[SmokeCompare]`: `baselineFrames=19`, `optimizedFrames=9`, `common=6`, `onlyBaseline=13`, `onlyOptimized=3`, `avgBestIou=0.000`, `minBestIou=0.000`
- `[SmokeQualityGate]`: `passed=False`
- 판단: 2스레드보다 AutoRunSummary wall-clock이 느리고 품질 실패 양상은 같다.

SCRFD 10G parallel 2:

- 모델: `.tmp/models/scrfd_10g_bnkps.onnx`
- `[AutoRunSummary]`: `detector=ScrfdOnnxDetector`, `mode=pipe-parallel`, `parallel=2`, `totalFrames=91`, `processed=90`, `detects=90`, `detectMs=24897`, `totalMs=13467`
- filter 로그: `regular=13`, `small=0`, `rejected=18`, `statsRejected=0`
- track 보정: `tracks=3`, `filled=7`, `lostFilled=0`, `lostFrames=none`, `removedShort=1`, `rewritten=19`
- ROI refiner: `attempts=7`, `hits=0`, `seeks=1`, `decoded=11`, `elapsedMs=1830`
- `faceMaskFrames=19`
- `[ExportRunSummary]`: `frames=91`, `directFaceFrames=19`, `maskMs=141`, `totalMs=4947`
- `[SmokeCompare]`: `baselineFrames=19`, `optimizedFrames=19`, `common=0`, `onlyBaseline=19`, `onlyOptimized=19`
- `[SmokeCompareFrames]`: `onlyBaseline=0,14,15,16,17,18,19,20,33,34,35,36,37,41,42,43,44,45,46`, `onlyOptimized=50,51,52,53,54,55,56,57,58,59,60,61,62,63,75,76,77,78,79`
- `[SmokeQualityGate]`: `passed=False`
- 판단: 최종 frame 수는 같지만 baseline과 공통 frame이 0이다. baseline-diff 기준으로 기존 FaceONNX 얼굴 구간과 완전히 다른 구간을 잡고 있으므로 현재 pipeline 기준 불합격이다. 실제 미탐/오탐 확정은 overlay 또는 GT 확인이 필요하지만, 이 수치만으로도 기본/accurate 후보로 올릴 수 없다.

SCRFD 10G parallel 4:

- `[AutoRunSummary]`: `detector=ScrfdOnnxDetector`, `mode=pipe-parallel`, `parallel=4`, `totalFrames=91`, `processed=90`, `detects=90`, `detectMs=258485`, `totalMs=66993`
- filter 로그: `regular=13`, `small=0`, `rejected=18`, `statsRejected=0`
- track 보정: `tracks=3`, `filled=7`, `lostFilled=0`, `lostFrames=none`, `removedShort=1`, `rewritten=19`
- ROI refiner: `attempts=7`, `hits=0`, `seeks=1`, `decoded=11`, `elapsedMs=8306`
- `faceMaskFrames=19`
- `[ExportRunSummary]`: `frames=91`, `directFaceFrames=19`, `maskMs=355`, `totalMs=8175`
- `[SmokeCompare]`: `baselineFrames=19`, `optimizedFrames=19`, `common=0`, `onlyBaseline=19`, `onlyOptimized=19`
- `[SmokeCompareFrames]`: `onlyBaseline=0,14,15,16,17,18,19,20,33,34,35,36,37,41,42,43,44,45,46`, `onlyOptimized=50,51,52,53,54,55,56,57,58,59,60,61,62,63,75,76,77,78,79`
- `[SmokeQualityGate]`: `passed=False`
- 판단: 10G 4스레드는 매우 느리고 품질도 실패다. CPU에서 10G를 4세션 병렬로 돌리는 것은 현재 PC 기준 후보가 아니다.

`260101_oneday6.mp4` 1차 결론:

- 이 구간의 기본 후보는 FaceONNX 유지다.
- FaceONNX parallel 2와 4는 모두 baseline과 완전 일치했고 품질 gate를 통과했다.
- AutoRunSummary 기준 최고속은 FaceONNX parallel 4(`totalMs=30003`)였고, export까지 포함한 `ExportRunSummary`는 FaceONNX parallel 2(`totalMs=4952`)와 4(`totalMs=5001`)가 거의 비슷했다.
- SCRFD 500M은 빠르지만 `faceMaskFrames=9`로 baseline 19프레임 대비 FaceONNX-only frame이 크고 SCRFD-only frame도 있어 기존 동작과 차이가 크다.
- SCRFD 10G는 frame 수만 맞고 baseline frame 위치가 전부 달라 현재 pipeline 기준 불합격이다.
- 이번 고정 threshold `0.2/0.25/0.7` 조건에서는 SCRFD 500M/10G 모두 기본 detector 후보가 아니다.

### 다음 세션 목표: SCRFD 전용 adapter/필터 정합

현재 A/B 결과는 SCRFD 모델 자체의 최종 성능으로 단정하지 않는다. 현재 코드 구조가 FaceONNX 출력 특성에 맞춰져 있고, SCRFD는 같은 후처리/필터/track 기준에 그대로 들어가고 있다. 따라서 다음 세션의 목표는 SCRFD를 FaceONNX 파이프라인에 억지로 끼우는 것이 아니라, SCRFD 전용 adapter와 검증 로그를 수정하면서 FaceShield 품질 기준에 맞추는 것이다.

목표 문장:

- `FaceONNX baseline은 유지한다. SCRFD 500M/10G는 detector adapter, bbox decode, 좌표 복원, detector별 필터 옵션을 수정하면서 공정하게 비교 가능한 상태로 맞춘다. 최종 기준은 미탐 0, 오탐 0, 깜박임 0, 원본 해상도, 전 프레임 검출, tracking on, threshold sweep 기반 기본값 산정이다.`

다음 세션에서 해야 할 일:

1. SCRFD raw output 검증
   - output tensor 이름, shape, score tensor, bbox tensor 순서를 로그로 남긴다.
   - 현재 `PairScoreAndBoxTensors()`가 실제 모델 output 순서와 맞는지 확인한다.
   - `GuessStride()` 결과가 8/16/32 stride별 실제 output count와 맞는지 확인한다.
   - `anchorsPerPoint` 계산이 모델별로 올바른지 확인한다.

2. SCRFD bbox decode 검증
   - `MultiplyBboxByStride=true/false`를 비교한다.
   - `UseLetterboxResize=true/false`를 비교한다.
   - RGB/BGR 입력을 비교한다.
   - raw candidate의 `x,y,w,h,area,aspect,confidence`를 frame별로 dump한다.
   - FaceONNX baseline box와 SCRFD raw box를 같은 frame에서 IoU로 비교한다.

3. detector별 필터 분리
   - 현재 `AutoMaskGenerator`의 면적/종횡비/skin/edge/luma 필터는 FaceONNX 출력에 맞춰져 있을 가능성이 높다.
   - `FaceCandidateKind`, `SmallFaceConfidenceMin`, `StatsBypassConfidence`, skin/edge/luma 기준을 detector별 옵션으로 분리한다.
   - SCRFD 후보에는 FaceONNX용 skin/luma 필터를 그대로 적용하지 않고, 먼저 raw detector 품질을 확인한 뒤 별도 기준을 만든다.

4. SCRFD track 후처리 분리
   - `FaceTrackPostProcessOptions`의 `StrongConfidence`, `ShortTrackMaxConfidence`, `SmallTrackMaxAreaRatio`, `MinTrackIou`, `MaxCenterShiftRatio`, `MaxAreaChangeRatio`가 SCRFD confidence/box 특성과 맞는지 확인한다.
   - SCRFD 전용 track 옵션 또는 detector별 option profile을 만든다.
   - SCRFD가 같은 사람을 다른 frame 구간으로 밀어 잡는 문제가 bbox decode 문제인지, track matching 문제인지 분리해서 판단한다.

5. 검증 스크립트 보강
   - `scripts/run-srcTest-smoke.ps1`에 SCRFD debug dump 옵션을 추가한다.
   - raw detector 결과와 post-filter 결과를 따로 출력한다.
   - `baselineFrames`, `optimizedFrames`, `onlyBaseline`, `onlyOptimized`뿐 아니라 raw candidate 수, filter reject 사유, detector별 confidence 분포를 기록한다.

6. 재검증 순서
   - `D:\WorkSpace\src\260101_oneday6.mp4`에서 3초 clip으로 먼저 빠르게 반복한다.
   - FaceONNX baseline을 고정한다.
   - SCRFD 500M부터 raw decode를 맞춘다.
   - SCRFD 500M이 baseline frame/box에 근접하면 10G를 같은 방식으로 확인한다.
   - 이후 threshold sweep으로 기본값 후보를 다시 정한다.

완료 기준:

- SCRFD raw box가 같은 frame에서 실제 얼굴 근처에 그려지는지 확인된다.
- SCRFD post-filter 전/후 차이가 문서화된다.
- SCRFD가 실패할 경우 원인이 모델 미탐인지, decode 오류인지, FaceONNX 기준 필터 탈락인지 구분된다.
- SCRFD 500M/10G 각각에 대해 `후보 유지`, `보류`, `폐기` 판단과 근거가 남는다.
- FaceONNX 기본값을 유지할지, SCRFD를 accurate mode 후보로 둘지, 또는 SCRFD 구현을 더 수정할지 다음 결정이 가능해야 한다.

### 2026-05-13 SCRFD adapter/필터 정합 검증

코드 변경:

- `AutoMaskOptions.FilterProfile`을 추가해 FaceONNX와 SCRFD 후보 필터를 분리했다.
- FaceONNX 기본 profile은 기존 면적/종횡비/skin/edge/luma 필터 기준을 유지한다.
- SCRFD profile은 우선 raw detector 품질을 확인하기 위해 skin/edge/luma 통계 필터를 끄고, small 후보 confidence 기준을 `0.25`로 낮췄다.
- `AutoMaskOptions.DumpDetectionDiagnostics`와 `[AutoMaskDetectionDump]` 로그를 추가해 frame별 raw 후보 수와 post-filter 후보 수, top box 좌표/area/aspect/confidence를 확인할 수 있게 했다.
- `ScrfdOnnxDetectorOptions.DumpDebug`, `DebugCandidateLimit`을 추가했다.
- SCRFD debug 로그는 output tensor 이름/shape, score-box pairing, stride, feature map 크기, anchors per point, raw-after-threshold 후보와 NMS 후 top box를 출력한다.
- `scripts/run-srcTest-smoke.ps1`에 `-ScrfdDebugDump`, `-ScrfdNoStrideScale`을 추가했다. 기존 `-ScrfdUseBgr`, `-ScrfdStretchInput`과 함께 RGB/BGR, letterbox/stretch, bbox stride scale true/false를 비교할 수 있다.
- smoke harness에서 SCRFD 실행 시 `FaceTrackPostProcessOptions`를 별도 profile로 낮춰 `StrongConfidence=0.55`, `ShortTrackMaxConfidence=0.55`, `MinTrackIou=0.08`, `MaxCenterShiftRatio=0.75`, `MaxAreaChangeRatio=4.0`을 적용했다.
- 기존 FaceONNX baseline 실행에는 FaceONNX detector, FaceONNX filter profile, 기존 track 옵션을 유지했다.

검증 clip:

- 원본: `D:\WorkSpace\src\260101_oneday6.mp4`
- clip: `.tmp/srcTest-smoke/oneday6-0030-3s.mp4`
- 공통 조건: 원본 해상도, `DetectEveryNFrames=1`, tracking on, `DetectionThreshold=0.2`, `ConfidenceThreshold=0.25`, `NmsThreshold=0.7`, `ParallelDetectorCount=2`, export 생략
- YuNet은 이번 라운드에서도 실행하지 않았다.

FaceONNX baseline:

- `faceMaskFrames=19`
- `[AutoRunSummary]`: `detector=FaceOnnxDetector/CPU`, `processed=90`, `detects=90`, `totalMs=30825~32445`
- filter: `regular=5`, `small=5`, `rejected=0`, `statsRejected=0`
- track 보정: `tracks=6`, `filled=8`, `lostFilled=3`, `lostFrames=44,45,46`, `removedShort=2`, `rewritten=19`
- baseline 얼굴 frame은 `0,14~20,33~37,41~46` 구간이었다.

SCRFD 500M RGB/letterbox/stride-scale on:

- output: `score_8[1x12800x1]`, `score_16[1x3200x1]`, `score_32[1x800x1]`, `bbox_8[1x12800x4]`, `bbox_16[1x3200x4]`, `bbox_32[1x800x4]`
- pairing/stride: score/bbox count 기준 pairing이 `8/16/32` stride와 `80x80/40x40/20x20`, `anchorsPerPoint=2`로 맞았다.
- `[AutoRunSummary]`: `totalMs=4554`, filter `regular=13`, `small=3`, `rejected=1`, `statsRejected=0`
- track 보정: `tracks=9`, `filled=14`, `lostFilled=0`, `removedShort=6`, `rewritten=24`
- `faceMaskFrames=24`
- baseline 비교: `baselineFrames=19`, `optimizedFrames=24`, `common=8`, `onlyBaseline=11`, `onlyOptimized=16`, `avgBestIou=0.000`, `minBestIou=0.000`
- raw 후보는 frame 3/4, 11~19, 22~34, 50 등에서 나오지만 baseline 얼굴 좌표와 겹치지 않았다.
- 판정: output pairing과 bbox stride decode 자체는 구조상 맞아 보인다. 실패 원인은 FaceONNX용 skin/luma 필터 탈락이 아니라, raw 후보가 baseline 얼굴과 다른 위치/구간에 나오는 detector/전처리 품질 문제다.

SCRFD 500M `MultiplyBboxByStride=false`:

- `[AutoRunSummary]`: `totalMs=5809`, filter `regular=0`, `small=0`, `rejected=24`, `statsRejected=0`
- `faceMaskFrames=0`
- raw top box들이 `areaRatio=0.000003~0.0001` 수준으로 지나치게 작아졌고 post-filter에서 전부 제거됐다.
- 판정: 이 모델은 bbox distance에 stride scale을 곱해야 한다. `MultiplyBboxByStride=false`는 폐기한다.

SCRFD 500M BGR/letterbox/stride-scale on:

- `[AutoRunSummary]`: `totalMs=5230`, filter `regular=13`, `small=2`, `rejected=4`, `statsRejected=0`
- track 보정: `tracks=11`, `filled=13`, `lostFilled=0`, `removedShort=8`, `rewritten=20`
- `faceMaskFrames=20`
- 일부 후보 수는 baseline과 비슷하지만 frame 3/4, 44~54, 59~65 등 baseline과 다른 구간/좌표가 중심이었다.
- 판정: BGR 전환으로 baseline 정합이 회복되지 않았다.

SCRFD 500M RGB/stretch/stride-scale on:

- `[AutoRunSummary]`: `totalMs=5362`, filter `regular=64`, `small=15`, `rejected=6`, `statsRejected=0`
- track 보정: `tracks=15`, `filled=25`, `lostFilled=1`, `removedShort=8`, `rewritten=51`
- `faceMaskFrames=51`
- stretch는 후보를 크게 늘렸지만 다수의 optimized-only 후보가 생겨 오탐 위험이 커졌다.
- 판정: stretch 입력은 이 clip에서 품질을 회복하지 못하고 오탐을 늘렸다.

SCRFD 10G RGB/letterbox/stride-scale on:

- output: `448[12800x1]`, `471[3200x1]`, `494[800x1]`, `451[12800x4]`, `474[3200x4]`, `497[800x4]`, keypoint `454/477/500`은 last dimension 10이라 box pairing에서 제외됐다.
- pairing/stride: score/bbox pairing은 `8/16/32` stride와 `anchorsPerPoint=2`로 맞았다.
- `[AutoRunSummary]`: `totalMs=5234`, filter `regular=13`, `small=15`, `rejected=3`, `statsRejected=0`
- track 보정: `tracks=9`, `filled=19`, `lostFilled=0`, `removedShort=5`, `rewritten=42`
- `faceMaskFrames=42`
- baseline 비교: `baselineFrames=19`, `optimizedFrames=42`, `common=10`, `onlyBaseline=9`, `onlyOptimized=32`, `avgBestIou=0.000`, `minBestIou=0.000`
- raw/post-filter 후보가 frame 1~7, 14~17, 30~41, 50~63, 75~79 등 baseline과 다른 구간에 집중됐다.
- 판정: 10G도 output decode 구조는 맞아 보이지만, baseline 얼굴과 좌표가 맞지 않는다. 현재 adapter/전처리 조합에서는 raw detector 후보 품질 문제가 주된 실패 원인이다.

이번 라운드 결론:

- FaceONNX baseline은 유지한다.
- SCRFD 500M/10G 모두 FaceONNX skin/luma 필터 때문에만 실패한 것은 아니다. SCRFD profile로 stats 필터를 우회해도 baseline과 IoU가 0이고 optimized-only 후보가 많다.
- `PairScoreAndBoxTensors()`는 이번 두 모델의 output shape 기준으로는 score/bbox/keypoint를 올바르게 분리했다.
- `GuessStride()`와 anchors per point 계산도 500M/10G 모두 `8/16/32`, `2 anchors`로 맞았다.
- `MultiplyBboxByStride=false`는 box가 지나치게 작아지는 decode 오류 경로로 확인했다.
- RGB/BGR, letterbox/stretch 비교에서 baseline 정합을 회복한 조합은 없었다.
- track 옵션을 SCRFD 전용으로 완화해도 raw 후보 구간/좌표 자체가 다르기 때문에 품질 실패를 해결하지 못했다.

후보 판단:

- SCRFD 500M: `보류`. 빠르고 adapter 계측은 정상화됐지만, 현재 모델/전처리 조합에서는 raw 후보가 baseline 얼굴과 맞지 않고 FaceONNX-only/SCRFD-only 차이가 크다. 다른 SCRFD variant, 입력 정규화/letterbox 구현, 모델 출처별 preprocessing을 추가 확인하기 전까지 기본/accurate 후보로 올리지 않는다.
- SCRFD 10G: `폐기`. 500M보다 큰 모델인데도 같은 clip에서 baseline 정합이 회복되지 않고 optimized-only 후보가 더 많다. 현재 CPU/DirectML 실행에서도 500M 대비 후보 품질 이점이 확인되지 않았다.
- 기본 detector: `FaceONNX 유지`. 이 clip 기준 FaceONNX는 baseline/postprocess가 일관되고, SCRFD는 raw 후보 단계에서 실패한다.

### 2026-05-13 SCRFD preprocessing/decode 추가 검증

목표는 SCRFD 최종 폐기 자체가 아니라 FaceONNX 기준 후처리/필터에 억지로 들어가던 구조를 계속 분리하면서 실패 원인을 더 좁히는 것이다. 이번 추가 라운드에서도 YuNet은 실행하지 않았다.

코드 변경:

- `ScrfdOnnxDetectorOptions`에 `AnchorCenterOffset`, `CenterLetterboxPadding`, `LetterboxPaddingValue`, `InputMean`, `InputStd`, `InputWidth`, `InputHeight` 기반 입력 크기 override를 추가했다.
- smoke script에 `-ScrfdHalfStrideAnchor`, `-ScrfdCenterLetterbox`, `-ScrfdInputSize`, `-ScrfdInputMean`, `-ScrfdInputStd`, `-ScrfdPaddingValue`를 추가했다.
- letterbox padding 영역은 더 이상 tensor 기본값 `0`으로 방치하지 않고 `(paddingValue - mean) / std`로 채운다. 기본값은 InsightFace 계열 전처리에 맞춰 `paddingValue=0`, `mean=127.5`, `std=128`이다.
- bbox anchor center는 기존 `(x + 0.5) * stride`와 InsightFace식 `x * stride`를 비교할 수 있게 분리했다. 기본값은 `AnchorCenterOffset=0.0`이다.
- 10G처럼 입력 shape가 동적인 모델은 `-ScrfdInputSize`로 입력 크기를 바꿔 시도할 수 있게 했다. 500M은 model metadata가 `1x3x640x640` 고정이라 640 외 입력은 사용하지 않는다.

검증 clip/공통 조건:

- clip: `.tmp/srcTest-smoke/oneday6-0030-3s.mp4` (`D:\WorkSpace\src\260101_oneday6.mp4`에서 만든 3초 clip)
- 공통 조건: 원본 해상도, `DetectEveryNFrames=1`, tracking on, `DetectionThreshold=0.2`, `ConfidenceThreshold=0.25`, `NmsThreshold=0.7`, `ParallelDetectorCount=2`, export 생략
- FaceONNX baseline은 다시 `faceMaskFrames=19`, `filter regular=5/small=5/rejected=0/statsRejected=0`, track 후 `rewritten=19`로 확인됐다.

SCRFD 500M 추가 검증:

- RGB/letterbox/top-left padding/normalized padding/anchor offset `0.0`: `faceMaskFrames=15`, filter `regular=13`, `small=2`, `rejected=3`, track 후 `rewritten=15`. FaceONNX baseline 대비 `baselineFrames=19`, `optimizedFrames=15`, `common=2`, `onlyBaseline=17`, `onlyOptimized=13`, `avgBestIou=0.000`, `minBestIou=0.000`.
- 같은 조건에서 기존 half-stride anchor offset `0.5`: `faceMaskFrames=15`, filter `regular=12`, `small=2`, `rejected=4`, track 후 `rewritten=15`. anchor 기준을 되돌려도 baseline 정합은 회복되지 않았다.
- center letterbox padding: `faceMaskFrames=47`, filter `regular=31`, `small=13`, `rejected=2`, track 후 `rewritten=47`. 후보가 크게 늘어 오탐 위험이 커졌고 baseline 정합 개선 신호는 없었다.
- mean/std raw 입력(`-ScrfdInputMean 0 -ScrfdInputStd 1`): `faceMaskFrames=46`, filter `regular=50`, `small=5`, `rejected=0`, track 후 `rewritten=46`. raw 후보가 크게 늘어 오탐 위험이 커졌고 baseline 정합 개선 신호는 없었다.
- 결론: 500M은 output pairing/stride/anchor count뿐 아니라 padding 값, padding 위치, anchor center, mean/std를 분리해도 raw 후보가 실제 baseline 얼굴 근처로 안정적으로 이동하지 않았다. 다만 속도와 adapter 구조 자체는 유지 가치가 있어 `보류`로 둔다.

SCRFD 10G 추가 검증:

- RGB/letterbox/top-left padding/normalized padding/anchor offset `0.0`: output은 `448/471/494` score, `451/474/497` bbox, `454/477/500` keypoint로 분리됐고 score/bbox는 stride `8/16/32`, anchors per point `2`로 정합됐다. `faceMaskFrames=47`, filter `regular=13`, `small=15`, `rejected=2`, track 후 `rewritten=47`.
- FaceONNX baseline 대비 `baselineFrames=19`, `optimizedFrames=47`, `common=10`, `onlyBaseline=9`, `onlyOptimized=37`, `avgBestIou=0.000`, `minBestIou=0.000`.
- center letterbox padding: `faceMaskFrames=36`, filter `regular=16`, `small=10`, `rejected=2`, track 후 `rewritten=36`. 후보 수는 줄었지만 baseline 정합을 회복했다는 신호는 없었다.
- `-ScrfdInputSize 320`: 10G model metadata는 동적 입력처럼 보이나 DirectML 실행에서 `Reshape_223` 오류가 발생한 뒤 진행이 멈췄다. 따라서 현재 DML 실행 경로에서는 640 외 입력 크기 검증은 실패 경로로 기록한다.
- 결론: 10G는 500M보다 큰 모델이지만 같은 clip에서 raw/post-filter 후보가 baseline 얼굴과 맞지 않고 optimized-only가 많다. 입력 크기 변경도 현재 실행 경로에서 안정적으로 검증되지 않았다. 따라서 10G는 `폐기` 판단을 유지한다.

현재 판단:

- `FaceONNX`: 기본 detector 유지. 이 clip에서 baseline frame/postprocess가 안정적이다.
- `SCRFD 500M`: `보류`. 전처리와 bbox decode 축을 더 분리해도 정합이 회복되지 않았지만, 모델이 빠르고 adapter 실험 기반은 남길 가치가 있다. 다른 SCRFD variant나 모델 출처별 전처리 근거가 추가될 때만 재검토한다.
- `SCRFD 10G`: `폐기`. 500M 대비 품질 이점이 없고 optimized-only 후보가 많으며, 동적 input size 변경도 현재 DML 경로에서 실패했다.

## 2026-05-22 YOLO backend 분리 구현 목표

작업 브랜치: `feature/yolo-auto-mosaic-backend`

이번 라운드의 목표는 FaceONNX를 제거하거나 기존 최적화값을 덮어쓰는 것이 아니다. 현재 검증된 FaceONNX 품질/속도 설정은 그대로 보존하고, YOLO 계열 detector를 별도 backend/profile로 추가해 같은 자동 모자이크 파이프라인에서 선택 실행할 수 있게 만든다.

목표 문장:

```text
AUTO_MOSAIC_QUALITY_SPEED_PLAN.md:1 내용을 기준으로 FaceShield 자동 모자이크 파이프라인에 YOLO 기반 detector backend를 추가하고, 기존 FaceONNX 최적화 설정값과 동작은 그대로 유지한 채 FaceONNX와 YOLO를 모델별로 선택해 사용할 수 있도록 구현한다. FaceONNX용 threshold/filter/track/ROI/auto-tune 설정은 기존 검증값을 훼손하지 않고 보존하며, YOLOv8-Face 또는 YOLO5Face ONNX 후보에는 YOLO 전용 threshold, NMS, 후보 필터, small-face 처리, track 후처리, ROI 재검출, auto-tune/profile 설정을 별도로 분리해 최적화한다. 사용자는 FaceONNX와 YOLO 모델을 선택해 자동 모자이크를 실행할 수 있어야 하며, 각 모델은 서로 다른 최적화 값을 독립적으로 가져야 한다. srcTest 대표 구간에서 FaceONNX baseline과 YOLO 후보를 A/B 비교하고, YOLO가 실제 미탐/오탐/깜박임/박스 튐 품질을 유지하거나 개선하면서 자동 검출 totalMs 또는 export totalMs를 줄이는지 검증한다. 품질 gate를 통과한 YOLO 설정만 추천 후보로 문서화하고, 실패한 YOLO 설정은 원인과 보류/폐기 판단을 AUTO_MOSAIC_QUALITY_SPEED_PLAN.md에 기록한다. 이 목표에는 branch 생성, commit, push, pull, reset, stash 같은 git 작업은 포함하지 않는다. git 관련 작업은 별도 사용자 지시가 있을 때만 수행한다. 그 외 목표 범위 안의 코드 구현, 문서 수정, 로컬 빌드, smoke 실행, 모델 후보 다운로드/검증, A/B 테스트, 디버그 로그 추가, threshold/profile 튜닝은 사용자에게 매번 확인하지 않고 자율적으로 진행한다.
```

핵심 조건:

- 이 목표 범위에서 git 작업은 제외한다. branch 생성, commit, push, pull, reset, stash 등은 별도 지시가 있을 때만 수행한다.
- git을 제외한 구현/수정/로컬 검증/모델 후보 실험은 매 단계 승인 요청 없이 자율적으로 진행한다.
- FaceONNX 기존 기본값과 검증값은 변경하지 않는다.
- FaceONNX와 YOLO는 같은 설정 객체를 공유하지 않고 detector별 profile을 가진다.
- 사용자는 FaceONNX와 YOLO 모델을 선택해서 자동 모자이크를 실행할 수 있어야 한다.
- YOLO threshold, NMS, 후보 필터, small-face 기준, track 후처리, ROI 재검출, auto-tune 후보는 YOLO 전용 값으로 분리한다.
- YOLO가 FaceONNX보다 빠르더라도 실제 미탐, 실제 오탐, 깜박임, 박스 튐이 늘면 기본값으로 승격하지 않는다.
- YOLO가 품질 gate를 통과하지 못하면 실패 원인을 모델, decode, 전처리, post-filter, track/ROI 중 어디인지 분리해서 기록한다.

우선 검토 후보:

1. `YOLOv8-Face`
   - `nano`는 속도 후보로 본다.
   - `medium`은 품질 후보로 본다.
   - 단점: 공개 구현/weight의 라이선스가 GPL/Ultralytics 계열일 수 있으므로 배포 전 확인이 필요하다.

2. `YOLO5Face`
   - 작은 얼굴과 어려운 각도 대응 후보로 본다.
   - `n/s` 계열부터 ONNX runtime adapter를 붙여 속도와 품질을 확인한다.
   - 단점: 모델 파일 출처와 라이선스 확인이 필요하다.

초기 구현 방향:

- `FaceDetectorBackend`에 YOLO 계열 backend를 추가한다.
- `YoloFaceOnnxDetectorOptions`와 `YoloFaceOnnxDetector`를 새로 추가한다.
- YOLO output decode는 모델별 output shape를 먼저 inspect하고, anchor-free/anchor-based 구조를 명확히 분리한다.
- `FaceDetectorFactoryOptions`는 FaceONNX, SCRFD, YuNet, YOLO option을 독립적으로 가진다.
- `AutoMaskOptions.FilterProfile`에 YOLO profile을 추가하고 FaceONNX/SCRFD/YuNet과 분리한다.
- smoke script에 `-YoloModelPath`, `-YoloModelType`, `-YoloInputSize`, `-YoloConfidenceThreshold`, `-YoloNmsThreshold`, `-YoloDebugDump` 옵션을 추가한다.
- A/B 비교는 FaceONNX baseline을 기준으로 `faceMaskFrames`, `onlyBaseline`, `onlyOptimized`, `avgBestIou`, `minBestIou`, track/ROI 로그를 같이 본다.

검증 기준:

- 기본 FaceONNX gate는 기존과 동일하게 통과해야 한다.
- YOLO 후보는 같은 clip에서 FaceONNX baseline 대비 frame 누락/추가, 박스 수 차이, 낮은 IoU가 작아야 한다.
- `onlyBaseline`, `onlyOptimized`, `boxCountDiff`, `lowIou`는 실제 오탐/미탐 판정이 아니라 baseline-diff 신호로 기록한다.
- 실제 미탐/오탐은 representative overlay 육안 확인 또는 GT 라벨 기준으로만 확정한다.
- YOLO의 자동 검출 wall-clock은 `[AutoRunSummary].totalMs`로 판단한다. 병렬 thread 누적값인 `detectMs`만으로 빠르다고 판단하지 않는다.
- 대표 clip gate를 통과한 YOLO 후보만 30초 이상 구간과 export smoke로 확장한다.
- 전체 목표 완료 전에는 Avalonia GUI에서 detector 선택, 자동 검출, preview, export 흐름도 확인해야 한다.

## 2026-05-22 YOLO backend 1차 구현 및 YOLOv8n 후보 smoke

구현 상태:

- `FaceDetectorBackend.YoloFaceOnnx`를 추가했다.
- `YoloFaceOnnxDetectorOptions`, `YoloFaceModelType`, `YoloFaceOnnxDetector`를 추가했다.
- `YoloFaceOnnxDetector`는 ONNX Runtime으로 YOLOv8-Face/YOLO5Face 계열 후보를 실행하고, `[1,N,F]` 또는 `[1,F,N]` 형태의 YOLO 후보 텐서를 해석한다. YOLO5Face의 raw 3-scale feature map `[1,48,H,W]` 출력은 별도 decode 경로에서 처리한다.
- `FaceDetectorFactoryOptions`는 FaceONNX/SCRFD/YuNet/YOLO option을 별도 프로퍼티로 가진다.
- `AutoMaskOptions.FilterProfile`에 `Yolo`를 추가했고, YOLO 후보 필터는 FaceONNX와 분리했다.
- `WorkspaceViewModel`의 자동 실행 경로는 FaceONNX backend에서만 기존 `DetectorAutoTuner`를 적용하고, YOLO backend에서는 YOLO factory/options를 유지한다.
- 홈 자동 옵션 UI에서 `FaceONNX`와 `YOLO Face ONNX`를 선택할 수 있게 했다. YOLO 선택 시 YOLO 모델 종류, `.onnx` 경로, 입력 크기, objectness/confidence/NMS 값을 별도 입력한다.
- `scripts/run-srcTest-smoke.ps1`에 `-YoloModelPath`, `-YoloModelType`, `-YoloInputSize`, `-YoloObjectnessThreshold`, `-YoloConfidenceThreshold`, `-YoloNmsThreshold`, `-YoloDebugDump` 옵션을 추가했다. 이후 진단용으로 `-DumpCompareDetails`, `-YoloUseFaceOnnxRoiRefine`, `-YoloFaceOnnxRoiMinAreaRatio`, `-YoloFaceOnnxRoiMaxCandidates`도 추가했다.

FaceONNX 회귀 검증:

- `dotnet build FaceShield.sln` 성공.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify-auto-mosaic-default.ps1` 기본 실행 성공.
- 기본 verifier의 quality gate는 `baselineFrames=19`, `optimizedFrames=19`, `onlyBaseline=0`, `onlyOptimized=0`, `avgBestIou=1.000`, `minBestIou=1.000`, `passed=True`였다.
- 같은 verifier에서 ROI-hit 대표 구간은 `attempts=11`, `hits=5`를 유지했다.
- auto-tune short gate는 `FaceOnnxDetector/CPU`, `pipe-parallel`, `processed=150`, `detects=150`, `interpolated=0`, `totalMs=53,703ms`로 통과했다. 현재 장비/부하에서는 튜너가 CPU 2세션을 선택했다.

YOLO 후보:

- 다운로드 후보: `lindevs/yolov8-face` release `1.0.1`의 `yolov8n-face-lindevs.onnx`.
- 로컬 경로: `.tmp/models/yolov8n-face-lindevs.onnx`.
- SHA-256: `8d0bfb0c3383c5bd7a78dd24ef79a21e2aa456619b6ab5e53867092d1c7dc414`.
- 모델 metadata: input `images[1x3x640x640]`, output `output0[1x5x8400]`.
- 라이선스/배포 적합성은 아직 확정하지 않았다. 후보 실험용 로컬 파일로만 취급한다.

YOLOv8n 기본 threshold smoke:

- 명령:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/run-srcTest-smoke.ps1 -SkipTrim -Source .tmp/srcTest-smoke/smoke-0600-3s.mp4 -SkipExport -OptimizedCpuOnly -YoloModelPath .tmp/models/yolov8n-face-lindevs.onnx -YoloModelType YoloV8Face -YoloInputSize 640 -YoloObjectnessThreshold 0.25 -YoloConfidenceThreshold 0.35 -YoloNmsThreshold 0.45 -YoloDebugDump
```

- adapter 실행은 성공했다.
- YOLO optimized `totalMs=14,655ms`로 FaceONNX baseline `totalMs=62,090ms`보다 빨랐다.
- 하지만 YOLO는 후처리 후 `faceMaskFrames=0`이었다.
- A/B 결과는 `baselineFrames=19`, `optimizedFrames=0`, `onlyBaseline=19`, `onlyOptimized=0`, `avgBestIou=0.000`, `minBestIou=0.000`, `passed=False`.
- 판단: `폐기`. 이 threshold는 알려진 얼굴 구간에서 최종 마스크가 0프레임이라 quality gate를 통과하지 못한다.

YOLOv8n low-threshold smoke:

- 명령:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/run-srcTest-smoke.ps1 -SkipTrim -Source .tmp/srcTest-smoke/smoke-0600-3s.mp4 -SkipExport -OptimizedCpuOnly -YoloModelPath .tmp/models/yolov8n-face-lindevs.onnx -YoloModelType YoloV8Face -YoloInputSize 640 -YoloObjectnessThreshold 0.05 -YoloConfidenceThreshold 0.05 -YoloNmsThreshold 0.45
```

- YOLO optimized `totalMs=13,033ms`로 FaceONNX baseline `totalMs=59,679ms`보다 빨랐다.
- 후보는 생겼지만 후처리 후 `faceMaskFrames=8`에 그쳤다.
- A/B 결과는 `baselineFrames=19`, `optimizedFrames=8`, `common=8`, `onlyBaseline=11`, `onlyOptimized=0`, `avgBestIou=0.603`, `minBestIou=0.048`, `boxCountDiffFrames=7`, `passed=False`.
- 판단: `보류`. 속도는 유의미하게 빠르지만 미탐과 박스 정합 실패가 커서 추천 후보가 아니다. 실패 축은 모델 후보의 작은 얼굴/장면 적합성, 640 고정 입력 해상도, low-threshold 후보의 위치 정합, YOLO 전용 후처리 모두에 걸쳐 있다. 현재 증거만으로는 decode 자체 실패라고 단정하지 않는다.

현재 추천 후보:

- 없음.

다음 후보:

- 같은 adapter로 다른 YOLOv8-Face 후보를 비교한다. 우선순위는 더 큰 입력 또는 더 높은 mAP 후보지만, 현재 모델은 metadata가 `1x3x640x640` 고정이라 단순 `-YoloInputSize 1280` 실험은 먼저 모델 동적 입력 여부를 확인한 뒤 진행한다.
- YOLO5Face ONNX 후보를 확보해 `Yolo5Face` decode 경로를 검증한다.
- YOLO가 3초 gate를 통과하기 전에는 30초/export smoke로 승격하지 않는다.

## 2026-05-22 YOLO tiling 실험

구현 상태:

- `YoloFaceOnnxDetectorOptions`에 `UseTiling`, `IncludeFullFrameWhenTiling`, `TileColumns`, `TileRows`, `TileOverlapRatio`를 추가했다.
- `YoloFaceOnnxDetector`는 전체 프레임 단일 추론 외에 source frame을 겹치는 tile region으로 나눠 YOLO를 실행할 수 있다.
- 홈 자동 옵션 UI에 YOLO 전용 타일 검출, 타일만 실행, 타일 행/열, 타일 겹침 값을 추가했다.
- `scripts/run-srcTest-smoke.ps1`에 `-YoloUseTiling`, `-YoloTileOnly`, `-YoloTileColumns`, `-YoloTileRows`, `-YoloTileOverlapRatio`를 추가했다.

실험 1: YOLOv8n low-threshold + 2x2 tile-only

- 명령:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/run-srcTest-smoke.ps1 -SkipTrim -Source .tmp/srcTest-smoke/smoke-0600-3s.mp4 -SkipExport -OptimizedCpuOnly -YoloModelPath .tmp/models/yolov8n-face-lindevs.onnx -YoloModelType YoloV8Face -YoloInputSize 640 -YoloObjectnessThreshold 0.05 -YoloConfidenceThreshold 0.05 -YoloNmsThreshold 0.45 -YoloUseTiling -YoloTileOnly -YoloTileColumns 2 -YoloTileRows 2 -YoloTileOverlapRatio 0.15
```

- FaceONNX baseline: `baselineFrames=19`, `totalMs=58,923ms`.
- YOLO tile-only: `optimizedFrames=16`, `totalMs=46,520ms`.
- A/B 결과: `common=9`, `onlyBaseline=10`, `onlyOptimized=7`, `avgBestIou=0.741`, `minBestIou=0.572`, `boxCountDiffFrames=8`, `passed=False`.
- 판단: `보류`. tiling은 FaceONNX-only frame을 일부 줄였지만 YOLO-only/불일치 frame이 생겼고 IoU gate를 통과하지 못했다. 속도도 non-tiled YOLO보다 크게 느려졌다.

실험 2: YOLOv8n low-threshold + full-frame + 2x2 tile

- 명령:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/run-srcTest-smoke.ps1 -SkipTrim -Source .tmp/srcTest-smoke/smoke-0600-3s.mp4 -SkipBaseline -SkipExport -OptimizedCpuOnly -YoloModelPath .tmp/models/yolov8n-face-lindevs.onnx -YoloModelType YoloV8Face -YoloInputSize 640 -YoloObjectnessThreshold 0.05 -YoloConfidenceThreshold 0.05 -YoloNmsThreshold 0.45 -YoloUseTiling -YoloTileColumns 2 -YoloTileRows 2 -YoloTileOverlapRatio 0.15
```

- YOLO full+tile: `faceMaskFrames=23`, `totalMs=61,397ms`, ROI refine `attempts=8`, `hits=1`.
- 이 실행은 빠른 진단용으로 baseline compare를 생략했다.
- 판단: `보류`. frame 수는 baseline 19보다 많아졌지만, 직전 tile-only A/B에서 이미 `onlyOptimized`가 발생했고 full+tile은 `totalMs`가 FaceONNX baseline 수준까지 올라왔다. 따라서 추천 후보로 승격하지 않는다.

현재 YOLOv8n 정리:

- non-tiled low-threshold: 빠르지만 `optimizedFrames=8`, IoU 실패.
- tile-only low-threshold: 미탐은 줄었지만 `onlyBaseline=10`, `onlyOptimized=7`, IoU 실패.
- full+tile low-threshold: 후보 수는 늘었지만 속도 이점이 거의 사라지고 오탐 가능성이 커졌다.
- 따라서 `yolov8n-face-lindevs.onnx`는 현재 `보류` 유지. 추천 후보 없음.

9분 2초 추가 gate:

- `YoloObjectnessThreshold=0.05`, `YoloConfidenceThreshold=0.05`, `YoloNmsThreshold=0.45` low-threshold 조합을 `.tmp/srcTest-smoke/smoke-0900-2s.mp4`에서 재검증했다.
- FaceONNX baseline: `baselineFrames=55`, `totalMs=24,280ms`.
- YOLOv8n optimized: `optimizedFrames=62`, `totalMs=12,401ms`.
- A/B 결과: `common=55`, `onlyBaseline=0`, `onlyOptimized=7`, `avgBestIou=0.731`, `minBestIou=0.488`, `boxCountDiffFrames=39`, `passed=False`.
- 추가 frame은 `0,1,2,3,4,8,9`였고, low-threshold 특성상 화면 상단 작은 얼굴 후보 외에 하단/중단 물체성 후보도 같이 들어왔다.
- `YoloObjectnessThreshold=0.20`, `YoloConfidenceThreshold=0.20`, `YoloNmsThreshold=0.45` 중간 threshold도 확인했다.
- FaceONNX baseline: `baselineFrames=55`, `totalMs=24,099ms`.
- YOLOv8n optimized: `optimizedFrames=57`, `totalMs=11,254ms`.
- A/B 결과: `common=53`, `onlyBaseline=2`, `onlyOptimized=4`, `avgBestIou=0.720`, `minBestIou=0.000`, `boxCountDiffFrames=27`, `passed=False`.
- 판단: 9분 구간에서 YOLOv8n은 threshold를 낮추면 YOLO-only 후보가 많고, threshold를 올리면 일부 FaceONNX-only frame이 생긴다. 큰 얼굴 박스 정합도 FaceONNX 기준 gate를 넘지 못한다. 실제 오탐/미탐 여부는 overlay 또는 GT 확인이 필요하지만, 현재 baseline-diff gate 기준으로는 추천 후보가 아니다.

FaceONNX 회귀 재검증:

- YOLO tiling 구현 후 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify-auto-mosaic-default.ps1` 기본 실행을 다시 통과했다.
- quality gate는 `baselineFrames=19`, `optimizedFrames=19`, `onlyBaseline=0`, `onlyOptimized=0`, `avgBestIou=1.000`, `minBestIou=1.000`, `passed=True`였다.
- ROI-hit 대표 구간은 `attempts=11`, `hits=5`였다.
- auto-tune short gate는 `FaceOnnxDetector/CPU`, `pipe-parallel`, `processed=150`, `detects=150`, `interpolated=0`, `totalMs=50,439ms`로 통과했다.

## 2026-05-22 YOLO5Face feature-map decode 및 smoke

구현 상태:

- `YoloFaceOnnxDetector`에 YOLO5Face 전용 raw feature-map decode를 추가했다.
- 적용 대상 output shape는 `pred0[1x48x80x80]`, `pred1[1x48x40x40]`, `pred2[1x48x20x20]` 같은 3-scale NCHW tensor다.
- anchors는 `deepcam-cn/yolov5-face`의 `models/yolov5s.yaml` 설정을 기준으로 했다: P3/8 `[4,5, 8,10, 13,16]`, P4/16 `[23,29, 43,55, 73,105]`, P5/32 `[146,217, 231,300, 335,433]`.
- decode는 YOLO5Face `Detect.forward`의 inference 공식을 따른다. `xy=(sigmoid(xy)*2-0.5+grid)*stride`, `wh=(sigmoid(wh)*2)^2*anchor`, `score=sigmoid(objectness)*sigmoid(class)`로 후보를 만든 뒤 letterbox padding/scale을 되돌린다.
- 기존 generic `[1,F,N]` 경로가 `[1,48,H,W]`를 잘못 후보 tensor로 해석하던 문제를 피하기 위해 YOLO5Face 4D feature-map 경로를 먼저 검사한다.

YOLO5Face 후보:

- 다운로드 후보: Hugging Face `hayashiLin/deepfacelivemodels`의 `YoloV5Face.onnx`.
- 로컬 경로: `.tmp/models/YoloV5Face.onnx`.
- SHA-256: `907c295f79eba1b0f4be59bcf5d8aabe4e2a9002ec44c5d1c518b97eb9fb13da`.
- Hugging Face 페이지 기준 license는 `GPL-3.0`으로 표시되어 있다. 현재는 로컬 실험 후보로만 취급하고 배포 후보로 추천하지 않는다.

실험 1: YOLO5Face 기본 threshold

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/run-srcTest-smoke.ps1 -SkipTrim -Source .tmp/srcTest-smoke/smoke-0600-3s.mp4 -SkipExport -OptimizedCpuOnly -YoloModelPath .tmp/models/YoloV5Face.onnx -YoloModelType Yolo5Face -YoloInputSize 640 -YoloObjectnessThreshold 0.25 -YoloConfidenceThreshold 0.25 -YoloNmsThreshold 0.45
```

- FaceONNX baseline: `baselineFrames=19`, `totalMs=58,029ms`.
- YOLO5Face optimized: `optimizedFrames=9`, `totalMs=16,116ms`.
- A/B 결과: `common=9`, `onlyBaseline=10`, `onlyOptimized=0`, `avgBestIou=0.973`, `minBestIou=0.953`, `boxCountDiffFrames=0`, `passed=False`.
- 판단: `보류`. decode와 박스 정합은 정상에 가깝지만 recall이 부족해 품질 gate를 통과하지 못한다.

실험 2: YOLO5Face low threshold

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/run-srcTest-smoke.ps1 -SkipTrim -Source .tmp/srcTest-smoke/smoke-0600-3s.mp4 -SkipExport -OptimizedCpuOnly -YoloModelPath .tmp/models/YoloV5Face.onnx -YoloModelType Yolo5Face -YoloInputSize 640 -YoloObjectnessThreshold 0.10 -YoloConfidenceThreshold 0.10 -YoloNmsThreshold 0.45
```

- FaceONNX baseline: `baselineFrames=19`, `totalMs=67,228ms`.
- YOLO5Face optimized: `optimizedFrames=23`, `totalMs=22,355ms`.
- A/B 결과: `common=18`, `onlyBaseline=1`, `onlyOptimized=5`, `avgBestIou=0.755`, `minBestIou=0.000`, `boxCountDiffFrames=5`, `passed=False`.
- 판단: `보류`. threshold를 낮추면 FaceONNX-only frame은 줄지만 YOLO-only/프레임 불일치가 생긴다.

실험 3: YOLO5Face 중간 threshold

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/run-srcTest-smoke.ps1 -SkipTrim -Source .tmp/srcTest-smoke/smoke-0600-3s.mp4 -SkipExport -OptimizedCpuOnly -YoloModelPath .tmp/models/YoloV5Face.onnx -YoloModelType Yolo5Face -YoloInputSize 640 -YoloObjectnessThreshold 0.15 -YoloConfidenceThreshold 0.15 -YoloNmsThreshold 0.45
```

- FaceONNX baseline: `baselineFrames=19`, `totalMs=57,546ms`.
- YOLO5Face optimized: `optimizedFrames=20`, `totalMs=18,010ms`.
- A/B 결과: `common=18`, `onlyBaseline=1`, `onlyOptimized=2`, `avgBestIou=0.724`, `minBestIou=0.000`, `boxCountDiffFrames=2`, `passed=False`.
- 판단: `보류`. 기본 threshold보다 recall은 좋아졌지만 gate 기준의 frame set과 IoU를 만족하지 못한다.

실험 4: YOLO5Face 기본 threshold + 2x2 tile-only

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/run-srcTest-smoke.ps1 -SkipTrim -Source .tmp/srcTest-smoke/smoke-0600-3s.mp4 -SkipExport -OptimizedCpuOnly -YoloModelPath .tmp/models/YoloV5Face.onnx -YoloModelType Yolo5Face -YoloInputSize 640 -YoloObjectnessThreshold 0.25 -YoloConfidenceThreshold 0.25 -YoloNmsThreshold 0.45 -YoloUseTiling -YoloTileOnly -YoloTileColumns 2 -YoloTileRows 2 -YoloTileOverlapRatio 0.15
```

- FaceONNX baseline: `baselineFrames=19`, `totalMs=53,496ms`.
- YOLO5Face tile-only: `optimizedFrames=39`, `totalMs=56,705ms`.
- A/B 결과: `common=10`, `onlyBaseline=9`, `onlyOptimized=29`, `avgBestIou=0.778`, `minBestIou=0.620`, `boxCountDiffFrames=2`, `passed=False`.
- 판단: `폐기`. tiling은 YOLO-only 후보를 크게 늘리고 속도 이점도 사라진다.

실험 5: YOLO5Face 근접 threshold `objectness=0.12`, `confidence=0.18`

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/run-srcTest-smoke.ps1 -SkipTrim -Source .tmp/srcTest-smoke/smoke-0600-3s.mp4 -SkipExport -OptimizedCpuOnly -YoloModelPath .tmp/models/YoloV5Face.onnx -YoloModelType Yolo5Face -YoloInputSize 640 -YoloObjectnessThreshold 0.12 -YoloConfidenceThreshold 0.18 -YoloNmsThreshold 0.45
```

- FaceONNX baseline: `baselineFrames=19`, `totalMs=76,625ms`.
- YOLO5Face optimized: `optimizedFrames=19`, `totalMs=17,580ms`.
- A/B 결과: `common=18`, `onlyBaseline=1`, `onlyOptimized=1`, `avgBestIou=0.790`, `minBestIou=0.000`, `boxCountDiffFrames=2`, `passed=False`.
- mismatch frame은 `onlyBaseline=86`, `onlyOptimized=9`였다.
- 판단: `보류`. 지금까지의 YOLO5Face 후보 중 frame 수와 속도는 가장 근접했지만, 실제 frame set과 최소 IoU가 gate를 통과하지 못한다. 실패 축은 모델/threshold curve와 YOLO 전용 track 후처리 경계에 가깝다. decode는 기본 threshold에서 높은 IoU를 보였으므로 현재 증거만으로 decode 실패라고 보지는 않는다.

YOLO 전용 track 후처리 1차 보정:

- YOLO profile에서 `MaxLostFillFrames`를 `3`에서 `1`로 줄였다.
- YOLO profile의 `ShortTrackMaxConfidence`를 `0.58`에서 `0.38`로 낮췄다.
- 목적은 YOLO 저신뢰 tail box가 화면 밖 방향으로 길게 extrapolate되는 false positive를 줄이고, 실제 단발 얼굴 후보인 frame 86을 short-track 제거에서 보존하는 것이다.
- FaceONNX profile 값은 변경하지 않았다.

보정 후 같은 0.12/0.18 재검증:

- FaceONNX baseline: `baselineFrames=19`, `totalMs=54,624ms`.
- YOLO5Face optimized: `optimizedFrames=19`, `totalMs=19,930ms`.
- A/B 결과: `common=19`, `onlyBaseline=0`, `onlyOptimized=0`, `avgBestIou=0.798`, `minBestIou=0.000`, `boxCountDiffFrames=1`, `passed=False`.
- bad frame 로그: `boxCountDiff=27`, `lowIou=6,7,8,27`.
- 판단: `보류`. frame set mismatch는 해소됐지만, YOLO의 저신뢰 edge-tail 박스가 frame 6~8에서 FaceONNX baseline보다 위쪽으로 크게 튀고, frame 27의 두 얼굴 중 하나가 postprocess에서 정합되지 않는다. 실패 축은 decode보다는 YOLO 전용 track/post-filter와 false-positive/false-negative 경계에 가깝다.

YOLO 전용 track 후처리 2차 보정:

- `FaceTrackPostProcessOptions`에 저신뢰 tail pruning 옵션을 추가했다.
- YOLO profile에서 `UnstableTailMaxConfidence=0.40`, `UnstableTailMinStableDetections=3`, `UnstableTailMinIou=0.45`, `UnstableTailMaxAreaChangeRatio=1.8`을 사용한다.
- 안정 track 뒤에 붙은 저신뢰 tail detection이 면적/IoU 기준으로 급격히 튀면 해당 tail detection을 제거하고, 이전 안정 track 기준 lost-fill이 적용되게 했다.
- YOLO profile의 `MaxLostFillFrames`는 다시 `3`으로 두고, `ShortTrackMaxConfidence`는 `0.18`로 낮춰 실제 단발 얼굴 후보를 제거하지 않게 했다.
- FaceONNX profile은 tail pruning 비활성 기본값(`UnstableTailMaxConfidence=0`)을 유지한다.

2차 보정 후 같은 0.12/0.18 재검증:

- FaceONNX baseline: `baselineFrames=19`, `totalMs=55,885ms`.
- YOLO5Face optimized: `optimizedFrames=19`, `totalMs=16,149ms`.
- A/B 결과: `common=19`, `onlyBaseline=0`, `onlyOptimized=0`, `avgBestIou=0.971`, `minBestIou=0.944`, `boxCountDiffFrames=0`, `passed=True`.
- bad frame 로그: `boxCountDiff=none`, `lowIou=none`.
- 판단: `3초 gate 통과`. 이 설정은 현재 대표 3초 구간에서 FaceONNX baseline 대비 frame set, 박스 수, IoU gate를 통과했고 자동 검출 wall-clock도 약 3.5배 빨랐다. 단, 이 결과만으로 YOLO 최종 최적화 완료나 전체 영상 기본값 승격으로 보지는 않는다.

30초 확장 smoke:

- 대상: `.tmp/srcTest-smoke/smoke-1200-30s.mp4`
- 명령 조건: `YoloV5Face`, `objectness=0.12`, `confidence=0.18`, `nms=0.45`, `InputSize=640`, `ParallelDetectorCount=2`, `DownscaleRatio=1.0`, `DetectEveryNFrames=1`.
- 자동 검출: `processed=899`, `detects=899`, `interpolated=0`, `faceMaskFrames=774`, `totalMs=157,604ms`.
- track/ROI: `tracks=144`, `filled=338`, `lostFilled=150`, `removedShort=36`, ROI `attempts=32`, `hits=23`.
- 기존 FaceONNX 30초 문서 기록은 `processed=899`, `faceMaskFrames=778`, `totalMs=316,366ms`였으므로, frame 수는 근접하고 자동 검출 wall-clock은 약 2배 빠르다.
- 이 실행은 baseline A/B 비교가 아니라 확장 smoke이므로, 30초 구간의 미탐/오탐/박스 튐이 완전히 없다고 단정하지 않는다.

30초 export smoke:

- 같은 30초 구간에서 export 포함 실행을 완료했다.
- 자동 검출: `processed=899`, `faceMaskFrames=774`, `totalMs=158,063ms`.
- export: `frames=902`, `bitmapMaskFrames=0`, `directFaceFrames=774`, `swsToBgraMs=13,898`, `maskMs=44,094`, `swsToEncMs=22,153`, `encodeMs=4,442`, `totalMs=135,572ms`.
- 기존 FaceONNX 30초 medium export 기록은 자동 검출 `totalMs=316,366ms`, export `totalMs=127,750ms`, `directFaceFrames=778`이었다. YOLO는 자동 검출이 크게 빠르지만 export는 face rect 수가 비슷해 거의 같은 병목 구조를 가진다.
- export output은 `.tmp/srcTest-smoke/smoke-1200-30s_blur.mp4`로 생성됐다.

당시 YOLO5Face 추천 상태:

- `YoloV5Face.onnx`, `objectness=0.12`, `confidence=0.18`, `nms=0.45`, `InputSize=640`, YOLO 전용 unstable-tail pruning 적용 조합은 이 시점에는 `조건부 추천 후보`로 올렸다.
- 조건부인 이유는 대표 3초 gate와 30초/export smoke는 통과했지만, 30초 구간의 baseline A/B 품질 비교와 육안 확인, 다른 시간대 대표 구간, 긴 구간 export 검증은 아직 남아 있기 때문이다.
- 현재 실패 축은 완전 해소가 아니라 1차 대표 구간에서 해소된 상태다. 다음 검증은 다른 대표 3초 구간과 30초 baseline A/B 또는 육안 검토다.
- 홈 자동 옵션의 신규 YOLO 기본 profile도 `YOLO5Face`, `objectness=0.12`, `confidence=0.18`, `nms=0.45`로 맞췄다. 저장된 사용자 설정이 있으면 기존처럼 저장값이 우선 적용된다.

현재 YOLO5Face 정리:

- feature-map decode는 동작한다. 기본 threshold의 `avgBestIou=0.973`, `minBestIou=0.953`가 이를 뒷받침한다.
- 기본 threshold는 빠르고 위치가 맞지만 FaceONNX-only frame이 크다.
- 낮은 threshold와 중간 threshold는 FaceONNX-only frame을 줄이는 대신 YOLO-only frame과 frame mismatch를 만든다.
- 2x2 tile-only는 추천 후보가 아니다.
- `objectness=0.12`, `confidence=0.18`은 3초 gate에서 `faceMaskFrames=19`까지 맞췄지만 `onlyBaseline`/`onlyOptimized`와 IoU 실패가 남았다.
- YOLO 전용 track 후처리 보정 후 frame set mismatch는 사라졌지만, 저신뢰 edge-tail 박스 튐과 frame 27 박스 수 차이가 남아 품질 gate를 통과하지 못했다.
- YOLO 전용 unstable-tail pruning 적용 후 대표 3초 gate는 통과했고, 30초 자동 검출/export smoke도 완료했다.
- 따라서 이 시점의 `YoloV5Face.onnx` `0.12/0.18/0.45` 설정은 조건부 추천 후보로 두었다. 아래 9분 2초 추가 gate 실패에 따라 최종 추천 상태는 다시 보정한다.

FaceONNX 회귀 재검증:

- YOLO5Face feature-map decode 추가 후 `dotnet build FaceShield.sln`은 기존 FFmpeg obsolete warning 7개만 남기고 성공했다.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify-auto-mosaic-default.ps1` 기본 실행을 다시 통과했다.
- quality gate는 `baselineFrames=19`, `optimizedFrames=19`, `onlyBaseline=0`, `onlyOptimized=0`, `avgBestIou=1.000`, `minBestIou=1.000`, `passed=True`였다.
- ROI-hit 대표 구간은 `attempts=11`, `hits=5`였다.
- auto-tune short gate는 `FaceOnnxDetector/CPU`, `pipe-parallel`, `processed=150`, `detects=150`, `interpolated=0`, `totalMs=47,742ms`로 통과했다.

YOLO 전용 후처리 보정 후 회귀 재검증:

- `dotnet build FaceShield.sln` 성공. 기존 FFmpeg obsolete warning 7개만 남았다.
- `git diff --check` 통과.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify-auto-mosaic-default.ps1` 기본 실행 성공.
- quality gate는 `baselineFrames=19`, `optimizedFrames=19`, `onlyBaseline=0`, `onlyOptimized=0`, `avgBestIou=1.000`, `minBestIou=1.000`, `passed=True`였다.
- ROI-hit 대표 구간은 `attempts=11`, `hits=5`였다.
- auto-tune short gate는 `FaceOnnxDetector/CPU`, `pipe-parallel`, `processed=150`, `detects=150`, `interpolated=0`, `totalMs=75,233ms`로 통과했다. 현재 실행에서는 auto-tune이 CPU 2세션을 선택했다.

YOLO unstable-tail pruning 추가 후 회귀 재검증:

- `dotnet build FaceShield.sln` 성공. 기존 FFmpeg obsolete warning 7개만 남았다.
- `git diff --check` 통과.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify-auto-mosaic-default.ps1` 기본 실행 성공.
- quality gate는 `baselineFrames=19`, `optimizedFrames=19`, `onlyBaseline=0`, `onlyOptimized=0`, `avgBestIou=1.000`, `minBestIou=1.000`, `passed=True`였다.
- ROI-hit 대표 구간은 `attempts=11`, `hits=5`였다.
- auto-tune short gate는 `FaceOnnxDetector/CPU`, `pipe-parallel`, `processed=150`, `detects=150`, `interpolated=0`, `totalMs=49,033ms`로 통과했다. 현재 실행에서는 auto-tune이 CPU 2세션/default를 선택했다.

YOLO5Face 기본 profile 연결 후 회귀 재검증:

- `dotnet build FaceShield.sln` 성공. 기존 FFmpeg obsolete warning 7개만 남았다.
- `git diff --check` 통과.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify-auto-mosaic-default.ps1` 기본 실행 성공.
- quality gate는 `baselineFrames=19`, `optimizedFrames=19`, `onlyBaseline=0`, `onlyOptimized=0`, `avgBestIou=1.000`, `minBestIou=1.000`, `passed=True`였다.
- ROI-hit 대표 구간은 `attempts=11`, `hits=5`였다.
- auto-tune short gate는 `FaceOnnxDetector/CPU`, `pipe-parallel`, `processed=150`, `detects=150`, `interpolated=0`, `totalMs=47,649ms`로 통과했다. 현재 실행에서는 auto-tune이 CPU 2세션/default를 선택했다.

다른 대표 구간 추가 gate: 9분 2초 구간

- 대상: `.tmp/srcTest-smoke/smoke-0900-2s.mp4`
- 목적: 6분 3초 gate를 통과한 YOLO5Face `0.12/0.18/0.45` 조합이 얼굴 수가 많은 다른 구간에서도 FaceONNX baseline과 같은 품질 gate를 통과하는지 확인한다.

실험 1: YOLO5Face `objectness=0.12`, `confidence=0.18`, `nms=0.45`

- FaceONNX baseline: `baselineFrames=55`, `totalMs=19,772ms`.
- YOLO5Face optimized: `optimizedFrames=58`, `totalMs=12,314ms`.
- A/B 결과: `common=55`, `onlyBaseline=0`, `onlyOptimized=3`, `avgBestIou=0.798`, `minBestIou=0.625`, `boxCountDiffFrames=34`, `passed=False`.
- bad frame 로그: `boxCountDiff=10,17,18,19,20,21,22,23,24,25,31,32,33,34,35,36,37,38,39,40,...`, `lowIou=11,12,13,14,15,22,24,43,47,49,55,57,60,61`.
- 판단: `보류`. 6분 3초 대표 gate와 달리 얼굴 수가 많은 9분 구간에서는 YOLO-only frame, 박스 수 차이, 낮은 IoU가 발생한다. YOLO-only 후보 중 일부는 실제 얼굴일 수 있으므로 실제 오탐으로 단정하지 않는다. 다만 기존 동작 변화 폭이 크고 overlay/GT 기준 통과 증거가 부족해 최종 추천 후보로 유지하지 않는다.

실험 2: YOLO5Face `objectness=0.18`, `confidence=0.25`, `nms=0.45`

- FaceONNX baseline: `baselineFrames=55`, `totalMs=16,920ms`.
- YOLO5Face optimized: `optimizedFrames=54`, `totalMs=10,720ms`.
- A/B 결과: `common=53`, `onlyBaseline=2`, `onlyOptimized=1`, `avgBestIou=0.793`, `minBestIou=0.625`, `boxCountDiffFrames=29`, `passed=False`.
- bad frame 로그: `boxCountDiff=20,21,22,23,24,31,32,33,34,35,36,37,38,39,40,41,42,43,44,45,...`, `lowIou=11,12,13,14,15,22,24,43,47,49,55,57,60,61`.
- 판단: threshold를 올리면 YOLO-only 후보는 일부 줄지만, FaceONNX-only/YOLO-only frame과 박스 정합 실패가 남아 gate를 통과하지 못한다. 이 단계만으로 실제 미탐/오탐 여부는 확정하지 않는다.

실험 3: YOLO5Face `objectness=0.18`, `confidence=0.25`, `nms=0.30`

- FaceONNX baseline: `baselineFrames=55`, `totalMs=17,226ms`.
- YOLO5Face optimized: `optimizedFrames=54`, `totalMs=10,662ms`.
- A/B 결과: `common=53`, `onlyBaseline=2`, `onlyOptimized=1`, `avgBestIou=0.793`, `minBestIou=0.625`, `boxCountDiffFrames=29`, `passed=False`.
- 판단: NMS를 더 강하게 낮춰도 실험 2와 결과가 사실상 같았다. 따라서 이 구간의 주된 실패 원인은 단순 중복 박스 NMS가 아니라 YOLO5Face의 후보 분포, FaceONNX 대비 박스 크기/위치 차이, YOLO 전용 post-filter/track 정합 문제에 가깝다.

현재 YOLO5Face 판정 보정:

- `YoloV5Face.onnx` `0.12/0.18/0.45` 조합은 6분 3초 gate와 12분 30초 smoke 기준으로는 빠르고 유망하지만, 9분 2초 추가 gate에서 실패했다.
- 따라서 이 조합을 최종 `추천 후보`로 보지 않는다. 현재 상태는 `대표 6분 3초 구간 통과 후보 / 전체 추천 보류`다.
- Home UI에서 YOLO를 선택했을 때의 초기 profile은 지금까지 가장 나은 YOLO5Face 조합으로 남겨두지만, 앱 기본 detector는 계속 FaceONNX다.
- 다음 YOLO 작업은 9분 구간의 box count diff와 low-IoU frame을 기준으로 원인을 더 분리해야 한다. 우선순위는 raw YOLO 후보 dump, FaceONNX 대비 후보 수 차이, 큰 얼굴/다중 얼굴 구간의 YOLO 전용 post-filter, track merge 기준 재조정이다.

9분 구간 원인 분리 및 YOLO 하단 저신뢰 track 필터:

- `-DumpDetections`와 대표 frame overlay로 9분 구간 mismatch를 확인했다.
- frame 20/32 overlay 기준 YOLO의 추가 청록 박스는 뒤쪽 사람 얼굴 후보로 보이며, FaceONNX baseline이 놓친 실제 얼굴일 가능성이 있다.
- frame 32의 노란 박스는 손/물체 영역을 얼굴로 잡은 오탐이다.
- 큰 얼굴 박스는 YOLO가 FaceONNX보다 더 넓게 잡는 경향이 있어 `avgBestIou`와 `minBestIou`를 낮춘다. 이 차이는 단순 NMS 문제는 아니다.
- 따라서 9분 실패 축은 하나가 아니라 다음이 섞여 있다.
  - 실제 작은/뒤쪽 얼굴 추가 검출: 모델 recall 측면에서는 개선일 수 있지만, FaceONNX 기준 strict frame/box gate에서는 `onlyOptimized`와 box count diff로 잡힌다.
  - 손/물체 오탐: YOLO 전용 false-positive filter 대상이다.
  - 큰 얼굴 box shape 차이: YOLO decode는 동작하지만 FaceONNX 대비 박스 정의가 넓어 box 정합 gate를 낮춘다.

코드 보정:

- `FaceTrackPostProcessOptions`에 YOLO 전용 하단 저신뢰 track 제거 옵션을 추가했다.
- `LowerFrameTrackMaxConfidence`, `LowerFrameTrackMinCenterYRatio`, `LowerFrameTrackMinAreaRatio`, `LowerFrameTrackMaxAreaRatio`를 추가했다.
- FaceONNX profile은 기본값 `LowerFrameTrackMaxConfidence=0`으로 비활성이다.
- YOLO profile에서는 `LowerFrameTrackMaxConfidence=0.50`, `LowerFrameTrackMinCenterYRatio=0.58`, `LowerFrameTrackMinAreaRatio=0.015`, `LowerFrameTrackMaxAreaRatio=0.045`를 적용한다.
- 목적은 9분 frame 31~38에 나온 손/물체 오탐처럼 화면 하단의 중간 크기 저신뢰 track만 좁게 제거하는 것이다.
- `FaceTrackPostProcessResult`와 로그에 `removedLower`를 추가해 기존 `removedShort`와 분리했다.

보정 후 9분 2초 gate:

- FaceONNX baseline: `baselineFrames=55`, `totalMs=20,840ms`.
- YOLO5Face optimized: `optimizedFrames=58`, `totalMs=12,909ms`.
- YOLO track 후처리: `removedShort=0`, `removedLower=7`, `rewritten=58`.
- A/B 결과: `common=55`, `onlyBaseline=0`, `onlyOptimized=3`, `avgBestIou=0.798`, `minBestIou=0.625`, `boxCountDiffFrames=33`, `passed=False`.
- bad frame 로그: `boxCountDiff=10,17,18,19,20,21,22,23,24,25,32,33,34,35,36,37,38,39,40,41,...`, `lowIou=11,12,13,14,15,22,24,43,47,49,55,57,60,61`.
- 판단: 육안 확인된 손/물체 오탐 track 일부는 제거됐지만, gate 실패의 대부분은 YOLO-only 뒤쪽 얼굴 후보와 큰 얼굴 박스 정의 차이라서 이 필터만으로 통과하지 못한다. YOLO-only 뒤쪽 후보는 실제 얼굴 가능성이 있으므로 오탐으로 단정하지 않는다. 현재 추천 상태는 계속 `전체 추천 보류`다.

추가 box 보정 실험:

- `YoloFaceOnnxDetectorOptions`에 큰 YOLO5Face 박스를 축소하는 `LargeBoxWidthScale`, `LargeBoxHeightScale`, `LargeBoxMinAreaRatio` 옵션을 추가했다.
- 기본값은 `1.0/1.0/0.0`이라 앱 Home YOLO profile에서는 비활성이다. 현재는 `run-srcTest-smoke.ps1`에서 명시적으로 넘겨 실험할 수 있는 옵션으로만 둔다.
- 9분 2초 gate에서 `LargeBoxWidthScale=0.84`, `LargeBoxHeightScale=0.97`, `LargeBoxMinAreaRatio=0.03`을 적용하면 `avgBestIou=0.786`, `minBestIou=0.631`, `boxCountDiffFrames=33`, `passed=False`로 오히려 악화됐다.
- 판단: 큰 얼굴 박스가 넓은 문제는 확인됐지만, 일괄 축소 보정은 다른 frame의 정합을 같이 깨서 기본 profile에 넣지 않는다.

추가 진단 및 2단계 실험:

- `scripts/run-srcTest-smoke.ps1`에 `-DumpCompareDetails`를 추가했다. 이 옵션을 켜면 `[SmokeCompareDetail]` 로그로 `onlyBaseline`, `onlyOptimized`, `boxCountDiff`, `lowIou` frame의 baseline/optimized 박스 `x/y/w/h`, 정규화 중심점, 면적 비율, confidence를 출력한다.
- `[SmokeCompareNote]`를 추가해 `onlyBaseline`/`onlyOptimized`가 실제 정답 라벨의 미탐/오탐이 아니라 detector 간 차이임을 로그에 명시한다.
- 9분 2초 상세 로그 기준, YOLO5Face 실패는 두 가지가 섞여 있다.
  - `onlyOptimized=4,8,9`와 frame 10~49의 `boxCountDiff`는 화면 상단의 작은 두 번째 얼굴 후보가 FaceONNX보다 먼저 잡히는 현상이다. 예: frame 10의 추가 후보는 `cx=0.576`, `cy=0.052`, `area=0.00424`, `conf=0.455`였다.
  - `lowIou`는 큰 얼굴 박스의 정의 차이다. 예: frame 11은 FaceONNX `w=535,h=638,area=0.04115` 대비 YOLO `w=727.8,h=663.9,area=0.05826`으로 YOLO 폭이 더 넓다.
- `YoloConfidenceThreshold=0.70` 실험은 작은 추가 후보 일부를 줄였지만 큰 얼굴도 FaceONNX 대비 누락되어 `onlyBaseline=5,6,7,10`, `avgBestIou=0.777`, `minBestIou=0.000`, `boxCountDiffFrames=13`, `passed=False`였다. threshold만 올리는 방식은 baseline-diff 기준으로 누락을 만든다. 실제 미탐 여부는 해당 frame overlay 확인이 필요하다.
- `YoloInputSize=800` 실험은 `totalMs=21,556ms`로 FaceONNX baseline `20,883ms`보다 느려졌고, `avgBestIou=0.783`, `minBestIou=0.553`, `boxCountDiffFrames=37`, `passed=False`였다. 입력 크기 확대는 속도/품질 모두 기본 후보보다 나쁘다.
- `run-srcTest-smoke.ps1`에 `-YoloUseFaceOnnxRoiRefine` 실험 옵션을 추가했다. YOLO 결과 중 `YoloFaceOnnxRoiMinAreaRatio` 이상 큰 박스만 FaceONNX ROI detector로 재검출한다.
- 9분 2초에서 `-YoloUseFaceOnnxRoiRefine -YoloFaceOnnxRoiMinAreaRatio 0.03 -YoloFaceOnnxRoiMaxCandidates 64`는 `candidates=52`, `attempts=50`, `hits=50`, `elapsedMs=22,956`이었다.
- 이 2단계 실험은 `avgBestIou=0.816`으로 기본 YOLO `0.798`보다 조금 나아졌지만 `minBestIou=0.567`, `boxCountDiffFrames=33`, `passed=False`였고, ROI refine 추가 시간 때문에 속도 이점도 사라진다.
- 판단: 현재 9분 구간 실패는 threshold, 입력 크기, 단순 큰 박스 축소, 큰 박스 FaceONNX ROI refiner만으로 해결되지 않는다. 남은 후보는 더 세밀한 박스 보정 모델, 작은 상단 얼굴 후보를 실제 얼굴/오탐으로 분류할 verifier, 또는 다른 YOLO face 모델이다.

보정 후 6분 3초 회귀 gate:

- FaceONNX baseline: `baselineFrames=19`, `totalMs=57,937ms`.
- YOLO5Face optimized: `optimizedFrames=19`, `totalMs=17,651ms`.
- YOLO track 후처리: `removedShort=0`, `removedLower=0`, `rewritten=19`.
- A/B 결과: `common=19`, `onlyBaseline=0`, `onlyOptimized=0`, `avgBestIou=0.971`, `minBestIou=0.944`, `boxCountDiffFrames=0`, `passed=True`.
- 판단: 하단 저신뢰 track 필터가 기존 6분 3초 통과 구간을 깨지는 않았다.
- script 진단 옵션 추가 후에도 6분 3초 gate는 다시 통과했다. 최신 실행은 FaceONNX baseline `totalMs=120,601ms`, YOLO optimized `totalMs=20,367ms`, `avgBestIou=0.971`, `minBestIou=0.944`, `boxCountDiffFrames=0`, `passed=True`였다. 이 실행의 FaceONNX baseline 시간은 같은 세션의 부하 영향이 커서 속도 비교 기준값으로 고정하지 않는다.

회귀 검증:

- `dotnet build FaceShield.sln` 성공. 기존 FFmpeg obsolete warning 7개만 남았다.
- `git diff --check` 통과.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify-auto-mosaic-default.ps1` 기본 실행 성공.
- default verifier는 track postprocess policy, 6분 3초 FaceONNX all-frame parallel quality gate, ROI-hit 대표 구간, short auto-tune provider gate를 모두 통과했다.
- 최신 short auto-tune gate는 `FaceOnnxDetector/CPU`, `pipe-parallel`, `processed=150`, `detects=150`, `interpolated=0`, `faceMaskFrames=19`, `totalMs=48,805ms`였다.
