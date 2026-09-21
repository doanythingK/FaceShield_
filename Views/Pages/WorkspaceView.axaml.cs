using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using FaceShield.Enums.Workspace;
using FaceShield.ViewModels.Pages;
using FaceShield.ViewModels.Workspace;
using System.Linq;

namespace FaceShield.Views.Pages;

public partial class WorkspaceView : UserControl
{
    private WorkspaceViewModel? _configuredWorkspace;
    private bool _manualTimelineAttached;
    private bool _syncingTargetPicker;
    private StackPanel? _manualTargetBar;
    private ComboBox? _manualTargetPicker;
    private Button? _targetTrackButton;
    private Button? _legacyNextTrackButton;
    private Button? _legacyTrackButton;

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
        if (vm.Mode == WorkspaceMode.Manual)
        {
            vm.FramePreview.ConfigureManualTargets(vm.FrameList.VideoPath);
            vm.ToolPanel.ManualTargetExportGuard = vm.FramePreview.CanExportLegacyMask;
        }
        else
        {
            vm.ToolPanel.ManualTargetExportGuard = null;
        }

        EnsureManualTargetControls();
        SyncManualTargetControls();
    }

    private void EnsureManualTargetControls()
    {
        if (_manualTargetBar != null || Content is not Grid root)
            return;
        Border? headerBorder = root.Children.OfType<Border>()
            .FirstOrDefault(border => Grid.GetRow(border) == 0);
        if (headerBorder?.Child is not StackPanel header)
            return;
        Border? trackingBorder = header.Children.OfType<Border>().FirstOrDefault();
        if (trackingBorder?.Child is StackPanel tracking)
        {
            Button[] legacy = tracking.Children.OfType<Button>().Take(2).ToArray();
            _legacyNextTrackButton = legacy.ElementAtOrDefault(0);
            _legacyTrackButton = legacy.ElementAtOrDefault(1);
            _targetTrackButton = new Button { Content = "선택한 얼굴 연속 추적" };
            _targetTrackButton.Bind(Button.CommandProperty,
                new Binding("FramePreview.TrackSelectedManualTargetCommand"));
            _targetTrackButton.Bind(Button.IsEnabledProperty,
                new Binding("FramePreview.CanTrackSelectedManualTarget"));
            tracking.Children.Insert(0, _targetTrackButton);
        }

        var bar = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        bar.Children.Add(new TextBlock
        {
            Text = "수동 얼굴",
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        });
        var add = new Button { Content = "새 얼굴" };
        add.Click += (_, _) =>
        {
            if (DataContext is WorkspaceViewModel workspace &&
                workspace.Mode == WorkspaceMode.Manual)
            {
                workspace.FramePreview.CreateManualTargetCommand.Execute(null);
                SyncManualTargetControls();
                workspace.FramePreview.RefreshManualTargetPreview();
            }
        };
        bar.Children.Add(add);
        var picker = new ComboBox { Width = 190 };
        picker.ItemTemplate = new FuncDataTemplate<ManualTargetChoice>(
            (choice, _) => new TextBlock { Text = choice.Label });
        picker.SelectionChanged += (_, _) =>
        {
            if (_syncingTargetPicker || picker.SelectedItem is not ManualTargetChoice choice ||
                DataContext is not WorkspaceViewModel workspace)
                return;
            workspace.FramePreview.SelectedManualTarget = choice;
            workspace.FramePreview.RefreshManualTargetPreview();
            SyncManualTargetControls();
        };
        bar.Children.Add(picker);
        bar.Children.Add(new TextBlock
        {
            Text = "대상을 선택하고 마스크를 그린 뒤 해당 얼굴을 추적하세요.",
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Opacity = 0.8
        });
        header.Children.Insert(1, bar);
        _manualTargetBar = bar;
        _manualTargetPicker = picker;
    }

    private void SyncManualTargetControls()
    {
        if (_manualTargetBar == null || DataContext is not WorkspaceViewModel vm)
            return;
        bool manual = vm.Mode == WorkspaceMode.Manual;
        _manualTargetBar.IsVisible = manual;
        _manualTargetBar.IsEnabled = manual && vm.ToolPanel.CanEditWorkspace;
        _syncingTargetPicker = true;
        try
        {
            if (_manualTargetPicker != null)
            {
                _manualTargetPicker.ItemsSource = manual
                    ? vm.FramePreview.ManualTargetChoices : null;
                _manualTargetPicker.SelectedItem = manual
                    ? vm.FramePreview.SelectedManualTarget : null;
            }
        }
        finally
        {
            _syncingTargetPicker = false;
        }
        bool selected = manual && vm.FramePreview.HasSelectedManualTarget;
        if (_targetTrackButton != null)
            _targetTrackButton.IsVisible = selected;
        if (_legacyNextTrackButton != null)
            _legacyNextTrackButton.IsVisible = !selected;
        if (_legacyTrackButton != null)
            _legacyTrackButton.IsVisible = !selected;
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

        vm.ToolPanel.UndoCommand.Execute(null);
        e.Handled = true;
    }
}
