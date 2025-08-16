using LunaBgmLibrary.Models;
using LunaBgmLibrary.Services;
using LunaBgmLibrary.Controls;
using NAudio.Wave;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace LunaBgmLibrary
{
    public partial class MainWindow : Window
    {
        private const string AllFoldersLabel = "All folders";

        private readonly string _bgmDir;
        private readonly AudioPlayer _player = new AudioPlayer();
        private readonly SpectrumAnalyzer _spectrumAnalyzer = new SpectrumAnalyzer(128, 8192);
        private ObservableCollection<TrackInfo> _allTracks = new();
        private ObservableCollection<TrackInfo> _filtered = new();

        private ObservableCollection<string> _folderItems = new();

        private readonly FileSystemWatcher _watcher;
        private readonly System.Timers.Timer _positionTimer = new System.Timers.Timer(200);
        private readonly System.Timers.Timer _fswDebounce = new System.Timers.Timer(300) { AutoReset = false };

        private int _currentAllIndex = -1;
        private bool _isSeeking = false;
        private readonly Random _rng = new Random();
        private CancellationTokenSource? _playCts;
        private volatile bool _isSwitching = false;

        private readonly string _settingsPath;
        private UserSettings _settings = new UserSettings();

        public MainWindow()
        {
            InitializeComponent();

            _bgmDir = Path.Combine(AppContext.BaseDirectory, "BGM");
            Directory.CreateDirectory(_bgmDir);

            _settingsPath = Path.Combine(AppContext.BaseDirectory, "user-settings.json");
            _settings = UserSettings.Load(_settingsPath);

            FolderPicker.ItemsSource = _folderItems;
            RefreshFolderList(selectLabel: AllFoldersLabel);

            ReloadPlaylist();
            PlaylistView.ItemsSource = _filtered;

            _watcher = new FileSystemWatcher(_bgmDir)
            {
                IncludeSubdirectories = true,
                EnableRaisingEvents = true
            };
            _watcher.Created += OnFsChanged;
            _watcher.Deleted += OnFsChanged;
            _watcher.Renamed += OnFsChanged;
            _fswDebounce.Elapsed += (_, __) => Dispatcher.Invoke(ReloadAllSafe);

            _player.PlaybackStopped += (_, __) => Dispatcher.Invoke(OnPlaybackStopped);

            _player.SpectrumAnalyzer = _spectrumAnalyzer;
            SpectrumDisplay.SpectrumAnalyzer = _spectrumAnalyzer;

            _positionTimer.Elapsed += (_, __) => Dispatcher.Invoke(UpdatePositionUi);
            _positionTimer.Start();

            this.PreviewKeyDown += MainWindow_PreviewKeyDown;

            try
            {
                VolumeSlider.Value = Math.Clamp(_settings.Volume, 0.0, 1.0);
                _player.SetVolume((float)VolumeSlider.Value);

                _player.SetEqualizerBands(_settings.EqBands);
            }
            catch
            {
                _player.SetVolume(_settings.Volume);
                _player.SetEqualizerBands(_settings.EqBands);
            }
        }

        private void OnFsChanged(object? s, FileSystemEventArgs e)
        {
            _fswDebounce.Stop();
            _fswDebounce.Start();
        }

        private void ReloadAllSafe()
        {
            try
            {
                RefreshFolderList();
                ReloadPlaylist();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Reload failed: {ex.Message}");
            }
        }

        private string? GetSelectedRelativeFolderOrNull()
        {
            var sel = FolderPicker.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(sel) || sel == AllFoldersLabel)
                return null;
            return sel.Replace('/', Path.DirectorySeparatorChar);
        }

        private void RefreshFolderList(string? selectLabel = null)
        {
            var currentSel = selectLabel ?? (FolderPicker.SelectedItem as string) ?? AllFoldersLabel;

            var rels = PlaylistService.GetRelativeFolders(_bgmDir);
            _folderItems.Clear();
            _folderItems.Add(AllFoldersLabel);
            foreach (var r in rels)
                _folderItems.Add(r);

            if (_folderItems.Contains(currentSel))
                FolderPicker.SelectedItem = currentSel;
            else
                FolderPicker.SelectedItem = AllFoldersLabel;
        }

        private void ReloadPlaylist()
        {
            var rel = GetSelectedRelativeFolderOrNull();
            _allTracks = PlaylistService.LoadFromFolder(_bgmDir, rel);
            _filtered = new ObservableCollection<TrackInfo>(_allTracks);
            PlaylistView.ItemsSource = _filtered;

            if (_currentAllIndex >= _allTracks.Count)
                _currentAllIndex = _allTracks.Count - 1;
        }

        private async void FolderPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                var prev = Interlocked.Exchange(ref _playCts, null);
                try { prev?.Cancel(); } catch { }
                prev?.Dispose();
                await _player.StopAsync();
            }
            catch {}

            ReloadPlaylist();
        }

        private async void TrackItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Grid g) return;
            if (g.DataContext is not TrackInfo t) return;

            var idx = _allTracks.IndexOf(t);
            if (idx >= 0) await PlayAtAllIndexAsync(idx);
        }

        private async Task PlayAtAllIndexAsync(int allIndex)
        {
            if (allIndex < 0 || allIndex >= _allTracks.Count) return;
            var track = _allTracks[allIndex];
            _currentAllIndex = allIndex;

            var newCts = new CancellationTokenSource();
            var prev = Interlocked.Exchange(ref _playCts, newCts);
            try { prev?.Cancel(); } catch { }
            prev?.Dispose();

            NowTitle.Text = track.Title ?? track.FileName;
            NowArtist.Text = track.Artist ?? "";
            NowAlbum.Text = track.Album ?? "";
            NowArt.Source  = track.Artwork;
            ElapsedText.Text = "00:00";
            TotalText.Text   = "00:00";
            PlayPauseBtn.Content = "⏳";

            _isSwitching = true;
            try
            {
                await _player.LoadAndPlayAsync(track.FilePath, (float)VolumeSlider.Value, newCts.Token);
                TotalText.Text = _player.TotalTime.ToString(@"mm\:ss");
                PlayPauseBtn.Content = "⏸";
            }
            catch (OperationCanceledException)
            {
                PlayPauseBtn.Content = "▶";
            }
            catch (Exception ex)
            {
                PlayPauseBtn.Content = "▶";
                MessageBox.Show($"Failed to play: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isSwitching = false;
            }
        }

        private async void PlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (_player.PlaybackState == PlaybackState.Playing)
            {
                _player.Pause();
                PlayPauseBtn.Content = "▶";
            }
            else if (_player.PlaybackState == PlaybackState.Paused)
            {
                _player.Play();
                PlayPauseBtn.Content = "⏸";
            }
            else
            {
                int start = _currentAllIndex >= 0 ? _currentAllIndex : 0;
                if (_allTracks.Any()) await PlayAtAllIndexAsync(start);
            }
        }

        private async void Prev_Click(object sender, RoutedEventArgs e)
        {
            if (!_allTracks.Any()) return;
            if (_currentAllIndex < 0) _currentAllIndex = 0;
            _currentAllIndex = (_currentAllIndex - 1 + _allTracks.Count) % _allTracks.Count;
            await PlayAtAllIndexAsync(_currentAllIndex);
        }

        private void NextCoreIndex()
        {
            if (ShuffleToggle.IsChecked == true)
                _currentAllIndex = _rng.Next(0, _allTracks.Count);
            else
                _currentAllIndex = (_currentAllIndex + 1) % _allTracks.Count;
        }

        private async void Next_Click(object sender, RoutedEventArgs e)
        {
            if (!_allTracks.Any()) return;
            if (_currentAllIndex < 0) _currentAllIndex = 0;
            NextCoreIndex();
            await PlayAtAllIndexAsync(_currentAllIndex);
        }

        private async void OnPlaybackStopped()
        {
            if (_isSwitching) return;
            if (_currentAllIndex < 0 || !_allTracks.Any()) return;

            if (RepeatToggle.IsChecked == true)
            {
                await PlayAtAllIndexAsync(_currentAllIndex);
                return;
            }
            NextCoreIndex();
            await PlayAtAllIndexAsync(_currentAllIndex);
        }

        private void UpdatePositionUi()
        {
            try
            {
                if (_player.PlaybackState == PlaybackState.Stopped || _isSeeking) return;
                var cur = _player.CurrentTime;
                var tot = _player.TotalTime;
                if (tot.TotalSeconds > 0)
                    SeekBar.Value = cur.TotalSeconds / tot.TotalSeconds * 100.0;
                ElapsedText.Text = cur.ToString(@"mm\:ss");
                TotalText.Text   = tot.ToString(@"mm\:ss");
            }
            catch { }
        }

        private void SeekRelative(int seconds)
        {
            var newTime = _player.CurrentTime + TimeSpan.FromSeconds(seconds);
            if (newTime < TimeSpan.Zero) newTime = TimeSpan.Zero;
            if (newTime > _player.TotalTime) newTime = _player.TotalTime;
            _player.CurrentTime = newTime;
        }

        private void MainWindow_PreviewKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space) { PlayPause_Click(this, new RoutedEventArgs()); e.Handled = true; }
            else if (e.Key == Key.Left) { SeekRelative(-5); e.Handled = true; }
            else if (e.Key == Key.Right) { SeekRelative(5); e.Handled = true; }
            else if (e.Key == Key.Up) { VolumeSlider.Value = Math.Min(1.0, VolumeSlider.Value + 0.05); e.Handled = true; }
            else if (e.Key == Key.Down) { VolumeSlider.Value = Math.Max(0.0, VolumeSlider.Value - 0.05); e.Handled = true; }
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            _player.SetVolume((float)e.NewValue);

            _settings.Volume = (float)Math.Clamp(e.NewValue, 0.0, 1.0);
            UserSettings.Save(_settingsPath, _settings);
        }

        private void SeekBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isSeeking) return;
            if (_player.TotalTime.TotalSeconds > 0)
            {
                var pos = TimeSpan.FromSeconds(_player.TotalTime.TotalSeconds * (SeekBar.Value / 100.0));
                ElapsedText.Text = pos.ToString(@"mm\:ss");
            }
        }

        private void SeekBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _isSeeking = true;
        }

        private void SeekBar_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _isSeeking = false;
            if (_player.TotalTime.TotalSeconds > 0)
            {
                var pos = TimeSpan.FromSeconds(_player.TotalTime.TotalSeconds * (SeekBar.Value / 100.0));
                _player.CurrentTime = pos;
            }
        }

        private async void UnpackButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var pckDir = Path.Combine(AppContext.BaseDirectory, "PCK");
                if (!Directory.Exists(pckDir))
                {
                    MessageBox.Show("PCK folder not found. Please create a PCK folder and place your PCK files inside.", "PCK Folder Missing", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var pckFiles = Directory.GetFiles(pckDir, "*.pck", SearchOption.AllDirectories);
                if (pckFiles.Length == 0)
                {
                    MessageBox.Show("No PCK files found in the PCK folder.", "No PCK Files", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var result = MessageBox.Show($"Found {pckFiles.Length} PCK file(s). This will unpack them to FLAC format in the BGM folder and delete the PCK files afterward. Continue?", 
                    "Confirm Unpack", MessageBoxButton.YesNo, MessageBoxImage.Question);
                
                if (result != MessageBoxResult.Yes)
                    return;

                UnpackButton.IsEnabled = false;
                UnpackButton.Content = "⏳";

                _watcher.EnableRaisingEvents = false;

                await Task.Run(() => UnpackPckFiles(pckDir));

                _watcher.EnableRaisingEvents = true;
                ReloadAllSafe();

                MessageBox.Show("PCK files unpacked successfully!", "Unpack Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error during unpacking: {ex.Message}", "Unpack Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _watcher.EnableRaisingEvents = true;
                UnpackButton.IsEnabled = true;
                UnpackButton.Content = "📦";
            }
        }

        private void UnpackPckFiles(string pckDir)
        {
            var toolsDir = Path.Combine(AppContext.BaseDirectory, "Tools");
            var quickbmsPath = Path.Combine(toolsDir, "quickbms.exe");
            var extractorScript = Path.Combine(toolsDir, "wwise_pck_extractor.bms");
            var vgmstreamPath = Path.Combine(toolsDir, "vgmstream-cli.exe");

            if (!File.Exists(quickbmsPath) || !File.Exists(extractorScript) || !File.Exists(vgmstreamPath))
            {
                throw new FileNotFoundException("Required tools not found in Tools folder.");
            }

            var pckFiles = Directory.GetFiles(pckDir, "*.pck", SearchOption.AllDirectories);
            
            foreach (var pckFile in pckFiles)
            {
                var relativePath = Path.GetRelativePath(pckDir, Path.GetDirectoryName(pckFile));
                var outputDir = Path.Combine(_bgmDir, relativePath);
                Directory.CreateDirectory(outputDir);

                var tempDir = Path.Combine(Path.GetTempPath(), "luna_pck_extract", Path.GetRandomFileName());
                Directory.CreateDirectory(tempDir);

                try
                {
                    var extractProcess = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = quickbmsPath,
                            Arguments = $"-o \"{extractorScript}\" \"{pckFile}\" \"{tempDir}\"",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        }
                    };

                    extractProcess.Start();
                    extractProcess.WaitForExit();

                    var extractedFiles = Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories)
                        .Where(f => !Path.GetExtension(f).Equals(".txt", StringComparison.OrdinalIgnoreCase))
                        .ToArray();

                    foreach (var extractedFile in extractedFiles)
                    {
                        var fileName = Path.GetFileNameWithoutExtension(extractedFile);
                        var outputFile = Path.Combine(outputDir, fileName + ".flac");

                        var convertProcess = new Process
                        {
                            StartInfo = new ProcessStartInfo
                            {
                                FileName = vgmstreamPath,
                                Arguments = $"-o \"{outputFile}\" \"{extractedFile}\"",
                                UseShellExecute = false,
                                CreateNoWindow = true,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true
                            }
                        };

                        convertProcess.Start();
                        convertProcess.WaitForExit();
                    }
                }
                finally
                {
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, true);
                    }
                }
            }

            foreach (var item in Directory.GetFileSystemEntries(pckDir))
            {
                if (Directory.Exists(item))
                    Directory.Delete(item, true);
                else
                    File.Delete(item);
            }
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var bands = UserSettings.ToBands(_settings.EqGainDb);
            var dlg = new EqualizerWindow(bands, _settings.EqPresetName) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                _player.SetEqualizerBands(dlg.Bands);
                _settings.EqGainDb     = UserSettings.ToGainArray(dlg.Bands);
                _settings.EqPresetName = dlg.SelectedPresetName ?? "Custom";
                _settings.EqBands = dlg.Bands.Select(b => new EqBandSetting { Frequency = b.Frequency, GainDb = b.GainDb, Q = 1.0f }).ToList();
                UserSettings.Save(_settingsPath, _settings);
            }
        }

        protected override async void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            try
            {
                _settings.Volume = (float)Math.Clamp(VolumeSlider.Value, 0.0, 1.0);
                UserSettings.Save(_settingsPath, _settings);
            }
            catch {}

            try { _positionTimer?.Stop(); } catch { }
            try { _fswDebounce?.Stop(); } catch { }
            var prev = Interlocked.Exchange(ref _playCts, null);
            try { prev?.Cancel(); } catch { }
            prev?.Dispose();
            await _player.StopAsync();
            _watcher?.Dispose();
            _spectrumAnalyzer?.Dispose();
        }
    }
}
