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
        private const int SuspiciousNoFaceMaxGap = 8;
        private int _autoResumeIndex;
        private bool _autoCompleted;
        private string? _autoRunSignature;
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
            _autoResumeIndex > 0 &&
            IsAutoResumeSignatureCurrent(BuildAutoRunSignature(_autoOptions, _detectorFactoryOptions));

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
            bool detectionCompleted = false;
            string runId = $"auto-{Guid.NewGuid():N}";
            try
            {
                var detectorOptions = _detectorOptions;
                var detectorFactoryOptions = _detectorFactoryOptions;
                string runSignature = BuildAutoRunSignature(_autoOptions, detectorFactoryOptions);
                ResetStaleAutoResumeIfSettingsChanged(runSignature);
                _autoRunSignature = runSignature;

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

                var postProcess = new AutoMaskPostProcessPipeline(
                    _maskProvider,
                    _autoOptions,
                    FrameList.TotalFrames);
                postProcess.Apply(
                    FrameList.VideoPath,
                    token,
                    detector as IBgraFaceDetector,
                    detectorOptions,
                    _detectorFactoryOptions.Backend == FaceDetectorBackend.FaceOnnx);
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
                if (!persisted)
                    PersistWorkspaceState(includePreviewMask: !exportAfter);
            }
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
                _autoCompleted,
                _autoRunSignature);
        }

        private void ApplySnapshot(WorkspaceSnapshot snapshot)
        {
            if (snapshot == null)
                return;

            _autoResumeIndex = snapshot.AutoResumeIndex;
            _autoCompleted = snapshot.AutoCompleted;
            _autoRunSignature = snapshot.AutoRunSignature;

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

        private void ResetStaleAutoResumeIfSettingsChanged(string currentSignature)
        {
            if (_autoResumeIndex <= 0 || IsAutoResumeSignatureCurrent(currentSignature))
                return;

            System.Diagnostics.Debug.WriteLine(
                $"[AutoMaskResumeReset] reason=settings-changed resumeIndex={_autoResumeIndex}");
            _autoResumeIndex = 0;
            _autoCompleted = false;
        }

        private static string BuildAutoRunSignature(
            AutoMaskOptions autoOptions,
            FaceDetectorFactoryOptions detectorFactoryOptions)
        {
            autoOptions ??= new AutoMaskOptions();
            detectorFactoryOptions ??= FaceDetectorFactoryOptions.ForOnnx(new FaceOnnxDetectorOptions());

            var parts = new List<string>
            {
                "v2",
                $"backend={detectorFactoryOptions.Backend}",
                $"profile={autoOptions.FilterProfile}",
                $"downscale={autoOptions.DownscaleRatio:0.###}",
                $"quality={autoOptions.DownscaleQuality}",
                $"tracking={autoOptions.UseTracking}",
                $"everyN={autoOptions.DetectEveryNFrames}",
                $"parallel={autoOptions.ParallelDetectorCount}",
                $"dump={autoOptions.DumpDetectionDiagnostics}"
            };

            switch (detectorFactoryOptions.Backend)
            {
                case FaceDetectorBackend.YoloFaceOnnx:
                    AppendYoloSignature(parts, detectorFactoryOptions.YoloFaceOnnxOptions);
                    break;
                case FaceDetectorBackend.FaceOnnx:
                    AppendFaceOnnxSignature(parts, detectorFactoryOptions.FaceOnnxOptions);
                    break;
                default:
                    parts.Add($"detector={detectorFactoryOptions.Backend}");
                    break;
            }

            return string.Join("|", parts);
        }

        private static void AppendFaceOnnxSignature(List<string> parts, FaceOnnxDetectorOptions? options)
        {
            options ??= new FaceOnnxDetectorOptions();
            parts.Add($"ort={options.UseOrtOptimization}");
            parts.Add($"gpu={options.UseGpu}");
            parts.Add($"intra={options.IntraOpNumThreads?.ToString() ?? "null"}");
            parts.Add($"inter={options.InterOpNumThreads?.ToString() ?? "null"}");
            parts.Add($"parallelExec={options.UseParallelExecution?.ToString() ?? "null"}");
            parts.Add($"detect={options.DetectionThreshold?.ToString("0.###") ?? "null"}");
            parts.Add($"conf={options.ConfidenceThreshold?.ToString("0.###") ?? "null"}");
            parts.Add($"nms={options.NmsThreshold?.ToString("0.###") ?? "null"}");
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
            parts.Add($"input={options.InputWidth?.ToString() ?? "null"}x{options.InputHeight?.ToString() ?? "null"}");
            parts.Add($"obj={options.ObjectnessThreshold:0.###}");
            parts.Add($"conf={options.ConfidenceThreshold:0.###}");
            parts.Add($"nms={options.NmsThreshold:0.###}");
            parts.Add($"max={options.MaxDetections}");
            parts.Add($"tiling={options.UseTiling}");
            parts.Add($"tileOnly={!options.IncludeFullFrameWhenTiling}");
            parts.Add($"tiles={options.TileColumns}x{options.TileRows}");
            parts.Add($"tileOverlap={options.TileOverlapRatio:0.###}");
            parts.Add($"letterbox={options.UseLetterboxResize}");
            parts.Add($"centerPad={options.CenterLetterboxPadding}");
            parts.Add($"rgb={options.UseRgbInput}");
            parts.Add($"lowPos={options.UseLowConfidencePositionFilter}:{options.LowConfidencePositionMaxConfidence:0.###}:{options.LowConfidencePositionMinCenterYRatio:0.###}");
            parts.Add($"small={options.UseSmallAreaFilter}:{options.SmallAreaMaxAreaRatio:0.####}");
            parts.Add($"aspect={options.UseAspectRatioFilter}:{options.MinAspectRatio:0.###}:{options.MaxAspectRatio:0.###}");
            parts.Add($"topSmall={options.UseTopSmallLowConfidenceFilter}:{options.TopSmallLowConfidenceMaxConfidence:0.###}:{options.TopSmallLowConfidenceMaxCenterYRatio:0.###}:{options.TopSmallLowConfidenceMaxAreaRatio:0.####}");
            parts.Add($"largeScale={options.LargeBoxWidthScale:0.###}:{options.LargeBoxHeightScale:0.###}:{options.LargeBoxMinAreaRatio:0.####}");
            parts.Add($"landmark={options.UseYolo5LandmarkBoxRefine}:{options.Yolo5LandmarkBoxMinAreaRatio:0.####}:{options.Yolo5LandmarkBoxWidthScale:0.###}:{options.Yolo5LandmarkBoxHeightScale:0.###}:{options.Yolo5LandmarkBoxCenterYOffsetRatio:0.###}:{options.Yolo5LandmarkBoxMinOriginalIou:0.###}");
        }

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

    }
}
