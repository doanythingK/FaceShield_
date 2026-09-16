# Manual mask keyframe editing

`refactor/manual-mask-keyframes`에서 수동 편집 마스크는 프레임별 독립 마스크가 아니라 키프레임 + 추적 타임라인으로 해석한다.

## 동작 계약

- 수동 모드에서 브러시/지우개로 실제 수정한 프레임은 수동 마스크 키프레임이 된다.
- 현재 프레임에 직접 저장된 마스크가 없고 추적 결과가 아직 없는 구간은 가장 가까운 이전 키프레임을 `hold` 방식으로 표시한다.
- 사용자가 `현재 마스크 자동 추적`을 실행하면 현재 프레임부터 다음 명시적 키프레임 직전 또는 영상 끝까지 추적한다.
- 현재 프레임이 상속/추적된 마스크라면 실제 추적 샘플이 생성되어 결과가 저장될 때만 새 소스 키프레임으로 승격한다. 취소되거나 추적할 다음 프레임이 없으면 타임라인을 변경하지 않는다.
- 추적된 프레임은 위치 이동과 크기 변화를 적용한 마스크를 사용한다.
- 다음 명시적 키프레임이 나오면 그 프레임부터 새 키프레임이 기준이 된다.
- 단순히 프레임을 이동하거나 재생해서 확인한 것만으로는 중간 프레임 마스크를 새로 저장하지 않는다.
- 상속/추적된 마스크를 수정하면 그 프레임이 새 키프레임이 되고 이전 추적 구간은 그 지점에서 끊긴다.
- 상속된 마스크를 완전히 지워 저장하면 그 프레임부터 다음 키프레임 전까지 블러가 없는 구간이 된다.
- 수동 모드에서 현재 프레임 자동 검출로 만든 얼굴 마스크도 명시적 키프레임으로 취급한다.
- 자동 워크스페이스의 기존 프레임별 마스크 의미는 변경하지 않는다.

## 추적 방식

수동 추적은 새 OpenCV 의존성을 추가하지 않고 기존 `FfFrameExtractor`를 사용한다.

- 추적용 프레임은 최대 폭 480px로 축소해 순차 디코딩한다.
- 마스크의 분리된 연결 영역을 기본 추적 단위로 사용한다.
- 서로 다른 연결 영역이라도 source bounding box가 겹치거나, 영역을 합친 bounding envelope가 다른 영역을 포함하게 되면 같은 그룹으로 묶는다. 이는 한 영역의 transform이 다른 영역 픽셀까지 끌고 가는 것을 막기 위한 보수적 처리다.
- 각 최종 그룹은 위치 이동과 제한된 스케일 변화를 독립적으로 추적한다.
- 템플릿은 프레임 진행에 따라 갱신하되 이전 템플릿 일부를 유지한다.
- 장면 차이가 임계값을 넘으면 장면 전환으로 판단하고 그 프레임부터 추적을 중단한다.
- 한 영역이라도 추적 신뢰도가 기준보다 낮아지거나 후보를 찾지 못하면 해당 프레임에서 전체 추적 구간을 중단한다.
- 실패 전에 생성된 추적 샘플이 있으면 성공한 구간까지만 저장한다. 첫 다음 프레임부터 실패해 샘플이 하나도 없으면 추적 결과와 새 소스 키프레임을 저장하지 않는다.
- 추적 중단 이후 프레임에는 실패한 추적 결과를 계속 끌고 가지 않는다. 사용자가 중단 프레임에서 마스크를 수정한 뒤 다시 추적하는 방식이다.
- 추적 중에는 편집, 저장, 화면 이탈을 막고 별도 취소 명령을 제공한다.
- 후보 탐색, scene difference, component/template 처리 내부에서도 cancellation token을 주기적으로 확인해 취소가 한 프레임의 무거운 탐색 전체가 끝날 때까지 지연되지 않도록 한다.

현재 구현은 회전/원근 변형을 모델링하지 않는다. 이동과 균일 스케일 변화가 주 대상이다. 따라서 큰 회전, 급격한 자세 변화, 심한 가림에서는 신뢰도 기준에 의해 추적이 중단될 수 있다.

## 수명 및 취소

수동 tracking은 `WorkspaceOperationLifetime` 작업으로 등록한다.

- workspace dispose 또는 terminal shutdown이 admission을 닫으면 실행 중 tracking CTS에 취소를 전달한다.
- tracking 작업이 lifetime `End()`에 도달하기 전에는 workspace가 `FramePreview`, video session, mask provider를 dispose하지 않는다.
- tracking task는 별도로 보관되어 cancel + drain이 가능한 형태이며, view detach 시에는 먼저 취소를 요청한다.
- `FramePreviewViewModel.Dispose()`가 상위 workspace lifetime을 거치지 않고 직접 호출되는 경우에도 Dispose가 bitmap 상태를 비우는 시점에 manual tracking cancellation을 요청한다.
- cancellation과 결과 commit은 같은 commit gate를 사용한다. 취소가 gate를 먼저 획득하면 결과를 commit하지 않고, commit이 먼저 시작된 경우에는 source/workspace/track metadata의 짧은 commit 구간을 완료한 뒤 취소가 관찰된다.
- commit-start flag는 source 승격, workspace persistence, track metadata 저장을 포함하는 전체 commit 구간의 `try/finally`에서 관리하며, bitmap clone/`SetMask`/저장 예외가 발생해도 반드시 해제된다.

## 저장 방식

기존 workspace persistence 형식을 유지한다. 실제 키프레임 프레임만 기존 `mask_<frame>.png` 또는 face-mask entry로 저장하고, 중간 프레임을 일괄 bitmap 파일로 materialize하지 않는다.

추적 결과는 별도의 compact track state에 프레임별 `offset X/Y`, `scale`, `confidence`를 저장한다. 원본 영상의 파일 크기/수정 시각과 소스 키프레임 마스크 fingerprint를 함께 검증해, 영상이나 키프레임이 바뀐 뒤 오래된 추적 결과가 재사용되지 않도록 한다.

추적 결과를 commit할 때는 source keyframe을 먼저 workspace persistence에 저장하고 그 다음 compact track state를 저장한다. tracking 전용 저장은 `QueueSaveAsync()` 이후 현재 persistence tail을 `FlushAsync()`로 drain한 다음 완료되므로, latest-wins에서 해당 요청이 stale skip되더라도 더 최신 snapshot이 실제 저장을 끝내기 전에 track metadata를 먼저 쓰지 않는다. source workspace 저장이 실패하면 새로 승격한 source keyframe은 메모리 provider에서 롤백하고 tracking metadata는 쓰지 않는다.

workspace queued-save snapshot capture와 request publication은 같은 ordering gate를 사용한다. terminal `SaveNow()`와 coordinator `Dispose()`도 이 gate를 통과하므로 캡처 중인 이전 snapshot이 뒤늦게 더 높은 request id를 얻거나, dispose가 아직 publish되지 않은 snapshot을 앞질러가는 경로를 막는다.

track state 저장은 호출마다 고유한 임시 파일을 사용하고 프로세스 내 저장은 serialize한 뒤 최종 파일로 교체한다. 같은 영상에 대한 동시 저장이 고정 `.tmp` 파일을 서로 덮어쓰는 문제를 피한다.

따라서 긴 구간을 추적해도 프레임 수만큼 4K 마스크 파일이 생성되지 않는다.

## Preview / Export 일관성

Preview와 export는 같은 tracking segment를 해석한다.

- 정상 추적 구간에서는 동일한 이동/스케일 결과를 사용한다.
- 저장된 bitmap 키프레임은 export 동안 read-only source bitmap과 재사용 가능한 scratch bitmap을 사용해 프레임마다 4K `WriteableBitmap`을 새로 만들지 않는다.
- 추적 데이터가 현재 키프레임 fingerprint와 맞지 않으면 stale segment로 판단하고 preview/export 모두 해당 추적을 적용하지 않는다.
- 장면 전환 또는 신뢰도 실패 이후에는 실패 경계를 넘어 추적 마스크를 적용하지 않는다.
- unresolved tracking failure가 있으면 export를 시작하지 않는다.
- 일반 preview load는 손상된 track metadata를 적용하지 않고 keyframe hold로 복귀할 수 있도록 tolerant하게 유지한다.
- export는 strict track-state load를 사용한다. track 파일이 아예 없는 경우는 정상적인 비추적 workspace로 허용하지만, 파일이 존재하면서 읽기 실패, JSON/schema 손상, 버전 불일치, source evidence 불일치가 발생하면 fail-closed로 export를 차단한다.
- export preflight는 export CTS/status를 먼저 준비한 뒤 background task에서 실행하며, source-mask fingerprint 계산도 cancellation token을 확인한다.
- 수동 단일 Auto가 현재 키프레임 provider를 바꾸면 Auto 종료 시 현재 `MaskBitmap`을 provider 기준으로 다시 구성하고 기존 tracking source fingerprint를 재검증한다.

## 검증 상태

이 문서는 현재 브랜치의 소스 동작 계약을 설명한다. 실제 영상에서의 추적 품질, Windows/macOS 빌드 및 런타임 검증 결과를 의미하지 않는다.
