using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Actions;
using GamesByMalcs.Melvor2.Data;
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
    /// <summary>"For N items" counts only this item (gather entries), instead of the main products.</summary>
    public string? CountItemId { get; set; }
    /// <summary>Added by the queue to gather materials for the entry after it (not looped).</summary>
    public bool Auto { get; set; }
    /// <summary>The rest of an entry that ran out of materials part-way (not looped; the original was).</summary>
    public bool Resume { get; set; }
    public int GatherDepth { get; set; }
    public int GatherRounds { get; set; }

    public QueueEntry CopyForLoop() => new() { Target = Target, Stop = Stop, StopValue = StopValue, CountItemId = CountItemId };

    public string Describe() => (Auto ? "Gather: " : "") + Target.Label + Stop switch
    {
        StopKind.Level => $" - until level {StopValue}",
        StopKind.Items when CountItemId != null => $" - for {StopValue:N0} {ItemName(CountItemId)}",
        StopKind.Items => $" - for {StopValue:N0} items",
        _ => " - until it stops",
    };

    private static string ItemName(string id) =>
        TimeBank.TimeBank.Game?.Data.TryGetObject<GamesByMalcs.Melvor2.Data.Items.Item>(id, out var item) == true
            ? item.DisplayName.Plural : id;
}

public sealed class QueueState
{
    public List<QueueEntry> Entries { get; set; } = new();
    public bool Enabled { get; set; } = true;
    /// <summary>Each entry goes back on the end of the queue when it starts.</summary>
    public bool Loop { get; set; }
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
    private static string PresetsPath => Path.Combine(Settings.DataDir, "queue-presets.json");
    /// <summary>Saved queues by name (shared by all saves).</summary>
    internal static SortedDictionary<string, List<QueueEntry>> Presets { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static event Action? Changed;

    private static readonly Func<Guid> s_activeSaveId =
        AccessTools.MethodDelegate<Func<Guid>>(AccessTools.PropertyGetter(typeof(SaveManager), "ActiveSaveId"));

    internal static string? SaveKey { get { Guid id = s_activeSaveId(); return id == Guid.Empty ? null : id.ToString(); } }

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
        try
        {
            if (File.Exists(PresetsPath) &&
                JsonSerializer.Deserialize<Dictionary<string, List<QueueEntry>>>(File.ReadAllText(PresetsPath), Json) is { } p)
                Presets = new(p, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) { Plugin.Log("queue-presets.json unreadable: " + e.Message); }
    }

    private static void SavePresets()
    {
        try { File.WriteAllText(PresetsPath, JsonSerializer.Serialize(Presets, Json)); }
        catch (Exception e) { Plugin.Log("Could not save queue presets: " + e.Message); }
        Changed?.Invoke();
    }

    /// <summary>Saves the queue (without gather entries / leftovers) under a name.</summary>
    internal static bool SavePreset(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || State is not { } s) return false;
        Presets[name] = s.Entries.Where(e => !e.Auto && !e.Resume).Select(e => e.CopyForLoop()).ToList();
        SavePresets();
        return true;
    }

    /// <summary>Replaces the queue with a saved one.</summary>
    internal static void LoadPreset(string name)
    {
        if (State is not { } s || !Presets.TryGetValue(name, out var entries)) return;
        s.Entries = entries.Select(e => e.CopyForLoop()).ToList();
        Save();
    }

    internal static void DeletePreset(string name) { if (Presets.Remove(name)) SavePresets(); }

    internal static void SetLoop(bool on) { if (State is { } s) { s.Loop = on; Save(); } }

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
        // "Until it stops" on an entry the queue started keeps it tracked (so it can go back if overridden).
        state.Current = kind == StopKind.None && state.Current == null ? null : new QueueEntry { Target = target, Stop = kind, StopValue = value };
        Arm(action, state.Current);
        Save();
    }

    /// <summary>Puts a limit back on the running action (after Queue mode briefly switched away), keeping its progress.</summary>
    internal static void RestoreRule(QueueEntry entry, int progress)
    {
        var action = RunningAction;
        if (State is not { } state || action == null) return;
        state.Current = entry;
        Arm(action, entry);
        if (ReferenceEquals(s_armedEntry, entry)) s_itemCount = progress;
        Save();
    }

    private static void Arm(ActionComponent action, QueueEntry? entry)
    {
        Disarm();
        if (entry == null) return;
        s_armedAction = action;
        s_armedEntry = entry;
        s_itemCount = 0;
        if (entry.Stop == StopKind.None) return; // tracked (to put back if overridden), nothing to count
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
        if (s_armedEntry.CountItemId != null ? e.Item.Id != s_armedEntry.CountItemId
                                             : Features.SkillingTweaks.IsSideDrop(s_armedAction, e.Item)) return;
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
    internal static void Add(QueueEntry entry)
    {
        if (State is not { } state) return;
        state.Entries.Add(entry);
        Save();
    }

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
                // Stopped by itself before its limit: a crafting entry that ran out goes back on the
                // front of the queue, so it gathers what it's missing and carries on.
                if (__state && s_armedEntry is { } unfinished) ResumeLater(unfinished);
                // Another action was started over it (not by the queue or Queue mode): back in the queue.
                else if (!__state && s_switching > 0 && s_armedEntry is { } overridden && !QueueMode.Internal && !QueueMode.IsCapturing)
                    PutBack(overridden);
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

    /// <summary>
    /// The running queue entry was overridden by the player (another action started, or another target
    /// picked in the same skill): it goes back on the front of the queue with what it had left.
    /// </summary>
    private static void PutBack(QueueEntry entry)
    {
        if (State is not { } s) return;
        int left = entry.Stop == StopKind.Items ? entry.StopValue - s_itemCount : entry.StopValue;
        if (entry.Stop == StopKind.Items && left <= 0) return;
        s.Entries.Insert(0, new QueueEntry
        {
            Target = entry.Target, Stop = entry.Stop, StopValue = left, CountItemId = entry.CountItemId,
            Auto = entry.Auto, Resume = true, GatherDepth = entry.GatherDepth,
        });
        Plugin.Log($"Queue: {entry.Target.Label} was overridden, back in the queue ({s.Entries[0].Describe()})");
    }

    /// <summary>Same skill, another target (e.g. a different tree): the entry is overridden too.</summary>
    [HarmonyPatch(typeof(ActionComponent), nameof(ActionComponent.CurrentDescription), MethodType.Setter)]
    private static class SwitchTargetPatch
    {
        private static void Postfix(ActionComponent __instance, IDescribedObject? value)
        {
            if (value == null || !ReferenceEquals(__instance, s_armedAction) || s_armedEntry is not { } entry) return;
            if (QueueMode.Internal || QueueMode.IsActive || __instance is CombatActionComponent) return; // combat targets change by themselves (auto-slayer)
            if (!__instance.IsStarted || SkillActions.Capture(__instance) is not { } now || now.SameAs(entry.Target)) return;
            PutBack(entry);
            Disarm();
            if (State is QueueState cs) { cs.Current = null; Save(); }
        }
    }

    private static void ResumeLater(QueueEntry entry)
    {
        if (!Settings.Current.QueueGatherFirst || State is not { Enabled: true } s || TimeBank.TimeBank.Game is not { } game) return;
        if (!Gather.IsCrafting(game, entry.Target) || Gather.CraftsAffordable(game, entry.Target) is not 0) return;
        int left = entry.Stop == StopKind.Items ? entry.StopValue - s_itemCount : entry.StopValue;
        if (left <= 0) return;
        s.Entries.Insert(0, new QueueEntry
        {
            Target = entry.Target, Stop = entry.Stop, StopValue = left, CountItemId = entry.CountItemId,
            Auto = entry.Auto, Resume = true, GatherDepth = entry.GatherDepth, GatherRounds = entry.GatherRounds,
        });
        Plugin.Log($"Queue: {entry.Target.Label} ran out of materials, gathering then resuming ({left:N0} left)");
    }

    internal static int? LevelOf(Game game, ActionTarget t) =>
        XpOf(game, t)?.Level;

    internal static ExperienceComponent? XpOf(Game game, ActionTarget t) =>
        game.Entities.TryGetObjectById(t.EntityId, out var entity) && entity != null
            ? entity.Components.Values.OfType<ExperienceComponent>().FirstOrDefault() : null;

    /// <summary>How many crafts an entry should gather materials for.</summary>
    private static int CraftsWanted(Game game, QueueEntry entry)
    {
        int perCraft = Gather.ProductPerCraft(game, entry.Target);
        switch (entry.Stop)
        {
            case StopKind.Items:
                return (entry.StopValue + perCraft - 1) / perCraft;
            case StopKind.Level when XpOf(game, entry.Target) is { } xp &&
                                     ActionLog.XpPerAction(ActionLog.Get(entry.Target)) is double perAction:
                double need = xp.Table.GetXpForLevel(entry.StopValue) - xp.Xp;
                return (int)Math.Clamp(Math.Ceiling(need / perAction), 1, 1_000_000);
            default:
                return Math.Max(1, Settings.Current.QueueGatherBatch);
        }
    }

    /// <summary>
    /// Starts the next queued entry when nothing is running (the queue otherwise only advances when a
    /// running action stops by itself). Returns a status line for the menu, or "" when nothing to say.
    /// </summary>
    internal static string StartIfIdle()
    {
        var state = State;
        var game = TimeBank.TimeBank.Game;
        if (state == null || game == null || !state.Enabled || state.Entries.Count == 0) return "";
        if (RunningAction is { IsStarted: true }) return "";
        s_lastSkip = "";
        if (Advance(game) != null) return "";
        return state.Loop ? $"Nothing in the queue can start right now. Last: {s_lastSkip}"
                          : $"Nothing in the queue could start, so it was emptied. Last: {s_lastSkip}";
    }

    private static string s_lastSkip = "";

    private static QueueEntry? Advance(Game game)
    {
        var state = State;
        if (state == null || !state.Enabled) return null;
        int failsInARow = 0;
        for (int guard = 0; state.Entries.Count > 0 && guard < 500; guard++)
        {
            // Looping: stop once every entry has failed in a row (they'd just go round again).
            if (state.Loop && failsInARow > state.Entries.Count) break;
            var entry = state.Entries[0];
            state.Entries.RemoveAt(0);
            bool loops = state.Loop && !entry.Auto && !entry.Resume;
            if (loops) state.Entries.Add(entry.CopyForLoop());

            if (entry.Stop == StopKind.Level && LevelOf(game, entry.Target) is int lvl && lvl >= entry.StopValue)
            {
                s_lastSkip = $"{entry.Target.Label} (already level {lvl})";
                Plugin.Log($"Queue: skipped {s_lastSkip}");
                failsInARow++;
                continue;
            }

            // Crafting: gather what it's missing first (those entries go in front of it).
            if (Settings.Current.QueueGatherFirst && entry.GatherDepth < 4 && entry.GatherRounds < 5)
            {
                var plan = Gather.Plan(game, entry, CraftsWanted(game, entry), out string note);
                if (note.Length > 0) Plugin.Log($"Queue: {entry.Target.Label}: {note}");
                if (plan.Count > 0)
                {
                    entry.GatherRounds++;
                    if (loops) state.Entries.RemoveAt(state.Entries.Count - 1); // re-added when it comes round again
                    state.Entries.InsertRange(0, plan.Append(entry));
                    Plugin.Log($"Queue: gathering for {entry.Target.Label}: {string.Join("; ", plan.Select(p => p.Describe()))}");
                    continue;
                }
            }

            bool started;
            string reason;
            QueueMode.Internal = true; // our own start, not a click to capture
            try { started = SkillActions.Start(game, entry.Target, out reason); }
            finally { QueueMode.Internal = false; }
            if (started)
            {
                entry.GatherRounds = 0;
                Plugin.Log($"Queue: started {entry.Describe()}");
                state.Current = entry; // tracked even without a limit, so an override can put it back
                if (RunningAction is ActionComponent running) Arm(running, state.Current);
                Save();
                return entry;
            }
            s_lastSkip = $"{entry.Target.Label} ({reason})";
            Plugin.Log($"Queue: skipped {s_lastSkip}");
            failsInARow++;
        }
        Save();
        return null;
    }
}
