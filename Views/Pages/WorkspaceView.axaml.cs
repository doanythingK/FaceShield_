using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using FaceShield.Enums.Workspace;
using FaceShield.ViewModels.Pages;

namespace FaceShield.Views.Pages;

public partial class WorkspaceView : UserControl
{
    public WorkspaceView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnAnyKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) => ConfigureManualMaskTimeline();
        AttachedToVisualTree += (_, _) => ConfigureManualMaskTimeline();
    }

    private void ConfigureManualMaskTimeline()
    {
        if (DataContext is not WorkspaceViewModel vm)
            return;

        vm.FramePreview.ConfigureManualMaskKeyframes(
            vm.Mode == WorkspaceMode.Manual,
            vm.FrameList.VideoPath,
            vm.FrameList.TotalFrames);
    }

    private void OnAnyKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not WorkspaceViewModel vm)
            return;

        bool isUndo = (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))
            && e.Key == Key.Z;
        if (!isUndo || !vm.ToolPanel.CanEditWorkspace)
            return;

        // Route keyboard undo through the same ToolPanel command/event path as the
        // toolbar button so manual tracking invalidation runs after the mask restore.
        vm.ToolPanel.UndoCommand.Execute(null);
        e.Handled = true;
    }
}
