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
using System.Net.Http;
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
        private const int CurrentAutoSettingsVersion = 10;
        private const int BalancedDownscaleQualitySettingsVersion = 10;
        private const double DefaultYolo5FaceObjectnessThreshold = 0.12;
        private const double DefaultYolo5FaceConfidenceThreshold = 0.18;
        private const double DefaultYoloNmsThreshold = 0.45;
        private const string DefaultYoloModelDirectory = "Models/Yolo";
        private static readonly string[] DefaultYolo5FaceModelFileNames = ["YoloV5Face.onnx", "Yolo5Face.onnx"];
        private static readonly string[] DefaultYoloV8FaceModelFileNames =
        [
            "yolov8n-face-lindevs.onnx",
            "yolov8s-face-lindevs.onnx",
            "yolov8m-face-lindevs.onnx",
            "yolov8l-face-lindevs.onnx"
        ];
        private static readonly HttpClient YoloModelDownloadHttpClient = new();
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
        private bool _isApplyingAutoSettings;
        private bool _isApplyingYoloProfile;
        private YoloFaceModelType _activeYoloModelType = YoloFaceModelType.Yolo5Face;
        private YoloProfileState _yoloV8Profile = YoloProfileState.CreateDefault(YoloFaceModelType.YoloV8Face);
        private YoloProfileState _yolo5Profile = YoloProfileState.CreateDefault(YoloFaceModelType.Yolo5Face);

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

        public sealed class AutoDetectorBackendOption
        {
            public string Label { get; }
            public FaceDetectorBackend Backend { get; }

            public AutoDetectorBackendOption(string label, FaceDetectorBackend backend)
            {
                Label = label;
                Backend = backend;
            }
        }

        public IReadOnlyList<AutoDetectorBackendOption> AutoDetectorBackendOptions { get; } = new[]
        {
            new AutoDetectorBackendOption("FaceONNX", FaceDetectorBackend.FaceOnnx),
            new AutoDetectorBackendOption("YOLO Face ONNX", FaceDetectorBackend.YoloFaceOnnx)
        };

        [ObservableProperty]
        private AutoDetectorBackendOption? selectedAutoDetectorBackendOption;

        public sealed class AutoProcessingModeOption
        {
            public string Label { get; }
            public AutoMaskProcessingMode Mode { get; }

            public AutoProcessingModeOption(string label, AutoMaskProcessingMode mode)
            {
                Label = label;
                Mode = mode;
            }
        }

        public IReadOnlyList<AutoProcessingModeOption> AutoProcessingModeOptions { get; } = new[]
        {
            new AutoProcessingModeOption("자동 안정화 (권장)", AutoMaskProcessingMode.Tracked),
            new AutoProcessingModeOption("검출 결과 그대로", AutoMaskProcessingMode.Raw),
            new AutoProcessingModeOption("전체 보정", AutoMaskProcessingMode.Full),
            new AutoProcessingModeOption("이전 설정 호환", AutoMaskProcessingMode.Legacy)
        };

        [ObservableProperty]
        private AutoProcessingModeOption? selectedAutoProcessingModeOption;

        public sealed class YoloModelTypeOption
        {
            public string Label { get; }
            public YoloFaceModelType ModelType { get; }

            public YoloModelTypeOption(string label, YoloFaceModelType modelType)
            {
                Label = label;
                ModelType = modelType;
            }
        }

        public IReadOnlyList<YoloModelTypeOption> YoloModelTypeOptions { get; } = new[]
        {
            new YoloModelTypeOption("YOLOv8-Face", YoloFaceModelType.YoloV8Face),
            new YoloModelTypeOption("YOLO5Face", YoloFaceModelType.Yolo5Face)
        };

        private sealed class YoloModelDownloadInfo
        {
            public string FileName { get; }
            public string DownloadUrl { get; }
            public string SourceLabel { get; }
            public string LicenseLabel { get; }

            public YoloModelDownloadInfo(string fileName, string downloadUrl, string sourceLabel, string licenseLabel)
            {
                FileName = fileName;
                DownloadUrl = downloadUrl;
                SourceLabel = sourceLabel;
                LicenseLabel = licenseLabel;
            }
        }

        private sealed class YoloProfileState
        {
            public string? ModelPath { get; init; }
            public double ObjectnessThreshold { get; init; }
            public double ConfidenceThreshold { get; init; }
            public double NmsThreshold { get; init; }
            public int InputSize { get; init; }
            public bool UseTiling { get; init; }
            public bool TileOnly { get; init; }
            public int TileColumns { get; init; }
            public int TileRows { get; init; }
            public double TileOverlapRatio { get; init; }
            public double DownscaleRatio { get; init; }
            public DownscaleQuality DownscaleQuality { get; init; }
            public bool AutoTrackingEnabled { get; init; }
            public int AutoDetectEveryNFrames { get; init; }
            public int ParallelSessionCount { get; init; }

            public static YoloProfileState CreateDefault(YoloFaceModelType modelType)
            {
                return new YoloProfileState
                {
                    ModelPath = ResolveDefaultYoloModelPath(modelType),
                    ObjectnessThreshold = modelType == YoloFaceModelType.Yolo5Face ? DefaultYolo5FaceObjectnessThreshold : 0.25,
                    ConfidenceThreshold = modelType == YoloFaceModelType.Yolo5Face ? DefaultYolo5FaceConfidenceThreshold : 0.35,
                    NmsThreshold = DefaultYoloNmsThreshold,
                    InputSize = 640,
                    UseTiling = false,
                    TileOnly = false,
                    TileColumns = 2,
                    TileRows = 2,
                    TileOverlapRatio = 0.15,
                    DownscaleRatio = 1.0,
                    DownscaleQuality = DownscaleQuality.BalancedBilinear,
                    AutoTrackingEnabled = true,
                    AutoDetectEveryNFrames = DefaultAutoDetectEveryNFrames,
                    ParallelSessionCount = 2
                };
            }
        }

        [ObservableProperty]
        private YoloModelTypeOption? selectedYoloModelTypeOption;

        [ObservableProperty]
        private string? autoYoloModelPath;

        [ObservableProperty]
        private bool isYoloModelDownloading;

        [ObservableProperty]
        private int yoloModelDownloadProgress;

        [ObservableProperty]
        private string? yoloModelDownloadStatus;

        [ObservableProperty]
        private double autoYoloObjectnessThreshold = DefaultYolo5FaceObjectnessThreshold;

        [ObservableProperty]
        private double autoYoloConfidenceThreshold = DefaultYolo5FaceConfidenceThreshold;

        [ObservableProperty]
        private double autoYoloNmsThreshold = DefaultYoloNmsThreshold;

        [ObservableProperty]
        private int autoYoloInputSize = 640;

        [ObservableProperty]
        private bool autoYoloUseTiling;

        [ObservableProperty]
        private bool autoYoloTileOnly;

        [ObservableProperty]
        private bool autoYoloEnableCoreMl;

        [ObservableProperty]
        private int autoYoloTileColumns = 2;

        [ObservableProperty]
        private int autoYoloTileRows = 2;

        [ObservableProperty]
        private double autoYoloTileOverlapRatio = 0.15;

        public IReadOnlyList<int> DetectEveryOptions { get; } = new[] { 1, 2, 3, 5 };

        [ObservableProperty]
        private int autoDetectEveryNFrames = DefaultAutoDetectEveryNFrames;

        public IReadOnlyList<int> ParallelSessionOptions { get; } = new[] { 1, 2, 3, 4 };

        [ObservableProperty]
        private int selectedParallelSessionCount = 2;

        [ObservableProperty]
        private bool autoTrackingEnabled = true;

        [ObservableProperty]
        private bool enablePostProcessing = false;

        [ObservableProperty]
        private bool enableRoiPostProcess = false;

        [ObservableProperty]
        private bool enableYoloWeakIsolatedCleanup = false;

        [ObservableProperty]
        private bool enableYoloGapFill = false;

        [ObservableProperty]
        private bool enableYoloSceneCutCarryCleanup = false;

        [ObservableProperty]
        private bool enableYoloTemporalSmoothing = false;

        [ObservableProperty]
        private bool enableYoloRiskCascade = false;

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
            selectedAutoDetectorBackendOption = AutoDetectorBackendOptions[0];
            selectedAutoProcessingModeOption = AutoProcessingModeOptions[0];
            selectedYoloModelTypeOption = YoloModelTypeOptions[1];
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

        public void ApplyStartupOptions(AppStartupOptions options)
        {
            if (options.DetectorBackend.HasValue)
            {
                var backend = AutoDetectorBackendOptions.FirstOrDefault(o => o.Backend == options.DetectorBackend.Value);
                if (backend is not null)
                    SelectedAutoDetectorBackendOption = backend;
            }

            if (options.YoloModelType.HasValue)
            {
                var modelType = YoloModelTypeOptions.FirstOrDefault(o => o.ModelType == options.YoloModelType.Value);
                if (modelType is not null)
                    SelectedYoloModelTypeOption = modelType;
            }

            if (options.ProcessingMode.HasValue)
            {
                var processingMode = AutoProcessingModeOptions.FirstOrDefault(o => o.Mode == options.ProcessingMode.Value);
                if (processingMode != null)
                    SelectedAutoProcessingModeOption = processingMode;
            }

            if (options.EnableYoloRiskCascade.HasValue)
                EnableYoloRiskCascade = options.EnableYoloRiskCascade.Value;

            if (!string.IsNullOrWhiteSpace(options.YoloModelPath))
                AutoYoloModelPath = options.YoloModelPath;

            if (!string.IsNullOrWhiteSpace(options.VideoPath))
                SelectedVideoPath = options.VideoPath;

            if (options.AutoExportAfter.HasValue)
                AutoExportAfter = options.AutoExportAfter.Value;

            PersistAutoSettings();
        }

        public bool CanOpenWorkspace => !string.IsNullOrWhiteSpace(SelectedVideoPath);
        public bool CanStartWorkspace => CanOpenWorkspace && !IsAutoRunning && !IsWorkspaceLoading;
        public string SelectedVideoDisplayName => string.IsNullOrWhiteSpace(SelectedVideoPath)
            ? "영상을 선택해 주세요"
            : Path.GetFileName(SelectedVideoPath);
        public string SelectedVideoDirectory => string.IsNullOrWhiteSpace(SelectedVideoPath)
            ? ""
            : Path.GetDirectoryName(SelectedVideoPath) ?? "";
        public bool HasRecents => Recents.Count > 0;
        public bool HasNoRecents => !HasRecents;
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
        public bool IsLegacyProcessingMode =>
            (SelectedAutoProcessingModeOption?.Mode ?? AutoMaskProcessingMode.Tracked) == AutoMaskProcessingMode.Legacy;
        public bool IsTrackingIntervalVisible =>
            (SelectedAutoProcessingModeOption?.Mode ?? AutoMaskProcessingMode.Tracked) != AutoMaskProcessingMode.Raw;
        public bool IsTrackingIntervalEnabled => !IsLegacyProcessingMode || AutoTrackingEnabled;
        public bool IsYoloCascadeOptionVisible => IsYoloDetectorSelected &&
            (SelectedAutoProcessingModeOption?.Mode is AutoMaskProcessingMode.Legacy or AutoMaskProcessingMode.Full);
        public bool IsYoloCascadeOptionEnabled => IsYoloCascadeOptionVisible &&
            ((!IsLegacyProcessingMode || AutoTrackingEnabled)
                ? AutoDetectEveryNFrames <= 1
                : true);
        public string EffectiveProcessingModeDescription =>
            (SelectedAutoProcessingModeOption?.Mode ?? AutoMaskProcessingMode.Tracked) switch
            {
                AutoMaskProcessingMode.Raw => "매 프레임 검출만 수행하며 추적·보간·후처리를 적용하지 않습니다.",
                AutoMaskProcessingMode.Tracked => "짧은 검출 누락만 연결하며 전체 후처리는 사용하지 않습니다.",
                AutoMaskProcessingMode.Full => "추적과 모든 후처리 모듈을 적용합니다.",
                _ => "기존 추적 및 후처리 토글 조합을 그대로 사용합니다."
            };
        public bool IsAutoStatusVisible => IsAutoRunning;
        public bool IsYoloDetectorSelected => SelectedAutoDetectorBackendOption?.Backend == FaceDetectorBackend.YoloFaceOnnx;
        public bool IsFaceOnnxDetectorSelected => !IsYoloDetectorSelected;
        public bool CanDownloadYoloModel => IsYoloDetectorSelected && !IsYoloModelDownloading;
        public int MinBlurRadius => MinBlurRadiusValue;
        public int MaxBlurRadius => MaxBlurRadiusValue;

        partial void OnSelectedVideoPathChanged(string? value)
        {
            OnPropertyChanged(nameof(CanOpenWorkspace));
            OnPropertyChanged(nameof(CanStartWorkspace));
            OnPropertyChanged(nameof(SelectedVideoDisplayName));
            OnPropertyChanged(nameof(SelectedVideoDirectory));
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

            _activeAutoWorkspace.UpdateDetectorFactoryOptions(BuildDetectorFactoryOptions());
            _autoRestartRequested = true;
            AutoStatusText = "가속 옵션 변경 감지 · 재시작 준비 중...";
            _autoCts?.Cancel();
        }

        partial void OnAutoUseGpuChanged(bool value)
        {
            PersistAutoSettings();
            if (!IsAutoRunning || _activeAutoWorkspace == null)
                return;

            _activeAutoWorkspace.UpdateDetectorFactoryOptions(BuildDetectorFactoryOptions());
            _autoRestartRequested = true;
            AutoStatusText = "GPU 옵션 변경 감지 · 재시작 준비 중...";
            _autoCts?.Cancel();
        }

        partial void OnAutoYoloEnableCoreMlChanged(bool value)
        {
            PersistAutoSettings();
            if (IsYoloDetectorSelected)
                RequestAutoRestartForDetectorFactoryOptions("YOLO CoreML 옵션 변경 감지 · 재시작 준비 중...");
        }

        partial void OnAutoTrackingEnabledChanged(bool value)
        {
            if (_isApplyingYoloProfile)
                return;

            PersistAutoSettings();
            OnPropertyChanged(nameof(IsTrackingOptionsEnabled));
            OnPropertyChanged(nameof(IsTrackingIntervalEnabled));
            OnPropertyChanged(nameof(IsYoloCascadeOptionEnabled));
            RequestAutoRestartForOptions("자동 옵션 변경 감지 · 재시작 준비 중...");
        }

        partial void OnSelectedAutoProcessingModeOptionChanged(AutoProcessingModeOption? value)
        {
            PersistAutoSettings();
            OnPropertyChanged(nameof(IsLegacyProcessingMode));
            OnPropertyChanged(nameof(IsTrackingIntervalVisible));
            OnPropertyChanged(nameof(IsTrackingIntervalEnabled));
            OnPropertyChanged(nameof(IsYoloCascadeOptionVisible));
            OnPropertyChanged(nameof(IsYoloCascadeOptionEnabled));
            OnPropertyChanged(nameof(EffectiveProcessingModeDescription));
            RequestAutoRestartForOptions("분석 모드 변경 감지 · 재시작 준비 중...");
        }

        partial void OnEnablePostProcessingChanged(bool value)
        {
            if (_isApplyingYoloProfile)
                return;

            PersistAutoSettings();
            RequestAutoRestartForOptions("후처리 기본 토글 변경 감지 · 재시작 준비 중...");
        }

        partial void OnEnableRoiPostProcessChanged(bool value)
        {
            if (_isApplyingYoloProfile)
                return;

            PersistAutoSettings();
            if (IsYoloDetectorSelected)
                RequestAutoRestartForOptions("후처리 ROI 토글 변경 감지 · 재시작 준비 중...");
        }

        partial void OnEnableYoloWeakIsolatedCleanupChanged(bool value)
        {
            if (_isApplyingYoloProfile)
                return;

            PersistAutoSettings();
            if (IsYoloDetectorSelected)
                RequestAutoRestartForOptions("후처리 오탐 제거 토글 변경 감지 · 재시작 준비 중...");
        }

        partial void OnEnableYoloGapFillChanged(bool value)
        {
            if (_isApplyingYoloProfile)
                return;

            PersistAutoSettings();
            if (IsYoloDetectorSelected)
                RequestAutoRestartForOptions("후처리 간극 보완 토글 변경 감지 · 재시작 준비 중...");
        }

        partial void OnEnableYoloSceneCutCarryCleanupChanged(bool value)
        {
            if (_isApplyingYoloProfile)
                return;

            PersistAutoSettings();
            if (IsYoloDetectorSelected)
                RequestAutoRestartForOptions("후처리 장면 전환 토글 변경 감지 · 재시작 준비 중...");
        }

        partial void OnEnableYoloTemporalSmoothingChanged(bool value)
        {
            if (_isApplyingYoloProfile)
                return;

            PersistAutoSettings();
            if (IsYoloDetectorSelected)
                RequestAutoRestartForOptions("후처리 시계열 스무딩 토글 변경 감지 · 재시작 준비 중...");
        }

        partial void OnEnableYoloRiskCascadeChanged(bool value)
        {
            PersistAutoSettings();
            if (IsYoloDetectorSelected)
                RequestAutoRestartForOptions("YOLO 위험 프레임 재검출 변경 감지 · 재시작 준비 중...");
        }

        partial void OnAutoDetectEveryNFramesChanged(int value)
        {
            if (_isApplyingYoloProfile)
                return;

            PersistAutoSettings();
            OnPropertyChanged(nameof(IsYoloCascadeOptionEnabled));
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
            if (_isApplyingYoloProfile)
                return;

            PersistAutoSettings();
            RequestAutoRestartForOptions("자동 옵션 변경 감지 · 재시작 준비 중...");
        }

        partial void OnSelectedDownscaleOptionChanged(DownscaleOption? value)
        {
            if (_isApplyingYoloProfile)
                return;

            PersistAutoSettings();
            RequestAutoRestartForOptions("자동 옵션 변경 감지 · 재시작 준비 중...");
        }

        partial void OnSelectedDownscaleQualityOptionChanged(DownscaleQualityOption? value)
        {
            if (_isApplyingYoloProfile)
                return;

            PersistAutoSettings();
            RequestAutoRestartForOptions("자동 옵션 변경 감지 · 재시작 준비 중...");
        }

        partial void OnSelectedOrtThreadOptionChanged(OrtThreadOption? value)
        {
            PersistAutoSettings();
            if (!IsAutoRunning || _activeAutoWorkspace == null)
                return;

            _activeAutoWorkspace.UpdateDetectorFactoryOptions(BuildDetectorFactoryOptions());
            _autoRestartRequested = true;
            AutoStatusText = "가속 옵션 변경 감지 · 재시작 준비 중...";
            _autoCts?.Cancel();
        }

        partial void OnAutoExportAfterChanged(bool value)
        {
            PersistAutoSettings();
        }

        partial void OnSelectedAutoDetectorBackendOptionChanged(AutoDetectorBackendOption? value)
        {
            PersistAutoSettings();
            OnPropertyChanged(nameof(IsYoloDetectorSelected));
            OnPropertyChanged(nameof(IsFaceOnnxDetectorSelected));
            OnPropertyChanged(nameof(IsYoloCascadeOptionVisible));
            OnPropertyChanged(nameof(IsYoloCascadeOptionEnabled));
            OnPropertyChanged(nameof(CanDownloadYoloModel));
            DownloadYoloModelCommand.NotifyCanExecuteChanged();
            RequestAutoRestartForDetectorFactoryOptions("검출 모델 변경 감지 · 재시작 준비 중...");
        }

        partial void OnSelectedYoloModelTypeOptionChanged(YoloModelTypeOption? value)
        {
            var modelType = value?.ModelType ?? YoloFaceModelType.YoloV8Face;
            if (_isApplyingAutoSettings)
            {
                _activeYoloModelType = modelType;
                return;
            }

            StoreCurrentYoloProfile();
            _activeYoloModelType = modelType;
            ApplyYoloProfile(GetYoloProfile(modelType));
            YoloModelDownloadStatus = null;
            YoloModelDownloadProgress = 0;
            DownloadYoloModelCommand.NotifyCanExecuteChanged();
            PersistAutoSettings();
            RequestAutoRestartForDetectorFactoryOptions("YOLO 모델 종류 변경 감지 · 재시작 준비 중...");
        }

        private YoloProfileState GetYoloProfile(YoloFaceModelType modelType)
        {
            return modelType == YoloFaceModelType.Yolo5Face
                ? _yolo5Profile
                : _yoloV8Profile;
        }

        private void StoreCurrentYoloProfile()
        {
            var modelType = _activeYoloModelType;
            var profile = CaptureCurrentYoloProfile();
            if (modelType == YoloFaceModelType.Yolo5Face)
            {
                _yolo5Profile = profile;
                return;
            }

            _yoloV8Profile = profile;
        }

        private YoloProfileState CaptureCurrentYoloProfile()
        {
            return new YoloProfileState
            {
                ModelPath = AutoYoloModelPath,
                ObjectnessThreshold = AutoYoloObjectnessThreshold,
                ConfidenceThreshold = AutoYoloConfidenceThreshold,
                NmsThreshold = AutoYoloNmsThreshold,
                InputSize = AutoYoloInputSize,
                UseTiling = AutoYoloUseTiling,
                TileOnly = AutoYoloTileOnly,
                TileColumns = AutoYoloTileColumns,
                TileRows = AutoYoloTileRows,
                TileOverlapRatio = AutoYoloTileOverlapRatio,
                DownscaleRatio = SelectedDownscaleOption?.Ratio ?? 1.0,
                DownscaleQuality = SelectedDownscaleQualityOption?.Quality ?? DownscaleQuality.BalancedBilinear,
                AutoTrackingEnabled = AutoTrackingEnabled,
                AutoDetectEveryNFrames = Math.Max(1, AutoDetectEveryNFrames),
                ParallelSessionCount = Math.Max(1, SelectedParallelSessionCount)
            };
        }

        private void ApplyYoloProfile(YoloProfileState profile)
        {
            _isApplyingYoloProfile = true;
            try
            {
                AutoYoloModelPath = profile.ModelPath;
                AutoYoloObjectnessThreshold = profile.ObjectnessThreshold;
                AutoYoloConfidenceThreshold = profile.ConfidenceThreshold;
                AutoYoloNmsThreshold = profile.NmsThreshold;
                AutoYoloInputSize = profile.InputSize;
                AutoYoloUseTiling = profile.UseTiling;
                AutoYoloTileOnly = profile.TileOnly;
                AutoYoloTileColumns = profile.TileColumns;
                AutoYoloTileRows = profile.TileRows;
                AutoYoloTileOverlapRatio = profile.TileOverlapRatio;
                var downscale = DownscaleOptions.FirstOrDefault(o => Math.Abs(o.Ratio - profile.DownscaleRatio) < 0.0001);
                if (downscale != null)
                    SelectedDownscaleOption = downscale;
                var quality = DownscaleQualityOptions.FirstOrDefault(o => o.Quality == profile.DownscaleQuality);
                if (quality != null)
                    SelectedDownscaleQualityOption = quality;
                AutoTrackingEnabled = profile.AutoTrackingEnabled;
                AutoDetectEveryNFrames = Math.Max(1, profile.AutoDetectEveryNFrames);
                SelectedParallelSessionCount = Math.Max(1, profile.ParallelSessionCount);
                OnPropertyChanged(nameof(IsTrackingOptionsEnabled));
            }
            finally
            {
                _isApplyingYoloProfile = false;
            }
        }

        partial void OnAutoYoloModelPathChanged(string? value)
        {
            if (_isApplyingYoloProfile)
                return;

            PersistAutoSettings();
            RequestAutoRestartForDetectorFactoryOptions("YOLO 모델 경로 변경 감지 · 재시작 준비 중...");
        }

        partial void OnIsYoloModelDownloadingChanged(bool value)
        {
            OnPropertyChanged(nameof(CanDownloadYoloModel));
            DownloadYoloModelCommand.NotifyCanExecuteChanged();
        }

        partial void OnAutoYoloObjectnessThresholdChanged(double value)
        {
            if (_isApplyingYoloProfile)
                return;

            PersistAutoSettings();
            RequestAutoRestartForDetectorFactoryOptions("YOLO objectness 변경 감지 · 재시작 준비 중...");
        }

        partial void OnAutoYoloConfidenceThresholdChanged(double value)
        {
            if (_isApplyingYoloProfile)
                return;

            PersistAutoSettings();
            RequestAutoRestartForDetectorFactoryOptions("YOLO confidence 변경 감지 · 재시작 준비 중...");
        }

        partial void OnAutoYoloNmsThresholdChanged(double value)
        {
            if (_isApplyingYoloProfile)
                return;

            PersistAutoSettings();
            RequestAutoRestartForDetectorFactoryOptions("YOLO NMS 변경 감지 · 재시작 준비 중...");
        }

        partial void OnAutoYoloInputSizeChanged(int value)
        {
            if (_isApplyingYoloProfile)
                return;

            PersistAutoSettings();
            RequestAutoRestartForDetectorFactoryOptions("YOLO 입력 크기 변경 감지 · 재시작 준비 중...");
        }

        partial void OnAutoYoloUseTilingChanged(bool value)
        {
            if (_isApplyingYoloProfile)
                return;

            PersistAutoSettings();
            RequestAutoRestartForDetectorFactoryOptions("YOLO 타일 옵션 변경 감지 · 재시작 준비 중...");
        }

        partial void OnAutoYoloTileOnlyChanged(bool value)
        {
            if (_isApplyingYoloProfile)
                return;

            PersistAutoSettings();
            RequestAutoRestartForDetectorFactoryOptions("YOLO 타일 옵션 변경 감지 · 재시작 준비 중...");
        }

        partial void OnAutoYoloTileColumnsChanged(int value)
        {
            if (_isApplyingYoloProfile)
                return;

            PersistAutoSettings();
            RequestAutoRestartForDetectorFactoryOptions("YOLO 타일 옵션 변경 감지 · 재시작 준비 중...");
        }

        partial void OnAutoYoloTileRowsChanged(int value)
        {
            if (_isApplyingYoloProfile)
                return;

            PersistAutoSettings();
            RequestAutoRestartForDetectorFactoryOptions("YOLO 타일 옵션 변경 감지 · 재시작 준비 중...");
        }

        partial void OnAutoYoloTileOverlapRatioChanged(double value)
        {
            if (_isApplyingYoloProfile)
                return;

            PersistAutoSettings();
            RequestAutoRestartForDetectorFactoryOptions("YOLO 타일 옵션 변경 감지 · 재시작 준비 중...");
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

            _activeAutoWorkspace.UpdateDetectorFactoryOptions(BuildDetectorFactoryOptions());
            _autoRestartRequested = true;
            AutoStatusText = statusText;
            _autoCts?.Cancel();
        }

        private void RequestAutoRestartForDetectorFactoryOptions(string statusText)
        {
            if (!IsAutoRunning || _activeAutoWorkspace == null)
                return;

            _activeAutoWorkspace.UpdateAutoOptions(BuildAutoOptions());
            _activeAutoWorkspace.UpdateDetectorFactoryOptions(BuildDetectorFactoryOptions());
            _autoRestartRequested = true;
            AutoStatusText = statusText;
            _autoCts?.Cancel();
        }

        private static YoloProfileState ReadSavedYoloProfile(
            AutoSettingsState saved,
            YoloFaceModelType modelType,
            YoloFaceModelType selectedModelType)
        {
            var defaults = YoloProfileState.CreateDefault(modelType);
            bool useLegacyActiveProfile = selectedModelType == modelType;
            string? legacyModelPath = useLegacyActiveProfile ? NormalizeYoloModelPath(saved.YoloModelPath) : null;

            if (modelType == YoloFaceModelType.Yolo5Face)
            {
                return new YoloProfileState
                {
                    ModelPath = NormalizeYoloModelPath(saved.Yolo5ModelPath) ?? legacyModelPath ?? defaults.ModelPath,
                    ObjectnessThreshold = saved.Yolo5ObjectnessThreshold ?? (useLegacyActiveProfile ? saved.YoloObjectnessThreshold : null) ?? defaults.ObjectnessThreshold,
                    ConfidenceThreshold = saved.Yolo5ConfidenceThreshold ?? (useLegacyActiveProfile ? saved.YoloConfidenceThreshold : null) ?? defaults.ConfidenceThreshold,
                    NmsThreshold = saved.Yolo5NmsThreshold ?? (useLegacyActiveProfile ? saved.YoloNmsThreshold : null) ?? defaults.NmsThreshold,
                    InputSize = Math.Clamp(saved.Yolo5InputSize ?? (useLegacyActiveProfile ? saved.YoloInputSize : null) ?? defaults.InputSize, 64, 2048),
                    UseTiling = saved.Yolo5UseTiling ?? (useLegacyActiveProfile ? (bool?)saved.YoloUseTiling : null) ?? defaults.UseTiling,
                    TileOnly = saved.Yolo5TileOnly ?? (useLegacyActiveProfile ? (bool?)saved.YoloTileOnly : null) ?? defaults.TileOnly,
                    TileColumns = Math.Clamp(saved.Yolo5TileColumns ?? (useLegacyActiveProfile ? saved.YoloTileColumns : null) ?? defaults.TileColumns, 1, 8),
                    TileRows = Math.Clamp(saved.Yolo5TileRows ?? (useLegacyActiveProfile ? saved.YoloTileRows : null) ?? defaults.TileRows, 1, 8),
                    TileOverlapRatio = Math.Clamp(saved.Yolo5TileOverlapRatio ?? (useLegacyActiveProfile ? saved.YoloTileOverlapRatio : null) ?? defaults.TileOverlapRatio, 0.0, 0.45),
                    DownscaleRatio = ResolveSavedYoloDownscaleRatio(saved.Yolo5DownscaleRatio, useLegacyActiveProfile ? saved.DownscaleRatio : null, defaults.DownscaleRatio),
                    DownscaleQuality = ResolveSavedYoloDownscaleQuality(
                        saved.Yolo5DownscaleQuality,
                        useLegacyActiveProfile ? saved.DownscaleQuality : null,
                        defaults.DownscaleQuality,
                        saved.SettingsVersion < BalancedDownscaleQualitySettingsVersion),
                    AutoTrackingEnabled = saved.Yolo5AutoTrackingEnabled ?? (useLegacyActiveProfile ? (bool?)saved.AutoTrackingEnabled : null) ?? defaults.AutoTrackingEnabled,
                    AutoDetectEveryNFrames = Math.Max(1, saved.Yolo5AutoDetectEveryNFrames ?? (useLegacyActiveProfile ? (int?)saved.AutoDetectEveryNFrames : null) ?? defaults.AutoDetectEveryNFrames),
                    ParallelSessionCount = Math.Max(1, saved.Yolo5ParallelSessionCount ?? (useLegacyActiveProfile ? (int?)saved.ParallelSessionCount : null) ?? defaults.ParallelSessionCount)
                };
            }

            return new YoloProfileState
            {
                ModelPath = NormalizeYoloModelPath(saved.YoloV8ModelPath) ?? legacyModelPath ?? defaults.ModelPath,
                ObjectnessThreshold = saved.YoloV8ObjectnessThreshold ?? (useLegacyActiveProfile ? saved.YoloObjectnessThreshold : null) ?? defaults.ObjectnessThreshold,
                ConfidenceThreshold = saved.YoloV8ConfidenceThreshold ?? (useLegacyActiveProfile ? saved.YoloConfidenceThreshold : null) ?? defaults.ConfidenceThreshold,
                NmsThreshold = saved.YoloV8NmsThreshold ?? (useLegacyActiveProfile ? saved.YoloNmsThreshold : null) ?? defaults.NmsThreshold,
                InputSize = Math.Clamp(saved.YoloV8InputSize ?? (useLegacyActiveProfile ? saved.YoloInputSize : null) ?? defaults.InputSize, 64, 2048),
                UseTiling = saved.YoloV8UseTiling ?? (useLegacyActiveProfile ? (bool?)saved.YoloUseTiling : null) ?? defaults.UseTiling,
                TileOnly = saved.YoloV8TileOnly ?? (useLegacyActiveProfile ? (bool?)saved.YoloTileOnly : null) ?? defaults.TileOnly,
                TileColumns = Math.Clamp(saved.YoloV8TileColumns ?? (useLegacyActiveProfile ? saved.YoloTileColumns : null) ?? defaults.TileColumns, 1, 8),
                TileRows = Math.Clamp(saved.YoloV8TileRows ?? (useLegacyActiveProfile ? saved.YoloTileRows : null) ?? defaults.TileRows, 1, 8),
                TileOverlapRatio = Math.Clamp(saved.YoloV8TileOverlapRatio ?? (useLegacyActiveProfile ? saved.YoloTileOverlapRatio : null) ?? defaults.TileOverlapRatio, 0.0, 0.45),
                DownscaleRatio = ResolveSavedYoloDownscaleRatio(saved.YoloV8DownscaleRatio, useLegacyActiveProfile ? saved.DownscaleRatio : null, defaults.DownscaleRatio),
                DownscaleQuality = ResolveSavedYoloDownscaleQuality(
                    saved.YoloV8DownscaleQuality,
                    useLegacyActiveProfile ? saved.DownscaleQuality : null,
                    defaults.DownscaleQuality,
                    saved.SettingsVersion < BalancedDownscaleQualitySettingsVersion),
                AutoTrackingEnabled = saved.YoloV8AutoTrackingEnabled ?? (useLegacyActiveProfile ? (bool?)saved.AutoTrackingEnabled : null) ?? defaults.AutoTrackingEnabled,
                AutoDetectEveryNFrames = Math.Max(1, saved.YoloV8AutoDetectEveryNFrames ?? (useLegacyActiveProfile ? (int?)saved.AutoDetectEveryNFrames : null) ?? defaults.AutoDetectEveryNFrames),
                ParallelSessionCount = Math.Max(1, saved.YoloV8ParallelSessionCount ?? (useLegacyActiveProfile ? (int?)saved.ParallelSessionCount : null) ?? defaults.ParallelSessionCount)
            };
        }

        private static double ResolveSavedYoloDownscaleRatio(double? savedValue, double? legacyValue, double defaultValue)
        {
            double value = savedValue ?? legacyValue ?? defaultValue;
            return value is 1.0 or 0.75 or 0.5 or 0.33 ? value : defaultValue;
        }

        private static DownscaleQuality ResolveSavedYoloDownscaleQuality(
            int? savedValue,
            int? legacyValue,
            DownscaleQuality defaultValue,
            bool requiresQualityMigration)
        {
            int value = savedValue ?? legacyValue ?? (int)defaultValue;
            return Enum.IsDefined(typeof(DownscaleQuality), value)
                ? ResolveSavedDownscaleQuality(value, requiresQualityMigration)
                : defaultValue;
        }

        private static DownscaleQuality ResolveSavedDownscaleQuality(int savedValue, bool requiresQualityMigration)
        {
            if (!Enum.IsDefined(typeof(DownscaleQuality), savedValue))
                return DownscaleQuality.BalancedBilinear;

            var quality = (DownscaleQuality)savedValue;
            // v9 이하는 0이 과거 기본값인지 명시적 선택인지 구분할 수 없어 품질 우선 기본값으로 한 번 승격합니다.
            return requiresQualityMigration && quality == DownscaleQuality.FastNearest
                ? DownscaleQuality.BalancedBilinear
                : quality;
        }

        private static AutoMaskProcessingMode ResolveSavedAutoProcessingMode(
            int settingsVersion,
            int? savedValue)
        {
            int value = savedValue ?? (int)AutoMaskProcessingMode.Tracked;
            if (settingsVersion < CurrentAutoSettingsVersion && (value == (int)AutoMaskProcessingMode.Legacy || value == (int)AutoMaskProcessingMode.Raw))
                value = (int)AutoMaskProcessingMode.Tracked;

            return Enum.IsDefined(typeof(AutoMaskProcessingMode), value)
                ? (AutoMaskProcessingMode)value
                : AutoMaskProcessingMode.Tracked;
        }

        private void ApplySavedAutoSettings()
        {
            var saved = _stateStore.GetAutoSettings();
            if (saved == null)
                return;

            bool isLegacyAutoSettings = saved.SettingsVersion < 4;
            bool requiresSettingsUpgrade = saved.SettingsVersion < CurrentAutoSettingsVersion;
            bool requiresQualityMigration = saved.SettingsVersion < BalancedDownscaleQualitySettingsVersion;
            var yoloType = YoloModelTypeOptions.FirstOrDefault(o => (int)o.ModelType == saved.YoloModelType);
            var selectedYoloModelType = yoloType?.ModelType ?? YoloFaceModelType.Yolo5Face;

            _isApplyingAutoSettings = true;
            try
            {
                var downscale = DownscaleOptions.FirstOrDefault(o => Math.Abs(o.Ratio - saved.DownscaleRatio) < 0.0001);
                if (!isLegacyAutoSettings && downscale != null)
                    SelectedDownscaleOption = downscale;

                var savedQuality = ResolveSavedDownscaleQuality(saved.DownscaleQuality, requiresQualityMigration);
                var quality = DownscaleQualityOptions.FirstOrDefault(o => o.Quality == savedQuality);
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
                AutoYoloEnableCoreMl = saved.AutoYoloEnableCoreMl;
                AutoExportAfter = saved.AutoExportAfter;
                if (saved.DetectionThreshold.HasValue)
                    AutoDetectionThreshold = saved.DetectionThreshold.Value;
                if (saved.ConfidenceThreshold.HasValue)
                    AutoConfidenceThreshold = saved.ConfidenceThreshold.Value;
                if (saved.NmsThreshold.HasValue)
                    AutoNmsThreshold = saved.NmsThreshold.Value;
                if (saved.BlurRadius.HasValue)
                    BlurRadius = Math.Clamp(saved.BlurRadius.Value, MinBlurRadiusValue, MaxBlurRadiusValue);
                var backend = AutoDetectorBackendOptions.FirstOrDefault(o => (int)o.Backend == saved.DetectorBackend);
                if (backend != null)
                    SelectedAutoDetectorBackendOption = backend;
                var savedProcessingMode = ResolveSavedAutoProcessingMode(
                    saved.SettingsVersion,
                    saved.ProcessingMode);
                SelectedAutoProcessingModeOption = AutoProcessingModeOptions.FirstOrDefault(
                    o => o.Mode == savedProcessingMode) ?? AutoProcessingModeOptions[0];

                EnablePostProcessing = requiresSettingsUpgrade ? false : saved.EnablePostProcessing;
                EnableRoiPostProcess = requiresSettingsUpgrade ? false : saved.EnableRoiPostProcess;
                EnableYoloWeakIsolatedCleanup = requiresSettingsUpgrade ? false : saved.EnableYoloWeakIsolatedCleanup;
                EnableYoloGapFill = requiresSettingsUpgrade ? false : saved.EnableYoloGapFill;
                EnableYoloSceneCutCarryCleanup = requiresSettingsUpgrade ? false : saved.EnableYoloSceneCutCarryCleanup;
                EnableYoloTemporalSmoothing = requiresSettingsUpgrade ? false : saved.EnableYoloTemporalSmoothing;
                EnableYoloRiskCascade = requiresSettingsUpgrade ? false : saved.EnableYoloRiskCascade;

                _yoloV8Profile = ReadSavedYoloProfile(saved, YoloFaceModelType.YoloV8Face, selectedYoloModelType);
                _yolo5Profile = ReadSavedYoloProfile(saved, YoloFaceModelType.Yolo5Face, selectedYoloModelType);
                if (yoloType != null)
                    SelectedYoloModelTypeOption = yoloType;
                _activeYoloModelType = selectedYoloModelType;
                ApplyYoloProfile(GetYoloProfile(selectedYoloModelType));
            }
            finally
            {
                _isApplyingAutoSettings = false;
            }

            if (requiresSettingsUpgrade)
                PersistAutoSettings();
        }

        private void PersistAutoSettings()
        {
            if (_isApplyingAutoSettings || _isApplyingYoloProfile)
                return;

            var activeYoloModelType = SelectedYoloModelTypeOption?.ModelType ?? _activeYoloModelType;
            var yoloV8Profile = activeYoloModelType == YoloFaceModelType.YoloV8Face
                ? CaptureCurrentYoloProfile()
                : _yoloV8Profile;
            var yolo5Profile = activeYoloModelType == YoloFaceModelType.Yolo5Face
                ? CaptureCurrentYoloProfile()
                : _yolo5Profile;
            var activeYoloProfile = activeYoloModelType == YoloFaceModelType.Yolo5Face
                ? yolo5Profile
                : yoloV8Profile;

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
                AutoYoloEnableCoreMl = AutoYoloEnableCoreMl,
                OrtThreads = SelectedOrtThreadOption?.Threads,
                AutoExportAfter = AutoExportAfter,
                DetectionThreshold = AutoDetectionThreshold,
                ConfidenceThreshold = AutoConfidenceThreshold,
                NmsThreshold = AutoNmsThreshold,
                BlurRadius = BlurRadius,
                DetectorBackend = (int)(SelectedAutoDetectorBackendOption?.Backend ?? FaceDetectorBackend.FaceOnnx),
                ProcessingMode = (int)(SelectedAutoProcessingModeOption?.Mode ?? AutoMaskProcessingMode.Tracked),
                YoloModelType = (int)activeYoloModelType,
                YoloModelPath = activeYoloProfile.ModelPath,
                YoloObjectnessThreshold = activeYoloProfile.ObjectnessThreshold,
                YoloConfidenceThreshold = activeYoloProfile.ConfidenceThreshold,
                YoloNmsThreshold = activeYoloProfile.NmsThreshold,
                YoloInputSize = activeYoloProfile.InputSize,
                YoloUseTiling = activeYoloProfile.UseTiling,
                YoloTileOnly = activeYoloProfile.TileOnly,
                YoloTileColumns = activeYoloProfile.TileColumns,
                YoloTileRows = activeYoloProfile.TileRows,
                YoloTileOverlapRatio = activeYoloProfile.TileOverlapRatio,
                YoloV8ModelPath = yoloV8Profile.ModelPath,
                YoloV8ObjectnessThreshold = yoloV8Profile.ObjectnessThreshold,
                YoloV8ConfidenceThreshold = yoloV8Profile.ConfidenceThreshold,
                YoloV8NmsThreshold = yoloV8Profile.NmsThreshold,
                YoloV8InputSize = yoloV8Profile.InputSize,
                YoloV8UseTiling = yoloV8Profile.UseTiling,
                YoloV8TileOnly = yoloV8Profile.TileOnly,
                YoloV8TileColumns = yoloV8Profile.TileColumns,
                YoloV8TileRows = yoloV8Profile.TileRows,
                YoloV8TileOverlapRatio = yoloV8Profile.TileOverlapRatio,
                YoloV8DownscaleRatio = yoloV8Profile.DownscaleRatio,
                YoloV8DownscaleQuality = (int)yoloV8Profile.DownscaleQuality,
                YoloV8AutoTrackingEnabled = yoloV8Profile.AutoTrackingEnabled,
                YoloV8AutoDetectEveryNFrames = yoloV8Profile.AutoDetectEveryNFrames,
                YoloV8ParallelSessionCount = yoloV8Profile.ParallelSessionCount,
                Yolo5ModelPath = yolo5Profile.ModelPath,
                Yolo5ObjectnessThreshold = yolo5Profile.ObjectnessThreshold,
                Yolo5ConfidenceThreshold = yolo5Profile.ConfidenceThreshold,
                Yolo5NmsThreshold = yolo5Profile.NmsThreshold,
                Yolo5InputSize = yolo5Profile.InputSize,
                Yolo5UseTiling = yolo5Profile.UseTiling,
                Yolo5TileOnly = yolo5Profile.TileOnly,
                Yolo5TileColumns = yolo5Profile.TileColumns,
                Yolo5TileRows = yolo5Profile.TileRows,
                Yolo5TileOverlapRatio = yolo5Profile.TileOverlapRatio,
                Yolo5DownscaleRatio = yolo5Profile.DownscaleRatio,
                Yolo5DownscaleQuality = (int)yolo5Profile.DownscaleQuality,
                Yolo5AutoTrackingEnabled = yolo5Profile.AutoTrackingEnabled,
                Yolo5AutoDetectEveryNFrames = yolo5Profile.AutoDetectEveryNFrames,
                Yolo5ParallelSessionCount = yolo5Profile.ParallelSessionCount,
                EnablePostProcessing = EnablePostProcessing,
                EnableRoiPostProcess = EnableRoiPostProcess,
                EnableYoloWeakIsolatedCleanup = EnableYoloWeakIsolatedCleanup,
                EnableYoloGapFill = EnableYoloGapFill,
                EnableYoloSceneCutCarryCleanup = EnableYoloSceneCutCarryCleanup,
                EnableYoloTemporalSmoothing = EnableYoloTemporalSmoothing,
                EnableYoloRiskCascade = EnableYoloRiskCascade
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

        public async Task PickYoloModelAsync(IStorageProvider storageProvider)
        {
            var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "YOLO ONNX 모델 선택",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("ONNX")
                    {
                        Patterns = ["*.onnx"]
                    }
                ]
            });

            var file = files.Count > 0 ? files[0] : null;
            if (file is null)
                return;

            var localPath = file.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(localPath))
                return;

            AutoYoloModelPath = localPath;
        }

        [RelayCommand(CanExecute = nameof(CanDownloadYoloModel))]
        private async Task DownloadYoloModelAsync()
        {
            var modelType = SelectedYoloModelTypeOption?.ModelType ?? YoloFaceModelType.Yolo5Face;
            var downloadInfo = GetYoloModelDownloadInfo(modelType);
            var downloadDirectory = GetYoloModelDownloadDirectory();
            Directory.CreateDirectory(downloadDirectory);

            var destinationPath = Path.Combine(downloadDirectory, downloadInfo.FileName);
            if (File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 0)
            {
                AutoYoloModelPath = destinationPath;
                YoloModelDownloadProgress = 100;
                YoloModelDownloadStatus = $"이미 다운로드됨: {downloadInfo.FileName}";
                return;
            }

            var tempPath = destinationPath + ".download";
            IsYoloModelDownloading = true;
            YoloModelDownloadProgress = 0;
            YoloModelDownloadStatus = $"다운로드 시작: {downloadInfo.SourceLabel} ({downloadInfo.LicenseLabel})";

            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);

                using var request = new HttpRequestMessage(HttpMethod.Get, downloadInfo.DownloadUrl);
                request.Headers.UserAgent.ParseAdd("FaceShield/1.0");
                using var response = await YoloModelDownloadHttpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    CancellationToken.None);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength;
                await using var source = await response.Content.ReadAsStreamAsync(CancellationToken.None);
                await using var destination = new FileStream(
                    tempPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 1024 * 128,
                    useAsync: true);

                var buffer = new byte[1024 * 128];
                long readBytes = 0;
                while (true)
                {
                    int read = await source.ReadAsync(buffer, CancellationToken.None);
                    if (read <= 0)
                        break;

                    await destination.WriteAsync(buffer.AsMemory(0, read), CancellationToken.None);
                    readBytes += read;
                    if (totalBytes.HasValue && totalBytes.Value > 0)
                        YoloModelDownloadProgress = Math.Clamp((int)Math.Round(readBytes * 100.0 / totalBytes.Value), 0, 99);
                }

                await destination.FlushAsync(CancellationToken.None);
                destination.Close();

                File.Move(tempPath, destinationPath, overwrite: true);
                AutoYoloModelPath = destinationPath;
                YoloModelDownloadProgress = 100;
                YoloModelDownloadStatus = $"다운로드 완료: {downloadInfo.FileName}";
            }
            catch (Exception ex)
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);

                YoloModelDownloadStatus = $"다운로드 실패: {ex.Message}";
            }
            finally
            {
                IsYoloModelDownloading = false;
            }
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
                var detectorFactoryOptions = BuildDetectorFactoryOptions();
                TouchRecent(SelectedVideoPath);

                vm = await Task.Run(
                    () => GetOrCreateWorkspace(
                        WorkspaceMode.Manual,
                        progress,
                        autoOptions,
                        detectorFactoryOptions));
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
                EnsureSelectedDetectorReady();
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
                var detectorFactoryOptions = BuildDetectorFactoryOptions();
                TouchRecent(SelectedVideoPath);

                vm = await Task.Run(
                    () => GetOrCreateWorkspace(
                        WorkspaceMode.Auto,
                        loadProgress: null,
                        autoOptions,
                        detectorFactoryOptions));
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
            var accel = IsYoloDetectorSelected
                ? YoloFaceOnnxDetector.GetLastExecutionProviderLabel()
                : FaceOnnxDetector.GetLastExecutionProviderLabel();
            var accelError = IsYoloDetectorSelected
                ? YoloFaceOnnxDetector.GetLastExecutionProviderError()
                : FaceOnnxDetector.GetLastExecutionProviderError();
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
            var processingMode = SelectedAutoProcessingModeOption?.Mode ?? AutoMaskProcessingMode.Tracked;
            bool useTracking = processingMode switch
            {
                AutoMaskProcessingMode.Raw => false,
                AutoMaskProcessingMode.Tracked or AutoMaskProcessingMode.Full => true,
                _ => AutoTrackingEnabled
            };
            int detectEvery = useTracking ? Math.Max(1, AutoDetectEveryNFrames) : 1;
            var quality = SelectedDownscaleQualityOption?.Quality ?? DownscaleQuality.BalancedBilinear;

            return new AutoMaskOptions
            {
                ProcessingMode = processingMode,
                DownscaleRatio = ratio,
                DownscaleQuality = quality,
                EnablePostProcessing = EnablePostProcessing,
                EnableRoiPostProcess = EnableRoiPostProcess,
                EnableYoloWeakIsolatedCleanup = EnableYoloWeakIsolatedCleanup,
                EnableYoloGapFill = EnableYoloGapFill,
                EnableYoloSceneCutCarryCleanup = EnableYoloSceneCutCarryCleanup,
                EnableYoloTemporalSmoothing = EnableYoloTemporalSmoothing,
                EnableYoloRiskCascade = IsYoloDetectorSelected && EnableYoloRiskCascade && detectEvery <= 1,
                UseTracking = useTracking,
                DetectEveryNFrames = detectEvery,
                ParallelDetectorCount = Math.Max(1, SelectedParallelSessionCount),
                FilterProfile = IsYoloDetectorSelected ? FaceFilterProfile.Yolo : FaceFilterProfile.FaceOnnx
            };
        }

        private FaceDetectorFactoryOptions BuildDetectorFactoryOptions()
        {
            var faceOnnxOptions = BuildDetectorOptions();
            if (SelectedAutoDetectorBackendOption?.Backend == FaceDetectorBackend.YoloFaceOnnx)
            {
                var yoloModelType = SelectedYoloModelTypeOption?.ModelType ?? YoloFaceModelType.YoloV8Face;
                var yoloModelPath = ResolveSelectedYoloModelPath(yoloModelType);
                return FaceDetectorFactoryOptions.ForYoloFaceOnnx(
                    new YoloFaceOnnxDetectorOptions
                    {
                        ModelPath = yoloModelPath,
                        ModelType = yoloModelType,
                        UseOrtOptimization = AutoUseOrtOptimization,
                        UseGpu = AutoUseGpu,
                        EnableCoreMl = AutoYoloEnableCoreMl,
                        IntraOpNumThreads = SelectedOrtThreadOption?.Threads,
                        InterOpNumThreads = null,
                        ObjectnessThreshold = (float)Math.Clamp(AutoYoloObjectnessThreshold, 0.01, 0.99),
                        ConfidenceThreshold = (float)Math.Clamp(AutoYoloConfidenceThreshold, 0.01, 0.99),
                        NmsThreshold = (float)Math.Clamp(AutoYoloNmsThreshold, 0.01, 0.99),
                        UseAspectRatioFilter = true,
                        MinAspectRatio = 0.35,
                        MaxAspectRatio = 1.65,
                        UseTopSmallLowConfidenceFilter = true,
                        TopSmallLowConfidenceMaxCenterYRatio = 0.07,
                        TopSmallLowConfidenceMaxAreaRatio = 0.0045,
                        TopSmallLowConfidenceMaxConfidence = 0.55f,
                        InputWidth = AutoYoloInputSize > 0 ? AutoYoloInputSize : null,
                        InputHeight = AutoYoloInputSize > 0 ? AutoYoloInputSize : null,
                        UseTiling = AutoYoloUseTiling,
                        IncludeFullFrameWhenTiling = !AutoYoloTileOnly,
                        TileColumns = Math.Clamp(AutoYoloTileColumns, 1, 8),
                        TileRows = Math.Clamp(AutoYoloTileRows, 1, 8),
                        TileOverlapRatio = Math.Clamp(AutoYoloTileOverlapRatio, 0.0, 0.45)
                    });
            }

            return FaceDetectorFactoryOptions.ForOnnx(faceOnnxOptions);
        }

        private string? ResolveSelectedYoloModelPath(YoloFaceModelType modelType)
        {
            return NormalizeYoloModelPath(AutoYoloModelPath) ?? ResolveDefaultYoloModelPath(modelType);
        }

        private static string? ResolveDefaultYoloModelPath(YoloFaceModelType modelType)
        {
            var fileNames = modelType == YoloFaceModelType.Yolo5Face
                ? DefaultYolo5FaceModelFileNames
                : DefaultYoloV8FaceModelFileNames;

            foreach (var directory in EnumerateDefaultYoloModelDirectories())
            {
                foreach (var fileName in fileNames)
                {
                    var path = Path.Combine(directory, fileName);
                    if (File.Exists(path))
                        return path;
                }
            }

            return null;
        }

        private static IEnumerable<string> EnumerateDefaultYoloModelDirectories()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var downloadDirectory = GetYoloModelDownloadDirectory();
            if (seen.Add(downloadDirectory))
                yield return downloadDirectory;

            foreach (var root in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            {
                var current = root;
                for (int i = 0; i < 6 && !string.IsNullOrWhiteSpace(current); i++)
                {
                    var candidate = Path.Combine(current, DefaultYoloModelDirectory);
                    if (seen.Add(candidate))
                        yield return candidate;

                    current = Directory.GetParent(current)?.FullName;
                }
            }
        }

        private static string GetYoloModelDownloadDirectory()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var root = string.IsNullOrWhiteSpace(localAppData)
                ? Path.Combine(Path.GetTempPath(), "FaceShield")
                : Path.Combine(localAppData, "FaceShield");

            return Path.Combine(root, "Models", "Yolo");
        }

        private static YoloModelDownloadInfo GetYoloModelDownloadInfo(YoloFaceModelType modelType)
        {
            if (modelType == YoloFaceModelType.Yolo5Face)
            {
                return new YoloModelDownloadInfo(
                    "YoloV5Face.onnx",
                    "https://huggingface.co/hayashiLin/deepfacelivemodels/resolve/main/YoloV5Face.onnx?download=true",
                    "Hugging Face hayashiLin/deepfacelivemodels",
                    "GPL-3.0 표시");
            }

            return new YoloModelDownloadInfo(
                "yolov8n-face-lindevs.onnx",
                "https://github.com/lindevs/yolov8-face/releases/download/1.0.1/yolov8n-face-lindevs.onnx",
                "GitHub lindevs/yolov8-face 1.0.1",
                "MIT 표시 + YOLOv8 upstream license caveat");
        }

        private static string? NormalizeYoloModelPath(string? modelPath)
        {
            return string.IsNullOrWhiteSpace(modelPath) ? null : modelPath;
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

        private void EnsureSelectedDetectorReady()
        {
            if (SelectedAutoDetectorBackendOption?.Backend == FaceDetectorBackend.YoloFaceOnnx)
            {
                var yoloModelType = SelectedYoloModelTypeOption?.ModelType ?? YoloFaceModelType.YoloV8Face;
                var yoloModelPath = ResolveSelectedYoloModelPath(yoloModelType);
                if (string.IsNullOrWhiteSpace(yoloModelPath))
                    throw new InvalidOperationException($"YOLO ONNX 모델 파일을 선택하거나 솔루션의 {DefaultYoloModelDirectory} 폴더에 기본 파일명을 넣어야 합니다.");
                if (!File.Exists(yoloModelPath))
                    throw new FileNotFoundException("YOLO ONNX 모델 파일을 찾지 못했습니다.", yoloModelPath);
                if (string.IsNullOrWhiteSpace(AutoYoloModelPath))
                    AutoYoloModelPath = yoloModelPath;
                return;
            }

            FaceOnnxDetector.EnsureRuntimeAvailable();
        }

        private WorkspaceViewModel GetOrCreateWorkspace(
            WorkspaceMode mode,
            IProgress<int>? loadProgress,
            AutoMaskOptions autoOptions,
            FaceDetectorFactoryOptions detectorFactoryOptions)
        {
            if (string.IsNullOrWhiteSpace(SelectedVideoPath))
                throw new InvalidOperationException("SelectedVideoPath is empty.");

            string key = $"{mode}:{SelectedVideoPath}";
            if (_workspaceCache.TryGetValue(key, out var cached))
            {
                cached.UpdateAutoOptions(autoOptions);
                cached.UpdateDetectorFactoryOptions(detectorFactoryOptions);
                cached.ToolPanel.BlurRadius = BlurRadius;
                return cached;
            }

            var vm = new WorkspaceViewModel(
                SelectedVideoPath,
                mode,
                loadProgress,
                _onBackHome,
                autoOptions,
                detectorFactoryOptions.FaceOnnxOptions ?? new FaceOnnxDetectorOptions(),
                _stateStore,
                detectorFactoryOptions: detectorFactoryOptions,
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
            OnPropertyChanged(nameof(HasRecents));
            OnPropertyChanged(nameof(HasNoRecents));
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
