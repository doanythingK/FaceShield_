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
using FaceShield.Services.Workspace;
using FaceShield.Views.Dialogs;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
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
        private DownscaleOption selectedDownscaleOption;

        public IReadOnlyList<int> DetectEveryOptions { get; } = new[] { 1, 2, 3, 5 };

        [ObservableProperty]
        private int autoDetectEveryNFrames = 1;

        [ObservableProperty]
        private bool autoTrackingEnabled;

        [ObservableProperty]
        private bool autoUseOrtOptimization = true;

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
            IsAutoRunning
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
                AutoEtaText = null;
        }

        partial void OnIsWorkspaceLoadingChanged(bool value)
        {
            OnPropertyChanged(nameof(CanStartWorkspace));
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(BusyProgress));
            OnPropertyChanged(nameof(BusyMessage));
            OnPropertyChanged(nameof(IsBusyIndeterminate));
        }

        partial void OnAutoProgressChanged(int value)
        {
            OnPropertyChanged(nameof(BusyProgress));

            if (!IsAutoRunning || value <= 0)
                return;

            var elapsed = DateTime.UtcNow - _autoStartTimeUtc;
            if (elapsed.TotalSeconds < 1)
                return;

            double remainingRatio = (100.0 - value) / value;
            if (remainingRatio < 0)
                remainingRatio = 0;

            var remaining = TimeSpan.FromSeconds(elapsed.TotalSeconds * remainingRatio);
            AutoEtaText = $"예상 남은 시간: {FormatEta(remaining)}";
        }

        partial void OnWorkspaceLoadingProgressChanged(int value)
        {
            OnPropertyChanged(nameof(BusyProgress));
        }

        partial void OnWorkspaceLoadingMessageChanged(string? value)
        {
            OnPropertyChanged(nameof(BusyMessage));
        }

        partial void OnIsWorkspaceLoadingIndeterminateChanged(bool value)
        {
            OnPropertyChanged(nameof(IsBusyIndeterminate));
        }

        partial void OnAutoTrackingEnabledChanged(bool value)
        {
            OnPropertyChanged(nameof(IsTrackingOptionsEnabled));
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
            _autoCts = new CancellationTokenSource();
            _autoStartTimeUtc = DateTime.UtcNow;
            AutoEtaText = "예상 남은 시간 계산 중...";

            bool completed = false;
            try
            {
                var progress = new Progress<int>(p =>
                    Dispatcher.UIThread.Post(() => AutoProgress = p));
                completed = await vm.RunAutoAsync(exportAfter: true, progress, _autoCts.Token);
            }
            finally
            {
                _autoCts?.Dispose();
                _autoCts = null;
                IsAutoRunning = false;
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

        private static string FormatEta(TimeSpan remaining)
        {
            if (remaining.TotalHours >= 1)
                return remaining.ToString(@"hh\:mm\:ss");

            return remaining.ToString(@"mm\:ss");
        }

        private AutoMaskOptions BuildAutoOptions()
        {
            double ratio = SelectedDownscaleOption?.Ratio ?? 1.0;
            bool useTracking = AutoTrackingEnabled;
            int detectEvery = useTracking ? Math.Max(1, AutoDetectEveryNFrames) : 1;

            return new AutoMaskOptions
            {
                DownscaleRatio = ratio,
                UseTracking = useTracking,
                DetectEveryNFrames = detectEvery
            };
        }

        private FaceOnnxDetectorOptions BuildDetectorOptions()
        {
            return new FaceOnnxDetectorOptions
            {
                UseOrtOptimization = AutoUseOrtOptimization,
                IntraOpNumThreads = null,
                InterOpNumThreads = null
            };
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
                return cached;

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
