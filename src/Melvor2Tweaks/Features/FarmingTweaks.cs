using System.Collections.Generic;
using System.Reflection.Emit;
using GamesByMalcs.Melvor2.Components.Inventories;
using GamesByMalcs.Melvor2.Entities.Farming;
using HarmonyLib;

namespace Melvor2Tweaks.Features;

/// <summary>
/// Farming automation only runs on a plot state change, so a plot stuck on "can't afford seeds" or
/// "bank full" never retries. This retries every few seconds of game time, and never pays the
/// auto-harvest cost while the crop can't fit in the bank.
/// </summary>
internal static class FarmingTweaks
{
    // Every 5 s of game time (the game runs 60 ticks/s; read it rather than assume).
    private static int RetryEveryTicks => GamesByMalcs.Melvor2.Core.Constants.TicksPerSecond * 5;
    private static int s_counter;

    [HarmonyPatch(typeof(FarmingPassiveComponent), "OnTick")]
    private static class RetryPatch
    {
        private static void Postfix(FarmingPassiveComponent __instance)
        {
            if (!Settings.Current.FarmingRetryAutomation || ++s_counter < RetryEveryTicks) return;
            s_counter = 0;
            foreach (ActiveFarmingPlot plot in __instance.ActivePlots.Values)
            {
                if (plot.CanHarvest ? plot.AutoHarvest && CropFits(plot)
                                    : plot.CanPlant && plot.AutoPlant && plot.CanPlantSelectedRecipe())
                    Traverse.Create(plot).Method("PerformStateChangeAutomation").GetValue();
            }
        }
    }

    [HarmonyPatch(typeof(ActiveFarmingPlot), "PerformAutoHarvest")]
    private static class HarvestGuardPatch
    {
        // Skipping here keeps the automation cost from being paid for a harvest that would fail.
        private static bool Prefix(ActiveFarmingPlot __instance) =>
            !Settings.Current.FarmingRetryAutomation || !__instance.CanHarvest || CropFits(__instance);
    }

    private static bool CropFits(ActiveFarmingPlot plot)
    {
        var product = plot.ReadyToHarvestProduct;
        if (product == null) return true;
        var component = Traverse.Create(plot).Property("Component").GetValue();
        if (Traverse.Create(component).Property("Inventory").GetValue() is not IInventory bank) return true;
        return bank.GetQuantity(product.Item) > 0 || bank.StackCount < bank.MaxStacks;
    }
}

/// <summary>Raises the event log's hard-coded 100-entry cap.</summary>
[HarmonyPatch(typeof(GamesByMalcs.Melvor2.EventLog.EventLogManager), nameof(GamesByMalcs.Melvor2.EventLog.EventLogManager.LogEvent))]
internal static class EventLogCapPatch
{
    public static int Cap() => System.Math.Max(100, Settings.Current.EventLogCap);

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        int replaced = 0;
        foreach (var ci in instructions)
        {
            if ((ci.opcode == OpCodes.Ldc_I4_S || ci.opcode == OpCodes.Ldc_I4) && System.Convert.ToInt32(ci.operand) == 100)
            {
                replaced++;
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(EventLogCapPatch), nameof(Cap))).MoveLabelsFrom(ci);
            }
            else yield return ci;
        }
        if (replaced != 1) Plugin.Log($"EventLogCap: expected 1 constant, replaced {replaced}");
    }
}
