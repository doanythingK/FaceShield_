using Avalonia.Controls;
using Avalonia.Input;
using FaceShield.ViewModels;
using FaceShield.ViewModels.Pages;

namespace FaceShield.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Handled || IsInteractiveInputSource(e.Source))
                return;

            if (DataContext is not MainWindowViewModel vm)
                return;

            if (vm.CurrentPage is not WorkspaceViewModel workspace)
                return;

            if (!workspace.ToolPanel.IsSessionReady)
                return;

            if (e.Key == Key.Q || e.Key == Key.E)
            {
                if (!workspace.ToolPanel.CanEditWorkspace ||
                    !workspace.HasAutoAnomalies)
                {
                    return;
                }

                if (e.Key == Key.Q)
                    workspace.PrevAutoAnomalyCommand.Execute(null);
                else
                    workspace.NextAutoAnomalyCommand.Execute(null);

                e.Handled = true;
                return;
            }

            if (workspace.FrameList.HandleKey(e.Key, e.KeyModifiers))
                e.Handled = true;
        }

        private static bool IsInteractiveInputSource(object? source)
            => source is TextBox or ComboBox or Slider or Button;
    }
}
