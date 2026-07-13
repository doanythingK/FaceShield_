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
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FaceShield.ViewModels.Pages
{
    public partial class WorkspaceViewModel : ViewModelBase
    {
        public ToolPanelViewModel ToolPanel { get; } = new();
        public FramePreviewViewModel FramePreview { get; }
        public FrameListViewModel FrameList { get; }
        private readonly Action? _onBack;
        private AutoMaskOptions _autoOptions;
        private FaceOnnxDetectorOptions _detectorOptions;
        private FaceDetectorFactoryOptions _detectorFactoryOptions;
        private readonly WorkspaceStateStore? _stateStore;
        private int[] _autoAnomalies = Array.Empty<int>();
        private const float LowConfidenceMargin = 0.05f;
        private const int SuspiciousNoFaceMaxGap = 8;
        private const int NoDetectionReviewSampleCount = 12;
        private const int SparseNoFaceReviewSampleCount = 12;
        private const double SparseNoFaceReviewMaxCoverageRatio = 0.20;
        private const int AutoDetectionCompletionTailToleranceFrames = 0;
        private int _autoResumeIndex;
        private bool _autoCompleted;
        private string? _autoRunSignature;
        private string? _autoExecutionSignature;
        private int _autoLastProcessedFrame = -1;
        private DateTime _autoLastProcessedAtUtc = DateTime.MinValue;
        private bool _sessionInitialized;
        private readonly Queue<(DateTime Timestamp, int FrameIndex)> _exportEtaSamples = new();
        private (DateTime Timestamp, int FrameIndex) _exportLastSample;
        private readonly ObservableCollection<IssueEntryViewModel> _noFaceIssueEntries = new();
        private readonly ObservableCollection<IssueEntryViewModel> _lowConfidenceIssueEntries = new();
        private readonly ObservableCollection<IssueEntryViewModel> _flickerIssueEntries = new();
        private HashSet<int> _noFaceIssueSet = new();
        private HashSet<int> _lowConfidenceIssueSet = new();
        private HashSet<int> _flickerIssueSet = new();

        // 프레임별 최종 마스크 저장소
        private readonly FrameMaskProvider _maskProvider = new();

        // 🔹 자동 분석 상태 관리용 (최소한 재진입 방지)
        private bool _isAutoRunning;
        private long _autoLastPreviewTick;
        private bool _autoPreviewNeedsExactRefresh;
        private CancellationTokenSource? _autoCts;
        private CancellationTokenSource? _exportCts;
        private bool _autoExportGateRequired;
        private bool _autoExportGatePassed;
        private string? _autoExportGateFailure;
        private AutoMaskRunSummary? _lastCompletedAutoRunSummary;
        private bool _autoExportHybridPolicyAvailable;
        private const string HybridCopyDisabledReason = "bitstream-compatibility-unverified";
        private bool _autoExportAllowHybridCopy;
        private string? _autoExportHybridDisableReasons;

        // 🔹 현재 워크스페이스 모드 (Auto / Manual)
        public WorkspaceMode Mode { get; }

        [ObservableProperty]
        private int autoAnomalyCount;

        [ObservableProperty]
        private bool hasAutoAnomalies;

        public ObservableCollection<IssueEntryViewModel> NoFaceIssueEntries => _noFaceIssueEntries;
        public ObservableCollection<IssueEntryViewModel> LowConfidenceIssueEntries => _lowConfidenceIssueEntries;
        public ObservableCollection<IssueEntryViewModel> FlickerIssueEntries => _flickerIssueEntries;

        public bool AutoSummaryVisible => AutoAnomalyCount > 0;
        public string AutoSummaryText => $"문제 프레임 {AutoAnomalyCount}개";

        [ObservableProperty]
        private bool hideResolvedIssues = true;

        public bool NeedsAutoResumePrompt =>
            Mode == WorkspaceMode.Auto &&
            !_autoCompleted &&
            _autoResumeIndex > 0 &&
            !RequiresCompleteAutoTimeline(_autoOptions, _detectorFactoryOptions) &&
            IsAutoResumeSignatureCurrent(BuildAutoRunIntentSignature(
                _autoOptions,
                _detectorOptions,
                _detectorFactoryOptions));

        public int AutoLastProcessedFrame => _autoLastProcessedFrame;
        public DateTime AutoLastProcessedAtUtc => _autoLastProcessedAtUtc;

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
            FaceDetectorFactoryOptions? detectorFactoryOptions = null)
        {
            Mode = mode;
            _onBack = onBack;
            _autoOptions = autoOptions ?? new AutoMaskOptions();
            _detectorOptions = detectorOptions ?? new FaceOnnxDetectorOptions();
            _detectorFactoryOptions = detectorFactoryOptions ?? FaceDetectorFactoryOptions.ForOnnx(_detectorOptions);
            _stateStore = stateStore;
            FrameList = new FrameListViewModel(videoPath);
            FramePreview = new FramePreviewViewModel(ToolPanel, _maskProvider);
            if (!deferSessionInit)
                InitializeSession(loadProgress);

            // 🔹 자동/최종 마스크 provider 주입
            FramePreview.SetMaskProvider(_maskProvider);

            FrameList.SelectedFrameIndexChanged += index =>
            {
                if (!FrameList.IsPlaying)
                {
                    if (_isAutoRunning && Mode == WorkspaceMode.Auto)
                    {
                        // 분석 중에는 타임라인 번호만 갱신합니다. 정확 프레임 seek는
                        // 종료 시 한 번 수행하고, FramePreview의 편집 대상 인덱스는
                        // 현재 표시 중인 exact 프레임에 그대로 유지합니다.
                        _autoPreviewNeedsExactRefresh = true;
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
                    FramePreview.StartPlayback(
                        FrameList.VideoPath,
                        FrameList.SelectedFrameIndex,
                        FrameList.Fps,
                        FrameList.TotalFrames,
                        FrameList.SetPlaybackFrameIndex,
                        FrameList.NotifyPlaybackStopped);
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

        public async Task EnsureSessionInitializedAsync(IProgress<int>? loadProgress)
        {
            if (_sessionInitialized)
                return;

            var session = await Task.Run(() => new VideoSession(FrameList.VideoPath, progress: loadProgress));
            FramePreview.InitializeSession(session);
            _sessionInitialized = true;

            if (FrameList.SelectedFrameIndex >= 0)
                FramePreview.OnFrameIndexChanged(FrameList.SelectedFrameIndex);
        }

        private void InitializeSession(IProgress<int>? loadProgress)
        {
            var session = new VideoSession(FrameList.VideoPath, progress: loadProgress);
            FramePreview.InitializeSession(session);
            _sessionInitialized = true;
        }


        private async Task<bool> SaveVideoAsync(
            IProgress<ExportProgress>? exportProgress = null,
            CancellationToken cancellationToken = default,
            bool updateToolPanel = true,
            string? runId = null,
            AutoMaskRunSummary? autoRunSummary = null,
            AutoMaskOptions? autoRunOptions = null)
        {
            string input = FrameList.VideoPath;
            string exportRunId = string.IsNullOrWhiteSpace(runId)
                ? $"export-{Guid.NewGuid():N}"
                : runId;
            if (_isAutoRunning && autoRunOptions == null)
            {
                const string reason = "auto-analysis-in-progress";
                string line = $"[ExportBlocked] runId={exportRunId}, reason={reason}";
                System.Diagnostics.Debug.WriteLine(line);
                RunMetricsLog.AppendRunLines(exportRunId, line);
                throw new InvalidOperationException(
                    "자동 분석이 진행 중이므로 내보내기를 중단했습니다. 분석 완료 후 다시 시도해 주세요.");
            }

            AutoMaskRunSummary? effectiveAutoRunSummary =
                autoRunSummary ?? _lastCompletedAutoRunSummary;
            string? cascadeFailure = null;
            string cascadeError = "n/a";
            if (autoRunOptions != null)
            {
                cascadeFailure = GetRequiredYoloCascadeFailure(autoRunOptions, autoRunSummary);
                cascadeError = autoRunSummary?.YoloCascadeError ?? "summary-missing";
            }
            if (cascadeFailure == null && _autoExportGateRequired && !_autoExportGatePassed)
            {
                cascadeFailure = string.IsNullOrWhiteSpace(_autoExportGateFailure)
                    ? "persisted-auto-export-gate-failed"
                    : _autoExportGateFailure;
                cascadeError = "persisted-gate";
            }
            if (cascadeFailure != null)
            {
                string line =
                    $"[ExportBlocked] runId={exportRunId}, reason={cascadeFailure}, cascadeError={cascadeError}";
                System.Diagnostics.Debug.WriteLine(line);
                RunMetricsLog.AppendRunLines(exportRunId, line);
                throw new InvalidOperationException(
                    $"자동 분석 품질 검증이 완료되지 않아 내보내기를 중단했습니다. " +
                    $"reason={cascadeFailure}");
            }

            string output = BuildDefaultExportPath(input);

            string? resolvedOutput = await ResolveExportOutputPathAsync(output);
            if (string.IsNullOrWhiteSpace(resolvedOutput))
                return false;
            output = resolvedOutput;

            var exporter = new VideoExportService(_maskProvider);

            if (updateToolPanel)
            {
                ToolPanel.IsExportRunning = true;
                ToolPanel.ExportProgress = 0;
                ToolPanel.ExportEtaText = "예상 남은 시간 계산 중...";
                ToolPanel.ExportStatusText = null;
            }
            _exportEtaSamples.Clear();

            var progress = new Progress<ExportProgress>(p =>
            {
                exportProgress?.Report(p);
                if (updateToolPanel)
                {
                    int percent = Math.Clamp(p.Percent, 0, 100);
                    ToolPanel.ExportProgress = percent;
                    UpdateExportEta(DateTime.UtcNow, p.FrameIndex, p.TotalFrames);
                    if (!string.IsNullOrWhiteSpace(p.StatusMessage))
                        ToolPanel.ExportStatusText = p.StatusMessage;
                }
            });

            try
            {
                _exportCts?.Dispose();
                _exportCts = cancellationToken.CanBeCanceled
                    ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                    : new CancellationTokenSource();
                (bool allowHybridCopy, IReadOnlyList<string> disableReasons) hybridPolicy = (
                    false,
                    new[] { HybridCopyDisabledReason });
                System.Diagnostics.Debug.WriteLine(
                    $"[WorkspaceExportPolicy] runId={exportRunId}, autoRunSummary={(effectiveAutoRunSummary?.RunId ?? "n/a")}, persistedPolicy={(_autoExportHybridPolicyAvailable && effectiveAutoRunSummary == null).ToString().ToLowerInvariant()}, allowHybridCopy={hybridPolicy.allowHybridCopy.ToString().ToLowerInvariant()}, disableReasons={FormatTextListForLog(hybridPolicy.disableReasons)}");
                RunMetricsLog.AppendRunLines(
                    exportRunId,
                    $"[ExportRunConfig] runId={exportRunId}, blurRadius={ToolPanel.BlurRadius}, allowHybridCopy={hybridPolicy.allowHybridCopy.ToString().ToLowerInvariant()}, disableReasons={FormatTextListForLog(hybridPolicy.disableReasons)}");

                await Task.Run(() =>
                {
                    exporter.Export(
                        input,
                        output,
                        blurRadius: ToolPanel.BlurRadius,
                        progress,
                        _exportCts.Token,
                        exportRunId,
                        allowHybridCopy: hybridPolicy.allowHybridCopy);
                }, _exportCts.Token);
                if (exporter.LastExportSummary != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[WorkspaceExport] {exporter.LastExportSummary.ToLogLine()}");
                    LogExportQualityGate(
                        effectiveAutoRunSummary,
                        exporter.LastExportSummary,
                        allowHybridCopy: hybridPolicy.allowHybridCopy,
                        autoHybridDisableReasons: hybridPolicy.disableReasons);
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                // 사용자 취소는 정상 흐름
                return false;
            }
            finally
            {
                if (updateToolPanel)
                {
                    ToolPanel.IsExportRunning = false;
                    ToolPanel.ExportProgress = 0;
                    ToolPanel.ExportEtaText = null;
                    ToolPanel.ExportStatusText = null;
                }
                _exportCts?.Dispose();
                _exportCts = null;
            }
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

        private async Task<string?> ResolveExportOutputPathAsync(string outputPath)
        {
            if (!File.Exists(outputPath))
                return outputPath;

            var result = await ShowExportConflictDialogAsync(outputPath);
            if (result == ExportConflictResult.Overwrite)
                return outputPath;

            if (result == ExportConflictResult.SaveAs)
                return GetUniqueExportPath(outputPath);

            return null;
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

            return outputPath;
        }

        private static string BuildDefaultExportPath(string inputPath)
        {
            string extension = Path.GetExtension(inputPath);
            string normalizedExtension = extension.ToLowerInvariant();
            if (normalizedExtension is not (
                ".mp4" or ".mov" or ".mkv" or ".avi" or ".wmv" or ".webm"))
            {
                extension = ".mp4";
            }

            string directory = Path.GetDirectoryName(inputPath) ?? string.Empty;
            string baseName = Path.GetFileNameWithoutExtension(inputPath);
            return Path.Combine(directory, $"{baseName}_blur{extension}");
        }

        private IFaceDetectorFactory CreateFaceDetectorFactory()
        {
            return new FaceDetectorFactory(_detectorFactoryOptions);
        }

        private AutoMaskGenerator CreateAutoMaskGenerator(
            IFaceDetector detector,
            IFaceDetectorFactory detectorFactory)
        {
            return new AutoMaskGenerator(
                detector,
                _maskProvider,
                _autoOptions,
                detectorFactory);
        }

        private AutoMaskGenerator CreateAutoMaskGenerator(
            IFaceDetector detector,
            IFaceDetectorFactory detectorFactory,
            AutoMaskOptions options)
        {
            return new AutoMaskGenerator(
                detector,
                _maskProvider,
                options,
                detectorFactory);
        }

        public Task<bool> RunAutoAsync(
            bool exportAfter,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default,
            IProgress<ExportProgress>? exportProgress = null)
        {
            if (_isAutoRunning)
                return Task.FromResult(false);

            _isAutoRunning = true;
            _autoCts = cancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : new CancellationTokenSource();

            if (!exportAfter)
            {
                ToolPanel.IsAutoRunning = true;
                ToolPanel.AutoProgress = 0;
            }

            return RunAutoCoreAsync(exportAfter, progress, exportProgress);
        }

        private async Task<bool> RunAutoCoreAsync(
            bool exportAfter,
            IProgress<int>? progress,
            IProgress<ExportProgress>? exportProgress)
        {
            bool persisted = false;
            bool detectionCompleted = false;
            string runId = $"auto-{Guid.NewGuid():N}";
            try
            {
                // Progress updates no longer invoke the exact-frame path on every
                // selection change, so preserve any pending manual edit once up front.
                FramePreview.PersistCurrentMask();
                _autoPreviewNeedsExactRefresh = false;

                var detectorOptions = _detectorOptions;
                var effectiveAutoOptions = _autoOptions.ResolveProcessingMode();
                var detectorFactoryOptions = ResolveAutoRunDetectorFactoryOptions(
                    effectiveAutoOptions,
                    detectorOptions,
                    _detectorFactoryOptions,
                    out FaceOnnxDetectorOptions? yoloSecondaryOptions);

                string runSignature = BuildAutoRunSignature(effectiveAutoOptions, detectorFactoryOptions);

                int tunedSessions = Math.Max(1, effectiveAutoOptions.ParallelDetectorCount);
                if (_detectorFactoryOptions.Backend == FaceDetectorBackend.FaceOnnx &&
                    _detectorOptions.AllowAutoTune != false)
                {
                    var tuneToken = _autoCts?.Token ?? CancellationToken.None;
                    var tuneResult = await Task.Run(() =>
                    {
                        bool tuned = DetectorAutoTuner.TryTune(
                            FrameList.VideoPath,
                            effectiveAutoOptions.DownscaleRatio,
                            effectiveAutoOptions.DownscaleQuality,
                            _detectorOptions,
                            tunedSessions,
                            _detectorOptions.AllowAutoGpu == true,
                            tuneToken,
                            out var tunedOptions,
                            out var tunedCount,
                            out var tuneLabel);

                        return (tuned, tunedOptions, tunedCount, tuneLabel);
                    }, tuneToken);

                    if (tuneToken.IsCancellationRequested)
                        return false;

                    if (tuneResult.tuned)
                    {
                        detectorOptions = tuneResult.tunedOptions;
                        tunedSessions = Math.Max(1, tuneResult.tunedCount);
                        System.Diagnostics.Debug.WriteLine($"[AutoTune] applied {tuneResult.tuneLabel}");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("[AutoTune] skipped; using configured detector options.");
                    }

                    detectorFactoryOptions = detectorFactoryOptions.WithFaceOnnxOptions(detectorOptions);
                }

                bool useAllPostProcessModules = effectiveAutoOptions.ProcessingMode == AutoMaskProcessingMode.Full;
                var runOptions = new AutoMaskOptions
                {
                    ProcessingMode = effectiveAutoOptions.ProcessingMode,
                    DownscaleRatio = effectiveAutoOptions.DownscaleRatio,
                    DownscaleQuality = effectiveAutoOptions.DownscaleQuality,
                    EnablePostProcessing = effectiveAutoOptions.EnablePostProcessing,
                    EnableRoiPostProcess = useAllPostProcessModules || effectiveAutoOptions.EnableRoiPostProcess,
                    EnableYoloWeakIsolatedCleanup = useAllPostProcessModules || effectiveAutoOptions.EnableYoloWeakIsolatedCleanup,
                    EnableYoloGapFill = useAllPostProcessModules || effectiveAutoOptions.EnableYoloGapFill,
                    EnableYoloSceneCutCarryCleanup = useAllPostProcessModules || effectiveAutoOptions.EnableYoloSceneCutCarryCleanup,
                    EnableYoloTemporalSmoothing = useAllPostProcessModules || effectiveAutoOptions.EnableYoloTemporalSmoothing,
                    EnableYoloRiskCascade = effectiveAutoOptions.EnableYoloRiskCascade,
                    YoloStrongFullScanIntervalSeconds = effectiveAutoOptions.YoloStrongFullScanIntervalSeconds,
                    YoloRiskLowConfidenceThreshold = effectiveAutoOptions.YoloRiskLowConfidenceThreshold,
                    YoloRiskSmallFaceAreaRatio = effectiveAutoOptions.YoloRiskSmallFaceAreaRatio,
                    YoloRiskEdgeMarginRatio = effectiveAutoOptions.YoloRiskEdgeMarginRatio,
                    YoloRiskMaxTrackGapFrames = effectiveAutoOptions.YoloRiskMaxTrackGapFrames,
                    YoloStrongConfirmationFrames = effectiveAutoOptions.YoloStrongConfirmationFrames,
                    EnableYoloPrimaryRoiShortcut = effectiveAutoOptions.EnableYoloPrimaryRoiShortcut,
                    YoloSecondaryDetectorOptions = yoloSecondaryOptions,
                    RoiRefinerDetectorOptions = useAllPostProcessModules || effectiveAutoOptions.EnableRoiPostProcess
                        ? detectorOptions
                        : null,
                    UseFaceOnnxRoiRefiner = detectorFactoryOptions.Backend == FaceDetectorBackend.FaceOnnx,
                    UseTracking = effectiveAutoOptions.UseTracking,
                    DetectEveryNFrames = effectiveAutoOptions.DetectEveryNFrames,
                    ParallelDetectorCount = tunedSessions,
                    RunId = runId,
                    FilterProfile = effectiveAutoOptions.FilterProfile,
                    DumpDetectionDiagnostics = effectiveAutoOptions.DumpDetectionDiagnostics
                }.ResolveProcessingMode();
                var detectorFactory = new FaceDetectorFactory(detectorFactoryOptions);
                using IFaceDetector detector = detectorFactory.CreateDetector();
                string sourceEvidenceId = BuildSourceEvidenceId(FrameList.VideoPath);
                string executionSignature = BuildAutoExecutionSignature(
                    runOptions,
                    detectorFactoryOptions,
                    GetDetectorExecutionProviderLabel(detector),
                    sourceEvidenceId);
                RunMetricsLog.AppendRunLines(
                    runId,
                    $"[AutoRunConfig] runId={runId}, sourceId={sourceEvidenceId}, totalFrames={FrameList.TotalFrames}, signature={BuildAutoRunEvidenceSignature(runOptions, detectorFactoryOptions)}, executionSignature={executionSignature}");
                var generator = CreateAutoMaskGenerator(detector, detectorFactory, runOptions);
                ResetStaleAutoResumeIfRunChanged(runSignature, executionSignature);
                _autoRunSignature = runSignature;
                _autoExecutionSignature = executionSignature;
                _autoCompleted = false;
                int lastProcessed = Math.Max(0, _autoResumeIndex);
                if (RequiresCompleteAutoTimeline(runOptions, detectorFactoryOptions) &&
                    lastProcessed > 0)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[AutoMaskResumeReset] reason=yolo-risk-cascade-requires-complete-timeline resumeIndex={lastProcessed}");
                    lastProcessed = 0;
                    _autoResumeIndex = 0;
                }
                BeginAutoExportGate();
                ResetAutoFaceMasksForRun(lastProcessed);

                // TODO: 필요하면 IProgress<int>를 WorkspaceViewModel 프로퍼티로 노출해서
                //       진행률 UI를 그릴 수 있습니다.
                var effectiveProgress = new Progress<int>(p =>
                {
                    progress?.Report(p);
                    if (!exportAfter)
                        ToolPanel.AutoProgress = p;
                });
                var token = _autoCts?.Token ?? CancellationToken.None;
                await generator.GenerateAsync(
                    FrameList.VideoPath,
                    effectiveProgress,
                    token,
                    startFrameIndex: lastProcessed,
                    onFrameProcessed: idx =>
                    {
                        lastProcessed = idx;
                        _autoResumeIndex = idx;
                        _autoLastProcessedFrame = idx;
                        _autoLastProcessedAtUtc = DateTime.UtcNow;
                        if (!exportAfter)
                            TryUpdateAutoPreview(idx);
                    });
                if (generator.LastRunSummary != null)
                    System.Diagnostics.Debug.WriteLine($"[WorkspaceAuto] {generator.LastRunSummary.ToLogLine()}");

                if (token.IsCancellationRequested)
                {
                    _autoCompleted = false;
                    PersistWorkspaceState(includePreviewMask: !exportAfter);
                    persisted = true;
                    return false;
                }

                if (!IsAutoDetectionRunComplete(generator.LastRunSummary, lastProcessed, FrameList.TotalFrames))
                {
                    _autoCompleted = false;
                    _autoResumeIndex = Math.Clamp(lastProcessed, 0, Math.Max(0, FrameList.TotalFrames - 1));
                    System.Diagnostics.Debug.WriteLine(
                        $"[AutoMaskPostProcessSkipped] reason=incomplete totalFrames={FrameList.TotalFrames} startFrame={generator.LastRunSummary?.StartFrameIndex ?? lastProcessed} processed={generator.LastRunSummary?.ProcessedFrames ?? 0} decoded={generator.LastRunSummary?.DecodedFrames ?? 0} decodeEof={generator.LastRunSummary?.ReachedDecoderEof.ToString().ToLowerInvariant() ?? "false"} decodeCancelled={generator.LastRunSummary?.DecodeCancelled.ToString().ToLowerInvariant() ?? "false"} decodeError={generator.LastRunSummary?.DecodeError ?? "summary-missing"} lastProcessed={lastProcessed} resumeIndex={_autoResumeIndex}");
                    PersistWorkspaceState(includePreviewMask: !exportAfter);
                    persisted = true;
                    return false;
                }

                CompleteAutoExportGate(runOptions, generator.LastRunSummary);
                RefreshAutoPreviewAfterPostProcess(exportAfter);

                if (!exportAfter)
                    await BuildAutoAnomaliesAsync();

                // Detection and post-processing are complete at this point. If export is
                // canceled afterwards, do not reopen as a partial detection resume.
                _autoCompleted = true;
                _autoResumeIndex = 0;
                detectionCompleted = true;

                if (exportAfter)
                {
                    string? cascadeFailure = GetRequiredYoloCascadeFailure(
                        runOptions,
                        generator.LastRunSummary);
                    if (cascadeFailure != null)
                    {
                        string cascadeError = generator.LastRunSummary?.YoloCascadeError ?? "summary-missing";
                        string line =
                            $"[AutoExportBlocked] runId={runId}, reason={cascadeFailure}, cascadeError={cascadeError}";
                        System.Diagnostics.Debug.WriteLine(line);
                        RunMetricsLog.AppendRunLines(runId, line);
                        PersistWorkspaceState(includePreviewMask: false);
                        persisted = true;
                        return false;
                    }

                    bool exported = await SaveVideoAsync(
                        exportProgress,
                        _autoCts?.Token ?? CancellationToken.None,
                        updateToolPanel: false,
                        runId: runId,
                        autoRunSummary: generator.LastRunSummary,
                        autoRunOptions: runOptions);
                    if (!exported)
                    {
                        PersistWorkspaceState(includePreviewMask: false);
                        persisted = true;
                        return false;
                    }
                }

                PersistWorkspaceState(includePreviewMask: !exportAfter);
                persisted = true;

                return true;
            }
            catch (OperationCanceledException)
            {
                _autoCompleted = detectionCompleted;
                if (detectionCompleted)
                    _autoResumeIndex = 0;
                PersistWorkspaceState(includePreviewMask: !exportAfter);
                persisted = true;
                return false;
            }
            finally
            {
                _autoCts?.Dispose();
                _autoCts = null;
                _isAutoRunning = false;
                ToolPanel.IsAutoRunning = false;
                if (!exportAfter &&
                    _autoPreviewNeedsExactRefresh &&
                    FrameList.SelectedFrameIndex >= 0)
                {
                    _autoPreviewNeedsExactRefresh = false;
                    FramePreview.OnFrameIndexChanged(FrameList.SelectedFrameIndex);
                }
                if (!persisted)
                    PersistWorkspaceState(includePreviewMask: !exportAfter);
            }
        }

        private static bool IsAutoDetectionRunComplete(
            AutoMaskRunSummary? summary,
            int lastProcessedFrame,
            int workspaceTotalFrames)
        {
            if (summary == null ||
                !summary.ReachedDecoderEof ||
                summary.DecodeCancelled ||
                !string.Equals(summary.DecodeError, "none", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            AutoMaskRunSummary runSummary = summary;
            int totalFrames = runSummary.TotalFrames > 0
                ? runSummary.TotalFrames
                : workspaceTotalFrames;
            if (totalFrames <= 0)
                return false;

            int completionFrame = Math.Max(
                0,
                totalFrames - 1 - AutoDetectionCompletionTailToleranceFrames);
            if (lastProcessedFrame >= completionFrame)
                return true;

            if (runSummary.ProcessedFrames <= 0)
                return false;

            int summaryLastFrame = runSummary.StartFrameIndex + runSummary.ProcessedFrames - 1;
            return summaryLastFrame >= completionFrame;
        }

        private static string? GetRequiredYoloCascadeFailure(
            AutoMaskOptions options,
            AutoMaskRunSummary? summary)
        {
            if (!options.EnableYoloRiskCascade || options.FilterProfile != FaceFilterProfile.Yolo)
                return null;
            if (summary == null)
                return "yolo-risk-cascade-summary-missing";
            if (!summary.YoloRiskCascadeEnabled)
                return "yolo-risk-cascade-not-executed";
            if (summary.YoloTimelineFrameCount <= 0 ||
                summary.YoloPtsTimingFrameCount != summary.YoloTimelineFrameCount ||
                summary.YoloUnalignedTimelineFrameCount != 0)
            {
                return "yolo-risk-cascade-incomplete-pts-coverage";
            }
            if (summary.YoloUnalignedRiskFrameCount != 0)
                return "yolo-risk-cascade-unaligned-risk-frames";
            if (!string.Equals(summary.YoloCascadeError, "none", StringComparison.OrdinalIgnoreCase))
                return "yolo-risk-cascade-error";
            if (summary.YoloSecondaryAttemptCount + summary.YoloProtectedStoredMaskFrameCount !=
                summary.YoloRiskFrameCount)
            {
                return "yolo-risk-cascade-incomplete-coverage";
            }

            return null;
        }

        private void BeginAutoExportGate()
        {
            _autoExportGateRequired = true;
            _autoExportGatePassed = false;
            _autoExportGateFailure = "auto-run-incomplete";
            _lastCompletedAutoRunSummary = null;
            _autoExportHybridPolicyAvailable = false;
            _autoExportAllowHybridCopy = false;
            _autoExportHybridDisableReasons = HybridCopyDisabledReason;
        }

        private void CompleteAutoExportGate(
            AutoMaskOptions options,
            AutoMaskRunSummary? summary)
        {
            _lastCompletedAutoRunSummary = summary;
            _autoExportHybridPolicyAvailable = true;
            _autoExportAllowHybridCopy = false;
            _autoExportHybridDisableReasons = HybridCopyDisabledReason;

            string? failure = summary == null
                ? "auto-run-summary-missing"
                : GetRequiredYoloCascadeFailure(options, summary);
            _autoExportGatePassed = failure == null;
            _autoExportGateFailure = failure;
        }

        private void ResetAutoFaceMasksForRun(int startFrameIndex)
        {
            if (startFrameIndex <= 0)
            {
                _maskProvider.ClearFaceMasks();
                return;
            }

            int removed = _maskProvider.RemoveFaceMasksFrom(startFrameIndex);
            if (removed > 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[AutoMaskResumeReset] start={startFrameIndex} removedStaleFaceMasks={removed}");
            }
        }

        partial void OnAutoAnomalyCountChanged(int value)
        {
            OnPropertyChanged(nameof(AutoSummaryVisible));
            OnPropertyChanged(nameof(AutoSummaryText));
        }

        partial void OnHideResolvedIssuesChanged(bool value)
        {
            UpdateIssueVisibility(value);
        }

        private void TryUpdateAutoPreview(int frameIndex)
        {
            if (Mode != WorkspaceMode.Auto)
                return;

            long now = Environment.TickCount64;
            if (now - _autoLastPreviewTick < 200)
                return;
            _autoLastPreviewTick = now;

            Dispatcher.UIThread.Post(() =>
            {
                if (!_isAutoRunning)
                    return;
                if (FrameList.SelectedFrameIndex == frameIndex)
                    return;

                FrameList.SelectedFrameIndex = frameIndex;
            });
        }

        private void RefreshAutoPreviewAfterPostProcess(bool exportAfter)
        {
            if (exportAfter || FrameList.SelectedFrameIndex < 0)
                return;

            _autoPreviewNeedsExactRefresh = false;
            FramePreview.OnFrameIndexChanged(FrameList.SelectedFrameIndex);
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
            _autoCts?.Cancel();
        }

        private void OnExportCancelRequested()
        {
            _exportCts?.Cancel();
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

        private void UpdateExportEta(DateTime timestamp, int frameIndex, int totalFrames)
        {
            if (totalFrames <= 0 || frameIndex <= 0)
            {
                if (string.IsNullOrWhiteSpace(ToolPanel.ExportEtaText))
                    ToolPanel.ExportEtaText = "예상 남은 시간 계산 중...";
                return;
            }
            if (frameIndex >= totalFrames)
            {
                ToolPanel.ExportEtaText = null;
                return;
            }

            if (_exportEtaSamples.Count > 0 && frameIndex <= _exportLastSample.FrameIndex)
                return;

            _exportEtaSamples.Enqueue((timestamp, frameIndex));
            _exportLastSample = (timestamp, frameIndex);

            while (_exportEtaSamples.Count > 0 &&
                   (timestamp - _exportEtaSamples.Peek().Timestamp).TotalSeconds > 10)
                _exportEtaSamples.Dequeue();

            if (_exportEtaSamples.Count < 2)
            {
                ToolPanel.ExportEtaText = "예상 남은 시간 계산 중...";
                return;
            }

            var first = _exportEtaSamples.Peek();
            var last = _exportLastSample;
            var elapsedSeconds = (last.Timestamp - first.Timestamp).TotalSeconds;
            var progressed = last.FrameIndex - first.FrameIndex;

            if (elapsedSeconds <= 0 || progressed <= 0)
                return;

            double ratePerSecond = progressed / elapsedSeconds;
            double remainingFrames = (totalFrames - frameIndex);
            double remaining = remainingFrames / ratePerSecond;
            if (remaining < 0)
                return;

            ToolPanel.ExportEtaText = $"예상 남은 시간: {FormatEta(TimeSpan.FromSeconds(remaining))}";
        }

        private static string FormatEta(TimeSpan remaining)
        {
            if (remaining.TotalHours >= 1)
                return $"{(int)remaining.TotalHours}시간 {Math.Max(0, remaining.Minutes)}분 {Math.Max(0, remaining.Seconds)}초";
            if (remaining.TotalMinutes >= 1)
                return $"{(int)remaining.TotalMinutes}분 {Math.Max(0, remaining.Seconds)}초";
            return $"{Math.Max(0, (int)remaining.TotalSeconds)}초";
        }

        [RelayCommand]
        private void GoBack()
        {
            FramePreview.PersistCurrentMask();
            PersistWorkspaceState();
            _onBack?.Invoke();
        }

        private Task<bool> RunAutoSingleFrameAsync()
        {
            int frameIndex = FrameList.SelectedFrameIndex;
            if (frameIndex < 0)
                return Task.FromResult(false);

            return RunAutoSingleFrameCoreAsync(frameIndex);
        }

        private async Task<bool> RunAutoSingleFrameCoreAsync(int frameIndex)
        {
            if (_isAutoRunning)
                return false;

            _isAutoRunning = true;
            _autoCts = new CancellationTokenSource();

            ToolPanel.IsAutoRunning = true;
            ToolPanel.AutoProgress = 0;

            try
            {
                var detectorFactory = CreateFaceDetectorFactory();
                using IFaceDetector detector = detectorFactory.CreateDetector();
                var generator = CreateAutoMaskGenerator(detector, detectorFactory);

                var effectiveProgress = new Progress<int>(p =>
                {
                    ToolPanel.AutoProgress = p;
                });

                var token = _autoCts?.Token ?? CancellationToken.None;
                bool generated = await generator.GenerateFrameAsync(FrameList.VideoPath, frameIndex, effectiveProgress, token);
                if (!generated || token.IsCancellationRequested)
                {
                    PersistWorkspaceState();
                    return false;
                }

                FramePreview.OnFrameIndexChanged(frameIndex);
                _autoLastProcessedFrame = frameIndex;
                _autoLastProcessedAtUtc = DateTime.UtcNow;
                PersistWorkspaceState();
                return true;
            }
            catch (OperationCanceledException)
            {
                PersistWorkspaceState();
                return false;
            }
            finally
            {
                _autoCts?.Dispose();
                _autoCts = null;
                _isAutoRunning = false;
                ToolPanel.IsAutoRunning = false;
                PersistWorkspaceState();
            }
        }

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
            if (_autoAnomalies.Length == 0)
                return;

            int current = FrameList.SelectedFrameIndex;
            int idx = Array.BinarySearch(_autoAnomalies, current);

            if (forward)
            {
                idx = idx >= 0 ? idx + 1 : ~idx;
                if (idx >= _autoAnomalies.Length) idx = 0;
            }
            else
            {
                idx = idx >= 0 ? idx - 1 : ~idx - 1;
                if (idx < 0) idx = _autoAnomalies.Length - 1;
            }

            FrameList.SelectedFrameIndex = _autoAnomalies[idx];
        }

        [RelayCommand]
        private void JumpToIssue(int frameIndex)
        {
            FrameList.SelectedFrameIndex = Math.Clamp(frameIndex, 0, FrameList.TotalFrames - 1);
        }

        [RelayCommand]
        private void ReviewAutoAnomalies()
        {
            if (_autoAnomalies.Length == 0)
                return;
            FrameList.SelectedFrameIndex = _autoAnomalies[0];
        }

        private async Task BuildAutoAnomaliesAsync()
        {
            int total = FrameList.TotalFrames;
            if (total <= 0)
            {
                _autoAnomalies = Array.Empty<int>();
                AutoAnomalyCount = 0;
                HasAutoAnomalies = false;
                FrameList.NoFaceIssueFrames = Array.Empty<int>();
                FrameList.LowConfidenceIssueFrames = Array.Empty<int>();
                FrameList.FlickerIssueFrames = Array.Empty<int>();
                ResetIssueList(_noFaceIssueEntries, Array.Empty<int>(), "얼굴 없음");
                ResetIssueList(_lowConfidenceIssueEntries, Array.Empty<int>(), "신뢰도 낮음");
                ResetIssueList(_flickerIssueEntries, Array.Empty<int>(), "연속 끊김");
                return;
            }

            float lowConfidenceCutoff = GetLowConfidenceCutoff();

            var (noFaceFrames, lowConfidenceFrames, flickerFrames) = await Task.Run(() =>
            {
                var noFace = new System.Collections.Generic.List<int>();
                var lowConfidence = new System.Collections.Generic.List<int>();
                var flicker = new System.Collections.Generic.List<int>();
                var hasFace = new bool[total];

                foreach (var entry in _maskProvider.GetFaceMaskEntries())
                {
                    int i = entry.Key;
                    if (i < 0 || i >= total)
                        continue;

                    var data = entry.Value;
                    if (data.Faces.Count > 0)
                    {
                        hasFace[i] = true;
                        if (data.MinConfidence.HasValue &&
                            data.MinConfidence.Value < lowConfidenceCutoff)
                        {
                            lowConfidence.Add(i);
                        }
                    }
                }

                int faceFrameCount = hasFace.Count(static x => x);
                if (_autoOptions.FilterProfile == FaceFilterProfile.Yolo)
                {
                    if (faceFrameCount == 0)
                        AddNoDetectionReviewFrames(total, noFace);
                    else
                    {
                        AddSuspiciousNoFaceGaps(hasFace, noFace);
                        AddSparseNoFaceReviewFrames(hasFace, faceFrameCount, noFace);
                    }
                }
                else
                {
                    AddSuspiciousNoFaceGaps(hasFace, noFace);
                }

                for (int i = 1; i < total - 1; i++)
                {
                    if (!hasFace[i] && hasFace[i - 1] && hasFace[i + 1])
                        flicker.Add(i);
                }

                return (noFace.ToArray(), lowConfidence.ToArray(), flicker.ToArray());
            });

            FrameList.NoFaceIssueFrames = noFaceFrames;
            FrameList.LowConfidenceIssueFrames = lowConfidenceFrames;
            FrameList.FlickerIssueFrames = flickerFrames;

            var anomalies = MergeSortedFrames(noFaceFrames, lowConfidenceFrames);
            anomalies = MergeSortedFrames(anomalies, flickerFrames);
            _autoAnomalies = anomalies;
            AutoAnomalyCount = anomalies.Length;
            HasAutoAnomalies = anomalies.Length > 0;

            _noFaceIssueSet = new HashSet<int>(noFaceFrames);
            _lowConfidenceIssueSet = new HashSet<int>(lowConfidenceFrames);
            _flickerIssueSet = new HashSet<int>(flickerFrames);
            ResetIssueList(_noFaceIssueEntries, noFaceFrames, "얼굴 없음");
            ResetIssueList(_lowConfidenceIssueEntries, lowConfidenceFrames, "신뢰도 낮음");
            ResetIssueList(_flickerIssueEntries, flickerFrames, "연속 끊김");
        }

        private float GetLowConfidenceCutoff()
        {
            var defaults = FaceOnnxDetector.GetDefaultThresholds();
            float baseThreshold = _detectorFactoryOptions.Backend switch
            {
                FaceDetectorBackend.YoloFaceOnnx when _detectorFactoryOptions.YoloFaceOnnxOptions != null =>
                    _detectorFactoryOptions.YoloFaceOnnxOptions.ConfidenceThreshold,
                FaceDetectorBackend.ScrfdOnnx when _detectorFactoryOptions.ScrfdOnnxOptions != null =>
                    _detectorFactoryOptions.ScrfdOnnxOptions.ConfidenceThreshold,
                FaceDetectorBackend.YuNetOnnx when _detectorFactoryOptions.YuNetOnnxOptions != null =>
                    _detectorFactoryOptions.YuNetOnnxOptions.ConfidenceThreshold,
                FaceDetectorBackend.FaceOnnx when _detectorFactoryOptions.FaceOnnxOptions != null =>
                    _detectorFactoryOptions.FaceOnnxOptions.ConfidenceThreshold ?? defaults.Confidence,
                _ => _detectorOptions.ConfidenceThreshold ?? defaults.Confidence
            };
            return Math.Clamp(baseThreshold + LowConfidenceMargin, 0.0f, 0.99f);
        }

        private static void AddSuspiciousNoFaceGaps(bool[] hasFace, List<int> noFace)
        {
            int total = hasFace.Length;
            int i = 0;
            while (i < total)
            {
                if (hasFace[i])
                {
                    i++;
                    continue;
                }

                int start = i;
                while (i < total && !hasFace[i])
                    i++;

                int endExclusive = i;
                int length = endExclusive - start;
                if (length > SuspiciousNoFaceMaxGap)
                    continue;

                bool hasPreviousFace = start > 0 && hasFace[start - 1];
                bool hasNextFace = endExclusive < total && hasFace[endExclusive];
                if (!hasPreviousFace || !hasNextFace)
                    continue;

                for (int frame = start; frame < endExclusive; frame++)
                    noFace.Add(frame);
            }
        }

        private static void AddNoDetectionReviewFrames(int totalFrames, List<int> noFace)
        {
            if (totalFrames <= 0)
                return;

            int sampleCount = Math.Min(NoDetectionReviewSampleCount, totalFrames);
            if (sampleCount <= 0)
                return;

            var frames = new SortedSet<int>();
            if (sampleCount == 1)
            {
                frames.Add(0);
            }
            else
            {
                for (int i = 0; i < sampleCount; i++)
                {
                    int frame = (int)Math.Round(i * (totalFrames - 1) / (double)(sampleCount - 1));
                    frames.Add(Math.Clamp(frame, 0, totalFrames - 1));
                }
            }

            noFace.AddRange(frames);
            System.Diagnostics.Debug.WriteLine(
                $"[AutoMaskNoDetectionReview] frames={string.Join(",", frames)} totalFrames={totalFrames}");
        }

        private static void AddSparseNoFaceReviewFrames(bool[] hasFace, int faceFrameCount, List<int> noFace)
        {
            int totalFrames = hasFace.Length;
            if (totalFrames <= 0 || faceFrameCount <= 0)
                return;

            double coverage = faceFrameCount / (double)totalFrames;
            if (coverage > SparseNoFaceReviewMaxCoverageRatio)
                return;

            int firstFace = Array.FindIndex(hasFace, static x => x);
            int lastFace = Array.FindLastIndex(hasFace, static x => x);
            if (firstFace < 0 || lastFace <= firstFace)
                return;

            var candidateFrames = new List<int>();
            int i = 0;
            while (i < totalFrames)
            {
                if (hasFace[i])
                {
                    i++;
                    continue;
                }

                int start = i;
                while (i < totalFrames && !hasFace[i])
                    i++;

                int endExclusive = i;
                int length = endExclusive - start;
                if (length <= SuspiciousNoFaceMaxGap)
                    continue;

                bool hasPreviousFace = start > firstFace;
                bool hasNextFace = endExclusive <= lastFace;
                if (!hasPreviousFace || !hasNextFace)
                    continue;

                candidateFrames.Add(start);
                if (length > 2)
                    candidateFrames.Add(start + length / 2);
                candidateFrames.Add(endExclusive - 1);
            }

            if (candidateFrames.Count == 0)
                return;

            var frames = new SortedSet<int>(noFace);
            int sampleCount = Math.Min(SparseNoFaceReviewSampleCount, candidateFrames.Count);
            if (sampleCount == 1)
            {
                frames.Add(candidateFrames[0]);
            }
            else
            {
                for (int sample = 0; sample < sampleCount; sample++)
                {
                    int index = (int)Math.Round(sample * (candidateFrames.Count - 1) / (double)(sampleCount - 1));
                    frames.Add(Math.Clamp(candidateFrames[index], 0, totalFrames - 1));
                }
            }

            noFace.Clear();
            noFace.AddRange(frames);
            System.Diagnostics.Debug.WriteLine(
                $"[AutoMaskSparseNoFaceReview] faceFrames={faceFrameCount} totalFrames={totalFrames} coverage={coverage:0.000} frames={string.Join(",", frames)}");
        }

        private static int[] MergeSortedFrames(IReadOnlyList<int> first, IReadOnlyList<int> second)
        {
            if (first.Count == 0)
                return CopyFrames(second);
            if (second.Count == 0)
                return CopyFrames(first);

            var merged = new int[first.Count + second.Count];
            int i = 0;
            int j = 0;
            int k = 0;

            while (i < first.Count && j < second.Count)
            {
                int a = first[i];
                int b = second[j];

                if (a == b)
                {
                    merged[k++] = a;
                    i++;
                    j++;
                }
                else if (a < b)
                {
                    merged[k++] = a;
                    i++;
                }
                else
                {
                    merged[k++] = b;
                    j++;
                }
            }

            while (i < first.Count)
                merged[k++] = first[i++];
            while (j < second.Count)
                merged[k++] = second[j++];

            if (k == merged.Length)
                return merged;

            var trimmed = new int[k];
            Array.Copy(merged, trimmed, k);
            return trimmed;
        }

        private static int[] CopyFrames(IReadOnlyList<int> source)
        {
            if (source is int[] arr)
                return (int[])arr.Clone();

            var copy = new int[source.Count];
            for (int i = 0; i < source.Count; i++)
                copy[i] = source[i];
            return copy;
        }

        private void ResetIssueList(
            ObservableCollection<IssueEntryViewModel> target,
            IReadOnlyList<int> frames,
            string label)
        {
            target.Clear();
            for (int i = 0; i < frames.Count; i++)
            {
                var entry = new IssueEntryViewModel(frames[i], label, FormatFrameTime(frames[i], FrameList.Fps))
                {
                    HideResolved = HideResolvedIssues
                };
                entry.Resolved += OnIssueResolved;
                target.Add(entry);
            }
        }

        private void OnIssueResolved(IssueEntryViewModel entry)
        {
            ResolveIssueForFrame(entry.FrameIndex);
        }

        private void OnMaskEdited(int frameIndex)
        {
            ResolveIssueForFrame(frameIndex);
        }

        private void ResolveIssueForFrame(int frameIndex)
        {
            bool changed = false;
            if (_noFaceIssueSet.Remove(frameIndex))
                changed = true;
            if (_lowConfidenceIssueSet.Remove(frameIndex))
                changed = true;
            if (_flickerIssueSet.Remove(frameIndex))
                changed = true;

            if (!changed)
                return;

            RemoveIssueEntry(_noFaceIssueEntries, frameIndex);
            RemoveIssueEntry(_lowConfidenceIssueEntries, frameIndex);
            RemoveIssueEntry(_flickerIssueEntries, frameIndex);

            FrameList.NoFaceIssueFrames = _noFaceIssueSet.OrderBy(x => x).ToArray();
            FrameList.LowConfidenceIssueFrames = _lowConfidenceIssueSet.OrderBy(x => x).ToArray();
            FrameList.FlickerIssueFrames = _flickerIssueSet.OrderBy(x => x).ToArray();

            var anomalies = MergeSortedFrames(FrameList.NoFaceIssueFrames, FrameList.LowConfidenceIssueFrames);
            anomalies = MergeSortedFrames(anomalies, FrameList.FlickerIssueFrames);
            _autoAnomalies = anomalies;
            AutoAnomalyCount = anomalies.Length;
            HasAutoAnomalies = anomalies.Length > 0;
        }

        private static void RemoveIssueEntry(ObservableCollection<IssueEntryViewModel> target, int frameIndex)
        {
            for (int i = target.Count - 1; i >= 0; i--)
            {
                if (target[i].FrameIndex == frameIndex)
                    target.RemoveAt(i);
            }
        }

        private void UpdateIssueVisibility(bool hideResolved)
        {
            SetIssueVisibility(_noFaceIssueEntries, hideResolved);
            SetIssueVisibility(_lowConfidenceIssueEntries, hideResolved);
            SetIssueVisibility(_flickerIssueEntries, hideResolved);
        }

        private static void SetIssueVisibility(
            ObservableCollection<IssueEntryViewModel> entries,
            bool hideResolved)
        {
            for (int i = 0; i < entries.Count; i++)
                entries[i].HideResolved = hideResolved;
        }

        private static string FormatFrameTime(int frameIndex, double fps)
        {
            if (fps <= 0)
                return "00:00.00";

            double framesPerSecond = fps;
            int wholeSeconds = (int)Math.Floor(frameIndex / framesPerSecond);
            int frameRemainder = (int)Math.Round(frameIndex - wholeSeconds * framesPerSecond);
            int fpsInt = (int)Math.Round(framesPerSecond);
            if (fpsInt > 0 && frameRemainder >= fpsInt)
            {
                wholeSeconds += frameRemainder / fpsInt;
                frameRemainder %= fpsInt;
            }

            int minutes = wholeSeconds / 60;
            int seconds = wholeSeconds % 60;
            return $"{minutes:D2}:{seconds:D2}.{frameRemainder:D2}";
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

        private void PersistWorkspaceState(bool includePreviewMask)
        {
            if (_stateStore == null)
                return;

            if (includePreviewMask)
                FramePreview.PersistCurrentMask();
            var snapshot = BuildSnapshot();
            _stateStore.SaveWorkspace(snapshot, _maskProvider);
        }

        private WorkspaceSnapshot BuildSnapshot()
        {
            return new WorkspaceSnapshot(
                FrameList.VideoPath,
                Mode,
                FrameList.SelectedFrameIndex,
                FrameList.ViewStartSeconds,
                FrameList.SecondsPerScreen,
                DateTimeOffset.Now,
                _autoResumeIndex,
                _autoCompleted,
                _autoRunSignature,
                _autoExportGateRequired,
                _autoExportGatePassed,
                _autoExportGateFailure,
                _autoExportHybridPolicyAvailable,
                _autoExportAllowHybridCopy,
                _autoExportHybridDisableReasons,
                _autoExecutionSignature);
        }

        private void ApplySnapshot(WorkspaceSnapshot snapshot)
        {
            if (snapshot == null)
                return;

            _autoResumeIndex = snapshot.AutoResumeIndex;
            _autoCompleted = snapshot.AutoCompleted;
            _autoRunSignature = snapshot.AutoRunSignature;
            _autoExecutionSignature = snapshot.AutoExecutionSignature;
            _autoExportGateRequired = snapshot.AutoExportGateRequired;
            _autoExportGatePassed = snapshot.AutoExportGatePassed;
            _autoExportGateFailure = snapshot.AutoExportGateFailure;
            _lastCompletedAutoRunSummary = null;
            _autoExportHybridPolicyAvailable = snapshot.AutoExportHybridPolicyAvailable;
            _autoExportAllowHybridCopy = false;
            _autoExportHybridDisableReasons = HybridCopyDisabledReason;

            bool legacyIncompleteRun =
                !_autoExportGateRequired &&
                !_autoExportGatePassed &&
                _autoResumeIndex > 0 &&
                !_autoCompleted;
            bool legacyYoloCascadeEvidenceMissing =
                !_autoExportGateRequired &&
                !_autoExportGatePassed &&
                !string.IsNullOrWhiteSpace(_autoRunSignature) &&
                _autoRunSignature.Contains("profile=Yolo", StringComparison.OrdinalIgnoreCase) &&
                _autoRunSignature.Contains("riskCascade=True", StringComparison.OrdinalIgnoreCase);
            if (legacyIncompleteRun || legacyYoloCascadeEvidenceMissing)
            {
                _autoExportGateRequired = true;
                _autoExportGatePassed = false;
                _autoExportGateFailure = legacyIncompleteRun
                    ? "legacy-auto-run-incomplete"
                    : "legacy-cascade-evidence-missing";
            }

            double secondsPerScreen = snapshot.SecondsPerScreen;
            if (secondsPerScreen <= 0)
                secondsPerScreen = FrameList.SecondsPerScreen;
            FrameList.SecondsPerScreen = secondsPerScreen;

            double maxStart = Math.Max(0, FrameList.TotalDurationSeconds - FrameList.SecondsPerScreen);
            FrameList.ViewStartSeconds = Math.Clamp(snapshot.ViewStartSeconds, 0, maxStart);

            int index;
            if (FrameList.TotalFrames <= 0)
                index = -1;
            else
                index = Math.Clamp(snapshot.SelectedFrameIndex, 0, FrameList.TotalFrames - 1);

            FrameList.SelectedFrameIndex = index;
        }

        private bool IsAutoResumeSignatureCurrent(string currentSignature)
            => !string.IsNullOrWhiteSpace(_autoRunSignature) &&
                string.Equals(_autoRunSignature, currentSignature, StringComparison.Ordinal);

        private void ResetStaleAutoResumeIfRunChanged(
            string currentRunSignature,
            string currentExecutionSignature)
        {
            string? reason = GetAutoResumeResetReason(
                _autoResumeIndex,
                _autoRunSignature,
                currentRunSignature,
                _autoExecutionSignature,
                currentExecutionSignature);
            if (reason == null)
                return;

            System.Diagnostics.Debug.WriteLine(
                $"[AutoMaskResumeReset] reason={reason} resumeIndex={_autoResumeIndex}");
            _autoResumeIndex = 0;
            _autoCompleted = false;
        }

        private static string? GetAutoResumeResetReason(
            int resumeIndex,
            string? storedRunSignature,
            string currentRunSignature,
            string? storedExecutionSignature,
            string currentExecutionSignature)
        {
            if (resumeIndex <= 0)
                return null;
            if (string.IsNullOrWhiteSpace(storedRunSignature) ||
                !string.Equals(storedRunSignature, currentRunSignature, StringComparison.Ordinal))
            {
                return "settings-changed";
            }
            if (string.IsNullOrWhiteSpace(storedExecutionSignature) ||
                string.IsNullOrWhiteSpace(currentExecutionSignature))
            {
                return "execution-signature-missing";
            }
            if (!string.Equals(storedExecutionSignature, currentExecutionSignature, StringComparison.Ordinal))
            {
                return "execution-changed";
            }

            return null;
        }

        private static string BuildAutoRunSignature(
            AutoMaskOptions autoOptions,
            FaceDetectorFactoryOptions detectorFactoryOptions)
        {
            autoOptions = (autoOptions ?? new AutoMaskOptions()).ResolveProcessingMode();
            detectorFactoryOptions ??= FaceDetectorFactoryOptions.ForOnnx(new FaceOnnxDetectorOptions());
            bool useLegacyCompatibleSignature =
                autoOptions.ProcessingMode == AutoMaskProcessingMode.Legacy &&
                !autoOptions.EnableYoloRiskCascade &&
                autoOptions.EnableYoloPrimaryRoiShortcut;

            var parts = new List<string>
            {
                useLegacyCompatibleSignature ? "v3" : "v5",
                $"backend={detectorFactoryOptions.Backend}",
                $"profile={autoOptions.FilterProfile}",
                $"downscale={FormatSignatureNumber(autoOptions.DownscaleRatio)}",
                $"quality={autoOptions.DownscaleQuality}",
                $"post={autoOptions.EnablePostProcessing}",
                $"roi={autoOptions.EnableRoiPostProcess}",
                $"iso={autoOptions.EnableYoloWeakIsolatedCleanup}",
                $"gap={autoOptions.EnableYoloGapFill}",
                $"scene={autoOptions.EnableYoloSceneCutCarryCleanup}",
                $"smooth={autoOptions.EnableYoloTemporalSmoothing}"
            };
            if (!useLegacyCompatibleSignature)
            {
                parts.Add($"processingMode={autoOptions.ProcessingMode}");
                parts.Add($"riskCascade={autoOptions.EnableYoloRiskCascade}");
                parts.Add($"strongInterval={FormatSignatureNumber(autoOptions.YoloStrongFullScanIntervalSeconds)}");
                parts.Add($"riskConfidence={FormatSignatureNumber(autoOptions.YoloRiskLowConfidenceThreshold)}");
                parts.Add($"riskArea={FormatSignatureNumber(autoOptions.YoloRiskSmallFaceAreaRatio)}");
                parts.Add($"riskEdge={FormatSignatureNumber(autoOptions.YoloRiskEdgeMarginRatio)}");
                parts.Add($"riskGap={autoOptions.YoloRiskMaxTrackGapFrames}");
                parts.Add($"strongConfirm={autoOptions.YoloStrongConfirmationFrames}");
                parts.Add($"primaryRoi={autoOptions.EnableYoloPrimaryRoiShortcut}");
            }

            parts.AddRange(new[]
            {
                $"tracking={autoOptions.UseTracking}",
                $"everyN={autoOptions.DetectEveryNFrames}",
                $"parallel={autoOptions.ParallelDetectorCount}",
                $"dump={autoOptions.DumpDetectionDiagnostics}"
            });

            switch (detectorFactoryOptions.Backend)
            {
                case FaceDetectorBackend.YoloFaceOnnx:
                    AppendYoloSignature(parts, detectorFactoryOptions.YoloFaceOnnxOptions);
                    if (autoOptions.EnableYoloRiskCascade)
                        AppendSecondaryFaceOnnxSignature(parts, detectorFactoryOptions.FaceOnnxOptions);
                    break;
                case FaceDetectorBackend.FaceOnnx:
                    AppendFaceOnnxSignature(parts, detectorFactoryOptions.FaceOnnxOptions);
                    break;
                case FaceDetectorBackend.ScrfdOnnx:
                    AppendScrfdSignature(parts, detectorFactoryOptions.ScrfdOnnxOptions);
                    break;
                case FaceDetectorBackend.YuNetOnnx:
                    AppendYuNetSignature(parts, detectorFactoryOptions.YuNetOnnxOptions);
                    break;
                default:
                    parts.Add($"detector={detectorFactoryOptions.Backend}");
                    break;
            }

            return string.Join("|", parts);
        }

        private static string BuildAutoRunIntentSignature(
            AutoMaskOptions autoOptions,
            FaceOnnxDetectorOptions detectorOptions,
            FaceDetectorFactoryOptions detectorFactoryOptions)
        {
            AutoMaskOptions effectiveAutoOptions = (autoOptions ?? new AutoMaskOptions()).ResolveProcessingMode();
            FaceDetectorFactoryOptions effectiveFactoryOptions = ResolveAutoRunDetectorFactoryOptions(
                effectiveAutoOptions,
                detectorOptions,
                detectorFactoryOptions,
                out _);
            return BuildAutoRunSignature(effectiveAutoOptions, effectiveFactoryOptions);
        }

        private static bool RequiresCompleteAutoTimeline(
            AutoMaskOptions autoOptions,
            FaceDetectorFactoryOptions detectorFactoryOptions)
        {
            AutoMaskOptions effectiveOptions = (autoOptions ?? new AutoMaskOptions()).ResolveProcessingMode();
            return effectiveOptions.EnableYoloRiskCascade &&
                detectorFactoryOptions?.Backend == FaceDetectorBackend.YoloFaceOnnx;
        }

        private static FaceDetectorFactoryOptions ResolveAutoRunDetectorFactoryOptions(
            AutoMaskOptions autoOptions,
            FaceOnnxDetectorOptions detectorOptions,
            FaceDetectorFactoryOptions detectorFactoryOptions,
            out FaceOnnxDetectorOptions? yoloSecondaryOptions)
        {
            detectorOptions ??= new FaceOnnxDetectorOptions();
            detectorFactoryOptions ??= FaceDetectorFactoryOptions.ForOnnx(detectorOptions);
            yoloSecondaryOptions = null;
            if (detectorFactoryOptions.Backend != FaceDetectorBackend.YoloFaceOnnx ||
                !autoOptions.EnableYoloRiskCascade)
            {
                return detectorFactoryOptions;
            }

            yoloSecondaryOptions = CreateYoloSecondaryDetectorOptions(detectorOptions);
            return detectorFactoryOptions.WithFaceOnnxOptions(yoloSecondaryOptions);
        }

        private static void AppendFaceOnnxSignature(List<string> parts, FaceOnnxDetectorOptions? options)
        {
            options ??= new FaceOnnxDetectorOptions();
            parts.Add($"ort={options.UseOrtOptimization}");
            parts.Add($"gpu={options.UseGpu}");
            parts.Add($"intra={options.IntraOpNumThreads?.ToString() ?? "null"}");
            parts.Add($"inter={options.InterOpNumThreads?.ToString() ?? "null"}");
            parts.Add($"parallelExec={options.UseParallelExecution?.ToString() ?? "null"}");
            parts.Add($"preprocess={options.EnablePreprocessParallelism?.ToString() ?? "null"}");
            parts.Add($"autoTune={options.AllowAutoTune?.ToString() ?? "null"}");
            parts.Add($"autoGpu={options.AllowAutoGpu?.ToString() ?? "null"}");
            parts.Add($"detect={FormatSignatureNumber(options.DetectionThreshold)}");
            parts.Add($"conf={FormatSignatureNumber(options.ConfidenceThreshold)}");
            parts.Add($"nms={FormatSignatureNumber(options.NmsThreshold)}");
        }

        private static void AppendSecondaryFaceOnnxSignature(List<string> parts, FaceOnnxDetectorOptions? options)
        {
            options ??= new FaceOnnxDetectorOptions();
            parts.Add($"secondaryOrt={options.UseOrtOptimization}");
            parts.Add($"secondaryGpu={options.UseGpu}");
            parts.Add($"secondaryIntra={options.IntraOpNumThreads?.ToString() ?? "null"}");
            parts.Add($"secondaryInter={options.InterOpNumThreads?.ToString() ?? "null"}");
            parts.Add($"secondaryParallelExec={options.UseParallelExecution?.ToString() ?? "null"}");
            parts.Add($"secondaryPreprocess={options.EnablePreprocessParallelism?.ToString() ?? "null"}");
            parts.Add($"secondaryAutoTune={options.AllowAutoTune?.ToString() ?? "null"}");
            parts.Add($"secondaryAutoGpu={options.AllowAutoGpu?.ToString() ?? "null"}");
            parts.Add($"secondaryDetect={FormatSignatureNumber(options.DetectionThreshold)}");
            parts.Add($"secondaryConf={FormatSignatureNumber(options.ConfidenceThreshold)}");
            parts.Add($"secondaryNms={FormatSignatureNumber(options.NmsThreshold)}");
        }

        private static FaceOnnxDetectorOptions CreateYoloSecondaryDetectorOptions(
            FaceOnnxDetectorOptions? configured)
        {
            configured ??= new FaceOnnxDetectorOptions();
            return new FaceOnnxDetectorOptions
            {
                UseOrtOptimization = configured.UseOrtOptimization,
                UseGpu = false,
                IntraOpNumThreads = configured.IntraOpNumThreads,
                InterOpNumThreads = configured.InterOpNumThreads,
                UseParallelExecution = false,
                DetectionThreshold = null,
                ConfidenceThreshold = null,
                NmsThreshold = null,
                EnablePreprocessParallelism = configured.EnablePreprocessParallelism,
                AllowAutoTune = false,
                AllowAutoGpu = false
            };
        }

        private static void AppendYoloSignature(List<string> parts, YoloFaceOnnxDetectorOptions? options)
        {
            if (options == null)
            {
                parts.Add("yolo=null");
                return;
            }

            parts.Add($"model={NormalizeSignaturePath(options.ModelPath)}");
            parts.Add($"type={options.ModelType}");
            parts.Add($"ort={options.UseOrtOptimization}");
            parts.Add($"gpu={options.UseGpu}");
            parts.Add($"intra={options.IntraOpNumThreads?.ToString() ?? "null"}");
            parts.Add($"inter={options.InterOpNumThreads?.ToString() ?? "null"}");
            parts.Add($"parallelExec={options.UseParallelExecution?.ToString() ?? "null"}");
            if (options.EnableCoreMl)
                parts.Add("coreMl=True");
            parts.Add($"input={options.InputWidth?.ToString() ?? "null"}x{options.InputHeight?.ToString() ?? "null"}");
            parts.Add($"obj={FormatSignatureNumber(options.ObjectnessThreshold)}");
            parts.Add($"conf={FormatSignatureNumber(options.ConfidenceThreshold)}");
            parts.Add($"nms={FormatSignatureNumber(options.NmsThreshold)}");
            parts.Add($"max={options.MaxDetections}");
            parts.Add($"tiling={options.UseTiling}");
            parts.Add($"tileOnly={!options.IncludeFullFrameWhenTiling}");
            parts.Add($"tiles={options.TileColumns}x{options.TileRows}");
            parts.Add($"tileOverlap={FormatSignatureNumber(options.TileOverlapRatio)}");
            parts.Add($"letterbox={options.UseLetterboxResize}");
            parts.Add($"centerPad={options.CenterLetterboxPadding}");
            parts.Add($"padValue={FormatSignatureNumber(options.LetterboxPaddingValue)}");
            parts.Add($"rgb={options.UseRgbInput}");
            parts.Add($"inputScale={FormatSignatureNumber(options.InputScale)}");
            parts.Add($"lowPos={options.UseLowConfidencePositionFilter}:{FormatSignatureNumber(options.LowConfidencePositionMaxConfidence)}:{FormatSignatureNumber(options.LowConfidencePositionMinCenterYRatio)}");
            parts.Add($"small={options.UseSmallAreaFilter}:{FormatSignatureNumber(options.SmallAreaMaxAreaRatio)}");
            parts.Add($"aspect={options.UseAspectRatioFilter}:{FormatSignatureNumber(options.MinAspectRatio)}:{FormatSignatureNumber(options.MaxAspectRatio)}");
            parts.Add($"topSmall={options.UseTopSmallLowConfidenceFilter}:{FormatSignatureNumber(options.TopSmallLowConfidenceMaxConfidence)}:{FormatSignatureNumber(options.TopSmallLowConfidenceMaxCenterYRatio)}:{FormatSignatureNumber(options.TopSmallLowConfidenceMaxAreaRatio)}");
            parts.Add($"largeScale={FormatSignatureNumber(options.LargeBoxWidthScale)}:{FormatSignatureNumber(options.LargeBoxHeightScale)}:{FormatSignatureNumber(options.LargeBoxMinAreaRatio)}");
            parts.Add($"landmark={options.UseYolo5LandmarkBoxRefine}:{FormatSignatureNumber(options.Yolo5LandmarkBoxMinAreaRatio)}:{FormatSignatureNumber(options.Yolo5LandmarkBoxWidthScale)}:{FormatSignatureNumber(options.Yolo5LandmarkBoxHeightScale)}:{FormatSignatureNumber(options.Yolo5LandmarkBoxCenterYOffsetRatio)}:{FormatSignatureNumber(options.Yolo5LandmarkBoxMinOriginalIou)}");
        }

        private static void AppendScrfdSignature(List<string> parts, ScrfdOnnxDetectorOptions? options)
        {
            if (options == null)
            {
                parts.Add("scrfd=null");
                return;
            }

            parts.Add($"model={NormalizeSignaturePath(options.ModelPath)}");
            parts.Add($"ort={options.UseOrtOptimization}");
            parts.Add($"gpu={options.UseGpu}");
            parts.Add($"intra={options.IntraOpNumThreads?.ToString() ?? "null"}");
            parts.Add($"inter={options.InterOpNumThreads?.ToString() ?? "null"}");
            parts.Add($"parallelExec={options.UseParallelExecution?.ToString() ?? "null"}");
            parts.Add($"conf={FormatSignatureNumber(options.ConfidenceThreshold)}");
            parts.Add($"nms={FormatSignatureNumber(options.NmsThreshold)}");
            parts.Add($"input={options.InputWidth?.ToString() ?? "null"}x{options.InputHeight?.ToString() ?? "null"}");
            parts.Add($"normalize={FormatSignatureNumber(options.InputMean)}:{FormatSignatureNumber(options.InputStd)}");
            parts.Add($"bboxStride={options.MultiplyBboxByStride}");
            parts.Add($"anchorOffset={FormatSignatureNumber(options.AnchorCenterOffset)}");
            parts.Add($"letterbox={options.UseLetterboxResize}");
            parts.Add($"centerPad={options.CenterLetterboxPadding}");
            parts.Add($"padValue={FormatSignatureNumber(options.LetterboxPaddingValue)}");
            parts.Add($"rgb={options.UseRgbInput}");
        }

        private static void AppendYuNetSignature(List<string> parts, YuNetOnnxDetectorOptions? options)
        {
            if (options == null)
            {
                parts.Add("yunet=null");
                return;
            }

            parts.Add($"model={NormalizeSignaturePath(options.ModelPath)}");
            parts.Add($"ort={options.UseOrtOptimization}");
            parts.Add($"intra={options.IntraOpNumThreads?.ToString() ?? "null"}");
            parts.Add($"inter={options.InterOpNumThreads?.ToString() ?? "null"}");
            parts.Add($"parallelExec={options.UseParallelExecution?.ToString() ?? "null"}");
            parts.Add($"conf={FormatSignatureNumber(options.ConfidenceThreshold)}");
            parts.Add($"nms={FormatSignatureNumber(options.NmsThreshold)}");
            parts.Add($"topK={options.TopK}");
            parts.Add($"tiling={options.UseTiling}");
            parts.Add($"tileOnly={!options.IncludeFullFrameWhenTiling}");
            parts.Add($"tiles={options.TileColumns}x{options.TileRows}");
            parts.Add($"tileOverlap={FormatSignatureNumber(options.TileOverlapRatio)}");
        }

        private static string FormatSignatureNumber(double value)
            => value.ToString("R", CultureInfo.InvariantCulture);

        private static string FormatSignatureNumber(float value)
            => value.ToString("R", CultureInfo.InvariantCulture);

        private static string FormatSignatureNumber(float? value)
            => value?.ToString("R", CultureInfo.InvariantCulture) ?? "null";

        private static string NormalizeSignaturePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return "null";

            try
            {
                return Path.GetFullPath(path).Trim();
            }
            catch
            {
                return path.Trim();
            }
        }

        private static string BuildAutoRunEvidenceSignature(
            AutoMaskOptions autoOptions,
            FaceDetectorFactoryOptions detectorFactoryOptions)
        {
            string signature = BuildAutoRunSignature(autoOptions, detectorFactoryOptions);
            string? modelPath = GetDetectorModelPath(detectorFactoryOptions);
            if (string.IsNullOrWhiteSpace(modelPath))
                return signature;

            string normalizedPath = NormalizeSignaturePath(modelPath);
            string modelIdentity;
            try
            {
                var modelFile = new FileInfo(normalizedPath);
                modelIdentity = $"{modelFile.Name}:{modelFile.Length}:{modelFile.LastWriteTimeUtc.Ticks}";
            }
            catch
            {
                modelIdentity = Path.GetFileName(modelPath);
            }
            return signature.Replace(
                $"model={normalizedPath}",
                $"model={modelIdentity}",
                StringComparison.Ordinal);
        }

        private static string? GetDetectorModelPath(FaceDetectorFactoryOptions options)
        {
            return options.Backend switch
            {
                FaceDetectorBackend.YoloFaceOnnx => options.YoloFaceOnnxOptions?.ModelPath,
                FaceDetectorBackend.ScrfdOnnx => options.ScrfdOnnxOptions?.ModelPath,
                FaceDetectorBackend.YuNetOnnx => options.YuNetOnnxOptions?.ModelPath,
                _ => null
            };
        }

        private static string BuildAutoExecutionSignature(
            AutoMaskOptions autoOptions,
            FaceDetectorFactoryOptions detectorFactoryOptions,
            string executionProviderLabel,
            string sourceEvidenceId)
        {
            string provider = NormalizeExecutionProviderLabel(executionProviderLabel);
            string source = string.IsNullOrWhiteSpace(sourceEvidenceId)
                ? "unavailable"
                : sourceEvidenceId.Trim();
            return $"exec-v1|{BuildAutoRunEvidenceSignature(autoOptions, detectorFactoryOptions)}|provider={provider}|source={source}";
        }

        private static string NormalizeExecutionProviderLabel(string? label)
            => DetectorExecutionProviderIdentity.NormalizeLabel(label);

        private static string GetDetectorExecutionProviderLabel(IFaceDetector detector)
        {
            return DetectorExecutionProviderIdentity.GetCanonicalLabel(detector);
        }

        private static string BuildSourceEvidenceId(string path)
        {
            try
            {
                var file = new FileInfo(Path.GetFullPath(path));
                string identity = $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
                byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
                return Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
            }
            catch
            {
                return "unavailable";
            }
        }

    }
}
