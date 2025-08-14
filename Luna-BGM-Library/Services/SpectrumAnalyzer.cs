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
        private volatile bool _enabled;

        private float _emaTopDb   = -25f;
        private float _emaFloorDb = -85f;

        private const float TopAttack   = 0.35f;
        private const float TopRelease  = 0.06f;
        private const float FloorAttack = 0.08f;
        private const float FloorRelease= 0.02f;

        private const float TargetWindowDb = 60f;
        private const float MinWindowDb    = 45f;
        private const float MaxWindowDb    = 75f;

        private const float Eps = 1e-12f;
        private const float NoiseGateDb = -100f;

        public event EventHandler<SpectrumEventArgs>? SpectrumUpdated;

        public int SpectrumBands { get; }
        public bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        public SpectrumAnalyzer(int spectrumBands = 32, int fftSize = 4096)
        {
            SpectrumBands = Math.Max(4, Math.Min(128, spectrumBands));
            _fftLength = Math.Max(1024, GetNextPowerOfTwo(fftSize));
            _fftBuffer = new Complex[_fftLength];
            _spectrumData = new float[SpectrumBands];
            _enabled = true;
        }

        public void ProcessSamples(float[] samples, WaveFormat format)
        {
            if (!_enabled || samples == null || samples.Length == 0) return;

            lock (_lock)
            {
                try
                {
                    Array.Clear(_fftBuffer, 0, _fftBuffer.Length);

                    int samplesToProcess = Math.Min(samples.Length, _fftLength);

                    if (format.Channels == 2)
                    {
                        int monoCount = Math.Min(samplesToProcess / 2, _fftLength);
                        for (int i = 0; i < monoCount; i++)
                        {
                            float mono = (samples[i * 2] + samples[i * 2 + 1]) * 0.5f;
                            _fftBuffer[i].X = mono;
                            _fftBuffer[i].Y = 0;
                        }
                        samplesToProcess = monoCount;
                    }
                    else
                    {
                        for (int i = 0; i < samplesToProcess; i++)
                        {
                            _fftBuffer[i].X = samples[i];
                            _fftBuffer[i].Y = 0;
                        }
                    }

                    ApplyHannWindow(_fftBuffer, samplesToProcess);

                    FastFourierTransform.FFT(true, (int)Math.Log(_fftLength, 2.0), _fftBuffer);

                    ConvertToSpectrumBands(format.SampleRate);

                    SpectrumUpdated?.Invoke(this, new SpectrumEventArgs(_spectrumData));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Spectrum analysis error: {ex.Message}");
                }
            }
        }

        private void ApplyHannWindow(Complex[] buffer, int length)
        {
            for (int i = 0; i < length; i++)
            {
                double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (length - 1));
                buffer[i].X *= (float)w;
            }
        }

        private void ConvertToSpectrumBands(int sampleRate)
        {
            Array.Clear(_spectrumData, 0, _spectrumData.Length);

            int nyquist = sampleRate / 2;
            int usableFFTBins = _fftLength / 2;

            float topDb = float.NegativeInfinity;
            float floorDb = float.PositiveInfinity;

            Span<float> bandDb = stackalloc float[SpectrumBands];

            for (int band = 0; band < SpectrumBands; band++)
            {
                double startFreq = GetBandFrequency(band, SpectrumBands, nyquist);
                double endFreq   = GetBandFrequency(band + 1, SpectrumBands, nyquist);

                int startBin = Math.Max(1, (int)(startFreq * usableFFTBins / nyquist));
                int endBin   = Math.Min(usableFFTBins - 1, (int)(endFreq  * usableFFTBins / nyquist));

                float mag = 0f;
                int binCount = 0;

                for (int bin = startBin; bin <= endBin; bin++)
                {
                    float re = _fftBuffer[bin].X;
                    float im = _fftBuffer[bin].Y;
                    mag += (float)Math.Sqrt(re * re + im * im);
                    binCount++;
                }

                if (binCount > 0) mag /= binCount;

                float db = 20f * (float)Math.Log10(mag + Eps);

                if (db < NoiseGateDb) db = NoiseGateDb;

                bandDb[band] = db;
                if (db > topDb) topDb = db;
                if (db < floorDb) floorDb = db;
            }

            UpdateEma(ref _emaTopDb,   topDb,   TopAttack,   TopRelease);
            UpdateEma(ref _emaFloorDb, floorDb, FloorAttack, FloorRelease);

            float window = Clamp(TargetWindowDb, MinWindowDb, MaxWindowDb);

            float displayTop   = Math.Max(-10f, _emaTopDb);
            float displayFloor = Math.Min(_emaFloorDb, displayTop - window);

            float invRange = 1f / Math.Max(1e-6f, (displayTop - displayFloor));

            for (int i = 0; i < SpectrumBands; i++)
            {
                float v = (bandDb[i] - displayFloor) * invRange;
                v = Clamp(v, 0f, 1f);

                const float gamma = 0.85f;
                v = (float)Math.Pow(v, gamma);

                _spectrumData[i] = v;
            }
        }

        private static void UpdateEma(ref float ema, float x, float attack, float release)
        {
            float a = x > ema ? attack : release;
            ema = ema + a * (x - ema);
        }

        private static double GetBandFrequency(int band, int totalBands, int nyquist)
        {
            double minFreq = 10.0;
            double maxFreq = Math.Min(nyquist * 0.95, 20000.0);
            return minFreq * Math.Pow(maxFreq / minFreq, (double)band / totalBands);
        }

        private static int GetNextPowerOfTwo(int value)
        {
            int power = 1;
            while (power < value) power <<= 1;
            return power;
        }

        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);

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
