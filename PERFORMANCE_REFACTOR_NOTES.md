# 성능 및 얼굴 검출 리팩터링 기록

## 배경
10분짜리 영상에서 자동 처리와 export까지 약 2시간이 걸리는 문제가 있었다. 사용자는 퀄리티를 중요하게 보고 있으며, 얼굴이 빠지거나 엉뚱한 곳이 잡히는 오탐/미탐도 결과물 품질을 떨어뜨리는 핵심 문제로 봤다.

중요한 전제는 `3프레임마다 검출`로 설정해도 처리 시간이 길었다는 점이다. 따라서 단순히 “검출 간격을 늘리면 된다”는 접근은 맞지 않다. 병목은 얼굴 검출뿐 아니라 export, 프레임 변환, 마스크 생성, 블러, 인코딩에도 있을 수 있다.

## 솔루션 구조 분석
FaceShield는 .NET 8 기반 Avalonia 데스크톱 앱이다. 화면은 `Views`, 상태와 명령은 `ViewModels`, 영상 처리와 얼굴 검출은 `Services`에 나뉘어 있다. 전체 방향은 MVVM 구조를 따르고 있으며, 단순 데모가 아니라 실제 영상 처리 앱에 필요한 자동 모자이크, 수동 보정, export, 재개 상태 저장, macOS/Windows 패키징까지 고려한 프로젝트다.

좋은 점은 역할 분리가 어느 정도 되어 있다는 것이다. 예를 들어 얼굴 검출은 `Services/FaceDetection`, 자동 마스크는 `Services/Analysis`, 영상 디코딩과 export는 `Services/Video`, 작업 화면 상태는 `ViewModels/Pages/WorkspaceViewModel.cs` 쪽에 있다. FFmpeg, ONNX Runtime, DirectML/CoreML 시도, 하드웨어 디코딩 실패 시 fallback 같은 실사용 대응도 들어가 있다.

다만 구현이 오래 쌓이면서 핵심 클래스가 너무 커졌다. `WorkspaceViewModel`은 UI 상태, 자동 실행, export, 이슈 목록, 저장/복원, 에러 처리까지 많이 들고 있다. `AutoMaskGenerator`도 검출, 파이프라인, 병렬 처리, ROI 재검출, 필터링, 통계 판단이 한 클래스에 몰려 있다. `VideoExportService` 역시 디코딩, 오디오 처리, 하이브리드 복사, 마스크 적용, 인코딩을 모두 관리한다.

이 구조는 기능을 빠르게 붙이기에는 좋았지만, 지금처럼 성능과 퀄리티를 동시에 잡아야 하는 단계에서는 부담이 된다. 특정 모델이나 특정 export 방식에 코드가 직접 묶여 있으면 실험을 하기가 어렵고, 한 부분을 고치다 다른 부분이 깨질 가능성도 커진다.

또 하나의 리스크는 자동화 테스트가 없다는 점이다. 영상 처리, FFmpeg 포인터, ONNX 실행 공급자, export 품질은 회귀가 생기기 쉬운 영역이다. 현재는 `dotnet build`와 수동 테스트가 중심이므로, 앞으로는 적어도 순수 로직부터 테스트를 붙이는 것이 필요하다.

## 리팩터링 판단
처음에는 “AI 모델을 더 가벼운 것으로 바꾸면 되지 않을까”를 검토했다. 하지만 현재 문제는 모델 하나만의 문제가 아닐 가능성이 크다. 사용자가 이미 `3프레임마다 검출`로 돌렸는데도 10분 영상이 약 2시간 걸렸기 때문이다.

따라서 우선순위를 다음처럼 잡았다.

1. 모델을 교체할 수 있는 구조를 먼저 만든다.
2. export에서 불필요한 전체 마스크 생성 비용을 줄인다.
3. 3프레임 간격 검출에서 생길 수 있는 어색한 박스 튐을 줄인다.
4. 실제 병목을 확인할 수 있게 로그를 남긴다.

퀄리티가 중요한 앱이므로 단순히 검출 횟수를 줄이는 방식은 적절하지 않다. 얼굴이 빠지면 안 되고, 마스크가 튀거나 흔들려도 결과물이 어색해진다. 그래서 향후 방향은 “빠른 모델 하나로 대체”가 아니라 “빠른 1차 검출 + 의심 구간 재검출 + 시간축 안정화”가 되어야 한다.

## 작업 브랜치
작업 브랜치: `detector-backend-refactor`

현재까지 push된 주요 커밋:

- `851463f refactor: decouple face detector backend`
- `f0793de perf: optimize face mask export path`
- `8d37cc6 docs: record performance refactor notes`
- `2f30a04 docs: add solution analysis notes`

## 다음 세션 이어받기 메모
이 문서는 사용자 설명용이 아니라 다음 세션에서 바로 이어서 개발하기 위한 작업 로그다. 다음 세션에서는 먼저 이 브랜치 상태를 확인하고, 실제 문제 영상 기준으로 성능 로그를 확보한 뒤 다음 리팩터링을 이어가면 된다.

현재 브랜치는 `detector-backend-refactor`이고 원격 브랜치도 같은 이름이다. 기존 untracked `.codex`는 작업 대상이 아니므로 건드리지 않는다.

핵심 변경 entry point:

- `Services/FaceDetection/IBgraFaceDetector.cs`: BGRA 프레임 기반 얼굴 검출 인터페이스.
- `Services/FaceDetection/IFaceDetectorFactory.cs`: 자동 처리 쪽에서 구체 모델 생성을 직접 알지 않게 하는 팩토리 인터페이스.
- `Services/FaceDetection/FaceDetectorFactory.cs`: 현재는 `FaceDetectorBackend.FaceOnnx`만 생성한다. 새 모델을 붙일 때 우선 수정할 위치다.
- `Services/Analysis/AutoMaskGenerator.cs`: 자동 분석 파이프라인. `IBgraFaceDetector`를 이용한 raw/BGRA 경로와 병렬 detector 생성 흐름이 들어 있다.
- `ViewModels/Pages/WorkspaceViewModel.cs`: detector factory 생성, 자동 분석 실행, `ApplyAutoTemporalSmoothing()` 호출 위치가 있다.
- `Services/Video/MaskedVideoExporter.cs`: `ApplyFaceRectsAndBlur()`가 자동 얼굴 박스용 direct blur 경로다.
- `Services/Video/VideoExportService.cs`: export 중 자동 face rect는 direct blur, 수동 mask는 기존 bitmap mask blur로 분기한다.

주의할 점:

- 아직 기본 detector는 FaceONNX다. 모델 교체 구조만 열어둔 상태다.
- 자동 face rect가 있는 프레임만 direct blur를 탄다. 수동 마스크나 bitmap mask가 필요한 프레임은 기존 경로를 유지한다.
- `ApplyAutoTemporalSmoothing()`은 자동 생성 프레임에만 적용해야 한다. 사용자가 직접 수정한 프레임까지 smoothing하면 의도한 수동 보정을 망칠 수 있다.
- 성능 개선 폭은 아직 확정하지 않았다. 실제 10분 영상 재측정 전까지는 개선 수치를 단정하면 안 된다.
- 오탐/미탐은 모델, threshold, ROI 재검출, tracking 부재가 같이 얽힌 문제일 수 있다. 모델 하나만 바꾸면 해결된다고 단정하지 않는다.

## 모델 교체를 위한 구조 정리
기존 자동 모자이크 코드는 `FaceOnnxDetector`라는 특정 얼굴 검출기에 직접 묶여 있었다. 이 구조에서는 나중에 더 가벼운 모델이나 더 정확한 모델을 실험하려면 자동 처리 코드 여러 곳을 직접 수정해야 했다.

이를 줄이기 위해 얼굴 검출기 생성 책임을 팩토리로 분리했다. 이제 자동 처리 코드는 “얼굴 검출기를 만들어 달라”고 요청하고, 실제로 어떤 모델을 쓸지는 별도 생성 담당이 정한다.

또한 빠른 영상 처리를 위한 BGRA 기반 얼굴 검출 인터페이스를 만들었다. 현재 FaceONNX도 이 인터페이스를 구현한다. 나중에 BlazeFace, SCRFD, 다른 ONNX 모델을 붙일 때 같은 형태로 구현하면 자동 마스크 파이프라인에 넣을 수 있다.

현재 기본 모델은 여전히 FaceONNX다. 모델 자체를 바꾼 것은 아니다.

## Export 경로 최적화
기존 export 흐름에서는 자동 검출 결과가 얼굴 박스뿐이어도 매 프레임마다 영상 전체 크기의 마스크 이미지를 만들었다. 4K 영상이나 긴 영상에서는 이 과정이 매우 비쌀 수 있다.

수정 후에는 자동 검출 얼굴 박스가 있는 프레임은 전체 마스크 이미지를 만들지 않는다. 얼굴 주변 영역만 직접 계산해서 블러를 적용한다. 부드러운 가장자리 처리는 유지했다.

수동으로 사용자가 칠한 마스크는 기존 방식 그대로 처리한다. 수동 편집은 정확도가 중요하므로 비트맵 마스크 경로를 유지했다.

## 시간축 안정화
3프레임마다 검출하면 얼굴 박스가 프레임 사이에서 계단처럼 움직이거나 살짝 튀어 보일 수 있다. 이를 줄이기 위해 자동 검출 결과에 시간축 smoothing을 추가했다.

앞뒤 프레임의 얼굴 위치를 참고해 현재 프레임의 얼굴 박스를 부드럽게 섞는다. 단, 위치나 크기 차이가 너무 크면 같은 얼굴로 보지 않는다. 잘못된 얼굴 위치로 끌려가는 것을 막기 위한 제한이다.

수동 편집된 프레임은 smoothing 대상에서 제외한다.

## 성능 로그 보강
export가 실제로 어디서 느린지 다음 테스트 때 확인할 수 있도록 로그를 보강했다.

마지막 export 로그에는 다음 값들이 남는다.

```text
bitmapMaskFrames=...
directFaceFrames=...
swsToBgraMs=...
maskMs=...
swsToEncMs=...
encodeMs=...
totalMs=...
```

이 값으로 전체 시간 중 마스크/블러, 색상 변환, 인코딩 중 어디가 큰지 확인할 수 있다.

## 검증 결과
`dotnet build FaceShield.sln` 빌드는 성공했다.

결과:

- Error: 0
- Warning: 13

경고는 기존 중복 using, nullable 경고, FFmpeg obsolete API, 일부 Avalonia dialog 생성자 관련 경고다. 이번 변경으로 새 컴파일 에러는 발생하지 않았다.

## 아직 확실하지 않은 점
실제 10분 영상에서 2시간이 얼마나 줄어드는지는 아직 측정하지 않았다. 따라서 성능 개선 폭은 알 수 없다.

이번 변경은 export에서 전체 마스크 이미지를 만들던 비용을 줄이는 방향이다. export 쪽이 병목이었다면 개선 가능성이 크다. 하지만 병목이 인코딩이나 디코딩에 더 크다면 추가 최적화가 필요하다.

오탐/미탐 문제도 모델 교체만으로 단정할 수 없다. 퀄리티를 유지하려면 다음 단계에서 다음 작업이 필요하다.

- 빠른 1차 모델과 강한 2차 모델을 나누는 구조
- 의심 구간만 재검출하는 흐름
- 얼굴별 트랙 관리
- scene cut 감지
- 실제 샘플 영상 기준 속도/미탐/오탐 비교

## 다음 권장 작업
1. 사용자가 문제를 느낀 10분 영상으로 다시 실행한다.
2. export 로그의 `maskMs`, `encodeMs`, `swsToBgraMs`, `swsToEncMs`, `totalMs`를 확인한다.
3. 자동 검출 시간과 export 시간을 분리해서 기록한다.
4. 미탐/오탐이 발생한 구간을 샘플로 모은다.
5. FaceONNX, SCRFD 계열, BlazeFace 계열을 같은 샘플 영상으로 비교한다.

## 현재 결론
이번 작업은 최종 해결이 아니라 기반 정리와 1차 성능 개선이다.

핵심은 두 가지다.

- 모델을 갈아끼울 수 있는 구조를 만들었다.
- 자동 얼굴 박스 export에서 불필요한 전체 마스크 생성을 줄였다.

다음 판단은 실제 영상 재실행 로그를 보고 해야 한다.

## 2026-05-06 추가 작업: 브랜치 비교 후 품질 유지형 자동 튜닝
작업 브랜치: `perf/quality-speed-max`

비교한 브랜치:

- `main`
- `fix/auto-persist-quality`
- `fix/macos-manual-and-auto-quality-speed`
- `detector-backend-refactor`

판단:

- `detector-backend-refactor`는 `fix/macos-manual-and-auto-quality-speed` 위에 얼굴 검출 백엔드 분리, direct face-rect export, smoothing, 성능 로그가 추가된 상태다.
- `fix/auto-persist-quality`에는 자동 튜닝, proxy/refine, 캐시/상태 저장 변경이 따로 있으나 UI/상태 저장 범위까지 크게 갈라져 있어 그대로 병합하면 충돌과 회귀 위험이 크다.
- 이번 작업에서는 품질을 낮추는 검출 간격 증가나 threshold 완화는 하지 않고, 자동 튜닝의 핵심만 현재 detector factory 구조에 맞춰 이식했다.

변경 내용:

- `FaceOnnxDetectorOptions.UseOrtOptimization` 기본값을 `true`로 바꿔 ONNX Runtime 그래프 최적화를 기본 적용한다.
- `FaceOnnxDetectorOptions`에 `EnablePreprocessParallelism`, `AllowAutoTune`, `AllowAutoGpu` 옵션을 추가했다.
- `FaceOnnxDetector.GetDefaultThresholds()`는 매번 임시 detector를 만들지 않도록 `Lazy` 캐시로 바꿨다.
- `DetectorAutoTuner`를 추가해 자동 실행 시작 시 첫 프레임 기준으로 CPU 세션 수와 intra-op 스레드 수 후보를 짧게 측정한다. 다운스케일 자동 처리에서는 원본이 아니라 실제 검출 입력 해상도 기준으로 측정하고, 캐시는 검출 입력 크기/품질/threshold/스레드 설정 기준으로 분리한다.
- `WorkspaceViewModel.RunAutoCoreAsync()`는 튜닝 결과를 이번 자동 실행에만 적용하고, 기존 사용자 설정의 downscale/quality/tracking/detect interval은 유지한다.
- GPU 사용 설정이 켜져 있어도 자동 튜닝은 CPU 후보와 GPU 후보를 모두 비교한다. 특정 장비에서 GPU 초기화/전송 비용이 더 크면 CPU 조합을 선택할 수 있게 했다.
- `UseTracking=true`와 `DetectEveryNFrames > 1` 조합에서도 병렬 detector 파이프라인을 탈 수 있도록 `sparse-pipe-parallel` 경로를 추가했다. 검출 대상 프레임만 BGRA로 변환해 여러 detector에 분배하고, 검출 프레임은 즉시 반영하며 완료/취소 시점에 중간 프레임으로 tracking 결과를 펼친다.
- 자동 후처리(`ApplyAutoTemporalSmoothing`, `BuildAutoAnomaliesAsync`)는 전체 프레임마다 dictionary 조회를 반복하지 않고 현재 저장된 face-mask entry snapshot 기준으로 채우도록 바꿨다. 결과 판정은 유지하고 조회 비용만 줄이는 변경이다.

검증:

- `dotnet build FaceShield.sln`
- 결과: 성공
- Error: 0
- Warning: 13

아직 확실하지 않은 점:

- 실제 10분 영상에서 개선 폭은 아직 측정하지 않았다. 성능 수치는 실제 샘플의 `[AutoTune]`, `[AutoMaskPipe]`, `[Export]` 로그를 확인하기 전까지 알 수 없다.
- GPU 자동 선택은 기본값이 꺼져 있다. DirectML/CoreML 적용은 플랫폼별 native 의존성 검증 후 별도 테스트가 필요하다.
