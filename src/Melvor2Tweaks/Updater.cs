using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Melvor2Tweaks;

/// <summary>
/// Keeps the mod up to date from its GitHub releases. A while after the game starts, and every few hours,
/// it reads the latest release; if it's newer than this build it downloads this platform's zip and
/// unpacks it over the game folder. Melvor2Tweaks.dll is written last, so the loader's hot reload swaps
/// the new version in straight away; the loader, Harmony and the patcher are picked up on the next start
/// (in-use files are renamed aside on Windows and cleaned up later). Turn it off in Settings > Updates.
/// </summary>
internal static class Updater
{
    internal const string Repo = "ZeldoKavira/Melvor2Tweaks";
    private static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private static CancellationTokenSource? s_cts;

    /// <summary>The folder holding mods/ (game folder; Contents/Resources on macOS).</summary>
    private static string Root => Path.GetDirectoryName(ModsDir)!;
    private static string ModsDir => Path.GetDirectoryName(typeof(Melvor2Loader.Loader).Assembly.Location)!;

    internal static string Platform =>
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    internal static void Start()
    {
        CleanOldFiles();
        s_cts = new CancellationTokenSource();
        var token = s_cts.Token;
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(FirstCheck, token);
                while (!token.IsCancellationRequested)
                {
                    if (Settings.Current.AutoUpdate) await CheckAndInstall(token);
                    await Task.Delay(Interval, token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Plugin.Log("Updater: " + e.Message); }
        });
    }

    internal static void Stop()
    {
        s_cts?.Cancel();
        s_cts = null;
    }

    /// <summary>Checks the latest release and installs it when newer. Returns a status line.</summary>
    internal static async Task<string> CheckAndInstall(CancellationToken token, bool force = false)
    {
        string status;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Melvor2Tweaks-Updater");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            string json = await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest", token);
            using var doc = JsonDocument.Parse(json);
            string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest) || !Version.TryParse(Plugin.Version, out var current))
                return Report($"can't compare versions ({tag} vs {Plugin.Version})");
            if (latest <= current && !force) return Report($"up to date (v{Plugin.Version}, latest {tag})");

            string suffix = $"-{Platform}.zip";
            var asset = doc.RootElement.GetProperty("assets").EnumerateArray()
                .FirstOrDefault(a => a.GetProperty("name").GetString()?.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) == true);
            if (asset.ValueKind == JsonValueKind.Undefined) return Report($"{tag} has no {Platform} download");
            string url = asset.GetProperty("browser_download_url").GetString()!;

            Plugin.Log($"Updater: downloading {tag} ({asset.GetProperty("name").GetString()})");
            byte[] zip = await http.GetByteArrayAsync(url, token);
            bool restartNeeded = Install(zip);
            status = $"updated to {tag}" + (restartNeeded ? " - restart the game to finish (loader/patcher changed)" : "");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { status = "check failed: " + e.Message; }
        return Report(status);
    }

    private static string Report(string status)
    {
        Plugin.Log("Updater: " + status);
        return status;
    }

    /// <summary>Unpacks a release zip over the game folder. True when something only a restart picks up changed.</summary>
    internal static bool Install(byte[] zipBytes)
    {
        string root = Path.GetFullPath(Root);
        using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        var files = zip.Entries.Where(e => e.Length > 0 || !e.FullName.EndsWith("/")).Where(e => !e.FullName.EndsWith("/")).ToList();
        // The mod itself last: writing it triggers the hot reload, so everything else must be in place first.
        files = files.OrderBy(e => Path.GetFileName(e.FullName).Equals("Melvor2Tweaks.dll", StringComparison.OrdinalIgnoreCase) ? 1 : 0).ToList();
        bool restartNeeded = false;
        foreach (var entry in files)
        {
            string target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue; // no escaping the game folder
            byte[] data;
            using (var s = entry.Open()) using (var ms = new MemoryStream()) { s.CopyTo(ms); data = ms.ToArray(); }

            if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(data)) continue;

            string name = Path.GetFileName(target);
            if (!name.Equals("Melvor2Tweaks.dll", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".pdb") &&
                (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || entry.FullName.StartsWith("mods/patcher/")))
                restartNeeded = true;
            Write(target, data);
            if (!OperatingSystem.IsWindows() && entry.FullName.StartsWith("mods/patcher/Melvor2Patcher"))
                File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                             UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        return restartNeeded;
    }

    /// <summary>Writes beside the target then swaps it in; an in-use file (Windows) is renamed aside first.</summary>
    private static void Write(string target, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string staged = target + ".new";
        File.WriteAllBytes(staged, data);
        try { File.Move(staged, target, overwrite: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            File.Move(target, target + ".old" + DateTime.UtcNow.Ticks);
            File.Move(staged, target);
        }
    }

    /// <summary>Removes files renamed aside by an earlier update (no longer in use after a restart).</summary>
    private static void CleanOldFiles()
    {
        try
        {
            foreach (var dir in new[] { ModsDir, Path.Combine(ModsDir, "patcher") })
                if (Directory.Exists(dir))
                    foreach (var f in Directory.GetFiles(dir, "*.old*"))
                        try { File.Delete(f); } catch { }
        }
        catch { }
    }
}
