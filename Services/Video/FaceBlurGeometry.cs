using Avalonia;
using System;

namespace FaceShield.Services.Video;

internal static class FaceBlurGeometry
{
    internal const double SoftEdgeRatio = 0.35;

    internal static int GetRadius(Rect face, int frameWidth, int frameHeight, int baseRadius)
    {
        if (baseRadius <= 1)
            return Math.Max(1, baseRadius);

        double area = Math.Max(1.0, face.Width * face.Height);
        double frameArea = Math.Max(1.0, frameWidth * (double)frameHeight);
        double percent = area / frameArea * 100.0;

        double scale = percent switch
        {
            <= 0.25 => 0.4,
            <= 1.0 => 0.4 + (percent - 0.25) / 0.75 * 0.15,
            <= 1.5 => 0.55,
            <= 3.0 => 0.55 + (percent - 1.5) / 1.5 * 0.15,
            <= 5.0 => 0.7 + (percent - 3.0) / 2.0 * 0.3,
            _ => 1.0
        };

        int radius = (int)Math.Round(baseRadius * scale);
        return Math.Clamp(radius, 1, baseRadius);
    }

    internal static Rect GetPaddedRect(Rect face, int width, int height)
    {
        double padX = Math.Max(6.0, face.Width * 0.15);
        double padY = Math.Max(6.0, face.Height * 0.25);
        double x = Math.Max(0, face.X - padX);
        double y = Math.Max(0, face.Y - padY);
        double right = Math.Min(width, face.X + face.Width + padX);
        double bottom = Math.Min(height, face.Y + face.Height + padY);
        return new Rect(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }
}
