using Avalonia;
using Avalonia.Media.Imaging;
using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace FaceShield.Services.Video;

internal static unsafe class VideoFrameProcessingPolicy
{
    internal static void ProcessDecodedVideoFrame(
        IFrameMaskProvider maskProvider,
        MaskedVideoExporter masked,
        bool staticHdrConfigured,
        bool x264RgbConfigured,
        VideoHdrMetadata? configuredHdrMetadata,
        ref int directFaceBlurFrames,
        ref int bitmapMaskBlurFrames,
        ref int nativeYuvBlurFrames,
        AVFrame* frame,
        AVFrame* bgra,
        AVFrame* encFrame,
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
            VideoExportCompatibilityPolicy.ThrowInterlacedAutoMosaicUnsupported(frameFieldOrder);
        }

        string? unsupportedFrameMetadata =
            FFmpegHdrMetadataGuard.FindUnsupportedMetadata(frame);
        if (unsupportedFrameMetadata != null)
            VideoExportCompatibilityPolicy.ThrowUnsupportedDynamicVideoMetadata(unsupportedFrameMetadata);
        VideoExportFidelityPolicy.ValidateDecodedFrameFormatCompatibility(frame, enc, x264RgbConfigured);

        if (FFmpegHdrMetadataGuard.RequiresStaticHdrConfiguration(
                frame,
                staticHdrConfigured))
        {
            throw new InvalidOperationException(
                "정적 HDR 메타데이터를 인코더 초기화 전에 확인하지 못했습니다. " +
                "품질 저하를 막기 위해 내보내기를 중단했습니다.");
        }
        VideoHdrMetadataPolicy.ValidateFrameStaticHdrMetadata(frame, configuredHdrMetadata);

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
        WriteableBitmap? ownedMask = null;
        IReadOnlyList<Rect>? faceRects = null;
        bool mightHaveMask = blurRanges == null ||
            VideoExportFrameRangePolicy.IsFrameInBlurRanges(decodedFrameOrdinal, blurRanges, ref blurRangeCursor);
        bool frameWasBlurred = false;

        if (mightHaveMask && maskProvider is FrameMaskProvider provider)
        {
            if (provider.TryGetStoredMaskBorrowed(decodedFrameOrdinal, out var stored))
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
            ownedMask = maskProvider.GetFinalMask(decodedFrameOrdinal);
            mask = ownedMask;
        }

        using var ownedMaskScope = ownedMask;
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
                VideoExportFfmpegDiagnostics.Throw(ffmpeg.av_frame_make_writable(frame));
                nativeYuvFrame = frame;
            }
            else if (MaskedVideoExporter.CanApplyNativeYuv(encFrame))
            {
                var tNativeSws = Stopwatch.StartNew();
                VideoExportFfmpegDiagnostics.Throw(ffmpeg.av_frame_make_writable(encFrame));
                VideoFrameColorPolicy.CopyFrameEncodingProperties(frame, encFrame);
                VideoFrameColorPolicy.ScaleFramePreservingColor(
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
                    ? masked.TryApplyMaskAndBlurNative(nativeYuvFrame, mask, blurRadius)
                    : masked.TryApplyFaceRectsAndBlurNative(nativeYuvFrame, faceRects!, blurRadius);
                tNativeMask.Stop();
                maskMs += tNativeMask.ElapsedMilliseconds;
            }
        }

        if (nativeYuvApplied)
        {
            if (mask != null)
                bitmapMaskBlurFrames++;
            else
                directFaceBlurFrames++;
            nativeYuvBlurFrames++;
            frameWasBlurred = true;
            VideoExportTimingPolicy.ApplyEncodingTiming(nativeYuvFrame, encodedPts, encodedDuration);

            encodeTimer.Start();
            VideoExportFfmpegDiagnostics.ThrowVideoEncoderError(
                ffmpeg.avcodec_send_frame(enc, nativeYuvFrame),
                enc,
                "프레임 전송");
            VideoExportPacketPolicy.DrainEncoderPackets(
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
            VideoExportFfmpegDiagnostics.Throw(ffmpeg.av_frame_make_writable(bgra));
            VideoFrameColorPolicy.SetBgraColorProperties(frame, bgra);
            VideoFrameColorPolicy.ScaleFramePreservingColor(
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
                bgraBlurApplied = masked.ApplyMaskAndBlur(bgra, mask, blurRadius, faceRects);
                bitmapMaskBlurFrames++;
            }
            else
            {
                bgraBlurApplied = masked.ApplyFaceRectsAndBlur(bgra, faceRects!, blurRadius);
                directFaceBlurFrames++;
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
            VideoExportFfmpegDiagnostics.Throw(ffmpeg.av_frame_make_writable(encFrame));
            VideoFrameColorPolicy.CopyFrameEncodingProperties(frame, encFrame);
            VideoFrameColorPolicy.ScaleFramePreservingColor(
                swsBgraToEnc,
                encFrame,
                bgra,
                "BGRA 마스크 출력 변환");
            tEncSws.Stop();
            swsToEncMs += tEncSws.ElapsedMilliseconds;

            VideoExportTimingPolicy.ApplyEncodingTiming(encFrame, encodedPts, encodedDuration);

            encodeTimer.Start();
            VideoExportFfmpegDiagnostics.ThrowVideoEncoderError(
                ffmpeg.avcodec_send_frame(enc, encFrame),
                enc,
                "프레임 전송");
            VideoExportPacketPolicy.DrainEncoderPackets(
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
                VideoExportFfmpegDiagnostics.Throw(ffmpeg.av_frame_make_writable(encFrame));
                VideoFrameColorPolicy.CopyFrameEncodingProperties(frame, encFrame);
                VideoFrameColorPolicy.ScaleFramePreservingColor(
                    swsDecToEnc,
                    encFrame,
                    frame,
                    "인코더 픽셀 형식 변환");
                tEncSws.Stop();
                swsToEncMs += tEncSws.ElapsedMilliseconds;

                VideoExportTimingPolicy.ApplyEncodingTiming(encFrame, encodedPts, encodedDuration);

                encodeTimer.Start();
                VideoExportFfmpegDiagnostics.ThrowVideoEncoderError(
                    ffmpeg.avcodec_send_frame(enc, encFrame),
                    enc,
                    "프레임 전송");
                VideoExportPacketPolicy.DrainEncoderPackets(
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
                VideoExportFfmpegDiagnostics.ThrowVideoEncoderError(
                    ffmpeg.avcodec_send_frame(enc, frame),
                    enc,
                    "프레임 전송");
                VideoExportPacketPolicy.DrainEncoderPackets(
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

        VideoExportProgressPolicy.ReportVideoProgress(progress, totalFrames, ref lastReportedFrame, decodedFrameOrdinal);
        if (decodedFrameOrdinal % 60 == 0)
        {
            Debug.WriteLine(
                $"[Export] frames={decodedFrameOrdinal}, swsToBgraMs={swsToBgraMs}, maskMs={maskMs}, swsToEncMs={swsToEncMs}, encodeMs={encodeTimer.ElapsedMilliseconds}, totalMs={swTotal.ElapsedMilliseconds}");
        }
    }

    internal static void FlushVideoPipeline(
        IFrameMaskProvider maskProvider,
        MaskedVideoExporter masked,
        bool staticHdrConfigured,
        bool x264RgbConfigured,
        VideoHdrMetadata? configuredHdrMetadata,
        ref int directFaceBlurFrames,
        ref int bitmapMaskBlurFrames,
        ref int nativeYuvBlurFrames,
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
            VideoExportFfmpegDiagnostics.Throw(sendErr);

        int videoReceiveResult;
        while ((videoReceiveResult = ffmpeg.avcodec_receive_frame(dec, frame)) == 0)
        {
            ProcessDecodedVideoFrame(
                maskProvider,
                masked,
                staticHdrConfigured,
                x264RgbConfigured,
                configuredHdrMetadata,
                ref directFaceBlurFrames,
                ref bitmapMaskBlurFrames,
                ref nativeYuvBlurFrames,
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
                expectedBlurFrameIndices,
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
                $"비디오 디코더 flush 중 오류가 발생했습니다: {VideoExportFfmpegDiagnostics.GetErrorMessage(videoReceiveResult)}");
        }

        encodeTimer.Start();
        encoderFlushTimer.Start();
        int encErr = ffmpeg.avcodec_send_frame(enc, null);
        if (encErr < 0 && encErr != ffmpeg.AVERROR_EOF)
            VideoExportFfmpegDiagnostics.ThrowVideoEncoderError(encErr, enc, "종료 프레임 전송");
        VideoExportPacketPolicy.DrainEncoderPackets(
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
}
