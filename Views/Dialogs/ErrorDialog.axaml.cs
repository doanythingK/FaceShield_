using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FaceShield.Views.Dialogs;

public partial class ErrorDialog : Window
{
    public string Message { get; }
    public string DialogTitle { get; }

    public ErrorDialog(string title, string message)
    {
        DialogTitle = string.IsNullOrWhiteSpace(title) ? "오류" : title;
        Message = message ?? string.Empty;

        InitializeComponent();
        DataContext = this;
        Title = DialogTitle;
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
