using System;

namespace FaceShield.Services.Analysis;

internal static class SparseSceneSignature
{
    private const int Columns = 24;
    private const int Rows = 14;

    internal static unsafe double[] Compute(
        byte* basePtr,
        int stride,
        int width,
        int height)
    {
        if (basePtr == null || width <= 0 || height <= 0 || stride <= 0)
            return Array.Empty<double>();

        var signature = new double[Columns * Rows];
        int index = 0;
        for (int sy = 0; sy < Rows; sy++)
        {
            int y = Math.Clamp(
                (int)Math.Round((sy + 0.5) * height / Rows),
                0,
                height - 1);
            byte* row = basePtr + y * stride;

            for (int sx = 0; sx < Columns; sx++)
            {
                int x = Math.Clamp(
                    (int)Math.Round((sx + 0.5) * width / Columns),
                    0,
                    width - 1);
                byte* pixel = row + x * 4;
                signature[index++] =
                    ((77 * pixel[2]) + (150 * pixel[1]) + (29 * pixel[0])) /
                    (255.0 * 256.0);
            }
        }

        return signature;
    }
}
