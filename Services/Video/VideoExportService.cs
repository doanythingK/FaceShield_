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
    private const bool EnableHybridCopyWindow = true;
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
        bool allowHybridCopy = true)
    {
        string finalOutputPath = Path.GetFullPath(outputPath);
        string stagedOutputPath = BuildStagedOutputPath(finalOutputPath);
        try
        {
            try
            {
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
            catch (InvalidOperationException ex) when (IsInvalidArgumentError(ex))
            {
                Debug.WriteLine($"[Export] mode=fallback-safe로 재시도: Invalid argument 발생. {ex.Message}");
                try
                {
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
                catch (InvalidOperationException nestedEx) when (IsInvalidArgumentError(nestedEx))
                {
                    Debug.WriteLine($"[Export] mode=fallback-h264로 재시도: 안전 모드에서도 실패. {nestedEx.Message}");
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

            LastExportSummary = LastExportSummary with { OutputCommitted = true };
            string committedLine =
                $"[ExportCommitted] runId={LastExportSummary.RunId ?? "n/a"}, mode={commitMode}";
            Debug.WriteLine(committedLine);
            Debug.WriteLine(LastExportSummary.ToLogLine());
            RunMetricsLog.AppendRunLines(
                LastExportSummary.RunId,
                committedLine,
                LastExportSummary.ToLogLine());
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
        _masked.ResetTemporalState();
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
        long encodeMs = 0;
        long lastAudioEncPacketPts = -1;
        bool hasLastAudioEncPacketPts = false;
        long lastAudioEncPacketDts = -1;
        bool hasLastAudioEncPacketDts = false;
        long lastVideoCopyPacketPts = -1;
        bool hasLastVideoCopyPacketPts = false;
        long lastVideoCopyPacketDts = -1;
        bool hasLastVideoCopyPacketDts = false;
        long lastAudioCopyPacketPts = -1;
        bool hasLastAudioCopyPacketPts = false;
        long lastAudioCopyPacketDts = -1;
        bool hasLastAudioCopyPacketDts = false;
        bool wasLastPacketEncoded = false;
        int inputVideoPacketCount = 0;
        int outputVideoPacketCount = 0;
        int copiedVideoPacketCount = 0;
        int copiedSourceVideoPacketCount = 0;
        int encodedSourceVideoPacketCount = 0;
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
        long keyframeTimestampBase = 0;
        bool hasKeyframeTimestampBase = false;
        long frameTimestampBase = 0;
        bool hasFrameTimestampBase = false;
        int packetFrameIndexReliabilityFailureCount = 0;
        var swTotal = Stopwatch.StartNew();

        try
        {
            // ───────── input ─────────
            Throw(ffmpeg.avformat_open_input(&inFmt, inputPath, null, null));
            Throw(ffmpeg.avformat_find_stream_info(inFmt, null));

            for (int i = 0; i < inFmt->nb_streams; i++)
            {
                var stream = inFmt->streams[i];
                if (stream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    if (videoStreamIndex < 0)
                        videoStreamIndex = i;
                }
                else if (stream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                {
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
            hybridCopyVideoFrameStep = GetVideoFrameStep(sourceFps, inStream->time_base);
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
                blurFrameSet = BuildBlurFrameSet(frameMaskProvider, totalFrames);
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
                        remuxCounts.MissingVideoPacketTimestamps > 0 ||
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
                        CopiedSourceVideoPackets: remuxCounts.OutputVideoPackets,
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
                        TargetVideoBitrate: ResolveSourceVideoBitrate(inStream, null));
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

                blurRanges = BuildBlurFrameRanges(blurFrameSet);
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
                    blurRanges = AlignRangesToKeyframes(blurRanges, keyframes, totalFrames);
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

            AVCodec* decoder = ffmpeg.avcodec_find_decoder(inStream->codecpar->codec_id);
            dec = ffmpeg.avcodec_alloc_context3(decoder);
            Throw(ffmpeg.avcodec_parameters_to_context(dec, inStream->codecpar));
            Throw(ffmpeg.avcodec_open2(dec, decoder, null));

            // ───────── output ─────────
            Throw(ffmpeg.avformat_alloc_output_context2(&outFmt, null, null, outputPath));

            AVCodec* encoder;
            AVCodecID inputCodecId = inStream->codecpar->codec_id;
            AVCodecID encoderInputCodecId = forceH264Fallback && inputCodecId != AVCodecID.AV_CODEC_ID_H264
                ? AVCodecID.AV_CODEC_ID_H264
                : inputCodecId;
            enc = TryCreateEncoderContext(
                encoderInputCodecId,
                inStream,
                dec,
                outFmt,
                out encoder,
                out var encoderError,
                forceSoftwareEncoder,
                forceSafeEncoding);

            string? exportNotice = null;
            if (enc == null)
            {
                string inputName = GetCodecName(encoderInputCodecId);
                var fallbackCodecId = AVCodecID.AV_CODEC_ID_H264;
                string fallbackName = GetCodecName(fallbackCodecId);
                string reason = string.IsNullOrWhiteSpace(encoderError)
                    ? "원본 코덱 인코더를 찾을 수 없습니다."
                    : encoderError;
                exportNotice = $"원본 코덱({inputName}) 인코더를 사용할 수 없어 {fallbackName}로 내보냅니다. 사유: {reason}";

                enc = TryCreateEncoderContext(fallbackCodecId, inStream, dec, outFmt, out encoder, out var fallbackError, forceSoftwareEncoder, forceSafeEncoding);
                if (enc == null)
                    throw new InvalidOperationException($"대체 인코더 초기화 실패: {fallbackError}");
            }

            AVStream* outAudioStream = null;
            string? audioNotice = null;
            bool audioCopy = false;
            bool audioReencode = false;
            long audioPts = 0;
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
                        audioNotice = "오디오 스트림을 생성하지 못해 소리를 포함하지 않습니다.";
                    else
                    {
                        Throw(ffmpeg.avcodec_parameters_copy(outAudioStream->codecpar, inAudioStream->codecpar));
                        outAudioStream->codecpar->codec_tag = 0;
                        outAudioStream->time_base = inAudioStream->time_base;
                        audioCopy = true;
                    }
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
                        audioNotice = $"오디오 변환 초기화 실패: {audioError}. 소리를 포함하지 않습니다.";
                    }
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

            if (dec != null && dec->has_b_frames > 0)
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
                !IsHardwareEncoder(encoder) &&
                (hybridEncodeWindow.Value.Start > 0 ||
                 hybridEncodeWindow.Value.EndExclusive < totalFrames);

            if (dec != null && dec->has_b_frames > 0)
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
                    IsKeyframeFrameIndex(hybridCandidateKeyframes, window.Start);
                bool validEndBoundary =
                    window.EndExclusive >= totalFrames ||
                    IsKeyframeFrameIndex(hybridCandidateKeyframes, window.EndExclusive);
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
            long sourceEncodedFrameStep = enc != null ? GetVideoFrameStep(sourceFps, enc->time_base) : 1;
            long predictedHybridCopyFrameStep = useHybridCopyWindow
                ? (inStream->time_base.num > 0 && inStream->time_base.den > 0
                    ? GetVideoFrameStep(sourceFps, inStream->time_base)
                    : GetVideoFrameStep(sourceFps, enc->time_base))
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
                hybridCopyVideoFrameStep = GetVideoFrameStep(sourceFps, outStream->time_base);
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
            encodedPacketFrameStep = GetVideoFrameStep(sourceFps, outStream->time_base);
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
                encodedPacketFrameStep = GetVideoFrameStep(sourceFps, outStream->time_base);
            }
            if (encodedPacketFrameStep <= 0)
                encodedPacketFrameStep = 1;
            if (hybridCopyVideoFrameStep <= 0)
                hybridCopyVideoFrameStep = encodedPacketFrameStep;

            if ((outFmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
                Throw(ffmpeg.avio_open(&outFmt->pb, outputPath, ffmpeg.AVIO_FLAG_WRITE));

            Throw(ffmpeg.avformat_write_header(outFmt, null));

            // Muxers may rewrite outStream->time_base while writing the header.
            // Packet gap checks must use the final output stream time base.
            long muxedOutputFrameStep = GetVideoFrameStep(sourceFps, outStream->time_base);
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

            // ✅ sws context (정정)
            swsDecToBgra = ffmpeg.sws_getContext(
                dec->width, dec->height, dec->pix_fmt,
                dec->width, dec->height, AVPixelFormat.AV_PIX_FMT_BGRA,
                (int)SwsFlags.SWS_FAST_BILINEAR,
                null, null, null);

            swsBgraToEnc = ffmpeg.sws_getContext(
                enc->width, enc->height, AVPixelFormat.AV_PIX_FMT_BGRA,
                enc->width, enc->height, enc->pix_fmt,
                (int)SwsFlags.SWS_FAST_BILINEAR,
                null, null, null);

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
            int lastResolvedFrameIndex = -1;
            int lastReportedFrame = -1;
            bool videoFlushed = false;

            while (ffmpeg.av_read_frame(inFmt, pkt) >= 0)
            {
                if (pkt->stream_index == audioStreamIndex)
                {
                    if (audioCopy && outAudioStream != null && inAudioStream != null)
                    {
                        ffmpeg.av_packet_rescale_ts(pkt, inAudioStream->time_base, outAudioStream->time_base);
                        _ = NormalizeCopiedPacketTimestamps(
                            pkt,
                            ref lastAudioCopyPacketPts,
                            ref hasLastAudioCopyPacketPts,
                            ref lastAudioCopyPacketDts,
                            ref hasLastAudioCopyPacketDts);
                        pkt->stream_index = outAudioStream->index;
                        Throw(ffmpeg.av_interleaved_write_frame(outFmt, pkt));
                        ffmpeg.av_packet_unref(pkt);
                        continue;
                    }

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

                int resolvedPacketFrameIndex = ResolveFrameIndexFromPacket(
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
                                ref blurRangeCursor,
                                sourceFps,
                                forceSafeEncoding,
                                totalFrames,
                                encodeWindowStart,
                                encodeWindowEnd,
                                ref frameIndex,
                                ref lastResolvedFrameIndex,
                                ref frameTimestampBase,
                                ref hasFrameTimestampBase,
                                ref swsToBgraMs,
                                ref maskMs,
                                ref swsToEncMs,
                                ref encodeMs,
                                ref lastEncodedPts,
                                ref hasLastEncodedPts,
                                ref lastEncodedPacketPts,
                                ref hasLastEncodedPacketPts,
                                ref lastEncodedPacketDts,
                                ref hasLastEncodedPacketDts,
                                ref reusableFaceMask,
                                ref outputVideoPacketCount,
                                ref outputPacketPtsGapOutlierCount,
                                ref maxOutputPacketPtsGap,
                                encodedTimestampIntegrity,
                            progress,
                            ref lastReportedFrame,
                            swTotal,
                            cancellationToken,
                            exportSampleWindowFrames,
                            ref sampleEncodedFrameCount,
                            ref sampleBlurredFrameCount,
                            ref encodedWindowFrameCount,
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
                            ref blurRangeCursor,
                            sourceFps,
                            forceSafeEncoding,
                            totalFrames,
                            encodeWindowStart,
                            encodeWindowEnd,
                            ref frameIndex,
                            ref lastResolvedFrameIndex,
                            ref frameTimestampBase,
                            ref hasFrameTimestampBase,
                            ref swsToBgraMs,
                            ref maskMs,
                            ref swsToEncMs,
                            ref encodeMs,
                            ref lastEncodedPts,
                            ref hasLastEncodedPts,
                            ref lastEncodedPacketPts,
                            ref hasLastEncodedPacketPts,
                            ref lastEncodedPacketDts,
                            ref hasLastEncodedPacketDts,
                            ref reusableFaceMask,
                            ref outputVideoPacketCount,
                            ref outputPacketPtsGapOutlierCount,
                            ref maxOutputPacketPtsGap,
                            encodedTimestampIntegrity,
                            progress,
                            ref lastReportedFrame,
                            swTotal,
                            cancellationToken,
                            exportSampleWindowFrames,
                            ref sampleEncodedFrameCount,
                            ref sampleBlurredFrameCount,
                            ref encodedWindowFrameCount,
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
                    bool timestampAdjusted = NormalizeCopiedPacketTimestamps(
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
            while (ffmpeg.avcodec_receive_frame(dec, frame) == 0)
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
                    ref blurRangeCursor,
                    sourceFps,
                    forceSafeEncoding,
                    totalFrames,
                    encodeWindowStart,
                    encodeWindowEnd,
                    ref frameIndex,
                    ref lastResolvedFrameIndex,
                    ref frameTimestampBase,
                    ref hasFrameTimestampBase,
                    ref swsToBgraMs,
                    ref maskMs,
                    ref swsToEncMs,
                    ref encodeMs,
                    ref lastEncodedPts,
                    ref hasLastEncodedPts,
                    ref lastEncodedPacketPts,
                    ref hasLastEncodedPacketPts,
                    ref lastEncodedPacketDts,
                    ref hasLastEncodedPacketDts,
                    ref reusableFaceMask,
                    ref outputVideoPacketCount,
                    ref outputPacketPtsGapOutlierCount,
                    ref maxOutputPacketPtsGap,
                    encodedTimestampIntegrity,
                    progress,
                    ref lastReportedFrame,
                    swTotal,
                    cancellationToken,
                    exportSampleWindowFrames,
                    ref sampleEncodedFrameCount,
                    ref sampleBlurredFrameCount,
                    ref encodedWindowFrameCount,
                    encodedPacketFrameStep);
                    ffmpeg.av_frame_unref(frame);
                }
            }

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
                ref blurRangeCursor,
                sourceFps,
                forceSafeEncoding,
                totalFrames,
                encodeWindowStart,
                encodeWindowEnd,
                            ref frameIndex,
                            ref lastResolvedFrameIndex,
                            ref frameTimestampBase,
                            ref hasFrameTimestampBase,
                            ref swsToBgraMs,
                            ref maskMs,
                            ref swsToEncMs,
                ref encodeMs,
                ref lastEncodedPts,
                ref hasLastEncodedPts,
                    ref lastEncodedPacketPts,
                    ref hasLastEncodedPacketPts,
                    ref lastEncodedPacketDts,
                    ref hasLastEncodedPacketDts,
                ref reusableFaceMask,
                ref outputVideoPacketCount,
                ref outputPacketPtsGapOutlierCount,
                ref maxOutputPacketPtsGap,
                encodedTimestampIntegrity,
                progress,
                    ref lastReportedFrame,
                    swTotal,
                    cancellationToken,
                    exportSampleWindowFrames,
                    ref sampleEncodedFrameCount,
                    ref sampleBlurredFrameCount,
                    ref encodedWindowFrameCount,
                    encodedPacketFrameStep);

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
                        NormalizeEncodedPacketTimestamps(
                            audioPkt,
                            ref lastAudioEncPacketPts,
                            ref hasLastAudioEncPacketPts,
                            ref lastAudioEncPacketDts,
                            ref hasLastAudioEncPacketDts);
                        Throw(ffmpeg.av_interleaved_write_frame(outFmt, audioPkt));
                        ffmpeg.av_packet_unref(audioPkt);
                    }
            }

            Throw(ffmpeg.av_write_trailer(outFmt));
            int sampleWindowLimit = totalFrames > 0
                ? Math.Min(exportSampleWindowFrames, totalFrames)
                : 0;
            int sampleWindowSourceFrames = Math.Max(0, Math.Min(sampleWindowLimit, sampleSourceVideoPacketCount));
            int sampleWindowProducedFrames = sampleCopiedVideoPacketCount + sampleEncodedFrameCount;
            int sampleWindowFrameShortfall = sampleWindowSourceFrames > 0
                ? Math.Max(0, sampleWindowSourceFrames - sampleWindowProducedFrames)
                : 0;
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
            int droppedVideoPackets = useHybridCopyWindow
                ? hybridCopySourcePacketLoss + hybridEncodedWindowFrameLoss
                : Math.Max(0, inputVideoPacketCount - outputVideoPacketCount);
            int outputPacketCountMismatch = (int)Math.Min(
                int.MaxValue,
                Math.Abs((long)inputVideoPacketCount - outputVideoPacketCount));
            int outputPacketDropCount = outputPacketCountMismatch;
            if (inputVideoPacketCount <= 0 || outputVideoPacketCount <= 0)
            {
                string invalidPacketCountReason =
                    $"final-output-packet-count-invalid={inputVideoPacketCount}/{outputVideoPacketCount}";
                packetDropFallbackReason = string.IsNullOrWhiteSpace(packetDropFallbackReason)
                    ? invalidPacketCountReason
                    : $"{packetDropFallbackReason}; {invalidPacketCountReason}";

                if (allowPacketDropRetry)
                {
                    shouldRetryWithFullEncode = true;
                }
                else
                {
                    throw new InvalidOperationException(
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

                if (allowPacketDropRetry)
                {
                    shouldRetryWithFullEncode = true;
                }
                else
                {
                    throw new InvalidOperationException(
                        "Invalid argument: 인코더 출력 타임스탬프 보정이 필요해 품질 보전을 위해 중단합니다.");
                }
            }
            if (sampleWindowFrameShortfall > 0)
            {
                packetDropFallbackReason =
                    string.IsNullOrWhiteSpace(packetDropFallbackReason)
                        ? $"sample-window-frame-loss={sampleWindowFrameShortfall} (source={sampleWindowSourceFrames}, copied={sampleCopiedVideoPacketCount}, encoded={sampleEncodedFrameCount})"
                        : $"{packetDropFallbackReason}; sample-window-frame-loss={sampleWindowFrameShortfall} (source={sampleWindowSourceFrames}, copied={sampleCopiedVideoPacketCount}, encoded={sampleEncodedFrameCount})";

                if (allowPacketDropRetry)
                {
                    shouldRetryWithFullEncode = true;
                }
                else
                {
                    throw new InvalidOperationException(
                        "Invalid argument: 최종 샘플 구간 출력 프레임 손실이 감지되어 품질 보전을 위해 중단합니다.");
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
                && outputPacketDropCount > 0)
            {
                shouldRetryWithFullEncode = true;
                packetDropFallbackReason =
                    string.IsNullOrWhiteSpace(packetDropFallbackReason)
                        ? $"hybrid-output-packet-loss={outputPacketDropCount} / input={inputVideoPacketCount}"
                        : $"{packetDropFallbackReason}; hybrid-output-packet-loss={outputPacketDropCount} / input={inputVideoPacketCount}";
            }
            if (outputPacketDropCount > MaxAllowedOutputPacketLoss)
            {
                string outputLossReason =
                    $"final-output-packet-count-mismatch={inputVideoPacketCount}/{outputVideoPacketCount}";
                packetDropFallbackReason = string.IsNullOrWhiteSpace(packetDropFallbackReason)
                    ? outputLossReason
                    : $"{packetDropFallbackReason}; {outputLossReason}";

                if (allowPacketDropRetry)
                {
                    shouldRetryWithFullEncode = true;
                }
                else
                {
                    throw new InvalidOperationException(
                        "Invalid argument: 최종 출력 패킷 손실 감지로 품질 보전을 위해 중단합니다.");
                }
            }

            if (droppedVideoPackets > 0)
            {
                if (useHybridCopyWindow)
                {
                    Debug.WriteLine(
                        $"[Export] packetDropHint copySource={copiedSourceVideoPacketCount}, copyOutput={copiedVideoPacketCount}, copyLoss={hybridCopySourcePacketLoss}, encodeWindow={encodeWindowStart}-{encodeWindowEnd}, encodedWindowFrames={encodedWindowFrameCount}, encodedShortfall={hybridEncodedWindowFrameLoss}, dropped={droppedVideoPackets}");
                }
                else
                {
                    Debug.WriteLine(
                        $"[Export] packetDropHint inputVideoPackets={inputVideoPacketCount}, outputVideoPackets={outputVideoPacketCount}, dropped={droppedVideoPackets}");
                }

                if (allowPacketDropRetry && useHybridCopyWindow && droppedVideoPackets > 0)
                {
                    shouldRetryWithFullEncode = true;
                    packetDropFallbackReason =
                        string.IsNullOrWhiteSpace(packetDropFallbackReason)
                            ? useHybridCopyWindow
                                ? $"copy-source={copiedSourceVideoPacketCount}, copy-output={copiedVideoPacketCount}, dropped={droppedVideoPackets}"
                                : $"input={inputVideoPacketCount}, output={outputVideoPacketCount}, dropped={droppedVideoPackets}"
                            : $"{packetDropFallbackReason}; {(
                                useHybridCopyWindow
                                    ? $"copy-source={copiedSourceVideoPacketCount}, copy-output={copiedVideoPacketCount}, dropped={droppedVideoPackets}"
                                    : $"input={inputVideoPacketCount}, output={outputVideoPacketCount}, dropped={droppedVideoPackets}")}";
                }
            }
            if (outputPacketPtsGapOutlierCount > 0)
            {
                packetDropFallbackReason =
                    string.IsNullOrWhiteSpace(packetDropFallbackReason)
                        ? $"output-pts-gap-outlier-count={outputPacketPtsGapOutlierCount}, maxGap={maxOutputPacketPtsGap}"
                        : $"{packetDropFallbackReason}; output-pts-gap-outlier-count={outputPacketPtsGapOutlierCount}, maxGap={maxOutputPacketPtsGap}";
                if (allowPacketDropRetry)
                {
                    shouldRetryWithFullEncode = true;
                }
                else
                {
                    throw new InvalidOperationException(
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
                encodeMs,
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
                EncoderQualityMode: GetEncoderQualityMode(encoder, forceSafeEncoding),
                SourcePixelFormat: GetPixelFormatName(dec->pix_fmt),
                OutputPixelFormat: GetPixelFormatName(enc->pix_fmt),
                SourceBitDepth: GetPixelFormatBitDepth(dec->pix_fmt),
                OutputBitDepth: GetPixelFormatBitDepth(enc->pix_fmt),
                SourceVideoBitrate: ResolveSourceVideoBitrate(inStream, dec),
                TargetVideoBitrate: enc->bit_rate,
                NativeYuvBlurFrames: _nativeYuvBlurFrames);
            Debug.WriteLine(
                $"[Export] done frames={frameIndex}, bitmapMaskFrames={_bitmapMaskBlurFrames}, directFaceFrames={_directFaceBlurFrames}, swsToBgraMs={swsToBgraMs}, maskMs={maskMs}, swsToEncMs={swsToEncMs}, encodeMs={encodeMs}, totalMs={swTotal.ElapsedMilliseconds}");
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
                if ((outFmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
                    ffmpeg.avio_closep(&outFmt->pb);
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
        ref int outputPacketPtsGapOutlierCount,
        ref long maxOutputPacketPtsGap,
        VideoPacketTimestampIntegrity timestampIntegrity,
        long encodedPacketFrameStep)
    {
        while (ffmpeg.avcodec_receive_packet(enc, outPkt) == 0)
        {
            long previousPacketPts = lastPacketPts;
            bool hadPreviousPacketPts = hasLastPacketPts;

            ffmpeg.av_packet_rescale_ts(outPkt, enc->time_base, outStream->time_base);
            bool hasMissingTimestamp =
                outPkt->pts == ffmpeg.AV_NOPTS_VALUE ||
                outPkt->dts == ffmpeg.AV_NOPTS_VALUE;
            if (hasMissingTimestamp)
            {
                timestampIntegrity.MissingPacketTimestamps++;
                long originalPts = outPkt->pts;
                long originalDts = outPkt->dts;
                _ = NormalizeEncodedPacketTimestamps(
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

                if (outPkt->duration <= 0)
                    outPkt->duration = Math.Max(1, encodedPacketFrameStep);
                lastPacketPts = outPkt->pts;
                hasLastPacketPts = true;
                lastPacketDts = outPkt->dts;
                hasLastPacketDts = true;
            }

            if (hadPreviousPacketPts)
            {
                long gap = Math.Abs(outPkt->pts - previousPacketPts);
                if (gap > 0 && gap > maxOutputPacketPtsGap)
                    maxOutputPacketPtsGap = gap;
            }

            outPkt->stream_index = outStream->index;
            Throw(ffmpeg.av_interleaved_write_frame(outFmt, outPkt));
            outputVideoPacketCount++;
            ffmpeg.av_packet_unref(outPkt);
        }
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
        ref int blurRangeCursor,
        double sourceFps,
        bool forceSafeEncoding,
        int totalFrames,
        int encodeWindowStart,
        int encodeWindowEnd,
        ref int frameIndex,
        ref int lastResolvedFrameIndex,
        ref long frameTimestampBase,
        ref bool hasFrameTimestampBase,
        ref long swsToBgraMs,
        ref long maskMs,
        ref long swsToEncMs,
        ref long encodeMs,
        ref long lastEncodedPts,
        ref bool hasLastEncodedPts,
        ref long lastEncodedPacketPts,
        ref bool hasLastEncodedPacketPts,
        ref long lastEncodedPacketDts,
        ref bool hasLastEncodedPacketDts,
        ref WriteableBitmap? reusableFaceMask,
        ref int outputVideoPacketCount,
        ref int outputPacketPtsGapOutlierCount,
        ref long maxOutputPacketPtsGap,
        VideoPacketTimestampIntegrity timestampIntegrity,
        IProgress<ExportProgress>? progress,
        ref int lastReportedFrame,
        Stopwatch swTotal,
        System.Threading.CancellationToken cancellationToken,
        int sampleWindowFrames,
        ref int sampleEncodedFrameCount,
        ref int sampleBlurredFrameCount,
        ref int encodedWindowFrameCount,
        long encodedPacketFrameStep)
    {
        if (cancellationToken.IsCancellationRequested)
            throw new OperationCanceledException(cancellationToken);

        int fallbackIndex = Math.Max(frameIndex, lastResolvedFrameIndex + 1);
        int resolvedFrameIndex = ResolveFrameIndexFromFrame(
            frame,
            inStream->time_base,
            sourceFps,
            fallbackIndex,
            totalFrames,
            ref frameTimestampBase,
            ref hasFrameTimestampBase);
        if (resolvedFrameIndex < fallbackIndex)
            resolvedFrameIndex = fallbackIndex;
        lastResolvedFrameIndex = resolvedFrameIndex;
        if (resolvedFrameIndex >= encodeWindowStart && resolvedFrameIndex < encodeWindowEnd)
            encodedWindowFrameCount++;
        frameIndex = resolvedFrameIndex + 1;

        long encodedPts = ResolveEncodePts(
            frame,
            inStream->time_base,
            enc->time_base,
            resolvedFrameIndex,
            sourceFps,
            ref lastEncodedPts,
            ref hasLastEncodedPts);

        WriteableBitmap? mask = null;
        IReadOnlyList<Rect>? faceRects = null;
        bool mightHaveMask = blurRanges == null || IsFrameInBlurRanges(resolvedFrameIndex, blurRanges, ref blurRangeCursor);
        bool frameWasBlurred = false;

        if (mightHaveMask && _maskProvider is FrameMaskProvider provider)
        {
            if (provider.TryGetStoredMask(resolvedFrameIndex, out var stored))
            {
                mask = stored;
            }
            else if (provider.TryGetFaceMaskData(resolvedFrameIndex, out var faces))
            {
                faceRects = faces.Faces;
            }
        }
        else if (mightHaveMask)
        {
            mask = _maskProvider.GetFinalMask(resolvedFrameIndex);
        }

        bool nativeYuvApplied = false;
        AVFrame* nativeYuvFrame = null;
        if (mask == null && faceRects != null && faceRects.Count > 0)
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
                if (swsDecToEnc == null)
                {
                    swsDecToEnc = ffmpeg.sws_getContext(
                        dec->width, dec->height, dec->pix_fmt,
                        enc->width, enc->height, enc->pix_fmt,
                        (int)SwsFlags.SWS_FAST_BILINEAR,
                        null, null, null);
                    if (swsDecToEnc == null)
                        throw new InvalidOperationException("YUV 품질 보존 변환 컨텍스트를 만들 수 없습니다.");
                }

                var tNativeSws = Stopwatch.StartNew();
                Throw(ffmpeg.av_frame_make_writable(encFrame));
                Throw(ffmpeg.sws_scale(
                    swsDecToEnc,
                    frame->data,
                    frame->linesize,
                    0,
                    frame->height,
                    encFrame->data,
                    encFrame->linesize));
                tNativeSws.Stop();
                swsToEncMs += tNativeSws.ElapsedMilliseconds;
                nativeYuvFrame = encFrame;
            }

            if (nativeYuvFrame != null)
            {
                var tNativeMask = Stopwatch.StartNew();
                nativeYuvApplied = _masked.TryApplyFaceRectsAndBlurNative(
                    nativeYuvFrame,
                    faceRects,
                    blurRadius);
                tNativeMask.Stop();
                maskMs += tNativeMask.ElapsedMilliseconds;
            }
        }

        if (nativeYuvApplied)
        {
            _directFaceBlurFrames++;
            _nativeYuvBlurFrames++;
            frameWasBlurred = true;
            ApplyEncodingPts(nativeYuvFrame, encodedPts);

            var tEncode = Stopwatch.StartNew();
            Throw(ffmpeg.avcodec_send_frame(enc, nativeYuvFrame));
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
                ref outputPacketPtsGapOutlierCount,
                ref maxOutputPacketPtsGap,
                timestampIntegrity,
                encodedPacketFrameStep);
            tEncode.Stop();
            encodeMs += tEncode.ElapsedMilliseconds;
        }
        else if (mask != null || (faceRects != null && faceRects.Count > 0))
        {
            var tBgra = Stopwatch.StartNew();
            Throw(ffmpeg.av_frame_make_writable(bgra));
            Throw(ffmpeg.sws_scale(
                swsDecToBgra,
                frame->data,
                frame->linesize,
                0,
                frame->height,
                bgra->data,
                bgra->linesize));
            tBgra.Stop();
            swsToBgraMs += tBgra.ElapsedMilliseconds;

            var tMask = Stopwatch.StartNew();
            if (mask != null)
            {
                _masked.ApplyMaskAndBlur(bgra, mask, blurRadius, faceRects);
                _bitmapMaskBlurFrames++;
                frameWasBlurred = true;
            }
            else
            {
                _masked.ApplyFaceRectsAndBlur(bgra, faceRects!, blurRadius);
                _directFaceBlurFrames++;
                frameWasBlurred = true;
            }
            tMask.Stop();
            maskMs += tMask.ElapsedMilliseconds;

            var tEncSws = Stopwatch.StartNew();
            Throw(ffmpeg.av_frame_make_writable(encFrame));
            Throw(ffmpeg.sws_scale(
                swsBgraToEnc,
                bgra->data,
                bgra->linesize,
                0,
                bgra->height,
                encFrame->data,
                encFrame->linesize));
            tEncSws.Stop();
            swsToEncMs += tEncSws.ElapsedMilliseconds;

            ApplyEncodingPts(encFrame, encodedPts);

            var tEncode = Stopwatch.StartNew();
            Throw(ffmpeg.avcodec_send_frame(enc, encFrame));
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
                ref outputPacketPtsGapOutlierCount,
                ref maxOutputPacketPtsGap,
                timestampIntegrity,
                encodedPacketFrameStep);
            tEncode.Stop();
            encodeMs += tEncode.ElapsedMilliseconds;
        }
        else
        {
            bool direct = frame->format == (int)enc->pix_fmt
                && frame->width == enc->width
                && frame->height == enc->height;

            if (!direct)
            {
                if (swsDecToEnc == null)
                {
                    swsDecToEnc = ffmpeg.sws_getContext(
                        dec->width, dec->height, dec->pix_fmt,
                        enc->width, enc->height, enc->pix_fmt,
                        (int)SwsFlags.SWS_FAST_BILINEAR,
                        null, null, null);
                }

                var tEncSws = Stopwatch.StartNew();
                Throw(ffmpeg.av_frame_make_writable(encFrame));
                Throw(ffmpeg.sws_scale(
                    swsDecToEnc,
                    frame->data,
                    frame->linesize,
                    0,
                    frame->height,
                    encFrame->data,
                    encFrame->linesize));
                tEncSws.Stop();
                swsToEncMs += tEncSws.ElapsedMilliseconds;

                ApplyEncodingPts(encFrame, encodedPts);

                var tEncode = Stopwatch.StartNew();
                Throw(ffmpeg.avcodec_send_frame(enc, encFrame));
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
                ref outputPacketPtsGapOutlierCount,
                ref maxOutputPacketPtsGap,
                timestampIntegrity,
                encodedPacketFrameStep);
                tEncode.Stop();
                encodeMs += tEncode.ElapsedMilliseconds;
            }
            else
            {
                var tEncode = Stopwatch.StartNew();
                ApplyEncodingPts(frame, encodedPts);
                Throw(ffmpeg.avcodec_send_frame(enc, frame));
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
                ref outputPacketPtsGapOutlierCount,
                ref maxOutputPacketPtsGap,
                timestampIntegrity,
                encodedPacketFrameStep);
                tEncode.Stop();
                encodeMs += tEncode.ElapsedMilliseconds;
            }
        }
        if (resolvedFrameIndex < sampleWindowFrames)
            sampleEncodedFrameCount++;
        if (frameWasBlurred && resolvedFrameIndex < sampleWindowFrames)
            sampleBlurredFrameCount++;

        ReportVideoProgress(progress, totalFrames, ref lastReportedFrame, resolvedFrameIndex);
        if (resolvedFrameIndex % 60 == 0)
        {
            Debug.WriteLine(
                $"[Export] frames={resolvedFrameIndex}, swsToBgraMs={swsToBgraMs}, maskMs={maskMs}, swsToEncMs={swsToEncMs}, encodeMs={encodeMs}, totalMs={swTotal.ElapsedMilliseconds}");
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
        ref int blurRangeCursor,
        double sourceFps,
        bool forceSafeEncoding,
        int totalFrames,
        int encodeWindowStart,
        int encodeWindowEnd,
        ref int frameIndex,
        ref int lastResolvedFrameIndex,
        ref long frameTimestampBase,
        ref bool hasFrameTimestampBase,
        ref long swsToBgraMs,
        ref long maskMs,
        ref long swsToEncMs,
        ref long encodeMs,
        ref long lastEncodedPts,
        ref bool hasLastEncodedPts,
        ref long lastEncodedPacketPts,
        ref bool hasLastEncodedPacketPts,
        ref long lastEncodedPacketDts,
        ref bool hasLastEncodedPacketDts,
        ref WriteableBitmap? reusableFaceMask,
        ref int outputVideoPacketCount,
        ref int outputPacketPtsGapOutlierCount,
        ref long maxOutputPacketPtsGap,
        VideoPacketTimestampIntegrity timestampIntegrity,
        IProgress<ExportProgress>? progress,
        ref int lastReportedFrame,
        Stopwatch swTotal,
        System.Threading.CancellationToken cancellationToken,
        int sampleWindowFrames,
        ref int sampleEncodedFrameCount,
        ref int sampleBlurredFrameCount,
        ref int encodedWindowFrameCount,
        long encodedPacketFrameStep)
    {
        if (videoFlushed)
            return;

        int sendErr = ffmpeg.avcodec_send_packet(dec, null);
        if (sendErr < 0 && sendErr != ffmpeg.AVERROR_EOF)
            Throw(sendErr);

        while (ffmpeg.avcodec_receive_frame(dec, frame) == 0)
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
                ref blurRangeCursor,
                sourceFps,
                forceSafeEncoding,
                totalFrames,
                encodeWindowStart,
                encodeWindowEnd,
                ref frameIndex,
                ref lastResolvedFrameIndex,
                ref frameTimestampBase,
                ref hasFrameTimestampBase,
                ref swsToBgraMs,
                ref maskMs,
                ref swsToEncMs,
                ref encodeMs,
                ref lastEncodedPts,
                ref hasLastEncodedPts,
                ref lastEncodedPacketPts,
                ref hasLastEncodedPacketPts,
                ref lastEncodedPacketDts,
                ref hasLastEncodedPacketDts,
                ref reusableFaceMask,
                ref outputVideoPacketCount,
                ref outputPacketPtsGapOutlierCount,
                ref maxOutputPacketPtsGap,
                timestampIntegrity,
                    progress,
                    ref lastReportedFrame,
                    swTotal,
                    cancellationToken,
                    sampleWindowFrames,
                    ref sampleEncodedFrameCount,
                    ref sampleBlurredFrameCount,
                    ref encodedWindowFrameCount,
                    encodedPacketFrameStep);
            ffmpeg.av_frame_unref(frame);
        }

        int encErr = ffmpeg.avcodec_send_frame(enc, null);
        if (encErr < 0 && encErr != ffmpeg.AVERROR_EOF)
            Throw(encErr);
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
                ref outputPacketPtsGapOutlierCount,
                ref maxOutputPacketPtsGap,
                timestampIntegrity,
                encodedPacketFrameStep);
        videoFlushed = true;
    }

    private static HashSet<int> BuildBlurFrameSet(FrameMaskProvider provider, int totalFrames)
    {
        var result = new HashSet<int>();
        if (provider == null || !provider.HasAnyMaskEntries())
            return result;

        foreach (int index in provider.GetStoredMaskFrameIndices())
        {
            if (index < 0)
                continue;
            if (totalFrames > 0 && index >= totalFrames)
                continue;
            result.Add(index);
        }

        foreach (int index in provider.GetFaceMaskFrameIndices())
        {
            if (index < 0)
                continue;
            if (totalFrames > 0 && index >= totalFrames)
                continue;
            result.Add(index);
        }

        return result;
    }

    private static List<(int Start, int EndExclusive)> BuildBlurFrameRanges(HashSet<int> blurFrameSet)
    {
        var ranges = new List<(int Start, int EndExclusive)>();
        if (blurFrameSet == null || blurFrameSet.Count == 0)
            return ranges;

        int[] sorted = new int[blurFrameSet.Count];
        blurFrameSet.CopyTo(sorted);
        Array.Sort(sorted);

        int start = sorted[0];
        int end = start + 1;
        for (int i = 1; i < sorted.Length; i++)
        {
            int current = sorted[i];
            if (current == end)
            {
                end++;
                continue;
            }

            ranges.Add((start, end));
            start = current;
            end = current + 1;
        }

        ranges.Add((start, end));
        return ranges;
    }

    private static bool IsFrameInBlurRanges(int frameIndex, List<(int Start, int EndExclusive)> ranges, ref int cursor)
    {
        while (cursor < ranges.Count && frameIndex >= ranges[cursor].EndExclusive)
            cursor++;

        if (cursor >= ranges.Count)
            return false;

        var current = ranges[cursor];
        return frameIndex >= current.Start && frameIndex < current.EndExclusive;
    }

    private static List<(int Start, int EndExclusive)> AlignRangesToKeyframes(
        List<(int Start, int EndExclusive)> ranges,
        List<int> keyframes,
        int totalFrames)
    {
        if (ranges == null || ranges.Count == 0 || keyframes == null || keyframes.Count == 0)
            return ranges ?? new List<(int Start, int EndExclusive)>();

        keyframes.Sort();
        if (keyframes[0] > 0)
            keyframes.Insert(0, 0);

        var aligned = new List<(int Start, int EndExclusive)>(ranges.Count);
        foreach (var (start, endExclusive) in ranges)
        {
            int safeStart = Math.Max(0, start);
            int safeEnd = Math.Max(safeStart + 1, endExclusive);
            int alignedStart = FindFloorKeyframe(keyframes, safeStart);
            int alignedEnd = FindNextKeyframe(keyframes, safeEnd - 1);
            if (alignedEnd <= alignedStart)
                alignedEnd = safeEnd;
            if (totalFrames > 0)
                alignedEnd = Math.Min(alignedEnd, totalFrames);
            aligned.Add((alignedStart, alignedEnd));
        }

        if (aligned.Count <= 1)
            return aligned;

        aligned.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(int Start, int EndExclusive)>(aligned.Count);
        int curStart = aligned[0].Start;
        int curEnd = aligned[0].EndExclusive;
        for (int i = 1; i < aligned.Count; i++)
        {
            var next = aligned[i];
            if (next.Start <= curEnd)
            {
                curEnd = Math.Max(curEnd, next.EndExclusive);
                continue;
            }

            merged.Add((curStart, curEnd));
            curStart = next.Start;
            curEnd = next.EndExclusive;
        }
        merged.Add((curStart, curEnd));
        return merged;
    }

    private static int FindFloorKeyframe(List<int> keyframes, int value)
    {
        int lo = 0;
        int hi = keyframes.Count - 1;
        int result = keyframes[0];
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            int current = keyframes[mid];
            if (current <= value)
            {
                result = current;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return result;
    }

    private static int FindNextKeyframe(List<int> keyframes, int value)
    {
        int lo = 0;
        int hi = keyframes.Count - 1;
        int result = keyframes[hi];
        bool found = false;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            int current = keyframes[mid];
            if (current > value)
            {
                result = current;
                found = true;
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return found ? result : Math.Max(value + 1, keyframes[keyframes.Count - 1] + 1);
    }

    private static bool IsKeyframeFrameIndex(List<int> keyframes, int frameIndex)
    {
        if (keyframes == null || keyframes.Count == 0)
            return false;

        int lo = 0;
        int hi = keyframes.Count - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            int current = keyframes[mid];
            if (current == frameIndex)
                return true;

            if (current < frameIndex)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return false;
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

            int videoStreamIndex = -1;
            for (int i = 0; i < inFmt->nb_streams; i++)
            {
                if (inFmt->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    videoStreamIndex = i;
                    break;
                }
            }
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

                int resolvedFrameIndex = ResolveFrameIndexFromPacket(
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

    private static unsafe long ResolveEncodePts(
        AVFrame* frame,
        AVRational sourceTimeBase,
        AVRational targetTimeBase,
        int fallbackFrameIndex,
        double sourceFps,
        ref long lastPts,
        ref bool hasLastPts)
    {
        long frameStep = GetVideoFrameStep(sourceFps, targetTimeBase);
        long rawPts = frame != null
            ? (frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE
                ? frame->best_effort_timestamp
                : frame->pts)
            : ffmpeg.AV_NOPTS_VALUE;

        if (rawPts == ffmpeg.AV_NOPTS_VALUE)
        {
            rawPts = hasLastPts
                ? (lastPts + frameStep)
                : Math.Max(0, fallbackFrameIndex * frameStep);
        }

        if (rawPts < 0)
            rawPts = 0;

        long normalizedPts = rawPts;
        if (sourceTimeBase.num > 0 && sourceTimeBase.den > 0 && targetTimeBase.num > 0 && targetTimeBase.den > 0)
            normalizedPts = ffmpeg.av_rescale_q(rawPts, sourceTimeBase, targetTimeBase);

        if (normalizedPts < 0)
            normalizedPts = 0;

        if (!hasLastPts)
        {
            hasLastPts = true;
        }
        else if (normalizedPts <= lastPts)
        {
            // 디코딩 타임스탬프의 경계 편차가 있을 때도
            // 매 프레임이 연속된 재생 속도를 유지하도록 보정한다.
            normalizedPts = lastPts + Math.Max(1, frameStep);
        }

        lastPts = normalizedPts;
        return normalizedPts;
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

    private static long GetVideoFrameStep(double sourceFps, AVRational targetTimeBase)
    {
        if (sourceFps <= 0.0 || targetTimeBase.den == 0 || targetTimeBase.num == 0)
            return 1;

        double tickSeconds = ffmpeg.av_q2d(targetTimeBase);
        if (tickSeconds <= 0.0 || double.IsNaN(tickSeconds) || double.IsInfinity(tickSeconds))
            return 1;

        double frameStep = (1.0 / sourceFps) / tickSeconds;
        if (double.IsNaN(frameStep) || double.IsInfinity(frameStep))
            return 1;

        return Math.Max(1, (long)Math.Round(frameStep));
    }

    private static unsafe bool NormalizeEncodedPacketTimestamps(
        AVPacket* packet,
        ref long lastPacketPts,
        ref bool hasLastPacketPts,
        ref long lastPacketDts,
        ref bool hasLastPacketDts,
        long expectedFrameStep = 1)
    {
        if (packet == null)
            return false;

        expectedFrameStep = Math.Max(1, expectedFrameStep);
        bool hadAdjustment = false;

        long noValue = ffmpeg.AV_NOPTS_VALUE;
        long originalPts = packet->pts;
        long originalDts = packet->dts;

        // Preserve valid reordered and negative timestamps. Only missing values are synthesized.
        long normalizedPts = packet->pts;
        if (normalizedPts == noValue)
        {
            normalizedPts = packet->dts != noValue
                ? packet->dts
                : hasLastPacketPts
                    ? lastPacketPts + expectedFrameStep
                    : hasLastPacketDts
                        ? lastPacketDts + expectedFrameStep
                        : 0;
            hadAdjustment = true;
        }

        long normalizedDts = packet->dts;
        if (normalizedDts == noValue)
        {
            normalizedDts = normalizedPts;
            hadAdjustment = true;
        }

        if (normalizedPts != originalPts || normalizedDts != originalDts)
            hadAdjustment = true;

        packet->pts = normalizedPts;
        packet->dts = normalizedDts;

        lastPacketPts = normalizedPts;
        hasLastPacketPts = true;
        lastPacketDts = normalizedDts;
        hasLastPacketDts = true;

        return hadAdjustment;
    }

    private static unsafe bool NormalizeCopiedPacketTimestamps(
        AVPacket* packet,
        ref long lastPacketPts,
        ref bool hasLastPacketPts,
        ref long lastPacketDts,
        ref bool hasLastPacketDts,
        long expectedFrameStep = 1)
    {
        return NormalizeEncodedPacketTimestamps(
            packet,
            ref lastPacketPts,
            ref hasLastPacketPts,
            ref lastPacketDts,
            ref hasLastPacketDts,
            expectedFrameStep);
    }

    private static unsafe void ApplyEncodingPts(AVFrame* frame, long pts)
    {
        if (frame == null)
            return;

        frame->pts = pts;
        frame->pkt_dts = ffmpeg.AV_NOPTS_VALUE;
    }

    private static int ResolveFrameIndexFromPacket(
        AVPacket* packet,
        AVRational timeBase,
        double sourceFps,
        int fallback,
        int totalFrames,
        ref long timestampBase,
        ref bool hasTimestampBase)
    {
        if (packet == null || sourceFps <= 0.0)
        {
            if (totalFrames > 0)
                return Math.Clamp(fallback, 0, totalFrames - 1);
            return Math.Max(0, fallback);
        }

        if (totalFrames > 0)
            fallback = Math.Clamp(fallback, 0, totalFrames - 1);
        else if (fallback < 0)
            fallback = 0;

        long ts = packet->dts != ffmpeg.AV_NOPTS_VALUE
            ? packet->dts
            : packet->pts;
        if (ts == ffmpeg.AV_NOPTS_VALUE)
            return fallback;

        long normalizedTs = hasTimestampBase
            ? ts - timestampBase
            : ts;

        if (!hasTimestampBase)
        {
            timestampBase = ts;
            hasTimestampBase = true;
            normalizedTs = 0;
        }

        double seconds = normalizedTs * ffmpeg.av_q2d(timeBase);
        if (double.IsNaN(seconds) || double.IsInfinity(seconds))
            return fallback;

        int index = (int)Math.Floor(seconds * sourceFps);
        if (index < 0)
            index = 0;
        if (totalFrames > 0 && index >= totalFrames)
            index = Math.Max(0, totalFrames - 1);
        return index;
    }

    private static int ResolveFrameIndexFromFrame(
        AVFrame* frame,
        AVRational timeBase,
        double sourceFps,
        int fallback,
        int totalFrames,
        ref long timestampBase,
        ref bool hasTimestampBase)
    {
        if (frame == null || sourceFps <= 0.0)
        {
            if (totalFrames > 0)
                return Math.Clamp(fallback, 0, totalFrames - 1);
            return Math.Max(0, fallback);
        }

        if (totalFrames > 0)
            fallback = Math.Clamp(fallback, 0, totalFrames - 1);
        else if (fallback < 0)
            fallback = 0;

        long ts = frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE
            ? frame->best_effort_timestamp
            : frame->pts;
        if (ts == ffmpeg.AV_NOPTS_VALUE)
            return fallback;

        long normalizedTs = hasTimestampBase
            ? ts - timestampBase
            : ts;

        if (!hasTimestampBase)
        {
            timestampBase = ts;
            hasTimestampBase = true;
            normalizedTs = 0;
        }

        double seconds = normalizedTs * ffmpeg.av_q2d(timeBase);
        if (double.IsNaN(seconds) || double.IsInfinity(seconds))
            return fallback;

        int index = (int)Math.Floor(seconds * sourceFps);
        if (index < 0)
            index = 0;
        if (totalFrames > 0 && index >= totalFrames)
            index = Math.Max(0, totalFrames - 1);
        return index;
    }

    private static unsafe (
        int InputVideoPackets,
        int OutputVideoPackets,
        int MissingVideoPacketTimestamps,
        int VideoPacketTimestampAdjustments,
        int OutputPacketPtsGapOutlierCount,
        long MaxOutputPacketPtsGap) ExportByRemuxCopy(
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
            long[] lastRemuxPacketPts = Array.Empty<long>();
            bool[] hasLastRemuxPacketPts = Array.Empty<bool>();
            long[] lastRemuxPacketDts = Array.Empty<long>();
            bool[] hasLastRemuxPacketDts = Array.Empty<bool>();
            int inputVideoPackets = 0;
            int outputVideoPackets = 0;
            int missingVideoPacketTimestamps = 0;
            int videoPacketTimestampAdjustments = 0;
            int outputPacketPtsGapOutlierCount = 0;
            long maxOutputPacketPtsGap = 0;

        try
        {
            Throw(ffmpeg.avformat_alloc_output_context2(&outFmt, null, null, outputPath));

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

            int streamTotal = Math.Max(0, streamCount);
            lastRemuxPacketPts = new long[streamTotal];
            hasLastRemuxPacketPts = new bool[streamTotal];
            lastRemuxPacketDts = new long[streamTotal];
            hasLastRemuxPacketDts = new bool[streamTotal];

            if ((outFmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
                Throw(ffmpeg.avio_open(&outFmt->pb, outputPath, ffmpeg.AVIO_FLAG_WRITE));

            Throw(ffmpeg.avformat_write_header(outFmt, null));

            int lastReportedFrame = -1;
            while (ffmpeg.av_read_frame(inFmt, pkt) >= 0)
            {
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
                long normalizeStep = inStream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO
                    ? GetVideoFrameStep(sourceFps, outStream->time_base)
                    : 1;
                bool isVideoPacket = inIndex == videoStreamIndex;
                bool hasMissingVideoTimestamp = isVideoPacket &&
                    (pkt->pts == ffmpeg.AV_NOPTS_VALUE || pkt->dts == ffmpeg.AV_NOPTS_VALUE);
                long previousVideoPacketDts = isVideoPacket
                    ? lastRemuxPacketDts[outIndex]
                    : 0;
                bool hadPreviousVideoPacketDts = isVideoPacket && hasLastRemuxPacketDts[outIndex];
                ffmpeg.av_packet_rescale_ts(pkt, inStream->time_base, outStream->time_base);
                if (isVideoPacket)
                {
                    if (hasMissingVideoTimestamp)
                        missingVideoPacketTimestamps++;

                    bool requiresTimestampRepair = hasMissingVideoTimestamp ||
                        (hadPreviousVideoPacketDts && pkt->dts <= previousVideoPacketDts);
                    if (requiresTimestampRepair)
                    {
                        videoPacketTimestampAdjustments++;
                        NormalizeCopiedPacketTimestamps(
                            pkt,
                            ref lastRemuxPacketPts[outIndex],
                            ref hasLastRemuxPacketPts[outIndex],
                            ref lastRemuxPacketDts[outIndex],
                            ref hasLastRemuxPacketDts[outIndex],
                            normalizeStep);
                    }
                    else
                    {
                        lastRemuxPacketPts[outIndex] = pkt->pts;
                        hasLastRemuxPacketPts[outIndex] = true;
                        lastRemuxPacketDts[outIndex] = pkt->dts;
                        hasLastRemuxPacketDts[outIndex] = true;
                    }

                    if (hadPreviousVideoPacketDts)
                    {
                        long timestampGap = Math.Abs(pkt->dts - previousVideoPacketDts);
                        if (timestampGap > maxOutputPacketPtsGap)
                            maxOutputPacketPtsGap = timestampGap;

                    }
                }
                else
                {
                    NormalizeCopiedPacketTimestamps(
                        pkt,
                        ref lastRemuxPacketPts[outIndex],
                        ref hasLastRemuxPacketPts[outIndex],
                        ref lastRemuxPacketDts[outIndex],
                        ref hasLastRemuxPacketDts[outIndex],
                        normalizeStep);
                }
                pkt->stream_index = outStream->index;
                pkt->pos = -1;
                Throw(ffmpeg.av_interleaved_write_frame(outFmt, pkt));
                if (inIndex == videoStreamIndex)
                    outputVideoPackets++;

                if (progress != null && inIndex == videoStreamIndex && totalFrames > 0 && sourceFps > 0.0)
                {
                    long ts = pkt->pts != ffmpeg.AV_NOPTS_VALUE ? pkt->pts : pkt->dts;
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

            Throw(ffmpeg.av_write_trailer(outFmt));
            progress?.Report(new ExportProgress(totalFrames, totalFrames, "블러 대상이 없어 무손실 고속 복사로 완료했습니다."));
        }
        finally
        {
            ffmpeg.av_packet_free(&pkt);

            if (outFmt != null)
            {
                if ((outFmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
                    ffmpeg.avio_closep(&outFmt->pb);
                ffmpeg.avformat_free_context(outFmt);
            }
        }

        return (
            inputVideoPackets,
            outputVideoPackets,
            missingVideoPacketTimestamps,
            videoPacketTimestampAdjustments,
            outputPacketPtsGapOutlierCount,
            maxOutputPacketPtsGap);
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

    private static unsafe AVCodecContext* TryCreateEncoderContext(
        AVCodecID codecId,
        AVStream* inStream,
        AVCodecContext* dec,
        AVFormatContext* outFmt,
        out AVCodec* encoder,
        out string? error,
        bool forceSoftwareEncoder,
        bool forceSafeEncoding)
    {
        encoder = null;
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

        var attemptedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidateName in GetPreferredEncoderNames(codecId, forceSoftwareEncoder))
        {
            if (string.IsNullOrWhiteSpace(candidateName) || !attemptedNames.Add(candidateName))
                continue;

            AVCodec* candidate = ffmpeg.avcodec_find_encoder_by_name(candidateName);
            if (candidate == null || candidate->id != codecId)
                continue;

            var ctx = TryOpenEncoderContext(candidate, inStream, dec, outFmt, out var openError, forceSafeEncoding);
            if (ctx != null)
            {
                encoder = candidate;
                return ctx;
            }

            error = AppendEncoderError(error, candidateName, openError);
        }

        AVCodec* fallback = SelectFallbackEncoder(codecId, forceSoftwareEncoder);
        if (fallback == null)
        {
            error = AppendEncoderError(
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
            var ctx = TryOpenEncoderContext(fallback, inStream, dec, outFmt, out var fallbackError, forceSafeEncoding);
            if (ctx != null)
            {
                encoder = fallback;
                return ctx;
            }

            error = AppendEncoderError(error, fallbackName, fallbackError);
        }

        return null;
    }

    private static unsafe AVCodecContext* TryOpenEncoderContext(
        AVCodec* encoder,
        AVStream* inStream,
        AVCodecContext* dec,
        AVFormatContext* outFmt,
        out string? error,
        bool forceSafeEncoding)
    {
        error = null;
        AVCodecContext* ctx = ffmpeg.avcodec_alloc_context3(encoder);
        if (ctx == null)
        {
            error = "인코더 컨텍스트를 만들 수 없습니다.";
            return null;
        }

        ctx->width = dec->width;
        ctx->height = dec->height;
        ctx->pix_fmt = dec->pix_fmt;
        if (ctx->pix_fmt == AVPixelFormat.AV_PIX_FMT_NONE && inStream->codecpar->format != -1)
            ctx->pix_fmt = (AVPixelFormat)inStream->codecpar->format;
        ctx->time_base = inStream->time_base;
        ctx->framerate = inStream->r_frame_rate.num != 0 ? inStream->r_frame_rate : inStream->avg_frame_rate;
        if (ctx->framerate.num == 0 || ctx->framerate.den == 0)
            ctx->framerate = new AVRational { num = 30, den = 1 };
        if (ctx->framerate.num > 0 && ctx->framerate.den > 0)
        {
            AVRational stableTimeBase = ffmpeg.av_inv_q(ctx->framerate);
            if (stableTimeBase.num > 0 && stableTimeBase.den > 0)
                ctx->time_base = stableTimeBase;
        }
        if (ctx->time_base.num <= 0 || ctx->time_base.den <= 0)
            ctx->time_base = inStream->time_base;

        int sourceBitrate = ClampBitrate(ResolveSourceVideoBitrate(inStream, dec));
        int targetBitrate = sourceBitrate > 0
            ? ClampBitrate((long)sourceBitrate * 3L / 2L)
            : EstimateHighQualityBitrate(ctx->width, ctx->height, ctx->framerate);
        targetBitrate = Math.Max(targetBitrate, 2_000_000);

        string encoderName = GetEncoderName(encoder);
        bool usesSoftwareConstantQuality =
            encoderName.Contains("x264", StringComparison.OrdinalIgnoreCase) ||
            encoderName.Contains("x265", StringComparison.OrdinalIgnoreCase);
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

        if (inStream->codecpar->profile != -99)
            ctx->profile = inStream->codecpar->profile;
        if (inStream->codecpar->level > 0)
            ctx->level = inStream->codecpar->level;

        ctx->sample_aspect_ratio = inStream->sample_aspect_ratio;
        ctx->color_range = dec->color_range;
        ctx->color_primaries = dec->color_primaries;
        ctx->color_trc = dec->color_trc;
        ctx->colorspace = dec->colorspace;
        ctx->chroma_sample_location = dec->chroma_sample_location;

        if ((outFmt->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
            ctx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

        if (!IsPixFmtSupported(encoder, ctx->pix_fmt))
            ctx->pix_fmt = PickPreferredPixelFormat(encoder, ctx->pix_fmt);

        string? pixelFormatLoss = GetPixelFormatLossReason(dec->pix_fmt, ctx->pix_fmt);
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
        ctx->thread_count = IsHardwareEncoder(encoder)
            ? 0
            : Math.Max(1, Environment.ProcessorCount - 2);

        ApplyHighQualityEncoderOptions(ctx, encoder, forceSafeEncoding);

        int openErr = ffmpeg.avcodec_open2(ctx, encoder, null);
        if (openErr < 0)
        {
            error = GetErrorMessage(openErr);
            ffmpeg.avcodec_free_context(&ctx);
            return null;
        }

        return ctx;
    }

    private static bool IsHardwareEncoder(AVCodec* encoder)
    {
        if (encoder == null || encoder->name == null)
            return false;

        string name = Marshal.PtrToStringAnsi((IntPtr)encoder->name) ?? string.Empty;
        if (name.Length == 0)
            return false;

        return name.Contains("videotoolbox", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("nvenc", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("qsv", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("amf", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("vaapi", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("v4l2m2m", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> GetPreferredEncoderNames(AVCodecID codecId, bool forceSoftwareOnly)
    {
        if (forceSoftwareOnly)
        {
            if (codecId == AVCodecID.AV_CODEC_ID_H264)
                return new[] { "libx264" };
            if (codecId == AVCodecID.AV_CODEC_ID_HEVC)
                return new[] { "libx265" };
            return new[] { "libx264", "libx265" };
        }

        bool isMac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        if (codecId == AVCodecID.AV_CODEC_ID_H264)
        {
            if (isMac)
                return new[] { "h264_videotoolbox", "libx264" };
            if (isWindows)
                return new[] { "h264_nvenc", "h264_qsv", "h264_amf", "libx264" };
            return new[] { "h264_nvenc", "h264_vaapi", "libx264" };
        }

        if (codecId == AVCodecID.AV_CODEC_ID_HEVC)
        {
            if (isMac)
                return new[] { "hevc_videotoolbox", "libx265" };
            if (isWindows)
                return new[] { "hevc_nvenc", "hevc_qsv", "hevc_amf", "libx265" };
            return new[] { "hevc_nvenc", "hevc_vaapi", "libx265" };
        }

        return Array.Empty<string>();
    }

    private static unsafe AVCodec* SelectFallbackEncoder(AVCodecID codecId, bool forceSoftwareOnly)
    {
        if (forceSoftwareOnly)
        {
            if (codecId == AVCodecID.AV_CODEC_ID_H264)
                return ffmpeg.avcodec_find_encoder_by_name("libx264");
            if (codecId == AVCodecID.AV_CODEC_ID_HEVC)
                return ffmpeg.avcodec_find_encoder_by_name("libx265");
        }

        return ffmpeg.avcodec_find_encoder(codecId);
    }

    private static string AppendEncoderError(string? existing, string encoderName, string? detail)
    {
        string part = string.IsNullOrWhiteSpace(detail)
            ? $"{encoderName}: 초기화 실패"
            : $"{encoderName}: {detail}";

        if (string.IsNullOrWhiteSpace(existing))
            return part;
        return $"{existing} | {part}";
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

    private static unsafe void ApplyHighQualityEncoderOptions(AVCodecContext* ctx, AVCodec* encoder, bool forceSafeEncoding)
    {
        if (ctx == null || encoder == null || encoder->name == null)
            return;

        string name = Marshal.PtrToStringAnsi((IntPtr)encoder->name) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
            return;

        bool isX264 = name.Contains("x264", StringComparison.OrdinalIgnoreCase);
        bool isX265 = name.Contains("x265", StringComparison.OrdinalIgnoreCase);
        bool isH264Family = name.Contains("h264", StringComparison.OrdinalIgnoreCase);
        bool isHevcFamily = name.Contains("hevc", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("h265", StringComparison.OrdinalIgnoreCase);
        bool isNvenc = name.Contains("nvenc", StringComparison.OrdinalIgnoreCase);

        if (forceSafeEncoding)
        {
            if (isX264)
            {
                TrySetEncoderOption(ctx, "preset", "fast");
                TrySetEncoderOption(ctx, "crf", "14");
            }
            else if (isX265)
            {
                TrySetEncoderOption(ctx, "preset", "fast");
                TrySetEncoderOption(ctx, "crf", "16");
            }
            return;
        }

        if (isX264)
        {
            TrySetEncoderOption(ctx, "preset", "fast");
            TrySetEncoderOption(ctx, "crf", "14");
            return;
        }

        if (isX265)
        {
            TrySetEncoderOption(ctx, "preset", "fast");
            TrySetEncoderOption(ctx, "crf", "16");
            return;
        }

        if (isNvenc)
        {
            TrySetEncoderOption(ctx, "preset", "p6");
            TrySetEncoderOption(ctx, "tune", "hq");
            TrySetEncoderOption(ctx, "rc", "vbr");
            TrySetEncoderOption(ctx, "cq", isHevcFamily ? "14" : "12");
            TrySetEncoderOption(ctx, "multipass", "qres");
            TrySetEncoderOption(ctx, "spatial_aq", "1");
            TrySetEncoderOption(ctx, "temporal_aq", "1");
            TrySetEncoderOption(ctx, "rc-lookahead", "20");
            return;
        }

        if (isH264Family || isHevcFamily)
        {
            TrySetEncoderOption(ctx, "qmin", "8");
            TrySetEncoderOption(ctx, "qmax", "20");
        }

        if (name.Contains("videotoolbox", StringComparison.OrdinalIgnoreCase))
            TrySetEncoderOption(ctx, "realtime", "false");
    }

    private static unsafe bool TrySetEncoderOption(AVCodecContext* ctx, string key, string value)
    {
        if (ctx == null || ctx->priv_data == null)
            return false;

        int result = ffmpeg.av_opt_set(ctx->priv_data, key, value, 0);
        if (result < 0)
            Debug.WriteLine($"[ExportEncoderOption] key={key}, value={value}, applied=false, error={GetErrorMessage(result)}");
        return result >= 0;
    }

    private static unsafe string GetEncoderName(AVCodec* encoder)
    {
        if (encoder == null || encoder->name == null)
            return "unknown";
        return Marshal.PtrToStringAnsi((IntPtr)encoder->name) ?? "unknown";
    }

    private static unsafe string GetEncoderQualityMode(AVCodec* encoder, bool forceSafeEncoding)
    {
        string name = GetEncoderName(encoder);
        if (name.Contains("x264", StringComparison.OrdinalIgnoreCase))
            return forceSafeEncoding ? "crf14-fast-safe" : "crf14-fast";
        if (name.Contains("x265", StringComparison.OrdinalIgnoreCase))
            return forceSafeEncoding ? "crf16-fast-safe" : "crf16-fast";
        if (name.Contains("nvenc", StringComparison.OrdinalIgnoreCase))
            return name.Contains("hevc", StringComparison.OrdinalIgnoreCase)
                ? "p6-hq-vbr-cq14"
                : "p6-hq-vbr-cq12";
        if (name.Contains("videotoolbox", StringComparison.OrdinalIgnoreCase))
            return "quality-vbr-realtime-off";
        return forceSafeEncoding ? "quality-bounded-safe" : "quality-bounded";
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

        int sourceBitDepth = source->nb_components == 0 ? 0 : source->comp[0].depth;
        int outputBitDepth = output->nb_components == 0 ? 0 : output->comp[0].depth;
        if (sourceBitDepth > 0 && outputBitDepth > 0 && outputBitDepth < sourceBitDepth)
        {
            return $"비트 심도 하락 {GetPixelFormatName(sourcePixelFormat)}({sourceBitDepth}) -> " +
                   $"{GetPixelFormatName(outputPixelFormat)}({outputBitDepth})";
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

    private static unsafe long ResolveSourceVideoBitrate(AVStream* stream, AVCodecContext* decoder)
    {
        if (stream != null && stream->codecpar != null && stream->codecpar->bit_rate > 0)
            return stream->codecpar->bit_rate;
        if (decoder != null && decoder->bit_rate > 0)
            return decoder->bit_rate;
        return 0;
    }

    private static unsafe void CopyStreamPresentationMetadata(AVStream* source, AVStream* output)
    {
        if (source == null || output == null)
            return;

        output->avg_frame_rate = source->avg_frame_rate;
        output->r_frame_rate = source->r_frame_rate;
        output->sample_aspect_ratio = source->sample_aspect_ratio;
        output->disposition = source->disposition;
        ffmpeg.av_dict_copy(&output->metadata, source->metadata, 0);
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
                    NormalizeEncodedPacketTimestamps(outPkt, ref lastPacketPts, ref hasLastPacketPts, ref lastPacketDts, ref hasLastPacketDts);
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

    private static void Throw(int err)
    {
        if (err >= 0) return;

        byte* buf = stackalloc byte[1024];
        ffmpeg.av_strerror(err, buf, 1024);
        throw new InvalidOperationException(
            System.Text.Encoding.UTF8.GetString(new ReadOnlySpan<byte>(buf, 1024)).TrimEnd('\0'));
    }
}
