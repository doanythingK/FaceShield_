param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$work = Join-Path $repo ".tmp\av1-encoder-policy"
$project = Join-Path $work "Av1EncoderPolicyHarness.csproj"
$program = Join-Path $work "Program.cs"

try {
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

FFmpegBootstrap.Initialize();

MethodInfo getPreferredEncoderNames = typeof(VideoExportService).GetMethod(
    "GetPreferredEncoderNames",
    BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("GetPreferredEncoderNames was not found.");
MethodInfo usesSoftwareConstantQuality = typeof(VideoExportService).GetMethod(
    "UsesSoftwareConstantQuality",
    BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("UsesSoftwareConstantQuality was not found.");
MethodInfo resolveTargetBitrate = typeof(VideoExportService).GetMethod(
    "ResolveHighQualityTargetBitrate",
    BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("ResolveHighQualityTargetBitrate was not found.");

string[] softwareCandidates = InvokeCandidates(forceSoftwareOnly: true);
AssertSequence(
    "software AV1 candidates",
    softwareCandidates,
    ["libsvtav1", "libaom-av1"]);

string[] expectedPlatformCandidates = OperatingSystem.IsWindows()
    ? ["av1_nvenc", "av1_qsv", "av1_amf", "libsvtav1", "libaom-av1"]
    : OperatingSystem.IsMacOS()
        ? ["libsvtav1", "libaom-av1"]
        : ["av1_nvenc", "av1_qsv", "libsvtav1", "libaom-av1"];
string[] platformCandidates = InvokeCandidates(forceSoftwareOnly: false);
AssertSequence("platform AV1 candidates", platformCandidates, expectedPlatformCandidates);

foreach (string name in softwareCandidates)
{
    bool usesCq = (bool)(usesSoftwareConstantQuality.Invoke(null, [name])
        ?? throw new InvalidOperationException($"CQ policy returned no value for {name}."));
    if (!usesCq)
        throw new InvalidOperationException($"AV1 software encoder is not in CQ mode: {name}");
}

AssertTargetBitrate(
    "low-bitrate 4K AV1 floor",
    sourceBitrate: 1_000_000,
    width: 3840,
    height: 2160,
    codecId: AVCodecID.AV_CODEC_ID_AV1,
    expected: 28_000_000);
AssertTargetBitrate(
    "high-bitrate AV1 source scale",
    sourceBitrate: 30_000_000,
    width: 3840,
    height: 2160,
    codecId: AVCodecID.AV_CODEC_ID_AV1,
    expected: 45_000_000);
AssertTargetBitrate(
    "legacy H264 source scale",
    sourceBitrate: 1_000_000,
    width: 3840,
    height: 2160,
    codecId: AVCodecID.AV_CODEC_ID_H264,
    expected: 2_000_000);

int availableHardware = VerifyHardwarePixelFormats(platformCandidates);
int availableSoftware = 0;
int encodedPackets = 0;
foreach (string encoderName in softwareCandidates)
{
    unsafe
    {
        AVCodec* encoder = ffmpeg.avcodec_find_encoder_by_name(encoderName);
        if (encoder == null)
        {
            Console.WriteLine($"[Av1EncoderPolicyVerify] SKIP encoder={encoderName} reason=not-available");
            continue;
        }
    }

    availableSoftware++;
    encodedPackets += VerifySoftwareEncode(encoderName, AVPixelFormat.AV_PIX_FMT_YUV420P);
    encodedPackets += VerifySoftwareEncode(encoderName, AVPixelFormat.AV_PIX_FMT_YUV420P10LE);
}

if (availableSoftware == 0)
    throw new InvalidOperationException("No AV1 software fallback encoder was available.");

encodedPackets += VerifySoftwareEncode("libaom-av1", AVPixelFormat.AV_PIX_FMT_YUV420P12LE);
encodedPackets += VerifySoftwareEncode("libaom-av1", AVPixelFormat.AV_PIX_FMT_YUV422P);
encodedPackets += VerifySoftwareEncode("libaom-av1", AVPixelFormat.AV_PIX_FMT_YUV444P);
encodedPackets += VerifySoftwareEncode("libaom-av1", AVPixelFormat.AV_PIX_FMT_GBRP);

Console.WriteLine(
    $"[Av1EncoderPolicyVerify] PASS candidates={string.Join(',', platformCandidates)} " +
    $"software={string.Join(',', softwareCandidates)} hardware={availableHardware} " +
    $"softwareAvailable={availableSoftware} encodedPackets={encodedPackets} " +
    "bitDepths=8,10,12 chroma=420,422,444 rgb=true bitrateFloor=true");

string[] InvokeCandidates(bool forceSoftwareOnly)
{
    object? value = getPreferredEncoderNames.Invoke(
        null,
        [AVCodecID.AV_CODEC_ID_AV1, forceSoftwareOnly]);
    if (value is not IEnumerable<string> names)
        throw new InvalidOperationException("AV1 candidate policy returned no names.");
    return names.ToArray();
}

void AssertTargetBitrate(
    string name,
    long sourceBitrate,
    int width,
    int height,
    AVCodecID codecId,
    int expected)
{
    object? value = resolveTargetBitrate.Invoke(
        null,
        [
            sourceBitrate,
            width,
            height,
            new AVRational { num = 30, den = 1 },
            codecId
        ]);
    if (value is not int actual || actual != expected)
        throw new InvalidOperationException($"{name} mismatch: expected={expected}, actual={value}");
}

static void AssertSequence(string name, IReadOnlyList<string> actual, IReadOnlyList<string> expected)
{
    if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
    {
        throw new InvalidOperationException(
            $"{name} mismatch: expected={string.Join(',', expected)}, actual={string.Join(',', actual)}");
    }
}

static unsafe int VerifyHardwarePixelFormats(IEnumerable<string> candidates)
{
    int available = 0;
    foreach (string name in candidates.Where(IsHardwareName))
    {
        AVCodec* encoder = ffmpeg.avcodec_find_encoder_by_name(name);
        if (encoder == null)
            continue;

        available++;
        bool supportsEightBit = false;
        bool supportsTenBit = false;
#pragma warning disable CS0618
        if (encoder->pix_fmts != null)
        {
            for (AVPixelFormat* format = encoder->pix_fmts;
                 *format != AVPixelFormat.AV_PIX_FMT_NONE;
                 format++)
            {
                AVPixFmtDescriptor* descriptor = ffmpeg.av_pix_fmt_desc_get(*format);
                if (descriptor == null || descriptor->nb_components == 0)
                    continue;
                int depth = descriptor->comp[0].depth;
                supportsEightBit |= depth == 8;
                supportsTenBit |= depth >= 10;
            }
        }
#pragma warning restore CS0618

        if (!supportsEightBit || !supportsTenBit)
        {
            throw new InvalidOperationException(
                $"{name} does not advertise both 8-bit and 10-bit AV1 formats.");
        }
    }

    return available;
}

static bool IsHardwareName(string name)
{
    return name.Contains("nvenc", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("qsv", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("amf", StringComparison.OrdinalIgnoreCase);
}

static unsafe int VerifySoftwareEncode(string encoderName, AVPixelFormat pixelFormat)
{
    AVCodec* encoder = ffmpeg.avcodec_find_encoder_by_name(encoderName);
    if (encoder == null)
        throw new InvalidOperationException($"Encoder disappeared during verification: {encoderName}");
    if (!SupportsPixelFormat(encoder, pixelFormat))
    {
        throw new InvalidOperationException(
            $"{encoderName} does not advertise {GetPixelFormatName(pixelFormat)}.");
    }

    AVCodecContext* context = ffmpeg.avcodec_alloc_context3(encoder);
    if (context == null)
        throw new InvalidOperationException($"Unable to allocate {encoderName}.");

    AVFrame* frame = null;
    AVPacket* packet = null;
    try
    {
        context->width = 128;
        context->height = 72;
        context->pix_fmt = pixelFormat;
        context->time_base = new AVRational { num = 1, den = 30 };
        context->framerate = new AVRational { num = 30, den = 1 };
        context->thread_count = 2;
        context->bit_rate = 0;
        context->rc_max_rate = 0;
        context->rc_buffer_size = 0;
        context->gop_size = 30;
        context->max_b_frames = 0;

        if (encoderName.Contains("svtav1", StringComparison.OrdinalIgnoreCase))
        {
            SetRequiredOption(context, encoderName, "preset", "6");
            SetRequiredOption(context, encoderName, "crf", "12");
            SetRequiredOption(context, encoderName, "svtav1-params", "tune=0");
        }
        else
        {
            SetRequiredOption(context, encoderName, "usage", "good");
            SetRequiredOption(context, encoderName, "cpu-used", "4");
            SetRequiredOption(context, encoderName, "crf", "12");
            SetOptionalOption(context, "row-mt", "1");
            SetOptionalOption(context, "tune", "psnr");
        }

        int openResult = ffmpeg.avcodec_open2(context, encoder, null);
        if (openResult < 0)
        {
            throw new InvalidOperationException(
                $"{encoderName} rejected {GetPixelFormatName(pixelFormat)}: {GetError(openResult)}");
        }

        frame = ffmpeg.av_frame_alloc();
        packet = ffmpeg.av_packet_alloc();
        if (frame == null || packet == null)
            throw new InvalidOperationException($"Unable to allocate {encoderName} frame or packet.");

        frame->format = (int)pixelFormat;
        frame->width = context->width;
        frame->height = context->height;
        int bufferResult = ffmpeg.av_frame_get_buffer(frame, 32);
        if (bufferResult < 0)
            throw new InvalidOperationException($"Unable to allocate AV1 frame: {GetError(bufferResult)}");

        int packetCount = 0;
        for (int frameIndex = 0; frameIndex < 3; frameIndex++)
        {
            int writableResult = ffmpeg.av_frame_make_writable(frame);
            if (writableResult < 0)
                throw new InvalidOperationException($"AV1 frame is not writable: {GetError(writableResult)}");
            FillFrame(frame, frameIndex, pixelFormat);
            frame->pts = frameIndex;

            int sendResult = ffmpeg.avcodec_send_frame(context, frame);
            if (sendResult < 0)
                throw new InvalidOperationException($"{encoderName} frame send failed: {GetError(sendResult)}");
            packetCount += DrainPackets(context, packet, encoderName);
        }

        int flushResult = ffmpeg.avcodec_send_frame(context, null);
        if (flushResult < 0 && flushResult != ffmpeg.AVERROR_EOF)
            throw new InvalidOperationException($"{encoderName} flush failed: {GetError(flushResult)}");
        packetCount += DrainPackets(context, packet, encoderName);
        if (packetCount < 1)
        {
            throw new InvalidOperationException(
                $"{encoderName} emitted no packet for {GetPixelFormatName(pixelFormat)}.");
        }

        Console.WriteLine(
            $"[Av1EncoderPolicyVerify] PASS encoder={encoderName} " +
            $"pixelFormat={GetPixelFormatName(pixelFormat)} packets={packetCount}");
        return packetCount;
    }
    finally
    {
        if (packet != null)
            ffmpeg.av_packet_free(&packet);
        if (frame != null)
            ffmpeg.av_frame_free(&frame);
        ffmpeg.avcodec_free_context(&context);
    }
}

static unsafe bool SupportsPixelFormat(AVCodec* encoder, AVPixelFormat expected)
{
#pragma warning disable CS0618
    if (encoder->pix_fmts == null)
        return true;
    for (AVPixelFormat* format = encoder->pix_fmts;
         *format != AVPixelFormat.AV_PIX_FMT_NONE;
         format++)
    {
        if (*format == expected)
            return true;
    }
    return false;
#pragma warning restore CS0618
}

static unsafe void FillFrame(AVFrame* frame, int frameIndex, AVPixelFormat pixelFormat)
{
    AVPixFmtDescriptor* descriptor = ffmpeg.av_pix_fmt_desc_get(pixelFormat);
    if (descriptor == null || descriptor->nb_components < 3)
        throw new InvalidOperationException($"Unsupported test pixel format: {pixelFormat}");

    int bitDepth = descriptor->comp[0].depth;
    int chromaWidth = (frame->width + (1 << descriptor->log2_chroma_w) - 1) >> descriptor->log2_chroma_w;
    int chromaHeight = (frame->height + (1 << descriptor->log2_chroma_h) - 1) >> descriptor->log2_chroma_h;
    if (bitDepth > 8)
    {
        ushort neutral = (ushort)(1 << (bitDepth - 1));
        ushort luma = (ushort)((1 << (bitDepth - 2)) + frameIndex * (1 << Math.Max(0, bitDepth - 6)));
        for (int y = 0; y < frame->height; y++)
            new Span<ushort>(frame->data[0] + y * frame->linesize[0], frame->width).Fill(luma);
        for (int plane = 1; plane <= 2; plane++)
        {
            for (int y = 0; y < chromaHeight; y++)
            {
                new Span<ushort>(
                    frame->data[(uint)plane] + y * frame->linesize[(uint)plane],
                    chromaWidth).Fill(neutral);
            }
        }
        return;
    }

    byte luma8 = (byte)(64 + frameIndex * 16);
    for (int y = 0; y < frame->height; y++)
        new Span<byte>(frame->data[0] + y * frame->linesize[0], frame->width).Fill(luma8);
    for (int plane = 1; plane <= 2; plane++)
    {
        for (int y = 0; y < chromaHeight; y++)
        {
            new Span<byte>(
                frame->data[(uint)plane] + y * frame->linesize[(uint)plane],
                chromaWidth).Fill(128);
        }
    }
}

static unsafe int DrainPackets(AVCodecContext* context, AVPacket* packet, string encoderName)
{
    int packetCount = 0;
    while (true)
    {
        int receiveResult = ffmpeg.avcodec_receive_packet(context, packet);
        if (receiveResult == 0)
        {
            packetCount++;
            ffmpeg.av_packet_unref(packet);
            continue;
        }
        if (receiveResult == ffmpeg.AVERROR(ffmpeg.EAGAIN) ||
            receiveResult == ffmpeg.AVERROR_EOF)
        {
            return packetCount;
        }

        throw new InvalidOperationException(
            $"{encoderName} packet receive failed: {GetError(receiveResult)}");
    }
}

static unsafe void SetRequiredOption(
    AVCodecContext* context,
    string encoderName,
    string key,
    string value)
{
    int result = ffmpeg.av_opt_set(context->priv_data, key, value, 0);
    if (result < 0)
    {
        throw new InvalidOperationException(
            $"{encoderName} option {key}={value} failed: {GetError(result)}");
    }
}

static unsafe void SetOptionalOption(AVCodecContext* context, string key, string value)
{
    ffmpeg.av_opt_set(context->priv_data, key, value, 0);
}

static string GetPixelFormatName(AVPixelFormat pixelFormat)
{
    string? name = ffmpeg.av_get_pix_fmt_name(pixelFormat);
    return string.IsNullOrWhiteSpace(name) ? pixelFormat.ToString() : name;
}

static unsafe string GetError(int result)
{
    byte* buffer = stackalloc byte[512];
    ffmpeg.av_strerror(result, buffer, 512);
    return Marshal.PtrToStringAnsi((IntPtr)buffer) ?? result.ToString();
}
'@ | Set-Content -Encoding UTF8 -Path $program

    & dotnet run --project $project --configuration Debug
    if ($LASTEXITCODE -ne 0) {
        throw "AV1 encoder policy verifier failed with exit code $LASTEXITCODE."
    }
}
finally {
    if (Test-Path $work) {
        Remove-Item -Recurse -Force -Path $work
    }
    if (Test-Path $work) {
        throw "AV1 encoder policy harness directory remained: $work"
    }
}
