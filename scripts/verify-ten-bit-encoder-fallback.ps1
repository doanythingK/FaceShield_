param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$work = Join-Path $repo ".tmp\ten-bit-encoder-fallback"
$project = Join-Path $work "TenBitEncoderFallbackHarness.csproj"
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

MethodInfo getCandidates = typeof(VideoExportService).GetMethod(
    "GetEncoderCandidateNames",
    BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("GetEncoderCandidateNames was not found.");
MethodInfo shouldRetry = typeof(VideoExportService).GetMethod(
    "ShouldRetryWithSafeEncoding",
    BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("ShouldRetryWithSafeEncoding was not found.");

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

VerifyRetryPolicy();
int encodedPackets = VerifyLibx265TenBitEncode();

Console.WriteLine(
    $"[TenBitEncoderFallbackVerify] PASS software={string.Join(",", softwareTenBit)} " +
    $"normal={string.Join(",", normalTenBit)} libx265TenBitOpen=true " +
    $"encodedPackets={encodedPackets} retryPolicy=true");

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

void VerifyRetryPolicy()
{
    Type encoderExceptionType = typeof(VideoExportService).GetNestedType(
        "VideoEncoderException",
        BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("VideoEncoderException was not found.");
    ConstructorInfo encoderExceptionConstructor = encoderExceptionType.GetConstructors(
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single();
    Type integrityExceptionType = typeof(VideoExportService).GetNestedType(
        "VideoExportIntegrityException",
        BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("VideoExportIntegrityException was not found.");

    var hardwareIoError = (InvalidOperationException)encoderExceptionConstructor.Invoke(
        ["hardware I/O error", -5, "packet receive", "h264_nvenc", true]);
    var softwareIoError = (InvalidOperationException)encoderExceptionConstructor.Invoke(
        ["software I/O error", -5, "packet receive", "libx264", false]);
    var integrityError = (InvalidOperationException)Activator.CreateInstance(
        integrityExceptionType,
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        binder: null,
        args: ["Invalid argument: integrity"],
        culture: null)!;

    AssertRetry("hardware encoder error", hardwareIoError, expected: true);
    AssertRetry("software encoder error", softwareIoError, expected: false);
    AssertRetry(
        "legacy invalid argument",
        new InvalidOperationException("Invalid argument"),
        expected: true);
    AssertRetry("generic I/O error", new InvalidOperationException("I/O error"), expected: false);
    AssertRetry("integrity error", integrityError, expected: false);
}

void AssertRetry(string name, InvalidOperationException exception, bool expected)
{
    bool actual = (bool)(shouldRetry.Invoke(null, [exception])
        ?? throw new InvalidOperationException("Retry policy did not return a value."));
    if (actual != expected)
        throw new InvalidOperationException($"{name} retry mismatch: expected={expected}, actual={actual}.");
}

static unsafe int VerifyLibx265TenBitEncode()
{
    FFmpegBootstrap.Initialize();
    AVCodec* encoder = ffmpeg.avcodec_find_encoder_by_name("libx265");
    if (encoder == null || encoder->id != AVCodecID.AV_CODEC_ID_HEVC)
        throw new InvalidOperationException("Bundled FFmpeg does not provide libx265 HEVC encoding.");

    AVCodecContext* context = ffmpeg.avcodec_alloc_context3(encoder);
    if (context == null)
        throw new InvalidOperationException("Unable to allocate the libx265 encoder context.");

    AVFrame* frame = null;
    AVPacket* packet = null;
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

        frame = ffmpeg.av_frame_alloc();
        packet = ffmpeg.av_packet_alloc();
        if (frame == null || packet == null)
            throw new InvalidOperationException("Unable to allocate a 10-bit encode frame or packet.");

        frame->format = (int)context->pix_fmt;
        frame->width = context->width;
        frame->height = context->height;
        int bufferResult = ffmpeg.av_frame_get_buffer(frame, 32);
        if (bufferResult < 0)
            throw new InvalidOperationException($"Unable to allocate a 10-bit frame: {GetError(bufferResult)}");

        int packetCount = 0;
        for (int i = 0; i < 3; i++)
        {
            int writableResult = ffmpeg.av_frame_make_writable(frame);
            if (writableResult < 0)
                throw new InvalidOperationException($"10-bit frame is not writable: {GetError(writableResult)}");
            FillFrame(frame, i);
            frame->pts = i;

            int sendResult = ffmpeg.avcodec_send_frame(context, frame);
            if (sendResult < 0)
                throw new InvalidOperationException($"libx265 frame send failed: {GetError(sendResult)}");
            packetCount += DrainPackets(context, packet);
        }

        int flushResult = ffmpeg.avcodec_send_frame(context, null);
        if (flushResult < 0 && flushResult != ffmpeg.AVERROR_EOF)
            throw new InvalidOperationException($"libx265 flush failed: {GetError(flushResult)}");
        packetCount += DrainPackets(context, packet);
        if (packetCount < 1)
            throw new InvalidOperationException("libx265 did not emit a packet for three 10-bit frames.");
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

static unsafe void FillFrame(AVFrame* frame, int frameIndex)
{
    ushort luma = (ushort)(64 + frameIndex * 32);
    for (int y = 0; y < frame->height; y++)
    {
        new Span<ushort>(frame->data[0] + y * frame->linesize[0], frame->width).Fill(luma);
    }

    for (int plane = 1; plane <= 2; plane++)
    {
        for (int y = 0; y < frame->height / 2; y++)
        {
            new Span<ushort>(
                frame->data[(uint)plane] + y * frame->linesize[(uint)plane],
                frame->width / 2)
                .Fill((ushort)512);
        }
    }
}

static unsafe int DrainPackets(AVCodecContext* context, AVPacket* packet)
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

        throw new InvalidOperationException($"libx265 packet receive failed: {GetError(receiveResult)}");
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

& dotnet run --project $project --configuration Debug
if ($LASTEXITCODE -ne 0) {
    throw "Ten-bit encoder fallback verifier failed with exit code $LASTEXITCODE."
}
}
finally {
    if (Test-Path $work) {
        Remove-Item -Recurse -Force -Path $work
    }
    if (Test-Path $work) {
        throw "Ten-bit encoder fallback harness directory remained: $work"
    }
}
