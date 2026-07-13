param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$work = Join-Path $repo ".tmp\detector-auto-tuner-session-range"
$project = Join-Path $work "DetectorAutoTunerSessionRangeHarness.csproj"
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
using FaceShield.Services.Analysis;
using FaceShield.Services.FaceDetection;
using System.Collections;
using System.Reflection;

var buildCandidates = typeof(DetectorAutoTuner).GetMethod(
    "BuildCandidates",
    BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("BuildCandidates was not found.");
var roiPolicy = typeof(AutoMaskGenerator).GetMethod(
    "ShouldUsePrimaryRoiShortcut",
    BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("ShouldUsePrimaryRoiShortcut was not found.");

var baseOptions = new FaceOnnxDetectorOptions
{
    UseOrtOptimization = false,
    UseGpu = false,
    DetectionThreshold = 0.21f,
    ConfidenceThreshold = 0.31f,
    NmsThreshold = 0.71f,
    EnablePreprocessParallelism = true
};

List<Candidate> ReadCandidates(int maxSessions, bool allowGpu)
{
    object value = buildCandidates.Invoke(null, [baseOptions, maxSessions, allowGpu])
        ?? throw new InvalidOperationException("BuildCandidates returned null.");
    var result = new List<Candidate>();
    foreach (object item in (IEnumerable)value)
    {
        Type type = item.GetType();
        var options = (FaceOnnxDetectorOptions)(type.GetField("Item1")?.GetValue(item)
            ?? throw new InvalidOperationException("Candidate options were not found."));
        int sessions = (int)(type.GetField("Item2")?.GetValue(item)
            ?? throw new InvalidOperationException("Candidate session count was not found."));
        string label = (string)(type.GetField("Item3")?.GetValue(item)
            ?? throw new InvalidOperationException("Candidate label was not found."));
        result.Add(new Candidate(options, sessions, label));
    }

    return result;
}

void AssertCandidateInvariants(IReadOnlyList<Candidate> candidates, int maxSessions)
{
    if (candidates.Count == 0)
        throw new InvalidOperationException("No tuning candidates were created.");

    foreach (Candidate candidate in candidates)
    {
        if (candidate.Sessions < 1 || candidate.Sessions > maxSessions)
            throw new InvalidOperationException($"Session count out of range: {candidate.Sessions}.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(
                candidate.Label,
                $"^(CPU|GPU) {candidate.Sessions}[^0-9]"))
            throw new InvalidOperationException($"Label/session mismatch: {candidate.Label}.");
        if (candidate.Options.UseOrtOptimization != baseOptions.UseOrtOptimization ||
            candidate.Options.DetectionThreshold != baseOptions.DetectionThreshold ||
            candidate.Options.ConfidenceThreshold != baseOptions.ConfidenceThreshold ||
            candidate.Options.NmsThreshold != baseOptions.NmsThreshold)
        {
            throw new InvalidOperationException($"Detector policy changed for {candidate.Label}.");
        }
    }
}

static string SessionSet(IEnumerable<Candidate> candidates)
    => string.Join(",", candidates.Select(candidate => candidate.Sessions).Distinct().OrderBy(value => value));

var cpuCandidates = ReadCandidates(4, allowGpu: false);
AssertCandidateInvariants(cpuCandidates, 4);
if (SessionSet(cpuCandidates) != "1,2,3,4")
    throw new InvalidOperationException($"CPU session range mismatch: {SessionSet(cpuCandidates)}.");
if (cpuCandidates.Any(candidate => candidate.Options.UseGpu))
    throw new InvalidOperationException("CPU-only tuning created a GPU candidate.");
for (int sessions = 1; sessions <= 4; sessions++)
{
    if (!cpuCandidates.Any(candidate => candidate.Sessions == sessions && !candidate.Options.UseGpu))
        throw new InvalidOperationException($"CPU candidate missing for {sessions} sessions.");
}

var gpuCandidates = ReadCandidates(4, allowGpu: true);
AssertCandidateInvariants(gpuCandidates, 4);
string gpuSessions = SessionSet(gpuCandidates.Where(candidate => candidate.Options.UseGpu));
if (gpuSessions != "1,2,3,4")
    throw new InvalidOperationException($"GPU session range mismatch: {gpuSessions}.");

var singleCandidates = ReadCandidates(1, allowGpu: false);
AssertCandidateInvariants(singleCandidates, 1);
if (SessionSet(singleCandidates) != "1")
    throw new InvalidOperationException($"Single-session range mismatch: {SessionSet(singleCandidates)}.");

bool UsesRoi(AutoMaskOptions options)
    => (bool)(roiPolicy.Invoke(null, [options])
        ?? throw new InvalidOperationException("ROI policy was not returned."));

var roiCases = new (string Name, AutoMaskOptions Options, bool Expected)[]
{
    ("tracked FaceONNX", new AutoMaskOptions(), false),
    ("full FaceONNX", new AutoMaskOptions { ProcessingMode = AutoMaskProcessingMode.Full }, false),
    ("raw FaceONNX", new AutoMaskOptions { ProcessingMode = AutoMaskProcessingMode.Raw }, false),
    ("legacy FaceONNX", new AutoMaskOptions { ProcessingMode = AutoMaskProcessingMode.Legacy }, true),
    ("legacy YOLO enabled", new AutoMaskOptions
    {
        ProcessingMode = AutoMaskProcessingMode.Legacy,
        FilterProfile = FaceFilterProfile.Yolo,
        EnableYoloPrimaryRoiShortcut = true
    }, true),
    ("legacy YOLO disabled", new AutoMaskOptions
    {
        ProcessingMode = AutoMaskProcessingMode.Legacy,
        FilterProfile = FaceFilterProfile.Yolo,
        EnableYoloPrimaryRoiShortcut = false
    }, false)
};
foreach (var test in roiCases)
{
    bool actual = UsesRoi(test.Options.ResolveProcessingMode());
    if (actual != test.Expected)
        throw new InvalidOperationException($"{test.Name}: expected ROI={test.Expected}, actual={actual}.");
}

Console.WriteLine(
    "[DetectorAutoTunerSessionRangeVerify] PASS " +
    $"cpuSessions={SessionSet(cpuCandidates)} " +
    $"gpuSessions={gpuSessions} singleSessions={SessionSet(singleCandidates)} roiPolicies={roiCases.Length}");

internal sealed record Candidate(FaceOnnxDetectorOptions Options, int Sessions, string Label);
'@ | Set-Content -Encoding UTF8 -Path $program

    dotnet run --project $project --configuration Debug
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    $source = Get-Content -Raw -Path (Join-Path $repo "Services\FaceDetection\DetectorAutoTuner.cs")
    if ($source -notmatch 'for\s*\(int\s+sessions\s*=\s*1;\s*sessions\s*<=\s*requestedSessions;\s*sessions\+\+\)') {
        throw "Auto tuner does not enumerate every session count from one through the configured maximum."
    }
    if ($source -notmatch 'foreach\s*\(var\s+candidate\s+in\s+candidates\)[\s\S]*MeasureThroughput\([\s\S]*candidate\.Options,[\s\S]*candidate\.Sessions') {
        throw "Generated session candidates are not connected to throughput measurement."
    }

    $autoMaskSource = Get-Content -Raw -Path (Join-Path $repo "Services\Analysis\AutoMaskGenerator.cs")
    $roiPolicyUses = [regex]::Matches(
        $autoMaskSource,
        'ShouldUsePrimaryRoiShortcut\(_options\)\s*\?\s*lastFaces\s*:\s*null').Count
    if ($roiPolicyUses -ne 2) {
        throw "Expected sequential and single-session detection to share the ROI policy, found $roiPolicyUses paths."
    }

    Write-Host "[DetectorAutoTunerSessionRangeVerify] PASS measurement-connected=True fullFramePaths=2"
}
finally {
    if (Test-Path $work) {
        Remove-Item -Recurse -Force -Path $work
    }
}
