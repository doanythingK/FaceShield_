from pathlib import Path


def read(path: str) -> str:
    return Path(path).read_text(encoding="utf-8-sig").replace("\r\n", "\n")


def write(path: str, text: str) -> None:
    Path(path).write_text(text, encoding="utf-8", newline="\n")


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{label} mismatch: {count}")
    return text.replace(old, new, 1)


# Known source bitrate is the output target. Do not inflate it by 25%.
path = "Services/Video/VideoExportFidelityPolicy.cs"
text = read(path)
if "using System.IO;\n" not in text:
    text = text.replace("using System;\n", "using System;\nusing System.IO;\n", 1)
text = replace_once(
    text,
    '''    internal static int ResolveKnownSourceTargetBitrate(
        long sourceBitrate,
        int resolutionFloor)
    {
        int boundedSourceBitrate = ClampBitrate(sourceBitrate);
        if (boundedSourceBitrate <= 0)
            return 0;

        int sourceRelativeGuardrail = ClampBitrate(
            Math.Min(
                Math.Max(0L, resolutionFloor),
                (long)boundedSourceBitrate * 5L / 4L));
        return Math.Max(boundedSourceBitrate, sourceRelativeGuardrail);
    }''',
    '''    internal static int ResolveKnownSourceTargetBitrate(
        long sourceBitrate,
        int resolutionFloor)
    {
        // Once the source video rate is known, keep it authoritative. Raising the
        // target above the source made re-encoded blur exports unnecessarily large.
        return ClampBitrate(sourceBitrate);
    }''',
    "known source target")
marker = '''    internal static long ResolveSourceVideoBitrate(
        AVStream* stream,'''
helper = '''    internal static long EstimateContainerBitrateFromFile(
        long fileLengthBytes,
        long durationMicroseconds)
    {
        if (fileLengthBytes <= 0 || durationMicroseconds <= 0)
            return 0;

        double bitsPerSecond =
            fileLengthBytes * 8.0 * ffmpeg.AV_TIME_BASE / durationMicroseconds;
        if (double.IsNaN(bitsPerSecond) ||
            double.IsInfinity(bitsPerSecond) ||
            bitsPerSecond <= 0)
        {
            return 0;
        }

        return bitsPerSecond >= long.MaxValue
            ? long.MaxValue
            : (long)Math.Round(bitsPerSecond);
    }

    internal static long ResolveContainerBitrateFallback(
        AVFormatContext* formatContext,
        string inputPath)
    {
        if (formatContext != null && formatContext->bit_rate > 0)
            return formatContext->bit_rate;

        if (formatContext == null ||
            formatContext->duration <= 0 ||
            string.IsNullOrWhiteSpace(inputPath) ||
            !File.Exists(inputPath))
        {
            return 0;
        }

        return EstimateContainerBitrateFromFile(
            new FileInfo(inputPath).Length,
            formatContext->duration);
    }

'''
text = replace_once(text, marker, helper + marker, "source bitrate helper marker")
write(path, text)

# Source-known non-RGB encoders must use bitrate mode instead of CRF/CQ mode.
path = "Services/Video/VideoEncoderSelectionPolicy.cs"
text = read(path)
text = replace_once(
    text,
    '''    internal static bool ShouldApplySoftwareRateGuardrail(
        string encoderName,
        long sourceBitrate)
    {
        if (sourceBitrate <= 0 || string.IsNullOrWhiteSpace(encoderName))
            return false;
        if (string.Equals(encoderName, "libx264rgb", StringComparison.OrdinalIgnoreCase))
            return false;

        return UsesSoftwareConstantQuality(encoderName);
    }''',
    '''    internal static bool ShouldUseSourceRelativeRateControl(
        string encoderName,
        long sourceBitrate)
    {
        if (sourceBitrate <= 0 || string.IsNullOrWhiteSpace(encoderName))
            return false;

        return !string.Equals(
            encoderName,
            "libx264rgb",
            StringComparison.OrdinalIgnoreCase);
    }''',
    "selection rate-control policy")
write(path, text)

path = "Services/Video/VideoEncoderQualityPolicy.cs"
text = read(path)
text = replace_once(
    text,
    '''        AVCodecContext* ctx,
        AVCodec* encoder,
        bool forceSafeEncoding,
        bool hasStaticHdrMetadata,''',
    '''        AVCodecContext* ctx,
        AVCodec* encoder,
        bool forceSafeEncoding,
        bool useSourceRelativeRateControl,
        bool hasStaticHdrMetadata,''',
    "quality Apply signature")
for old, new, label in [
    (
        '''        else if (isX264)
        {
            SetOption("preset", "fast", required: true);
            SetOption("crf", "19", required: true);
            mode = forceSafeEncoding ? "crf19-fast-safe" : "crf19-fast";
        }''',
        '''        else if (isX264)
        {
            SetOption("preset", "fast", required: true);
            if (useSourceRelativeRateControl)
                mode = forceSafeEncoding ? "source-bitrate-fast-safe" : "source-bitrate-fast";
            else
            {
                SetOption("crf", "19", required: true);
                mode = forceSafeEncoding ? "crf19-fast-safe" : "crf19-fast";
            }
        }''',
        "x264 quality"),
    (
        '''            if (hasStaticHdrMetadata)
            {
                SetOption("crf", "18", required: true);''',
        '''            if (hasStaticHdrMetadata)
            {
                if (!useSourceRelativeRateControl)
                    SetOption("crf", "18", required: true);''',
        "x265 hdr crf"),
    (
        '''                mode = forceSafeEncoding ? "crf18-fast-safe-hdr" : "crf18-fast-hdr";
            }
            else
            {
                SetOption("crf", "20", required: true);
                mode = forceSafeEncoding ? "crf20-fast-safe" : "crf20-fast";
            }''',
        '''                mode = useSourceRelativeRateControl
                    ? forceSafeEncoding ? "source-bitrate-fast-safe-hdr" : "source-bitrate-fast-hdr"
                    : forceSafeEncoding ? "crf18-fast-safe-hdr" : "crf18-fast-hdr";
            }
            else if (useSourceRelativeRateControl)
                mode = forceSafeEncoding ? "source-bitrate-fast-safe" : "source-bitrate-fast";
            else
            {
                SetOption("crf", "20", required: true);
                mode = forceSafeEncoding ? "crf20-fast-safe" : "crf20-fast";
            }''',
        "x265 quality"),
    (
        '''        else if (isSvtAv1)
        {
            SetOption("preset", "6", required: true);
            SetOption("crf", "20", required: true);
            SetOption("svtav1-params", "tune=0", required: true);
            mode = forceSafeEncoding ? "crf20-preset6-vq-safe" : "crf20-preset6-vq";
        }''',
        '''        else if (isSvtAv1)
        {
            SetOption("preset", "6", required: true);
            if (!useSourceRelativeRateControl)
                SetOption("crf", "20", required: true);
            SetOption("svtav1-params", "tune=0", required: true);
            mode = useSourceRelativeRateControl
                ? forceSafeEncoding ? "source-bitrate-preset6-safe" : "source-bitrate-preset6"
                : forceSafeEncoding ? "crf20-preset6-vq-safe" : "crf20-preset6-vq";
        }''',
        "svt quality"),
    (
        '''        else if (isAomAv1)
        {
            SetOption("usage", "good", required: true);
            SetOption("cpu-used", "4", required: true);
            SetOption("crf", "20", required: true);
            SetOption("row-mt", "1", required: false);
            SetOption("tune", "psnr", required: false);
            mode = forceSafeEncoding ? "crf20-cpu4-good-safe" : "crf20-cpu4-good";
        }''',
        '''        else if (isAomAv1)
        {
            SetOption("usage", "good", required: true);
            SetOption("cpu-used", "4", required: true);
            if (!useSourceRelativeRateControl)
                SetOption("crf", "20", required: true);
            SetOption("row-mt", "1", required: false);
            SetOption("tune", "psnr", required: false);
            mode = useSourceRelativeRateControl
                ? forceSafeEncoding ? "source-bitrate-cpu4-good-safe" : "source-bitrate-cpu4-good"
                : forceSafeEncoding ? "crf20-cpu4-good-safe" : "crf20-cpu4-good";
        }''',
        "aom quality"),
    (
        '''            SetOption("rc", "vbr", required: true);
            SetOption("cq", "18", required: true);''',
        '''            SetOption("rc", "vbr", required: true);
            if (!useSourceRelativeRateControl)
                SetOption("cq", "18", required: true);''',
        "nvenc cq"),
    (
        '''            mode = forceSafeEncoding ? "p6-hq-vbr-cq18-safe" : "p6-hq-vbr-cq18";''',
        '''            mode = useSourceRelativeRateControl
                ? forceSafeEncoding ? "p6-hq-source-bitrate-safe" : "p6-hq-source-bitrate"
                : forceSafeEncoding ? "p6-hq-vbr-cq18-safe" : "p6-hq-vbr-cq18";''',
        "nvenc mode"),
]:
    text = replace_once(text, old, new, label)
write(path, text)

# Make target bitrate a strict max on every source-known non-RGB path.
path = "Services/Video/VideoEncoderContextPolicy.cs"
text = read(path)
text = replace_once(
    text,
    '''        bool usesSoftwareConstantQuality =
            VideoEncoderSelectionPolicy.UsesSoftwareConstantQuality(
                encoderName);
        bool applySoftwareRateGuardrail =
            VideoEncoderSelectionPolicy.ShouldApplySoftwareRateGuardrail(
                encoderName,
                sourceBitrate);
        if (usesSoftwareConstantQuality)
        {
            if (applySoftwareRateGuardrail)
            {
                // Keep CRF quality selection, but cap sustained rate to the
                // source-relative target so re-encoding cannot grow without bound.
                ctx->bit_rate = targetBitrate;
                ctx->rc_max_rate = targetBitrate;
                ctx->rc_buffer_size =
                    VideoExportFidelityPolicy.ClampBitrate(
                        (long)targetBitrate * 2L);
            }
            else
            {
                // Unknown source rate and RGB H.264 retain the existing quality path.
                ctx->bit_rate = 0;
                ctx->rc_max_rate = 0;
                ctx->rc_buffer_size = 0;
            }
        }
        else
        {
            ctx->bit_rate = targetBitrate;
            ctx->rc_max_rate =
                VideoExportFidelityPolicy.ClampBitrate(
                    (long)targetBitrate * 2L);
            ctx->rc_buffer_size =
                VideoExportFidelityPolicy.ClampBitrate(
                    (long)targetBitrate * 4L);
        }''',
    '''        bool usesSoftwareConstantQuality =
            VideoEncoderSelectionPolicy.UsesSoftwareConstantQuality(
                encoderName);
        bool useSourceRelativeRateControl =
            VideoEncoderSelectionPolicy.ShouldUseSourceRelativeRateControl(
                encoderName,
                sourceBitrate);
        if (useSourceRelativeRateControl)
        {
            ctx->bit_rate = targetBitrate;
            ctx->rc_max_rate = targetBitrate;
            ctx->rc_buffer_size =
                VideoExportFidelityPolicy.ClampBitrate(
                    (long)targetBitrate * 2L);
        }
        else if (usesSoftwareConstantQuality)
        {
            // Unknown source rate and RGB H.264 retain their existing quality path.
            ctx->bit_rate = 0;
            ctx->rc_max_rate = 0;
            ctx->rc_buffer_size = 0;
        }
        else
        {
            ctx->bit_rate = targetBitrate;
            ctx->rc_max_rate =
                VideoExportFidelityPolicy.ClampBitrate(
                    (long)targetBitrate * 2L);
            ctx->rc_buffer_size =
                VideoExportFidelityPolicy.ClampBitrate(
                    (long)targetBitrate * 4L);
        }''',
    "encoder context rate block")
text = replace_once(
    text,
    '''        qualityConfiguration = VideoEncoderQualityPolicy.Apply(
            ctx,
            encoder,
            forceSafeEncoding,
            hdrMetadata?.HasStaticMetadata == true,''',
    '''        qualityConfiguration = VideoEncoderQualityPolicy.Apply(
            ctx,
            encoder,
            forceSafeEncoding,
            useSourceRelativeRateControl,
            hdrMetadata?.HasStaticMetadata == true,''',
    "quality Apply call")
write(path, text)

# Resolve fallback bitrate once after input discovery and thread it through export.
path = "Services/Video/VideoExportService.cs"
text = read(path)
text = replace_once(
    text,
    '''            AVStream* inStream = inFmt->streams[videoStreamIndex];
            AVStream* inAudioStream = audioStreamIndex >= 0 ? inFmt->streams[audioStreamIndex] : null;
            totalFrames = (int)inStream->nb_frames;''',
    '''            AVStream* inStream = inFmt->streams[videoStreamIndex];
            AVStream* inAudioStream = audioStreamIndex >= 0 ? inFmt->streams[audioStreamIndex] : null;
            long sourceContainerBitrate =
                VideoExportFidelityPolicy.ResolveContainerBitrateFallback(
                    inFmt,
                    inputPath);
            totalFrames = (int)inStream->nb_frames;''',
    "service bitrate insertion")
if text.count("inFmt->bit_rate,") != 2:
    raise SystemExit(f"encoder bitrate argument count mismatch: {text.count('inFmt->bit_rate,')}")
text = text.replace("inFmt->bit_rate,", "sourceContainerBitrate,")
text = text.replace(
    "VideoExportFidelityPolicy.ResolveSourceVideoBitrate(inStream, null)",
    "VideoExportFidelityPolicy.ResolveSourceVideoBitrate(inStream, null, sourceContainerBitrate)")
text = text.replace(
    "VideoExportFidelityPolicy.ResolveSourceVideoBitrate(inStream, dec)",
    "VideoExportFidelityPolicy.ResolveSourceVideoBitrate(inStream, dec, sourceContainerBitrate)")
write(path, text)
