using FFmpeg.AutoGen;

namespace FaceShield.Services.Video;

public static unsafe class FFmpegStreamSelection
{
    public static int FindPrimaryVideoStreamIndex(AVFormatContext* format)
    {
        if (format == null)
            return -1;

        int firstVideoStreamIndex = -1;
        for (int i = 0; i < format->nb_streams; i++)
        {
            AVStream* stream = format->streams[i];
            if (stream == null ||
                stream->codecpar == null ||
                stream->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_VIDEO ||
                IsAuxiliaryImageStream(stream))
            {
                continue;
            }

            if (firstVideoStreamIndex < 0)
                firstVideoStreamIndex = i;

            if ((stream->disposition & ffmpeg.AV_DISPOSITION_DEFAULT) != 0)
                return i;
        }

        return firstVideoStreamIndex;
    }

    private static bool IsAuxiliaryImageStream(AVStream* stream)
    {
        int auxiliaryDisposition =
            ffmpeg.AV_DISPOSITION_ATTACHED_PIC |
            ffmpeg.AV_DISPOSITION_TIMED_THUMBNAILS |
            ffmpeg.AV_DISPOSITION_STILL_IMAGE;
        return (stream->disposition & auxiliaryDisposition) != 0;
    }
}
