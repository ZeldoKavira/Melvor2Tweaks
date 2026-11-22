using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using GamesByMalcs.Melvor2;
using Godot;

namespace Melvor2Loader;

/// <summary>
/// Stable mod loader with hot reload. Started by a module initializer Melvor2Patcher adds to the game's
/// Serilog.dll and SmartFormat.dll (Boot, whichever loads first), or by the older hook in the game DLL
/// (Init). It only wires up assembly resolution and Harmony hooks on Main, then loads mods once Main is
/// ready, and re-runs the patcher if a game update replaced one of the hooked libraries (SelfHeal).
///
/// Mods are the other *.dll files in the mods folder. Each is loaded from bytes (the file stays
/// unlocked) into its own load context that shares the game's assemblies and 0Harmony, and must expose
/// a static class with <c>public static void ModLoad()</c> and <c>public static void ModUnload()</c>.
/// When a mod DLL changes on disk it is reloaded in place: old ModUnload -> new copy -> ModLoad.
/// </summary>
public static class Loader
{
    private static readonly HashSet<string> NotMods = new(StringComparer.OrdinalIgnoreCase) { "Melvor2Loader", "0Harmony" };

    internal static string ModsDir { get; private set; } = "";
    internal static AssemblyLoadContext HostContext { get; private set; } = null!;
    private static bool s_godotReady;
    private static readonly Dictionary<string, LoadedMod> s_mods = new(StringComparer.OrdinalIgnoreCase);
    private static FileSystemWatcher? s_watcher;
    private static volatile bool s_dirty;
    private static DateTime s_lastChange;

    private static bool s_initialized;

    /// <summary>Entry point from the patched libraries.</summary>
    public static void Boot() => Init(typeof(Main).Assembly);

    public static void Init(Assembly gameAssembly)
    {
        if (s_initialized) return; // both hooked libraries call in
        s_initialized = true;
        ModsDir = Path.GetDirectoryName(typeof(Loader).Assembly.Location)!;
        HostContext = AssemblyLoadContext.GetLoadContext(typeof(Loader).Assembly)!;
        try { File.WriteAllText(LogPath, ""); } catch { }

        HostContext.Resolving += (alc, name) =>
        {
            string candidate = Path.Combine(ModsDir, name.Name + ".dll");
            return File.Exists(candidate) ? alc.LoadFromAssemblyPath(candidate) : null;
        };
        Log($"Melvor2Loader starting (runtime {System.Environment.Version}, context '{HostContext.Name}')");
        try
        {
            HookReady();
        }
        catch (Exception e)
        {
            Log("Could not hook Main._Ready, mods will not load: " + e);
        }
    }

    // Separate method: 0Harmony is only resolvable after the Resolving handler above is registered.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void HookReady()
    {
        var harmony = new HarmonyLib.Harmony("net.zeldo.melvor2loader");
        harmony.Patch(HarmonyLib.AccessTools.Method(typeof(Main), nameof(Main._Ready)),
            postfix: new HarmonyLib.HarmonyMethod(typeof(Loader), nameof(OnMainReady)));
        // Fallback in case Boot ran while Main._Ready was already executing (a postfix added then
        // doesn't apply to that call): the first frame loads the mods instead.
        harmony.Patch(HarmonyLib.AccessTools.Method(typeof(Main), nameof(Main._Process)),
            postfix: new HarmonyLib.HarmonyMethod(typeof(Loader), nameof(OnMainReady)));
        Log("Waiting for Main._Ready");
    }

    private static void OnMainReady()
    {
        if (s_godotReady) return;
        s_godotReady = true;
        Log("Main ready - loading mods");

        foreach (string dll in Directory.GetFiles(ModsDir, "*.dll"))
        {
            string name = Path.GetFileNameWithoutExtension(dll);
            if (!NotMods.Contains(name)) LoadMod(dll);
        }

        s_watcher = new FileSystemWatcher(ModsDir, "*.dll")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        s_watcher.Changed += OnFileEvent;
        s_watcher.Created += OnFileEvent;
        s_watcher.Renamed += OnFileEvent;

        // Reloads run on the main thread, once the copy has settled.
        ((SceneTree)Engine.GetMainLoop()).ProcessFrame += Pump;

        System.Threading.Tasks.Task.Run(SelfHeal);
    }

    /// <summary>
    /// A game update can replace one of the libraries the patcher hooked (the mod still loaded through the
    /// other one). Compares them with mods/patch.stamp and, if one changed, runs the patcher in the
    /// background so the hook is back on the next start.
    /// </summary>
    private static void SelfHeal()
    {
        try
        {
            string root = Path.GetDirectoryName(ModsDir)!;
            string dataDir = Path.GetDirectoryName(typeof(Main).Assembly.Location)!;
            // Files an earlier re-patch renamed aside while they were in use (free now).
            foreach (string lib in new[] { "Serilog.dll", "SmartFormat.dll", "GamesByMalcs.Melvor2.dll" })
                foreach (string old in Directory.GetFiles(dataDir, lib + ".old*"))
                    try { File.Delete(old); } catch { }
            string stampPath = Path.Combine(ModsDir, "patch.stamp");
            string patchDir = Path.Combine(ModsDir, "patcher");
            string expected = File.Exists(Path.Combine(patchDir, "patch-version.txt"))
                ? File.ReadAllText(Path.Combine(patchDir, "patch-version.txt")).Trim() : "";
            var stamp = File.Exists(stampPath)
                ? File.ReadAllLines(stampPath).Select(l => l.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1].Trim())
                : new Dictionary<string, string>();

            var changed = new List<string>();
            foreach (string lib in new[] { "Serilog.dll", "SmartFormat.dll" })
            {
                string path = Path.Combine(dataDir, lib);
                if (!stamp.TryGetValue(lib, out var md5) || !File.Exists(path) || Md5(path) != md5) changed.Add(lib);
            }
            if (expected.Length > 0 && (!stamp.TryGetValue("patch", out var v) || v != expected)) changed.Add("patch version " + expected);
            if (changed.Count == 0) return;

            string exe = OperatingSystem.IsWindows() ? "Melvor2Patcher.exe"
                : OperatingSystem.IsMacOS() ? (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "Melvor2Patcher-arm64" : "Melvor2Patcher-x64")
                : "Melvor2Patcher";
            string patcher = Path.Combine(patchDir, exe);
            if (!File.Exists(patcher)) { Log($"Hook needs re-applying ({string.Join(", ", changed)}) but {patcher} is missing - run the installer again."); return; }
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(patcher, File.GetUnixFileMode(patcher) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);

            Log($"Re-applying the hook ({string.Join(", ", changed)} changed)");
            var psi = new System.Diagnostics.ProcessStartInfo(patcher) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            psi.ArgumentList.Add(root);
            using var proc = System.Diagnostics.Process.Start(psi)!;
            string output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            Log($"Patcher exit {proc.ExitCode}: {output.Trim()}");
        }
        catch (Exception e) { Log("Self-heal failed: " + e.Message); }
    }

    private static string Md5(string path)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        using var f = File.OpenRead(path);
        return Convert.ToHexString(md5.ComputeHash(f)).ToLowerInvariant();
    }

    private static void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        s_lastChange = DateTime.UtcNow;
        s_dirty = true;
    }

    private static void Pump()
    {
        if (!s_dirty || (DateTime.UtcNow - s_lastChange).TotalMilliseconds < 500) return;
        s_dirty = false;

        foreach (string dll in Directory.GetFiles(ModsDir, "*.dll"))
        {
            string name = Path.GetFileNameWithoutExtension(dll);
            if (NotMods.Contains(name)) continue;
            DateTime stamp = File.GetLastWriteTimeUtc(dll);
            if (s_mods.TryGetValue(name, out var mod) && mod.Stamp == stamp) continue;
            LoadMod(dll);
        }
    }

    private static void LoadMod(string dll)
    {
        string name = Path.GetFileNameWithoutExtension(dll);
        if (s_mods.TryGetValue(name, out var old))
        {
            Log($"Reloading {name}");
            old.Unload();
            s_mods.Remove(name);
        }

        try
        {
            var mod = LoadedMod.Load(dll);
            s_mods[name] = mod;
            Log($"Loaded {name} {mod.Version} (#{mod.Generation})");
        }
        catch (Exception e)
        {
            Log($"Failed to load {name}: {e}");
        }
    }

    internal static string LogPath => Path.Combine(ModsDir, "Melvor2Loader.log");

    public static void Log(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] [Melvor2Loader] {message}";
        try { File.AppendAllText(LogPath, line + System.Environment.NewLine); } catch { }
        if (s_godotReady) GD.Print(line);
    }
}

/// <summary>One loaded copy of a mod assembly.</summary>
internal sealed class LoadedMod
{
    private static int s_generation;

    public required string Name { get; init; }
    public required DateTime Stamp { get; init; }
    public required int Generation { get; init; }
    public required string Version { get; init; }
    private MethodInfo? UnloadMethod { get; init; }

    public static LoadedMod Load(string dll)
    {
        string name = Path.GetFileNameWithoutExtension(dll);
        DateTime stamp = File.GetLastWriteTimeUtc(dll);
        byte[] image = ReadShared(dll);
        string pdbPath = Path.ChangeExtension(dll, ".pdb");
        byte[]? pdb = File.Exists(pdbPath) ? ReadShared(pdbPath) : null;

        int gen = ++s_generation;
        var ctx = new ModLoadContext($"{name}#{gen}");
        Assembly asm = pdb != null
            ? ctx.LoadFromStream(new MemoryStream(image), new MemoryStream(pdb))
            : ctx.LoadFromStream(new MemoryStream(image));

        Type entry = asm.GetTypes().FirstOrDefault(t =>
                         t.GetMethod("ModLoad", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes) != null &&
                         t.GetMethod("ModUnload", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes) != null)
                     ?? throw new Exception("no static class with public static ModLoad() and ModUnload()");

        entry.GetMethod("ModLoad", Type.EmptyTypes)!.Invoke(null, null);
        return new LoadedMod
        {
            Name = name,
            Stamp = stamp,
            Generation = gen,
            Version = asm.GetName().Version?.ToString() ?? "?",
            UnloadMethod = entry.GetMethod("ModUnload", Type.EmptyTypes),
        };
    }

    public void Unload()
    {
        try { UnloadMethod?.Invoke(null, null); }
        catch (Exception e) { Loader.Log($"{Name} ModUnload threw: {e}"); }
    }

    // The build may still be writing; retry briefly and never lock the file.
    private static byte[] ReadShared(string path)
    {
        for (int i = 0; ; i++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var ms = new MemoryStream();
                fs.CopyTo(ms);
                return ms.ToArray();
            }
            catch (IOException) when (i < 10)
            {
                System.Threading.Thread.Sleep(100);
            }
        }
    }
}

/// <summary>
/// A mod's own context. Anything already loaded in the game's context (the game, GodotSharp, Serilog,
/// 0Harmony, the loader) is shared so types and Harmony state match; other files next to the mod are
/// loaded privately; the framework falls through to the default context.
/// </summary>
internal sealed class ModLoadContext(string name) : AssemblyLoadContext(name, isCollectible: false)
{
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        Assembly? shared = Loader.HostContext.Assemblies.FirstOrDefault(a =>
            string.Equals(a.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase));
        if (shared != null) return shared;

        try { return Loader.HostContext.LoadFromAssemblyName(assemblyName); } catch { }

        string local = Path.Combine(Loader.ModsDir, assemblyName.Name + ".dll");
        return File.Exists(local) ? LoadFromStream(new MemoryStream(File.ReadAllBytes(local))) : null;
    }
}
