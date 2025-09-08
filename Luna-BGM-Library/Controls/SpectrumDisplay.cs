using LunaBgmLibrary.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace LunaBgmLibrary.Controls
{
    public class SpectrumDisplay : Control
    {
        private float[] _spectrumData = new float[32];
        private float[] _smoothedData = new float[32];
        private readonly DispatcherTimer _updateTimer;
        private bool _isRenderingHooked;
        private readonly Stopwatch _renderStopwatch = new Stopwatch();
        private double _lastInvalidateMs;
        private SpectrumAnalyzer? _analyzer;

        public static readonly DependencyProperty MaxRefreshHzProperty =
            DependencyProperty.Register(nameof(MaxRefreshHz), typeof(double), typeof(SpectrumDisplay),
                new PropertyMetadata(120.0));

        public static readonly DependencyProperty BarColorProperty =
            DependencyProperty.Register(nameof(BarColor), typeof(Brush), typeof(SpectrumDisplay),
                new PropertyMetadata(new SolidColorBrush(Color.FromRgb(0, 150, 255))));

        public static readonly DependencyProperty BackgroundColorProperty =
            DependencyProperty.Register(nameof(BackgroundColor), typeof(Brush), typeof(SpectrumDisplay),
                new PropertyMetadata(new SolidColorBrush(Color.FromRgb(17, 17, 17))));

        public static readonly DependencyProperty BarCountProperty =
            DependencyProperty.Register(nameof(BarCount), typeof(int), typeof(SpectrumDisplay),
                new PropertyMetadata(32, OnBarCountChanged));

        // New gradient and shaping properties for neon waveform
        public static readonly DependencyProperty StartColorProperty =
            DependencyProperty.Register(nameof(StartColor), typeof(Color), typeof(SpectrumDisplay),
                new PropertyMetadata(Color.FromRgb(0x00, 0xE5, 0xFF))); // cyan

        public static readonly DependencyProperty EndColorProperty =
            DependencyProperty.Register(nameof(EndColor), typeof(Color), typeof(SpectrumDisplay),
                new PropertyMetadata(Color.FromRgb(0xFF, 0x4B, 0xCD))); // magenta

        public static readonly DependencyProperty ShowGlowProperty =
            DependencyProperty.Register(nameof(ShowGlow), typeof(bool), typeof(SpectrumDisplay),
                new PropertyMetadata(true));

        public static readonly DependencyProperty PeakExponentProperty =
            DependencyProperty.Register(nameof(PeakExponent), typeof(double), typeof(SpectrumDisplay),
                new PropertyMetadata(1.30));

        public static readonly DependencyProperty VerticalScaleProperty =
            DependencyProperty.Register(nameof(VerticalScale), typeof(double), typeof(SpectrumDisplay),
                new PropertyMetadata(1.60));

        public static readonly DependencyProperty LobeWidthFactorProperty =
            DependencyProperty.Register(nameof(LobeWidthFactor), typeof(double), typeof(SpectrumDisplay),
                new PropertyMetadata(2.2));

        public static readonly DependencyProperty LobeSharpnessProperty =
            DependencyProperty.Register(nameof(LobeSharpness), typeof(double), typeof(SpectrumDisplay),
                new PropertyMetadata(2.2));

        public Brush BarColor
        {
            get => (Brush)GetValue(BarColorProperty);
            set => SetValue(BarColorProperty, value);
        }

        public Brush BackgroundColor
        {
            get => (Brush)GetValue(BackgroundColorProperty);
            set => SetValue(BackgroundColorProperty, value);
        }

        public int BarCount
        {
            get => (int)GetValue(BarCountProperty);
            set => SetValue(BarCountProperty, Math.Max(4, Math.Min(128, value)));
        }

        public Color StartColor
        {
            get => (Color)GetValue(StartColorProperty);
            set => SetValue(StartColorProperty, value);
        }

        public Color EndColor
        {
            get => (Color)GetValue(EndColorProperty);
            set => SetValue(EndColorProperty, value);
        }

        public bool ShowGlow
        {
            get => (bool)GetValue(ShowGlowProperty);
            set => SetValue(ShowGlowProperty, value);
        }

        // Shapes the perceived height. < 1 expands low values; > 1 compresses
        public double PeakExponent
        {
            get => (double)GetValue(PeakExponentProperty);
            set => SetValue(PeakExponentProperty, Math.Max(0.2, Math.Min(3.0, value)));
        }

        public double VerticalScale
        {
            get => (double)GetValue(VerticalScaleProperty);
            set => SetValue(VerticalScaleProperty, Math.Max(0.1, Math.Min(3.0, value)));
        }

        public double LobeWidthFactor
        {
            get => (double)GetValue(LobeWidthFactorProperty);
            set => SetValue(LobeWidthFactorProperty, Math.Max(0.2, Math.Min(4.0, value)));
        }

        public double LobeSharpness
        {
            get => (double)GetValue(LobeSharpnessProperty);
            set => SetValue(LobeSharpnessProperty, Math.Max(0.1, Math.Min(6.0, value)));
        }

        public SpectrumAnalyzer? SpectrumAnalyzer
        {
            get => _analyzer;
            set
            {
                if (_analyzer != null)
                    _analyzer.SpectrumUpdated -= OnSpectrumUpdated;
                
                _analyzer = value;
                
                if (_analyzer != null)
                {
                    _analyzer.SpectrumUpdated += OnSpectrumUpdated;
                    // Ensure bar count matches analyzer output bands
                    if (BarCount != _analyzer.SpectrumBands)
                        BarCount = _analyzer.SpectrumBands;
                }
            }
        }

        static SpectrumDisplay()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(SpectrumDisplay),
                new FrameworkPropertyMetadata(typeof(SpectrumDisplay)));
        }

        public SpectrumDisplay()
        {
            _updateTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(1)
            };
            _updateTimer.Tick += (_, __) => InvalidateVisual();
            _updateTimer.Start();

            IsHitTestVisible = false;

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            _renderStopwatch.Start();
        }

        public double MaxRefreshHz
        {
            get => (double)GetValue(MaxRefreshHzProperty);
            set => SetValue(MaxRefreshHzProperty, Math.Max(1.0, Math.Min(240.0, value)));
        }

        private static void OnBarCountChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is SpectrumDisplay display)
            {
                int newCount = (int)e.NewValue;
                display._spectrumData = new float[newCount];
                display._smoothedData = new float[newCount];
            }
        }

        private void OnSpectrumUpdated(object? sender, SpectrumEventArgs e)
        {
            if (e.SpectrumData.Length != _spectrumData.Length)
                return;

            Array.Copy(e.SpectrumData, _spectrumData, _spectrumData.Length);
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);

            drawingContext.DrawRectangle(BackgroundColor, null, new Rect(0, 0, ActualWidth, ActualHeight));

            if (ActualWidth <= 0 || ActualHeight <= 0 || _spectrumData.Length == 0)
                return;

            SmoothSpectrum();

            // Draw 32 overlapping lobes (mirrored polygons) to emulate the aesthetic
            DrawOverlappingLobes(drawingContext);

            // Center line highlight
            double midY = ActualHeight * 0.5;
            var centerBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5)
            };
            centerBrush.GradientStops.Add(new GradientStop(Color.FromArgb(200, 255, 255, 255), 0.0));
            centerBrush.GradientStops.Add(new GradientStop(Color.FromArgb(130, 255, 255, 255), 0.5));
            centerBrush.GradientStops.Add(new GradientStop(Color.FromArgb(200, 255, 255, 255), 1.0));
            drawingContext.DrawRectangle(centerBrush, null, new Rect(0, midY - 0.75, ActualWidth, 1.5));
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Prefer CompositionTarget.Rendering for high refresh (syncs to monitor refresh).
            if (!_isRenderingHooked)
            {
                CompositionTarget.Rendering += OnCompositionRendering;
                _isRenderingHooked = true;
            }
            // Disable fallback timer while rendering hook is active.
            _updateTimer.Stop();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (_isRenderingHooked)
            {
                CompositionTarget.Rendering -= OnCompositionRendering;
                _isRenderingHooked = false;
            }
            // Re-enable fallback timer when not attached to visual tree.
            _updateTimer.Start();
        }

        private void OnCompositionRendering(object? sender, EventArgs e)
        {
            double targetMs = 1000.0 / Math.Max(1.0, MaxRefreshHz);
            double nowMs = _renderStopwatch.Elapsed.TotalMilliseconds;
            if (nowMs - _lastInvalidateMs >= targetMs - 0.25) // small slack
            {
                _lastInvalidateMs = nowMs;
                InvalidateVisual();
            }
        }

        private void SmoothSpectrum()
        {
            const float smoothingFactor = 0.70f;

            for (int i = 0; i < _spectrumData.Length; i++)
            {
                if (_spectrumData[i] > _smoothedData[i])
                {
                    _smoothedData[i] = _spectrumData[i];
                }
                else
                {
                    _smoothedData[i] = _smoothedData[i] * smoothingFactor + _spectrumData[i] * (1f - smoothingFactor);
                }
            }
        }

        private static List<Point> BuildTopPoints(float[] values, double width, double height, double exponent, double vScale)
        {
            var pts = new List<Point>(values.Length);
            if (width <= 0 || height <= 0 || values.Length == 0) return pts;

            double midY = height * 0.5;
            double usable = midY * 0.98; // use more vertical space
            double step = width / (values.Length - 1);

            for (int i = 0; i < values.Length; i++)
            {
                double x = i * step;
                double v = Math.Clamp(values[i], 0.0f, 1.0f);
                v = Math.Pow(v, exponent) * vScale;
                v = Math.Min(1.0, v);
                double y = midY - (v * usable);
                pts.Add(new Point(x, y));
            }

            return pts;
        }

        private void DrawOverlappingLobes(DrawingContext dc)
        {
            int n = _smoothedData.Length;
            double w = ActualWidth;
            double h = ActualHeight;
            double midY = h * 0.5;
            if (n <= 0 || w <= 0 || h <= 0) return;

            double step = w / n;
            double usable = midY * 0.98;
            double halfWidthBase = step * LobeWidthFactor * 0.5;

            // Build sortable list by amplitude so taller peaks are drawn last (on top)
            var items = new List<(int i, double x, double a)>();
            for (int i = 0; i < n; i++)
            {
                double v = Math.Clamp(_smoothedData[i], 0f, 1f);
                v = Math.Pow(v, PeakExponent) * VerticalScale;
                v = Math.Min(1.0, v);
                double cx = (i + 0.5) * step;
                items.Add((i, cx, v));
            }
            items.Sort((a, b) => a.a.CompareTo(b.a));

            foreach (var (i, cx, a) in items)
            {
                // Outer colored lobe
                double half = halfWidthBase * (1.0 + 0.6 * a); // widen by amplitude
                // Sharpen tip as amplitude grows (reduced)
                double sharp = Math.Max(1.0, LobeSharpness + 1.4 * a);
                // Slope shaping gentler: keep gamma closer to 1 (less compression near top)
                double gamma = Math.Clamp(1.0 - 0.35 * a, 0.70, 1.0);
                var color = InterpolateColor(StartColor, EndColor, Math.Clamp(cx / w, 0, 1));
                byte alpha = (byte)Math.Clamp(70 + a * 150, 70, 210); // adaptive opacity
                var outer = Color.FromArgb(alpha, color.R, color.G, color.B);
                var brushOuter = new SolidColorBrush(outer);
                brushOuter.Freeze();

                var geoOuter = CreateLobeGeometry(cx, a * usable, half, midY, w, h, sharp, gamma);
                dc.DrawGeometry(brushOuter, null, geoOuter);

                // Inner highlight (narrower, whiter)
                double halfInner = half * 0.70;
                byte whiteA = (byte)Math.Clamp(40 + a * 130, 40, 200);
                var white = Color.FromArgb(whiteA,
                    (byte)Math.Min(255, (color.R + 255) / 2),
                    (byte)Math.Min(255, (color.G + 255) / 2),
                    (byte)Math.Min(255, (color.B + 255) / 2));
                var brushInner = new SolidColorBrush(white);
                brushInner.Freeze();
                var geoInner = CreateLobeGeometry(cx, a * usable * 0.9, halfInner, midY, w, h, sharp + 0.8 + 0.6 * a, gamma);
                dc.DrawGeometry(brushInner, null, geoInner);
            }

            // Optional subtle glow stroke across combined path (center line already adds focus)
            if (ShowGlow)
            {
                var glow = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0.5),
                    EndPoint = new Point(1, 0.5)
                };
                glow.GradientStops.Add(new GradientStop(Color.FromArgb(70, StartColor.R, StartColor.G, StartColor.B), 0));
                glow.GradientStops.Add(new GradientStop(Color.FromArgb(70, (byte)((StartColor.R + EndColor.R) / 2), (byte)((StartColor.G + EndColor.G) / 2), (byte)((StartColor.B + EndColor.B) / 2)), 0.5));
                glow.GradientStops.Add(new GradientStop(Color.FromArgb(70, EndColor.R, EndColor.G, EndColor.B), 1));
                var pen = new Pen(glow, 2.0) { LineJoin = PenLineJoin.Round };
                dc.DrawLine(pen, new Point(0, midY), new Point(w, midY));
            }
        }

        private static StreamGeometry CreateLobeGeometry(double centerX, double ampPixels, double halfWidth, double midY, double totalWidth, double totalHeight, double sharpness, double gamma)
        {
            // Build a symmetric lobe using a cosine^sharpness profile, mirrored vertically
            int segments = 16;
            var geom = new StreamGeometry { FillRule = FillRule.Nonzero };
            using (var ctx = geom.Open())
            {
                double left = Math.Max(0, centerX - halfWidth);
                double right = Math.Min(totalWidth, centerX + halfWidth);
                double step = (right - left) / segments;
                // small round cap near the top: within this normalized half-width,
                // use a reduced exponent to soften the tip while keeping steep flanks
                const double capWidth = 0.20; // 0..1 range of |t| (wider for rounder tips)
                double topSharpFactor = 0.40;   // fraction of sharpness at the very top

                // top path
                ctx.BeginFigure(new Point(left, midY), isFilled: true, isClosed: true);
                for (int s = 0; s <= segments; s++)
                {
                    double x = left + s * step;
                    double t = (x - centerX) / halfWidth; // -1..1
                    t = Math.Clamp(t, -1.0, 1.0);
                    double tm = Math.Pow(Math.Abs(t), gamma); // slope shaping
                    // local sharpness blending (smootherstep)
                    double u = Math.Min(1.0, tm / capWidth);
                    double blend = u * u * (3 - 2 * u);
                    double topSharp = Math.Max(1.0, sharpness * topSharpFactor);
                    double localSharp = topSharp + (sharpness - topSharp) * blend;
                    double profile = Math.Pow(Math.Max(0.0, Math.Cos(Math.PI * tm / 2.0)), localSharp);
                    double y = midY - profile * ampPixels;
                    ctx.LineTo(new Point(x, y), isStroked: true, isSmoothJoin: false);
                }

                // bottom path mirror
                for (int s = segments; s >= 0; s--)
                {
                    double x = left + s * step;
                    double t = (x - centerX) / halfWidth;
                    t = Math.Clamp(t, -1.0, 1.0);
                    double tm = Math.Pow(Math.Abs(t), gamma);
                    double u = Math.Min(1.0, tm / capWidth);
                    double blend = u * u * (3 - 2 * u);
                    double topSharp = Math.Max(1.0, sharpness * topSharpFactor);
                    double localSharp = topSharp + (sharpness - topSharp) * blend;
                    double profile = Math.Pow(Math.Max(0.0, Math.Cos(Math.PI * tm / 2.0)), localSharp);
                    double y = midY + profile * ampPixels;
                    ctx.LineTo(new Point(x, y), isStroked: true, isSmoothJoin: false);
                }
            }
            geom.Freeze();
            return geom;
        }

        private static StreamGeometry BuildMirroredGeometry(IReadOnlyList<Point> topPoints, double height)
        {
            double midY = height * 0.5;
            var geom = new StreamGeometry { FillRule = FillRule.Nonzero };

            using (var ctx = geom.Open())
            {
                ctx.BeginFigure(topPoints[0], isFilled: true, isClosed: true);
                AddCatmullRom(ctx, topPoints);

                var bottom = new List<Point>(topPoints.Count);
                for (int i = topPoints.Count - 1; i >= 0; i--)
                {
                    var tp = topPoints[i];
                    bottom.Add(new Point(tp.X, 2 * midY - tp.Y));
                }
                AddCatmullRom(ctx, bottom);
            }

            geom.Freeze();
            return geom;
        }


        private static void AddCatmullRom(StreamGeometryContext ctx, IReadOnlyList<Point> points, double tension = 0.5)
        {
            if (points.Count < 2) return;

            for (int i = 0; i < points.Count - 1; i++)
            {
                Point p0 = i > 0 ? points[i - 1] : points[i];
                Point p1 = points[i];
                Point p2 = points[i + 1];
                Point p3 = i + 2 < points.Count ? points[i + 2] : points[i + 1];

                var c1 = new Point(
                    p1.X + (p2.X - p0.X) * (tension / 6.0),
                    p1.Y + (p2.Y - p0.Y) * (tension / 6.0));
                var c2 = new Point(
                    p2.X - (p3.X - p1.X) * (tension / 6.0),
                    p2.Y - (p3.Y - p1.Y) * (tension / 6.0));

                ctx.BezierTo(c1, c2, p2, isStroked: true, isSmoothJoin: true);
            }
        }

        private static Color InterpolateColor(Color a, Color b, double t)
        {
            t = Math.Clamp(t, 0.0, 1.0);
            return Color.FromArgb(
                (byte)(a.A + (b.A - a.A) * t),
                (byte)(a.R + (b.R - a.R) * t),
                (byte)(a.G + (b.G - a.G) * t),
                (byte)(a.B + (b.B - a.B) * t));
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            InvalidateVisual();
        }

        ~SpectrumDisplay()
        {
            _updateTimer?.Stop();
            if (_isRenderingHooked)
            {
                CompositionTarget.Rendering -= OnCompositionRendering;
                _isRenderingHooked = false;
            }
            
            if (_analyzer != null)
                _analyzer.SpectrumUpdated -= OnSpectrumUpdated;
        }
    }
}
