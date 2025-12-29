using Avalonia;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FaceShield.Services.FaceDetection
{
    public sealed class FaceDetectionResult
    {
        public Rect Bounds { get; init; }
        public float Confidence { get; init; }
    }
}
