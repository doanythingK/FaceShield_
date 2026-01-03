using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FaceShield.Enums.Workspace; // 🔹 추가
using FaceShield.Models;
using FaceShield.Services.Analysis;
using FaceShield.Services.FaceDetection;
using FaceShield.Services.Video;
using FaceShield.Services.Workspace;
using FaceShield.Views.Dialogs;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace FaceShield.ViewModels.Pages
{
    public partial class HomePageViewModel : ViewModelBase
    {
        private const int MaxRecents = 5;
        private readonly Action<WorkspaceViewModel> _onStartWorkspace;
        private readonly Action _onBackHome;
        private readonly WorkspaceStateStore _stateStore;
        private readonly Dictionary<string, WorkspaceViewModel> _workspaceCache = new(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource? _autoCts;
        private DateTime _autoStartTimeUtc;
        private DateTime _autoLastProgressAtUtc;
        private WorkspaceViewModel? _activeAutoWorkspace;
        private DispatcherTimer? _autoStatusTimer;
        private bool _autoRestartRequested;
        private readonly Queue<(DateTime Timestamp, int FrameIndex)> _etaFrameSamples = new();
        private (DateTime Timestamp, int FrameIndex) _etaLastFrameSample;
        private readonly Queue<(DateTime Timestamp, int Progress)> _workspaceEtaSamples = new();
        private (DateTime Timestamp, int Progress) _workspaceLastSample;
        private readonly Queue<(DateTime Timestamp, int Progress)> _exportEtaSamples = new();
        private (DateTime Timestamp, int Progress) _exportLastSample;

        [ObservableProperty]
        private string? selectedVideoPath;

        [ObservableProperty]
        private RecentItem? selectedRecent;

        [ObservableProperty]
        private int autoProgress;

        [ObservableProperty]
        private bool isAutoRunning;

        [ObservableProperty]
        private bool isWorkspaceLoading;

        [ObservableProperty]
        private string? workspaceLoadingMessage;

        [ObservableProperty]
        private int workspaceLoadingProgress;

        [ObservableProperty]
        private bool isWorkspaceLoadingIndeterminate;

        [ObservableProperty]
        private string? autoEtaText;

        [ObservableProperty]
        private string? workspaceEtaText;

        [ObservableProperty]
        private string? autoStatusText;

        [ObservableProperty]
        private string? autoAccelStatus;

        [ObservableProperty]
        private bool isExportRunning;

        [ObservableProperty]
        private int exportProgress;

        [ObservableProperty]
        private string? exportEtaText;

        public sealed class DownscaleOption
        {
            public string Label { get; }
            public double Ratio { get; }

            public DownscaleOption(string label, double ratio)
            {
                Label = label;
                Ratio = ratio;
            }
        }

        public IReadOnlyList<DownscaleOption> DownscaleOptions { get; } = new[]
        {
            new DownscaleOption("100% (원본)", 1.0),
            new DownscaleOption("75%", 0.75),
            new DownscaleOption("50%", 0.5),
            new DownscaleOption("33%", 0.33)
        };

        [ObservableProperty]
        private DownscaleOption? selectedDownscaleOption;

        public sealed class DownscaleQualityOption
        {
            public string Label { get; }
            public DownscaleQuality Quality { get; }

            public DownscaleQualityOption(string label, DownscaleQuality quality)
            {
                Label = label;
                Quality = quality;
            }
        }

        public IReadOnlyList<DownscaleQualityOption> DownscaleQualityOptions { get; } = new[]
        {
            new DownscaleQualityOption("빠름(최근접)", DownscaleQuality.FastNearest),
            new DownscaleQualityOption("균형(보간)", DownscaleQuality.BalancedBilinear)
        };

        [ObservableProperty]
        private DownscaleQualityOption? selectedDownscaleQualityOption;

        public sealed class OrtThreadOption
        {
            public string Label { get; }
            public int? Threads { get; }

            public OrtThreadOption(string label, int? threads)
            {
                Label = label;
                Threads = threads;
            }
        }

        public IReadOnlyList<OrtThreadOption> OrtThreadOptions { get; }
        [ObservableProperty]
        private OrtThreadOption? selectedOrtThreadOption;

        public IReadOnlyList<int> DetectEveryOptions { get; } = new[] { 1, 2, 3, 5 };

        [ObservableProperty]
        private int autoDetectEveryNFrames = 1;

        public IReadOnlyList<int> ParallelSessionOptions { get; } = new[] { 1, 2, 3, 4 };

        [ObservableProperty]
        private int selectedParallelSessionCount = 2;

        [ObservableProperty]
        private bool autoTrackingEnabled;

        [ObservableProperty]
        private bool autoUseOrtOptimization = true;

        [ObservableProperty]
        private bool autoUseGpu = false;

        [ObservableProperty]
        private bool autoExportAfter = true;

        public ObservableCollection<RecentItem> Recents { get; } = new();

        public HomePageViewModel(
            Action<WorkspaceViewModel> onStartWorkspace,
            Action onBackHome,
            WorkspaceStateStore stateStore)
        {
            _onStartWorkspace = onStartWorkspace;
            _onBackHome = onBackHome;
            _stateStore = stateStore;
            selectedDownscaleOption = DownscaleOptions[0];
            selectedDownscaleQualityOption = DownscaleQualityOptions[1];

            OrtThreadOptions = BuildOrtThreadOptions();
            selectedOrtThreadOption = OrtThreadOptions[0];
            ApplySavedAutoSettings();
            foreach (var recent in _stateStore.GetRecents())
                Recents.Add(recent);

            TrimRecents();
        }

        public bool CanOpenWorkspace => !string.IsNullOrWhiteSpace(SelectedVideoPath);
        public bool CanStartWorkspace => CanOpenWorkspace && !IsAutoRunning && !IsWorkspaceLoading;
        public bool IsBusy => IsWorkspaceLoading || IsAutoRunning;
        public int BusyProgress => IsAutoRunning ? AutoProgress : WorkspaceLoadingProgress;
        public bool IsBusyIndeterminate => IsAutoRunning ? false : IsWorkspaceLoadingIndeterminate;
        public string BusyMessage =>
            IsExportRunning
                ? "파일 저장 중..."
                : IsAutoRunning
                    ? "자동 모자이크 진행 중..."
                    : (WorkspaceLoadingMessage ?? "로딩 중...");
        public bool IsTrackingOptionsEnabled => AutoTrackingEnabled;

        partial void OnSelectedVideoPathChanged(string? value)
        {
            OnPropertyChanged(nameof(CanOpenWorkspace));
            OnPropertyChanged(nameof(CanStartWorkspace));
        }

        partial void OnSelectedRecentChanged(RecentItem? value)
        {
            if (value is not null)
            {
                SelectedVideoPath = value.Path;
            }
        }

        partial void OnIsAutoRunningChanged(bool value)
        {
            OnPropertyChanged(nameof(CanStartWorkspace));
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(BusyProgress));
            OnPropertyChanged(nameof(BusyMessage));
            OnPropertyChanged(nameof(IsBusyIndeterminate));

            if (!value)
            {
                AutoEtaText = null;
                _etaFrameSamples.Clear();
            }
        }

        partial void OnIsExportRunningChanged(bool value)
        {
            OnPropertyChanged(nameof(BusyMessage));
            if (!value)
            {
                ExportProgress = 0;
                ExportEtaText = null;
                _exportEtaSamples.Clear();
            }
        }

        partial void OnIsWorkspaceLoadingChanged(bool value)
        {
            OnPropertyChanged(nameof(CanStartWorkspace));
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(BusyProgress));
            OnPropertyChanged(nameof(BusyMessage));
            OnPropertyChanged(nameof(IsBusyIndeterminate));

            if (!value)
            {
                WorkspaceEtaText = null;
                _workspaceEtaSamples.Clear();
            }
        }

        partial void OnAutoProgressChanged(int value)
        {
            OnPropertyChanged(nameof(BusyProgress));

            if (!IsAutoRunning || value <= 0)
                return;
        }

        partial void OnWorkspaceLoadingProgressChanged(int value)
        {
            OnPropertyChanged(nameof(BusyProgress));

            if (!IsWorkspaceLoading || IsWorkspaceLoadingIndeterminate || value <= 0)
                return;

            UpdateWorkspaceEta(DateTime.UtcNow, value);
        }

        partial void OnWorkspaceLoadingMessageChanged(string? value)
        {
            OnPropertyChanged(nameof(BusyMessage));
        }

        partial void OnIsWorkspaceLoadingIndeterminateChanged(bool value)
        {
            OnPropertyChanged(nameof(IsBusyIndeterminate));

            if (value)
            {
                WorkspaceEtaText = null;
                _workspaceEtaSamples.Clear();
            }
        }

        partial void OnAutoUseGpuChanged(bool value)
        {
            PersistAutoSettings();
            if (!IsAutoRunning || _activeAutoWorkspace == null)
                return;

            _activeAutoWorkspace.UpdateDetectorOptions(BuildDetectorOptions());
            _autoRestartRequested = true;
            AutoStatusText = "GPU 옵션 변경 감지 · 재시작 준비 중...";
            _autoCts?.Cancel();
        }

        partial void OnAutoUseOrtOptimizationChanged(bool value)
        {
            PersistAutoSettings();
            if (!IsAutoRunning || _activeAutoWorkspace == null)
                return;

            _activeAutoWorkspace.UpdateDetectorOptions(BuildDetectorOptions());
            _autoRestartRequested = true;
            AutoStatusText = "가속 옵션 변경 감지 · 재시작 준비 중...";
            _autoCts?.Cancel();
        }

        partial void OnAutoTrackingEnabledChanged(bool value)
        {
            PersistAutoSettings();
            OnPropertyChanged(nameof(IsTrackingOptionsEnabled));
            RequestAutoRestartForOptions("자동 옵션 변경 감지 · 재시작 준비 중...");
        }

        partial void OnAutoDetectEveryNFramesChanged(int value)
        {
            PersistAutoSettings();
            RequestAutoRestartForOptions("자동 옵션 변경 감지 · 재시작 준비 중...");
        }

        partial void OnSelectedParallelSessionCountChanged(int value)
        {
            PersistAutoSettings();
            RequestAutoRestartForOptions("자동 옵션 변경 감지 · 재시작 준비 중...");
        }

        partial void OnSelectedDownscaleOptionChanged(DownscaleOption value)
        {
            PersistAutoSettings();
            RequestAutoRestartForOptions("자동 옵션 변경 감지 · 재시작 준비 중...");
        }

        partial void OnSelectedDownscaleQualityOptionChanged(DownscaleQualityOption value)
        {
            PersistAutoSettings();
            RequestAutoRestartForOptions("자동 옵션 변경 감지 · 재시작 준비 중...");
        }

        partial void OnSelectedOrtThreadOptionChanged(OrtThreadOption value)
        {
            PersistAutoSettings();
            if (!IsAutoRunning || _activeAutoWorkspace == null)
                return;

            _activeAutoWorkspace.UpdateDetectorOptions(BuildDetectorOptions());
            _autoRestartRequested = true;
            AutoStatusText = "가속 옵션 변경 감지 · 재시작 준비 중...";
            _autoCts?.Cancel();
        }

        partial void OnAutoExportAfterChanged(bool value)
        {
            PersistAutoSettings();
        }

        private void RequestAutoRestartForOptions(string statusText)
        {
            if (!IsAutoRunning || _activeAutoWorkspace == null)
                return;

            _activeAutoWorkspace.UpdateAutoOptions(BuildAutoOptions());
            _autoRestartRequested = true;
            AutoStatusText = statusText;
            _autoCts?.Cancel();
        }

        private void ApplySavedAutoSettings()
        {
            var saved = _stateStore.GetAutoSettings();
            if (saved == null)
                return;

            var downscale = DownscaleOptions.FirstOrDefault(o => Math.Abs(o.Ratio - saved.DownscaleRatio) < 0.0001);
            if (downscale != null)
                selectedDownscaleOption = downscale;

            var quality = DownscaleQualityOptions.FirstOrDefault(o => (int)o.Quality == saved.DownscaleQuality);
            if (quality != null)
                selectedDownscaleQualityOption = quality;

            var ort = OrtThreadOptions.FirstOrDefault(o => o.Threads == saved.OrtThreads);
            if (ort != null)
                selectedOrtThreadOption = ort;

            AutoTrackingEnabled = saved.AutoTrackingEnabled;
            AutoDetectEveryNFrames = Math.Max(1, saved.AutoDetectEveryNFrames);
            SelectedParallelSessionCount = Math.Max(1, saved.ParallelSessionCount);
            AutoUseOrtOptimization = saved.AutoUseOrtOptimization;
            AutoUseGpu = saved.AutoUseGpu;
            AutoExportAfter = saved.AutoExportAfter;
        }

        private void PersistAutoSettings()
        {
            _stateStore.SaveAutoSettings(new AutoSettingsState
            {
                DownscaleRatio = SelectedDownscaleOption?.Ratio ?? 1.0,
                DownscaleQuality = (int)(SelectedDownscaleQualityOption?.Quality ?? DownscaleQuality.BalancedBilinear),
                AutoTrackingEnabled = AutoTrackingEnabled,
                AutoDetectEveryNFrames = Math.Max(1, AutoDetectEveryNFrames),
                ParallelSessionCount = Math.Max(1, SelectedParallelSessionCount),
                AutoUseOrtOptimization = AutoUseOrtOptimization,
                AutoUseGpu = AutoUseGpu,
                OrtThreads = SelectedOrtThreadOption?.Threads,
                AutoExportAfter = AutoExportAfter
            });
        }

        public async Task PickVideoAsync(IStorageProvider storageProvider)
        {
            var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "영상 파일 선택",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Video")
                    {
                        Patterns = ["*.mp4", "*.mov", "*.mkv", "*.avi", "*.wmv", "*.webm"]
                    }
                ]
            });

            var file = files.Count > 0 ? files[0] : null;
            if (file is null)
                return;

            var localPath = file.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(localPath))
                return;

            SelectedVideoPath = localPath;
            TouchRecent(localPath);
        }

        // 기존과 호환: "워크스페이스 열기"는 Manual로 동작
        [RelayCommand]
        private async Task OpenWorkspace()
        {
            await OpenManualWorkspace();
        }

        [RelayCommand]
        private async Task OpenManualWorkspace()
        {
            if (!CanStartWorkspace)
                return;

            IsWorkspaceLoading = true;
            WorkspaceLoadingMessage = "워크스페이스 로딩 중...";
            WorkspaceLoadingProgress = 0;
            IsWorkspaceLoadingIndeterminate = false;

            WorkspaceViewModel vm;
            try
            {
                var progress = new Progress<int>(p =>
                    Dispatcher.UIThread.Post(() => WorkspaceLoadingProgress = p));

                var autoOptions = BuildAutoOptions();
                var detectorOptions = BuildDetectorOptions();
                TouchRecent(SelectedVideoPath);

                vm = await Task.Run(
                    () => GetOrCreateWorkspace(
                        WorkspaceMode.Manual,
                        progress,
                        autoOptions,
                        detectorOptions));
            }
            finally
            {
                IsWorkspaceLoading = false;
            }

            _onStartWorkspace(vm);
        }

        [RelayCommand]
        private async Task OpenAutoWorkspace()
        {
            if (!CanStartWorkspace)
                return;

            try
            {
                FaceOnnxDetector.EnsureRuntimeAvailable();
            }
            catch (Exception ex)
            {
                await ShowAutoErrorAsync(ex, isDuringRun: false);
                return;
            }

            IsWorkspaceLoading = true;
            WorkspaceLoadingMessage = "워크스페이스 준비 중...";
            WorkspaceLoadingProgress = 0;
            IsWorkspaceLoadingIndeterminate = false;

            WorkspaceViewModel vm;
            try
            {
                var autoOptions = BuildAutoOptions();
                var detectorOptions = BuildDetectorOptions();
                TouchRecent(SelectedVideoPath);

                vm = await Task.Run(
                    () => GetOrCreateWorkspace(
                        WorkspaceMode.Auto,
                        loadProgress: null,
                        autoOptions,
                        detectorOptions));
            }
            finally
            {
                IsWorkspaceLoading = false;
            }

            if (vm.NeedsAutoResumePrompt)
            {
                bool resume = await ShowResumeAutoDialogAsync();
                if (!resume)
                {
                    await EnsureWorkspaceReadyAsync(vm);
                    _onStartWorkspace(vm);
                    return;
                }
            }

            if (IsAutoRunning)
                return;

            IsAutoRunning = true;
            AutoProgress = 0;
            _activeAutoWorkspace = vm;
            AutoStatusText = "진행 상태 확인 중...";
            AutoAccelStatus = "가속 상태: 확인 중...";
            StartAutoStatusTimer();
            AutoEtaText = "예상 남은 시간 계산 중...";

            bool completed = false;
            try
            {
                do
                {
                    _autoRestartRequested = false;
                    _autoCts?.Dispose();
                    _autoCts = new CancellationTokenSource();
                    _autoStartTimeUtc = DateTime.UtcNow;
                    _autoLastProgressAtUtc = _autoStartTimeUtc;

                    var progress = new Progress<int>(p =>
                        Dispatcher.UIThread.Post(() =>
                        {
                            AutoProgress = p;
                            _autoLastProgressAtUtc = DateTime.UtcNow;
                        }));

                    var exportProgress = new Progress<ExportProgress>(p =>
                        Dispatcher.UIThread.Post(() =>
                        {
                            IsExportRunning = true;
                            ExportProgress = Math.Clamp(p.Percent, 0, 100);
                            UpdateExportEta(DateTime.UtcNow, ExportProgress);
                        }));

                    completed = await vm.RunAutoAsync(
                        exportAfter: AutoExportAfter,
                        progress,
                        _autoCts.Token,
                        exportProgress);
                }
                while (_autoRestartRequested);
            }
            catch (Exception ex)
            {
                await ShowAutoErrorAsync(ex, isDuringRun: true);
                return;
            }
            finally
            {
                _autoCts?.Dispose();
                _autoCts = null;
                IsAutoRunning = false;
                IsExportRunning = false;
                StopAutoStatusTimer();
                _activeAutoWorkspace = null;
            }

            if (completed)
            {
                await EnsureWorkspaceReadyAsync(vm);
                _onStartWorkspace(vm);
            }
        }

        [RelayCommand]
        private void OpenSettings() { }

        [RelayCommand]
        private void OpenAbout() { }

        [RelayCommand]
        private void CancelAuto()
        {
            _autoCts?.Cancel();
        }

        [RelayCommand]
        private void CancelExport()
        {
            _autoCts?.Cancel();
        }

        private static string FormatEta(TimeSpan remaining)
        {
            if (remaining.TotalHours >= 1)
            {
                return $"{(int)remaining.TotalHours}시간 {Math.Max(0, remaining.Minutes)}분 {Math.Max(0, remaining.Seconds)}초";
            }

            if (remaining.TotalMinutes >= 1)
                return $"{(int)remaining.TotalMinutes}분 {Math.Max(0, remaining.Seconds)}초";

            return $"{Math.Max(0, remaining.Seconds)}초";
        }

        private static string FormatAge(TimeSpan age)
        {
            if (age.TotalMinutes >= 1)
                return $"{(int)age.TotalMinutes}분 {Math.Max(0, age.Seconds)}초";

            return $"{Math.Max(0, age.Seconds)}초";
        }

        private void StartAutoStatusTimer()
        {
            if (_autoStatusTimer == null)
            {
                _autoStatusTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(1)
                };
                _autoStatusTimer.Tick += (_, _) => UpdateAutoStatusText();
            }

            _autoStatusTimer.Start();
        }

        private void StopAutoStatusTimer()
        {
            if (_autoStatusTimer == null)
                return;

            _autoStatusTimer.Stop();
            UpdateAutoStatusText(clear: true);
            _etaFrameSamples.Clear();
        }

        private void UpdateAutoStatusText(bool clear = false)
        {
            if (clear)
            {
                AutoStatusText = null;
                AutoAccelStatus = null;
                return;
            }

            var now = DateTime.UtcNow;
            var vm = _activeAutoWorkspace;
            int total = vm?.FrameList.TotalFrames ?? 0;
            int lastFrame = vm?.AutoLastProcessedFrame ?? -1;
            DateTime lastAt = vm?.AutoLastProcessedAtUtc ?? _autoLastProgressAtUtc;

            string frameInfo = (lastFrame >= 0 && total > 0)
                ? $"{lastFrame + 1}/{total}"
                : "정보 없음";

            var since = now - lastAt;
            AutoStatusText = $"마지막 처리: {frameInfo} · 업데이트 {FormatAge(since)} 전";
            var accel = FaceOnnxDetector.GetLastExecutionProviderLabel();
            var accelError = FaceOnnxDetector.GetLastExecutionProviderError();
            var decode = FfFrameExtractor.GetLastDecodeStatus();
            var decodeError = FfFrameExtractor.GetLastDecodeError();
            var decodeDiag = FfFrameExtractor.GetLastDecodeDiagnostics();
            string decodeText = decodeError == null ? decode : $"{decode} · 오류: {decodeError}";
            if (!string.IsNullOrWhiteSpace(decodeDiag))
                decodeText += $" · {decodeDiag}";
            string threadText =
                $"onnx={SelectedOrtThreadOption?.Label ?? "자동"}, cores={Environment.ProcessorCount}, sessions={SelectedParallelSessionCount}";

            AutoAccelStatus = accelError == null
                ? $"가속 상태: {accel} · {decodeText} · {threadText}"
                : $"가속 상태: {accel} · 오류: {accelError} · {decodeText} · {threadText}";

            if (lastFrame >= 0 && total > 0)
            {
                UpdateEtaFrameSamples(lastAt, lastFrame);
                var remaining = EstimateRemainingByFrames(total, lastFrame);
                if (remaining != null)
                    AutoEtaText = $"예상 남은 시간: {FormatEta(remaining.Value)}";
            }
        }

        private void UpdateEtaFrameSamples(DateTime timestamp, int frameIndex)
        {
            if (frameIndex < 0)
                return;

            if (_etaFrameSamples.Count > 0 && frameIndex <= _etaLastFrameSample.FrameIndex)
                return;

            _etaFrameSamples.Enqueue((timestamp, frameIndex));
            _etaLastFrameSample = (timestamp, frameIndex);

            while (_etaFrameSamples.Count > 0 && (timestamp - _etaFrameSamples.Peek().Timestamp).TotalSeconds > 10)
                _etaFrameSamples.Dequeue();

            if (_etaFrameSamples.Count == 0)
                _etaFrameSamples.Enqueue(_etaLastFrameSample);
        }

        private TimeSpan? EstimateRemainingByFrames(int totalFrames, int currentFrameIndex)
        {
            if (_etaFrameSamples.Count < 2)
                return null;

            var first = _etaFrameSamples.Peek();
            var last = _etaLastFrameSample;
            var elapsedSeconds = (last.Timestamp - first.Timestamp).TotalSeconds;
            var progressed = last.FrameIndex - first.FrameIndex;

            if (elapsedSeconds <= 0 || progressed <= 0)
                return null;

            double ratePerSecond = progressed / elapsedSeconds;
            int remainingFrames = Math.Max(0, (totalFrames - 1) - currentFrameIndex);
            return TimeSpan.FromSeconds(remainingFrames / ratePerSecond);
        }

        private void UpdateWorkspaceEta(DateTime timestamp, int progress)
        {
            if (progress <= 0 || progress >= 100)
            {
                WorkspaceEtaText = null;
                return;
            }

            if (_workspaceEtaSamples.Count > 0 && progress <= _workspaceLastSample.Progress)
                return;

            _workspaceEtaSamples.Enqueue((timestamp, progress));
            _workspaceLastSample = (timestamp, progress);

            while (_workspaceEtaSamples.Count > 0 &&
                   (timestamp - _workspaceEtaSamples.Peek().Timestamp).TotalSeconds > 10)
                _workspaceEtaSamples.Dequeue();

            if (_workspaceEtaSamples.Count < 2)
                return;

            var first = _workspaceEtaSamples.Peek();
            var last = _workspaceLastSample;
            var elapsedSeconds = (last.Timestamp - first.Timestamp).TotalSeconds;
            var progressed = last.Progress - first.Progress;

            if (elapsedSeconds <= 0 || progressed <= 0)
                return;

            double ratePerSecond = progressed / elapsedSeconds;
            double remaining = (100 - progress) / ratePerSecond;
            if (remaining < 0)
                return;

            WorkspaceEtaText = $"예상 남은 시간: {FormatEta(TimeSpan.FromSeconds(remaining))}";
        }

        private void UpdateExportEta(DateTime timestamp, int progress)
        {
            if (progress <= 0 || progress >= 100)
            {
                ExportEtaText = null;
                return;
            }

            if (_exportEtaSamples.Count > 0 && progress <= _exportLastSample.Progress)
                return;

            _exportEtaSamples.Enqueue((timestamp, progress));
            _exportLastSample = (timestamp, progress);

            while (_exportEtaSamples.Count > 0 &&
                   (timestamp - _exportEtaSamples.Peek().Timestamp).TotalSeconds > 10)
                _exportEtaSamples.Dequeue();

            if (_exportEtaSamples.Count < 2)
                return;

            var first = _exportEtaSamples.Peek();
            var last = _exportLastSample;
            var elapsedSeconds = (last.Timestamp - first.Timestamp).TotalSeconds;
            var progressed = last.Progress - first.Progress;

            if (elapsedSeconds <= 0 || progressed <= 0)
                return;

            double ratePerSecond = progressed / elapsedSeconds;
            double remaining = (100 - progress) / ratePerSecond;
            if (remaining < 0)
                return;

            ExportEtaText = $"예상 남은 시간: {FormatEta(TimeSpan.FromSeconds(remaining))}";
        }

        private AutoMaskOptions BuildAutoOptions()
        {
            double ratio = SelectedDownscaleOption?.Ratio ?? 1.0;
            bool useTracking = AutoTrackingEnabled;
            int detectEvery = useTracking ? Math.Max(1, AutoDetectEveryNFrames) : 1;
            var quality = SelectedDownscaleQualityOption?.Quality ?? DownscaleQuality.BalancedBilinear;

            return new AutoMaskOptions
            {
                DownscaleRatio = ratio,
                DownscaleQuality = quality,
                UseTracking = useTracking,
                DetectEveryNFrames = detectEvery,
                ParallelDetectorCount = Math.Max(1, SelectedParallelSessionCount)
            };
        }

        private FaceOnnxDetectorOptions BuildDetectorOptions()
        {
            return new FaceOnnxDetectorOptions
            {
                UseOrtOptimization = AutoUseOrtOptimization,
                UseGpu = AutoUseGpu,
                IntraOpNumThreads = SelectedOrtThreadOption?.Threads,
                InterOpNumThreads = null
            };
        }

        private static IReadOnlyList<OrtThreadOption> BuildOrtThreadOptions()
        {
            int max = Math.Max(1, Environment.ProcessorCount);
            var list = new List<OrtThreadOption>
            {
                new OrtThreadOption("자동", null)
            };

            int[] candidates = { 1, 2, 4, 6, 8, 12, 16, 24, 32 };
            foreach (var c in candidates)
            {
                if (c <= max)
                    list.Add(new OrtThreadOption($"{c} 스레드", c));
            }

            if (!list.Any(o => o.Threads == max))
                list.Add(new OrtThreadOption($"{max} 스레드", max));

            return list;
        }

        private async Task EnsureWorkspaceReadyAsync(WorkspaceViewModel vm)
        {
            IsWorkspaceLoading = true;
            WorkspaceLoadingMessage = "워크스페이스 준비 중...";
            WorkspaceLoadingProgress = 0;
            IsWorkspaceLoadingIndeterminate = false;

            try
            {
                var loadProgress = new Progress<int>(p =>
                    Dispatcher.UIThread.Post(() => WorkspaceLoadingProgress = p));
                await vm.EnsureSessionInitializedAsync(loadProgress);
            }
            finally
            {
                IsWorkspaceLoading = false;
            }
        }

        private async Task<bool> ShowResumeAutoDialogAsync()
        {
            var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            var owner = lifetime?.MainWindow;
            if (owner == null)
                return false;

            var dialog = new ResumeAutoDialog();
            return await dialog.ShowDialog<bool>(owner);
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

        [RelayCommand]
        private async Task CopyAccelStatusAsync()
        {
            if (string.IsNullOrWhiteSpace(AutoAccelStatus))
                return;

            var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            var clipboard = lifetime?.MainWindow?.Clipboard;
            if (clipboard == null)
                return;

            await clipboard.SetTextAsync(AutoAccelStatus);
        }

        private Task ShowAutoErrorAsync(Exception ex, bool isDuringRun)
        {
            string title = isDuringRun ? "자동 모드 실행 중 오류" : "자동 모드 준비 실패";
            string message = BuildAutoErrorMessage(ex);
            return ShowErrorDialogAsync(title, message);
        }

        private string BuildAutoErrorMessage(Exception ex)
        {
            string hint =
                RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                    ? "\n\nmacOS에서는 ONNX Runtime이 OpenMP(libomp.dylib)에 의존합니다. Homebrew로 'brew install libomp' 실행 후 다시 시도하고, 앱(.app/Contents/MacOS) 안에 onnxruntime 관련 dylib가 있는지 확인하세요."
                    : string.Empty;

            return $"{ex.Message}{hint}";
        }

        private WorkspaceViewModel GetOrCreateWorkspace(
            WorkspaceMode mode,
            IProgress<int>? loadProgress,
            AutoMaskOptions autoOptions,
            FaceOnnxDetectorOptions detectorOptions)
        {
            if (string.IsNullOrWhiteSpace(SelectedVideoPath))
                throw new InvalidOperationException("SelectedVideoPath is empty.");

            string key = $"{mode}:{SelectedVideoPath}";
            if (_workspaceCache.TryGetValue(key, out var cached))
            {
                cached.UpdateAutoOptions(autoOptions);
                cached.UpdateDetectorOptions(detectorOptions);
                return cached;
            }

            var vm = new WorkspaceViewModel(
                SelectedVideoPath,
                mode,
                loadProgress,
                _onBackHome,
                autoOptions,
                detectorOptions,
                _stateStore,
                deferSessionInit: mode == WorkspaceMode.Auto);

            vm.RestoreFromStore(_stateStore);

            _workspaceCache[key] = vm;
            return vm;
        }

        private void TouchRecent(string? videoPath)
        {
            if (string.IsNullOrWhiteSpace(videoPath))
                return;

            int existingIndex = -1;
            for (int i = 0; i < Recents.Count; i++)
            {
                if (string.Equals(Recents[i].Path, videoPath, StringComparison.OrdinalIgnoreCase))
                {
                    existingIndex = i;
                    break;
                }
            }

            if (existingIndex >= 0)
                Recents.RemoveAt(existingIndex);

            Recents.Insert(0, new RecentItem(Path.GetFileName(videoPath), videoPath, DateTimeOffset.Now));
            TrimRecents();
            _stateStore.SaveRecents(Recents);
        }

        public void PersistAllWorkspaces()
        {
            foreach (var vm in _workspaceCache.Values)
                vm.PersistWorkspaceState();

            _stateStore.SaveRecents(Recents);
        }

        private void TrimRecents()
        {
            while (Recents.Count > MaxRecents)
            {
                var removed = Recents[^1];
                Recents.RemoveAt(Recents.Count - 1);
                RemoveCachedWorkspaces(removed.Path);
                _stateStore.RemoveWorkspacesForPath(removed.Path);
            }
        }

        private void RemoveCachedWorkspaces(string videoPath)
        {
            if (string.IsNullOrWhiteSpace(videoPath))
                return;

            var keys = new List<string>();
            foreach (var entry in _workspaceCache)
            {
                if (entry.Key.EndsWith(videoPath, StringComparison.OrdinalIgnoreCase))
                    keys.Add(entry.Key);
            }

            foreach (var key in keys)
                _workspaceCache.Remove(key);
        }
    }
}
