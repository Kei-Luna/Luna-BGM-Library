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
            int bins = _fftLength / 2;
            float binWidth = (float)sampleRate / _fftLength;
            
            for (int i = 0; i < SpectrumBands; i++)
            {
                float freq = GetFrequencyForBand(i);
                int binIndex = (int)(freq / binWidth);
                
                if (binIndex < bins)
                {
                    float magnitude = GetMagnitude(binIndex);
                    _spectrumData[i] = Math.Min(1.0f, magnitude * 2.0f);
                }
                else
                {
                    _spectrumData[i] = 0.0f;
                }
            }
        }

        private float GetFrequencyForBand(int band)
        {
            float minFreq = 250.0f;
            float maxFreq = 20000.0f;
            float logMin = (float)Math.Log10(minFreq);
            float logMax = (float)Math.Log10(maxFreq);
            float logRange = logMax - logMin;
            
            return (float)Math.Pow(10, logMin + (logRange * band / (SpectrumBands - 1)));
        }

        private float GetMagnitude(int binIndex)
        {
            if (binIndex >= _fftBuffer.Length) return 0.0f;
            
            float real = _fftBuffer[binIndex].X;
            float imag = _fftBuffer[binIndex].Y;
            return (float)Math.Sqrt(real * real + imag * imag);
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
