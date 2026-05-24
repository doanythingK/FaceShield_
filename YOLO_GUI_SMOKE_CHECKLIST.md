# YOLO GUI Smoke Checklist

이 문서는 YOLO 자동 모자이크 목표에서 마지막으로 수동 확인해야 하는 Avalonia GUI 스모크 항목을 정리한다.

자동 검증 스크립트는 backend/profile 분리, FaceONNX 기본값 보존, YOLO 후보 비교, track-hold 알고리즘, full-GT 리뷰 상태를 확인한다. 다만 실제 화면에서 영상 열기, YOLO 선택, 다운로드 버튼, 미리보기, 수동 편집, export, 재오픈 상태는 사람이 앱을 조작해 증거를 남겨야 한다.

## 현재 완료 조건

- `.tmp/yolo-gui-smoke/manual-smoke-checklist.csv`의 모든 행이 `status=pass`여야 한다.
- 각 행은 `evidenceType`, `artifactPath`, `evidence`가 채워져야 한다.
- `artifactPath`는 실제로 존재하는 비어 있지 않은 파일이어야 한다.
- `preview-track-hold`는 짧은 detector miss 구간에서 이미 모자이크된 대상이 꺼졌다 켜지지 않고 유지되는 녹화 파일이어야 한다.
- `export`는 실제 export 결과 동영상 파일이어야 한다.
- 모든 행을 채운 뒤 GUI 스모크 verifier가 통과해야 한다.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\verify-yolo-gui-smoke-state.ps1 -ChecklistCsv ".tmp\yolo-gui-smoke\manual-smoke-checklist.csv" -RequireManualPass
```

## 앱 실행

아래 명령 중 하나로 YOLO 스모크용 앱 상태를 연다.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\open-yolo-manual-gates.ps1 -OpenApp
dotnet run --project FaceShield.csproj -- --yolo-smoke --open-manual
dotnet run --project FaceShield.csproj -- --yolo-smoke --open-auto
```

대시보드와 현재 남은 항목을 다시 만들려면 다음을 실행한다.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\open-yolo-manual-gates.ps1 -WriteSummary -OpenDashboard
```

## 확인 항목

| stepId | 확인할 내용 | 증거 파일 |
| --- | --- | --- |
| `open-video` | 홈 화면에서 짧은 `srcTest` 영상을 열고 Workspace preview frame이 보이는지 확인한다. | `.tmp/yolo-gui-smoke/evidence/open-video.png` |
| `select-yolo-backend` | detector를 `YOLO Face ONNX`로 바꾸고 YOLO5Face 또는 YOLOv8-Face profile을 선택한다. FaceONNX threshold UI와 YOLO threshold UI가 분리되어 있는지도 확인한다. | `.tmp/yolo-gui-smoke/evidence/select-yolo-backend.png` |
| `download-yolo-model` | detector 선택 줄에 있는 YOLO 모델 다운로드 버튼을 누르거나, 이미 받은 모델이 감지되어 `AutoYoloModelPath`가 채워지는지 확인한다. | `.tmp/yolo-gui-smoke/evidence/download-yolo-model.log` |
| `run-yolo-auto-detect` | YOLO 선택 상태에서 자동 모자이크를 실행하고 진행/완료 상태가 crash 없이 끝나는지 확인한다. | `.tmp/yolo-gui-smoke/evidence/run-yolo-auto-detect.log` |
| `preview-result` | preview를 재생하거나 scrub 하면서 얼굴 모자이크가 적용되고 눈에 띄는 깜박임이 없는지 확인한다. | `.tmp/yolo-gui-smoke/evidence/preview-result.mp4` |
| `preview-track-hold` | 이미 모자이크된 얼굴이 detector에 잠깐 미탐되더라도 모자이크가 꺼졌다 켜지지 않고 유지되는 구간을 녹화한다. | `.tmp/yolo-gui-smoke/evidence/preview-track-hold.mp4` |
| `manual-edit` | 수동 모드, brush, eraser, undo 중 하나 이상을 사용하고 preview에 편집 결과가 반영되는지 확인한다. | `.tmp/yolo-gui-smoke/evidence/manual-edit.png` |
| `export` | YOLO workspace 상태에서 최종 영상을 export하고 생성된 파일이 재생 가능한지 확인한다. | `.tmp/yolo-gui-smoke/evidence/export.mp4` |
| `reopen-state` | 같은 workspace를 다시 열어 YOLO 모델/profile 설정과 mask 상태가 복원되는지 확인한다. | `.tmp/yolo-gui-smoke/evidence/reopen-state.png` |

## 증거 기록 명령

각 증거 파일을 만든 뒤 해당 `stepId`에 대해 아래 형식으로 기록한다. `-Evidence` 값에는 파일명만 쓰지 말고 실제 관찰 결과를 적는다.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\set-yolo-gui-smoke-evidence.ps1 -StepId open-video -Evidence "srcTest video opened and workspace preview frames rendered."
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\set-yolo-gui-smoke-evidence.ps1 -StepId select-yolo-backend -Evidence "YOLO Face ONNX selected, YOLO5Face profile selected, FaceONNX thresholds stayed separate."
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\set-yolo-gui-smoke-evidence.ps1 -StepId download-yolo-model -Evidence "YOLO model download completed or existing model path was detected."
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\set-yolo-gui-smoke-evidence.ps1 -StepId run-yolo-auto-detect -Evidence "YOLO automatic mosaic completed without crash."
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\set-yolo-gui-smoke-evidence.ps1 -StepId preview-result -Evidence "Preview playback showed masked faces without obvious flicker on the tested clip."
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\set-yolo-gui-smoke-evidence.ps1 -StepId preview-track-hold -Evidence "Previously masked face stayed covered during short detector miss."
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\set-yolo-gui-smoke-evidence.ps1 -StepId manual-edit -Evidence "Manual brush or eraser edit was reflected in preview."
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\set-yolo-gui-smoke-evidence.ps1 -StepId export -Evidence "YOLO workspace export file was created and played successfully."
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\set-yolo-gui-smoke-evidence.ps1 -StepId reopen-state -Evidence "Reopened workspace restored YOLO profile and mask state."
```

## 완료 후 최종 확인

GUI 스모크가 통과하면 manual gate와 목표 완료 verifier를 다시 실행한다.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\verify-yolo-manual-readiness-state.ps1 -FullGtReviewCsv ".tmp\yolo-full-gt\review-package-smoke\full-gt-review.csv" -FullFrameReviewCsv ".tmp\yolo-full-gt\review-package-smoke\full-frame-review.csv" -GuiChecklistCsv ".tmp\yolo-gui-smoke\manual-smoke-checklist.csv" -FullGtPredictionLog ".tmp\yolo-ten-minute-detection-smoke\yolo-ten-minute-yolo-only-20260523-022022.log" -FullGtMinIou 0.5 -FullGtMaxMisses 0 -FullGtMaxFalsePositives 0 -FullGtMaxLowIou 0 -AllowCompletedFullGt -AllowCompletedGuiSmoke -AllowQualityGateFailure

powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\complete-yolo-goal-after-manual-gates.ps1 -FullGtReviewCsv ".tmp\yolo-full-gt\review-package-smoke\full-gt-review.csv" -FullFrameReviewCsv ".tmp\yolo-full-gt\review-package-smoke\full-frame-review.csv" -GuiChecklistCsv ".tmp\yolo-gui-smoke\manual-smoke-checklist.csv" -PredictionLog ".tmp\yolo-ten-minute-detection-smoke\yolo-ten-minute-yolo-only-20260523-022022.log" -MinIou 0.5 -MaxMisses 0 -MaxFalsePositives 0 -MaxLowIou 0 -AllowQualityGateFailure -UpdatePlan -RunYoloState
```
