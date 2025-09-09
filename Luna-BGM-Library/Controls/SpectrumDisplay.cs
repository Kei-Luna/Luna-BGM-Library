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

            // Clip spectrum drawings to bounds so they don't overflow
            drawingContext.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));

            SmoothSpectrum();

            // Draw multiple mountains using two cubic Bézier curves per mountain
            DrawBezierMountains(drawingContext);

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

            // Remove clipping region
            drawingContext.Pop();
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
            const float smoothingFactor = 0.30f;

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

        private void DrawBezierMountains(DrawingContext dc)
        {
            int n = _smoothedData.Length;
            double w = ActualWidth;
            double h = ActualHeight;
            double midY = h * 0.5;
            if (n <= 0 || w <= 0 || h <= 0) return;
            double step = w / n;
            double usable = midY * 0.98;
            // Base half width used for width interpolation per mountain height
            double halfWidthBase = step * LobeWidthFactor * 0.9;
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
                // Width interpolation by height: higher -> smaller, lower -> larger
                // half = halfWidthBase * lerp(widthMin, widthMax, (1 - a))
                const double widthMin = 0.45; // scale at highest mountains
                const double widthMax = 1.25; // scale at lowest mountains
                double half = halfWidthBase * (widthMin + (widthMax - widthMin) * (1.0 - a));
                half = Math.Max(1.0, half);
                // Clamp to nearest edge so outer mountains touch bounds exactly
                double maxHalfByBounds = Math.Max(1.0, Math.Min(cx, w - cx));
                if (half > maxHalfByBounds)
                    half = maxHalfByBounds;
                // Handle ratios α, β by height: higher -> smaller, lower -> larger
                // α controls base handle length, β controls approach near the peak
                const double alphaMin = 0.25, alphaMax = 0.65;
                const double betaMin = 0.10, betaMax = 0.40;
                double alphaHandle = alphaMin + (alphaMax - alphaMin) * (1.0 - a);
                double betaHandle  = betaMin  + (betaMax  - betaMin)  * (1.0 - a);
                var color = InterpolateColor(StartColor, EndColor, Math.Clamp(cx / w, 0, 1));
                byte aByte = (byte)Math.Clamp(70 + a * 150, 70, 210); // adaptive opacity
                var outer = Color.FromArgb(aByte, color.R, color.G, color.B);
                var brushOuter = new SolidColorBrush(outer);
                brushOuter.Freeze();
                var geoOuter = CreateBezierMountainGeometry(cx, a * usable, half, midY, alphaHandle, betaHandle);
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
                var geoInner = CreateBezierMountainGeometry(cx, a * usable * 0.9, halfInner, midY, alphaHandle, betaHandle);
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

        private static StreamGeometry CreateBezierMountainGeometry(double centerX, double height, double halfWidth, double midY, double alpha, double beta)
        {
            // Build a symmetric closed shape composed of two cubic Bézier curves (top)
            // and their vertical mirror (bottom), connecting at the peak.
            double c = centerX;
            double h = Math.Max(0.0, height);
            double w = Math.Max(1.0, halfWidth);
            // Clamp ratios to reasonable ranges
            alpha = Math.Clamp(alpha, 0.0, 1.0);
            beta  = Math.Clamp(beta,  0.0, 1.0);
            // Top: left curve P0->P3 then right curve P3->Q3
            var P0 = new Point(c - w, midY);
            var P1 = new Point(c - w + alpha * w, midY);
            var P2 = new Point(c - beta * w, midY - h);
            var P3 = new Point(c,         midY - h); // peak
            var Q1 = new Point(c + beta * w, midY - h);
            var Q2 = new Point(c + w - alpha * w, midY);
            var Q3 = new Point(c + w, midY);
            // Bottom mirror:
            var B3 = new Point(c,         midY + h);
            var B2r = new Point(c + beta * w, midY + h);
            var B1r = new Point(c + w - alpha * w, midY);
            var B0r = new Point(c + w, midY);
            var B1l = new Point(c - beta * w, midY + h);
            var B2l = new Point(c - w + alpha * w, midY);
            var B3l = new Point(c - w, midY);
            var geom = new StreamGeometry { FillRule = FillRule.Nonzero };
            using (var ctx = geom.Open())
            {
                ctx.BeginFigure(P0, isFilled: true, isClosed: true);
                // Top left
                ctx.BezierTo(P1, P2, P3, isStroked: true, isSmoothJoin: true);
                // Top right
                ctx.BezierTo(Q1, Q2, Q3, isStroked: true, isSmoothJoin: true);
                // Bottom right (mirror)
                ctx.BezierTo(B1r, B2r, B3, isStroked: true, isSmoothJoin: true);
                // Bottom left (mirror)
                ctx.BezierTo(B1l, B2l, B3l, isStroked: true, isSmoothJoin: true);
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
