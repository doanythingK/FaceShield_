param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$work = Join-Path $repo ".tmp\auto-resume-mask-reset"
$project = Join-Path $work "AutoResumeMaskResetHarness.csproj"
$program = Join-Path $work "Program.cs"
$providerPath = Join-Path $repo "Services\Video\FrameMaskProvider.cs"
$workspacePath = Join-Path $repo "ViewModels\Pages\WorkspaceViewModel.cs"
$stateStorePath = Join-Path $repo "Services\Workspace\WorkspaceStateStore.cs"
$yoloDetectorPath = Join-Path $repo "Services\FaceDetection\YoloFaceOnnxDetector.cs"
$generatorPath = Join-Path $repo "Services\Analysis\AutoMaskGenerator.cs"

function Assert-Contains {
    param(
        [string]$Name,
        [string]$Text,
        [string]$Expected
    )

    if (-not $Text.Contains($Expected)) {
        throw "$Name missing text: $Expected"
    }

    Write-Host "[AutoResumeMaskResetVerify] pass $Name"
}

foreach ($requiredPath in @($providerPath, $workspacePath, $stateStorePath, $yoloDetectorPath, $generatorPath)) {
    if (-not (Test-Path $requiredPath)) {
        throw "Required source not found: $requiredPath"
    }
}

New-Item -ItemType Directory -Force -Path $work | Out-Null

try {
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$repo\FaceShield.csproj" />
  </ItemGroup>
</Project>
"@ | Set-Content -Encoding UTF8 -Path $project

    @'
using FaceShield.Enums.Workspace;
using FaceShield.Services.Analysis;
using FaceShield.Services.FaceDetection;
using FaceShield.Services.Workspace;
using FaceShield.ViewModels.Pages;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

var flags = BindingFlags.NonPublic | BindingFlags.Static;
var runSignatureMethod = typeof(WorkspaceViewModel).GetMethod("BuildAutoRunSignature", flags)
    ?? throw new InvalidOperationException("BuildAutoRunSignature was not found.");
var executionSignatureMethod = typeof(WorkspaceViewModel).GetMethod("BuildAutoExecutionSignature", flags)
    ?? throw new InvalidOperationException("BuildAutoExecutionSignature was not found.");
var resetReasonMethod = typeof(WorkspaceViewModel).GetMethod("GetAutoResumeResetReason", flags)
    ?? throw new InvalidOperationException("GetAutoResumeResetReason was not found.");
var intentSignatureMethod = typeof(WorkspaceViewModel).GetMethod("BuildAutoRunIntentSignature", flags)
    ?? throw new InvalidOperationException("BuildAutoRunIntentSignature was not found.");
var requiresTimelineMethod = typeof(WorkspaceViewModel).GetMethod("RequiresCompleteAutoTimeline", flags)
    ?? throw new InvalidOperationException("RequiresCompleteAutoTimeline was not found.");
var providerIdentityType = typeof(FaceOnnxDetector).Assembly.GetType(
    "FaceShield.Services.FaceDetection.DetectorExecutionProviderIdentity")
    ?? throw new InvalidOperationException("DetectorExecutionProviderIdentity was not found.");
var providerCompatibilityMethod = providerIdentityType.GetMethod(
    "AreLabelsCompatible",
    BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("AreLabelsCompatible was not found.");

string RunSignature(AutoMaskOptions options, FaceDetectorFactoryOptions factory)
    => (string)(runSignatureMethod.Invoke(null, [options, factory])
        ?? throw new InvalidOperationException("Run signature was not returned."));

string ExecutionSignature(
    AutoMaskOptions options,
    FaceDetectorFactoryOptions factory,
    string provider,
    string source)
    => (string)(executionSignatureMethod.Invoke(null, [options, factory, provider, source])
        ?? throw new InvalidOperationException("Execution signature was not returned."));

string? ResetReason(
    int resumeIndex,
    string? storedRun,
    string currentRun,
    string? storedExecution,
    string currentExecution)
    => resetReasonMethod.Invoke(
        null,
        [resumeIndex, storedRun, currentRun, storedExecution, currentExecution]) as string;

string IntentSignature(
    AutoMaskOptions options,
    FaceOnnxDetectorOptions detector,
    FaceDetectorFactoryOptions factory)
    => (string)(intentSignatureMethod.Invoke(null, [options, detector, factory])
        ?? throw new InvalidOperationException("Intent signature was not returned."));

bool ProvidersCompatible(string primary, string candidate)
    => (bool)(providerCompatibilityMethod.Invoke(null, [primary, candidate])
        ?? throw new InvalidOperationException("Provider compatibility was not returned."));

bool RequiresCompleteTimeline(AutoMaskOptions options, FaceDetectorFactoryOptions factory)
    => (bool)(requiresTimelineMethod.Invoke(null, [options, factory])
        ?? throw new InvalidOperationException("Complete-timeline policy was not returned."));

var autoOptions = new AutoMaskOptions
{
    ProcessingMode = AutoMaskProcessingMode.Tracked,
    ParallelDetectorCount = 2
}.ResolveProcessingMode();
var preciseA = FaceDetectorFactoryOptions.ForOnnx(new FaceOnnxDetectorOptions
{
    DetectionThreshold = 0.1234f,
    IntraOpNumThreads = 4,
    AllowAutoTune = true,
    AllowAutoGpu = true
});
var preciseB = FaceDetectorFactoryOptions.ForOnnx(new FaceOnnxDetectorOptions
{
    DetectionThreshold = 0.12345f,
    IntraOpNumThreads = 4,
    AllowAutoTune = true,
    AllowAutoGpu = true
});

string runA = RunSignature(autoOptions, preciseA);
string runB = RunSignature(autoOptions, preciseB);
if (runA == runB)
    throw new InvalidOperationException("Distinct detector thresholds collided in the run signature.");

string downscaleA = RunSignature(
    new AutoMaskOptions { DownscaleRatio = 0.5001 }.ResolveProcessingMode(),
    preciseA);
string downscaleB = RunSignature(
    new AutoMaskOptions { DownscaleRatio = 0.5004 }.ResolveProcessingMode(),
    preciseA);
if (downscaleA == downscaleB)
    throw new InvalidOperationException("Distinct downscale dimensions collided in the run signature.");

string scrfdA = RunSignature(
    autoOptions,
    FaceDetectorFactoryOptions.ForScrfdOnnx(new ScrfdOnnxDetectorOptions
    {
        ModelPath = "scrfd.onnx",
        InputMean = 127.5f
    }));
string scrfdB = RunSignature(
    autoOptions,
    FaceDetectorFactoryOptions.ForScrfdOnnx(new ScrfdOnnxDetectorOptions
    {
        ModelPath = "scrfd.onnx",
        InputMean = 127.6f
    }));
if (scrfdA == scrfdB)
    throw new InvalidOperationException("SCRFD preprocessing options were not signed.");

string yuNetA = RunSignature(
    autoOptions,
    FaceDetectorFactoryOptions.ForYuNetOnnx(new YuNetOnnxDetectorOptions
    {
        ModelPath = "yunet.onnx",
        TopK = 1000
    }));
string yuNetB = RunSignature(
    autoOptions,
    FaceDetectorFactoryOptions.ForYuNetOnnx(new YuNetOnnxDetectorOptions
    {
        ModelPath = "yunet.onnx",
        TopK = 1001
    }));
if (yuNetA == yuNetB)
    throw new InvalidOperationException("YuNet detection options were not signed.");

var riskOptions = new AutoMaskOptions
{
    ProcessingMode = AutoMaskProcessingMode.Full,
    EnableYoloRiskCascade = true
}.ResolveProcessingMode();
var configuredSecondary = new FaceOnnxDetectorOptions
{
    UseGpu = true,
    AllowAutoTune = true,
    AllowAutoGpu = true
};
var riskFactory = FaceDetectorFactoryOptions.ForYoloFaceOnnx(
    new YoloFaceOnnxDetectorOptions { ModelPath = "yolo.onnx" },
    configuredSecondary);
string riskIntent = IntentSignature(riskOptions, configuredSecondary, riskFactory);
if (!riskIntent.Contains("secondaryGpu=False", StringComparison.Ordinal) ||
    !riskIntent.Contains("secondaryAutoTune=False", StringComparison.Ordinal) ||
    !riskIntent.Contains("secondaryAutoGpu=False", StringComparison.Ordinal))
{
    throw new InvalidOperationException("YOLO risk-cascade intent did not use the effective secondary detector options.");
}
if (!RequiresCompleteTimeline(riskOptions, riskFactory) ||
    RequiresCompleteTimeline(autoOptions, preciseA) ||
    RequiresCompleteTimeline(autoOptions, riskFactory))
{
    throw new InvalidOperationException("Complete-timeline policy did not isolate YOLO risk-cascade runs.");
}

CultureInfo originalCulture = CultureInfo.CurrentCulture;
try
{
    foreach (string cultureName in new[] { "en-US", "de-DE" })
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
        if (RunSignature(autoOptions, preciseA) != runA)
            throw new InvalidOperationException($"Run signature changed under {cultureName}.");
    }
}
finally
{
    CultureInfo.CurrentCulture = originalCulture;
}

string executionA = ExecutionSignature(autoOptions, preciseA, "GPU:DirectML", "source-a");
if (ExecutionSignature(autoOptions, preciseA, "CPU", "source-a") == executionA)
    throw new InvalidOperationException("Execution provider was not signed.");
if (ExecutionSignature(autoOptions, preciseA, "GPU:DirectML", "source-b") == executionA)
    throw new InvalidOperationException("Source identity was not signed.");
if (ExecutionSignature(
        new AutoMaskOptions
        {
            ProcessingMode = AutoMaskProcessingMode.Tracked,
            ParallelDetectorCount = 3
        }.ResolveProcessingMode(),
        preciseA,
        "GPU:DirectML",
        "source-a") == executionA)
{
    throw new InvalidOperationException("Tuned detector session count was not signed.");
}
if (ExecutionSignature(autoOptions, preciseA, "CoreML(default)", "source-a") !=
    ExecutionSignature(autoOptions, preciseA, "GPU:CoreML", "source-a"))
{
    throw new InvalidOperationException("Equivalent CoreML provider labels were not canonicalized.");
}
if (!ProvidersCompatible("GPU:DirectML", "gpu:directml") ||
    !ProvidersCompatible("CPU(가속 실패)", "CPU") ||
    ProvidersCompatible("GPU:DirectML", "CPU"))
{
    throw new InvalidOperationException("Parallel detector provider compatibility policy was not fail-closed.");
}

string modelPath = Path.Combine(Path.GetTempPath(), $"faceshield-model-{Guid.NewGuid():N}.onnx");
try
{
    File.WriteAllBytes(modelPath, [1]);
    var modelFactory = FaceDetectorFactoryOptions.ForYoloFaceOnnx(new YoloFaceOnnxDetectorOptions
    {
        ModelPath = modelPath
    });
    string modelA = ExecutionSignature(autoOptions, modelFactory, "CPU", "source-a");
    File.WriteAllBytes(modelPath, [1, 2]);
    string modelB = ExecutionSignature(autoOptions, modelFactory, "CPU", "source-a");
    if (modelA == modelB)
        throw new InvalidOperationException("Changed detector model identity was not signed.");
}
finally
{
    File.Delete(modelPath);
}

var resetCases = new (string Name, int Resume, string? StoredRun, string CurrentRun, string? StoredExecution, string CurrentExecution, string? Expected)[]
{
    ("fresh run", 0, null, runA, null, executionA, null),
    ("changed settings", 25, runB, runA, executionA, executionA, "settings-changed"),
    ("legacy missing execution", 25, runA, runA, null, executionA, "execution-signature-missing"),
    ("changed execution", 25, runA, runA, executionA + "-old", executionA, "execution-changed"),
    ("matching execution", 25, runA, runA, executionA, executionA, null)
};
foreach (var test in resetCases)
{
    string? actual = ResetReason(
        test.Resume,
        test.StoredRun,
        test.CurrentRun,
        test.StoredExecution,
        test.CurrentExecution);
    if (actual != test.Expected)
        throw new InvalidOperationException($"{test.Name}: expected={test.Expected ?? "null"}, actual={actual ?? "null"}.");
}

var snapshot = new WorkspaceSnapshot(
    "video.mp4",
    WorkspaceMode.Auto,
    0,
    0,
    10,
    DateTimeOffset.UtcNow,
    25,
    false,
    runA,
    false,
    false,
    null,
    false,
    false,
    null,
    executionA);
if (snapshot.AutoExecutionSignature != executionA)
    throw new InvalidOperationException("Workspace snapshot did not retain the execution signature.");

Type persistedStateType = typeof(WorkspaceStateStore).GetNestedType(
    "WorkspaceState",
    BindingFlags.NonPublic)
    ?? throw new InvalidOperationException("Persisted workspace state type was not found.");
PropertyInfo persistedExecutionProperty = persistedStateType.GetProperty("AutoExecutionSignature")
    ?? throw new InvalidOperationException("Persisted execution signature property was not found.");
object persistedState = Activator.CreateInstance(persistedStateType, nonPublic: true)
    ?? throw new InvalidOperationException("Persisted workspace state could not be created.");
persistedExecutionProperty.SetValue(persistedState, executionA);
string stateJson = JsonSerializer.Serialize(persistedState, persistedStateType);
object restoredState = JsonSerializer.Deserialize(stateJson, persistedStateType)
    ?? throw new InvalidOperationException("Persisted workspace state did not deserialize.");
if (!Equals(persistedExecutionProperty.GetValue(restoredState), executionA))
    throw new InvalidOperationException("Execution signature did not survive the state JSON round trip.");
object legacyState = JsonSerializer.Deserialize("{}", persistedStateType)
    ?? throw new InvalidOperationException("Legacy workspace state did not deserialize.");
if (persistedExecutionProperty.GetValue(legacyState) != null)
    throw new InvalidOperationException("Legacy workspace state did not default the execution signature to null.");

Console.WriteLine(
    "[AutoResumeMaskResetVerify] PASS " +
    $"resetCases={resetCases.Length} precision=True culture=True provider=True source=True sessions=True " +
    "models=True backends=True riskIntent=True timelinePrompt=True providerPool=True snapshot=True json=True");
'@ | Set-Content -Encoding UTF8 -Path $program

    dotnet run --project $project --configuration Debug
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    $provider = Get-Content -Raw -Path $providerPath
    $workspace = Get-Content -Raw -Path $workspacePath
    $stateStore = Get-Content -Raw -Path $stateStorePath
    $yoloDetector = Get-Content -Raw -Path $yoloDetectorPath
    $generator = Get-Content -Raw -Path $generatorPath

    Assert-Contains "provider exposes ranged face-mask cleanup" $provider "public int RemoveFaceMasksFrom(int startFrameIndex)"
    Assert-Contains "provider preserves earlier face masks" $provider "if (frameIndex < startFrameIndex)"
    Assert-Contains "provider removes matching future face masks" $provider "_faceMasks.TryRemove(frameIndex, out _)"
    Assert-Contains "workspace resets masks before generator run" $workspace "ResetAutoFaceMasksForRun(lastProcessed);"
    Assert-Contains "fresh run clears all face masks" $workspace "_maskProvider.ClearFaceMasks();"
    Assert-Contains "resume run clears stale future face masks" $workspace "_maskProvider.RemoveFaceMasksFrom(startFrameIndex)"
    Assert-Contains "workspace stores run intent signature" $workspace "_autoRunSignature"
    Assert-Contains "workspace stores execution signature" $workspace "_autoExecutionSignature"
    Assert-Contains "workspace gates prompt by effective intent signature" $workspace "IsAutoResumeSignatureCurrent(BuildAutoRunIntentSignature"
    Assert-Contains "workspace hides resume prompt for full-timeline runs" $workspace "!RequiresCompleteAutoTimeline(_autoOptions, _detectorFactoryOptions)"
    Assert-Contains "workspace checks run and execution before resume" $workspace "ResetStaleAutoResumeIfRunChanged(runSignature, executionSignature);"
    Assert-Contains "workspace signs actual provider" $workspace "GetDetectorExecutionProviderLabel(detector)"
    Assert-Contains "workspace signs scrfd options" $workspace "AppendScrfdSignature"
    Assert-Contains "workspace signs yunet options" $workspace "AppendYuNetSignature"
    Assert-Contains "workspace logs missing execution signature" $workspace 'return "execution-signature-missing";'
    Assert-Contains "workspace logs changed execution" $workspace 'return "execution-changed";'
    Assert-Contains "workspace uses invariant round-trip numbers" $workspace 'ToString("R", CultureInfo.InvariantCulture)'
    Assert-Contains "state store saves execution signature" $stateStore "AutoExecutionSignature = snapshot.AutoExecutionSignature"
    Assert-Contains "state store loads execution signature" $stateStore "state.AutoExecutionSignature"
    Assert-Contains "yolo detector exposes its instance provider" $yoloDetector "internal string ExecutionProviderLabel { get; }"
    Assert-Contains "parallel detector rejects mixed providers" $generator "DetectorExecutionProviderIdentity.AreCompatible(primary, candidate)"

    $providerPoolChecks = [regex]::Matches(
        $generator,
        "TryAddCompatibleParallelDetector\(detectors, (?:bgraDetector|sparseBgraDetector), created\)").Count
    if ($providerPoolChecks -ne 2) {
        throw "Expected provider compatibility checks in both parallel detector paths, found $providerPoolChecks."
    }

    $executionCheck = $workspace.IndexOf("ResetStaleAutoResumeIfRunChanged(runSignature, executionSignature);", [StringComparison]::Ordinal)
    $maskReset = $workspace.IndexOf("ResetAutoFaceMasksForRun(lastProcessed);", [StringComparison]::Ordinal)
    if ($executionCheck -lt 0 -or $maskReset -lt 0 -or $executionCheck -ge $maskReset) {
        throw "Execution compatibility must be checked before existing masks are retained."
    }

    Write-Host "[AutoResumeMaskResetVerify] all requested checks passed"
}
finally {
    if (Test-Path $work) {
        Remove-Item -Recurse -Force -Path $work
    }
}
