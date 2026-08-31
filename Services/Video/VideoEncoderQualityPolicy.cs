using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FaceShield.Services.Video;

internal sealed record EncoderQualityConfiguration(
    string Mode,
    bool RequiredOptionsApplied,
    string AppliedOptions,
    string FailedOptions)
{
    internal static EncoderQualityConfiguration Unconfigured { get; } =
        new("unconfigured", false, string.Empty, "encoder-options-not-configured");
}

internal static unsafe class VideoEncoderQualityPolicy
{
    internal static EncoderQualityConfiguration Apply(
        AVCodecContext* ctx,
        AVCodec* encoder,
        bool forceSafeEncoding,
        bool hasStaticHdrMetadata,
        string? x265Params)
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
            if (hasStaticHdrMetadata)
            {
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

    private static bool TrySetEncoderOption(
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
            Debug.WriteLine(
                $"[ExportEncoderOption] key={key}, value={value}, applied=false, error={error}");
            return false;
        }

        error = null;
        Debug.WriteLine($"[ExportEncoderOption] key={key}, value={value}, applied=true");
        return true;
    }

    private static string GetErrorMessage(int errorCode)
    {
        byte* buffer = stackalloc byte[1024];
        ffmpeg.av_strerror(errorCode, buffer, 1024);
        return System.Text.Encoding.UTF8
            .GetString(new ReadOnlySpan<byte>(buffer, 1024))
            .TrimEnd('\0');
    }
}
