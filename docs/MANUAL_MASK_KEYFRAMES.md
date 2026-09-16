# Manual mask keyframe editing

작업 브랜치: `refactor/manual-mask-keyframes`. 수동 편집 마스크는 키프레임 + 추적 타임라인으로 해석한다. **현재 비-AI 수동 추적 엔진의 세부 구현, 성능·정확도 한계 및 재현 테스트 절차는 [MANUAL_TRACKING_CLASSICAL_CV.md](MANUAL_TRACKING_CLASSICAL_CV.md)를 참고한다.** 이 변경은 자동 블러의 얼굴 검출·후처리 파이프라인을 변경하지 않는다.

## 동작 계약

- 수동 모드에서 브러시/지우개로 실제 수정한 프레임, 또는 현재 프레임 자동 검출로 생성한 마스크는 명시적 키프레임이다.
- 직접 저장된 마스크와 추적 결과가 없는 프레임에서는 가장 가까운 이전 키프레임을 `hold` 방식으로 표시한다. 단, 추적 실패 경계 이후에는 기존 추적 마스크를 계속 적용하지 않는다.
- `현재 마스크 자동 추적`은 현재 프레임부터 다음 명시적 키프레임 직전 또는 영상 끝까지 진행한다. 상속/추적된 마스크를 시작점으로 선택한 경우 실제 샘플이 생성되고 소스 저장이 완료되어야 새 키프레임으로 승격한다. 취소 또는 샘플 0개이면 타임라인을 바꾸지 않는다.
- 추적 샘플은 마스크 연결 영역별 위치 이동 및 균일 크기 변화를 저장한다. 회전·원근 변형은 아직 지원하지 않는다.
- 단순 프레임 이동·재생은 키프레임을 추가하지 않는다. 상속/추적된 마스크를 수정하면 그 프레임부터 새 키프레임이 되고 이전 구간은 그 지점에서 끊긴다. 완전히 지운 마스크는 다음 키프레임 전까지 블러가 없는 구간으로 해석한다.
- 자동 워크스페이스의 기존 프레임별 마스크 의미는 변경하지 않는다.

## 현재 수동 추적 엔진

`ManualMaskTrackingService`는 OpenCV나 별도 AI 모델 없이 기존 `FfFrameExtractor`로 최대 폭 480px 프레임을 순차 디코딩한다.

- 연결된 마스크 영역을 추적 단위로 사용하고 bounding envelope가 겹치는 영역은 같은 그룹으로 묶는다. 그룹마다 마스크 내부에서 밝기 변화가 있는 특징점을 최대 64개 선택한다. 최소 6개가 없으면 불확실한 추적을 시작하지 않는다.
- 두 프레임 간 지역 패치를 성기게 탐색한 뒤 정밀 탐색하고, 다시 역방향으로 추적하여 출발점에 돌아오는지 확인한다. 역방향 오차가 큰 점은 제외한다. 이는 *피라미드 KLT 광학 흐름* 구현이 아니다.
- 남은 특징점으로 두 점 기반 RANSAC 유사 합의(위치 + 균일 스케일, 회전 없음)를 만들고 합의 점으로 변환을 재계산한다. 기하학적 오차, 특징점 수, 합의 비율, 크기, 화면 경계, 신뢰도를 검증한다. 모든 그룹이 통과하기 전에 어느 그룹의 결과도 저장하지 않는다.
- 전체 밝기 히스토그램, 4×4 구역별 밝기 히스토그램, 프레임 간 픽셀 차이를 함께 검사하여 장면 전환 조건을 판정한다. 하나의 전역 평균 밝기 차이 임계값만 사용하던 기존 방식과 다르다.
- 순차 디코딩에서 프레임 번호가 건너뛰면 확인되지 않은 프레임에 마스크를 적용하지 않고 중단한다. 장면 전환이나 추적 실패가 감지되면 그 프레임을 `StopFrame`과 `EndExclusive`로 설정해 이후 구간을 막는다.
- 실패 전에 생성된 샘플이 있으면 이전에 통과한 구간까지만 저장한다. 첫 다음 프레임부터 실패해 샘플이 0개이면 추적 결과와 승격 소스 키프레임을 저장하지 않는다. 추적 중에는 편집·저장·화면 이탈을 제한하고 별도 취소 명령을 제공한다.

**제한:** 특징점 패치의 성긴 탐색→정밀 탐색은 다중 해상도 광학 흐름이 아니며 회전, 큰 움직임, 가림, 텍스처 부족에서 중단할 수 있다. 장면 전환 후 다른 영역으로 잘못 이동하는 모든 경우를 차단한다고 보증할 수 없다. 실패 발견 *이전*에 허용한 결과를 자동으로 되돌리는 기능은 아직 없다. 추적된 모든 프레임의 실제 정확도는 영상 테스트가 필요하다.

## 수명·취소 및 저장

- 수동 추적은 `WorkspaceOperationLifetime` 작업으로 등록된다. 종료 시 CTS에 취소를 전달하고 tracking 작업이 `End()`에 도달하기 전에는 워크스페이스가 공유 session/provider를 해제하지 않는다.
- 수동 키프레임이 구성된 `FramePreviewViewModel.Dispose()`를 직접 호출한 경우 tracking, 수동 프레임 로드, 재생 task를 세션의 지연 해제 조건에 등록한다. 취소 요청은 UI thread에서 native decoder를 동기 대기하는 것과 구분한다. 구성되지 않은 별도 직접 Dispose 경로까지 보장하는지는 별도 검증이 필요하다.
- 취소와 tracking 결과 commit은 같은 gate를 사용한다. 취소가 선행하면 commit하지 않고, commit이 시작되면 source/workspace/track state commit을 완료한 뒤 종료한다. commit flag는 예외 발생 시에도 `finally`에서 해제한다.
- 실제 키프레임만 기존 `mask_<frame>.png` 또는 face-mask entry로 저장한다. 프레임마다 4K 마스크 파일을 생성하지 않는다. 추적은 compact state에 `OffsetX`, `OffsetY`, `Scale`, `Confidence`, 원본 파일 evidence 및 source mask fingerprint를 저장한다.
- 먼저 source 키프레임 workspace를 저장하고 persistence tail을 drain한 뒤 track state를 저장한다. source 저장 실패 시 새로 승격한 키프레임을 롤백하며 tracking metadata는 쓰지 않는다.
- `WorkspacePersistenceCoordinator.QueueSaveAsync(Func<WorkspaceSnapshot>)` 및 `SaveNow(Func<WorkspaceSnapshot>)`는 scalar `BuildSnapshot` 실행과 provider mask 캡처를 같은 `_captureGate`에 넣는다. 일반·terminal·tracking 전용 저장은 메서드 그룹 `BuildSnapshot`을 전달한다. 이 gate가 외부 모든 상태 변경을 트랜잭션으로 보호하지는 않는다.
- track state 파일은 호출마다 별도 임시 파일을 만들고 프로세스 내 저장을 직렬화한 다음 최종 파일을 교체한다.

## Preview / Export 동작

- Preview와 Export는 같은 tracking segment의 동일한 위치/스케일 샘플을 사용한다. Export는 source bitmap을 읽기 전용으로 참조하고 scratch bitmap을 재사용한다.
- source mask fingerprint 불일치 시 해당 추적을 적용하지 않는다. 실패 경계 이후에는 기존 추적을 이어 붙이지 않는다. 해결되지 않은 tracking failure가 있으면 Export를 차단한다.
- Preview의 tolerant load는 손상된 개별 segment를 제외할 수 있다. Export의 strict load는 파일이 존재하면서 읽기 실패·손상·중복 키프레임·버전/source evidence 불일치·필수 JSON 필드 누락·유효하지 않은 숫자/경계가 있으면 fail-closed 처리한다. tracking state 파일 자체가 없으면 비추적 workspace로 허용한다.
- Export preflight는 CTS/status 준비 후 background에서 수행하고 source fingerprint 계산 시에도 취소를 확인한다. 수동 단일 Auto로 키프레임이 변경되면 provider 기준으로 현재 마스크를 다시 생성하고 추적 소스 fingerprint를 재검증한다.

## 2026-09-16 수정 이력 및 검증 경계

1. Workspace scalar/mask snapshot 정합성, tolerant/strict JSON 읽기, tracking commit과 직접 Dispose 시 수명 처리를 수정했다.
2. 이어서 수동 tracking의 고정 밝기 패턴 검색을 특징점 패치의 전·후방 검증과 RANSAC 합의 기반 위치·크기 추정으로 교체했다. 평균 밝기 차이 한 가지 대신 전체/공간 밝기 분포와 픽셀 차이로 장면 전환 판정을 보강했다.
3. RANSAC 두 번째 점이 첫 번째 점과 같아질 수 있는 조합을 방지하고, 성긴 탐색과 정밀 탐색의 점수를 분리했다. 자동 블러 쪽 구현은 변경하지 않았다.

**빌드 통과와 추적 정확도 검증은 다르다.** 회전·원근·실제 피라미드 광학 흐름, 사후 오추적 롤백, 실제 영상의 총 프레임 수/크기와 track JSON의 대조, 비구성 Preview 직접 Dispose, 손상 JSON 재현, 저장 동시성, 550→750→851 재현 영상의 정확도·속도·Preview/Export 결과는 아직 개별 런타임 검증이 필요하다. 최신 CI는 해당 커밋의 GitHub Actions에서 확인한다.
