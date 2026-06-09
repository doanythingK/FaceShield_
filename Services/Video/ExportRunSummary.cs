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
    string? RunId,
    string ExportMode,
    bool HybridCopyAttempted,
    bool HybridCopyUsed,
    string? HybridCopyFallbackReason,
    int HybridWindowStartFrame,
    int HybridWindowEndFrame,
    int HybridModeTransitionCount,
    int HybridModeTimestampSyncCount,
    int InputVideoPackets,
    int OutputVideoPackets,
    int CopiedVideoPackets,
    int DroppedVideoPackets,
    int HybridCopyTimestampFixCount,
    string? PacketLossFallbackReason,
    bool ForceSoftwareEncoder,
    bool ForceSafeEncoding,
    bool ForceAudioTranscode,
    bool ForceH264Fallback)
{
    public string ToLogLine()
    {
            string run = string.IsNullOrWhiteSpace(RunId) ? "n/a" : RunId;
            return
            $"[ExportRunSummary] runId={run}, mode={ExportMode}, frames={Frames}, bitmapMaskFrames={BitmapMaskFrames}, directFaceFrames={DirectFaceFrames}, swsToBgraMs={SwsToBgraMs}, maskMs={MaskMs}, swsToEncMs={SwsToEncMs}, encodeMs={EncodeMs}, totalMs={TotalMs}, hybridCopyAttempted={HybridCopyAttempted.ToString().ToLowerInvariant()}, hybridCopyUsed={HybridCopyUsed.ToString().ToLowerInvariant()}, hybridCopyFallbackReason={HybridCopyFallbackReason ?? \"\"}, hybridWindowStart={HybridWindowStartFrame}, hybridWindowEnd={HybridWindowEndFrame}, hybridModeTransitions={HybridModeTransitionCount}, hybridModeTimestampSyncs={HybridModeTimestampSyncCount}, inputVideoPackets={InputVideoPackets}, outputVideoPackets={OutputVideoPackets}, copiedVideoPackets={CopiedVideoPackets}, droppedVideoPackets={DroppedVideoPackets}, hybridCopyTimestampFixCount={HybridCopyTimestampFixCount}, packetLossFallbackReason={PacketLossFallbackReason ?? \"\"}, forceSoftwareEncoder={ForceSoftwareEncoder.ToString().ToLowerInvariant()}, forceSafeEncoding={ForceSafeEncoding.ToString().ToLowerInvariant()}, forceAudioTranscode={ForceAudioTranscode.ToString().ToLowerInvariant()}, forceH264Fallback={ForceH264Fallback.ToString().ToLowerInvariant()}";
    }
}
