using System;
using System.Linq;
using System.Runtime.CompilerServices;
using GamesByMalcs.Melvor2.Components.Characters;
using GamesByMalcs.Melvor2.Components.CombatFood;
using GamesByMalcs.Melvor2.Entities.Agility;
using HarmonyLib;

namespace Melvor2Tweaks.Features;

/// <summary>
/// Agility "stop before dying". A failed obstacle hits for 1..its damage stat, and dying costs a random
/// toolbelt item. The game's auto-eat only fires at 25% hitpoints, after a hit, so one big hit can kill
/// even with food. Before each obstacle attempt: if its max hit could take the player to 0, eat (the
/// game's ManualEat) until it can't; if that's not possible (no food, or the hit exceeds max hitpoints)
/// stop the course instead, as an automatic stop so the action queue moves on.
/// </summary>
internal static class AgilitySafety
{
    private static readonly ConditionalWeakTable<AgilityActionComponent, FoodComponent> s_food = new();

    [HarmonyPatch(typeof(AgilityActionComponent), "ActionTimerFiredHandler")]
    private static class ObstaclePatch
    {
        private static bool Prefix(AgilityActionComponent __instance)
        {
            if (!Settings.Current.AgilityStopBeforeDeath || __instance.CurrentPath is not { } path) return true;
            int maxHit = (int)path.CurrentObstacle.Stats.Damage.Value;
            var health = __instance.Health;
            if (maxHit <= 0 || health.Current > maxHit) return true;

            var food = FindFood(__instance);
            for (int guard = 0; guard < 100 && health.Current <= maxHit && !health.IsFull && food != null; guard++)
            {
                if (Settings.Current.FoodAutoSwap ? !CombatTweaks.EnsureFood(food)
                    : food.SelectedFood == null || food.Inventory.GetQuantity(food.SelectedFood.Item) <= 0) break;
                int before = health.Current;
                food.ManualEat();
                if (health.Current <= before) break;
            }
            if (health.Current > maxHit) return true;

            string reason = health.Max <= maxHit
                ? $"Stopped: {path.CurrentObstacle.Obstacle.DisplayName} can hit for {maxHit}, more than your max hitpoints"
                : $"Stopped: {path.CurrentObstacle.Obstacle.DisplayName} can hit for {maxHit} and you're out of food";
            Actions.ActionQueue.AutoStopHint = true;
            bool stopped;
            try { stopped = __instance.TryStop(); } finally { Actions.ActionQueue.AutoStopHint = false; }
            if (!stopped) return true; // couldn't stop: let the game carry on as normal
            __instance.EventLog.LogActionStoppedWithReasonEvent(__instance, reason);
            Plugin.Log(reason);
            return false;
        }
    }

    /// <summary>The FoodComponent that heals the same health as Agility (its own entity first, then any).</summary>
    private static FoodComponent? FindFood(AgilityActionComponent agility)
    {
        if (s_food.TryGetValue(agility, out var cached)) return cached;
        bool SameHealth(FoodComponent f) => ReferenceEquals(Traverse.Create(f).Property("Health").GetValue(), agility.Health);

        var found = agility.Entity.Components.Values.OfType<FoodComponent>().FirstOrDefault(SameHealth);
        if (found == null && TimeBank.TimeBank.Game is { } game)
            found = game.Entities.GetAllObjects().SelectMany(e => e.Components.Values).OfType<FoodComponent>().FirstOrDefault(SameHealth);
        if (found != null) s_food.AddOrUpdate(agility, found);
        Plugin.Log($"Agility safety: food component {(found == null ? "not found" : found.Entity.Id)}");
        return found;
    }

    /// <summary>Dev aid: what the check sees right now, without changing anything.</summary>
    internal static string Describe(AgilityActionComponent agility)
    {
        var food = FindFood(agility);
        string next = agility.CurrentPath is { } p ? $"{p.CurrentObstacle.Obstacle.DisplayName} max hit {(int)p.CurrentObstacle.Stats.Damage.Value}" : "no course running";
        string eat = food?.SelectedFood is { } sel ? $"{sel.Item.DisplayName.Singular} x{food.Inventory.GetQuantity(sel.Item)}" : "none selected";
        return $"agility: hp {agility.Health.Current}/{agility.Health.Max}, next {next}, food {eat}";
    }
}
