# FaceShield

FaceShield는 Windows와 macOS에서 동작하는 Avalonia 기반 영상 얼굴 모자이크 편집기입니다.

자동 얼굴 검출, 프레임별 마스크 검토·수정, 자동 처리 재개, 상태 복구, FFmpeg 기반 영상 내보내기를 하나의 데스크톱 앱에서 제공합니다.

현재 운영 기준 브랜치는 `main`입니다.

## 주요 기능

- 영상 선택 후 자동 모자이크 또는 직접 편집
- FaceONNX 기반 자동 얼굴 검출
- 프레임별 얼굴 박스 및 마스크 저장
- 자동 처리 중 취소 후 이어서 진행
- 얼굴 없음 / 낮은 신뢰도 / 연속성 문제 프레임 검토
- 브러시와 지우개를 이용한 수동 마스크 수정
- 최근 영상 및 워크스페이스 복원
- Windows GPU 가속 경로 및 CPU fallback
- macOS 가속 경로 및 CPU fallback
- 원본 해상도 기반 FFmpeg 내보내기
- 프레임 수, 타임스탬프, 색상, 오디오, HDR 관련 무결성 검사
- Windows/macOS self-contained 배포 workflow

## 지원 환경

| 구분 | 대상 | 현재 검증 범위 |
| --- | --- | --- |
| Windows | x64 / `win-x64` | Release build 및 Windows publish workflow |
| macOS Apple Silicon | `osx-arm64` | Release build 및 macOS app workflow |
| macOS Intel | `osx-x64` | publish workflow 제공 |
| Linux | 별도 지원 대상 아님 | 배포/실행 검증 범위 밖 |
| .NET | .NET 8 | `net8.0` |
| UI | Avalonia 11.3.9 | Desktop |
| FFmpeg binding | FFmpeg.AutoGen 8.0.0 | native FFmpeg 별도 포함 |

Windows와 macOS 모두 64비트 환경을 전제로 합니다.

## 빠른 사용법

1. FaceShield를 실행합니다.
2. 홈 화면에서 입력 영상을 선택합니다.
3. 모자이크 강도를 설정합니다.
4. 자동 처리라면 **자동 모자이크**, 직접 편집 중심이라면 **직접 편집**을 선택합니다.
5. 자동 처리 완료 후 문제 프레임을 확인합니다.
6. 필요한 프레임에서 브러시 또는 지우개로 마스크를 수정합니다.
7. 저장을 실행해 최종 영상을 내보냅니다.

기본 모자이크 강도는 28이며 UI 허용 범위는 6~40입니다.

출력 파일은 일반적으로 입력 파일과 같은 폴더에 다음 형식으로 생성됩니다.

~~~text
<원본이름>_blur<원본확장자>
~~~

같은 이름의 파일이 이미 있으면 덮어쓰기 또는 고유 이름 생성을 선택할 수 있습니다.

## 자동 모자이크

자동 모자이크는 영상을 순차적으로 분석하면서 얼굴 검출 결과를 프레임별 마스크로 저장합니다.

자동 처리 중 취소하면 이미 처리한 상태와 마스크를 저장합니다. 같은 영상을 다시 열었을 때 실행 조건이 호환되면 이어서 진행할 수 있습니다.

설정이나 실행 환경이 이전 실행과 달라졌거나 저장된 실행 증거가 불완전하면 안전을 위해 처음부터 다시 처리할 수 있습니다.

### 자동 완료 후 저장

홈 화면의 자동 저장 옵션으로 자동 처리 완료 후 동작을 선택할 수 있습니다.

- 켜짐: 자동 분석 완료 후 바로 결과 영상을 저장
- 꺼짐: 자동 분석 결과를 워크스페이스에서 확인한 뒤 수동으로 저장

## 직접 편집

직접 편집에서는 자동 검출 결과에 의존하지 않고 프레임별 마스크를 직접 수정할 수 있습니다.

워크스페이스 주요 기능:

- 현재 프레임 검출 표시/숨김
- 브러시로 마스크 추가
- 지우개로 마스크 삭제
- 최근 편집 되돌리기
- 문제 프레임 목록 확인
- 이전/다음 이상 프레임 이동
- 프레임 단위 이동
- 재생 및 정지
- 최종 영상 저장

## 문제 프레임 검토

자동 처리 후 다음 유형의 프레임을 검토할 수 있습니다.

- 얼굴이 검출되지 않은 프레임
- 신뢰도가 낮은 프레임
- 이전/다음 프레임과 연결이 불안정한 프레임
- 추적 또는 후처리 결과 확인이 필요한 프레임

최종 저장 전 문제 프레임을 확인하는 것을 권장합니다.

## 단축키

| 키 | 동작 |
| --- | --- |
| Q / E | 이전 / 다음 이상 프레임 |
| ← / → | 1프레임 이동 |
| Shift + ← / → | 10프레임 이동 |
| ↑ / ↓ | 약 1초 이동 |
| Home / End | 처음 / 마지막 프레임 |
| Space | 재생 / 정지 |

## 자동 분석 설정

### 처리 모드

| UI 이름 | 내부 모드 | 동작 |
| --- | --- | --- |
| 자동 안정화 (권장) | Tracked | 추적을 사용해 짧은 검출 누락을 연결 |
| 검출 결과 그대로 | Raw | 검출 결과 중심, 추적·보간 최소화 |
| 전체 보정 | Full | 추적 및 후처리 기능 사용 |
| 이전 설정 호환 | Legacy | 이전 버전 설정 방식 유지 |

### 검출 해상도

선택값:

- 100% (원본)
- 75%
- 50%
- 33%

해상도를 낮추면 속도와 메모리 사용량을 줄일 수 있지만 작은 얼굴 검출에는 불리할 수 있습니다.

### 축소 품질

- 빠름(최근접)
- 균형(보간)

기본값은 균형(보간)입니다.

### 검출 간격

선택값:

- 1프레임
- 2프레임
- 3프레임
- 5프레임

1은 매 프레임 검출입니다.

간격을 늘리면 속도는 빨라질 수 있지만 짧게 등장하는 얼굴의 누락 가능성이 증가할 수 있습니다.

### 병렬 세션

선택 범위:

- 1
- 2
- 3
- 4

기본값은 2입니다.

세션 수를 늘리면 처리 속도가 향상될 수 있지만 CPU/GPU 사용량, 메모리 사용량, 발열이 증가할 수 있습니다.

### ONNX Runtime 설정

지원 설정:

- ONNX Runtime 최적화
- GPU 사용 시도
- ONNX thread 수
- 자동 detector tuning 일부 옵션

GPU 실행 공급자를 사용할 수 없거나 초기화에 실패하면 CPU 경로로 전환할 수 있습니다.

### 품질 우선 권장 시작값

- 검출 해상도: 100%
- 축소 품질: 균형(보간)
- 검출 간격: 1
- 병렬 세션: 2
- ONNX Runtime 최적화: 켜짐

성능 조정은 실제 사용하는 영상으로 결과를 확인하면서 변경하는 것이 안전합니다.

## 후처리

자동 분석에는 프레임 간 연속성을 보강하거나 약한 검출 결과를 정리하는 후처리 경로가 있습니다.

대표 기능:

- ROI 기반 재검토
- 약한 단일 검출 제거
- 짧은 누락 구간 보강
- 장면 전환 경계 정리
- 시간축 흔들림 완화
- sparse tracking materialization

후처리를 많이 사용할수록 처리 결과가 부드러워질 수 있지만 영상 특성에 따라 잘못된 연결이 생길 수 있으므로 최종 검토가 필요합니다.

## GPU / 실행 환경

### Windows

Windows에서는 ONNX Runtime DirectML 경로를 사용할 수 있습니다.

publish 결과에는 다음 native 파일이 포함되어야 합니다.

~~~text
onnxruntime.dll
onnxruntime_providers_shared.dll
DirectML.dll
~~~

GPU 초기화에 실패하면 CPU 경로로 전환할 수 있습니다.

### macOS

macOS에서는 ONNX Runtime CPU 경로를 사용할 수 있으며, 실행 공급자 설정에 따라 macOS 가속 경로를 시도할 수 있습니다.

native 실행에는 FFmpeg dylib와 환경에 따라 `libomp.dylib`가 필요합니다.

## 상태 저장과 복구

FaceShield는 `Environment.SpecialFolder.LocalApplicationData` 아래의 `FaceShield` 디렉터리를 사용합니다.

~~~text
FaceShield/
├─ state.json
├─ state.json.bak
├─ workspaces/
│  └─ <video-path-hash>/
├─ Logs/
└─ crash.log
~~~

저장 대상:

- 최근 영상
- 자동 분석 설정
- 현재 워크스페이스 위치
- 프레임별 얼굴 및 마스크 정보
- 자동 처리 resume 상태
- 내보내기 gate 상태

### state.json

현재 상태를 저장합니다.

저장 시 temp 파일을 먼저 작성하고 flush한 뒤 최종 파일로 교체합니다.

### state.json.bak

이전 또는 동기화된 상태의 backup입니다.

기본 상태 파일을 읽을 수 없으면 backup 복원을 시도합니다.

### workspace generation

워크스페이스 마스크 데이터는 generation 기반 디렉터리로 저장합니다.

새 상태가 정상적으로 저장된 뒤 참조되지 않는 이전 generation을 정리합니다.

workspace 삭제 시에는 primary state와 backup이 모두 해당 데이터를 더 이상 참조하지 않는 것을 확인한 뒤 실제 디렉터리를 삭제합니다.

backup 동기화에 실패하면 데이터 손실을 피하기 위해 orphan 디렉터리를 남기는 쪽을 선택합니다.

## 내보내기

FaceShield의 영상 저장은 FFmpeg 기반입니다.

주요 정책:

- 입력 영상 해상도 유지
- 프레임별 마스크를 원본 프레임에 적용
- 영상 프레임 coverage 검사
- PTS / DTS 누락 및 순서 검사
- 오디오 stream 처리
- container 및 stream metadata 복사
- 색 공간 및 color range 보존 검사
- chroma location 보존 검사
- static HDR metadata 보존 가능 여부 검사
- staging 파일에 먼저 기록
- trailer 기록 및 output close 완료 후 최종 파일 확정

영상 품질이나 프레임 무결성을 조용히 손상시키는 것보다 내보내기를 중단하는 정책을 우선합니다.

### 내보내기가 중단될 수 있는 입력

다음과 같은 입력은 원본 특성을 안전하게 유지할 수 없다고 판단되면 내보내기를 중단할 수 있습니다.

- 인터레이스 영상
- 안전하게 보존할 수 없는 dynamic HDR metadata
- 지원되지 않는 복잡한 stream group 구조
- 프로그램 단위 stream 구성
- 영상 도중 해상도 변경
- 영상 도중 pixel format 변경
- frame/packet coverage 손실
- 유효하지 않은 PTS/DTS 순서

원본 영상 파일 자체는 수정하지 않습니다.

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

현재 별도의 conventional unit test 프로젝트는 없습니다.

GitHub Quality Gate는 현재 Windows/macOS Release 컴파일·빌드를 검증합니다.

## Windows 개발 환경

### 기본 빌드

~~~powershell
dotnet restore FaceShield.csproj -r win-x64
dotnet build FaceShield.csproj -c Debug -r win-x64
~~~

### 실행

~~~powershell
dotnet run --project FaceShield.csproj
~~~

### FFmpeg 준비

필요한 경우 저장소의 FFmpeg bundle을 풉니다.

~~~powershell
New-Item -ItemType Directory -Force FFmpeg/win-x64 | Out-Null
tar -xzf FFmpeg/win-x64-binaries.tar.gz -C FFmpeg/win-x64
~~~

Windows 실행/배포에서는 FFmpeg DLL과 ONNX Runtime/DirectML native 파일이 실행 파일 근처에 있어야 합니다.

## macOS 개발 환경

### Homebrew 의존성

~~~bash
brew install ffmpeg libomp srt
~~~

### FFmpeg dylib 준비

현재 Mac 아키텍처 자동 선택:

~~~bash
bash scripts/prepare-ffmpeg-osx.sh
~~~

Apple Silicon:

~~~bash
bash scripts/prepare-ffmpeg-osx.sh osx-arm64
~~~

Intel:

~~~bash
bash scripts/prepare-ffmpeg-osx.sh osx-x64
~~~

### 빌드 및 실행

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
| Avalonia.Themes.Fluent | 11.3.9 |
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
│  ├─ Analysis/
│  ├─ FaceDetection/
│  ├─ Video/
│  └─ Workspace/
├─ Models/
├─ FFmpeg/
├─ Native/
├─ scripts/
├─ .github/
│  └─ workflows/
├─ FaceShield.csproj
└─ FaceShield.sln
~~~

### 주요 책임

`Services/Analysis`

- 자동 얼굴 검출 흐름
- tracking
- sparse materialization
- scene cut guard
- 후처리

`Services/FaceDetection`

- detector abstraction
- FaceONNX detector
- ONNX Runtime 실행 공급자 관리
- threshold 및 auto tuning

`Services/Video`

- 영상 decode
- timeline
- frame mask
- 색상 처리
- encoder 선택
- audio transcode
- HDR metadata
- packet/timestamp 무결성
- 최종 export

`Services/Workspace`

- 상태 저장
- workspace generation
- 실행 signature
- export gate

## 명령줄 시작 옵션

GUI 실행 시 일부 시작 상태를 지정할 수 있습니다.

~~~bash
dotnet run --project FaceShield.csproj --   --video /absolute/path/input.mp4   --detector faceonnx   --open-auto   --no-auto-export   --auto-processing-mode tracked   --frame 12
~~~

일반 사용 문서에서 다루는 옵션:

| 옵션 | 설명 |
| --- | --- |
| `--video <path>` | 입력 영상 |
| `--detector faceonnx` | 기본 검출기 지정 |
| `--open-manual` | 수동 워크스페이스 시작 |
| `--open-auto` | 자동 워크스페이스 시작 |
| `--auto-export` | 자동 처리 후 바로 저장 |
| `--no-auto-export` | 자동 처리 후 워크스페이스 검토 |
| `--auto-processing-mode legacy\|raw\|tracked\|full` | 자동 처리 모드 |
| `--frame <index>` | 시작 프레임, 0부터 시작 |

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

주요 포함 파일:

- FaceShield.exe
- FFmpeg DLL
- ONNX Runtime DLL
- DirectML DLL

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

출력:

~~~text
bin/Release/net8.0/<rid>/publish/
~~~

macOS는 native dylib 배치를 위해 single-file publish를 사용하지 않습니다.

## GitHub Actions

### Hardening Quality Gate

파일:

~~~text
.github/workflows/quality-gate.yml
~~~

자동 실행:

- `main` push
- `hardening/**` push
- `main` 대상 pull request

검증:

- Windows `win-x64` Release restore/build
- macOS `osx-arm64` Release restore/build

현재 `dotnet test`는 실행하지 않습니다.

### Build Windows App

파일:

~~~text
.github/workflows/windows-build.yml
~~~

수동 `workflow_dispatch` 실행입니다.

작업:

- `win-x64` self-contained publish
- FFmpeg native 파일 확인
- ONNX Runtime/DirectML native 파일 확인
- ZIP artifact 생성

### Build macOS App

파일:

~~~text
.github/workflows/macos-build.yml
~~~

수동 `workflow_dispatch` 실행입니다.

대상:

- `osx-arm64`
- `osx-x64`

작업:

- Homebrew 의존성 설치
- FFmpeg dylib 준비
- self-contained publish
- `.app` bundle 생성
- dylib 수집
- ad-hoc codesign
- ZIP artifact 생성

## 배포본 실행

### Windows

1. Windows artifact ZIP을 풉니다.
2. 포함된 native DLL을 삭제하거나 다른 폴더로 이동하지 않습니다.
3. `FaceShield.exe`를 실행합니다.

### macOS

1. CPU 아키텍처에 맞는 ZIP을 풉니다.
2. `FaceShield.app`을 원하는 위치로 이동합니다.
3. Gatekeeper가 차단하면 제공된 local signing script를 사용할 수 있습니다.

~~~bash
bash macos-sign-local.command /path/to/FaceShield.app
~~~

시작 문제 로그:

~~~bash
bash macos-run-log.command /path/to/FaceShield.app
~~~

## 로그와 문제 진단

예기치 않은 시작 오류는 LocalApplicationData 아래 `FaceShield/crash.log`에 기록될 수 있습니다.

run metrics는 `FaceShield/Logs/` 아래에 기록됩니다.

내보내기 문제 확인 시 유용한 항목:

- `droppedVideoPackets`
- `videoFrameDropCount`
- `outputFrames`
- `outputPackets`
- `packetLossFallbackReason`
- scene cut 관련 지표
- gap fill 관련 지표

로그에는 입력 파일 경로가 포함될 수 있으므로 외부 공유 전에 확인해야 합니다.

## 검증

기본 빌드:

~~~bash
dotnet restore FaceShield.csproj
dotnet build FaceShield.csproj -c Release
~~~

macOS Quality Gate와 동일한 형태:

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

현재 자동 Quality Gate가 확인하는 것은 **컴파일 및 Release build 성공**입니다.

다음은 별도 검증이 필요합니다.

- 실제 Windows/macOS GUI 시작
- GPU 실행 공급자 실제 로드
- 긴 영상 처리 중 메모리 사용
- 다양한 컨테이너와 코덱 호환성
- 실제 얼굴 검출 정확도
- 작은 얼굴, 측면 얼굴, 가림, 조명 변화에 대한 검출 성능
- 내보낸 결과의 시각적 품질

실제 검출 정확도 수치는 라벨된 ground-truth 영상 데이터셋 없이 신뢰할 수 있게 산출할 수 없습니다.

build PASS만으로 실제 정확도나 영상 품질을 보증하지 않습니다.

## 관련 문서

- `AUTO_MOSAIC_QUALITY_SPEED_PLAN.md`
- `PERFORMANCE_REFACTOR_NOTES.md`
- `scripts/VIDEO_EXPORT_QUALITY_GATE.md`
- `FUTURE_FEATURE_ROADMAP.md`

## 개발 시 주의사항

- 원본 영상 파일은 직접 수정하지 않습니다.
- native FFmpeg/ONNX Runtime 파일은 대상 RID와 일치해야 합니다.
- macOS와 Windows용 native 파일을 섞지 않습니다.
- export integrity guard를 임의로 우회해 오류를 숨기지 않습니다.
- 상태 저장 구조를 바꿀 때 backup과 generation cleanup을 함께 고려합니다.
- 현재 `main`을 운영 기준으로 사용합니다.
