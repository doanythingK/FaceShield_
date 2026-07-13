param(
    [string]$SourcePath = "",
    [string]$OutputPath = "",
    [long]$ExpectedMaxGap = -1
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$work = Join-Path $repo ".tmp\encoded-presentation-gaps"
$project = Join-Path $work "EncodedPresentationGapsHarness.csproj"
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
using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

var method = typeof(VideoExportService).GetMethod(
    "EvaluateEncodedPresentationGaps",
    BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("EvaluateEncodedPresentationGaps was not found.");
var durationMethod = typeof(VideoExportService).GetMethod(
    "ResolveEncodedPacketDuration",
    BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("ResolveEncodedPacketDuration was not found.");

AssertGaps(
    "CFR B-frame packet order",
    submitted: [0, 1001, 2002, 3003, 4004],
    emitted: [0, 4004, 2002, 1001, 3003],
    expectedOutliers: 0,
    expectedMaxGap: 1001);

AssertGaps(
    "VFR B-frame packet order",
    submitted: [0, 1024, 10240, 11264],
    emitted: [0, 10240, 1024, 11264],
    expectedOutliers: 0,
    expectedMaxGap: 9216);

AssertGaps(
    "negative reordered timestamps",
    submitted: [-300, -100, 100],
    emitted: [100, -300, -100],
    expectedOutliers: 0,
    expectedMaxGap: 200);

AssertGaps(
    "missing middle timestamp",
    submitted: [0, 100, 200],
    emitted: [0, 200],
    expectedOutliers: 2,
    expectedMaxGap: 200);

AssertGaps(
    "missing final timestamp",
    submitted: [0, 100, 200],
    emitted: [0, 100],
    expectedOutliers: 1,
    expectedMaxGap: 100);

AssertGaps(
    "duplicate emitted timestamp",
    submitted: [0, 100, 200],
    emitted: [0, 100, 100, 200],
    expectedOutliers: 2,
    expectedMaxGap: 100);

AssertGaps(
    "substituted timestamp",
    submitted: [0, 100, 200],
    emitted: [0, 110, 200],
    expectedOutliers: 2,
    expectedMaxGap: 110);

AssertGaps(
    "constant timestamp offset",
    submitted: [0, 100, 200],
    emitted: [10, 110, 210],
    expectedOutliers: 0,
    expectedMaxGap: 100);

AssertGaps(
    "signed long boundaries",
    submitted: [long.MinValue + 1, 0, long.MaxValue],
    emitted: [long.MaxValue, long.MinValue + 1, 0],
    expectedOutliers: 0,
    expectedMaxGap: long.MaxValue);

AssertGaps(
    "mux output time-base units",
    submitted: [0, 100, 200],
    emitted: [200, 0, 100],
    expectedOutliers: 0,
    expectedMaxGap: 512,
    muxPts: [1024, 0, 512]);

AssertGaps(
    "single timestamp",
    submitted: [42],
    emitted: [42],
    expectedOutliers: 0,
    expectedMaxGap: 0);

AssertGaps(
    "empty timestamps",
    submitted: [],
    emitted: [],
    expectedOutliers: 0,
    expectedMaxGap: 0);

AssertRejectNoPts();
AssertPacketDurations();

Console.WriteLine("[EncodedPresentationGapsVerify] PASS gapCases=14 durationPolicies=7");

if (args.Length is 2 or 3)
{
    string sourcePath = Path.GetFullPath(args[0]);
    string outputPath = Path.GetFullPath(args[1]);
    long expectedMaxGap = args.Length == 3
        ? long.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture)
        : -1;
    if (File.Exists(outputPath))
        File.Delete(outputPath);

    FFmpegBootstrap.Initialize();
    var exporter = new VideoExportService(new FrameMaskProvider());
    exporter.Export(sourcePath, outputPath, 20, runId: "remux-presentation-gaps");
    ExportRunSummary summary = exporter.LastExportSummary
        ?? throw new InvalidOperationException("Remux summary was not created.");
    if (!summary.OutputCommitted ||
        !string.Equals(summary.EncoderName, "stream-copy", StringComparison.Ordinal) ||
        summary.InputVideoPackets <= 0 ||
        summary.InputVideoPackets != summary.OutputVideoPackets ||
        summary.OutputPacketPtsGapOutlierCount != 0 ||
        (expectedMaxGap >= 0 && summary.MaxOutputPacketPtsGap != expectedMaxGap))
    {
        throw new InvalidOperationException($"Unexpected remux summary: {summary.ToLogLine()}");
    }

    Console.WriteLine(summary.ToLogLine());
    Console.WriteLine("[EncodedPresentationGapsVerify] PASS remux-export");
}
else if (args.Length != 0)
{
    throw new ArgumentException("Expected no arguments or <source> <output> [expected-max-gap].");
}

void AssertGaps(
    string name,
    long[] submitted,
    long[] emitted,
    int expectedOutliers,
    long expectedMaxGap,
    long[]? muxPts = null)
{
    object result = method.Invoke(
        null,
        [new HashSet<long>(submitted), CountPts(emitted), muxPts ?? emitted])
        ?? throw new InvalidOperationException($"{name}: invocation returned null.");
    Type resultType = result.GetType();
    int actualOutliers = (int)(resultType.GetField("Item1")?.GetValue(result)
        ?? throw new InvalidOperationException($"{name}: outlier field was not found."));
    long actualMaxGap = (long)(resultType.GetField("Item2")?.GetValue(result)
        ?? throw new InvalidOperationException($"{name}: max-gap field was not found."));

    if (actualOutliers != expectedOutliers || actualMaxGap != expectedMaxGap)
    {
        throw new InvalidOperationException(
            $"{name}: expected {expectedOutliers}/{expectedMaxGap}, " +
            $"got {actualOutliers}/{actualMaxGap}.");
    }
}

void AssertRejectNoPts()
{
    try
    {
        _ = method.Invoke(
            null,
            [new HashSet<long> { long.MinValue }, new Dictionary<long, int>(), Array.Empty<long>()]);
        throw new InvalidOperationException("AV_NOPTS_VALUE must be rejected.");
    }
    catch (TargetInvocationException ex) when (ex.InnerException is ArgumentException)
    {
    }

    try
    {
        _ = method.Invoke(
            null,
            [new HashSet<long>(), new Dictionary<long, int>(), new long[] { long.MinValue }]);
        throw new InvalidOperationException("Mux AV_NOPTS_VALUE must be rejected.");
    }
    catch (TargetInvocationException ex) when (ex.InnerException is ArgumentException)
    {
    }
}

void AssertPacketDurations()
{
    AssertDurationSequence(
        "CFR reordered packets",
        submitted: new() { [0] = 512, [512] = 512, [1024] = 512, [1536] = 512 },
        packetPts: [0, 1024, 512, 1536],
        packetDurations: [0, 0, 0, 0],
        expected: [512, 512, 512, 512]);
    AssertDurationSequence(
        "VFR presentation gap",
        submitted: new() { [0] = 100, [100] = 100, [1000] = 100, [1100] = 100 },
        packetPts: [0, 1000, 100, 1100],
        packetDurations: [0, 0, 0, 0],
        expected: [100, 100, 100, 100]);
    AssertDurationSequence(
        "VFR reordered packets",
        submitted: new() { [0] = 40, [40] = 160, [200] = 80, [280] = 250 },
        packetPts: [0, 200, 40, 280],
        packetDurations: [0, 0, 0, 0],
        expected: [40, 80, 160, 250]);

    AssertDuration("positive encoder duration", 33, 40, new() { [40] = 160 }, 33, 0);
    AssertDuration("unknown submitted duration", 0, 40, new() { [40] = 0 }, 0, 0);
    AssertDuration("missing submitted PTS", 0, 40, new() { [80] = 160 }, 0, 1);
    AssertDuration("missing packet PTS", 0, ffmpeg.AV_NOPTS_VALUE, new() { [40] = 160 }, 0, 1);

    long muxDuration = ffmpeg.av_rescale_q(
        1,
        new AVRational { num = 1, den = 30 },
        new AVRational { num = 1, den = 15360 });
    if (muxDuration != 512)
        throw new InvalidOperationException($"Duration rescale mismatch: expected=512, actual={muxDuration}.");
}

void AssertDurationSequence(
    string name,
    Dictionary<long, long> submitted,
    long[] packetPts,
    long[] packetDurations,
    long[] expected)
{
    if (packetPts.Length != packetDurations.Length || packetPts.Length != expected.Length)
        throw new ArgumentException($"{name}: sequence lengths differ.");

    for (int i = 0; i < packetPts.Length; i++)
    {
        AssertDuration(
            $"{name}[{i}]",
            packetDurations[i],
            packetPts[i],
            submitted,
            expected[i],
            packetPts.Length - i - 1);
    }
}

void AssertDuration(
    string name,
    long packetDuration,
    long packetPts,
    Dictionary<long, long> submitted,
    long expectedDuration,
    int expectedRemaining)
{
    long actual = (long)(durationMethod.Invoke(
        null,
        [packetDuration, packetPts, submitted])
        ?? throw new InvalidOperationException($"{name}: invocation returned null."));
    if (actual != expectedDuration || submitted.Count != expectedRemaining)
    {
        throw new InvalidOperationException(
            $"{name}: expected duration/remaining {expectedDuration}/{expectedRemaining}, " +
            $"got {actual}/{submitted.Count}.");
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

$runArguments = @("run", "--project", $project, "--configuration", "Debug")
if (-not [string]::IsNullOrWhiteSpace($SourcePath)) {
    $source = (Resolve-Path (Join-Path $repo $SourcePath)).Path
    $output = if ([string]::IsNullOrWhiteSpace($OutputPath)) {
        Join-Path $work "remux-output.mkv"
    }
    elseif ([IO.Path]::IsPathRooted($OutputPath)) {
        [IO.Path]::GetFullPath($OutputPath)
    }
    else {
        [IO.Path]::GetFullPath((Join-Path $repo $OutputPath))
    }
    $runArguments += @(
        "--",
        $source,
        $output,
        $ExpectedMaxGap.ToString([Globalization.CultureInfo]::InvariantCulture)
    )
}

dotnet @runArguments
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$source = Get-Content -Raw (Join-Path $repo "Services\Video\VideoExportService.cs")
if ($source.Contains("Math.Abs(outPkt->pts - previousPacketPts)")) {
    throw "Encoded PTS gaps must not be measured in packet emission order."
}
if (-not $source.Contains("outputPacketPtsGapOutlierCount = presentationGapIntegrity.OutlierCount;")) {
    throw "Presentation-gap integrity must update the export summary counter."
}
if (-not $source.Contains("maxOutputPacketPtsGap = GetMaxSortedPresentationGap(outputVideoPts);")) {
    throw "Remux maximum PTS gap must use sorted presentation order."
}
if ($source.Contains("outPkt->duration <= 0")) {
    throw "Unknown packet duration must not be synthesized from average FPS."
}
if (-not $source.Contains("submittedEncodedFrameDurations.Remove(")) {
    throw "Encoded packet duration recovery must consume the exact submitted PTS mapping."
}
if ($source.Contains(": GetVideoFrameStep(sourceFps, targetTimeBase);")) {
    throw "Unknown frame duration must remain unknown instead of using average FPS."
}

Write-Host "[EncodedPresentationGapsVerify] PASS integration-guards"
