using NAudio.Dsp;
using NAudio.Wave;
using System;

namespace LunaBgmLibrary.Services
{
    public sealed class SpectrumAnalyzer : IDisposable
    {
        private readonly object _lock = new object();
        private readonly int _fftLength;
        private readonly Complex[] _fftBuffer;
        private readonly float[] _spectrumData;
        private readonly float[] _smoothedData;
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
        }

        public void ProcessSamples(float[] samples, WaveFormat format)
        {
            if (!_enabled || samples == null || samples.Length == 0) return;

            lock (_lock)
            {
                try
                {
                    PrepareFFTBuffer(samples, format);
                    PerformFFT();
                    ConvertToSpectrum(format.SampleRate);
                    ApplySmoothing();
                    
                    SpectrumUpdated?.Invoke(this, new SpectrumEventArgs(_smoothedData));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Spectrum analysis error: {ex.Message}");
                }
            }
        }

        private void PrepareFFTBuffer(float[] samples, WaveFormat format)
        {
            Array.Clear(_fftBuffer, 0, _fftBuffer.Length);
            int samplesToProcess = Math.Min(samples.Length, _fftLength);

            if (format.Channels == 2)
            {
                for (int i = 0; i < samplesToProcess / 2 && i < _fftLength; i++)
                {
                    float mono = (samples[i * 2] + samples[i * 2 + 1]) * 0.5f;
                    _fftBuffer[i].X = mono * GetWindow(i, samplesToProcess / 2);
                    _fftBuffer[i].Y = 0;
                }
            }
            else
            {
                for (int i = 0; i < samplesToProcess && i < _fftLength; i++)
                {
                    _fftBuffer[i].X = samples[i] * GetWindow(i, samplesToProcess);
                    _fftBuffer[i].Y = 0;
                }
            }
        }

        private void PerformFFT()
        {
            FastFourierTransform.FFT(true, (int)Math.Log(_fftLength, 2.0), _fftBuffer);
        }

        private float GetWindow(int i, int length)
        {
            return (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (length - 1)));
        }

        private void ConvertToSpectrum(int sampleRate)
        {
            // Build higher-contrast bands by aggregating over log-spaced ranges
            int bins = _fftLength / 2;
            float binWidth = (float)sampleRate / _fftLength;

            float minFreq = 50.0f;
            float maxFreq = Math.Min(sampleRate / 2f, 20000.0f);

            for (int band = 0; band < SpectrumBands; band++)
            {
                float f1 = GetFrequencyForEdge(band, SpectrumBands, minFreq, maxFreq);
                float f2 = GetFrequencyForEdge(band + 1, SpectrumBands, minFreq, maxFreq);

                int start = Math.Max(1, (int)Math.Floor(f1 / binWidth));
                int end = Math.Min(bins - 1, Math.Max(start + 1, (int)Math.Ceiling(f2 / binWidth)));

                double peak = 0.0;
                double sumSq = 0.0;
                int count = 0;

                for (int i = start; i < end; i++)
                {
                    double real = _fftBuffer[i].X;
                    double imag = _fftBuffer[i].Y;
                    double mag = Math.Sqrt(real * real + imag * imag);
                    peak = Math.Max(peak, mag);
                    sumSq += mag * mag;
                    count++;
                }

                double rms = count > 0 ? Math.Sqrt(sumSq / count) : 0.0;
                double value = 0.75 * peak + 0.25 * rms; // stronger peak emphasis

                // Perceptual/log mapping 0..1 (raise constant to push small values lower)
                double vLog = Math.Log10(1.0 + 25.0 * value) / Math.Log10(26.0);

                // Expand dynamic range: small smaller, large larger
                float v = (float)Math.Pow(Math.Clamp(vLog, 0.0, 1.0), 1.70f);

                // Additional soft floor to reduce small lobes further without affecting peaks
                const float floor = 0.18f;  // below this level, attenuate strongly
                if (v < floor)
                {
                    v *= 0.28f; // push very small energies lower but not to zero
                }
                else
                {
                    float t = (v - floor) / (1f - floor); // remap to 0..1
                    v = (float)Math.Pow(Math.Clamp(t, 0f, 1f), 1.25f); // gentle shaping above floor
                }

                _spectrumData[band] = Math.Clamp(v, 0f, 1f);
            }
        }

        private static float GetFrequencyForEdge(int edgeIndex, int bands, float minFreq, float maxFreq)
        {
            // Log-spaced edges including both ends
            float logMin = (float)Math.Log10(minFreq);
            float logMax = (float)Math.Log10(maxFreq);
            float t = Math.Clamp(edgeIndex / (float)bands, 0f, 1f);
            return (float)Math.Pow(10, logMin + (logMax - logMin) * t);
        }

        private void ApplySmoothing()
        {
            const float smoothFactor = 0.8f;
            
            for (int i = 0; i < SpectrumBands; i++)
            {
                _smoothedData[i] = _smoothedData[i] * smoothFactor + _spectrumData[i] * (1.0f - smoothFactor);
            }
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
