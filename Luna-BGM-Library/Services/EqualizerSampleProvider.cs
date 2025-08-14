using System;
using System.Collections.Generic;
using NAudio.Wave;
using NAudio.Dsp;

namespace LunaBgmLibrary.Services
{
    public sealed class EqualizerSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly object _lock = new();
        private BiQuadFilter[]? _filtersL;
        private BiQuadFilter[]? _filtersR;

        private List<EqBandSetting> _bands;
        private int _channels;
        private int _sampleRate;

        public WaveFormat WaveFormat => _source.WaveFormat;

        public EqualizerSampleProvider(ISampleProvider source, List<EqBandSetting> bands)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _bands = bands ?? throw new ArgumentNullException(nameof(bands));
            _channels = _source.WaveFormat.Channels;
            _sampleRate = _source.WaveFormat.SampleRate;
            RebuildFilters(); // initial build
        }

        public void UpdateBands(List<EqBandSetting> bands)
        {
            lock (_lock)
            {
                _bands = bands ?? throw new ArgumentNullException(nameof(bands));
                RebuildFilters();
            }
        }

        private void RebuildFilters()
        {
            var count = _bands.Count;
            _filtersL = new BiQuadFilter[count];
            _filtersR = _channels >= 2 ? new BiQuadFilter[count] : null;

            for (int i = 0; i < count; i++)
            {
                var b = _bands[i];
                var q = b.Q <= 0 ? 1.0f : b.Q;
                _filtersL[i] = BiQuadFilter.PeakingEQ(_sampleRate, b.Frequency, q, b.GainDb);
                if (_filtersR != null)
                    _filtersR[i] = BiQuadFilter.PeakingEQ(_sampleRate, b.Frequency, q, b.GainDb);
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            if (read <= 0) return read;

            lock (_lock)
            {
                if (_filtersL == null) return read;

                if (_channels == 1)
                {
                    for (int n = 0; n < read; n++)
                    {
                        float x = buffer[offset + n];
                        for (int i = 0; i < _filtersL.Length; i++)
                            x = _filtersL[i].Transform(x);
                        buffer[offset + n] = x;
                    }
                }
                else
                {
                    for (int n = 0; n < read; n += _channels)
                    {
                        // L
                        float xl = buffer[offset + n];
                        for (int i = 0; i < _filtersL.Length; i++)
                            xl = _filtersL[i].Transform(xl);
                        buffer[offset + n] = xl;

                        // R
                        if (_filtersR != null && n + 1 < read)
                        {
                            float xr = buffer[offset + n + 1];
                            for (int i = 0; i < _filtersR.Length; i++)
                                xr = _filtersR[i].Transform(xr);
                            buffer[offset + n + 1] = xr;
                        }
                    }
                }
            }
            return read;
        }
    }
}
