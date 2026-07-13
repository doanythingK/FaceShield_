using FFmpeg.AutoGen;

namespace FaceShield.Services.Video;

public static unsafe class FFmpegContainerStructureGuard
{
    public static string? FindUnsupportedStructure(AVFormatContext* format)
    {
        if (format == null)
            return null;
        if (format->nb_programs > 0)
            return "programs";
        if (format->nb_stream_groups > 0)
            return "stream-groups";

        return null;
    }
}
