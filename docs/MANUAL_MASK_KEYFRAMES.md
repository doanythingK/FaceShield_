# Manual mask keyframe editing

작업 브랜치: `refactor/manual-mask-keyframes`. 수동 편집 마스크는 키프레임 + 추적 타임라인으로 해석한다. **최신 비-AI 수동 추적 엔진, 제한과 재현 테스트는 [MANUAL_TRACKING_CLASSICAL_CV.md](MANUAL_TRACKING_CLASSICAL_CV.md)에 기록한다.** 자동 블러의 얼굴 검출·후처리 파이프라인은 변경하지 않았다.

## 동작 계약

- 수동 모드에서 브러시/지우개로 실제 수정한 프레임 또는 현재 프레임 자동 검출로 만든 마스크는 명시적 키프레임이다.
- 직접 저장된 마스크와 추적 결과가 없는 프레임에서는 이전 키프레임을 `hold`한다. 단, 추적 실패 경계 이후에는 해당 키프레임의 추적 마스크를 계속 적용하지 않는다.
- `현재 마스크 자동 추적`은 현재 프레임부터 다음 명시적 키프레임 직전 또는 영상 끝까지 진행한다. 상속/추적된 마스크를 시작점으로 선택한 경우 실제 추적 샘플을 만들고 소스 저장을 완료해야 새 키프레임으로 승격한다. 취소나 샘플 0개이면 타임라인을 바꾸지 않는다.
- 추적 샘플은 연결 영역별 위치 이동·균일 크기 변화·화면 내 **2차원 회전**을 저장한다. 원근 변환이나 얼굴의 3차원 자세 변화는 지원하지 않는다.
- 단순 프레임 이동·재생은 키프레임을 추가하지 않는다. 상속/추적된 마스크를 수정하면 그 프레임부터 새 키프레임이 되고 이전 추적 구간은 끊긴다. 완전히 지운 마스크는 다음 키프레임 전까지 블러가 없는 구간으로 해석한다.
- 자동 워크스페이스의 기존 프레임별 마스크 의미는 변경하지 않는다.

## 수동 추적 엔진

`ManualMaskTrackingService`는 OpenCV나 새 AI 모델 없이 기존 `FfFrameExtractor`로 최대 폭 480px의 프레임을 순차 디코딩한다.

- 마스크 연결 영역에서 서로 겹치는 bounding envelope는 하나의 그룹으로 합친다. 그룹마다 마스크 내부 특징점을 최대 64개 선택하며 최소 6개가 없으면 시작하지 않는다.
- 실제 축소 영상 피라미드(최대 4단계)의 낮은 해상도에서 위치를 초기화한 다음 높은 해상도로 진행하는 Lucas–Kanade 광학 흐름으로 프레임 간 특징점을 추적한다. 반대 방향으로 되짚어 출발점과의 오차가 큰 점은 제외한다.
- 남은 특징점에 두 점 기반 RANSAC 및 합의 점 재추정을 적용해 위치·크기·회전을 계산한다. 특징점 수, 합의 비율, 기하학적 오차, 프레임 간 회전·크기, 화면 경계, 신뢰도를 검사한다. 모든 그룹이 통과하기 전에는 그 프레임의 어느 그룹 결과도 저장하지 않는다.
- 장면 전환 검사는 마스크 주변이 아니라 **화면 전체**에서 RGB 색상 히스토그램, 4×4 화면 구역별 색상 히스토그램, 같은 위치의 색상·구조 차이를 비교한다. 강한 전환 증거는 특징점 추적 전에 중단하고, 전환이 의심되면서 특징점 검증까지 실패하면 `장면 전환 의심` 이유를 기록한다.
- 순차 디코딩에서 프레임 번호가 건너뛰면 중단한다. 장면 전환이나 추적 실패 시 해당 프레임을 `StopFrame`/`EndExclusive`로 기록한다. **이전에 저장된 성공 샘플을 무조건 소급 삭제하지 않는다.**
- 샘플이 있는 경우 중단 직전까지만 저장한다. 첫 다음 프레임부터 실패해 샘플 0개이면 추적 결과와 승격 소스 키프레임을 저장하지 않는다. 추적 중 편집·저장·화면 이탈을 제한하고 별도 취소 명령을 제공한다.

**제한:** 색상이 비슷한 컷, 카메라 이동, 모션 블러, 가림, 텍스처 부족, 프레임 가장자리에서는 오검출·미검출 또는 조기 중단이 가능하다. 보고된 550→750→851 사례를 원본 영상으로 재현하지 않아 750프레임 감지 성공 여부는 확인되지 않았다. 실패 전에 통과한 모든 프레임이 실제로 정확하다는 보장도 없다.

## 수명·취소 및 저장

- 수동 추적은 `WorkspaceOperationLifetime` 작업으로 등록한다. 종료 시 CTS에 취소를 전달하고 tracking 작업이 `End()`에 도달하기 전 공유 session/provider를 해제하지 않는다.
- 키프레임이 구성된 `FramePreviewViewModel.Dispose()` 직접 호출에서는 tracking·수동 프레임 로드·재생 task를 세션 지연 해제 조건에 등록한다. UI thread에서 native decoder 완료를 동기 대기하지 않는다. 구성되지 않은 별도 직접 Dispose 경로까지는 개별 검증이 필요하다.
- 취소와 tracking 결과 commit은 같은 gate를 사용한다. 취소가 먼저면 commit하지 않고, commit이 시작되면 source/workspace/track state commit을 완료한 뒤 종료한다. commit flag는 예외 시에도 `finally`에서 해제한다.
- 실제 키프레임만 기존 `mask_<frame>.png` 또는 face-mask entry로 저장한다. 프레임마다 4K 마스크 파일을 생성하지 않는다. compact track state는 `OffsetX`, `OffsetY`, `Scale`, `Confidence`, 선택적 `RotationRadians`, 원본 파일 evidence 및 source mask fingerprint를 저장한다. 회전 필드가 없는 기존 v3 파일은 회전 0으로 해석한다.
- source 키프레임 workspace를 먼저 저장하고 persistence tail을 drain한 뒤 track state를 저장한다. source 저장 실패 시 새로 승격한 키프레임은 롤백하고 tracking metadata를 쓰지 않는다.
- `WorkspacePersistenceCoordinator.QueueSaveAsync(Func<WorkspaceSnapshot>)` 및 `SaveNow(Func<WorkspaceSnapshot>)`는 scalar `BuildSnapshot`과 provider mask 캡처를 같은 `_captureGate` 안에서 실행한다. 일반·terminal·tracking 전용 저장은 메서드 그룹 `BuildSnapshot`을 전달한다. 이 gate는 임의 외부 변경까지 보호하는 전역 트랜잭션은 아니다.
- 트랙 파일은 호출마다 고유 임시 파일을 만들고 프로세스 내 저장을 직렬화한 뒤 최종 파일을 교체한다.

## Preview / Export 동작

- Preview와 Export는 동일한 tracking segment 및 **공통 위치·크기·회전 역변환 래스터라이저**를 사용한다. Export는 source bitmap을 읽기 전용으로 참조하고 scratch bitmap을 재사용한다.
- source mask fingerprint가 바뀐 추적은 적용하지 않는다. 실패 경계 이후에는 추적 마스크를 이어 붙이지 않는다. 해결되지 않은 tracking failure가 있으면 Export를 차단한다.
- Preview의 tolerant load는 손상된 개별 segment를 제외할 수 있다. Export의 strict load는 파일이 존재하면서 읽기 실패·손상·중복 키프레임·버전/source evidence 불일치·필수 JSON 필드 누락·유효하지 않은 숫자/회전/경계가 있으면 fail-closed 처리한다. 트랙 파일 자체가 없으면 비추적 workspace로 허용한다.
- Export preflight는 CTS/status 준비 후 background에서 수행하고 source fingerprint 계산 시에도 취소를 확인한다. 수동 단일 Auto로 키프레임이 변경되면 provider 기준으로 현재 마스크를 다시 생성하고 추적 소스 fingerprint를 재검증한다.

## 수정 이력과 검증 경계

- 2026-09-16: Workspace scalar/mask snapshot 정합성, tolerant/strict JSON, tracking commit, 직접 Dispose 수명 처리를 보강하고 수동 추적을 특징점 전·후방 검사와 RANSAC 합의로 교체했다.
- 2026-09-17: 진짜 영상 피라미드 광학 흐름, 회전 유사 변환/저장/공통 Preview·Export 렌더러, 색상·구조 기반 전역 장면 전환 증거를 추가했다. 자동 블러는 변경하지 않았다.

**빌드 통과는 추적 정확도 검증이 아니다.** 사용자 원본 영상의 전환 프레임, 회전 마스크 Preview/Export 일치, 기존 v3 재열기, 카메라 이동 오검출, 속도·CPU·메모리, 손상 JSON·저장 동시성·수명 경계는 별도의 런타임 검증이 필요하다. 사후 오추적 롤백과 얼굴 신원 확인은 포함하지 않았다.
