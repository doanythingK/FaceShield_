using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FaceShield.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FaceShield.ViewModels.Pages
{
    public partial class HomePageViewModel : ViewModelBase
    {
        private readonly Action<WorkspaceViewModel> _onStartWorkspace;

        [ObservableProperty]
        private string? selectedVideoPath;

        [ObservableProperty]
        private RecentItem? selectedRecent;

        public ObservableCollection<RecentItem> Recents { get; } = new();

        public HomePageViewModel(Action<WorkspaceViewModel> onStartWorkspace)
        {
            _onStartWorkspace = onStartWorkspace;

            // 샘플: 실제로는 추후 로컬 설정 파일에서 로딩
            Recents.Add(new RecentItem("샘플 프로젝트", @"C:\Temp\sample.mp4", DateTimeOffset.Now.AddDays(-1)));
            Recents.Add(new RecentItem("테스트 영상", @"D:\Videos\test_4k.mp4", DateTimeOffset.Now.AddDays(-3)));
        }

        public bool CanOpenWorkspace => !string.IsNullOrWhiteSpace(SelectedVideoPath);

        partial void OnSelectedVideoPathChanged(string? value)
        {
            OnPropertyChanged(nameof(CanOpenWorkspace));
        }

        partial void OnSelectedRecentChanged(RecentItem? value)
        {
            if (value is not null)
            {
                SelectedVideoPath = value.Path;
            }
        }

        /// <summary>
        /// View에서 StorageProvider를 넘겨받아 호출합니다.
        /// </summary>
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

            // 최근 목록 업데이트(최소)
            var title = Path.GetFileName(localPath);
            Recents.Insert(0, new RecentItem(title, localPath, DateTimeOffset.Now));
        }

        [RelayCommand]
        private void OpenWorkspace()
        {
            if (string.IsNullOrWhiteSpace(SelectedVideoPath))
                return;

            var vm = new WorkspaceViewModel(SelectedVideoPath);
            _onStartWorkspace(vm);
        }

        [RelayCommand]
        private void OpenSettings()
        {
            // 다음 단계에서 SettingsPageViewModel로 교체하면 됩니다.
            // 지금은 "첫 화면부터 실행" 목표이므로 자리만 둡니다.
        }

        [RelayCommand]
        private void OpenAbout()
        {
            // 다음 단계에서 AboutDialog/페이지로 교체
        }
    }
}
