using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Hooks Melvor2Tweaks into Melvor Idle 2 without touching the game's own code.
//
// The game DLL (GamesByMalcs.Melvor2.dll) changes with every game update, and Steam replaces it each time.
// So instead this adds a module initializer to two third-party libraries the game ships and loads early -
// Serilog.dll (the game's logger, created in Main's constructor) and SmartFormat.dll (set up by an autoload
// before the main scene) - which Steam leaves alone unless they change. The initializer loads
// <root>/mods/Melvor2Loader.dll into the game's AssemblyLoadContext and calls Melvor2Loader.Loader.Boot();
// whichever library loads first wins, the loader ignores the second call. If an update does replace one of
// them, the other still loads the mod, which then runs this patcher again (see Loader.SelfHeal).
//
// <root> is the folder holding the game's data_GamesByMalcs.Melvor2_<platform> folder: the game folder on
// Windows and Linux, "Melvor Idle 2.app/Contents/Resources" on macOS. Mods live in <root>/mods.
//
// Safe to run while the game is open: patched files are written beside the originals and swapped in (on
// Windows an in-use DLL is renamed out of the way first); they take effect on the next start.
// Writes <root>/mods/patch.stamp ("<file>=<md5>" per patched file, then "patch=<PatchVersion>").
//
// Usage: Melvor2Patcher [root] [--restore] [--check]
//   root defaults to two folders above this executable (it ships in <root>/mods/patcher/).

internal static class Program
{
    /// <summary>Libraries that get the hook.</summary>
    private static readonly string[] Targets = { "Serilog.dll", "SmartFormat.dll" };
    /// <summary>Earlier versions patched the game DLL itself; that patch is undone.</summary>
    private const string LegacyGameDll = "GamesByMalcs.Melvor2.dll";
    private const string BootstrapName = "__Melvor2ModBootstrap";
    /// <summary>Bump when the injected code changes, so installs re-patch.</summary>
    internal const int PatchVersion = 2;

    private static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception e) { Console.Error.WriteLine("Melvor2Patcher failed: " + e); return 1; }
    }

    private static int Run(string[] args)
    {
        string root = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--"))
            ?? Path.Combine(AppContext.BaseDirectory, "..", ".."));
        string? dataDir = FindDataDir(root);
        if (dataDir == null) { Console.Error.WriteLine($"No data_GamesByMalcs.Melvor2_* folder with {LegacyGameDll} under {root}"); return 2; }
        string stamp = Path.Combine(root, "mods", "patch.stamp");
        CleanOld(dataDir);

        if (args.Contains("--check"))
        {
            var missing = Targets.Where(t => !IsPatched(Path.Combine(dataDir, t), dataDir)).ToList();
            Console.WriteLine(missing.Count == 0 ? "patched" : "not patched: " + string.Join(", ", missing));
            return missing.Count == 0 ? 0 : 3;
        }

        if (args.Contains("--restore"))
        {
            foreach (var t in Targets.Append(LegacyGameDll)) Restore(Path.Combine(dataDir, t), dataDir);
            TryDelete(stamp);
            return 0;
        }

        // The old hook in the game DLL would load the loader a second time: put the original back.
        Restore(Path.Combine(dataDir, LegacyGameDll), dataDir);

        var lines = new List<string>();
        foreach (var t in Targets)
        {
            string dll = Path.Combine(dataDir, t);
            if (!File.Exists(dll)) { Console.Error.WriteLine($"Not found: {dll}"); return 1; }
            PatchFile(dll, dataDir);
            lines.Add($"{t}={Md5(dll)}");
        }
        lines.Add($"patch={PatchVersion}");
        Directory.CreateDirectory(Path.GetDirectoryName(stamp)!);
        File.WriteAllText(stamp, string.Join("\n", lines) + "\n");
        Console.WriteLine($"Mods load from {Path.Combine(root, "mods")}");
        return 0;
    }

    /// <summary>
    /// Patches one library. Unpatched = pristine (fresh install or the game updated it): refresh the backup
    /// from it. Patched = re-patch from the backup, so a changed hook can be re-applied safely.
    /// </summary>
    private static void PatchFile(string dll, string dataDir)
    {
        string backup = dll + ".orig";
        if (IsPatched(dll, dataDir))
        {
            if (!File.Exists(backup)) throw new Exception($"{Path.GetFileName(dll)} is patched but its .orig backup is missing; verify the game files in Steam and run this again.");
        }
        else
        {
            File.Copy(dll, backup, overwrite: true);
        }

        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(dataDir);
        string temp = dll + ".new";
        using (var asm = AssemblyDefinition.ReadAssembly(new MemoryStream(File.ReadAllBytes(backup)),
                   new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate }))
        {
            AddModuleInitializer(asm.MainModule, dataDir);
            asm.Write(temp);
        }
        Replace(temp, dll, copy: false);
        Console.WriteLine($"Patched {dll}");
    }

    private static void Restore(string dll, string dataDir)
    {
        string backup = dll + ".orig";
        if (File.Exists(dll) && File.Exists(backup) && IsPatched(dll, dataDir))
        {
            Replace(backup, dll, copy: true);
            Console.WriteLine($"Restored {Path.GetFileName(dll)}");
        }
        TryDelete(backup);
    }

    /// <summary>The data_GamesByMalcs.Melvor2_* folder (any platform) that holds the game.</summary>
    private static string? FindDataDir(string root)
    {
        if (!Directory.Exists(root)) return null;
        return Directory.GetDirectories(root, "data_GamesByMalcs.Melvor2_*")
            .FirstOrDefault(d => File.Exists(Path.Combine(d, LegacyGameDll)));
    }

    /// <summary>
    /// Puts <paramref name="source"/> at <paramref name="target"/>. A DLL the running game has loaded can't be
    /// overwritten on Windows but can be renamed, so on failure the old file is moved aside first
    /// (deleted on a later run).
    /// </summary>
    private static void Replace(string source, string target, bool copy)
    {
        string staged = copy ? target + ".new" : source;
        if (copy) File.Copy(source, staged, overwrite: true);
        try { File.Move(staged, target, overwrite: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            File.Move(target, target + ".old" + DateTime.UtcNow.Ticks);
            File.Move(staged, target);
        }
    }

    private static void CleanOld(string dataDir)
    {
        foreach (var t in Targets.Append(LegacyGameDll))
        {
            foreach (var f in Directory.GetFiles(dataDir, t + ".old*")) TryDelete(f);
            TryDelete(Path.Combine(dataDir, t + ".new"));
        }
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    private static string Md5(string path)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        using var f = File.OpenRead(path);
        return Convert.ToHexString(md5.ComputeHash(f)).ToLowerInvariant();
    }

    private static bool IsPatched(string dll, string dataDir)
    {
        if (!File.Exists(dll)) return false;
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(dataDir);
        using var asm = AssemblyDefinition.ReadAssembly(new MemoryStream(File.ReadAllBytes(dll)),
            new ReaderParameters { AssemblyResolver = resolver });
        var module = asm.MainModule;
        if (module.GetType("<Module>")?.Methods.Any(m => m.Name == BootstrapName) == true) return true;
        // Older versions hooked the game's entry point instead.
        return module.GetType("GodotPlugins.Game.Main")?.Methods.Any(m => m.Name == BootstrapName) == true;
    }

    /// <summary>
    /// Adds to the module:
    /// <code>
    /// static void __Melvor2ModBootstrap() {
    ///   try {
    ///     Assembly self = typeof(SomeTypeInThisModule).Assembly;
    ///     string path = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(self.Location)), "mods", "Melvor2Loader.dll");
    ///     if (File.Exists(path))
    ///       AssemblyLoadContext.GetLoadContext(self).LoadFromAssemblyPath(path)
    ///         .GetType("Melvor2Loader.Loader", true).GetMethod("Boot").Invoke(null, null);
    ///   } catch (Exception e) { Console.Error.WriteLine(e); }
    /// }
    /// </code>
    /// and calls it first thing in the module initializer (&lt;Module&gt;..cctor), which the runtime runs before
    /// any other code in the library. When the other library already loaded the loader, LoadFromAssemblyPath
    /// throws (same name, same context) and the catch swallows it.
    /// </summary>
    private static void AddModuleInitializer(ModuleDefinition module, string dataDir)
    {
        TypeDefinition moduleType = module.GetType("<Module>") ?? throw new Exception("<Module> type not found");
        TypeDefinition anchorType = module.Types.FirstOrDefault(t => t.Name != "<Module>" && !t.HasGenericParameters && t.IsClass)
            ?? throw new Exception("no type to anchor typeof() on");

        // References scoped to the game's own runtime assemblies (works whether the library targets
        // netstandard or net*).
        AssemblyNameReference Ref(string name)
        {
            var existing = module.AssemblyReferences.FirstOrDefault(r => r.Name == name);
            if (existing != null) return existing;
            var def = AssemblyDefinition.ReadAssembly(Path.Combine(dataDir, name + ".dll")).Name;
            var added = new AssemblyNameReference(def.Name, def.Version) { PublicKeyToken = def.PublicKeyToken };
            module.AssemblyReferences.Add(added);
            return added;
        }
        var runtime = Ref("System.Runtime");
        var loader = Ref("System.Runtime.Loader");
        var console = Ref("System.Console");

        TypeReference T(AssemblyNameReference scope, string ns, string name) => new TypeReference(ns, name, module, scope);
        var tVoid = module.TypeSystem.Void;
        var tString = module.TypeSystem.String;
        var tObject = module.TypeSystem.Object;
        var tBool = module.TypeSystem.Boolean;
        var tType = T(runtime, "System", "Type");
        var tRth = T(runtime, "System", "RuntimeTypeHandle"); tRth.IsValueType = true;
        var tAssembly = T(runtime, "System.Reflection", "Assembly");
        var tMethodInfo = T(runtime, "System.Reflection", "MethodInfo");
        var tMethodBase = T(runtime, "System.Reflection", "MethodBase");
        var tPath = T(runtime, "System.IO", "Path");
        var tFile = T(runtime, "System.IO", "File");
        var tException = T(runtime, "System", "Exception");
        var tAlc = T(loader, "System.Runtime.Loader", "AssemblyLoadContext");
        var tConsole = T(console, "System", "Console");
        var tTextWriter = T(runtime, "System.IO", "TextWriter");
        // Corlib-typed signatures must point at System.Runtime too, not at the library's own corlib reference.
        var rVoid = T(runtime, "System", "Void"); rVoid.IsValueType = true;
        var rString = T(runtime, "System", "String");
        var rObject = T(runtime, "System", "Object");
        var rBool = T(runtime, "System", "Boolean"); rBool.IsValueType = true;

        MethodReference M(TypeReference decl, string name, TypeReference ret, bool hasThis, params TypeReference[] ps)
        {
            var m = new MethodReference(name, ret, decl) { HasThis = hasThis };
            foreach (var p in ps) m.Parameters.Add(new ParameterDefinition(p));
            return m;
        }

        var getTypeFromHandle = M(tType, "GetTypeFromHandle", tType, false, tRth);
        var getAssembly = M(tType, "get_Assembly", tAssembly, true);
        var getLocation = M(tAssembly, "get_Location", rString, true);
        var getDirName = M(tPath, "GetDirectoryName", rString, false, rString);
        var combine3 = M(tPath, "Combine", rString, false, rString, rString, rString);
        var fileExists = M(tFile, "Exists", rBool, false, rString);
        var getLoadContext = M(tAlc, "GetLoadContext", tAlc, false, tAssembly);
        var loadFromPath = M(tAlc, "LoadFromAssemblyPath", tAssembly, true, rString);
        var asmGetType = M(tAssembly, "GetType", tType, true, rString, rBool);
        var typeGetMethod = M(tType, "GetMethod", tMethodInfo, true, rString);
        var invoke = M(tMethodBase, "Invoke", rObject, true, rObject, new ArrayType(rObject));
        var getError = M(tConsole, "get_Error", tTextWriter, false);
        var writeLine = M(tTextWriter, "WriteLine", rVoid, true, rObject);

        var boot = new MethodDefinition(BootstrapName, MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, tVoid);
        boot.Body.InitLocals = true;
        var vSelf = new VariableDefinition(tAssembly);
        var vPath = new VariableDefinition(tString);
        var vEx = new VariableDefinition(tException);
        boot.Body.Variables.Add(vSelf);
        boot.Body.Variables.Add(vPath);
        boot.Body.Variables.Add(vEx);
        var il = boot.Body.GetILProcessor();
        var ret = Instruction.Create(OpCodes.Ret);

        var tryStart = Instruction.Create(OpCodes.Ldtoken, anchorType);
        il.Append(tryStart);
        il.Emit(OpCodes.Call, getTypeFromHandle);
        il.Emit(OpCodes.Callvirt, getAssembly);
        il.Emit(OpCodes.Stloc, vSelf);
        il.Emit(OpCodes.Ldloc, vSelf);
        il.Emit(OpCodes.Callvirt, getLocation);
        il.Emit(OpCodes.Call, getDirName);
        il.Emit(OpCodes.Call, getDirName);
        il.Emit(OpCodes.Ldstr, "mods");
        il.Emit(OpCodes.Ldstr, "Melvor2Loader.dll");
        il.Emit(OpCodes.Call, combine3);
        il.Emit(OpCodes.Stloc, vPath);
        il.Emit(OpCodes.Ldloc, vPath);
        il.Emit(OpCodes.Call, fileExists);
        var leaveTry = Instruction.Create(OpCodes.Leave, ret);
        il.Emit(OpCodes.Brfalse, leaveTry);
        il.Emit(OpCodes.Ldloc, vSelf);
        il.Emit(OpCodes.Call, getLoadContext);
        il.Emit(OpCodes.Ldloc, vPath);
        il.Emit(OpCodes.Callvirt, loadFromPath);
        il.Emit(OpCodes.Ldstr, "Melvor2Loader.Loader");
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Callvirt, asmGetType);
        il.Emit(OpCodes.Ldstr, "Boot");
        il.Emit(OpCodes.Callvirt, typeGetMethod);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Callvirt, invoke);
        il.Emit(OpCodes.Pop);
        il.Append(leaveTry);

        var storeEx = Instruction.Create(OpCodes.Stloc, vEx);
        il.Append(storeEx);
        il.Emit(OpCodes.Call, getError);
        il.Emit(OpCodes.Ldloc, vEx);
        il.Emit(OpCodes.Callvirt, writeLine);
        il.Append(Instruction.Create(OpCodes.Leave, ret));
        il.Append(ret);

        boot.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            CatchType = tException, TryStart = tryStart, TryEnd = storeEx, HandlerStart = storeEx, HandlerEnd = ret,
        });
        moduleType.Methods.Add(boot);

        // Call it first thing in the module initializer (create one if the library has none).
        var cctor = moduleType.Methods.FirstOrDefault(m => m.IsConstructor && m.IsStatic);
        if (cctor == null)
        {
            cctor = new MethodDefinition(".cctor",
                MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig |
                MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, tVoid);
            cctor.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
            moduleType.Methods.Add(cctor);
        }
        var first = cctor.Body.Instructions[0];
        cctor.Body.GetILProcessor().InsertBefore(first, Instruction.Create(OpCodes.Call, boot));
    }
}
