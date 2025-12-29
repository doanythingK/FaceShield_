// FILE: Services/FaceDetection/IFaceDetector.cs
using System;
using System.Collections.Generic;
using Avalonia.Media.Imaging;
using FaceShield.Models.Analysis;

namespace FaceShield.Services.FaceDetection
{
    public interface IFaceDetector : IDisposable
    {
        IReadOnlyList<FaceDetectionResult> DetectFaces(WriteableBitmap frame);
    }
}
