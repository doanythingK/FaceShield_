using System;
using System.Collections.Generic;
using Avalonia.Media.Imaging;
using FaceShield.Models.Analysis;
using FaceShield.Services.Analysis;

namespace FaceShield.Services.FaceDetection
{
    public interface IBgraFaceDetector : IFaceDetector
    {
        IReadOnlyList<FaceDetectionResult> DetectFacesDownscaled(
            WriteableBitmap frame,
            double ratio,
            DownscaleQuality quality);

        IReadOnlyList<FaceDetectionResult> DetectFacesBgra(
            IntPtr data,
            int stride,
            int width,
            int height,
            double ratio,
            DownscaleQuality quality);
    }
}
