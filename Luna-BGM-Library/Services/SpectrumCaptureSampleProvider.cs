using NAudio.Wave;
using System;

namespace LunaBgmLibrary.Services
{
    public class SpectrumCaptureSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly SpectrumAnalyzer _analyzer;
        private readonly float[] _sampleBuffer;

        public WaveFormat WaveFormat => _source.WaveFormat;

        public SpectrumCaptureSampleProvider(ISampleProvider source, SpectrumAnalyzer analyzer)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
            _sampleBuffer = new float[4096];
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int samplesRead = _source.Read(buffer, offset, count);

            if (samplesRead > 0 && _analyzer.Enabled)
            {
                try
                {
                    int samplesToAnalyze = Math.Min(samplesRead, _sampleBuffer.Length);
                    Array.Copy(buffer, offset, _sampleBuffer, 0, samplesToAnalyze);
                    
                    _analyzer.ProcessSamples(_sampleBuffer, WaveFormat);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Spectrum capture error: {ex.Message}");
                }
            }

            return samplesRead;
        }
    }
}