using System.Linq;
using System.Runtime.CompilerServices;
using GamesByMalcs.Melvor2.Components.Actions;
using GamesByMalcs.Melvor2.Components.Characters;
using GamesByMalcs.Melvor2.Components.CombatFood;
using GamesByMalcs.Melvor2.Entities.Agility;
using GamesByMalcs.Melvor2.Entities.Thieving;
using HarmonyLib;

namespace Melvor2Tweaks.Features;

/// <summary>
/// "Stop before dying" for Agility and Thieving. A failed obstacle / pickpocket hits for 1..its damage,
/// and dying costs a random toolbelt item (or 10% of a stack). The game's auto-eat only fires at 25%
/// hitpoints, after a hit, so one big hit can kill even with food. Before each attempt: if the max hit
/// could take the player to 0, eat (the game's ManualEat, from that skill's own food) until it can't;
/// if that's not possible (no food, or the hit exceeds max hitpoints) stop the action instead, as an
/// automatic stop so the action queue moves on.
/// </summary>
internal static class DeathGuard
{
    private static readonly ConditionalWeakTable<ICharacterHealth, FoodComponent> s_food = new();

    [HarmonyPatch(typeof(AgilityActionComponent), "ActionTimerFiredHandler")]
    private static class AgilityPatch
    {
        private static bool Prefix(AgilityActionComponent __instance)
        {
            if (!Settings.Current.AgilityStopBeforeDeath || __instance.CurrentPath is not { } path) return true;
            return Guard(__instance, __instance.Health, (int)path.CurrentObstacle.Stats.Damage.Value, path.CurrentObstacle.Obstacle.DisplayName.Text);
        }
    }

    [HarmonyPatch(typeof(ThievingActionComponent), "ActionTimerFiredHandler")]
    private static class ThievingPatch
    {
        private static bool Prefix(ThievingActionComponent __instance)
        {
            // A stunned tick only ends the stun; no attempt, no hit.
            if (!Settings.Current.ThievingStopBeforeDeath || __instance.CurrentRecipe is not { } recipe
                || __instance.StunState == ThievingStunState.Stunned) return true;
            return Guard(__instance, __instance.Health, recipe.Recipe.Damage, recipe.Recipe.DisplayName.Text);
        }
    }

    /// <summary>True to let the attempt run; false when the action was stopped instead.</summary>
    private static bool Guard(ActionComponent action, ICharacterHealth health, int maxHit, string what)
    {
        if (maxHit <= 0 || health.Current > maxHit) return true;

        var food = FindFood(action, health);
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
            ? $"Stopped: {what} can hit for {maxHit}, more than your max hitpoints"
            : $"Stopped: {what} can hit for {maxHit} and you're out of food";
        Actions.ActionQueue.AutoStopHint = true;
        bool stopped;
        try { stopped = action.TryStop(); } finally { Actions.ActionQueue.AutoStopHint = false; }
        if (!stopped) return true; // couldn't stop: let the game carry on as normal
        action.EventLog.LogActionStoppedWithReasonEvent(action, reason);
        Plugin.Log(reason);
        return false;
    }

    /// <summary>The FoodComponent that heals this health (the skill's own entity first, then any).</summary>
    private static FoodComponent? FindFood(ActionComponent action, ICharacterHealth health)
    {
        if (s_food.TryGetValue(health, out var cached)) return cached;
        bool SameHealth(FoodComponent f) => ReferenceEquals(Traverse.Create(f).Property("Health").GetValue(), health);

        var found = action.Entity.Components.Values.OfType<FoodComponent>().FirstOrDefault(SameHealth);
        if (found == null && TimeBank.TimeBank.Game is { } game)
            found = game.Entities.GetAllObjects().SelectMany(e => e.Components.Values).OfType<FoodComponent>().FirstOrDefault(SameHealth);
        if (found != null) s_food.AddOrUpdate(health, found);
        Plugin.Log($"Death guard: {action.Entity.Id} food component {(found == null ? "not found" : found.Entity.Id)}");
        return found;
    }

    /// <summary>Dev aid: what the check sees right now, without changing anything.</summary>
    internal static string Describe(ActionComponent action)
    {
        (ICharacterHealth? health, string next) = action switch
        {
            AgilityActionComponent a => (a.Health, a.CurrentPath is { } p
                ? $"{p.CurrentObstacle.Obstacle.DisplayName.Text} max hit {(int)p.CurrentObstacle.Stats.Damage.Value}" : "no course running"),
            ThievingActionComponent t => (t.Health, t.CurrentRecipe is { } r
                ? $"{r.Recipe.DisplayName.Text} max hit {r.Recipe.Damage}" : "no target selected"),
            _ => ((ICharacterHealth?)null, "?"),
        };
        if (health == null) return $"{action.Entity.Id}: not guarded";
        var food = FindFood(action, health);
        string eat = food?.SelectedFood is { } sel ? $"{sel.Item.DisplayName.Singular} x{food.Inventory.GetQuantity(sel.Item)}" : "none selected";
        return $"{action.Entity.Id}: hp {health.Current}/{health.Max}, next {next}, food {eat}";
    }
}
