using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using FaceShield.ViewModels.Pages;

namespace FaceShield.Views.Pages;

public partial class HomePageView : UserControl
{
    public HomePageView()
    {
        InitializeComponent();
    }

    private async void PickVideo_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not HomePageViewModel vm)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        var storageProvider = topLevel?.StorageProvider;
        if (storageProvider is null)
            return;

        await vm.PickVideoAsync(storageProvider);
    }
}