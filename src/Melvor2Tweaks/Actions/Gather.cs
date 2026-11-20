using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Actions;
using GamesByMalcs.Melvor2.Components.CraftingAction;
using GamesByMalcs.Melvor2.Conditions;
using GamesByMalcs.Melvor2.Data;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Fishing;
using GamesByMalcs.Melvor2.Entities.Mining;
using GamesByMalcs.Melvor2.Entities.Woodcutting;
using HarmonyLib;

namespace Melvor2Tweaks.Actions;

/// <summary>
/// Recipe data for the queue: what a crafting target consumes, and which action can produce an item
/// (woodcutting / mining / fishing, or another crafting recipe - e.g. bars for smithing).
/// </summary>
internal static class Gather
{
    private static ActionComponent? ActionOf(Game game, string entityId) =>
        game.Entities.TryGetObjectById(entityId, out GameEntity? entity) && entity != null &&
        entity.Components.TryGetValue("Action", out var c) ? c as ActionComponent : null;

    internal static IEnumerable<IActiveCraftingRecipe> AllCraftingRecipes(ActionComponent action) => CraftingRecipes(action);
    private static IEnumerable<IActiveCraftingRecipe> CraftingRecipes(ActionComponent action) =>
        Traverse.Create(action).Property("ActiveRecipes").GetValue() is IDictionary d
            ? d.Values.OfType<IActiveCraftingRecipe>() : Enumerable.Empty<IActiveCraftingRecipe>();

    private static IActiveCraftingRecipe? CraftingRecipe(Game game, ActionTarget t) =>
        ActionOf(game, t.EntityId) is { } a && a is not WoodcuttingActionComponent && a is not MiningActionComponent && a is not FishingActionComponent
            ? CraftingRecipes(a).FirstOrDefault(r => r.Recipe.Id == t.Ids.FirstOrDefault()) : null;

    internal static bool IsCrafting(Game game, ActionTarget t) => CraftingRecipe(game, t) != null;

    /// <summary>Items one craft consumes (after cost modifiers), or null if the target isn't a crafting recipe.</summary>
    internal static List<(Item item, int qty)>? Costs(Game game, ActionTarget t) =>
        CraftingRecipe(game, t)?.Stats.ItemCost.Values.Select(v => (v.Value.Item, v.Value.Quantity)).Where(c => c.Quantity > 0).ToList();

    internal static int ProductPerCraft(Game game, ActionTarget t) =>
        Math.Max(1, CraftingRecipe(game, t) is { } r ? PrimaryProduct(r).qty : 1);

    /// <summary>What one craft makes: Stats.Product (cooking, herblore...) or the first of Stats.Products (artisan skills).</summary>
    internal static (Item? item, int qty) PrimaryProduct(IActiveCraftingRecipe r)
    {
        if (r.Stats.Product?.Value is { } p) return (p.Item, p.Quantity);
        if (r.Stats.Products?.Values.FirstOrDefault()?.Value is { } c) return (c.Item, c.Quantity);
        return (null, 1);
    }

    /// <summary>How many crafts the bank can pay for right now (null if not a crafting target).</summary>
    internal static int? CraftsAffordable(Game game, ActionTarget t)
    {
        var costs = Costs(game, t);
        if (costs == null) return null;
        if (costs.Count == 0) return int.MaxValue;
        return costs.Min(c => Features.ItemRules.InBank(c.item) / c.qty);
    }

    /// <summary>An unlocked action that produces the item: gathering first, then crafting recipes.</summary>
    /// <summary>
    /// Every action that makes <paramref name="item"/>, locked or not: gathering recipes (woodcutting, mining,
    /// fishing) flagged so they can be preferred over crafting ones.
    /// </summary>
    private static IEnumerable<(ActionTarget target, ConditionGroup requirements, bool gathering)> Makers(Game game, Item item)
    {
        foreach (var entity in game.Entities.GetAllObjects())
        {
            if (!entity.Components.TryGetValue("Action", out var c) || c is not ActionComponent action) continue;
            string skill = entity.DisplayName.Text;
            switch (action)
            {
                case WoodcuttingActionComponent wc:
                    foreach (var r in wc.Trees.Keys)
                        if (r.Product.Item == item) yield return (Make(entity, r.Id, skill, r.DisplayName.Text), r.Requirements, true);
                    break;
                case MiningActionComponent m:
                    foreach (var r in m.Rocks.Keys)
                        if (r.Product.Item == item) yield return (Make(entity, r.Id, skill, r.DisplayName.Text), r.Requirements, true);
                    break;
                case FishingActionComponent f:
                    foreach (var r in f.Recipes.Keys)
                        if (r.RandomProducts.Items.Any(p => p.Item == item)) yield return (Make(entity, r.Id, skill, r.DisplayName.Text), r.Requirements, true);
                    break;
                default:
                    foreach (var r in CraftingRecipes(action))
                        if (PrimaryProduct(r).item == item)
                            yield return (Make(entity, r.Recipe.Id, skill, r.Recipe.DisplayName.Text), r.Recipe.Requirements, false);
                    break;
            }
        }
    }

    /// <summary>The unlocked action that makes the item (gathering preferred), or null.</summary>
    internal static ActionTarget? Producer(Game game, Item item)
    {
        ActionTarget? crafted = null;
        foreach (var (target, req, gathering) in Makers(game, item))
        {
            if (!req.AreMet) continue;
            if (gathering) return target;
            crafted ??= target;
        }
        return crafted;
    }

    /// <summary>
    /// When the item can be made but only by locked actions: the action and what it still needs
    /// (e.g. "Smithing Level 40"). Null when something unlocked makes it, or nothing does.
    /// </summary>
    internal static (ActionTarget target, string needs)? LockedProducer(Game game, Item item)
    {
        (ActionTarget target, string needs)? best = null;
        int bestUnmet = int.MaxValue;
        foreach (var (target, req, _) in Makers(game, item))
        {
            if (req.AreMet) return null;
            var unmet = req.Conditions.Where(c => !c.IsMet).ToList();
            if (unmet.Count >= bestUnmet) continue;
            bestUnmet = unmet.Count;
            best = (target, string.Join(", ", unmet.Select(c => PlainText(c.GetDescription(false, false)))));
        }
        return best;
    }

    private static string PlainText(string bbcode) =>
        System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(bbcode, @"\[img[^\]]*\].*?\[/img\]", ""), @"\[[^\]]*\]", "").Trim();

    private static ActionTarget Make(GameEntity entity, string id, string skill, string name) =>
        new() { EntityId = entity.Id, Ids = new[] { id }, Label = $"{skill}: {name}" };

    /// <summary>
    /// Entries that gather what this crafting entry is missing, for the amount it will use. Empty when it
    /// has everything (or isn't crafting). <paramref name="crafts"/> is how many crafts to cover.
    /// </summary>
    internal static List<QueueEntry> Plan(Game game, QueueEntry entry, int crafts, out string note)
    {
        note = "";
        var plan = new List<QueueEntry>();
        var costs = Costs(game, entry.Target);
        if (costs == null || crafts <= 0) return plan;
        var missingNoSource = new List<string>();
        foreach (var (item, qty) in costs)
        {
            long need = (long)qty * crafts - Features.ItemRules.InBank(item);
            if (need <= 0) continue;
            var producer = Producer(game, item);
            if (producer == null) { missingNoSource.Add(item.DisplayName.Plural); continue; }
            plan.Add(new QueueEntry
            {
                Target = producer, Stop = StopKind.Items, StopValue = (int)Math.Min(need, 10_000_000),
                CountItemId = item.Id, Auto = true, GatherDepth = entry.GatherDepth + 1,
            });
        }
        if (missingNoSource.Count > 0) note = "nothing you can do produces " + string.Join(", ", missingNoSource);
        return plan;
    }
}
