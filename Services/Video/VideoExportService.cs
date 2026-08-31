using FaceShield.Services.Diagnostics;
using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace FaceShield.Services.Video;

public unsafe sealed class VideoExportService
{
    private readonly IFrameMaskProvider _maskProvider;
    private readonly MaskedVideoExporter _masked = new();
    private int _directFaceBlurFrames;
    private int _bitmapMaskBlurFrames;
    private int _nativeYuvBlurFrames;
    private bool _staticHdrConfigured;
    private bool _losslessX264RgbConfigured;
    private VideoHdrMetadata? _configuredHdrMetadata;

    public ExportRunSummary? LastExportSummary { get; private set; }

    public VideoExportService(IFrameMaskProvider maskProvider)
    {
        _maskProvider = maskProvider;
    }

    public void Export(
        string inputPath,
        string outputPath,
        int blurRadius,
        IProgress<ExportProgress>? progress = null,
        System.Threading.CancellationToken cancellationToken = default,
        string? runId = null,
        bool allowHybridCopy = false)
    {
        var endToEndTimer = Stopwatch.StartNew();
        int attemptCount = 0;
        string finalOutputPath = Path.GetFullPath(outputPath);
        string stagedOutputPath = VideoExportStagingPolicy.BuildStagedOutputPath(finalOutputPath);
        try
        {
            try
            {
                attemptCount++;
                ExportInternal(
                    inputPath,
                    stagedOutputPath,
                    blurRadius,
                    progress,
                    cancellationToken,
                    runId,
                    exportMode: "primary",
                    forceSoftwareEncoder: false,
                    allowHybridCopy: allowHybridCopy && VideoHybridCopyPolicy.EnableHybridCopyWindow,
                    forceSafeEncoding: false,
                    forceAudioTranscode: false,
                    forceH264Fallback: false);
            }
            catch (InvalidOperationException ex) when (VideoExportRetryPolicy.ShouldRetryWithSafeEncoding(ex))
            {
                Debug.WriteLine($"[Export] mode=fallback-safe로 재시도: {ex.Message}");
                try
                {
                    attemptCount++;
                    ExportInternal(
                        inputPath,
                        stagedOutputPath,
                        blurRadius,
                        progress,
                        cancellationToken,
                        runId,
                        exportMode: "fallback-safe",
                        forceSoftwareEncoder: true,
                        allowHybridCopy: false,
                        forceSafeEncoding: true,
                        forceAudioTranscode: false,
                        forceH264Fallback: false);
                }
                catch (InvalidOperationException nestedEx) when (
                    VideoExportRetryPolicy.ShouldRetryWithH264Fallback(nestedEx, _staticHdrConfigured))
                {
                    Debug.WriteLine($"[Export] mode=fallback-h264로 재시도: 안전 모드에서도 실패. {nestedEx.Message}");
                    attemptCount++;
                    ExportInternal(
                        inputPath,
                        stagedOutputPath,
                        blurRadius,
                        progress,
                        cancellationToken,
                        runId,
                        exportMode: "fallback-h264",
                        forceSoftwareEncoder: true,
                        allowHybridCopy: false,
                        forceSafeEncoding: true,
                        forceAudioTranscode: false,
                        forceH264Fallback: true);
                }
            }

            if (!File.Exists(stagedOutputPath))
                throw new InvalidOperationException("검증된 임시 출력 파일이 생성되지 않았습니다.");
            if (LastExportSummary == null)
                throw new InvalidOperationException("검증된 내보내기 요약이 생성되지 않았습니다.");

            int completedFrames = Math.Max(
                1,
                LastExportSummary.SubmittedVideoFrames > 0
                    ? LastExportSummary.SubmittedVideoFrames
                    : LastExportSummary.Frames);
            progress?.Report(new ExportProgress(
                completedFrames,
                completedFrames,
                "출력 파일을 적용하는 중..."));

            var outputCommitTimer = Stopwatch.StartNew();
            string commitMode;
            if (File.Exists(finalOutputPath))
            {
                File.Replace(
                    stagedOutputPath,
                    finalOutputPath,
                    destinationBackupFileName: null,
                    ignoreMetadataErrors: true);
                commitMode = "replace";
            }
            else
            {
                File.Move(stagedOutputPath, finalOutputPath);
                commitMode = "move";
            }

            outputCommitTimer.Stop();
            endToEndTimer.Stop();
            LastExportSummary = LastExportSummary with
            {
                OutputCommitted = true,
                FinalAttemptMs = LastExportSummary.TotalMs,
                OutputCommitMs = outputCommitTimer.ElapsedMilliseconds,
                AttemptCount = attemptCount,
                TotalMs = endToEndTimer.ElapsedMilliseconds
            };
            string committedLine =
                $"[ExportCommitted] runId={LastExportSummary.RunId ?? "n/a"}, mode={commitMode}";
            Debug.WriteLine(committedLine);
            Debug.WriteLine(LastExportSummary.ToLogLine());
            RunMetricsLog.AppendRunLines(
                LastExportSummary.RunId,
                committedLine,
                LastExportSummary.ToLogLine());
            progress?.Report(ExportProgress.Completed(
                completedFrames,
                "내보내기가 완료되었습니다."));
        }
        finally
        {
            VideoExportStagingPolicy.TryDeleteStagedOutput(stagedOutputPath);
        }
    }

    private unsafe void ExportInternal(
        string inputPath,
        string outputPath,
        int blurRadius,
        IProgress<ExportProgress>? progress,
        System.Threading.CancellationToken cancellationToken,
        string? runId,
        string exportMode,
        bool forceSoftwareEncoder,
        bool allowHybridCopy,
        bool forceSafeEncoding,
        bool forceAudioTranscode,
        bool forceH264Fallback,
        bool allowPacketDropRetry = true)
    {
        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
        _directFaceBlurFrames = 0;
        _bitmapMaskBlurFrames = 0;
        _nativeYuvBlurFrames = 0;
        _staticHdrConfigured = false;
        _losslessX264RgbConfigured = false;
        LastExportSummary = null;
        bool shouldRetryWithFullEncode = false;
        string? packetDropFallbackReason = null;

        AVFormatContext* inFmt = null;
        AVFormatContext* outFmt = null;
        AVCodecContext* dec = null;
        AVCodecContext* enc = null;
        AVCodecContext* audioDec = null;
        AVCodecContext* audioEnc = null;
        SwsContext* swsDecToBgra = null;
        SwsContext* swsBgraToEnc = null;
        SwsContext* swsDecToEnc = null;
        SwrContext* swr = null;
        AVAudioFifo* audioFifo = null;

        AVPacket* pkt = ffmpeg.av_packet_alloc();
        AVPacket* outPkt = ffmpeg.av_packet_alloc();
        AVPacket* audioPkt = ffmpeg.av_packet_alloc();
        AVFrame* frame = ffmpeg.av_frame_alloc();
        AVFrame* bgra = ffmpeg.av_frame_alloc();
        AVFrame* encFrame = ffmpeg.av_frame_alloc();
        AVFrame* audioFrame = ffmpeg.av_frame_alloc();
        AVFrame* audioConvFrame = ffmpeg.av_frame_alloc();
        AVFrame* audioEncFrame = ffmpeg.av_frame_alloc();

        int videoStreamIndex = -1;
        int audioStreamIndex = -1;
        int frameIndex = 0;
        int totalFrames = 0;
        double sourceFps = 0.0;
        long lastEncodedPts = -1;
        bool hasLastEncodedPts = false;
        long lastEncodedPacketPts = -1;
        bool hasLastEncodedPacketPts = false;
        long lastEncodedPacketDts = -1;
        bool hasLastEncodedPacketDts = false;
        long swsToBgraMs = 0;
        long maskMs = 0;
        long swsToEncMs = 0;
        var encodeTimer = new Stopwatch();
        var encoderFlushTimer = new Stopwatch();
        var outputCloseTimer = new Stopwatch();
        long lastAudioEncPacketPts = -1;
        bool hasLastAudioEncPacketPts = false;
        long lastAudioEncPacketDts = -1;
        bool hasLastAudioEncPacketDts = false;
        long lastVideoCopyPacketPts = -1;
        bool hasLastVideoCopyPacketPts = false;
        long lastVideoCopyPacketDts = -1;
        bool hasLastVideoCopyPacketDts = false;
        bool wasLastPacketEncoded = false;
        int inputVideoPacketCount = 0;
        int outputVideoPacketCount = 0;
        int copiedVideoPacketCount = 0;
        int copiedSourceVideoPacketCount = 0;
        int encodedSourceVideoPacketCount = 0;
        int submittedVideoFrameCount = 0;
        var submittedEncodedFramePts = new HashSet<long>();
        var submittedEncodedFrameDurations = new Dictionary<long, long>();
        var emittedEncodedFramePts = new Dictionary<long, int>();
        var emittedEncodedMuxPts = new List<long>();
        var appliedBlurFrameIndices = new HashSet<int>();
        int sampleSourceVideoPacketCount = 0;
        int sampleCopiedVideoPacketCount = 0;
        int sampleEncodedFrameCount = 0;
        int sampleBlurredFrameCount = 0;
        int encodedWindowFrameCount = 0;
        int copyTimestampFixCount = 0;
        int outputPacketPtsGapOutlierCount = 0;
        long maxOutputPacketPtsGap = 0;
        var encodedTimestampIntegrity = new VideoPacketTimestampIntegrity();
        int copyGapOutlierCount = 0;
        long maxCopyGap = 0;
        int lastPacketFrameIndexForHybrid = -1;
        bool hasLastPacketFrameForHybrid = false;
        long encodedPacketFrameStep = 1;
        long hybridCopyVideoFrameStep = 1;
        long packetTimestampBase = 0;
        bool hasPacketTimestampBase = false;
        int packetFrameIndexReliabilityFailureCount = 0;
        var swTotal = Stopwatch.StartNew();

        try
        {
            // ───────── input ─────────
            VideoExportFfmpegDiagnostics.Throw(ffmpeg.avformat_open_input(&inFmt, inputPath, null, null));
            VideoExportFfmpegDiagnostics.Throw(ffmpeg.avformat_find_stream_info(inFmt, null));
            VideoPresentationMetadataPolicy.EnsureContainerStructureSupported(inFmt);

            videoStreamIndex = FFmpegStreamSelection.FindPrimaryVideoStreamIndex(inFmt);
            var audioStreamIndices = new List<int>();
            for (int i = 0; i < inFmt->nb_streams; i++)
            {
                var stream = inFmt->streams[i];
                if (stream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                {
                    audioStreamIndices.Add(i);
                    if (audioStreamIndex < 0)
                        audioStreamIndex = i;
                }
            }
            if (videoStreamIndex < 0)
                throw new InvalidOperationException("Video stream not found.");

            AVStream* inStream = inFmt->streams[videoStreamIndex];
            AVStream* inAudioStream = audioStreamIndex >= 0 ? inFmt->streams[audioStreamIndex] : null;
            totalFrames = (int)inStream->nb_frames;
            sourceFps =
                inStream->avg_frame_rate.num != 0
                    ? ffmpeg.av_q2d(inStream->avg_frame_rate)
                    : inStream->r_frame_rate.num != 0
                        ? ffmpeg.av_q2d(inStream->r_frame_rate)
                        : 0.0;
            hybridCopyVideoFrameStep = VideoExportTimingPolicy.GetVideoFrameStep(sourceFps, inStream->time_base);
            if (totalFrames <= 0)
            {
                double durationSeconds = inStream->duration != 0
                    ? inStream->duration * ffmpeg.av_q2d(inStream->time_base)
                    : inFmt->duration > 0
                        ? inFmt->duration / (double)ffmpeg.AV_TIME_BASE
                        : 0.0;

                if (sourceFps > 0 && durationSeconds > 0)
                    totalFrames = (int)Math.Round(durationSeconds * sourceFps);
            }

            HashSet<int>? blurFrameSet = null;
            List<(int Start, int EndExclusive)>? blurRanges = null;
            int blurRangeCursor = 0;
            (int Start, int EndExclusive)? hybridEncodeWindow = null;
            List<int>? hybridCandidateKeyframes = null;
            bool hybridCopyAttempted = false;
            bool allowHybridCopyCurrent = allowHybridCopy;
            string? hybridCopyFallbackReason = null;
            int hybridWindowStartFrame = -1;
            int hybridWindowEndFrame = -1;
            int hybridModeTransitionCount = 0;
            int hybridModeTimestampSyncCount = 0;
            if (_maskProvider is FrameMaskProvider frameMaskProvider)
            {
                blurFrameSet = VideoExportFrameRangePolicy.BuildBlurFrameSet(frameMaskProvider);
                if (blurFrameSet.Count == 0)
                {
                    progress?.Report(new ExportProgress(0, totalFrames, "블러 대상이 없어 원본 스트림을 고속 복사합니다..."));
                    var remuxCounts = VideoExportCopyPolicy.ExportByRemuxCopy(
                        inFmt,
                        outputPath,
                        videoStreamIndex,
                        totalFrames,
                        sourceFps,
                        progress,
                        cancellationToken);
                    int remuxPacketCountMismatch = (int)Math.Min(
                        int.MaxValue,
                        Math.Abs((long)remuxCounts.InputVideoPackets - remuxCounts.OutputVideoPackets));
                    bool remuxPacketIntegrityInvalid =
                        remuxCounts.InputVideoPackets <= 0 ||
                        remuxCounts.OutputVideoPackets <= 0 ||
                        remuxPacketCountMismatch > 0 ||
                        remuxCounts.VideoPacketTimestampAdjustments > 0 ||
                        remuxCounts.OutputPacketPtsGapOutlierCount > 0;
                    LastExportSummary = new ExportRunSummary(
                        remuxCounts.OutputVideoPackets,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        swTotal.ElapsedMilliseconds,
                        runId,
                        exportMode,
                        HybridCopyAttempted: false,
                        HybridCopyUsed: false,
                        HybridCopyFallbackReason: null,
                        HybridWindowStartFrame: -1,
                        HybridWindowEndFrame: -1,
                        HybridModeTransitionCount: 0,
                        HybridModeTimestampSyncCount: 0,
                        InputVideoPackets: remuxCounts.InputVideoPackets,
                        OutputVideoPackets: remuxCounts.OutputVideoPackets,
                        CopiedVideoPackets: remuxCounts.OutputVideoPackets,
                        CopiedSourceVideoPackets: remuxCounts.InputVideoPackets,
                        EncodedSourceVideoPackets: 0,
                        DroppedVideoPackets: Math.Max(0, remuxCounts.InputVideoPackets - remuxCounts.OutputVideoPackets),
                        OutputPacketPtsGapOutlierCount: remuxCounts.OutputPacketPtsGapOutlierCount,
                        MaxOutputPacketPtsGap: remuxCounts.MaxOutputPacketPtsGap,
                        HybridCopyTimestampFixCount: 0,
                        PacketLossFallbackReason: remuxPacketIntegrityInvalid
                            ? $"remux-packet-integrity={remuxCounts.InputVideoPackets}/{remuxCounts.OutputVideoPackets}"
                            : null,
                        HybridEncodedPacketFrameStep: 0,
                        HybridCopyPacketFrameStep: 0,
                        ForceSoftwareEncoder: forceSoftwareEncoder,
                        ForceSafeEncoding: forceSafeEncoding,
                        ForceAudioTranscode: forceAudioTranscode,
                        ForceH264Fallback: forceH264Fallback,
                        OutputPacketCountMismatch: remuxPacketCountMismatch,
                        MissingVideoPacketTimestampCount: remuxCounts.MissingVideoPacketTimestamps,
                        VideoPacketTimestampAdjustmentCount: remuxCounts.VideoPacketTimestampAdjustments,
                        EncoderName: "stream-copy",
                        EncoderQualityMode: "lossless-remux",
                        SourcePixelFormat: VideoExportFidelityPolicy.GetPixelFormatName((AVPixelFormat)inStream->codecpar->format),
                        OutputPixelFormat: VideoExportFidelityPolicy.GetPixelFormatName((AVPixelFormat)inStream->codecpar->format),
                        SourceBitDepth: VideoExportFidelityPolicy.GetPixelFormatBitDepth((AVPixelFormat)inStream->codecpar->format),
                        OutputBitDepth: VideoExportFidelityPolicy.GetPixelFormatBitDepth((AVPixelFormat)inStream->codecpar->format),
                        SourceVideoBitrate: VideoExportFidelityPolicy.ResolveSourceVideoBitrate(inStream, null),
                        TargetVideoBitrate: VideoExportFidelityPolicy.ResolveSourceVideoBitrate(inStream, null),
                        OutputCloseMs: remuxCounts.OutputCloseMs);
                    Debug.WriteLine(LastExportSummary.ToLogLine());
                    RunMetricsLog.AppendRunLines(LastExportSummary.RunId, LastExportSummary.ToLogLine());
                    if (remuxPacketIntegrityInvalid)
                    {
                        throw new InvalidOperationException(
                            $"원본 스트림 복사 출력의 비디오 패킷/PTS 무결성이 확인되지 않아 중단합니다. " +
                            $"input={remuxCounts.InputVideoPackets}, output={remuxCounts.OutputVideoPackets}, " +
                            $"missingTs={remuxCounts.MissingVideoPacketTimestamps}, " +
                            $"timestampAdjustments={remuxCounts.VideoPacketTimestampAdjustments}, " +
                            $"ptsOutliers={remuxCounts.OutputPacketPtsGapOutlierCount}");
                    }
                    return;
                }

                blurRanges = VideoExportFrameRangePolicy.BuildBlurFrameRanges(blurFrameSet);
            }

            var hybridPlan = VideoHybridCopyPolicy.BuildPlan(
                inputPath,
                sourceFps,
                totalFrames,
                inStream->time_base,
                allowHybridCopyCurrent,
                blurRanges);
            allowHybridCopyCurrent = hybridPlan.AllowHybridCopy;
            totalFrames = hybridPlan.TotalFrames;
            blurRanges = hybridPlan.BlurRanges;
            hybridEncodeWindow = hybridPlan.EncodeWindow;
            hybridCandidateKeyframes = hybridPlan.CandidateKeyframes;
            hybridCopyAttempted = hybridPlan.Attempted;
            hybridCopyFallbackReason = hybridPlan.FallbackReason;
            hybridWindowStartFrame = hybridPlan.WindowStartFrame;
            hybridWindowEndFrame = hybridPlan.WindowEndFrame;

            int exportSampleWindowFrames = VideoExportProgressPolicy.ResolveExportSampleWindowFrames(sourceFps, totalFrames);

            string? unsupportedStreamMetadata =
                FFmpegHdrMetadataGuard.FindUnsupportedMetadata(inStream->codecpar);
            if (unsupportedStreamMetadata != null)
                VideoExportCompatibilityPolicy.ThrowUnsupportedDynamicVideoMetadata(unsupportedStreamMetadata);

            AVCodec* decoder = ffmpeg.avcodec_find_decoder(inStream->codecpar->codec_id);
            dec = ffmpeg.avcodec_alloc_context3(decoder);
            VideoExportFfmpegDiagnostics.Throw(ffmpeg.avcodec_parameters_to_context(dec, inStream->codecpar));
            VideoHdrMetadataPolicy.ConfigureDecoderSideDataExport(dec, inStream->codecpar->codec_id);
            VideoExportFfmpegDiagnostics.Throw(ffmpeg.avcodec_open2(dec, decoder, null));
            AVFieldOrder sourceFieldOrder = VideoExportCompatibilityPolicy.ResolveSourceFieldOrder(inStream, dec);
            if (VideoExportCompatibilityPolicy.IsInterlacedFieldOrder(sourceFieldOrder))
                VideoExportCompatibilityPolicy.ThrowInterlacedAutoMosaicUnsupported(sourceFieldOrder);
            bool sourceUsesHdrTransfer = dec->color_trc is
                AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084 or
                AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67;
            bool sourceMayCarryHdrMetadata =
                sourceUsesHdrTransfer ||
                VideoExportFidelityPolicy.GetPixelFormatBitDepth(dec->pix_fmt) > 8 ||
                inStream->codecpar->codec_id is
                    AVCodecID.AV_CODEC_ID_HEVC or
                    AVCodecID.AV_CODEC_ID_AV1;
            VideoHdrMetadata? hdrMetadata = sourceMayCarryHdrMetadata
                ? VideoHdrProbePolicy.ProbeVideoHdrMetadata(inputPath)
                : null;
            _configuredHdrMetadata = hdrMetadata;
            _staticHdrConfigured = hdrMetadata?.HasStaticMetadata == true;
            bool sourceIsRgbH264 =
                inStream->codecpar->codec_id == AVCodecID.AV_CODEC_ID_H264 &&
                VideoExportFidelityPolicy.IsRgbPixelFormat(VideoExportFidelityPolicy.ResolveSourcePixelFormat(inStream, dec));
            if (sourceIsRgbH264 && hdrMetadata?.HasStaticMetadata == true)
            {
                throw new VideoExportIntegrityException(
                    "정적 HDR 메타데이터가 있는 RGB H.264 영상은 libx264rgb에서 " +
                    "원본 표현을 보존할 수 없습니다. HEVC 손실 변환 없이 내보내기를 중단했습니다.");
            }

            // ───────── output ─────────
            VideoExportFfmpegDiagnostics.Throw(ffmpeg.avformat_alloc_output_context2(&outFmt, null, null, outputPath));
            VideoPresentationMetadataPolicy.CopyFormatPresentationMetadata(inFmt, outFmt);

            AVCodec* encoder;
            AVCodecID inputCodecId = inStream->codecpar->codec_id;
            AVCodecID encoderInputCodecId = hdrMetadata?.HasStaticMetadata == true
                ? inputCodecId == AVCodecID.AV_CODEC_ID_AV1
                    ? AVCodecID.AV_CODEC_ID_AV1
                    : AVCodecID.AV_CODEC_ID_HEVC
                : forceH264Fallback && inputCodecId != AVCodecID.AV_CODEC_ID_H264
                    ? AVCodecID.AV_CODEC_ID_H264
                    : inputCodecId;
            enc = VideoEncoderContextPolicy.TryCreateEncoderContext(
                encoderInputCodecId,
                inStream,
                dec,
                outFmt,
                out encoder,
                out var encoderQualityConfiguration,
                out var encoderError,
                forceSoftwareEncoder,
                forceSafeEncoding,
                hdrMetadata);

            string? exportNotice = null;
            if (enc == null)
            {
                if (hdrMetadata?.HasStaticMetadata == true &&
                    inputCodecId == AVCodecID.AV_CODEC_ID_AV1)
                {
                    string hdrReason = string.IsNullOrWhiteSpace(encoderError)
                        ? "검증된 SVT-AV1 HDR 인코더를 사용할 수 없습니다."
                        : encoderError;
                    throw new VideoExportIntegrityException(
                        "AV1 HDR을 다른 코덱으로 바꾸면 원본 표현을 보장할 수 없어 " +
                        $"내보내기를 중단했습니다. 사유: {hdrReason}");
                }

                string inputName = VideoExportFfmpegDiagnostics.GetCodecName(encoderInputCodecId);
                var fallbackCodecId = hdrMetadata?.HasStaticMetadata == true
                    ? AVCodecID.AV_CODEC_ID_HEVC
                    : AVCodecID.AV_CODEC_ID_H264;
                string fallbackName = VideoExportFfmpegDiagnostics.GetCodecName(fallbackCodecId);
                string reason = string.IsNullOrWhiteSpace(encoderError)
                    ? "원본 코덱 인코더를 찾을 수 없습니다."
                    : encoderError;
                exportNotice = $"원본 코덱({inputName}) 인코더를 사용할 수 없어 {fallbackName}로 내보냅니다. 사유: {reason}";

                enc = VideoEncoderContextPolicy.TryCreateEncoderContext(
                    fallbackCodecId,
                    inStream,
                    dec,
                    outFmt,
                    out encoder,
                    out encoderQualityConfiguration,
                    out var fallbackError,
                    forceSoftwareEncoder,
                    forceSafeEncoding,
                    hdrMetadata);
                if (enc == null)
                    throw new InvalidOperationException($"대체 인코더 초기화 실패: {fallbackError}");
            }
            else if (encoder != null && encoder->id != inputCodecId)
            {
                exportNotice =
                    $"10비트 원본 품질을 보존하기 위해 " +
                    $"{VideoExportFfmpegDiagnostics.GetEncoderName(encoder)} 인코더를 사용합니다.";
            }
            _losslessX264RgbConfigured = string.Equals(
                VideoExportFfmpegDiagnostics.GetEncoderName(encoder),
                "libx264rgb",
                StringComparison.OrdinalIgnoreCase);

            AVStream* outAudioStream = null;
            string? audioNotice = null;
            bool audioCopy = false;
            bool audioReencode = false;
            long audioPts = 0;
            var copiedStreams = new Dictionary<int, StreamCopyState>();
            if (inAudioStream != null)
            {
                int supported = outFmt->oformat != null
                    ? ffmpeg.avformat_query_codec(outFmt->oformat, inAudioStream->codecpar->codec_id, 0)
                    : 0;
                bool allowAudioCopy = supported > 0 && !forceAudioTranscode;
                if (allowAudioCopy)
                {
                    outAudioStream = ffmpeg.avformat_new_stream(outFmt, null);
                    if (outAudioStream == null)
                        throw new InvalidOperationException(
                            "오디오 출력 스트림을 생성하지 못해 원본 소리를 보존할 수 없습니다.");
                    else
                    {
                        VideoExportFfmpegDiagnostics.Throw(ffmpeg.avcodec_parameters_copy(outAudioStream->codecpar, inAudioStream->codecpar));
                        outAudioStream->codecpar->codec_tag = 0;
                        outAudioStream->time_base = inAudioStream->time_base;
                        audioCopy = true;
                        copiedStreams.Add(
                            audioStreamIndex,
                            new StreamCopyState(audioStreamIndex, inAudioStream, outAudioStream));
                    }
                }
                else if (!forceAudioTranscode)
                {
                    string audioCodec = VideoExportFfmpegDiagnostics.GetCodecName(inAudioStream->codecpar->codec_id);
                    throw new InvalidOperationException(
                        $"출력 컨테이너가 원본 오디오 코덱({audioCodec})을 지원하지 않아 " +
                        "무손실 복사를 수행할 수 없습니다.");
                }
                else
                {
                    if (VideoAudioTranscodePolicy.TryInitAudioTranscode(inAudioStream, outFmt, out audioDec, out audioEnc, out outAudioStream, out swr, out audioFifo, out var audioError))
                    {
                        audioReencode = true;
                        string audioCodec = VideoExportFfmpegDiagnostics.GetCodecName(inAudioStream->codecpar->codec_id);
                        audioNotice = forceAudioTranscode
                            ? $"오디오 코덱({audioCodec})이 출력 복사 경로를 사용하지 않도록 강제 재인코딩하여 AAC로 변환합니다."
                            : $"오디오 코덱({audioCodec})을 출력 컨테이너가 지원하지 않아 AAC로 변환합니다.";
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            $"오디오 변환 초기화 실패로 원본 소리를 보존할 수 없습니다: {audioError}");
                    }
                }

                for (int audioPosition = 1; audioPosition < audioStreamIndices.Count; audioPosition++)
                {
                    int extraAudioStreamIndex = audioStreamIndices[audioPosition];
                    AVStream* extraInputStream = inFmt->streams[extraAudioStreamIndex];
                    int extraSupported = outFmt->oformat != null
                        ? ffmpeg.avformat_query_codec(outFmt->oformat, extraInputStream->codecpar->codec_id, 0)
                        : 0;
                    if (extraSupported <= 0)
                    {
                        string audioCodec = VideoExportFfmpegDiagnostics.GetCodecName(extraInputStream->codecpar->codec_id);
                        throw new InvalidOperationException(
                            $"추가 오디오 트랙 {audioPosition + 1}의 원본 코덱({audioCodec})을 " +
                            "출력 컨테이너에 무손실로 보존할 수 없습니다.");
                    }

                    AVStream* extraOutputStream = ffmpeg.avformat_new_stream(outFmt, null);
                    if (extraOutputStream == null)
                    {
                        throw new InvalidOperationException(
                            $"추가 오디오 트랙 {audioPosition + 1}의 출력 스트림을 생성하지 못했습니다.");
                    }

                    VideoExportFfmpegDiagnostics.Throw(ffmpeg.avcodec_parameters_copy(extraOutputStream->codecpar, extraInputStream->codecpar));
                    extraOutputStream->codecpar->codec_tag = 0;
                    extraOutputStream->time_base = extraInputStream->time_base;
                    VideoPresentationMetadataPolicy.CopyStreamPresentationMetadata(extraInputStream, extraOutputStream);
                    copiedStreams.Add(
                        extraAudioStreamIndex,
                        new StreamCopyState(extraAudioStreamIndex, extraInputStream, extraOutputStream));
                }

                if (audioStreamIndices.Count > 1)
                {
                    string multiAudioNotice = $"원본 오디오 트랙 {audioStreamIndices.Count}개를 모두 보존합니다.";
                    audioNotice = string.IsNullOrWhiteSpace(audioNotice)
                        ? multiAudioNotice
                        : $"{audioNotice} {multiAudioNotice}";
                }
            }
            else
            {
                audioNotice = "입력 영상에 오디오 스트림이 없습니다.";
            }

            if (inAudioStream != null && outAudioStream != null)
                VideoPresentationMetadataPolicy.CopyStreamPresentationMetadata(inAudioStream, outAudioStream);

            if (progress != null)
            {
                string status = !string.IsNullOrWhiteSpace(exportNotice)
                    ? exportNotice
                    : "내보내기 시작...";
                if (!string.IsNullOrWhiteSpace(audioNotice))
                    status = $"{status} {audioNotice}";
                progress.Report(new ExportProgress(0, totalFrames, status));
            }

            bool allowHybridWithAudio = inAudioStream == null || audioCopy;
            if (!allowHybridWithAudio && allowHybridCopyCurrent)
            {
                if (!hybridCopyAttempted)
                    hybridCopyAttempted = true;

                string audioReason =
                    $"오디오 재인코딩 경로에서 하이브리드 비사용 (audioStreamPresent={inAudioStream != null}, audioCopy={audioCopy}, audioReencode={audioReencode})";
                hybridCopyFallbackReason = string.IsNullOrWhiteSpace(hybridCopyFallbackReason)
                    ? audioReason
                    : $"{hybridCopyFallbackReason}; {audioReason}";
            }

            if (allowHybridCopyCurrent && dec != null && dec->has_b_frames > 0)
            {
                if (hybridCopyAttempted)
                {
                    hybridCopyFallbackReason = string.IsNullOrWhiteSpace(hybridCopyFallbackReason)
                        ? $"디코더 B-프레임이 존재해 하이브리드 구간 복사를 비활성화했습니다 (has_b_frames={dec->has_b_frames})"
                        : $"{hybridCopyFallbackReason}; 디코더 B-프레임이 존재해 하이브리드 구간 복사를 비활성화했습니다 (has_b_frames={dec->has_b_frames})";
                }
                else
                {
                    hybridCopyFallbackReason =
                        $"디코더 B-프레임이 존재해 하이브리드 구간 복사를 비활성화했습니다 (has_b_frames={dec->has_b_frames})";
                }

                hybridCopyAttempted = true;
            }

            bool useHybridCopyWindow =
                allowHybridCopyCurrent &&
                allowHybridWithAudio &&
                hybridEncodeWindow.HasValue &&
                enc != null &&
                encoder != null &&
                enc->codec_id == inStream->codecpar->codec_id &&
                !VideoEncoderSelectionPolicy.IsHardwareEncoder(encoder) &&
                (hybridEncodeWindow.Value.Start > 0 ||
                 hybridEncodeWindow.Value.EndExclusive < totalFrames);

            if (allowHybridCopyCurrent && dec != null && dec->has_b_frames > 0)
            {
                useHybridCopyWindow = false;
            }

            if (useHybridCopyWindow &&
                hybridCandidateKeyframes != null &&
                hybridCandidateKeyframes.Count > 0 &&
                hybridEncodeWindow.HasValue)
            {
                var window = hybridEncodeWindow.Value;
                bool validStartBoundary =
                    window.Start <= 0 ||
                    VideoExportFrameRangePolicy.IsKeyframeFrameIndex(hybridCandidateKeyframes, window.Start);
                bool validEndBoundary =
                    window.EndExclusive >= totalFrames ||
                    VideoExportFrameRangePolicy.IsKeyframeFrameIndex(hybridCandidateKeyframes, window.EndExclusive);
                if (!validStartBoundary || !validEndBoundary)
                {
                    useHybridCopyWindow = false;
                    hybridCopyAttempted = true;
                    string boundaryReason =
                        $"키프레임 경계 미일치(start={window.Start}:{validStartBoundary}, end={window.EndExclusive}:{validEndBoundary})로 하이브리드 비활성화";
                    hybridCopyFallbackReason = string.IsNullOrWhiteSpace(hybridCopyFallbackReason)
                        ? boundaryReason
                        : $"{hybridCopyFallbackReason}; {boundaryReason}";
                }
            }

            AVStream* outStream = ffmpeg.avformat_new_stream(outFmt, useHybridCopyWindow ? null : encoder);
            long sourceEncodedFrameStep = enc != null ? VideoExportTimingPolicy.GetVideoFrameStep(sourceFps, enc->time_base) : 1;
            long predictedHybridCopyFrameStep = useHybridCopyWindow
                ? (inStream->time_base.num > 0 && inStream->time_base.den > 0
                    ? VideoExportTimingPolicy.GetVideoFrameStep(sourceFps, inStream->time_base)
                    : VideoExportTimingPolicy.GetVideoFrameStep(sourceFps, enc->time_base))
                : 1;
            if (useHybridCopyWindow && predictedHybridCopyFrameStep > 0 && sourceEncodedFrameStep > 0
                && Math.Abs(sourceEncodedFrameStep - predictedHybridCopyFrameStep) > VideoHybridCopyPolicy.MaxHybridFrameStepTolerance)
            {
                useHybridCopyWindow = false;
                hybridCopyAttempted = true;
                string stepMismatchReason =
                    $"하이브리드 패킷 간격 불일치(encode={sourceEncodedFrameStep}, copy={predictedHybridCopyFrameStep}, sourceFps={sourceFps:0.###})";
                hybridCopyFallbackReason = string.IsNullOrWhiteSpace(hybridCopyFallbackReason)
                    ? stepMismatchReason
                    : $"{hybridCopyFallbackReason}; {stepMismatchReason}";
            }
            if (useHybridCopyWindow)
            {
                VideoExportFfmpegDiagnostics.Throw(ffmpeg.avcodec_parameters_copy(outStream->codecpar, inStream->codecpar));
                outStream->codecpar->codec_tag = 0;
                outStream->time_base = inStream->time_base.num > 0 && inStream->time_base.den > 0
                    ? inStream->time_base
                    : enc->time_base;
                hybridCopyVideoFrameStep = VideoExportTimingPolicy.GetVideoFrameStep(sourceFps, outStream->time_base);
                if (progress != null && hybridEncodeWindow.HasValue)
                {
                    var window = hybridEncodeWindow.Value;
                    progress.Report(new ExportProgress(
                        Math.Max(0, window.Start),
                        totalFrames,
                        $"구간 고속 내보내기: [{window.Start}..{window.EndExclusive}) 프레임만 인코딩합니다."));
                }
            }
            else
            {
                VideoExportFfmpegDiagnostics.Throw(ffmpeg.avcodec_parameters_from_context(outStream->codecpar, enc));
                outStream->time_base = enc->time_base;
            }
            VideoPresentationMetadataPolicy.CopyStreamPresentationMetadata(inStream, outStream);
            encodedPacketFrameStep = VideoExportTimingPolicy.GetVideoFrameStep(sourceFps, outStream->time_base);
            if (useHybridCopyWindow
                && Math.Abs(encodedPacketFrameStep - hybridCopyVideoFrameStep) > VideoHybridCopyPolicy.MaxHybridFrameStepTolerance)
            {
                useHybridCopyWindow = false;
                hybridCopyAttempted = true;
                string outStepMismatchReason =
                    $"하이브리드 출력 시점 간격 불일치(encodeOutput={encodedPacketFrameStep}, copy={hybridCopyVideoFrameStep}, sourceFps={sourceFps:0.###})";
                hybridCopyFallbackReason = string.IsNullOrWhiteSpace(hybridCopyFallbackReason)
                    ? outStepMismatchReason
                    : $"{hybridCopyFallbackReason}; {outStepMismatchReason}";
                VideoExportFfmpegDiagnostics.Throw(ffmpeg.avcodec_parameters_from_context(outStream->codecpar, enc));
                outStream->time_base = enc->time_base;
                encodedPacketFrameStep = VideoExportTimingPolicy.GetVideoFrameStep(sourceFps, outStream->time_base);
            }
            if (encodedPacketFrameStep <= 0)
                encodedPacketFrameStep = 1;
            if (hybridCopyVideoFrameStep <= 0)
                hybridCopyVideoFrameStep = encodedPacketFrameStep;

            for (int inputStreamIndex = 0; inputStreamIndex < inFmt->nb_streams; inputStreamIndex++)
            {
                if (inputStreamIndex == videoStreamIndex || audioStreamIndices.Contains(inputStreamIndex))
                    continue;

                AVStream* auxiliaryInputStream = inFmt->streams[inputStreamIndex];
                int supported = outFmt->oformat != null
                    ? ffmpeg.avformat_query_codec(
                        outFmt->oformat,
                        auxiliaryInputStream->codecpar->codec_id,
                        0)
                    : 0;
                if (supported < 0)
                {
                    string mediaType = VideoExportFfmpegDiagnostics.GetMediaTypeName(auxiliaryInputStream->codecpar->codec_type);
                    string codec = VideoExportFfmpegDiagnostics.GetCodecName(auxiliaryInputStream->codecpar->codec_id);
                    throw new InvalidOperationException(
                        $"출력 컨테이너에 {mediaType} 스트림 {inputStreamIndex + 1}의 " +
                        $"원본 코덱({codec})을 보존할 수 없습니다.");
                }

                AVStream* auxiliaryOutputStream = ffmpeg.avformat_new_stream(outFmt, null);
                if (auxiliaryOutputStream == null)
                {
                    string mediaType = VideoExportFfmpegDiagnostics.GetMediaTypeName(auxiliaryInputStream->codecpar->codec_type);
                    throw new InvalidOperationException(
                        $"{mediaType} 스트림 {inputStreamIndex + 1}의 출력 스트림을 생성하지 못했습니다.");
                }

                VideoExportFfmpegDiagnostics.Throw(ffmpeg.avcodec_parameters_copy(
                    auxiliaryOutputStream->codecpar,
                    auxiliaryInputStream->codecpar));
                auxiliaryOutputStream->codecpar->codec_tag = 0;
                auxiliaryOutputStream->time_base = auxiliaryInputStream->time_base;
                VideoPresentationMetadataPolicy.CopyStreamPresentationMetadata(auxiliaryInputStream, auxiliaryOutputStream);
                copiedStreams.Add(
                    inputStreamIndex,
                    new StreamCopyState(inputStreamIndex, auxiliaryInputStream, auxiliaryOutputStream));
            }

            if ((outFmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
                VideoExportFfmpegDiagnostics.Throw(ffmpeg.avio_open(&outFmt->pb, outputPath, ffmpeg.AVIO_FLAG_WRITE));

            VideoExportFfmpegDiagnostics.Throw(ffmpeg.avformat_write_header(outFmt, null));

            // Muxers may rewrite outStream->time_base while writing the header.
            // Packet gap checks must use the final output stream time base.
            long muxedOutputFrameStep = VideoExportTimingPolicy.GetVideoFrameStep(sourceFps, outStream->time_base);
            if (muxedOutputFrameStep <= 0)
                muxedOutputFrameStep = 1;
            encodedPacketFrameStep = muxedOutputFrameStep;
            if (useHybridCopyWindow)
                hybridCopyVideoFrameStep = muxedOutputFrameStep;

            // ───────── frames ─────────
            bgra->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
            bgra->width = dec->width;
            bgra->height = dec->height;
            VideoExportFfmpegDiagnostics.Throw(ffmpeg.av_frame_get_buffer(bgra, 32));

            encFrame->format = (int)enc->pix_fmt;
            encFrame->width = enc->width;
            encFrame->height = enc->height;
            VideoExportFfmpegDiagnostics.Throw(ffmpeg.av_frame_get_buffer(encFrame, 32));

            // Dynamic frame scaling reads matrix, range, and chroma location from each frame.
            swsDecToBgra = VideoFrameColorPolicy.CreateDynamicSwsContext("디코더-BGRA 변환");
            swsBgraToEnc = VideoFrameColorPolicy.CreateDynamicSwsContext("BGRA-인코더 변환");
            swsDecToEnc = VideoFrameColorPolicy.CreateDynamicSwsContext("디코더-인코더 변환");

            // ───────── main loop ─────────
            int encodeWindowStart = hybridEncodeWindow?.Start ?? 0;
            int encodeWindowEnd = hybridEncodeWindow?.EndExclusive ?? int.MaxValue;
            if (encodeWindowEnd <= encodeWindowStart)
            {
                useHybridCopyWindow = false;
                hybridCopyFallbackReason = "하이브리드 윈도우 유효 범위가 비정상적입니다";
            }
            if (hybridCopyAttempted && !useHybridCopyWindow && progress != null)
            {
                progress.Report(new ExportProgress(
                    0,
                    totalFrames,
                    string.IsNullOrWhiteSpace(hybridCopyFallbackReason)
                        ? "구간 복사 조건을 만족하지 않아 일반 내보내기로 진행합니다..."
                        : $"구간 복사 조건 미충족으로 일반 내보내기로 진행({hybridCopyFallbackReason})"));
            }
            if (hybridCopyAttempted && !useHybridCopyWindow && !string.IsNullOrWhiteSpace(hybridCopyFallbackReason))
            {
                Debug.WriteLine($"[VideoExport] 하이브리드 폴백 사유: {hybridCopyFallbackReason}");
            }

            int packetFrameFallback = 0;
            int lastReportedFrame = -1;
            bool videoFlushed = false;

            while (ffmpeg.av_read_frame(inFmt, pkt) >= 0)
            {
                VideoPresentationMetadataPolicy.EnsureContainerStructureSupported(inFmt);
                if (copiedStreams.TryGetValue(pkt->stream_index, out StreamCopyState? copyState))
                {
                    ffmpeg.av_packet_rescale_ts(
                        pkt,
                        copyState.InputStream->time_base,
                        copyState.OutputStream->time_base);
                    if (copyState.ShouldRepairMissingTimestamps)
                    {
                        _ = VideoExportTimingPolicy.NormalizeCopiedPacketTimestamps(
                            pkt,
                            ref copyState.LastPacketPts,
                            ref copyState.HasLastPacketPts,
                            ref copyState.LastPacketDts,
                            ref copyState.HasLastPacketDts);
                    }
                    pkt->stream_index = copyState.OutputStream->index;
                    pkt->pos = -1;
                    VideoExportFfmpegDiagnostics.Throw(ffmpeg.av_interleaved_write_frame(outFmt, pkt));
                    ffmpeg.av_packet_unref(pkt);
                    continue;
                }

                if (pkt->stream_index == audioStreamIndex)
                {
                    if (audioReencode && audioDec != null && audioEnc != null && swr != null && audioFifo != null)
                    {
                        int sendErr = ffmpeg.avcodec_send_packet(audioDec, pkt);
                        ffmpeg.av_packet_unref(pkt);
                        if (sendErr < 0)
                            VideoExportFfmpegDiagnostics.Throw(sendErr);

                        while (ffmpeg.avcodec_receive_frame(audioDec, audioFrame) == 0)
                        {
                            VideoAudioTranscodePolicy.ConvertAndQueueAudioFrame(audioFrame, audioDec, audioEnc, swr, audioFifo, audioConvFrame);
                            VideoAudioTranscodePolicy.DrainAudioFifo(
                                audioFifo,
                                audioEnc,
                                outAudioStream,
                                outFmt,
                                audioPkt,
                                audioEncFrame,
                                ref audioPts,
                                ref lastAudioEncPacketPts,
                                ref hasLastAudioEncPacketPts,
                                ref lastAudioEncPacketDts,
                                ref hasLastAudioEncPacketDts,
                                flush: false);
                            ffmpeg.av_frame_unref(audioFrame);
                        }
                        continue;
                    }

                    ffmpeg.av_packet_unref(pkt);
                    continue;
                }

                if (pkt->stream_index != videoStreamIndex)
                {
                    ffmpeg.av_packet_unref(pkt);
                    continue;
                }
                inputVideoPacketCount++;

                int resolvedPacketFrameIndex = VideoExportTimingPolicy.ResolveFrameIndexFromPacket(
                    pkt,
                    inStream->time_base,
                    sourceFps,
                    packetFrameFallback,
                    totalFrames,
                    ref packetTimestampBase,
                    ref hasPacketTimestampBase);

                int packetFrameIndex = packetFrameFallback;
                bool isPacketFrameIndexReliable =
                    Math.Abs(resolvedPacketFrameIndex - packetFrameFallback) <= 2;
                if (isPacketFrameIndexReliable)
                {
                    packetFrameIndex = resolvedPacketFrameIndex;
                    packetFrameIndexReliabilityFailureCount = 0;
                }
                else
                {
                    packetFrameIndexReliabilityFailureCount++;
                    if (useHybridCopyWindow && packetFrameIndexReliabilityFailureCount >= VideoHybridCopyPolicy.MaxHybridPacketFrameIndexUnreliableSequence)
                    {
                        shouldRetryWithFullEncode = true;
                        packetDropFallbackReason =
                            string.IsNullOrWhiteSpace(packetDropFallbackReason)
                                ? $"hybrid-frame-index-unstable frame={resolvedPacketFrameIndex}, fallback={packetFrameFallback}, failures={packetFrameIndexReliabilityFailureCount}"
                                : $"{packetDropFallbackReason}; hybrid-frame-index-unstable frame={resolvedPacketFrameIndex}, fallback={packetFrameFallback}, failures={packetFrameIndexReliabilityFailureCount}";
                        throw new InvalidOperationException(
                            "Invalid argument: 하이브리드 경로에서 프레임 인덱스 신뢰 구간을 초과해 rollback 합니다.");
                    }
                }

                if (packetFrameIndex < packetFrameFallback)
                    packetFrameIndex = packetFrameFallback;
                packetFrameFallback = packetFrameIndex + 1;
                if (packetFrameIndex < exportSampleWindowFrames)
                    sampleSourceVideoPacketCount++;

                bool packetInEncodeWindow =
                    !useHybridCopyWindow ||
                    (packetFrameIndex >= encodeWindowStart && packetFrameIndex < encodeWindowEnd);

                if (useHybridCopyWindow)
                {
                    if (!wasLastPacketEncoded && packetInEncodeWindow)
                    {
                        if ((pkt->flags & ffmpeg.AV_PKT_FLAG_KEY) == 0)
                        {
                            shouldRetryWithFullEncode = true;
                            packetDropFallbackReason =
                                string.IsNullOrWhiteSpace(packetDropFallbackReason)
                                    ? $"hybrid-encode-entry-not-keyframe frame={packetFrameIndex}"
                                    : $"{packetDropFallbackReason}; hybrid-encode-entry-not-keyframe frame={packetFrameIndex}";
                            throw new InvalidOperationException(
                                "Invalid argument: 하이브리드 인코딩 구간 진입 패킷이 키프레임이 아닙니다.");
                        }
                    }

                    if (wasLastPacketEncoded && !packetInEncodeWindow)
                    {
                        if ((pkt->flags & ffmpeg.AV_PKT_FLAG_KEY) == 0)
                        {
                            shouldRetryWithFullEncode = true;
                            packetDropFallbackReason =
                                string.IsNullOrWhiteSpace(packetDropFallbackReason)
                                    ? $"hybrid-copy-exit-not-keyframe frame={packetFrameIndex}"
                                    : $"{packetDropFallbackReason}; hybrid-copy-exit-not-keyframe frame={packetFrameIndex}";
                            throw new InvalidOperationException(
                                "Invalid argument: 하이브리드 인코딩 구간 종료 패킷이 키프레임이 아닙니다.");
                        }
                    }

                    if (hasLastPacketFrameForHybrid && packetFrameIndex < lastPacketFrameIndexForHybrid)
                    {
                        shouldRetryWithFullEncode = true;
                        packetDropFallbackReason = $"hybrid-frame-backstep prev={lastPacketFrameIndexForHybrid}, curr={packetFrameIndex}";
                        throw new InvalidOperationException(
                            "Invalid argument: 하이브리드 경로에서 패킷 프레임 인덱스가 역행해 fallback 합니다.");
                    }

                    if (hasLastPacketFrameForHybrid &&
                        packetFrameIndex - lastPacketFrameIndexForHybrid > VideoHybridCopyPolicy.MaxHybridFrameGapBeforeFallback)
                    {
                        shouldRetryWithFullEncode = true;
                        packetDropFallbackReason =
                            string.IsNullOrWhiteSpace(packetDropFallbackReason)
                                ? $"hybrid-frame-jump prev={lastPacketFrameIndexForHybrid}, curr={packetFrameIndex}, gap={packetFrameIndex - lastPacketFrameIndexForHybrid}"
                                : $"{packetDropFallbackReason}; hybrid-frame-jump prev={lastPacketFrameIndexForHybrid}, curr={packetFrameIndex}, gap={packetFrameIndex - lastPacketFrameIndexForHybrid}";
                        throw new InvalidOperationException(
                            "Invalid argument: 하이브리드 경로에서 패킷 프레임 인덱스 점프가 과도하여 rollback 합니다.");
                    }

                    lastPacketFrameIndexForHybrid = packetFrameIndex;
                    hasLastPacketFrameForHybrid = true;
                }

                bool flushedForHybridBoundary = false;
                if (useHybridCopyWindow && packetInEncodeWindow != wasLastPacketEncoded)
                {
                    hybridModeTransitionCount++;
                    if (hybridModeTransitionCount > VideoHybridCopyPolicy.MaxHybridCopyModeTransitionsBeforeFallback)
                    {
                        shouldRetryWithFullEncode = true;
                        packetDropFallbackReason = $"hybrid-transition-unstable={hybridModeTransitionCount}, frame={packetFrameIndex}";
                        throw new InvalidOperationException(
                            "Invalid argument: 하이브리드 경로 전환이 비정상적으로 반복되어 fallback 합니다.");
                    }

                    if (hasLastEncodedPacketPts || hasLastEncodedPacketDts || hasLastVideoCopyPacketPts || hasLastVideoCopyPacketDts)
                        hybridModeTimestampSyncCount++;

                    if (packetInEncodeWindow)
                    {
                        if (!hasLastVideoCopyPacketPts || !hasLastVideoCopyPacketDts)
                        {
                            shouldRetryWithFullEncode = true;
                            packetDropFallbackReason =
                                string.IsNullOrWhiteSpace(packetDropFallbackReason)
                                    ? "hybrid-encode-entry-no-copy-state"
                                    : $"{packetDropFallbackReason}; hybrid-encode-entry-no-copy-state";
                            throw new InvalidOperationException(
                                "Invalid argument: 하이브리드 인코딩 진입 시 복사 상태 기준이 없어 rollback 합니다.");
                        }

                        if (hasLastVideoCopyPacketPts)
                        {
                            lastEncodedPacketPts = lastVideoCopyPacketPts;
                            hasLastEncodedPacketPts = true;
                        }
                        else
                        {
                            hasLastEncodedPacketPts = false;
                        }

                        if (hasLastVideoCopyPacketDts)
                        {
                            lastEncodedPacketDts = lastVideoCopyPacketDts;
                            hasLastEncodedPacketDts = true;
                        }
                        else
                        {
                            hasLastEncodedPacketDts = false;
                        }
                    }
                    else
                    {
                        if (packetFrameIndex >= encodeWindowEnd)
                        {
                            VideoFrameProcessingPolicy.FlushVideoPipeline(
                                _maskProvider,
                                _masked,
                                _staticHdrConfigured,
                                _losslessX264RgbConfigured,
                                _configuredHdrMetadata,
                                ref _directFaceBlurFrames,
                                ref _bitmapMaskBlurFrames,
                                ref _nativeYuvBlurFrames,
                                ref videoFlushed,
                                dec,
                                enc,
                                frame,
                                bgra,
                                encFrame,
                                swsDecToBgra,
                                swsBgraToEnc,
                                ref swsDecToEnc,
                                inStream,
                                outPkt,
                                outStream,
                                outFmt,
                                blurRadius,
                                blurRanges,
                                blurFrameSet,
                                appliedBlurFrameIndices,
                                ref blurRangeCursor,
                                sourceFps,
                                totalFrames,
                                encodeWindowStart,
                                encodeWindowEnd,
                                ref frameIndex,
                                ref swsToBgraMs,
                                ref maskMs,
                                ref swsToEncMs,
                                encodeTimer,
                                encoderFlushTimer,
                                ref lastEncodedPts,
                                ref hasLastEncodedPts,
                                ref lastEncodedPacketPts,
                                ref hasLastEncodedPacketPts,
                                ref lastEncodedPacketDts,
                                ref hasLastEncodedPacketDts,
                                ref outputVideoPacketCount,
                                encodedTimestampIntegrity,
                                progress,
                                ref lastReportedFrame,
                                swTotal,
                                cancellationToken,
                                exportSampleWindowFrames,
                                ref sampleEncodedFrameCount,
                                ref sampleBlurredFrameCount,
                                ref encodedWindowFrameCount,
                                ref submittedVideoFrameCount,
                                submittedEncodedFramePts,
                                submittedEncodedFrameDurations,
                                emittedEncodedFramePts,
                                emittedEncodedMuxPts,
                                encodedPacketFrameStep);

                            flushedForHybridBoundary = true;
                        }

                        if (!hasLastEncodedPacketPts || !hasLastEncodedPacketDts)
                        {
                            shouldRetryWithFullEncode = true;
                            packetDropFallbackReason =
                                string.IsNullOrWhiteSpace(packetDropFallbackReason)
                                    ? "hybrid-copy-exit-no-encoded-state"
                                    : $"{packetDropFallbackReason}; hybrid-copy-exit-no-encoded-state";
                            throw new InvalidOperationException(
                                "Invalid argument: 하이브리드 복사 진입 시 인코더 상태 기준이 없어 rollback 합니다.");
                        }

                        if (hasLastEncodedPacketPts)
                        {
                            lastVideoCopyPacketPts = lastEncodedPacketPts;
                            hasLastVideoCopyPacketPts = true;
                        }
                        else
                        {
                            hasLastVideoCopyPacketPts = false;
                        }

                        if (hasLastEncodedPacketDts)
                        {
                            lastVideoCopyPacketDts = lastEncodedPacketDts;
                            hasLastVideoCopyPacketDts = true;
                        }
                        else
                        {
                            hasLastVideoCopyPacketDts = false;
                        }
                    }
                }

                if (!packetInEncodeWindow)
                {
                    copiedSourceVideoPacketCount++;
                    if (packetFrameIndex < exportSampleWindowFrames)
                        sampleCopiedVideoPacketCount++;

                    if (useHybridCopyWindow && packetFrameIndex >= encodeWindowEnd && !flushedForHybridBoundary)
                    {
                        VideoFrameProcessingPolicy.FlushVideoPipeline(
                            _maskProvider,
                            _masked,
                            _staticHdrConfigured,
                            _losslessX264RgbConfigured,
                            _configuredHdrMetadata,
                            ref _directFaceBlurFrames,
                            ref _bitmapMaskBlurFrames,
                            ref _nativeYuvBlurFrames,
                            ref videoFlushed,
                            dec,
                            enc,
                            frame,
                            bgra,
                            encFrame,
                            swsDecToBgra,
                            swsBgraToEnc,
                            ref swsDecToEnc,
                            inStream,
                            outPkt,
                            outStream,
                            outFmt,
                            blurRadius,
                            blurRanges,
                            blurFrameSet,
                            appliedBlurFrameIndices,
                            ref blurRangeCursor,
                            sourceFps,
                            totalFrames,
                            encodeWindowStart,
                            encodeWindowEnd,
                            ref frameIndex,
                            ref swsToBgraMs,
                            ref maskMs,
                            ref swsToEncMs,
                            encodeTimer,
                            encoderFlushTimer,
                            ref lastEncodedPts,
                            ref hasLastEncodedPts,
                            ref lastEncodedPacketPts,
                            ref hasLastEncodedPacketPts,
                            ref lastEncodedPacketDts,
                            ref hasLastEncodedPacketDts,
                            ref outputVideoPacketCount,
                            encodedTimestampIntegrity,
                            progress,
                            ref lastReportedFrame,
                            swTotal,
                            cancellationToken,
                            exportSampleWindowFrames,
                            ref sampleEncodedFrameCount,
                            ref sampleBlurredFrameCount,
                            ref encodedWindowFrameCount,
                            ref submittedVideoFrameCount,
                            submittedEncodedFramePts,
                            submittedEncodedFrameDurations,
                            emittedEncodedFramePts,
                            emittedEncodedMuxPts,
                            encodedPacketFrameStep);

                        if (hasLastEncodedPacketPts)
                        {
                            lastVideoCopyPacketPts = lastEncodedPacketPts;
                            hasLastVideoCopyPacketPts = true;
                        }
                        else
                        {
                            hasLastVideoCopyPacketPts = false;
                        }

                        if (hasLastEncodedPacketDts)
                        {
                            lastVideoCopyPacketDts = lastEncodedPacketDts;
                            hasLastVideoCopyPacketDts = true;
                        }
                        else
                        {
                            hasLastVideoCopyPacketDts = false;
                        }
                    }

                    ffmpeg.av_packet_rescale_ts(pkt, inStream->time_base, outStream->time_base);
                    bool hasMissingTimestamps = pkt->pts == ffmpeg.AV_NOPTS_VALUE && pkt->dts == ffmpeg.AV_NOPTS_VALUE;
                    long previousPacketPts = lastVideoCopyPacketPts;
                    bool hasPreviousPacketPts = hasLastVideoCopyPacketPts;
                    bool timestampAdjusted = VideoExportTimingPolicy.NormalizeCopiedPacketTimestamps(
                        pkt,
                        ref lastVideoCopyPacketPts,
                        ref hasLastVideoCopyPacketPts,
                        ref lastVideoCopyPacketDts,
                        ref hasLastVideoCopyPacketDts,
                        hybridCopyVideoFrameStep);
                    if (hasPreviousPacketPts)
                    {
                        long copyGap = Math.Abs(pkt->pts - previousPacketPts);
                        long expectedGap = Math.Max(1, hybridCopyVideoFrameStep);
                        long copyGapJitterTolerance = Math.Max(1, expectedGap / VideoHybridCopyPolicy.MaxHybridCopyPtsJitterDivisor);
                        long copyGapThreshold = expectedGap + copyGapJitterTolerance;
                        if (copyGap > copyGapThreshold)
                        {
                            copyGapOutlierCount++;
                            if (copyGap > maxCopyGap)
                                maxCopyGap = copyGap;
                        }
                    }
                    if (timestampAdjusted && string.IsNullOrWhiteSpace(packetDropFallbackReason))
                    {
                        packetDropFallbackReason = hasMissingTimestamps
                            ? $"missing-ts-frame={packetFrameIndex}"
                            : $"copy-ts-adjust-frame={packetFrameIndex}";
                    }
                    if (timestampAdjusted)
                    {
                        copyTimestampFixCount++;
                        if (copyTimestampFixCount > VideoHybridCopyPolicy.MaxHybridCopyTimestampFixBeforeFallback)
                        {
                            packetDropFallbackReason = $"copy-ts-fix={copyTimestampFixCount}, frame={packetFrameIndex}";
                            throw new InvalidOperationException(
                                "Invalid argument: 하이브리드 복사 타임스탬프 보정 횟수가 임계값을 초과해 fallback합니다.");
                        }
                    }

                    pkt->stream_index = outStream->index;
                    pkt->pos = -1;
                    VideoExportFfmpegDiagnostics.Throw(ffmpeg.av_interleaved_write_frame(outFmt, pkt));
                    outputVideoPacketCount++;
                    copiedVideoPacketCount++;
                    hasLastVideoCopyPacketPts = true;
                    hasLastVideoCopyPacketDts = true;
                    lastVideoCopyPacketPts = pkt->pts;
                    lastVideoCopyPacketDts = pkt->dts;
                    VideoExportProgressPolicy.ReportVideoProgress(progress, totalFrames, ref lastReportedFrame, packetFrameIndex);
                    ffmpeg.av_packet_unref(pkt);
                    wasLastPacketEncoded = false;
                    continue;
                }

                wasLastPacketEncoded = true;
                encodedSourceVideoPacketCount++;
                VideoExportFfmpegDiagnostics.Throw(ffmpeg.avcodec_send_packet(dec, pkt));
                ffmpeg.av_packet_unref(pkt);
                int videoReceiveResult;
                while ((videoReceiveResult = ffmpeg.avcodec_receive_frame(dec, frame)) == 0)
                {
                    VideoFrameProcessingPolicy.ProcessDecodedVideoFrame(
                        _maskProvider,
                        _masked,
                        _staticHdrConfigured,
                        _losslessX264RgbConfigured,
                        _configuredHdrMetadata,
                        ref _directFaceBlurFrames,
                        ref _bitmapMaskBlurFrames,
                        ref _nativeYuvBlurFrames,
                        frame,
                        bgra,
                        encFrame,
                        enc,
                        swsDecToBgra,
                        swsBgraToEnc,
                        ref swsDecToEnc,
                        inStream,
                        outPkt,
                        outStream,
                        outFmt,
                        blurRadius,
                        blurRanges,
                        blurFrameSet,
                        appliedBlurFrameIndices,
                        ref blurRangeCursor,
                        sourceFps,
                        totalFrames,
                        encodeWindowStart,
                        encodeWindowEnd,
                        ref frameIndex,
                        ref swsToBgraMs,
                        ref maskMs,
                        ref swsToEncMs,
                        encodeTimer,
                        ref lastEncodedPts,
                        ref hasLastEncodedPts,
                        ref lastEncodedPacketPts,
                        ref hasLastEncodedPacketPts,
                        ref lastEncodedPacketDts,
                        ref hasLastEncodedPacketDts,
                        ref outputVideoPacketCount,
                        encodedTimestampIntegrity,
                        progress,
                        ref lastReportedFrame,
                        swTotal,
                        cancellationToken,
                        exportSampleWindowFrames,
                        ref sampleEncodedFrameCount,
                        ref sampleBlurredFrameCount,
                        ref encodedWindowFrameCount,
                        ref submittedVideoFrameCount,
                        submittedEncodedFramePts,
                        submittedEncodedFrameDurations,
                        emittedEncodedFramePts,
                        emittedEncodedMuxPts,
                        encodedPacketFrameStep);
                    ffmpeg.av_frame_unref(frame);
                }

                int tryAgain = ffmpeg.AVERROR(ffmpeg.EAGAIN);
                if (videoReceiveResult != tryAgain && videoReceiveResult != ffmpeg.AVERROR_EOF)
                {
                    throw new VideoExportIntegrityException(
                        $"비디오 디코딩 중 오류가 발생했습니다: {VideoExportFfmpegDiagnostics.GetErrorMessage(videoReceiveResult)}");
                }
            }

            progress?.Report(new ExportProgress(
                totalFrames,
                totalFrames,
                "인코더 버퍼를 마무리하는 중..."));
            VideoFrameProcessingPolicy.FlushVideoPipeline(
                _maskProvider,
                _masked,
                _staticHdrConfigured,
                _losslessX264RgbConfigured,
                _configuredHdrMetadata,
                ref _directFaceBlurFrames,
                ref _bitmapMaskBlurFrames,
                ref _nativeYuvBlurFrames,
                ref videoFlushed,
                dec,
                enc,
                frame,
                bgra,
                encFrame,
                swsDecToBgra,
                swsBgraToEnc,
                ref swsDecToEnc,
                inStream,
                outPkt,
                outStream,
                outFmt,
                blurRadius,
                blurRanges,
                blurFrameSet,
                appliedBlurFrameIndices,
                ref blurRangeCursor,
                sourceFps,
                totalFrames,
                encodeWindowStart,
                encodeWindowEnd,
                ref frameIndex,
                ref swsToBgraMs,
                ref maskMs,
                ref swsToEncMs,
                encodeTimer,
                encoderFlushTimer,
                ref lastEncodedPts,
                ref hasLastEncodedPts,
                ref lastEncodedPacketPts,
                ref hasLastEncodedPacketPts,
                ref lastEncodedPacketDts,
                ref hasLastEncodedPacketDts,
                ref outputVideoPacketCount,
                encodedTimestampIntegrity,
                progress,
                ref lastReportedFrame,
                swTotal,
                cancellationToken,
                exportSampleWindowFrames,
                ref sampleEncodedFrameCount,
                ref sampleBlurredFrameCount,
                ref encodedWindowFrameCount,
                ref submittedVideoFrameCount,
                submittedEncodedFramePts,
                submittedEncodedFrameDurations,
                emittedEncodedFramePts,
                emittedEncodedMuxPts,
                encodedPacketFrameStep);

            int[] missingBlurFrameIndices = VideoExportFrameRangePolicy.GetMissingExpectedFrameIndices(
                blurFrameSet,
                appliedBlurFrameIndices);
            if (missingBlurFrameIndices.Length > 0)
            {
                string sample = string.Join(",", missingBlurFrameIndices.AsSpan(0, Math.Min(8, missingBlurFrameIndices.Length)).ToArray());
                throw new VideoExportIntegrityException(
                    $"자동 모자이크 적용 누락이 감지되었습니다 " +
                    $"(missing={missingBlurFrameIndices.Length}, frames={sample}).");
            }

            if (audioReencode && audioDec != null && audioEnc != null && swr != null && audioFifo != null)
            {
                VideoAudioTranscodePolicy.FlushAudioTranscode(
                    audioDec,
                    audioEnc,
                    outAudioStream,
                    outFmt,
                    swr,
                    audioFifo,
                    audioFrame,
                    audioConvFrame,
                    audioEncFrame,
                    audioPkt,
                    ref audioPts,
                    ref lastAudioEncPacketPts,
                    ref hasLastAudioEncPacketPts,
                    ref lastAudioEncPacketDts,
                    ref hasLastAudioEncPacketDts);
            }

            progress?.Report(new ExportProgress(
                totalFrames,
                totalFrames,
                "출력 파일을 마무리하는 중..."));
            VideoExportFfmpegDiagnostics.Throw(ffmpeg.av_write_trailer(outFmt));
            outputCloseTimer.Start();
            VideoExportCopyPolicy.CloseOutputOrThrow(outFmt);
            outputCloseTimer.Stop();
            var finalIntegrity = VideoExportIntegrityPolicy.Evaluate(
                totalFrames,
                exportSampleWindowFrames,
                copiedSourceVideoPacketCount,
                copiedVideoPacketCount,
                submittedEncodedFramePts,
                emittedEncodedFramePts,
                emittedEncodedMuxPts,
                useHybridCopyWindow,
                encodeWindowStart,
                encodeWindowEnd,
                encodedWindowFrameCount,
                outputVideoPacketCount,
                encodedTimestampIntegrity,
                allowPacketDropRetry,
                inputVideoPacketCount,
                submittedVideoFrameCount,
                copyGapOutlierCount,
                maxCopyGap,
                packetDropFallbackReason);
            shouldRetryWithFullEncode = finalIntegrity.ShouldRetryWithFullEncode;
            packetDropFallbackReason = finalIntegrity.PacketDropFallbackReason;
            int sampleWindowLimit = finalIntegrity.SampleWindowLimit;
            int sampleWindowSourceFrames = finalIntegrity.SampleWindowSourceFrames;
            int sampleWindowProducedFrames = finalIntegrity.SampleWindowProducedFrames;
            int sampleWindowFrameShortfall = finalIntegrity.SampleWindowFrameShortfall;
            int expectedHybridWindowEncodedFrames = finalIntegrity.ExpectedHybridWindowEncodedFrames;
            int encodedWindowFrameShortfall = finalIntegrity.EncodedWindowFrameShortfall;
            int outputPacketPtsGapOutlierCountFinal = finalIntegrity.OutputPacketPtsGapOutlierCount;
            long maxOutputPacketPtsGapFinal = finalIntegrity.MaxOutputPacketPtsGap;
            int missingEncodedFrameCount = finalIntegrity.MissingEncodedFrameCount;
            int unexpectedEncodedFrameCount = finalIntegrity.UnexpectedEncodedFrameCount;
            int videoFrameCoverageMismatch = finalIntegrity.VideoFrameCoverageMismatch;
            int videoFrameDropCount = finalIntegrity.VideoFrameDropCount;
            int outputPacketCountMismatch = finalIntegrity.OutputPacketCountMismatch;
            int droppedVideoPacketsForSummary = finalIntegrity.DroppedVideoPacketsForSummary;
            string? packetLossFallbackReason = finalIntegrity.PacketLossFallbackReason;
            outputPacketPtsGapOutlierCount = outputPacketPtsGapOutlierCountFinal;
            maxOutputPacketPtsGap = maxOutputPacketPtsGapFinal;
            LastExportSummary = new ExportRunSummary(
                frameIndex,
                _bitmapMaskBlurFrames,
                _directFaceBlurFrames,
                swsToBgraMs,
                maskMs,
                swsToEncMs,
                encodeTimer.ElapsedMilliseconds,
                swTotal.ElapsedMilliseconds,
                runId,
                exportMode,
                hybridCopyAttempted,
                useHybridCopyWindow,
                useHybridCopyWindow
                    ? null
                    : hybridCopyAttempted
                        ? hybridCopyFallbackReason
                        : null,
                HybridWindowStartFrame: hybridWindowStartFrame,
                HybridWindowEndFrame: hybridWindowEndFrame,
                HybridModeTransitionCount: hybridModeTransitionCount,
                HybridModeTimestampSyncCount: hybridModeTimestampSyncCount,
                InputVideoPackets: inputVideoPacketCount,
                OutputVideoPackets: outputVideoPacketCount,
                CopiedVideoPackets: copiedVideoPacketCount,
                CopiedSourceVideoPackets: copiedSourceVideoPacketCount,
                EncodedSourceVideoPackets: encodedSourceVideoPacketCount,
                DroppedVideoPackets: droppedVideoPacketsForSummary,
                OutputPacketPtsGapOutlierCount: outputPacketPtsGapOutlierCount,
                MaxOutputPacketPtsGap: maxOutputPacketPtsGap,
                HybridCopyTimestampFixCount: copyTimestampFixCount,
                HybridEncodedPacketFrameStep: encodedPacketFrameStep,
                HybridCopyPacketFrameStep: hybridCopyVideoFrameStep,
                PacketLossFallbackReason: packetLossFallbackReason,
                ForceSoftwareEncoder: forceSoftwareEncoder,
                ForceSafeEncoding: forceSafeEncoding,
                ForceAudioTranscode: forceAudioTranscode,
                ForceH264Fallback: forceH264Fallback,
                HybridWindowExpectedEncodedFrames: expectedHybridWindowEncodedFrames,
                HybridWindowEncodedFrames: encodedWindowFrameCount,
                HybridWindowFrameShortfall: encodedWindowFrameShortfall,
                SampleWindowSourceFrames: sampleWindowSourceFrames,
                SampleWindowProducedFrames: sampleWindowProducedFrames,
                SampleWindowFrameShortfall: sampleWindowFrameShortfall,
                OutputPacketCountMismatch: outputPacketCountMismatch,
                MissingVideoPacketTimestampCount: encodedTimestampIntegrity.MissingPacketTimestamps,
                VideoPacketTimestampAdjustmentCount: encodedTimestampIntegrity.PacketTimestampAdjustments,
                EncoderName: VideoExportFfmpegDiagnostics.GetEncoderName(encoder),
                EncoderQualityMode: encoderQualityConfiguration.Mode,
                EncoderOptionsApplied: encoderQualityConfiguration.AppliedOptions,
                EncoderOptionFailures: encoderQualityConfiguration.FailedOptions,
                SourcePixelFormat: VideoExportFidelityPolicy.GetPixelFormatName(dec->pix_fmt),
                OutputPixelFormat: VideoExportFidelityPolicy.GetPixelFormatName(enc->pix_fmt),
                SourceBitDepth: VideoExportFidelityPolicy.GetPixelFormatBitDepth(dec->pix_fmt),
                OutputBitDepth: VideoExportFidelityPolicy.GetPixelFormatBitDepth(enc->pix_fmt),
                SourceVideoBitrate: VideoExportFidelityPolicy.ResolveSourceVideoBitrate(inStream, dec),
                TargetVideoBitrate: VideoExportFidelityPolicy.ResolveTargetVideoBitrateForSummary(encoder, inStream, dec, enc),
                NativeYuvBlurFrames: _nativeYuvBlurFrames,
                EncoderFlushMs: encoderFlushTimer.ElapsedMilliseconds,
                OutputCloseMs: outputCloseTimer.ElapsedMilliseconds,
                SubmittedVideoFrames: submittedVideoFrameCount,
                VideoFrameCoverageMismatch: videoFrameCoverageMismatch,
                MissingEncodedVideoFrames: missingEncodedFrameCount,
                UnexpectedEncodedVideoFrames: unexpectedEncodedFrameCount,
                VideoFrameDropCount: videoFrameDropCount,
                ExpectedBlurFrames: blurFrameSet?.Count ?? 0,
                AppliedBlurFrames: appliedBlurFrameIndices.Count);
            Debug.WriteLine(
                $"[Export] done frames={frameIndex}, bitmapMaskFrames={_bitmapMaskBlurFrames}, directFaceFrames={_directFaceBlurFrames}, swsToBgraMs={swsToBgraMs}, maskMs={maskMs}, swsToEncMs={swsToEncMs}, encodeMs={encodeTimer.ElapsedMilliseconds}, encoderFlushMs={encoderFlushTimer.ElapsedMilliseconds}, totalMs={swTotal.ElapsedMilliseconds}");
            Debug.WriteLine(
                $"[Export] sampleWindow={(sampleWindowLimit > 0 ? $"0-{sampleWindowLimit - 1}" : "none")} sourcePackets={sampleSourceVideoPacketCount} copiedPackets={sampleCopiedVideoPacketCount} encodedFrames={sampleEncodedFrameCount} blurredFrames={sampleBlurredFrameCount} sampleShortfall={sampleWindowFrameShortfall}");
            Debug.WriteLine(LastExportSummary.ToLogLine());
            RunMetricsLog.AppendRunLines(LastExportSummary.RunId, LastExportSummary.ToLogLine());
            if (shouldRetryWithFullEncode)
            {
                Debug.WriteLine($"[Export] 패킷 누락 감지로 품질 보정 fallback 수행: {packetDropFallbackReason}");
            }
        }
        finally
        {
            if (swsDecToBgra != null) ffmpeg.sws_freeContext(swsDecToBgra);
            if (swsBgraToEnc != null) ffmpeg.sws_freeContext(swsBgraToEnc);
            if (swsDecToEnc != null) ffmpeg.sws_freeContext(swsDecToEnc);

            ffmpeg.av_frame_free(&frame);
            ffmpeg.av_frame_free(&bgra);
            ffmpeg.av_frame_free(&encFrame);
            ffmpeg.av_frame_free(&audioFrame);
            ffmpeg.av_frame_free(&audioConvFrame);
            ffmpeg.av_frame_free(&audioEncFrame);
            ffmpeg.av_packet_free(&pkt);
            ffmpeg.av_packet_free(&outPkt);
            ffmpeg.av_packet_free(&audioPkt);

            ffmpeg.avcodec_free_context(&dec);
            ffmpeg.avcodec_free_context(&enc);
            ffmpeg.avcodec_free_context(&audioDec);
            ffmpeg.avcodec_free_context(&audioEnc);
            if (swr != null) ffmpeg.swr_free(&swr);
            if (audioFifo != null) ffmpeg.av_audio_fifo_free(audioFifo);

            if (outFmt != null)
            {
                if ((outFmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0 && outFmt->pb != null)
                    _ = ffmpeg.avio_closep(&outFmt->pb);
                ffmpeg.avformat_free_context(outFmt);
            }

            if (inFmt != null)
                ffmpeg.avformat_close_input(&inFmt);
        }

        if (shouldRetryWithFullEncode)
        {
            ExportInternal(
                inputPath,
                outputPath,
                blurRadius,
                progress,
                cancellationToken,
                runId,
                exportMode: "fallback-full-encode",
                forceSoftwareEncoder: forceSoftwareEncoder,
                allowHybridCopy: false,
                forceSafeEncoding: forceSafeEncoding,
                forceAudioTranscode: forceAudioTranscode,
                forceH264Fallback: forceH264Fallback,
                allowPacketDropRetry: false);
        }
    }


}
