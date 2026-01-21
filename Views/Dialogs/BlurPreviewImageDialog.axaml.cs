using Avalonia.Controls;
using Avalonia.Media.Imaging;
using System;

namespace FaceShield.Views.Dialogs
{
    public partial class BlurPreviewImageDialog : Window
    {
        private WriteableBitmap? _image;

        public BlurPreviewImageDialog(WriteableBitmap image, string label)
        {
            InitializeComponent();
            UpdatePreview(image, label);
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
            _image?.Dispose();
            _image = null;
            base.OnClosed(e);
        }
    }
}
