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
        public static readonly StyledProperty<int> TotalFramesProperty =
            AvaloniaProperty.Register<TimelineFrameStrip, int>(nameof(TotalFrames), 0);

        public int TotalFrames
        {
            get => GetValue(TotalFramesProperty);
            set => SetValue(TotalFramesProperty, value);
        }

        public static readonly StyledProperty<double> TotalDurationSecondsProperty =
            AvaloniaProperty.Register<TimelineFrameStrip, double>(
                nameof(TotalDurationSeconds),
                0d);

        public double TotalDurationSeconds
        {
            get => GetValue(TotalDurationSecondsProperty);
            set => SetValue(TotalDurationSecondsProperty, value);
        }

        public static readonly StyledProperty<int> RenderVersionProperty =
            AvaloniaProperty.Register<TimelineFrameStrip, int>(
                nameof(RenderVersion),
                0);

        public int RenderVersion
        {
            get => GetValue(RenderVersionProperty);
            set => SetValue(RenderVersionProperty, value);
        }

        public static readonly StyledProperty<bool> IsTotalFramesEstimatedProperty =
            AvaloniaProperty.Register<TimelineFrameStrip, bool>(
                nameof(IsTotalFramesEstimated),
                false);

        public bool IsTotalFramesEstimated
        {
            get => GetValue(IsTotalFramesEstimatedProperty);
            set => SetValue(IsTotalFramesEstimatedProperty, value);
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

        public static readonly StyledProperty<IReadOnlyList<int>?> FlickerIssueFramesProperty =
            AvaloniaProperty.Register<TimelineFrameStrip, IReadOnlyList<int>?>(nameof(FlickerIssueFrames));

        public IReadOnlyList<int>? FlickerIssueFrames
        {
            get => GetValue(FlickerIssueFramesProperty);
            set => SetValue(FlickerIssueFramesProperty, value);
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

        public static readonly StyledProperty<bool> ShowFlickerIssuesProperty =
            AvaloniaProperty.Register<TimelineFrameStrip, bool>(nameof(ShowFlickerIssues), true);

        public bool ShowFlickerIssues
        {
            get => GetValue(ShowFlickerIssuesProperty);
            set => SetValue(ShowFlickerIssuesProperty, value);
        }

        private double _hoverSeconds = double.NaN;
        private readonly TimelineFrameStripRequestCoordinator _requests;

        public TimelineFrameStrip()
        {
            _requests = new TimelineFrameStripRequestCoordinator(
                () => ThumbnailProvider,
                () => SelectedFrameIndex,
                frameIndex => SetCurrentValue(SelectedFrameIndexProperty, frameIndex),
                InvalidateVisual);
        }

        static TimelineFrameStrip()
        {
            AffectsRender<TimelineFrameStrip>(
                TotalFramesProperty,
                TotalDurationSecondsProperty,
                RenderVersionProperty,
                IsTotalFramesEstimatedProperty,
                FpsProperty,
                SelectedFrameIndexProperty,
                SecondsPerScreenProperty,
                ViewStartSecondsProperty,
                ThumbnailProviderProperty,
                NoFaceIssueFramesProperty,
                LowConfidenceIssueFramesProperty,
                FlickerIssueFramesProperty,
                ShowNoFaceIssuesProperty,
                ShowLowConfidenceIssuesProperty,
                ShowFlickerIssuesProperty);
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

            double seconds = XToSeconds(pos.X);
            var provider = ThumbnailProvider;
            if (provider == null)
            {
                e.Handled = true;
                return;
            }

            if (provider.TryGetFrameIndexAtTimestamp(
                    seconds,
                    out int cachedFrameIndex))
            {
                SetCurrentValue(
                    SelectedFrameIndexProperty,
                    NormalizeResolvedFrameIndex(cachedFrameIndex, total));
                InvalidateVisual();
            }
            else
            {
                _requests.RequestExactFrameSelection(
                    provider,
                    seconds,
                    total,
                    SelectedFrameIndex,
                    frameIndex => NormalizeResolvedFrameIndex(frameIndex, total));
            }

            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);

            int total = ResolveTotalFrames();
            if (total <= 0) return;

            double hoverSeconds = XToSeconds(e.GetPosition(this).X);
            if (!double.IsFinite(_hoverSeconds) ||
                Math.Abs(hoverSeconds - _hoverSeconds) > 0.000001)
            {
                _hoverSeconds = hoverSeconds;
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
            double stripH = Math.Max(24, h - 22);

            // background
            ctx.FillRectangle(Brushes.Black, new Rect(0, 0, w, h));
            ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(20, 20, 20)),
                new Rect(0, 0, w, stripH));

            double startSec = ViewStartSeconds;
            double spanSec = Math.Max(0.05, SecondsPerScreen);
            double endSec = startSec + spanSec;

            DrawGridLines(ctx, w, stripH, startSec, endSec);
            DrawThumbnailsDense(ctx, w, stripH, startSec, endSec, totalFrames);
            DrawIssueMarkers(ctx, w, stripH, startSec, endSec, totalFrames);
            DrawAxis(ctx, w, stripH, startSec, endSec);

            // Selected-frame PTS may be resolved by the view model or another
            // background request. If it is still missing, warm it here and repaint
            // when the exact mapping becomes available.
            var timelineProvider = ThumbnailProvider;
            if (SelectedFrameIndex >= 0 &&
                SelectedFrameIndex < totalFrames &&
                timelineProvider != null)
            {
                if (timelineProvider.TryGetFrameTimestampSeconds(
                        SelectedFrameIndex,
                        out double selSec))
                {
                    double x = (selSec - startSec) / Math.Max(0.0001, spanSec) * w;
                    ctx.DrawLine(new Pen(Brushes.Lime, 2), new Point(x, 0), new Point(x, stripH));
                }
                else
                {
                    _requests.RequestSelectedFrameTimestamp(
                        timelineProvider,
                        SelectedFrameIndex);
                }
            }

            // Hover is already a timeline time coordinate, so no frame-rate conversion is needed.
            if (double.IsFinite(_hoverSeconds))
            {
                double x = (_hoverSeconds - startSec) / Math.Max(0.0001, spanSec) * w;
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
            int totalFrames)
        {
            var provider = ThumbnailProvider;
            if (provider == null) return;

            _requests.EnsureThumbnailRequestScope(provider, startSec, endSec);
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

                WriteableBitmap? bmp;
                try
                {
                    if (!provider.TryGetCachedThumbnailAtTime(sec, out bmp))
                    {
                        _requests.RequestThumbnail(provider, sec);
                        continue;
                    }
                }
                catch
                {
                    continue;
                }

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
            int totalFrames)
        {
            bool hasVisibleIssues =
                (ShowNoFaceIssues && NoFaceIssueFrames is { Count: > 0 }) ||
                (ShowLowConfidenceIssues && LowConfidenceIssueFrames is { Count: > 0 }) ||
                (ShowFlickerIssues && FlickerIssueFrames is { Count: > 0 });
            if (!hasVisibleIssues)
                return;

            double range = Math.Max(0.0001, endSec - startSec);
            var provider = ThumbnailProvider;
            if (provider == null)
                return;

            const double markerH = 6;
            double yNoFace = Math.Max(0, stripH - markerH);
            double yLowConf = Math.Max(0, stripH - markerH * 2);
            double yFlicker = Math.Max(0, stripH - markerH * 3);

            bool hasStartFrame =
                provider.TryGetFrameIndexAtTimestamp(
                    startSec,
                    out int startFrame);
            bool hasEndFrame =
                provider.TryGetFrameIndexAtTimestamp(
                    endSec,
                    out int endFrame);
            if (!hasStartFrame || !hasEndFrame)
            {
                double centerSec = startSec + range * 0.5;
                int anchorFrame;
                if (!provider.TryGetFrameIndexAtTimestamp(
                        centerSec,
                        out anchorFrame))
                {
                    if (_requests.RequestIssueViewportMapping(provider, centerSec))
                        return;

                    anchorFrame = Math.Max(0, SelectedFrameIndex);
                }

                int nextMissingFrame = -1;

                if (ShowNoFaceIssues && NoFaceIssueFrames is { Count: > 0 })
                {
                    nextMissingFrame = PickCloserMissingFrame(
                        nextMissingFrame,
                        DrawCachedIssueMarkerSeries(
                            ctx,
                            provider,
                            NoFaceIssueFrames,
                            startSec,
                            endSec,
                            range,
                            w,
                            anchorFrame,
                            yNoFace,
                            markerH,
                            new SolidColorBrush(Color.FromRgb(220, 60, 60))),
                        anchorFrame);
                }

                if (ShowLowConfidenceIssues && LowConfidenceIssueFrames is { Count: > 0 })
                {
                    nextMissingFrame = PickCloserMissingFrame(
                        nextMissingFrame,
                        DrawCachedIssueMarkerSeries(
                            ctx,
                            provider,
                            LowConfidenceIssueFrames,
                            startSec,
                            endSec,
                            range,
                            w,
                            anchorFrame,
                            yLowConf,
                            markerH,
                            new SolidColorBrush(Color.FromRgb(255, 160, 60))),
                        anchorFrame);
                }

                if (ShowFlickerIssues && FlickerIssueFrames is { Count: > 0 })
                {
                    nextMissingFrame = PickCloserMissingFrame(
                        nextMissingFrame,
                        DrawCachedIssueMarkerSeries(
                            ctx,
                            provider,
                            FlickerIssueFrames,
                            startSec,
                            endSec,
                            range,
                            w,
                            anchorFrame,
                            yFlicker,
                            markerH,
                            new SolidColorBrush(Color.FromRgb(80, 180, 255))),
                        anchorFrame);
                }

                if (nextMissingFrame >= 0)
                    _requests.RequestIssueFrameMapping(provider, nextMissingFrame);
                return;
            }

            if (IsTotalFramesEstimated)
            {
                startFrame = Math.Max(0, startFrame);
                endFrame = Math.Max(startFrame, endFrame);
            }
            else
            {
                startFrame = Math.Clamp(
                    startFrame,
                    0,
                    Math.Max(0, totalFrames - 1));
                endFrame = Math.Clamp(
                    endFrame,
                    startFrame,
                    Math.Max(startFrame, totalFrames - 1));
            }

            if (ShowNoFaceIssues && NoFaceIssueFrames is { Count: > 0 })
            {
                DrawIssueMarkerSeries(
                    ctx,
                    NoFaceIssueFrames,
                    startSec,
                    range,
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
                    w,
                    startFrame,
                    endFrame,
                    yLowConf,
                    markerH,
                    new SolidColorBrush(Color.FromRgb(255, 160, 60)));
            }

            if (ShowFlickerIssues && FlickerIssueFrames is { Count: > 0 })
            {
                DrawIssueMarkerSeries(
                    ctx,
                    FlickerIssueFrames,
                    startSec,
                    range,
                    w,
                    startFrame,
                    endFrame,
                    yFlicker,
                    markerH,
                    new SolidColorBrush(Color.FromRgb(80, 180, 255)));
            }
        }

        private int NormalizeResolvedFrameIndex(
            int frameIndex,
            int totalFrames)
        {
            if (IsTotalFramesEstimated)
                return Math.Max(0, frameIndex);

            return Math.Clamp(
                frameIndex,
                0,
                Math.Max(0, totalFrames - 1));
        }

        private static int PickCloserMissingFrame(
            int current,
            int candidate,
            int anchorFrame)
        {
            if (candidate < 0)
                return current;
            if (current < 0)
                return candidate;

            long currentDistance = Math.Abs((long)current - anchorFrame);
            long candidateDistance = Math.Abs((long)candidate - anchorFrame);
            return candidateDistance < currentDistance
                ? candidate
                : current;
        }

        private int DrawCachedIssueMarkerSeries(
            DrawingContext ctx,
            TimelineThumbnailProvider provider,
            IReadOnlyList<int> frames,
            double startSec,
            double endSec,
            double range,
            double width,
            int anchorFrame,
            double y,
            double h,
            IBrush brush)
        {
            int nearestMissingFrame = -1;
            long nearestMissingDistance = long.MaxValue;

            // Viewport ordinal bounds are not available yet. Inspect only a bounded
            // neighborhood around the resolved center anchor; the normal draw path
            // takes over once start/end timestamp mappings have been resolved.
            const int neighborCount = 64;
            int pivot = FindFirstIndexAtOrAfter(frames, anchorFrame);
            int startIndex = Math.Max(0, pivot - neighborCount);
            int endIndex = Math.Min(frames.Count, pivot + neighborCount + 1);
            long maxWarmupDistance = GetIssueWarmupFrameDistance(range);

            for (int i = startIndex; i < endIndex; i++)
            {
                int frame = frames[i];
                if (!provider.TryGetFrameTimestampSeconds(frame, out double sec))
                {
                    long distance = Math.Abs((long)frame - anchorFrame);
                    if (distance <= maxWarmupDistance &&
                        distance < nearestMissingDistance)
                    {
                        nearestMissingDistance = distance;
                        nearestMissingFrame = frame;
                    }
                    continue;
                }

                if (sec < startSec || sec > endSec)
                    continue;

                double x = (sec - startSec) / range * width;
                if (x < -1 || x > width + 1)
                    continue;

                ctx.FillRectangle(brush, new Rect(x, y, 2, h));
            }

            return nearestMissingFrame;
        }

        private long GetIssueWarmupFrameDistance(double rangeSeconds)
        {
            double safeFps = double.IsFinite(Fps) && Fps > 0
                ? Fps
                : 30.0;
            double estimatedFrames = Math.Max(
                30.0,
                Math.Max(0.0, rangeSeconds) * safeFps * 2.0);
            if (!double.IsFinite(estimatedFrames) ||
                estimatedFrames >= int.MaxValue)
            {
                return int.MaxValue;
            }

            return (long)Math.Ceiling(estimatedFrames);
        }

        private void DrawIssueMarkerSeries(
            DrawingContext ctx,
            IReadOnlyList<int> frames,
            double startSec,
            double range,
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

                if (ThumbnailProvider?.TryGetFrameTimestampSeconds(
                        frame,
                        out double sec) != true)
                {
                    continue;
                }

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
            => Math.Max(0, TotalFrames);

        private double TotalDurationSec(int totalFrames)
        {
            if (TotalDurationSeconds > 0 &&
                double.IsFinite(TotalDurationSeconds))
            {
                return TotalDurationSeconds;
            }

            // Unknown duration stays unknown. Do not synthesize a VFR time axis
            // from totalFrames / average FPS.
            return 0;
        }

        private static double ClampStart(double start, double span, double totalSec)
        {
            double maxStart = Math.Max(0, totalSec - span);
            return Math.Clamp(start, 0, maxStart);
        }

        private double XToSeconds(double x)
        {
            double w = Math.Max(1, Bounds.Width);
            double t = Math.Clamp(x / w, 0.0, 1.0);
            return ViewStartSeconds +
                Math.Max(0.05, SecondsPerScreen) * t;
        }

    }
}
