using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using FaceShield.ViewModels.Workspace;
using System;

namespace FaceShield.Views.Workspace;

public partial class FramePreviewView : UserControl
{
    public FramePreviewView()
    {
        InitializeComponent();
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        => Forward(e, isPressed: true);

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            Forward(e, isPressed: false);
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
        => Forward(e, isReleased: true);

    private void Forward(PointerEventArgs e, bool isPressed = false, bool isReleased = false)
    {
        if (DataContext is not FramePreviewViewModel vm)
            return;

        if (vm.FrameBitmap is null)
            return;

        // ✅ Image 컨트롤을 기준 좌표계로 사용해야 함
        var img = this.FindControl<Image>("FrameImage");
        if (img is null)
            return;

        // 레이아웃 완료 전이면 Bounds가 0일 수 있음
        if (img.Bounds.Width <= 0 || img.Bounds.Height <= 0)
            return;

        // ✅ 포인터 위치도 Image 기준으로 받는다 (기존 코드의 핵심 문제)
        var imagePoint = e.GetPosition(img);

        double imgW = vm.FrameBitmap.PixelSize.Width;
        double imgH = vm.FrameBitmap.PixelSize.Height;

        if (imgW <= 0 || imgH <= 0)
            return;

        double scale = Math.Min(
            img.Bounds.Width / imgW,
            img.Bounds.Height / imgH);

        double renderW = imgW * scale;
        double renderH = imgH * scale;

        double offsetX = (img.Bounds.Width - renderW) / 2;
        double offsetY = (img.Bounds.Height - renderH) / 2;

        double x = (imagePoint.X - offsetX) / scale;
        double y = (imagePoint.Y - offsetY) / scale;

        if (x < 0 || y < 0 || x >= imgW || y >= imgH)
            return;

        var p = new Point(x, y);

        if (isPressed)
            vm.OnPointerPressed(p);
        else if (isReleased)
            vm.OnPointerReleased(p);
        else
            vm.OnPointerMoved(p);
    }
}
