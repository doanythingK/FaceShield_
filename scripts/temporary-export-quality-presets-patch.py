from pathlib import Path

ROOT = Path.cwd()


def replace_once(relative_path: str, old: str, new: str) -> None:
    path = ROOT / relative_path
    text = path.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{relative_path}: expected one match, found {count}: {old[:120]!r}")
    path.write_text(text.replace(old, new, 1), encoding="utf-8")


def replace_count(relative_path: str, old: str, new: str, expected: int) -> None:
    path = ROOT / relative_path
    text = path.read_text(encoding="utf-8")
    count = text.count(old)
    if count != expected:
        raise SystemExit(f"{relative_path}: expected {expected} matches, found {count}: {old[:120]!r}")
    path.write_text(text.replace(old, new), encoding="utf-8")


preset_path = ROOT / "Services/Video/VideoExportQualityPreset.cs"
if preset_path.exists():
    raise SystemExit("Services/Video/VideoExportQualityPreset.cs already exists")
preset_path.write_text(
    '''namespace FaceShield.Services.Video;\n\npublic enum VideoExportQualityPreset\n{\n    SizePriority = 0,\n    Balanced = 1,\n    QualityPriority = 2\n}\n\ninternal static class VideoExportQualityPresetPolicy\n{\n    internal static double GetSourceBitrateMultiplier(VideoExportQualityPreset preset)\n        => preset switch\n        {\n            VideoExportQualityPreset.SizePriority => 1.00,\n            VideoExportQualityPreset.Balanced => 1.20,\n            VideoExportQualityPreset.QualityPriority => 1.40,\n            _ => 1.20\n        };\n\n    internal static int ApplyBitrateMultiplier(int bitrate, VideoExportQualityPreset preset)\n    {\n        if (bitrate <= 0)\n            return 0;\n\n        double scaled = bitrate * GetSourceBitrateMultiplier(preset);\n        if (!double.IsFinite(scaled) || scaled <= 0)\n            return bitrate;\n\n        return VideoExportFidelityPolicy.ClampBitrate(\n            (long)Math.Round(scaled, MidpointRounding.AwayFromZero));\n    }\n}\n''',
    encoding="utf-8")

replace_once(
    "ViewModels/Workspace/ToolPanelViewModel.cs",
    '''using FaceShield.Enums.Workspace;\nusing System;\n''',
    '''using FaceShield.Enums.Workspace;\nusing FaceShield.Services.Video;\nusing System;\nusing System.Collections.Generic;\n''')

replace_once(
    "ViewModels/Workspace/ToolPanelViewModel.cs",
    '''namespace FaceShield.ViewModels.Workspace\n{\n    public partial class ToolPanelViewModel : ViewModelBase\n''',
    '''namespace FaceShield.ViewModels.Workspace\n{\n    public sealed record ExportQualityChoice(\n        VideoExportQualityPreset Preset,\n        string Label,\n        string Description)\n    {\n        public override string ToString() => Label;\n    }\n\n    public partial class ToolPanelViewModel : ViewModelBase\n''')

replace_once(
    "ViewModels/Workspace/ToolPanelViewModel.cs",
    '''        private const int MaxBlurRadiusValue = 40;\n\n        [ObservableProperty]\n        private EditMode currentMode = EditMode.None;\n''',
    '''        private const int MaxBlurRadiusValue = 40;\n\n        private static readonly ExportQualityChoice SizePriorityExportQuality = new(\n            VideoExportQualityPreset.SizePriority,\n            "용량 우선",\n            "원본 영상 bitrate와 비슷한 수준(1.00×)으로 제한합니다.");\n        private static readonly ExportQualityChoice BalancedExportQuality = new(\n            VideoExportQualityPreset.Balanced,\n            "균형 (권장)",\n            "재인코딩 화질 여유를 위해 원본 영상 bitrate의 최대 1.20×를 사용합니다.");\n        private static readonly ExportQualityChoice QualityPriorityExportQuality = new(\n            VideoExportQualityPreset.QualityPriority,\n            "화질 우선",\n            "복잡한 장면의 재인코딩 손실을 줄이기 위해 원본 영상 bitrate의 최대 1.40×를 사용합니다.");\n\n        public IReadOnlyList<ExportQualityChoice> ExportQualityChoices { get; } = new[]\n        {\n            SizePriorityExportQuality,\n            BalancedExportQuality,\n            QualityPriorityExportQuality\n        };\n\n        [ObservableProperty]\n        private ExportQualityChoice selectedExportQuality = BalancedExportQuality;\n\n        public VideoExportQualityPreset ExportQualityPreset => SelectedExportQuality.Preset;\n\n        [ObservableProperty]\n        private EditMode currentMode = EditMode.None;\n''')

replace_once(
    "ViewModels/Workspace/ToolPanelViewModel.cs",
    '''        partial void OnIsExportRunningChanged(bool value)\n        {\n            OnPropertyChanged(nameof(ShowAutoProgress));\n            OnPropertyChanged(nameof(CanEditWorkspace));\n        }\n\n\n        public event Action? UndoRequested;\n''',
    '''        partial void OnIsExportRunningChanged(bool value)\n        {\n            OnPropertyChanged(nameof(ShowAutoProgress));\n            OnPropertyChanged(nameof(CanEditWorkspace));\n        }\n\n        partial void OnSelectedExportQualityChanged(ExportQualityChoice value)\n        {\n            OnPropertyChanged(nameof(ExportQualityPreset));\n        }\n\n\n        public event Action? UndoRequested;\n''')

replace_once(
    "Views/Workspace/ToolPanelView.axaml",
    '''      </StackPanel>\n\n      <StackPanel Orientation="Horizontal" Spacing="8" IsVisible="{Binding ShowAutoProgress}">\n''',
    '''      </StackPanel>\n\n      <Border Background="#1E1E1E" CornerRadius="6" Padding="8,6"\n              IsEnabled="{Binding CanEditWorkspace}">\n        <StackPanel Orientation="Horizontal" Spacing="10">\n          <TextBlock Text="내보내기 화질/용량" VerticalAlignment="Center"/>\n          <ComboBox Width="150"\n                    ItemsSource="{Binding ExportQualityChoices}"\n                    SelectedItem="{Binding SelectedExportQuality, Mode=TwoWay}"/>\n          <TextBlock MaxWidth="460"\n                     Text="{Binding SelectedExportQuality.Description}"\n                     Opacity="0.82"\n                     VerticalAlignment="Center"\n                     TextWrapping="Wrap"/>\n        </StackPanel>\n      </Border>\n\n      <StackPanel Orientation="Horizontal" Spacing="8" IsVisible="{Binding ShowAutoProgress}">\n''')

replace_once(
    "ViewModels/Workspace/WorkspaceExportCoordinator.cs",
    '''        string? runId = null,\n        AutoMaskRunSummary? autoRunSummary = null,\n        AutoMaskOptions? autoRunOptions = null)\n''',
    '''        string? runId = null,\n        AutoMaskRunSummary? autoRunSummary = null,\n        AutoMaskOptions? autoRunOptions = null,\n        VideoExportQualityPreset qualityPreset = VideoExportQualityPreset.Balanced)\n''')

replace_once(
    "ViewModels/Workspace/WorkspaceExportCoordinator.cs",
    '''                runId,\n                autoRunSummary,\n                autoRunOptions);\n''',
    '''                runId,\n                autoRunSummary,\n                autoRunOptions,\n                qualityPreset);\n''')

replace_once(
    "ViewModels/Workspace/WorkspaceExportCoordinator.cs",
    '''        bool updateToolPanel,\n        string? runId,\n        AutoMaskRunSummary? autoRunSummary,\n        AutoMaskOptions? autoRunOptions)\n''',
    '''        bool updateToolPanel,\n        string? runId,\n        AutoMaskRunSummary? autoRunSummary,\n        AutoMaskOptions? autoRunOptions,\n        VideoExportQualityPreset qualityPreset)\n''')

replace_once(
    "ViewModels/Workspace/WorkspaceExportCoordinator.cs",
    '''                $"[ExportRunConfig] runId={exportRunId}, blurRadius={blurRadius}, allowHybridCopy={hybridPolicy.allowHybridCopy.ToString().ToLowerInvariant()}, disableReasons={FormatTextListForLog(hybridPolicy.disableReasons)}");\n''',
    '''                $"[ExportRunConfig] runId={exportRunId}, blurRadius={blurRadius}, qualityPreset={qualityPreset}, allowHybridCopy={hybridPolicy.allowHybridCopy.ToString().ToLowerInvariant()}, disableReasons={FormatTextListForLog(hybridPolicy.disableReasons)}");\n''')

replace_once(
    "ViewModels/Workspace/WorkspaceExportCoordinator.cs",
    '''                    exportRunId,\n                    allowHybridCopy: hybridPolicy.allowHybridCopy,\n                    allowOutputOverwrite: allowOutputOverwrite);\n''',
    '''                    exportRunId,\n                    allowHybridCopy: hybridPolicy.allowHybridCopy,\n                    allowOutputOverwrite: allowOutputOverwrite,\n                    qualityPreset: qualityPreset);\n''')

replace_once(
    "ViewModels/Pages/WorkspaceViewModel.cs",
    '''                runId,\n                autoRunSummary,\n                autoRunOptions);\n''',
    '''                runId,\n                autoRunSummary,\n                autoRunOptions,\n                qualityPreset: ToolPanel.ExportQualityPreset);\n''')

replace_once(
    "Services/Video/VideoExportService.cs",
    '''        string? runId = null,\n        bool allowHybridCopy = false,\n        bool allowOutputOverwrite = true)\n''',
    '''        string? runId = null,\n        bool allowHybridCopy = false,\n        bool allowOutputOverwrite = true,\n        VideoExportQualityPreset qualityPreset = VideoExportQualityPreset.SizePriority)\n''')

replace_once(
    "Services/Video/VideoExportService.cs",
    '''                    forceSafeEncoding: attempt.ForceSafeEncoding,\n                    forceAudioTranscode: attempt.ForceAudioTranscode,\n                    forceH264Fallback: attempt.ForceH264Fallback));\n''',
    '''                    forceSafeEncoding: attempt.ForceSafeEncoding,\n                    forceAudioTranscode: attempt.ForceAudioTranscode,\n                    forceH264Fallback: attempt.ForceH264Fallback,\n                    qualityPreset: qualityPreset));\n''')

replace_once(
    "Services/Video/VideoExportService.cs",
    '''        bool forceSafeEncoding,\n        bool forceAudioTranscode,\n        bool forceH264Fallback,\n        bool allowPacketDropRetry = true)\n''',
    '''        bool forceSafeEncoding,\n        bool forceAudioTranscode,\n        bool forceH264Fallback,\n        VideoExportQualityPreset qualityPreset,\n        bool allowPacketDropRetry = true)\n''')

replace_count(
    "Services/Video/VideoExportService.cs",
    '''                forceSoftwareEncoder,\n                forceSafeEncoding,\n                hdrMetadata);\n''',
    '''                forceSoftwareEncoder,\n                forceSafeEncoding,\n                hdrMetadata,\n                qualityPreset);\n''',
    2)

replace_once(
    "Services/Video/VideoExportService.cs",
    '''                forceSafeEncoding: forceSafeEncoding,\n                forceAudioTranscode: forceAudioTranscode,\n                forceH264Fallback: forceH264Fallback,\n                allowPacketDropRetry: false);\n''',
    '''                forceSafeEncoding: forceSafeEncoding,\n                forceAudioTranscode: forceAudioTranscode,\n                forceH264Fallback: forceH264Fallback,\n                qualityPreset: qualityPreset,\n                allowPacketDropRetry: false);\n''')

replace_once(
    "Services/Video/VideoEncoderContextPolicy.cs",
    '''        bool forceSoftwareEncoder,\n        bool forceSafeEncoding,\n        VideoHdrMetadata? hdrMetadata)\n''',
    '''        bool forceSoftwareEncoder,\n        bool forceSafeEncoding,\n        VideoHdrMetadata? hdrMetadata,\n        VideoExportQualityPreset qualityPreset)\n''')

replace_count(
    "Services/Video/VideoEncoderContextPolicy.cs",
    '''                forceSafeEncoding,\n                hdrMetadata);\n''',
    '''                forceSafeEncoding,\n                hdrMetadata,\n                qualityPreset);\n''',
    2)

replace_once(
    "Services/Video/VideoEncoderContextPolicy.cs",
    '''        out EncoderQualityConfiguration qualityConfiguration,\n        out string? error,\n        bool forceSafeEncoding,\n        VideoHdrMetadata? hdrMetadata)\n''',
    '''        out EncoderQualityConfiguration qualityConfiguration,\n        out string? error,\n        bool forceSafeEncoding,\n        VideoHdrMetadata? hdrMetadata,\n        VideoExportQualityPreset qualityPreset)\n''')

replace_once(
    "Services/Video/VideoEncoderContextPolicy.cs",
    '''                ctx->height,\n                ctx->framerate,\n                encoder->id);\n''',
    '''                ctx->height,\n                ctx->framerate,\n                encoder->id,\n                qualityPreset);\n''')

replace_once(
    "Services/Video/VideoExportFidelityPolicy.cs",
    '''    internal static int ResolveHighQualityTargetBitrate(\n        long sourceBitrate,\n        int width,\n        int height,\n        AVRational framerate,\n        AVCodecID codecId)\n    {\n        int resolutionFloor = EstimateHighQualityBitrate(width, height, framerate);\n        int boundedSourceBitrate = ClampBitrate(sourceBitrate);\n        if (boundedSourceBitrate > 0)\n            return ResolveKnownSourceTargetBitrate(boundedSourceBitrate, resolutionFloor);\n\n        return Math.Max(resolutionFloor, 2_000_000);\n    }\n\n    internal static int ResolveKnownSourceTargetBitrate(\n        long sourceBitrate,\n        int resolutionFloor)\n    {\n        // Once the source video rate is known, keep it authoritative. Raising the\n        // target above the source made re-encoded blur exports unnecessarily large.\n        return ClampBitrate(sourceBitrate);\n    }\n''',
    '''    internal static int ResolveHighQualityTargetBitrate(\n        long sourceBitrate,\n        int width,\n        int height,\n        AVRational framerate,\n        AVCodecID codecId)\n        => ResolveHighQualityTargetBitrate(\n            sourceBitrate,\n            width,\n            height,\n            framerate,\n            codecId,\n            VideoExportQualityPreset.SizePriority);\n\n    internal static int ResolveHighQualityTargetBitrate(\n        long sourceBitrate,\n        int width,\n        int height,\n        AVRational framerate,\n        AVCodecID codecId,\n        VideoExportQualityPreset qualityPreset)\n    {\n        int resolutionFloor = EstimateHighQualityBitrate(width, height, framerate);\n        int boundedSourceBitrate = ClampBitrate(sourceBitrate);\n        if (boundedSourceBitrate > 0)\n        {\n            return ResolveKnownSourceTargetBitrate(\n                boundedSourceBitrate,\n                resolutionFloor,\n                qualityPreset);\n        }\n\n        int fallback = Math.Max(resolutionFloor, 2_000_000);\n        return VideoExportQualityPresetPolicy.ApplyBitrateMultiplier(\n            fallback,\n            qualityPreset);\n    }\n\n    internal static int ResolveKnownSourceTargetBitrate(\n        long sourceBitrate,\n        int resolutionFloor)\n        => ResolveKnownSourceTargetBitrate(\n            sourceBitrate,\n            resolutionFloor,\n            VideoExportQualityPreset.SizePriority);\n\n    internal static int ResolveKnownSourceTargetBitrate(\n        long sourceBitrate,\n        int resolutionFloor,\n        VideoExportQualityPreset qualityPreset)\n    {\n        // The source rate remains the baseline. The user-selected preset may grant\n        // bounded headroom for a second lossy encode, while rc_max_rate keeps the\n        // old multi-x file-size regression from returning.\n        int boundedSourceBitrate = ClampBitrate(sourceBitrate);\n        return VideoExportQualityPresetPolicy.ApplyBitrateMultiplier(\n            boundedSourceBitrate,\n            qualityPreset);\n    }\n''')

verify_path = ROOT / "scripts/verify-export-quality-presets.ps1"
if verify_path.exists():
    raise SystemExit("scripts/verify-export-quality-presets.ps1 already exists")
verify_path.write_text(
    '''param()\n\n$ErrorActionPreference = "Stop"\n\n$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path\n$work = Join-Path $repo ".tmp\\export-quality-presets"\n$project = Join-Path $work "ExportQualityPresetsHarness.csproj"\n$program = Join-Path $work "Program.cs"\n\ntry {\n    New-Item -ItemType Directory -Force -Path $work | Out-Null\n\n    @"\n<Project Sdk="Microsoft.NET.Sdk">\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n    <TargetFramework>net8.0</TargetFramework>\n    <Nullable>enable</Nullable>\n  </PropertyGroup>\n  <ItemGroup>\n    <ProjectReference Include="$repo\\FaceShield.csproj" />\n  </ItemGroup>\n</Project>\n"@ | Set-Content -Encoding UTF8 $project\n\n    @'\nusing FaceShield.Services.Video;\nusing FaceShield.ViewModels.Workspace;\nusing System;\nusing System.Linq;\nusing System.Reflection;\n\nType fidelityPolicy = typeof(VideoExportService).Assembly.GetType(\n    "FaceShield.Services.Video.VideoExportFidelityPolicy")\n    ?? throw new InvalidOperationException("VideoExportFidelityPolicy not found.");\nMethodInfo resolveKnown = fidelityPolicy.GetMethod(\n    "ResolveKnownSourceTargetBitrate",\n    BindingFlags.NonPublic | BindingFlags.Static,\n    binder: null,\n    types: new[] { typeof(long), typeof(int), typeof(VideoExportQualityPreset) },\n    modifiers: null)\n    ?? throw new InvalidOperationException("Preset-aware bitrate resolver not found.");\n\nconst long source = 10_000_000;\nAssertTarget(VideoExportQualityPreset.SizePriority, 10_000_000);\nAssertTarget(VideoExportQualityPreset.Balanced, 12_000_000);\nAssertTarget(VideoExportQualityPreset.QualityPriority, 14_000_000);\n\nvar toolPanel = new ToolPanelViewModel();\nif (toolPanel.ExportQualityPreset != VideoExportQualityPreset.Balanced)\n    throw new InvalidOperationException($"Expected Balanced UI default, got {toolPanel.ExportQualityPreset}.");\nif (toolPanel.ExportQualityChoices.Count != 3)\n    throw new InvalidOperationException($"Expected 3 UI choices, got {toolPanel.ExportQualityChoices.Count}.");\nstring labels = string.Join("|", toolPanel.ExportQualityChoices.Select(static choice => choice.Label));\nif (labels != "용량 우선|균형 (권장)|화질 우선")\n    throw new InvalidOperationException($"Unexpected UI labels: {labels}");\n\nConsole.WriteLine("[ExportQualityPresets] PASS size=1.00 balanced=1.20 quality=1.40 default=Balanced");\n\nvoid AssertTarget(VideoExportQualityPreset preset, int expected)\n{\n    object? value = resolveKnown.Invoke(null, new object[] { source, 6_000_000, preset });\n    int actual = value is int bitrate\n        ? bitrate\n        : throw new InvalidOperationException($"No bitrate result for {preset}.");\n    if (actual != expected)\n        throw new InvalidOperationException($"{preset}: expected {expected}, got {actual}.");\n}\n'@ | Set-Content -Encoding UTF8 $program\n\n    dotnet run --project $project -c Release -p:UseAppHost=false --nologo\n    if ($LASTEXITCODE -ne 0) {\n        throw "Export quality preset harness failed: $LASTEXITCODE"\n    }\n}\nfinally {\n    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue\n}\n''',
    encoding="utf-8")

print("export quality preset patch applied")
