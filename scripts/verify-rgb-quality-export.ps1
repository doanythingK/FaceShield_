param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$work = Join-Path $repo ".tmp\rgb-quality-export"
$project = Join-Path $work "RgbQualityExportHarness.csproj"
$program = Join-Path $work "Program.cs"
$artifacts = Join-Path $work "artifacts"
$bundle = Join-Path $repo "FFmpeg\win-x64"
$successRunId = "rgb-quality-export-$([Guid]::NewGuid().ToString('N'))"
$failureRunId = "rgb-ten-bit-fail-$([Guid]::NewGuid().ToString('N'))"

try {
    if (Test-Path $work) {
        Remove-Item -Recurse -Force -Path $work
    }
    if (Test-Path $work) {
        throw "Unable to remove stale RGB quality export harness: $work"
    }
    if (-not (Test-Path (Join-Path $bundle "avcodec-62.dll"))) {
        throw "Bundled Windows FFmpeg was not found: $bundle"
    }

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
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using FaceShield;
using FaceShield.Services.Video;
using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

if (args.Length != 4)
    throw new ArgumentException("Expected bundle, work, success run ID, and failure run ID.");

string bundle = Path.GetFullPath(args[0]);
string work = Path.GetFullPath(args[1]);
string successRunId = args[2];
string failureRunId = args[3];
string sourcePath = Path.Combine(work, "rgb-source.mkv");
string outputPath = Path.Combine(work, "rgb-output.mkv");
string tenBitSourcePath = Path.Combine(work, "rgb-ten-bit-source.mkv");
string tenBitOutputPath = Path.Combine(work, "rgb-ten-bit-output.mkv");
ffmpeg.RootPath = bundle;

try
{
    unsafe
    {
        string version = ffmpeg.av_version_info();
        if (!version.StartsWith("8.0.1", StringComparison.Ordinal))
            throw new InvalidOperationException($"Unexpected bundled FFmpeg version: {version}");
        if (ffmpeg.avcodec_find_encoder_by_name("libx264rgb") == null)
            throw new InvalidOperationException("Bundled libx264rgb encoder was not found.");
        if (ffmpeg.avcodec_find_encoder_by_name("libx264") == null)
            throw new InvalidOperationException("Bundled libx264 encoder was not found.");

        EncodeFixture(sourcePath, tenBit: false);
        DecodedVideo source = DecodeFixture(sourcePath);
        ValidateEightBitFixture(source);
        ValidateGeneratedPixels(source);

        RunQualityServiceExport(sourcePath, outputPath, successRunId, source);
        DecodedVideo output = DecodeFixture(outputPath);
        ValidateEightBitOutput(output, source);
        ValidateMaskedPixelFidelity(source, output);

        EncodeFixture(tenBitSourcePath, tenBit: true);
        DecodedVideo tenBitSource = DecodeFixture(tenBitSourcePath);
        ValidateTenBitFixture(tenBitSource);
        RunTenBitFailClosedExport(
            tenBitSourcePath,
            tenBitOutputPath,
            failureRunId);

        Console.WriteLine(
            $"[RgbQualityExportVerify] PASS ffmpeg={version} fixture=libx264rgb " +
            $"codec=h264 container=matroska frames={source.Frames.Count} packets={source.Packets} " +
            $"pixelFormat={source.PixelFormat} canonicalRgb=true spatialPattern=true");
    }
}
finally
{
    DeleteOutputAndStagedFiles(outputPath);
    DeleteOutputAndStagedFiles(tenBitOutputPath);
    DeleteMetricsLog(successRunId);
    DeleteMetricsLog(failureRunId);
}

static unsafe void EncodeFixture(string outputPath, bool tenBit)
{
    const int width = 96;
    const int height = 64;
    const int frameCount = 6;
    string encoderName = tenBit ? "libx264" : "libx264rgb";
    AVPixelFormat pixelFormat = tenBit
        ? AVPixelFormat.AV_PIX_FMT_YUV444P10LE
        : AVPixelFormat.AV_PIX_FMT_BGR24;
    AVCodec* encoder = ffmpeg.avcodec_find_encoder_by_name(encoderName);
    if (encoder == null || encoder->id != AVCodecID.AV_CODEC_ID_H264)
        throw new InvalidOperationException($"Required H.264 encoder was not found: {encoderName}");
    if (!SupportsPixelFormat(encoder, pixelFormat))
        throw new InvalidOperationException($"{encoderName} does not support {PixelFormatName(pixelFormat)}.");

    AVFormatContext* output = null;
    AVCodecContext* context = null;
    AVFrame* frame = null;
    AVPacket* packet = null;
    try
    {
        Throw(ffmpeg.avformat_alloc_output_context2(&output, null, "matroska", outputPath));
        if (output == null || output->oformat == null)
            throw new InvalidOperationException("Unable to allocate Matroska output context.");

        context = ffmpeg.avcodec_alloc_context3(encoder);
        if (context == null)
            throw new InvalidOperationException($"Unable to allocate {encoderName} context.");
        context->width = width;
        context->height = height;
        context->pix_fmt = pixelFormat;
        context->time_base = new AVRational { num = 1, den = 30 };
        context->framerate = new AVRational { num = 30, den = 1 };
        context->gop_size = 30;
        context->max_b_frames = 0;
        context->thread_count = 2;
        context->bit_rate = 0;
        context->rc_max_rate = 0;
        context->rc_buffer_size = 0;
        context->color_range = AVColorRange.AVCOL_RANGE_JPEG;
        context->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
        context->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
        context->colorspace = AVColorSpace.AVCOL_SPC_RGB;
        context->chroma_sample_location = AVChromaLocation.AVCHROMA_LOC_UNSPECIFIED;
        if ((output->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
            context->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

        RequiredOption(context, "preset", "fast");
        RequiredOption(context, "crf", "0");
        Throw(ffmpeg.avcodec_open2(context, encoder, null));

        AVStream* stream = ffmpeg.avformat_new_stream(output, null);
        if (stream == null)
            throw new InvalidOperationException("Unable to create H.264 stream.");
        stream->time_base = context->time_base;
        stream->avg_frame_rate = context->framerate;
        stream->r_frame_rate = context->framerate;
        Throw(ffmpeg.avcodec_parameters_from_context(stream->codecpar, context));
        if (stream->codecpar->codec_id != AVCodecID.AV_CODEC_ID_H264)
            throw new InvalidOperationException("Fixture codec is not H.264.");

        if ((output->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
            Throw(ffmpeg.avio_open(&output->pb, outputPath, ffmpeg.AVIO_FLAG_WRITE));
        Throw(ffmpeg.avformat_write_header(output, null));

        frame = ffmpeg.av_frame_alloc();
        packet = ffmpeg.av_packet_alloc();
        if (frame == null || packet == null)
            throw new InvalidOperationException("Unable to allocate fixture frame or packet.");
        frame->format = (int)pixelFormat;
        frame->width = width;
        frame->height = height;
        frame->color_range = context->color_range;
        frame->color_primaries = context->color_primaries;
        frame->color_trc = context->color_trc;
        frame->colorspace = context->colorspace;
        frame->chroma_location = context->chroma_sample_location;
        Throw(ffmpeg.av_frame_get_buffer(frame, 32));

        int packets = 0;
        for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            Throw(ffmpeg.av_frame_make_writable(frame));
            if (tenBit)
                FillTenBitRgbMatrixFrame(frame, frameIndex);
            else
                FillEightBitBgrFrame(frame, frameIndex);
            frame->pts = frameIndex;
            frame->duration = 1;
            Throw(ffmpeg.avcodec_send_frame(context, frame));
            packets += DrainMuxPackets(context, packet, stream, output);
        }
        Throw(ffmpeg.avcodec_send_frame(context, null));
        packets += DrainMuxPackets(context, packet, stream, output);
        if (packets != frameCount)
            throw new InvalidOperationException($"Fixture packet count mismatch: {packets}/{frameCount}");

        Throw(ffmpeg.av_write_trailer(output));
        if ((output->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0 && output->pb != null)
            Throw(ffmpeg.avio_closep(&output->pb));
    }
    finally
    {
        ffmpeg.av_frame_free(&frame);
        ffmpeg.av_packet_free(&packet);
        ffmpeg.avcodec_free_context(&context);
        if (output != null)
        {
            if ((output->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0 && output->pb != null)
                _ = ffmpeg.avio_closep(&output->pb);
            ffmpeg.avformat_free_context(output);
        }
    }
}

static unsafe DecodedVideo DecodeFixture(string inputPath)
{
    AVFormatContext* input = null;
    AVCodecContext* context = null;
    AVFrame* frame = null;
    AVPacket* packet = null;
    try
    {
        Throw(ffmpeg.avformat_open_input(&input, inputPath, null, null));
        Throw(ffmpeg.avformat_find_stream_info(input, null));
        int streamIndex = ffmpeg.av_find_best_stream(
            input,
            AVMediaType.AVMEDIA_TYPE_VIDEO,
            -1,
            -1,
            null,
            0);
        Throw(streamIndex);
        AVStream* stream = input->streams[streamIndex];
        if (stream->codecpar->codec_id != AVCodecID.AV_CODEC_ID_H264)
            throw new InvalidOperationException("Decoded fixture codec is not H.264.");
        AVCodec* decoder = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
        if (decoder == null)
            throw new InvalidOperationException("Bundled H.264 decoder was not found.");
        context = ffmpeg.avcodec_alloc_context3(decoder);
        if (context == null)
            throw new InvalidOperationException("Unable to allocate H.264 decoder context.");
        Throw(ffmpeg.avcodec_parameters_to_context(context, stream->codecpar));
        Throw(ffmpeg.avcodec_open2(context, decoder, null));

        frame = ffmpeg.av_frame_alloc();
        packet = ffmpeg.av_packet_alloc();
        if (frame == null || packet == null)
            throw new InvalidOperationException("Unable to allocate decode frame or packet.");

        var frames = new List<byte[]>();
        string? pixelFormat = null;
        int bitDepth = 0;
        int fullRangeFrames = 0;
        int rgbSpaceFrames = 0;
        int bt709Frames = 0;
        int packets = 0;
        while (ffmpeg.av_read_frame(input, packet) >= 0)
        {
            if (packet->stream_index != streamIndex)
            {
                ffmpeg.av_packet_unref(packet);
                continue;
            }
            packets++;
            int send = ffmpeg.avcodec_send_packet(context, packet);
            ffmpeg.av_packet_unref(packet);
            Throw(send);
            ReceiveFrames(
                context,
                frame,
                frames,
                ref pixelFormat,
                ref bitDepth,
                ref fullRangeFrames,
                ref rgbSpaceFrames,
                ref bt709Frames);
        }
        Throw(ffmpeg.avcodec_send_packet(context, null));
        ReceiveFrames(
            context,
            frame,
            frames,
            ref pixelFormat,
            ref bitDepth,
            ref fullRangeFrames,
            ref rgbSpaceFrames,
            ref bt709Frames);

        return new DecodedVideo(
            context->width,
            context->height,
            packets,
            frames,
            pixelFormat ?? "unknown",
            bitDepth,
            fullRangeFrames,
            rgbSpaceFrames,
            bt709Frames,
            stream->codecpar->codec_id,
            stream->codecpar->profile,
            stream->codecpar->color_range,
            stream->codecpar->color_space,
            stream->codecpar->color_primaries,
            stream->codecpar->color_trc);
    }
    finally
    {
        ffmpeg.av_frame_free(&frame);
        ffmpeg.av_packet_free(&packet);
        ffmpeg.avcodec_free_context(&context);
        if (input != null)
            ffmpeg.avformat_close_input(&input);
    }
}

static unsafe void ReceiveFrames(
    AVCodecContext* context,
    AVFrame* frame,
    List<byte[]> frames,
    ref string? pixelFormat,
    ref int bitDepth,
    ref int fullRangeFrames,
    ref int rgbSpaceFrames,
    ref int bt709Frames)
{
    while (true)
    {
        int receive = ffmpeg.avcodec_receive_frame(context, frame);
        if (receive == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receive == ffmpeg.AVERROR_EOF)
            return;
        Throw(receive);

        AVPixelFormat format = (AVPixelFormat)frame->format;
        AVPixFmtDescriptor* descriptor = ffmpeg.av_pix_fmt_desc_get(format);
        if (descriptor == null || descriptor->nb_components < 3 ||
            (descriptor->flags & ffmpeg.AV_PIX_FMT_FLAG_RGB) == 0)
        {
            throw new InvalidOperationException(
                $"Decoded frame is not canonical RGB: {PixelFormatName(format)}");
        }
        string currentPixelFormat = PixelFormatName(format);
        int currentDepth = descriptor->comp[0].depth;
        if (pixelFormat != null && !string.Equals(pixelFormat, currentPixelFormat, StringComparison.Ordinal))
            throw new InvalidOperationException($"Decoded pixel format changed: {pixelFormat}/{currentPixelFormat}");
        if (bitDepth != 0 && bitDepth != currentDepth)
            throw new InvalidOperationException($"Decoded bit depth changed: {bitDepth}/{currentDepth}");
        pixelFormat = currentPixelFormat;
        bitDepth = currentDepth;

        if (frame->color_range == AVColorRange.AVCOL_RANGE_JPEG)
            fullRangeFrames++;
        if (frame->colorspace == AVColorSpace.AVCOL_SPC_RGB)
            rgbSpaceFrames++;
        if (frame->color_primaries == AVColorPrimaries.AVCOL_PRI_BT709 &&
            frame->color_trc == AVColorTransferCharacteristic.AVCOL_TRC_BT709)
        {
            bt709Frames++;
        }

        if (currentDepth == 8)
            frames.Add(ExtractCanonicalRgb8(frame, descriptor));
        else
            frames.Add(Array.Empty<byte>());
        ffmpeg.av_frame_unref(frame);
    }
}

static unsafe byte[] ExtractCanonicalRgb8(AVFrame* frame, AVPixFmtDescriptor* descriptor)
{
    if (frame->width <= 0 || frame->height <= 0)
        throw new InvalidOperationException("Decoded RGB frame dimensions are invalid.");
    var rgb = new byte[checked(frame->width * frame->height * 3)];
    for (int component = 0; component < 3; component++)
    {
        AVComponentDescriptor value = descriptor->comp[(uint)component];
        uint plane = (uint)value.plane;
        if (value.depth != 8 || value.shift != 0 || value.plane > 3 ||
            frame->data[plane] == null || frame->linesize[plane] == 0)
        {
            throw new InvalidOperationException(
                $"Unsupported RGB component layout: component={component}, " +
                $"plane={value.plane}, depth={value.depth}, shift={value.shift}");
        }
        int stride = frame->linesize[plane];
        for (int y = 0; y < frame->height; y++)
        {
            byte* row = stride >= 0
                ? frame->data[plane] + y * stride
                : frame->data[plane] + (frame->height - 1 - y) * -stride;
            for (int x = 0; x < frame->width; x++)
            {
                rgb[(y * frame->width + x) * 3 + component] =
                    row[x * value.step + value.offset];
            }
        }
    }
    return rgb;
}

static void ValidateEightBitFixture(DecodedVideo source)
{
    ValidateVideoContract(source, expectedBitDepth: 8, expectedFrames: 6);
    if (!string.Equals(source.PixelFormat, "gbrp", StringComparison.Ordinal))
        throw new InvalidOperationException($"Expected decoded gbrp fixture, got {source.PixelFormat}.");
}

static void ValidateEightBitOutput(DecodedVideo output, DecodedVideo source)
{
    ValidateVideoContract(output, expectedBitDepth: 8, expectedFrames: source.Frames.Count);
    if (output.Packets != source.Packets)
        throw new InvalidOperationException($"Output packet count changed: {source.Packets}/{output.Packets}");
}

static void ValidateVideoContract(DecodedVideo video, int expectedBitDepth, int expectedFrames)
{
    if (video.Width != 96 || video.Height != 64 ||
        video.Frames.Count != expectedFrames || video.Packets != expectedFrames ||
        video.Codec != AVCodecID.AV_CODEC_ID_H264 || video.Profile != 244 ||
        video.BitDepth != expectedBitDepth ||
        video.FullRangeFrames != expectedFrames ||
        video.RgbSpaceFrames != expectedFrames ||
        video.Bt709Frames != expectedFrames ||
        video.StreamRange != AVColorRange.AVCOL_RANGE_JPEG ||
        video.StreamSpace != AVColorSpace.AVCOL_SPC_RGB ||
        video.StreamPrimaries != AVColorPrimaries.AVCOL_PRI_BT709 ||
        video.StreamTransfer != AVColorTransferCharacteristic.AVCOL_TRC_BT709)
    {
        throw new InvalidOperationException(
            $"RGB video contract mismatch: size={video.Width}x{video.Height}, " +
            $"frames={video.Frames.Count}, packets={video.Packets}, codec={video.Codec}, " +
            $"profile={video.Profile}, format={video.PixelFormat}, depth={video.BitDepth}, " +
            $"rangeFrames={video.FullRangeFrames}, rgbFrames={video.RgbSpaceFrames}, " +
            $"bt709Frames={video.Bt709Frames}, streamRange={video.StreamRange}, " +
            $"streamSpace={video.StreamSpace}, primaries={video.StreamPrimaries}, " +
            $"transfer={video.StreamTransfer}");
    }
}

static void ValidateGeneratedPixels(DecodedVideo source)
{
    for (int frameIndex = 0; frameIndex < source.Frames.Count; frameIndex++)
    {
        byte[] expected = CreateExpectedCanonicalRgb(frameIndex, source.Width, source.Height);
        if (!source.Frames[frameIndex].AsSpan().SequenceEqual(expected))
            throw new InvalidOperationException($"Canonical RGB fixture changed frame {frameIndex} pixels.");
    }
}

static void ValidateMaskedPixelFidelity(DecodedVideo source, DecodedVideo output)
{
    if (source.Frames.Count != output.Frames.Count)
        throw new InvalidOperationException("Decoded frame count changed after RGB export.");

    long outsideChannelErrorSum = 0;
    long outsideChannelCount = 0;
    int maxOutsideChannelError = 0;
    int changedInsidePixels = 0;
    int insidePixels = 32 * 32;

    for (int frameIndex = 0; frameIndex < source.Frames.Count; frameIndex++)
    {
        ReadOnlySpan<byte> before = source.Frames[frameIndex];
        ReadOnlySpan<byte> after = output.Frames[frameIndex];
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                int offset = (y * source.Width + x) * 3;
                bool insideMask =
                    frameIndex == 2 &&
                    x >= 32 && x < 64 &&
                    y >= 16 && y < 48;

                int d0 = Math.Abs(before[offset] - after[offset]);
                int d1 = Math.Abs(before[offset + 1] - after[offset + 1]);
                int d2 = Math.Abs(before[offset + 2] - after[offset + 2]);

                if (insideMask)
                {
                    if (d0 != 0 || d1 != 0 || d2 != 0)
                        changedInsidePixels++;
                    continue;
                }

                outsideChannelErrorSum += d0 + d1 + d2;
                outsideChannelCount += 3;
                maxOutsideChannelError = Math.Max(
                    maxOutsideChannelError,
                    Math.Max(d0, Math.Max(d1, d2)));
            }
        }
    }

    double meanOutsideChannelError =
        outsideChannelCount > 0
            ? outsideChannelErrorSum / (double)outsideChannelCount
            : 0.0;

    if (meanOutsideChannelError > 6.0 ||
        maxOutsideChannelError > 40 ||
        changedInsidePixels < insidePixels / 2)
    {
        throw new InvalidOperationException(
            $"RGB quality budget mismatch: meanOutside={meanOutsideChannelError:0.###}, " +
            $"maxOutside={maxOutsideChannelError}, " +
            $"changedInside={changedInsidePixels}/{insidePixels}");
    }

    Console.WriteLine(
        "[RgbQualityExportVerify] PASS pixelQuality=true " +
        $"meanOutsideChannelError={meanOutsideChannelError:0.###} " +
        $"maxOutsideChannelError={maxOutsideChannelError} " +
        $"changedInside={changedInsidePixels}/{insidePixels} canonicalRgb=true");
}

static unsafe void RunQualityServiceExport(
    string inputPath,
    string outputPath,
    string runId,
    DecodedVideo source)
{
    DeleteOutputAndStagedFiles(outputPath);
    AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
    using var mask = new WriteableBitmap(
        new PixelSize(96, 64),
        new Vector(96, 96),
        PixelFormat.Bgra8888,
        AlphaFormat.Premul);
    using (var framebuffer = mask.Lock())
    {
        byte* address = (byte*)framebuffer.Address;
        for (int y = 0; y < 64; y++)
            new Span<byte>(address + y * framebuffer.RowBytes, 96 * 4).Clear();
        for (int y = 16; y < 48; y++)
        {
            byte* row = address + y * framebuffer.RowBytes;
            for (int x = 32; x < 64; x++)
                row[x * 4 + 3] = byte.MaxValue;
        }
    }

    var masks = new FrameMaskProvider();
    masks.SetMask(2, mask);
    FFmpegBootstrap.Initialize();
    var service = new VideoExportService(masks);
    service.Export(
        inputPath,
        outputPath,
        blurRadius: 6,
        runId: runId,
        allowHybridCopy: false);
    ExportRunSummary summary = service.LastExportSummary
        ?? throw new InvalidOperationException("RGB export summary was not created.");
    if (!summary.OutputCommitted || summary.AttemptCount != 1 ||
        summary.Frames != 6 || summary.SubmittedVideoFrames != 6 ||
        summary.ExpectedBlurFrames != 1 || summary.AppliedBlurFrames != 1 ||
        summary.BitmapMaskFrames != 1 || summary.DirectFaceFrames != 0 ||
        summary.VideoFrameCoverageMismatch != 0 || summary.VideoFrameDropCount != 0 ||
        summary.OutputPacketCountMismatch != 0 ||
        summary.InputVideoPackets != source.Packets ||
        summary.OutputVideoPackets != source.Packets ||
        !string.Equals(summary.EncoderName, "libx264rgb", StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(summary.EncoderQualityMode, "crf18-fast-rgb", StringComparison.Ordinal) ||
        !string.Equals(summary.SourcePixelFormat, "gbrp", StringComparison.Ordinal) ||
        !string.Equals(summary.OutputPixelFormat, "bgr24", StringComparison.Ordinal) ||
        summary.SourceBitDepth != 8 || summary.OutputBitDepth != 8 ||
        !summary.EncoderOptionsApplied.Contains("preset=fast", StringComparison.Ordinal) ||
        !summary.EncoderOptionsApplied.Contains("crf=18", StringComparison.Ordinal) ||
        !string.IsNullOrEmpty(summary.EncoderOptionFailures))
    {
        throw new InvalidOperationException($"Unexpected RGB export summary: {summary.ToLogLine()}");
    }
    if (!File.Exists(outputPath))
        throw new InvalidOperationException("Committed RGB output was not created.");
    string[] stagedOutputs = FindStagedOutputs(outputPath);
    if (stagedOutputs.Length != 0)
        throw new InvalidOperationException($"RGB export left staged outputs: {string.Join(',', stagedOutputs)}");

    Console.WriteLine(
        "[RgbQualityExportVerify] PASS serviceExport=true " +
        $"encoder={summary.EncoderName} quality={summary.EncoderQualityMode} " +
        $"source={summary.SourcePixelFormat}/{summary.SourceBitDepth} " +
        $"output={summary.OutputPixelFormat}/{summary.OutputBitDepth} " +
        $"frames={summary.Frames} submitted={summary.SubmittedVideoFrames} " +
        $"masked={summary.AppliedBlurFrames} packets={summary.InputVideoPackets}/{summary.OutputVideoPackets} " +
        $"attemptCount={summary.AttemptCount} outputCommitted=true packetMismatch=0");
}

static void ValidateTenBitFixture(DecodedVideo source)
{
    ValidateVideoContract(source, expectedBitDepth: 10, expectedFrames: 6);
    if (!string.Equals(source.PixelFormat, "gbrp10le", StringComparison.Ordinal))
        throw new InvalidOperationException(
            $"10-bit RGB matrix fixture did not decode as gbrp10le: {source.PixelFormat}");
    Console.WriteLine(
        "[RgbQualityExportVerify] PASS tenBitFixture=true encoder=libx264 " +
        $"encoderInput=yuv444p10le decoded={source.PixelFormat} bitDepth={source.BitDepth} " +
        "colorspace=rgb range=jpeg profile=244");
}

static void RunTenBitFailClosedExport(
    string inputPath,
    string outputPath,
    string runId)
{
    DeleteOutputAndStagedFiles(outputPath);
    var masks = new FrameMaskProvider();
    masks.SetFaceRects(
        2,
        [new Rect(32, 16, 32, 32)],
        new PixelSize(96, 64),
        1.0f,
        [1.0f]);
    var service = new VideoExportService(masks);
    InvalidOperationException? failure = null;
    try
    {
        service.Export(
            inputPath,
            outputPath,
            blurRadius: 6,
            runId: runId,
            allowHybridCopy: false);
    }
    catch (InvalidOperationException ex)
    {
        failure = ex;
    }

    const string expectedMessage = "8\uBE44\uD2B8\uAC00 \uC544\uB2CC RGB";
    if (failure == null ||
        !failure.Message.Contains(expectedMessage, StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            "10-bit RGB export did not fail closed with the expected integrity message: " +
            (failure?.Message ?? "no exception"));
    }
    if (service.LastExportSummary?.OutputCommitted == true)
        throw new InvalidOperationException("Failed 10-bit RGB export was marked committed.");
    if (File.Exists(outputPath))
        throw new InvalidOperationException("Failed 10-bit RGB export left a final output.");
    string[] stagedOutputs = FindStagedOutputs(outputPath);
    if (stagedOutputs.Length != 0)
        throw new InvalidOperationException($"Failed 10-bit RGB export left staged outputs: {string.Join(',', stagedOutputs)}");

    Console.WriteLine(
        "[RgbQualityExportVerify] PASS tenBitFailClosed=true " +
        "message='non-8-bit RGB' finalOutput=false stagedOutputs=0 outputCommitted=false");
}

static byte[] CreateExpectedCanonicalRgb(int frameIndex, int width, int height)
{
    var rgb = new byte[checked(width * height * 3)];
    for (int y = 0; y < height; y++)
    {
        for (int x = 0; x < width; x++)
        {
            int offset = (y * width + x) * 3;
            rgb[offset] = R(x, y, frameIndex);
            rgb[offset + 1] = G(x, y, frameIndex);
            rgb[offset + 2] = B(x, y, frameIndex);
        }
    }
    return rgb;
}

static unsafe void FillEightBitBgrFrame(AVFrame* frame, int frameIndex)
{
    for (int y = 0; y < frame->height; y++)
    {
        Span<byte> row = new(frame->data[0] + y * frame->linesize[0], frame->width * 3);
        for (int x = 0; x < frame->width; x++)
        {
            row[x * 3] = B(x, y, frameIndex);
            row[x * 3 + 1] = G(x, y, frameIndex);
            row[x * 3 + 2] = R(x, y, frameIndex);
        }
    }
}

static byte R(int x, int y, int frame) =>
    (byte)((x * 17 + y * 29 + frame * 47 + x * y * 3 + (x ^ y) * 11) & 0xff);

static byte G(int x, int y, int frame) =>
    (byte)((x * 31 + y * 7 + frame * 61 + x * x * 5 + y * y * 3) & 0xff);

static byte B(int x, int y, int frame) =>
    (byte)((x * 13 + y * 37 + frame * 73 + x * y * 7 + (x + y) * 19) & 0xff);

static unsafe void FillTenBitRgbMatrixFrame(AVFrame* frame, int frameIndex)
{
    for (int plane = 0; plane < 3; plane++)
    {
        for (int y = 0; y < frame->height; y++)
        {
            Span<ushort> row = new(
                frame->data[(uint)plane] + y * frame->linesize[(uint)plane],
                frame->width);
            for (int x = 0; x < frame->width; x++)
            {
                row[x] = (ushort)((64 + x * (17 + plane * 4) + y * (23 + plane * 6) +
                    frameIndex * (41 + plane * 12) + x * y * (plane + 1)) & 0x3ff);
            }
        }
    }
}

static unsafe bool SupportsPixelFormat(AVCodec* codec, AVPixelFormat format)
{
#pragma warning disable CS0618
    if (codec == null || codec->pix_fmts == null)
        return true;
    for (AVPixelFormat* current = codec->pix_fmts;
         *current != AVPixelFormat.AV_PIX_FMT_NONE;
         current++)
    {
        if (*current == format)
            return true;
    }
    return false;
#pragma warning restore CS0618
}

static unsafe int DrainMuxPackets(
    AVCodecContext* context,
    AVPacket* packet,
    AVStream* stream,
    AVFormatContext* output)
{
    int packets = 0;
    while (true)
    {
        int receive = ffmpeg.avcodec_receive_packet(context, packet);
        if (receive == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receive == ffmpeg.AVERROR_EOF)
            return packets;
        Throw(receive);
        ffmpeg.av_packet_rescale_ts(packet, context->time_base, stream->time_base);
        packet->stream_index = stream->index;
        Throw(ffmpeg.av_interleaved_write_frame(output, packet));
        ffmpeg.av_packet_unref(packet);
        packets++;
    }
}

static unsafe void RequiredOption(AVCodecContext* context, string key, string value)
{
    int result = ffmpeg.av_opt_set(context->priv_data, key, value, 0);
    if (result < 0)
        throw new InvalidOperationException($"Option {key}={value} failed: {Error(result)}");
}

static void DeleteOutputAndStagedFiles(string outputPath)
{
    if (File.Exists(outputPath))
        File.Delete(outputPath);
    foreach (string stagedOutput in FindStagedOutputs(outputPath))
        File.Delete(stagedOutput);
}

static string[] FindStagedOutputs(string outputPath)
{
    string fullPath = Path.GetFullPath(outputPath);
    string directory = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
    string name = Path.GetFileNameWithoutExtension(fullPath);
    string extension = Path.GetExtension(fullPath);
    return Directory.Exists(directory)
        ? Directory.GetFiles(
            directory,
            $".{name}.faceshield-*{extension}",
            SearchOption.TopDirectoryOnly)
        : Array.Empty<string>();
}

static void DeleteMetricsLog(string runId)
{
    string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    if (string.IsNullOrWhiteSpace(localAppData))
        return;
    string path = Path.Combine(
        localAppData,
        "FaceShield",
        "Logs",
        $"run-metrics-{runId}.log");
    if (File.Exists(path))
        File.Delete(path);
}

static unsafe string PixelFormatName(AVPixelFormat format)
{
    string? name = ffmpeg.av_get_pix_fmt_name(format);
    return string.IsNullOrWhiteSpace(name) ? format.ToString() : name;
}

static void Throw(int result)
{
    if (result < 0)
        throw new InvalidOperationException(Error(result));
}

static unsafe string Error(int result)
{
    byte* buffer = stackalloc byte[512];
    ffmpeg.av_strerror(result, buffer, 512);
    return Marshal.PtrToStringAnsi((IntPtr)buffer) ?? result.ToString();
}

sealed record DecodedVideo(
    int Width,
    int Height,
    int Packets,
    IReadOnlyList<byte[]> Frames,
    string PixelFormat,
    int BitDepth,
    int FullRangeFrames,
    int RgbSpaceFrames,
    int Bt709Frames,
    AVCodecID Codec,
    int Profile,
    AVColorRange StreamRange,
    AVColorSpace StreamSpace,
    AVColorPrimaries StreamPrimaries,
    AVColorTransferCharacteristic StreamTransfer);
'@ | Set-Content -Encoding UTF8 -Path $program

    $runArguments = @(
        "run"
        "--project", $project
        "--configuration", "Debug"
        "--property:UseArtifactsOutput=true"
        "--property:ArtifactsPath=$artifacts"
        "--"
        $bundle
        $work
        $successRunId
        $failureRunId
    )
    & dotnet @runArguments
    if ($LASTEXITCODE -ne 0) {
        throw "RGB quality export E2E harness failed with exit code $LASTEXITCODE."
    }
}
finally {
    $cleanupFailure = $null
    try {
        foreach ($runId in @($successRunId, $failureRunId)) {
            if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
                $metricsPath = Join-Path `
                    (Join-Path $env:LOCALAPPDATA "FaceShield\Logs") `
                    "run-metrics-$runId.log"
                if (Test-Path $metricsPath) {
                    Remove-Item -Force -Path $metricsPath
                }
            }
        }
        if (Test-Path $work) {
            Remove-Item -Recurse -Force -Path $work
        }
    }
    catch {
        $cleanupFailure = $_.Exception.Message
    }

    if (Test-Path $work) {
        $cleanupFailure = "Harness directory remained after cleanup: $work"
    }
    if ($cleanupFailure) {
        throw "RGB quality export harness cleanup failed: $cleanupFailure"
    }
}
