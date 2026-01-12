using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using FaceShield.ViewModels.Workspace;
using System;

namespace FaceShield.Views.Workspace;

public partial class FramePreviewView : UserControl
{
    private FramePreviewViewModel? _vm;

    public FramePreviewView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        LayoutUpdated += (_, __) => UpdateOverlay();
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        UpdateBrushCursor(e);
        Forward(e, isPressed: true);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        UpdateBrushCursor(e);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            Forward(e, isPressed: false);
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        UpdateBrushCursor(e);
        Forward(e, isReleased: true);
    }

    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        SetBrushCursorVisible(false);
    }

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

    private void UpdateBrushCursor(PointerEventArgs e)
    {
        if (DataContext is not FramePreviewViewModel vm)
        {
            SetBrushCursorVisible(false);
            return;
        }

        if (!vm.ShowBrushCursor || vm.FrameBitmap is null)
        {
            SetBrushCursorVisible(false);
            return;
        }

        var img = this.FindControl<Image>("FrameImage");
        if (img is null)
        {
            SetBrushCursorVisible(false);
            return;
        }

        if (img.Bounds.Width <= 0 || img.Bounds.Height <= 0)
        {
            SetBrushCursorVisible(false);
            return;
        }

        double imgW = vm.FrameBitmap.PixelSize.Width;
        double imgH = vm.FrameBitmap.PixelSize.Height;
        if (imgW <= 0 || imgH <= 0)
        {
            SetBrushCursorVisible(false);
            return;
        }

        var imagePoint = e.GetPosition(img);
        double scale = Math.Min(
            img.Bounds.Width / imgW,
            img.Bounds.Height / imgH);

        double renderW = imgW * scale;
        double renderH = imgH * scale;

        double offsetX = (img.Bounds.Width - renderW) / 2;
        double offsetY = (img.Bounds.Height - renderH) / 2;

        double x = (imagePoint.X - offsetX);
        double y = (imagePoint.Y - offsetY);

        if (x < 0 || y < 0 || x >= renderW || y >= renderH)
        {
            SetBrushCursorVisible(false);
            return;
        }

        var controlPoint = e.GetPosition(this);
        double diameter = Math.Max(1, vm.BrushDiameter * scale);

        if (this.FindControl<Ellipse>("BrushCursor") is { } cursor)
        {
            cursor.Width = diameter;
            cursor.Height = diameter;
            Canvas.SetLeft(cursor, controlPoint.X - diameter / 2);
            Canvas.SetTop(cursor, controlPoint.Y - diameter / 2);
            cursor.IsVisible = true;
        }
    }

    private void SetBrushCursorVisible(bool isVisible)
    {
        if (this.FindControl<Ellipse>("BrushCursor") is { } cursor)
            cursor.IsVisible = isVisible;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm != null)
            _vm.PropertyChanged -= OnViewModelPropertyChanged;

        _vm = DataContext as FramePreviewViewModel;
        if (_vm != null)
            _vm.PropertyChanged += OnViewModelPropertyChanged;

        UpdateOverlay();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FramePreviewViewModel.DetectionRects) ||
            e.PropertyName == nameof(FramePreviewViewModel.ShowDetectionOverlay) ||
            e.PropertyName == nameof(FramePreviewViewModel.FrameBitmap))
        {
            UpdateOverlay();
        }
    }

    private void UpdateOverlay()
    {
        if (this.FindControl<Canvas>("OverlayCanvas") is not { } canvas)
            return;

        if (DataContext is not FramePreviewViewModel vm ||
            vm.FrameBitmap is null ||
            !vm.ShowDetectionOverlay ||
            vm.DetectionRects.Count == 0)
        {
            canvas.Children.Clear();
            canvas.IsVisible = false;
            return;
        }

        var img = this.FindControl<Image>("FrameImage");
        if (img is null || img.Bounds.Width <= 0 || img.Bounds.Height <= 0)
        {
            canvas.Children.Clear();
            canvas.IsVisible = false;
            return;
        }

        double imgW = vm.FrameBitmap.PixelSize.Width;
        double imgH = vm.FrameBitmap.PixelSize.Height;
        if (imgW <= 0 || imgH <= 0)
        {
            canvas.Children.Clear();
            canvas.IsVisible = false;
            return;
        }

        double scale = Math.Min(
            img.Bounds.Width / imgW,
            img.Bounds.Height / imgH);

        double renderW = imgW * scale;
        double renderH = imgH * scale;

        double offsetX = (img.Bounds.Width - renderW) / 2;
        double offsetY = (img.Bounds.Height - renderH) / 2;

        canvas.Children.Clear();
        canvas.IsVisible = true;

        foreach (var rect in vm.DetectionRects)
        {
            var overlay = new Rectangle
            {
                Width = rect.Width * scale,
                Height = rect.Height * scale,
                Stroke = Brushes.Lime,
                StrokeThickness = 1.5,
                Fill = Brushes.Transparent
            };

            Canvas.SetLeft(overlay, offsetX + rect.X * scale);
            Canvas.SetTop(overlay, offsetY + rect.Y * scale);
            canvas.Children.Add(overlay);
        }
    }
}
