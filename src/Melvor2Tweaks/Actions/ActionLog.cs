using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Actions;
using GamesByMalcs.Melvor2.Components.Characters;
using GamesByMalcs.Melvor2.Components.CombatLoot;
using GamesByMalcs.Melvor2.Components.Experience;
using GamesByMalcs.Melvor2.Components.Inventories;
using GamesByMalcs.Melvor2.Core;
using GamesByMalcs.Melvor2.Core.TickTimer;
using GamesByMalcs.Melvor2.Data;
using GamesByMalcs.Melvor2.Entities.Combat;
using HarmonyLib;

namespace Melvor2Tweaks.Actions;

/// <summary>All-time totals for one action target (per save).</summary>
public sealed class ActionLogEntry
{
    public string Label { get; set; } = "";
    /// <summary>Completed actions (timer fires; enemy kills in combat).</summary>
    public long Actions { get; set; }
    /// <summary>Game time spent running it (independent of the speed multiplier).</summary>
    public double GameMs { get; set; }
    public double Xp { get; set; }
    /// <summary>Main products only (no gems, nests, junk) - what "For N items" counts.</summary>
    public long Products { get; set; }
    /// <summary>Every item received while it ran, item id -> count.</summary>
    public Dictionary<string, long> Items { get; set; } = new();
    public DateTime Since { get; set; } = DateTime.Now;
}

/// <summary>
/// Drop log: what each action has produced for all time (until reset), with action count, time and XP.
/// Also the source of the rates behind the queue's time-left estimates.
/// Follows the running action each tick; subscribes to its timer (or enemy kills), its inventory and its XP.
/// </summary>
internal static class ActionLog
{
    private static Dictionary<string, Dictionary<string, ActionLogEntry>> s_all = new();
    private static string FilePath => Path.Combine(Settings.DataDir, "actionlog.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    internal static string Key(ActionTarget t) => t.EntityId + "|" + string.Join(",", t.Ids);

    internal static Dictionary<string, ActionLogEntry>? ForSave
    {
        get
        {
            string? id = ActionQueue.SaveKey;
            if (id == null) return null;
            if (!s_all.TryGetValue(id, out var d)) s_all[id] = d = new();
            return d;
        }
    }

    internal static ActionLogEntry? Get(ActionTarget? t) =>
        t != null && ForSave is { } d && d.TryGetValue(Key(t), out var e) ? e : null;

    internal static string? CurrentKey => s_key;

    internal static void Reset(string key)
    {
        if (ForSave is not { } d || !d.TryGetValue(key, out var e)) return;
        var fresh = new ActionLogEntry { Label = e.Label };
        d[key] = fresh;
        if (key == s_key) s_entry = fresh;
        Save();
    }

    // ---- rates -------------------------------------------------------------------------------------
    /// <summary>Game ms per counted item (a specific item, or main products), or null if never seen.</summary>
    internal static double? MsPerItem(ActionLogEntry? e, string? itemId)
    {
        if (e == null || e.GameMs <= 0) return null;
        long n = itemId == null ? e.Products : e.Items.GetValueOrDefault(itemId);
        return n > 0 ? e.GameMs / n : null;
    }

    internal static double? MsPerXp(ActionLogEntry? e) => e == null || e.Xp <= 0 ? null : e.GameMs / e.Xp;
    internal static double? MsPerAction(ActionLogEntry? e) => e == null || e.Actions <= 0 ? null : e.GameMs / e.Actions;
    internal static double? XpPerAction(ActionLogEntry? e) => e == null || e.Actions <= 0 || e.Xp <= 0 ? null : e.Xp / e.Actions;

    // ---- persistence -------------------------------------------------------------------------------
    private static bool s_dirty;
    private static ulong s_lastSave;

    internal static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                s_all = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, ActionLogEntry>>>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch (Exception e) { Plugin.Log("actionlog.json unreadable: " + e.Message); s_all = new(); }
    }

    internal static void Save()
    {
        s_dirty = false;
        s_lastSave = Godot.Time.GetTicksMsec();
        try
        {
            Directory.CreateDirectory(Settings.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(s_all, Json));
        }
        catch (Exception e) { Plugin.Log("Could not save action log: " + e.Message); }
    }

    internal static void Shutdown()
    {
        Detach();
        if (s_dirty) Save();
    }

    // ---- tracking ----------------------------------------------------------------------------------
    private static ActionComponent? s_action;
    private static object? s_desc;
    private static string? s_key;
    private static ActionLogEntry? s_entry;
    private static TickTimer? s_timer;
    private static EnemyComponent? s_enemy;
    private static TrackableInventory? s_inventory;
    private static ExperienceComponent? s_xp;

    [HarmonyPatch(typeof(Game), nameof(Game.PropagateTick))]
    private static class TickPatch
    {
        private static void Postfix() => Tick();
    }

    private static void Tick()
    {
        var action = ActionQueue.RunningAction;
        if (action == null || !action.IsStarted)
        {
            if (s_action != null) Detach();
        }
        else
        {
            if (!ReferenceEquals(action, s_action) || !ReferenceEquals(action.CurrentDescription, s_desc)) Attach(action);
            if (s_entry != null) { s_entry.GameMs += Constants.TickInterval; s_dirty = true; }
        }
        if (s_dirty && Godot.Time.GetTicksMsec() - s_lastSave > 30_000) Save();
    }

    private static void Attach(ActionComponent action)
    {
        Detach();
        s_action = action;
        s_desc = action.CurrentDescription;
        var target = SkillActions.Capture(action);
        if (target == null || ForSave is not { } log) return; // retried when the description changes
        s_key = Key(target);
        if (!log.TryGetValue(s_key, out var entry)) log[s_key] = entry = new ActionLogEntry();
        entry.Label = target.Label;
        s_entry = entry;

        if (action is CombatActionComponent combat)
        {
            s_enemy = combat.Enemy;
            s_enemy.Killed += OnKilled;
            var loot = action.Entity.Components.Values.OfType<CombatLootComponent>().FirstOrDefault();
            s_inventory = loot?.Inventory ?? action.Inventory;
        }
        else
        {
            s_timer = action.ActionTimer;
            if (s_timer != null) s_timer.Fired += OnFired;
            s_inventory = action.Inventory;
        }
        s_inventory.ItemAdded += OnItemAdded;
        s_xp = action.Entity.Components.Values.OfType<ExperienceComponent>().FirstOrDefault();
        if (s_xp != null) s_xp.XpChanged += OnXpChanged;
    }

    private static void Detach()
    {
        if (s_timer != null) s_timer.Fired -= OnFired;
        if (s_enemy != null) s_enemy.Killed -= OnKilled;
        if (s_inventory != null) s_inventory.ItemAdded -= OnItemAdded;
        if (s_xp != null) s_xp.XpChanged -= OnXpChanged;
        s_timer = null; s_enemy = null; s_inventory = null; s_xp = null;
        s_action = null; s_desc = null; s_key = null; s_entry = null;
    }

    private static void OnFired(object? sender, EventArgs e) { if (s_entry != null) s_entry.Actions++; }

    private static void OnKilled(object? sender, EnemyKilledEventArgs e) { if (s_entry != null) s_entry.Actions++; }

    private static void OnItemAdded(object? sender, ItemAddedEventArgs e)
    {
        if (s_entry == null || e.Quantity <= 0) return;
        s_entry.Items[e.Item.Id] = s_entry.Items.GetValueOrDefault(e.Item.Id) + e.Quantity;
        if (s_action != null && !Features.SkillingTweaks.IsSideDrop(s_action, e.Item)) s_entry.Products += e.Quantity;
    }

    private static void OnXpChanged(object? sender, ValueChangedEventArgs<double> e)
    {
        if (s_entry != null && e.NewValue > e.OldValue) s_entry.Xp += e.NewValue - e.OldValue;
    }
}
