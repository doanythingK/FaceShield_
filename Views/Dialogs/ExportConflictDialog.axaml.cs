using Avalonia.Controls;
using Avalonia.Interactivity;

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

        public ExportConflictDialog(string outputPath)
        {
            OutputPath = outputPath ?? string.Empty;
            InitializeComponent();
            DataContext = this;
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
