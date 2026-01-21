using System;

namespace FaceShield.Services.Video;

public readonly record struct ProxyProgress(int FrameIndex, int TotalFrames, DateTime Timestamp);
