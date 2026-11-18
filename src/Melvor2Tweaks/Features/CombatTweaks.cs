using System.Linq;
using System.Runtime.CompilerServices;
using GamesByMalcs.Melvor2.Components.CombatFood;
using GamesByMalcs.Melvor2.Components.Characters;
using GamesByMalcs.Melvor2.Entities.Combat;
using HarmonyLib;

namespace Melvor2Tweaks.Features;

/// <summary>Flee before dying, food auto-swap, and skipping pause-on-spawn.</summary>
internal static class CombatTweaks
{
    // ---- food auto-swap (all FoodComponents: combat, agility, thieving) ---------------------------
    [HarmonyPatch(typeof(FoodComponent), "AutoEat")]
    private static class FoodSwapPatch
    {
        private static void Prefix(FoodComponent __instance)
        {
            if (Settings.Current.FoodAutoSwap) EnsureFood(__instance);
        }
    }

    /// <summary>If the selected food is used up, selects the best remaining one. Returns true if food is available.</summary>
    internal static bool EnsureFood(FoodComponent food)
    {
        if (food.SelectedFood != null && food.Inventory.GetQuantity(food.SelectedFood.Item) > 0) return true;

        var next = food.Inventory.Stacks
            .Where(s => s.Quantity > 0 && !food.IsItemHidden(s.Item)
                        && s.Item.Capabilities.TryGet<FoodCapability>(out _))
            .Select(s => (s.Item, cap: GetFood(s.Item)))
            .OrderByDescending(x => food.IsItemFavourited(x.Item))
            .ThenByDescending(x => x.cap.BaseHealing)
            .FirstOrDefault();
        if (next.Item == null) return false;

        string? old = food.SelectedFood?.Item.DisplayName.Singular;
        food.SelectFood(next.Item);
        Plugin.Log($"Food swap: {old ?? "none"} -> {next.Item.DisplayName.Singular}");
        return true;
    }

    private static FoodCapability GetFood(GamesByMalcs.Melvor2.Data.Items.Item item)
    {
        item.Capabilities.TryGet<FoodCapability>(out var cap);
        return cap!;
    }

    // ---- flee ------------------------------------------------------------------------------------
    private static readonly ConditionalWeakTable<PlayerComponent, object> s_cantAttack = new();

    [HarmonyPatch(typeof(PlayerComponent), "CanAttack")]
    private static class CanAttackPatch
    {
        private static void Postfix(PlayerComponent __instance, bool __result)
        {
            if (!__result) s_cantAttack.AddOrUpdate(__instance, true);
        }
    }

    [HarmonyPatch(typeof(CombatActionComponent), "OnTick")]
    private static class CombatTickPatch
    {
        private static void Postfix(CombatActionComponent __instance)
        {
            var s = Settings.Current;
            if (!__instance.IsStarted || __instance.State != CombatActionState.Fighting) { s_cantAttack.Remove(__instance.Player); return; }

            string? reason = null;
            if (s.FleeWhenCantAttack && s_cantAttack.TryGetValue(__instance.Player, out _))
                reason = "Fled: out of ammo or runes";
            else if (s.FleeEnabled)
            {
                var health = __instance.Player.Health;
                if (health.Max > 0 && health.Current <= health.Max * s.FleeHpFraction && !HasFood(__instance))
                    reason = $"Fled: hitpoints below {s.FleeHpFraction:P0} with no food";
            }
            s_cantAttack.Remove(__instance.Player);
            if (reason == null) return;

            Actions.ActionQueue.AutoStopHint = true; // a flee counts as an automatic stop for the queue
            bool stopped;
            try { stopped = __instance.TryStop(); } finally { Actions.ActionQueue.AutoStopHint = false; }
            if (stopped)
            {
                __instance.EventLog.LogActionStoppedWithReasonEvent(__instance, reason);
                Plugin.Log(reason);
            }
        }
    }

    private static bool HasFood(CombatActionComponent combat)
    {
        foreach (var component in combat.Entity.Components.Values)
        {
            if (component is not CombatFoodComponent food) continue;
            if (Settings.Current.FoodAutoSwap ? EnsureFood(food)
                : food.SelectedFood != null && food.Inventory.GetQuantity(food.SelectedFood.Item) > 0)
                return true;
        }
        return false;
    }

    // ---- pause on spawn ----------------------------------------------------------------------------
    [HarmonyPatch(typeof(CombatActionComponent), "SpawnTimerFiredHandler")]
    private static class SpawnPausePatch
    {
        private static void Postfix(CombatActionComponent __instance)
        {
            if (Settings.Current.BossAutoUnpause && __instance.IsPaused) __instance.Unpause();
        }
    }
}
