using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;

namespace FaceShield.Services.Video;

internal static unsafe class VideoExportPacketPolicy
{
    internal static void DrainEncoderPackets(
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

            VideoExportFfmpegDiagnostics.ThrowVideoEncoderError(
                receiveResult,
                enc,
                "패킷 수신");

            long encoderPacketPts = outPkt->pts;
            outPkt->duration =
                VideoExportTimingPolicy.ResolveEncodedPacketDuration(
                    outPkt->duration,
                    encoderPacketPts,
                    submittedEncodedFrameDurations);
            if (outPkt->duration < 0)
            {
                throw new InvalidOperationException(
                    "Invalid argument: 음수 인코더 패킷 길이는 내보낼 수 없습니다 " +
                    $"(duration={outPkt->duration}).");
            }

            ffmpeg.av_packet_rescale_ts(
                outPkt,
                enc->time_base,
                outStream->time_base);

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

                if (outPkt->pts != originalPts ||
                    outPkt->dts != originalDts)
                {
                    timestampIntegrity.PacketTimestampAdjustments++;
                }
            }
            else
            {
                bool allowsEqualDts =
                    outFmt->oformat != null &&
                    (outFmt->oformat->flags &
                     ffmpeg.AVFMT_TS_NONSTRICT) != 0;
                bool invalidDtsOrder =
                    hasLastPacketDts &&
                    (allowsEqualDts
                        ? outPkt->dts < lastPacketDts
                        : outPkt->dts <= lastPacketDts);

                if (outPkt->pts < outPkt->dts || invalidDtsOrder)
                {
                    throw new InvalidOperationException(
                        "Invalid argument: 인코더 출력 패킷 순서가 유효하지 않습니다 " +
                        $"(pts={outPkt->pts}, dts={outPkt->dts}, " +
                        $"previousDts={lastPacketDts}).");
                }

                lastPacketPts = outPkt->pts;
                hasLastPacketPts = true;
                lastPacketDts = outPkt->dts;
                hasLastPacketDts = true;
            }

            outPkt->stream_index = outStream->index;
            long muxPacketPts = outPkt->pts;
            VideoExportFfmpegDiagnostics.Throw(
                ffmpeg.av_interleaved_write_frame(outFmt, outPkt));

            if (encoderPacketPts != ffmpeg.AV_NOPTS_VALUE)
            {
                emittedEncodedFramePts.TryGetValue(
                    encoderPacketPts,
                    out int emittedCount);
                emittedEncodedFramePts[encoderPacketPts] =
                    emittedCount + 1;
            }

            if (muxPacketPts != ffmpeg.AV_NOPTS_VALUE)
                emittedEncodedMuxPts.Add(muxPacketPts);

            outputVideoPacketCount++;
            ffmpeg.av_packet_unref(outPkt);
        }
    }

    internal static (
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
            if (!emittedEncodedFramePts.TryGetValue(
                    submittedPts,
                    out int emittedCount) ||
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

            unexpectedEncodedFrames +=
                submittedEncodedFramePts.Contains(emittedPts)
                    ? Math.Max(0, emittedCount - 1)
                    : emittedCount;
        }

        int copiedPacketMismatch = (int)Math.Min(
            int.MaxValue,
            Math.Abs(
                (long)copiedSourcePacketCount -
                copiedOutputPacketCount));
        int expectedOutputFrames = checked(
            copiedSourcePacketCount +
            submittedEncodedFramePts.Count);
        int mismatchCount = checked(
            copiedPacketMismatch +
            missingEncodedFrames +
            unexpectedEncodedFrames);
        int dropCount = checked(
            Math.Max(
                0,
                copiedSourcePacketCount - copiedOutputPacketCount) +
            missingEncodedFrames);

        return (
            expectedOutputFrames,
            mismatchCount,
            dropCount,
            missingEncodedFrames,
            unexpectedEncodedFrames,
            copiedPacketMismatch);
    }
}
