using Avalonia.Controls;
using Avalonia.Interactivity;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace FaceShield.Views.Dialogs
{
    public enum ExportConflictResult
    {
        Cancel,
        Overwrite,
        SaveAs
    }

    public partial class ExportConflictDialog : Window
    {
        public string OutputPath { get; }

        public ExportConflictDialog()
            : this(string.Empty)
        {
        }

        public ExportConflictDialog(string outputPath)
        {
            OutputPath = outputPath ?? string.Empty;
            InitializeComponent();
            DataContext = this;
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
                    $"[ExportConflictDialog] dialog display suppressed during shutdown/detach: {ex.Message}");
                return default!;
            }
        }

        private void OnOverwriteClick(object? sender, RoutedEventArgs e)
        {
            Close(ExportConflictResult.Overwrite);
        }

        private void OnSaveAsClick(object? sender, RoutedEventArgs e)
        {
            Close(ExportConflictResult.SaveAs);
        }

        private void OnCancelClick(object? sender, RoutedEventArgs e)
        {
            Close(ExportConflictResult.Cancel);
        }
    }
}
