using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using FaceShield.ViewModels.Pages;
using FaceShield.Views.Dialogs;
using System;
using System.Threading.Tasks;

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

        try
        {
            await vm.PickVideoAsync(storageProvider);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            await ShowPickerErrorAsync(vm, "영상 파일 선택 실패", ex);
        }
    }

    private async void PickYoloModel_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not HomePageViewModel vm)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        var storageProvider = topLevel?.StorageProvider;
        if (storageProvider is null)
            return;

        try
        {
            await vm.PickYoloModelAsync(storageProvider);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            await ShowPickerErrorAsync(vm, "모델 파일 선택 실패", ex);
        }
    }

    private async Task ShowPickerErrorAsync(
        HomePageViewModel vm,
        string title,
        Exception exception)
    {
        if (vm.IsShutdownRequested)
            return;

        try
        {
            if (TopLevel.GetTopLevel(this) is not Window owner ||
                vm.IsShutdownRequested)
            {
                return;
            }

            var dialog = new ErrorDialog(title, exception.Message);
            await dialog.ShowDialog(owner);
        }
        catch (Exception dialogException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[HomePage] picker error dialog suppressed during shutdown/detach: {dialogException.Message}");
        }
    }
}
