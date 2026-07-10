namespace FaceShield.Services.Analysis
{
    public enum FrameTimingSource
    {
        None = 0,
        PresentationTimestamp = 1,
        FpsFallback = 2
    }

    public readonly record struct FrameTimingSample(
        double TimestampSeconds,
        FrameTimingSource Source);
}
