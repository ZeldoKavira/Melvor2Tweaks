using System.Linq;
using GamesByMalcs.Melvor2.Components.Equipment;
using GamesByMalcs.Melvor2.Components.Inventories;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Bank;
using Godot;
using HarmonyLib;

namespace Melvor2Tweaks.Features;

/// <summary>
/// Double-click equipment in the bank to equip it. The bank grid raises "Selected" on every click of a
/// stack, so two clicks on the same stack within the double-click window (no modifier keys) count.
/// Equipping goes through the same ItemEquippingComponent.EquipItem call as the Selected Item panel's
/// slot buttons, so requirements, swaps and two-handed rules are the game's.
/// </summary>
internal static class BankDoubleClick
{
    private const ulong WindowMs = 400;
    private static ulong s_lastTime;
    private static IReadonlyItemStack? s_lastStack;

    [HarmonyPatch(typeof(ItemStackGridScroller), "StackViewSelectedHandler")]
    private static class SelectedPatch
    {
        private static void Postfix(ItemStackGridScroller __instance, object? sender)
        {
            if (!Settings.Current.BankDoubleClickEquip || sender is not ItemStackButton { Stack: { } stack }) return;
            if (__instance.Query == null && Traverse.Create(__instance).Property("Inventory").GetValue() is not BankTabInventory) return;
            if (Input.IsKeyPressed(Key.Shift) || Input.IsKeyPressed(Key.Ctrl)) { s_lastStack = null; return; }

            ulong now = Time.GetTicksMsec();
            bool isDouble = ReferenceEquals(stack, s_lastStack) && now - s_lastTime <= WindowMs;
            s_lastStack = isDouble ? null : stack; // a third click starts over
            s_lastTime = now;
            if (isDouble) Equip(stack);
        }
    }

    internal static void Equip(IReadonlyItemStack stack, bool dryRun = false)
    {
        var item = stack.Item;
        if (stack.Quantity <= 0 || !item.Capabilities.TryGet<EquipmentCapability>(out var cap)) return;
        if (!cap.Requirements.AreMet) { Plugin.Log($"Double-click equip: requirements not met for {item.Id}"); return; }
        if (ItemRules.Bank?.ItemEquipping is not { } equipping) return;

        var slots = equipping.Equipment.GetAvailableSlotsForItem(item);
        if (slots.Count == 0) return;
        // Same item already worn (ammo tops up), else a free slot that displaces nothing, else the first.
        var target = slots.FirstOrDefault(s => s.Slot.Equipped?.Item == item)
                     ?? slots.FirstOrDefault(s => s.Slot.IsEmpty && s.Replaces.All(r => r.IsEmpty))
                     ?? slots[0];
        int quantity = cap.CanEquipQuantity ? stack.Quantity : 1;
        if (dryRun) { Plugin.Log($"Double-click equip (dry run): {quantity} x {item.Id} -> {target.Slot.Slot.Id}, replaces {target.Replaces.Count}"); return; }
        equipping.EquipItem(item, quantity, target.Slot.Slot);
        Plugin.Log($"Double-click equip: {quantity} x {item.Id} -> {target.Slot.Slot.Id}");
    }
}
