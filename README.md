# FaceShield

FaceShield는 Windows와 macOS용 Avalonia 기반 영상 얼굴 모자이크 편집기입니다.

자동 얼굴 검출, 프레임별 마스크 검토·수정, 자동 처리 재개, 원본 영상 특성을 가능한 범위에서 보존하는 FFmpeg 내보내기를 하나의 데스크톱 앱에서 제공합니다. 기본 검출기는 FaceONNX이며 YOLO Face ONNX를 선택적으로 사용할 수 있습니다.

현재 운영 기준 브랜치는 `main`입니다.

## 주요 기능

- 영상 선택 후 자동 모자이크 또는 직접 편집
- FaceONNX 자동 얼굴 검출
- YOLO5Face / YOLOv8-Face ONNX 선택 지원
- 자동 처리 모드: Raw / Tracked / Full / Legacy
- 프레임별 얼굴 박스·마스크 저장
- 문제 프레임 검토: 얼굴 없음, 낮은 신뢰도, 연속성 끊김
- 브러시·지우개 기반 수동 마스크 보정
- 자동 처리 취소 및 이후 재개
- 최근 영상 및 워크스페이스 상태 복원
- Windows DirectML / macOS CoreML 또는 CPU 실행 경로
- FFmpeg 기반 원본 해상도 내보내기
- 영상/오디오/색상/HDR/타임스탬프/프레임 무결성 검사
- Windows/macOS self-contained 배포 workflow

## 지원 환경

| 구분 | 대상 | 현재 검증 범위 |
| --- | --- | --- |
| Windows | x64 / `win-x64` | GitHub Actions Release build 및 Windows publish workflow |
| macOS Apple Silicon | `osx-arm64` | GitHub Actions Release build 및 macOS app workflow |
| macOS Intel | `osx-x64` | publish workflow 제공 |
| Linux | 별도 지원 대상 아님 | 소스 일부가 컴파일될 수 있어도 배포/실행 검증 범위 밖 |
| .NET | .NET 8 | `net8.0` |
| UI | Avalonia 11.3.9 | Desktop |
| FFmpeg binding | FFmpeg.AutoGen 8.0.0 | native FFmpeg 별도 포함 |
| ONNX Runtime | Windows DirectML 1.23.0 / macOS·기타 CPU/CoreML 1.23.2 | RID별 패키지 분리 |

Windows와 macOS 모두 64비트 환경을 전제로 합니다.

## 사용자 빠른 시작

1. FaceShield를 실행합니다.
2. 홈 화면에서 영상을 선택합니다.
3. 모자이크 강도를 설정합니다.
4. 자동 처리라면 **자동 모자이크**, 직접 수정 중심이라면 **직접 편집**으로 시작합니다.
5. 자동 처리 완료 후 문제 프레임을 확인합니다.
6. 필요한 프레임에서 브러시 또는 지우개로 마스크를 수정합니다.
7. 저장을 실행해 최종 영상을 내보냅니다.

기본 모자이크 강도는 28이며 UI 허용 범위는 6~40입니다.

출력 파일은 일반적으로 입력 파일과 같은 폴더에 다음 형식으로 생성됩니다.

~~~text
<원본이름>_blur<원본확장자>
~~~

동일 이름의 파일이 이미 있으면 덮어쓰기 또는 고유 이름 생성을 선택할 수 있습니다.

## 자동 처리와 수동 편집

### 자동 모자이크

자동 모자이크는 선택한 얼굴 검출기와 처리 모드로 영상을 순차 분석합니다.

자동 처리 중 취소하면 처리된 상태와 마스크를 저장하고, 동일 영상을 다시 열었을 때 가능한 경우 이어서 진행할 수 있습니다. 자동 실행 설정이 달라졌거나 이전 실행의 증거가 불완전하면 안전을 위해 재시작할 수 있습니다.

### 직접 편집

직접 편집에서는 자동 검출 결과에 의존하지 않고 프레임별 마스크를 추가·삭제할 수 있습니다.

워크스페이스의 주요 기능:

- 검출 결과 표시/숨김
- 브러시로 마스크 추가
- 지우개로 마스크 삭제
- 최근 편집 되돌리기
- 얼굴 없음 문제 프레임
- 낮은 신뢰도 문제 프레임
- 연속성 끊김 문제 프레임
- 이전/다음 이상 프레임 이동
- 재생 및 프레임 탐색
- 최종 영상 저장

### 주요 단축키

| 키 | 동작 |
| --- | --- |
| Q / E | 이전 / 다음 이상 프레임 |
| ← / → | 1프레임 이동 |
| Shift + ← / → | 10프레임 이동 |
| ↑ / ↓ | 약 1초 이동 |
| Home / End | 처음 / 마지막 프레임 |
| Space | 재생 / 정지 |

## 자동 검출기

### 현재 UI에서 선택 가능한 검출기

| 검출기 | 기본 여부 | 모델 준비 | 가속 |
| --- | --- | --- | --- |
| FaceONNX | 기본 | FaceONNX 패키지 경로 사용 | Windows DirectML 시도, macOS CoreML 시도, 실패 시 CPU |
| YOLO Face ONNX | 선택 | YOLO ONNX 모델 필요 | Windows DirectML, macOS는 CoreML 옵션을 명시적으로 켠 경우 시도, 실패 시 CPU |

현재 홈 화면과 명령줄의 일반 사용자 경로는 FaceONNX와 YOLO Face ONNX 두 가지입니다.

코드 레벨의 `FaceDetectorFactory`에는 SCRFD ONNX와 YuNet ONNX 백엔드도 구현되어 있습니다. 다만 현재 홈 UI와 시작 옵션에는 노출되지 않으므로 일반 사용자용 기능으로 문서화하지 않습니다. 개발자가 직접 `FaceDetectorFactoryOptions`를 구성하는 경우에만 사용할 수 있습니다.

### FaceONNX

FaceONNX는 앱의 기본 검출 경로입니다.

설정 가능한 항목:

- Detection threshold
- Confidence threshold
- NMS threshold
- ONNX Runtime 최적화
- GPU 사용 시도
- ONNX intra-op thread 수

GPU 실행 공급자를 사용할 수 없거나 초기화에 실패하면 CPU 경로로 전환합니다.

### YOLO Face ONNX

지원 프로필:

- YOLO5Face
- YOLOv8-Face

각 프로필은 모델 경로, objectness/confidence/NMS threshold, 입력 크기, 타일 검출, 축소 비율, tracking 간격, 병렬 세션 수를 별도로 저장합니다.

기본 YOLO 입력 크기는 640이며 UI 입력 범위는 64~2048입니다.

#### 모델 파일 준비

YOLO를 선택한 다음 다음 중 하나를 사용합니다.

- 홈 화면의 **YOLO 모델 다운로드**
- **찾기** 버튼으로 로컬 `.onnx` 지정
- 개발 환경의 `Models/Yolo/` 폴더에 지원 파일명으로 배치

지원 기본 파일명:

~~~text
YoloV5Face.onnx
Yolo5Face.onnx
yolov8n-face-lindevs.onnx
yolov8s-face-lindevs.onnx
yolov8m-face-lindevs.onnx
yolov8l-face-lindevs.onnx
~~~

앱에서 다운로드한 모델의 논리 저장 위치:

~~~text
<LocalApplicationData>/FaceShield/Models/Yolo/
~~~

대표적인 경로:

- Windows: `%LOCALAPPDATA%\FaceShield\Models\Yolo`
- macOS: 일반적으로 `~/Library/Application Support/FaceShield/Models/Yolo`

내장 다운로드 대상:

| 파일 | 출처 | 코드에 표시된 라이선스 안내 |
| --- | --- | --- |
| `YoloV5Face.onnx` | Hugging Face `hayashiLin/deepfacelivemodels` | GPL-3.0 표시 |
| `yolov8n-face-lindevs.onnx` | GitHub `lindevs/yolov8-face` 1.0.1 | MIT 표시 + YOLOv8 upstream caveat |

다운로드는 취소할 수 있으며, 부분 `.download` 파일은 실패/취소 시 정리합니다. 서버가 `Content-Length`를 제공하는 경우 실제 수신 크기와 비교하고, 0 byte 파일은 완료로 인정하지 않습니다.

현재 저장소에는 해당 모델들의 권위 있는 SHA-256 값이 고정되어 있지 않으므로 다운로드 후 cryptographic checksum pinning은 아직 수행하지 않습니다. 모델의 배포·상업적 사용 조건은 각 upstream 라이선스를 별도로 확인해야 합니다.

## 자동 분석 설정

### 처리 모드

| UI 이름 | 내부 모드 | 동작 |
| --- | --- | --- |
| 자동 안정화 (권장) | Tracked | 추적을 사용해 짧은 누락을 연결하되 전체 후처리는 강제하지 않음 |
| 검출 결과 그대로 | Raw | 매 프레임 검출, 추적·보간·후처리 비활성 |
| 전체 보정 | Full | 추적 및 후처리 모듈 사용 |
| 이전 설정 호환 | Legacy | 이전 버전의 tracking/후처리 토글 조합 유지 |

### 품질·속도 설정

| 항목 | 선택값 / 기본 |
| --- | --- |
| 검출 해상도 | 100% / 75% / 50% / 33%, 기본 100% |
| 축소 품질 | 빠름(최근접) / 균형(보간), 기본 균형 |
| 검출 간격 | 1 / 2 / 3 / 5프레임 |
| 병렬 세션 | 1~4, 기본 2 |
| ONNX Runtime 최적화 | 기본 켜짐 |
| GPU 사용 | Windows/macOS에서 초기 기본 켜짐 |
| ONNX thread | 자동 또는 CPU 논리 코어 범위에서 선택 |
| 자동 완료 후 저장 | 기본 켜짐 |
| 모자이크 강도 | 기본 28, 범위 6~40 |

품질 우선 시작점:

- 검출 해상도 100%
- 균형(보간)
- 검출 간격 1
- 병렬 세션 2
- ONNX Runtime 최적화 켜짐

해상도를 낮추거나 검출 간격을 늘리면 속도는 빨라질 수 있지만 작은 얼굴과 짧은 등장 구간의 누락 가능성이 증가할 수 있습니다.

### 후처리 설정

현재 자동 처리에는 다음 옵션이 있습니다.

- ROI 후처리
- 약한 단일 오탐 제거
- 짧은 gap 보강
- 장면 전환 carry 정리
- 시간축 smoothing
- YOLO risk cascade

YOLO risk cascade는 YOLO 위험 프레임을 FaceONNX 계열 보조 검출로 재검증하는 경로입니다. 실행 증거가 불완전하면 자동 내보내기 gate가 실패할 수 있습니다.

## GPU / 실행 공급자

### Windows

- ONNX Runtime DirectML 패키지 사용
- `onnxruntime.dll`
- `onnxruntime_providers_shared.dll`
- `DirectML.dll`

필요한 native DLL은 NuGet 또는 저장소의 fallback native 파일에서 publish 디렉터리로 복사됩니다.

### macOS

- CPU ONNX Runtime 기본 사용 가능
- FaceONNX는 GPU 옵션 사용 시 CoreML provider를 시도할 수 있음
- YOLO는 **YOLO CoreML** 옵션을 켠 경우에만 CoreML provider 시도
- CoreML 사용 불가 시 CPU 경로 사용
- OpenMP가 필요한 native 경로를 위해 `libomp` 사용

실제 GPU 사용 여부는 드라이버, ONNX Runtime provider, 모델 호환성에 따라 달라질 수 있습니다.

## 상태 저장과 복구

FaceShield는 `Environment.SpecialFolder.LocalApplicationData` 아래의 `FaceShield` 디렉터리를 사용합니다.

~~~text
FaceShield/
├─ state.json
├─ state.json.bak
├─ Models/
│  └─ Yolo/
├─ workspaces/
│  └─ <video-path-hash>/
└─ Logs/
~~~

저장 대상:

- 최근 영상
- 자동 분석 설정
- YOLO v5/v8 개별 프로필
- 현재 워크스페이스 위치
- 프레임별 얼굴/마스크 정보
- 자동 처리 resume 상태
- 자동 내보내기 gate 상태

`state.json` 저장은 temp 파일과 flush를 사용하고 기존 상태를 `state.json.bak`으로 보존합니다. 기본 상태 파일이 손상되면 backup 로드를 시도합니다.

워크스페이스 데이터는 generation 기반 디렉터리를 사용합니다. 삭제할 때는 primary state와 backup이 모두 해당 workspace를 더 이상 참조하지 않는 상태가 된 뒤 실제 디렉터리를 제거합니다. backup 동기화에 실패하면 데이터 손실보다 orphan 디렉터리를 남기는 쪽을 선택합니다.

## 내보내기

FaceShield 내보내기는 FFmpeg 기반입니다.

주요 정책:

- 입력 해상도 유지
- 프레임별 마스크를 원본 영상 프레임에 적용
- 영상 프레임 수와 인코더 출력 coverage 검사
- PTS/DTS 순서 및 누락 검사
- 오디오 stream copy 또는 필요한 경우 AAC transcode
- presentation/container metadata 복사
- 색 공간, color range, chroma location 보존 검사
- static HDR mastering/CLL metadata 보존 가능한 경로 검사
- 출력 완료 전 trailer 및 파일 close 검증
- staging 파일에 먼저 기록 후 성공 시 최종 경로 확정

영상 품질을 조용히 떨어뜨리는 것보다 내보내기를 중단하는 정책을 우선합니다.

### 현재 의도적으로 중단하는 대표 입력

다음 입력은 원본 속성을 안전하게 보존할 수 없다고 판단되면 내보내기를 중단할 수 있습니다.

- 인터레이스 영상
- 원본 그대로 보존할 수 없는 dynamic HDR metadata
- 프로그램 단위 스트림 구성
- IAMF 등 지원되지 않는 stream group 구조
- 영상 도중 해상도 또는 픽셀 형식이 변경되는 경우
- frame/packet coverage 손실
- 유효하지 않은 PTS/DTS 순서

현재 hybrid copy window 구현은 코드에 존재하지만 production 정책에서는 비활성화되어 있으며 안전한 full encode 경로가 기본입니다.

## 개발 환경

### 저장소 받기

~~~bash
git clone https://github.com/doanythingK/FaceShield_.git
cd FaceShield_
git switch main
~~~

### 필수 SDK

.NET 8 SDK가 필요합니다.

~~~bash
dotnet --info
dotnet restore FaceShield.sln
dotnet build FaceShield.sln
~~~

별도의 테스트 `.csproj`는 현재 없습니다. 현재 자동 Quality Gate는 conventional unit test가 아니라 Windows/macOS Release **컴파일·빌드 검증**입니다.

### Windows 개발 실행

Windows에서는 저장소의 FFmpeg bundle과 DirectML/ONNX native DLL을 사용합니다.

~~~powershell
dotnet restore FaceShield.csproj -r win-x64
dotnet build FaceShield.csproj -c Debug -r win-x64
dotnet run --project FaceShield.csproj
~~~

필요한 경우 FFmpeg bundle을 풉니다.

~~~powershell
New-Item -ItemType Directory -Force FFmpeg/win-x64 | Out-Null
tar -xzf FFmpeg/win-x64-binaries.tar.gz -C FFmpeg/win-x64
~~~

### macOS 개발 실행

Homebrew 의존성:

~~~bash
brew install ffmpeg libomp srt
~~~

현재 Mac 아키텍처에 맞게 FFmpeg dylib를 준비합니다.

~~~bash
bash scripts/prepare-ffmpeg-osx.sh

# 명시 지정
bash scripts/prepare-ffmpeg-osx.sh osx-arm64
bash scripts/prepare-ffmpeg-osx.sh osx-x64
~~~

그 다음 실행합니다.

~~~bash
dotnet restore FaceShield.csproj
dotnet build FaceShield.csproj
dotnet run --project FaceShield.csproj
~~~

## 주요 NuGet 패키지

| 패키지 | 버전 |
| --- | --- |
| Avalonia | 11.3.9 |
| Avalonia.Desktop | 11.3.9 |
| CommunityToolkit.Mvvm | 8.2.1 |
| FaceONNX | 4.1.1.3 |
| FaceONNX.Addons | 4.1.1.3 |
| FFmpeg.AutoGen | 8.0.0 |
| Microsoft.ML.OnnxRuntime | 1.23.2 |
| Microsoft.ML.OnnxRuntime.DirectML | 1.23.0 |
| Microsoft.AI.DirectML | 1.15.4 |
| SixLabors.ImageSharp | 3.1.12 |

## 프로젝트 구조

~~~text
FaceShield_
├─ ViewModels/
│  └─ Pages/
│     ├─ HomePageViewModel.cs
│     └─ WorkspaceViewModel.cs
├─ Views/
│  └─ Pages/
├─ Services/
│  ├─ Analysis/          # 자동 검출, tracking, post-process
│  ├─ FaceDetection/     # detector backend와 ONNX 실행
│  ├─ Models/            # YOLO 모델 다운로드/프로필
│  ├─ Video/             # decode, timeline, mask, export
│  └─ Workspace/         # 상태 저장, export gate, run signature
├─ Models/
├─ FFmpeg/
├─ Native/
├─ scripts/
├─ .github/workflows/
├─ FaceShield.csproj
└─ FaceShield.sln
~~~

Video export는 큰 단일 서비스에 모든 정책을 두지 않고 timing, HDR, audio transcode, encoder selection/quality, packet integrity, frame processing, presentation metadata 등의 정책 클래스로 분리되어 있습니다.

## 명령줄 시작 옵션

GUI 실행 시 일부 시작 상태를 지정할 수 있습니다.

~~~bash
dotnet run --project FaceShield.csproj --   --video /absolute/path/input.mp4   --detector faceonnx   --open-auto   --no-auto-export   --auto-processing-mode tracked   --frame 12
~~~

YOLO 예시:

~~~bash
dotnet run --project FaceShield.csproj --   --video /absolute/path/input.mp4   --detector yolo   --yolo-model-type yolov8   --yolo-model /absolute/path/model.onnx   --open-auto
~~~

지원 옵션:

| 옵션 | 설명 |
| --- | --- |
| `--video <path>` | 입력 영상 |
| `--detector faceonnx\|yolo` | 시작 검출기 |
| `--yolo-model-type yolo5\|yolov8` | YOLO 모델 프로필 |
| `--yolo-model <path>` | YOLO ONNX 모델 |
| `--open-manual` | 수동 워크스페이스 |
| `--open-auto` | 자동 워크스페이스 |
| `--auto-export` | 자동 처리 후 저장 |
| `--no-auto-export` | 자동 처리 후 워크스페이스 검토 |
| `--auto-processing-mode legacy\|raw\|tracked\|full` | 자동 처리 모드 |
| `--yolo-risk-cascade` | YOLO risk cascade 켜기 |
| `--no-yolo-risk-cascade` | YOLO risk cascade 끄기 |
| `--frame <index>` | 시작 프레임, 0부터 시작 |

`--yolo-smoke`는 저장소 내부 검증용 경로가 실제로 존재할 때만 유효한 개발 옵션입니다.

현재 CLI parser는 SCRFD/YuNet 선택 문자열을 제공하지 않습니다.

## Release 빌드

### Windows x64

~~~powershell
New-Item -ItemType Directory -Force FFmpeg/win-x64 | Out-Null
tar -xzf FFmpeg/win-x64-binaries.tar.gz -C FFmpeg/win-x64

dotnet restore FaceShield.csproj -r win-x64
dotnet publish FaceShield.csproj -c Release -r win-x64 --self-contained true
~~~

출력:

~~~text
bin/Release/net8.0/win-x64/publish/
~~~

publish에는 `FaceShield.exe`, FFmpeg DLL, ONNX Runtime, DirectML native 파일이 함께 있어야 합니다.

### macOS Apple Silicon

~~~bash
brew install ffmpeg libomp srt
bash scripts/prepare-ffmpeg-osx.sh osx-arm64
dotnet publish FaceShield.csproj -c Release -r osx-arm64 --self-contained true
~~~

### macOS Intel

~~~bash
brew install ffmpeg libomp srt
bash scripts/prepare-ffmpeg-osx.sh osx-x64
dotnet publish FaceShield.csproj -c Release -r osx-x64 --self-contained true
~~~

macOS publish 결과는 다음 경로입니다.

~~~text
bin/Release/net8.0/<rid>/publish/
~~~

macOS는 native dylib 배치를 위해 single-file publish를 사용하지 않습니다.

## GitHub Actions

### Hardening Quality Gate

`.github/workflows/quality-gate.yml`

자동 실행:

- `main` push
- `hardening/**` push
- `main` 대상 pull request

검증 항목:

- Windows `win-x64` Release restore/build
- macOS `osx-arm64` Release restore/build

이 workflow는 현재 `dotnet test`를 실행하지 않습니다.

### Build Windows App

`.github/workflows/windows-build.yml`

`workflow_dispatch` 수동 실행입니다.

- `win-x64` self-contained publish
- FFmpeg / ONNX Runtime / DirectML native 파일 검사
- `FaceShield-win-x64` artifact 생성

### Build macOS App

`.github/workflows/macos-build.yml`

`workflow_dispatch` 수동 실행입니다.

- `osx-arm64` on `macos-15`
- `osx-x64` on `macos-15-intel`
- Homebrew FFmpeg/libomp/srt 설치
- `.app` bundle 생성
- dylib 수집
- ad-hoc codesign
- ZIP artifact 업로드

## 배포본 실행

### Windows

1. `FaceShield-win-x64` artifact ZIP을 풉니다.
2. native DLL을 이동하거나 삭제하지 않습니다.
3. `FaceShield.exe`를 실행합니다.

### macOS

1. CPU에 맞는 `FaceShield-osx-arm64` 또는 `FaceShield-osx-x64` ZIP을 풉니다.
2. `FaceShield.app`을 원하는 위치로 이동합니다.
3. Gatekeeper가 차단하면 ZIP에 포함된 `macos-sign-local.command`를 사용할 수 있습니다.

~~~bash
bash macos-sign-local.command /path/to/FaceShield.app
~~~

시작 로그:

~~~bash
bash macos-run-log.command /path/to/FaceShield.app
~~~

## 로그와 문제 진단

예기치 않은 시작 오류는 LocalApplicationData 아래 `FaceShield/crash.log`에 기록될 수 있습니다.

run metrics는 `FaceShield/Logs/` 아래에 기록됩니다. 내보내기 문제를 확인할 때 다음 지표가 유용합니다.

- `droppedVideoPackets`
- `videoFrameDropCount`
- `outputFrames`
- `outputPackets`
- `hybridCopyFallbackReason`
- `packetLossFallbackReason`
- scene cut / gap fill 관련 지표

로그에는 입력 파일 경로 등 개인 정보가 포함될 수 있으므로 외부 공유 전에 확인하십시오.

## 검증 스크립트

`scripts/`에는 export, HDR, VFR, container, mask, tracking, YOLO 품질 검토 등을 위한 PowerShell/bash 검증 스크립트가 있습니다.

기본 소스 검증:

~~~bash
dotnet restore FaceShield.csproj
dotnet build FaceShield.csproj -c Release
git diff --check
~~~

RID별 Quality Gate와 동일한 형태:

~~~bash
dotnet restore FaceShield.csproj -r osx-arm64
dotnet build FaceShield.csproj -c Release -r osx-arm64 --no-restore -p:ContinuousIntegrationBuild=true
~~~

Windows:

~~~powershell
dotnet restore FaceShield.csproj -r win-x64
dotnet build FaceShield.csproj -c Release -r win-x64 --no-restore -p:ContinuousIntegrationBuild=true
~~~

native publish 검증에는 `scripts/verify-native-publish.ps1`을 사용할 수 있습니다.

## 현재 검증 수준과 한계

현재 GitHub Quality Gate가 확인하는 것은 **컴파일/Release build 성공**입니다.

다음은 별도 검증이 필요합니다.

- 실제 Windows/macOS GUI 시작
- DirectML/CoreML provider 실제 로드
- 긴 영상의 메모리 사용
- 다양한 컨테이너/코덱 재생 호환성
- 실제 얼굴 검출 정확도
- 작은 얼굴, 측면 얼굴, 가림, 조명 변화에 대한 recall
- 내보낸 결과의 시각적 품질

특히 얼굴 검출 정확도 수치는 라벨된 ground-truth 영상 데이터셋 없이 신뢰할 수 있게 산출할 수 없습니다. 저장소의 스크립트나 build PASS만으로 실제 accuracy를 주장하지 않습니다.

## 관련 문서

- `Models/Yolo/README.md` — 로컬 YOLO 모델 파일 규칙
- `AUTO_MOSAIC_QUALITY_SPEED_PLAN.md` — 자동 모자이크 품질/속도 설계
- `PERFORMANCE_REFACTOR_NOTES.md` — 성능 리팩터링 기록
- `scripts/VIDEO_EXPORT_QUALITY_GATE.md` — 영상 내보내기 검증 기준
- `FUTURE_FEATURE_ROADMAP.md` — 향후 기능 기록
- YOLO 관련 smoke/GT/review 문서는 별도 검출 품질 검토용이며 전체 앱 사용 설명서를 대체하지 않습니다.

## 개발 시 주의사항

- 원본 영상 파일은 직접 수정하지 않습니다.
- 모델 가중치는 저장소에 커밋하지 않습니다.
- native FFmpeg/ONNX Runtime 파일은 RID가 맞아야 합니다.
- macOS/Windows 간 publish native 파일을 혼합하지 않습니다.
- export integrity guard를 단순히 우회해 오류를 숨기지 않습니다.
- 상태 저장 구조 변경 시 `state.json.bak`과 generation cleanup을 함께 고려합니다.
- 현재 `main`을 운영 기준으로 사용합니다.
