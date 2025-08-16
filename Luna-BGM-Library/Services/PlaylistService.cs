using LunaBgmLibrary.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;

namespace LunaBgmLibrary.Services
{
    public static class PlaylistService
    {
        private static readonly string[] Supported = new[] { ".mp3", ".wav", ".flac" };

        public static ObservableCollection<TrackInfo> LoadFromFolder(string bgmRoot, string? relativeSubdir = null)
        {
            if (!Directory.Exists(bgmRoot))
                Directory.CreateDirectory(bgmRoot);

            string searchRoot = bgmRoot;
            SearchOption searchOption;
            
            if (relativeSubdir == null)
            {
                // All folders - search all subdirectories recursively
                searchOption = SearchOption.AllDirectories;
            }
            else if (relativeSubdir == "")
            {
                // BGM root folder only - no subdirectories
                searchOption = SearchOption.TopDirectoryOnly;
            }
            else
            {
                // Specific subfolder - only files in that folder, no deeper subdirectories
                var full = Path.GetFullPath(Path.Combine(bgmRoot, relativeSubdir));
                var rootFull = Path.GetFullPath(bgmRoot) + Path.DirectorySeparatorChar;
                if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                    full = bgmRoot;
                searchRoot = full;
                searchOption = SearchOption.TopDirectoryOnly;
            }

            var files = Directory.EnumerateFiles(
                            searchRoot,
                            "*.*",
                            searchOption)
                        .Where(f => Supported.Contains(Path.GetExtension(f).ToLowerInvariant()))
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                        .ToList();

            var list = new List<TrackInfo>(files.Count);
            foreach (var f in files)
            {
                list.Add(ReadTrack(f));
            }

            var sorted = list.OrderBy(x => x.Track == 0 ? int.MaxValue : x.Track)
                             .ThenBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
                             .ToList();

            return new ObservableCollection<TrackInfo>(sorted);
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
