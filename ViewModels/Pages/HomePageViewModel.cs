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
using Avalonia.Media.Imaging;

namespace FaceShield.ViewModels.Pages
{
    public partial class HomePageViewModel : ViewModelBase
    {
        private const int MaxRecents = 5;
        private const int DefaultBlurRadius = 28;
        private const int MinBlurRadiusValue = 6;
        private const int MaxBlurRadiusValue = 40;
        private const int DefaultAutoDetectEveryNFrames = 1;
        private const int CurrentAutoSettingsVersion = 3;
        private static readonly bool DefaultAutoUseGpu =
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
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
        private readonly Queue<(DateTime Timestamp, int FrameIndex)> _exportEtaSamples = new();
        private (DateTime Timestamp, int FrameIndex) _exportLastSample;

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

        [ObservableProperty]
        private string? exportStatusText;

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
        private int autoDetectEveryNFrames = DefaultAutoDetectEveryNFrames;

        public IReadOnlyList<int> ParallelSessionOptions { get; } = new[] { 1, 2, 3, 4 };

        [ObservableProperty]
        private int selectedParallelSessionCount = 2;

        [ObservableProperty]
        private bool autoTrackingEnabled = true;

        [ObservableProperty]
        private bool autoUseOrtOptimization = true;

        [ObservableProperty]
        private bool autoUseGpu = DefaultAutoUseGpu;

        [ObservableProperty]
        private bool autoExportAfter = true;

        [ObservableProperty]
        private double autoDetectionThreshold;

        [ObservableProperty]
        private double autoConfidenceThreshold;

        [ObservableProperty]
        private double autoNmsThreshold;

        [ObservableProperty]
        private int blurRadius = DefaultBlurRadius;

        [ObservableProperty]
        private IReadOnlyList<BlurExampleItem> blurExamples = Array.Empty<BlurExampleItem>();

        public sealed class ResolutionOption
        {
            public string Label { get; }
            public int Width { get; }
            public int Height { get; }

            public ResolutionOption(string label, int width, int height)
            {
                Label = label;
                Width = width;
                Height = height;
            }
        }

        public IReadOnlyList<ResolutionOption> ResolutionOptions { get; } = new[]
        {
            new ResolutionOption("1920x1080 (FHD)", 1920, 1080),
            new ResolutionOption("1280x720 (HD)", 1280, 720),
            new ResolutionOption("854x480", 854, 480),
            new ResolutionOption("640x360", 640, 360)
        };

        [ObservableProperty]
        private ResolutionOption? selectedResolutionOption;

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
            selectedResolutionOption = ResolutionOptions[0];
            var defaults = FaceOnnxDetector.GetDefaultThresholds();
            autoDetectionThreshold = defaults.Detection;
            autoConfidenceThreshold = defaults.Confidence;
            autoNmsThreshold = defaults.Nms;
            RegenerateBlurExamples();
            ApplySavedAutoSettings();
            foreach (var recent in _stateStore.GetRecents())
                Recents.Add(recent);

            TrimRecents();
        }

        public bool CanOpenWorkspace => !string.IsNullOrWhiteSpace(SelectedVideoPath);
        public bool CanStartWorkspace => CanOpenWorkspace && !IsAutoRunning && !IsWorkspaceLoading;
        public bool IsBusy => IsWorkspaceLoading || IsAutoRunning || IsExportRunning;
        public int BusyProgress => IsExportRunning
            ? ExportProgress
            : IsAutoRunning ? AutoProgress : WorkspaceLoadingProgress;
        public bool IsBusyIndeterminate => IsExportRunning
            ? false
            : IsAutoRunning ? false : IsWorkspaceLoadingIndeterminate;
        public string BusyMessage =>
            IsExportRunning
                ? "파일 저장 중..."
                : IsAutoRunning
                    ? "자동 모자이크 진행 중..."
                    : (WorkspaceLoadingMessage ?? "로딩 중...");
        public bool IsTrackingOptionsEnabled => AutoTrackingEnabled;
        public bool IsAutoStatusVisible => IsAutoRunning;
        public int MinBlurRadius => MinBlurRadiusValue;
        public int MaxBlurRadius => MaxBlurRadiusValue;

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
            OnPropertyChanged(nameof(IsAutoStatusVisible));

            if (!value)
            {
                AutoEtaText = null;
                _etaFrameSamples.Clear();
            }
        }

        partial void OnIsExportRunningChanged(bool value)
        {
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(BusyProgress));
            OnPropertyChanged(nameof(BusyMessage));
            OnPropertyChanged(nameof(IsBusyIndeterminate));
            OnPropertyChanged(nameof(IsAutoStatusVisible));
            if (!value)
            {
                ExportProgress = 0;
                ExportEtaText = null;
                ExportStatusText = null;
                _exportEtaSamples.Clear();
            }
        }

        partial void OnExportProgressChanged(int value)
        {
            if (IsExportRunning)
                OnPropertyChanged(nameof(BusyProgress));
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

        partial void OnAutoDetectionThresholdChanged(double value)
        {
            PersistAutoSettings();
            RequestAutoRestartForDetectorOptions("검출 임계값 변경 감지 · 재시작 준비 중...");
        }

        partial void OnAutoConfidenceThresholdChanged(double value)
        {
            PersistAutoSettings();
            RequestAutoRestartForDetectorOptions("신뢰도 임계값 변경 감지 · 재시작 준비 중...");
        }

        partial void OnAutoNmsThresholdChanged(double value)
        {
            PersistAutoSettings();
            RequestAutoRestartForDetectorOptions("NMS 임계값 변경 감지 · 재시작 준비 중...");
        }

        partial void OnBlurRadiusChanged(int value)
        {
            RegenerateBlurExamples();
            PersistAutoSettings();
            if (_activeAutoWorkspace != null)
                _activeAutoWorkspace.ToolPanel.BlurRadius = value;
        }

        partial void OnSelectedResolutionOptionChanged(ResolutionOption? value)
        {
            RegenerateBlurExamples();
        }

        partial void OnSelectedParallelSessionCountChanged(int value)
        {
            PersistAutoSettings();
            RequestAutoRestartForOptions("자동 옵션 변경 감지 · 재시작 준비 중...");
        }

        partial void OnSelectedDownscaleOptionChanged(DownscaleOption? value)
        {
            PersistAutoSettings();
            RequestAutoRestartForOptions("자동 옵션 변경 감지 · 재시작 준비 중...");
        }

        partial void OnSelectedDownscaleQualityOptionChanged(DownscaleQualityOption? value)
        {
            PersistAutoSettings();
            RequestAutoRestartForOptions("자동 옵션 변경 감지 · 재시작 준비 중...");
        }

        partial void OnSelectedOrtThreadOptionChanged(OrtThreadOption? value)
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

        private void RequestAutoRestartForDetectorOptions(string statusText)
        {
            if (!IsAutoRunning || _activeAutoWorkspace == null)
                return;

            _activeAutoWorkspace.UpdateDetectorOptions(BuildDetectorOptions());
            _autoRestartRequested = true;
            AutoStatusText = statusText;
            _autoCts?.Cancel();
        }

        private void ApplySavedAutoSettings()
        {
            var saved = _stateStore.GetAutoSettings();
            if (saved == null)
                return;

            bool isLegacyAutoSettings = saved.SettingsVersion < CurrentAutoSettingsVersion;
            var downscale = DownscaleOptions.FirstOrDefault(o => Math.Abs(o.Ratio - saved.DownscaleRatio) < 0.0001);
            if (!isLegacyAutoSettings && downscale != null)
                SelectedDownscaleOption = downscale;

            var quality = DownscaleQualityOptions.FirstOrDefault(o => (int)o.Quality == saved.DownscaleQuality);
            if (quality != null)
                SelectedDownscaleQualityOption = quality;

            var ort = OrtThreadOptions.FirstOrDefault(o => o.Threads == saved.OrtThreads);
            if (ort != null)
                SelectedOrtThreadOption = ort;

            AutoTrackingEnabled = saved.AutoTrackingEnabled;
            AutoDetectEveryNFrames = isLegacyAutoSettings
                ? DefaultAutoDetectEveryNFrames
                : Math.Max(1, saved.AutoDetectEveryNFrames);
            SelectedParallelSessionCount = isLegacyAutoSettings
                ? Math.Max(2, saved.ParallelSessionCount)
                : Math.Max(1, saved.ParallelSessionCount);
            AutoUseOrtOptimization = saved.AutoUseOrtOptimization;
            AutoUseGpu = isLegacyAutoSettings ? DefaultAutoUseGpu : saved.AutoUseGpu;
            AutoExportAfter = saved.AutoExportAfter;
            if (saved.DetectionThreshold.HasValue)
                AutoDetectionThreshold = saved.DetectionThreshold.Value;
            if (saved.ConfidenceThreshold.HasValue)
                AutoConfidenceThreshold = saved.ConfidenceThreshold.Value;
            if (saved.NmsThreshold.HasValue)
                AutoNmsThreshold = saved.NmsThreshold.Value;
            if (saved.BlurRadius.HasValue)
                BlurRadius = Math.Clamp(saved.BlurRadius.Value, MinBlurRadiusValue, MaxBlurRadiusValue);

            if (isLegacyAutoSettings)
                PersistAutoSettings();
        }

        private void PersistAutoSettings()
        {
            _stateStore.SaveAutoSettings(new AutoSettingsState
            {
                SettingsVersion = CurrentAutoSettingsVersion,
                DownscaleRatio = SelectedDownscaleOption?.Ratio ?? 1.0,
                DownscaleQuality = (int)(SelectedDownscaleQualityOption?.Quality ?? DownscaleQuality.BalancedBilinear),
                AutoTrackingEnabled = AutoTrackingEnabled,
                AutoDetectEveryNFrames = Math.Max(1, AutoDetectEveryNFrames),
                ParallelSessionCount = Math.Max(1, SelectedParallelSessionCount),
                AutoUseOrtOptimization = AutoUseOrtOptimization,
                AutoUseGpu = AutoUseGpu,
                OrtThreads = SelectedOrtThreadOption?.Threads,
                AutoExportAfter = AutoExportAfter,
                DetectionThreshold = AutoDetectionThreshold,
                ConfidenceThreshold = AutoConfidenceThreshold,
                NmsThreshold = AutoNmsThreshold,
                BlurRadius = BlurRadius
            });
        }

        private void RegenerateBlurExamples()
        {
            const int w = 120;
            const int h = 90;
            var percents = new[] { 1.0, 3.0, 5.0, 12.0 };
            var resolution = SelectedResolutionOption ?? ResolutionOptions[0];

            var list = new List<BlurExampleItem>(percents.Length);
            foreach (var p in percents)
            {
                var face = BuildCenteredFaceRect(w, h, p);
                var src = CreatePatternImage(w, h);
                var mask = FrameMaskProvider.CreateMaskFromFaceRects(new PixelSize(w, h), new[] { face });
                var preview = PreviewBlurProcessor.CreateBlurPreview(src, mask, BlurRadius, new[] { face });
                string label = BuildBlurLabel(p, resolution.Width, resolution.Height);
                list.Add(new BlurExampleItem(p, label, preview));

                src.Dispose();
                mask.Dispose();
            }

            if (BlurExamples != null)
            {
                foreach (var item in BlurExamples)
                    item.Image.Dispose();
            }

            BlurExamples = list;
        }

        private static string BuildBlurLabel(double percent, int width, int height)
        {
            double ratio = Math.Sqrt(Math.Max(0.1, percent) / 100.0);
            int faceW = Math.Max(1, (int)Math.Round(width * ratio));
            int faceH = Math.Max(1, (int)Math.Round(height * ratio));
            return $"얼굴 {percent:0.#}% ({faceW}x{faceH}px)";
        }

        public BlurPreviewPayload? BuildBlurPreview(double percent)
        {
            var resolution = SelectedResolutionOption ?? ResolutionOptions[0];
            int w = resolution.Width;
            int h = resolution.Height;

            var face = BuildCenteredFaceRect(w, h, percent);
            var src = CreatePatternImage(w, h);
            var mask = FrameMaskProvider.CreateMaskFromFaceRects(new PixelSize(w, h), new[] { face });
            var preview = PreviewBlurProcessor.CreateBlurPreview(src, mask, BlurRadius, new[] { face });

            src.Dispose();
            mask.Dispose();
            int faceW = (int)Math.Round(face.Width);
            int faceH = (int)Math.Round(face.Height);
            string label = $"{resolution.Label} / 얼굴 {percent:0.#}% ({faceW}x{faceH}px)";
            return new BlurPreviewPayload(preview, label);
        }

        private static Rect BuildCenteredFaceRect(int width, int height, double areaPercent)
        {
            double ratio = Math.Sqrt(Math.Max(0.1, areaPercent) / 100.0);
            double size = Math.Max(10, Math.Min(width, height) * ratio);
            double x = (width - size) * 0.5;
            double y = (height - size) * 0.5;
            return new Rect(x, y, size, size);
        }

        private static WriteableBitmap CreatePatternImage(int width, int height)
        {
            var bmp = new WriteableBitmap(
                new PixelSize(width, height),
                new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888,
                Avalonia.Platform.AlphaFormat.Premul);

            using var fb = bmp.Lock();
            unsafe
            {
                byte* basePtr = (byte*)fb.Address;
                int stride = fb.RowBytes;

                for (int y = 0; y < height; y++)
                {
                    byte* row = basePtr + y * stride;
                    for (int x = 0; x < width; x++)
                    {
                        bool checker = ((x / 6) + (y / 6)) % 2 == 0;
                        byte r = checker ? (byte)210 : (byte)160;
                        byte g = checker ? (byte)190 : (byte)140;
                        byte b = checker ? (byte)120 : (byte)90;

                        if (x % 12 == 0 || y % 12 == 0)
                        {
                            r = 240;
                            g = 220;
                            b = 140;
                        }

                        int idx = x * 4;
                        row[idx + 0] = b;
                        row[idx + 1] = g;
                        row[idx + 2] = r;
                        row[idx + 3] = 255;
                    }
                }
            }

            return bmp;
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
                var progress = new Progress<int>(p => WorkspaceLoadingProgress = p);

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
                    {
                        AutoProgress = p;
                        _autoLastProgressAtUtc = DateTime.UtcNow;
                    });

                    int lastExportPercent = -1;
                    string? lastExportStatus = null;
                    long lastExportUiTick = 0;
                    var exportProgress = new Progress<ExportProgress>(p =>
                    {
                        int percent = Math.Clamp(p.Percent, 0, 100);
                        string? status = string.IsNullOrWhiteSpace(p.StatusMessage) ? null : p.StatusMessage;
                        bool percentChanged = percent != lastExportPercent;
                        bool statusChanged = status != null &&
                            !string.Equals(status, lastExportStatus, StringComparison.Ordinal);
                        long nowTick = Environment.TickCount64;
                        bool etaDue = nowTick - lastExportUiTick >= 250;
                        if (!percentChanged && !statusChanged && !etaDue)
                            return;

                        lastExportPercent = percent;
                        if (statusChanged)
                            lastExportStatus = status;
                        lastExportUiTick = nowTick;

                        IsExportRunning = true;
                        ExportProgress = percent;
                        UpdateExportEta(DateTime.UtcNow, p.FrameIndex, p.TotalFrames);
                        if (statusChanged)
                            ExportStatusText = status;
                        if (ExportProgress == 0 && string.IsNullOrWhiteSpace(ExportEtaText))
                            ExportEtaText = "예상 남은 시간 계산 중...";
                    });

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
                if (!AutoExportAfter)
                {
                    await EnsureWorkspaceReadyAsync(vm);
                    _onStartWorkspace(vm);
                }
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

        [RelayCommand]
        private async Task ShowBlurPreviewAsync()
        {
            var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            var owner = lifetime?.MainWindow;
            if (owner == null)
                return;

            var dialog = new BlurPreviewDialog(this);
            await dialog.ShowDialog(owner);
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

        private void UpdateExportEta(DateTime timestamp, int frameIndex, int totalFrames)
        {
            if (totalFrames <= 0 || frameIndex <= 0)
            {
                if (string.IsNullOrWhiteSpace(ExportEtaText))
                    ExportEtaText = "예상 남은 시간 계산 중...";
                return;
            }
            if (frameIndex >= totalFrames)
            {
                ExportEtaText = null;
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
                ExportEtaText = "예상 남은 시간 계산 중...";
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
                AllowAutoGpu = AutoUseGpu,
                IntraOpNumThreads = SelectedOrtThreadOption?.Threads,
                InterOpNumThreads = null,
                DetectionThreshold = (float)Math.Clamp(AutoDetectionThreshold, 0.01, 0.99),
                ConfidenceThreshold = (float)Math.Clamp(AutoConfidenceThreshold, 0.01, 0.99),
                NmsThreshold = (float)Math.Clamp(AutoNmsThreshold, 0.01, 0.99)
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
                var loadProgress = new Progress<int>(p => WorkspaceLoadingProgress = p);
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
                cached.ToolPanel.BlurRadius = BlurRadius;
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
            vm.ToolPanel.BlurRadius = BlurRadius;

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
