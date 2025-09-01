using System;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace LunaBgmLibrary.Utilities
{
    public static class VersionUtil
    {
        public static string GetLocalVersionRaw()
        {
            try
            {
                // Prefer generated constant from Version.txt at build time
                if (!string.IsNullOrWhiteSpace(BuildInfo.VersionRaw) && BuildInfo.VersionRaw != "unknown")
                    return BuildInfo.VersionRaw;

                var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
                var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(info)) return info!;

                var v = asm.GetName().Version?.ToString();
                if (!string.IsNullOrWhiteSpace(v)) return v!;
            }
            catch {}
            return "unknown";
        }

        public static string GetLocalVersionDisplay()
        {
            var raw = GetLocalVersionRaw();
            return string.IsNullOrEmpty(raw) ? "unknown" : raw;
        }

        public static bool TryParseNormalized(string input, out Version version)
        {
            version = new Version(0, 0, 0, 0);
            if (string.IsNullOrWhiteSpace(input)) return false;

            // Strip leading 'v' or 'V', allow underscores as separators
            var s = input.Trim();
            if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(1);

            // Replace underscores with dots, also accept mismatched separators
            s = s.Replace('_', '.');

            // Remove any non version suffix (e.g., -beta)
            int idx = s.IndexOf('-');
            if (idx > 0) s = s.Substring(0, idx);

            // Ensure we have at least Major.Minor
            var parts = s.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return false;

            int[] nums = new int[Math.Max(2, Math.Min(4, parts.Length))];
            for (int i = 0; i < nums.Length; i++)
            {
                if (i < parts.Length && int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                    nums[i] = n;
                else
                    nums[i] = 0;
            }

            try
            {
                version = new Version(nums.Length >= 2 ? nums[0] : 0,
                                      nums.Length >= 2 ? nums[1] : 0,
                                      nums.Length >= 3 ? nums[2] : 0,
                                      nums.Length >= 4 ? nums[3] : 0);
                return true;
            }
            catch { return false; }
        }

        public static int Compare(string a, string b)
        {
            if (!TryParseNormalized(a, out var va)) return -1;
            if (!TryParseNormalized(b, out var vb)) return 1;
            return va.CompareTo(vb);
        }
    }
}
