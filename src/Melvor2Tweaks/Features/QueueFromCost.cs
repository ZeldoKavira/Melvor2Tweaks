using System;
using System.Collections.Generic;
using GamesByMalcs.Melvor2.Core.CostTracking;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.InfoTables;
using GamesByMalcs.Melvor2.Stats.Nodes;
using GamesByMalcs.Melvor2.Tooltips;
using Godot;
using HarmonyLib;
using Melvor2Tweaks.Actions;

namespace Melvor2Tweaks.Features;

/// <summary>
/// A "Queue" button beside every item cost the game shows with its cost component (item upgrades in the
/// bank, shop, quests, agility, farming plots, ranching, bonfires...): queues the action that makes the
/// item, for exactly the shortfall (required - owned), counting only that item. Crafting pages' Input rows get
/// one too, for "Crafts to gather for" crafts' worth. A crafted item (e.g. Steel
/// Bars) is an item-limit entry, so the queue gathers its ores first as usual.
/// The button is a copy of the row's own quick-buy button, shown whenever you're short and some action
/// makes the item. If every such action is still locked, the button is greyed out and its tooltip says what
/// unlocks it (e.g. "Smithing Level 40").
/// </summary>
internal static class QueueFromCost
{
    private const string ButtonName = "Melvor2TweaksQueueCost";

    [HarmonyPatch(typeof(ItemCostTrackerView), nameof(ItemCostTrackerView.Bind))]
    private static class BindPatch
    {
        private static void Postfix(ItemCostTrackerView __instance) => Sync(__instance);
    }

    [HarmonyPatch(typeof(ItemCostTrackerView), nameof(ItemCostTrackerView.Update))]
    private static class UpdatePatch
    {
        private static void Postfix(ItemCostTrackerView __instance) => Sync(__instance);
    }

    [HarmonyPatch(typeof(RequiredItemRow), nameof(RequiredItemRow.Update))]
    private static class InputRowPatch
    {
        private static void Postfix(RequiredItemRow __instance) => Sync(__instance);
    }

    private static ItemCostTracker? TrackerOf(ItemCostTrackerView view) =>
        Traverse.Create(view).Property("Tracker").GetValue<ItemCostTracker>();

    /// <summary>What a cost row is short of: the item, how many to make, and how that was worked out.</summary>
    private readonly record struct Need(Item Item, int Count, string Why);

    /// <summary>Costs (upgrades, shop, quests...): the shortfall, required - owned.</summary>
    private static Need? Shortfall(ItemCostTrackerView view)
    {
        if (TrackerOf(view) is not { } t) return null;
        int required = t.Quantity * Math.Max(1, view.DesiredMultiple);
        int need = required - t.CurrentQuantity;
        return need > 0 ? new Need(t.Item, need, "what you're short") : null;
    }

    /// <summary>
    /// A crafting recipe's Input row (one craft's worth): enough for "Crafts to gather for" crafts, less what
    /// you have. Hidden once you have that many.
    /// </summary>
    private static Need? Shortfall(RequiredItemRow row)
    {
        var t = Traverse.Create(row);
        var quantity = t.Property("ItemQuantityNode").GetValue<ValueStatNode<ItemQuantity>>()?.Value
                       ?? t.Property("ItemQuantity").GetValue<ItemQuantity>();
        if (quantity?.Item is not { } item || quantity.Quantity <= 0) return null;
        int crafts = Math.Max(1, Settings.Current.QueueGatherBatch);
        long need = (long)quantity.Quantity * crafts - ItemRules.InBank(item);
        return need > 0 ? new Need(item, (int)Math.Min(need, 10_000_000), $"enough for {crafts:N0} crafts") : null;
    }

    private static void Sync(ItemCostTrackerView view) =>
        Sync(view, view.QuickBuyButton, () => Shortfall(view));

    private static void Sync(RequiredItemRow row) =>
        Sync(row, Traverse.Create(row).Property("QuickBuyButton").GetValue<Button>(), () => Shortfall(row));

    private static void Sync(Control host, Button? quickBuy, Func<Need?> shortfall)
    {
        try
        {
            var button = Button(host, quickBuy, shortfall);
            if (button == null) return;
            var need = shortfall();
            var source = need is { } n ? CachedSource(n.Item) : default;
            button.Visible = source.producer != null || source.locked != null;
            button.Disabled = source.producer == null;
            if (need is not { } sf) return;
            if (source.producer is { } producer)
                SetTip(button, $"{producer.Label} - for {sf.Count:N0} {sf.Item.DisplayName.Plural} ({sf.Why}). " +
                                         "Crafted items gather their materials first.");
            else if (source.locked is { } locked)
                SetTip(button, $"Can't queue {sf.Item.DisplayName.Plural} yet: {locked.target.Label} needs {locked.needs}.");
        }
        catch (Exception e) { Plugin.Log("Queue from cost: " + e.Message); }
    }

    /// <summary>The row's Queue button, created on first use as a copy of its quick-buy button.</summary>
    private static Button? Button(Control host, Button? quickBuy, Func<Need?> shortfall)
    {
        if (host.FindChild(ButtonName, recursive: true, owned: false) is Button existing) return existing;
        if (quickBuy == null || quickBuy.GetParent() is not Node parent) return null;
        var b = (Button)quickBuy.Duplicate((int)(Node.DuplicateFlags.Groups | Node.DuplicateFlags.Scripts));
        b.Name = ButtonName;
        b.Visible = false;
        if (ResourceLoader.Exists("res://Assets/Icons/TimeInterval_48.png"))
        {
            b.Icon = GD.Load<Texture2D>("res://Assets/Icons/TimeInterval_48.png");
            b.ExpandIcon = true;
        }
        if (b.Text.Length > 0) b.Text = "Queue";
        foreach (var c in b.GetChildren())
        {
            if (c is TextureRect tr) tr.Texture = b.Icon;                  // icon drawn by a child in some scenes
            else if (c is TextTooltip old) { b.RemoveChild(old); old.QueueFree(); } // the copied "quick buy" tooltip
        }
        UI.UiKit.AddTooltip(b, "Add to queue", "");
        b.Pressed += () => OnPressed(b, shortfall);
        parent.AddChild(b);
        parent.MoveChild(b, quickBuy.GetIndex() + 1);
        return b;
    }

    private static void OnPressed(Button button, Func<Need?> shortfall)
    {
        if (shortfall() is not { } s || CachedSource(s.Item).producer is not { } producer) return;
        var entry = new QueueEntry
        {
            Target = producer, Stop = StopKind.Items, StopValue = s.Count, CountItemId = s.Item.Id,
        };
        ActionQueue.Add(entry);
        Plugin.Log("Queued from a cost: " + entry.Describe());
        if (ActionQueue.RunningAction is not { IsStarted: true }) ActionQueue.StartIfIdle();
        // Feedback: a quick gold flash on the button.
        button.Modulate = new Color(1.6f, 1.3f, 0.5f);
        button.CreateTween().TweenProperty(button, "modulate", Colors.White, 0.6f);
        SetTip(button, "Queued: " + entry.Describe());
    }

    private static void SetTip(Button b, string text)
    {
        b.SetMeta("m2t_tip", text);
        Tip(b)?.SetContent(text);
    }

    /// <summary>Dev aid: every Queue button on screen, its state and tooltip.</summary>
    internal static void LogButtons()
    {
        foreach (var node in UI.NodeSearch.All<Button>())
            if (node.Name == ButtonName && node.IsVisibleInTree())
                Plugin.Log($"queue-cost button on {node.GetParent()?.GetParent()?.GetType().Name}: disabled={node.Disabled} - {node.GetMeta("m2t_tip", "")}");
    }

    private static TextTooltip? Tip(Button b)
    {
        foreach (var c in b.GetChildren()) if (c is TextTooltip t) return t;
        return null;
    }

    // Producer lookups walk every skill's recipes; cost views update often (shop lists many), so cache briefly.
    private static readonly Dictionary<string, ((ActionTarget? producer, (ActionTarget target, string needs)? locked) source, long at)> s_cache = new();

    /// <summary>The unlocked action that makes the item, or else the locked one and what it needs.</summary>
    private static (ActionTarget? producer, (ActionTarget target, string needs)? locked) CachedSource(Item item)
    {
        long now = System.Environment.TickCount64;
        if (s_cache.TryGetValue(item.Id, out var c) && now - c.at < 15_000) return c.source;
        var game = TimeBank.TimeBank.Game;
        var producer = game == null ? null : Gather.Producer(game, item);
        var locked = game == null || producer != null ? null : Gather.LockedProducer(game, item);
        s_cache[item.Id] = ((producer, locked), now);
        return (producer, locked);
    }

    /// <summary>Hot load: give cost views already on screen their button now (not on their next update).</summary>
    internal static void AttachToExisting()
    {
        foreach (var view in UI.NodeSearch.All<ItemCostTrackerView>()) Sync(view);
        foreach (var row in UI.NodeSearch.All<RequiredItemRow>()) Sync(row);
    }

    /// <summary>Hot unload: remove the buttons this copy added.</summary>
    internal static void DetachAll()
    {
        foreach (var view in UI.NodeSearch.All<ItemCostTrackerView>())
            if (view.FindChild(ButtonName, true, false) is Node b) { b.GetParent().RemoveChild(b); b.QueueFree(); }
        foreach (var row in UI.NodeSearch.All<RequiredItemRow>())
            if (row.FindChild(ButtonName, true, false) is Node b) { b.GetParent().RemoveChild(b); b.QueueFree(); }
    }
}
