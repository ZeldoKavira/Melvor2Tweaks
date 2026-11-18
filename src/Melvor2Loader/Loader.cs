using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using GamesByMalcs.Melvor2;
using Godot;

namespace Melvor2Loader;

/// <summary>
/// Stable mod loader with hot reload. Called once by the patched game DLL (Melvor2Patcher) from
/// GodotPlugins.Game.Main.InitializeFromGameProject - Godot is NOT usable yet at that point, so Init
/// only wires up assembly resolution and one Harmony hook on Main._Ready.
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

    public static void Init(Assembly gameAssembly)
    {
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
