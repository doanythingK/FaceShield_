param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$work = Join-Path $repo ".tmp\primary-video-stream-selection"
$project = Join-Path $work "PrimaryVideoStreamSelectionHarness.csproj"
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

Verify(
    expected: 2,
    (AVMediaType.AVMEDIA_TYPE_VIDEO, ffmpeg.AV_DISPOSITION_ATTACHED_PIC),
    (AVMediaType.AVMEDIA_TYPE_AUDIO, 0),
    (AVMediaType.AVMEDIA_TYPE_VIDEO, 0));
Verify(
    expected: 1,
    (AVMediaType.AVMEDIA_TYPE_VIDEO, 0),
    (AVMediaType.AVMEDIA_TYPE_VIDEO, ffmpeg.AV_DISPOSITION_DEFAULT));
Verify(
    expected: -1,
    (AVMediaType.AVMEDIA_TYPE_VIDEO, ffmpeg.AV_DISPOSITION_TIMED_THUMBNAILS),
    (AVMediaType.AVMEDIA_TYPE_VIDEO, ffmpeg.AV_DISPOSITION_STILL_IMAGE));
Verify(
    expected: 1,
    (AVMediaType.AVMEDIA_TYPE_VIDEO, ffmpeg.AV_DISPOSITION_ATTACHED_PIC),
    (AVMediaType.AVMEDIA_TYPE_VIDEO, ffmpeg.AV_DISPOSITION_DEFAULT),
    (AVMediaType.AVMEDIA_TYPE_VIDEO, 0));

Console.WriteLine("[PrimaryVideoStreamSelectionVerify] PASS cases=4");

static unsafe void Verify(
    int expected,
    params (AVMediaType MediaType, int Disposition)[] streams)
{
    AVFormatContext* format = ffmpeg.avformat_alloc_context();
    if (format == null)
        throw new InvalidOperationException("Unable to allocate format context.");

    try
    {
        foreach ((AVMediaType mediaType, int disposition) in streams)
        {
            AVStream* stream = ffmpeg.avformat_new_stream(format, null);
            if (stream == null || stream->codecpar == null)
                throw new InvalidOperationException("Unable to allocate stream.");
            stream->codecpar->codec_type = mediaType;
            stream->disposition = disposition;
        }

        int actual = FFmpegStreamSelection.FindPrimaryVideoStreamIndex(format);
        if (actual != expected)
            throw new InvalidOperationException($"expected={expected}, actual={actual}");
    }
    finally
    {
        ffmpeg.avformat_free_context(format);
    }
}
'@ | Set-Content -Encoding UTF8 $program

dotnet run --project $project --configuration Debug
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
