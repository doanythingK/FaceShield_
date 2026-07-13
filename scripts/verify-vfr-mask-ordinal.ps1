param(
    [string]$SourcePath = "",
    [string]$OutputPath = "",
    [string]$FfprobePath = ""
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$work = Join-Path $repo ".tmp\vfr-mask-ordinal"
$project = Join-Path $work "VfrMaskOrdinalHarness.csproj"
$program = Join-Path $work "Program.cs"

function Resolve-RepoPath {
    param(
        [string]$Path,
        [bool]$MustExist = $true
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return ""
    }

    $candidate = if ([IO.Path]::IsPathRooted($Path)) { $Path } else { Join-Path $repo $Path }
    if ($MustExist) {
        return (Resolve-Path $candidate).Path
    }

    return [IO.Path]::GetFullPath($candidate)
}

function Resolve-Tool {
    param(
        [string]$PreferredPath,
        [string]$ToolName
    )

    if (-not [string]::IsNullOrWhiteSpace($PreferredPath)) {
        return Resolve-RepoPath $PreferredPath
    }

    $command = Get-Command $ToolName -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        throw "$ToolName was not found. Add it to PATH or pass -${ToolName}Path."
    }

    return $command.Source
}

function Get-FramePts {
    param(
        [string]$Tool,
        [string]$Path
    )

    $rows = & $Tool @(
        "-v", "error",
        "-select_streams", "v:0",
        "-show_entries", "frame=best_effort_timestamp_time",
        "-of", "csv=p=0",
        $Path
    ) 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "ffprobe failed: $($rows | Out-String)"
    }

    return @($rows | ForEach-Object {
        $text = ([string]$_ -split ",")[0].Trim()
        if (-not [string]::IsNullOrWhiteSpace($text)) {
            [double]::Parse($text, [Globalization.CultureInfo]::InvariantCulture)
        }
    })
}

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
using Avalonia;
using FaceShield.Services.Video;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

var coverageMethod = typeof(VideoExportService).GetMethod(
    "GetMissingExpectedFrameIndices",
    BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("GetMissingExpectedFrameIndices was not found.");

AssertMissing(
    new HashSet<int> { 0, 2, 4 },
    new HashSet<int> { 0, 4 },
    [2]);
AssertMissing(
    new HashSet<int> { 0, 2 },
    new HashSet<int> { 0, 2, 3 },
    []);
AssertMissing(null, new HashSet<int>(), []);
Console.WriteLine("[VfrMaskOrdinalVerify] PASS coverage-cases=3");

if (args.Length is 2 or 3)
{
    string sourcePath = Path.GetFullPath(args[0]);
    string outputPath = Path.GetFullPath(args[1]);
    int maskFrameIndex = args.Length == 3
        ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture)
        : 2;
    if (File.Exists(outputPath))
        File.Delete(outputPath);

    FFmpegBootstrap.Initialize();
    var masks = new FrameMaskProvider();
    masks.SetFaceRects(
        maskFrameIndex,
        [new Rect(96, 48, 96, 96)],
        new PixelSize(320, 240),
        1.0f,
        [1.0f]);

    var exporter = new VideoExportService(masks);
    exporter.Export(sourcePath, outputPath, 20, runId: "vfr-mask-ordinal", allowHybridCopy: false);
    ExportRunSummary summary = exporter.LastExportSummary
        ?? throw new InvalidOperationException("Export summary was not created.");
    if (!summary.OutputCommitted ||
        summary.Frames != 4 ||
        summary.SubmittedVideoFrames != 4 ||
        summary.ExpectedBlurFrames != 1 ||
        summary.AppliedBlurFrames != 1 ||
        summary.DirectFaceFrames + summary.BitmapMaskFrames != 1 ||
        summary.VideoFrameCoverageMismatch != 0)
    {
        throw new InvalidOperationException($"Unexpected VFR export summary: {summary.ToLogLine()}");
    }

    Console.WriteLine(summary.ToLogLine());
    Console.WriteLine("[VfrMaskOrdinalVerify] PASS export-mask-frame=2");
}
else if (args.Length != 0)
{
    throw new ArgumentException("Expected no arguments or <source> <output> [mask-frame-index].");
}

void AssertMissing(HashSet<int>? expected, HashSet<int> applied, int[] wanted)
{
    int[] actual = (int[])(coverageMethod.Invoke(null, [expected, applied])
        ?? throw new InvalidOperationException("Coverage invocation returned null."));
    if (!actual.AsSpan().SequenceEqual(wanted))
    {
        throw new InvalidOperationException(
            $"Missing-frame mismatch: expected=[{string.Join(',', wanted)}], actual=[{string.Join(',', actual)}]");
    }
}
'@ | Set-Content -Encoding UTF8 $program

$runArguments = @("run", "--project", $project, "--configuration", "Debug")
$source = ""
$output = ""
if (-not [string]::IsNullOrWhiteSpace($SourcePath)) {
    $source = Resolve-RepoPath $SourcePath
    $output = if ([string]::IsNullOrWhiteSpace($OutputPath)) {
        Join-Path $work "vfr-mask-output.mkv"
    }
    else {
        Resolve-RepoPath $OutputPath $false
    }
    $runArguments += @("--", $source, $output)
}

$runOutput = & dotnet @runArguments 2>&1
$runExitCode = $LASTEXITCODE
$runOutput | ForEach-Object { Write-Host $_ }
if ($runExitCode -ne 0) {
    exit $runExitCode
}

$exportSource = Get-Content -Raw (Join-Path $repo "Services\Video\VideoExportService.cs")
if ($exportSource.Contains("ResolveFrameIndexFromFrame")) {
    throw "Export mask lookup must not derive frame ordinals from timestamps."
}
if (-not $exportSource.Contains("int decodedFrameOrdinal = frameIndex;")) {
    throw "Export must use the sequential decoded-frame ordinal."
}

if (-not [string]::IsNullOrWhiteSpace($source)) {
    $ffprobe = Resolve-Tool $FfprobePath "ffprobe"
    $sourcePts = @(Get-FramePts $ffprobe $source)
    $outputPts = @(Get-FramePts $ffprobe $output)
    if ($sourcePts.Count -ne 4 -or $outputPts.Count -ne $sourcePts.Count) {
        throw "Expected four VFR frames in both source and output."
    }

    for ($i = 0; $i -lt $sourcePts.Count; $i++) {
        if ([Math]::Abs($sourcePts[$i] - $outputPts[$i]) -gt 0.000001) {
            throw "Frame PTS changed at ordinal ${i}: $($sourcePts[$i]) -> $($outputPts[$i])."
        }
    }

    Write-Host "[VfrMaskOrdinalVerify] PASS pts=0,0.1,1.0,1.1"

    $missingOutput = Join-Path $work "missing-mask-output.mkv"
    Remove-Item -Force -ErrorAction SilentlyContinue $missingOutput
    $failureArguments = @(
        "run", "--project", $project, "--configuration", "Debug", "--",
        $source, $missingOutput, "4"
    )
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $failureOutput = & dotnet @failureArguments 2>&1
        $failureExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($failureExitCode -eq 0) {
        throw "An out-of-range mask frame must fail export."
    }
    if (Test-Path $missingOutput) {
        throw "Failed export left a committed output file: $missingOutput"
    }

    Write-Host "[VfrMaskOrdinalVerify] PASS missing-mask-fail-closed"
}
