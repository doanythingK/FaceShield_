param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$work = Join-Path $repo ".tmp\export-quality-presets"
$project = Join-Path $work "ExportQualityPresetsHarness.csproj"
$program = Join-Path $work "Program.cs"

try {
    New-Item -ItemType Directory -Force -Path $work | Out-Null

    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$repo\FaceShield.csproj" />
  </ItemGroup>
</Project>
"@ | Set-Content -Encoding UTF8 $project

    @'
using FaceShield.Services.Video;
using FaceShield.ViewModels.Workspace;
using System;
using System.Linq;
using System.Reflection;

Type fidelityPolicy = typeof(VideoExportService).Assembly.GetType(
    "FaceShield.Services.Video.VideoExportFidelityPolicy")
    ?? throw new InvalidOperationException("VideoExportFidelityPolicy not found.");
MethodInfo resolveKnown = fidelityPolicy.GetMethod(
    "ResolveKnownSourceTargetBitrate",
    BindingFlags.NonPublic | BindingFlags.Static,
    binder: null,
    types: new[] { typeof(long), typeof(int), typeof(VideoExportQualityPreset) },
    modifiers: null)
    ?? throw new InvalidOperationException("Preset-aware bitrate resolver not found.");

const long source = 10_000_000;
AssertTarget(VideoExportQualityPreset.SizePriority, 10_000_000);
AssertTarget(VideoExportQualityPreset.Balanced, 12_000_000);
AssertTarget(VideoExportQualityPreset.QualityPriority, 14_000_000);

var toolPanel = new ToolPanelViewModel();
if (toolPanel.ExportQualityPreset != VideoExportQualityPreset.Balanced)
    throw new InvalidOperationException($"Expected Balanced UI default, got {toolPanel.ExportQualityPreset}.");
if (toolPanel.ExportQualityChoices.Count != 3)
    throw new InvalidOperationException($"Expected 3 UI choices, got {toolPanel.ExportQualityChoices.Count}.");
string labels = string.Join("|", toolPanel.ExportQualityChoices.Select(static choice => choice.Label));
if (labels != "용량 우선|균형 (권장)|화질 우선")
    throw new InvalidOperationException($"Unexpected UI labels: {labels}");

Console.WriteLine("[ExportQualityPresets] PASS size=1.00 balanced=1.20 quality=1.40 default=Balanced");

void AssertTarget(VideoExportQualityPreset preset, int expected)
{
    object? value = resolveKnown.Invoke(null, new object[] { source, 6_000_000, preset });
    int actual = value is int bitrate
        ? bitrate
        : throw new InvalidOperationException($"No bitrate result for {preset}.");
    if (actual != expected)
        throw new InvalidOperationException($"{preset}: expected {expected}, got {actual}.");
}
'@ | Set-Content -Encoding UTF8 $program

    dotnet run --project $project -c Release -p:UseAppHost=false --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Export quality preset harness failed: $LASTEXITCODE"
    }
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
