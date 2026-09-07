from pathlib import Path

panel_path = Path('ViewModels/Workspace/ToolPanelViewModel.cs')
view_path = Path('Views/Workspace/ToolPanelView.axaml')
panel_original = panel_path.read_bytes()
view_original = view_path.read_bytes()

# Apply the semantic patch to all other files first.
exec(compile(
    Path('scripts/temporary-export-quality-presets-patch-v2.py').read_text(encoding='utf-8'),
    'scripts/temporary-export-quality-presets-patch-v2.py',
    'exec'), {'__name__': '__main__'})


def replace_once(data: bytes, old: bytes, new: bytes, label: str) -> bytes:
    count = data.count(old)
    if count != 1:
        raise SystemExit(f'{label}: expected one byte-level match, found {count}')
    return data.replace(old, new, 1)

panel = panel_original
panel = replace_once(
    panel,
    b'using FaceShield.Enums.Workspace;\nusing System;\n',
    'using FaceShield.Enums.Workspace;\nusing FaceShield.Services.Video;\nusing System;\nusing System.Collections.Generic;\n'.encode('utf-8'),
    'panel-usings')
panel = replace_once(
    panel,
    b'namespace FaceShield.ViewModels.Workspace\n{\n    public partial class ToolPanelViewModel : ViewModelBase\n',
    '''namespace FaceShield.ViewModels.Workspace\n{\n    public sealed record ExportQualityChoice(\n        VideoExportQualityPreset Preset,\n        string Label,\n        string Description)\n    {\n        public override string ToString() => Label;\n    }\n\n    public partial class ToolPanelViewModel : ViewModelBase\n'''.encode('utf-8'),
    'panel-choice-record')
panel = replace_once(
    panel,
    b'        private const int MaxBlurRadiusValue = 40;\n\n        [ObservableProperty]\n        private EditMode currentMode = EditMode.None;\n',
    '''        private const int MaxBlurRadiusValue = 40;\n\n        private static readonly ExportQualityChoice SizePriorityExportQuality = new(\n            VideoExportQualityPreset.SizePriority,\n            "용량 우선",\n            "원본 영상 bitrate와 비슷한 수준(1.00×)으로 제한합니다.");\n        private static readonly ExportQualityChoice BalancedExportQuality = new(\n            VideoExportQualityPreset.Balanced,\n            "균형 (권장)",\n            "재인코딩 화질 여유를 위해 원본 영상 bitrate의 최대 1.20×를 사용합니다.");\n        private static readonly ExportQualityChoice QualityPriorityExportQuality = new(\n            VideoExportQualityPreset.QualityPriority,\n            "화질 우선",\n            "복잡한 장면의 재인코딩 손실을 줄이기 위해 원본 영상 bitrate의 최대 1.40×를 사용합니다.");\n\n        public IReadOnlyList<ExportQualityChoice> ExportQualityChoices { get; } = new[]\n        {\n            SizePriorityExportQuality,\n            BalancedExportQuality,\n            QualityPriorityExportQuality\n        };\n\n        [ObservableProperty]\n        private ExportQualityChoice selectedExportQuality = BalancedExportQuality;\n\n        public VideoExportQualityPreset ExportQualityPreset => SelectedExportQuality.Preset;\n\n        [ObservableProperty]\n        private EditMode currentMode = EditMode.None;\n'''.encode('utf-8'),
    'panel-preset-state')
panel = replace_once(
    panel,
    b'        partial void OnIsExportRunningChanged(bool value)\n        {\n            OnPropertyChanged(nameof(ShowAutoProgress));\n            OnPropertyChanged(nameof(CanEditWorkspace));\n        }\n\n\n        public event Action? UndoRequested;\n',
    '''        partial void OnIsExportRunningChanged(bool value)\n        {\n            OnPropertyChanged(nameof(ShowAutoProgress));\n            OnPropertyChanged(nameof(CanEditWorkspace));\n        }\n\n        partial void OnSelectedExportQualityChanged(ExportQualityChoice value)\n        {\n            OnPropertyChanged(nameof(ExportQualityPreset));\n        }\n\n\n        public event Action? UndoRequested;\n'''.encode('utf-8'),
    'panel-change-handler')
panel_path.write_bytes(panel)

view = view_original
old_view = '''      </StackPanel>\n\n      <StackPanel Orientation="Horizontal" Spacing="8" IsVisible="{Binding ShowAutoProgress}">\n'''.replace('\n', '\r\n').encode('utf-8')
new_view = '''      </StackPanel>\n\n      <Border Background="#1E1E1E" CornerRadius="6" Padding="8,6"\n              IsEnabled="{Binding CanEditWorkspace}">\n        <StackPanel Orientation="Horizontal" Spacing="10">\n          <TextBlock Text="내보내기 화질/용량" VerticalAlignment="Center"/>\n          <ComboBox Width="150"\n                    ItemsSource="{Binding ExportQualityChoices}"\n                    SelectedItem="{Binding SelectedExportQuality, Mode=TwoWay}"/>\n          <TextBlock MaxWidth="460"\n                     Text="{Binding SelectedExportQuality.Description}"\n                     Opacity="0.82"\n                     VerticalAlignment="Center"\n                     TextWrapping="Wrap"/>\n        </StackPanel>\n      </Border>\n\n      <StackPanel Orientation="Horizontal" Spacing="8" IsVisible="{Binding ShowAutoProgress}">\n'''.replace('\n', '\r\n').encode('utf-8')
view = replace_once(view, old_view, new_view, 'view-quality-selector')
view_path.write_bytes(view)

print('export quality preset v3 patch applied with original UI line endings')
