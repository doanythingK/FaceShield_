using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace FaceShield.Views.Dialogs;

public partial class ErrorDialog : Window
{
    public string Message { get; }
    public string DialogTitle { get; }

    public ErrorDialog()
        : this("오류", string.Empty)
    {
    }

    public ErrorDialog(string title, string message)
    {
        DialogTitle = string.IsNullOrWhiteSpace(title) ? "오류" : title;
        Message = message ?? string.Empty;

        InitializeComponent();
        DataContext = this;
        Title = DialogTitle;
    }

    public new async Task ShowDialog(Window owner)
    {
        try
        {
            await base.ShowDialog(owner);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[ErrorDialog] dialog display suppressed during shutdown/detach: {ex.Message}");
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnOk(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
    }
}
