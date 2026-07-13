param(
    [switch]$KeepHarness
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$work = Join-Path $repo ".tmp\auto-processing-mode-migration"
$project = Join-Path $work "AutoProcessingModeMigrationHarness.csproj"
$program = Join-Path $work "Program.cs"

New-Item -ItemType Directory -Force -Path $work | Out-Null

try {
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
using FaceShield.Services.Analysis;
using FaceShield.Services.Workspace;
using FaceShield.ViewModels.Pages;
using System;
using System.Reflection;
using System.Text.Json;

var resolver = typeof(HomePageViewModel).GetMethod(
    "ResolveSavedAutoProcessingMode",
    BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("Saved processing mode resolver was not found.");

AssertResolved("missing", 0, null, AutoMaskProcessingMode.Tracked);
AssertResolved("v9-raw", 9, (int)AutoMaskProcessingMode.Raw, AutoMaskProcessingMode.Tracked);
AssertResolved("v9-legacy", 9, (int)AutoMaskProcessingMode.Legacy, AutoMaskProcessingMode.Legacy);
AssertResolved("v10-raw", 10, (int)AutoMaskProcessingMode.Raw, AutoMaskProcessingMode.Raw);
AssertResolved("v10-tracked", 10, (int)AutoMaskProcessingMode.Tracked, AutoMaskProcessingMode.Tracked);
AssertResolved("invalid", 10, 999, AutoMaskProcessingMode.Tracked);

var missingState = JsonSerializer.Deserialize<AutoSettingsState>("{}")
    ?? throw new InvalidOperationException("Missing settings JSON did not deserialize.");
if (missingState.ProcessingMode != (int)AutoMaskProcessingMode.Tracked)
    throw new InvalidOperationException($"Missing JSON mode resolved to {missingState.ProcessingMode}.");

Console.WriteLine(
    "[AutoProcessingModeMigrationVerify] PASS " +
    "missing=Tracked v9Raw=Tracked v9Legacy=Legacy v10Raw=Raw invalid=Tracked jsonMissing=Tracked");

void AssertResolved(
    string name,
    int settingsVersion,
    int? savedValue,
    AutoMaskProcessingMode expected)
{
    object? result = resolver.Invoke(null, new object?[] { settingsVersion, savedValue });
    if (result is not AutoMaskProcessingMode actual || actual != expected)
        throw new InvalidOperationException($"{name}: expected={expected}, actual={result ?? "null"}");
}
'@ | Set-Content -Encoding UTF8 $program

    dotnet run --project $project --configuration Debug
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}
finally {
    if (-not $KeepHarness.IsPresent -and (Test-Path $work)) {
        Remove-Item -Recurse -Force -Path $work
    }
}
