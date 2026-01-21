using Avalonia.Controls;
using FaceShield.ViewModels.Pages;
using System;

namespace FaceShield.Views.Dialogs
{
    public partial class BlurPreviewDialog : Window
    {
        private BlurPreviewImageDialog? _previewWindow;
        private double? _selectedPercent;

        public BlurPreviewDialog(HomePageViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        private void OnExampleClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (DataContext is not HomePageViewModel vm)
                return;
            if (sender is not Button button)
                return;
            if (button.DataContext is not BlurExampleItem item)
                return;

            var payload = vm.BuildBlurPreview(item.Percent);
            if (payload == null)
                return;

            _selectedPercent = item.Percent;

            if (_previewWindow == null || !_previewWindow.IsVisible)
            {
                _previewWindow = new BlurPreviewImageDialog(payload.Image, payload.Label)
                {
                    Title = "블러 예시 (원본 크기)"
                };
                _previewWindow.Closed += (_, _) => _previewWindow = null;
                _previewWindow.Show(this);
            }
            else
            {
                _previewWindow.UpdatePreview(payload.Image, payload.Label);
                _previewWindow.Activate();
            }
        }

        private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (_selectedPercent == null)
                return;
            if (_previewWindow == null || !_previewWindow.IsVisible)
                return;
            if (DataContext is not HomePageViewModel vm)
                return;

            if (e.PropertyName == nameof(HomePageViewModel.BlurRadius) ||
                e.PropertyName == nameof(HomePageViewModel.SelectedResolutionOption))
            {
                var payload = vm.BuildBlurPreview(_selectedPercent.Value);
                if (payload != null)
                    _previewWindow.UpdatePreview(payload.Image, payload.Label);
            }
        }

        private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            if (DataContext is HomePageViewModel vm)
                vm.PropertyChanged -= OnViewModelPropertyChanged;
            if (_previewWindow != null)
            {
                _previewWindow.Close();
                _previewWindow = null;
            }
            base.OnClosed(e);
        }
    }
}
