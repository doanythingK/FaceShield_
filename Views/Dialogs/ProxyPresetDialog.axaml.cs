using Avalonia.Controls;
using Avalonia.Interactivity;
using FaceShield.Services.Video;

namespace FaceShield.Views.Dialogs
{
    public partial class ProxyPresetDialog : Window
    {
        public DetectionProxyPreset CurrentPreset { get; }
        public string CurrentLabel => $"현재 선택: {LabelFor(CurrentPreset)}";
        private bool _selected;

        public ProxyPresetDialog(DetectionProxyPreset current)
        {
            InitializeComponent();
            CurrentPreset = current;
            DataContext = this;
            Closing += OnClosing;
        }

        private static string LabelFor(DetectionProxyPreset preset)
        {
            return preset switch
            {
                DetectionProxyPreset.OptionA => "A옵션",
                DetectionProxyPreset.OptionB => "B옵션",
                _ => "원래 설정"
            };
        }

        private void OnDefaultClick(object? sender, RoutedEventArgs e)
        {
            _selected = true;
            Close(DetectionProxyPreset.Default);
        }

        private void OnOptionAClick(object? sender, RoutedEventArgs e)
        {
            _selected = true;
            Close(DetectionProxyPreset.OptionA);
        }

        private void OnOptionBClick(object? sender, RoutedEventArgs e)
        {
            _selected = true;
            Close(DetectionProxyPreset.OptionB);
        }

        private void OnClosing(object? sender, WindowClosingEventArgs e)
        {
            if (!_selected)
                e.Cancel = true;
        }
    }
}
