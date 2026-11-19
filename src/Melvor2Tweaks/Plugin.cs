using System;
using Godot;

namespace Melvor2Tweaks;

/// <summary>
/// Mod entry points, called by Melvor2Loader on the main thread once the game's Main node is ready,
/// and again on every hot reload (old copy's ModUnload, then the new copy's ModLoad). The mod may be
/// loaded mid-game, so everything here must attach to existing state and ModUnload must undo it all.
/// </summary>
public static class Plugin
{
    public const string Version = "0.10.2";
    public const string HarmonyId = "net.zeldo.melvor2tweaks";

    private static HarmonyLib.Harmony? s_harmony;

    public static void ModLoad()
    {
        Settings.Load();
        TimeBank.TimeBankStore.Load();
        Actions.ActionQueue.Load();
        Actions.ActionLog.Load();

        s_harmony = new HarmonyLib.Harmony(HarmonyId);
        s_harmony.PatchAll(typeof(Plugin).Assembly);
        int count = 0;
        foreach (var _ in s_harmony.GetPatchedMethods()) count++;
        Log($"v{Version}: Harmony patched {count} method(s)");

        TimeBank.TimeBank.AttachToRunningGame();
        try { UI.TopBar.AttachToExisting(); } catch (Exception e) { Log("Top bar: " + e); }
        try { UI.ActionsMenu.AttachToExisting(); } catch (Exception e) { Log("Actions menu: " + e); }
        try { UI.ItemsMenu.AttachToExisting(); } catch (Exception e) { Log("Items menu: " + e); }
        try { Actions.ActionQueue.Rearm(); } catch (Exception e) { Log("Queue rearm: " + e); }
        try { UI.SettingsTab.AttachToExisting(); } catch (Exception e) { Log("Settings tab: " + e); }
        try { UI.FarmingButtons.AttachToExisting(); } catch (Exception e) { Log("Farming buttons: " + e); }
        try { Features.ItemRules.AttachToExisting(); } catch (Exception e) { Log("Item rules: " + e); }
    }

    public static void ModUnload()
    {
        try { UI.TopBar.Detach(); } catch (Exception e) { Log("Top bar teardown: " + e.Message); }
        try { UI.ActionsMenu.Detach(); } catch (Exception e) { Log("Actions menu teardown: " + e.Message); }
        try { UI.ItemsMenu.Detach(); } catch (Exception e) { Log("Items menu teardown: " + e.Message); }
        try { Actions.ActionQueue.Shutdown(); } catch (Exception e) { Log("Queue teardown: " + e.Message); }
        try { Actions.ActionLog.Shutdown(); } catch (Exception e) { Log("Action log teardown: " + e.Message); }
        try { Actions.QueueMode.Shutdown(); } catch (Exception e) { Log("Queue mode teardown: " + e.Message); }
        try { UI.SettingsTab.DetachAll(); } catch (Exception e) { Log("Settings tab teardown: " + e.Message); }
        try { UI.FarmingButtons.DetachAll(); } catch (Exception e) { Log("Farming buttons teardown: " + e.Message); }
        try { Features.ItemRules.DetachAll(); } catch (Exception e) { Log("Item rules teardown: " + e.Message); }
        s_harmony?.UnpatchAll(HarmonyId);
        s_harmony = null;
        TimeBank.TimeBank.State = null;
        Log("unloaded");
    }

    // Goes to mods\Melvor2Loader.log (written immediately) and godot.log (buffered by Godot in release builds).
    public static void Log(string message) => Melvor2Loader.Loader.Log("[Melvor2Tweaks] " + message);
}
