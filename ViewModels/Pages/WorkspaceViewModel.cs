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
    public partial class WorkspaceViewModel : ViewModelBase, IDisposable
    {
        public ToolPanelViewModel ToolPanel { get; } = new();
        public FramePreviewViewModel FramePreview { get; }
        public FrameListViewModel FrameList { get; }
        private readonly Action? _onBack;
        private AutoMaskOptions _autoOptions;
        private FaceOnnxDetectorOptions _detectorOptions;
        private FaceOnnxDetectorOptions? _effectiveDetectorOptions;
        private readonly WorkspaceStateStore? _stateStore;
        private int[] _autoAnomalies = Array.Empty<int>();
        private const float LowConfidenceMargin = 0.05f;
        private const float TemporalConfidenceStrong = 0.7f;
        private const float TemporalConfidenceWeak = 0.5f;
        private const double TemporalIouMin = 0.2;
        private const double TemporalMaxCenterShiftRatio = 0.5;
        private const double TemporalMaxAreaChangeRatio = 3.0;
        private const int TemporalMinRunLength = 2;
        private const int AutoCheckpointFrameInterval = 120;
        private static readonly TimeSpan AutoCheckpointInterval = TimeSpan.FromSeconds(5);
        private int _autoResumeIndex;
        private bool _autoCompleted;
        private int _autoLastProcessedFrame = -1;
        private DateTime _autoLastProcessedAtUtc = DateTime.MinValue;
        private int _autoLastCheckpointFrame = -1;
        private DateTime _autoLastCheckpointAtUtc = DateTime.MinValue;
        private readonly object _checkpointSync = new();
        private Task? _checkpointTask;
        private bool _checkpointPending;
        private bool _checkpointPendingIncludePreview;
        private readonly object _stateSaveLock = new();
        private bool _sessionInitialized;
        private readonly Queue<(DateTime Timestamp, int FrameIndex)> _exportEtaSamples = new();
        private (DateTime Timestamp, int FrameIndex) _exportLastSample;
        private readonly ObservableCollection<IssueEntryViewModel> _noFaceIssueEntries = new();
        private readonly ObservableCollection<IssueEntryViewModel> _lowConfidenceIssueEntries = new();
        private readonly ObservableCollection<IssueEntryViewModel> _flickerIssueEntries = new();
        private HashSet<int> _noFaceIssueSet = new();
        private HashSet<int> _lowConfidenceIssueSet = new();
        private HashSet<int> _flickerIssueSet = new();
        private bool _disposed;
        private bool _useProxyStage;
        private IProgress<int>? _autoProgressReporter;
        private bool _isProxyGenerating;
        private int _proxyFrameIndex;
        private int _proxyTotalFrames;
        private string? _proxyEtaText;
        private string? _proxyDecisionText;
        private string? _autoTuneDecisionText;
        private readonly Queue<(DateTime Timestamp, int FrameIndex)> _proxyEtaSamples = new();
        private (DateTime Timestamp, int FrameIndex) _proxyLastSample;

        // 프레임별 최종 마스크 저장소
        private readonly FrameMaskProvider _maskProvider = new();

        public bool IsProxyGenerating => _isProxyGenerating;

        public string ProxyStatusText
        {
            get
            {
                if (!_isProxyGenerating)
                    return string.Empty;

                if (_proxyTotalFrames > 0)
                {
                    int percent = (int)Math.Round(_proxyFrameIndex * 100.0 / Math.Max(1, _proxyTotalFrames));
                    if (percent > 100) percent = 100;
                    return $"1/2 프록시 생성중: {_proxyFrameIndex}/{_proxyTotalFrames} ({percent}%)";
                }

                return "1/2 프록시 생성중...";
            }
        }

        public string? ProxyEtaText => _proxyEtaText;
        public string? ProxyDecisionText => _proxyDecisionText;
        public string? AutoTuneDecisionText => _autoTuneDecisionText;

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
            bool deferSessionInit = false)
        {
            Mode = mode;
            _onBack = onBack;
            _autoOptions = autoOptions ?? new AutoMaskOptions();
            _detectorOptions = detectorOptions ?? new FaceOnnxDetectorOptions();
            _stateStore = stateStore;
            FrameList = new FrameListViewModel(videoPath);
            FramePreview = new FramePreviewViewModel(ToolPanel, _maskProvider);
            if (!deferSessionInit)
                InitializeSession(loadProgress);

            // 🔹 자동/최종 마스크 provider 주입
            FramePreview.SetMaskProvider(_maskProvider);

            FrameList.SelectedFrameIndexChanged += index =>
            {
                FramePreview.OnFrameIndexChanged(index);
            };
            FrameList.PlaybackStopped += () =>
            {
                if (FrameList.SelectedFrameIndex >= 0)
                    FramePreview.OnPlaybackStopped(FrameList.SelectedFrameIndex);
            };
            FrameList.PlaybackStateChanged += isPlaying =>
            {
                FramePreview.SetPlaying(isPlaying);
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


        private async Task SaveVideoAsync(
            IProgress<ExportProgress>? exportProgress = null,
            CancellationToken cancellationToken = default)
        {
            string input = FrameList.VideoPath;
            string output = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(input)!,
                System.IO.Path.GetFileNameWithoutExtension(input) + "_blur.mp4");

            output = await ResolveExportOutputPathAsync(output);
            if (string.IsNullOrWhiteSpace(output))
                return;

            var exporter = new VideoExportService(_maskProvider);

            ToolPanel.IsExportRunning = true;
            ToolPanel.ExportProgress = 0;
            ToolPanel.ExportEtaText = "예상 남은 시간 계산 중...";
            ToolPanel.ExportStatusText = null;
            _exportEtaSamples.Clear();
            exportProgress?.Report(new ExportProgress(0, 0, "내보내기 시작..."));

            var progress = new Progress<ExportProgress>(p =>
            {
                exportProgress?.Report(p);
                int percent = Math.Clamp(p.Percent, 0, 100);
                ToolPanel.ExportProgress = percent;
                UpdateExportEta(DateTime.UtcNow, p.FrameIndex, p.TotalFrames);
                if (!string.IsNullOrWhiteSpace(p.StatusMessage))
                    ToolPanel.ExportStatusText = p.StatusMessage;
            });

            try
            {
                _exportCts?.Dispose();
                _exportCts = cancellationToken.CanBeCanceled
                    ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                    : new CancellationTokenSource();

                await Task.Run(() =>
                {
                    exporter.Export(input, output, blurRadius: ToolPanel.BlurRadius, progress, _exportCts.Token);
                }, _exportCts.Token);
            }
            catch (OperationCanceledException)
            {
                // 사용자 취소는 정상 흐름
            }
            finally
            {
                ToolPanel.IsExportRunning = false;
                ToolPanel.ExportProgress = 0;
                ToolPanel.ExportEtaText = null;
                ToolPanel.ExportStatusText = null;
                _exportCts?.Dispose();
                _exportCts = null;
            }
        }

        public Task ExportAutoResultAsync(
            IProgress<ExportProgress>? exportProgress = null,
            CancellationToken cancellationToken = default)
        {
            return SaveVideoAsync(exportProgress, cancellationToken);
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

            ToolPanel.IsAutoRunning = true;
            ToolPanel.AutoProgress = 0;

            return RunAutoCoreAsync(exportAfter, progress, exportProgress);
        }

        private async Task<bool> RunAutoCoreAsync(
            bool exportAfter,
            IProgress<int>? progress,
            IProgress<ExportProgress>? exportProgress)
        {
            try
            {
                FaceOnnxDetectorOptions detectorOptions = _detectorOptions;
                int tunedSessions = Math.Max(1, _autoOptions.ParallelDetectorCount);
                _autoTuneDecisionText = null;
                if (_detectorOptions.AllowAutoTune == true)
                {
                    bool allowGpuAuto = _detectorOptions.AllowAutoGpu == true;
                    double tuneRatio = _autoOptions.EnableHybridRefine
                        ? _autoOptions.HybridDownscaleRatio
                        : _autoOptions.DownscaleRatio;
                    if (DetectorAutoTuner.TryTune(
                            FrameList.VideoPath,
                            tuneRatio,
                            _autoOptions.DownscaleQuality,
                            _detectorOptions,
                            tunedSessions,
                            allowGpuAuto,
                            out var tunedOptions,
                            out var tunedCount,
                            out var tuneLabel))
                    {
                        detectorOptions = tunedOptions;
                        tunedSessions = Math.Max(1, tunedCount);
                        _autoTuneDecisionText = string.IsNullOrWhiteSpace(tuneLabel)
                            ? "튜닝: 적용됨"
                            : $"튜닝: {tuneLabel}";
                    }
                    else
                    {
                        _autoTuneDecisionText = "튜닝: 기본값 사용";
                    }
                }

                _effectiveDetectorOptions = detectorOptions;
                using IFaceDetector detector = new FaceOnnxDetector(detectorOptions);
                AutoMaskOptions runOptions = new AutoMaskOptions
                {
                    DownscaleRatio = _autoOptions.DownscaleRatio,
                    DownscaleQuality = _autoOptions.DownscaleQuality,
                    UseTracking = _autoOptions.UseTracking,
                    DetectEveryNFrames = _autoOptions.DetectEveryNFrames,
                    ParallelDetectorCount = tunedSessions,
                    EnableHybridRefine = _autoOptions.EnableHybridRefine,
                    HybridDownscaleRatio = _autoOptions.HybridDownscaleRatio,
                    HybridRefineInterval = _autoOptions.HybridRefineInterval,
                    HybridSmallFaceAreaRatio = _autoOptions.HybridSmallFaceAreaRatio,
                    ProxyPreset = _autoOptions.ProxyPreset
                };
                string detectionPath = FrameList.VideoPath;
                _autoProgressReporter = progress;
                _useProxyStage = false;
                _proxyDecisionText = null;
                if (_autoOptions.EnableHybridRefine)
                {
                    var proxyToken = _autoCts?.Token ?? CancellationToken.None;
                    _isProxyGenerating = true;
                    _proxyFrameIndex = 0;
                    _proxyTotalFrames = 0;
                    _proxyEtaText = "예상 남은 시간 계산 중...";
                    _proxyEtaSamples.Clear();
                    _proxyLastSample = default;
                    var proxyProgress = new Progress<ProxyProgress>(UpdateProxyProgress);

                    (string? path, string? status) proxyResult;
                    try
                    {
                        proxyResult = await Task.Run(() =>
                        {
                            string? status = null;
                            string? path = DetectionProxyService.EnsureDetectionProxy(
                                FrameList.VideoPath,
                                _autoOptions.ProxyPreset,
                                proxyProgress,
                                out status,
                                proxyToken);
                            return (path, status);
                        }, proxyToken);
                    }
                    finally
                    {
                        _isProxyGenerating = false;
                        _proxyEtaText = null;
                    }

                    if (!string.IsNullOrWhiteSpace(proxyResult.path))
                        detectionPath = proxyResult.path!;
                    _proxyDecisionText = string.IsNullOrWhiteSpace(proxyResult.status)
                        ? "프록시 결과: 알 수 없음"
                        : proxyResult.status;
                    _useProxyStage = !string.Equals(detectionPath, FrameList.VideoPath, StringComparison.OrdinalIgnoreCase);
                    if (_useProxyStage)
                        ReportAutoProgressScaled(50);
                }
                else
                {
                    _proxyDecisionText = BuildProxySkipReason(_autoOptions);
                }
                if (_autoOptions.EnableHybridRefine)
                {
                    double hybridRatio = Math.Clamp(_autoOptions.HybridDownscaleRatio, 0.2, 0.95);
                    runOptions = new AutoMaskOptions
                    {
                        DownscaleRatio = hybridRatio,
                        DownscaleQuality = _autoOptions.DownscaleQuality,
                        UseTracking = false,
                        DetectEveryNFrames = 1,
                        ParallelDetectorCount = tunedSessions,
                        EnableHybridRefine = _autoOptions.EnableHybridRefine,
                        HybridDownscaleRatio = hybridRatio,
                        HybridRefineInterval = _autoOptions.HybridRefineInterval,
                        HybridSmallFaceAreaRatio = _autoOptions.HybridSmallFaceAreaRatio,
                        ProxyPreset = _autoOptions.ProxyPreset
                    };
                }

                var generator = new AutoMaskGenerator(
                    detector,
                    _maskProvider,
                    runOptions,
                    detectorFactory: () => new FaceOnnxDetector(detectorOptions));
                _autoCompleted = false;
                int lastProcessed = Math.Max(0, _autoResumeIndex);
                _autoLastCheckpointFrame = _autoResumeIndex;
                _autoLastCheckpointAtUtc = DateTime.UtcNow;

                // TODO: 필요하면 IProgress<int>를 WorkspaceViewModel 프로퍼티로 노출해서
                //       진행률 UI를 그릴 수 있습니다.
                var effectiveProgress = new Progress<int>(p =>
                {
                    int scaled = _useProxyStage ? 50 + (int)Math.Round(p * 0.5) : p;
                    if (scaled < 0) scaled = 0;
                    if (scaled > 100) scaled = 100;
                    progress?.Report(scaled);
                    ToolPanel.AutoProgress = scaled;
                });
                var token = _autoCts?.Token ?? CancellationToken.None;
                await generator.GenerateAsync(
                    detectionPath,
                    effectiveProgress,
                    token,
                    startFrameIndex: lastProcessed,
                    onFrameProcessed: idx =>
                    {
                        lastProcessed = idx;
                        _autoResumeIndex = idx;
                        _autoLastProcessedFrame = idx;
                        _autoLastProcessedAtUtc = DateTime.UtcNow;
                        TryUpdateAutoPreview(idx);
                        MaybeCheckpointAuto(idx);
                    });

                if (token.IsCancellationRequested)
                {
                    _autoCompleted = false;
                    PersistWorkspaceState();
                    return false;
                }

                // 자동 마스크 생성 후, 현재 프레임 다시 렌더링 (마스크 반영)
                if (FrameList.SelectedFrameIndex >= 0)
                {
                    FramePreview.OnFrameIndexChanged(FrameList.SelectedFrameIndex);
                }

                ApplyAutoTemporalFixes();

                bool refined = await RefineAutoAnomaliesAsync(_autoCts?.Token ?? CancellationToken.None);
                if (refined)
                    ApplyAutoTemporalFixes();

                await BuildAutoAnomaliesAsync();

                if (exportAfter)
                    await SaveVideoAsync(exportProgress, _autoCts?.Token ?? CancellationToken.None);

                _autoCompleted = true;
                _autoResumeIndex = 0;
                PersistWorkspaceState();

                return true;
            }
            catch (OperationCanceledException)
            {
                _autoCompleted = false;
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

        private void MaybeCheckpointAuto(int frameIndex)
        {
            if (_stateStore == null)
                return;

            if (frameIndex <= 0)
                return;

            var now = DateTime.UtcNow;
            if (frameIndex - _autoLastCheckpointFrame < AutoCheckpointFrameInterval &&
                (now - _autoLastCheckpointAtUtc) < AutoCheckpointInterval)
                return;

            _autoLastCheckpointFrame = frameIndex;
            _autoLastCheckpointAtUtc = now;

            QueueCheckpoint(includePreviewMask: false);
        }

        private void QueueCheckpoint(bool includePreviewMask)
        {
            if (_stateStore == null)
                return;

            lock (_checkpointSync)
            {
                _checkpointPending = true;
                _checkpointPendingIncludePreview |= includePreviewMask;
                if (_checkpointTask == null || _checkpointTask.IsCompleted)
                    _checkpointTask = Task.Run(ProcessCheckpointQueue);
            }
        }

        private void ProcessCheckpointQueue()
        {
            while (true)
            {
                bool includePreview;
                lock (_checkpointSync)
                {
                    if (!_checkpointPending)
                    {
                        _checkpointTask = null;
                        return;
                    }
                    if (_disposed)
                    {
                        _checkpointPending = false;
                        _checkpointPendingIncludePreview = false;
                        _checkpointTask = null;
                        return;
                    }
                    _checkpointPending = false;
                    includePreview = _checkpointPendingIncludePreview;
                    _checkpointPendingIncludePreview = false;
                }

                if (_disposed)
                    return;

                try
                {
                    PersistWorkspaceStateInternal(includePreviewMask: includePreview);
                }
                catch
                {
                    // ignore checkpoint failures
                }
            }
        }

        private void ApplyAutoTemporalFixes()
        {
            if (_autoOptions.DetectEveryNFrames > 1 && !_autoOptions.UseTracking)
                return;

            int total = FrameList.TotalFrames;
            if (total < 3)
                return;

            var facesByFrame = new List<Rect>?[total];
            var confByFrame = new List<float>?[total];
            var sizeByFrame = new PixelSize[total];
            var hasStored = new bool[total];

            for (int i = 0; i < total; i++)
            {
                hasStored[i] = _maskProvider.TryGetStoredMask(i, out _);
                if (hasStored[i])
                    continue;

                if (_maskProvider.TryGetFaceMaskData(i, out var data) && data.Faces.Count > 0)
                {
                    facesByFrame[i] = new List<Rect>(data.Faces);
                    confByFrame[i] = new List<float>(data.Confidences);
                    sizeByFrame[i] = data.Size;
                }
            }

            for (int i = 1; i < total - 1; i++)
            {
                if (hasStored[i] || facesByFrame[i] != null)
                    continue;
                if (facesByFrame[i - 1] == null || facesByFrame[i + 1] == null)
                    continue;

                facesByFrame[i] = new List<Rect>(facesByFrame[i - 1]!);
                confByFrame[i] = new List<float>(confByFrame[i - 1]!);
                sizeByFrame[i] = sizeByFrame[i - 1];
            }

            for (int i = 0; i < total; i++)
            {
                if (hasStored[i] || facesByFrame[i] == null)
                    continue;

                var faces = facesByFrame[i]!;
                var confs = confByFrame[i] ?? new List<float>(faces.Count);
                if (confs.Count != faces.Count)
                {
                    confs = new List<float>(faces.Count);
                    for (int j = 0; j < faces.Count; j++)
                        confs.Add(1.0f);
                    confByFrame[i] = confs;
                }

                var keptFaces = new List<Rect>(faces.Count);
                var keptConfs = new List<float>(faces.Count);
                var prevFaces = i > 0 ? facesByFrame[i - 1] : null;
                var nextFaces = i + 1 < total ? facesByFrame[i + 1] : null;

                for (int j = 0; j < faces.Count; j++)
                {
                    var face = faces[j];
                    float conf = confs[j];
                    if (conf < TemporalConfidenceWeak)
                        continue;

                    double prevIou = GetMaxIoU(face, prevFaces, out var prevMatch);
                    double nextIou = GetMaxIoU(face, nextFaces, out var nextMatch);
                    bool hasContinuity = prevIou >= TemporalIouMin || nextIou >= TemporalIouMin;

                    if (conf < TemporalConfidenceStrong && !hasContinuity)
                        continue;

                    if (hasContinuity)
                    {
                        var match = prevIou >= nextIou ? prevMatch : nextMatch;
                        if (match.Width > 0 && match.Height > 0)
                        {
                            double area = Math.Max(1.0, face.Width * face.Height);
                            double matchArea = Math.Max(1.0, match.Width * match.Height);
                            double ratio = area / matchArea;
                            if ((ratio > TemporalMaxAreaChangeRatio || ratio < 1.0 / TemporalMaxAreaChangeRatio) &&
                                conf < TemporalConfidenceStrong)
                                continue;

                            double cx = face.X + face.Width * 0.5;
                            double cy = face.Y + face.Height * 0.5;
                            double mx = match.X + match.Width * 0.5;
                            double my = match.Y + match.Height * 0.5;
                            double shift = Math.Sqrt((cx - mx) * (cx - mx) + (cy - my) * (cy - my));
                            double maxDim = Math.Max(1.0, Math.Max(match.Width, match.Height));
                            if (shift / maxDim > TemporalMaxCenterShiftRatio &&
                                conf < TemporalConfidenceStrong)
                                continue;
                        }
                    }

                    keptFaces.Add(face);
                    keptConfs.Add(conf);
                }

                if (keptFaces.Count == 0)
                {
                    facesByFrame[i] = null;
                    confByFrame[i] = null;
                }
                else
                {
                    facesByFrame[i] = keptFaces;
                    confByFrame[i] = keptConfs;
                }
            }

            for (int i = 0; i < total; i++)
            {
                if (facesByFrame[i] == null)
                    continue;

                int start = i;
                while (i < total && facesByFrame[i] != null)
                    i++;

                int length = i - start;
                if (length < TemporalMinRunLength)
                {
                    for (int j = start; j < i; j++)
                    {
                        if (hasStored[j])
                            continue;
                        facesByFrame[j] = null;
                        confByFrame[j] = null;
                    }
                }
            }

            for (int i = 0; i < total; i++)
            {
                if (hasStored[i])
                    continue;

                if (facesByFrame[i] == null || facesByFrame[i]!.Count == 0)
                {
                    _maskProvider.RemoveFaceMask(i);
                    continue;
                }

                var faces = facesByFrame[i]!;
                var confs = confByFrame[i] ?? new List<float>(faces.Count);
                if (confs.Count != faces.Count)
                {
                    confs = new List<float>(faces.Count);
                    for (int j = 0; j < faces.Count; j++)
                        confs.Add(1.0f);
                }

                float minConf = float.MaxValue;
                for (int j = 0; j < confs.Count; j++)
                    minConf = Math.Min(minConf, confs[j]);

                _maskProvider.SetFaceRects(
                    i,
                    faces,
                    sizeByFrame[i],
                    minConf == float.MaxValue ? null : minConf,
                    confs);
            }
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

                bool exportAfter = _stateStore?.GetAutoSettings()?.AutoExportAfter ?? false;
                await RunAutoAsync(exportAfter: exportAfter);
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

        private void UpdateProxyProgress(ProxyProgress progress)
        {
            _proxyFrameIndex = Math.Max(0, progress.FrameIndex);
            _proxyTotalFrames = Math.Max(0, progress.TotalFrames);
            UpdateProxyEtaSamples(progress.Timestamp, _proxyFrameIndex);

            var remaining = EstimateProxyRemaining();
            _proxyEtaText = remaining.HasValue
                ? $"예상 남은 시간: {FormatEta(remaining.Value)}"
                : "예상 남은 시간 계산 중...";

            if (_proxyTotalFrames > 0)
            {
                int percent = (int)Math.Round(_proxyFrameIndex * 100.0 / Math.Max(1, _proxyTotalFrames));
                if (percent > 100) percent = 100;
                int scaled = (int)Math.Round(percent * 0.5);
                ReportAutoProgressScaled(scaled);
            }
        }

        private void UpdateProxyEtaSamples(DateTime timestamp, int frameIndex)
        {
            if (frameIndex < 0)
                return;

            if (_proxyEtaSamples.Count > 0 && frameIndex <= _proxyLastSample.FrameIndex)
                return;

            _proxyEtaSamples.Enqueue((timestamp, frameIndex));
            _proxyLastSample = (timestamp, frameIndex);

            while (_proxyEtaSamples.Count > 0 && (timestamp - _proxyEtaSamples.Peek().Timestamp).TotalSeconds > 10)
                _proxyEtaSamples.Dequeue();

            if (_proxyEtaSamples.Count == 0)
                _proxyEtaSamples.Enqueue(_proxyLastSample);
        }

        private TimeSpan? EstimateProxyRemaining()
        {
            if (_proxyTotalFrames <= 0 || _proxyEtaSamples.Count < 2)
                return null;

            var first = _proxyEtaSamples.Peek();
            var last = _proxyLastSample;
            var elapsedSeconds = (last.Timestamp - first.Timestamp).TotalSeconds;
            var progressed = last.FrameIndex - first.FrameIndex;

            if (elapsedSeconds <= 0 || progressed <= 0)
                return null;

            double ratePerSecond = progressed / elapsedSeconds;
            int remainingFrames = Math.Max(0, (_proxyTotalFrames - 1) - _proxyFrameIndex);
            return TimeSpan.FromSeconds(remainingFrames / ratePerSecond);
        }

        private static string BuildProxySkipReason(AutoMaskOptions options)
        {
            if (options.UseTracking)
                return "프록시 생략: 트래킹 ON";
            if (options.DetectEveryNFrames > 1)
                return $"프록시 생략: {options.DetectEveryNFrames}프레임마다 검출";
            if (options.DownscaleRatio < 0.99)
                return "프록시 생략: 다운스케일 적용됨";
            return "프록시 생략: 하이브리드 비활성화";
        }

        private void ReportAutoProgressScaled(int value)
        {
            int clamped = Math.Clamp(value, 0, 100);
            _autoProgressReporter?.Report(clamped);
            ToolPanel.AutoProgress = clamped;
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
                using IFaceDetector detector = new FaceOnnxDetector(_detectorOptions);
                var generator = new AutoMaskGenerator(
                    detector,
                    _maskProvider,
                    _autoOptions,
                    detectorFactory: () => new FaceOnnxDetector(_detectorOptions));

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

        private async Task<(int[] NoFace, int[] LowConfidence, int[] Flicker)> AnalyzeIssueFramesAsync()
        {
            int total = FrameList.TotalFrames;
            if (total <= 0)
                return (Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>());

            float lowConfidenceCutoff = GetLowConfidenceCutoff();

            return await Task.Run(() =>
            {
                var noFace = new List<int>();
                var lowConfidence = new List<int>();
                var flicker = new List<int>();
                var hasFace = new bool[total];
                for (int i = 0; i < total; i++)
                {
                    if (_maskProvider.TryGetStoredMask(i, out _))
                    {
                        hasFace[i] = true;
                        continue;
                    }

                    if (_maskProvider.TryGetFaceMaskData(i, out var data) && data.Faces.Count > 0)
                    {
                        hasFace[i] = true;
                        if (data.MinConfidence.HasValue &&
                            data.MinConfidence.Value < lowConfidenceCutoff)
                        {
                            lowConfidence.Add(i);
                        }
                        continue;
                    }

                    noFace.Add(i);
                }

                for (int i = 1; i < total - 1; i++)
                {
                    if (!hasFace[i] && hasFace[i - 1] && hasFace[i + 1])
                        flicker.Add(i);
                }

                return (noFace.ToArray(), lowConfidence.ToArray(), flicker.ToArray());
            });
        }

        private async Task<bool> RefineAutoAnomaliesAsync(CancellationToken ct)
        {
            if (!_isAutoRunning)
                return false;
            if (!_autoOptions.EnableHybridRefine &&
                _autoOptions.DownscaleRatio >= 0.999 &&
                !_autoOptions.UseTracking &&
                _autoOptions.DetectEveryNFrames <= 1 &&
                _autoOptions.DownscaleQuality == DownscaleQuality.BalancedBilinear)
            {
                return false;
            }

            var (noFaceFrames, lowConfidenceFrames, flickerFrames) = await AnalyzeIssueFramesAsync();
            var targets = MergeSortedFrames(noFaceFrames, lowConfidenceFrames);
            targets = MergeSortedFrames(targets, flickerFrames);
            if (_autoOptions.EnableHybridRefine)
            {
                int total = FrameList.TotalFrames;
                var smallFaces = FindSmallFaceFrames(total, _autoOptions.HybridSmallFaceAreaRatio);
                var periodic = BuildIntervalFrames(total, _autoOptions.HybridRefineInterval);
                targets = MergeSortedFrames(targets, smallFaces);
                targets = MergeSortedFrames(targets, periodic);
            }
            if (targets.Length == 0)
                return false;

            var refineOptions = new AutoMaskOptions
            {
                DownscaleRatio = 1.0,
                DownscaleQuality = DownscaleQuality.BalancedBilinear,
                UseTracking = false,
                DetectEveryNFrames = 1,
                ParallelDetectorCount = 1
            };

            try
            {
                var effectiveOptions = _effectiveDetectorOptions ?? _detectorOptions;
                using IFaceDetector detector = new FaceOnnxDetector(effectiveOptions);
                var generator = new AutoMaskGenerator(
                    detector,
                    _maskProvider,
                    refineOptions);

                await generator.GenerateFramesAsync(
                    FrameList.VideoPath,
                    targets,
                    progress: null,
                    ct,
                    clearIfNone: true);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            return true;
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

            var (noFaceFrames, lowConfidenceFrames, flickerFrames) = await AnalyzeIssueFramesAsync();

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
            float extra = 0.0f;
            double effectiveRatio = _autoOptions.EnableHybridRefine
                ? Math.Min(_autoOptions.DownscaleRatio, _autoOptions.HybridDownscaleRatio)
                : _autoOptions.DownscaleRatio;
            if (effectiveRatio < 0.9)
                extra += 0.03f;
            if (_autoOptions.UseTracking || _autoOptions.DetectEveryNFrames > 1)
                extra += 0.03f;
            return Math.Clamp(baseThreshold + LowConfidenceMargin + extra, 0.0f, 0.99f);
        }

        private int[] FindSmallFaceFrames(int totalFrames, double areaRatio)
        {
            if (totalFrames <= 0 || areaRatio <= 0)
                return Array.Empty<int>();

            var list = new List<int>();
            for (int i = 0; i < totalFrames; i++)
            {
                if (_maskProvider.TryGetFaceMaskData(i, out var data) && data.Faces.Count > 0)
                {
                    double frameArea = Math.Max(1.0, data.Size.Width * (double)data.Size.Height);
                    for (int j = 0; j < data.Faces.Count; j++)
                    {
                        var face = data.Faces[j];
                        double area = Math.Max(0.0, face.Width * face.Height);
                        if (area / frameArea <= areaRatio)
                        {
                            list.Add(i);
                            break;
                        }
                    }
                }
            }

            return list.ToArray();
        }

        private static int[] BuildIntervalFrames(int totalFrames, int interval)
        {
            if (totalFrames <= 0 || interval <= 1)
                return Array.Empty<int>();

            var list = new List<int>();
            for (int i = 0; i < totalFrames; i += interval)
                list.Add(i);
            return list.ToArray();
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

        public void UpdateDetectorOptions(FaceOnnxDetectorOptions options)
        {
            _detectorOptions = options ?? new FaceOnnxDetectorOptions();
            _effectiveDetectorOptions = null;
        }

        public void UpdateAutoOptions(AutoMaskOptions options)
        {
            _autoOptions = options ?? new AutoMaskOptions();
        }

        public void PersistWorkspaceState()
        {
            PersistWorkspaceStateInternal(includePreviewMask: true);
        }

        private void PersistWorkspaceStateInternal(bool includePreviewMask)
        {
            if (_stateStore == null)
                return;

            if (includePreviewMask)
                FramePreview.PersistCurrentMask();

            var snapshot = BuildSnapshot();
            lock (_stateSaveLock)
            {
                _stateStore.SaveWorkspace(snapshot, _maskProvider);
            }
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

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            Task? checkpointTask;
            lock (_checkpointSync)
            {
                _checkpointPending = false;
                _checkpointPendingIncludePreview = false;
                checkpointTask = _checkpointTask;
                _checkpointTask = null;
            }

            if (checkpointTask != null)
            {
                try { checkpointTask.Wait(TimeSpan.FromSeconds(2)); }
                catch { }
            }

            _autoCts?.Cancel();
            _autoCts?.Dispose();
            _autoCts = null;

            _exportCts?.Cancel();
            _exportCts?.Dispose();
            _exportCts = null;

            FramePreview.Dispose();
            FrameList.Dispose();
            _maskProvider.Dispose();
        }
    }
}
