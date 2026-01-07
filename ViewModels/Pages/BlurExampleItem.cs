using Avalonia.Media.Imaging;

namespace FaceShield.ViewModels.Pages
{
    public sealed record BlurExampleItem(double Percent, string Label, WriteableBitmap Image);
    public sealed record BlurPreviewPayload(WriteableBitmap Image, string Label);
}
