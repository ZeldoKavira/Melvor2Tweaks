using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2.Components;
using GamesByMalcs.Melvor2.Components.Actions;
using GamesByMalcs.Melvor2.Components.Toolbelt;
using GamesByMalcs.Melvor2.Components.ToolbeltEquipping;
using GamesByMalcs.Melvor2.Data;
using GamesByMalcs.Melvor2.Data.Currencies;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Modifiers;
using HarmonyLib;

namespace Melvor2Tweaks.Features;

/// <summary>
/// Looks at one skill's toolbelt (its per-skill equipment) and the bank:
///  - Empty: an unlocked slot is empty although the bank holds an item you can put in it.
///  - Upgrade: a slot holds an item, but the bank holds one that is at least as good on every bonus that
///    applies to what this skill is doing right now, and better on at least one.
/// "Applies" means the bonus has no recipe/category/location restriction, or its restriction matches the
/// skill's current action. Items equipped for their effect alone (no bonuses, e.g. a lockpick or a key a
/// recipe needs) are never reported as replaceable.
/// </summary>
internal static class ToolbeltCheck
{
    internal enum Severity { None, Upgrade, Empty }

    internal sealed record SlotIssue(ToolbeltSlot Slot, Severity Severity, Item? Equipped, Item Suggested);

    internal sealed record Result(List<SlotIssue> Issues)
    {
        internal Severity Worst => Issues.Count == 0 ? Severity.None : Issues.Max(i => i.Severity);
        internal SlotIssue? For(ToolbeltSlot slot) => Issues.FirstOrDefault(i => i.Slot == slot);
    }

    internal static Result Check(ToolbeltEquippingComponent equipping)
    {
        var toolbelt = equipping.Toolbelt;
        var issues = new List<SlotIssue>();
        var current = CurrentObjects(equipping.Entity);

        // Bank items that fit this toolbelt, can be equipped now and aren't already in it.
        var candidates = new List<(Item item, ToolbeltCapability cap)>();
        foreach (var stack in equipping.Inventory.Stacks)
        {
            if (stack.IsPlaceholder || stack.Quantity <= 0) continue;
            var item = stack.Item;
            if (!item.Capabilities.TryGet<ToolbeltCapability>(out var cap)) continue;
            if (!cap.Requirements.AreMet || toolbelt.IsItemEquipped(item) || equipping.IsItemHidden(item)) continue;
            candidates.Add((item, cap));
        }

        foreach (var slot in toolbelt.Config.ToolbeltSlots)
        {
            if (!slot.UnlockRequirements.AreMet || !toolbelt.TryGetActiveSlot(slot, out var active)) continue;
            var fits = candidates.Where(c => c.cap.FitsInSlot == slot.FitsType).ToList();
            if (fits.Count == 0) continue;

            if (active.Equipped == null)
            {
                var best = fits.OrderByDescending(c => Total(Score(c.cap, current))).First();
                issues.Add(new SlotIssue(slot, Severity.Empty, null, best.item));
                continue;
            }

            var equipped = active.Equipped.Toolbelt;
            if (!HasAnyBonus(equipped)) continue; // worn for its effect (lockpick, key, requirement)
            var mine = Score(equipped, current);
            var better = fits
                .Select(c => (c.item, score: Score(c.cap, current)))
                .Where(c => Dominates(c.score, mine))
                .OrderByDescending(c => Total(c.score))
                .FirstOrDefault();
            if (better.item != null)
                issues.Add(new SlotIssue(slot, Severity.Upgrade, active.Equipped.Item, better.item));
        }
        return new Result(issues);
    }

    /// <summary>One line per issue, for tooltips and the dev log.</summary>
    internal static string Describe(SlotIssue issue) => issue.Severity == Severity.Empty
        ? $"{issue.Slot.DisplayName.Text} is empty - you have {issue.Suggested.DisplayName.Singular}"
        : $"{issue.Slot.DisplayName.Text}: {issue.Suggested.DisplayName.Singular} beats {issue.Equipped?.DisplayName.Singular}";

    private static bool HasAnyBonus(ToolbeltCapability cap) =>
        cap.Bonuses.Component.Count > 0 || cap.Bonuses.Global.Count > 0;

    private static double Total(Dictionary<string, double> score) => score.Values.Sum();

    private static bool Dominates(Dictionary<string, double> a, Dictionary<string, double> b)
    {
        bool strictly = false;
        foreach (var key in a.Keys.Union(b.Keys))
        {
            double x = a.GetValueOrDefault(key), y = b.GetValueOrDefault(key);
            if (x < y - 1e-9) return false;
            if (x > y + 1e-9) strictly = true;
        }
        return strictly;
    }

    /// <summary>
    /// Bonus "goodness" per stat for what the skill is doing: positive = helps. Bonuses restricted to a
    /// different recipe, category or location are left out. Item/currency qualifiers become part of the key
    /// ("bird nest chance" is a different stat from "geode chance").
    /// </summary>
    internal static Dictionary<string, double> Score(ToolbeltCapability cap, HashSet<object> current)
    {
        var score = new Dictionary<string, double>();
        foreach (var mod in cap.Bonuses.Component)
        {
            double sign = mod.Stat.Kind switch { ModifierKind.Positive => 1, ModifierKind.Negative => -1, _ => 0 };
            if (sign == 0) continue;
            string key = mod.Stat.StatsClassId + ":" + mod.Stat.Name;
            bool applies = true;
            foreach (var o in mod.LocalContext.Objects)
            {
                switch (o)
                {
                    case GameComponent or GameEntity: break;                 // the skill itself
                    case Item or Currency: key += "|" + ((INamespacedObject)o).Id; break; // what the stat targets
                    default: if (!current.Contains(o)) applies = false; break; // recipe/category/location filter
                }
            }
            if (applies) score[key] = score.GetValueOrDefault(key) + sign * mod.Value;
        }
        return score;
    }

    /// <summary>The skill's current recipe (or tree, rock, spot, animal...) and the data objects it belongs to.</summary>
    internal static HashSet<object> CurrentObjects(GameEntity entity)
    {
        var set = new HashSet<object>();
        var action = Traverse.Create(entity).Property("Action").GetValue() as ActionComponent;
        if (action == null) return set;
        foreach (var root in Roots(action))
        {
            if (root == null) continue;
            set.Add(root);
            foreach (var p in root.GetType().GetProperties())
            {
                if (p.GetIndexParameters().Length > 0) continue;
                bool single = typeof(INamespacedObject).IsAssignableFrom(p.PropertyType);
                bool many = !single && typeof(IEnumerable).IsAssignableFrom(p.PropertyType) && p.PropertyType != typeof(string);
                if (!single && !many) continue;
                object? v;
                try { v = p.GetValue(root); } catch { continue; }
                if (single) { if (v != null) set.Add(v); }
                else if (v is IEnumerable list && p.Name.Contains("Categor"))
                    foreach (var e in list) if (e is INamespacedObject) set.Add(e);
            }
        }
        return set;
    }

    private static IEnumerable<object?> Roots(ActionComponent action)
    {
        var t = Traverse.Create(action);
        object? Get(params string[] path)
        {
            var x = t;
            foreach (var name in path) x = x.Property(name);
            try { return x.GetValue(); } catch { return null; }
        }
        yield return Get("CuttingTree", "Recipe");        // woodcutting
        yield return Get("CurrentRock", "Recipe");        // mining
        yield return Get("CurrentRecipe", "Recipe");      // fishing, thieving
        yield return Get("CurrentAnimal", "Animal");      // hunting
        yield return Get("CurrentAnimal", "Location");
        yield return Get("CurrentPath", "Path");          // agility
        yield return Get("CurrentPath", "Course", "Course");
        yield return Get("StudiedConstellation", "Constellation"); // astrology
        yield return Get("CreatingRecipe", "Recipe");     // artisan skills, cooking, herblore...
    }
}
