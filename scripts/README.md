# Scripts

이 디렉터리는 FaceShield의 **개발·검증 도구**를 보관합니다. 일부 스크립트는 서로의 고정 경로를 참조하므로 단순히 하위 폴더로 이동하지 않습니다.

## Platform / native preparation

- `prepare-ffmpeg-osx.sh`: Homebrew FFmpeg dylib를 현재 macOS RID용 `FFmpeg/<rid>/`에 준비합니다.
- `collect-macos-dylibs.sh`: publish된 `.app`에 필요한 dylib 의존성을 수집합니다.
- `resolve-yolo-model-path.ps1`: 로컬/다운로드된 YOLO 모델 경로를 해석합니다.

## Core regression verification

`verify-*.ps1` 중 video/export/native/VFR/face-track 계열은 영상 처리 회귀를 확인하는 개발용 검증 도구입니다. 예:

- `verify-video-export-quality.ps1`
- `verify-native-publish.ps1`
- `verify-vfr-frame-extractor-ordinal.ps1`
- `verify-vfr-mask-ordinal.ps1`
- `verify-face-track-postprocess.ps1`
- `verify-face-track-scene-cut-guard.ps1`
- `verify-blur-render-consistency.ps1`

실제 파일을 사용하는 검증은 원본 영상과 FFmpeg 환경이 필요할 수 있습니다.

## YOLO quality / evidence tooling

다음 prefix의 스크립트는 YOLO 품질 검증과 수동 review evidence 생성에 사용됩니다.

- `new-yolo-*`
- `verify-yolo-*`
- `write-yolo-*`
- `invoke-yolo-*`
- `compare-yolo-*`
- `run-yolo-*`

이 도구들은 다음 루트 문서/상태 파일을 읽는 경우가 있습니다.

- `AUTO_MOSAIC_QUALITY_SPEED_PLAN.md`
- `YOLO_GUI_SMOKE_RESULT.md`
- `YOLO_PROBLEM_SPAN_VERIFICATION.md`

따라서 해당 파일이나 스크립트를 이동할 때는 참조 경로를 먼저 검색해야 합니다.

## Temporary output

스크립트가 만드는 실행 결과, 샘플 영상, review package, 로그는 가능한 한 `.tmp/` 아래에 생성하고 Git에 커밋하지 않습니다.

## Cleanup rule

스크립트를 삭제하기 전 최소한 다음을 확인합니다.

1. `.github/workflows/`에서 호출하지 않는지
2. 다른 `scripts/` 파일에서 경로를 참조하지 않는지
3. README 또는 docs에서 사용 절차로 안내하지 않는지
4. source/runtime 계약을 검증하는 유일한 regression harness가 아닌지

단순히 파일 수가 많다는 이유만으로 검증 스크립트를 제거하지 않습니다.
