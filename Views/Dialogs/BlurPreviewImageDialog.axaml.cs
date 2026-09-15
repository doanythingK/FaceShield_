using Avalonia.Controls;
using Avalonia.Media.Imaging;
using System;
using System.Diagnostics;

namespace FaceShield.Views.Dialogs
{
    public partial class BlurPreviewImageDialog : Window
    {
        private WriteableBitmap? _image;

        public BlurPreviewImageDialog()
        {
            InitializeComponent();
        }

        public BlurPreviewImageDialog(WriteableBitmap image, string label)
        {
            InitializeComponent();
            UpdatePreview(image, label);
        }

        public new void Show(Window owner)
        {
            try
            {
                base.Show(owner);
            }
            catch (Exception ex)
            {
                ReleaseImage();
                Debug.WriteLine(
                    $"[BlurPreviewImageDialog] window display suppressed during shutdown/detach: {ex.Message}");
            }
        }

        private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            Close();
        }

        public void UpdatePreview(WriteableBitmap image, string label)
        {
            _image?.Dispose();
            _image = image;
            PreviewImage.Source = image;
            LabelText.Text = label;
        }

        protected override void OnClosed(EventArgs e)
        {
            ReleaseImage();
            base.OnClosed(e);
        }

        private void ReleaseImage()
        {
            PreviewImage.Source = null;
            _image?.Dispose();
            _image = null;
        }
    }
}
