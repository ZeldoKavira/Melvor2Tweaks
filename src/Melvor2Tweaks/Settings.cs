using System;
using System.IO;
using System.Text.Json;
using Godot;

namespace Melvor2Tweaks;

/// <summary>Marks a setting shown on the in-game settings page (generated from these attributes).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SettingAttribute(string category, string label, string description) : Attribute
{
    public string Category { get; } = category;
    public string Label { get; } = label;
    public string Description { get; } = description;
    public double Min { get; init; } = 0;
    public double Max { get; init; } = 100;
    public double Step { get; init; } = 1;
    /// <summary>Show a 0..1 value as a percentage.</summary>
    public bool Percent { get; init; }
    /// <summary>Labels for an enum setting, in enum order (shown as a dropdown).</summary>
    public string[]? Options { get; init; }
}

/// <summary>User settings, stored as JSON in the game's user data folder (Melvor2Tweaks\settings.json).</summary>
public sealed class Settings
{
    // ---- Updates -----------------------------------------------------------------------------------
    [Setting("Updates", "Update automatically", "Download new versions of Melvor2Tweaks from GitHub (checked a little after the game starts and " +
             "every 6 hours). Most updates apply immediately; a few need a game restart.")]
    public bool AutoUpdate { get; set; } = true;

    // ---- Time bank (defaults mirror the Melvor 1 Offline Time Bank mod) ---------------------------
    [Setting("Time Bank", "Bank offline time", "Offline time goes into the time bank instead of being simulated when you return. Spend it from the top bar.")]
    public bool TimeBankEnabled { get; set; } = true;

    [Setting("Time Bank", "Banked time multiplier", "Offline time is multiplied by this before it is banked.", Min = 0, Max = 2, Step = 0.05, Percent = true)]
    public double OfflineMultiplier { get; set; } = 0.8;

    [Setting("Time Bank", "Max offline hours per absence", "Most offline time banked from one absence. -1 = unlimited.", Min = -1, Max = 720, Step = 1)]
    public double MaxAwayHours { get; set; } = 24;

    [Setting("Time Bank", "Share of offline time banked", "The rest runs as normal offline progress (not multiplied).", Min = 0, Max = 1, Step = 0.05, Percent = true)]
    public double BankRatio { get; set; } = 1.0;

    // ---- Speed -------------------------------------------------------------------------------------
    /// <summary>Current speed; changed from the top bar, not the settings page.</summary>
    public double Speed { get; set; } = 1;

    public double[] SpeedPresets { get; set; } = { 1, 2, 3, 5, 10 };

    // ---- Combat ------------------------------------------------------------------------------------
    [Setting("Combat", "Flee before dying", "Stop combat when your hitpoints drop below the threshold and you have no food left.")]
    public bool FleeEnabled { get; set; } = true;

    [Setting("Combat", "Flee hitpoints threshold", "Flee at or below this share of max hitpoints (only when out of food).", Min = 0.05, Max = 0.9, Step = 0.05, Percent = true)]
    public double FleeHpFraction { get; set; } = 0.2;

    [Setting("Combat", "Flee when unable to attack", "Stop combat when you run out of ammo or runes instead of standing there taking hits.")]
    public bool FleeWhenCantAttack { get; set; } = true;

    [Setting("Combat", "Swap food when it runs out", "When the selected food runs out, select another (favourites first, then the strongest heal). Also applies to agility and thieving.")]
    public bool FoodAutoSwap { get; set; } = true;

    [Setting("Combat", "Don't pause on boss spawn", "Some enemies pause combat when they spawn until you press unpause; skip that.")]
    public bool BossAutoUnpause { get; set; } = true;

    // ---- Skilling ----------------------------------------------------------------------------------
    [Setting("Skilling", "Agility: stop before a lethal obstacle", "Before an obstacle that could hit hard enough to kill you, eat until it can't (auto-eat only kicks in at 25% hitpoints, after the hit). Out of food, the course stops instead of risking a toolbelt item.")]
    public bool AgilityStopBeforeDeath { get; set; } = true;

    [Setting("Skilling", "Thieving: stop before a lethal pickpocket", "Before a pickpocket that could hit hard enough to kill you, eat until it can't (auto-eat only kicks in at 25% hitpoints, after the hit). Out of food, thieving stops instead of risking a toolbelt item.")]
    public bool ThievingStopBeforeDeath { get; set; } = true;

    [Setting("Skilling", "Auto-activate bonus events", "Rare and astrology bonus events activate as soon as they are found. A waiting bonus blocks new ones.")]
    public bool AutoActivateBonuses { get; set; } = true;

    [Setting("Skilling", "Keep going when a side drop won't fit", "Woodcutting, mining and fishing only stop on a full bank if the main product can't be stored, not a gem, nest or junk.")]
    public bool IgnoreSideDropBankFull { get; set; } = true;

    [Setting("Skilling", "Ranching: auto-pet", "Pet animals as soon as they want it.")]
    public bool RanchingAutoPet { get; set; } = true;

    [Setting("Skilling", "Ranching: auto-repair", "Repair enclosures when maintenance drops below the threshold, if you can afford it.")]
    public bool RanchingAutoRepair { get; set; } = true;

    [Setting("Skilling", "Ranching: repair below", "Maintenance level that triggers an auto-repair.", Min = 0.05, Max = 0.95, Step = 0.05, Percent = true)]
    public double RanchingRepairBelow { get; set; } = 0.75;

    [Setting("Skilling", "Farming: retry automation", "Plots stuck because seeds ran out or the bank was full retry auto-plant / auto-harvest every few seconds. Auto-harvest is skipped (and not paid for) while the crop can't fit in the bank.")]
    public bool FarmingRetryAutomation { get; set; } = true;

    [Setting("Skilling", "Grain mill: top up from bank", "The grain mill pulls the grain it needs from the bank instead of switching off.")]
    public bool GrainMillAutoTopUp { get; set; } = true;

    [Setting("Skilling", "Bonfire: relight automatically", "When the bonfire burns out (while not firemaking), refill it with the selected logs.")]
    public bool BonfireAutoRelight { get; set; } = true;

    // ---- Action queue ------------------------------------------------------------------------------
    [Setting("Action queue", "Gather missing materials first", "When a queued crafting action with an item or level limit (smithing, cooking, " +
             "fletching...) is missing materials, the queue first gathers or makes them (woodcutting, mining, fishing, or another recipe such as " +
             "bars), then crafts; if it runs out part-way it gathers more and carries on until the limit.")]
    public bool QueueGatherFirst { get; set; } = true;

    [Setting("Action queue", "Gather for \"Until it stops\" crafts", "Off: an \"Until it stops\" craft uses the materials you have, then the " +
             "queue moves on (it's skipped if you have none). On: it first gathers enough for the number of crafts below, makes them, then moves on.")]
    public bool QueueGatherForUntilStops { get; set; } = false;

    [Setting("Action queue", "Crafts to gather for", "How many crafts' worth of materials to gather when there's no item count to go by: " +
             "\"Until it stops\" crafts (with the setting above on), and \"Until level\" before the action has a known XP rate.", Min = 10, Max = 100000, Step = 10)]
    public int QueueGatherBatch { get; set; } = 200;

    // ---- Interface ---------------------------------------------------------------------------------
    [Setting("Interface", "Event log size", "How many entries the event log keeps (game default 100). Takes effect for new entries.", Min = 100, Max = 2000, Step = 50)]
    public int EventLogCap { get; set; } = 500;

    [Setting("Interface", "Double-click to equip", "Double-click equipment in the bank to equip it (into a free slot when there is one; ammo equips the whole stack).")]
    public bool BankDoubleClickEquip { get; set; } = true;

    // ---- Items -------------------------------------------------------------------------------------
    [Setting("Items", "Default Keep for new rules", "What the Keep amount starts at when you give an item Auto-sell, Auto-open or Auto-bury " +
             "(bank Sell panel and Items menu). Change Keep before picking the action to override it.",
             Options = new[] { "0", "1", "All but 1", "All" })]
    public Features.KeepDefault NewRuleKeep { get; set; } = Features.KeepDefault.Zero;

    /// <summary>Item automation by bank category (BankTagCategory id); an item's own rule wins.</summary>
    public System.Collections.Generic.Dictionary<string, Features.ItemRule> CategoryRules { get; set; } = new();

    /// <summary>Item automation: item id -> Sell / Open / Bury everything above a keep minimum.</summary>
    public System.Collections.Generic.Dictionary<string, Features.ItemRule> ItemRules { get; set; } = new();

    /// <summary>v0.7 junk list (id -> keep); migrated into <see cref="ItemRules"/> as Sell rules on load.</summary>
    public System.Collections.Generic.Dictionary<string, int> Junk { get; set; } = new();

    /// <summary>v0.5 junk list (no minimums); migrated into <see cref="Junk"/> on load.</summary>
    public System.Collections.Generic.List<string> JunkItems { get; set; } = new();

    // -----------------------------------------------------------------------------------------------
    public static Settings Current { get; private set; } = new();

    public static event Action? Changed;

    public static string DataDir => Path.Combine(OS.GetUserDataDir(), "Melvor2Tweaks");
    private static string FilePath => Path.Combine(DataDir, "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
        }
        catch (Exception e)
        {
            Plugin.Log("Settings unreadable, using defaults: " + e.Message);
            Current = new Settings();
        }
        foreach (string id in Current.JunkItems) Current.Junk.TryAdd(id, 0);
        Current.JunkItems.Clear();
        foreach (var (id, keep) in Current.Junk)
            Current.ItemRules.TryAdd(id, new Features.ItemRule { Action = Features.RuleAction.Sell, Keep = keep });
        Current.Junk.Clear();
        Save(); // writes defaults for any newly added fields
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, Json));
        }
        catch (Exception e)
        {
            Plugin.Log("Could not save settings: " + e.Message);
        }
        Changed?.Invoke();
    }
}
