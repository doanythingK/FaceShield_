using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace FaceShield.Services.Video;

internal static unsafe class VideoExportCopyPolicy
{
    internal static List<int> CollectKeyframeFrameIndices(
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
            if (ffmpeg.avformat_open_input(
                    &inFmt,
                    inputPath,
                    null,
                    null) < 0)
                return keyframes;
            if (ffmpeg.avformat_find_stream_info(inFmt, null) < 0)
                return keyframes;

            int videoStreamIndex =
                FFmpegStreamSelection.FindPrimaryVideoStreamIndex(inFmt);
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

                int resolvedFrameIndex =
                    VideoExportTimingPolicy.ResolveFrameIndexFromPacket(
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

                fallbackFrameIndex =
                    Math.Max(fallbackFrameIndex, resolvedFrameIndex + 1);
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
        int previous = int.MinValue;
        foreach (int index in keyframes)
        {
            if (index == previous)
                continue;
            distinct.Add(index);
            previous = index;
        }

        return distinct;
    }

    internal static (
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
            CancellationToken cancellationToken)
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
            VideoExportFfmpegDiagnostics.Throw(
                ffmpeg.avformat_alloc_output_context2(
                    &outFmt,
                    null,
                    null,
                    outputPath));
            VideoPresentationMetadataPolicy.CopyFormatPresentationMetadata(
                inFmt,
                outFmt);

            int streamCount = (int)inFmt->nb_streams;
            var streamMap = new int[streamCount];
            for (int i = 0; i < streamCount; i++)
            {
                streamMap[i] = -1;
                AVStream* inStream = inFmt->streams[i];
                AVStream* outStream =
                    ffmpeg.avformat_new_stream(outFmt, null);
                if (outStream == null)
                    throw new InvalidOperationException("출력 스트림을 생성하지 못했습니다.");

                VideoExportFfmpegDiagnostics.Throw(
                    ffmpeg.avcodec_parameters_copy(
                        outStream->codecpar,
                        inStream->codecpar));
                outStream->codecpar->codec_tag = 0;
                outStream->time_base = inStream->time_base;
                VideoPresentationMetadataPolicy.CopyStreamPresentationMetadata(
                    inStream,
                    outStream);
                streamMap[i] = outStream->index;
            }

            if ((outFmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
            {
                VideoExportFfmpegDiagnostics.Throw(
                    ffmpeg.avio_open(
                        &outFmt->pb,
                        outputPath,
                        ffmpeg.AVIO_FLAG_WRITE));
            }

            VideoExportFfmpegDiagnostics.Throw(
                ffmpeg.avformat_write_header(outFmt, null));

            int lastReportedFrame = -1;
            while (ffmpeg.av_read_frame(inFmt, pkt) >= 0)
            {
                VideoPresentationMetadataPolicy.EnsureContainerStructureSupported(inFmt);
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);

                int inIndex = pkt->stream_index;
                if (inIndex < 0 ||
                    inIndex >= streamMap.Length ||
                    streamMap[inIndex] < 0)
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
                bool hasMissingVideoTimestamp =
                    isVideoPacket &&
                    (pkt->pts == ffmpeg.AV_NOPTS_VALUE ||
                     pkt->dts == ffmpeg.AV_NOPTS_VALUE);

                ffmpeg.av_packet_rescale_ts(
                    pkt,
                    inStream->time_base,
                    outStream->time_base);
                if (isVideoPacket && hasMissingVideoTimestamp)
                    missingVideoPacketTimestamps++;

                pkt->stream_index = outStream->index;
                pkt->pos = -1;
                long muxVideoPts =
                    isVideoPacket ? pkt->pts : ffmpeg.AV_NOPTS_VALUE;
                long muxVideoDts =
                    isVideoPacket ? pkt->dts : ffmpeg.AV_NOPTS_VALUE;

                VideoExportFfmpegDiagnostics.Throw(
                    ffmpeg.av_interleaved_write_frame(outFmt, pkt));

                if (inIndex == videoStreamIndex)
                {
                    outputVideoPackets++;
                    if (muxVideoPts != ffmpeg.AV_NOPTS_VALUE)
                        outputVideoPts.Add(muxVideoPts);
                }

                if (progress != null &&
                    inIndex == videoStreamIndex &&
                    totalFrames > 0 &&
                    sourceFps > 0.0)
                {
                    long timestamp =
                        muxVideoPts != ffmpeg.AV_NOPTS_VALUE
                            ? muxVideoPts
                            : muxVideoDts;
                    if (timestamp != ffmpeg.AV_NOPTS_VALUE)
                    {
                        double seconds =
                            timestamp * ffmpeg.av_q2d(outStream->time_base);
                        int currentFrame =
                            Math.Clamp(
                                (int)Math.Round(seconds * sourceFps),
                                0,
                                totalFrames);

                        if (currentFrame - lastReportedFrame >= 15 ||
                            currentFrame >= totalFrames)
                        {
                            progress.Report(
                                new ExportProgress(
                                    currentFrame,
                                    totalFrames,
                                    "원본 스트림 복사 중..."));
                            lastReportedFrame = currentFrame;
                        }
                    }
                }

                ffmpeg.av_packet_unref(pkt);
            }

            maxOutputPacketPtsGap =
                VideoExportTimingPolicy.GetMaxSortedPresentationGap(
                    outputVideoPts);

            progress?.Report(
                new ExportProgress(
                    totalFrames,
                    totalFrames,
                    "원본 스트림 복사와 파일 검증을 마무리하는 중..."));
            VideoExportFfmpegDiagnostics.Throw(
                ffmpeg.av_write_trailer(outFmt));

            outputCloseTimer.Start();
            CloseOutputOrThrow(outFmt);
            outputCloseTimer.Stop();
        }
        finally
        {
            ffmpeg.av_packet_free(&pkt);

            if (outFmt != null)
            {
                if ((outFmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0 &&
                    outFmt->pb != null)
                {
                    _ = ffmpeg.avio_closep(&outFmt->pb);
                }

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

    internal static void CloseOutputOrThrow(AVFormatContext* format)
    {
        if (format == null ||
            format->oformat == null ||
            (format->oformat->flags & ffmpeg.AVFMT_NOFILE) != 0 ||
            format->pb == null)
        {
            return;
        }

        VideoExportFfmpegDiagnostics.Throw(
            ffmpeg.avio_closep(&format->pb));
    }
}
