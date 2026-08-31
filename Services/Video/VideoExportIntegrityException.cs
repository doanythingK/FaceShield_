using System;

namespace FaceShield.Services.Video;

internal sealed class VideoExportIntegrityException : InvalidOperationException
{
    internal VideoExportIntegrityException(string message)
        : base(message)
    {
    }
}
