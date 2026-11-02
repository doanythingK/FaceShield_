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
        private readonly int? _startupFrameIndex;
        private bool _windowCloseStatePersisted;

        public MainWindowViewModel()
            : this(null)
        {
        }

        public MainWindowViewModel(IReadOnlyList<string>? startupArgs)
        {
            // 앱 시작 시 첫 화면: Home
            HomePageViewModel? home = null;
            home = new HomePageViewModel(
                onStartWorkspace: vm => CurrentPage = vm,
                onBackHome: () =>
                {
                    if (home is not { } targetHome)
                        return;

                    CurrentPage = targetHome;
                    targetHome.PruneDeferredWorkspaceEvictions();
                },
                stateStore: _stateStore,
                isWorkspaceCurrent: vm => ReferenceEquals(CurrentPage, vm)
            );
            _home = home;

            var startupOptions = AppStartupOptions.Parse(startupArgs);
            if (startupOptions.HasValues)
                _home.ApplyStartupOptions(startupOptions);

            _startupOpenMode = startupOptions.OpenMode;
            _startupFrameIndex = startupOptions.FrameIndex;
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

                if (_home.IsShutdownRequested)
                    return;

                if (_startupFrameIndex.HasValue &&
                    CurrentPage is WorkspaceViewModel workspace &&
                    workspace.FrameList.TotalFrames > 0)
                {
                    workspace.FrameList.SelectedFrameIndex = Math.Clamp(
                        _startupFrameIndex.Value,
                        0,
                        workspace.FrameList.TotalFrames - 1);
                }
            }
            catch (Exception ex)
            {
                if (!_home.IsShutdownRequested)
                    _home.WorkspaceLoadingMessage = $"시작 옵션 처리 실패: {ex.Message}";
            }
        }

        // Drain all running workspace operations before the cancellable
        // window-close save. Exit must not re-save a moving Auto snapshot.
        public async Task PersistBeforeWindowCloseAsync()
        {
            _windowCloseStatePersisted = false;
            await _home.PersistAllWorkspacesForWindowCloseAsync();
            _windowCloseStatePersisted = true;
        }

        public void PersistAppState()
        {
            _home.PrepareAllWorkspacesForShutdown();
            // Normal closing has already saved every workspace. Skip a second
            // write that might fail after the window can no longer be reopened.
            // Other shutdown paths still attempt saving before disposing.
            if (!_windowCloseStatePersisted)
                _home.PersistAllWorkspacesForShutdown();
            CurrentPage = _home;
            _home.DisposeAllWorkspaces();
        }
    }
}
