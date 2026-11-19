using System;
using System.Collections.Generic;
using GamesByMalcs.Melvor2.Core.CostTracking;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Tooltips;
using Godot;
using HarmonyLib;
using Melvor2Tweaks.Actions;

namespace Melvor2Tweaks.Features;

/// <summary>
/// A "Queue" button beside every item cost the game shows with its cost component (item upgrades in the
/// bank, shop, quests, agility, farming plots, ranching, bonfires...): queues the action that makes the
/// item, for exactly the shortfall (required - owned), counting only that item. A crafted item (e.g. Steel
/// Bars) is an item-limit entry, so the queue gathers its ores first as usual.
/// The button is a copy of the row's own quick-buy button, shown only when you're short and an unlocked
/// action can make the item.
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

    private static ItemCostTracker? TrackerOf(ItemCostTrackerView view) =>
        Traverse.Create(view).Property("Tracker").GetValue<ItemCostTracker>();

    /// <summary>(item, required, owned) for the view's cost, or null.</summary>
    private static (Item item, int need)? Shortfall(ItemCostTrackerView view)
    {
        if (TrackerOf(view) is not { } t) return null;
        int required = t.Quantity * Math.Max(1, view.DesiredMultiple);
        int need = required - t.CurrentQuantity;
        return need > 0 ? (t.Item, need) : null;
    }

    private static void Sync(ItemCostTrackerView view)
    {
        try
        {
            var button = Button(view);
            if (button == null) return;
            var shortfall = Shortfall(view);
            var producer = shortfall is { } s ? CachedProducer(s.item) : null;
            button.Visible = producer != null;
            if (producer != null && shortfall is { } sf)
                Tip(button)?.SetContent($"{producer.Label} - for {sf.need:N0} {sf.item.DisplayName.Plural} (what you're short). " +
                                         "Crafted items gather their materials first.");
        }
        catch (Exception e) { Plugin.Log("Queue from cost: " + e.Message); }
    }

    /// <summary>The view's Queue button, created on first use as a copy of its quick-buy button.</summary>
    private static Button? Button(ItemCostTrackerView view)
    {
        if (view.FindChild(ButtonName, recursive: true, owned: false) is Button existing) return existing;
        var quickBuy = view.QuickBuyButton;
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
        b.Pressed += () => OnPressed(view, b);
        parent.AddChild(b);
        parent.MoveChild(b, quickBuy.GetIndex() + 1);
        return b;
    }

    private static void OnPressed(ItemCostTrackerView view, Button button)
    {
        if (Shortfall(view) is not { } s || CachedProducer(s.item) is not { } producer) return;
        var entry = new QueueEntry
        {
            Target = producer, Stop = StopKind.Items, StopValue = s.need, CountItemId = s.item.Id,
        };
        ActionQueue.Add(entry);
        Plugin.Log("Queued from a cost: " + entry.Describe());
        if (ActionQueue.RunningAction is not { IsStarted: true }) ActionQueue.StartIfIdle();
        // Feedback: a quick gold flash on the button.
        button.Modulate = new Color(1.6f, 1.3f, 0.5f);
        button.CreateTween().TweenProperty(button, "modulate", Colors.White, 0.6f);
        Tip(button)?.SetContent("Queued: " + entry.Describe());
    }

    private static TextTooltip? Tip(Button b)
    {
        foreach (var c in b.GetChildren()) if (c is TextTooltip t) return t;
        return null;
    }

    // Producer lookups walk every skill's recipes; cost views update often (shop lists many), so cache briefly.
    private static readonly Dictionary<string, (ActionTarget? producer, long at)> s_cache = new();

    private static ActionTarget? CachedProducer(Item item)
    {
        long now = System.Environment.TickCount64;
        if (s_cache.TryGetValue(item.Id, out var c) && now - c.at < 15_000) return c.producer;
        var game = TimeBank.TimeBank.Game;
        var producer = game == null ? null : Gather.Producer(game, item);
        s_cache[item.Id] = (producer, now);
        return producer;
    }

    /// <summary>Hot load: give cost views already on screen their button now (not on their next update).</summary>
    internal static void AttachToExisting()
    {
        foreach (var view in UI.NodeSearch.All<ItemCostTrackerView>()) Sync(view);
    }

    /// <summary>Hot unload: remove the buttons this copy added.</summary>
    internal static void DetachAll()
    {
        foreach (var view in UI.NodeSearch.All<ItemCostTrackerView>())
            if (view.FindChild(ButtonName, true, false) is Node b) { b.GetParent().RemoveChild(b); b.QueueFree(); }
    }
}
