param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$work = Join-Path $repo ".tmp\export-frame-coverage"
$project = Join-Path $work "ExportFrameCoverageHarness.csproj"
$program = Join-Path $work "Program.cs"

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
using System;
using System.Collections.Generic;
using System.Reflection;

var method = typeof(VideoExportService).GetMethod(
    "EvaluateVideoFrameCoverage",
    BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("EvaluateVideoFrameCoverage was not found.");

AssertCoverage(
    "matching encoded frames",
    copiedSourcePackets: 0,
    copiedOutputPackets: 0,
    submittedPts: [100, 200, 300],
    emittedPts: [100, 200, 300],
    expected: [3, 0, 0, 0, 0, 0]);

AssertCoverage(
    "missing encoded frame",
    copiedSourcePackets: 0,
    copiedOutputPackets: 0,
    submittedPts: [100, 200, 300],
    emittedPts: [100, 300],
    expected: [3, 1, 1, 1, 0, 0]);

AssertCoverage(
    "unexpected encoded frame",
    copiedSourcePackets: 0,
    copiedOutputPackets: 0,
    submittedPts: [100, 200],
    emittedPts: [100, 200, 300],
    expected: [2, 1, 0, 0, 1, 0]);

AssertCoverage(
    "hybrid copy loss",
    copiedSourcePackets: 3,
    copiedOutputPackets: 2,
    submittedPts: [100, 200],
    emittedPts: [100, 200],
    expected: [5, 1, 1, 0, 0, 1]);

AssertCoverage(
    "duplicate emitted PTS",
    copiedSourcePackets: 0,
    copiedOutputPackets: 0,
    submittedPts: [100, 200],
    emittedPts: [100, 100, 200],
    expected: [2, 1, 0, 0, 1, 0]);

AssertCoverage(
    "missing and unexpected PTS",
    copiedSourcePackets: 0,
    copiedOutputPackets: 0,
    submittedPts: [100, 200],
    emittedPts: [100, 300],
    expected: [2, 2, 1, 1, 1, 0]);

AssertCoverage(
    "hybrid copy surplus",
    copiedSourcePackets: 2,
    copiedOutputPackets: 3,
    submittedPts: [],
    emittedPts: [],
    expected: [2, 1, 0, 0, 0, 1]);

AssertCoverage(
    "negative and reordered PTS",
    copiedSourcePackets: 0,
    copiedOutputPackets: 0,
    submittedPts: [-300, -100, 100],
    emittedPts: [100, -300, -100],
    expected: [3, 0, 0, 0, 0, 0]);

AssertCoverage(
    "empty input",
    copiedSourcePackets: 0,
    copiedOutputPackets: 0,
    submittedPts: [],
    emittedPts: [],
    expected: [0, 0, 0, 0, 0, 0]);

Console.WriteLine("[ExportFrameCoverageVerify] PASS cases=9");

void AssertCoverage(
    string name,
    int copiedSourcePackets,
    int copiedOutputPackets,
    long[] submittedPts,
    long[] emittedPts,
    int[] expected)
{
    object result = method.Invoke(
        null,
        [
            copiedSourcePackets,
            copiedOutputPackets,
            new HashSet<long>(submittedPts),
            CountPts(emittedPts)
        ]) ?? throw new InvalidOperationException($"{name}: invocation returned null.");

    Type resultType = result.GetType();
    for (int i = 0; i < expected.Length; i++)
    {
        int actual = (int)(resultType.GetField($"Item{i + 1}")?.GetValue(result)
            ?? throw new InvalidOperationException($"{name}: result item {i + 1} was not found."));
        if (actual != expected[i])
        {
            throw new InvalidOperationException(
                $"{name}: item {i + 1} expected {expected[i]}, got {actual}.");
        }
    }
}

static Dictionary<long, int> CountPts(IEnumerable<long> values)
{
    var counts = new Dictionary<long, int>();
    foreach (long value in values)
    {
        counts.TryGetValue(value, out int count);
        counts[value] = count + 1;
    }

    return counts;
}
'@ | Set-Content -Encoding UTF8 $program

dotnet run --project $project --configuration Debug
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$exportSource = Get-Content -Raw (Join-Path $repo "Services\Video\VideoExportService.cs")
if ($exportSource.Contains('Math.Abs((long)inputVideoPacketCount - outputVideoPacketCount)')) {
    throw "Demux packet count must not be compared with encoded frame packet count."
}

$hybridRetryPattern =
    'bool canRetryWithFullEncode = allowPacketDropRetry && useHybridCopyWindow;'
if (-not $exportSource.Contains($hybridRetryPattern)) {
    throw "Only a hybrid attempt may retry as a full encode."
}

if (-not $exportSource.Contains('ex is not VideoExportIntegrityException')) {
    throw "Deterministic decode and timeline integrity failures must not enter encoder fallback."
}

Write-Host "[ExportFrameCoverageVerify] PASS packet-frame-accounting-guards"
