using Avalonia.Controls;
using Avalonia.Input;
using FaceShield.ViewModels;
using FaceShield.ViewModels.Pages;
using FaceShield.Views.Dialogs;
using System;

namespace FaceShield.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            if (!e.Cancel && DataContext is MainWindowViewModel vm)
            {
                try
                {
                    // Save current and cached hidden workspaces while closing
                    // can still be canceled. Never let Exit be their first save.
                    vm.PersistBeforeWindowClose();
                }
                catch (Exception ex)
                {
                    e.Cancel = true;
                    if (vm.CurrentPage is WorkspaceViewModel workspace)
                        workspace.FramePreview.ReportManualTargetFailure(
                            "창 닫기 전 워크스페이스 저장", ex);
                    _ = new ErrorDialog("워크스페이스 저장 실패 — 창 닫기 취소", ex.Message)
                        .ShowDialog(this);
                }
            }

            base.OnClosing(e);
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
