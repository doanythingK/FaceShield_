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
    int CopiedSourceVideoPackets,
    int EncodedSourceVideoPackets,
    int DroppedVideoPackets,
    int OutputPacketPtsGapOutlierCount,
    long MaxOutputPacketPtsGap,
    int HybridCopyTimestampFixCount,
    string? PacketLossFallbackReason,
    bool ForceSoftwareEncoder,
    bool ForceSafeEncoding,
    bool ForceAudioTranscode,
    bool ForceH264Fallback,
    long HybridEncodedPacketFrameStep,
    long HybridCopyPacketFrameStep,
    int HybridWindowExpectedEncodedFrames = 0,
    int HybridWindowEncodedFrames = 0,
    int HybridWindowFrameShortfall = 0,
    int SampleWindowSourceFrames = 0,
    int SampleWindowProducedFrames = 0,
    int SampleWindowFrameShortfall = 0,
    int OutputPacketCountMismatch = 0,
    int MissingVideoPacketTimestampCount = 0,
    int VideoPacketTimestampAdjustmentCount = 0,
    bool OutputCommitted = false,
    string EncoderName = "",
    string EncoderQualityMode = "",
    string SourcePixelFormat = "",
    string OutputPixelFormat = "",
    int SourceBitDepth = 0,
    int OutputBitDepth = 0,
    long SourceVideoBitrate = 0,
    long TargetVideoBitrate = 0,
    int NativeYuvBlurFrames = 0,
    long EncoderFlushMs = 0)
{
    public string ToLogLine()
    {
        string run = string.IsNullOrWhiteSpace(RunId) ? "n/a" : RunId;
        return
            $"[ExportRunSummary] runId={run}, mode={ExportMode}, frames={Frames}, bitmapMaskFrames={BitmapMaskFrames}, directFaceFrames={DirectFaceFrames}, nativeYuvBlurFrames={NativeYuvBlurFrames}, swsToBgraMs={SwsToBgraMs}, maskMs={MaskMs}, swsToEncMs={SwsToEncMs}, encodeMs={EncodeMs}, encoderFlushMs={EncoderFlushMs}, totalMs={TotalMs}, encoder={EncoderName}, qualityMode={EncoderQualityMode}, sourcePixFmt={SourcePixelFormat}, outputPixFmt={OutputPixelFormat}, sourceBitDepth={SourceBitDepth}, outputBitDepth={OutputBitDepth}, sourceVideoBitrate={SourceVideoBitrate}, targetVideoBitrate={TargetVideoBitrate}, hybridCopyAttempted={HybridCopyAttempted.ToString().ToLowerInvariant()}, hybridCopyUsed={HybridCopyUsed.ToString().ToLowerInvariant()}, hybridCopyFallbackReason={HybridCopyFallbackReason ?? ""}, hybridWindowStart={HybridWindowStartFrame}, hybridWindowEnd={HybridWindowEndFrame}, hybridModeTransitions={HybridModeTransitionCount}, hybridModeTimestampSyncs={HybridModeTimestampSyncCount}, hybridEncodedPacketFrameStep={HybridEncodedPacketFrameStep}, hybridCopyPacketFrameStep={HybridCopyPacketFrameStep}, inputVideoPackets={InputVideoPackets}, outputVideoPackets={OutputVideoPackets}, outputPacketCountMismatch={OutputPacketCountMismatch}, missingVideoPacketTimestampCount={MissingVideoPacketTimestampCount}, videoPacketTimestampAdjustmentCount={VideoPacketTimestampAdjustmentCount}, outputCommitted={OutputCommitted.ToString().ToLowerInvariant()}, copiedVideoPackets={CopiedVideoPackets}, copiedSourceVideoPackets={CopiedSourceVideoPackets}, encodedSourceVideoPackets={EncodedSourceVideoPackets}, droppedVideoPackets={DroppedVideoPackets}, outputPacketPtsGapOutlierCount={OutputPacketPtsGapOutlierCount}, maxOutputPacketPtsGap={MaxOutputPacketPtsGap}, hybridCopyTimestampFixCount={HybridCopyTimestampFixCount}, packetLossFallbackReason={PacketLossFallbackReason ?? ""}, forceSoftwareEncoder={ForceSoftwareEncoder.ToString().ToLowerInvariant()}, forceSafeEncoding={ForceSafeEncoding.ToString().ToLowerInvariant()}, forceAudioTranscode={ForceAudioTranscode.ToString().ToLowerInvariant()}, forceH264Fallback={ForceH264Fallback.ToString().ToLowerInvariant()}, hybridWindowExpectedEncodedFrames={HybridWindowExpectedEncodedFrames}, hybridWindowEncodedFrames={HybridWindowEncodedFrames}, hybridWindowFrameShortfall={HybridWindowFrameShortfall}, sampleWindowSourceFrames={SampleWindowSourceFrames}, sampleWindowProducedFrames={SampleWindowProducedFrames}, sampleWindowFrameShortfall={SampleWindowFrameShortfall}";
    }
}
