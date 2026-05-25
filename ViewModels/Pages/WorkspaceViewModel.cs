using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FaceShield.Enums.Workspace; // 🔹 추가
using FaceShield.Services.Analysis;
using FaceShield.Services.FaceDetection;
using FaceShield.Services.Video;
using FaceShield.Services.Video.Session;
using FaceShield.Services.Workspace;
using FaceShield.ViewModels.Workspace;
using FaceShield.Views.Dialogs;
using System;
using System.Collections.Generic;
using System.IO;
using System.Collections.ObjectModel;
using System.Linq;
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
        private const float TemporalConfidenceStrong = 0.68f;
        private const float TemporalConfidenceWeak = 0.50f;
        private const double TemporalIouMin = 0.20;
        private const double TemporalMaxCenterShiftRatio = 0.55;
        private const double TemporalMaxAreaChangeRatio = 3.2;
        private const double TemporalHoleFillIouMin = 0.12;
        private const double TemporalDuplicateIouMin = 0.35;
        private const double TemporalSmoothIouMin = 0.18;
        private const double TemporalSmoothWeight = 0.42;
        private const int TemporalSmoothPasses = 2;
        private const int TemporalSmoothSearchWindowFrames = 2;
        private const int TemporalMinRunLength = 2;
        private const int SuspiciousNoFaceMaxGap = 8;
        private const float YoloFinalMaskLowConfidenceThreshold = 0.38f;
        private const int FinalMaskShortGapMaxFrames = 3;
        private const double FinalMaskLargeJumpAreaChangeRatio = 4.0;
        private const double FinalMaskLargeJumpCenterShift = 0.20;
        private const float YoloFinalMaskWeakIsolatedConfidenceMax = 0.50f;
        private const float YoloSceneCutDirectCarryMaxConfidence = 0.78f;
        private const float YoloSceneCutPostCutCarryMaxConfidence = 0.78f;
        private const double YoloSceneCutDifferenceThreshold = 0.15;
        private const double YoloSceneCutDirectDifferenceThreshold = 0.15;
        private const int YoloSceneCutMatchingTailMaxFrames = 5;
        private const float YoloSceneCutMatchingTailMaxConfidence = 0.90f;
        private const double YoloSceneCutCandidateMatchMinIou = 0.55;
        private const double YoloSceneCutCandidateMatchMaxCenterShiftRatio = 0.45;
        private const double YoloSceneCutCandidateMatchMaxAreaChangeRatio = 2.0;
        private const int YoloSceneCutPostCutLookbackFrames = 3;
        private const int YoloFinalMaskStableGapMaxFrames = 5;
        private const double YoloFinalMaskEdgeMarginRatio = 0.02;
        private const double YoloFinalMaskTinyWeakAreaRatio = 0.0012;
        private const float YoloFinalMaskTinyShortConfidenceMax = 0.62f;
        private const double YoloFinalMaskTinyShortAreaRatio = 0.0009;
        private const float YoloFinalMaskUpperWeakConfidenceMax = 0.60f;
        private const double YoloFinalMaskUpperWeakCenterYRatio = 0.10;
        private const double YoloFinalMaskUpperWeakAreaRatio = 0.0065;
        private int _autoResumeIndex;
        private bool _autoCompleted;
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
        private CancellationTokenSource? _autoCts;
        private CancellationTokenSource? _exportCts;

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
            _autoResumeIndex > 0;

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
                    FramePreview.OnFrameIndexChanged(index);
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

                await SaveVideoAsync();
                PersistWorkspaceState();
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
            string? runId = null)
        {
            string input = FrameList.VideoPath;
            string exportRunId = string.IsNullOrWhiteSpace(runId)
                ? $"export-{Guid.NewGuid():N}"
                : runId;
            string output = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(input)!,
                System.IO.Path.GetFileNameWithoutExtension(input) + "_blur.mp4");

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

                await Task.Run(() =>
                {
                    exporter.Export(input, output, blurRadius: ToolPanel.BlurRadius, progress, _exportCts.Token, exportRunId);
                }, _exportCts.Token);
                if (exporter.LastExportSummary != null)
                    System.Diagnostics.Debug.WriteLine($"[WorkspaceExport] {exporter.LastExportSummary.ToLogLine()}");

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
            string runId = $"auto-{Guid.NewGuid():N}";
            try
            {
                var detectorOptions = _detectorOptions;
                var detectorFactoryOptions = _detectorFactoryOptions;
                int tunedSessions = Math.Max(1, _autoOptions.ParallelDetectorCount);
                if (_detectorFactoryOptions.Backend == FaceDetectorBackend.FaceOnnx &&
                    _detectorOptions.AllowAutoTune != false)
                {
                    var tuneToken = _autoCts?.Token ?? CancellationToken.None;
                    var tuneResult = await Task.Run(() =>
                    {
                        bool tuned = DetectorAutoTuner.TryTune(
                            FrameList.VideoPath,
                            _autoOptions.DownscaleRatio,
                            _autoOptions.DownscaleQuality,
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

                var runOptions = new AutoMaskOptions
                {
                    DownscaleRatio = _autoOptions.DownscaleRatio,
                    DownscaleQuality = _autoOptions.DownscaleQuality,
                    UseTracking = _autoOptions.UseTracking,
                    DetectEveryNFrames = _autoOptions.DetectEveryNFrames,
                    ParallelDetectorCount = tunedSessions,
                    RunId = runId,
                    FilterProfile = _autoOptions.FilterProfile,
                    DumpDetectionDiagnostics = _autoOptions.DumpDetectionDiagnostics
                };

                var detectorFactory = new FaceDetectorFactory(detectorFactoryOptions);
                using IFaceDetector detector = detectorFactory.CreateDetector();
                var generator = CreateAutoMaskGenerator(detector, detectorFactory, runOptions);
                _autoCompleted = false;
                int lastProcessed = Math.Max(0, _autoResumeIndex);
                if (lastProcessed == 0)
                    _maskProvider.ClearFaceMasks();

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

                var trackPost = ApplyAutoTemporalFixes();
                if (detector is IBgraFaceDetector bgraDetector)
                    RefineAutoFacesWithRoi(FrameList.VideoPath, bgraDetector, trackPost, detectorOptions);
                if (_autoOptions.FilterProfile == FaceFilterProfile.Yolo)
                    RemoveYoloWeakIsolatedFinalMasks(FrameList.VideoPath, token);
                IReadOnlyList<string> yoloPreSmoothCutPairs = Array.Empty<string>();
                if (_autoOptions.UseTracking && _autoOptions.FilterProfile == FaceFilterProfile.Yolo)
                {
                    var preSmoothGuard = RemoveYoloTrackFillAcrossSceneCuts(FrameList.VideoPath, trackPost, token, "pre-smooth");
                    yoloPreSmoothCutPairs = preSmoothGuard.CutFramePairs;
                }
                if (_autoOptions.UseTracking)
                {
                    ApplyAutoTemporalSmoothing(_autoOptions.FilterProfile == FaceFilterProfile.Yolo
                        ? yoloPreSmoothCutPairs
                        : Array.Empty<string>());
                }
                if (_autoOptions.UseTracking && _autoOptions.FilterProfile == FaceFilterProfile.Yolo)
                    RemoveYoloTrackFillAcrossSceneCuts(FrameList.VideoPath, trackPost, token, "post-smooth");
                if (_autoOptions.FilterProfile == FaceFilterProfile.Yolo)
                    RemoveYoloWeakIsolatedFinalMasks(FrameList.VideoPath, token, fillStableGaps: false);
                LogFinalMaskSummary();
                RefreshAutoPreviewAfterPostProcess(exportAfter);

                if (!exportAfter)
                    await BuildAutoAnomaliesAsync();

                // Detection and post-processing are complete at this point. If export is
                // canceled afterwards, do not reopen as a partial detection resume.
                _autoCompleted = true;
                _autoResumeIndex = 0;

                if (exportAfter)
                {
                    bool exported = await SaveVideoAsync(
                        exportProgress,
                        _autoCts?.Token ?? CancellationToken.None,
                        updateToolPanel: false,
                        runId: runId);
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
                _autoCompleted = false;
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
                if (!persisted)
                    PersistWorkspaceState(includePreviewMask: !exportAfter);
            }
        }

        private void ApplyAutoTemporalSmoothing(IReadOnlyList<string>? blockedCutPairs = null)
        {
            int total = FrameList.TotalFrames;
            if (total < 3)
                return;

            var blockedCutStarts = BuildTemporalSmoothingCutStarts(blockedCutPairs);
            var facesByFrame = new List<Rect>?[total];
            var confByFrame = new List<float>?[total];
            var sizeByFrame = new PixelSize[total];
            var hasStored = new bool[total];

            foreach (int index in _maskProvider.GetStoredMaskFrameIndices())
            {
                if (index >= 0 && index < total)
                    hasStored[index] = true;
            }

            foreach (var entry in _maskProvider.GetFaceMaskEntries())
            {
                int i = entry.Key;
                if (i < 0 || i >= total || hasStored[i])
                    continue;

                var data = entry.Value;
                if (data.Faces.Count > 0)
                {
                    facesByFrame[i] = new List<Rect>(data.Faces);
                    confByFrame[i] = new List<float>(data.Confidences);
                    sizeByFrame[i] = data.Size;
                }
            }

            for (int pass = 0; pass < TemporalSmoothPasses; pass++)
            {
                for (int i = 1; i < total - 1; i++)
                {
                    if (hasStored[i] || facesByFrame[i] == null)
                        continue;

                    var currentFaces = facesByFrame[i]!;
                    var smoothed = new List<Rect>(currentFaces.Count);
                    var prevFaces = FindNearestTemporalFaces(facesByFrame, i, -1, TemporalSmoothSearchWindowFrames, blockedCutStarts);
                    var nextFaces = FindNearestTemporalFaces(facesByFrame, i, 1, TemporalSmoothSearchWindowFrames, blockedCutStarts);

                    for (int j = 0; j < currentFaces.Count; j++)
                    {
                        var current = currentFaces[j];
                        Rect target = current;
                        int targetCount = 0;

                        double prevIou = GetMaxIoU(current, prevFaces, out var prevMatch);
                        double nextIou = GetMaxIoU(current, nextFaces, out var nextMatch);

                        if (prevIou >= TemporalSmoothIouMin && IsReasonableTemporalMatch(current, prevMatch))
                        {
                            target = prevMatch;
                            targetCount++;
                        }

                        if (nextIou >= TemporalSmoothIouMin && IsReasonableTemporalMatch(current, nextMatch))
                        {
                            target = targetCount == 0
                                ? nextMatch
                                : InterpolateRect(target, nextMatch);
                            targetCount++;
                        }

                        smoothed.Add(targetCount == 0
                            ? current
                            : BlendRect(current, target, TemporalSmoothWeight));
                    }

                    facesByFrame[i] = smoothed;
                }
            }

            for (int i = 0; i < total; i++)
            {
                if (hasStored[i] || facesByFrame[i] == null || facesByFrame[i]!.Count == 0)
                    continue;

                var confs = confByFrame[i] ?? new List<float>(facesByFrame[i]!.Count);
                if (confs.Count != facesByFrame[i]!.Count)
                {
                    confs = new List<float>(facesByFrame[i]!.Count);
                    for (int j = 0; j < facesByFrame[i]!.Count; j++)
                        confs.Add(1.0f);
                }

                float minConf = float.MaxValue;
                for (int j = 0; j < confs.Count; j++)
                    minConf = Math.Min(minConf, confs[j]);

                _maskProvider.SetFaceRects(
                    i,
                    facesByFrame[i]!,
                    sizeByFrame[i],
                    minConf == float.MaxValue ? null : minConf,
                    confs);
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

        private FaceTrackPostProcessResult ApplyAutoTemporalFixes()
        {
            if (!_autoOptions.UseTracking)
                return FaceTrackPostProcessResult.Empty;

            var result = new FaceTrackInterpolator().Apply(
                _maskProvider,
                FrameList.TotalFrames,
                BuildTrackPostProcessOptions(_autoOptions.FilterProfile));

            if (result.FilledGapFaces > 0 ||
                result.FilledLostFaces > 0 ||
                result.FilledInitialFaces > 0 ||
                result.RemovedShortFaces > 0 ||
                result.RemovedSparseFaces > 0 ||
                result.RemovedEdgeTailFaces > 0 ||
                result.RemovedLowerFrameFaces > 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[FaceTrackPost] tracks={result.TrackCount} filled={result.FilledGapFaces} lostFilled={result.FilledLostFaces} initialFilled={result.FilledInitialFaces} lostFrames={FormatFrameList(result.FilledLostFrameIndices)} removedShort={result.RemovedShortFaces} removedSparse={result.RemovedSparseFaces} removedEdgeTail={result.RemovedEdgeTailFaces} removedLower={result.RemovedLowerFrameFaces} rewritten={result.RewrittenFrames}");
            }

            return result;
        }

        private FaceTrackSceneCutGuardResult RemoveYoloTrackFillAcrossSceneCuts(
            string videoPath,
            FaceTrackPostProcessResult trackPost,
            CancellationToken cancellationToken,
            string stage)
        {
            var guard = new FaceTrackSceneCutGuard();
            var directCandidates = guard.BuildWeakTrackTransitionCandidates(
                _maskProvider,
                BuildTrackPostProcessOptions(FaceFilterProfile.Yolo),
                maxTargetConfidence: YoloSceneCutDirectCarryMaxConfidence,
                maxTransitionGap: SuspiciousNoFaceMaxGap,
                minConfidenceDrop: 0.06f,
                maxPostCutCarryFrames: 5);
            var postCutCandidates = guard.BuildWeakPostCutCarryCandidates(
                _maskProvider,
                maxTargetConfidence: YoloSceneCutPostCutCarryMaxConfidence,
                maxCarryFrames: 5,
                sourceLookbackFrames: YoloSceneCutPostCutLookbackFrames);
            var candidates = trackPost.FilledGapFacesInfo
                .Concat(trackPost.FilledLostFacesInfo)
                .Concat(trackPost.FilledInitialFacesInfo)
                .Concat(directCandidates)
                .Concat(postCutCandidates)
                .ToArray();

            if (candidates.Length > 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[FaceTrackSceneCutGuard] stage={stage} start directCandidates={directCandidates.Count} filled={trackPost.FilledGapFacesInfo.Count} lostFilled={trackPost.FilledLostFacesInfo.Count} initialFilled={trackPost.FilledInitialFacesInfo.Count} totalCandidates={candidates.Length}");
            }

            var result = guard.Apply(
                _maskProvider,
                videoPath,
                candidates,
                differenceThreshold: YoloSceneCutDifferenceThreshold,
                directDifferenceThreshold: YoloSceneCutDirectDifferenceThreshold,
                removeMatchingTailFrames: YoloSceneCutMatchingTailMaxFrames,
                removeMatchingTailMaxConfidence: YoloSceneCutMatchingTailMaxConfidence,
                candidateMatchMinIou: YoloSceneCutCandidateMatchMinIou,
                candidateMatchMaxCenterShiftRatio: YoloSceneCutCandidateMatchMaxCenterShiftRatio,
                candidateMatchMaxAreaChangeRatio: YoloSceneCutCandidateMatchMaxAreaChangeRatio,
                cancellationToken: cancellationToken);

            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[FaceTrackSceneCutGuard] stage={stage} skipped directCandidates={directCandidates.Count} postCutCandidates={postCutCandidates.Count} checked={result.Checked} checkedPairs={FormatTextList(result.CheckedFramePairs)} maxDiff={result.MaxDifference:0.###} cutPairs={FormatTextList(result.CutFramePairs)} removed={result.Removed} removedFrames={FormatFrameList(result.RemovedFrameIndices)} error={result.Error}");
                return result;
            }

            if (result.Checked > 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[FaceTrackSceneCutGuard] stage={stage} directCandidates={directCandidates.Count} postCutCandidates={postCutCandidates.Count} checked={result.Checked} checkedPairs={FormatTextList(result.CheckedFramePairs)} maxDiff={result.MaxDifference:0.###} cutPairs={FormatTextList(result.CutFramePairs)} removed={result.Removed} removedFrames={FormatFrameList(result.RemovedFrameIndices)} threshold={result.Threshold:0.###} elapsedMs={result.ElapsedMs}");
            }

            return result;
        }

        private void RemoveYoloWeakIsolatedFinalMasks(
            string videoPath,
            CancellationToken cancellationToken,
            bool fillStableGaps = true)
        {
            var postProcessor = new YoloFinalMaskPostProcessor();
            var cleanup = postProcessor.RemoveWeakIsolatedMasks(
                _maskProvider,
                new YoloFinalMaskCleanupOptions
                {
                    NeighborWindowFrames = 1,
                    WeakConfidenceMax = YoloFinalMaskWeakIsolatedConfidenceMax,
                    EdgeMarginRatio = YoloFinalMaskEdgeMarginRatio
                });
            if (cleanup.RemovedWeakIsolatedFaces <= 0)
            {
                if (fillStableGaps)
                    FillYoloStableFinalMaskGaps(postProcessor, videoPath, cancellationToken);
                return;
            }

            System.Diagnostics.Debug.WriteLine(
                $"[YoloFinalMaskCleanup] removedWeakIsolated={cleanup.RemovedWeakIsolatedFaces} removedWeakUnsupported={cleanup.RemovedWeakUnsupportedFaces} removedWeakShortClusters={cleanup.RemovedWeakShortClusterFaces} removedWeakTinyClusters={cleanup.RemovedWeakTinyClusterFaces} removedTinyShortClusters={cleanup.RemovedTinyShortClusterFaces} removedTinyIsolated={cleanup.RemovedTinyIsolatedFaces} removedUpperWeakClusters={cleanup.RemovedUpperWeakClusterFaces} removedFrames={FormatFrameList(cleanup.RemovedFrameIndices)} maxConf={YoloFinalMaskWeakIsolatedConfidenceMax:0.###}");

            if (fillStableGaps)
                FillYoloStableFinalMaskGaps(postProcessor, videoPath, cancellationToken);
        }

        private void FillYoloStableFinalMaskGaps(
            YoloFinalMaskPostProcessor postProcessor,
            string videoPath,
            CancellationToken cancellationToken)
        {
            var gapFill = postProcessor.FillShortStableGaps(
                _maskProvider,
                new YoloFinalMaskGapFillOptions
                {
                    MaxGapFrames = YoloFinalMaskStableGapMaxFrames
                });
            if (gapFill.FilledFaces <= 0)
                return;

            System.Diagnostics.Debug.WriteLine(
                $"[YoloFinalMaskGapFill] filled={gapFill.FilledFaces} frames={FormatFrameList(gapFill.FilledFrameIndices)}");

            if (gapFill.CutGuardFacesInfo.Count == 0)
                return;

            var guard = new FaceTrackSceneCutGuard().Apply(
                _maskProvider,
                videoPath,
                gapFill.CutGuardFacesInfo,
                differenceThreshold: YoloSceneCutDifferenceThreshold,
                directDifferenceThreshold: YoloSceneCutDirectDifferenceThreshold,
                candidateMatchMinIou: YoloSceneCutCandidateMatchMinIou,
                candidateMatchMaxCenterShiftRatio: YoloSceneCutCandidateMatchMaxCenterShiftRatio,
                candidateMatchMaxAreaChangeRatio: YoloSceneCutCandidateMatchMaxAreaChangeRatio,
                cancellationToken: cancellationToken);

            if (!string.IsNullOrWhiteSpace(guard.Error))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[YoloFinalMaskGapFillSceneCutGuard] skipped candidates={gapFill.CutGuardFacesInfo.Count} checked={guard.Checked} checkedPairs={FormatTextList(guard.CheckedFramePairs)} maxDiff={guard.MaxDifference:0.###} cutPairs={FormatTextList(guard.CutFramePairs)} removed={guard.Removed} removedFrames={FormatFrameList(guard.RemovedFrameIndices)} error={guard.Error}");
                return;
            }

            if (guard.Checked > 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[YoloFinalMaskGapFillSceneCutGuard] candidates={gapFill.CutGuardFacesInfo.Count} checked={guard.Checked} checkedPairs={FormatTextList(guard.CheckedFramePairs)} maxDiff={guard.MaxDifference:0.###} cutPairs={FormatTextList(guard.CutFramePairs)} removed={guard.Removed} removedFrames={FormatFrameList(guard.RemovedFrameIndices)} threshold={guard.Threshold:0.###} elapsedMs={guard.ElapsedMs}");
            }
        }

        private void LogFinalMaskSummary()
        {
            if (_autoOptions.FilterProfile != FaceFilterProfile.Yolo)
                return;

            var entries = _maskProvider.GetFaceMaskEntries()
                .Where(static x => x.Value.Faces.Count > 0)
                .OrderBy(static x => x.Key)
                .ToArray();
            if (entries.Length == 0)
            {
                System.Diagnostics.Debug.WriteLine("[FinalMaskSummary] profile=Yolo frames=0 rows=0 frameRange=none shortGaps=0 shortGapRanges=none largeJumpGaps=0 largeJumpRanges=none isolated=0 isolatedFrames=none lowConf=0 lowConfFrames=none weakNonEdge=0 weakNonEdgeFrames=none upperWeak=0 upperWeakFrames=none tinyWeak=0 tinyWeakFrames=none tinyShort=0 tinyShortFrames=none");
                return;
            }

            var frames = entries.Select(static x => x.Key).ToArray();
            int rows = entries.Sum(static x => x.Value.Faces.Count);
            var shortGapRanges = new List<string>();
            var largeJumpGapRanges = new List<string>();
            int shortGapCount = 0;
            for (int i = 1; i < frames.Length; i++)
            {
                int missing = frames[i] - frames[i - 1] - 1;
                if (missing <= 0 || missing > FinalMaskShortGapMaxFrames)
                    continue;

                shortGapCount++;
                int start = frames[i - 1] + 1;
                int end = frames[i] - 1;
                string range = FormatFrameRange(start, end);
                shortGapRanges.Add(range);

                if (TryGetBestFinalMaskFace(entries[i - 1].Value, out var previousFace) &&
                    TryGetBestFinalMaskFace(entries[i].Value, out var nextFace))
                {
                    double areaChange = GetFinalMaskAreaChange(previousFace, nextFace);
                    double centerShift = GetFinalMaskCenterShift(previousFace, nextFace);
                    if (areaChange >= FinalMaskLargeJumpAreaChangeRatio ||
                        centerShift >= FinalMaskLargeJumpCenterShift)
                    {
                        largeJumpGapRanges.Add(range);
                    }
                }
            }

            var isolatedFrames = new List<int>();
            for (int i = 0; i < frames.Length; i++)
            {
                bool hasPreviousNeighbor = i > 0 && frames[i] - frames[i - 1] <= 1;
                bool hasNextNeighbor = i < frames.Length - 1 && frames[i + 1] - frames[i] <= 1;
                if (!hasPreviousNeighbor && !hasNextNeighbor)
                    isolatedFrames.Add(frames[i]);
            }

            int lowConfidenceRows = 0;
            int weakNonEdgeRows = 0;
            int upperWeakRows = 0;
            int tinyWeakRows = 0;
            int tinyShortRows = 0;
            var lowConfidenceFrames = new HashSet<int>();
            var weakNonEdgeFrames = new HashSet<int>();
            var upperWeakFrames = new HashSet<int>();
            var tinyWeakFrames = new HashSet<int>();
            var tinyShortFrames = new HashSet<int>();
            foreach (var entry in entries)
            {
                int frameIndex = entry.Key;
                var data = entry.Value;
                for (int i = 0; i < data.Faces.Count; i++)
                {
                    var face = data.Faces[i];
                    float confidence = i < data.Confidences.Count
                        ? data.Confidences[i]
                        : data.MinConfidence ?? 1.0f;
                    if (confidence <= YoloFinalMaskLowConfidenceThreshold)
                    {
                        lowConfidenceRows++;
                        lowConfidenceFrames.Add(frameIndex);
                    }

                    if (confidence <= YoloFinalMaskWeakIsolatedConfidenceMax &&
                        !TouchesFinalMaskFrameEdge(face, data.Size))
                    {
                        weakNonEdgeRows++;
                        weakNonEdgeFrames.Add(frameIndex);
                        if (IsTinyFinalMaskFace(face, data.Size, YoloFinalMaskTinyWeakAreaRatio))
                        {
                            tinyWeakRows++;
                            tinyWeakFrames.Add(frameIndex);
                        }
                    }

                    if (confidence <= YoloFinalMaskUpperWeakConfidenceMax &&
                        !TouchesFinalMaskFrameEdge(face, data.Size) &&
                        IsUpperWeakFinalMaskFace(face, data.Size))
                    {
                        upperWeakRows++;
                        upperWeakFrames.Add(frameIndex);
                    }

                    if (confidence <= YoloFinalMaskTinyShortConfidenceMax &&
                        !TouchesFinalMaskFrameEdge(face, data.Size) &&
                        IsTinyFinalMaskFace(face, data.Size, YoloFinalMaskTinyShortAreaRatio))
                    {
                        tinyShortRows++;
                        tinyShortFrames.Add(frameIndex);
                    }
                }
            }

            System.Diagnostics.Debug.WriteLine(
                $"[FinalMaskSummary] profile=Yolo frames={frames.Length} rows={rows} frameRange={frames[0]}-{frames[^1]} shortGaps={shortGapCount} shortGapRanges={FormatTextList(shortGapRanges)} largeJumpGaps={largeJumpGapRanges.Count} largeJumpRanges={FormatTextList(largeJumpGapRanges)} isolated={isolatedFrames.Count} isolatedFrames={FormatFrameList(isolatedFrames)} lowConf={lowConfidenceRows} lowConfFrames={FormatFrameList(lowConfidenceFrames.OrderBy(static x => x).ToArray())} weakNonEdge={weakNonEdgeRows} weakNonEdgeFrames={FormatFrameList(weakNonEdgeFrames.OrderBy(static x => x).ToArray())} upperWeak={upperWeakRows} upperWeakFrames={FormatFrameList(upperWeakFrames.OrderBy(static x => x).ToArray())} tinyWeak={tinyWeakRows} tinyWeakFrames={FormatFrameList(tinyWeakFrames.OrderBy(static x => x).ToArray())} tinyShort={tinyShortRows} tinyShortFrames={FormatFrameList(tinyShortFrames.OrderBy(static x => x).ToArray())}");
        }

        private static bool TouchesFinalMaskFrameEdge(Rect face, PixelSize size)
        {
            if (size.Width <= 0 || size.Height <= 0)
                return false;

            double marginX = size.Width * YoloFinalMaskEdgeMarginRatio;
            double marginY = size.Height * YoloFinalMaskEdgeMarginRatio;
            return face.X <= marginX ||
                face.Y <= marginY ||
                face.Right >= size.Width - marginX ||
                face.Bottom >= size.Height - marginY;
        }

        private static bool IsTinyFinalMaskFace(Rect face, PixelSize size, double maxAreaRatio)
        {
            if (size.Width <= 0 || size.Height <= 0)
                return false;

            double frameArea = Math.Max(1.0, size.Width * (double)size.Height);
            double areaRatio = Math.Max(0.0, face.Width * face.Height) / frameArea;
            return areaRatio <= maxAreaRatio;
        }

        private static bool IsUpperWeakFinalMaskFace(Rect face, PixelSize size)
        {
            if (size.Width <= 0 || size.Height <= 0)
                return false;

            double frameArea = Math.Max(1.0, size.Width * (double)size.Height);
            double areaRatio = Math.Max(0.0, face.Width * face.Height) / frameArea;
            double centerYRatio = (face.Y + face.Height * 0.5) / size.Height;
            return centerYRatio <= YoloFinalMaskUpperWeakCenterYRatio &&
                areaRatio <= YoloFinalMaskUpperWeakAreaRatio;
        }

        private static bool TryGetBestFinalMaskFace(
            FrameMaskProvider.FaceMaskData data,
            out (double CenterX, double CenterY, double AreaRatio, double Confidence) evidence)
        {
            evidence = default;
            if (data.Faces.Count == 0 || data.Size.Width <= 0 || data.Size.Height <= 0)
                return false;

            double frameArea = Math.Max(1.0, data.Size.Width * (double)data.Size.Height);
            int bestIndex = 0;
            double bestConfidence = double.NegativeInfinity;
            for (int i = 0; i < data.Faces.Count; i++)
            {
                double confidence = i < data.Confidences.Count
                    ? data.Confidences[i]
                    : data.MinConfidence ?? 1.0f;
                if (confidence > bestConfidence)
                {
                    bestConfidence = confidence;
                    bestIndex = i;
                }
            }

            var face = data.Faces[bestIndex];
            evidence = (
                (face.X + face.Width * 0.5) / data.Size.Width,
                (face.Y + face.Height * 0.5) / data.Size.Height,
                Math.Max(0.0, face.Width * face.Height) / frameArea,
                bestConfidence);
            return true;
        }

        private static double GetFinalMaskAreaChange(
            (double CenterX, double CenterY, double AreaRatio, double Confidence) previous,
            (double CenterX, double CenterY, double AreaRatio, double Confidence) next)
        {
            double a = Math.Max(0.000001, previous.AreaRatio);
            double b = Math.Max(0.000001, next.AreaRatio);
            return Math.Max(a / b, b / a);
        }

        private static double GetFinalMaskCenterShift(
            (double CenterX, double CenterY, double AreaRatio, double Confidence) previous,
            (double CenterX, double CenterY, double AreaRatio, double Confidence) next)
        {
            double dx = next.CenterX - previous.CenterX;
            double dy = next.CenterY - previous.CenterY;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static string FormatFrameRange(int start, int end)
        {
            return start == end
                ? start.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{start}-{end}");
        }

        private void RefreshAutoPreviewAfterPostProcess(bool exportAfter)
        {
            if (exportAfter || FrameList.SelectedFrameIndex < 0)
                return;

            FramePreview.OnFrameIndexChanged(FrameList.SelectedFrameIndex);
        }

        private void RefineAutoFacesWithRoi(
            string videoPath,
            IBgraFaceDetector detector,
            FaceTrackPostProcessResult trackPost,
            FaceOnnxDetectorOptions detectorOptions)
        {
            var candidates = trackPost.FilledGapFacesInfo
                .Concat(trackPost.FilledLostFacesInfo)
                .Concat(trackPost.FilledInitialFacesInfo)
                .ToArray();
            if (candidates.Length == 0)
                return;

            using var roiDetector = _detectorFactoryOptions.Backend == FaceDetectorBackend.FaceOnnx
                ? new FaceOnnxDetector(CreateRoiRefinerDetectorOptions(detectorOptions))
                : null;
            var refineDetector = roiDetector ?? detector;
            var refine = new FaceTrackRoiRefiner().Apply(
                _maskProvider,
                videoPath,
                refineDetector,
                candidates,
                _autoOptions.DownscaleQuality);

            if (refine.Attempts > 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[FaceTrackRoiRefine] attempts={refine.Attempts} hits={refine.Hits} seeks={refine.SeekCount} decoded={refine.DecodedFrames} elapsedMs={refine.ElapsedMs}");
            }
        }

        private static FaceTrackPostProcessOptions BuildTrackPostProcessOptions(FaceFilterProfile profile)
        {
            if (profile == FaceFilterProfile.Yolo)
            {
                return new FaceTrackPostProcessOptions
                {
                    MaxTrackGap = SuspiciousNoFaceMaxGap,
                    MaxFillGap = Math.Min(5, SuspiciousNoFaceMaxGap),
                    MaxLostFillFrames = 2,
                    MaxInitialFillFrames = 3,
                    MaxConfirmedTrackHoldFrames = SuspiciousNoFaceMaxGap,
                    AllowSmallTrackLostFill = true,
                    WeakConfidence = 0.38f,
                    StrongConfidence = 0.58f,
                    DropShortTrackMaxDetections = 2,
                    DropShortSmallTrackMaxDetections = 3,
                    ShortTrackMaxConfidence = 0.48f,
                    DropSparseTrackMaxDetections = 3,
                    DropSparseTrackMinSpanFrames = 8,
                    DropSparseTrackMaxDensity = 0.42,
                    SparseTrackMaxConfidence = 0.56f,
                    EdgeTailMaxConfidence = 0.50f,
                    EdgeTailMinStableDetections = 3,
                    EdgeLostFillMaxConfidence = 0.60f,
                    SmallTrackMaxAreaRatio = 0.00070,
                    MinTrackIou = 0.08,
                    MaxCenterShiftRatio = 0.72,
                    MaxAreaChangeRatio = 4.0,
                    DuplicateIou = TemporalDuplicateIouMin,
                    UnstableTailMaxConfidence = 0.40f,
                    UnstableTailMinStableDetections = 3,
                    UnstableTailMinIou = 0.45,
                    UnstableTailMaxAreaChangeRatio = 1.8,
                    LowerFrameTrackMaxConfidence = 0.50f,
                    LowerFrameTrackMinCenterYRatio = 0.58,
                    LowerFrameTrackMinAreaRatio = 0.015,
                    LowerFrameTrackMaxAreaRatio = 0.045
                };
            }

            if (profile == FaceFilterProfile.Scrfd)
            {
                return new FaceTrackPostProcessOptions
                {
                    MaxTrackGap = SuspiciousNoFaceMaxGap,
                    MaxFillGap = Math.Min(5, SuspiciousNoFaceMaxGap),
                    WeakConfidence = 0.35f,
                    StrongConfidence = 0.55f,
                    ShortTrackMaxConfidence = 0.55f,
                    SmallTrackMaxAreaRatio = 0.00075,
                    MinTrackIou = 0.08,
                    MaxCenterShiftRatio = 0.75,
                    MaxAreaChangeRatio = 4.0,
                    DuplicateIou = TemporalDuplicateIouMin
                };
            }

            return new FaceTrackPostProcessOptions
            {
                MaxTrackGap = SuspiciousNoFaceMaxGap,
                MaxFillGap = Math.Min(5, SuspiciousNoFaceMaxGap),
                WeakConfidence = TemporalConfidenceWeak,
                StrongConfidence = TemporalConfidenceStrong,
                ShortTrackMaxConfidence = TemporalConfidenceStrong,
                SmallTrackMaxAreaRatio = 0.00075,
                MinTrackIou = TemporalHoleFillIouMin,
                MaxCenterShiftRatio = TemporalMaxCenterShiftRatio,
                MaxAreaChangeRatio = TemporalMaxAreaChangeRatio,
                DuplicateIou = TemporalDuplicateIouMin
            };
        }

        private static FaceOnnxDetectorOptions CreateRoiRefinerDetectorOptions(FaceOnnxDetectorOptions source)
        {
            var defaults = FaceOnnxDetector.GetDefaultThresholds();
            float detection = source.DetectionThreshold ?? defaults.Detection;
            float confidence = source.ConfidenceThreshold ?? defaults.Confidence;
            float nms = source.NmsThreshold ?? defaults.Nms;

            return new FaceOnnxDetectorOptions
            {
                UseOrtOptimization = true,
                UseGpu = false,
                IntraOpNumThreads = source.IntraOpNumThreads,
                InterOpNumThreads = source.InterOpNumThreads,
                UseParallelExecution = false,
                EnablePreprocessParallelism = true,
                AllowAutoTune = false,
                AllowAutoGpu = false,
                DetectionThreshold = Math.Min(detection, 0.12f),
                ConfidenceThreshold = Math.Min(confidence, 0.12f),
                NmsThreshold = Math.Max(nms, 0.75f)
            };
        }

        private static string FormatFrameList(IReadOnlyList<int> frames)
        {
            if (frames.Count == 0)
                return "none";

            const int maxFrames = 16;
            var selected = frames
                .Take(maxFrames)
                .Select(static x => x.ToString(System.Globalization.CultureInfo.InvariantCulture));
            string text = string.Join(",", selected);
            return frames.Count > maxFrames
                ? $"{text},+{frames.Count - maxFrames}"
                : text;
        }

        private static string FormatTextList(IReadOnlyList<string> values)
        {
            if (values.Count == 0)
                return "none";

            const int maxValues = 16;
            string text = string.Join(",", values.Take(maxValues));
            return values.Count > maxValues
                ? $"{text},+{values.Count - maxValues}"
                : text;
        }

        private static IReadOnlyList<Rect>? FindNearestTemporalFaces(
            IReadOnlyList<Rect>?[] facesByFrame,
            int frameIndex,
            int direction,
            int maxDistanceFrames,
            IReadOnlySet<int>? blockedCutStarts)
        {
            if (maxDistanceFrames <= 0)
                return null;

            int index = frameIndex + direction;
            int searched = 0;
            int previousIndex = frameIndex;
            while (index >= 0 && index < facesByFrame.Length)
            {
                if (IsBlockedTemporalSmoothingStep(previousIndex, index, blockedCutStarts))
                    break;

                searched++;
                if (searched > maxDistanceFrames)
                    break;

                var faces = facesByFrame[index];
                if (faces != null && faces.Count > 0)
                    return faces;

                previousIndex = index;
                index += direction;
            }

            return null;
        }

        private static IReadOnlySet<int> BuildTemporalSmoothingCutStarts(IReadOnlyList<string>? cutPairs)
        {
            if (cutPairs == null || cutPairs.Count == 0)
                return new HashSet<int>();

            var blocked = new HashSet<int>();
            foreach (string pair in cutPairs)
            {
                if (string.IsNullOrWhiteSpace(pair))
                    continue;

                var parts = pair.Split("->", StringSplitOptions.TrimEntries);
                if (parts.Length != 2)
                    continue;

                if (!int.TryParse(parts[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int a) ||
                    !int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int b))
                {
                    continue;
                }

                int start = Math.Min(a, b);
                int end = Math.Max(a, b);
                for (int frame = start; frame < end; frame++)
                    blocked.Add(frame);
            }

            return blocked;
        }

        private static bool IsBlockedTemporalSmoothingStep(int fromFrame, int toFrame, IReadOnlySet<int>? blockedCutStarts)
        {
            if (blockedCutStarts == null || blockedCutStarts.Count == 0)
                return false;

            int cutStart = Math.Min(fromFrame, toFrame);
            return blockedCutStarts.Contains(cutStart);
        }

        private static bool IsReasonableTemporalMatch(Rect current, Rect match)
        {
            if (match.Width <= 0 || match.Height <= 0)
                return false;

            double area = Math.Max(1.0, current.Width * current.Height);
            double matchArea = Math.Max(1.0, match.Width * match.Height);
            double ratio = area / matchArea;
            if (ratio > TemporalMaxAreaChangeRatio || ratio < 1.0 / TemporalMaxAreaChangeRatio)
                return false;

            double cx = current.X + current.Width * 0.5;
            double cy = current.Y + current.Height * 0.5;
            double mx = match.X + match.Width * 0.5;
            double my = match.Y + match.Height * 0.5;
            double shift = Math.Sqrt((cx - mx) * (cx - mx) + (cy - my) * (cy - my));
            double maxDim = Math.Max(1.0, Math.Max(match.Width, match.Height));
            return shift / maxDim <= TemporalMaxCenterShiftRatio;
        }

        private static Rect BlendRect(Rect current, Rect target, double weight)
        {
            double keep = 1.0 - weight;
            return new Rect(
                current.X * keep + target.X * weight,
                current.Y * keep + target.Y * weight,
                Math.Max(0.0, current.Width * keep + target.Width * weight),
                Math.Max(0.0, current.Height * keep + target.Height * weight));
        }

        private static double GetMaxIoU(Rect rect, IReadOnlyList<Rect>? others, out Rect match)
        {
            match = default;
            if (others == null || others.Count == 0)
                return 0.0;

            double best = 0.0;
            for (int i = 0; i < others.Count; i++)
            {
                var other = others[i];
                double iou = IoU(rect, other);
                if (iou > best)
                {
                    best = iou;
                    match = other;
                }
            }

            return best;
        }

        private static double IoU(Rect a, Rect b)
        {
            double ax1 = a.X;
            double ay1 = a.Y;
            double ax2 = a.X + a.Width;
            double ay2 = a.Y + a.Height;

            double bx1 = b.X;
            double by1 = b.Y;
            double bx2 = b.X + b.Width;
            double by2 = b.Y + b.Height;

            double ix1 = Math.Max(ax1, bx1);
            double iy1 = Math.Max(ay1, by1);
            double ix2 = Math.Min(ax2, bx2);
            double iy2 = Math.Min(ay2, by2);

            double iw = Math.Max(0.0, ix2 - ix1);
            double ih = Math.Max(0.0, iy2 - iy1);
            double inter = iw * ih;
            if (inter <= 0.0)
                return 0.0;

            double union = a.Width * a.Height + b.Width * b.Height - inter;
            if (union <= 0.0)
                return 0.0;
            return inter / union;
        }

        private static List<(Rect Prev, Rect Next, float Confidence)> MatchFacesForInterpolation(
            IReadOnlyList<Rect>? prevFaces,
            IReadOnlyList<float>? prevConfs,
            IReadOnlyList<Rect>? nextFaces,
            IReadOnlyList<float>? nextConfs)
        {
            var matches = new List<(Rect Prev, Rect Next, float Confidence)>();
            if (prevFaces == null || nextFaces == null || prevFaces.Count == 0 || nextFaces.Count == 0)
                return matches;

            var nextUsed = new bool[nextFaces.Count];
            for (int i = 0; i < prevFaces.Count; i++)
            {
                double bestIou = 0.0;
                int bestIndex = -1;
                for (int j = 0; j < nextFaces.Count; j++)
                {
                    if (nextUsed[j])
                        continue;

                    double iou = IoU(prevFaces[i], nextFaces[j]);
                    if (iou < TemporalHoleFillIouMin || iou <= bestIou)
                        continue;

                    bestIou = iou;
                    bestIndex = j;
                }

                if (bestIndex < 0)
                    continue;

                nextUsed[bestIndex] = true;
                float conf = Math.Max(GetConfidenceAt(prevConfs, i), GetConfidenceAt(nextConfs, bestIndex));
                matches.Add((prevFaces[i], nextFaces[bestIndex], conf));
            }

            return matches;
        }

        private static Rect InterpolateRect(Rect prev, Rect next)
        {
            double x = (prev.X + next.X) * 0.5;
            double y = (prev.Y + next.Y) * 0.5;
            double width = (prev.Width + next.Width) * 0.5;
            double height = (prev.Height + next.Height) * 0.5;
            return new Rect(x, y, Math.Max(0.0, width), Math.Max(0.0, height));
        }

        private static bool HasSimilarFace(Rect candidate, IReadOnlyList<Rect>? faces, double minIou)
        {
            if (faces == null || faces.Count == 0)
                return false;

            for (int i = 0; i < faces.Count; i++)
            {
                if (IoU(candidate, faces[i]) >= minIou)
                    return true;
            }

            return false;
        }

        private static float GetConfidenceAt(IReadOnlyList<float>? confs, int index)
        {
            if (confs == null || index < 0 || index >= confs.Count)
                return TemporalConfidenceStrong;
            return confs[index];
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

                AddSuspiciousNoFaceGaps(hasFace, noFace);

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
            float baseThreshold = _detectorOptions.ConfidenceThreshold ?? defaults.Confidence;
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
                _autoCompleted);
        }

        private void ApplySnapshot(WorkspaceSnapshot snapshot)
        {
            if (snapshot == null)
                return;

            _autoResumeIndex = snapshot.AutoResumeIndex;
            _autoCompleted = snapshot.AutoCompleted;

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
    }
}
