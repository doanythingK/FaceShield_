param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$work = Join-Path $repo ".tmp\container-structure-guard"
$project = Join-Path $work "ContainerStructureGuardHarness.csproj"
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

FFmpegBootstrap.Initialize();

unsafe
{
    AVFormatContext* format = ffmpeg.avformat_alloc_context();
    if (format == null)
        throw new InvalidOperationException("Unable to allocate format context.");
    try
    {
        AssertStructure(format, null);

        format->nb_programs = 1;
        AssertStructure(format, "programs");

        format->nb_programs = 0;
        format->nb_stream_groups = 1;
        AssertStructure(format, "stream-groups");
    }
    finally
    {
        format->nb_programs = 0;
        format->nb_stream_groups = 0;
        ffmpeg.avformat_free_context(format);
    }
}

Console.WriteLine("[ContainerStructureGuardVerify] PASS cases=3");

static unsafe void AssertStructure(AVFormatContext* format, string? expected)
{
    string? actual = FFmpegContainerStructureGuard.FindUnsupportedStructure(format);
    if (!string.Equals(actual, expected, StringComparison.Ordinal))
        throw new InvalidOperationException($"Structure mismatch: expected={expected}, actual={actual}");
}
'@ | Set-Content -Encoding UTF8 $program

dotnet run --project $project --configuration Debug
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$exportSource = Get-Content -Raw (Join-Path $repo "Services\Video\VideoExportService.cs")
$initialGuardPattern =
    'avformat_find_stream_info\(inFmt, null\)\);\s*EnsureContainerStructureSupported\(inFmt\);'
if (-not [Regex]::IsMatch($exportSource, $initialGuardPattern)) {
    throw "Container structure guard must run immediately after stream discovery."
}

$packetLoopGuardPattern =
    'while \(ffmpeg\.av_read_frame\(inFmt, pkt\) >= 0\)\s*\{\s*EnsureContainerStructureSupported\(inFmt\);'
$packetLoopGuardCount = [Regex]::Matches($exportSource, $packetLoopGuardPattern).Count
if ($packetLoopGuardCount -lt 2) {
    throw "Both full encode and remux packet loops must guard late container structures."
}

Write-Host "[ContainerStructureGuardVerify] PASS discovery-and-packet-loop-guards"
