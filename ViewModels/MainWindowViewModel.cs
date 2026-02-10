using CommunityToolkit.Mvvm.ComponentModel;
using FaceShield.Services.Workspace;
using FaceShield.ViewModels.Pages;

namespace FaceShield.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase
    {
        [ObservableProperty]
        private object? currentPage;

        private readonly WorkspaceStateStore _stateStore = new();
        private readonly HomePageViewModel _home;

        public MainWindowViewModel()
        {
            // 앱 시작 시 첫 화면: Home
            _home = new HomePageViewModel(
                onStartWorkspace: vm => CurrentPage = vm,
                onBackHome: () => CurrentPage = _home,
                stateStore: _stateStore
            );

            CurrentPage = _home;
        }

        public void PersistAppState()
        {
            _home.PersistAllWorkspaces();
        }
    }
}
