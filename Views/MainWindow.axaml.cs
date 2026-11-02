using Avalonia.Controls;
using Avalonia.Input;
using FaceShield.ViewModels;
using FaceShield.ViewModels.Pages;
using FaceShield.Views.Dialogs;
using System;
using System.Threading.Tasks;

namespace FaceShield.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private bool _closePreflightRunning;
        private bool _closePreflightComplete;

        protected override async void OnClosing(WindowClosingEventArgs e)
        {
            if (e.Cancel || _closePreflightComplete ||
                DataContext is not MainWindowViewModel vm)
            {
                base.OnClosing(e);
                return;
            }

            // The UI dispatcher must remain free while Auto/native workers
            // cancel and drain. A successful preflight re-enters Close once.
            e.Cancel = true;
            base.OnClosing(e);
            if (_closePreflightRunning)
                return;

            _closePreflightRunning = true;
            try
            {
                await vm.PersistBeforeWindowCloseAsync();
                _closePreflightComplete = true;
                Close();
            }
            catch (Exception ex)
            {
                if (vm.CurrentPage is WorkspaceViewModel workspace)
                    workspace.FramePreview.ReportManualTargetFailure(
                        "창 닫기 전 워크스페이스 저장", ex);
                _ = new ErrorDialog("워크스페이스 저장 실패 — 창 닫기 취소", ex.Message)
                    .ShowDialog(this);
            }
            finally
            {
                _closePreflightRunning = false;
            }
        }

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Handled || IsInteractiveInputSource(e.Source))
                return;

            if (DataContext is not MainWindowViewModel vm)
                return;

            if (vm.CurrentPage is not WorkspaceViewModel workspace)
                return;

            if (!workspace.ToolPanel.CanEditWorkspace)
                return;

            if (e.Key == Key.Q || e.Key == Key.E)
            {
                if (!workspace.HasAutoAnomalies)
                    return;

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
