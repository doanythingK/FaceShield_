using CommunityToolkit.Mvvm.ComponentModel;
using FaceShield.Enums.Workspace;
using FaceShield.Models;
using FaceShield.Services.Workspace;
using FaceShield.ViewModels.Pages;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FaceShield.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase
    {
        [ObservableProperty]
        private object? currentPage;

        private readonly WorkspaceStateStore _stateStore = new();
        private readonly HomePageViewModel _home;
        private readonly WorkspaceMode? _startupOpenMode;

        public MainWindowViewModel()
            : this(null)
        {
        }

        public MainWindowViewModel(IReadOnlyList<string>? startupArgs)
        {
            // 앱 시작 시 첫 화면: Home
            _home = new HomePageViewModel(
                onStartWorkspace: vm => CurrentPage = vm,
                onBackHome: () => CurrentPage = _home,
                stateStore: _stateStore
            );

            var startupOptions = AppStartupOptions.Parse(startupArgs);
            if (startupOptions.HasValues)
                _home.ApplyStartupOptions(startupOptions);

            _startupOpenMode = startupOptions.OpenMode;
            CurrentPage = _home;
        }

        public bool ShouldOpenStartupWorkspace => _startupOpenMode.HasValue;

        public async Task OpenStartupWorkspaceAsync()
        {
            if (!_startupOpenMode.HasValue || !_home.CanStartWorkspace)
                return;

            try
            {
                if (_startupOpenMode == WorkspaceMode.Auto)
                    await _home.OpenAutoWorkspaceCommand.ExecuteAsync(null);
                else
                    await _home.OpenManualWorkspaceCommand.ExecuteAsync(null);
            }
            catch (Exception ex)
            {
                _home.WorkspaceLoadingMessage = $"시작 옵션 처리 실패: {ex.Message}";
            }
        }

        public void PersistAppState()
        {
            _home.PersistAllWorkspaces();
        }
    }
}
