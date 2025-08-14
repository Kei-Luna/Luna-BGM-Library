using LunaBgmLibrary.Services;
using System;
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
        private SpectrumAnalyzer? _analyzer;

        public static readonly DependencyProperty BarColorProperty =
            DependencyProperty.Register(nameof(BarColor), typeof(Brush), typeof(SpectrumDisplay),
                new PropertyMetadata(new SolidColorBrush(Color.FromRgb(0, 150, 255))));

        public static readonly DependencyProperty BackgroundColorProperty =
            DependencyProperty.Register(nameof(BackgroundColor), typeof(Brush), typeof(SpectrumDisplay),
                new PropertyMetadata(new SolidColorBrush(Color.FromRgb(17, 17, 17))));

        public static readonly DependencyProperty BarCountProperty =
            DependencyProperty.Register(nameof(BarCount), typeof(int), typeof(SpectrumDisplay),
                new PropertyMetadata(32, OnBarCountChanged));

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

        public SpectrumAnalyzer? SpectrumAnalyzer
        {
            get => _analyzer;
            set
            {
                if (_analyzer != null)
                    _analyzer.SpectrumUpdated -= OnSpectrumUpdated;
                
                _analyzer = value;
                
                if (_analyzer != null)
                    _analyzer.SpectrumUpdated += OnSpectrumUpdated;
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
                Interval = TimeSpan.FromMilliseconds(15)
            };
            _updateTimer.Tick += (_, __) => InvalidateVisual();
            _updateTimer.Start();

            IsHitTestVisible = false;
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

            double barWidth = ActualWidth / _smoothedData.Length;
            double spacing = Math.Max(0.25, barWidth * 0.025);
            double actualBarWidth = Math.Max(1, barWidth - spacing);

            var brush = BarColor;
            var pen = new Pen(brush, 0);

            for (int i = 0; i < _smoothedData.Length; i++)
            {
                double barHeight = _smoothedData[i] * ActualHeight;
                double x = i * barWidth + spacing * 0.5;
                double y = ActualHeight - barHeight;

                var rect = new Rect(x, y, actualBarWidth, barHeight);
                
                var gradientBrush = CreateGradientBrush(barHeight / ActualHeight);
                drawingContext.DrawRectangle(gradientBrush, pen, rect);
            }
        }

        private void SmoothSpectrum()
        {
            const float smoothingFactor = 0.85f;

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

        private Brush CreateGradientBrush(double intensity)
        {
            var baseColor = ((SolidColorBrush)BarColor).Color;
            
            var gradient = new LinearGradientBrush
            {
                StartPoint = new Point(0, 1),
                EndPoint = new Point(0, 0)
            };

            var bottomColor = Color.FromArgb(
                (byte)(baseColor.A * 0.3),
                baseColor.R,
                baseColor.G,
                baseColor.B);

            var topColor = Color.FromArgb(
                baseColor.A,
                (byte)Math.Min(255, baseColor.R + (int)(intensity * 100)),
                (byte)Math.Min(255, baseColor.G + (int)(intensity * 100)),
                (byte)Math.Min(255, baseColor.B + (int)(intensity * 100)));

            gradient.GradientStops.Add(new GradientStop(bottomColor, 0.0));
            gradient.GradientStops.Add(new GradientStop(baseColor, 0.5));
            gradient.GradientStops.Add(new GradientStop(topColor, 1.0));

            return gradient;
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            InvalidateVisual();
        }

        ~SpectrumDisplay()
        {
            _updateTimer?.Stop();
            
            if (_analyzer != null)
                _analyzer.SpectrumUpdated -= OnSpectrumUpdated;
        }
    }
}