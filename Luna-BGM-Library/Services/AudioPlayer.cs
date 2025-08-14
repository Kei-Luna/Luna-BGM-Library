using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LunaBgmLibrary.Services
{
    public sealed class AudioPlayer : IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private IWavePlayer? _output;
        private MediaFoundationReader? _mfReader;
        private AudioFileReader? _afReader;

        private ISampleProvider? _currentProvider;
        private EqualizerSampleProvider? _eqProvider;
        private VolumeSampleProvider? _volumeProvider;

        private float _volume = 0.5f;

        public event EventHandler? PlaybackStopped;
        public SpectrumAnalyzer? SpectrumAnalyzer { get; set; }

        public System.Collections.Generic.List<EqBandSetting>? EqBands { get; private set; }

        public PlaybackState PlaybackState => _output?.PlaybackState ?? PlaybackState.Stopped;

        public TimeSpan CurrentTime
        {
            get
            {
                if (_mfReader != null) return _mfReader.CurrentTime;
                if (_afReader != null) return _afReader.CurrentTime;
                return TimeSpan.Zero;
            }
            set
            {
                if (_mfReader != null) _mfReader.CurrentTime = value;
                if (_afReader != null) _afReader.CurrentTime = value;
            }
        }

        public TimeSpan TotalTime
        {
            get
            {
                if (_mfReader != null) return _mfReader.TotalTime;
                if (_afReader != null) return _afReader.TotalTime;
                return TimeSpan.Zero;
            }
        }

        public async Task StopAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                try { _output?.Stop(); } catch { }
                _output?.Dispose(); _output = null;

                _afReader?.Dispose(); _afReader = null;
                _mfReader?.Dispose(); _mfReader = null;

                _volumeProvider = null;
                _eqProvider = null;
                _currentProvider = null;
            }
            finally { _gate.Release(); }
        }

        public void SetEqualizerBands(System.Collections.Generic.List<EqBandSetting> bands)
        {
            if (bands == null || bands.Count == 0) return;
        
            var safe = new System.Collections.Generic.List<EqBandSetting>(10);
            int n = Math.Min(10, bands.Count);
            for (int i = 0; i < n; i++)
            {
                safe.Add(new EqBandSetting
                {
                    Frequency = bands[i].Frequency,
                    GainDb = Math.Clamp(bands[i].GainDb, -12f, 12f),
                    Q = (bands[i].Q <= 0 ? 1.0f : bands[i].Q)
                });
            }
            for (int i = n; i < 10; i++) safe.Add(new EqBandSetting { Frequency = 0, GainDb = 0, Q = 1 });
        
            EqBands = safe;
            _eqProvider?.UpdateBands(safe);
        }

        public async Task LoadAndPlayAsync(string filePath, float volume01, CancellationToken ct)
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            IWavePlayer? newOutput = null;
            MediaFoundationReader? newMf = null;
            AudioFileReader? newAf = null;
            ISampleProvider? newProvider = null;
            EqualizerSampleProvider? newEq = null;
            VolumeSampleProvider? newVol = null;

            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    if (ext == ".mp3" || ext == ".wav" || ext == ".aiff" || ext == ".aif")
                    {
                        newAf = new AudioFileReader(filePath);
                        newProvider = newAf.ToSampleProvider();
                    }
                    else
                    {
                        newMf = new MediaFoundationReader(filePath);
                        newProvider = newMf.ToSampleProvider();
                    }
                }
                catch
                {
                    try
                    {
                        newMf?.Dispose(); newMf = null;
                        newAf?.Dispose(); newAf = null;

                        newMf = new MediaFoundationReader(filePath);
                        newProvider = newMf.ToSampleProvider();
                    }
                    catch
                    {
                        newMf?.Dispose(); newMf = null;
                        newAf?.Dispose(); newAf = null;

                        newAf = new AudioFileReader(filePath);
                        newProvider = newAf.ToSampleProvider();
                    }
                }

                // EQ
                var bands = EqBands ?? LunaBgmLibrary.EqDefaults.CreateDefault10Band();
                newEq = new EqualizerSampleProvider(newProvider!, bands);

                // Volume
                newVol = new VolumeSampleProvider(newEq) { Volume = Math.Clamp(volume01, 0f, 1f) };

                // Spectrum
                ISampleProvider finalProvider = newVol;
                if (SpectrumAnalyzer != null)
                {
                    var spectrumCapture = new SpectrumCaptureSampleProvider(newVol, SpectrumAnalyzer);
                    finalProvider = spectrumCapture;
                }

                // Output
                try
                {
                    var waveOut = new WaveOutEvent { DesiredLatency = 200, NumberOfBuffers = 3 };
                    newOutput = waveOut;
                    newOutput.Init(finalProvider);
                }
                catch
                {
                    newOutput?.Dispose();
                    newOutput = new WasapiOut(AudioClientShareMode.Shared, false, 200);
                    newOutput.Init(finalProvider);
                }
            }, ct).ConfigureAwait(false);

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                try { _output?.Stop(); } catch { }
                _output?.Dispose(); _output = null;
                _afReader?.Dispose(); _afReader = null;
                _mfReader?.Dispose(); _mfReader = null;

                _output = newOutput; newOutput = null;
                _afReader = newAf; newAf = null;
                _mfReader = newMf; newMf = null;
                _eqProvider = newEq; newEq = null;
                _volumeProvider = newVol; newVol = null;
                _currentProvider = _volumeProvider;
                _volume = Math.Clamp(volume01, 0f, 1f);

                if (_output != null)
                {
                    _output.PlaybackStopped += (_, __) => PlaybackStopped?.Invoke(this, EventArgs.Empty);
                    _output.Play();
                }
            }
            finally
            {
                newOutput?.Dispose();
                newAf?.Dispose();
                newMf?.Dispose();
                _gate.Release();
            }
        }

        public void SetVolume(float volume01)
        {
            _volume = Math.Clamp(volume01, 0f, 1f);
            if (_volumeProvider != null) _volumeProvider.Volume = _volume;
        }

        public void Play()  => _output?.Play();
        public void Pause() => _output?.Pause();

        public void Dispose()
        {
            StopAsync().GetAwaiter().GetResult();
            _gate.Dispose();
        }
    }
}
