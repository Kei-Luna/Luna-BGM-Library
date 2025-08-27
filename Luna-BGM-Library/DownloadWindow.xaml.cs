using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Text.Json;
using SharpCompress.Archives;
using SharpCompress.Common;
using System.Diagnostics;

namespace LunaBgmLibrary
{
    public partial class DownloadWindow : Window
    {
        public class GameOption : INotifyPropertyChanged
        {
            private bool _isSelected;
            public string Name { get; set; } = "";
            public List<string> DownloadUrls { get; set; } = new();
            
            public bool IsSelected 
            { 
                get => _isSelected; 
                set { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); } 
            }

            public event PropertyChangedEventHandler? PropertyChanged;
            protected virtual void OnPropertyChanged(string propertyName)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        private readonly ObservableCollection<GameOption> _games = new();
        private readonly HttpClient _httpClient = new();
        private CancellationTokenSource? _downloadCts;
        private readonly string _bgmDir;

        public DownloadWindow(string bgmDirectory)
        {
            InitializeComponent();
            _bgmDir = bgmDirectory;
            GamesList.ItemsSource = _games;
            LoadGameOptions();
        }

        private void LoadGameOptions()
        {
            try
            {
                var gamesJsonPath = Path.Combine(AppContext.BaseDirectory, "games.json");
                if (!File.Exists(gamesJsonPath))
                {
                    MessageBox.Show("games.json file not found. Please make sure the file exists in the application directory.", 
                        "Configuration Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var jsonContent = File.ReadAllText(gamesJsonPath);
                var gamesConfig = JsonSerializer.Deserialize<GamesConfiguration>(jsonContent, new JsonSerializerOptions 
                { 
                    PropertyNameCaseInsensitive = true 
                });

                if (gamesConfig?.Games != null)
                {
                    foreach (var game in gamesConfig.Games)
                    {
                        _games.Add(new GameOption
                        {
                            Name = game.Name,
                            DownloadUrls = game.DownloadUrls.ToList()
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading games configuration: {ex.Message}", 
                    "Configuration Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public class GamesConfiguration
        {
            public List<GameConfig> Games { get; set; } = new();
        }

        public class GameConfig
        {
            public string Name { get; set; } = "";
            public string[] DownloadUrls { get; set; } = Array.Empty<string>();
        }

        private async void DownloadButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedGames = _games.Where(g => g.IsSelected).ToList();
            if (!selectedGames.Any())
            {
                MessageBox.Show("Please select at least one game to download.", "No Selection", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                DownloadButton.IsEnabled = false;
                DownloadProgress.Visibility = Visibility.Visible;
                StatusText.Visibility = Visibility.Visible;
                
                _downloadCts = new CancellationTokenSource();
                
                foreach (var game in selectedGames)
                {
                    StatusText.Text = $"Downloading {game.Name}...";
                    await DownloadGameFiles(game, _downloadCts.Token);
                }

                StatusText.Text = "Download completed successfully!";
                MessageBox.Show("All selected games have been downloaded and extracted.", 
                    "Download Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                
                DialogResult = true;
                Close();
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "Download cancelled.";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
                MessageBox.Show($"Download failed: {ex.Message}", "Download Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                DownloadButton.IsEnabled = true;
                DownloadProgress.Visibility = Visibility.Collapsed;
                _downloadCts?.Dispose();
                _downloadCts = null;
            }
        }

        private async Task DownloadGameFiles(GameOption game, CancellationToken cancellationToken)
        {
            var downloadsFolder = Path.Combine(AppContext.BaseDirectory, "download");
            Directory.CreateDirectory(downloadsFolder);
            
            for (int i = 0; i < game.DownloadUrls.Count; i++)
            {
                var url = game.DownloadUrls[i];
                var fileName = Path.GetFileName(new Uri(url).LocalPath);
                if (string.IsNullOrEmpty(fileName))
                {
                    fileName = $"{game.Name}_part{i + 1}.zip";
                }
                
                var downloadPath = Path.Combine(downloadsFolder, fileName);
                
                StatusText.Text = $"Downloading {fileName}...";
                
                using (var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                {
                    response.EnsureSuccessStatusCode();
                    
                    var totalBytes = response.Content.Headers.ContentLength ?? 0;
                    var downloadedBytes = 0L;
                    
                    using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken))
                    using (var fileStream = new FileStream(downloadPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        var buffer = new byte[8192];
                        int bytesRead;
                        
                        while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                        {
                            await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                            downloadedBytes += bytesRead;
                            
                            if (totalBytes > 0)
                            {
                                var progressPercentage = (double)downloadedBytes / totalBytes * 100;
                                DownloadProgress.Value = progressPercentage;
                            }
                        }
                    }
                }
                
                StatusText.Text = $"Extracting {fileName}...";
                await ExtractArchiveFile(downloadPath, _bgmDir, cancellationToken);
                
                await Task.Delay(100, cancellationToken);
                
                try
                {
                    File.Delete(downloadPath);
                }
                catch (Exception ex)
                {
                    StatusText.Text = $"Warning: Could not delete temporary file: {ex.Message}";
                    await Task.Delay(1000, cancellationToken);
                }
            }
        }

        private async Task ExtractArchiveFile(string archivePath, string extractPath, CancellationToken cancellationToken)
        {
            var fileExtension = Path.GetExtension(archivePath).ToLowerInvariant();
            
            if (fileExtension == ".7z")
            {
                await Extract7zWithExternalTool(archivePath, extractPath, cancellationToken);
            }
            else if (fileExtension == ".zip")
            {
                await Task.Run(() =>
                {
                    using var archive = ZipFile.OpenRead(archivePath);
                    foreach (var entry in archive.Entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        
                        if (!string.IsNullOrEmpty(entry.Name))
                        {
                            var destinationPath = Path.Combine(extractPath, entry.FullName);
                            var destinationDir = Path.GetDirectoryName(destinationPath);
                            
                            if (!string.IsNullOrEmpty(destinationDir))
                            {
                                Directory.CreateDirectory(destinationDir);
                            }
                            
                            entry.ExtractToFile(destinationPath, true);
                        }
                    }
                }, cancellationToken);
            }
            else
            {
                throw new NotSupportedException($"Archive format '{fileExtension}' is not supported. Supported formats: .zip, .7z");
            }
        }

        private async Task Extract7zWithExternalTool(string archivePath, string extractPath, CancellationToken cancellationToken)
        {
            var toolsDir = Path.Combine(AppContext.BaseDirectory, "Tools");
            var sevenZipPath = Path.Combine(toolsDir, "7za.exe");
            
            if (!File.Exists(sevenZipPath))
            {
                await ExtractWith7zFallback(archivePath, extractPath, cancellationToken);
                return;
            }

            await Task.Run(() =>
            {
                var processInfo = new ProcessStartInfo
                {
                    FileName = sevenZipPath,
                    Arguments = $"x \"{archivePath}\" -o\"{extractPath}\" -y -bd",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = Process.Start(processInfo);
                if (process != null)
                {
                    process.WaitForExit();
                    if (process.ExitCode != 0)
                    {
                        throw new InvalidOperationException($"7z extraction failed with exit code: {process.ExitCode}");
                    }
                }
            }, cancellationToken);
        }

        private async Task ExtractWith7zFallback(string archivePath, string extractPath, CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                using var fileStream = File.OpenRead(archivePath);
                using var archive = ArchiveFactory.Open(fileStream);
                
                var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
                var processed = 0;
                
                Parallel.ForEach(entries, new ParallelOptions 
                { 
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = Environment.ProcessorCount
                }, entry =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    var destinationPath = Path.Combine(extractPath, entry.Key);
                    var destinationDir = Path.GetDirectoryName(destinationPath);
                    
                    if (!string.IsNullOrEmpty(destinationDir))
                    {
                        Directory.CreateDirectory(destinationDir);
                    }
                    
                    using var entryStream = entry.OpenEntryStream();
                    using var outputStream = File.Create(destinationPath);
                    entryStream.CopyTo(outputStream);
                    
                    Interlocked.Increment(ref processed);
                });
            }, cancellationToken);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _downloadCts?.Cancel();
            DialogResult = false;
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            _downloadCts?.Cancel();
            _downloadCts?.Dispose();
            _httpClient.Dispose();
            base.OnClosed(e);
        }
    }
}