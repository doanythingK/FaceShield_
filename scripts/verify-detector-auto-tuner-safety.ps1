param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$work = Join-Path $repo ".tmp\detector-auto-tuner-safety"
$project = Join-Path $work "DetectorAutoTunerSafetyHarness.csproj"
$program = Join-Path $work "Program.cs"

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
using Avalonia;
using FaceShield.Services.Analysis;
using FaceShield.Services.FaceDetection;
using System.Reflection;

var flags = BindingFlags.NonPublic | BindingFlags.Static;
var qualityMethod = typeof(DetectorAutoTuner).GetMethod("IsQualityCompatible", flags)
    ?? throw new InvalidOperationException("IsQualityCompatible was not found.");
var providerMethod = typeof(DetectorAutoTuner).GetMethod("IsActiveGpuProvider", flags)
    ?? throw new InvalidOperationException("IsActiveGpuProvider was not found.");
var sourceIdentityMethod = typeof(DetectorAutoTuner).GetMethod("BuildCacheSourceIdentity", flags)
    ?? throw new InvalidOperationException("BuildCacheSourceIdentity was not found.");

bool Quality(IReadOnlyList<FaceDetectionResult>? reference, IReadOnlyList<FaceDetectionResult>? candidate)
    => (bool)(qualityMethod.Invoke(null, [reference, candidate])
        ?? throw new InvalidOperationException("Quality result was not returned."));

var face = new FaceDetectionResult { Bounds = new Rect(20, 20, 80, 80), Confidence = 0.9f };
var matchingFace = new FaceDetectionResult { Bounds = new Rect(22, 22, 80, 80), Confidence = 0.9f };
var distantFace = new FaceDetectionResult { Bounds = new Rect(200, 120, 80, 80), Confidence = 0.9f };
var qualityCases = new (string Name, IReadOnlyList<FaceDetectionResult>? Reference, IReadOnlyList<FaceDetectionResult>? Candidate, bool Expected)[]
{
    ("null reference", null, Array.Empty<FaceDetectionResult>(), false),
    ("no positive reference", Array.Empty<FaceDetectionResult>(), Array.Empty<FaceDetectionResult>(), false),
    ("matching positive", new[] { face }, new[] { matchingFace }, true),
    ("count mismatch", new[] { face }, Array.Empty<FaceDetectionResult>(), false),
    ("low overlap", new[] { face }, new[] { distantFace }, false)
};
foreach (var test in qualityCases)
{
    bool actual = Quality(test.Reference, test.Candidate);
    if (actual != test.Expected)
        throw new InvalidOperationException($"{test.Name}: expected={test.Expected}, actual={actual}.");
}

bool Provider(string? label)
    => (bool)(providerMethod.Invoke(null, [label])
        ?? throw new InvalidOperationException("Provider result was not returned."));

var providerCases = new (string? Label, bool Expected)[]
{
    ("GPU:DirectML", true),
    ("gpu:CoreML", true),
    ("CPU(가속 실패)", false),
    (null, false)
};
foreach (var test in providerCases)
{
    bool actual = Provider(test.Label);
    if (actual != test.Expected)
        throw new InvalidOperationException($"provider={test.Label ?? "null"}: expected={test.Expected}, actual={actual}.");
}

string Identity(string path, bool requireSource)
    => (string)(sourceIdentityMethod.Invoke(null, [path, requireSource])
        ?? throw new InvalidOperationException("Source identity was not returned."));

string sourceA = Path.Combine(Path.GetTempPath(), $"faceshield-tune-{Guid.NewGuid():N}.mp4");
string sourceB = Path.Combine(Path.GetTempPath(), $"faceshield-tune-{Guid.NewGuid():N}.mp4");
try
{
    File.WriteAllBytes(sourceA, [1, 2, 3]);
    File.WriteAllBytes(sourceB, [1, 2, 3]);
    string sharedA = Identity(sourceA, requireSource: false);
    string sharedB = Identity(sourceB, requireSource: false);
    if (sharedA != sharedB)
        throw new InvalidOperationException("CPU-only performance cache was split by source.");

    string gpuA1 = Identity(sourceA, requireSource: true);
    string gpuA2 = Identity(sourceA, requireSource: true);
    string gpuB = Identity(sourceB, requireSource: true);
    if (gpuA1 != gpuA2 || gpuA1 == gpuB)
        throw new InvalidOperationException("GPU quality cache source identity was not stable and source-specific.");

    using (var stream = new FileStream(sourceA, FileMode.Append, FileAccess.Write, FileShare.None))
        stream.WriteByte(4);
    string gpuAChanged = Identity(sourceA, requireSource: true);
    if (gpuA1 == gpuAChanged)
        throw new InvalidOperationException("GPU quality cache identity ignored a changed source file.");
}
finally
{
    File.Delete(sourceA);
    File.Delete(sourceB);
}

var baseOptions = new FaceOnnxDetectorOptions();
string missingVideo = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.mp4");
bool tuned = DetectorAutoTuner.TryTune(
    missingVideo,
    1.0,
    DownscaleQuality.BalancedBilinear,
    baseOptions,
    2,
    false,
    CancellationToken.None,
    out var fallbackOptions,
    out int fallbackSessions,
    out string? fallbackLabel);
if (tuned || !ReferenceEquals(fallbackOptions, baseOptions) || fallbackSessions != 2 || fallbackLabel != null)
    throw new InvalidOperationException("Auto-tune failure did not preserve configured fallback values.");

using var canceled = new CancellationTokenSource();
canceled.Cancel();
bool cancellationThrown = false;
try
{
    DetectorAutoTuner.TryTune(
        missingVideo,
        1.0,
        DownscaleQuality.BalancedBilinear,
        baseOptions,
        2,
        false,
        canceled.Token,
        out _,
        out _,
        out _);
}
catch (OperationCanceledException)
{
    cancellationThrown = true;
}
if (!cancellationThrown)
    throw new InvalidOperationException("Caller cancellation was swallowed by auto-tune fallback.");

Console.WriteLine(
    "[DetectorAutoTunerSafetyVerify] PASS " +
    $"qualityCases={qualityCases.Length} providerCases={providerCases.Length} " +
    "sourceIdentity=True fallback=True cancellation=True");
'@ | Set-Content -Encoding UTF8 -Path $program

    dotnet run --project $project --configuration Debug
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    $source = Get-Content -Raw -Path (Join-Path $repo "Services\FaceDetection\DetectorAutoTuner.cs")
    if ($source -notmatch 'catch\s*\(OperationCanceledException\)\s*when\s*\(cancellationToken\.IsCancellationRequested\)[\s\S]*throw;[\s\S]*catch\s*\(Exception\s+ex\)[\s\S]*benchmark failed') {
        throw "Candidate benchmark failures are not isolated while cancellation remains observable."
    }
    if ($source -notmatch 'candidate\.Options,[\s\S]*out\s+string\?\s+gpuProviderLabel[\s\S]*IsActiveGpuProvider\(gpuProviderLabel\)') {
        throw "GPU quality probes are not gated by their own execution provider."
    }
    if ($source -notmatch 'options\.UseGpu\s*&&\s*!detector\.UsesGpuExecutionProvider') {
        throw "GPU throughput sessions are not gated by their own execution provider."
    }

    $workspace = Get-Content -Raw -Path (Join-Path $repo "ViewModels\Pages\WorkspaceViewModel.cs")
    foreach ($token in @(
        "preprocess=",
        "autoTune=",
        "autoGpu=",
        "secondaryPreprocess=",
        "secondaryAutoTune=",
        "secondaryAutoGpu="
    )) {
        if (-not $workspace.Contains($token)) {
            throw "Auto-run signature is missing $token"
        }
    }

    Write-Host "[DetectorAutoTunerSafetyVerify] PASS candidateIsolation=True signatureOptions=True"
}
finally {
    if (Test-Path $work) {
        Remove-Item -Recurse -Force -Path $work
    }
}
