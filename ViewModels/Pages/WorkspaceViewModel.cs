using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FaceShield.Enums.Workspace; // 🔹 추가
using FaceShield.Services.Analysis;
using FaceShield.Services.FaceDetection;
using FaceShield.Services.Video;
using FaceShield.Services.Video.Session;
using FaceShield.Services.Workspace;
using FaceShield.ViewModels.Workspace;
using System;
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
        private readonly AutoMaskOptions _autoOptions;
        private readonly FaceOnnxDetectorOptions _detectorOptions;
        private readonly WorkspaceStateStore? _stateStore;
        private int[] _autoAnomalies = Array.Empty<int>();
        private int _autoResumeIndex;
        private bool _autoCompleted;
        private bool _sessionInitialized;

        // 프레임별 최종 마스크 저장소
        private readonly FrameMaskProvider _maskProvider = new();

        // 🔹 자동 분석 상태 관리용 (최소한 재진입 방지)
        private bool _isAutoRunning;
        private CancellationTokenSource? _autoCts;

        // 🔹 현재 워크스페이스 모드 (Auto / Manual)
        public WorkspaceMode Mode { get; }

        [ObservableProperty]
        private int autoAnomalyCount;

        [ObservableProperty]
        private bool hasAutoAnomalies;

        public bool NeedsAutoResumePrompt =>
            Mode == WorkspaceMode.Auto &&
            !_autoCompleted &&
            _autoResumeIndex > 0;

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

            ToolPanel.SaveRequested += async () =>
            {
                FramePreview.PersistCurrentMask();

                await SaveVideoAsync();
                PersistWorkspaceState();
            };

            // 🔹 자동 모드 버튼 → 자동 마스크 생성 연결
            ToolPanel.AutoRequested += OnAutoRequested;
            ToolPanel.AutoCancelRequested += OnAutoCancelRequested;
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


        private Task SaveVideoAsync()
        {
            string input = FrameList.VideoPath;
            string output = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(input)!,
                System.IO.Path.GetFileNameWithoutExtension(input) + "_blur.mp4");

            var exporter = new VideoExportService(_maskProvider);

            return Task.Run(() =>
            {
                exporter.Export(input, output, blurRadius: 6);
            });
        }

        public Task<bool> RunAutoAsync(
            bool exportAfter,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (_isAutoRunning)
                return Task.FromResult(false);

            _isAutoRunning = true;
            _autoCts = cancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : new CancellationTokenSource();

            ToolPanel.IsAutoRunning = true;
            ToolPanel.AutoProgress = 0;

            return RunAutoCoreAsync(exportAfter, progress);
        }

        private async Task<bool> RunAutoCoreAsync(bool exportAfter, IProgress<int>? progress)
        {
            try
            {
                using IFaceDetector detector = new FaceOnnxDetector(_detectorOptions);

                var generator = new AutoMaskGenerator(detector, _maskProvider, _autoOptions);
                _autoCompleted = false;
                int lastProcessed = Math.Max(0, _autoResumeIndex);

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

                await BuildAutoAnomaliesAsync();

                if (exportAfter)
                    await SaveVideoAsync();

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

        private async void OnAutoRequested()
        {
            if (Mode == WorkspaceMode.Manual)
            {
                await RunAutoSingleFrameAsync();
                ToolPanel.CurrentMode = EditMode.Manual;
                return;
            }

            await RunAutoAsync(exportAfter: false);
        }

        private void OnAutoCancelRequested()
        {
            _autoCts?.Cancel();
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
                var generator = new AutoMaskGenerator(detector, _maskProvider, _autoOptions);

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

        private async Task BuildAutoAnomaliesAsync()
        {
            int total = FrameList.TotalFrames;
            if (total <= 0)
            {
                _autoAnomalies = Array.Empty<int>();
                AutoAnomalyCount = 0;
                HasAutoAnomalies = false;
                return;
            }

            var anomalies = await Task.Run(() =>
            {
                var list = new System.Collections.Generic.List<int>();
                for (int i = 0; i < total; i++)
                {
                    if (_maskProvider.GetFinalMask(i) == null)
                        list.Add(i);
                }
                return list.ToArray();
            });

            _autoAnomalies = anomalies;
            AutoAnomalyCount = anomalies.Length;
            HasAutoAnomalies = anomalies.Length > 0;
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
