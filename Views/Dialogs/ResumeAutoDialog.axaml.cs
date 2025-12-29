using Avalonia.Controls;
using Avalonia.Interactivity;

namespace FaceShield.Views.Dialogs
{
    public partial class ResumeAutoDialog : Window
    {
        public ResumeAutoDialog()
        {
            InitializeComponent();
        }

        private void OnYesClick(object? sender, RoutedEventArgs e)
        {
            Close(true);
        }

        private void OnNoClick(object? sender, RoutedEventArgs e)
        {
            Close(false);
        }
    }
}
