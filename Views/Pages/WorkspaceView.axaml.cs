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
    private Button? _targetAbsentButton;
    private Button? _legacyNextTrackButton;
    private Button? _legacyTrackButton;
    private (Guid TargetId, int Frame)? _restoredUndoContext;

    public WorkspaceView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnAnyKeyDown, RoutingStrategies.Tunnel);
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
                _configuredWorkspace.FramePreview.TryCommitPendingManualTargetEdit();
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
            // ToolPanel invokes this guard synchronously before SaveRequested.
            // Retain it when the view detaches: programmatic Save calls on the
            // still-live workspace must not bypass target-owned persistence.
            vm.ToolPanel.ManualTargetExportGuard = vm.FramePreview.TryCommitPendingManualTargetEdit;
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
            if (!workspace.FramePreview.TryCommitPendingManualTargetEdit())
            {
                SyncManualTargetControls();
                return;
            }
            workspace.FramePreview.PreserveManualTargetUndo();
            Guid? previousId = workspace.FramePreview.SelectedManualTarget?.Id;
            try
            {
                workspace.FramePreview.CreateManualTargetCommand.Execute(null);
            }
            catch (Exception ex)
            {
                // Staged creation has not replaced the live workspace on a
                // failed write. Keep the picker on its original face and show
                // the failure instead of throwing from an Avalonia click event.
                workspace.FramePreview.ReportManualTargetFailure("새 얼굴 생성", ex);
                SyncManualTargetControls();
                return;
            }
            if (workspace.FramePreview.SelectedManualTarget?.Id == previousId)
            {
                // A gated/no-op command must not invalidate current Undo.
                SyncManualTargetControls();
                return;
            }
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
            if (!workspace.FramePreview.TryCommitPendingManualTargetEdit())
            {
                SyncManualTargetControls();
                return;
            }
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

        // An empty frame has no pixels for the eraser to change. Provide an
        // explicit same-face absence boundary after tracking loses a face;
        // other targets and Auto masks retain their own coverage.
        _targetAbsentButton = new Button { Content = "이 프레임부터 선택 얼굴 없음" };
        _targetAbsentButton.Click += (_, _) =>
        {
            if (DataContext is not WorkspaceViewModel workspace ||
                workspace.Mode != WorkspaceMode.Manual)
                return;
            try
            {
                if (!workspace.FramePreview.MarkSelectedManualTargetAbsentAtCurrentFrame())
                    return;
                _restoredUndoContext = null;
                workspace.FramePreview.RefreshManualTargetPreview();
                SyncManualTargetControls();
            }
            catch (Exception ex)
            {
                workspace.FramePreview.ReportManualTargetFailure("선택 얼굴 없음 지정", ex);
            }
        };
        bar.Children.Add(_targetAbsentButton);
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
        if (_targetAbsentButton != null)
            _targetAbsentButton.IsVisible = selected;
        if (_legacyNextTrackButton != null)
            _legacyNextTrackButton.IsVisible = !selected;
        if (_legacyTrackButton != null)
            _legacyTrackButton.IsVisible = !selected;
    }

    private void OnBeforeWorkspacePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_configuredWorkspace?.Mode != WorkspaceMode.Manual)
            return;
        if (!_configuredWorkspace.FramePreview.TryCommitPendingManualTargetEdit())
        {
            e.Handled = true;
            return;
        }
        // Pointer-driven timeline navigation and face selection can otherwise
        // clear the shared undo stack before the previous face/frame is saved.
        _configuredWorkspace.FramePreview.PreserveManualTargetUndo();
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
            configured.FramePreview.TryCommitPendingManualTargetEdit();
            configured.FramePreview.DetachManualTrackingContext();
        }
    }

    private void OnAnyKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not WorkspaceViewModel vm)
            return;
        if (vm.Mode == WorkspaceMode.Manual)
        {
            if (!vm.FramePreview.TryCommitPendingManualTargetEdit())
            {
                e.Handled = true;
                return;
            }
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
