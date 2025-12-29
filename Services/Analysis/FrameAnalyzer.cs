using Avalonia;
using Avalonia.Media.Imaging;
using FaceShield.Models.Analysis;
using FaceShield.Services.FaceDetection;
using FaceShield.Services.Video;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FaceShield.Services.Analysis
{
    public sealed class FrameAnalyzer : IFrameAnalyzer
    {
        private readonly IFaceDetector _detector;

        public FrameAnalyzer(IFaceDetector detector)
        {
            _detector = detector;
        }

        public async Task<IReadOnlyList<FrameAnalysisResult>> AnalyzeAsync(
            string videoPath,
            IProgress<int>? progress,
            CancellationToken ct)
        {
            // (확실) FfFrameExtractor 안에 이미 FPS 계산 로직이 있어서
            // 여기서는 전체 프레임 수만 추정해도 충분합니다.
            var (fps, totalFrames, _) = ReadVideoInfo(videoPath);

            if (fps <= 0 || totalFrames <= 0)
                return Array.Empty<FrameAnalysisResult>();

            var list = new List<FrameAnalysisResult>();

            using var extractor = new FfFrameExtractor(videoPath);

            await Task.Run(() =>
            {
                for (int idx = 0; idx < totalFrames; idx++)
                {
                    ct.ThrowIfCancellationRequested();

                    var bmp = extractor.GetFrameByIndex(idx);
                    if (bmp == null) continue;

                    var faces = _detector.DetectFaces(bmp);
                    bool hasFace = faces.Count > 0;

                    Rect? first = hasFace ? faces[0].Bounds : null;

                    list.Add(new FrameAnalysisResult
                    {
                        FrameIndex = idx,
                        TimestampSec = idx / fps,
                        HasFace = hasFace,
                        Confidence = hasFace ? 1.0f : 0.0f, // 점수는 실제 detector에서 추출 가능하면 교체
                        FaceBounds = first
                        // 필요하면 FrameAnalysisResult에 리스트 필드 추가해서 전체 bounds 보내도 됨
                    });

                    progress?.Report((int)(idx * 100.0 / Math.Max(1, totalFrames - 1)));
                }

                progress?.Report(100);
            }, ct);

            return list;
        }

        // ↓ 아래 ReadVideoInfo는 AutoMaskGenerator에서도 쓸 거라면,
        // util 클래스로 빼는 게 좋지만 지금은 여기 안에 둡니다.
        private unsafe static (double fps, int totalFrames, double durationSeconds) ReadVideoInfo(string path)
        {
            FFmpeg.AutoGen.AVFormatContext* fmt = null;

            try
            {
                FFmpeg.AutoGen.ffmpeg.av_log_set_level(FFmpeg.AutoGen.ffmpeg.AV_LOG_QUIET);

                if (FFmpeg.AutoGen.ffmpeg.avformat_open_input(&fmt, path, null, null) < 0)
                    throw new InvalidOperationException("Failed to open video.");

                if (FFmpeg.AutoGen.ffmpeg.avformat_find_stream_info(fmt, null) < 0)
                    throw new InvalidOperationException("Failed to read stream info.");

                FFmpeg.AutoGen.AVStream* videoStream = null;

                for (int i = 0; i < fmt->nb_streams; i++)
                {
                    if (fmt->streams[i]->codecpar->codec_type == FFmpeg.AutoGen.AVMediaType.AVMEDIA_TYPE_VIDEO)
                    {
                        videoStream = fmt->streams[i];
                        break;
                    }
                }

                if (videoStream == null)
                    throw new InvalidOperationException("Video stream not found.");

                double fpsValue =
                    videoStream->avg_frame_rate.num != 0
                        ? FFmpeg.AutoGen.ffmpeg.av_q2d(videoStream->avg_frame_rate)
                        : videoStream->r_frame_rate.num != 0
                            ? FFmpeg.AutoGen.ffmpeg.av_q2d(videoStream->r_frame_rate)
                            : 30.0;

                double durationSeconds;

                if (videoStream->duration > 0)
                {
                    durationSeconds =
                        videoStream->duration * FFmpeg.AutoGen.ffmpeg.av_q2d(videoStream->time_base);
                }
                else if (fmt->duration > 0)
                {
                    durationSeconds =
                        fmt->duration / (double)FFmpeg.AutoGen.ffmpeg.AV_TIME_BASE;
                }
                else
                {
                    durationSeconds = 0;
                }

                int frames = (int)Math.Floor(durationSeconds * fpsValue);

                return (
                    fps: fpsValue,
                    totalFrames: Math.Max(frames, 0),
                    durationSeconds: Math.Max(durationSeconds, 0));
            }
            finally
            {
                if (fmt != null)
                    FFmpeg.AutoGen.ffmpeg.avformat_close_input(&fmt);
            }
        }
    }
}
