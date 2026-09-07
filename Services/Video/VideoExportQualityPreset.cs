namespace FaceShield.Services.Video;

public enum VideoExportQualityPreset
{
    SizePriority = 0,
    Balanced = 1,
    QualityPriority = 2
}

internal static class VideoExportQualityPresetPolicy
{
    internal static double GetSourceBitrateMultiplier(VideoExportQualityPreset preset)
        => preset switch
        {
            VideoExportQualityPreset.SizePriority => 1.00,
            VideoExportQualityPreset.Balanced => 1.20,
            VideoExportQualityPreset.QualityPriority => 1.40,
            _ => 1.20
        };

    internal static int ApplyBitrateMultiplier(int bitrate, VideoExportQualityPreset preset)
    {
        if (bitrate <= 0)
            return 0;

        double scaled = bitrate * GetSourceBitrateMultiplier(preset);
        if (!double.IsFinite(scaled) || scaled <= 0)
            return bitrate;

        return VideoExportFidelityPolicy.ClampBitrate(
            (long)System.Math.Round(
                scaled,
                System.MidpointRounding.AwayFromZero));
    }
}
