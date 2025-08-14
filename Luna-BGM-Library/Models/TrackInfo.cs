using System;
using System.Windows.Media;

namespace LunaBgmLibrary.Models
{
    public class TrackInfo
    {
        public string FilePath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public string? Album { get; set; }
        public int Track { get; set; }
        public TimeSpan Duration { get; set; }
        public ImageSource? Artwork { get; set; }

        public override string ToString() => string.IsNullOrWhiteSpace(Title) ? FileName : Title!;
    }
}