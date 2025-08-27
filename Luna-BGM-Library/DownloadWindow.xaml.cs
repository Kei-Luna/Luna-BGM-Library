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
                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }
                
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
                
                // Only extract if it's not a split archive or if it's the last part downloaded
                if (ShouldExtractArchive(fileName, game.DownloadUrls, i))
                {
                    StatusText.Text = $"Extracting {fileName}...";
                    await ExtractArchiveFile(downloadPath, _bgmDir, cancellationToken);
                    
                    // After successful extraction, delete all parts of split archives
                    await CleanupSplitArchiveParts(fileName, downloadsFolder, cancellationToken);
                }
                else if (!Is7zSplitArchive(fileName))
                {
                    // Only delete non-split archives immediately
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
                // Split archive parts are kept until extraction is complete
            }
        }

        private async Task ExtractArchiveFile(string archivePath, string extractPath, CancellationToken cancellationToken)
        {
            var fileExtension = Path.GetExtension(archivePath).ToLowerInvariant();
            var fileName = Path.GetFileName(archivePath);
            
            // Check for split 7z files (.001, .002, etc.) or regular .7z files
            if (fileExtension == ".7z" || Is7zSplitArchive(fileName))
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
                throw new NotSupportedException($"Archive format '{fileExtension}' is not supported. Supported formats: .zip, .7z, split 7z (.001, .002, etc.)");
            }
        }

        private bool Is7zSplitArchive(string fileName)
        {
            // Check if the file is a split 7z archive (e.g., filename.7z.001, filename.7z.002)
            if (fileName.Contains(".7z.") && System.Text.RegularExpressions.Regex.IsMatch(fileName, @"\.7z\.\d{3}$"))
            {
                return true;
            }
            
            // Check for other common split archive patterns (e.g., filename.001 where filename contains "7z")
            if (System.Text.RegularExpressions.Regex.IsMatch(fileName, @"\.\d{3}$") && 
                (fileName.ToLowerInvariant().Contains("7z") || fileName.ToLowerInvariant().Contains(".7z")))
            {
                return true;
            }
            
            return false;
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
                // For split archives, we need to use the first part (.001)
                var targetArchive = GetFirstPartOfSplitArchive(archivePath);
                
                // Debug: Check if we found the first part
                if (!File.Exists(targetArchive))
                {
                    throw new InvalidOperationException($"First part of split archive not found: {targetArchive}");
                }
                
                var processInfo = new ProcessStartInfo
                {
                    FileName = sevenZipPath,
                    Arguments = $"x \"{targetArchive}\" -o\"{extractPath}\" -y -bd",
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
                        var stdout = process.StandardOutput.ReadToEnd();
                        var stderr = process.StandardError.ReadToEnd();
                        throw new InvalidOperationException($"7z extraction failed with exit code: {process.ExitCode}\nOriginal file: {archivePath}\nTarget file: {targetArchive}\nStdOut: {stdout}\nStdErr: {stderr}");
                    }
                }
            }, cancellationToken);
        }

        private string GetFirstPartOfSplitArchive(string archivePath)
        {
            var fileName = Path.GetFileName(archivePath);
            var directory = Path.GetDirectoryName(archivePath) ?? "";
            
            // If it's already the first part (.001), return as is
            if (fileName.EndsWith(".001", StringComparison.OrdinalIgnoreCase))
            {
                return archivePath;
            }
            
            // If it's a split archive part (.002, .003, etc.), find the first part
            if (System.Text.RegularExpressions.Regex.IsMatch(fileName, @"\.\d{3}$"))
            {
                var baseFileName = System.Text.RegularExpressions.Regex.Replace(fileName, @"\.\d{3}$", ".001");
                var firstPartPath = Path.Combine(directory, baseFileName);
                
                if (File.Exists(firstPartPath))
                {
                    return firstPartPath;
                }
                else
                {
                    // Look for any .001 file in the directory that matches the pattern
                    var allFiles = Directory.GetFiles(directory, "*.001");
                    foreach (var file in allFiles)
                    {
                        var fileBaseName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(file));
                        var originalBaseName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(fileName));
                        if (fileBaseName.Equals(originalBaseName, StringComparison.OrdinalIgnoreCase))
                        {
                            return file;
                        }
                    }
                }
            }
            
            // If it's a .7z.xxx format
            if (System.Text.RegularExpressions.Regex.IsMatch(fileName, @"\.7z\.\d{3}$"))
            {
                var baseFileName = System.Text.RegularExpressions.Regex.Replace(fileName, @"\.7z\.\d{3}$", ".7z.001");
                var firstPartPath = Path.Combine(directory, baseFileName);
                
                if (File.Exists(firstPartPath))
                {
                    return firstPartPath;
                }
                else
                {
                    // Look for any .7z.001 file in the directory
                    var pattern = System.Text.RegularExpressions.Regex.Replace(fileName, @"\.7z\.\d{3}$", ".7z.001");
                    var searchPattern = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(pattern)) + ".7z.001";
                    var matchingFiles = Directory.GetFiles(directory, "*" + searchPattern);
                    if (matchingFiles.Length > 0)
                    {
                        return matchingFiles[0];
                    }
                }
            }
            
            // Return original path if not a split archive or first part not found
            return archivePath;
        }

        private bool ShouldExtractArchive(string fileName, List<string> allUrls, int currentIndex)
        {
            // If it's not a split archive, extract immediately
            if (!Is7zSplitArchive(fileName))
            {
                return true;
            }
            
            // For split archives, only extract when we've downloaded all parts
            // Check if this is the last file in the download sequence (by index)
            return currentIndex == allUrls.Count - 1;
        }
        
        private int ExtractPartNumber(string fileName)
        {
            var match = System.Text.RegularExpressions.Regex.Match(fileName, @"\.(\d{3})$");
            if (match.Success && int.TryParse(match.Groups[1].Value, out int partNumber))
            {
                return partNumber;
            }
            return -1;
        }

        private async Task CleanupSplitArchiveParts(string fileName, string downloadsFolder, CancellationToken cancellationToken)
        {
            try
            {
                if (!Is7zSplitArchive(fileName))
                {
                    // Not a split archive, delete only this file
                    var filePath = Path.Combine(downloadsFolder, fileName);
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                    }
                    return;
                }

                // For split archives, find and delete all parts
                var basePattern = GetArchiveBasePattern(fileName);
                if (string.IsNullOrEmpty(basePattern))
                {
                    return;
                }

                var allFiles = Directory.GetFiles(downloadsFolder, basePattern + "*");
                foreach (var file in allFiles)
                {
                    var fileFileName = Path.GetFileName(file);
                    if (Is7zSplitArchive(fileFileName) && IsSameArchiveSet(fileName, fileFileName))
                    {
                        try
                        {
                            File.Delete(file);
                        }
                        catch (Exception ex)
                        {
                            StatusText.Text = $"Warning: Could not delete {fileFileName}: {ex.Message}";
                            await Task.Delay(500, cancellationToken);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Warning: Error during cleanup: {ex.Message}";
                await Task.Delay(1000, cancellationToken);
            }
        }

        private string GetArchiveBasePattern(string fileName)
        {
            // Remove the .xxx extension to get base pattern
            if (System.Text.RegularExpressions.Regex.IsMatch(fileName, @"\.7z\.\d{3}$"))
            {
                return System.Text.RegularExpressions.Regex.Replace(fileName, @"\.7z\.\d{3}$", "");
            }
            else if (System.Text.RegularExpressions.Regex.IsMatch(fileName, @"\.\d{3}$"))
            {
                return System.Text.RegularExpressions.Regex.Replace(fileName, @"\.\d{3}$", "");
            }
            return fileName;
        }

        private bool IsSameArchiveSet(string fileName1, string fileName2)
        {
            var base1 = GetArchiveBasePattern(fileName1);
            var base2 = GetArchiveBasePattern(fileName2);
            return base1.Equals(base2, StringComparison.OrdinalIgnoreCase);
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
                    
                    var destinationPath = Path.Combine(extractPath, entry.Key ?? "");
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