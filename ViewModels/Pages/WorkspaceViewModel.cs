using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FaceShield.Enums.Workspace; // 🔹 추가
using FaceShield.Services.Analysis;
using FaceShield.Services.Diagnostics;
using FaceShield.Services.FaceDetection;
using FaceShield.Services.Video;
using FaceShield.Services.Workspace;
using FaceShield.ViewModels.Workspace;
using FaceShield.Views.Dialogs;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FaceShield.ViewModels.Pages
{
    public partial class WorkspaceViewModel : ViewModelBase, IDisposable
    {
        public ToolPanelViewModel ToolPanel { get; } = new();
        public FramePreviewViewModel FramePreview { get; }
        public FrameListViewModel FrameList { get; }
        private readonly Action? _onBack;
        private AutoMaskOptions _autoOptions;
        private FaceOnnxDetectorOptions _detectorOptions;
        private FaceDetectorFactoryOptions _detectorFactoryOptions;
        private readonly WorkspaceStateStore? _stateStore;
        private readonly WorkspacePersistenceCoordinator? _workspacePersistence;
        private readonly IssueReviewCoordinator _issueReview;
        private readonly WorkspaceExportCoordinator _exportCoordinator;
        private readonly AutoMaskRunCoordinator _autoRunCoordinator;
        private readonly WorkspaceSessionPlaybackCoordinator _sessionPlaybackCoordinator;
        // 프레임별 최종 마스크 저장소
        private readonly FrameMaskProvider _maskProvider = new();

        private readonly object _lifetimeSync = new();
        private int _activeLifetimeOperations;
        private bool _disposeRequested;
        private bool _resourcesDisposed;

        // 🔹 현재 워크스페이스 모드 (Auto / Manual)
        public WorkspaceMode Mode { get; }

        [ObservableProperty]
        private int autoAnomalyCount;

        [ObservableProperty]
        private bool hasAutoAnomalies;

        public ObservableCollection<IssueEntryViewModel> NoFaceIssueEntries => _issueReview.NoFaceIssueEntries;
        public ObservableCollection<IssueEntryViewModel> LowConfidenceIssueEntries => _issueReview.LowConfidenceIssueEntries;
        public ObservableCollection<IssueEntryViewModel> FlickerIssueEntries => _issueReview.FlickerIssueEntries;

        public bool AutoSummaryVisible => AutoAnomalyCount > 0;
        public string AutoSummaryText => $"문제 프레임 {AutoAnomalyCount}개";

        [ObservableProperty]
        private bool hideResolvedIssues = true;

        public bool NeedsAutoResumePrompt => _autoRunCoordinator.NeedsResumePrompt();

        public int AutoLastProcessedFrame => _autoRunCoordinator.LastProcessedFrame;
        public DateTime AutoLastProcessedAtUtc => _autoRunCoordinator.LastProcessedAtUtc;
        public string? AutoExecutionProviderLabel => _autoRunCoordinator.ExecutionProviderLabel;
        public string? AutoExecutionProviderError => _autoRunCoordinator.ExecutionProviderError;


        public WorkspaceViewModel(string videoPath)
            : this(videoPath, WorkspaceMode.Manual, null, null)
        {
        }

        public WorkspaceViewModel(string videoPath, WorkspaceMode mode)
            : this(videoPath, mode, null, null)
        {
        }

        public WorkspaceViewModel(
            string videoPath,
            WorkspaceMode mode,
            IProgress<int>? loadProgress,
            Action? onBack,
            AutoMaskOptions? autoOptions = null,
            FaceOnnxDetectorOptions? detectorOptions = null,
            WorkspaceStateStore? stateStore = null,
            bool deferSessionInit = false,
            FaceDetectorFactoryOptions? detectorFactoryOptions = null,
            CancellationToken initializationToken = default)
        {
            Mode = mode;
            _onBack = onBack;
            _autoOptions = autoOptions ?? new AutoMaskOptions();
            _detectorOptions = detectorOptions ?? new FaceOnnxDetectorOptions();
            _detectorFactoryOptions = detectorFactoryOptions ?? FaceDetectorFactoryOptions.ForOnnx(_detectorOptions);
            _stateStore = stateStore;
            _workspacePersistence = stateStore == null
                ? null
                : new WorkspacePersistenceCoordinator(stateStore, _maskProvider);
            initializationToken.ThrowIfCancellationRequested();
            FrameList = new FrameListViewModel(
                videoPath,
                initializationToken);
            FramePreview = new FramePreviewViewModel(ToolPanel, _maskProvider);
            _issueReview = new IssueReviewCoordinator(
                _maskProvider,
                TryBeginLifetimeOperation,
                EndLifetimeOperation);
            _issueReview.StateChanged += ApplyIssueReviewState;
            _exportCoordinator = new WorkspaceExportCoordinator(
                _maskProvider,
                ToolPanel,
                () => _autoRunCoordinator?.IsRunning == true,
                TryBeginLifetimeOperation,
                EndLifetimeOperation,
                ResolveExportOutputPathAsync);
            _autoRunCoordinator = new AutoMaskRunCoordinator(
                Mode,
                _maskProvider,
                FrameList,
                FramePreview,
                ToolPanel,
                _issueReview,
                _exportCoordinator,
                () => _autoOptions,
                () => _detectorOptions,
                () => _detectorFactoryOptions,
                () => HideResolvedIssues,
                TryBeginLifetimeOperation,
                EndLifetimeOperation,
                PersistWorkspaceState);
            _sessionPlaybackCoordinator = new WorkspaceSessionPlaybackCoordinator(
                Mode,
                FrameList,
                FramePreview,
                () => _autoRunCoordinator.IsRunning,
                _autoRunCoordinator.MarkPreviewNeedsExactRefresh,
                TryBeginLifetimeOperation,
                EndLifetimeOperation,
                message => ShowErrorDialogAsync("재생 실패", message));
            if (!deferSessionInit)
                _sessionPlaybackCoordinator.Initialize(loadProgress, initializationToken);

            // 🔹 자동/최종 마스크 provider 주입
            FramePreview.SetMaskProvider(_maskProvider);

            ToolPanel.UndoRequested += () => FramePreview.Undo();
            FramePreview.MaskEdited += OnMaskEdited;

            ToolPanel.SaveRequested += async () =>
            {
                if (_autoRunCoordinator.IsRunning || ToolPanel.IsAutoRunning)
                    return;

                FramePreview.PersistCurrentMask();

                try
                {
                    await SaveVideoAsync();
                }
                catch (Exception ex)
                {
                    await ShowExportErrorAsync(ex);
                }
                finally
                {
                    PersistWorkspaceState();
                }
            };

            // 🔹 자동 모드 버튼 → 자동 마스크 생성 연결
            ToolPanel.AutoRequested += OnAutoRequested;
            ToolPanel.AutoCancelRequested += OnAutoCancelRequested;
            ToolPanel.ExportCancelRequested += OnExportCancelRequested;

            _sessionPlaybackCoordinator.ScheduleInitialPreview();
        }

        public Task EnsureSessionInitializedAsync(
            IProgress<int>? loadProgress,
            CancellationToken cancellationToken = default)
            => _sessionPlaybackCoordinator.EnsureInitializedAsync(
                loadProgress,
                cancellationToken);

        private Task<bool> SaveVideoAsync(
            IProgress<ExportProgress>? exportProgress = null,
            CancellationToken cancellationToken = default,
            bool updateToolPanel = true,
            string? runId = null,
            AutoMaskRunSummary? autoRunSummary = null,
            AutoMaskOptions? autoRunOptions = null)
        {
            return _exportCoordinator.ExportAsync(
                FrameList.VideoPath,
                ToolPanel.BlurRadius,
                exportProgress,
                cancellationToken,
                updateToolPanel,
                runId,
                autoRunSummary,
                autoRunOptions,
                qualityPreset: ToolPanel.ExportQualityPreset);
        }

        private static string? SerializeAutoExportHybridDisableReasons(
            IReadOnlyList<string> values)
        {
            return values.Count == 0
                ? null
                : string.Join('|', values);
        }

        private static IReadOnlyList<string> ParseAutoExportHybridDisableReasons(
            string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return Array.Empty<string>();

            return value.Split(
                '|',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        private static (bool allowHybridCopy, IReadOnlyList<string> disableReasons) EvaluateAutoExportHybridPolicy(
            AutoMaskRunSummary? autoRunSummary)
        {
            if (autoRunSummary == null)
                return (true, Array.Empty<string>());

            const int SampleGapRateMinWindowFrames = 240;
            const double SampleMissRecoveryRateThreshold = 0.001;     // 0.1%
            const double SampleFpSuppressedRateThreshold = 0.001;     // 0.1%
            const double FinalSceneCutRemovalRateThreshold = 0.02;    // 2%
            const double FinalSceneCutProtectedRateThreshold = 0.02;  // 2%
            const double OffModeSceneCutResetRateThreshold = 0.02;   // 2%
            var reasons = new List<string>();
            int sampleWindowFrames = autoRunSummary.SampleWindowFrames > 0
                ? autoRunSummary.SampleWindowFrames
                : Math.Min(autoRunSummary.TotalFrames, Math.Min(
                    Math.Max(1, (int)Math.Round(autoRunSummary.SourceFps * 30.0)),
                    900));
            if (sampleWindowFrames <= 0)
                sampleWindowFrames = Math.Max(1, autoRunSummary.TotalFrames);
            double sampleMissRecoveryRate = autoRunSummary.SampleMissRecoveryFillCount / (double)sampleWindowFrames;
            double sampleFpSuppressedRate = autoRunSummary.SampleFalsePositiveSuppressionCount / (double)sampleWindowFrames;
            double finalOffModeWeakCleanupRate = autoRunSummary.TotalFrames > 0
                ? autoRunSummary.FinalOffModeWeakCleanupCount / (double)autoRunSummary.TotalFrames
                : 0.0;
            double sampleOffModeWeakCleanupRate = sampleWindowFrames > 0
                ? autoRunSummary.SampleOffModeWeakCleanupSuppressionCount / (double)sampleWindowFrames
                : 0.0;
            int sampleGapFillBlockedTotal = autoRunSummary.SampleGapFillBlockedCutGapFrames
                + autoRunSummary.SampleGapFillBlockedCutGapFramesBeforeCut
                + autoRunSummary.SampleGapFillBlockedCutGapFramesAfterCut
                + autoRunSummary.SampleGapFillBlockedCleanupGapFrames
                + autoRunSummary.SampleGapFillBlockedSceneCarryGapFrames;
            double sampleGapFillBlockedRate = sampleWindowFrames > 0
                ? sampleGapFillBlockedTotal / (double)sampleWindowFrames
                : 0.0;
            double finalSceneCutRemovalRate = autoRunSummary.FinalSceneCutCarryPairCount > 0
                ? autoRunSummary.FinalSceneCutCarryRemovedCount / (double)autoRunSummary.FinalSceneCutCarryPairCount
                : 0.0;
            double finalSceneCutProtectedRate = autoRunSummary.FinalSceneCutCarryPairCount > 0
                ? autoRunSummary.FinalSceneCutProtectedFrameCount / (double)autoRunSummary.FinalSceneCutCarryPairCount
                : 0.0;
            double finalGapFillBlockedRate = autoRunSummary.TotalFrames > 0
                ? (autoRunSummary.FinalGapFillBlockedCutGapFrames
                    + autoRunSummary.FinalGapFillBlockedCutGapFramesBeforeCut
                    + autoRunSummary.FinalGapFillBlockedCutGapFramesAfterCut
                    + autoRunSummary.FinalGapFillBlockedCleanupGapFrames
                    + autoRunSummary.FinalGapFillBlockedSceneCarryGapFrames) / (double)autoRunSummary.TotalFrames
                : 0.0;
            double offModeSceneCutResetBeforeRate = autoRunSummary.FinalOffModeSceneCutResetBeforeWindowFrameCount > 0
                ? autoRunSummary.FinalOffModeSceneCutResetRemovedBeforeFrameCount / (double)autoRunSummary.FinalOffModeSceneCutResetBeforeWindowFrameCount
                : 0.0;
            double offModeSceneCutResetAfterRate = autoRunSummary.FinalOffModeSceneCutResetAfterWindowFrameCount > 0
                ? autoRunSummary.FinalOffModeSceneCutResetRemovedAfterFrameCount / (double)autoRunSummary.FinalOffModeSceneCutResetAfterWindowFrameCount
                : 0.0;

            bool finalRisk = autoRunSummary.FinalMaskReviewRequired;
            if (autoRunSummary.FinalMaskReviewReasons != "none" &&
                !string.IsNullOrWhiteSpace(autoRunSummary.FinalMaskReviewReasons))
            {
                finalRisk = true;
                reasons.Add("final-review-reasons");
            }

            if (autoRunSummary.FinalMaskShortGapCount > 0
                || autoRunSummary.FinalMaskPerFaceShortGapCount > 0
                || autoRunSummary.FinalMaskLargeJumpGapCount > 0)
            {
                finalRisk = true;
                reasons.Add("final-gap-risk");
            }

            if (finalSceneCutRemovalRate > FinalSceneCutRemovalRateThreshold
                || finalSceneCutProtectedRate > FinalSceneCutProtectedRateThreshold
                || autoRunSummary.FinalProtectedSceneCarryFrameCount > 0
                || autoRunSummary.FinalSceneCutProtectedFrameCount > 0
                || autoRunSummary.FinalSceneCutCarryRemovedCount > 0)
            {
                finalRisk = true;
                reasons.Add("final-scene-cut-carry");
            }
            if (autoRunSummary.FinalGapFillBlockedCutGapFrames > 0
                || autoRunSummary.FinalGapFillBlockedCutGapFramesBeforeCut > 0
                || autoRunSummary.FinalGapFillBlockedCutGapFramesAfterCut > 0
                || autoRunSummary.FinalGapFillBlockedCleanupGapFrames > 0
                || autoRunSummary.FinalGapFillBlockedSceneCarryGapFrames > 0)
            {
                finalRisk = true;
                reasons.Add("final-gap-fill-blocked");
            }
            if (autoRunSummary.FinalOffModeSceneCutResetPairCount > 0)
            {
                finalRisk = true;
                reasons.Add("auto-off-mode-scene-cut-reset");
            }
            if (autoRunSummary.FinalOffModeWeakCleanupCount > 0)
            {
                finalRisk = true;
                reasons.Add($"auto-off-mode-weak-cleanup:{finalOffModeWeakCleanupRate:0.0000}");
            }
            if (offModeSceneCutResetBeforeRate > OffModeSceneCutResetRateThreshold)
            {
                finalRisk = true;
                reasons.Add($"auto-off-mode-reset-before-rate:{offModeSceneCutResetBeforeRate:0.0000}");
            }
            if (offModeSceneCutResetAfterRate > OffModeSceneCutResetRateThreshold)
            {
                finalRisk = true;
                reasons.Add($"auto-off-mode-reset-after-rate:{offModeSceneCutResetAfterRate:0.0000}");
            }

            bool sampleRisk = autoRunSummary.SampleReviewRequired;
            if (autoRunSummary.SampleReviewReasons != "none" &&
                !string.IsNullOrWhiteSpace(autoRunSummary.SampleReviewReasons))
            {
                sampleRisk = true;
                reasons.Add("sample-review-reasons");
            }

            if (sampleWindowFrames >= SampleGapRateMinWindowFrames && sampleMissRecoveryRate > SampleMissRecoveryRateThreshold)
            {
                sampleRisk = true;
                reasons.Add("sample-miss-recovery-rate-above-threshold");
            }
            if (sampleWindowFrames >= SampleGapRateMinWindowFrames && sampleFpSuppressedRate > SampleFpSuppressedRateThreshold)
            {
                sampleRisk = true;
                reasons.Add("sample-fp-suppressed-rate-above-threshold");
            }
            if (sampleWindowFrames >= SampleGapRateMinWindowFrames && sampleGapFillBlockedRate > SampleFpSuppressedRateThreshold)
            {
                sampleRisk = true;
                reasons.Add("sample-gap-fill-blocked-rate-above-threshold");
            }
            if (sampleWindowFrames >= SampleGapRateMinWindowFrames &&
                sampleOffModeWeakCleanupRate > SampleFpSuppressedRateThreshold)
            {
                sampleRisk = true;
                reasons.Add("sample-off-mode-weak-cleanup-rate-above-threshold");
            }
            if (autoRunSummary.SampleMissRecoveryFillCount > 0
                || autoRunSummary.SampleFalsePositiveSuppressionCount > 0
                || autoRunSummary.SampleProtectedSceneCarryFrameCount > 0
                || autoRunSummary.SampleShortGapCount > 0
                || autoRunSummary.SamplePerFaceShortGapCount > 0
                || autoRunSummary.SampleLargeJumpGapCount > 0)
            {
                sampleRisk = true;
                reasons.Add("sample-gap-risk");
            }
            if (autoRunSummary.SampleOffModeWeakCleanupSuppressionCount > 0)
            {
                sampleRisk = true;
                reasons.Add("sample-off-mode-weak-cleanup");
            }
            if (sampleMissRecoveryRate > SampleMissRecoveryRateThreshold)
                reasons.Add($"sample-miss-recovery-rate:{sampleMissRecoveryRate:0.0000}");
            if (sampleFpSuppressedRate > SampleFpSuppressedRateThreshold)
                reasons.Add($"sample-fp-suppressed-rate:{sampleFpSuppressedRate:0.0000}");
            if (sampleGapFillBlockedRate > SampleFpSuppressedRateThreshold)
                reasons.Add($"sample-gap-fill-blocked-rate:{sampleGapFillBlockedRate:0.0000}");
            if (finalGapFillBlockedRate > SampleFpSuppressedRateThreshold)
                reasons.Add($"final-gap-fill-blocked-rate:{finalGapFillBlockedRate:0.0000}");
            if (finalOffModeWeakCleanupRate > SampleFpSuppressedRateThreshold)
                reasons.Add($"final-off-mode-weak-cleanup-rate:{finalOffModeWeakCleanupRate:0.0000}");
            if (finalSceneCutRemovalRate > 0.0)
                reasons.Add($"final-scene-cut-removal-rate:{finalSceneCutRemovalRate:0.0000}");
            if (finalSceneCutProtectedRate > 0.0)
                reasons.Add($"final-scene-cut-protected-rate:{finalSceneCutProtectedRate:0.0000}");

            if (autoRunSummary.FinalMaskFrames == 0)
                reasons.Add("final-empty-mask");

            bool allowHybridCopy = !(finalRisk || sampleRisk);
            return (
                allowHybridCopy: allowHybridCopy,
                disableReasons: reasons.Distinct().ToList()
            );
        }

        private async Task<(string? Path, bool AllowOverwrite)> ResolveExportOutputPathAsync(
            string outputPath)
        {
            if (!File.Exists(outputPath))
                return (outputPath, false);

            var result = await ShowExportConflictDialogAsync(outputPath);
            if (result == ExportConflictResult.Overwrite)
                return (outputPath, true);

            if (result == ExportConflictResult.SaveAs)
                return (GetUniqueExportPath(outputPath), false);

            return (null, false);
        }

        private static string GetUniqueExportPath(string outputPath)
        {
            string dir = Path.GetDirectoryName(outputPath) ?? string.Empty;
            string ext = Path.GetExtension(outputPath);
            string baseName = Path.GetFileNameWithoutExtension(outputPath);

            for (int i = 1; i < 10000; i++)
            {
                string candidate = Path.Combine(dir, $"{baseName} ({i}){ext}");
                if (!File.Exists(candidate))
                    return candidate;
            }

            for (int attempt = 0; attempt < 100; attempt++)
            {
                string suffix = Guid.NewGuid().ToString("N")[..8];
                string candidate = Path.Combine(dir, $"{baseName} ({suffix}){ext}");
                if (!File.Exists(candidate))
                    return candidate;
            }

            throw new IOException("덮어쓰지 않는 고유한 내보내기 파일명을 만들 수 없습니다.");
        }

        public Task<bool> RunAutoAsync(
            bool exportAfter,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default,
            IProgress<ExportProgress>? exportProgress = null)
        {
            return _autoRunCoordinator.RunAsync(
                exportAfter,
                progress,
                cancellationToken,
                exportProgress);
        }

        partial void OnAutoAnomalyCountChanged(int value)
        {
            OnPropertyChanged(nameof(AutoSummaryVisible));
            OnPropertyChanged(nameof(AutoSummaryText));
        }

        partial void OnHideResolvedIssuesChanged(bool value)
        {
            _issueReview.SetHideResolved(value);
        }

        private async void OnAutoRequested()
        {
            try
            {
                if (Mode == WorkspaceMode.Manual)
                {
                    await RunAutoSingleFrameAsync();
                    ToolPanel.CurrentMode = EditMode.Manual;
                    return;
                }

                await RunAutoAsync(exportAfter: false);
            }
            catch (Exception ex)
            {
                await ShowAutoErrorAsync(ex, isDuringRun: true);
            }
        }

        private void OnAutoCancelRequested()
        {
            _autoRunCoordinator.Cancel();
        }

        private void OnExportCancelRequested()
        {
            _exportCoordinator.Cancel();
        }

        private Task ShowAutoErrorAsync(Exception ex, bool isDuringRun)
        {
            string title = isDuringRun ? "자동 모드 실행 중 오류" : "자동 모드 준비 실패";
            string message = BuildAutoErrorMessage(ex);
            return ShowErrorDialogAsync(title, message);
        }

        private Task ShowExportErrorAsync(Exception ex)
        {
            string title = "내보내기 실패";
            string message = BuildAutoErrorMessage(ex);
            return ShowErrorDialogAsync(title, message);
        }

        private Task ShowErrorDialogAsync(string title, string message)
        {
            var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            var owner = lifetime?.MainWindow;
            if (owner == null)
                return Task.CompletedTask;

            var dialog = new ErrorDialog(title, message);
            return dialog.ShowDialog(owner);
        }

        private async Task<ExportConflictResult> ShowExportConflictDialogAsync(string outputPath)
        {
            var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            var owner = lifetime?.MainWindow;
            if (owner == null)
                return ExportConflictResult.Cancel;

            var dialog = new ExportConflictDialog(outputPath);
            return await dialog.ShowDialog<ExportConflictResult>(owner);
        }

        private static string BuildAutoErrorMessage(Exception ex)
        {
            if (ex is System.IO.FileNotFoundException fnf && !string.IsNullOrWhiteSpace(fnf.FileName))
                return $"{fnf.Message}\n누락 파일: {fnf.FileName}";

            return ex.Message;
        }

        [RelayCommand]
        private async Task GoBack()
        {
            if (_autoRunCoordinator.IsRunning || ToolPanel.IsAutoRunning)
                return;

            FramePreview.PersistCurrentMask();
            PersistWorkspaceState(includePreviewMask: false);

            if (_workspacePersistence != null)
            {
                try
                {
                    await _workspacePersistence.FlushAsync();
                }
                catch (Exception ex)
                {
                    await ShowErrorDialogAsync("워크스페이스 저장 실패", ex.Message);
                    return;
                }
            }

            _onBack?.Invoke();
        }

        private Task<bool> RunAutoSingleFrameAsync()
            => _autoRunCoordinator.RunSingleFrameAsync();

        [RelayCommand]
        private void NextAutoAnomaly()
        {
            JumpAutoAnomaly(forward: true);
        }

        [RelayCommand]
        private void PrevAutoAnomaly()
        {
            JumpAutoAnomaly(forward: false);
        }

        private void JumpAutoAnomaly(bool forward)
        {
            if (!_issueReview.TryGetAdjacentAnomaly(
                    FrameList.SelectedFrameIndex,
                    forward,
                    out int targetFrame))
            {
                return;
            }

            FrameList.SelectedFrameIndex = targetFrame;
            RefreshIssueTimesInBackground(targetFrame);
        }

        [RelayCommand]
        private void JumpToIssue(int frameIndex)
        {
            int targetFrame = Math.Clamp(frameIndex, 0, FrameList.TotalFrames - 1);
            FrameList.SelectedFrameIndex = targetFrame;
            RefreshIssueTimesInBackground(targetFrame);
        }

        [RelayCommand]
        private void ReviewAutoAnomalies()
        {
            if (!_issueReview.TryGetFirstAnomaly(out int targetFrame))
                return;

            FrameList.SelectedFrameIndex = targetFrame;
            RefreshIssueTimesInBackground(targetFrame);
        }

        private void RefreshIssueTimesInBackground(int? preferredFrameIndex = null)
        {
            _issueReview.RefreshTimes(
                FrameList.ThumbnailProvider,
                preferredFrameIndex ?? FrameList.SelectedFrameIndex,
                FrameList.Fps,
                FrameList.SecondsPerScreen);
        }

        private void CancelIssueTimeRefresh()
            => _issueReview.CancelTimeRefresh();

        private void OnMaskEdited(int frameIndex)
            => _issueReview.ResolveIssueForFrame(frameIndex);

        private void ApplyIssueReviewState()
        {
            IssueReviewStateSnapshot state = _issueReview.CreateStateSnapshot();
            FrameList.NoFaceIssueFrames = state.NoFaceFrames;
            FrameList.LowConfidenceIssueFrames = state.LowConfidenceFrames;
            FrameList.FlickerIssueFrames = state.FlickerFrames;
            AutoAnomalyCount = state.Anomalies.Length;
            HasAutoAnomalies = state.Anomalies.Length > 0;
        }

        public void RestoreFromStore(WorkspaceStateStore store)
        {
            if (store == null)
                return;

            if (!store.TryLoadWorkspace(FrameList.VideoPath, Mode, _maskProvider, out var snapshot))
                return;

            if (snapshot == null)
                return;

            ApplySnapshot(snapshot);
        }

        public void UpdateDetectorOptions(FaceOnnxDetectorOptions? options)
        {
            _detectorOptions = options ?? new FaceOnnxDetectorOptions();
            _detectorFactoryOptions = FaceDetectorFactoryOptions.ForOnnx(_detectorOptions);
        }

        public void UpdateDetectorFactoryOptions(FaceDetectorFactoryOptions? options)
        {
            _detectorFactoryOptions = options ?? FaceDetectorFactoryOptions.ForOnnx(new FaceOnnxDetectorOptions());
            _detectorOptions = _detectorFactoryOptions.FaceOnnxOptions ?? new FaceOnnxDetectorOptions();
        }

        public void UpdateAutoOptions(AutoMaskOptions options)
        {
            _autoOptions = options ?? new AutoMaskOptions();
        }

        public void PersistWorkspaceState()
        {
            PersistWorkspaceState(includePreviewMask: true);
        }

        public void PersistWorkspaceStateImmediate()
        {
            if (_stateStore == null)
                return;

            FramePreview.PersistCurrentMask();
            WorkspaceSnapshot snapshot = BuildSnapshot();
            if (_workspacePersistence != null)
                _workspacePersistence.SaveNow(snapshot);
            else
                _stateStore.SaveWorkspace(snapshot, _maskProvider);
        }

        private void PersistWorkspaceState(bool includePreviewMask)
        {
            if (_workspacePersistence == null)
                return;

            if (includePreviewMask)
                FramePreview.PersistCurrentMask();

            WorkspaceSnapshot snapshot = BuildSnapshot();
            if (!TryBeginLifetimeOperation())
                return;

            Task saveTask;
            try
            {
                saveTask = _workspacePersistence.QueueSaveAsync(snapshot);
            }
            catch
            {
                EndLifetimeOperation();
                throw;
            }

            _ = ObserveWorkspacePersistenceAsync(saveTask);
        }

        private async Task ObserveWorkspacePersistenceAsync(Task saveTask)
        {
            try
            {
                await saveTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[WorkspacePersistence] background save failed: {ex.Message}");
            }
            finally
            {
                EndLifetimeOperation();
            }
        }

        private WorkspaceSnapshot BuildSnapshot()
        {
            WorkspaceAutoExportGateState exportState = _exportCoordinator.GateState;
            AutoMaskRunStateSnapshot autoState = _autoRunCoordinator.CreateStateSnapshot();
            var state = new WorkspaceStateCapture(
                FrameList.VideoPath,
                Mode,
                FrameList.SelectedFrameIndex,
                FrameList.ViewStartSeconds,
                FrameList.SecondsPerScreen,
                FrameList.TimelineExtentSeconds,
                autoState.ResumeIndex,
                autoState.Completed,
                autoState.RunSignature,
                autoState.ExecutionSignature,
                exportState.Required,
                exportState.Passed,
                exportState.Failure,
                exportState.HybridPolicyAvailable,
                exportState.AllowHybridCopy,
                exportState.HybridDisableReasons);
            return WorkspaceStateMapper.CreateSnapshot(state, DateTimeOffset.Now);
        }

        private void ApplySnapshot(WorkspaceSnapshot snapshot)
        {
            if (snapshot == null)
                return;

            WorkspaceRestoreState state = WorkspaceStateMapper.CreateRestoreState(
                snapshot,
                FrameList.SecondsPerScreen,
                FrameList.TotalFrames,
                WorkspaceExportCoordinator.HybridCopyDisabledReason);

            _autoRunCoordinator.RestoreState(
                state.AutoResumeIndex,
                state.AutoCompleted,
                state.AutoRunSignature,
                state.AutoExecutionSignature);
            _exportCoordinator.ApplyGateState(state.ExportGateState);

            FrameList.SecondsPerScreen = state.SecondsPerScreen;
            FrameList.RestoreTimelineExtentSeconds(state.TimelineExtentSeconds);
            FrameList.ViewStartSeconds = WorkspaceStateMapper.ClampViewStart(
                state.RequestedViewStartSeconds,
                FrameList.TimelineExtentSeconds,
                FrameList.SecondsPerScreen);
            FrameList.SelectedFrameIndex = state.SelectedFrameIndex;
        }

        private bool TryBeginLifetimeOperation()
        {
            lock (_lifetimeSync)
            {
                if (_disposeRequested)
                    return false;

                _activeLifetimeOperations++;
                return true;
            }
        }

        private void EndLifetimeOperation()
        {
            bool disposeNow = false;
            lock (_lifetimeSync)
            {
                if (_activeLifetimeOperations > 0)
                    _activeLifetimeOperations--;

                if (_disposeRequested &&
                    _activeLifetimeOperations == 0 &&
                    !_resourcesDisposed)
                {
                    _resourcesDisposed = true;
                    disposeNow = true;
                }
            }

            if (disposeNow)
                ScheduleOwnedResourceDispose();
        }

        private void ScheduleOwnedResourceDispose()
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                DisposeOwnedResources();
                return;
            }

            Dispatcher.UIThread.Post(DisposeOwnedResources);
        }

        private void DisposeOwnedResources()
        {
            _sessionPlaybackCoordinator.Dispose();
            _autoRunCoordinator.Dispose();
            _exportCoordinator.Dispose();
            _issueReview.Dispose();
            _workspacePersistence?.Dispose();
            FramePreview.Dispose();
            FrameList.Dispose();
            _maskProvider.Dispose();
        }

        public void Dispose()
        {
            bool disposeNow = false;
            lock (_lifetimeSync)
            {
                if (_disposeRequested)
                    return;

                _disposeRequested = true;
                if (_activeLifetimeOperations == 0 && !_resourcesDisposed)
                {
                    _resourcesDisposed = true;
                    disposeNow = true;
                }
            }

            _autoRunCoordinator.Cancel();
            _exportCoordinator.Cancel();
            _sessionPlaybackCoordinator.CancelInitialization();
            CancelIssueTimeRefresh();

            if (disposeNow)
                ScheduleOwnedResourceDispose();
        }


    }
}
