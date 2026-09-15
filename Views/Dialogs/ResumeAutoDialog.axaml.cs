using Avalonia.Controls;
using Avalonia.Interactivity;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace FaceShield.Views.Dialogs
{
    public partial class ResumeAutoDialog : Window
    {
        public ResumeAutoDialog()
        {
            InitializeComponent();
        }

        public new async Task<TResult> ShowDialog<TResult>(Window owner)
        {
            try
            {
                return await base.ShowDialog<TResult>(owner);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[ResumeAutoDialog] dialog display suppressed during shutdown/detach: {ex.Message}");
                return default!;
            }
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
