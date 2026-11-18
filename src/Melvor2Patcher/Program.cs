using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Adds one guarded call to the game's Godot entry point (GodotPlugins.Game.Main.InitializeFromGameProject)
// that loads <GameDir>\mods\Melvor2Loader.dll into the game's AssemblyLoadContext and calls
// Melvor2Loader.Loader.Init(Assembly gameAssembly). Run again after every game update.
//
// Usage: Melvor2Patcher [gameDir] [--restore]

internal static class Program
{
    private const string GameDll = "GamesByMalcs.Melvor2.dll";
    private const string DataDirName = "data_GamesByMalcs.Melvor2_windows_x86_64";
    private const string BootstrapName = "__Melvor2ModBootstrap";

    private static int Main(string[] args)
    {
        string gameDir = args.FirstOrDefault(a => !a.StartsWith("--"))
            ?? @"C:\Program Files (x86)\Steam\steamapps\common\Melvor Idle 2";
        string dataDir = Path.Combine(gameDir, DataDirName);
        string dll = Path.Combine(dataDir, GameDll);
        string backup = dll + ".orig";

        if (!File.Exists(dll)) { Console.Error.WriteLine($"Not found: {dll}"); return 1; }

        if (args.Contains("--restore"))
        {
            if (!File.Exists(backup)) { Console.Error.WriteLine("No backup to restore."); return 1; }
            File.Copy(backup, dll, overwrite: true);
            Console.WriteLine("Restored original game DLL.");
            return 0;
        }

        // Unpatched DLL = pristine (fresh install or game update): refresh the backup from it.
        // Patched DLL = re-patch from the backup, so the patch can be changed and re-applied safely.
        if (IsPatched(dll, dataDir))
        {
            if (!File.Exists(backup)) { Console.Error.WriteLine("DLL is patched but the .orig backup is missing; verify game files in Steam."); return 1; }
            Console.WriteLine("Already patched - re-applying from backup.");
        }
        else
        {
            File.Copy(dll, backup, overwrite: true);
        }

        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(dataDir);
        using (var asm = AssemblyDefinition.ReadAssembly(new MemoryStream(File.ReadAllBytes(backup)),
                   new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate }))
        {
            Patch(asm.MainModule);
            asm.Write(dll);
        }
        Console.WriteLine($"Patched {dll}");
        Console.WriteLine($"Backup at {backup}");
        Console.WriteLine($"Mods load from {Path.Combine(gameDir, "mods")}");
        return 0;
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
