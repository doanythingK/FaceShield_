namespace FaceShield.Services.Video;

public sealed record ExportRunSummary(
    int Frames,
    int BitmapMaskFrames,
    int DirectFaceFrames,
    long SwsToBgraMs,
    long MaskMs,
    long SwsToEncMs,
    long EncodeMs,
    long TotalMs,
    string? RunId)
{
    public string ToLogLine()
    {
        string run = string.IsNullOrWhiteSpace(RunId) ? "n/a" : RunId;
        return
            $"[ExportRunSummary] runId={run}, frames={Frames}, bitmapMaskFrames={BitmapMaskFrames}, directFaceFrames={DirectFaceFrames}, swsToBgraMs={SwsToBgraMs}, maskMs={MaskMs}, swsToEncMs={SwsToEncMs}, encodeMs={EncodeMs}, totalMs={TotalMs}";
    }
}
