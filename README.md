# FaceShield

FaceShield는 Windows와 macOS에서 동작하는 **로컬 영상 얼굴 모자이크 편집기**입니다. Avalonia/.NET 8 기반이며, 자동 얼굴 검출과 수동 마스크 보정, 작업 상태 복구, FFmpeg 기반 영상 내보내기를 한 앱에서 처리합니다.

현재 운영 기준 브랜치는 `main`입니다.

## 주요 기능

- 영상 자동 얼굴 검출 및 모자이크
- FaceONNX / YOLO Face ONNX 검출 경로
- 프레임별 얼굴 박스 및 마스크 검토
- 브러시/지우개 기반 수동 마스크 수정
- 자동 처리 취소 후 이어서 진행
- 최근 영상과 workspace 상태 복구
- 낮은 신뢰도·연속성 문제 프레임 검토
- Windows DirectML 시도 및 CPU fallback
- macOS CPU/가속 경로 지원
- 원본 해상도 기반 FFmpeg export
- 프레임 수, timestamp, 색상, 오디오, HDR 및 container 무결성 검사
- Windows/macOS self-contained 배포 workflow

## 지원 환경

| 구분 | 대상 |
| --- | --- |
| Windows | x64 / `win-x64` |
| macOS Apple Silicon | `osx-arm64` |
| macOS Intel | `osx-x64` |
| .NET | .NET 8 / `net8.0` |
| UI | Avalonia 11.3.9 |
| FFmpeg binding | FFmpeg.AutoGen 8.0.0 |

Linux는 현재 배포/검증 대상이 아닙니다.

## 빠른 사용 흐름

1. 영상을 선택합니다.
2. 자동 모자이크 또는 직접 편집을 선택합니다.
3. 자동 처리 후 문제 프레임을 검토합니다.
4. 필요한 프레임의 마스크를 브러시/지우개로 수정합니다.
5. 최종 영상을 저장합니다.

기본 출력 이름은 다음 형식입니다.

```text
<원본이름>_blur<원본확장자>
```

원본 영상 파일 자체는 수정하지 않습니다.

## 자동 분석

자동 처리에는 다음 요소가 포함됩니다.

- 얼굴 검출
- frame 간 tracking / interpolation
- ROI 기반 재검토
- 짧은 누락 구간 보강
- scene-cut 경계 보호
- 시간축 흔들림 완화
- sparse tracking materialization

검출 해상도, 검출 간격, 병렬 세션, ONNX Runtime 설정 등을 조절할 수 있습니다. 처리 속도를 높이는 설정은 작은 얼굴이나 짧게 등장하는 얼굴의 검출률에 영향을 줄 수 있으므로 실제 영상으로 확인해야 합니다.

## 수동 편집

워크스페이스에서는 다음 작업을 할 수 있습니다.

- 현재 프레임 검출 표시/숨김
- 브러시로 마스크 추가
- 지우개로 마스크 삭제
- 최근 편집 되돌리기
- 문제 프레임 목록 확인
- 이전/다음 이상 프레임 이동
- 프레임 단위 이동
- 재생/정지
- 최종 영상 저장

주요 단축키:

| 키 | 동작 |
| --- | --- |
| Q / E | 이전 / 다음 이상 프레임 |
| ← / → | 1프레임 이동 |
| Shift + ← / → | 10프레임 이동 |
| ↑ / ↓ | 약 1초 이동 |
| Home / End | 처음 / 마지막 프레임 |
| Space | 재생 / 정지 |

## 상태 저장

FaceShield는 `Environment.SpecialFolder.LocalApplicationData` 아래 `FaceShield` 디렉터리에 실행 상태를 저장합니다.

```text
FaceShield/
├─ state.json
├─ state.json.bak
├─ workspaces/
├─ Logs/
└─ crash.log
```

상태 저장에는 최근 영상, 자동 분석 설정, 현재 workspace 위치, frame별 mask/face 정보, resume 상태와 export gate 상태가 포함될 수 있습니다.

## 영상 내보내기

내보내기는 FFmpeg 기반이며 결과 손상을 조용히 허용하기보다 **검증 실패 시 중단하는 쪽**을 우선합니다.

주요 검증 대상:

- 입력 해상도와 frame coverage
- PTS / DTS 순서와 누락
- video/audio stream 처리
- 색 공간과 color range
- chroma location
- static HDR metadata
- container 구조
- 해상도/pixel-format 중간 변경
- staging 파일 완료 후 최종 파일 확정

독립 export 검증 방법은 [`docs/VIDEO_EXPORT_QUALITY_GATE.md`](docs/VIDEO_EXPORT_QUALITY_GATE.md)를 참고하십시오.

## 개발 환경

```bash
git clone https://github.com/doanythingK/FaceShield_.git
cd FaceShield_
git switch main
```

.NET 8 SDK가 필요합니다.

```bash
dotnet --info
dotnet restore FaceShield.sln
dotnet build FaceShield.sln
```

현재 별도의 conventional unit-test 프로젝트는 없습니다. 대신 `scripts/`에 영상 처리·검출·export 회귀를 확인하기 위한 검증 스크립트가 있습니다.

## Windows 개발/배포

Windows용 FFmpeg DLL 묶음은 다음 파일을 source bundle로 사용합니다.

```text
FFmpeg/win-x64-binaries.tar.gz
```

필요할 때 압축을 풉니다.

```powershell
New-Item -ItemType Directory -Force FFmpeg/win-x64 | Out-Null
tar -xzf FFmpeg/win-x64-binaries.tar.gz -C FFmpeg/win-x64
```

빌드:

```powershell
dotnet restore FaceShield.csproj -r win-x64
dotnet build FaceShield.csproj -c Release -r win-x64
```

배포:

```powershell
dotnet publish FaceShield.csproj -c Release -r win-x64 --self-contained true
```

Windows ONNX Runtime native 파일은 NuGet package 경로를 우선 사용합니다. `Native/win-x64/DirectML.dll`은 DirectML package 파일을 찾을 수 없는 경우를 위한 fallback으로 유지합니다.

## macOS 개발/배포

macOS FFmpeg dylib는 저장소에 생성물로 커밋하지 않습니다. Homebrew 의존성을 설치한 뒤 준비 스크립트로 생성합니다.

```bash
brew install ffmpeg libomp srt
bash scripts/prepare-ffmpeg-osx.sh
```

아키텍처를 명시할 수도 있습니다.

```bash
bash scripts/prepare-ffmpeg-osx.sh osx-arm64
bash scripts/prepare-ffmpeg-osx.sh osx-x64
```

배포 예시:

```bash
dotnet publish FaceShield.csproj -c Release -r osx-arm64 --self-contained true
```

macOS package workflow는 필요한 dylib를 `.app` bundle 안으로 수집하고 ad-hoc signing 후 ZIP artifact를 생성합니다.

## 프로젝트 구조

```text
FaceShield_/
├─ Assets/
├─ Controls/
├─ Enums/
├─ Models/
├─ Services/
│  ├─ Analysis/
│  ├─ FaceDetection/
│  ├─ Models/
│  ├─ Video/
│  └─ Workspace/
├─ ViewModels/
├─ Views/
├─ docs/
├─ scripts/
├─ FFmpeg/
├─ Native/
├─ .github/workflows/
├─ FaceShield.csproj
└─ FaceShield.sln
```

### 코드 책임

- `Services/Analysis`: 자동 분석, tracking, scene-cut, 후처리
- `Services/FaceDetection`: detector abstraction과 ONNX 기반 detector
- `Services/Video`: decode, timeline, mask, 색상, encoder, audio/HDR, export 무결성
- `Services/Workspace`: 상태 저장, workspace generation, 실행 signature, export gate
- `ViewModels` / `Views`: Avalonia UI와 화면 상태
- `scripts`: 회귀 검증, 품질 evidence 생성, native 준비/검사 도구

## GitHub Actions

### Hardening Quality Gate

`.github/workflows/quality-gate.yml`

- `main` push
- `hardening/**` push
- `main` 대상 pull request
- Windows `win-x64` Release build
- macOS `osx-arm64` Release build

이 workflow는 현재 compile/build gate이며 실제 GUI 조작이나 얼굴 검출 정확도까지 보증하지 않습니다.

### Build Windows App

`.github/workflows/windows-build.yml`

수동 실행 전용입니다. Windows self-contained publish와 native DLL 확인 후 ZIP artifact를 생성하며 artifact 보관기간은 1일입니다.

### Build macOS App

`.github/workflows/macos-build.yml`

수동 실행 전용입니다. `osx-arm64` / `osx-x64` package를 생성하며 같은 이름의 과거 artifact를 정리하고 새 artifact는 1일 보관합니다.

### Actions History Cleanup

`.github/workflows/actions-history-cleanup.yml`

수동 실행 전용입니다. workflow별 최근 run을 제한하고 disposable Actions cache를 제거합니다.

## 검증 수준과 한계

자동 build가 성공해도 다음 항목은 별도 검증이 필요합니다.

- 실제 Windows/macOS GUI 시작
- GPU execution provider 실제 로드
- 긴 영상 처리 중 메모리 사용
- 다양한 container/codec 호환성
- 실제 얼굴 검출 정확도
- 작은 얼굴, 측면 얼굴, 가림, 조명 변화
- 내보낸 결과의 시각적 품질

라벨된 ground-truth 데이터 없이 검출 정확도 수치를 신뢰성 있게 단정할 수 없습니다.

## 저장소에서 의도적으로 유지하는 대형/특수 파일

다음 파일은 단순 잔여물이 아니므로 이유 없이 삭제하지 않습니다.

- `FFmpeg/win-x64-binaries.tar.gz`: Windows build/publish가 사용하는 FFmpeg source bundle
- `Native/win-x64/DirectML.dll`: NuGet DirectML native 파일 부재 시 fallback
- `AUTO_MOSAIC_QUALITY_SPEED_PLAN.md`: 일부 YOLO 검증 스크립트가 상태/evidence 키를 읽는 호환 문서
- `YOLO_GUI_SMOKE_RESULT.md`, `YOLO_PROBLEM_SPAN_VERIFICATION.md`: YOLO 검증 체인의 evidence/guide

반대로 macOS FFmpeg dylib는 `scripts/prepare-ffmpeg-osx.sh`가 재생성하므로 Git에 추적하지 않습니다.

## 관련 문서

- [`Future Feature Roadmap`](docs/FUTURE_FEATURE_ROADMAP.md)
- [`Video Export Quality Gate`](docs/VIDEO_EXPORT_QUALITY_GATE.md)

## 개발 시 주의사항

- 원본 영상 파일을 직접 수정하지 않습니다.
- native FFmpeg/ONNX Runtime 파일은 대상 RID와 일치해야 합니다.
- Windows/macOS native 파일을 섞지 않습니다.
- export integrity guard를 임의로 우회해 오류를 숨기지 않습니다.
- 상태 저장 구조를 바꿀 때 backup과 generation cleanup을 함께 고려합니다.
- YOLO evidence/state 파일을 이동하거나 이름을 바꿀 때는 이를 읽는 verifier 경로도 함께 수정해야 합니다.
