using NAudio.Dsp;
using NAudio.Wave;
using System;
using System.Runtime.CompilerServices;

namespace LunaBgmLibrary.Services
{
    public sealed class SpectrumAnalyzer : IDisposable
    {
        private readonly object _lock = new object();
        private readonly int _fftLength;
        private readonly Complex[] _fftBuffer;
        private readonly float[] _spectrumData;
        private readonly float[] _smoothedData;
        
        private const int WebDataSize = 256;
        private const int WebLowpassRadius = 14;
        private readonly double[] _webFir;
        private readonly double[] _webWindow;
        private float[] _webPrevSpectrum = new float[WebDataSize / 2];
        private float[] _history;
        private int _historyWrite;
        private long _historyTotal;
        private int _historyCapacity;
        private volatile bool _enabled;

        public event EventHandler<SpectrumEventArgs>? SpectrumUpdated;

        public int SpectrumBands { get; }
        public bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        public SpectrumAnalyzer(int spectrumBands = 64, int fftSize = 2048)
        {
            SpectrumBands = Math.Max(8, Math.Min(256, spectrumBands));
            _fftLength = GetNextPowerOfTwo(fftSize);
            _fftBuffer = new Complex[_fftLength];
            _spectrumData = new float[SpectrumBands];
            _smoothedData = new float[SpectrumBands];
            _enabled = true;

            _webFir = BuildGaussianFir(WebLowpassRadius, 50.0);
            _webWindow = BuildBlackman(WebDataSize);
            _historyCapacity = 0;
            _history = Array.Empty<float>();
        }

        public void ProcessSamples(float[] samples, WaveFormat format)
        {
            if (!_enabled || samples == null || samples.Length == 0) return;

            lock (_lock)
            {
                try
                {
                    AppendToHistory(samples, format);

                    if (ComputeWebSpectrum(format.SampleRate))
                    {
                        SpectrumUpdated?.Invoke(this, new SpectrumEventArgs(_smoothedData));
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Spectrum analysis error: {ex.Message}");
                }
            }
        }
        
        private void AppendToHistory(float[] samples, WaveFormat format)
        {
            int channels = Math.Max(1, format.Channels);
            EnsureHistoryCapacity(format.SampleRate);

            if (channels == 1)
            {
                for (int i = 0; i < samples.Length; i++)
                {
                    _history[_historyWrite] = samples[i];
                    _historyWrite = (_historyWrite + 1) % _historyCapacity;
                    _historyTotal++;
                }
            }
            else
            {
                for (int i = 0; i < samples.Length; i += channels)
                {
                    float sum = samples[i];
                    sum += (i + 1 < samples.Length ? samples[i + 1] : 0f);
                    float mono = sum * 0.5f;
                    _history[_historyWrite] = mono;
                    _historyWrite = (_historyWrite + 1) % _historyCapacity;
                    _historyTotal++;
                }
            }
        }

        private void EnsureHistoryCapacity(int sampleRate)
        {
            int skip = Math.Max(1, (int)Math.Round(40.0 * sampleRate / 44100.0));
            int span = (WebDataSize - 1) * skip + (WebLowpassRadius * 4) + 1;
            int minCapacity = Math.Max(span + 1024, sampleRate * 2);

            if (minCapacity > _historyCapacity)
            {
                _historyCapacity = NextPowerOfTwo(minCapacity);
                _history = new float[_historyCapacity];
                _historyWrite = 0;
                _historyTotal = 0;
            }
        }

        private bool ComputeWebSpectrum(int sampleRate)
        {
            if (_historyCapacity == 0 || _historyTotal < 1) return false;

            int skip = Math.Max(1, (int)Math.Round(40.0 * sampleRate / 44100.0));
            long presentAbs = _historyTotal - 1;
            long baseIndexAbs = presentAbs - (WebDataSize - 1L) * skip - (2L * WebLowpassRadius);

            long earliestNeeded = baseIndexAbs - (2L * WebLowpassRadius);
            long earliestHave = _historyTotal - Math.Min(_historyCapacity, (int)_historyTotal);
            if (earliestNeeded < earliestHave)
            {
                return false;
            }

            Span<float> timeData = stackalloc float[WebDataSize];
            for (int i = 0; i < WebDataSize; i++)
            {
                long idx = baseIndexAbs + (long)i * skip;
                double v = LowpassAt(idx);
                timeData[i] = (float)(v * _webWindow[i]);
            }

            Complex[] buf = new Complex[WebDataSize];
            for (int i = 0; i < WebDataSize; i++)
            {
                buf[i].X = timeData[i];
                buf[i].Y = 0f;
            }
            FastFourierTransform.FFT(true, (int)Math.Log(WebDataSize, 2.0), buf);

            int half = WebDataSize / 2;
            float[] halfMag = new float[half];
            double norm = 2.0 / WebDataSize;
            for (int i = 0; i < half; i++)
            {
                double re = buf[i].X;
                double im = buf[i].Y;
                double mag = Math.Sqrt(re * re + im * im) * norm;
                float cur = (float)mag;
                // Disable temporal smoothing: use current magnitude only
                halfMag[i] = cur;
                _webPrevSpectrum[i] = cur;
            }

            int bins = half;
            float effectiveSampleRate = (float)sampleRate / skip;
            float binWidth = effectiveSampleRate / WebDataSize;
            float minFreq = 50.0f;
            float maxFreq = Math.Min(effectiveSampleRate / 2f, 20000.0f);

            const double minDb = -64.0;
            const double maxDb = 10.0;
            for (int band = 0; band < SpectrumBands; band++)
            {
                float f1 = GetFrequencyForEdge(band, SpectrumBands, minFreq, maxFreq);
                float f2 = GetFrequencyForEdge(band + 1, SpectrumBands, minFreq, maxFreq);

                int start = Math.Max(1, (int)Math.Floor(f1 / binWidth));
                int end = Math.Min(bins, Math.Max(start + 1, (int)Math.Ceiling(f2 / binWidth)));

                double peak = 0.0;
                double sumSq = 0.0;
                int count = 0;
                for (int i = start; i < end; i++)
                {
                    double mag = halfMag[i];
                    if (mag > peak) peak = mag;
                    sumSq += mag * mag;
                    count++;
                }

                double rms = count > 0 ? Math.Sqrt(sumSq / Math.Max(1, count)) : 0.0;
                double value = 0.75 * peak + 0.25 * rms;

                double db = 20.0 * Math.Log10(Math.Max(value, 1e-12));
                double t = (db - minDb) / (maxDb - minDb);
                float v = (float)Math.Clamp(t, 0.0, 1.0);
                v = (float)Math.Pow(v, 1.05);

                _spectrumData[band] = v;
            }

            Array.Copy(_spectrumData, _smoothedData, SpectrumBands);
            return true;
        }

        private static float GetFrequencyForEdge(int edgeIndex, int bands, float minFreq, float maxFreq)
        {
            float logMin = (float)Math.Log10(minFreq);
            float logMax = (float)Math.Log10(maxFreq);
            float t = Math.Clamp(edgeIndex / (float)bands, 0f, 1f);
            return (float)Math.Pow(10, logMin + (logMax - logMin) * t);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private double LowpassAt(long absIndex)
        {
            double value = 0.0;
            int center = WebLowpassRadius;
            for (int i = 0; i <= WebLowpassRadius; i++)
            {
                double wPlus = _webFir[center + i];
                double wMinus = _webFir[center - i];
                float sPlus = ReadHistory(absIndex + (2L * i));
                float sMinus = ReadHistory(absIndex - (2L * i));
                value += wPlus * sPlus + wMinus * sMinus;
            }
            value -= _webFir[center] * ReadHistory(absIndex);
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float ReadHistory(long absIndex)
        {
            int available = (int)Math.Min(_historyCapacity, _historyTotal);
            long firstAbs = _historyTotal - available;
            if (absIndex < firstAbs) return 0f;
            if (absIndex >= _historyTotal) return 0f;
            long rel = absIndex - firstAbs;
            int earliestIndex = (_historyTotal >= _historyCapacity) ? _historyWrite : 0;
            int idx = (int)((earliestIndex + rel) % _historyCapacity);
            return _history[idx];
        }

        private static double[] BuildBlackman(int length)
        {
            var w = new double[length];
            if (length <= 1) { w[0] = 1.0; return w; }
            for (int i = 0; i < length; i++)
            {
                double x = (double)i / (length - 1);
                w[i] = 0.42 - 0.5 * Math.Cos(2 * Math.PI * x) + 0.08 * Math.Cos(4 * Math.PI * x);
            }
            return w;
        }

        private static double[] BuildGaussianFir(int radius, double sigma)
        {
            int len = radius * 2 + 1;
            var fir = new double[len];
            double denom = Math.Sqrt(2.0 * Math.PI * sigma);
            for (int i = -radius; i <= radius; i++)
            {
                fir[i + radius] = Math.Exp(-(i * (double)i) / (2.0 * sigma)) / denom;
            }
            return fir;
        }

        private static int NextPowerOfTwo(int v)
        {
            v--;
            v |= v >> 1;
            v |= v >> 2;
            v |= v >> 4;
            v |= v >> 8;
            v |= v >> 16;
            v++;
            return v;
        }

        private static int GetNextPowerOfTwo(int value)
        {
            int power = 1;
            while (power < value) power <<= 1;
            return power;
        }

        public void Dispose()
        {
            _enabled = false;
        }
    }

    public class SpectrumEventArgs : EventArgs
    {
        public float[] SpectrumData { get; }

        public SpectrumEventArgs(float[] spectrumData)
        {
            SpectrumData = new float[spectrumData.Length];
            Array.Copy(spectrumData, SpectrumData, spectrumData.Length);
        }
    }
}
