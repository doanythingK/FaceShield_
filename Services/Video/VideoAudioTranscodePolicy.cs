using FFmpeg.AutoGen;
using System;

namespace FaceShield.Services.Video;

internal static unsafe class VideoAudioTranscodePolicy
{
    internal static bool TryInitAudioTranscode(
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
            error =
                $"오디오 디코더를 찾을 수 없습니다(코덱: {GetCodecName(inAudioStream->codecpar->codec_id)}).";
            return false;
        }

        audioDec = ffmpeg.avcodec_alloc_context3(decoder);
        if (audioDec == null)
        {
            error = "오디오 디코더 컨텍스트를 만들 수 없습니다.";
            return false;
        }

        int decErr = ffmpeg.avcodec_parameters_to_context(
            audioDec,
            inAudioStream->codecpar);
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

        int inSampleRate = audioDec->sample_rate > 0
            ? audioDec->sample_rate
            : inAudioStream->codecpar->sample_rate;
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

        AVChannelLayout outLayout =
            GetChannelLayout(aacEncoder, inLayout, inChannels);
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
        audioEnc->time_base = new AVRational
        {
            num = 1,
            den = audioEnc->sample_rate
        };

        if (inAudioStream->codecpar->bit_rate > 0)
            audioEnc->bit_rate = inAudioStream->codecpar->bit_rate;
        else
            audioEnc->bit_rate = 128_000;

        if (outFmt->oformat != null &&
            (outFmt->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
        {
            audioEnc->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
        }

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

        int parErr = ffmpeg.avcodec_parameters_from_context(
            outAudioStream->codecpar,
            audioEnc);
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

        audioFifo = ffmpeg.av_audio_fifo_alloc(
            audioEnc->sample_fmt,
            outChannels,
            1);
        if (audioFifo == null)
        {
            error = "오디오 FIFO 초기화 실패.";
            return false;
        }

        return true;
    }

    internal static void ConvertAndQueueAudioFrame(
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

        int written = ffmpeg.av_audio_fifo_write(
            fifo,
            (void**)convFrame->extended_data,
            converted);
        if (written < 0)
            Throw(written);
        if (written < converted)
        {
            throw new InvalidOperationException(
                "오디오 FIFO에 샘플을 충분히 쓰지 못했습니다.");
        }

        ffmpeg.av_frame_unref(convFrame);
    }

    internal static void DrainAudioFifo(
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
        if (fifo == null ||
            audioEnc == null ||
            outAudioStream == null ||
            outFmt == null)
        {
            return;
        }

        int frameSize = audioEnc->frame_size > 0
            ? audioEnc->frame_size
            : 1024;
        bool variable =
            audioEnc->codec != null &&
            (audioEnc->codec->capabilities &
             ffmpeg.AV_CODEC_CAP_VARIABLE_FRAME_SIZE) != 0;

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
            int read = ffmpeg.av_audio_fifo_read(
                fifo,
                (void**)outFrame->extended_data,
                readSamples);
            if (read < 0)
                Throw(read);

            if (pad && read < nbSamples)
            {
                int silence = nbSamples - read;
                int channels = audioEnc->ch_layout.nb_channels;
                ffmpeg.av_samples_set_silence(
                    outFrame->extended_data,
                    read,
                    silence,
                    channels,
                    audioEnc->sample_fmt);
            }

            outFrame->pts = audioPts;
            audioPts += nbSamples;

            Throw(ffmpeg.avcodec_send_frame(audioEnc, outFrame));
            ffmpeg.av_frame_unref(outFrame);

            while (ffmpeg.avcodec_receive_packet(audioEnc, outPkt) == 0)
            {
                outPkt->stream_index = outAudioStream->index;
                ffmpeg.av_packet_rescale_ts(
                    outPkt,
                    audioEnc->time_base,
                    outAudioStream->time_base);
                VideoExportTimingPolicy.NormalizeEncodedPacketTimestamps(
                    outPkt,
                    ref lastPacketPts,
                    ref hasLastPacketPts,
                    ref lastPacketDts,
                    ref hasLastPacketDts);
                Throw(ffmpeg.av_interleaved_write_frame(outFmt, outPkt));
                ffmpeg.av_packet_unref(outPkt);
            }

            if (!flush &&
                !variable &&
                ffmpeg.av_audio_fifo_size(fifo) < frameSize)
            {
                break;
            }
        }
    }

    internal static void FlushAudioTranscode(
        AVCodecContext* audioDec,
        AVCodecContext* audioEnc,
        AVStream* outAudioStream,
        AVFormatContext* outFmt,
        SwrContext* swr,
        AVAudioFifo* audioFifo,
        AVFrame* audioFrame,
        AVFrame* audioConvFrame,
        AVFrame* audioEncFrame,
        AVPacket* audioPkt,
        ref long audioPts,
        ref long lastPacketPts,
        ref bool hasLastPacketPts,
        ref long lastPacketDts,
        ref bool hasLastPacketDts)
    {
        int flushErr = ffmpeg.avcodec_send_packet(audioDec, null);
        if (flushErr < 0)
            Throw(flushErr);

        while (ffmpeg.avcodec_receive_frame(audioDec, audioFrame) == 0)
        {
            ConvertAndQueueAudioFrame(
                audioFrame,
                audioDec,
                audioEnc,
                swr,
                audioFifo,
                audioConvFrame);
            DrainAudioFifo(
                audioFifo,
                audioEnc,
                outAudioStream,
                outFmt,
                audioPkt,
                audioEncFrame,
                ref audioPts,
                ref lastPacketPts,
                ref hasLastPacketPts,
                ref lastPacketDts,
                ref hasLastPacketDts,
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
            ref lastPacketPts,
            ref hasLastPacketPts,
            ref lastPacketDts,
            ref hasLastPacketDts,
            flush: true);

        int sendFinalErr = ffmpeg.avcodec_send_frame(audioEnc, null);
        if (sendFinalErr < 0)
            Throw(sendFinalErr);

        while (ffmpeg.avcodec_receive_packet(audioEnc, audioPkt) == 0)
        {
            audioPkt->stream_index = outAudioStream->index;
            ffmpeg.av_packet_rescale_ts(
                audioPkt,
                audioEnc->time_base,
                outAudioStream->time_base);
            VideoExportTimingPolicy.NormalizeEncodedPacketTimestamps(
                audioPkt,
                ref lastPacketPts,
                ref hasLastPacketPts,
                ref lastPacketDts,
                ref hasLastPacketDts);
            Throw(ffmpeg.av_interleaved_write_frame(outFmt, audioPkt));
            ffmpeg.av_packet_unref(audioPkt);
        }
    }

    private static AVSampleFormat GetSampleFmt(
        AVCodec* encoder,
        AVSampleFormat preferred)
    {
#pragma warning disable CS0618
        if (encoder == null || encoder->sample_fmts == null)
            return preferred;

        for (AVSampleFormat* current = encoder->sample_fmts;
             *current != AVSampleFormat.AV_SAMPLE_FMT_NONE;
             current++)
        {
            if (*current == preferred)
                return preferred;
        }

        return encoder->sample_fmts[0];
#pragma warning restore CS0618
    }

    private static int GetSampleRate(AVCodec* encoder, int preferred)
    {
#pragma warning disable CS0618
        if (encoder == null || encoder->supported_samplerates == null)
            return preferred > 0 ? preferred : 44100;

        int* rates = encoder->supported_samplerates;
        int best = rates[0];
        for (int* current = rates; *current != 0; current++)
        {
            if (*current == preferred)
                return preferred;
            best = *current;
        }

        return best > 0
            ? best
            : (preferred > 0 ? preferred : 44100);
#pragma warning restore CS0618
    }

    private static AVChannelLayout GetChannelLayout(
        AVCodec* encoder,
        AVChannelLayout preferred,
        int preferredChannels)
    {
#pragma warning disable CS0618
        if (encoder == null || encoder->ch_layouts == null)
            return preferred;

        AVChannelLayout* layouts = encoder->ch_layouts;
        AVChannelLayout best = layouts[0];
        for (AVChannelLayout* current = layouts;
             current->nb_channels != 0;
             current++)
        {
            int channels = current->nb_channels;
            if (channels == preferredChannels)
                return *current;
            best = *current;
        }

        return best;
#pragma warning restore CS0618
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

        return string.IsNullOrWhiteSpace(name)
            ? codecId.ToString()
            : name;
    }

    private static string GetErrorMessage(int errorCode)
    {
        byte* buffer = stackalloc byte[1024];
        ffmpeg.av_strerror(errorCode, buffer, 1024);
        return System.Text.Encoding.UTF8
            .GetString(new ReadOnlySpan<byte>(buffer, 1024))
            .TrimEnd('\0');
    }

    private static void Throw(int errorCode)
    {
        if (errorCode >= 0)
            return;

        throw new InvalidOperationException(GetErrorMessage(errorCode));
    }
}
