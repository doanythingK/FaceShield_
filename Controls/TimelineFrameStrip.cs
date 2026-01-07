using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using FaceShield.Services.Video;
using FaceShield.ViewModels.Workspace;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace FaceShield.Controls
{
    public class TimelineFrameStrip : Control
    {
        public static readonly StyledProperty<IReadOnlyList<FrameItemViewModel>?> ItemsProperty =
            AvaloniaProperty.Register<TimelineFrameStrip, IReadOnlyList<FrameItemViewModel>?>(nameof(Items));

        public IReadOnlyList<FrameItemViewModel>? Items
        {
            get => GetValue(ItemsProperty);
            set => SetValue(ItemsProperty, value);
        }

        public static readonly StyledProperty<int> TotalFramesProperty =
            AvaloniaProperty.Register<TimelineFrameStrip, int>(nameof(TotalFrames), 0);

        public int TotalFrames
        {
            get => GetValue(TotalFramesProperty);
            set => SetValue(TotalFramesProperty, value);
        }

        public static readonly StyledProperty<double> FpsProperty =
            AvaloniaProperty.Register<TimelineFrameStrip, double>(nameof(Fps), 30d);

        public double Fps
        {
            get => GetValue(FpsProperty);
            set => SetValue(FpsProperty, value);
        }

        public static readonly StyledProperty<int> SelectedFrameIndexProperty =
    AvaloniaProperty.Register<TimelineFrameStrip, int>(
        nameof(SelectedFrameIndex),
        defaultValue: -1,
        defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

        public int SelectedFrameIndex
        {
            get => GetValue(SelectedFrameIndexProperty);
            set => SetCurrentValue(SelectedFrameIndexProperty, value);
        }

        public static readonly StyledProperty<double> SecondsPerScreenProperty =
            AvaloniaProperty.Register<TimelineFrameStrip, double>(nameof(SecondsPerScreen), 300d);

        public double SecondsPerScreen
        {
            get => GetValue(SecondsPerScreenProperty);
            set => SetValue(SecondsPerScreenProperty, value);
        }

        public static readonly StyledProperty<double> ViewStartSecondsProperty =
            AvaloniaProperty.Register<TimelineFrameStrip, double>(nameof(ViewStartSeconds), 0d);

        public double ViewStartSeconds
        {
            get => GetValue(ViewStartSecondsProperty);
            set => SetValue(ViewStartSecondsProperty, value);
        }

        public static readonly StyledProperty<TimelineThumbnailProvider?> ThumbnailProviderProperty =
            AvaloniaProperty.Register<TimelineFrameStrip, TimelineThumbnailProvider?>(nameof(ThumbnailProvider));

        public TimelineThumbnailProvider? ThumbnailProvider
        {
            get => GetValue(ThumbnailProviderProperty);
            set => SetValue(ThumbnailProviderProperty, value);
        }

        public static readonly StyledProperty<IReadOnlyList<int>?> NoFaceIssueFramesProperty =
            AvaloniaProperty.Register<TimelineFrameStrip, IReadOnlyList<int>?>(nameof(NoFaceIssueFrames));

        public IReadOnlyList<int>? NoFaceIssueFrames
        {
            get => GetValue(NoFaceIssueFramesProperty);
            set => SetValue(NoFaceIssueFramesProperty, value);
        }

        public static readonly StyledProperty<IReadOnlyList<int>?> LowConfidenceIssueFramesProperty =
            AvaloniaProperty.Register<TimelineFrameStrip, IReadOnlyList<int>?>(nameof(LowConfidenceIssueFrames));

        public IReadOnlyList<int>? LowConfidenceIssueFrames
        {
            get => GetValue(LowConfidenceIssueFramesProperty);
            set => SetValue(LowConfidenceIssueFramesProperty, value);
        }

        public static readonly StyledProperty<bool> ShowNoFaceIssuesProperty =
            AvaloniaProperty.Register<TimelineFrameStrip, bool>(nameof(ShowNoFaceIssues), true);

        public bool ShowNoFaceIssues
        {
            get => GetValue(ShowNoFaceIssuesProperty);
            set => SetValue(ShowNoFaceIssuesProperty, value);
        }

        public static readonly StyledProperty<bool> ShowLowConfidenceIssuesProperty =
            AvaloniaProperty.Register<TimelineFrameStrip, bool>(nameof(ShowLowConfidenceIssues), true);

        public bool ShowLowConfidenceIssues
        {
            get => GetValue(ShowLowConfidenceIssuesProperty);
            set => SetValue(ShowLowConfidenceIssuesProperty, value);
        }

        private int _hoverIndex = -1;

        static TimelineFrameStrip()
        {
            AffectsRender<TimelineFrameStrip>(
                ItemsProperty,
                TotalFramesProperty,
                FpsProperty,
                SelectedFrameIndexProperty,
                SecondsPerScreenProperty,
                ViewStartSecondsProperty,
                ThumbnailProviderProperty,
                NoFaceIssueFramesProperty,
                LowConfidenceIssueFramesProperty,
                ShowNoFaceIssuesProperty,
                ShowLowConfidenceIssuesProperty);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);

            var pt = e.GetCurrentPoint(this);
            if (!pt.Properties.IsLeftButtonPressed)
                return;

            int total = ResolveTotalFrames();
            if (total <= 0)
                return;

            var pos = e.GetPosition(this);
            double stripH = Math.Max(24, Bounds.Height - 22);
            if (pos.Y < 0 || pos.Y > stripH)
                return;

            int idx = XToFrameIndex(pos.X, total);

            // ✅ TwoWay 전파 확실히
            SetCurrentValue(SelectedFrameIndexProperty, idx);

            e.Handled = true;
            InvalidateVisual();
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);

            int total = ResolveTotalFrames();
            if (total <= 0) return;

            int idx = XToFrameIndex(e.GetPosition(this).X, total);
            if (idx != _hoverIndex)
            {
                _hoverIndex = idx;
                InvalidateVisual();
            }
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);

            int total = ResolveTotalFrames();
            if (total <= 0) return;

            double totalSec = TotalDurationSec(total);
            if (totalSec <= 0) return;

            bool ctrlOrCmd =
                e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
                e.KeyModifiers.HasFlag(KeyModifiers.Meta);

            double delta = e.Delta.Y;
            if (Math.Abs(delta) < 0.01) return;

            if (ctrlOrCmd)
            {
                // Zoom (마우스 위치 기준 앵커 줌)
                double oldSpan = Math.Max(0.05, SecondsPerScreen);
                double factor = delta > 0 ? 0.85 : 1.15;
                double newSpan = Math.Clamp(oldSpan * factor, 0.05, totalSec);

                double w = Math.Max(1, Bounds.Width);
                double anchorT = Math.Clamp(e.GetPosition(this).X / w, 0.0, 1.0);
                double anchorSec = ViewStartSeconds + oldSpan * anchorT;

                double newStart = anchorSec - newSpan * anchorT;
                newStart = ClampStart(newStart, newSpan, totalSec);

                SetCurrentValue(SecondsPerScreenProperty, newSpan);
                SetCurrentValue(ViewStartSecondsProperty, newStart);

                e.Handled = true;
                return;
            }

            // Pan (가로 스크롤)
            double pan = -delta * (SecondsPerScreen / 10.0);
            double s = ClampStart(ViewStartSeconds + pan, SecondsPerScreen, totalSec);
            SetCurrentValue(ViewStartSecondsProperty, s);

            e.Handled = true;
        }

        public override void Render(DrawingContext ctx)
        {
            base.Render(ctx);

            double w = Bounds.Width;
            double h = Bounds.Height;
            if (w <= 1 || h <= 1) return;

            int totalFrames = ResolveTotalFrames();
            double fps = Math.Max(1, Fps);

            double stripH = Math.Max(24, h - 22);

            // background
            ctx.FillRectangle(Brushes.Black, new Rect(0, 0, w, h));
            ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(20, 20, 20)),
                new Rect(0, 0, w, stripH));

            double startSec = ViewStartSeconds;
            double spanSec = Math.Max(0.05, SecondsPerScreen);
            double endSec = startSec + spanSec;

            DrawGridLines(ctx, w, stripH, startSec, endSec);
            DrawThumbnailsDense(ctx, w, stripH, startSec, endSec, fps, totalFrames);
            DrawIssueMarkers(ctx, w, stripH, startSec, endSec, fps, totalFrames);
            DrawAxis(ctx, w, stripH, startSec, endSec);

            // selected line
            if (SelectedFrameIndex >= 0 && SelectedFrameIndex < totalFrames)
            {
                double selSec = SelectedFrameIndex / fps;
                double x = (selSec - startSec) / Math.Max(0.0001, spanSec) * w;
                ctx.DrawLine(new Pen(Brushes.Lime, 2), new Point(x, 0), new Point(x, stripH));
            }

            // hover line
            if (_hoverIndex >= 0 && _hoverIndex < totalFrames)
            {
                double hovSec = _hoverIndex / fps;
                double x = (hovSec - startSec) / Math.Max(0.0001, spanSec) * w;

                var pen = new Pen(new SolidColorBrush(Color.FromRgb(255, 200, 0)), 2);
                ctx.DrawLine(pen, new Point(x, 0), new Point(x, stripH));
            }
        }

        // ✅ 썸네일을 "뜸"이 아니라 "촘촘하게"
        // - spacingPx를 줄이고
        // - 화면 폭 전체를 균등 샘플링
        private void DrawThumbnailsDense(
            DrawingContext ctx,
            double w,
            double stripH,
            double startSec,
            double endSec,
            double fps,
            int totalFrames)
        {
            var provider = ThumbnailProvider;
            if (provider == null) return;

            double range = Math.Max(0.0001, endSec - startSec);

            // 화면에 보여줄 썸네일 개수 먼저 결정
            int slots = (int)Math.Floor(w / 100); // 기준 폭(취향)
            if (slots < 1) slots = 1;
            if (slots > 300) slots = 300;

            // ⬇️ 핵심: 화면 폭을 슬롯 수로 나눠서 썸네일 폭 계산
            double thumbW = w / slots;

            for (int i = 0; i < slots; i++)
            {
                double x = i * thumbW;

                // 균등 샘플링
                double t = Math.Clamp((x + thumbW * 0.5) / Math.Max(1, w), 0.0, 1.0);
                double sec = startSec + range * t;

                int frame = (int)Math.Floor(sec * fps);
                frame = Math.Clamp(frame, 0, Math.Max(0, totalFrames - 1));

                WriteableBitmap? bmp;
                try { bmp = provider.GetThumbnail(frame); }
                catch { continue; }

                if (bmp == null) continue;

                var src = new Rect(0, 0, bmp.PixelSize.Width, bmp.PixelSize.Height);
                var dst = new Rect(x, 0, thumbW, stripH);

                ctx.DrawImage(bmp, src, dst);
            }
        }

        private static void DrawGridLines(DrawingContext ctx, double w, double stripH, double startSec, double endSec)
        {
            double range = Math.Max(0.0001, endSec - startSec);
            double step = NiceStep(range / 12);

            var pen = new Pen(new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)), 1);

            double first = Math.Floor(startSec / step) * step;
            for (double t = first; t <= endSec + step; t += step)
            {
                if (t < startSec) continue;
                double x = (t - startSec) / range * w;
                ctx.DrawLine(pen, new Point(x, 0), new Point(x, stripH));
            }
        }

        private void DrawIssueMarkers(
            DrawingContext ctx,
            double w,
            double stripH,
            double startSec,
            double endSec,
            double fps,
            int totalFrames)
        {
            double range = Math.Max(0.0001, endSec - startSec);
            int startFrame = (int)Math.Floor(startSec * fps);
            int endFrame = (int)Math.Ceiling(endSec * fps);
            startFrame = Math.Max(0, startFrame);
            endFrame = Math.Min(totalFrames - 1, endFrame);
            if (endFrame < startFrame)
                return;

            const double markerH = 6;
            double yNoFace = Math.Max(0, stripH - markerH);
            double yLowConf = Math.Max(0, stripH - markerH * 2);

            if (ShowNoFaceIssues && NoFaceIssueFrames is { Count: > 0 })
            {
                DrawIssueMarkerSeries(
                    ctx,
                    NoFaceIssueFrames,
                    startSec,
                    range,
                    fps,
                    w,
                    startFrame,
                    endFrame,
                    yNoFace,
                    markerH,
                    new SolidColorBrush(Color.FromRgb(220, 60, 60)));
            }

            if (ShowLowConfidenceIssues && LowConfidenceIssueFrames is { Count: > 0 })
            {
                DrawIssueMarkerSeries(
                    ctx,
                    LowConfidenceIssueFrames,
                    startSec,
                    range,
                    fps,
                    w,
                    startFrame,
                    endFrame,
                    yLowConf,
                    markerH,
                    new SolidColorBrush(Color.FromRgb(255, 160, 60)));
            }
        }

        private static void DrawIssueMarkerSeries(
            DrawingContext ctx,
            IReadOnlyList<int> frames,
            double startSec,
            double range,
            double fps,
            double width,
            int startFrame,
            int endFrame,
            double y,
            double h,
            IBrush brush)
        {
            int startIndex = FindFirstIndexAtOrAfter(frames, startFrame);
            for (int i = startIndex; i < frames.Count; i++)
            {
                int frame = frames[i];
                if (frame > endFrame)
                    break;

                double sec = frame / Math.Max(1, fps);
                double x = (sec - startSec) / range * width;
                if (x < -1 || x > width + 1)
                    continue;

                ctx.FillRectangle(brush, new Rect(x, y, 2, h));
            }
        }

        private static int FindFirstIndexAtOrAfter(IReadOnlyList<int> frames, int target)
        {
            int lo = 0;
            int hi = frames.Count - 1;
            int ans = frames.Count;

            while (lo <= hi)
            {
                int mid = lo + (hi - lo) / 2;
                int value = frames[mid];
                if (value >= target)
                {
                    ans = mid;
                    hi = mid - 1;
                }
                else
                {
                    lo = mid + 1;
                }
            }

            return ans;
        }

        private static double NiceStep(double raw)
        {
            double pow = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(raw, 1e-9))));
            double n = raw / pow;
            if (n <= 1) return 1 * pow;
            if (n <= 2) return 2 * pow;
            if (n <= 5) return 5 * pow;
            return 10 * pow;
        }

        private static void DrawAxis(DrawingContext ctx, double w, double stripH, double startSec, double endSec)
        {
            var axisBrush = Brushes.White;
            var typeface = new Typeface("Segoe UI");
            double y = stripH + 4;

            DrawTimeLabel(ctx, startSec, 0, y, TextAlignment.Left, axisBrush, typeface);
            DrawTimeLabel(ctx, (startSec + endSec) * 0.5, w * 0.5, y, TextAlignment.Center, axisBrush, typeface);
            DrawTimeLabel(ctx, endSec, w, y, TextAlignment.Right, axisBrush, typeface);
        }

        private static void DrawTimeLabel(DrawingContext ctx, double sec, double x, double y, TextAlignment align, IBrush brush, Typeface typeface)
        {
            var ts = TimeSpan.FromSeconds(Math.Max(0, sec));
            string label = ts.TotalHours >= 1 ? ts.ToString(@"hh\:mm\:ss") : ts.ToString(@"mm\:ss");

            var formatted = new FormattedText(
                label,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                11,
                brush);

            double tx = x;
            if (align == TextAlignment.Right) tx -= formatted.Width;
            else if (align == TextAlignment.Center) tx -= formatted.Width / 2;

            ctx.DrawText(formatted, new Point(tx, y));
        }

        private int ResolveTotalFrames()
        {
            var items = Items;
            if (items is { Count: > 0 })
            {
                int last = items[^1].Index;
                return Math.Max(0, last + 1);
            }
            return Math.Max(0, TotalFrames);
        }

        private static double TotalDurationSec(int totalFrames, double fps)
            => totalFrames <= 0 ? 0 : totalFrames / Math.Max(1, fps);

        private double TotalDurationSec(int totalFrames)
            => TotalDurationSec(totalFrames, Fps);

        private static double ClampStart(double start, double span, double totalSec)
        {
            double maxStart = Math.Max(0, totalSec - span);
            return Math.Clamp(start, 0, maxStart);
        }

        private int XToFrameIndex(double x, int totalFrames)
        {
            double w = Math.Max(1, Bounds.Width);
            double t = Math.Clamp(x / w, 0.0, 1.0);

            double sec = ViewStartSeconds + Math.Max(0.05, SecondsPerScreen) * t;
            int idx = (int)Math.Floor(sec * Math.Max(1, Fps));

            return Math.Clamp(idx, 0, Math.Max(0, totalFrames - 1));
        }
    }
}
