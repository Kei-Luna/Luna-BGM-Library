using LunaBgmLibrary.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace LunaBgmLibrary.Services
{
    public static class PlaylistService
    {
        private static readonly string[] Supported = new[] { ".mp3", ".wav", ".flac", ".aiff", ".aif", ".ogg", ".wma", ".aac", ".mp4", ".m4a" };
        
        private static readonly ConcurrentDictionary<string, TrackInfo> _trackCache = new();
        private static readonly ConcurrentDictionary<string, (DateTime lastModified, List<string> files)> _folderCache = new();

        public static async Task<ObservableCollection<TrackInfo>> LoadFromFolderAsync(string bgmRoot, string? relativeSubdir = null)
        {
            if (!Directory.Exists(bgmRoot))
                Directory.CreateDirectory(bgmRoot);

            string searchRoot = bgmRoot;
            SearchOption searchOption;
            
            if (relativeSubdir == null)
            {
                searchOption = SearchOption.AllDirectories;
            }
            else if (relativeSubdir == "")
            {
                searchOption = SearchOption.TopDirectoryOnly;
            }
            else
            {
                var full = Path.GetFullPath(Path.Combine(bgmRoot, relativeSubdir));
                var rootFull = Path.GetFullPath(bgmRoot) + Path.DirectorySeparatorChar;
                if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                    full = bgmRoot;
                searchRoot = full;
                searchOption = SearchOption.TopDirectoryOnly;
            }

            var cacheKey = $"{searchRoot}:{searchOption}";
            var lastWrite = Directory.GetLastWriteTime(searchRoot);
            
            List<string> files;
            if (_folderCache.TryGetValue(cacheKey, out var cached) && cached.lastModified >= lastWrite)
            {
                files = cached.files;
            }
            else
            {
                files = await Task.Run(() => Directory.EnumerateFiles(searchRoot, "*.*", searchOption)
                    .Where(f => Supported.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList());
                
                _folderCache[cacheKey] = (lastWrite, files);
            }

            var tracks = await Task.Run(() => 
            {
                var list = new ConcurrentBag<TrackInfo>();
                Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, f =>
                {
                    list.Add(ReadTrackCached(f));
                });
                return list.ToList();
            });

            var sorted = tracks.OrderBy(x => x.Track == 0 ? int.MaxValue : x.Track)
                             .ThenBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
                             .ToList();

            return new ObservableCollection<TrackInfo>(sorted);
        }

        [Obsolete("Use LoadFromFolderAsync for better performance")]
        public static ObservableCollection<TrackInfo> LoadFromFolder(string bgmRoot, string? relativeSubdir = null)
        {
            return LoadFromFolderAsync(bgmRoot, relativeSubdir).GetAwaiter().GetResult();
        }

        public static List<string> GetRelativeFolders(string bgmRoot)
        {
            if (!Directory.Exists(bgmRoot))
                return new List<string>();

            var rootFull = Path.GetFullPath(bgmRoot);
            var dirs = Directory.EnumerateDirectories(rootFull, "*", SearchOption.AllDirectories)
                                .Select(d => MakeRelative(rootFull, d))
                                .Where(rel => !string.IsNullOrWhiteSpace(rel))
                                .OrderBy(rel => rel, StringComparer.OrdinalIgnoreCase)
                                .ToList();
            return dirs;
        }

        private static string MakeRelative(string rootFull, string pathFull)
        {
            rootFull = Path.GetFullPath(rootFull).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(pathFull);
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                return string.Empty;
            return full.Substring(rootFull.Length).Replace(Path.DirectorySeparatorChar, '/');
        }

        private static TrackInfo ReadTrackCached(string f)
        {
            var lastWrite = File.GetLastWriteTime(f);
            var cacheKey = $"{f}:{lastWrite.Ticks}";
            
            if (_trackCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }
            
            var track = ReadTrack(f);
            
            _trackCache.TryAdd(cacheKey, track);
            
            if (_trackCache.Count > 10000)
            {
                var oldKeys = _trackCache.Keys.Take(_trackCache.Count / 4).ToList();
                foreach (var key in oldKeys)
                {
                    _trackCache.TryRemove(key, out _);
                }
            }
            
            return track;
        }

        private static TrackInfo ReadTrack(string f)
        {
            try
            {
                var tagFile = TagLib.File.Create(f);
                return new TrackInfo
                {
                    FilePath = f,
                    FileName = Path.GetFileName(f),
                    Title    = string.IsNullOrWhiteSpace(tagFile.Tag.Title) ? Path.GetFileNameWithoutExtension(f) : tagFile.Tag.Title,
                    Artist   = tagFile.Tag.FirstPerformer ?? "",
                    Album    = tagFile.Tag.Album ?? "",
                    Track    = (int)tagFile.Tag.Track,
                    Duration = tagFile.Properties.Duration,
                    Artwork  = ToImage(tagFile.Tag.Pictures?.FirstOrDefault()?.Data?.Data)
                };
            }
            catch
            {
                var fi = new FileInfo(f);
                return new TrackInfo
                {
                    FilePath = f,
                    FileName = fi.Name,
                    Title    = Path.GetFileNameWithoutExtension(f),
                    Duration = TimeSpan.Zero
                };
            }
        }

        private static BitmapImage? ToImage(byte[]? bytes)
        {
            if (bytes == null || bytes.Length == 0) return null;
            try
            {
                using var ms = new MemoryStream(bytes);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }
    }
}
