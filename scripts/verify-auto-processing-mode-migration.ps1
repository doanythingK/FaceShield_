param([switch]$KeepHarness)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$homeText = Get-Content -Raw (Join-Path $repo "ViewModels\Pages\HomePageViewModel.cs")
$properties = @(
    "EnablePostProcessing",
    "EnableRoiPostProcess",
    "EnableYoloWeakIsolatedCleanup",
    "EnableYoloGapFill",
    "EnableYoloSceneCutCarryCleanup",
    "EnableYoloTemporalSmoothing",
    "EnableYoloRiskCascade"
)

function Assert-SourceMatch([string]$Name, [string]$Text, [string]$Pattern) {
    if ($Text -notmatch $Pattern) { throw "$Name was not found." }
}

$resolver = [regex]::Match(
    $homeText,
    '(?s)private static AutoMaskProcessingMode ResolveSavedAutoProcessingMode\b.*?(?=\s+private void ApplySavedAutoSettings\b)')
$qualityResolvers = [regex]::Match(
    $homeText,
    '(?s)private static DownscaleQuality ResolveSavedYoloDownscaleQuality\b.*?(?=\s+private static AutoMaskProcessingMode ResolveSavedAutoProcessingMode\b)')
$load = [regex]::Match(
    $homeText,
    '(?s)private void ApplySavedAutoSettings\(\).*?(?=\s+private void PersistAutoSettings\b)')
$persist = [regex]::Match(
    $homeText,
    '(?s)private void PersistAutoSettings\(\).*?(?=\s+private void RegenerateBlurExamples\b)')
if (-not $resolver.Success -or -not $qualityResolvers.Success -or -not $load.Success -or -not $persist.Success) {
    throw "Auto settings load/save source blocks were not found."
}
if ($resolver.Value -match 'settingsVersion|CurrentAutoSettingsVersion|AutoMaskProcessingMode\.(Legacy|Raw)') {
    throw "Saved processing mode still contains version-based coercion."
}
Assert-SourceMatch "mode enum validation" $resolver.Value 'Enum\.IsDefined\(typeof\(AutoMaskProcessingMode\),\s*value\)'
Assert-SourceMatch "mode load connection" $load.Value 'ResolveSavedAutoProcessingMode\(saved\.ProcessingMode\)'
Assert-SourceMatch "mode persistence" $persist.Value 'ProcessingMode\s*=.*AutoMaskProcessingMode\.Tracked'
Assert-SourceMatch "upgrade persistence" $load.Value 'if\s*\(requiresSettingsUpgrade\)\s*PersistAutoSettings\(\)\s*;'
if ($qualityResolvers.Value -match 'settingsVersion|requiresQualityMigration|BalancedDownscaleQualitySettingsVersion') {
    throw "Saved downscale quality still contains version-based coercion."
}
Assert-SourceMatch "quality enum validation" $qualityResolvers.Value 'Enum\.IsDefined\(typeof\(DownscaleQuality\),\s*savedValue\)'
Assert-SourceMatch "quality load connection" $load.Value 'ResolveSavedDownscaleQuality\(saved\.DownscaleQuality\)'
Assert-SourceMatch "global quality persistence" $persist.Value 'DownscaleQuality\s*=.*DownscaleQuality\.BalancedBilinear'
Assert-SourceMatch "YOLO5 quality persistence" $persist.Value 'Yolo5DownscaleQuality\s*=\s*\(int\)yolo5Profile\.DownscaleQuality'
Assert-SourceMatch "YOLOv8 quality persistence" $persist.Value 'YoloV8DownscaleQuality\s*=\s*\(int\)yoloV8Profile\.DownscaleQuality'
foreach ($property in $properties) {
    Assert-SourceMatch "$property load" $load.Value ("\b{0}\s*=\s*saved\.{0}\s*;" -f $property)
    Assert-SourceMatch "$property save" $persist.Value ("\b{0}\s*=\s*{0}\s*[,}}]" -f $property)
    if ($load.Value -match ("\b{0}\s*=\s*requiresSettingsUpgrade\s*\?" -f $property)) {
        throw "$property is still reset during upgrade."
    }
}

$work = Join-Path $repo ".tmp\auto-processing-mode-migration"
$project = Join-Path $work "AutoProcessingModeMigrationHarness.csproj"
$program = Join-Path $work "Program.cs"
if (Test-Path $work) { Remove-Item -Recurse -Force $work }
New-Item -ItemType Directory -Force $work | Out-Null

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

    $programText = @'
using FaceShield.Services.Analysis;
using FaceShield.Services.Workspace;
using FaceShield.ViewModels.Pages;
using System;
using System.Reflection;
using System.Text.Json;

var resolver = typeof(HomePageViewModel).GetMethod(
    "ResolveSavedAutoProcessingMode",
    BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("Resolver was not found.");
if (resolver.GetParameters().Length != 1)
    throw new InvalidOperationException("Resolver must depend only on the saved value.");
var qualityResolver = typeof(HomePageViewModel).GetMethod(
    "ResolveSavedDownscaleQuality",
    BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("Downscale quality resolver was not found.");
var yoloQualityResolver = typeof(HomePageViewModel).GetMethod(
    "ResolveSavedYoloDownscaleQuality",
    BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("YOLO downscale quality resolver was not found.");
if (qualityResolver.GetParameters().Length != 1 || yoloQualityResolver.GetParameters().Length != 3)
    throw new InvalidOperationException("Quality resolvers must not depend on a settings version.");

int validModesPreserved = 0;
foreach (int version in new[] { 8, 9 })
{
    foreach (AutoMaskProcessingMode mode in Enum.GetValues<AutoMaskProcessingMode>())
    {
        AssertResolved($"v{version}-{mode}", (int)mode, mode);
        validModesPreserved++;
    }
}
AssertResolved("missing", null, AutoMaskProcessingMode.Tracked);
AssertResolved("invalid", 999, AutoMaskProcessingMode.Tracked);

int validQualitiesPreserved = 0;
foreach (int version in new[] { 8, 9 })
{
    foreach (DownscaleQuality quality in Enum.GetValues<DownscaleQuality>())
    {
        AssertQuality($"v{version}-{quality}", (int)quality, quality);
        validQualitiesPreserved++;
    }
}
AssertQuality("invalid-quality", 999, DownscaleQuality.BalancedBilinear);
AssertYoloQuality(
    "saved-fast",
    (int)DownscaleQuality.FastNearest,
    (int)DownscaleQuality.BalancedBilinear,
    DownscaleQuality.BalancedBilinear,
    DownscaleQuality.FastNearest);
AssertYoloQuality(
    "legacy-fast",
    null,
    (int)DownscaleQuality.FastNearest,
    DownscaleQuality.BalancedBilinear,
    DownscaleQuality.FastNearest);
AssertYoloQuality(
    "missing-quality",
    null,
    null,
    DownscaleQuality.BalancedBilinear,
    DownscaleQuality.BalancedBilinear);

var defaultOptions = new AutoMaskOptions();
var resolvedDefault = defaultOptions.ResolveProcessingMode();
if (defaultOptions.ProcessingMode != AutoMaskProcessingMode.Tracked ||
    !resolvedDefault.UseTracking ||
    resolvedDefault.EnablePostProcessing ||
    resolvedDefault.EnableRoiPostProcess ||
    resolvedDefault.EnableYoloWeakIsolatedCleanup ||
    resolvedDefault.EnableYoloGapFill ||
    resolvedDefault.EnableYoloSceneCutCarryCleanup ||
    resolvedDefault.EnableYoloTemporalSmoothing ||
    resolvedDefault.EnableYoloRiskCascade ||
    resolvedDefault.DetectEveryNFrames != 1)
{
    throw new InvalidOperationException("Tracked default invariants were not preserved.");
}

var explicitLegacy = new AutoMaskOptions
{
    ProcessingMode = AutoMaskProcessingMode.Legacy,
    UseTracking = true,
    DetectEveryNFrames = 7,
    EnablePostProcessing = true
};
if (!ReferenceEquals(explicitLegacy, explicitLegacy.ResolveProcessingMode()))
    throw new InvalidOperationException("Legacy option values were not preserved.");
var explicitRaw = new AutoMaskOptions
{
    ProcessingMode = AutoMaskProcessingMode.Raw,
    UseTracking = true,
    DetectEveryNFrames = 7,
    EnablePostProcessing = true
}.ResolveProcessingMode();
if (explicitRaw.UseTracking || explicitRaw.DetectEveryNFrames != 1 || explicitRaw.EnablePostProcessing)
    throw new InvalidOperationException("Raw mode execution semantics were not preserved.");
var explicitTracked = new AutoMaskOptions
{
    ProcessingMode = AutoMaskProcessingMode.Tracked,
    UseTracking = false,
    DetectEveryNFrames = 7,
    EnablePostProcessing = true
}.ResolveProcessingMode();
if (!explicitTracked.UseTracking || explicitTracked.DetectEveryNFrames != 7 || explicitTracked.EnablePostProcessing)
    throw new InvalidOperationException("Tracked mode execution semantics were not preserved.");
var explicitFull = new AutoMaskOptions
{
    ProcessingMode = AutoMaskProcessingMode.Full,
    UseTracking = false,
    DetectEveryNFrames = 7,
    EnableYoloRiskCascade = true
}.ResolveProcessingMode();
if (!explicitFull.UseTracking ||
    explicitFull.DetectEveryNFrames != 7 ||
    !explicitFull.EnablePostProcessing ||
    !explicitFull.EnableRoiPostProcess ||
    !explicitFull.EnableYoloWeakIsolatedCleanup ||
    !explicitFull.EnableYoloGapFill ||
    !explicitFull.EnableYoloSceneCutCarryCleanup ||
    !explicitFull.EnableYoloTemporalSmoothing ||
    !explicitFull.EnableYoloRiskCascade)
{
    throw new InvalidOperationException("Full mode execution semantics were not preserved.");
}

var missing = JsonSerializer.Deserialize<AutoSettingsState>("{}")!;
AssertState("missing-json", missing, 0, AutoMaskProcessingMode.Tracked, 0);

var fixtures = new[]
{
    ("v6-missing", """{"SettingsVersion":6}""", AutoMaskProcessingMode.Tracked, 0, DownscaleQuality.BalancedBilinear, (int?)null, (int?)null),
    ("v8-legacy", """{"SettingsVersion":8,"ProcessingMode":0,"DownscaleQuality":0,"Yolo5DownscaleQuality":0,"YoloV8DownscaleQuality":1,"EnablePostProcessing":true,"EnableYoloWeakIsolatedCleanup":true,"EnableYoloSceneCutCarryCleanup":true,"EnableYoloRiskCascade":true}""", AutoMaskProcessingMode.Legacy, 85, DownscaleQuality.FastNearest, (int?)0, (int?)1),
    ("v8-raw", """{"SettingsVersion":8,"ProcessingMode":1,"DownscaleQuality":1,"Yolo5DownscaleQuality":1,"YoloV8DownscaleQuality":0,"EnableRoiPostProcess":true,"EnableYoloGapFill":true,"EnableYoloTemporalSmoothing":true}""", AutoMaskProcessingMode.Raw, 42, DownscaleQuality.BalancedBilinear, (int?)1, (int?)0),
    ("v9-full", """{"SettingsVersion":9,"ProcessingMode":3,"DownscaleQuality":0,"Yolo5DownscaleQuality":0,"YoloV8DownscaleQuality":0,"EnablePostProcessing":true,"EnableRoiPostProcess":true,"EnableYoloWeakIsolatedCleanup":true,"EnableYoloGapFill":true,"EnableYoloSceneCutCarryCleanup":true,"EnableYoloTemporalSmoothing":true,"EnableYoloRiskCascade":true}""", AutoMaskProcessingMode.Full, 127, DownscaleQuality.FastNearest, (int?)0, (int?)0),
    ("v9-invalid", """{"SettingsVersion":9,"ProcessingMode":999,"DownscaleQuality":1,"EnablePostProcessing":true,"EnableYoloGapFill":true,"EnableYoloSceneCutCarryCleanup":true,"EnableYoloRiskCascade":true}""", AutoMaskProcessingMode.Tracked, 89, DownscaleQuality.BalancedBilinear, (int?)null, (int?)null)
};

foreach (var fixture in fixtures)
{
    var loaded = JsonSerializer.Deserialize<AutoSettingsState>(fixture.Item2)!;
    var mode = Resolve(loaded.ProcessingMode);
    if (mode != fixture.Item3 ||
        FlagBits(loaded) != fixture.Item4 ||
        loaded.DownscaleQuality != (int)fixture.Item5 ||
        loaded.Yolo5DownscaleQuality != fixture.Item6 ||
        loaded.YoloV8DownscaleQuality != fixture.Item7)
        throw new InvalidOperationException($"{fixture.Item1}: fixture values were not preserved.");

    var upgraded = new AutoSettingsState
    {
        SettingsVersion = 10,
        ProcessingMode = (int)mode,
        DownscaleQuality = loaded.DownscaleQuality,
        Yolo5DownscaleQuality = loaded.Yolo5DownscaleQuality,
        YoloV8DownscaleQuality = loaded.YoloV8DownscaleQuality,
        EnablePostProcessing = loaded.EnablePostProcessing,
        EnableRoiPostProcess = loaded.EnableRoiPostProcess,
        EnableYoloWeakIsolatedCleanup = loaded.EnableYoloWeakIsolatedCleanup,
        EnableYoloGapFill = loaded.EnableYoloGapFill,
        EnableYoloSceneCutCarryCleanup = loaded.EnableYoloSceneCutCarryCleanup,
        EnableYoloTemporalSmoothing = loaded.EnableYoloTemporalSmoothing,
        EnableYoloRiskCascade = loaded.EnableYoloRiskCascade
    };
    var reloaded = JsonSerializer.Deserialize<AutoSettingsState>(JsonSerializer.Serialize(upgraded))!;
    AssertState(
        fixture.Item1 + "-v10",
        reloaded,
        10,
        fixture.Item3,
        fixture.Item4,
        fixture.Item5,
        fixture.Item6,
        fixture.Item7);
}

if (validModesPreserved != 8)
    throw new InvalidOperationException($"Expected 8 valid mode cases, actual={validModesPreserved}.");
if (validQualitiesPreserved != 4)
    throw new InvalidOperationException($"Expected 4 valid quality cases, actual={validQualitiesPreserved}.");
Console.WriteLine(
    "[AutoProcessingModeMigrationVerify] PASS " +
    "validModesPreserved=8 validQualitiesPreserved=4 postprocessPreserved=7 " +
    "upgradeRoundTrip=true modeSemantics=4 isolatedNoUserStateAccess=true " +
    "missing=Tracked invalid=Tracked defaultInvariants=Tracked");

AutoMaskProcessingMode Resolve(int? value)
{
    object? result = resolver.Invoke(null, new object?[] { value });
    return result is AutoMaskProcessingMode mode
        ? mode
        : throw new InvalidOperationException($"Resolver returned {result ?? "null"}.");
}

void AssertResolved(string name, int? value, AutoMaskProcessingMode expected)
{
    var actual = Resolve(value);
    if (actual != expected)
        throw new InvalidOperationException($"{name}: expected={expected}, actual={actual}.");
}

void AssertQuality(string name, int value, DownscaleQuality expected)
{
    object? result = qualityResolver.Invoke(null, new object?[] { value });
    if (result is not DownscaleQuality actual || actual != expected)
        throw new InvalidOperationException($"{name}: expected={expected}, actual={result ?? "null"}.");
}

void AssertYoloQuality(
    string name,
    int? savedValue,
    int? legacyValue,
    DownscaleQuality defaultValue,
    DownscaleQuality expected)
{
    object? result = yoloQualityResolver.Invoke(
        null,
        new object?[] { savedValue, legacyValue, defaultValue });
    if (result is not DownscaleQuality actual || actual != expected)
        throw new InvalidOperationException($"{name}: expected={expected}, actual={result ?? "null"}.");
}

void AssertState(
    string name,
    AutoSettingsState state,
    int version,
    AutoMaskProcessingMode mode,
    int flags,
    DownscaleQuality quality = DownscaleQuality.BalancedBilinear,
    int? yolo5Quality = null,
    int? yoloV8Quality = null)
{
    if (state.SettingsVersion != version ||
        state.ProcessingMode != (int)mode ||
        FlagBits(state) != flags ||
        state.DownscaleQuality != (int)quality ||
        state.Yolo5DownscaleQuality != yolo5Quality ||
        state.YoloV8DownscaleQuality != yoloV8Quality)
        throw new InvalidOperationException($"{name}: state round-trip mismatch.");
}

int FlagBits(AutoSettingsState state) =>
    (state.EnablePostProcessing ? 1 : 0) |
    (state.EnableRoiPostProcess ? 2 : 0) |
    (state.EnableYoloWeakIsolatedCleanup ? 4 : 0) |
    (state.EnableYoloGapFill ? 8 : 0) |
    (state.EnableYoloSceneCutCarryCleanup ? 16 : 0) |
    (state.EnableYoloTemporalSmoothing ? 32 : 0) |
    (state.EnableYoloRiskCascade ? 64 : 0);
'@

    if ($programText -match 'AppBuilder|WorkspaceStateStore|RuntimeHelpers|Thread|LocalApplicationData|state\.json|File\.') {
        throw "Migration harness contains a user-state access path."
    }
    $programText | Set-Content -Encoding UTF8 $program
    dotnet run --project $project --configuration Debug
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    if (-not $KeepHarness.IsPresent -and (Test-Path $work)) {
        Remove-Item -Recurse -Force $work
    }
}
