// FILE: D:\WorkSpace\FaceShield\Views\Workspace\FrameListView.axaml.cs
using Avalonia.Controls;
using Avalonia.Input;
using FaceShield.ViewModels.Workspace;

namespace FaceShield.Views.Workspace;

public partial class FrameListView : UserControl
{
    public FrameListView()
    {
        InitializeComponent();

        // 워크스페이스 열리면 포커스 확보
        AttachedToVisualTree += (_, _) => Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (DataContext is not FrameListViewModel vm)
            return;

        if (vm.HandleKey(e.Key, e.KeyModifiers))
            e.Handled = true;
    }
}
