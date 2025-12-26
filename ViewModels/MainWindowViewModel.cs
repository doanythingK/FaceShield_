using CommunityToolkit.Mvvm.ComponentModel;
using FaceShield.ViewModels.Pages;

namespace FaceShield.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase
    {
        [ObservableProperty]
        private object? currentPage;

        public MainWindowViewModel()
        {
            // 앱 시작 시 첫 화면: Home
            var home = new HomePageViewModel(
                onStartWorkspace: vm => CurrentPage = vm
            );

            CurrentPage = home;
        }
    }
}
