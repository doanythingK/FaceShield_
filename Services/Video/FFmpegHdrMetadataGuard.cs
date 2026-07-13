using FFmpeg.AutoGen;

namespace FaceShield.Services.Video;

public static unsafe class FFmpegHdrMetadataGuard
{
    public static string? FindUnsupportedMetadata(AVCodecParameters* parameters)
    {
        if (parameters == null ||
            parameters->coded_side_data == null ||
            parameters->nb_coded_side_data <= 0)
        {
            return null;
        }

        for (int i = 0; i < parameters->nb_coded_side_data; i++)
        {
            string? name = GetUnsupportedPacketMetadataName(
                (parameters->coded_side_data + i)->type);
            if (name != null)
                return name;
        }

        return null;
    }

    public static string? FindUnsupportedMetadata(AVFrame* frame)
    {
        if (frame == null)
            return null;

        if (HasSideData(frame, AVFrameSideDataType.AV_FRAME_DATA_DOVI_RPU_BUFFER) ||
            HasSideData(frame, AVFrameSideDataType.AV_FRAME_DATA_DOVI_METADATA))
        {
            return "Dolby Vision";
        }

        if (HasSideData(frame, AVFrameSideDataType.AV_FRAME_DATA_DYNAMIC_HDR_PLUS))
            return "HDR10+";
        if (HasSideData(frame, AVFrameSideDataType.AV_FRAME_DATA_DYNAMIC_HDR_VIVID))
            return "HDR Vivid";
        if (HasSideData(frame, AVFrameSideDataType.AV_FRAME_DATA_AMBIENT_VIEWING_ENVIRONMENT))
            return "HDR ambient viewing environment";

        return null;
    }

    public static bool HasStaticHdrMetadata(AVFrame* frame)
    {
        return frame != null &&
            (HasSideData(frame, AVFrameSideDataType.AV_FRAME_DATA_MASTERING_DISPLAY_METADATA) ||
             HasSideData(frame, AVFrameSideDataType.AV_FRAME_DATA_CONTENT_LIGHT_LEVEL));
    }

    public static bool RequiresStaticHdrConfiguration(
        AVFrame* frame,
        bool staticHdrConfigured)
    {
        return !staticHdrConfigured && HasStaticHdrMetadata(frame);
    }

    private static string? GetUnsupportedPacketMetadataName(AVPacketSideDataType type)
    {
        return type switch
        {
            AVPacketSideDataType.AV_PKT_DATA_DOVI_CONF => "Dolby Vision",
            AVPacketSideDataType.AV_PKT_DATA_DYNAMIC_HDR10_PLUS => "HDR10+",
            AVPacketSideDataType.AV_PKT_DATA_AMBIENT_VIEWING_ENVIRONMENT =>
                "HDR ambient viewing environment",
            _ => null
        };
    }

    private static bool HasSideData(AVFrame* frame, AVFrameSideDataType type)
    {
        return ffmpeg.av_frame_get_side_data(frame, type) != null;
    }
}
