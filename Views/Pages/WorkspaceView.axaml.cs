using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using FaceShield.Enums.Workspace;
using FaceShield.ViewModels.Pages;
using FaceShield.ViewModels.Workspace;
using System;
using System.ComponentModel;
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
    private (Guid TargetId, int Frame)? _restoredUndoContext;

    public WorkspaceView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnAnyKeyDown, RoutingStrategies.Tunnel);
        // UI navigation must flush target-owned alpha before its ordinary
        // handler can invoke the legacy/global PersistCurrentMask path.
        AddHandler(PointerPressedEvent, OnBeforeWorkspacePointerPressed, RoutingStrategies.Tunnel);
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
            if (_configuredWorkspace != null)
            {
                _configuredWorkspace.FramePreview.PropertyChanged -= OnFramePreviewPropertyChanged;
                _configuredWorkspace.FramePreview.DetachManualTrackingContext();
            }
            _configuredWorkspace = vm;
            _restoredUndoContext = null;
            vm.FramePreview.PropertyChanged += OnFramePreviewPropertyChanged;
        }
        vm.ConfigureManualTrackingOwnership();
        vm.FramePreview.ConfigureManualMaskKeyframes(
            vm.Mode == WorkspaceMode.Manual,
            vm.FrameList.VideoPath,
            vm.FrameList.TotalFrames);
        if (vm.Mode == WorkspaceMode.Manual)
        {
            vm.FramePreview.ConfigureManualTargets(vm.FrameList.VideoPath);
            vm.FramePreview.RestorePersistedManualTargetSelection();
            vm.FramePreview.RefreshManualTargetPreview();
        }
        vm.ToolPanel.ManualTargetExportGuard = null;
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
        bar.Bind(Control.IsEnabledProperty, new Binding("ToolPanel.CanEditWorkspace"));
        bar.Children.Add(new TextBlock
        {
            Text = "수동 얼굴",
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        });
        var add = new Button { Content = "새 얼굴" };
        add.Click += (_, _) =>
        {
            if (DataContext is not WorkspaceViewModel workspace ||
                workspace.Mode != WorkspaceMode.Manual)
                return;
            workspace.FramePreview.CommitPendingManualTargetEdit();
            workspace.FramePreview.PreserveManualTargetUndo();
            workspace.FramePreview.CreateManualTargetCommand.Execute(null);
            workspace.FramePreview.PersistManualTargetSelection();
            _restoredUndoContext = null;
            workspace.FramePreview.RestoreManualTargetUndo();
            SyncManualTargetControls();
            workspace.FramePreview.RefreshManualTargetPreview();
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
            workspace.FramePreview.CommitPendingManualTargetEdit();
            workspace.FramePreview.PreserveManualTargetUndo();
            workspace.FramePreview.SelectedManualTarget = choice;
            if (workspace.FramePreview.SelectedManualTarget?.Id == choice.Id)
                workspace.FramePreview.PersistManualTargetSelection();
            _restoredUndoContext = null;
            workspace.FramePreview.RestoreManualTargetUndo();
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

    private void OnBeforeWorkspacePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_configuredWorkspace?.Mode == WorkspaceMode.Manual)
            _configuredWorkspace.FramePreview.CommitPendingManualTargetEdit();
    }

    private void OnFramePreviewPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FramePreviewViewModel.PreviewBitmap) ||
            _configuredWorkspace?.Mode != WorkspaceMode.Manual)
            return;
        FramePreviewViewModel preview = _configuredWorkspace.FramePreview;
        Guid? targetId = preview.SelectedManualTarget?.Id;
        int frame = preview.ManualTargetEditableFrameIndex;
        if (!targetId.HasValue || frame < 0 ||
            frame != _configuredWorkspace.FrameList.SelectedFrameIndex)
            return;
        var context = (targetId.Value, frame);
        if (_restoredUndoContext == context)
            return;
        _restoredUndoContext = context;
        preview.RestoreManualTargetUndo();
    }

    private void DetachManualMaskTimeline()
    {
        WorkspaceViewModel? configured = _configuredWorkspace;
        _configuredWorkspace = null;
        _restoredUndoContext = null;
        if (configured != null)
        {
            configured.FramePreview.PropertyChanged -= OnFramePreviewPropertyChanged;
            configured.FramePreview.CommitPendingManualTargetEdit();
            configured.FramePreview.DetachManualTrackingContext();
        }
    }

    private void OnAnyKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not WorkspaceViewModel vm)
            return;
        if (vm.Mode == WorkspaceMode.Manual)
        {
            vm.FramePreview.CommitPendingManualTargetEdit();
            if (e.Key is Key.Left or Key.Right or Key.Home or Key.End or
                Key.PageUp or Key.PageDown)
                vm.FramePreview.PreserveManualTargetUndo();
        }
        bool isUndo = (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))
            && e.Key == Key.Z;
        if (!isUndo || !vm.ToolPanel.CanEditWorkspace)
            return;
        vm.ToolPanel.UndoCommand.Execute(null);
        e.Handled = true;
    }
}
