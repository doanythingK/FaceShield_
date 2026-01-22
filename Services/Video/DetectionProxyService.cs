using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace FaceShield.Services.Video;

public static unsafe class DetectionProxyService
{
    private readonly record struct ProxyOutputFormat(string ContainerName, string Extension, AVCodecID CodecId, string Label);

    public static bool TryGetVideoBitDepth(
        string inputPath,
        out int bitDepth,
        out int width,
        out int height)
    {
        bitDepth = 8;
        width = 0;
        height = 0;

        AVFormatContext* fmt = null;
        try
        {
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_QUIET);
            if (ffmpeg.avformat_open_input(&fmt, inputPath, null, null) < 0)
                return false;
            if (ffmpeg.avformat_find_stream_info(fmt, null) < 0)
                return false;

            AVStream* videoStream = null;
            for (int i = 0; i < fmt->nb_streams; i++)
            {
                if (fmt->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    videoStream = fmt->streams[i];
                    break;
                }
            }

            if (videoStream == null)
                return false;

            width = videoStream->codecpar->width;
            height = videoStream->codecpar->height;
            if (videoStream->codecpar->format != -1)
            {
                AVPixelFormat fmtId = (AVPixelFormat)videoStream->codecpar->format;
                AVPixFmtDescriptor* desc = ffmpeg.av_pix_fmt_desc_get(fmtId);
                if (desc != null && desc->nb_components > 0)
                    bitDepth = desc->comp[0].depth;
            }
            else
            {
                int? detected = TryDetectBitDepthByDecoding(fmt, videoStream);
                if (detected.HasValue)
                    bitDepth = detected.Value;
            }

            return true;
        }
        finally
        {
            if (fmt != null)
                ffmpeg.avformat_close_input(&fmt);
        }
    }

    private static int? TryDetectBitDepthByDecoding(AVFormatContext* fmt, AVStream* videoStream)
    {
        if (fmt == null || videoStream == null)
            return null;

        AVCodec* decoder = ffmpeg.avcodec_find_decoder(videoStream->codecpar->codec_id);
        if (decoder == null)
            return null;

        AVCodecContext* dec = null;
        AVPacket* pkt = null;
        AVFrame* frame = null;

        try
        {
            dec = ffmpeg.avcodec_alloc_context3(decoder);
            if (dec == null)
                return null;

            if (ffmpeg.avcodec_parameters_to_context(dec, videoStream->codecpar) < 0)
                return null;

            if (ffmpeg.avcodec_open2(dec, decoder, null) < 0)
                return null;

            pkt = ffmpeg.av_packet_alloc();
            frame = ffmpeg.av_frame_alloc();
            if (pkt == null || frame == null)
                return null;

            int streamIndex = videoStream->index;
            int? depth = null;

            while (ffmpeg.av_read_frame(fmt, pkt) >= 0)
            {
                if (pkt->stream_index != streamIndex)
                {
                    ffmpeg.av_packet_unref(pkt);
                    continue;
                }

                if (ffmpeg.avcodec_send_packet(dec, pkt) < 0)
                {
                    ffmpeg.av_packet_unref(pkt);
                    break;
                }
                ffmpeg.av_packet_unref(pkt);

                while (ffmpeg.avcodec_receive_frame(dec, frame) == 0)
                {
                    AVPixelFormat fmtId = (AVPixelFormat)frame->format;
                    AVPixFmtDescriptor* desc = ffmpeg.av_pix_fmt_desc_get(fmtId);
                    if (desc != null && desc->nb_components > 0)
                        depth = desc->comp[0].depth;

                    ffmpeg.av_frame_unref(frame);
                    return depth;
                }
            }

            ffmpeg.avcodec_send_packet(dec, null);
            while (ffmpeg.avcodec_receive_frame(dec, frame) == 0)
            {
                AVPixelFormat fmtId = (AVPixelFormat)frame->format;
                AVPixFmtDescriptor* desc = ffmpeg.av_pix_fmt_desc_get(fmtId);
                if (desc != null && desc->nb_components > 0)
                    depth = desc->comp[0].depth;
                ffmpeg.av_frame_unref(frame);
                return depth;
            }

            return null;
        }
        finally
        {
            if (frame != null) ffmpeg.av_frame_free(&frame);
            if (pkt != null) ffmpeg.av_packet_free(&pkt);
            if (dec != null) ffmpeg.avcodec_free_context(&dec);
        }
    }

    public static string? EnsureDetectionProxy(
        string inputPath,
        DetectionProxyPreset preset,
        IProgress<ProxyProgress>? progress,
        out string? statusMessage,
        System.Threading.CancellationToken ct)
    {
        statusMessage = null;

        if (!TryGetVideoBitDepth(inputPath, out int bitDepth, out int width, out int height))
        {
            statusMessage = "프록시 생략: 비트뎁스 판별 실패";
            return null;
        }

        if (bitDepth <= 8)
        {
            statusMessage = $"프록시 생략: {bitDepth}bit 영상";
            return inputPath;
        }

        if (width <= 0 || height <= 0)
        {
            statusMessage = "프록시 생략: 해상도 정보 없음";
            return null;
        }

        var candidates = GetProxyOutputCandidates();
        if (candidates.Count == 0)
        {
            statusMessage = "프록시 생성 실패: 사용 가능한 인코더 없음";
            return null;
        }

        foreach (var candidate in candidates)
        {
            string cachedPath = BuildProxyPath(inputPath, preset, candidate);
            if (File.Exists(cachedPath))
            {
                statusMessage = $"프록시 캐시 사용 ({LabelFor(preset)}, {candidate.Label})";
                return cachedPath;
            }
        }

        string? lastError = null;
        foreach (var candidate in candidates)
        {
            string proxyPath = BuildProxyPath(inputPath, preset, candidate);
            string tempPath = BuildTempProxyPath(proxyPath);
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }

            statusMessage = $"프록시 생성 중... ({LabelFor(preset)}, {candidate.Label})";
            Directory.CreateDirectory(Path.GetDirectoryName(proxyPath)!);

            try
            {
                CreateProxyInternal(inputPath, tempPath, preset, candidate, progress, ct);
                if (File.Exists(proxyPath))
                    File.Delete(proxyPath);
                File.Move(tempPath, proxyPath, overwrite: true);
                statusMessage = $"프록시 생성 완료 ({LabelFor(preset)}, {candidate.Label})";
                return proxyPath;
            }
            catch (OperationCanceledException)
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }
                statusMessage = "프록시 생성 취소됨";
                return null;
            }
            catch (Exception ex)
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }
                lastError = ex.Message;
            }
        }

        statusMessage = $"프록시 생성 실패: {lastError ?? "알 수 없는 오류"}";
        return null;
    }

    private static string BuildProxyPath(
        string inputPath,
        DetectionProxyPreset preset,
        ProxyOutputFormat format)
    {
        var info = new FileInfo(inputPath);
        string key = $"{inputPath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{(int)preset}|{format.ContainerName}|{(int)format.CodecId}|{format.Extension}";
        string hash = HashKey(key);

        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FaceShield",
            "proxy");
        return Path.Combine(root, $"{hash}{format.Extension}");
    }

    private static string BuildTempProxyPath(string proxyPath)
    {
        string dir = Path.GetDirectoryName(proxyPath) ?? string.Empty;
        string name = Path.GetFileNameWithoutExtension(proxyPath);
        string ext = Path.GetExtension(proxyPath);
        return Path.Combine(dir, $"{name}_tmp{ext}");
    }

    private static string HashKey(string key)
    {
        using var sha = SHA256.Create();
        byte[] data = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(data).Substring(0, 16);
    }

    private static void CreateProxyInternal(
        string inputPath,
        string outputPath,
        DetectionProxyPreset preset,
        ProxyOutputFormat format,
        IProgress<ProxyProgress>? progress,
        System.Threading.CancellationToken ct)
    {
        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);

        AVFormatContext* inFmt = null;
        AVFormatContext* outFmt = null;
        AVCodecContext* dec = null;
        AVCodecContext* enc = null;
        SwsContext* sws = null;
        AVPacket* pkt = ffmpeg.av_packet_alloc();
        AVPacket* outPkt = ffmpeg.av_packet_alloc();
        AVFrame* decFrame = ffmpeg.av_frame_alloc();
        AVFrame* encFrame = ffmpeg.av_frame_alloc();

        int videoStreamIndex = -1;
        int frameIndex = 0;

        try
        {
            Throw(ffmpeg.avformat_open_input(&inFmt, inputPath, null, null));
            Throw(ffmpeg.avformat_find_stream_info(inFmt, null));

            AVStream* inStream = null;
            for (int i = 0; i < inFmt->nb_streams; i++)
            {
                if (inFmt->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    videoStreamIndex = i;
                    inStream = inFmt->streams[i];
                    break;
                }
            }

            if (videoStreamIndex < 0 || inStream == null)
                throw new InvalidOperationException("비디오 스트림을 찾을 수 없습니다.");

            int totalFrames = ResolveTotalFrames(inFmt, inStream);

            AVCodec* decoder = ffmpeg.avcodec_find_decoder(inStream->codecpar->codec_id);
            if (decoder == null)
                throw new InvalidOperationException("비디오 디코더를 찾을 수 없습니다.");

            dec = ffmpeg.avcodec_alloc_context3(decoder);
            if (dec == null)
                throw new InvalidOperationException("디코더 컨텍스트 생성 실패");
            Throw(ffmpeg.avcodec_parameters_to_context(dec, inStream->codecpar));
            Throw(ffmpeg.avcodec_open2(dec, decoder, null));

            Throw(ffmpeg.avformat_alloc_output_context2(&outFmt, null, format.ContainerName, outputPath));
            if (outFmt == null)
                throw new InvalidOperationException("출력 포맷 컨텍스트 생성 실패");

            AVCodec* encoder = ffmpeg.avcodec_find_encoder(format.CodecId);
            if (encoder == null)
                throw new InvalidOperationException("프록시 인코더를 찾을 수 없습니다.");

            AVStream* outStream = ffmpeg.avformat_new_stream(outFmt, encoder);
            if (outStream == null)
                throw new InvalidOperationException("출력 스트림 생성 실패");

            enc = ffmpeg.avcodec_alloc_context3(encoder);
            if (enc == null)
                throw new InvalidOperationException("인코더 컨텍스트 생성 실패");

            enc->width = dec->width;
            enc->height = dec->height;
            enc->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
            enc->time_base = inStream->time_base.num > 0 && inStream->time_base.den > 0
                ? inStream->time_base
                : new AVRational { num = 1, den = 30 };
            enc->framerate = inStream->avg_frame_rate.num != 0
                ? inStream->avg_frame_rate
                : inStream->r_frame_rate.num != 0 ? inStream->r_frame_rate : new AVRational { num = 30, den = 1 };
            enc->gop_size = 60;
            enc->max_b_frames = 0;
            enc->thread_count = Math.Max(1, Environment.ProcessorCount - 2);

            if ((outFmt->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
                enc->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

            (string presetLabel, int crf) = GetPresetSettings(preset);

            if (encoder->name != null)
            {
                string name = Marshal.PtrToStringAnsi((IntPtr)encoder->name) ?? string.Empty;
                if (name.Contains("x264", StringComparison.OrdinalIgnoreCase))
                {
                    ffmpeg.av_opt_set(enc->priv_data, "preset", presetLabel, 0);
                    ffmpeg.av_opt_set(enc->priv_data, "crf", crf.ToString(), 0);
                    ffmpeg.av_opt_set(enc->priv_data, "tune", "fastdecode", 0);
                }
            }

            Throw(ffmpeg.avcodec_open2(enc, encoder, null));
            Throw(ffmpeg.avcodec_parameters_from_context(outStream->codecpar, enc));
            outStream->time_base = enc->time_base;

            if ((outFmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
                Throw(ffmpeg.avio_open(&outFmt->pb, outputPath, ffmpeg.AVIO_FLAG_WRITE));

            Throw(ffmpeg.avformat_write_header(outFmt, null));

            encFrame->format = (int)enc->pix_fmt;
            encFrame->width = enc->width;
            encFrame->height = enc->height;
            Throw(ffmpeg.av_frame_get_buffer(encFrame, 32));

            sws = ffmpeg.sws_getContext(
                dec->width,
                dec->height,
                dec->pix_fmt,
                enc->width,
                enc->height,
                enc->pix_fmt,
                (int)SwsFlags.SWS_BILINEAR,
                null, null, null);

            var frameRate = enc->framerate;
            AVRational invFps = frameRate.num > 0 ? new AVRational { num = frameRate.den, den = frameRate.num } : enc->time_base;

            while (!ct.IsCancellationRequested && ffmpeg.av_read_frame(inFmt, pkt) >= 0)
            {
                if (pkt->stream_index != videoStreamIndex)
                {
                    ffmpeg.av_packet_unref(pkt);
                    continue;
                }

                Throw(ffmpeg.avcodec_send_packet(dec, pkt));
                ffmpeg.av_packet_unref(pkt);

                while (ffmpeg.avcodec_receive_frame(dec, decFrame) == 0)
                {
                    if (ct.IsCancellationRequested)
                        throw new OperationCanceledException(ct);

                    Throw(ffmpeg.av_frame_make_writable(encFrame));

                    ffmpeg.sws_scale(
                        sws,
                        decFrame->data,
                        decFrame->linesize,
                        0,
                        decFrame->height,
                        encFrame->data,
                        encFrame->linesize);

                    long pts = decFrame->best_effort_timestamp;
                    if (pts == ffmpeg.AV_NOPTS_VALUE)
                        pts = ffmpeg.av_rescale_q(frameIndex, invFps, enc->time_base);
                    encFrame->pts = pts;

                    Throw(ffmpeg.avcodec_send_frame(enc, encFrame));
                    while (ffmpeg.avcodec_receive_packet(enc, outPkt) == 0)
                    {
                        outPkt->stream_index = outStream->index;
                        Throw(ffmpeg.av_interleaved_write_frame(outFmt, outPkt));
                        ffmpeg.av_packet_unref(outPkt);
                    }

                    frameIndex++;
                    if (progress != null && (frameIndex % 15 == 0 || (totalFrames > 0 && frameIndex >= totalFrames)))
                        progress.Report(new ProxyProgress(frameIndex, totalFrames, DateTime.UtcNow));
                    ffmpeg.av_frame_unref(decFrame);
                }
            }

            Throw(ffmpeg.avcodec_send_frame(enc, null));
            while (ffmpeg.avcodec_receive_packet(enc, outPkt) == 0)
            {
                outPkt->stream_index = outStream->index;
                Throw(ffmpeg.av_interleaved_write_frame(outFmt, outPkt));
                ffmpeg.av_packet_unref(outPkt);
            }

            Throw(ffmpeg.av_write_trailer(outFmt));
            if (progress != null)
                progress.Report(new ProxyProgress(frameIndex, totalFrames, DateTime.UtcNow));
        }
        finally
        {
            if (sws != null) ffmpeg.sws_freeContext(sws);
            if (dec != null) ffmpeg.avcodec_free_context(&dec);
            if (enc != null) ffmpeg.avcodec_free_context(&enc);
            if (decFrame != null) ffmpeg.av_frame_free(&decFrame);
            if (encFrame != null) ffmpeg.av_frame_free(&encFrame);
            if (pkt != null) ffmpeg.av_packet_free(&pkt);
            if (outPkt != null) ffmpeg.av_packet_free(&outPkt);
            if (outFmt != null)
            {
                if ((outFmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
                    ffmpeg.avio_closep(&outFmt->pb);
                ffmpeg.avformat_free_context(outFmt);
            }
            if (inFmt != null)
                ffmpeg.avformat_close_input(&inFmt);
        }
    }

    private static (string presetLabel, int crf) GetPresetSettings(DetectionProxyPreset preset)
    {
        return preset switch
        {
            DetectionProxyPreset.OptionA => ("veryfast", 23),
            DetectionProxyPreset.OptionB => ("ultrafast", 28),
            _ => ("veryfast", 20)
        };
    }

    private static int ResolveTotalFrames(AVFormatContext* fmt, AVStream* stream)
    {
        if (stream == null)
            return 0;

        long nbFrames = stream->nb_frames;
        if (nbFrames > 0 && nbFrames < int.MaxValue)
            return (int)nbFrames;

        double fps =
            stream->avg_frame_rate.num != 0
                ? ffmpeg.av_q2d(stream->avg_frame_rate)
                : stream->r_frame_rate.num != 0
                    ? ffmpeg.av_q2d(stream->r_frame_rate)
                    : 0.0;

        double durationSeconds = 0.0;
        if (stream->duration > 0)
            durationSeconds = stream->duration * ffmpeg.av_q2d(stream->time_base);
        else if (fmt != null && fmt->duration > 0)
            durationSeconds = fmt->duration / (double)ffmpeg.AV_TIME_BASE;

        if (fps > 0 && durationSeconds > 0)
            return (int)Math.Round(durationSeconds * fps);

        return 0;
    }

    private static string LabelFor(DetectionProxyPreset preset)
    {
        return preset switch
        {
            DetectionProxyPreset.OptionA => "A옵션",
            DetectionProxyPreset.OptionB => "B옵션",
            _ => "원래 설정"
        };
    }

    private static void Throw(int err)
    {
        if (err >= 0) return;

        byte* buf = stackalloc byte[1024];
        ffmpeg.av_strerror(err, buf, 1024);
        throw new InvalidOperationException(
            Encoding.UTF8.GetString(new ReadOnlySpan<byte>(buf, 1024)).TrimEnd('\0'));
    }

    private static List<ProxyOutputFormat> GetProxyOutputCandidates()
    {
        var candidates = new List<ProxyOutputFormat>
        {
            new("mp4", ".mp4", AVCodecID.AV_CODEC_ID_H264, "MP4/H264"),
            new("mp4", ".mp4", AVCodecID.AV_CODEC_ID_MPEG4, "MP4/MPEG4"),
            new("matroska", ".mkv", AVCodecID.AV_CODEC_ID_H264, "MKV/H264"),
            new("matroska", ".mkv", AVCodecID.AV_CODEC_ID_MPEG4, "MKV/MPEG4"),
            new("avi", ".avi", AVCodecID.AV_CODEC_ID_MPEG4, "AVI/MPEG4")
        };

        var available = new List<ProxyOutputFormat>();
        foreach (var candidate in candidates)
        {
            if (!IsOutputFormatSupported(candidate.ContainerName, candidate.CodecId))
                continue;
            if (ffmpeg.avcodec_find_encoder(candidate.CodecId) == null)
                continue;
            available.Add(candidate);
        }

        return available;
    }

    private static bool IsOutputFormatSupported(string formatName, AVCodecID codecId)
    {
        AVOutputFormat* ofmt = ffmpeg.av_guess_format(formatName, null, null);
        if (ofmt == null)
            return false;

        int supported = ffmpeg.avformat_query_codec(ofmt, codecId, 0);
        return supported > 0;
    }
}
