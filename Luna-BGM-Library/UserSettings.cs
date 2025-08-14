using System;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;

namespace LunaBgmLibrary
{
    public sealed class UserSettings
    {
        public float Volume { get; set; } = 0.3f;

        public string EqPresetName { get; set; } = "Flat";

        public float[] EqGainDb { get; set; } = new float[10];

        public List<EqBandSetting>? EqBands { get; set; }

        public static UserSettings Load(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var s = JsonSerializer.Deserialize<UserSettings>(json);
                    if (s != null)
                    {
                        s.Volume = Math.Clamp(s.Volume, 0f, 1f);
                        s.EqPresetName = string.IsNullOrWhiteSpace(s.EqPresetName) ? "Custom" : s.EqPresetName;

                        if (s.EqGainDb == null || s.EqGainDb.Length != 10)
                            s.EqGainDb = new float[10];

                        bool eqGainAllZero = s.EqGainDb.All(v => Math.Abs(v) < 1e-6);
                        if (eqGainAllZero && s.EqBands != null && s.EqBands.Count > 0)
                        {
                            int n = Math.Min(10, s.EqBands.Count);
                            for (int i = 0; i < n; i++)
                                s.EqGainDb[i] = ClampDb(s.EqBands[i].GainDb);
                        }

                        for (int i = 0; i < 10; i++)
                            s.EqGainDb[i] = ClampDb(s.EqGainDb[i]);

                        return s;
                    }
                }
            }
            catch {}
            return new UserSettings();
        }

        public static void Save(string path, UserSettings settings)
        {
            try
            {
                settings.Volume = Math.Clamp(settings.Volume, 0f, 1f);

                if (settings.EqGainDb == null || settings.EqGainDb.Length != 10)
                    settings.EqGainDb = new float[10];

                for (int i = 0; i < 10; i++)
                    settings.EqGainDb[i] = ClampDb(settings.EqGainDb[i]);

                settings.EqBands = EqDefaults.CreateDefault10Band();
                for (int i = 0; i < 10; i++)
                {
                    settings.EqBands[i].GainDb = settings.EqGainDb[i];
                    settings.EqBands[i].Q = 1.0f;
                }

                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
            catch {}
        }

        public static List<EqBandSetting> ToBands(float[] gainDb)
        {
            var list = EqDefaults.CreateDefault10Band();
            if (gainDb != null && gainDb.Length == 10)
                for (int i = 0; i < 10; i++) list[i].GainDb = ClampDb(gainDb[i]);
            return list;
        }

        public static float[] ToGainArray(List<EqBandSetting> bands)
        {
            var arr = new float[10];
            if (bands != null && bands.Count > 0)
            {
                int n = Math.Min(10, bands.Count);
                for (int i = 0; i < n; i++) arr[i] = ClampDb(bands[i].GainDb);
            }
            return arr;
        }

        private static float ClampDb(float v) => Math.Max(-12f, Math.Min(12f, v));
    }

    public sealed class EqBandSetting
    {
        public float Frequency { get; set; }
        public float GainDb { get; set; }
        public float Q { get; set; } = 1.0f;
    }

    public static class EqDefaults
    {
        private static readonly float[] Centers = new float[]
        {
            31f, 62f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f
        };

        public static List<EqBandSetting> CreateDefault10Band(float gainDb = 0f, float q = 1.0f)
        {
            var list = new List<EqBandSetting>(Centers.Length);
            foreach (var f in Centers)
                list.Add(new EqBandSetting { Frequency = f, GainDb = gainDb, Q = q });
            return list;
        }

        public static (string, List<EqBandSetting>) Flat()        => ("Flat", CreateDefault10Band(0f));
        public static (string, List<EqBandSetting>) BassBoost()   { var b = CreateDefault10Band(0f); b[0].GainDb=6f; b[1].GainDb=5f; b[2].GainDb=3f; b[9].GainDb=-1f; return ("Bass Boost", b); }
        public static (string, List<EqBandSetting>) TrebleBoost() { var b = CreateDefault10Band(0f); b[7].GainDb=3f; b[8].GainDb=5f; b[9].GainDb=6f; b[0].GainDb=-1f; return ("Treble Boost", b); }
        public static (string, List<EqBandSetting>) Vocal()       { var b = CreateDefault10Band(0f); b[4].GainDb=2f; b[5].GainDb=3.5f; b[6].GainDb=3f; b[7].GainDb=2f; b[0].GainDb=-1.5f; b[1].GainDb=-1f; return ("Vocal", b); }
        public static (string, List<EqBandSetting>) Loudness()    { var b = CreateDefault10Band(0f); b[0].GainDb=5f; b[1].GainDb=4f; b[8].GainDb=3f; b[9].GainDb=4f; return ("Loudness", b); }

        public static IReadOnlyList<(string, List<EqBandSetting>)> Presets => new[]
        {
            Flat(), BassBoost(), TrebleBoost(), Vocal(), Loudness()
        };
    }
}
