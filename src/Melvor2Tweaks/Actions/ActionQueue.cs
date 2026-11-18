using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Actions;
using GamesByMalcs.Melvor2.Components.Experience;
using GamesByMalcs.Melvor2.Components.Inventories;
using GamesByMalcs.Melvor2.Entities.Combat;
using GamesByMalcs.Melvor2.Saves;
using HarmonyLib;

namespace Melvor2Tweaks.Actions;

public enum StopKind { None, Level, Items }

public sealed class QueueEntry
{
    public ActionTarget Target { get; set; } = new();
    public StopKind Stop { get; set; }
    public int StopValue { get; set; }

    public string Describe() => Target.Label + Stop switch
    {
        StopKind.Level => $" - until level {StopValue}",
        StopKind.Items => $" - for {StopValue:N0} items",
        _ => " - until it stops",
    };
}

public sealed class QueueState
{
    public List<QueueEntry> Entries { get; set; } = new();
    public bool Enabled { get; set; } = true;
    /// <summary>Stop rule on the running action (survives reloads; item progress restarts at 0).</summary>
    public QueueEntry? Current { get; set; }
}

/// <summary>
/// "Until X" for any action plus an action queue. Rules stop the running action through the game's own
/// QueueOrTryStop. When an action stops on its own (rule met, out of materials, bank full, no traps,
/// death) the next queued entry starts at the beginning of the next game tick - never from inside the
/// stopping action's code, and also during offline catch-up. The player's own Stop button, or starting
/// another action, does not advance the queue.
/// </summary>
internal static class ActionQueue
{
    private static Dictionary<string, QueueState> s_states = new();
    private static string FilePath => Path.Combine(Settings.DataDir, "queue.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static event Action? Changed;

    private static readonly Func<Guid> s_activeSaveId =
        AccessTools.MethodDelegate<Func<Guid>>(AccessTools.PropertyGetter(typeof(SaveManager), "ActiveSaveId"));

    internal static QueueState? State
    {
        get
        {
            Guid id = s_activeSaveId();
            if (id == Guid.Empty) return null;
            if (!s_states.TryGetValue(id.ToString(), out var s)) s_states[id.ToString()] = s = new QueueState();
            return s;
        }
    }

    internal static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                s_states = JsonSerializer.Deserialize<Dictionary<string, QueueState>>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch (Exception e) { Plugin.Log("queue.json unreadable: " + e.Message); s_states = new(); }
    }

    internal static void Save()
    {
        try
        {
            Directory.CreateDirectory(Settings.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(s_states, Json));
        }
        catch (Exception e) { Plugin.Log("Could not save queue: " + e.Message); }
        Changed?.Invoke();
    }

    internal static ActionComponent? RunningAction =>
        TimeBank.TimeBank.Game?.ActionManager.ActiveActions.FirstOrDefault();

    // ---- rules on the running action ---------------------------------------------------------------
    private static ActionComponent? s_armedAction;
    private static QueueEntry? s_armedEntry;
    private static ExperienceComponent? s_armedXp;
    private static int s_itemCount;

    internal static int ItemProgress => s_itemCount;
    internal static int? CurrentLevel => s_armedXp?.Level;

    internal static void SetRuleOnRunning(StopKind kind, int value)
    {
        var state = State;
        var action = RunningAction;
        if (state == null || action == null) return;
        var target = SkillActions.Capture(action);
        if (target == null) return;
        state.Current = kind == StopKind.None ? null : new QueueEntry { Target = target, Stop = kind, StopValue = value };
        Arm(action, state.Current);
        Save();
    }

    private static void Arm(ActionComponent action, QueueEntry? entry)
    {
        Disarm();
        if (entry == null || entry.Stop == StopKind.None) return;
        s_armedAction = action;
        s_armedEntry = entry;
        s_itemCount = 0;
        if (entry.Stop == StopKind.Level)
        {
            s_armedXp = action.Entity.Components.Values.OfType<ExperienceComponent>().FirstOrDefault();
            if (s_armedXp == null) { Plugin.Log("Stop rule: this skill has no level"); Disarm(); return; }
            s_armedXp.LevelChanged += OnLevelChanged;
            if (s_armedXp.Level >= entry.StopValue) RuleMet();
        }
        else
        {
            action.Inventory.ItemAdded += OnItemAdded;
        }
    }

    private static void Disarm()
    {
        if (s_armedXp != null) s_armedXp.LevelChanged -= OnLevelChanged;
        if (s_armedAction != null) s_armedAction.Inventory.ItemAdded -= OnItemAdded;
        s_armedAction = null; s_armedEntry = null; s_armedXp = null; s_itemCount = 0;
    }

    private static void OnLevelChanged(object? sender, GamesByMalcs.Melvor2.Core.ValueChangedEventArgs<int> e)
    {
        if (s_armedEntry != null && e.NewValue >= s_armedEntry.StopValue) RuleMet();
    }

    private static void OnItemAdded(object? sender, ItemAddedEventArgs e)
    {
        if (s_armedEntry == null || s_armedAction == null || e.Quantity <= 0) return;
        if (Features.SkillingTweaks.IsSideDrop(s_armedAction, e.Item)) return;
        s_itemCount += e.Quantity;
        if (s_itemCount >= s_armedEntry.StopValue) RuleMet();
    }

    private static void RuleMet()
    {
        var action = s_armedAction;
        Plugin.Log($"Stop rule met: {s_armedEntry?.Describe()}");
        Disarm();
        if (State is QueueState s) { s.Current = null; Save(); }
        action?.QueueOrTryStop();
    }

    // ---- queue editing -----------------------------------------------------------------------------
    internal static bool AddRunning(StopKind kind, int value, out string reason)
    {
        reason = "";
        var state = State;
        var action = RunningAction;
        var target = action == null ? null : SkillActions.Capture(action);
        if (state == null || target == null) { reason = "Start the action you want to queue first."; return false; }
        state.Entries.Add(new QueueEntry { Target = target, Stop = kind, StopValue = value });
        Save();
        return true;
    }

    internal static void RemoveAt(int i) { if (State is { } s && i >= 0 && i < s.Entries.Count) { s.Entries.RemoveAt(i); Save(); } }

    internal static void MoveUp(int i)
    {
        if (State is not { } s || i <= 0 || i >= s.Entries.Count) return;
        (s.Entries[i - 1], s.Entries[i]) = (s.Entries[i], s.Entries[i - 1]);
        Save();
    }

    internal static void Clear() { if (State is { } s) { s.Entries.Clear(); Save(); } }

    internal static void SetEnabled(bool on) { if (State is { } s) { s.Enabled = on; Save(); } }

    /// <summary>Unsubscribes from game events (hot unload: the game would keep calling this copy's handlers).</summary>
    internal static void Shutdown()
    {
        Disarm();
        s_pendingAdvance = false;
    }

    /// <summary>Re-arms a persisted rule when a save is opened or the mod is hot-loaded.</summary>
    internal static void Rearm()
    {
        Disarm();
        var current = State?.Current;
        var action = RunningAction;
        if (current == null || action == null) return;
        if (current.Target.SameAs(SkillActions.Capture(action))) Arm(action, current);
    }

    // ---- stop classification + advancing -----------------------------------------------------------
    private static int s_inQueueOrTryStop;
    private static int s_switching;
    internal static bool AutoStopHint;
    private static bool s_pendingAdvance;

    [HarmonyPatch(typeof(ActionComponent), nameof(ActionComponent.QueueOrTryStop))]
    private static class QueueOrTryStopPatch
    {
        private static void Prefix() => s_inQueueOrTryStop++;
        private static void Finalizer() => s_inQueueOrTryStop--;
    }

    [HarmonyPatch(typeof(ActiveActionManager), nameof(ActiveActionManager.TryStopActive))]
    private static class TryStopActivePatch
    {
        private static void Prefix() => s_switching++;
        private static void Finalizer() => s_switching--;
    }

    [HarmonyPatch(typeof(CombatActionComponent), "PlayerDiedHandler")]
    private static class PlayerDiedPatch
    {
        private static void Prefix() => AutoStopHint = true;
        private static void Finalizer() => AutoStopHint = false;
    }

    [HarmonyPatch(typeof(ActionComponent), nameof(ActionComponent.TryStop))]
    private static class TryStopPatch
    {
        private static readonly AccessTools.FieldRef<ActionComponent, bool>? s_performing =
            AccessTools.FieldRefAccess<ActionComponent, bool>("<IsPerformingAction>k__BackingField");

        private static void Prefix(ActionComponent __instance, bool force, out bool __state)
        {
            __state = s_switching == 0 && __instance.IsStarted &&
                      (force || __instance.QueueStop || s_inQueueOrTryStop > 0 || AutoStopHint ||
                       (s_performing?.Invoke(__instance) ?? false));
        }

        private static void Postfix(ActionComponent __instance, bool __result, bool __state)
        {
            if (!__result) return;
            if (ReferenceEquals(__instance, s_armedAction))
            {
                Disarm();
                if (State is QueueState cs) { cs.Current = null; Save(); }
            }
            if (__state && State is { Enabled: true } s && s.Entries.Count > 0) s_pendingAdvance = true;
        }
    }

    [HarmonyPatch(typeof(Game), nameof(Game.PropagateTick))]
    private static class TickPatch
    {
        private static void Prefix(Game __instance)
        {
            if (!s_pendingAdvance) return;
            s_pendingAdvance = false;
            Advance(__instance);
        }
    }

    private static void Advance(Game game)
    {
        var state = State;
        if (state == null || !state.Enabled) return;
        while (state.Entries.Count > 0)
        {
            var entry = state.Entries[0];
            state.Entries.RemoveAt(0);
            if (SkillActions.Start(game, entry.Target, out string reason))
            {
                Plugin.Log($"Queue: started {entry.Describe()}");
                state.Current = entry.Stop == StopKind.None ? null : entry;
                if (RunningAction is ActionComponent running) Arm(running, state.Current);
                Save();
                return;
            }
            Plugin.Log($"Queue: skipped {entry.Target.Label} ({reason})");
        }
        Save();
    }
}
