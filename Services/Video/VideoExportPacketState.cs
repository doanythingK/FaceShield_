using FFmpeg.AutoGen;

namespace FaceShield.Services.Video;

internal sealed class VideoPacketTimestampIntegrity
{
    internal int MissingPacketTimestamps { get; set; }
    internal int PacketTimestampAdjustments { get; set; }
}

internal unsafe sealed class StreamCopyState
{
    internal int InputStreamIndex { get; }
    internal AVStream* InputStream { get; }
    internal AVStream* OutputStream { get; }
    internal bool ShouldRepairMissingTimestamps { get; }
    internal long LastPacketPts = -1;
    internal bool HasLastPacketPts;
    internal long LastPacketDts = -1;
    internal bool HasLastPacketDts;

    internal StreamCopyState(
        int inputStreamIndex,
        AVStream* inputStream,
        AVStream* outputStream)
    {
        InputStreamIndex = inputStreamIndex;
        InputStream = inputStream;
        OutputStream = outputStream;
        ShouldRepairMissingTimestamps =
            inputStream != null &&
            inputStream->codecpar != null &&
            inputStream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO;
    }
}
