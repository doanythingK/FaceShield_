using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using FaceShield.ViewModels.Pages;

namespace FaceShield.Views.Pages;

public partial class WorkspaceView : UserControl
{
    public WorkspaceView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnAnyKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnAnyKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not WorkspaceViewModel vm)
            return;

        bool isUndo = (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))
            && e.Key == Key.Z;
        if (!isUndo)
            return;

        vm.FramePreview.Undo();
        e.Handled = true;
    }
}
