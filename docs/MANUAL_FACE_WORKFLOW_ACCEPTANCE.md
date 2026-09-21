# 수동 누락 얼굴 추적 — 사용자 작업 흐름과 완료 기준

기준 브랜치: `refactor/manual-overlay-workflow` (2026-09-21). **새 얼굴별 구현은 빌드·테스트·GUI·인코딩 출력 검증을 하지 않았다.** 아래의 ‘구현’은 소스에 해당 경로가 있다는 뜻이며 사용자 환경에서 동작이 입증됐다는 뜻이 아니다. 기존 `main`에는 반영하지 않았다.

## 기능 목표: 저장 시스템이 아니라 누락된 얼굴을 끝까지 가리는 작업

1. 사용자는 수동 모드에서 자동 탐지가 놓친 **얼굴 A**를 ‘새 얼굴’로 만들고 해당 프레임의 마스크를 그린다. A는 고유 GUID와 별도 보정 키프레임을 소유한다. 기존 자동 탐지 및 전역/레거시 수동 마스크를 A로 재분류하지 않는다.
2. ‘선택한 얼굴 연속 추적’은 **A의 명시적 마스크**만 입력받아 다음 A 보정 또는 영상 끝까지 프레임별 움직임을 검증한다. 다른 얼굴 B의 등장·보정은 A의 추적 경계가 아니다. 검증되지 않은 프레임에 이전 마스크를 정지 상태로 붙잡아두지 않는다.
3. 추적이 끊기면 사용자는 오류에 표시된 프레임으로 이동하여 **같은 얼굴을 다시 그리거나**, 실제로 사라졌다면 ‘이 프레임부터 선택 얼굴 없음’을 눌러 A만 종료한다. 빈 화면을 지우개로 문질러도 픽셀이 바뀌지 않아 종료 키프레임을 만들 수 없던 경우를 위해 명시적 조작을 제공한다. 이후 A가 다시 등장하면 A를 선택해 새 보정을 그려 새 구간을 시작한다.
4. 사용자가 B를 추가로 지정하거나 A를 다시 보정해도 B의 키프레임·검증 샘플은 손상되지 않아야 한다. 얼굴별 Undo도 다른 얼굴로 새어 나가지 않아야 한다.
5. 미리보기와 최종 영상에는 **해당 프레임의 Auto + 기존 수동 마스크 + 모든 얼굴별 검증 마스크**가 합쳐져야 한다. 미해결 비어 있지 않은 얼굴 구간, 부정확한 소스/크기, 손상된 상태는 성공한 출력으로 처리하지 않는다. 얼굴별 마스크가 없는 구간을 정지된 이전 마스크로 대체하지 않는다.

## 현재 소스 연결 상태 (동작 미검증)

| 사용자 단계 | 현재 연결된 코드 | 남은 확인 |
| --- | --- | --- |
| A/B 생성·선택·그리기 | `Views/Pages/WorkspaceView.axaml.cs`, `FramePreviewViewModel.ManualTargets.cs`, `ManualOverlayTargetEditCommitter.cs` | 생성·선택·저장 및 재시작 후 얼굴 식별 실제 확인 |
| A만 추적 및 오류 경계 | `ManualOverlayTargetTrackingService.cs`, `ManualMaskTrackingService.cs`, `ManualOverlayTargetWorkspace.cs` | 실제 움직임·가림·장면 전환·다중 얼굴·VFR 확인 |
| A만 명시적으로 사라짐 지정 | `FramePreviewViewModel.ManualTargetAbsence.cs`, WorkspaceView의 ‘이 프레임부터 선택 얼굴 없음’ 버튼 | 빈 프레임의 클릭·Undo·재등장 및 저장 실패 확인 |
| 모든 유효 마스크 합성·출력 차단 | `FramePreviewViewModel.ManualTargets.cs`, `ManualOverlayTargetExportMaskProvider.cs`, `WorkspaceExportCoordinator.cs` | 미리보기 픽셀과 인코딩 영상 픽셀 대조, 추가 내보내기 진입점 감사 |
| 데이터 보존 | `ManualOverlayStateStore.cs`, `ManualOverlayWorkspaceStore.cs` | 동시 작업·디스크 오류·강제 종료 시 복구; 파일 잠금 우회자 방어 불가 |

## 확인해야 할 구체적 시나리오 (모두 미실행)

- **두 얼굴 분리:** 동일 프레임의 A/B 겹침 및 A 단독 보정 후 B의 마스크·추적 샘플·Undo 불변.
- **끊김과 재등장:** A의 f 프레임 추적 실패 → f에서 직접 보정 또는 A 없음 지정 → f 이후 A의 옛 마스크가 보이지 않음 → 나중에 A를 다시 그리면 새 구간 시작. B와 Auto는 전 구간 보존.
- **프레임 누락 방지:** 중간 샘플이 없거나 VFR 디코딩 순서가 어긋나면 출력이 성공한 척하지 않음. 출력 영상 전 프레임을 미리보기와 대조.
- **작업 수명:** 저장 실패 상태에서 얼굴 전환·프레임 이동·내보내기·창 닫기가 편집을 버리지 않음. 강제 종료 복구는 별도 미구현 범위.
- **합성 테스트 소스:** `scripts/manual-overlay-regression.cs.txt`, `scripts/manual-overlay-absence-regression.cs.txt`, `scripts/manual-overlay-conflict-regression.cs.txt`. 테스트 파일은 작성됐지만 실행하지 않음.

**얼굴 식별:** 얼굴 번호는 다시 열 때 정렬 순서에 따라 바뀔 수 있어 선택 목록에 GUID 앞 8자리도 함께 표시하도록 수정했다. [표시 변경 `40005f0`](https://github.com/doanythingK/FaceShield_/commit/40005f01dc3cf10ab02766be50407b7136f09205). 이는 안정적인 *식별 단서*이지 사용자 지정 얼굴 이름·썸네일 저장이 아니다. 실제 사용성과 GUID 접두부 충돌 처리, 재등장·출력 동등성은 아직 미검증이다. 변경 이력 및 세부 위험은 [`MANUAL_OVERLAY_WORKFLOW.md`](MANUAL_OVERLAY_WORKFLOW.md)를 참고한다.
