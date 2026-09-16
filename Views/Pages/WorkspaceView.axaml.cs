using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using FaceShield.Enums.Workspace;
using FaceShield.ViewModels.Pages;

namespace FaceShield.Views.Pages;

public partial class WorkspaceView : UserControl
{
    private WorkspaceViewModel? _configuredWorkspace;
    private bool _manualTimelineAttached;

    public WorkspaceView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnAnyKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) =>
        {
            if (_manualTimelineAttached)
                ConfigureManualMaskTimeline();
        };
        AttachedToVisualTree += (_, _) =>
        {
            _manualTimelineAttached = true;
            ConfigureManualMaskTimeline();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _manualTimelineAttached = false;
            DetachManualMaskTimeline();
        };
    }

    private void ConfigureManualMaskTimeline()
    {
        if (DataContext is not WorkspaceViewModel vm)
        {
            DetachManualMaskTimeline();
            return;
        }

        if (!ReferenceEquals(_configuredWorkspace, vm))
        {
            _configuredWorkspace?.FramePreview.DetachManualTrackingContext();
            _configuredWorkspace = vm;
        }

        vm.ConfigureManualTrackingOwnership();
        vm.FramePreview.ConfigureManualMaskKeyframes(
            vm.Mode == WorkspaceMode.Manual,
            vm.FrameList.VideoPath,
            vm.FrameList.TotalFrames);
    }

    private void DetachManualMaskTimeline()
    {
        WorkspaceViewModel? configured = _configuredWorkspace;
        _configuredWorkspace = null;
        configured?.FramePreview.DetachManualTrackingContext();
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
