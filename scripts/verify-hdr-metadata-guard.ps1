param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$work = Join-Path $repo ".tmp\hdr-metadata-guard"
$project = Join-Path $work "HdrMetadataGuardHarness.csproj"
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
    AssertPacketMetadata(AVPacketSideDataType.AV_PKT_DATA_DOVI_CONF, "Dolby Vision");
    AssertPacketMetadata(AVPacketSideDataType.AV_PKT_DATA_DYNAMIC_HDR10_PLUS, "HDR10+");
    AssertPacketMetadata(
        AVPacketSideDataType.AV_PKT_DATA_AMBIENT_VIEWING_ENVIRONMENT,
        "HDR ambient viewing environment");

    AssertFrameMetadata(AVFrameSideDataType.AV_FRAME_DATA_DOVI_RPU_BUFFER, "Dolby Vision");
    AssertFrameMetadata(AVFrameSideDataType.AV_FRAME_DATA_DOVI_METADATA, "Dolby Vision");
    AssertFrameMetadata(AVFrameSideDataType.AV_FRAME_DATA_DYNAMIC_HDR_PLUS, "HDR10+");
    AssertFrameMetadata(AVFrameSideDataType.AV_FRAME_DATA_DYNAMIC_HDR_VIVID, "HDR Vivid");
    AssertFrameMetadata(
        AVFrameSideDataType.AV_FRAME_DATA_AMBIENT_VIEWING_ENVIRONMENT,
        "HDR ambient viewing environment");

    AVFrame* staticHdrFrame = ffmpeg.av_frame_alloc();
    if (staticHdrFrame == null)
        throw new InvalidOperationException("Unable to allocate static HDR frame.");
    try
    {
        if (ffmpeg.av_frame_new_side_data(
                staticHdrFrame,
                AVFrameSideDataType.AV_FRAME_DATA_MASTERING_DISPLAY_METADATA,
                1) == null)
        {
            throw new InvalidOperationException("Unable to add static HDR metadata.");
        }

        if (!FFmpegHdrMetadataGuard.HasStaticHdrMetadata(staticHdrFrame))
            throw new InvalidOperationException("Static HDR metadata was not detected.");
        if (FFmpegHdrMetadataGuard.FindUnsupportedMetadata(staticHdrFrame) != null)
            throw new InvalidOperationException("Static HDR metadata was incorrectly rejected.");
    }
    finally
    {
        ffmpeg.av_frame_free(&staticHdrFrame);
    }
}

Console.WriteLine("[HdrMetadataGuardVerify] PASS cases=9");

static unsafe void AssertPacketMetadata(AVPacketSideDataType type, string expected)
{
    AVCodecParameters* parameters = ffmpeg.avcodec_parameters_alloc();
    if (parameters == null)
        throw new InvalidOperationException("Unable to allocate codec parameters.");
    try
    {
        AVPacketSideData* sideData = ffmpeg.av_packet_side_data_new(
            &parameters->coded_side_data,
            &parameters->nb_coded_side_data,
            type,
            1,
            0);
        if (sideData == null)
            throw new InvalidOperationException($"Unable to add packet side data: {type}");

        string? actual = FFmpegHdrMetadataGuard.FindUnsupportedMetadata(parameters);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"Packet metadata mismatch: {type}, actual={actual}");
    }
    finally
    {
        ffmpeg.avcodec_parameters_free(&parameters);
    }
}

static unsafe void AssertFrameMetadata(AVFrameSideDataType type, string expected)
{
    AVFrame* frame = ffmpeg.av_frame_alloc();
    if (frame == null)
        throw new InvalidOperationException("Unable to allocate frame.");
    try
    {
        if (ffmpeg.av_frame_new_side_data(frame, type, 1) == null)
            throw new InvalidOperationException($"Unable to add frame side data: {type}");

        string? actual = FFmpegHdrMetadataGuard.FindUnsupportedMetadata(frame);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"Frame metadata mismatch: {type}, actual={actual}");
    }
    finally
    {
        ffmpeg.av_frame_free(&frame);
    }
}
'@ | Set-Content -Encoding UTF8 $program

dotnet run --project $project --configuration Debug
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$exportSource = Get-Content -Raw (Join-Path $repo "Services\Video\VideoExportService.cs")
$losslessRemuxIndex = $exportSource.IndexOf(
    "if (blurFrameSet.Count == 0)",
    [StringComparison]::Ordinal)
$streamGuardIndex = $exportSource.IndexOf(
    "FFmpegHdrMetadataGuard.FindUnsupportedMetadata(inStream->codecpar)",
    [StringComparison]::Ordinal)
if ($losslessRemuxIndex -lt 0 -or $streamGuardIndex -le $losslessRemuxIndex) {
    throw "Dynamic metadata guard must run after the lossless no-mask remux path."
}

Write-Host "[HdrMetadataGuardVerify] PASS no-mask-remux-before-reencode-guard"
