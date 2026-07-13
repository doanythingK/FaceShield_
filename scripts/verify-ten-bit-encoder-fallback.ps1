param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$work = Join-Path $repo ".tmp\ten-bit-encoder-fallback"
$project = Join-Path $work "TenBitEncoderFallbackHarness.csproj"
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
"@ | Set-Content -Encoding UTF8 -Path $project

@'
using FaceShield.Services.Video;
using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

MethodInfo getCandidates = typeof(VideoExportService).GetMethod(
    "GetEncoderCandidateNames",
    BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("GetEncoderCandidateNames was not found.");

string[] softwarePrimary = InvokeCandidates(
    AVCodecID.AV_CODEC_ID_H264,
    forceSoftwareOnly: true,
    allowTenBitHevcFallback: false);
AssertSequence("software H.264", softwarePrimary, "libx264");

string[] softwareTenBit = InvokeCandidates(
    AVCodecID.AV_CODEC_ID_H264,
    forceSoftwareOnly: true,
    allowTenBitHevcFallback: true);
AssertSequence("software 10-bit H.264 fallback", softwareTenBit, "libx264", "libx265");

string[] normalTenBit = InvokeCandidates(
    AVCodecID.AV_CODEC_ID_H264,
    forceSoftwareOnly: false,
    allowTenBitHevcFallback: true);
if (!normalTenBit.Any(name => name.StartsWith("h264_", StringComparison.OrdinalIgnoreCase)) ||
    !normalTenBit.Any(name => name.StartsWith("hevc_", StringComparison.OrdinalIgnoreCase)))
{
    throw new InvalidOperationException(
        $"10-bit candidates are missing hardware codec families: {string.Join(",", normalTenBit)}");
}
if (normalTenBit.Length < 4 ||
    !string.Equals(normalTenBit[^2], "libx264", StringComparison.OrdinalIgnoreCase) ||
    !string.Equals(normalTenBit[^1], "libx265", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException(
        $"Software fallbacks are not last in the expected order: {string.Join(",", normalTenBit)}");
}
if (normalTenBit.Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalTenBit.Length)
    throw new InvalidOperationException($"10-bit candidates contain duplicates: {string.Join(",", normalTenBit)}");

string[] guardedHevc = InvokeCandidates(
    AVCodecID.AV_CODEC_ID_HEVC,
    forceSoftwareOnly: true,
    allowTenBitHevcFallback: true);
AssertSequence("HEVC guard", guardedHevc, "libx265");

VerifyLibx265TenBitOpen();

Console.WriteLine(
    $"[TenBitEncoderFallbackVerify] PASS software={string.Join(",", softwareTenBit)} " +
    $"normal={string.Join(",", normalTenBit)} libx265TenBitOpen=true");

string[] InvokeCandidates(
    AVCodecID codecId,
    bool forceSoftwareOnly,
    bool allowTenBitHevcFallback)
{
    object? result = getCandidates.Invoke(
        null,
        [codecId, forceSoftwareOnly, allowTenBitHevcFallback]);
    return result is IEnumerable<string> names
        ? names.ToArray()
        : throw new InvalidOperationException("Encoder candidates were not returned.");
}

static void AssertSequence(string name, string[] actual, params string[] expected)
{
    if (!actual.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            $"{name} mismatch: expected={string.Join(",", expected)}, actual={string.Join(",", actual)}");
    }
}

static unsafe void VerifyLibx265TenBitOpen()
{
    FFmpegBootstrap.Initialize();
    AVCodec* encoder = ffmpeg.avcodec_find_encoder_by_name("libx265");
    if (encoder == null || encoder->id != AVCodecID.AV_CODEC_ID_HEVC)
        throw new InvalidOperationException("Bundled FFmpeg does not provide libx265 HEVC encoding.");

    AVCodecContext* context = ffmpeg.avcodec_alloc_context3(encoder);
    if (context == null)
        throw new InvalidOperationException("Unable to allocate the libx265 encoder context.");

    try
    {
        context->width = 64;
        context->height = 64;
        context->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P10LE;
        context->time_base = new AVRational { num = 1, den = 30 };
        context->framerate = new AVRational { num = 30, den = 1 };
        context->thread_count = 1;

        SetRequiredOption(context, "preset", "fast");
        SetRequiredOption(context, "crf", "16");

        int openResult = ffmpeg.avcodec_open2(context, encoder, null);
        if (openResult < 0)
        {
            throw new InvalidOperationException(
                $"libx265 rejected yuv420p10le: {GetError(openResult)}");
        }
    }
    finally
    {
        ffmpeg.avcodec_free_context(&context);
    }
}

static unsafe void SetRequiredOption(AVCodecContext* context, string key, string value)
{
    int result = ffmpeg.av_opt_set(context->priv_data, key, value, 0);
    if (result < 0)
        throw new InvalidOperationException($"libx265 option {key}={value} failed: {GetError(result)}");
}

static unsafe string GetError(int result)
{
    byte* buffer = stackalloc byte[512];
    ffmpeg.av_strerror(result, buffer, 512);
    return Marshal.PtrToStringAnsi((IntPtr)buffer) ?? result.ToString();
}
'@ | Set-Content -Encoding UTF8 -Path $program

dotnet run --project $project --configuration Debug
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
