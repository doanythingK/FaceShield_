from pathlib import Path

def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text(encoding="utf-8")
    if new in text:
        return
    if old not in text:
        raise RuntimeError(f"expected source block not found: {path}")
    p.write_text(text.replace(old, new, 1), encoding="utf-8")

coordinator_old = """    internal bool NeedsResumePrompt()
    {
        if (_mode != WorkspaceMode.Auto || Completed || ResumeIndex <= 0)
            return false;

        AutoMaskOptions options = _getAutoOptions();
        FaceOnnxDetectorOptions detectorOptions = _getDetectorOptions();
        FaceDetectorFactoryOptions factoryOptions = _getDetectorFactoryOptions();
        string intentSignature = AutoRunSignaturePolicy.BuildIntentSignature(
            options,
            detectorOptions,
            factoryOptions);
        return !AutoRunSignaturePolicy.RequiresCompleteTimeline(options, factoryOptions) &&
               AutoMaskGenerator.CanResumeFromFrame(options, ResumeIndex) &&
               IsResumeSignatureCurrent(intentSignature);
    }

    internal void MarkPreviewNeedsExactRefresh()
"""

coordinator_new = """    internal bool NeedsResumePrompt()
    {
        if (!HasInterruptedAutoRunForResume())
            return false;

        return GetResumeUnavailableReason() == null;
    }

    internal string? GetResumeUnavailableReason()
    {
        if (!HasInterruptedAutoRunForResume())
            return null;

        AutoMaskOptions options = _getAutoOptions();
        FaceOnnxDetectorOptions detectorOptions = _getDetectorOptions();
        FaceDetectorFactoryOptions factoryOptions = _getDetectorFactoryOptions();
        string intentSignature = AutoRunSignaturePolicy.BuildIntentSignature(
            options,
            detectorOptions,
            factoryOptions);

        if (!IsResumeSignatureCurrent(intentSignature))
        {
            return "이전 자동 작업과 현재 자동 분석 설정이 달라 중단 지점에서 안전하게 이어할 수 없습니다.";
        }

        if (string.IsNullOrWhiteSpace(ExecutionSignature))
        {
            return "이전 자동 작업의 실행 환경 정보를 확인할 수 없어 동일한 조건으로 이어하기를 보장할 수 없습니다.";
        }

        AutoMaskOptions effectiveOptions = options.ResolveProcessingMode();
        if (AutoRunSignaturePolicy.RequiresCompleteTimeline(options, factoryOptions))
        {
            return effectiveOptions.ProcessingMode switch
            {
                AutoMaskProcessingMode.Full =>
                    "현재 '전체 보정' 모드는 후처리와 프레임 간 연결을 위해 전체 타임라인을 다시 계산해야 하므로 중단 지점부터 이어할 수 없습니다.",
                AutoMaskProcessingMode.Tracked =>
                    "현재 '자동 안정화' 설정은 프레임 간 연속성을 다시 계산해야 하므로 중단 지점부터 안전하게 이어할 수 없습니다.",
                _ =>
                    "현재 자동 분석 설정은 전체 타임라인 연속성을 다시 계산해야 하므로 중단 지점부터 이어할 수 없습니다."
            };
        }

        if (!AutoMaskGenerator.CanResumeFromFrame(options, ResumeIndex))
        {
            return "저장된 중단 지점이 현재 검출 간격의 안전한 재개 경계와 맞지 않아 해당 위치부터 이어할 수 없습니다.";
        }

        return null;
    }

    private bool HasInterruptedAutoRunForResume()
        => _mode == WorkspaceMode.Auto && !Completed && ResumeIndex > 0;

    internal void MarkPreviewNeedsExactRefresh()
"""

replace_once(
    "ViewModels/Workspace/AutoMaskRunCoordinator.cs",
    coordinator_old,
    coordinator_new,
)

workspace_old = """        public bool NeedsAutoResumePrompt => _autoRunCoordinator.NeedsResumePrompt();

        public int AutoLastProcessedFrame => _autoRunCoordinator.LastProcessedFrame;
"""

workspace_new = """        public bool NeedsAutoResumePrompt => _autoRunCoordinator.NeedsResumePrompt();
        public string? AutoResumeUnavailableReason => _autoRunCoordinator.GetResumeUnavailableReason();

        public int AutoLastProcessedFrame => _autoRunCoordinator.LastProcessedFrame;
"""

replace_once(
    "ViewModels/Pages/WorkspaceViewModel.cs",
    workspace_old,
    workspace_new,
)

home_old = """                if (vm.NeedsAutoResumePrompt)
                {
                    bool resume = await ShowResumeAutoDialogAsync();
                    if (!CanApplyWorkspaceLoadProgress(loadCts))
                        return;
                    if (!resume)
                    {
                        if (await EnsureWorkspaceReadyAsync(vm, loadCts) &&
                            CanApplyWorkspaceLoadProgress(loadCts))
                        {
                            // Deferred initialization and navigation are part of the
                            // same accepted load generation. Do not release ownership
                            // between them.
                            _onStartWorkspace(vm);
                        }
                        return;
                    }
                }

                if (IsAutoRunning || !CanApplyWorkspaceLoadProgress(loadCts))
"""

home_new = """                if (vm.NeedsAutoResumePrompt)
                {
                    bool resume = await ShowResumeAutoDialogAsync();
                    if (!CanApplyWorkspaceLoadProgress(loadCts))
                        return;
                    if (!resume)
                    {
                        if (await EnsureWorkspaceReadyAsync(vm, loadCts) &&
                            CanApplyWorkspaceLoadProgress(loadCts))
                        {
                            // Deferred initialization and navigation are part of the
                            // same accepted load generation. Do not release ownership
                            // between them.
                            _onStartWorkspace(vm);
                        }
                        return;
                    }
                }
                else
                {
                    string? resumeUnavailableReason = vm.AutoResumeUnavailableReason;
                    if (!string.IsNullOrWhiteSpace(resumeUnavailableReason))
                    {
                        await ShowErrorDialogAsync(
                            "자동 작업 이어하기 불가",
                            $"{resumeUnavailableReason}\\n\\n현재 자동 작업은 처음부터 다시 시작됩니다.");
                        if (!CanApplyWorkspaceLoadProgress(loadCts))
                            return;
                    }
                }

                if (IsAutoRunning || !CanApplyWorkspaceLoadProgress(loadCts))
"""


workspace_auto_old = """                if (Mode == WorkspaceMode.Manual)
                {
                    await RunAutoSingleFrameAsync();
                    ToolPanel.CurrentMode = EditMode.Manual;
                    return;
                }

                await RunAutoAsync(exportAfter: false);
"""

workspace_auto_new = """                if (Mode == WorkspaceMode.Manual)
                {
                    await RunAutoSingleFrameAsync();
                    ToolPanel.CurrentMode = EditMode.Manual;
                    return;
                }

                string? resumeUnavailableReason = AutoResumeUnavailableReason;
                if (!string.IsNullOrWhiteSpace(resumeUnavailableReason))
                {
                    await ShowErrorDialogAsync(
                        "자동 작업 이어하기 불가",
                        $"{resumeUnavailableReason}\\n\\n현재 자동 작업은 처음부터 다시 시작됩니다.");
                }

                await RunAutoAsync(exportAfter: false);
"""

replace_once(
    "ViewModels/Pages/WorkspaceViewModel.cs",
    workspace_auto_old,
    workspace_auto_new,
)

replace_once(
    "ViewModels/Pages/HomePageViewModel.cs",
    home_old,
    home_new,
)

status_old = """- [x] Keep a YOLO risk-cascade soft failure as `Completed=false`, preserve the detection result without committing staged post-processing, persist the failed gate state, and require a later full Auto rerun.

## Hardening pass status
"""

status_new = """- [x] Keep a YOLO risk-cascade soft failure as `Completed=false`, preserve the detection result without committing staged post-processing, persist the failed gate state, and require a later full Auto rerun.

### Runtime Auto-resume UX follow-up

- [x] Preserve the existing resume-confirmation dialog when the interrupted Auto state is safe to resume.
- [x] When an interrupted Auto state exists but current settings, execution evidence, full-timeline requirements, or the saved resume boundary make resumption unsafe, surface the concrete reason instead of silently restarting from frame 0.
- [x] Keep the accepted workspace-load generation alive while the unavailable-resume notice is displayed, then re-check the generation before handing off to Auto.

## Hardening pass status
"""

replace_once(
    "OWNERSHIP_WORKSPACE_HARDENING_STATUS.md",
    status_old,
    status_new,
)

# Contract checks used by CI and by the final commit step.
checks = {
    "ViewModels/Workspace/AutoMaskRunCoordinator.cs": [
        "internal string? GetResumeUnavailableReason()",
        "HasInterruptedAutoRunForResume()",
        "AutoMaskProcessingMode.Tracked",
        "ExecutionSignature",
    ],
    "ViewModels/Pages/WorkspaceViewModel.cs": [
        "AutoResumeUnavailableReason => _autoRunCoordinator.GetResumeUnavailableReason()",
        "string? resumeUnavailableReason = AutoResumeUnavailableReason;",
        "\"자동 작업 이어하기 불가\"",
    ],
    "ViewModels/Pages/HomePageViewModel.cs": [
        "string? resumeUnavailableReason = vm.AutoResumeUnavailableReason;",
        "\"자동 작업 이어하기 불가\"",
        "현재 자동 작업은 처음부터 다시 시작됩니다.",
        "if (!CanApplyWorkspaceLoadProgress(loadCts))",
    ],
}

for path, markers in checks.items():
    text = Path(path).read_text(encoding="utf-8")
    for marker in markers:
        if marker not in text:
            raise RuntimeError(f"contract marker missing in {path}: {marker}")

print("auto resume unavailable prompt patch applied and verified")
