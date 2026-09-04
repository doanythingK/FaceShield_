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
using FaceShield.Services.Video.Session;
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
        private bool _sessionInitialized;
        // 프레임별 최종 마스크 저장소
        private readonly FrameMaskProvider _maskProvider = new();

        private CancellationTokenSource? _sessionInitCts;
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
                ResolveExportOutputPathAsync,
                LogExportQualityGate);
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
            if (!deferSessionInit)
                InitializeSession(loadProgress, initializationToken);

            // 🔹 자동/최종 마스크 provider 주입
            FramePreview.SetMaskProvider(_maskProvider);

            FrameList.SelectedFrameIndexChanged += index =>
            {
                if (!FrameList.IsPlaying)
                {
                    if (_autoRunCoordinator.IsRunning && Mode == WorkspaceMode.Auto)
                    {
                        // 분석 중에는 타임라인 번호만 갱신합니다. 정확 프레임 seek는
                        // 종료 시 한 번 수행하고, FramePreview의 편집 대상 인덱스는
                        // 현재 표시 중인 exact 프레임에 그대로 유지합니다.
                        _autoRunCoordinator.MarkPreviewNeedsExactRefresh();
                    }
                    else
                    {
                        FramePreview.OnFrameIndexChanged(index);
                    }
                }
            };
            FrameList.PlaybackStopped += () =>
            {
                if (FrameList.SelectedFrameIndex >= 0)
                    FramePreview.OnPlaybackStopped(FrameList.SelectedFrameIndex);
            };
            FrameList.PlaybackStateChanged += isPlaying =>
            {
                if (isPlaying)
                {
                    int playbackTotalFrames = FrameList.IsTotalFramesEstimated
                        ? 0
                        : FrameList.TotalFrames;
                    bool playbackDecodedFrame = false;
                    FramePreview.StartPlayback(
                        FrameList.VideoPath,
                        FrameList.SelectedFrameIndex,
                        FrameList.Fps,
                        playbackTotalFrames,
                        frameIndex =>
                        {
                            playbackDecodedFrame = true;
                            FrameList.SetPlaybackFrameIndex(frameIndex);
                        },
                        () =>
                        {
                            if (FrameList.IsTotalFramesEstimated &&
                                playbackDecodedFrame &&
                                FrameList.SelectedFrameIndex >= 0)
                            {
                                FrameList.UpdateActualTotalFrames(
                                    FrameList.SelectedFrameIndex + 1);
                            }

                            FrameList.NotifyPlaybackStopped();
                        },
                        message =>
                        {
                            FrameList.NotifyPlaybackStopped();
                            _ = ShowErrorDialogAsync("재생 실패", message);
                        });
                }
                else
                {
                    FramePreview.StopPlayback();
                }
            };

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

            if (_sessionInitialized && FrameList.SelectedFrameIndex >= 0)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_sessionInitialized && FrameList.SelectedFrameIndex >= 0)
                        FramePreview.OnFrameIndexChanged(FrameList.SelectedFrameIndex);
                });
            }
        }

        public async Task EnsureSessionInitializedAsync(
            IProgress<int>? loadProgress,
            CancellationToken cancellationToken = default)
        {
            if (_sessionInitialized || !TryBeginLifetimeOperation())
                return;

            var sessionCts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
            CancellationTokenSource? previousSessionCts =
                Interlocked.Exchange(ref _sessionInitCts, sessionCts);
            if (previousSessionCts != null)
            {
                try { previousSessionCts.Cancel(); }
                catch (ObjectDisposedException) { }
            }

            try
            {
                if (_sessionInitialized)
                    return;

                var session = await Task.Run(
                    () => new VideoSession(
                        FrameList.VideoPath,
                        progress: loadProgress,
                        cancellationToken: sessionCts.Token),
                    sessionCts.Token);

                lock (_lifetimeSync)
                {
                    if (_disposeRequested)
                    {
                        session.Dispose();
                        return;
                    }
                }

                FramePreview.InitializeSession(session);
                FrameList.SetThumbnailProvider(session.ThumbnailProvider);
                _sessionInitialized = true;

                if (FrameList.SelectedFrameIndex >= 0)
                    FramePreview.OnFrameIndexChanged(FrameList.SelectedFrameIndex);
            }
            catch (OperationCanceledException) when (sessionCts.IsCancellationRequested)
            {
                if (cancellationToken.IsCancellationRequested)
                    throw;
            }
            finally
            {
                Interlocked.CompareExchange(ref _sessionInitCts, null, sessionCts);
                sessionCts.Dispose();
                EndLifetimeOperation();
            }
        }

        private void InitializeSession(
            IProgress<int>? loadProgress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var session = new VideoSession(
                FrameList.VideoPath,
                progress: loadProgress,
                cancellationToken: cancellationToken);
            FramePreview.InitializeSession(session);
            FrameList.SetThumbnailProvider(session.ThumbnailProvider);
            _sessionInitialized = true;
        }


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
                autoRunOptions);
        }

        private static void LogExportQualityGate(
            AutoMaskRunSummary? autoRunSummary,
            ExportRunSummary exportSummary,
            bool allowHybridCopy,
            IReadOnlyList<string>? autoHybridDisableReasons)
        {
            string run = string.IsNullOrWhiteSpace(exportSummary.RunId)
                ? (autoRunSummary?.RunId ?? "n/a")
                : exportSummary.RunId;
            int outputFrames = exportSummary.SubmittedVideoFrames > 0
                ? exportSummary.SubmittedVideoFrames
                : exportSummary.Frames;
            int totalFrames = Math.Max(1, Math.Max(autoRunSummary?.TotalFrames ?? 0, outputFrames));
            int inputVideoPackets = Math.Max(
                0,
                exportSummary.HybridCopyUsed
                    ? Math.Max(exportSummary.CopiedSourceVideoPackets, exportSummary.EncodedSourceVideoPackets)
                    : exportSummary.InputVideoPackets);
            int droppedPackets = Math.Max(0, exportSummary.DroppedVideoPackets);
            double packetDropRate = inputVideoPackets > 0 ? (double)droppedPackets / inputVideoPackets : 0.0;
            int expectedVideoFrames = Math.Max(
                0,
                exportSummary.SubmittedVideoFrames + exportSummary.CopiedSourceVideoPackets);
            int droppedFrames = Math.Max(0, exportSummary.VideoFrameDropCount);
            double frameDropRate = expectedVideoFrames > 0
                ? (double)droppedFrames / expectedVideoFrames
                : 0.0;

            var riskReasons = new List<string>();
            int reviewRiskScore = 0;
            if (autoRunSummary?.FinalMaskReviewRequired == true)
            {
                reviewRiskScore++;
                riskReasons.Add("auto-review-required");
            }
            if (exportSummary.HybridCopyAttempted)
            {
                reviewRiskScore++;
                riskReasons.Add("hybrid-attempted");
            }
            if (exportSummary.HybridCopyUsed && exportSummary.HybridCopyTimestampFixCount > 0)
            {
                reviewRiskScore++;
                riskReasons.Add("hybrid-timestamp-fixes");
            }
            if (exportSummary.HybridCopyUsed &&
                exportSummary.HybridEncodedPacketFrameStep > 0 &&
                exportSummary.HybridCopyPacketFrameStep > 0 &&
                exportSummary.HybridEncodedPacketFrameStep != exportSummary.HybridCopyPacketFrameStep)
            {
                reviewRiskScore++;
                riskReasons.Add("hybrid-step-mismatch");
            }
            if (packetDropRate > 0.001 || frameDropRate > 0.001)
            {
                reviewRiskScore++;
                riskReasons.Add("video-drop-rate");
            }
            if (autoRunSummary != null &&
                (autoRunSummary.FinalMaskShortGapCount > 0 ||
                    autoRunSummary.FinalMaskPerFaceShortGapCount > 0 ||
                    autoRunSummary.FinalMaskLargeJumpGapCount > 0))
            {
                reviewRiskScore++;
                riskReasons.Add("auto-short-gap");
            }
            if (autoRunSummary != null &&
                (autoRunSummary.FinalSceneCutCarryRemovedCount > 0 ||
                    autoRunSummary.FinalSceneCutCarryPairCount > 0 ||
                    autoRunSummary.FinalSceneCutProtectedFrameCount > 0))
            {
                reviewRiskScore++;
                riskReasons.Add("auto-scene-cut-carry");
            }
            int offModeResetPairCount = autoRunSummary?.FinalOffModeSceneCutResetPairCount ?? 0;
            int finalOffModeWeakCleanupCount = autoRunSummary?.FinalOffModeWeakCleanupCount ?? 0;
            if (offModeResetPairCount > 0)
            {
                reviewRiskScore++;
                riskReasons.Add("auto-off-mode-scene-cut-reset");
            }
            if (finalOffModeWeakCleanupCount > 0)
            {
                reviewRiskScore++;
                riskReasons.Add("auto-off-mode-weak-cleanup");
            }
            if (autoRunSummary != null &&
                (!string.IsNullOrWhiteSpace(autoRunSummary.FinalMaskReviewReasons) &&
                    autoRunSummary.FinalMaskReviewReasons != "none"))
            {
                reviewRiskScore++;
                riskReasons.Add("auto-review-reasons");
            }

            if (!string.IsNullOrWhiteSpace(exportSummary.PacketLossFallbackReason))
            {
                reviewRiskScore++;
                riskReasons.Add("packet-loss-fallback");
            }
            if (!string.IsNullOrWhiteSpace(exportSummary.HybridCopyFallbackReason))
            {
                reviewRiskScore++;
                riskReasons.Add("hybrid-fallback");
            }
            if (!allowHybridCopy)
            {
                reviewRiskScore++;
                riskReasons.Add("auto-hybrid-disabled-by-summary");
            }
            if (autoHybridDisableReasons != null)
            {
                foreach (string reason in autoHybridDisableReasons)
                {
                    reviewRiskScore++;
                    riskReasons.Add($"auto-hybrid-reason:{reason}");
                }
            }

            int sampleWindowFrames = autoRunSummary?.SampleWindowFrames > 0
                ? autoRunSummary.SampleWindowFrames
                : autoRunSummary != null && autoRunSummary.SourceFps > 0
                    ? Math.Min(autoRunSummary.TotalFrames, (int)Math.Round(autoRunSummary.SourceFps * 30.0))
                    : Math.Min(autoRunSummary?.TotalFrames ?? 0, 900);
            int sampleFrameCount = autoRunSummary?.SampleFrameCount ?? 0;
            int sampleRowCount = autoRunSummary?.SampleRowCount ?? 0;
            int sampleShortGapCount = autoRunSummary?.SampleShortGapCount ?? 0;
            int samplePerFaceShortGapCount = autoRunSummary?.SamplePerFaceShortGapCount ?? 0;
            int sampleIsolatedFrameCount = autoRunSummary?.SampleIsolatedFrameCount ?? 0;
            int sampleLargeJumpGapCount = autoRunSummary?.SampleLargeJumpGapCount ?? 0;
            int sampleProtectedCarryCount = autoRunSummary?.SampleProtectedSceneCarryFrameCount ?? 0;
            int sampleGapFillBlockedCutGapFrames = autoRunSummary?.SampleGapFillBlockedCutGapFrames ?? 0;
            int sampleGapFillBlockedCutGapFramesBeforeCut = autoRunSummary?.SampleGapFillBlockedCutGapFramesBeforeCut ?? 0;
            int sampleGapFillBlockedCutGapFramesAfterCut = autoRunSummary?.SampleGapFillBlockedCutGapFramesAfterCut ?? 0;
            int sampleGapFillBlockedCleanupGapFrames = autoRunSummary?.SampleGapFillBlockedCleanupGapFrames ?? 0;
            int sampleGapFillBlockedSceneCarryGapFrames = autoRunSummary?.SampleGapFillBlockedSceneCarryGapFrames ?? 0;
            int sampleOffModeWeakCleanupSuppressionCount = autoRunSummary?.SampleOffModeWeakCleanupSuppressionCount ?? 0;
            int offModeResetRemoved = autoRunSummary?.FinalOffModeSceneCutResetRemovedFrameCount ?? 0;
            int offModeResetBeforeWindowFrames = autoRunSummary?.FinalOffModeSceneCutResetBeforeWindowFrameCount ?? 0;
            int offModeResetAfterWindowFrames = autoRunSummary?.FinalOffModeSceneCutResetAfterWindowFrameCount ?? 0;
            int offModeResetRemovedBeforeFrames = autoRunSummary?.FinalOffModeSceneCutResetRemovedBeforeFrameCount ?? 0;
            int offModeResetRemovedAfterFrames = autoRunSummary?.FinalOffModeSceneCutResetRemovedAfterFrameCount ?? 0;
            double offModeResetBeforeRate = offModeResetBeforeWindowFrames > 0
                ? offModeResetRemovedBeforeFrames / (double)offModeResetBeforeWindowFrames
                : 0.0;
            double offModeResetAfterRate = offModeResetAfterWindowFrames > 0
                ? offModeResetRemovedAfterFrames / (double)offModeResetAfterWindowFrames
                : 0.0;
            bool sampleReviewRequired = autoRunSummary?.SampleReviewRequired == true;
            string sampleReviewReasons = autoRunSummary?.SampleReviewReasons ?? "none";
            var sampleReasons = new List<string>();
            int sampleRiskScore = 0;
            if (sampleReviewRequired || string.IsNullOrWhiteSpace(sampleReviewReasons) == false && sampleReviewReasons != "none")
            {
                sampleRiskScore++;
                sampleReasons.Add("sample-review-required");
            }
            if (sampleShortGapCount > 0 || samplePerFaceShortGapCount > 0 || sampleLargeJumpGapCount > 0)
            {
                sampleRiskScore++;
                sampleReasons.Add("sample-short-gap");
            }
            if (sampleProtectedCarryCount > 0)
            {
                sampleRiskScore++;
                sampleReasons.Add("sample-scene-carry");
            }
            if (sampleGapFillBlockedCutGapFrames > 0
                || sampleGapFillBlockedCutGapFramesBeforeCut > 0
                || sampleGapFillBlockedCutGapFramesAfterCut > 0
                || sampleGapFillBlockedCleanupGapFrames > 0
                || sampleGapFillBlockedSceneCarryGapFrames > 0)
            {
                sampleRiskScore++;
                sampleReasons.Add("sample-gap-fill-blocked");
            }
            if (sampleOffModeWeakCleanupSuppressionCount > 0)
            {
                sampleRiskScore++;
                sampleReasons.Add("sample-off-mode-weak-cleanup");
            }
            string sampleRiskLabel = sampleRiskScore >= 3
                ? "high"
                : sampleRiskScore >= 2
                    ? "medium"
                    : sampleRiskScore >= 1
                        ? "low"
                        : "safe";

            string hybridRange = exportSummary.HybridCopyUsed
                ? $"{exportSummary.HybridWindowStartFrame}-{exportSummary.HybridWindowEndFrame}"
                : "n/a";
            int hybridWindowLength = exportSummary.HybridCopyUsed
                ? Math.Max(0, exportSummary.HybridWindowEndFrame - exportSummary.HybridWindowStartFrame)
                : 0;
            double throughputFps = exportSummary.TotalMs > 0 && outputFrames > 0
                ? outputFrames * 1000.0 / exportSummary.TotalMs
                : 0.0;
            double finalSceneCutRemovalRate = autoRunSummary != null && autoRunSummary.FinalSceneCutCarryPairCount > 0
                ? autoRunSummary.FinalSceneCutCarryRemovedCount / (double)autoRunSummary.FinalSceneCutCarryPairCount
                : 0.0;
            double finalSceneCutProtectedRate = autoRunSummary != null && autoRunSummary.FinalSceneCutCarryPairCount > 0
                ? autoRunSummary.FinalSceneCutProtectedFrameCount / (double)autoRunSummary.FinalSceneCutCarryPairCount
                : 0.0;
            int finalGapFillBlockedCutGapFrames = autoRunSummary?.FinalGapFillBlockedCutGapFrames ?? 0;
            int finalGapFillBlockedCutGapFramesBeforeCut = autoRunSummary?.FinalGapFillBlockedCutGapFramesBeforeCut ?? 0;
            int finalGapFillBlockedCutGapFramesAfterCut = autoRunSummary?.FinalGapFillBlockedCutGapFramesAfterCut ?? 0;
            int finalGapFillBlockedCleanupGapFrames = autoRunSummary?.FinalGapFillBlockedCleanupGapFrames ?? 0;
            int finalGapFillBlockedSceneCarryGapFrames = autoRunSummary?.FinalGapFillBlockedSceneCarryGapFrames ?? 0;
            int finalGapFillBlockedTotal = finalGapFillBlockedCutGapFrames
                + finalGapFillBlockedCutGapFramesBeforeCut
                + finalGapFillBlockedCutGapFramesAfterCut
                + finalGapFillBlockedCleanupGapFrames
                + finalGapFillBlockedSceneCarryGapFrames;
            int finalGapFillBlockedWindowFrames = Math.Max(1, autoRunSummary?.TotalFrames ?? 0);
            double finalGapFillBlockedRate = finalGapFillBlockedWindowFrames > 0
                ? finalGapFillBlockedTotal / (double)finalGapFillBlockedWindowFrames
                : 0.0;
            int opsWindowFrames = sampleWindowFrames > 0 ? sampleWindowFrames : 1;
            double sampleMissRecoveryRate = autoRunSummary != null
                ? autoRunSummary.SampleMissRecoveryFillCount / (double)opsWindowFrames
                : 0.0;
            double sampleFpSuppressedRate = autoRunSummary != null
                ? autoRunSummary.SampleFalsePositiveSuppressionCount / (double)opsWindowFrames
                : 0.0;
            int sampleGapFillBlockedTotal = autoRunSummary != null
                ? autoRunSummary.SampleGapFillBlockedCutGapFrames
                    + autoRunSummary.SampleGapFillBlockedCutGapFramesBeforeCut
                    + autoRunSummary.SampleGapFillBlockedCutGapFramesAfterCut
                    + autoRunSummary.SampleGapFillBlockedCleanupGapFrames
                    + autoRunSummary.SampleGapFillBlockedSceneCarryGapFrames
                : 0;
            double sampleGapFillBlockedRate = opsWindowFrames > 0
                ? sampleGapFillBlockedTotal / (double)opsWindowFrames
                : 0.0;
            string riskLabel = reviewRiskScore >= 3
                ? "high"
                : reviewRiskScore >= 2
                    ? "medium"
                    : reviewRiskScore >= 1
                        ? "low"
                        : "safe";

            System.Diagnostics.Debug.WriteLine(
                $"[QualityGate] runId={run}, totalFrames={totalFrames}, sampleWindowFrames={sampleWindowFrames}, exportMode={exportSummary.ExportMode}, throughputFps={throughputFps:0.00}, risk={riskLabel}, riskReasons={FormatTextListForLog(riskReasons)}, frameDropRate={frameDropRate:0.000000}, frameDrops={droppedFrames}/{expectedVideoFrames}, packetDropRate={packetDropRate:0.000000}, packetDrops={droppedPackets}/{inputVideoPackets}, outputFrames={outputFrames}, hybridRequested={allowHybridCopy.ToString().ToLowerInvariant()}, hybridUsed={exportSummary.HybridCopyUsed.ToString().ToLowerInvariant()}, hybridRange={hybridRange}, hybridLength={hybridWindowLength}, hybridFixes={exportSummary.HybridCopyTimestampFixCount}, hybridTransitions={exportSummary.HybridModeTransitionCount}, copiedPackets={exportSummary.CopiedVideoPackets}, copiedSourcePackets={exportSummary.CopiedSourceVideoPackets}, encodedSourcePackets={exportSummary.EncodedSourceVideoPackets}, outputPackets={exportSummary.OutputVideoPackets}, autoReviewRequired={autoRunSummary?.FinalMaskReviewRequired.ToString().ToLowerInvariant() ?? "n/a"}, finalShortGaps={autoRunSummary?.FinalMaskShortGapCount ?? -1}, finalPerFaceShortGaps={autoRunSummary?.FinalMaskPerFaceShortGapCount ?? -1}, finalLargeJumps={autoRunSummary?.FinalMaskLargeJumpGapCount ?? -1}, finalCarryFrames={autoRunSummary?.FinalProtectedSceneCarryFrameCount ?? -1}, sceneCut=preGuard:{autoRunSummary?.FinalSceneCutPreGuardPairCount ?? -1},preStrong:{autoRunSummary?.FinalSceneCutPreStrongProbePairCount ?? -1},postGuard:{autoRunSummary?.FinalSceneCutPostGuardPairCount ?? -1},postStrong:{autoRunSummary?.FinalSceneCutPostStrongProbePairCount ?? -1},carryPairs:{autoRunSummary?.FinalSceneCutCarryPairCount ?? -1},carryRemoved:{autoRunSummary?.FinalSceneCutCarryRemovedCount ?? -1},carryProtected:{autoRunSummary?.FinalSceneCutProtectedFrameCount ?? -1},offModeResetPairs:{offModeResetPairCount},offModeResetRemoved:{offModeResetRemoved},offModeResetWindows={offModeResetBeforeWindowFrames}/{offModeResetAfterWindowFrames},offModeResetRemovedWindows={offModeResetRemovedBeforeFrames}/{offModeResetRemovedAfterFrames},offModeResetBeforeRate={offModeResetBeforeRate:0.0000},offModeResetAfterRate={offModeResetAfterRate:0.0000}, finalOffModeWeakCleanupSuppressed={finalOffModeWeakCleanupCount}, autoHybridDisableReasons={FormatTextListForLog(autoHybridDisableReasons ?? Array.Empty<string>())}");
            System.Diagnostics.Debug.WriteLine(
                $"[QualityGateHybridTiming] runId={run}, encStep={exportSummary.HybridEncodedPacketFrameStep}, copyStep={exportSummary.HybridCopyPacketFrameStep}, frameGap={Math.Abs(exportSummary.HybridEncodedPacketFrameStep - exportSummary.HybridCopyPacketFrameStep)}, fallback={exportSummary.HybridCopyFallbackReason ?? "n/a"}, copyFixes={exportSummary.HybridCopyTimestampFixCount}, mode={exportSummary.ExportMode}, transitions={exportSummary.HybridModeTransitionCount}");
            System.Diagnostics.Debug.WriteLine(
                $"[QualityGateSample] runId={run}, mode={autoRunSummary?.Mode ?? "n/a"}, sampleWindow={autoRunSummary?.SampleWindowFrames ?? 0}, sampleFrames={sampleFrameCount}, sampleRows={sampleRowCount}, sampleRisk={sampleRiskLabel}, sampleRiskReasons={FormatTextListForLog(sampleReasons)}, sampleShortGaps={sampleShortGapCount}, samplePerFaceShortGaps={samplePerFaceShortGapCount}, sampleIsolated={sampleIsolatedFrameCount}, sampleLargeJumps={sampleLargeJumpGapCount}, sampleProtectedCarry={sampleProtectedCarryCount}, sampleReviewRequired={sampleReviewRequired.ToString().ToLowerInvariant()}, sampleReviewReasons={sampleReviewReasons}, sampleOffModeWeakCleanupSuppressed={sampleOffModeWeakCleanupSuppressionCount}, packetLossFallbackReason={exportSummary.PacketLossFallbackReason ?? "n/a"}, hybridFallbackReason={exportSummary.HybridCopyFallbackReason ?? "n/a"}");
            System.Diagnostics.Debug.WriteLine(
                $"[QualityGateSampleGapFillBlocked] runId={run}, sampleGapFillBlocked={sampleGapFillBlockedCutGapFrames}/{sampleGapFillBlockedCutGapFramesBeforeCut}/{sampleGapFillBlockedCutGapFramesAfterCut}/{sampleGapFillBlockedCleanupGapFrames}/{sampleGapFillBlockedSceneCarryGapFrames}, sampleOffModeWeakCleanupSuppressed={sampleOffModeWeakCleanupSuppressionCount}, sampleWindow={autoRunSummary?.SampleWindowFrames ?? 0}");
            int sampleGapCount = sampleShortGapCount + samplePerFaceShortGapCount + sampleLargeJumpGapCount;
            string sampleGapRisk = sampleWindowFrames > 0
                ? $"{sampleGapCount}/{sampleWindowFrames}"
                : "0/0";
            System.Diagnostics.Debug.WriteLine(
                $"[QualityGateOps] runId={run}, finalSceneCutRemovalRate={finalSceneCutRemovalRate:0.0000}, finalSceneCutProtectedRate={finalSceneCutProtectedRate:0.0000}, finalGapFillBlocked={finalGapFillBlockedCutGapFrames}/{finalGapFillBlockedCutGapFramesBeforeCut}/{finalGapFillBlockedCutGapFramesAfterCut}/{finalGapFillBlockedCleanupGapFrames}/{finalGapFillBlockedSceneCarryGapFrames}, finalGapFillBlockedRate={finalGapFillBlockedRate:0.0000}, finalOffModeWeakCleanupSuppressed={finalOffModeWeakCleanupCount}, sampleOffModeWeakCleanupSuppressed={sampleOffModeWeakCleanupSuppressionCount}, offModeResetPairCount={offModeResetPairCount}, offModeResetBeforeRate={offModeResetBeforeRate:0.0000}, offModeResetAfterRate={offModeResetAfterRate:0.0000}, sampleMissRecoveryRate={sampleMissRecoveryRate:0.0000}, sampleFpSuppressedRate={sampleFpSuppressedRate:0.0000}, sampleGapRisk={sampleGapRisk}, finalGapFillRiskRate={finalGapFillBlockedRate:0.0000}, frameDropRate={frameDropRate:0.000000}, throughputFps={throughputFps:0.00}, hybridRequested={allowHybridCopy.ToString().ToLowerInvariant()}, hybridUsed={exportSummary.HybridCopyUsed.ToString().ToLowerInvariant()}, hybridFixFallback={exportSummary.HybridCopyFallbackReason ?? "n/a"}, packetFallback={exportSummary.PacketLossFallbackReason ?? "n/a"}");
        }

        private static string FormatTextListForLog(IReadOnlyList<string> values)
        {
            if (values.Count == 0)
                return "none";

            const int maxValues = 12;
            string text = string.Join(",", values.Take(maxValues));
            return values.Count > maxValues
                ? $"{text},+{values.Count - maxValues}"
                : text;
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
            try { _sessionInitCts?.Cancel(); }
            catch { }
            CancelIssueTimeRefresh();

            if (disposeNow)
                ScheduleOwnedResourceDispose();
        }


    }
}
