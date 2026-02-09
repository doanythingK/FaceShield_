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
        private const int TemporalMinRunLength = 2;
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
                using IFaceDetector detector = new FaceOnnxDetector(_detectorOptions);

                var generator = new AutoMaskGenerator(
                    detector,
                    _maskProvider,
                    _autoOptions,
                    detectorFactory: () => new FaceOnnxDetector(_detectorOptions));
                _autoCompleted = false;
                int lastProcessed = Math.Max(0, _autoResumeIndex);
                if (lastProcessed == 0)
                    _maskProvider.ClearFaceMasks();

                // TODO: 필요하면 IProgress<int>를 WorkspaceViewModel 프로퍼티로 노출해서
                //       진행률 UI를 그릴 수 있습니다.
                var effectiveProgress = new Progress<int>(p =>
                {
                    progress?.Report(p);
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
                        TryUpdateAutoPreview(idx);
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
                if (hasStored[i])
                    continue;

                var prevFaces = facesByFrame[i - 1];
                var nextFaces = facesByFrame[i + 1];
                if (prevFaces == null || nextFaces == null)
                    continue;

                var matches = MatchFacesForInterpolation(
                    prevFaces,
                    confByFrame[i - 1],
                    nextFaces,
                    confByFrame[i + 1]);
                if (matches.Count == 0)
                    continue;

                if (facesByFrame[i] == null)
                {
                    facesByFrame[i] = new List<Rect>(matches.Count);
                    confByFrame[i] = new List<float>(matches.Count);
                }
                else
                {
                    confByFrame[i] ??= new List<float>(facesByFrame[i]!.Count);
                    while (confByFrame[i]!.Count < facesByFrame[i]!.Count)
                        confByFrame[i]!.Add(TemporalConfidenceStrong);
                }

                if (sizeByFrame[i].Width <= 0 || sizeByFrame[i].Height <= 0)
                {
                    sizeByFrame[i] = sizeByFrame[i - 1].Width > 0 && sizeByFrame[i - 1].Height > 0
                        ? sizeByFrame[i - 1]
                        : sizeByFrame[i + 1];
                }

                foreach (var match in matches)
                {
                    var interpolated = InterpolateRect(match.Prev, match.Next);
                    if (interpolated.Width <= 0 || interpolated.Height <= 0)
                        continue;
                    if (HasSimilarFace(interpolated, facesByFrame[i], TemporalDuplicateIouMin))
                        continue;

                    facesByFrame[i]!.Add(interpolated);
                    confByFrame[i]!.Add(Math.Clamp(match.Confidence, TemporalConfidenceWeak, 1.0f));
                }
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
                for (int i = 0; i < total; i++)
                {
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
        }

        public void UpdateAutoOptions(AutoMaskOptions options)
        {
            _autoOptions = options ?? new AutoMaskOptions();
        }

        public void PersistWorkspaceState()
        {
            if (_stateStore == null)
                return;

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
