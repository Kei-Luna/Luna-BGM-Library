using LunaBgmLibrary.Utilities;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
 

namespace LunaBgmLibrary.Services
{
    public sealed class UpdateInfo
    {
        public bool HasUpdate { get; init; }
        public string CurrentVersion { get; init; } = "";
        public string LatestVersion { get; init; } = "";
        public string DownloadUrl { get; init; } = "";
        public string? ReleaseNotesUrl { get; init; }
    }

    public static class UpdateService
    {
        private static string RemoteVersionUrl => "https://raw.githubusercontent.com/Kei-Luna/Luna-BGM-Library/main/Luna-BGM-Library/Version.txt";
        private static string BuildDownloadUrl(string version) => $"https://github.com/Kei-Luna/Luna-BGM-Library/releases/download/{version}/Luna-BGM-Library-{version}.zip";
        private static string BuildReleaseNotesUrl(string version) => $"https://github.com/Kei-Luna/Luna-BGM-Library/releases/tag/{version}";

        

        public static async Task<UpdateInfo> CheckForUpdateAsync(CancellationToken ct)
        {
            var current = VersionUtil.GetLocalVersionRaw();

            try
            {
                using var http = new HttpClient();
                http.DefaultRequestHeaders.UserAgent.ParseAdd("LunaBgmLibrary-Updater");
                var remote = await http.GetStringAsync(RemoteVersionUrl, ct);
                var latest = remote.Trim();

                bool hasUpdate = VersionUtil.Compare(current, latest) < 0;

                return new UpdateInfo
                {
                    HasUpdate = hasUpdate,
                    CurrentVersion = current,
                    LatestVersion = latest,
                    DownloadUrl = BuildDownloadUrl(latest),
                    ReleaseNotesUrl = BuildReleaseNotesUrl(latest)
                };
            }
            catch
            {
                return new UpdateInfo { HasUpdate = false, CurrentVersion = current, LatestVersion = current };
            }
        }

        public static async Task<bool> DownloadAndStartUpdateAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct)
        {
            if (info == null || !info.HasUpdate || string.IsNullOrWhiteSpace(info.DownloadUrl))
                return false;

            try
            {
                var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
                var tempDir = Path.Combine(baseDir, "temp");
                Directory.CreateDirectory(tempDir);
                var time = DateTime.Now.ToString("yyyyMMddHHmmss");
                var tempZip = Path.Combine(tempDir, $"LunaBgm_Update_{time}.zip");
                using (var http = new HttpClient())
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("LunaBgmLibrary-Updater");
                    using var resp = await http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                    resp.EnsureSuccessStatusCode();
                    var total = resp.Content.Headers.ContentLength ?? -1;
                    var canReport = total > 0 && progress != null;

                    await using var rs = await resp.Content.ReadAsStreamAsync(ct);
                    await using var fs = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None);
                    var buffer = new byte[81920];
                    long read = 0;
                    int n;
                    while ((n = await rs.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                    {
                        await fs.WriteAsync(buffer.AsMemory(0, n), ct);
                        read += n;
                        if (canReport)
                        {
                            progress!.Report(read / (double)total * 100.0);
                        }
                    }
                }

                // Prepare a PowerShell script file to wait for this process, extract to staging, copy over, restart, and cleanup
                var exePath = Environment.ProcessPath ?? Path.Combine(baseDir, "Luna-BGM-Library.exe");
                var exeName = Path.GetFileName(exePath);
                var pid = Process.GetCurrentProcess().Id;

                var scriptPath = Path.Combine(tempDir, $"run_update_{time}.ps1");

                var ps = new System.Text.StringBuilder();
                ps.AppendLine("$ErrorActionPreference='Continue'");
                ps.AppendLine($"$pidToWait={pid}");
                ps.AppendLine($"$zip=\"{EscapeForPs(tempZip)}\"");
                ps.AppendLine($"$target=\"{EscapeForPs(baseDir)}\"");
                ps.AppendLine($"$exe=\"{EscapeForPs(exeName)}\"");
                ps.AppendLine($"$tempDir=\"{EscapeForPs(tempDir)}\"");
                ps.AppendLine("$stage = Join-Path $tempDir 'extracted'");
                ps.AppendLine("try { Wait-Process -Id $pidToWait -Timeout 120 } catch {}");
                ps.AppendLine("Start-Sleep -Milliseconds 700");
                ps.AppendLine("New-Item -ItemType Directory -Path $stage -Force | Out-Null");
                ps.AppendLine("try { Expand-Archive -LiteralPath $zip -DestinationPath $stage -Force } catch { try { Add-Type -AssemblyName System.IO.Compression.FileSystem; [System.IO.Compression.ZipFile]::ExtractToDirectory($zip, $stage, $true) } catch { exit 1 } }");
                ps.AppendLine("$items = Get-ChildItem -LiteralPath $stage");
                ps.AppendLine("if ($items.Count -eq 1 -and $items[0].PSIsContainer) { $sourceRoot = $items[0].FullName } else { $sourceRoot = $stage }");
                ps.AppendLine("Copy-Item -Path (Join-Path $sourceRoot '*') -Destination $target -Recurse -Force");
                ps.AppendLine("Start-Process -FilePath (Join-Path $target $exe)");
                ps.AppendLine("try { Remove-Item -LiteralPath $zip -Force } catch {}");
                ps.AppendLine("try { Remove-Item -LiteralPath $stage -Recurse -Force } catch {}");
                // schedule self-clean of script + temp dir after restart
                ps.AppendLine(@"$cmd = 'timeout /t 3 >nul & del /f /q ' + '""' + $PSCommandPath + '""' + ' & rmdir /s /q ' + '""' + $tempDir + '""'");
                ps.AppendLine("Start-Process -FilePath cmd.exe -ArgumentList '/c', $cmd -WindowStyle Hidden");

                File.WriteAllText(scriptPath, ps.ToString());

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                Process.Start(psi);
                return true; // Caller should shutdown the app immediately after this
            }
            catch
            {
                return false;
            }
        }

        

        private static string EscapeForPs(string path)
        {
            return path.Replace("`", "``").Replace("\"", "`\"");
        }
    }
}
