using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Adds one guarded call to the game's Godot entry point (GodotPlugins.Game.Main.InitializeFromGameProject)
// that loads <root>/mods/Melvor2Loader.dll into the game's AssemblyLoadContext and calls
// Melvor2Loader.Loader.Init(Assembly gameAssembly). <root> is the folder holding the game's
// data_GamesByMalcs.Melvor2_<platform> folder: the game folder on Windows and Linux, and
// "Melvor Idle 2.app/Contents/Resources" on macOS.
//
// Safe to run while the game is open: the patched DLL is written beside the original and swapped in
// (on Windows an in-use DLL is renamed out of the way first); it takes effect on the next start.
// Writes <root>/mods/patch.stamp ("<md5 of the patched dll>\n<PatchVersion>") so the startup script
// (mods/melvor2_boot.gd) can tell when a game update has replaced the DLL and re-run this.
//
// Usage: Melvor2Patcher [root] [--restore] [--check]
//   root defaults to two folders above this executable (it ships in <root>/mods/patcher/).

internal static class Program
{
    private const string GameDll = "GamesByMalcs.Melvor2.dll";
    private const string BootstrapName = "__Melvor2ModBootstrap";
    /// <summary>Bump when the injected code changes, so installs re-patch.</summary>
    internal const int PatchVersion = 1;

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
        if (dataDir == null) { Console.Error.WriteLine($"No data_GamesByMalcs.Melvor2_* folder with {GameDll} under {root}"); return 2; }
        string dll = Path.Combine(dataDir, GameDll);
        string backup = dll + ".orig";
        string stamp = Path.Combine(root, "mods", "patch.stamp");
        CleanOld(dataDir);

        if (args.Contains("--check"))
        {
            bool ok = IsPatched(dll, dataDir);
            Console.WriteLine(ok ? "patched" : "not patched");
            return ok ? 0 : 3;
        }

        if (args.Contains("--restore"))
        {
            if (File.Exists(backup) && IsPatched(dll, dataDir))
            {
                Replace(backup, dll, copy: true);
                Console.WriteLine("Restored the original game DLL.");
            }
            else Console.WriteLine("Game DLL is not patched; nothing to restore.");
            TryDelete(backup);
            TryDelete(stamp);
            return 0;
        }

        // Unpatched DLL = pristine (fresh install or game update): refresh the backup from it.
        // Patched DLL = re-patch from the backup, so the patch can be changed and re-applied safely.
        if (IsPatched(dll, dataDir))
        {
            if (!File.Exists(backup)) { Console.Error.WriteLine("The DLL is patched but the .orig backup is missing; verify the game files in Steam and run this again."); return 1; }
            Console.WriteLine("Already patched - re-applying from the backup.");
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
            Patch(asm.MainModule);
            asm.Write(temp);
        }
        Replace(temp, dll, copy: false);

        Directory.CreateDirectory(Path.GetDirectoryName(stamp)!);
        File.WriteAllText(stamp, Md5(dll) + "\n" + PatchVersion + "\n");
        Console.WriteLine($"Patched {dll}");
        Console.WriteLine($"Mods load from {Path.Combine(root, "mods")}");
        return 0;
    }

    /// <summary>The data_GamesByMalcs.Melvor2_* folder (any platform) that holds the game DLL.</summary>
    private static string? FindDataDir(string root)
    {
        if (!Directory.Exists(root)) return null;
        return Directory.GetDirectories(root, "data_GamesByMalcs.Melvor2_*")
            .FirstOrDefault(d => File.Exists(Path.Combine(d, GameDll)));
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
        foreach (var f in Directory.GetFiles(dataDir, GameDll + ".old*")) TryDelete(f);
        TryDelete(Path.Combine(dataDir, GameDll + ".new"));
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
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(dataDir);
        using var asm = AssemblyDefinition.ReadAssembly(new MemoryStream(File.ReadAllBytes(dll)),
            new ReaderParameters { AssemblyResolver = resolver });
        return asm.MainModule.GetType("GodotPlugins.Game.Main")?.Methods.Any(m => m.Name == BootstrapName) == true;
    }

    private static void Patch(ModuleDefinition module)
    {
        TypeDefinition main = module.GetType("GodotPlugins.Game.Main")
            ?? throw new Exception("GodotPlugins.Game.Main not found - game layout changed.");
        MethodDefinition init = main.Methods.Single(m => m.Name == "InitializeFromGameProject");

        // --- references, scoped to the game's own framework assemblies -------------------------------
        AssemblyNameReference runtime = module.AssemblyReferences.First(r => r.Name == "System.Runtime");
        AssemblyNameReference loader = GetOrAddRef(module, "System.Runtime.Loader", runtime.Version);
        AssemblyNameReference console = GetOrAddRef(module, "System.Console", runtime.Version);

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

        MethodReference M(TypeReference decl, string name, TypeReference ret, bool hasThis, params TypeReference[] ps)
        {
            var m = new MethodReference(name, ret, decl) { HasThis = hasThis };
            foreach (var p in ps) m.Parameters.Add(new ParameterDefinition(p));
            return m;
        }

        var getTypeFromHandle = M(tType, "GetTypeFromHandle", tType, false, tRth);
        var getAssembly = M(tType, "get_Assembly", tAssembly, true);
        var getLocation = M(tAssembly, "get_Location", tString, true);
        var getDirName = M(tPath, "GetDirectoryName", tString, false, tString);
        var combine3 = M(tPath, "Combine", tString, false, tString, tString, tString);
        var fileExists = M(tFile, "Exists", tBool, false, tString);
        var getLoadContext = M(tAlc, "GetLoadContext", tAlc, false, tAssembly);
        var loadFromPath = M(tAlc, "LoadFromAssemblyPath", tAssembly, true, tString);
        var asmGetType = M(tAssembly, "GetType", tType, true, tString, tBool);
        var typeGetMethod = M(tType, "GetMethod", tMethodInfo, true, tString);
        var invoke = M(tMethodBase, "Invoke", tObject, true, tObject, new ArrayType(tObject));
        var getError = M(tConsole, "get_Error", tTextWriter, false);
        var writeLine = M(tTextWriter, "WriteLine", tVoid, true, tObject);

        // --- static void __Melvor2ModBootstrap() ------------------------------------------------------
        // try {
        //   Assembly game = typeof(Main).Assembly;
        //   string path = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(game.Location)), "mods", "Melvor2Loader.dll");
        //   if (File.Exists(path))
        //     AssemblyLoadContext.GetLoadContext(game).LoadFromAssemblyPath(path)
        //       .GetType("Melvor2Loader.Loader", true).GetMethod("Init").Invoke(null, new object[] { game });
        // } catch (Exception e) { Console.Error.WriteLine(e); }
        var boot = new MethodDefinition(BootstrapName, MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, tVoid);
        boot.Body.InitLocals = true;
        var vGame = new VariableDefinition(tAssembly);
        var vPath = new VariableDefinition(tString);
        boot.Body.Variables.Add(vGame);
        boot.Body.Variables.Add(vPath);
        var il = boot.Body.GetILProcessor();

        var ret = Instruction.Create(OpCodes.Ret);
        var leaveTarget = ret;

        var tryStart = Instruction.Create(OpCodes.Ldtoken, main);
        il.Append(tryStart);
        il.Emit(OpCodes.Call, getTypeFromHandle);
        il.Emit(OpCodes.Callvirt, getAssembly);
        il.Emit(OpCodes.Stloc, vGame);
        il.Emit(OpCodes.Ldloc, vGame);
        il.Emit(OpCodes.Callvirt, getLocation);
        il.Emit(OpCodes.Call, getDirName);
        il.Emit(OpCodes.Call, getDirName);
        il.Emit(OpCodes.Ldstr, "mods");
        il.Emit(OpCodes.Ldstr, "Melvor2Loader.dll");
        il.Emit(OpCodes.Call, combine3);
        il.Emit(OpCodes.Stloc, vPath);
        il.Emit(OpCodes.Ldloc, vPath);
        il.Emit(OpCodes.Call, fileExists);
        var leaveTry = Instruction.Create(OpCodes.Leave, leaveTarget);
        il.Emit(OpCodes.Brfalse, leaveTry);
        il.Emit(OpCodes.Ldloc, vGame);
        il.Emit(OpCodes.Call, getLoadContext);
        il.Emit(OpCodes.Ldloc, vPath);
        il.Emit(OpCodes.Callvirt, loadFromPath);
        il.Emit(OpCodes.Ldstr, "Melvor2Loader.Loader");
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Callvirt, asmGetType);
        il.Emit(OpCodes.Ldstr, "Init");
        il.Emit(OpCodes.Callvirt, typeGetMethod);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, tObject);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc, vGame);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Callvirt, invoke);
        il.Emit(OpCodes.Pop);
        il.Append(leaveTry);

        // catch (Exception e) { Console.Error.WriteLine(e); }
        var vEx = new VariableDefinition(tException);
        boot.Body.Variables.Add(vEx);
        var storeEx = Instruction.Create(OpCodes.Stloc, vEx);
        il.Append(storeEx);
        il.Emit(OpCodes.Call, getError);
        il.Emit(OpCodes.Ldloc, vEx);
        il.Emit(OpCodes.Callvirt, writeLine);
        var leaveHandler = Instruction.Create(OpCodes.Leave, leaveTarget);
        il.Append(leaveHandler);
        il.Append(ret);

        boot.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            CatchType = tException,
            TryStart = tryStart,
            TryEnd = storeEx,
            HandlerStart = storeEx,
            HandlerEnd = ret,
        });
        main.Methods.Add(boot);

        // --- call it right after ScriptManagerBridge.LookupScriptsInAssembly(...) ----------------------
        var anchor = init.Body.Instructions.FirstOrDefault(i =>
            i.OpCode == OpCodes.Call && i.Operand is MethodReference mr && mr.Name == "LookupScriptsInAssembly")
            ?? throw new Exception("LookupScriptsInAssembly call not found - game layout changed.");
        init.Body.GetILProcessor().InsertAfter(anchor, Instruction.Create(OpCodes.Call, boot));
    }

    private static AssemblyNameReference GetOrAddRef(ModuleDefinition module, string name, Version version)
    {
        var existing = module.AssemblyReferences.FirstOrDefault(r => r.Name == name);
        if (existing != null) return existing;
        var runtime = module.AssemblyReferences.First(r => r.Name == "System.Runtime");
        var added = new AssemblyNameReference(name, version) { PublicKeyToken = runtime.PublicKeyToken };
        module.AssemblyReferences.Add(added);
        return added;
    }
}
