using Avalonia;
using Avalonia.Media.Imaging;
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
    private sealed class VideoEncoderException : InvalidOperationException
    {
        public VideoEncoderException(
            string message,
            int errorCode,
            string operation,
            string encoderName,
            bool isHardwareEncoder)
            : base(message)
        {
            ErrorCode = errorCode;
            Operation = operation;
            EncoderName = encoderName;
            IsHardwareEncoder = isHardwareEncoder;
        }

        public int ErrorCode { get; }
        public string Operation { get; }
        public string EncoderName { get; }
        public bool IsHardwareEncoder { get; }
    }

    private const bool EnableHybridCopyWindow = false;
    private const int MinHybridCopyFrames = 240;
    private const double MinHybridCopyRatio = 0.05;
    private const int MinHybridCopySideFrames = 24;
    private const int MaxHybridCopyTimestampFixBeforeFallback = 0;
    private const int MaxHybridCopyModeTransitionsBeforeFallback = 2;
    private const int MaxHybridFrameGapBeforeFallback = 32;
    private const double MaxEstimatedFrameCountSkewRatio = 1.25;
    private const int MaxEstimatedFrameCountSkewAbsolute = 1200;
    private const long MaxHybridFrameStepTolerance = 1;
    private const long MaxHybridCopyPtsJitterDivisor = 10;
    private const int MaxHybridPacketFrameIndexUnreliableSequence = 4;
    private const int ExportSampleWindowSeconds = 30;
    private const int MaxAllowedOutputPacketLoss = 0;
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
        string stagedOutputPath = BuildStagedOutputPath(finalOutputPath);
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
                    allowHybridCopy: allowHybridCopy && EnableHybridCopyWindow,
                    forceSafeEncoding: false,
                    forceAudioTranscode: false,
                    forceH264Fallback: false);
            }
            catch (InvalidOperationException ex) when (ShouldRetryWithSafeEncoding(ex))
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
                    ShouldRetryWithH264Fallback(nestedEx))
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
            TryDeleteStagedOutput(stagedOutputPath);
        }
    }

    private static string BuildStagedOutputPath(string finalOutputPath)
    {
        string directory = Path.GetDirectoryName(finalOutputPath) ?? Directory.GetCurrentDirectory();
        string name = Path.GetFileNameWithoutExtension(finalOutputPath);
        string extension = Path.GetExtension(finalOutputPath);
        return Path.Combine(
            directory,
            $".{name}.faceshield-{Guid.NewGuid():N}{extension}");
    }

    private static void TryDeleteStagedOutput(string stagedOutputPath)
    {
        try
        {
            if (File.Exists(stagedOutputPath))
                File.Delete(stagedOutputPath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Export] 임시 출력 정리 실패: {stagedOutputPath}, {ex.Message}");
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
        WriteableBitmap? reusableFaceMask = null;
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
            Throw(ffmpeg.avformat_open_input(&inFmt, inputPath, null, null));
            Throw(ffmpeg.avformat_find_stream_info(inFmt, null));
            EnsureContainerStructureSupported(inFmt);

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
            if (allowHybridCopyCurrent &&
                (inStream->time_base.den <= 0 ||
                 inStream->time_base.num <= 0 ||
                 sourceFps <= 0.0))
            {
                allowHybridCopyCurrent = false;
                hybridCopyAttempted = true;
                hybridCopyFallbackReason =
                    $"하이브리드 사용 보류(타임스탬프 기준 불안정): sourceFps={sourceFps:0.###}, timeBase={inStream->time_base.num}/{inStream->time_base.den}";
            }

            if (_maskProvider is FrameMaskProvider frameMaskProvider)
            {
                blurFrameSet = VideoExportFrameRangePolicy.BuildBlurFrameSet(frameMaskProvider);
                if (blurFrameSet.Count == 0)
                {
                    progress?.Report(new ExportProgress(0, totalFrames, "블러 대상이 없어 원본 스트림을 고속 복사합니다..."));
                    var remuxCounts = ExportByRemuxCopy(
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
                        SourcePixelFormat: GetPixelFormatName((AVPixelFormat)inStream->codecpar->format),
                        OutputPixelFormat: GetPixelFormatName((AVPixelFormat)inStream->codecpar->format),
                        SourceBitDepth: GetPixelFormatBitDepth((AVPixelFormat)inStream->codecpar->format),
                        OutputBitDepth: GetPixelFormatBitDepth((AVPixelFormat)inStream->codecpar->format),
                        SourceVideoBitrate: ResolveSourceVideoBitrate(inStream, null),
                        TargetVideoBitrate: ResolveSourceVideoBitrate(inStream, null),
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
                bool canCopyOutsideBlurWindow =
                    allowHybridCopyCurrent &&
                    blurRanges.Count > 0 &&
                    sourceFps > 0.0 &&
                    totalFrames > 0 &&
                    (blurRanges[0].Start > 0 || blurRanges[^1].EndExclusive < totalFrames);
                if (canCopyOutsideBlurWindow)
                {
                    var keyframes = CollectKeyframeFrameIndices(inputPath, sourceFps, totalFrames, out var estimatedTotalFrames);
                    hybridCandidateKeyframes = keyframes;
                    if (keyframes.Count < 2)
                    {
                        canCopyOutsideBlurWindow = false;
                        allowHybridCopyCurrent = false;
                        hybridCopyAttempted = true;
                        string keyframeInsufficientReason =
                            $"키프레임 후보가 불충분해 하이브리드 비활성 (keyframes={keyframes.Count}, reported={totalFrames}, estimated={estimatedTotalFrames})";
                        hybridCopyFallbackReason = string.IsNullOrWhiteSpace(hybridCopyFallbackReason)
                            ? keyframeInsufficientReason
                            : $"{hybridCopyFallbackReason}; {keyframeInsufficientReason}";
                    }

                    if (estimatedTotalFrames > 0)
                    {
                        int frameCountSkew = estimatedTotalFrames - totalFrames;
                        int absFrameCountSkew = Math.Abs(frameCountSkew);
                        bool disableHybridForUnstableFrameCount =
                            absFrameCountSkew > MaxEstimatedFrameCountSkewAbsolute
                            || estimatedTotalFrames > (int)(totalFrames * MaxEstimatedFrameCountSkewRatio)
                            || estimatedTotalFrames < (int)(totalFrames / MaxEstimatedFrameCountSkewRatio);

                        if (disableHybridForUnstableFrameCount && canCopyOutsideBlurWindow)
                        {
                            canCopyOutsideBlurWindow = false;
                            allowHybridCopyCurrent = false;
                            hybridCopyAttempted = true;
                            string unstableFrameCountReason =
                                $"키프레임 기반 총 프레임 추정치 불일치로 하이브리드 비활성 (estimated={estimatedTotalFrames}, reported={totalFrames}, delta={frameCountSkew})";
                            hybridCopyFallbackReason = string.IsNullOrWhiteSpace(hybridCopyFallbackReason)
                                ? unstableFrameCountReason
                                : $"{hybridCopyFallbackReason}; {unstableFrameCountReason}";
                        }
                        else if (estimatedTotalFrames > totalFrames && canCopyOutsideBlurWindow)
                        {
                            Debug.WriteLine(
                                $"[Export] detected frame count expansion from keyframe scan: meta={totalFrames}, estimated={estimatedTotalFrames}");
                            totalFrames = estimatedTotalFrames;
                        }
                    }
                    blurRanges = VideoExportFrameRangePolicy.AlignRangesToKeyframes(blurRanges, keyframes, totalFrames);
                    if (blurRanges.Count == 0)
                    {
                        canCopyOutsideBlurWindow = false;
                        hybridCopyFallbackReason = "키프레임 정렬 후 블러 구간이 비어 하이브리드 후보가 사라짐";
                    }
                    else
                    {
                        int encodeStart = blurRanges[0].Start;
                        int encodeEnd = blurRanges[blurRanges.Count - 1].EndExclusive;
                        int copiedFrames = totalFrames - (encodeEnd - encodeStart);
                        int leadingCopyFrames = encodeStart;
                        int trailingCopyFrames = totalFrames - encodeEnd;
                        int minExpectedCopyFrames = Math.Max(
                            MinHybridCopyFrames,
                            (int)Math.Ceiling(totalFrames * MinHybridCopyRatio));
                        if (copiedFrames < minExpectedCopyFrames)
                        {
                            canCopyOutsideBlurWindow = false;
                            hybridCopyFallbackReason =
                                $"하이브리드 복사 구간 이득이 미미함(복사구간={copiedFrames}, 총={totalFrames}, 임계치={minExpectedCopyFrames})";
                        }
                        else if (encodeStart == 0 || encodeEnd == totalFrames)
                        {
                            canCopyOutsideBlurWindow = false;
                            hybridCopyFallbackReason =
                                $"하이브리드 구간이 영상 끝단에 붙어 있어 한쪽 재인코드 경계로만 동작합니다(start={encodeStart}, end={encodeEnd}, total={totalFrames})";
                        }
                        else if ((encodeStart > 0 && leadingCopyFrames < MinHybridCopySideFrames) ||
                                 (encodeEnd < totalFrames && trailingCopyFrames < MinHybridCopySideFrames))
                        {
                            canCopyOutsideBlurWindow = false;
                            hybridCopyFallbackReason =
                                $"복사 쪽 경계 보강 필요(leading={leadingCopyFrames}, trailing={trailingCopyFrames}, 최소={MinHybridCopySideFrames})";
                        }
                        else if (encodeStart > 0 || encodeEnd < totalFrames)
                        {
                            hybridEncodeWindow = (encodeStart, encodeEnd);
                            hybridWindowStartFrame = encodeStart;
                            hybridWindowEndFrame = encodeEnd;
                            hybridCopyAttempted = true;
                        }
                        else
                        {
                            hybridCopyFallbackReason = null;
                        }
                    }
                }
            }

            int exportSampleWindowFrames = ResolveExportSampleWindowFrames(sourceFps, totalFrames);

            string? unsupportedStreamMetadata =
                FFmpegHdrMetadataGuard.FindUnsupportedMetadata(inStream->codecpar);
            if (unsupportedStreamMetadata != null)
                ThrowUnsupportedDynamicVideoMetadata(unsupportedStreamMetadata);

            AVCodec* decoder = ffmpeg.avcodec_find_decoder(inStream->codecpar->codec_id);
            dec = ffmpeg.avcodec_alloc_context3(decoder);
            Throw(ffmpeg.avcodec_parameters_to_context(dec, inStream->codecpar));
            ConfigureDecoderSideDataExport(dec, inStream->codecpar->codec_id);
            Throw(ffmpeg.avcodec_open2(dec, decoder, null));
            AVFieldOrder sourceFieldOrder = ResolveSourceFieldOrder(inStream, dec);
            if (IsInterlacedFieldOrder(sourceFieldOrder))
                ThrowInterlacedAutoMosaicUnsupported(sourceFieldOrder);
            bool sourceUsesHdrTransfer = dec->color_trc is
                AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084 or
                AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67;
            bool sourceMayCarryHdrMetadata =
                sourceUsesHdrTransfer ||
                GetPixelFormatBitDepth(dec->pix_fmt) > 8 ||
                inStream->codecpar->codec_id is
                    AVCodecID.AV_CODEC_ID_HEVC or
                    AVCodecID.AV_CODEC_ID_AV1;
            VideoHdrMetadata? hdrMetadata = sourceMayCarryHdrMetadata
                ? ProbeVideoHdrMetadata(inputPath)
                : null;
            _configuredHdrMetadata = hdrMetadata;
            _staticHdrConfigured = hdrMetadata?.HasStaticMetadata == true;
            bool sourceIsRgbH264 =
                inStream->codecpar->codec_id == AVCodecID.AV_CODEC_ID_H264 &&
                IsRgbPixelFormat(ResolveSourcePixelFormat(inStream, dec));
            if (sourceIsRgbH264 && hdrMetadata?.HasStaticMetadata == true)
            {
                throw new VideoExportIntegrityException(
                    "정적 HDR 메타데이터가 있는 RGB H.264 영상은 libx264rgb에서 " +
                    "원본 표현을 보존할 수 없습니다. HEVC 손실 변환 없이 내보내기를 중단했습니다.");
            }

            // ───────── output ─────────
            Throw(ffmpeg.avformat_alloc_output_context2(&outFmt, null, null, outputPath));
            CopyFormatPresentationMetadata(inFmt, outFmt);

            AVCodec* encoder;
            AVCodecID inputCodecId = inStream->codecpar->codec_id;
            AVCodecID encoderInputCodecId = hdrMetadata?.HasStaticMetadata == true
                ? inputCodecId == AVCodecID.AV_CODEC_ID_AV1
                    ? AVCodecID.AV_CODEC_ID_AV1
                    : AVCodecID.AV_CODEC_ID_HEVC
                : forceH264Fallback && inputCodecId != AVCodecID.AV_CODEC_ID_H264
                    ? AVCodecID.AV_CODEC_ID_H264
                    : inputCodecId;
            enc = TryCreateEncoderContext(
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

                string inputName = GetCodecName(encoderInputCodecId);
                var fallbackCodecId = hdrMetadata?.HasStaticMetadata == true
                    ? AVCodecID.AV_CODEC_ID_HEVC
                    : AVCodecID.AV_CODEC_ID_H264;
                string fallbackName = GetCodecName(fallbackCodecId);
                string reason = string.IsNullOrWhiteSpace(encoderError)
                    ? "원본 코덱 인코더를 찾을 수 없습니다."
                    : encoderError;
                exportNotice = $"원본 코덱({inputName}) 인코더를 사용할 수 없어 {fallbackName}로 내보냅니다. 사유: {reason}";

                enc = TryCreateEncoderContext(
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
                    $"{GetEncoderName(encoder)} 인코더를 사용합니다.";
            }
            _losslessX264RgbConfigured = string.Equals(
                GetEncoderName(encoder),
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
                        Throw(ffmpeg.avcodec_parameters_copy(outAudioStream->codecpar, inAudioStream->codecpar));
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
                    string audioCodec = GetCodecName(inAudioStream->codecpar->codec_id);
                    throw new InvalidOperationException(
                        $"출력 컨테이너가 원본 오디오 코덱({audioCodec})을 지원하지 않아 " +
                        "무손실 복사를 수행할 수 없습니다.");
                }
                else
                {
                    if (TryInitAudioTranscode(inAudioStream, outFmt, out audioDec, out audioEnc, out outAudioStream, out swr, out audioFifo, out var audioError))
                    {
                        audioReencode = true;
                        string audioCodec = GetCodecName(inAudioStream->codecpar->codec_id);
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
                        string audioCodec = GetCodecName(extraInputStream->codecpar->codec_id);
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

                    Throw(ffmpeg.avcodec_parameters_copy(extraOutputStream->codecpar, extraInputStream->codecpar));
                    extraOutputStream->codecpar->codec_tag = 0;
                    extraOutputStream->time_base = extraInputStream->time_base;
                    CopyStreamPresentationMetadata(extraInputStream, extraOutputStream);
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
                CopyStreamPresentationMetadata(inAudioStream, outAudioStream);

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
                && Math.Abs(sourceEncodedFrameStep - predictedHybridCopyFrameStep) > MaxHybridFrameStepTolerance)
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
                Throw(ffmpeg.avcodec_parameters_copy(outStream->codecpar, inStream->codecpar));
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
                Throw(ffmpeg.avcodec_parameters_from_context(outStream->codecpar, enc));
                outStream->time_base = enc->time_base;
            }
            CopyStreamPresentationMetadata(inStream, outStream);
            encodedPacketFrameStep = VideoExportTimingPolicy.GetVideoFrameStep(sourceFps, outStream->time_base);
            if (useHybridCopyWindow
                && Math.Abs(encodedPacketFrameStep - hybridCopyVideoFrameStep) > MaxHybridFrameStepTolerance)
            {
                useHybridCopyWindow = false;
                hybridCopyAttempted = true;
                string outStepMismatchReason =
                    $"하이브리드 출력 시점 간격 불일치(encodeOutput={encodedPacketFrameStep}, copy={hybridCopyVideoFrameStep}, sourceFps={sourceFps:0.###})";
                hybridCopyFallbackReason = string.IsNullOrWhiteSpace(hybridCopyFallbackReason)
                    ? outStepMismatchReason
                    : $"{hybridCopyFallbackReason}; {outStepMismatchReason}";
                Throw(ffmpeg.avcodec_parameters_from_context(outStream->codecpar, enc));
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
                    string mediaType = GetMediaTypeName(auxiliaryInputStream->codecpar->codec_type);
                    string codec = GetCodecName(auxiliaryInputStream->codecpar->codec_id);
                    throw new InvalidOperationException(
                        $"출력 컨테이너에 {mediaType} 스트림 {inputStreamIndex + 1}의 " +
                        $"원본 코덱({codec})을 보존할 수 없습니다.");
                }

                AVStream* auxiliaryOutputStream = ffmpeg.avformat_new_stream(outFmt, null);
                if (auxiliaryOutputStream == null)
                {
                    string mediaType = GetMediaTypeName(auxiliaryInputStream->codecpar->codec_type);
                    throw new InvalidOperationException(
                        $"{mediaType} 스트림 {inputStreamIndex + 1}의 출력 스트림을 생성하지 못했습니다.");
                }

                Throw(ffmpeg.avcodec_parameters_copy(
                    auxiliaryOutputStream->codecpar,
                    auxiliaryInputStream->codecpar));
                auxiliaryOutputStream->codecpar->codec_tag = 0;
                auxiliaryOutputStream->time_base = auxiliaryInputStream->time_base;
                CopyStreamPresentationMetadata(auxiliaryInputStream, auxiliaryOutputStream);
                copiedStreams.Add(
                    inputStreamIndex,
                    new StreamCopyState(inputStreamIndex, auxiliaryInputStream, auxiliaryOutputStream));
            }

            if ((outFmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
                Throw(ffmpeg.avio_open(&outFmt->pb, outputPath, ffmpeg.AVIO_FLAG_WRITE));

            Throw(ffmpeg.avformat_write_header(outFmt, null));

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
            Throw(ffmpeg.av_frame_get_buffer(bgra, 32));

            encFrame->format = (int)enc->pix_fmt;
            encFrame->width = enc->width;
            encFrame->height = enc->height;
            Throw(ffmpeg.av_frame_get_buffer(encFrame, 32));

            // Dynamic frame scaling reads matrix, range, and chroma location from each frame.
            swsDecToBgra = CreateDynamicSwsContext("디코더-BGRA 변환");
            swsBgraToEnc = CreateDynamicSwsContext("BGRA-인코더 변환");
            swsDecToEnc = CreateDynamicSwsContext("디코더-인코더 변환");

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
                EnsureContainerStructureSupported(inFmt);
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
                    Throw(ffmpeg.av_interleaved_write_frame(outFmt, pkt));
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
                            Throw(sendErr);

                        while (ffmpeg.avcodec_receive_frame(audioDec, audioFrame) == 0)
                        {
                            ConvertAndQueueAudioFrame(audioFrame, audioDec, audioEnc, swr, audioFifo, audioConvFrame);
                            DrainAudioFifo(
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
                    if (useHybridCopyWindow && packetFrameIndexReliabilityFailureCount >= MaxHybridPacketFrameIndexUnreliableSequence)
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
                        packetFrameIndex - lastPacketFrameIndexForHybrid > MaxHybridFrameGapBeforeFallback)
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
                    if (hybridModeTransitionCount > MaxHybridCopyModeTransitionsBeforeFallback)
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
                            FlushVideoPipeline(
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
                                forceSafeEncoding,
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
                                ref reusableFaceMask,
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
                        FlushVideoPipeline(
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
                            forceSafeEncoding,
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
                            ref reusableFaceMask,
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
                        long copyGapJitterTolerance = Math.Max(1, expectedGap / MaxHybridCopyPtsJitterDivisor);
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
                        if (copyTimestampFixCount > MaxHybridCopyTimestampFixBeforeFallback)
                        {
                            packetDropFallbackReason = $"copy-ts-fix={copyTimestampFixCount}, frame={packetFrameIndex}";
                            throw new InvalidOperationException(
                                "Invalid argument: 하이브리드 복사 타임스탬프 보정 횟수가 임계값을 초과해 fallback합니다.");
                        }
                    }

                    pkt->stream_index = outStream->index;
                    pkt->pos = -1;
                    Throw(ffmpeg.av_interleaved_write_frame(outFmt, pkt));
                    outputVideoPacketCount++;
                    copiedVideoPacketCount++;
                    hasLastVideoCopyPacketPts = true;
                    hasLastVideoCopyPacketDts = true;
                    lastVideoCopyPacketPts = pkt->pts;
                    lastVideoCopyPacketDts = pkt->dts;
                    ReportVideoProgress(progress, totalFrames, ref lastReportedFrame, packetFrameIndex);
                    ffmpeg.av_packet_unref(pkt);
                    wasLastPacketEncoded = false;
                    continue;
                }

                wasLastPacketEncoded = true;
                encodedSourceVideoPacketCount++;
                Throw(ffmpeg.avcodec_send_packet(dec, pkt));
                ffmpeg.av_packet_unref(pkt);
                int videoReceiveResult;
                while ((videoReceiveResult = ffmpeg.avcodec_receive_frame(dec, frame)) == 0)
                {
                    ProcessDecodedVideoFrame(
                        frame,
                        bgra,
                        encFrame,
                        dec,
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
                        forceSafeEncoding,
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
                        ref reusableFaceMask,
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
                        $"비디오 디코딩 중 오류가 발생했습니다: {GetErrorMessage(videoReceiveResult)}");
                }
            }

            progress?.Report(new ExportProgress(
                totalFrames,
                totalFrames,
                "인코더 버퍼를 마무리하는 중..."));
            FlushVideoPipeline(
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
                forceSafeEncoding,
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
                ref reusableFaceMask,
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
                int flushErr = ffmpeg.avcodec_send_packet(audioDec, null);
                if (flushErr < 0)
                    Throw(flushErr);

                while (ffmpeg.avcodec_receive_frame(audioDec, audioFrame) == 0)
                {
                    ConvertAndQueueAudioFrame(audioFrame, audioDec, audioEnc, swr, audioFifo, audioConvFrame);
                    DrainAudioFifo(
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

                DrainAudioFifo(
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
                    flush: true);

                int sendFinalErr = ffmpeg.avcodec_send_frame(audioEnc, null);
                if (sendFinalErr < 0)
                    Throw(sendFinalErr);
                    while (ffmpeg.avcodec_receive_packet(audioEnc, audioPkt) == 0)
                    {
                        audioPkt->stream_index = outAudioStream->index;
                        ffmpeg.av_packet_rescale_ts(audioPkt, audioEnc->time_base, outAudioStream->time_base);
                        VideoExportTimingPolicy.NormalizeEncodedPacketTimestamps(
                            audioPkt,
                            ref lastAudioEncPacketPts,
                            ref hasLastAudioEncPacketPts,
                            ref lastAudioEncPacketDts,
                            ref hasLastAudioEncPacketDts);
                        Throw(ffmpeg.av_interleaved_write_frame(outFmt, audioPkt));
                        ffmpeg.av_packet_unref(audioPkt);
                    }
            }

            progress?.Report(new ExportProgress(
                totalFrames,
                totalFrames,
                "출력 파일을 마무리하는 중..."));
            Throw(ffmpeg.av_write_trailer(outFmt));
            outputCloseTimer.Start();
            CloseOutputOrThrow(outFmt);
            outputCloseTimer.Stop();
            int sampleWindowLimit = totalFrames > 0
                ? Math.Min(exportSampleWindowFrames, totalFrames)
                : exportSampleWindowFrames;
            // Demux packet count is diagnostic only. Decoder preroll and discard packets do not
            // necessarily produce frames, so final integrity uses submitted/emitted PTS coverage.
            int sampleWindowSourceFrames = Math.Min(
                sampleWindowLimit,
                copiedSourceVideoPacketCount + submittedEncodedFramePts.Count);
            int sampleWindowProducedFrames = Math.Min(
                sampleWindowLimit,
                copiedVideoPacketCount + emittedEncodedFramePts.Count);
            int sampleWindowFrameShortfall = Math.Max(
                0,
                sampleWindowSourceFrames - sampleWindowProducedFrames);
            int expectedHybridWindowEncodedFrames = useHybridCopyWindow && encodeWindowEnd > encodeWindowStart
                ? encodeWindowEnd - encodeWindowStart
                : 0;
            int encodedWindowFrameShortfall = Math.Max(0, expectedHybridWindowEncodedFrames - encodedWindowFrameCount);
            int hybridCopySourcePacketLoss = useHybridCopyWindow
                ? Math.Max(0, copiedSourceVideoPacketCount - copiedVideoPacketCount)
                : 0;
            int hybridEncodedWindowFrameLoss = useHybridCopyWindow
                ? encodedWindowFrameShortfall
                : 0;
            var frameCoverage = EvaluateVideoFrameCoverage(
                copiedSourceVideoPacketCount,
                copiedVideoPacketCount,
                submittedEncodedFramePts,
                emittedEncodedFramePts);
            var presentationGapIntegrity = VideoExportTimingPolicy.EvaluateEncodedPresentationGaps(
                submittedEncodedFramePts,
                emittedEncodedFramePts,
                emittedEncodedMuxPts);
            outputPacketPtsGapOutlierCount = presentationGapIntegrity.OutlierCount;
            maxOutputPacketPtsGap = presentationGapIntegrity.MaxOutputGap;
            int missingEncodedFrameCount = frameCoverage.MissingEncodedFrames;
            int unexpectedEncodedFrameCount = frameCoverage.UnexpectedEncodedFrames;
            int expectedOutputVideoFrames = frameCoverage.ExpectedOutputFrames;
            int videoFrameCoverageMismatch = frameCoverage.MismatchCount;
            int videoFrameDropCount = useHybridCopyWindow
                ? Math.Max(frameCoverage.DropCount, hybridCopySourcePacketLoss + hybridEncodedWindowFrameLoss)
                : frameCoverage.DropCount;
            int outputPacketCountMismatch = frameCoverage.CopiedPacketMismatch;
            int droppedVideoPackets = hybridCopySourcePacketLoss;
            bool canRetryWithFullEncode = allowPacketDropRetry && useHybridCopyWindow;
            if (expectedOutputVideoFrames <= 0 || outputVideoPacketCount <= 0)
            {
                string invalidPacketCountReason =
                    $"final-output-frame-coverage-invalid={expectedOutputVideoFrames}/{outputVideoPacketCount}";
                packetDropFallbackReason = string.IsNullOrWhiteSpace(packetDropFallbackReason)
                    ? invalidPacketCountReason
                    : $"{packetDropFallbackReason}; {invalidPacketCountReason}";

                if (canRetryWithFullEncode)
                {
                    shouldRetryWithFullEncode = true;
                }
                else
                {
                    throw new VideoExportIntegrityException(
                        "Invalid argument: 최종 출력 비디오 패킷 수가 유효하지 않아 품질 보전을 위해 중단합니다.");
                }
            }
            if (encodedTimestampIntegrity.MissingPacketTimestamps > 0 ||
                encodedTimestampIntegrity.PacketTimestampAdjustments > 0)
            {
                string timestampIntegrityReason =
                    $"encoded-packet-timestamps missing={encodedTimestampIntegrity.MissingPacketTimestamps}, adjustments={encodedTimestampIntegrity.PacketTimestampAdjustments}";
                packetDropFallbackReason = string.IsNullOrWhiteSpace(packetDropFallbackReason)
                    ? timestampIntegrityReason
                    : $"{packetDropFallbackReason}; {timestampIntegrityReason}";

                if (canRetryWithFullEncode)
                {
                    shouldRetryWithFullEncode = true;
                }
                else
                {
                    throw new VideoExportIntegrityException(
                        "Invalid argument: 인코더 출력 타임스탬프 보정이 필요해 품질 보전을 위해 중단합니다.");
                }
            }
            if (allowPacketDropRetry
                && useHybridCopyWindow
                && expectedHybridWindowEncodedFrames > 0)
            {
                if (encodedWindowFrameShortfall > 0)
                {
                    shouldRetryWithFullEncode = true;
                    packetDropFallbackReason =
                        string.IsNullOrWhiteSpace(packetDropFallbackReason)
                            ? $"encode-window-frame-loss={encodedWindowFrameShortfall} (expected={expectedHybridWindowEncodedFrames}, produced={encodedWindowFrameCount})"
                            : $"{packetDropFallbackReason}; encode-window-frame-loss={encodedWindowFrameShortfall} (expected={expectedHybridWindowEncodedFrames}, produced={encodedWindowFrameCount})";
                }
            }
            if (allowPacketDropRetry
                && useHybridCopyWindow
                && inputVideoPacketCount > 0
                && videoFrameDropCount > 0)
            {
                shouldRetryWithFullEncode = true;
                packetDropFallbackReason =
                    string.IsNullOrWhiteSpace(packetDropFallbackReason)
                        ? $"hybrid-output-frame-loss={videoFrameDropCount} / inputPackets={inputVideoPacketCount}"
                        : $"{packetDropFallbackReason}; hybrid-output-frame-loss={videoFrameDropCount} / inputPackets={inputVideoPacketCount}";
            }
            if (videoFrameCoverageMismatch > MaxAllowedOutputPacketLoss)
            {
                string outputLossReason =
                    $"final-output-frame-coverage-mismatch=missing:{missingEncodedFrameCount},unexpected:{unexpectedEncodedFrameCount},copy:{copiedSourceVideoPacketCount}/{copiedVideoPacketCount}," +
                    $"ptsGapOutliers:{outputPacketPtsGapOutlierCount},maxPtsGap:{maxOutputPacketPtsGap}";
                packetDropFallbackReason = string.IsNullOrWhiteSpace(packetDropFallbackReason)
                    ? outputLossReason
                    : $"{packetDropFallbackReason}; {outputLossReason}";

                if (canRetryWithFullEncode)
                {
                    shouldRetryWithFullEncode = true;
                }
                else
                {
                    throw new VideoExportIntegrityException(
                        "Invalid argument: 최종 출력 패킷 손실 감지로 품질 보전을 위해 중단합니다.");
                }
            }

            if (videoFrameDropCount > 0)
            {
                if (useHybridCopyWindow)
                {
                    Debug.WriteLine(
                        $"[Export] frameDropHint copySource={copiedSourceVideoPacketCount}, copyOutput={copiedVideoPacketCount}, copyLoss={hybridCopySourcePacketLoss}, encodeWindow={encodeWindowStart}-{encodeWindowEnd}, encodedWindowFrames={encodedWindowFrameCount}, encodedShortfall={hybridEncodedWindowFrameLoss}, dropped={videoFrameDropCount}");
                }
                else
                {
                    Debug.WriteLine(
                        $"[Export] frameCoverageHint submittedFrames={submittedVideoFrameCount}, outputVideoPackets={outputVideoPacketCount}, dropped={videoFrameDropCount}");
                }

                if (canRetryWithFullEncode)
                {
                    shouldRetryWithFullEncode = true;
                    string coverageReason =
                        $"copy-source={copiedSourceVideoPacketCount}, copy-output={copiedVideoPacketCount}, droppedFrames={videoFrameDropCount}";
                    packetDropFallbackReason =
                        string.IsNullOrWhiteSpace(packetDropFallbackReason)
                            ? coverageReason
                            : $"{packetDropFallbackReason}; {coverageReason}";
                }
            }
            if (outputPacketPtsGapOutlierCount > 0)
            {
                packetDropFallbackReason =
                    string.IsNullOrWhiteSpace(packetDropFallbackReason)
                        ? $"output-pts-gap-outlier-count={outputPacketPtsGapOutlierCount}, maxGap={maxOutputPacketPtsGap}"
                        : $"{packetDropFallbackReason}; output-pts-gap-outlier-count={outputPacketPtsGapOutlierCount}, maxGap={maxOutputPacketPtsGap}";
                if (canRetryWithFullEncode)
                {
                    shouldRetryWithFullEncode = true;
                }
                else
                {
                    throw new VideoExportIntegrityException(
                        "Invalid argument: 최종 출력 PTS 간격 이상치가 남아 품질 보전을 위해 중단합니다.");
                }
            }
            if (copyGapOutlierCount > 0 && !shouldRetryWithFullEncode)
                Debug.WriteLine($"[Export] hybrid copy source PTS gap observed count={copyGapOutlierCount}, maxGap={maxCopyGap}");
            int droppedVideoPacketsForSummary = Math.Max(0, droppedVideoPackets);
            string? packetLossFallbackReason = shouldRetryWithFullEncode ? $"fallback-full-encode={packetDropFallbackReason}" : null;
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
                EncoderName: GetEncoderName(encoder),
                EncoderQualityMode: encoderQualityConfiguration.Mode,
                EncoderOptionsApplied: encoderQualityConfiguration.AppliedOptions,
                EncoderOptionFailures: encoderQualityConfiguration.FailedOptions,
                SourcePixelFormat: GetPixelFormatName(dec->pix_fmt),
                OutputPixelFormat: GetPixelFormatName(enc->pix_fmt),
                SourceBitDepth: GetPixelFormatBitDepth(dec->pix_fmt),
                OutputBitDepth: GetPixelFormatBitDepth(enc->pix_fmt),
                SourceVideoBitrate: ResolveSourceVideoBitrate(inStream, dec),
                TargetVideoBitrate: ResolveTargetVideoBitrateForSummary(encoder, inStream, dec, enc),
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
            reusableFaceMask?.Dispose();

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

    private static void ReportVideoProgress(
        IProgress<ExportProgress>? progress,
        int totalFrames,
        ref int lastReportedFrame,
        int currentFrame,
        string? status = null)
    {
        if (progress == null || totalFrames <= 0)
            return;

        int bounded = Math.Clamp(currentFrame, 0, totalFrames);
        if (bounded - lastReportedFrame >= 15 || bounded >= totalFrames || status != null)
        {
            progress.Report(new ExportProgress(bounded, totalFrames, status));
            lastReportedFrame = bounded;
        }
    }

    private sealed class VideoPacketTimestampIntegrity
    {
        public int MissingPacketTimestamps { get; set; }
        public int PacketTimestampAdjustments { get; set; }
    }

    private sealed class StreamCopyState
    {
        public int InputStreamIndex { get; }
        public AVStream* InputStream { get; }
        public AVStream* OutputStream { get; }
        public bool ShouldRepairMissingTimestamps { get; }
        public long LastPacketPts = -1;
        public bool HasLastPacketPts;
        public long LastPacketDts = -1;
        public bool HasLastPacketDts;

        public StreamCopyState(
            int inputStreamIndex,
            AVStream* inputStream,
            AVStream* outputStream)
        {
            InputStreamIndex = inputStreamIndex;
            InputStream = inputStream;
            OutputStream = outputStream;
            ShouldRepairMissingTimestamps =
                inputStream != null &&
                inputStream->codecpar != null &&
                inputStream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO;
        }
    }

    private sealed record VideoHdrMetadata(
        string? MasterDisplay,
        string? MaxContentLightLevel,
        byte[]? MasteringDisplayPayload,
        byte[]? ContentLightPayload)
    {
        public bool HasStaticMetadata =>
            MasteringDisplayPayload != null || ContentLightPayload != null;

        public bool CanConfigureX265 =>
            (MasteringDisplayPayload == null || !string.IsNullOrWhiteSpace(MasterDisplay)) &&
            (ContentLightPayload == null || !string.IsNullOrWhiteSpace(MaxContentLightLevel));

        public string ToX265Params()
        {
            var values = new List<string>(2);
            if (!string.IsNullOrWhiteSpace(MasterDisplay))
                values.Add($"master-display={MasterDisplay}");
            if (!string.IsNullOrWhiteSpace(MaxContentLightLevel))
                values.Add($"max-cll={MaxContentLightLevel}");
            return string.Join(':', values);
        }
    }

    private sealed record EncoderQualityConfiguration(
        string Mode,
        bool RequiredOptionsApplied,
        string AppliedOptions,
        string FailedOptions)
    {
        public static EncoderQualityConfiguration Unconfigured { get; } =
            new("unconfigured", false, string.Empty, "encoder-options-not-configured");
    }

    private static unsafe VideoHdrMetadata? ProbeVideoHdrMetadata(string inputPath)
    {
        const int MaxProbeFrames = 16;
        AVFormatContext* format = null;
        AVCodecContext* decoderContext = null;
        AVPacket* packet = ffmpeg.av_packet_alloc();
        AVFrame* decodedFrame = ffmpeg.av_frame_alloc();
        if (packet == null || decodedFrame == null)
        {
            ffmpeg.av_packet_free(&packet);
            ffmpeg.av_frame_free(&decodedFrame);
            throw new InvalidOperationException("HDR 메타데이터 확인용 프레임을 할당할 수 없습니다.");
        }

        try
        {
            Throw(ffmpeg.avformat_open_input(&format, inputPath, null, null));
            Throw(ffmpeg.avformat_find_stream_info(format, null));

            int videoStreamIndex = FFmpegStreamSelection.FindPrimaryVideoStreamIndex(format);
            if (videoStreamIndex < 0)
                return null;

            AVCodecParameters* parameters = format->streams[videoStreamIndex]->codecpar;
            AVCodec* decoder = ffmpeg.avcodec_find_decoder(parameters->codec_id);
            if (decoder == null)
                throw new InvalidOperationException("HDR 메타데이터 확인용 비디오 디코더를 찾을 수 없습니다.");

            decoderContext = ffmpeg.avcodec_alloc_context3(decoder);
            if (decoderContext == null)
                throw new InvalidOperationException("HDR 메타데이터 확인용 디코더를 만들 수 없습니다.");
            Throw(ffmpeg.avcodec_parameters_to_context(decoderContext, parameters));
            ConfigureDecoderSideDataExport(decoderContext, parameters->codec_id);
            Throw(ffmpeg.avcodec_open2(decoderContext, decoder, null));

            int decodedFrames = 0;
            bool reachedProbeLimit = false;
            VideoHdrMetadata? accumulatedMetadata = null;
            while (!reachedProbeLimit && ffmpeg.av_read_frame(format, packet) >= 0)
            {
                if (packet->stream_index != videoStreamIndex)
                {
                    ffmpeg.av_packet_unref(packet);
                    continue;
                }

                int sendError = ffmpeg.avcodec_send_packet(decoderContext, packet);
                ffmpeg.av_packet_unref(packet);
                if (sendError < 0)
                    continue;

                while (ffmpeg.avcodec_receive_frame(decoderContext, decodedFrame) == 0)
                {
                    decodedFrames++;
                    string? unsupportedMetadata =
                        FFmpegHdrMetadataGuard.FindUnsupportedMetadata(decodedFrame);
                    if (unsupportedMetadata != null)
                        ThrowUnsupportedDynamicVideoMetadata(unsupportedMetadata);
                    accumulatedMetadata = MergeVideoHdrMetadata(
                        accumulatedMetadata,
                        ReadVideoHdrMetadata(decodedFrame));
                    ffmpeg.av_frame_unref(decodedFrame);
                    if (decodedFrames >= MaxProbeFrames)
                    {
                        reachedProbeLimit = true;
                        break;
                    }
                }
            }

            if (!reachedProbeLimit)
            {
                _ = ffmpeg.avcodec_send_packet(decoderContext, null);
                while (ffmpeg.avcodec_receive_frame(decoderContext, decodedFrame) == 0)
                {
                    decodedFrames++;
                    string? unsupportedMetadata =
                        FFmpegHdrMetadataGuard.FindUnsupportedMetadata(decodedFrame);
                    if (unsupportedMetadata != null)
                        ThrowUnsupportedDynamicVideoMetadata(unsupportedMetadata);
                    accumulatedMetadata = MergeVideoHdrMetadata(
                        accumulatedMetadata,
                        ReadVideoHdrMetadata(decodedFrame));
                    ffmpeg.av_frame_unref(decodedFrame);
                    if (decodedFrames >= MaxProbeFrames)
                        break;
                }
            }

            return accumulatedMetadata;
        }
        finally
        {
            ffmpeg.av_frame_free(&decodedFrame);
            ffmpeg.av_packet_free(&packet);
            ffmpeg.avcodec_free_context(&decoderContext);
            if (format != null)
                ffmpeg.avformat_close_input(&format);
        }
    }

    private static unsafe VideoHdrMetadata? ReadVideoHdrMetadata(AVFrame* frame)
    {
        if (frame == null)
            return null;

        byte[]? masteringDisplayPayload = null;
        AVFrameSideData* masteringSideData = ffmpeg.av_frame_get_side_data(
            frame,
            AVFrameSideDataType.AV_FRAME_DATA_MASTERING_DISPLAY_METADATA);
        if (masteringSideData != null)
        {
            masteringDisplayPayload = CopyNativeSideDataPayload(
                masteringSideData->data,
                masteringSideData->size,
                sizeof(AVMasteringDisplayMetadata),
                "mastering display");
        }

        byte[]? contentLightPayload = null;
        AVFrameSideData* contentLightSideData = ffmpeg.av_frame_get_side_data(
            frame,
            AVFrameSideDataType.AV_FRAME_DATA_CONTENT_LIGHT_LEVEL);
        if (contentLightSideData != null)
        {
            contentLightPayload = CopyNativeSideDataPayload(
                contentLightSideData->data,
                contentLightSideData->size,
                sizeof(AVContentLightMetadata),
                "content light");
        }

        return CreateVideoHdrMetadata(masteringDisplayPayload, contentLightPayload);
    }

    private static unsafe VideoHdrMetadata? CreateVideoHdrMetadata(
        byte[]? masteringDisplayPayload,
        byte[]? contentLightPayload)
    {
        string? masterDisplay = null;
        if (masteringDisplayPayload != null)
        {
            fixed (byte* payload = masteringDisplayPayload)
            {
                AVMasteringDisplayMetadata* mastering =
                    (AVMasteringDisplayMetadata*)payload;
                if (mastering->has_primaries == 0 && mastering->has_luminance == 0)
                {
                    masteringDisplayPayload = null;
                }
                else
                {
                    if (mastering->has_primaries != 0)
                    {
                        for (uint color = 0; color < 3; color++)
                        {
                            for (uint coordinate = 0; coordinate < 2; coordinate++)
                            {
                                _ = ScaleHdrRational(
                                    mastering->display_primaries[color][coordinate],
                                    50_000);
                            }
                        }
                        for (uint coordinate = 0; coordinate < 2; coordinate++)
                            _ = ScaleHdrRational(mastering->white_point[coordinate], 50_000);
                    }
                    if (mastering->has_luminance != 0)
                    {
                        _ = ScaleHdrRational(mastering->max_luminance, 10_000);
                        _ = ScaleHdrRational(mastering->min_luminance, 10_000);
                    }

                    if (mastering->has_primaries != 0 && mastering->has_luminance != 0)
                    {
                        long redX = ScaleHdrRational(mastering->display_primaries[0][0], 50_000);
                        long redY = ScaleHdrRational(mastering->display_primaries[0][1], 50_000);
                        long greenX = ScaleHdrRational(mastering->display_primaries[1][0], 50_000);
                        long greenY = ScaleHdrRational(mastering->display_primaries[1][1], 50_000);
                        long blueX = ScaleHdrRational(mastering->display_primaries[2][0], 50_000);
                        long blueY = ScaleHdrRational(mastering->display_primaries[2][1], 50_000);
                        long whiteX = ScaleHdrRational(mastering->white_point[0], 50_000);
                        long whiteY = ScaleHdrRational(mastering->white_point[1], 50_000);
                        long maxLuminance = ScaleHdrRational(mastering->max_luminance, 10_000);
                        long minLuminance = ScaleHdrRational(mastering->min_luminance, 10_000);
                        masterDisplay =
                            $"G({greenX},{greenY})B({blueX},{blueY})R({redX},{redY})" +
                            $"WP({whiteX},{whiteY})L({maxLuminance},{minLuminance})";
                    }
                }
            }
        }

        string? maxContentLightLevel = null;
        if (contentLightPayload != null)
        {
            fixed (byte* payload = contentLightPayload)
            {
                AVContentLightMetadata* contentLight = (AVContentLightMetadata*)payload;
                maxContentLightLevel = $"{contentLight->MaxCLL},{contentLight->MaxFALL}";
            }
        }

        return masteringDisplayPayload == null && contentLightPayload == null
            ? null
            : new VideoHdrMetadata(
                masterDisplay,
                maxContentLightLevel,
                masteringDisplayPayload,
                contentLightPayload);
    }

    private static VideoHdrMetadata? MergeVideoHdrMetadata(
        VideoHdrMetadata? accumulated,
        VideoHdrMetadata? current)
    {
        if (current == null)
            return accumulated;
        if (accumulated == null)
            return current;

        byte[]? masteringDisplayPayload = MergeStaticHdrPayload(
            "mastering display",
            AVFrameSideDataType.AV_FRAME_DATA_MASTERING_DISPLAY_METADATA,
            accumulated.MasteringDisplayPayload,
            current.MasteringDisplayPayload);
        byte[]? contentLightPayload = MergeStaticHdrPayload(
            "content light",
            AVFrameSideDataType.AV_FRAME_DATA_CONTENT_LIGHT_LEVEL,
            accumulated.ContentLightPayload,
            current.ContentLightPayload);

        return new VideoHdrMetadata(
            accumulated.MasterDisplay ?? current.MasterDisplay,
            accumulated.MaxContentLightLevel ?? current.MaxContentLightLevel,
            masteringDisplayPayload,
            contentLightPayload);
    }

    private static byte[]? MergeStaticHdrPayload(
        string metadataName,
        AVFrameSideDataType type,
        byte[]? accumulated,
        byte[]? current)
    {
        if (current == null)
            return accumulated;
        if (accumulated == null)
            return current;
        if (!AreStaticHdrPayloadsEquivalent(type, accumulated, current))
        {
            throw new InvalidOperationException(
                $"정적 HDR {metadataName} 메타데이터가 프레임 사이에서 변경됩니다. " +
                "원본 표현 손실을 막기 위해 내보내기를 중단했습니다.");
        }

        return accumulated;
    }

    private static unsafe bool AreStaticHdrPayloadsEquivalent(
        AVFrameSideDataType type,
        ReadOnlySpan<byte> left,
        ReadOnlySpan<byte> right)
    {
        if (type == AVFrameSideDataType.AV_FRAME_DATA_CONTENT_LIGHT_LEVEL)
        {
            if (left.Length < sizeof(AVContentLightMetadata) ||
                right.Length < sizeof(AVContentLightMetadata))
            {
                return false;
            }

            fixed (byte* leftPayload = left)
            fixed (byte* rightPayload = right)
            {
                AVContentLightMetadata* leftMetadata =
                    (AVContentLightMetadata*)leftPayload;
                AVContentLightMetadata* rightMetadata =
                    (AVContentLightMetadata*)rightPayload;
                return leftMetadata->MaxCLL == rightMetadata->MaxCLL &&
                    leftMetadata->MaxFALL == rightMetadata->MaxFALL;
            }
        }

        if (type != AVFrameSideDataType.AV_FRAME_DATA_MASTERING_DISPLAY_METADATA ||
            left.Length < sizeof(AVMasteringDisplayMetadata) ||
            right.Length < sizeof(AVMasteringDisplayMetadata))
        {
            return false;
        }

        fixed (byte* leftPayload = left)
        fixed (byte* rightPayload = right)
        {
            AVMasteringDisplayMetadata* leftMetadata =
                (AVMasteringDisplayMetadata*)leftPayload;
            AVMasteringDisplayMetadata* rightMetadata =
                (AVMasteringDisplayMetadata*)rightPayload;
            if (leftMetadata->has_primaries != rightMetadata->has_primaries ||
                leftMetadata->has_luminance != rightMetadata->has_luminance)
            {
                return false;
            }

            if (leftMetadata->has_primaries != 0)
            {
                for (int color = 0; color < 3; color++)
                {
                    for (int coordinate = 0; coordinate < 2; coordinate++)
                    {
                        if (!AreHdrRationalsEquivalent(
                                leftMetadata->display_primaries[(uint)color][(uint)coordinate],
                                rightMetadata->display_primaries[(uint)color][(uint)coordinate]))
                        {
                            return false;
                        }
                    }
                }

                for (int coordinate = 0; coordinate < 2; coordinate++)
                {
                    if (!AreHdrRationalsEquivalent(
                            leftMetadata->white_point[(uint)coordinate],
                            rightMetadata->white_point[(uint)coordinate]))
                    {
                        return false;
                    }
                }
            }

            return leftMetadata->has_luminance == 0 ||
                (AreHdrRationalsEquivalent(
                     leftMetadata->max_luminance,
                     rightMetadata->max_luminance) &&
                 AreHdrRationalsEquivalent(
                     leftMetadata->min_luminance,
                     rightMetadata->min_luminance));
        }
    }

    private static bool AreHdrRationalsEquivalent(AVRational left, AVRational right)
    {
        return left.den > 0 && right.den > 0 && left.num >= 0 && right.num >= 0 &&
            (long)left.num * right.den == (long)right.num * left.den;
    }

    private static unsafe byte[] CopyNativeSideDataPayload(
        byte* data,
        ulong size,
        int expectedSize,
        string metadataName)
    {
        if (data == null || expectedSize <= 0 || size < (ulong)expectedSize)
        {
            throw new InvalidOperationException(
                $"정적 HDR {metadataName} 메타데이터 payload가 올바르지 않습니다.");
        }

        var payload = new byte[expectedSize];
        Marshal.Copy((IntPtr)data, payload, 0, payload.Length);
        return payload;
    }

    private static long ScaleHdrRational(AVRational value, long scale)
    {
        if (value.den <= 0 || value.num < 0)
        {
            throw new InvalidOperationException(
                "정적 HDR 메타데이터에 유효하지 않은 좌표 또는 휘도 값이 있습니다. " +
                "원본 표현 손실을 막기 위해 내보내기를 중단했습니다.");
        }
        return (long)Math.Round(
            value.num * (double)scale / value.den,
            MidpointRounding.AwayFromZero);
    }

    private static unsafe void ConfigureDecoderSideDataExport(
        AVCodecContext* decoderContext,
        AVCodecID codecId)
    {
        if (decoderContext != null && codecId == AVCodecID.AV_CODEC_ID_AV1)
            decoderContext->export_side_data |= ffmpeg.AV_CODEC_EXPORT_DATA_FILM_GRAIN;
    }

    private static unsafe bool TryConfigureEncoderStaticHdrMetadata(
        AVCodecContext* encoderContext,
        VideoHdrMetadata hdrMetadata,
        out string? error)
    {
        error = null;
        if (!TryAddEncoderStaticHdrPayload(
                encoderContext,
                AVFrameSideDataType.AV_FRAME_DATA_MASTERING_DISPLAY_METADATA,
                hdrMetadata.MasteringDisplayPayload,
                "mastering display",
                out error))
        {
            return false;
        }

        return TryAddEncoderStaticHdrPayload(
            encoderContext,
            AVFrameSideDataType.AV_FRAME_DATA_CONTENT_LIGHT_LEVEL,
            hdrMetadata.ContentLightPayload,
            "content light",
            out error);
    }

    private static unsafe bool TryAddEncoderStaticHdrPayload(
        AVCodecContext* encoderContext,
        AVFrameSideDataType type,
        byte[]? payload,
        string metadataName,
        out string? error)
    {
        error = null;
        if (payload == null)
            return true;
        if (encoderContext == null || payload.Length == 0)
        {
            error = $"HDR {metadataName} 메타데이터 payload가 비어 있습니다.";
            return false;
        }

        ffmpeg.av_frame_side_data_remove(
            &encoderContext->decoded_side_data,
            &encoderContext->nb_decoded_side_data,
            type);
        AVFrameSideData* sideData = ffmpeg.av_frame_side_data_new(
            &encoderContext->decoded_side_data,
            &encoderContext->nb_decoded_side_data,
            type,
            (ulong)payload.Length,
            0);
        if (sideData == null || sideData->data == null)
        {
            error = $"HDR {metadataName} 메타데이터를 AV1 인코더에 전달할 수 없습니다.";
            return false;
        }

        Marshal.Copy(payload, 0, (IntPtr)sideData->data, payload.Length);
        return true;
    }

    private unsafe void ValidateFrameStaticHdrMetadata(AVFrame* frame)
    {
        if (frame == null)
            return;

        ValidateFrameStaticHdrPayload(
            frame,
            AVFrameSideDataType.AV_FRAME_DATA_MASTERING_DISPLAY_METADATA,
            _configuredHdrMetadata?.MasteringDisplayPayload,
            "mastering display");
        ValidateFrameStaticHdrPayload(
            frame,
            AVFrameSideDataType.AV_FRAME_DATA_CONTENT_LIGHT_LEVEL,
            _configuredHdrMetadata?.ContentLightPayload,
            "content light");
    }

    private static unsafe void ValidateFrameStaticHdrPayload(
        AVFrame* frame,
        AVFrameSideDataType type,
        byte[]? configuredPayload,
        string metadataName)
    {
        AVFrameSideData* sideData = ffmpeg.av_frame_get_side_data(frame, type);
        if (sideData == null)
            return;
        if (configuredPayload == null || sideData->data == null ||
            sideData->size < (ulong)configuredPayload.Length)
        {
            throw new InvalidOperationException(
                $"정적 HDR {metadataName} 메타데이터를 인코더 초기화 전에 정확히 구성하지 못했습니다. " +
                "품질 저하를 막기 위해 내보내기를 중단했습니다.");
        }

        var actualPayload = new ReadOnlySpan<byte>(sideData->data, configuredPayload.Length);
        if (!AreStaticHdrPayloadsEquivalent(type, configuredPayload, actualPayload))
        {
            throw new InvalidOperationException(
                $"정적 HDR {metadataName} 메타데이터가 탐색 구간 이후 변경됐습니다. " +
                "원본 표현 손실을 막기 위해 내보내기를 중단했습니다.");
        }
    }

    private static unsafe void DrainEncoderPackets(
        AVCodecContext* enc,
        AVPacket* outPkt,
        AVStream* outStream,
        AVFormatContext* outFmt,
        ref long lastPacketPts,
        ref bool hasLastPacketPts,
        ref long lastPacketDts,
        ref bool hasLastPacketDts,
        ref int outputVideoPacketCount,
        VideoPacketTimestampIntegrity timestampIntegrity,
        Dictionary<long, long> submittedEncodedFrameDurations,
        Dictionary<long, int> emittedEncodedFramePts,
        List<long> emittedEncodedMuxPts,
        long encodedPacketFrameStep)
    {
        while (true)
        {
            int receiveResult = ffmpeg.avcodec_receive_packet(enc, outPkt);
            if (receiveResult == ffmpeg.AVERROR(ffmpeg.EAGAIN) ||
                receiveResult == ffmpeg.AVERROR_EOF)
            {
                return;
            }
            ThrowVideoEncoderError(receiveResult, enc, "패킷 수신");

            long encoderPacketPts = outPkt->pts;
            outPkt->duration = VideoExportTimingPolicy.ResolveEncodedPacketDuration(
                outPkt->duration,
                encoderPacketPts,
                submittedEncodedFrameDurations);
            if (outPkt->duration < 0)
            {
                throw new InvalidOperationException(
                    $"Invalid argument: 음수 인코더 패킷 길이는 내보낼 수 없습니다 " +
                    $"(duration={outPkt->duration}).");
            }

            ffmpeg.av_packet_rescale_ts(outPkt, enc->time_base, outStream->time_base);
            bool hasMissingTimestamp =
                outPkt->pts == ffmpeg.AV_NOPTS_VALUE ||
                outPkt->dts == ffmpeg.AV_NOPTS_VALUE;
            if (hasMissingTimestamp)
            {
                timestampIntegrity.MissingPacketTimestamps++;
                long originalPts = outPkt->pts;
                long originalDts = outPkt->dts;
                _ = VideoExportTimingPolicy.NormalizeEncodedPacketTimestamps(
                    outPkt,
                    ref lastPacketPts,
                    ref hasLastPacketPts,
                    ref lastPacketDts,
                    ref hasLastPacketDts,
                    encodedPacketFrameStep);
                if (outPkt->pts != originalPts || outPkt->dts != originalDts)
                    timestampIntegrity.PacketTimestampAdjustments++;
            }
            else
            {
                // B-frame encoders emit packets in decode order. Their PTS can move backward and
                // initial DTS values can be negative; both are required for correct presentation.
                bool allowsEqualDts = outFmt->oformat != null &&
                    (outFmt->oformat->flags & ffmpeg.AVFMT_TS_NONSTRICT) != 0;
                bool invalidDtsOrder = hasLastPacketDts &&
                    (allowsEqualDts
                        ? outPkt->dts < lastPacketDts
                        : outPkt->dts <= lastPacketDts);
                if (outPkt->pts < outPkt->dts || invalidDtsOrder)
                {
                    throw new InvalidOperationException(
                        $"Invalid argument: 인코더 출력 패킷 순서가 유효하지 않습니다 " +
                        $"(pts={outPkt->pts}, dts={outPkt->dts}, previousDts={lastPacketDts}).");
                }

                lastPacketPts = outPkt->pts;
                hasLastPacketPts = true;
                lastPacketDts = outPkt->dts;
                hasLastPacketDts = true;
            }

            outPkt->stream_index = outStream->index;
            long muxPacketPts = outPkt->pts;
            Throw(ffmpeg.av_interleaved_write_frame(outFmt, outPkt));
            if (encoderPacketPts != ffmpeg.AV_NOPTS_VALUE)
            {
                emittedEncodedFramePts.TryGetValue(encoderPacketPts, out int emittedCount);
                emittedEncodedFramePts[encoderPacketPts] = emittedCount + 1;
            }
            if (muxPacketPts != ffmpeg.AV_NOPTS_VALUE)
                emittedEncodedMuxPts.Add(muxPacketPts);
            outputVideoPacketCount++;
            ffmpeg.av_packet_unref(outPkt);
        }
    }

    private static (
        int ExpectedOutputFrames,
        int MismatchCount,
        int DropCount,
        int MissingEncodedFrames,
        int UnexpectedEncodedFrames,
        int CopiedPacketMismatch) EvaluateVideoFrameCoverage(
            int copiedSourcePacketCount,
            int copiedOutputPacketCount,
            IReadOnlySet<long> submittedEncodedFramePts,
            IReadOnlyDictionary<long, int> emittedEncodedFramePts)
    {
        int missingEncodedFrames = 0;
        foreach (long submittedPts in submittedEncodedFramePts)
        {
            if (!emittedEncodedFramePts.TryGetValue(submittedPts, out int emittedCount) ||
                emittedCount <= 0)
            {
                missingEncodedFrames++;
            }
        }

        int unexpectedEncodedFrames = 0;
        foreach (var (emittedPts, emittedCount) in emittedEncodedFramePts)
        {
            if (emittedCount <= 0)
                continue;

            unexpectedEncodedFrames += submittedEncodedFramePts.Contains(emittedPts)
                ? Math.Max(0, emittedCount - 1)
                : emittedCount;
        }

        int copiedPacketMismatch = (int)Math.Min(
            int.MaxValue,
            Math.Abs((long)copiedSourcePacketCount - copiedOutputPacketCount));
        int expectedOutputFrames = checked(copiedSourcePacketCount + submittedEncodedFramePts.Count);
        int mismatchCount = checked(
            copiedPacketMismatch + missingEncodedFrames + unexpectedEncodedFrames);
        int dropCount = checked(
            Math.Max(0, copiedSourcePacketCount - copiedOutputPacketCount) + missingEncodedFrames);

        return (
            expectedOutputFrames,
            mismatchCount,
            dropCount,
            missingEncodedFrames,
            unexpectedEncodedFrames,
            copiedPacketMismatch);
    }

    private unsafe void ProcessDecodedVideoFrame(
        AVFrame* frame,
        AVFrame* bgra,
        AVFrame* encFrame,
        AVCodecContext* dec,
        AVCodecContext* enc,
        SwsContext* swsDecToBgra,
        SwsContext* swsBgraToEnc,
        ref SwsContext* swsDecToEnc,
        AVStream* inStream,
        AVPacket* outPkt,
        AVStream* outStream,
        AVFormatContext* outFmt,
        int blurRadius,
        List<(int Start, int EndExclusive)>? blurRanges,
        HashSet<int>? expectedBlurFrameIndices,
        HashSet<int> appliedBlurFrameIndices,
        ref int blurRangeCursor,
        double sourceFps,
        bool forceSafeEncoding,
        int totalFrames,
        int encodeWindowStart,
        int encodeWindowEnd,
        ref int frameIndex,
        ref long swsToBgraMs,
        ref long maskMs,
        ref long swsToEncMs,
        Stopwatch encodeTimer,
        ref long lastEncodedPts,
        ref bool hasLastEncodedPts,
        ref long lastEncodedPacketPts,
        ref bool hasLastEncodedPacketPts,
        ref long lastEncodedPacketDts,
        ref bool hasLastEncodedPacketDts,
        ref WriteableBitmap? reusableFaceMask,
        ref int outputVideoPacketCount,
        VideoPacketTimestampIntegrity timestampIntegrity,
        IProgress<ExportProgress>? progress,
        ref int lastReportedFrame,
        Stopwatch swTotal,
        System.Threading.CancellationToken cancellationToken,
        int sampleWindowFrames,
        ref int sampleEncodedFrameCount,
        ref int sampleBlurredFrameCount,
        ref int encodedWindowFrameCount,
        ref int submittedVideoFrameCount,
        HashSet<long> submittedEncodedFramePts,
        Dictionary<long, long> submittedEncodedFrameDurations,
        Dictionary<long, int> emittedEncodedFramePts,
        List<long> emittedEncodedMuxPts,
        long encodedPacketFrameStep)
    {
        if (cancellationToken.IsCancellationRequested)
            throw new OperationCanceledException(cancellationToken);

        if ((frame->flags & ffmpeg.AV_FRAME_FLAG_INTERLACED) != 0)
        {
            AVFieldOrder frameFieldOrder =
                (frame->flags & ffmpeg.AV_FRAME_FLAG_TOP_FIELD_FIRST) != 0
                    ? AVFieldOrder.AV_FIELD_TT
                    : AVFieldOrder.AV_FIELD_BB;
            ThrowInterlacedAutoMosaicUnsupported(frameFieldOrder);
        }

        string? unsupportedFrameMetadata =
            FFmpegHdrMetadataGuard.FindUnsupportedMetadata(frame);
        if (unsupportedFrameMetadata != null)
            ThrowUnsupportedDynamicVideoMetadata(unsupportedFrameMetadata);
        ValidateDecodedFramePixelFidelity(frame, enc);

        if (FFmpegHdrMetadataGuard.RequiresStaticHdrConfiguration(
                frame,
                _staticHdrConfigured))
        {
            throw new InvalidOperationException(
                "정적 HDR 메타데이터를 인코더 초기화 전에 확인하지 못했습니다. " +
                "품질 저하를 막기 위해 내보내기를 중단했습니다.");
        }
        ValidateFrameStaticHdrMetadata(frame);

        if (VideoEncoderSelectionPolicy.IsHardwareEncoder(enc->codec) &&
            FFmpegHdrMetadataGuard.HasStaticHdrMetadata(frame))
        {
            throw new InvalidOperationException(
                "현재 하드웨어 인코더로는 프레임의 HDR 부가정보를 보존할 수 없어 " +
                "내보내기를 중단합니다.");
        }

        // Analysis and preview masks are keyed by sequential decoded-frame ordinal.
        // Presentation timestamps remain independent and are preserved by ResolveEncodePts.
        int decodedFrameOrdinal = frameIndex;
        if (decodedFrameOrdinal >= encodeWindowStart && decodedFrameOrdinal < encodeWindowEnd)
            encodedWindowFrameCount++;

        long encodedPts = VideoExportTimingPolicy.ResolveEncodePts(
            frame,
            inStream->time_base,
            enc->time_base,
            decodedFrameOrdinal,
            sourceFps,
            ref lastEncodedPts,
            ref hasLastEncodedPts);
        long encodedDuration = VideoExportTimingPolicy.ResolveEncodeDuration(
            frame,
            inStream->time_base,
            enc->time_base);
        if (!submittedEncodedFramePts.Add(encodedPts) ||
            !submittedEncodedFrameDurations.TryAdd(encodedPts, encodedDuration))
        {
            throw new VideoExportIntegrityException(
                $"Invalid argument: 중복 인코더 입력 PTS가 감지되었습니다 (pts={encodedPts}).");
        }

        WriteableBitmap? mask = null;
        IReadOnlyList<Rect>? faceRects = null;
        bool mightHaveMask = blurRanges == null ||
            VideoExportFrameRangePolicy.IsFrameInBlurRanges(decodedFrameOrdinal, blurRanges, ref blurRangeCursor);
        bool frameWasBlurred = false;

        if (mightHaveMask && _maskProvider is FrameMaskProvider provider)
        {
            if (provider.TryGetStoredMask(decodedFrameOrdinal, out var stored))
            {
                mask = stored;
            }
            else if (provider.TryGetFaceMaskData(decodedFrameOrdinal, out var faces))
            {
                faceRects = faces.Faces;
            }
        }
        else if (mightHaveMask)
        {
            mask = _maskProvider.GetFinalMask(decodedFrameOrdinal);
        }

        bool nativeYuvApplied = false;
        AVFrame* nativeYuvFrame = null;
        if (mask != null || (faceRects != null && faceRects.Count > 0))
        {
            bool sourceMatchesEncoder =
                frame->format == (int)enc->pix_fmt &&
                frame->width == enc->width &&
                frame->height == enc->height;

            if (sourceMatchesEncoder && MaskedVideoExporter.CanApplyNativeYuv(frame))
            {
                Throw(ffmpeg.av_frame_make_writable(frame));
                nativeYuvFrame = frame;
            }
            else if (MaskedVideoExporter.CanApplyNativeYuv(encFrame))
            {
                var tNativeSws = Stopwatch.StartNew();
                Throw(ffmpeg.av_frame_make_writable(encFrame));
                CopyFrameEncodingProperties(frame, encFrame);
                ScaleFramePreservingColor(
                    swsDecToEnc,
                    encFrame,
                    frame,
                    "YUV 품질 보존 변환");
                tNativeSws.Stop();
                swsToEncMs += tNativeSws.ElapsedMilliseconds;
                nativeYuvFrame = encFrame;
            }

            if (nativeYuvFrame != null)
            {
                var tNativeMask = Stopwatch.StartNew();
                nativeYuvApplied = mask != null
                    ? _masked.TryApplyMaskAndBlurNative(nativeYuvFrame, mask, blurRadius)
                    : _masked.TryApplyFaceRectsAndBlurNative(nativeYuvFrame, faceRects!, blurRadius);
                tNativeMask.Stop();
                maskMs += tNativeMask.ElapsedMilliseconds;
            }
        }

        if (nativeYuvApplied)
        {
            if (mask != null)
                _bitmapMaskBlurFrames++;
            else
                _directFaceBlurFrames++;
            _nativeYuvBlurFrames++;
            frameWasBlurred = true;
            VideoExportTimingPolicy.ApplyEncodingTiming(nativeYuvFrame, encodedPts, encodedDuration);

            encodeTimer.Start();
            ThrowVideoEncoderError(
                ffmpeg.avcodec_send_frame(enc, nativeYuvFrame),
                enc,
                "프레임 전송");
            DrainEncoderPackets(
                enc,
                outPkt,
                outStream,
                outFmt,
                ref lastEncodedPacketPts,
                ref hasLastEncodedPacketPts,
                ref lastEncodedPacketDts,
                ref hasLastEncodedPacketDts,
                ref outputVideoPacketCount,
                timestampIntegrity,
                submittedEncodedFrameDurations,
                emittedEncodedFramePts,
                emittedEncodedMuxPts,
                encodedPacketFrameStep);
            encodeTimer.Stop();
        }
        else if (mask != null || (faceRects != null && faceRects.Count > 0))
        {
            var tBgra = Stopwatch.StartNew();
            Throw(ffmpeg.av_frame_make_writable(bgra));
            SetBgraColorProperties(frame, bgra);
            ScaleFramePreservingColor(
                swsDecToBgra,
                bgra,
                frame,
                "BGRA 마스크 입력 변환");
            tBgra.Stop();
            swsToBgraMs += tBgra.ElapsedMilliseconds;

            var tMask = Stopwatch.StartNew();
            bool bgraBlurApplied;
            if (mask != null)
            {
                bgraBlurApplied = _masked.ApplyMaskAndBlur(bgra, mask, blurRadius, faceRects);
                _bitmapMaskBlurFrames++;
            }
            else
            {
                bgraBlurApplied = _masked.ApplyFaceRectsAndBlur(bgra, faceRects!, blurRadius);
                _directFaceBlurFrames++;
            }
            tMask.Stop();
            maskMs += tMask.ElapsedMilliseconds;
            if (!bgraBlurApplied)
            {
                throw new VideoExportIntegrityException(
                    $"프레임 {decodedFrameOrdinal}의 자동 모자이크를 BGRA 경로에 적용할 수 없습니다.");
            }
            frameWasBlurred = true;

            var tEncSws = Stopwatch.StartNew();
            Throw(ffmpeg.av_frame_make_writable(encFrame));
            CopyFrameEncodingProperties(frame, encFrame);
            ScaleFramePreservingColor(
                swsBgraToEnc,
                encFrame,
                bgra,
                "BGRA 마스크 출력 변환");
            tEncSws.Stop();
            swsToEncMs += tEncSws.ElapsedMilliseconds;

            VideoExportTimingPolicy.ApplyEncodingTiming(encFrame, encodedPts, encodedDuration);

            encodeTimer.Start();
            ThrowVideoEncoderError(
                ffmpeg.avcodec_send_frame(enc, encFrame),
                enc,
                "프레임 전송");
            DrainEncoderPackets(
                enc,
                outPkt,
                outStream,
                outFmt,
                ref lastEncodedPacketPts,
                ref hasLastEncodedPacketPts,
                ref lastEncodedPacketDts,
                ref hasLastEncodedPacketDts,
                ref outputVideoPacketCount,
                timestampIntegrity,
                submittedEncodedFrameDurations,
                emittedEncodedFramePts,
                emittedEncodedMuxPts,
                encodedPacketFrameStep);
            encodeTimer.Stop();
        }
        else
        {
            bool direct = frame->format == (int)enc->pix_fmt
                && frame->width == enc->width
                && frame->height == enc->height;

            if (!direct)
            {
                var tEncSws = Stopwatch.StartNew();
                Throw(ffmpeg.av_frame_make_writable(encFrame));
                CopyFrameEncodingProperties(frame, encFrame);
                ScaleFramePreservingColor(
                    swsDecToEnc,
                    encFrame,
                    frame,
                    "인코더 픽셀 형식 변환");
                tEncSws.Stop();
                swsToEncMs += tEncSws.ElapsedMilliseconds;

                VideoExportTimingPolicy.ApplyEncodingTiming(encFrame, encodedPts, encodedDuration);

                encodeTimer.Start();
                ThrowVideoEncoderError(
                    ffmpeg.avcodec_send_frame(enc, encFrame),
                    enc,
                    "프레임 전송");
                DrainEncoderPackets(
                    enc,
                    outPkt,
                    outStream,
                    outFmt,
                    ref lastEncodedPacketPts,
                    ref hasLastEncodedPacketPts,
                    ref lastEncodedPacketDts,
                    ref hasLastEncodedPacketDts,
                    ref outputVideoPacketCount,
                    timestampIntegrity,
                    submittedEncodedFrameDurations,
                    emittedEncodedFramePts,
                    emittedEncodedMuxPts,
                    encodedPacketFrameStep);
                encodeTimer.Stop();
            }
            else
            {
                encodeTimer.Start();
                VideoExportTimingPolicy.ApplyEncodingTiming(frame, encodedPts, encodedDuration);
                ThrowVideoEncoderError(
                    ffmpeg.avcodec_send_frame(enc, frame),
                    enc,
                    "프레임 전송");
                DrainEncoderPackets(
                    enc,
                    outPkt,
                    outStream,
                    outFmt,
                    ref lastEncodedPacketPts,
                    ref hasLastEncodedPacketPts,
                    ref lastEncodedPacketDts,
                    ref hasLastEncodedPacketDts,
                    ref outputVideoPacketCount,
                    timestampIntegrity,
                    submittedEncodedFrameDurations,
                    emittedEncodedFramePts,
                    emittedEncodedMuxPts,
                    encodedPacketFrameStep);
                encodeTimer.Stop();
            }
        }
        submittedVideoFrameCount++;
        frameIndex = decodedFrameOrdinal + 1;
        if (frameWasBlurred && expectedBlurFrameIndices?.Contains(decodedFrameOrdinal) == true)
            appliedBlurFrameIndices.Add(decodedFrameOrdinal);
        if (decodedFrameOrdinal < sampleWindowFrames)
            sampleEncodedFrameCount++;
        if (frameWasBlurred && decodedFrameOrdinal < sampleWindowFrames)
            sampleBlurredFrameCount++;

        ReportVideoProgress(progress, totalFrames, ref lastReportedFrame, decodedFrameOrdinal);
        if (decodedFrameOrdinal % 60 == 0)
        {
            Debug.WriteLine(
                $"[Export] frames={decodedFrameOrdinal}, swsToBgraMs={swsToBgraMs}, maskMs={maskMs}, swsToEncMs={swsToEncMs}, encodeMs={encodeTimer.ElapsedMilliseconds}, totalMs={swTotal.ElapsedMilliseconds}");
        }
    }

    private unsafe void FlushVideoPipeline(
        ref bool videoFlushed,
        AVCodecContext* dec,
        AVCodecContext* enc,
        AVFrame* frame,
        AVFrame* bgra,
        AVFrame* encFrame,
        SwsContext* swsDecToBgra,
        SwsContext* swsBgraToEnc,
        ref SwsContext* swsDecToEnc,
        AVStream* inStream,
        AVPacket* outPkt,
        AVStream* outStream,
        AVFormatContext* outFmt,
        int blurRadius,
        List<(int Start, int EndExclusive)>? blurRanges,
        HashSet<int>? expectedBlurFrameIndices,
        HashSet<int> appliedBlurFrameIndices,
        ref int blurRangeCursor,
        double sourceFps,
        bool forceSafeEncoding,
        int totalFrames,
        int encodeWindowStart,
        int encodeWindowEnd,
        ref int frameIndex,
        ref long swsToBgraMs,
        ref long maskMs,
        ref long swsToEncMs,
        Stopwatch encodeTimer,
        Stopwatch encoderFlushTimer,
        ref long lastEncodedPts,
        ref bool hasLastEncodedPts,
        ref long lastEncodedPacketPts,
        ref bool hasLastEncodedPacketPts,
        ref long lastEncodedPacketDts,
        ref bool hasLastEncodedPacketDts,
        ref WriteableBitmap? reusableFaceMask,
        ref int outputVideoPacketCount,
        VideoPacketTimestampIntegrity timestampIntegrity,
        IProgress<ExportProgress>? progress,
        ref int lastReportedFrame,
        Stopwatch swTotal,
        System.Threading.CancellationToken cancellationToken,
        int sampleWindowFrames,
        ref int sampleEncodedFrameCount,
        ref int sampleBlurredFrameCount,
        ref int encodedWindowFrameCount,
        ref int submittedVideoFrameCount,
        HashSet<long> submittedEncodedFramePts,
        Dictionary<long, long> submittedEncodedFrameDurations,
        Dictionary<long, int> emittedEncodedFramePts,
        List<long> emittedEncodedMuxPts,
        long encodedPacketFrameStep)
    {
        if (videoFlushed)
            return;

        int sendErr = ffmpeg.avcodec_send_packet(dec, null);
        if (sendErr < 0 && sendErr != ffmpeg.AVERROR_EOF)
            Throw(sendErr);

        int videoReceiveResult;
        while ((videoReceiveResult = ffmpeg.avcodec_receive_frame(dec, frame)) == 0)
        {
            ProcessDecodedVideoFrame(
                frame,
                bgra,
                encFrame,
                dec,
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
                expectedBlurFrameIndices,
                appliedBlurFrameIndices,
                ref blurRangeCursor,
                sourceFps,
                forceSafeEncoding,
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
                ref reusableFaceMask,
                ref outputVideoPacketCount,
                timestampIntegrity,
                progress,
                ref lastReportedFrame,
                swTotal,
                cancellationToken,
                sampleWindowFrames,
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

        if (videoReceiveResult != ffmpeg.AVERROR_EOF)
        {
            if (videoReceiveResult == ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                throw new VideoExportIntegrityException(
                    "Invalid argument: 디코더가 종료 flush 후에도 추가 입력을 요청했습니다.");
            }

            throw new VideoExportIntegrityException(
                $"비디오 디코더 flush 중 오류가 발생했습니다: {GetErrorMessage(videoReceiveResult)}");
        }

        encodeTimer.Start();
        encoderFlushTimer.Start();
        int encErr = ffmpeg.avcodec_send_frame(enc, null);
        if (encErr < 0 && encErr != ffmpeg.AVERROR_EOF)
            ThrowVideoEncoderError(encErr, enc, "종료 프레임 전송");
        DrainEncoderPackets(
            enc,
            outPkt,
            outStream,
            outFmt,
            ref lastEncodedPacketPts,
            ref hasLastEncodedPacketPts,
            ref lastEncodedPacketDts,
            ref hasLastEncodedPacketDts,
            ref outputVideoPacketCount,
            timestampIntegrity,
            submittedEncodedFrameDurations,
            emittedEncodedFramePts,
            emittedEncodedMuxPts,
            encodedPacketFrameStep);
        encoderFlushTimer.Stop();
        encodeTimer.Stop();
        videoFlushed = true;
    }

    private static unsafe List<int> CollectKeyframeFrameIndices(
        string inputPath,
        double sourceFps,
        int totalFramesHint,
        out int estimatedTotalFrames)
    {
        var keyframes = new List<int>();
        estimatedTotalFrames = 0;
        if (string.IsNullOrWhiteSpace(inputPath))
            return keyframes;

        AVFormatContext* inFmt = null;
        AVPacket* pkt = ffmpeg.av_packet_alloc();
        if (pkt == null)
            return keyframes;
        long keyframeTimestampBase = 0;
        bool hasKeyframeTimestampBase = false;

        try
        {
            if (ffmpeg.avformat_open_input(&inFmt, inputPath, null, null) < 0)
                return keyframes;
            if (ffmpeg.avformat_find_stream_info(inFmt, null) < 0)
                return keyframes;

            int videoStreamIndex = FFmpegStreamSelection.FindPrimaryVideoStreamIndex(inFmt);
            if (videoStreamIndex < 0)
                return keyframes;

            AVStream* stream = inFmt->streams[videoStreamIndex];
            int estimatedTotalFrameCount = Math.Max(0, totalFramesHint);
            int fallbackFrameIndex = 0;
            while (ffmpeg.av_read_frame(inFmt, pkt) >= 0)
            {
                if (pkt->stream_index != videoStreamIndex)
                {
                    ffmpeg.av_packet_unref(pkt);
                    continue;
                }

                int resolvedFrameIndex = VideoExportTimingPolicy.ResolveFrameIndexFromPacket(
                    pkt,
                    stream->time_base,
                    sourceFps,
                    fallbackFrameIndex,
                    estimatedTotalFrameCount,
                    ref keyframeTimestampBase,
                    ref hasKeyframeTimestampBase);
                if (resolvedFrameIndex < fallbackFrameIndex)
                    resolvedFrameIndex = fallbackFrameIndex;

                if ((pkt->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0)
                    keyframes.Add(resolvedFrameIndex);

                fallbackFrameIndex = Math.Max(fallbackFrameIndex, resolvedFrameIndex + 1);
                if (fallbackFrameIndex > estimatedTotalFrames)
                    estimatedTotalFrames = fallbackFrameIndex;

                ffmpeg.av_packet_unref(pkt);
            }
        }
        catch
        {
            // 키프레임 스캔 실패 시 일반 경로로 폴백
        }
        finally
        {
            ffmpeg.av_packet_free(&pkt);
            if (inFmt != null)
                ffmpeg.avformat_close_input(&inFmt);
        }

        if (keyframes.Count == 0)
            return keyframes;

        keyframes.Sort();
        var distinct = new List<int>(keyframes.Count);
        int prev = int.MinValue;
        foreach (int index in keyframes)
        {
            if (index == prev)
                continue;
            distinct.Add(index);
            prev = index;
        }

        return distinct;
    }

    private static int ResolveExportSampleWindowFrames(double sourceFps, int totalFrames)
    {
        double windowFrames = sourceFps > 0.0
            ? sourceFps * ExportSampleWindowSeconds
            : 30d * ExportSampleWindowSeconds;
        if (double.IsNaN(windowFrames) || double.IsInfinity(windowFrames))
            windowFrames = 30d * ExportSampleWindowSeconds;

        int resolved = (int)Math.Round(windowFrames);
        if (resolved <= 0)
            resolved = 30 * ExportSampleWindowSeconds;

        if (totalFrames > 0)
            resolved = Math.Min(resolved, totalFrames);

        return Math.Max(1, resolved);
    }

    private static unsafe (
        int InputVideoPackets,
        int OutputVideoPackets,
        int MissingVideoPacketTimestamps,
        int VideoPacketTimestampAdjustments,
        int OutputPacketPtsGapOutlierCount,
        long MaxOutputPacketPtsGap,
        long OutputCloseMs) ExportByRemuxCopy(
        AVFormatContext* inFmt,
        string outputPath,
        int videoStreamIndex,
        int totalFrames,
        double sourceFps,
        IProgress<ExportProgress>? progress,
        System.Threading.CancellationToken cancellationToken)
    {
        if (inFmt == null)
            throw new ArgumentNullException(nameof(inFmt));

            AVFormatContext* outFmt = null;
            AVPacket* pkt = ffmpeg.av_packet_alloc();
            if (pkt == null)
                throw new InvalidOperationException("패킷 버퍼를 할당하지 못했습니다.");
            var outputVideoPts = new List<long>();
            int inputVideoPackets = 0;
            int outputVideoPackets = 0;
            int missingVideoPacketTimestamps = 0;
            int videoPacketTimestampAdjustments = 0;
            int outputPacketPtsGapOutlierCount = 0;
            long maxOutputPacketPtsGap = 0;
            var outputCloseTimer = new Stopwatch();

        try
        {
            Throw(ffmpeg.avformat_alloc_output_context2(&outFmt, null, null, outputPath));
            CopyFormatPresentationMetadata(inFmt, outFmt);

            int streamCount = (int)inFmt->nb_streams;
            var streamMap = new int[streamCount];
            for (int i = 0; i < streamCount; i++)
            {
                streamMap[i] = -1;
                AVStream* inStream = inFmt->streams[i];
                AVStream* outStream = ffmpeg.avformat_new_stream(outFmt, null);
                if (outStream == null)
                    throw new InvalidOperationException("출력 스트림을 생성하지 못했습니다.");

                Throw(ffmpeg.avcodec_parameters_copy(outStream->codecpar, inStream->codecpar));
                outStream->codecpar->codec_tag = 0;
                outStream->time_base = inStream->time_base;
                CopyStreamPresentationMetadata(inStream, outStream);
                streamMap[i] = outStream->index;
            }

            if ((outFmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
                Throw(ffmpeg.avio_open(&outFmt->pb, outputPath, ffmpeg.AVIO_FLAG_WRITE));

            Throw(ffmpeg.avformat_write_header(outFmt, null));

            int lastReportedFrame = -1;
            while (ffmpeg.av_read_frame(inFmt, pkt) >= 0)
            {
                EnsureContainerStructureSupported(inFmt);
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);

                int inIndex = pkt->stream_index;
                if (inIndex < 0 || inIndex >= streamMap.Length || streamMap[inIndex] < 0)
                {
                    ffmpeg.av_packet_unref(pkt);
                    continue;
                }

                AVStream* inStream = inFmt->streams[inIndex];
                int outIndex = streamMap[inIndex];
                AVStream* outStream = outFmt->streams[outIndex];
                if (inIndex == videoStreamIndex)
                    inputVideoPackets++;
                bool isVideoPacket = inIndex == videoStreamIndex;
                bool hasMissingVideoTimestamp = isVideoPacket &&
                    (pkt->pts == ffmpeg.AV_NOPTS_VALUE || pkt->dts == ffmpeg.AV_NOPTS_VALUE);
                ffmpeg.av_packet_rescale_ts(pkt, inStream->time_base, outStream->time_base);
                if (isVideoPacket)
                {
                    if (hasMissingVideoTimestamp)
                        missingVideoPacketTimestamps++;
                }
                pkt->stream_index = outStream->index;
                pkt->pos = -1;
                long muxVideoPts = isVideoPacket ? pkt->pts : ffmpeg.AV_NOPTS_VALUE;
                long muxVideoDts = isVideoPacket ? pkt->dts : ffmpeg.AV_NOPTS_VALUE;
                Throw(ffmpeg.av_interleaved_write_frame(outFmt, pkt));
                if (inIndex == videoStreamIndex)
                {
                    outputVideoPackets++;
                    if (muxVideoPts != ffmpeg.AV_NOPTS_VALUE)
                        outputVideoPts.Add(muxVideoPts);
                }

                if (progress != null && inIndex == videoStreamIndex && totalFrames > 0 && sourceFps > 0.0)
                {
                    long ts = muxVideoPts != ffmpeg.AV_NOPTS_VALUE ? muxVideoPts : muxVideoDts;
                    if (ts != ffmpeg.AV_NOPTS_VALUE)
                    {
                        double seconds = ts * ffmpeg.av_q2d(outStream->time_base);
                        int currentFrame = (int)Math.Round(seconds * sourceFps);
                        if (currentFrame < 0) currentFrame = 0;
                        if (currentFrame > totalFrames) currentFrame = totalFrames;

                        if (currentFrame - lastReportedFrame >= 15 || currentFrame >= totalFrames)
                        {
                            progress.Report(new ExportProgress(currentFrame, totalFrames, "원본 스트림 복사 중..."));
                            lastReportedFrame = currentFrame;
                        }
                    }
                }

                ffmpeg.av_packet_unref(pkt);
            }

            maxOutputPacketPtsGap = VideoExportTimingPolicy.GetMaxSortedPresentationGap(outputVideoPts);

            progress?.Report(new ExportProgress(
                totalFrames,
                totalFrames,
                "원본 스트림 복사와 파일 검증을 마무리하는 중..."));
            Throw(ffmpeg.av_write_trailer(outFmt));
            outputCloseTimer.Start();
            CloseOutputOrThrow(outFmt);
            outputCloseTimer.Stop();
        }
        finally
        {
            ffmpeg.av_packet_free(&pkt);

            if (outFmt != null)
            {
                if ((outFmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0 && outFmt->pb != null)
                    _ = ffmpeg.avio_closep(&outFmt->pb);
                ffmpeg.avformat_free_context(outFmt);
            }
        }

        return (
            inputVideoPackets,
            outputVideoPackets,
            missingVideoPacketTimestamps,
            videoPacketTimestampAdjustments,
            outputPacketPtsGapOutlierCount,
            maxOutputPacketPtsGap,
            outputCloseTimer.ElapsedMilliseconds);
    }

    private static unsafe void CloseOutputOrThrow(AVFormatContext* format)
    {
        if (format == null ||
            format->oformat == null ||
            (format->oformat->flags & ffmpeg.AVFMT_NOFILE) != 0 ||
            format->pb == null)
        {
            return;
        }

        Throw(ffmpeg.avio_closep(&format->pb));
    }

    private static unsafe bool IsPixFmtSupported(AVCodec* encoder, AVPixelFormat fmt)
    {
#pragma warning disable CS0618 // AVCodec.pix_fmts obsolete in ffmpeg headers
        if (encoder == null || encoder->pix_fmts == null)
            return true;

        for (AVPixelFormat* p = encoder->pix_fmts; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
        {
            if (*p == fmt)
                return true;
        }

        return false;
#pragma warning restore CS0618
    }

    private static unsafe AVPixelFormat ResolveSourcePixelFormat(
        AVStream* stream,
        AVCodecContext* decoder)
    {
        if (decoder != null && decoder->pix_fmt != AVPixelFormat.AV_PIX_FMT_NONE)
            return decoder->pix_fmt;
        if (stream != null && stream->codecpar != null && stream->codecpar->format != -1)
            return (AVPixelFormat)stream->codecpar->format;
        return AVPixelFormat.AV_PIX_FMT_NONE;
    }

    private static unsafe bool IsRgbPixelFormat(AVPixelFormat pixelFormat)
    {
        AVPixFmtDescriptor* descriptor = ffmpeg.av_pix_fmt_desc_get(pixelFormat);
        return descriptor != null &&
            (descriptor->flags & ffmpeg.AV_PIX_FMT_FLAG_RGB) != 0;
    }

    private static unsafe bool CanEncodeLosslessX264Rgb(
        AVPixelFormat sourcePixelFormat,
        AVColorRange sourceColorRange,
        AVColorSpace sourceColorSpace,
        out string? error)
    {
        error = null;
        AVPixFmtDescriptor* descriptor = ffmpeg.av_pix_fmt_desc_get(sourcePixelFormat);
        if (descriptor == null)
        {
            error = $"원본 픽셀 형식({GetPixelFormatName(sourcePixelFormat)}) 정보를 확인할 수 없습니다.";
            return false;
        }
        if ((descriptor->flags & ffmpeg.AV_PIX_FMT_FLAG_RGB) == 0)
        {
            error = $"원본 픽셀 형식({GetPixelFormatName(sourcePixelFormat)})이 RGB가 아닙니다.";
            return false;
        }
        if ((descriptor->flags & ffmpeg.AV_PIX_FMT_FLAG_ALPHA) != 0)
        {
            error = $"알파 채널이 있는 RGB 형식({GetPixelFormatName(sourcePixelFormat)})은 H.264에 보존할 수 없습니다.";
            return false;
        }
        if (descriptor->nb_components != 3)
        {
            error =
                $"RGB 색상 성분 수({descriptor->nb_components})를 H.264에서 정확히 보존할 수 없습니다.";
            return false;
        }
        if (descriptor->log2_chroma_w != 0 || descriptor->log2_chroma_h != 0)
        {
            error =
                $"서브샘플링된 RGB 형식({GetPixelFormatName(sourcePixelFormat)})은 " +
                "libx264rgb에서 정확히 보존할 수 없습니다.";
            return false;
        }
        for (int component = 0; component < descriptor->nb_components; component++)
        {
            if (descriptor->comp[(uint)component].depth != 8)
            {
                error =
                    $"8비트가 아닌 RGB 형식({GetPixelFormatName(sourcePixelFormat)})은 " +
                    "libx264rgb에서 원본 정밀도를 보존할 수 없습니다.";
                return false;
            }
        }
        if (sourceColorRange is not
            AVColorRange.AVCOL_RANGE_UNSPECIFIED and not
            AVColorRange.AVCOL_RANGE_JPEG)
        {
            error =
                $"지원하지 않는 RGB 범위 태그({sourceColorRange})는 " +
                "libx264rgb full-range 경로에서 정확히 보존할 수 없습니다.";
            return false;
        }
        if (sourceColorSpace is not
            AVColorSpace.AVCOL_SPC_UNSPECIFIED and not
            AVColorSpace.AVCOL_SPC_RGB)
        {
            error =
                $"RGB가 아닌 matrix 태그({sourceColorSpace})가 지정된 RGB 영상은 " +
                "색상 해석을 바꾸지 않고 보존할 수 없습니다.";
            return false;
        }

        return true;
    }

    private static unsafe AVCodecContext* TryCreateEncoderContext(
        AVCodecID codecId,
        AVStream* inStream,
        AVCodecContext* dec,
        AVFormatContext* outFmt,
        out AVCodec* encoder,
        out EncoderQualityConfiguration qualityConfiguration,
        out string? error,
        bool forceSoftwareEncoder,
        bool forceSafeEncoding,
        VideoHdrMetadata? hdrMetadata)
    {
        encoder = null;
        qualityConfiguration = EncoderQualityConfiguration.Unconfigured;
        error = null;

        if (outFmt != null && outFmt->oformat != null)
        {
            int supported = ffmpeg.avformat_query_codec(outFmt->oformat, codecId, 0);
            if (supported <= 0)
            {
                string formatName = GetOutputFormatName(outFmt);
                error = $"출력 컨테이너({formatName})가 코덱({GetCodecName(codecId)})을 지원하지 않습니다.";
                return null;
            }
        }

        AVPixelFormat sourcePixelFormat = ResolveSourcePixelFormat(inStream, dec);
        bool requiresLosslessRgbH264 =
            codecId == AVCodecID.AV_CODEC_ID_H264 &&
            IsRgbPixelFormat(sourcePixelFormat);
        if (requiresLosslessRgbH264 &&
            !CanEncodeLosslessX264Rgb(
                sourcePixelFormat,
                dec->color_range,
                dec->colorspace,
                out string? rgbCompatibilityError))
        {
            throw new VideoExportIntegrityException(
                "RGB H.264 원본을 품질 저하 없이 내보낼 수 없습니다. " +
                rgbCompatibilityError);
        }

        bool allowTenBitHevcFallback =
            codecId == AVCodecID.AV_CODEC_ID_H264 &&
            !requiresLosslessRgbH264 &&
            GetPixelFormatBitDepth(dec->pix_fmt) > 8;
        var attemptedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string> candidateNames = requiresLosslessRgbH264
            ? new[] { "libx264rgb" }
            : VideoEncoderSelectionPolicy.GetCandidateNames(
                codecId,
                forceSoftwareEncoder,
                allowTenBitHevcFallback);
        foreach (var candidateName in candidateNames)
        {
            if (string.IsNullOrWhiteSpace(candidateName) || !attemptedNames.Add(candidateName))
                continue;

            AVCodec* candidate = ffmpeg.avcodec_find_encoder_by_name(candidateName);
            if (candidate == null ||
                (candidate->id != codecId &&
                 !(allowTenBitHevcFallback && candidate->id == AVCodecID.AV_CODEC_ID_HEVC)))
                continue;

            if (hdrMetadata?.HasStaticMetadata == true && VideoEncoderSelectionPolicy.IsHardwareEncoder(candidate))
            {
                error = VideoEncoderSelectionPolicy.AppendError(
                    error,
                    candidateName,
                    "HDR mastering/CLL 메타데이터 전달이 검증되지 않은 하드웨어 인코더입니다.");
                continue;
            }

            AVChromaLocation sourceChromaLocation = ResolveSourceChromaLocation(inStream, dec);
            if (VideoEncoderSelectionPolicy.RequiresSoftwareEncoderForChromaLocation(sourceChromaLocation) &&
                VideoEncoderSelectionPolicy.IsHardwareEncoder(candidate))
            {
                error = VideoEncoderSelectionPolicy.AppendError(
                    error,
                    candidateName,
                    $"원본 chroma 위치({sourceChromaLocation}) 보존이 검증되지 않은 하드웨어 인코더입니다.");
                continue;
            }

            if (outFmt != null && outFmt->oformat != null &&
                ffmpeg.avformat_query_codec(outFmt->oformat, candidate->id, 0) <= 0)
            {
                error = VideoEncoderSelectionPolicy.AppendError(
                    error,
                    candidateName,
                    $"출력 컨테이너({GetOutputFormatName(outFmt)})가 코덱({GetCodecName(candidate->id)})을 지원하지 않습니다.");
                continue;
            }

            var ctx = TryOpenEncoderContext(
                candidate,
                inStream,
                dec,
                outFmt,
                out var candidateQualityConfiguration,
                out var openError,
                forceSafeEncoding,
                hdrMetadata);
            if (ctx != null)
            {
                encoder = candidate;
                qualityConfiguration = candidateQualityConfiguration;
                return ctx;
            }

            Debug.WriteLine(
                $"[ExportEncoderCandidate] name={candidateName}, codec={GetCodecName(candidate->id)}, " +
                $"opened=false, error={openError ?? "unknown"}");
            error = VideoEncoderSelectionPolicy.AppendError(error, candidateName, openError);
        }

        if (requiresLosslessRgbH264)
        {
            throw new VideoExportIntegrityException(
                "RGB H.264 원본은 검증된 무손실 libx264rgb 경로로만 내보낼 수 있습니다. " +
                $"인코더 초기화 실패: {error ?? "libx264rgb를 찾을 수 없습니다."}");
        }

        AVCodec* fallback = VideoEncoderSelectionPolicy.SelectFallbackEncoder(
            codecId,
            forceSoftwareEncoder || hdrMetadata?.HasStaticMetadata == true);
        if (fallback == null)
        {
            error = VideoEncoderSelectionPolicy.AppendError(
                error,
                GetCodecName(codecId),
                $"인코더를 찾을 수 없습니다(코덱: {GetCodecName(codecId)}). FFmpeg 빌드에 해당 인코더가 포함되어 있지 않을 수 있습니다.");
            return null;
        }

        string fallbackName = fallback->name != null
            ? (Marshal.PtrToStringAnsi((IntPtr)fallback->name) ?? GetCodecName(codecId))
            : GetCodecName(codecId);
        if (attemptedNames.Add(fallbackName))
        {
            var ctx = TryOpenEncoderContext(
                fallback,
                inStream,
                dec,
                outFmt,
                out var fallbackQualityConfiguration,
                out var fallbackError,
                forceSafeEncoding,
                hdrMetadata);
            if (ctx != null)
            {
                encoder = fallback;
                qualityConfiguration = fallbackQualityConfiguration;
                return ctx;
            }

            error = VideoEncoderSelectionPolicy.AppendError(error, fallbackName, fallbackError);
        }

        return null;
    }

    private static unsafe AVCodecContext* TryOpenEncoderContext(
        AVCodec* encoder,
        AVStream* inStream,
        AVCodecContext* dec,
        AVFormatContext* outFmt,
        out EncoderQualityConfiguration qualityConfiguration,
        out string? error,
        bool forceSafeEncoding,
        VideoHdrMetadata? hdrMetadata)
    {
        qualityConfiguration = EncoderQualityConfiguration.Unconfigured;
        error = null;
        AVCodecContext* ctx = ffmpeg.avcodec_alloc_context3(encoder);
        if (ctx == null)
        {
            error = "인코더 컨텍스트를 만들 수 없습니다.";
            return null;
        }

        string encoderName = GetEncoderName(encoder);
        bool isLosslessX264Rgb = string.Equals(
            encoderName,
            "libx264rgb",
            StringComparison.OrdinalIgnoreCase);
        AVPixelFormat sourcePixelFormat = ResolveSourcePixelFormat(inStream, dec);
        ctx->width = dec->width;
        ctx->height = dec->height;
        ctx->pix_fmt = sourcePixelFormat;
        if (isLosslessX264Rgb)
        {
            if (!CanEncodeLosslessX264Rgb(
                    sourcePixelFormat,
                    dec->color_range,
                    dec->colorspace,
                    out string? rgbCompatibilityError))
            {
                error = rgbCompatibilityError;
                ffmpeg.avcodec_free_context(&ctx);
                return null;
            }
            if (!IsPixFmtSupported(encoder, AVPixelFormat.AV_PIX_FMT_BGR24))
            {
                error = "libx264rgb가 검증된 BGR24 입력 형식을 지원하지 않습니다.";
                ffmpeg.avcodec_free_context(&ctx);
                return null;
            }
            ctx->pix_fmt = AVPixelFormat.AV_PIX_FMT_BGR24;
        }
        ctx->time_base = inStream->time_base;
        ctx->framerate = inStream->r_frame_rate.num != 0 ? inStream->r_frame_rate : inStream->avg_frame_rate;
        if (ctx->framerate.num == 0 || ctx->framerate.den == 0)
            ctx->framerate = new AVRational { num = 30, den = 1 };
        if ((ctx->time_base.num <= 0 || ctx->time_base.den <= 0) &&
            ctx->framerate.num > 0 &&
            ctx->framerate.den > 0)
        {
            AVRational stableTimeBase = ffmpeg.av_inv_q(ctx->framerate);
            if (stableTimeBase.num > 0 && stableTimeBase.den > 0)
                ctx->time_base = stableTimeBase;
        }
        if (ctx->time_base.num <= 0 || ctx->time_base.den <= 0)
            ctx->time_base = inStream->time_base;

        long sourceBitrate = ResolveSourceVideoBitrate(inStream, dec);
        int targetBitrate = ResolveHighQualityTargetBitrate(
            sourceBitrate,
            ctx->width,
            ctx->height,
            ctx->framerate,
            encoder->id);

        bool usesSoftwareConstantQuality = VideoEncoderSelectionPolicy.UsesSoftwareConstantQuality(encoderName);
        if (usesSoftwareConstantQuality)
        {
            ctx->bit_rate = 0;
            ctx->rc_max_rate = 0;
            ctx->rc_buffer_size = 0;
        }
        else
        {
            ctx->bit_rate = targetBitrate;
            ctx->rc_max_rate = ClampBitrate((long)targetBitrate * 2L);
            ctx->rc_buffer_size = ClampBitrate((long)targetBitrate * 4L);
        }

        if (encoder->id == inStream->codecpar->codec_id)
        {
            if (inStream->codecpar->profile != -99)
                ctx->profile = inStream->codecpar->profile;
            if (!isLosslessX264Rgb && inStream->codecpar->level > 0)
                ctx->level = inStream->codecpar->level;
        }

        ctx->sample_aspect_ratio = inStream->sample_aspect_ratio;
        ctx->color_range = dec->color_range;
        ctx->color_primaries = dec->color_primaries;
        ctx->color_trc = dec->color_trc;
        ctx->colorspace = dec->colorspace;
        ctx->chroma_sample_location = ResolveSourceChromaLocation(inStream, dec);
        if (isLosslessX264Rgb)
        {
            ctx->color_range = AVColorRange.AVCOL_RANGE_JPEG;
            ctx->colorspace = AVColorSpace.AVCOL_SPC_RGB;
            ctx->chroma_sample_location = AVChromaLocation.AVCHROMA_LOC_UNSPECIFIED;
        }
        ctx->flags |= ffmpeg.AV_CODEC_FLAG_FRAME_DURATION;

        if ((outFmt->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
            ctx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

        if (!IsPixFmtSupported(encoder, ctx->pix_fmt))
            ctx->pix_fmt = PickPreferredPixelFormat(encoder, ctx->pix_fmt);

        string? pixelFormatLoss = GetPixelFormatLossReason(sourcePixelFormat, ctx->pix_fmt);
        if (!string.IsNullOrWhiteSpace(pixelFormatLoss))
        {
            error = $"원본 픽셀 품질을 보존할 수 없습니다: {pixelFormatLoss}";
            ffmpeg.avcodec_free_context(&ctx);
            return null;
        }

        if (forceSafeEncoding)
        {
            ctx->max_b_frames = 0;
            ctx->gop_size = 60;
        }
        ctx->thread_count = VideoEncoderSelectionPolicy.IsHardwareEncoder(encoder)
            ? 0
            : Math.Max(1, Environment.ProcessorCount - 2);

        if (hdrMetadata?.HasStaticMetadata == true)
        {
            string hdrEncoderName = GetEncoderName(encoder);
            bool isX265 = hdrEncoderName.Contains("x265", StringComparison.OrdinalIgnoreCase);
            bool isSvtAv1 =
                encoder->id == AVCodecID.AV_CODEC_ID_AV1 &&
                hdrEncoderName.Contains("svtav1", StringComparison.OrdinalIgnoreCase);
            if (!isX265 && !isSvtAv1)
            {
                error = $"인코더({hdrEncoderName})가 HDR mastering/CLL 메타데이터 보존을 지원하지 않습니다.";
                ffmpeg.avcodec_free_context(&ctx);
                return null;
            }

            if (isX265 && !hdrMetadata.CanConfigureX265)
            {
                error = "불완전한 HDR mastering/CLL 메타데이터를 x265에 정확히 전달할 수 없습니다.";
                ffmpeg.avcodec_free_context(&ctx);
                return null;
            }

            if (isSvtAv1 &&
                !CanConfigureSvtAv1StaticHdr(hdrMetadata, out string? hdrCompatibilityError))
            {
                error = hdrCompatibilityError;
                ffmpeg.avcodec_free_context(&ctx);
                return null;
            }

            if (isSvtAv1 &&
                !TryConfigureEncoderStaticHdrMetadata(ctx, hdrMetadata, out string? hdrError))
            {
                error = hdrError;
                ffmpeg.avcodec_free_context(&ctx);
                return null;
            }
        }

        qualityConfiguration = ApplyHighQualityEncoderOptions(
            ctx,
            encoder,
            forceSafeEncoding,
            hdrMetadata);
        if (!qualityConfiguration.RequiredOptionsApplied)
        {
            error =
                $"필수 품질 옵션을 적용하지 못했습니다: {qualityConfiguration.FailedOptions}";
            ffmpeg.avcodec_free_context(&ctx);
            return null;
        }

        int openErr = ffmpeg.avcodec_open2(ctx, encoder, null);
        if (openErr < 0)
        {
            error = GetErrorMessage(openErr);
            ffmpeg.avcodec_free_context(&ctx);
            return null;
        }

        return ctx;
    }

    private static unsafe bool CanConfigureSvtAv1StaticHdr(
        VideoHdrMetadata hdrMetadata,
        out string? error)
    {
        error = null;
        if (hdrMetadata.MasteringDisplayPayload != null)
        {
            fixed (byte* payload = hdrMetadata.MasteringDisplayPayload)
            {
                AVMasteringDisplayMetadata* mastering =
                    (AVMasteringDisplayMetadata*)payload;
                if (mastering->has_primaries == 0 || mastering->has_luminance == 0)
                {
                    error =
                        "SVT-AV1은 부분 mastering display 메타데이터를 원본 형태로 보존할 수 없습니다.";
                    return false;
                }

                for (uint color = 0; color < 3; color++)
                {
                    for (uint coordinate = 0; coordinate < 2; coordinate++)
                    {
                        if (!IsExactlyRepresentableHdrRational(
                                mastering->display_primaries[color][coordinate],
                                1L << 16,
                                ushort.MaxValue))
                        {
                            error =
                                "SVT-AV1 Q16 범위에서 정확히 표현할 수 없는 mastering display 색좌표입니다.";
                            return false;
                        }
                    }
                }
                for (uint coordinate = 0; coordinate < 2; coordinate++)
                {
                    if (!IsExactlyRepresentableHdrRational(
                            mastering->white_point[coordinate],
                            1L << 16,
                            ushort.MaxValue))
                    {
                        error =
                            "SVT-AV1 Q16 범위에서 정확히 표현할 수 없는 mastering display 백색점입니다.";
                        return false;
                    }
                }
                if (!IsExactlyRepresentableHdrRational(
                        mastering->max_luminance,
                        1L << 8,
                        uint.MaxValue) ||
                    mastering->max_luminance.num <= 0 ||
                    !IsExactlyRepresentableHdrRational(
                        mastering->min_luminance,
                        1L << 14,
                        uint.MaxValue))
                {
                    error =
                        "SVT-AV1 고정폭 범위에서 정확히 표현할 수 없는 mastering display 휘도입니다.";
                    return false;
                }
            }
        }

        if (hdrMetadata.ContentLightPayload != null)
        {
            fixed (byte* payload = hdrMetadata.ContentLightPayload)
            {
                AVContentLightMetadata* contentLight = (AVContentLightMetadata*)payload;
                if (contentLight->MaxCLL == 0 ||
                    contentLight->MaxCLL > ushort.MaxValue ||
                    contentLight->MaxFALL > ushort.MaxValue)
                {
                    error =
                        "SVT-AV1 16비트 범위에서 정확히 표현할 수 없는 content light 메타데이터입니다.";
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsExactlyRepresentableHdrRational(
        AVRational value,
        long scale,
        ulong maxEncodedValue)
    {
        if (value.den <= 0 || value.num < 0 || scale <= 0)
            return false;

        long scaledNumerator = (long)value.num * scale;
        if (scaledNumerator % value.den != 0)
            return false;

        long encodedValue = scaledNumerator / value.den;
        return encodedValue >= 0 && (ulong)encodedValue <= maxEncodedValue;
    }

    private static unsafe AVFieldOrder ResolveSourceFieldOrder(
        AVStream* stream,
        AVCodecContext* decoder)
    {
        if (decoder != null && decoder->field_order != AVFieldOrder.AV_FIELD_UNKNOWN)
            return decoder->field_order;
        if (stream != null &&
            stream->codecpar != null &&
            stream->codecpar->field_order != AVFieldOrder.AV_FIELD_UNKNOWN)
        {
            return stream->codecpar->field_order;
        }
        return AVFieldOrder.AV_FIELD_UNKNOWN;
    }

    private static bool IsInterlacedFieldOrder(AVFieldOrder fieldOrder)
    {
        return fieldOrder is
            AVFieldOrder.AV_FIELD_TT or
            AVFieldOrder.AV_FIELD_BB or
            AVFieldOrder.AV_FIELD_TB or
            AVFieldOrder.AV_FIELD_BT;
    }

    private static void ThrowInterlacedAutoMosaicUnsupported(AVFieldOrder fieldOrder)
    {
        throw new VideoExportIntegrityException(
            "인터레이스 영상은 현재 자동 모자이크 시 필드 순서를 안전하게 보존할 수 없어 " +
            $"내보내기를 중단했습니다(fieldOrder={fieldOrder}). 원본 영상은 변경되지 않았습니다.");
    }

    private static unsafe AVChromaLocation ResolveSourceChromaLocation(
        AVStream* stream,
        AVCodecContext* decoder)
    {
        if (decoder != null &&
            decoder->chroma_sample_location != AVChromaLocation.AVCHROMA_LOC_UNSPECIFIED)
        {
            return decoder->chroma_sample_location;
        }
        if (stream != null &&
            stream->codecpar != null &&
            stream->codecpar->chroma_location != AVChromaLocation.AVCHROMA_LOC_UNSPECIFIED)
        {
            return stream->codecpar->chroma_location;
        }
        return AVChromaLocation.AVCHROMA_LOC_UNSPECIFIED;
    }

    private static int EstimateHighQualityBitrate(int width, int height, AVRational framerate)
    {
        long baseFloor;
        long pixels = Math.Max(1L, (long)width * height);
        if (pixels <= 1280L * 720L)
            baseFloor = 6_000_000;
        else if (pixels <= 1920L * 1080L)
            baseFloor = 10_000_000;
        else if (pixels <= 2560L * 1440L)
            baseFloor = 16_000_000;
        else if (pixels <= 3840L * 2160L)
            baseFloor = 28_000_000;
        else
            baseFloor = 40_000_000;

        double fps = framerate.den != 0 ? ffmpeg.av_q2d(framerate) : 0.0;
        if (fps <= 0.0)
            fps = 30.0;
        if (fps > 30.0)
            baseFloor = (long)Math.Round(baseFloor * (fps / 30.0));

        return ClampBitrate(baseFloor);
    }

    private static int ResolveHighQualityTargetBitrate(
        long sourceBitrate,
        int width,
        int height,
        AVRational framerate,
        AVCodecID codecId)
    {
        int resolutionFloor = EstimateHighQualityBitrate(width, height, framerate);
        int boundedSourceBitrate = ClampBitrate(sourceBitrate);
        int targetBitrate = boundedSourceBitrate > 0
            ? ClampBitrate((long)boundedSourceBitrate * 3L / 2L)
            : resolutionFloor;

        if (codecId == AVCodecID.AV_CODEC_ID_AV1)
            targetBitrate = Math.Max(targetBitrate, resolutionFloor);

        return Math.Max(targetBitrate, 2_000_000);
    }

    private static int ClampBitrate(long value)
    {
        if (value <= 0)
            return 0;
        if (value > int.MaxValue)
            return int.MaxValue;
        return (int)value;
    }

    private static unsafe AVPixelFormat PickPreferredPixelFormat(AVCodec* encoder, AVPixelFormat preferred)
    {
#pragma warning disable CS0618 // AVCodec.pix_fmts obsolete in ffmpeg headers
        if (encoder == null || encoder->pix_fmts == null)
        {
            if (preferred != AVPixelFormat.AV_PIX_FMT_NONE)
                return preferred;
            return AVPixelFormat.AV_PIX_FMT_YUV420P;
        }

        AVPixelFormat first = AVPixelFormat.AV_PIX_FMT_NONE;
        for (AVPixelFormat* p = encoder->pix_fmts; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
        {
            if (first == AVPixelFormat.AV_PIX_FMT_NONE)
                first = *p;
            if (*p == preferred)
                return preferred;
        }

        if (preferred != AVPixelFormat.AV_PIX_FMT_NONE)
        {
            int loss = 0;
            AVPixelFormat best = ffmpeg.avcodec_find_best_pix_fmt_of_list(
                encoder->pix_fmts,
                preferred,
                0,
                &loss);
            if (best != AVPixelFormat.AV_PIX_FMT_NONE)
                return best;
        }

        if (first != AVPixelFormat.AV_PIX_FMT_NONE)
            return first;
        return AVPixelFormat.AV_PIX_FMT_YUV420P;
#pragma warning restore CS0618
    }

    private static unsafe EncoderQualityConfiguration ApplyHighQualityEncoderOptions(
        AVCodecContext* ctx,
        AVCodec* encoder,
        bool forceSafeEncoding,
        VideoHdrMetadata? hdrMetadata)
    {
        if (ctx == null || encoder == null || encoder->name == null)
            return EncoderQualityConfiguration.Unconfigured;

        string name = Marshal.PtrToStringAnsi((IntPtr)encoder->name) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
            return EncoderQualityConfiguration.Unconfigured;

        var applied = new List<string>();
        var failed = new List<string>();
        var requiredFailures = new List<string>();
        void SetOption(string key, string value, bool required)
        {
            string setting = $"{key}={value}";
            if (TrySetEncoderOption(ctx, key, value, out string? optionError))
            {
                applied.Add(setting);
                return;
            }

            string failure = $"{setting}:{optionError ?? "unknown"}";
            failed.Add(failure);
            if (required)
                requiredFailures.Add(failure);
        }

        bool isX264Rgb = string.Equals(name, "libx264rgb", StringComparison.OrdinalIgnoreCase);
        bool isX264 = name.Contains("x264", StringComparison.OrdinalIgnoreCase);
        bool isX265 = name.Contains("x265", StringComparison.OrdinalIgnoreCase);
        bool isSvtAv1 = name.Contains("svtav1", StringComparison.OrdinalIgnoreCase);
        bool isAomAv1 = name.Contains("aom-av1", StringComparison.OrdinalIgnoreCase);
        bool isNvenc = name.Contains("nvenc", StringComparison.OrdinalIgnoreCase);
        bool isQsv = name.Contains("qsv", StringComparison.OrdinalIgnoreCase);
        bool isAmf = name.Contains("amf", StringComparison.OrdinalIgnoreCase);
        bool isVideoToolbox = name.Contains("videotoolbox", StringComparison.OrdinalIgnoreCase);
        string mode;

        if (isX264Rgb)
        {
            SetOption("preset", "fast", required: true);
            SetOption("crf", "0", required: true);
            mode = forceSafeEncoding
                ? "lossless-crf0-fast-rgb-safe"
                : "lossless-crf0-fast-rgb";
        }
        else if (isX264)
        {
            SetOption("preset", "fast", required: true);
            SetOption("crf", "14", required: true);
            mode = forceSafeEncoding ? "crf14-fast-safe" : "crf14-fast";
        }
        else if (isX265)
        {
            SetOption("preset", "fast", required: true);
            if (hdrMetadata?.HasStaticMetadata == true)
            {
                string x265Params = hdrMetadata.ToX265Params();
                SetOption("crf", "12", required: true);
                if (string.IsNullOrWhiteSpace(x265Params))
                {
                    const string failure = "x265-params=empty:missing-hdr-parameters";
                    failed.Add(failure);
                    requiredFailures.Add(failure);
                }
                else
                {
                    SetOption("x265-params", x265Params, required: true);
                }
                mode = forceSafeEncoding ? "crf12-fast-safe-hdr" : "crf12-fast-hdr";
            }
            else
            {
                SetOption("crf", "16", required: true);
                mode = forceSafeEncoding ? "crf16-fast-safe" : "crf16-fast";
            }
        }
        else if (isSvtAv1)
        {
            SetOption("preset", "6", required: true);
            SetOption("crf", "12", required: true);
            SetOption("svtav1-params", "tune=0", required: true);
            mode = forceSafeEncoding ? "crf12-preset6-vq-safe" : "crf12-preset6-vq";
        }
        else if (isAomAv1)
        {
            SetOption("usage", "good", required: true);
            SetOption("cpu-used", "4", required: true);
            SetOption("crf", "12", required: true);
            SetOption("row-mt", "1", required: false);
            SetOption("tune", "psnr", required: false);
            mode = forceSafeEncoding ? "crf12-cpu4-good-safe" : "crf12-cpu4-good";
        }
        else if (isNvenc)
        {
            SetOption("preset", "p6", required: true);
            SetOption("tune", "hq", required: true);
            SetOption("rc", "vbr", required: true);
            SetOption("cq", "12", required: true);
            SetOption("multipass", "qres", required: false);
            SetOption("spatial_aq", "1", required: false);
            SetOption("temporal_aq", "1", required: false);
            SetOption("rc-lookahead", "20", required: false);
            SetOption("extra_sei", "1", required: false);
            mode = forceSafeEncoding ? "p6-hq-vbr-cq12-safe" : "p6-hq-vbr-cq12";
        }
        else if (isQsv)
        {
            SetOption("preset", "veryslow", required: true);
            if (encoder->id == AVCodecID.AV_CODEC_ID_H264)
                SetOption("look_ahead", "1", required: false);
            SetOption("look_ahead_depth", "40", required: false);
            SetOption("rdo", "1", required: false);
            SetOption("adaptive_i", "1", required: false);
            SetOption("adaptive_b", "1", required: false);
            mode = forceSafeEncoding
                ? "veryslow-vbr-bitrate-safe"
                : "veryslow-vbr-bitrate";
        }
        else if (isAmf)
        {
            SetOption("usage", "high_quality", required: true);
            SetOption("quality", "quality", required: true);
            SetOption("rc", "hqvbr", required: true);
            SetOption("preanalysis", "1", required: false);
            SetOption("vbaq", "1", required: false);
            SetOption("high_motion_quality_boost_enable", "1", required: false);
            mode = forceSafeEncoding ? "high-quality-hqvbr-safe" : "high-quality-hqvbr";
        }
        else if (isVideoToolbox)
        {
            SetOption("realtime", "false", required: true);
            SetOption("prio_speed", "0", required: true);
            SetOption("spatial_aq", "1", required: false);
            mode = forceSafeEncoding
                ? "bitrate-quality-priority-safe"
                : "bitrate-quality-priority";
        }
        else
        {
            mode = forceSafeEncoding ? "bitrate-bounded-safe" : "bitrate-bounded";
        }

        return new EncoderQualityConfiguration(
            mode,
            requiredFailures.Count == 0,
            string.Join('|', applied),
            string.Join('|', failed));
    }

    private static unsafe bool TrySetEncoderOption(
        AVCodecContext* ctx,
        string key,
        string value,
        out string? error)
    {
        if (ctx == null || ctx->priv_data == null)
        {
            error = "encoder-private-options-unavailable";
            return false;
        }

        int result = ffmpeg.av_opt_set(ctx->priv_data, key, value, 0);
        if (result < 0)
        {
            error = GetErrorMessage(result);
            Debug.WriteLine($"[ExportEncoderOption] key={key}, value={value}, applied=false, error={error}");
            return false;
        }

        error = null;
        Debug.WriteLine($"[ExportEncoderOption] key={key}, value={value}, applied=true");
        return true;
    }

    private static unsafe string GetEncoderName(AVCodec* encoder)
    {
        if (encoder == null || encoder->name == null)
            return "unknown";
        return Marshal.PtrToStringAnsi((IntPtr)encoder->name) ?? "unknown";
    }

    private static unsafe string GetPixelFormatName(AVPixelFormat pixelFormat)
    {
        if (pixelFormat == AVPixelFormat.AV_PIX_FMT_NONE)
            return "unknown";
        string? name = ffmpeg.av_get_pix_fmt_name(pixelFormat);
        return string.IsNullOrWhiteSpace(name) ? pixelFormat.ToString() : name;
    }

    private static unsafe int GetPixelFormatBitDepth(AVPixelFormat pixelFormat)
    {
        if (pixelFormat == AVPixelFormat.AV_PIX_FMT_NONE)
            return 0;
        AVPixFmtDescriptor* descriptor = ffmpeg.av_pix_fmt_desc_get(pixelFormat);
        return descriptor == null || descriptor->nb_components == 0
            ? 0
            : descriptor->comp[0].depth;
    }

    private static unsafe string? GetPixelFormatLossReason(
        AVPixelFormat sourcePixelFormat,
        AVPixelFormat outputPixelFormat)
    {
        if (sourcePixelFormat == AVPixelFormat.AV_PIX_FMT_NONE ||
            outputPixelFormat == AVPixelFormat.AV_PIX_FMT_NONE)
        {
            return null;
        }

        AVPixFmtDescriptor* source = ffmpeg.av_pix_fmt_desc_get(sourcePixelFormat);
        AVPixFmtDescriptor* output = ffmpeg.av_pix_fmt_desc_get(outputPixelFormat);
        if (source == null || output == null)
            return null;

        bool sourceIsRgb = (source->flags & ffmpeg.AV_PIX_FMT_FLAG_RGB) != 0;
        bool outputIsRgb = (output->flags & ffmpeg.AV_PIX_FMT_FLAG_RGB) != 0;
        if (sourceIsRgb != outputIsRgb)
        {
            return $"색상 모델 변경 {GetPixelFormatName(sourcePixelFormat)} -> " +
                   GetPixelFormatName(outputPixelFormat);
        }

        bool sourceHasAlpha = (source->flags & ffmpeg.AV_PIX_FMT_FLAG_ALPHA) != 0;
        bool outputHasAlpha = (output->flags & ffmpeg.AV_PIX_FMT_FLAG_ALPHA) != 0;
        if (sourceHasAlpha && !outputHasAlpha)
        {
            return $"알파 채널 유실 {GetPixelFormatName(sourcePixelFormat)} -> " +
                   GetPixelFormatName(outputPixelFormat);
        }

        int comparableComponents = Math.Min(source->nb_components, output->nb_components);
        for (int component = 0; component < comparableComponents; component++)
        {
            int sourceBitDepth = source->comp[(uint)component].depth;
            int outputBitDepth = output->comp[(uint)component].depth;
            if (sourceBitDepth > 0 && outputBitDepth > 0 && outputBitDepth < sourceBitDepth)
            {
                return $"비트 심도 하락 {GetPixelFormatName(sourcePixelFormat)}({sourceBitDepth}) -> " +
                       $"{GetPixelFormatName(outputPixelFormat)}({outputBitDepth})";
            }
        }

        if (output->nb_components < source->nb_components)
        {
            return $"색상 성분 감소 {GetPixelFormatName(sourcePixelFormat)}({source->nb_components}) -> " +
                   $"{GetPixelFormatName(outputPixelFormat)}({output->nb_components})";
        }

        if (source->nb_components >= 3 && output->nb_components >= 3 &&
            (output->log2_chroma_w > source->log2_chroma_w ||
             output->log2_chroma_h > source->log2_chroma_h))
        {
            return $"색차 해상도 하락 {GetPixelFormatName(sourcePixelFormat)} -> " +
                   GetPixelFormatName(outputPixelFormat);
        }

        return null;
    }

    private unsafe void ValidateDecodedFramePixelFidelity(
        AVFrame* frame,
        AVCodecContext* encoderContext)
    {
        if (frame == null || encoderContext == null)
        {
            throw new VideoExportIntegrityException(
                "프레임 픽셀 품질 검증에 필요한 디코더 또는 인코더 정보가 없습니다.");
        }
        if (frame->width != encoderContext->width || frame->height != encoderContext->height)
        {
            throw new VideoExportIntegrityException(
                $"영상 도중 해상도가 변경됐습니다({frame->width}x{frame->height} -> " +
                $"{encoderContext->width}x{encoderContext->height}). " +
                "자동 크기 변환 없이 원본 품질을 보존할 수 없어 내보내기를 중단했습니다.");
        }

        AVPixelFormat framePixelFormat = (AVPixelFormat)frame->format;
        string? pixelFormatLoss = GetPixelFormatLossReason(
            framePixelFormat,
            encoderContext->pix_fmt);
        if (!string.IsNullOrWhiteSpace(pixelFormatLoss))
        {
            throw new VideoExportIntegrityException(
                $"영상 도중 원본 픽셀 형식이 변경되어 품질을 보존할 수 없습니다: {pixelFormatLoss}");
        }

        if (!_losslessX264RgbConfigured)
            return;

        if (!CanEncodeLosslessX264Rgb(
                framePixelFormat,
                frame->color_range,
                frame->colorspace,
                out string? rgbCompatibilityError))
        {
            throw new VideoExportIntegrityException(
                "영상 도중 RGB H.264 픽셀 속성이 변경됐습니다. " +
                rgbCompatibilityError);
        }
        if (frame->color_primaries != AVColorPrimaries.AVCOL_PRI_UNSPECIFIED &&
            frame->color_primaries != encoderContext->color_primaries)
        {
            throw new VideoExportIntegrityException(
                $"영상 도중 RGB color primaries가 변경됐습니다({frame->color_primaries}).");
        }
        if (frame->color_trc != AVColorTransferCharacteristic.AVCOL_TRC_UNSPECIFIED &&
            frame->color_trc != encoderContext->color_trc)
        {
            throw new VideoExportIntegrityException(
                $"영상 도중 RGB transfer characteristic이 변경됐습니다({frame->color_trc}).");
        }
    }

    private static unsafe long ResolveSourceVideoBitrate(AVStream* stream, AVCodecContext* decoder)
    {
        if (stream != null && stream->codecpar != null && stream->codecpar->bit_rate > 0)
            return stream->codecpar->bit_rate;
        if (decoder != null && decoder->bit_rate > 0)
            return decoder->bit_rate;
        return 0;
    }

    private static unsafe long ResolveTargetVideoBitrateForSummary(
        AVCodec* encoder,
        AVStream* stream,
        AVCodecContext* decoder,
        AVCodecContext* encoderContext)
    {
        if (encoderContext != null && encoderContext->bit_rate > 0)
            return encoderContext->bit_rate;
        if (!VideoEncoderSelectionPolicy.IsHardwareEncoder(encoder))
            return 0;

        long sourceBitrate = ResolveSourceVideoBitrate(stream, decoder);
        return sourceBitrate > 0
            ? ClampBitrate(sourceBitrate * 3L / 2L)
            : 0;
    }

    private static unsafe void CopyFrameEncodingProperties(AVFrame* source, AVFrame* destination)
    {
        if (source == null || destination == null || source == destination)
            return;

        ffmpeg.av_frame_side_data_free(
            &destination->side_data,
            &destination->nb_side_data);
        ffmpeg.av_dict_free(&destination->metadata);
        Throw(ffmpeg.av_frame_copy_props(destination, source));
    }

    private static unsafe SwsContext* CreateDynamicSwsContext(string stage)
    {
        SwsContext* context = ffmpeg.sws_alloc_context();
        if (context == null)
        {
            throw new VideoExportIntegrityException(
                $"{stage}용 색상 변환 컨텍스트를 만들 수 없습니다.");
        }

        int optionResult = ffmpeg.av_opt_set_int(
            context,
            "sws_flags",
            (long)SwsFlags.SWS_FAST_BILINEAR,
            0);
        if (optionResult < 0)
        {
            ffmpeg.sws_freeContext(context);
            throw new VideoExportIntegrityException(
                $"{stage}의 색상 변환 옵션을 설정할 수 없습니다: " +
                GetErrorMessage(optionResult));
        }

        return context;
    }

    private static unsafe void ScaleFramePreservingColor(
        SwsContext* context,
        AVFrame* destination,
        AVFrame* source,
        string stage)
    {
        if (context == null || destination == null || source == null)
        {
            throw new VideoExportIntegrityException(
                $"{stage}에 필요한 프레임 또는 색상 변환 컨텍스트가 없습니다.");
        }

        int result = ffmpeg.sws_scale_frame(context, destination, source);
        if (result < 0)
        {
            throw new VideoExportIntegrityException(
                $"{stage} 중 원본 색 공간, 범위 또는 색차 위치를 보존할 수 없습니다: " +
                GetErrorMessage(result));
        }
    }

    private static unsafe void SetBgraColorProperties(AVFrame* source, AVFrame* bgra)
    {
        if (source == null || bgra == null)
            return;

        CopyFrameEncodingProperties(source, bgra);
        bgra->color_range = AVColorRange.AVCOL_RANGE_JPEG;
        bgra->color_primaries = source->color_primaries;
        bgra->color_trc = source->color_trc;
        bgra->colorspace = AVColorSpace.AVCOL_SPC_RGB;
        bgra->chroma_location = AVChromaLocation.AVCHROMA_LOC_UNSPECIFIED;
        bgra->sample_aspect_ratio = source->sample_aspect_ratio;
    }

    private static void ThrowUnsupportedDynamicVideoMetadata(string metadataName)
    {
        throw new InvalidOperationException(
            $"{metadataName} 영상 메타데이터는 현재 내보내기에서 원본 그대로 보존할 수 없습니다. " +
            "품질 저하를 막기 위해 내보내기를 중단했습니다.");
    }

    private static unsafe void EnsureContainerStructureSupported(AVFormatContext* format)
    {
        string? unsupportedStructureKey =
            FFmpegContainerStructureGuard.FindUnsupportedStructure(format);
        if (unsupportedStructureKey == null)
            return;

        string unsupportedStructure = unsupportedStructureKey == "programs"
            ? "프로그램 단위 스트림 구성"
            : "IAMF 등 스트림 그룹 구성";

        throw new InvalidOperationException(
            $"{unsupportedStructure}은 현재 내보내기에서 원본 그대로 보존할 수 없습니다. " +
            "스트림 구성 유실을 막기 위해 내보내기를 중단했습니다.");
    }

    private static unsafe void CopyStreamPresentationMetadata(AVStream* source, AVStream* output)
    {
        if (source == null || output == null)
            return;

        output->avg_frame_rate = source->avg_frame_rate;
        output->r_frame_rate = source->r_frame_rate;
        output->sample_aspect_ratio = source->sample_aspect_ratio;
        output->disposition = source->disposition;
        Throw(ffmpeg.av_dict_copy(&output->metadata, source->metadata, 0));
        CopyCodecPresentationSideData(source->codecpar, output->codecpar);
    }

    private static unsafe void CopyFormatPresentationMetadata(
        AVFormatContext* source,
        AVFormatContext* output)
    {
        if (source == null || output == null)
            return;

        Throw(ffmpeg.av_dict_copy(&output->metadata, source->metadata, 0));
        int chapterCount = checked((int)source->nb_chapters);
        if (chapterCount <= 0)
            return;
        if (source->chapters == null)
            throw new InvalidOperationException("원본 챕터 목록 정보가 없습니다.");

        output->chapters = (AVChapter**)ffmpeg.av_calloc(
            (ulong)chapterCount,
            (ulong)sizeof(AVChapter*));
        if (output->chapters == null)
            throw new InvalidOperationException("챕터 목록 메모리를 할당할 수 없습니다.");

        for (int i = 0; i < chapterCount; i++)
        {
            AVChapter* sourceChapter = source->chapters[i];
            if (sourceChapter == null)
                throw new InvalidOperationException($"원본 챕터 {i + 1} 정보가 없습니다.");

            AVChapter* outputChapter = (AVChapter*)ffmpeg.av_mallocz((ulong)sizeof(AVChapter));
            if (outputChapter == null)
                throw new InvalidOperationException($"챕터 {i + 1} 메모리를 할당할 수 없습니다.");

            outputChapter->id = sourceChapter->id;
            outputChapter->time_base = sourceChapter->time_base;
            outputChapter->start = sourceChapter->start;
            outputChapter->end = sourceChapter->end;
            output->chapters[i] = outputChapter;
            output->nb_chapters++;
            Throw(ffmpeg.av_dict_copy(&outputChapter->metadata, sourceChapter->metadata, 0));
        }
    }

    private static unsafe void CopyCodecPresentationSideData(
        AVCodecParameters* source,
        AVCodecParameters* output)
    {
        if (source == null ||
            output == null ||
            source->coded_side_data == null ||
            source->nb_coded_side_data <= 0)
        {
            return;
        }

        for (int i = 0; i < source->nb_coded_side_data; i++)
        {
            AVPacketSideData* sourceSideData = source->coded_side_data + i;
            if (sourceSideData == null ||
                sourceSideData->data == null ||
                sourceSideData->size == 0 ||
                !IsPresentationSideData(sourceSideData->type))
            {
                continue;
            }

            ffmpeg.av_packet_side_data_remove(
                output->coded_side_data,
                &output->nb_coded_side_data,
                sourceSideData->type);
            ulong sourceSize = (ulong)sourceSideData->size;
            AVPacketSideData* outputSideData = ffmpeg.av_packet_side_data_new(
                &output->coded_side_data,
                &output->nb_coded_side_data,
                sourceSideData->type,
                sourceSize,
                0);
            if (outputSideData == null || outputSideData->data == null)
            {
                throw new InvalidOperationException(
                    $"스트림 부가정보({sourceSideData->type})를 복사할 수 없습니다.");
            }

            Buffer.MemoryCopy(
                sourceSideData->data,
                outputSideData->data,
                checked((long)sourceSize),
                checked((long)sourceSize));
        }
    }

    private static bool IsPresentationSideData(AVPacketSideDataType type)
    {
        return type is
            AVPacketSideDataType.AV_PKT_DATA_DISPLAYMATRIX or
            AVPacketSideDataType.AV_PKT_DATA_STEREO3D or
            AVPacketSideDataType.AV_PKT_DATA_MASTERING_DISPLAY_METADATA or
            AVPacketSideDataType.AV_PKT_DATA_SPHERICAL or
            AVPacketSideDataType.AV_PKT_DATA_CONTENT_LIGHT_LEVEL or
            AVPacketSideDataType.AV_PKT_DATA_ICC_PROFILE or
            AVPacketSideDataType.AV_PKT_DATA_DYNAMIC_HDR10_PLUS or
            AVPacketSideDataType.AV_PKT_DATA_AMBIENT_VIEWING_ENVIRONMENT;
    }

    private static unsafe bool TryInitAudioTranscode(
        AVStream* inAudioStream,
        AVFormatContext* outFmt,
        out AVCodecContext* audioDec,
        out AVCodecContext* audioEnc,
        out AVStream* outAudioStream,
        out SwrContext* swr,
        out AVAudioFifo* audioFifo,
        out string? error)
    {
        audioDec = null;
        audioEnc = null;
        outAudioStream = null;
        swr = null;
        audioFifo = null;
        error = null;

        AVCodec* decoder = ffmpeg.avcodec_find_decoder(inAudioStream->codecpar->codec_id);
        if (decoder == null)
        {
            error = $"오디오 디코더를 찾을 수 없습니다(코덱: {GetCodecName(inAudioStream->codecpar->codec_id)}).";
            return false;
        }

        audioDec = ffmpeg.avcodec_alloc_context3(decoder);
        if (audioDec == null)
        {
            error = "오디오 디코더 컨텍스트를 만들 수 없습니다.";
            return false;
        }

        int decErr = ffmpeg.avcodec_parameters_to_context(audioDec, inAudioStream->codecpar);
        if (decErr < 0)
        {
            error = $"오디오 디코더 초기화 실패: {GetErrorMessage(decErr)}";
            return false;
        }

        int openDecErr = ffmpeg.avcodec_open2(audioDec, decoder, null);
        if (openDecErr < 0)
        {
            error = $"오디오 디코더 열기 실패: {GetErrorMessage(openDecErr)}";
            return false;
        }

        AVCodec* aacEncoder = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC);
        if (aacEncoder == null)
        {
            error = "AAC 인코더를 찾을 수 없습니다.";
            return false;
        }

        audioEnc = ffmpeg.avcodec_alloc_context3(aacEncoder);
        if (audioEnc == null)
        {
            error = "오디오 인코더 컨텍스트를 만들 수 없습니다.";
            return false;
        }

        int inChannels = audioDec->ch_layout.nb_channels;
        if (inChannels <= 0)
            inChannels = inAudioStream->codecpar->ch_layout.nb_channels;
        if (inChannels <= 0)
            inChannels = 2;

        int inSampleRate = audioDec->sample_rate > 0 ? audioDec->sample_rate : inAudioStream->codecpar->sample_rate;
        if (inSampleRate <= 0)
            inSampleRate = 44100;
        if (audioDec->sample_rate <= 0)
            audioDec->sample_rate = inSampleRate;

        AVChannelLayout inLayout = audioDec->ch_layout;
        if (inLayout.nb_channels <= 0)
            inLayout = inAudioStream->codecpar->ch_layout;
        if (inLayout.nb_channels <= 0)
        {
            AVChannelLayout* inLayoutPtr = stackalloc AVChannelLayout[1];
            *inLayoutPtr = inLayout;
            ffmpeg.av_channel_layout_default(inLayoutPtr, inChannels);
            inLayout = *inLayoutPtr;
        }

        AVChannelLayout outLayout = GetChannelLayout(aacEncoder, inLayout, inChannels);
        if (outLayout.nb_channels <= 0)
        {
            AVChannelLayout* outLayoutDefaultPtr = stackalloc AVChannelLayout[1];
            *outLayoutDefaultPtr = outLayout;
            ffmpeg.av_channel_layout_default(outLayoutDefaultPtr, inChannels);
            outLayout = *outLayoutDefaultPtr;
        }

        audioEnc->ch_layout = outLayout;
        audioEnc->sample_rate = GetSampleRate(aacEncoder, inSampleRate);
        audioEnc->sample_fmt = GetSampleFmt(aacEncoder, audioDec->sample_fmt);
        audioEnc->time_base = new AVRational { num = 1, den = audioEnc->sample_rate };

        if (inAudioStream->codecpar->bit_rate > 0)
            audioEnc->bit_rate = inAudioStream->codecpar->bit_rate;
        else
            audioEnc->bit_rate = 128_000;

        if (outFmt->oformat != null && (outFmt->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
            audioEnc->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

        int openEncErr = ffmpeg.avcodec_open2(audioEnc, aacEncoder, null);
        if (openEncErr < 0)
        {
            error = $"AAC 인코더 열기 실패: {GetErrorMessage(openEncErr)}";
            return false;
        }

        outAudioStream = ffmpeg.avformat_new_stream(outFmt, aacEncoder);
        if (outAudioStream == null)
        {
            error = "오디오 출력 스트림을 생성하지 못했습니다.";
            return false;
        }

        int parErr = ffmpeg.avcodec_parameters_from_context(outAudioStream->codecpar, audioEnc);
        if (parErr < 0)
        {
            error = $"오디오 파라미터 설정 실패: {GetErrorMessage(parErr)}";
            return false;
        }

        outAudioStream->time_base = audioEnc->time_base;

        AVChannelLayout outLayoutCopy = audioEnc->ch_layout;
        AVChannelLayout* outLayoutPtr = stackalloc AVChannelLayout[1];
        AVChannelLayout* inLayoutPtr2 = stackalloc AVChannelLayout[1];
        *outLayoutPtr = outLayoutCopy;
        *inLayoutPtr2 = inLayout;
        SwrContext* swrLocal = null;
        int swrErr = ffmpeg.swr_alloc_set_opts2(
            &swrLocal,
            outLayoutPtr,
            audioEnc->sample_fmt,
            audioEnc->sample_rate,
            inLayoutPtr2,
            audioDec->sample_fmt,
            inSampleRate,
            0,
            null);
        if (swrErr < 0)
        {
            error = $"오디오 리샘플러 초기화 실패: {GetErrorMessage(swrErr)}";
            return false;
        }

        swr = swrLocal;
        swrErr = ffmpeg.swr_init(swr);
        if (swrErr < 0)
        {
            error = $"오디오 리샘플러 열기 실패: {GetErrorMessage(swrErr)}";
            return false;
        }

        int outChannels = audioEnc->ch_layout.nb_channels;
        if (outChannels <= 0)
            outChannels = inChannels;
        audioFifo = ffmpeg.av_audio_fifo_alloc(audioEnc->sample_fmt, outChannels, 1);
        if (audioFifo == null)
        {
            error = "오디오 FIFO 초기화 실패.";
            return false;
        }

        return true;
    }

    private static unsafe AVSampleFormat GetSampleFmt(AVCodec* encoder, AVSampleFormat preferred)
    {
        if (encoder == null || encoder->sample_fmts == null)
            return preferred;

        for (AVSampleFormat* p = encoder->sample_fmts; *p != AVSampleFormat.AV_SAMPLE_FMT_NONE; p++)
        {
            if (*p == preferred)
                return preferred;
        }

        return encoder->sample_fmts[0];
    }

    private static unsafe int GetSampleRate(AVCodec* encoder, int preferred)
    {
        if (encoder == null || encoder->supported_samplerates == null)
            return preferred > 0 ? preferred : 44100;

        int* rates = encoder->supported_samplerates;
        int best = rates[0];
        for (int* p = rates; *p != 0; p++)
        {
            if (*p == preferred)
                return preferred;
            best = *p;
        }

        return best > 0 ? best : (preferred > 0 ? preferred : 44100);
    }

    private static unsafe AVChannelLayout GetChannelLayout(
        AVCodec* encoder,
        AVChannelLayout preferred,
        int preferredChannels)
    {
        if (encoder == null || encoder->ch_layouts == null)
            return preferred;

        AVChannelLayout* layouts = encoder->ch_layouts;
        AVChannelLayout best = layouts[0];
        for (AVChannelLayout* p = layouts; p->nb_channels != 0; p++)
        {
            int channels = p->nb_channels;
            if (channels == preferredChannels)
                return *p;
            best = *p;
        }

        return best;
    }

    private static unsafe void ConvertAndQueueAudioFrame(
        AVFrame* input,
        AVCodecContext* audioDec,
        AVCodecContext* audioEnc,
        SwrContext* swr,
        AVAudioFifo* fifo,
        AVFrame* convFrame)
    {
        long delay = ffmpeg.swr_get_delay(swr, audioDec->sample_rate);
        int outSamples = (int)ffmpeg.av_rescale_rnd(
            delay + input->nb_samples,
            audioEnc->sample_rate,
            audioDec->sample_rate,
            AVRounding.AV_ROUND_UP);

        convFrame->nb_samples = outSamples;
        convFrame->ch_layout = audioEnc->ch_layout;
        convFrame->format = (int)audioEnc->sample_fmt;
        convFrame->sample_rate = audioEnc->sample_rate;
        Throw(ffmpeg.av_frame_get_buffer(convFrame, 0));

        int converted = ffmpeg.swr_convert(
            swr,
            convFrame->extended_data,
            outSamples,
            input->extended_data,
            input->nb_samples);
        if (converted < 0)
            Throw(converted);
        if (converted == 0)
        {
            ffmpeg.av_frame_unref(convFrame);
            return;
        }

        int newSize = ffmpeg.av_audio_fifo_size(fifo) + converted;
        int reallocErr = ffmpeg.av_audio_fifo_realloc(fifo, newSize);
        if (reallocErr < 0)
            Throw(reallocErr);

        int written = ffmpeg.av_audio_fifo_write(fifo, (void**)convFrame->extended_data, converted);
        if (written < 0)
            Throw(written);
        if (written < converted)
            throw new InvalidOperationException("오디오 FIFO에 샘플을 충분히 쓰지 못했습니다.");

        ffmpeg.av_frame_unref(convFrame);
    }

    private static unsafe void DrainAudioFifo(
        AVAudioFifo* fifo,
        AVCodecContext* audioEnc,
        AVStream* outAudioStream,
        AVFormatContext* outFmt,
        AVPacket* outPkt,
        AVFrame* outFrame,
        ref long audioPts,
        ref long lastPacketPts,
        ref bool hasLastPacketPts,
        ref long lastPacketDts,
        ref bool hasLastPacketDts,
        bool flush)
    {
        if (fifo == null || audioEnc == null || outAudioStream == null || outFmt == null)
            return;

        int frameSize = audioEnc->frame_size > 0 ? audioEnc->frame_size : 1024;
        bool variable = audioEnc->codec != null
            && (audioEnc->codec->capabilities & ffmpeg.AV_CODEC_CAP_VARIABLE_FRAME_SIZE) != 0;

        while (true)
        {
            int available = ffmpeg.av_audio_fifo_size(fifo);
            if (available <= 0)
                break;

            int nbSamples;
            bool pad = false;
            if (variable)
            {
                nbSamples = Math.Min(available, frameSize);
            }
            else
            {
                if (available < frameSize)
                {
                    if (!flush)
                        break;
                    nbSamples = frameSize;
                    pad = true;
                }
                else
                {
                    nbSamples = frameSize;
                }
            }

            outFrame->nb_samples = nbSamples;
            outFrame->ch_layout = audioEnc->ch_layout;
            outFrame->format = (int)audioEnc->sample_fmt;
            outFrame->sample_rate = audioEnc->sample_rate;
            Throw(ffmpeg.av_frame_get_buffer(outFrame, 0));

            int readSamples = Math.Min(available, nbSamples);
            int read = ffmpeg.av_audio_fifo_read(fifo, (void**)outFrame->extended_data, readSamples);
            if (read < 0)
                Throw(read);
            if (pad && read < nbSamples)
            {
                int silence = nbSamples - read;
                int channels = audioEnc->ch_layout.nb_channels;
                ffmpeg.av_samples_set_silence(outFrame->extended_data, read, silence, channels, audioEnc->sample_fmt);
            }

            outFrame->pts = audioPts;
            audioPts += nbSamples;

            Throw(ffmpeg.avcodec_send_frame(audioEnc, outFrame));
            ffmpeg.av_frame_unref(outFrame);

                while (ffmpeg.avcodec_receive_packet(audioEnc, outPkt) == 0)
                {
                    outPkt->stream_index = outAudioStream->index;
                    ffmpeg.av_packet_rescale_ts(outPkt, audioEnc->time_base, outAudioStream->time_base);
                    VideoExportTimingPolicy.NormalizeEncodedPacketTimestamps(outPkt, ref lastPacketPts, ref hasLastPacketPts, ref lastPacketDts, ref hasLastPacketDts);
                    Throw(ffmpeg.av_interleaved_write_frame(outFmt, outPkt));
                    ffmpeg.av_packet_unref(outPkt);
                }

            if (!flush && !variable && ffmpeg.av_audio_fifo_size(fifo) < frameSize)
                break;
        }
    }

    private static string GetCodecName(AVCodecID codecId)
    {
        string? name = null;
        try
        {
            name = ffmpeg.avcodec_get_name(codecId);
        }
        catch
        {
            // 일부 바인딩은 포인터 시그니처를 사용하므로 예외가 날 수 있음
        }

        if (string.IsNullOrWhiteSpace(name))
            return codecId.ToString();

        return name;
    }

    private static string GetMediaTypeName(AVMediaType mediaType)
    {
        return mediaType switch
        {
            AVMediaType.AVMEDIA_TYPE_VIDEO => "추가 영상",
            AVMediaType.AVMEDIA_TYPE_SUBTITLE => "자막",
            AVMediaType.AVMEDIA_TYPE_DATA => "데이터",
            AVMediaType.AVMEDIA_TYPE_ATTACHMENT => "첨부",
            _ => "보조"
        };
    }

    private static unsafe string GetOutputFormatName(AVFormatContext* outFmt)
    {
        if (outFmt == null || outFmt->oformat == null || outFmt->oformat->name == null)
            return "unknown";

        return Marshal.PtrToStringAnsi((IntPtr)outFmt->oformat->name) ?? "unknown";
    }

    private static string GetErrorMessage(int err)
    {
        byte* buf = stackalloc byte[1024];
        ffmpeg.av_strerror(err, buf, 1024);
        return System.Text.Encoding.UTF8.GetString(new ReadOnlySpan<byte>(buf, 1024)).TrimEnd('\0');
    }

    private static bool IsInvalidArgumentError(Exception ex)
    {
        if (ex is not InvalidOperationException)
            return false;

        return ex.Message.Contains("Invalid argument", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldRetryWithSafeEncoding(InvalidOperationException ex)
    {
        if (ex is VideoExportIntegrityException)
            return false;

        return ex is VideoEncoderException { IsHardwareEncoder: true } ||
               IsInvalidArgumentError(ex);
    }

    private bool ShouldRetryWithH264Fallback(InvalidOperationException ex)
    {
        return !_staticHdrConfigured &&
               ShouldRetryWithSafeEncoding(ex) &&
               IsInvalidArgumentError(ex);
    }

    private static unsafe void ThrowVideoEncoderError(
        int errorCode,
        AVCodecContext* context,
        string operation)
    {
        if (errorCode >= 0)
            return;

        AVCodec* encoder = context == null ? null : context->codec;
        string encoderName = GetEncoderName(encoder);
        bool isHardwareEncoder = VideoEncoderSelectionPolicy.IsHardwareEncoder(encoder);
        string detail = GetErrorMessage(errorCode);
        throw new VideoEncoderException(
            $"비디오 인코더({encoderName}) {operation} 실패: {detail}",
            errorCode,
            operation,
            encoderName,
            isHardwareEncoder);
    }

    private static void Throw(int err)
    {
        if (err >= 0) return;

        byte* buf = stackalloc byte[1024];
        ffmpeg.av_strerror(err, buf, 1024);
        throw new InvalidOperationException(
            System.Text.Encoding.UTF8.GetString(new ReadOnlySpan<byte>(buf, 1024)).TrimEnd('\0'));
    }
}
