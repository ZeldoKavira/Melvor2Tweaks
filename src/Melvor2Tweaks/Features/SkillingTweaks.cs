using System;
using System.Linq;
using GamesByMalcs.Melvor2.Components.Actions;
using GamesByMalcs.Melvor2.Components.RareEvents;
using GamesByMalcs.Melvor2.Entities.Astrology;
using GamesByMalcs.Melvor2.Entities.Farming;
using GamesByMalcs.Melvor2.Entities.Firemaking;
using GamesByMalcs.Melvor2.Entities.Fishing;
using GamesByMalcs.Melvor2.Entities.Mining;
using GamesByMalcs.Melvor2.Entities.Ranching;
using GamesByMalcs.Melvor2.Entities.Woodcutting;
using HarmonyLib;
using Item = GamesByMalcs.Melvor2.Data.Items.Item;

namespace Melvor2Tweaks.Features;

/// <summary>Bonus auto-activation, side drops, ranching, grain mill and bonfire.</summary>
internal static class SkillingTweaks
{
    // ---- bonus events ------------------------------------------------------------------------------
    // Only freshly found bonuses: ones the player paused themselves are left alone.
    [HarmonyPatch(typeof(RareEventsComponent), nameof(RareEventsComponent.SetActiveBonuses))]
    private static class RareBonusPatch
    {
        private static void Postfix(RareEventsComponent __instance)
        {
            if (Settings.Current.AutoActivateBonuses) __instance.ActiveBonuses?.Activate();
        }
    }

    [HarmonyPatch(typeof(AstrologyActionComponent), nameof(AstrologyActionComponent.SetActiveBonuses))]
    private static class AstrologyBonusPatch
    {
        private static void Postfix(AstrologyActionComponent __instance)
        {
            if (Settings.Current.AutoActivateBonuses) __instance.ActiveBonuses?.Activate();
        }
    }

    // ---- side drops don't stop gathering -----------------------------------------------------------
    [HarmonyPatch(typeof(ActionComponent), "TryAddItem")]
    private static class SideDropPatch
    {
        private static void Prefix(ActionComponent __instance, out bool __state) => __state = __instance.QueueStop;

        private static void Postfix(ActionComponent __instance, Item item, int __result, bool __state)
        {
            if (__result > 0 || __state || !__instance.QueueStop || !Settings.Current.IgnoreSideDropBankFull) return;
            if (IsSideDrop(__instance, item))
                Traverse.Create(__instance).Property("QueueStop").SetValue(false);
        }
    }

    internal static bool IsSideDrop(ActionComponent action, Item item) => action switch
    {
        WoodcuttingActionComponent wc => wc.CuttingTree != null && item != wc.CuttingTree.Recipe.Product.Item,
        MiningActionComponent m => m.CurrentRock != null && item != m.CurrentRock.Recipe.Product.Item,
        FishingActionComponent f => f.CurrentRecipe != null &&
                                    (item == f.CurrentRecipe.Recipe.JunkItem ||
                                     f.CurrentRecipe.Stats.SpecialProductChances.Values.Any(c => c.Value.Item == item)),
        _ => false,
    };

    // ---- ranching ----------------------------------------------------------------------------------
    [HarmonyPatch(typeof(ActiveRanchingAnimal), nameof(ActiveRanchingAnimal.OnPetTicksDepleted))]
    private static class PetTicksPatch
    {
        private static void Postfix(ActiveRanchingAnimal __instance) => AutoPet(__instance);
    }

    [HarmonyPatch(typeof(ActiveRanchingAnimal), "PetTimerFiredHandler")]
    private static class PetTimerPatch
    {
        private static void Postfix(ActiveRanchingAnimal __instance) => AutoPet(__instance);
    }

    private static void AutoPet(ActiveRanchingAnimal animal)
    {
        if (Settings.Current.RanchingAutoPet && animal.WantsPets && animal.Recipe.RequiresPets) animal.Pet();
    }

    [HarmonyPatch(typeof(ActiveRanchingEnclosure), nameof(ActiveRanchingEnclosure.OnMaintenanceTimerFired))]
    private static class RepairPatch
    {
        private static void Postfix(ActiveRanchingEnclosure __instance)
        {
            var s = Settings.Current;
            if (s.RanchingAutoRepair && __instance.Maintenance < s.RanchingRepairBelow) __instance.Repair();
        }
    }

    // ---- grain mill --------------------------------------------------------------------------------
    [HarmonyPatch(typeof(FarmingGrainMillComponent), "GrainTimerFiredHandler")]
    private static class GrainTimerPatch
    {
        private static void Prefix(FarmingGrainMillComponent __instance) => TopUp(__instance);
    }

    [HarmonyPatch(typeof(FarmingGrainMillComponent), "InventoryItemQuantityChangedHandler")]
    private static class GrainStockPatch
    {
        private static void Prefix(FarmingGrainMillComponent __instance) => TopUp(__instance);
    }

    private static bool s_toppingUp;

    private static void TopUp(FarmingGrainMillComponent mill)
    {
        var recipe = mill.SelectedRecipe;
        if (!Settings.Current.GrainMillAutoTopUp || s_toppingUp || recipe == null || mill.CanCreateRecipe(recipe)) return;
        s_toppingUp = true; // deposits raise ItemQuantityChanged, which re-enters here
        try
        {
            foreach (var cost in recipe.Stats.ItemCost.Values)
                mill.DepositItemToGrainMill(cost.Value.Item, int.MaxValue);
        }
        finally { s_toppingUp = false; }
    }

    // ---- bonfire -----------------------------------------------------------------------------------
    [HarmonyPatch(typeof(FiremakingBonfireComponent), nameof(FiremakingBonfireComponent.Tick))]
    private static class BonfirePatch
    {
        private static void Postfix(FiremakingBonfireComponent __instance)
        {
            if (!Settings.Current.BonfireAutoRelight || !__instance.IsActive || __instance.DurationTimer.TimeLeft > 0) return;
            int logs = __instance.GetLogsToAdd(1.0);
            if (logs > 0) __instance.AddLogs(logs); // fails silently (no-op) if the logs aren't in the bank
        }
    }
}
