param()

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$work = Join-Path $repo ".tmp\av1-hdr-film-grain-policy"
$project = Join-Path $work "Av1HdrFilmGrainPolicyHarness.csproj"
$program = Join-Path $work "Program.cs"
$artifacts = Join-Path $work "artifacts"
$bundle = Join-Path $repo "FFmpeg\win-x64"

try {
    if (Test-Path $work) {
        Remove-Item -Recurse -Force -Path $work
    }
    if (Test-Path $work) {
        throw "Unable to remove stale AV1 HDR/film-grain harness directory: $work"
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
using FFmpeg.AutoGen;
using Avalonia;
using FaceShield.Services.Video;
using System;
using System.IO;
using System.Runtime.InteropServices;

if (args.Length != 2)
    throw new ArgumentException("Expected bundled FFmpeg and work directories.");

string bundle = Path.GetFullPath(args[0]);
string work = Path.GetFullPath(args[1]);
string nativeOutputPath = Path.Combine(work, "native-svt-av1-hdr-film-grain.mp4");
string hdrSourcePath = Path.Combine(work, "service-hdr-source.mp4");
string hdrOutputPath = Path.Combine(work, "service-hdr-output.mp4");
string grainSourcePath = Path.Combine(work, "service-grain-source.mp4");
string grainRemuxOutputPath = Path.Combine(work, "service-grain-remux-output.mp4");
string grainOutputPath = Path.Combine(work, "service-grain-output.mp4");
ffmpeg.RootPath = bundle;

unsafe
{
    string version = ffmpeg.av_version_info();
    if (!version.StartsWith("8.0.1", StringComparison.Ordinal))
        throw new InvalidOperationException($"Unexpected bundled FFmpeg version: {version}");

    AVCodec* encoder = ffmpeg.avcodec_find_encoder_by_name("libsvtav1");
    if (encoder == null || encoder->id != AVCodecID.AV_CODEC_ID_AV1)
        throw new InvalidOperationException("Bundled libsvtav1 encoder was not found.");

    EncodeFixture(nativeOutputPath, encoder, contextHdr: true, generatedGrain: true);
    DecodeResult result = DecodeFixture(nativeOutputPath);

    if (result.Frames != 4 ||
        result.TenBitFrames != result.Frames ||
        result.MasteringFrames != result.Frames ||
        result.ContentLightFrames != result.Frames ||
        result.FilmGrainFrames != result.Frames)
    {
        throw new InvalidOperationException(
            $"Round-trip coverage mismatch: frames={result.Frames}, " +
            $"tenBit={result.TenBitFrames}, mastering={result.MasteringFrames}, " +
            $"contentLight={result.ContentLightFrames}, filmGrain={result.FilmGrainFrames}");
    }
    if (!result.StreamMastering || !result.StreamContentLight)
        throw new InvalidOperationException("MP4 stream-level HDR metadata was not preserved.");
    if (!result.MasteringValuesMatch || !result.ContentLightValuesMatch)
        throw new InvalidOperationException("Decoded HDR metadata values were not preserved.");
    if (result.MinimumFilmGrainPayloadBytes <= 0)
        throw new InvalidOperationException("Decoded film-grain payload was empty.");

    Console.WriteLine(
        $"[Av1HdrFilmGrainPolicyVerify] PASS ffmpeg={version} encoder=libsvtav1 " +
        $"decoder={result.DecoderName} codec=av1 frames={result.Frames} " +
        $"pixelFormat={result.PixelFormat} tenBitFrames={result.TenBitFrames} " +
        $"masteringFrames={result.MasteringFrames} contentLightFrames={result.ContentLightFrames} " +
        $"filmGrainFrames={result.FilmGrainFrames} " +
        $"filmGrainPayloadBytes>={result.MinimumFilmGrainPayloadBytes} " +
        "streamHdr=true bitstreamHdr=true exportFilmGrain=true");

    EncodeFixture(hdrSourcePath, encoder, contextHdr: true, generatedGrain: false);
    DecodeResult hdrSource = DecodeFixture(hdrSourcePath);
    if (hdrSource.Frames != 4 ||
        hdrSource.TenBitFrames != 4 ||
        hdrSource.MasteringFrames != 4 ||
        hdrSource.ContentLightFrames != 4 ||
        hdrSource.FilmGrainFrames != 0)
    {
        throw new InvalidOperationException(
            $"HDR-only fixture mismatch: frames={hdrSource.Frames}, " +
            $"tenBit={hdrSource.TenBitFrames}, mastering={hdrSource.MasteringFrames}, " +
            $"contentLight={hdrSource.ContentLightFrames}, filmGrain={hdrSource.FilmGrainFrames}");
    }

    RunHdrServiceExport(hdrSourcePath, hdrOutputPath);
    DecodeResult hdrExport = DecodeFixture(hdrOutputPath);
    if (hdrExport.Frames != 4 ||
        hdrExport.TenBitFrames != 4 ||
        hdrExport.MasteringFrames != 4 ||
        hdrExport.ContentLightFrames != 4 ||
        hdrExport.FilmGrainFrames != 0 ||
        !hdrExport.MasteringValuesMatch ||
        !hdrExport.ContentLightValuesMatch)
    {
        throw new InvalidOperationException(
            $"VideoExportService HDR output mismatch: frames={hdrExport.Frames}, " +
            $"tenBit={hdrExport.TenBitFrames}, mastering={hdrExport.MasteringFrames}, " +
            $"contentLight={hdrExport.ContentLightFrames}, " +
            $"filmGrain={hdrExport.FilmGrainFrames}");
    }
    Console.WriteLine(
        "[Av1HdrFilmGrainPolicyVerify] PASS serviceHdrDecoded=true codec=av1 " +
        $"frames={hdrExport.Frames} masteringFrames={hdrExport.MasteringFrames} " +
        $"contentLightFrames={hdrExport.ContentLightFrames} filmGrainFrames=0 " +
        "bitstreamHdr=true");

    EncodeFixture(grainSourcePath, encoder, contextHdr: false, generatedGrain: true);
    DecodeResult grainSource = DecodeFixture(grainSourcePath);
    if (grainSource.Frames != 4 ||
        grainSource.Packets <= 0 ||
        grainSource.FilmGrainFrames != grainSource.Frames)
    {
        throw new InvalidOperationException(
            $"Film-grain fixture mismatch: packets={grainSource.Packets}, " +
            $"frames={grainSource.Frames}, filmGrain={grainSource.FilmGrainFrames}");
    }
    RunFilmGrainLosslessRemux(
        grainSourcePath,
        grainRemuxOutputPath,
        grainSource);
    RunFilmGrainFailClosedExport(grainSourcePath, grainOutputPath);
}

static unsafe void EncodeFixture(
    string outputPath,
    AVCodec* encoder,
    bool contextHdr,
    bool generatedGrain)
{
    AVFormatContext* output = null;
    AVCodecContext* context = null;
    AVFrame* frame = null;
    AVPacket* packet = null;
    try
    {
        Throw(ffmpeg.avformat_alloc_output_context2(&output, null, "mp4", outputPath));
        if (output == null || output->oformat == null)
            throw new InvalidOperationException("Unable to allocate MP4 output context.");
        if (ffmpeg.avformat_query_codec(output->oformat, AVCodecID.AV_CODEC_ID_AV1, 0) <= 0)
            throw new InvalidOperationException("Bundled MP4 muxer does not support AV1.");

        context = ffmpeg.avcodec_alloc_context3(encoder);
        if (context == null)
            throw new InvalidOperationException("Unable to allocate libsvtav1 context.");

        context->width = 64;
        context->height = 64;
        context->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P10LE;
        context->time_base = new AVRational { num = 1, den = 30 };
        context->framerate = new AVRational { num = 30, den = 1 };
        context->thread_count = 2;
        context->bit_rate = 0;
        context->rc_max_rate = 0;
        context->rc_buffer_size = 0;
        context->gop_size = 30;
        context->max_b_frames = 0;
        context->color_range = AVColorRange.AVCOL_RANGE_MPEG;
        context->color_primaries = AVColorPrimaries.AVCOL_PRI_BT2020;
        context->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084;
        context->colorspace = AVColorSpace.AVCOL_SPC_BT2020_NCL;
        context->chroma_sample_location = AVChromaLocation.AVCHROMA_LOC_LEFT;
        if ((output->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
            context->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

        if (contextHdr)
            AddStaticHdrContextSideData(context);
        RequiredOption(context, "preset", "6");
        RequiredOption(context, "crf", "12");
        string svtParameters = generatedGrain
            ? "tune=0:film-grain=8:film-grain-denoise=0"
            : "tune=0";
        RequiredOption(context, "svtav1-params", svtParameters);
        Throw(ffmpeg.avcodec_open2(context, encoder, null));

        bool hasMastering = HasPacketSideData(
            context->coded_side_data,
            context->nb_coded_side_data,
            AVPacketSideDataType.AV_PKT_DATA_MASTERING_DISPLAY_METADATA);
        bool hasContentLight = HasPacketSideData(
            context->coded_side_data,
            context->nb_coded_side_data,
            AVPacketSideDataType.AV_PKT_DATA_CONTENT_LIGHT_LEVEL);
        if (hasMastering != contextHdr || hasContentLight != contextHdr)
        {
            throw new InvalidOperationException(
                $"Encoder HDR side-data mismatch: expected={contextHdr}, " +
                $"mastering={hasMastering}, contentLight={hasContentLight}");
        }

        AVStream* stream = ffmpeg.avformat_new_stream(output, null);
        if (stream == null)
            throw new InvalidOperationException("Unable to create AV1 output stream.");
        stream->time_base = context->time_base;
        stream->avg_frame_rate = context->framerate;
        Throw(ffmpeg.avcodec_parameters_from_context(stream->codecpar, context));
        if (stream->codecpar->codec_id != AVCodecID.AV_CODEC_ID_AV1 ||
            stream->codecpar->format != (int)AVPixelFormat.AV_PIX_FMT_YUV420P10LE)
        {
            throw new InvalidOperationException(
                $"Unexpected encoded stream: codec={stream->codecpar->codec_id}, " +
                $"format={(AVPixelFormat)stream->codecpar->format}");
        }

        if ((output->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
            Throw(ffmpeg.avio_open(&output->pb, outputPath, ffmpeg.AVIO_FLAG_WRITE));
        Throw(ffmpeg.avformat_write_header(output, null));

        frame = ffmpeg.av_frame_alloc();
        packet = ffmpeg.av_packet_alloc();
        if (frame == null || packet == null)
            throw new InvalidOperationException("Unable to allocate AV1 frame or packet.");
        frame->format = (int)context->pix_fmt;
        frame->width = context->width;
        frame->height = context->height;
        frame->color_range = context->color_range;
        frame->color_primaries = context->color_primaries;
        frame->color_trc = context->color_trc;
        frame->colorspace = context->colorspace;
        frame->chroma_location = context->chroma_sample_location;
        Throw(ffmpeg.av_frame_get_buffer(frame, 32));

        int packetCount = 0;
        for (int frameIndex = 0; frameIndex < 4; frameIndex++)
        {
            Throw(ffmpeg.av_frame_make_writable(frame));
            FillTenBitFrame(frame, frameIndex);
            frame->pts = frameIndex;
            frame->duration = 1;
            Throw(ffmpeg.avcodec_send_frame(context, frame));
            packetCount += DrainMuxPackets(context, packet, stream, output);
        }

        int flush = ffmpeg.avcodec_send_frame(context, null);
        if (flush < 0 && flush != ffmpeg.AVERROR_EOF)
            Throw(flush);
        packetCount += DrainMuxPackets(context, packet, stream, output);
        if (packetCount < 1)
            throw new InvalidOperationException("libsvtav1 emitted no packets.");

        Throw(ffmpeg.av_write_trailer(output));
        if ((output->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0 && output->pb != null)
            Throw(ffmpeg.avio_closep(&output->pb));
    }
    finally
    {
        if (packet != null)
            ffmpeg.av_packet_free(&packet);
        if (frame != null)
            ffmpeg.av_frame_free(&frame);
        ffmpeg.avcodec_free_context(&context);
        if (output != null)
        {
            if ((output->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0 && output->pb != null)
                _ = ffmpeg.avio_closep(&output->pb);
            ffmpeg.avformat_free_context(output);
        }
    }
}

static unsafe DecodeResult DecodeFixture(string inputPath)
{
    AVFormatContext* input = null;
    AVCodecContext* context = null;
    AVPacket* packet = ffmpeg.av_packet_alloc();
    AVFrame* frame = ffmpeg.av_frame_alloc();
    if (packet == null || frame == null)
        throw new InvalidOperationException("Unable to allocate decoder frame or packet.");

    int frames = 0;
    int packets = 0;
    int tenBitFrames = 0;
    int masteringFrames = 0;
    int contentLightFrames = 0;
    int filmGrainFrames = 0;
    int minimumFilmGrainPayloadBytes = int.MaxValue;
    bool masteringValuesMatch = true;
    bool contentLightValuesMatch = true;
    bool streamMastering = false;
    bool streamContentLight = false;
    string decoderName = "unknown";
    string pixelFormat = "unknown";

    try
    {
        Throw(ffmpeg.avformat_open_input(&input, inputPath, null, null));
        Throw(ffmpeg.avformat_find_stream_info(input, null));

        int streamIndex = -1;
        for (int i = 0; i < input->nb_streams; i++)
        {
            if (input->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
            {
                streamIndex = i;
                break;
            }
        }
        if (streamIndex < 0)
            throw new InvalidOperationException("Encoded fixture has no video stream.");

        AVCodecParameters* parameters = input->streams[streamIndex]->codecpar;
        if (parameters->codec_id != AVCodecID.AV_CODEC_ID_AV1)
            throw new InvalidOperationException($"Encoded fixture codec changed: {parameters->codec_id}");
        streamMastering = HasPacketSideData(
            parameters->coded_side_data,
            parameters->nb_coded_side_data,
            AVPacketSideDataType.AV_PKT_DATA_MASTERING_DISPLAY_METADATA);
        streamContentLight = HasPacketSideData(
            parameters->coded_side_data,
            parameters->nb_coded_side_data,
            AVPacketSideDataType.AV_PKT_DATA_CONTENT_LIGHT_LEVEL);

        AVCodec* decoder = ffmpeg.avcodec_find_decoder(parameters->codec_id);
        if (decoder == null)
            throw new InvalidOperationException("Bundled AV1 decoder was not found.");
        decoderName = Marshal.PtrToStringAnsi((IntPtr)decoder->name) ?? "unknown";
        context = ffmpeg.avcodec_alloc_context3(decoder);
        if (context == null)
            throw new InvalidOperationException("Unable to allocate AV1 decoder context.");
        Throw(ffmpeg.avcodec_parameters_to_context(context, parameters));

        // Remove container-global copies so decoded HDR must come from the AV1 bitstream.
        ffmpeg.av_packet_side_data_remove(
            context->coded_side_data,
            &context->nb_coded_side_data,
            AVPacketSideDataType.AV_PKT_DATA_MASTERING_DISPLAY_METADATA);
        ffmpeg.av_packet_side_data_remove(
            context->coded_side_data,
            &context->nb_coded_side_data,
            AVPacketSideDataType.AV_PKT_DATA_CONTENT_LIGHT_LEVEL);
        context->export_side_data |= ffmpeg.AV_CODEC_EXPORT_DATA_FILM_GRAIN;
        Throw(ffmpeg.avcodec_open2(context, decoder, null));

        while (ffmpeg.av_read_frame(input, packet) >= 0)
        {
            if (packet->stream_index == streamIndex)
            {
                packets++;
                Throw(ffmpeg.avcodec_send_packet(context, packet));
                InspectDecodedFrames(
                    context,
                    frame,
                    ref frames,
                    ref tenBitFrames,
                    ref masteringFrames,
                    ref contentLightFrames,
                    ref filmGrainFrames,
                    ref minimumFilmGrainPayloadBytes,
                    ref masteringValuesMatch,
                    ref contentLightValuesMatch,
                    ref pixelFormat);
            }
            ffmpeg.av_packet_unref(packet);
        }

        int flush = ffmpeg.avcodec_send_packet(context, null);
        if (flush < 0 && flush != ffmpeg.AVERROR_EOF)
            Throw(flush);
        InspectDecodedFrames(
            context,
            frame,
            ref frames,
            ref tenBitFrames,
            ref masteringFrames,
            ref contentLightFrames,
            ref filmGrainFrames,
            ref minimumFilmGrainPayloadBytes,
            ref masteringValuesMatch,
            ref contentLightValuesMatch,
            ref pixelFormat);
    }
    finally
    {
        ffmpeg.av_frame_free(&frame);
        ffmpeg.av_packet_free(&packet);
        ffmpeg.avcodec_free_context(&context);
        if (input != null)
            ffmpeg.avformat_close_input(&input);
    }

    return new DecodeResult(
        frames,
        packets,
        tenBitFrames,
        masteringFrames,
        contentLightFrames,
        filmGrainFrames,
        minimumFilmGrainPayloadBytes == int.MaxValue ? 0 : minimumFilmGrainPayloadBytes,
        masteringValuesMatch,
        contentLightValuesMatch,
        streamMastering,
        streamContentLight,
        decoderName,
        pixelFormat);
}

static void RunFilmGrainLosslessRemux(
    string inputPath,
    string outputPath,
    DecodeResult source)
{
    string runId = $"av1-grain-remux-policy-{Guid.NewGuid():N}";
    DeleteOutputAndStagedFiles(outputPath);
    try
    {
        FFmpegBootstrap.Initialize();
        var service = new VideoExportService(new FrameMaskProvider());
        service.Export(
            inputPath,
            outputPath,
            blurRadius: 4,
            runId: runId,
            allowHybridCopy: false);

        ExportRunSummary summary = service.LastExportSummary
            ?? throw new InvalidOperationException(
                "VideoExportService did not create a film-grain remux summary.");
        if (!summary.OutputCommitted ||
            !string.Equals(summary.EncoderName, "stream-copy", StringComparison.Ordinal) ||
            !string.Equals(summary.EncoderQualityMode, "lossless-remux", StringComparison.Ordinal) ||
            summary.Frames != source.Frames ||
            summary.InputVideoPackets != source.Packets ||
            summary.OutputVideoPackets != source.Packets ||
            summary.CopiedVideoPackets != source.Packets ||
            summary.CopiedSourceVideoPackets != source.Packets ||
            summary.OutputPacketCountMismatch != 0 ||
            summary.ExpectedBlurFrames != 0 ||
            summary.AppliedBlurFrames != 0)
        {
            throw new InvalidOperationException(
                $"Unexpected film-grain remux summary: {summary.ToLogLine()}");
        }
        if (!File.Exists(outputPath))
            throw new InvalidOperationException("Committed film-grain remux output was not created.");

        string[] stagedOutputs = FindStagedOutputs(outputPath);
        if (stagedOutputs.Length != 0)
        {
            throw new InvalidOperationException(
                $"Film-grain remux left staged outputs: {string.Join(',', stagedOutputs)}");
        }

        DecodeResult remux = DecodeFixture(outputPath);
        if (remux.Packets != source.Packets ||
            remux.Frames != source.Frames ||
            remux.FilmGrainFrames != source.FilmGrainFrames ||
            remux.FilmGrainFrames != remux.Frames ||
            remux.MinimumFilmGrainPayloadBytes <= 0)
        {
            throw new InvalidOperationException(
                $"Film-grain remux decode mismatch: " +
                $"packets={source.Packets}/{remux.Packets}, " +
                $"frames={source.Frames}/{remux.Frames}, " +
                $"filmGrain={source.FilmGrainFrames}/{remux.FilmGrainFrames}");
        }

        Console.WriteLine(
            "[Av1HdrFilmGrainPolicyVerify] PASS serviceFilmGrainRemux=true " +
            $"encoder={summary.EncoderName} quality={summary.EncoderQualityMode} " +
            $"outputCommitted={summary.OutputCommitted.ToString().ToLowerInvariant()} " +
            $"packets={source.Packets}/{remux.Packets} " +
            $"frames={source.Frames}/{remux.Frames} " +
            $"filmGrainFrames={source.FilmGrainFrames}/{remux.FilmGrainFrames} " +
            "stagedOutputs=0 codec=av1");
    }
    finally
    {
        DeleteOutputAndStagedFiles(outputPath);
        DeleteMetricsLog(runId);
    }
}

static void RunHdrServiceExport(string inputPath, string outputPath)
{
    string runId = $"av1-hdr-policy-{Guid.NewGuid():N}";
    DeleteOutputAndStagedFiles(outputPath);
    try
    {
        FFmpegBootstrap.Initialize();
        var masks = new FrameMaskProvider();
        masks.SetFaceRects(
            1,
            [new Rect(16, 16, 32, 32)],
            new PixelSize(64, 64),
            1.0f,
            [1.0f]);

        var service = new VideoExportService(masks);
        service.Export(
            inputPath,
            outputPath,
            blurRadius: 4,
            runId: runId,
            allowHybridCopy: false);
        ExportRunSummary summary = service.LastExportSummary
            ?? throw new InvalidOperationException("VideoExportService did not create a summary.");
        if (!summary.OutputCommitted ||
            summary.Frames != 4 ||
            summary.SubmittedVideoFrames != 4 ||
            summary.ExpectedBlurFrames != 1 ||
            summary.AppliedBlurFrames != 1 ||
            summary.DirectFaceFrames + summary.BitmapMaskFrames != 1 ||
            summary.SourceBitDepth < 10 ||
            summary.OutputBitDepth < 10 ||
            summary.VideoFrameCoverageMismatch != 0 ||
            !summary.EncoderName.Contains("svtav1", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Unexpected HDR service export summary: {summary.ToLogLine()}");
        }
        if (!File.Exists(outputPath))
            throw new InvalidOperationException("Committed HDR service output was not created.");
        string[] stagedOutputs = FindStagedOutputs(outputPath);
        if (stagedOutputs.Length != 0)
        {
            throw new InvalidOperationException(
                $"HDR service export left staged outputs: {string.Join(',', stagedOutputs)}");
        }

        Console.WriteLine(
            $"[Av1HdrFilmGrainPolicyVerify] PASS serviceHdrExport=true " +
            $"outputCommitted={summary.OutputCommitted.ToString().ToLowerInvariant()} " +
            $"frames={summary.Frames} submitted={summary.SubmittedVideoFrames} " +
            $"masked={summary.AppliedBlurFrames} encoder={summary.EncoderName} " +
            $"sourceBitDepth={summary.SourceBitDepth} outputBitDepth={summary.OutputBitDepth}");
    }
    finally
    {
        DeleteMetricsLog(runId);
    }
}

static void RunFilmGrainFailClosedExport(string inputPath, string outputPath)
{
    string runId = $"av1-grain-policy-{Guid.NewGuid():N}";
    DeleteOutputAndStagedFiles(outputPath);
    InvalidOperationException? failure = null;
    try
    {
        FFmpegBootstrap.Initialize();
        var masks = new FrameMaskProvider();
        masks.SetFaceRects(
            1,
            [new Rect(16, 16, 32, 32)],
            new PixelSize(64, 64),
            1.0f,
            [1.0f]);

        var service = new VideoExportService(masks);
        try
        {
            service.Export(
                inputPath,
                outputPath,
                blurRadius: 4,
                runId: runId,
                allowHybridCopy: false);
        }
        catch (InvalidOperationException ex)
        {
            failure = ex;
        }

        if (failure == null ||
            !failure.Message.Contains("AV1 film grain", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Film-grain export did not fail closed with the expected message: " +
                $"{failure?.Message ?? "no exception"}");
        }
        if (service.LastExportSummary?.OutputCommitted == true)
            throw new InvalidOperationException("Failed film-grain export was marked committed.");
        if (File.Exists(outputPath))
            throw new InvalidOperationException("Failed film-grain export left a final output.");
        string[] stagedOutputs = FindStagedOutputs(outputPath);
        if (stagedOutputs.Length != 0)
        {
            throw new InvalidOperationException(
                $"Failed film-grain export left staged outputs: {string.Join(',', stagedOutputs)}");
        }

        Console.WriteLine(
            "[Av1HdrFilmGrainPolicyVerify] PASS serviceFilmGrainFailClosed=true " +
            "message='AV1 film grain' finalOutput=false stagedOutputs=0 outputCommitted=false");
    }
    finally
    {
        DeleteMetricsLog(runId);
    }
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

static unsafe void InspectDecodedFrames(
    AVCodecContext* context,
    AVFrame* frame,
    ref int frames,
    ref int tenBitFrames,
    ref int masteringFrames,
    ref int contentLightFrames,
    ref int filmGrainFrames,
    ref int minimumFilmGrainPayloadBytes,
    ref bool masteringValuesMatch,
    ref bool contentLightValuesMatch,
    ref string pixelFormat)
{
    while (true)
    {
        int receive = ffmpeg.avcodec_receive_frame(context, frame);
        if (receive == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receive == ffmpeg.AVERROR_EOF)
            return;
        Throw(receive);
        frames++;

        AVPixelFormat format = (AVPixelFormat)frame->format;
        pixelFormat = ffmpeg.av_get_pix_fmt_name(format) ?? format.ToString();
        AVPixFmtDescriptor* descriptor = ffmpeg.av_pix_fmt_desc_get(format);
        if (descriptor != null && descriptor->nb_components >= 3 && descriptor->comp[0].depth >= 10)
            tenBitFrames++;

        if (frame->color_primaries != AVColorPrimaries.AVCOL_PRI_BT2020 ||
            frame->color_trc != AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084 ||
            frame->colorspace != AVColorSpace.AVCOL_SPC_BT2020_NCL)
        {
            throw new InvalidOperationException(
                $"Decoded HDR color fields changed: primaries={frame->color_primaries}, " +
                $"trc={frame->color_trc}, space={frame->colorspace}");
        }

        AVFrameSideData* mastering = ffmpeg.av_frame_get_side_data(
            frame,
            AVFrameSideDataType.AV_FRAME_DATA_MASTERING_DISPLAY_METADATA);
        if (mastering != null && mastering->data != null &&
            mastering->size >= (ulong)sizeof(AVMasteringDisplayMetadata))
        {
            masteringFrames++;
            AVMasteringDisplayMetadata* value = (AVMasteringDisplayMetadata*)mastering->data;
            AVRational* primaries = (AVRational*)&value->display_primaries;
            masteringValuesMatch &= value->has_primaries != 0 &&
                                     value->has_luminance != 0 &&
                                     Near(primaries[0], 0.68, 0.00002) &&
                                     Near(primaries[1], 0.32, 0.00002) &&
                                     Near(primaries[2], 0.265, 0.00002) &&
                                     Near(primaries[3], 0.69, 0.00002) &&
                                     Near(primaries[4], 0.15, 0.00002) &&
                                     Near(primaries[5], 0.06, 0.00002) &&
                                     Near(value->white_point[0], 0.3127, 0.00002) &&
                                     Near(value->white_point[1], 0.329, 0.00002) &&
                                     Near(value->max_luminance, 1000.0, 0.0001) &&
                                     Near(value->min_luminance, 0.0001, 0.00003);
        }

        AVFrameSideData* contentLight = ffmpeg.av_frame_get_side_data(
            frame,
            AVFrameSideDataType.AV_FRAME_DATA_CONTENT_LIGHT_LEVEL);
        if (contentLight != null && contentLight->data != null &&
            contentLight->size >= (ulong)sizeof(AVContentLightMetadata))
        {
            contentLightFrames++;
            AVContentLightMetadata* value = (AVContentLightMetadata*)contentLight->data;
            contentLightValuesMatch &= value->MaxCLL == 1000 && value->MaxFALL == 400;
        }

        AVFrameSideData* filmGrain = ffmpeg.av_frame_get_side_data(
            frame,
            AVFrameSideDataType.AV_FRAME_DATA_FILM_GRAIN_PARAMS);
        if (filmGrain != null && filmGrain->data != null && filmGrain->size > 0)
        {
            filmGrainFrames++;
            minimumFilmGrainPayloadBytes = Math.Min(
                minimumFilmGrainPayloadBytes,
                checked((int)filmGrain->size));
        }

        ffmpeg.av_frame_unref(frame);
    }
}

static unsafe void AddStaticHdrContextSideData(AVCodecContext* context)
{
    AVFrameSideData* masteringSideData = ffmpeg.av_frame_side_data_new(
        &context->decoded_side_data,
        &context->nb_decoded_side_data,
        AVFrameSideDataType.AV_FRAME_DATA_MASTERING_DISPLAY_METADATA,
        (ulong)sizeof(AVMasteringDisplayMetadata),
        0);
    AVFrameSideData* contentLightSideData = ffmpeg.av_frame_side_data_new(
        &context->decoded_side_data,
        &context->nb_decoded_side_data,
        AVFrameSideDataType.AV_FRAME_DATA_CONTENT_LIGHT_LEVEL,
        (ulong)sizeof(AVContentLightMetadata),
        0);
    if (masteringSideData == null || masteringSideData->data == null ||
        contentLightSideData == null || contentLightSideData->data == null)
    {
        throw new InvalidOperationException("Unable to allocate encoder HDR decoded_side_data.");
    }

    AVMasteringDisplayMetadata* mastering =
        (AVMasteringDisplayMetadata*)masteringSideData->data;
    AVRational* primaries = (AVRational*)&mastering->display_primaries;
    primaries[0] = new AVRational { num = 34000, den = 50000 };
    primaries[1] = new AVRational { num = 16000, den = 50000 };
    primaries[2] = new AVRational { num = 13250, den = 50000 };
    primaries[3] = new AVRational { num = 34500, den = 50000 };
    primaries[4] = new AVRational { num = 7500, den = 50000 };
    primaries[5] = new AVRational { num = 3000, den = 50000 };
    mastering->white_point[0] = new AVRational { num = 15635, den = 50000 };
    mastering->white_point[1] = new AVRational { num = 16450, den = 50000 };
    mastering->max_luminance = new AVRational { num = 1000, den = 1 };
    mastering->min_luminance = new AVRational { num = 1, den = 10000 };
    mastering->has_primaries = 1;
    mastering->has_luminance = 1;

    AVContentLightMetadata* contentLight =
        (AVContentLightMetadata*)contentLightSideData->data;
    contentLight->MaxCLL = 1000;
    contentLight->MaxFALL = 400;
}

static unsafe bool HasPacketSideData(
    AVPacketSideData* sideData,
    int count,
    AVPacketSideDataType type)
{
    return sideData != null && count > 0 &&
           ffmpeg.av_packet_side_data_get(sideData, count, type) != null;
}

static unsafe int DrainMuxPackets(
    AVCodecContext* context,
    AVPacket* packet,
    AVStream* stream,
    AVFormatContext* output)
{
    int packetCount = 0;
    while (true)
    {
        int receive = ffmpeg.avcodec_receive_packet(context, packet);
        if (receive == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receive == ffmpeg.AVERROR_EOF)
            return packetCount;
        Throw(receive);
        ffmpeg.av_packet_rescale_ts(packet, context->time_base, stream->time_base);
        packet->stream_index = stream->index;
        Throw(ffmpeg.av_interleaved_write_frame(output, packet));
        ffmpeg.av_packet_unref(packet);
        packetCount++;
    }
}

static unsafe void FillTenBitFrame(AVFrame* frame, int frameIndex)
{
    for (int y = 0; y < frame->height; y++)
    {
        Span<ushort> row = new(frame->data[0] + y * frame->linesize[0], frame->width);
        for (int x = 0; x < frame->width; x++)
            row[x] = (ushort)(128 + ((x * 8 + y * 4 + frameIndex * 32) % 768));
    }
    for (int plane = 1; plane <= 2; plane++)
    {
        for (int y = 0; y < frame->height / 2; y++)
        {
            new Span<ushort>(
                frame->data[(uint)plane] + y * frame->linesize[(uint)plane],
                frame->width / 2).Fill(512);
        }
    }
}

static unsafe void RequiredOption(AVCodecContext* context, string key, string value)
{
    int result = ffmpeg.av_opt_set(context->priv_data, key, value, 0);
    if (result < 0)
        throw new InvalidOperationException($"Option {key}={value} failed: {Error(result)}");
}

static bool Near(AVRational value, double expected, double tolerance)
{
    return value.den != 0 &&
           Math.Abs(value.num / (double)value.den - expected) <= tolerance;
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

sealed record DecodeResult(
    int Frames,
    int Packets,
    int TenBitFrames,
    int MasteringFrames,
    int ContentLightFrames,
    int FilmGrainFrames,
    int MinimumFilmGrainPayloadBytes,
    bool MasteringValuesMatch,
    bool ContentLightValuesMatch,
    bool StreamMastering,
    bool StreamContentLight,
    string DecoderName,
    string PixelFormat);
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
    )
    & dotnet @runArguments
    if ($LASTEXITCODE -ne 0) {
        throw "AV1 HDR/film-grain E2E harness failed with exit code $LASTEXITCODE."
    }

    $exportPath = Join-Path $repo "Services\Video\VideoExportService.cs"
    $guardPath = Join-Path $repo "Services\Video\FFmpegHdrMetadataGuard.cs"
    $exportSource = Get-Content -Raw -Path $exportPath
    $guardSource = Get-Content -Raw -Path $guardPath

    function Assert-SourcePattern {
        param(
            [string]$Name,
            [string]$Source,
            [string]$Pattern
        )

        if (-not [regex]::IsMatch(
                $Source,
                $Pattern,
                [Text.RegularExpressions.RegexOptions]::Singleline)) {
            throw "Production source policy check failed: $Name"
        }
    }

    Assert-SourcePattern `
        "main decoder exports AV1 film grain before open" `
        $exportSource `
        'avcodec_parameters_to_context\s*\(\s*dec\s*,\s*inStream->codecpar\s*\).*?ConfigureDecoderSideDataExport\s*\(\s*dec\s*,\s*inStream->codecpar->codec_id\s*\).*?avcodec_open2\s*\(\s*dec\s*,\s*decoder'
    Assert-SourcePattern `
        "HDR probe decoder exports AV1 film grain before open" `
        $exportSource `
        'avcodec_parameters_to_context\s*\(\s*decoderContext\s*,\s*parameters\s*\).*?ConfigureDecoderSideDataExport\s*\(\s*decoderContext\s*,\s*parameters->codec_id\s*\).*?avcodec_open2\s*\(\s*decoderContext\s*,\s*decoder'
    Assert-SourcePattern `
        "AV1 decoder export helper sets film-grain flag" `
        $exportSource `
        'ConfigureDecoderSideDataExport\s*\(.*?codecId\s*==\s*AVCodecID\.AV_CODEC_ID_AV1.*?export_side_data\s*\|=\s*ffmpeg\.AV_CODEC_EXPORT_DATA_FILM_GRAIN'
    Assert-SourcePattern `
        "SVT AV1 encoder receives mastering payload" `
        $exportSource `
        'TryConfigureEncoderStaticHdrMetadata\s*\(.*?AV_FRAME_DATA_MASTERING_DISPLAY_METADATA.*?MasteringDisplayPayload'
    Assert-SourcePattern `
        "SVT AV1 encoder receives content-light payload" `
        $exportSource `
        'TryConfigureEncoderStaticHdrMetadata\s*\(.*?AV_FRAME_DATA_CONTENT_LIGHT_LEVEL.*?ContentLightPayload'
    Assert-SourcePattern `
        "HDR payload is allocated in encoder decoded_side_data" `
        $exportSource `
        'av_frame_side_data_new\s*\(\s*&encoderContext->decoded_side_data\s*,\s*&encoderContext->nb_decoded_side_data'
    Assert-SourcePattern `
        "AV1 HDR selects SVT payload configuration" `
        $exportSource `
        'isSvtAv1\s*&&\s*!TryConfigureEncoderStaticHdrMetadata\s*\(\s*ctx\s*,\s*hdrMetadata'
    Assert-SourcePattern `
        "AV1 HDR keeps the AV1 codec" `
        $exportSource `
        'inputCodecId\s*==\s*AVCodecID\.AV_CODEC_ID_AV1\s*\?\s*AVCodecID\.AV_CODEC_ID_AV1'
    Assert-SourcePattern `
        "film-grain frame metadata is guarded" `
        $guardSource `
        'AV_FRAME_DATA_FILM_GRAIN_PARAMS.*?AV1 film grain'
    Assert-SourcePattern `
        "decoded-frame guard is used by export" `
        $exportSource `
        'FFmpegHdrMetadataGuard\.FindUnsupportedMetadata\s*\(\s*frame\s*\).*?ThrowUnsupportedDynamicVideoMetadata'

    $configureCallIndex = $exportSource.IndexOf(
        "!TryConfigureEncoderStaticHdrMetadata(ctx, hdrMetadata",
        [StringComparison]::Ordinal)
    $encoderOpenIndex = if ($configureCallIndex -ge 0) {
        $exportSource.IndexOf(
            "ffmpeg.avcodec_open2(ctx, encoder",
            $configureCallIndex,
            [StringComparison]::Ordinal)
    }
    else {
        -1
    }
    if ($configureCallIndex -lt 0 -or $encoderOpenIndex -le $configureCallIndex) {
        throw "SVT HDR payload must be configured before avcodec_open2."
    }

    Write-Host (
        "[Av1HdrFilmGrainPolicyVerify] PASS productionSource=" +
        "mainDecoderExport,probeDecoderExport,svtHdrPayload,filmGrainGuard")
}
finally {
    $cleanupFailure = $null
    try {
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
        throw "AV1 HDR/film-grain harness cleanup failed: $cleanupFailure"
    }
}
