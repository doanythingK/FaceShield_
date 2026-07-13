param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$work = Join-Path $repo ".tmp\video-timestamp-resolution"
$project = Join-Path $work "VideoTimestampResolutionHarness.csproj"
$program = Join-Path $work "Program.cs"

New-Item -ItemType Directory -Force -Path $work | Out-Null

@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
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
using System.Reflection;

unsafe
{
    var method = typeof(VideoExportService).GetMethod(
        "ResolveEncodePts",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ResolveEncodePts was not found.");

    var sourceTimeBase = new AVRational { num = 1, den = 1000 };
    var targetTimeBase = new AVRational { num = 1, den = 30000 };

    AVFrame negativeFrame = default;
    negativeFrame.best_effort_timestamp = -500;
    negativeFrame.pts = -500;
    object?[] negativeArgs =
    {
        Pointer.Box(&negativeFrame, typeof(AVFrame*)),
        sourceTimeBase,
        targetTimeBase,
        0,
        30d,
        -1L,
        false
    };
    long negativePts = (long)(method.Invoke(null, negativeArgs)
        ?? throw new InvalidOperationException("Negative PTS invocation returned null."));
    if (negativePts != -15000 || (long)negativeArgs[5]! != -15000 || !(bool)negativeArgs[6]!)
        throw new InvalidOperationException($"Expected -15000 for a valid negative PTS, got {negativePts}.");

    AVFrame missingFrame = default;
    missingFrame.best_effort_timestamp = ffmpeg.AV_NOPTS_VALUE;
    missingFrame.pts = ffmpeg.AV_NOPTS_VALUE;
    object?[] continuedArgs =
    {
        Pointer.Box(&missingFrame, typeof(AVFrame*)),
        sourceTimeBase,
        targetTimeBase,
        1,
        30d,
        -15000L,
        true
    };
    long continuedPts = (long)(method.Invoke(null, continuedArgs)
        ?? throw new InvalidOperationException("Continued PTS invocation returned null."));
    if (continuedPts != -14000)
        throw new InvalidOperationException($"Expected missing PTS to continue at -14000, got {continuedPts}.");

    object?[] fallbackArgs =
    {
        Pointer.Box(&missingFrame, typeof(AVFrame*)),
        sourceTimeBase,
        targetTimeBase,
        3,
        30d,
        -1L,
        false
    };
    long fallbackPts = (long)(method.Invoke(null, fallbackArgs)
        ?? throw new InvalidOperationException("Fallback PTS invocation returned null."));
    if (fallbackPts != 3000)
        throw new InvalidOperationException($"Expected fallback PTS 3000, got {fallbackPts}.");

    Console.WriteLine(
        $"[VideoTimestampResolutionVerify] PASS negative={negativePts}, continued={continuedPts}, fallback={fallbackPts}");
}
'@ | Set-Content -Encoding UTF8 $program

dotnet run --project $project --configuration Debug
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
