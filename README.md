# FaceShield

FaceShield는 Windows와 macOS에서 동작하는 Avalonia 기반 영상 모자이크 편집기입니다. 자동 얼굴 검출, 프레임별 마스크 보정, 원본 해상도 기준 영상 내보내기를 지원합니다.

자동 검출은 기본적으로 FaceONNX 경로를 사용하며, 선택적으로 YOLO Face ONNX를 사용할 수 있습니다. 검출 속도보다 화질·검출 연속성·출력 프레임 보존을 우선하는 구조이므로, 설정 변경 후에는 짧은 영상으로 결과를 먼저 확인하는 것을 권장합니다.

## 지원 환경

| 구분 | 지원 대상 | 비고 |
| --- | --- | --- |
| Windows | win-x64 | DirectML 및 Windows용 FFmpeg/ONNX native 파일 포함 |
| macOS Apple Silicon | osx-arm64 | macOS 15 GitHub runner 및 Apple Silicon 배포 대상 |
| macOS Intel | osx-x64 | macOS 15 Intel GitHub runner 및 Intel Mac 배포 대상 |
| 개발 SDK | .NET 8 SDK | net8.0, Avalonia 11.3.9 |

macOS에서 소스 빌드하거나 native 라이브러리를 준비할 때는 Homebrew와 다음 패키지가 필요합니다.

~~~bash
brew install ffmpeg libomp srt
~~~

Windows와 macOS 모두 64비트 환경을 전제로 합니다. Linux에서 소스 컴파일이 될 수는 있지만, 현재 배포 대상과 native 실행 검증 범위에는 포함되지 않습니다.

## 설치 및 개발 실행

### 1. 저장소 준비

~~~bash
git clone https://github.com/doanythingK/FaceShield_.git
cd FaceShield_
dotnet restore FaceShield.sln
~~~

### 2. 개발 빌드

~~~bash
dotnet build FaceShield.sln
~~~

### 3. 실행

~~~bash
dotnet run --project FaceShield.csproj
~~~

Windows 개발 실행에서는 저장소의 FFmpeg/win-x64 DLL과 DirectML/ONNX native 파일을 사용합니다. macOS에서 직접 실행할 때는 현재 Mac 아키텍처에 맞는 FFmpeg dylib를 먼저 준비합니다.

~~~bash
# 현재 Mac 아키텍처 자동 감지
bash scripts/prepare-ffmpeg-osx.sh

# 또는 명시적으로 지정
bash scripts/prepare-ffmpeg-osx.sh osx-arm64  # Apple Silicon
bash scripts/prepare-ffmpeg-osx.sh osx-x64    # Intel

dotnet run --project FaceShield.csproj
~~~

## 기본 사용법

1. 홈 화면에서 영상 선택으로 입력 영상을 선택합니다.
2. 모자이크 강도를 조절합니다. 기본값은 28이며 허용 범위는 6~40입니다.
3. 자동 검출을 사용할 경우 자동 모자이크 시작, 직접 마스크를 만들 경우 직접 편집을 선택합니다.
4. 워크스페이스에서 검출 결과와 문제 프레임을 확인합니다.
5. 필요한 경우 브러시·지우개로 마스크를 보정하고 저장을 눌러 내보냅니다.

### 자동 저장 체크박스

완료되면 원본 영상 폴더에 저장은 자동 모자이크 완료 후 동작을 결정합니다.

- 체크함: 자동 검출·후처리 후 원본 영상 폴더에 바로 _blur 출력 파일을 만들고 홈 화면에 남습니다.
- 체크하지 않음: 자동 검출 결과를 저장한 뒤 워크스페이스로 이동하여 검토·보정할 수 있습니다. 최종 출력은 워크스페이스의 저장 버튼으로 실행합니다.

자동 처리 중 취소하면 진행 상태와 마스크가 저장되며, 같은 영상을 다시 열 때 재개 여부를 선택할 수 있습니다. 설정과 최근 프로젝트도 자동 저장됩니다.

### 워크스페이스

- 검출 표시: 현재 프레임의 검출 결과 표시/숨김
- 문제 프레임: 얼굴 없음, 신뢰도 낮음, 연속 끊김 목록 확인
- 이전 이상 프레임 / 다음 이상 프레임: 검토 대상 프레임 이동
- 브러시: 마스크 추가
- 지우개: 마스크 제거
- 되돌리기: 최근 편집 취소
- 저장: 최종 영상 내보내기

### 단축키

- Q / E: 이전·다음 이상 프레임
- ← / →: 한 프레임 이동
- Shift + ← / →: 10프레임 이동
- ↑ / ↓: 1초 이동
- Home / End: 처음·끝 프레임
- Space: 재생·정지

출력 파일은 입력 영상과 같은 폴더에 <원본이름>_blur<확장자>로 생성됩니다. 같은 이름이 이미 있으면 덮어쓰기 또는 고유 이름 생성을 선택하는 대화상자가 표시됩니다. 영상 해상도는 입력 해상도를 기준으로 유지됩니다.

## 자동 검출 설정

홈 화면의 상세 설정에서 자동 검출기를 선택할 수 있습니다. 설정은 LocalApplicationData/FaceShield/state.json에 저장되어 다음 실행에도 복원됩니다.

### 검출 모델

- FaceONNX: 기본 검출 경로입니다. Windows에서는 DirectML을 시도하고, macOS에서는 ONNX Runtime의 CoreML 경로를 시도할 수 있으며 실패 시 CPU로 전환합니다.
- YOLO Face ONNX: 아래 YOLO 모델을 선택하여 사용합니다. 모델 파일은 저장소에 포함하지 않습니다.

### YOLO 모델 준비

YOLO를 선택한 뒤 홈 화면의 YOLO 모델 다운로드를 사용하거나 찾기로 직접 .onnx 파일을 지정합니다. 다운로드 모델은 다음 논리 경로에 저장됩니다.

~~~text
<LocalApplicationData>/FaceShield/Models/Yolo/
~~~

일반적인 실제 경로 예시는 다음과 같습니다.

- Windows: %LOCALAPPDATA%/FaceShield/Models/Yolo
- macOS: ~/Library/Application Support/FaceShield/Models/Yolo (OS/.NET 환경에 따라 달라질 수 있음)

지원되는 기본 파일명은 다음과 같습니다.

~~~text
YoloV5Face.onnx
Yolo5Face.onnx
yolov8n-face-lindevs.onnx
yolov8s-face-lindevs.onnx
yolov8m-face-lindevs.onnx
yolov8l-face-lindevs.onnx
~~~

내장 다운로드 출처와 라이선스 표시는 다음과 같습니다.

| 모델 | 다운로드 출처 | 코드에 표시된 라이선스 안내 |
| --- | --- | --- |
| YoloV5Face.onnx | Hugging Face hayashiLin/deepfacelivemodels | GPL-3.0 표시 |
| yolov8n-face-lindevs.onnx | GitHub lindevs/yolov8-face 1.0.1 | MIT 표시 및 YOLOv8 upstream caveat |

모델의 배포·상업적 사용 조건은 각 upstream 저장소와 모델 파일의 라이선스를 별도로 확인해야 합니다. 모델 가중치는 저장소에 커밋하지 않습니다.

### 분석 모드

| 모드 | 동작 |
| --- | --- |
| 자동 안정화 (권장) | 짧은 검출 누락만 연결하며 전체 후처리는 사용하지 않음 |
| 검출 결과 그대로 | 매 프레임 검출만 수행하고 추적·보간·후처리를 사용하지 않음 |
| 전체 보정 | 추적과 후처리 모듈을 모두 사용 |
| 이전 설정 호환 | 기존의 추적/후처리 체크박스 조합을 그대로 사용 |

### 품질·속도 옵션

- 검출 해상도: 100% (원본), 75%, 50%, 33%
- 축소 품질: 빠름(최근접) 또는 균형(보간)
- 검출 간격: 1, 2, 3, 5프레임. 1은 매 프레임 검출입니다.
- 병렬 세션: 1~4개, 기본값 2. 세션 수를 늘리면 속도가 빨라질 수 있지만 메모리 사용량과 발열이 증가할 수 있습니다.
- ONNX Runtime 최적화: 기본 활성화
- GPU 가속: Windows/macOS 첫 실행 기본값은 활성화되며, DirectML/CoreML을 사용할 수 없으면 CPU로 전환합니다.
- ONNX 스레드: 자동 또는 시스템에서 제공하는 스레드 수
- YOLO 입력 크기: 기본 640, UI 범위 64~2048
- 타일 검출: 작은 얼굴을 위한 분할 검출. 기본적으로 꺼져 있으며, 활성화 시 타일 구성과 겹침 비율을 조정합니다.

품질 우선 시작점은 100% (원본), 균형(보간), 검출 간격 1, 병렬 세션 2, ONNX Runtime 최적화 활성화입니다. 해상도 축소나 검출 간격 증가는 영상별로 누락·연속성에 영향을 줄 수 있으므로 결과를 확인한 뒤 적용해야 합니다.

### YOLO 후처리 및 실험 기능

전체 보정 또는 이전 설정 호환 모드에서 다음 후처리 기능을 선택할 수 있습니다.

- ROI 재검증
- 약한 단일 오탐 제거
- 짧은 누락 구간 보강
- 장면 전환 잔상 정리
- 시간축 흔들림 완화

위험 프레임 FaceONNX 재검출은 YOLO 후보를 보수적으로 재검증하는 실험 기능입니다. YOLO CoreML (macOS)도 선택 기능이며 기본적으로 꺼져 있습니다. CoreML의 실제 품질·속도는 Mac 모델과 영상 특성에 따라 별도 확인이 필요합니다.

## 명령줄 시작 옵션

GUI를 열 때 영상·검출기·워크스페이스 모드를 미리 지정할 수 있습니다.

~~~bash
dotnet run --project FaceShield.csproj -- \
  --video /absolute/path/input.mp4 \
  --detector yolo \
  --yolo-model-type yolov8 \
  --yolo-model /absolute/path/yolov8n-face-lindevs.onnx \
  --open-auto \
  --no-auto-export \
  --auto-processing-mode tracked \
  --frame 12
~~~

지원 옵션:

| 옵션 | 설명 |
| --- | --- |
| --video <path> | 입력 영상 지정 |
| --detector faceonnx\|yolo | 검출기 선택 |
| --yolo-model-type yolo5\|yolov8 | YOLO 모델 종류 |
| --yolo-model <path> | YOLO ONNX 파일 지정 |
| --open-manual / --open-auto | 시작 시 워크스페이스 모드 지정 |
| --auto-export / --no-auto-export | 자동 완료 후 즉시 내보내기 여부 |
| --auto-processing-mode legacy\|raw\|tracked\|full | 자동 분석 모드 |
| --yolo-risk-cascade / --no-yolo-risk-cascade | 위험 프레임 FaceONNX 재검증 토글 |
| --frame <index> | 워크스페이스 시작 프레임(0부터 시작) |

검증용 --yolo-smoke 프리셋은 저장소 내부의 테스트 영상과 로컬 모델 경로가 존재할 때만 사용할 수 있습니다.

## 배포 빌드

### Windows x64

저장소에 포함된 FFmpeg 압축 번들을 먼저 풀고 self-contained publish를 실행합니다.

~~~powershell
New-Item -ItemType Directory -Force FFmpeg/win-x64 | Out-Null
tar -xzf FFmpeg/win-x64-binaries.tar.gz -C FFmpeg/win-x64
dotnet restore FaceShield.sln
dotnet publish FaceShield.csproj -c Release -r win-x64 --self-contained true
powershell -ExecutionPolicy Bypass -File scripts/verify-native-publish.ps1 -RuntimeIdentifier win-x64 -PublishDir bin/Release/net8.0/win-x64/publish
~~~

주요 출력 경로는 bin/Release/net8.0/win-x64/publish이며 FaceShield.exe, FFmpeg DLL, onnxruntime.dll, DirectML.dll 등이 포함되어야 합니다.

### macOS Apple Silicon / Intel

~~~bash
brew install ffmpeg libomp srt

# Apple Silicon
bash scripts/prepare-ffmpeg-osx.sh osx-arm64
dotnet publish FaceShield.csproj -c Release -r osx-arm64 --self-contained true

# Intel
bash scripts/prepare-ffmpeg-osx.sh osx-x64
dotnet publish FaceShield.csproj -c Release -r osx-x64 --self-contained true
~~~

macOS publish 결과는 bin/Release/net8.0/<rid>/publish에 생성됩니다. native dylib가 별도 파일로 배치되므로 macOS publish에서는 single-file 설정을 사용하지 않습니다. scripts/verify-native-publish.ps1로 libonnxruntime.dylib와 FFmpeg dylib를 검사할 수 있습니다.

.app 생성, dylib 수집, ad-hoc 서명, ZIP 패키징은 GitHub Actions의 Build macOS App workflow를 사용하는 방법이 가장 간단합니다.

## GitHub Actions 수동 빌드

현재 workflow는 자동 push/PR 실행이 아니라 workflow_dispatch 수동 실행만 사용합니다.

1. GitHub 저장소의 Actions 탭으로 이동합니다.
2. Build Windows App 또는 Build macOS App을 선택합니다.
3. Run workflow에서 대상 브랜치를 선택하고 실행합니다.
4. 완료 후 workflow의 Artifacts에서 결과 ZIP을 다운로드합니다.

macOS workflow는 다음 두 작업을 병렬로 실행합니다.

- osx-arm64 (macos-15): FaceShield-osx-arm64
- osx-x64 (macos-15-intel): FaceShield-osx-x64

Windows workflow는 windows-latest에서 win-x64 self-contained publish와 native DLL 검사를 수행하고 FaceShield-win-x64 artifact를 업로드합니다.

자동 push/PR 빌드가 필요하지 않은 저장소 운영 정책을 유지하므로, 코드 push만으로 workflow가 실행되지는 않습니다.

## 배포본 설치

현재 공식 workflow가 제공하는 배포 형식은 ZIP입니다. MSI·DMG 설치 프로그램은 제공하지 않습니다.

### Windows

1. Actions artifact에서 FaceShield-win-x64 ZIP을 다운로드합니다.
2. 원하는 폴더에 압축을 해제합니다.
3. 압축 해제 폴더의 FaceShield.exe를 실행합니다.

압축 파일에는 self-contained .NET 실행 파일과 FFmpeg, ONNX Runtime, DirectML native 파일이 함께 포함되어야 합니다. DLL을 실행 파일과 다른 폴더로 이동하면 시작 또는 영상 처리가 실패할 수 있습니다.

### macOS

1. Mac CPU에 맞는 FaceShield-osx-arm64 또는 FaceShield-osx-x64 artifact를 다운로드합니다.
2. 압축을 해제하여 FaceShield.app를 응용 프로그램 폴더 등 원하는 위치로 이동합니다.
3. Gatekeeper가 차단하면 아래 macOS 서명 절차를 실행합니다.

## macOS 앱 실행 및 서명

GitHub Actions에서 받은 ZIP을 압축 해제한 뒤 FaceShield.app를 실행합니다. 서명되지 않은 앱을 macOS Gatekeeper가 차단하면 ZIP 안의 macos-sign-local.command를 실행합니다.

~~~bash
bash macos-sign-local.command /path/to/FaceShield.app
~~~

이 스크립트는 quarantine 속성을 제거하고 ad-hoc 서명을 적용합니다. 실행 로그는 macos-sign-local.log에 기록됩니다.

앱 시작 실패 원인을 확인하려면 다음 스크립트를 사용합니다.

~~~bash
bash macos-run-log.command /path/to/FaceShield.app
~~~

실행 로그는 macos-run.log에 기록됩니다. libomp.dylib 또는 ONNX Runtime dylib 오류가 표시되면 다음을 확인합니다.

~~~bash
brew install libomp
~~~

## 상태 파일과 로그

Environment.SpecialFolder.LocalApplicationData 아래에 다음 데이터가 저장됩니다.

~~~text
FaceShield/
├─ state.json                 # 최근 영상·자동 설정·워크스페이스 메타데이터
├─ Models/Yolo/               # 앱에서 다운로드한 YOLO 모델
├─ workspaces/<path-hash>/    # 프레임 마스크와 자동 처리 재개 상태
└─ Logs/
   └─ run-metrics-<runId>.log # 자동 검출·내보내기·품질 게이트 지표
~~~

예기치 않은 시작 오류는 FaceShield/crash.log에 기록됩니다. 로그에서 다음 항목을 확인하면 프레임 누락과 내보내기 문제를 구분하는 데 도움이 됩니다.

- droppedVideoPackets
- videoFrameDropCount
- outputFrames, outputPackets
- hybridCopyFallbackReason, packetLossFallbackReason
- sampleShortGaps, samplePerFaceShortGaps, sampleMissRecovery
- sceneCut 및 gapFill 관련 지표

로그와 영상에는 입력 파일 경로가 포함될 수 있으므로 외부 공유 전에 경로와 개인정보를 확인합니다.

## 검증 명령

최소 검증:

~~~bash
dotnet restore FaceShield.sln
dotnet build FaceShield.sln
bash -n scripts/prepare-ffmpeg-osx.sh scripts/collect-macos-dylibs.sh
git diff --check
~~~

native publish 검증:

~~~powershell
powershell -ExecutionPolicy Bypass -File scripts/verify-native-publish.ps1 -RuntimeIdentifier win-x64
# macOS에서는 해당 RID를 사용
pwsh -File scripts/verify-native-publish.ps1 -RuntimeIdentifier osx-arm64
~~~

빌드·publish 성공만으로 실제 macOS 앱 시작, CoreML/DirectML 로드, 영상 품질, 재생 호환성까지 보증할 수는 없습니다. 변경 후에는 짧은 실제 영상을 열어 자동 검출, 워크스페이스 보정, 내보내기를 확인하고 다음을 점검합니다.

- 입력/출력 프레임 수 동일
- 평균 FPS와 재생시간 변화 없음
- droppedVideoPackets=0
- 출력 프레임 또는 FPS 감소 없음
- 마지막 프레임 포함 전체 구간에서 마스크 누락·잔상 없음

## 관련 문서

- Models/Yolo/README.md: 저장소 내 YOLO 모델 파일명과 배치 규칙
- YOLO_GUI_SMOKE_CHECKLIST.md: YOLO GUI 수동 smoke 절차
- YOLO_PROBLEM_SPAN_VERIFICATION.md: 문제 구간 검증 기준
- AUTO_MOSAIC_QUALITY_SPEED_PLAN.md: 자동 모자이크 품질·속도 설계 기록
- YOLO_LOW_CONFIDENCE_ROI_CASCADE_PLAN.md: YOLO 저신뢰도 후보 좌표 ROI 보조 검증 설계안
- FUTURE_FEATURE_ROADMAP.md: 예정 기능
