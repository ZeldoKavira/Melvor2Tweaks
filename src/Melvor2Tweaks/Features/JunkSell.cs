using System.Collections.Generic;
using System.Runtime.CompilerServices;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Inventories;
using GamesByMalcs.Melvor2.Entities.Bank;
using Godot;
using HarmonyLib;
using Item = GamesByMalcs.Melvor2.Data.Items.Item;

namespace Melvor2Tweaks.Features;

/// <summary>
/// Junk list: items marked junk (checkbox under the bank's Sell button) are sold as they drop,
/// through the game's own ItemSaleComponent so sale price, stats and "item found" all stay correct.
/// Only drops are sold (AddItemFlags.Found), not items moved back into the bank. If the bank is full
/// and the junk item has no stack yet, it is sold directly instead of being lost.
/// </summary>
internal static class JunkSell
{
    private const string CheckName = "Melvor2TweaksJunkCheck";

    internal static bool IsJunk(Item item) => Settings.Current.JunkItems.Contains(item.Id);

    internal static void SetJunk(Item item, bool junk)
    {
        var list = Settings.Current.JunkItems;
        if (junk && !list.Contains(item.Id)) list.Add(item.Id);
        if (!junk) list.Remove(item.Id);
        Settings.Save();
        Plugin.Log($"Junk {(junk ? "+" : "-")} {item.Id}");
    }

    private static BankEntity? Bank =>
        TimeBank.TimeBank.Game is Game g ? Traverse.Create(g).Property("Bank").GetValue<BankEntity>() : null;

    [HarmonyPatch(typeof(BankInventoryComponent), nameof(BankInventoryComponent.AddQuantity))]
    private static class AddPatch
    {
        private static bool Prefix(BankInventoryComponent __instance, Item item, int quantity, AddItemFlags options, ref int __result)
        {
            if (quantity <= 0 || !options.HasFlag(AddItemFlags.Found) || !IsJunk(item)) return true;
            // No room for a new stack: sell it outright instead of losing it.
            if (__instance.GetQuantity(item) <= 0 && __instance.StackCount >= __instance.MaxStacks && Bank is BankEntity bank)
            {
                var stats = bank.ItemSales.GetItemStats(item);
                var wallet = Traverse.Create(bank.ItemSales).Property("Wallet")
                    .GetValue<GamesByMalcs.Melvor2.Components.CurrencyWallets.ICurrencyWallet>();
                if (wallet == null) return true;
                wallet.AddQuantity(item.SellsFor.Currency, stats.PricePerItem.Value * quantity);
                __result = quantity;
                return false;
            }
            return true;
        }

        private static void Postfix(BankInventoryComponent __instance, Item item, int quantity, AddItemFlags options, int __result)
        {
            if (__result <= 0 || !options.HasFlag(AddItemFlags.Found) || !IsJunk(item)) return;
            if (Traverse.Create(__instance).Property("LockedItems").GetValue() is ICollection<Item> locked && locked.Contains(item)) return;
            if (Bank is not BankEntity bank) return;
            bank.ItemSales.SellItem(item, bank.ItemSales.GetItemStats(item), __result);
        }
    }

    // ---- UI: checkbox in the bank's sell panel ---------------------------------------------------
    private static readonly ConditionalWeakTable<SellItemOptions, StrongBox<Item?>> s_current = new();

    [HarmonyPatch(typeof(SellItemOptions), nameof(SellItemOptions._Ready))]
    private static class ReadyPatch
    {
        private static void Postfix(SellItemOptions __instance) => Attach(__instance);
    }

    [HarmonyPatch(typeof(SellItemOptions), nameof(SellItemOptions.BindItemStack))]
    private static class BindPatch
    {
        private static void Postfix(SellItemOptions __instance, IReadonlyItemStack stack)
        {
            s_current.GetOrCreateValue(__instance).Value = stack.Item;
            if (Find(__instance) is CheckBox check) check.SetPressedNoSignal(IsJunk(stack.Item));
        }
    }

    internal static void AttachToExisting()
    {
        foreach (var options in UI.NodeSearch.All<SellItemOptions>())
        {
            Attach(options);
            var stack = Traverse.Create(options).Property("Stack").GetValue<IReadonlyItemStack>();
            if (stack != null)
            {
                s_current.GetOrCreateValue(options).Value = stack.Item;
                if (Find(options) is CheckBox check) check.SetPressedNoSignal(IsJunk(stack.Item));
            }
        }
    }

    internal static void DetachAll()
    {
        foreach (var options in UI.NodeSearch.All<SellItemOptions>())
        {
            if (Find(options) is not Node check) continue;
            check.GetParent().RemoveChild(check);
            check.QueueFree();
        }
    }

    private static Node? Find(SellItemOptions options) =>
        Traverse.Create(options).Property("SellButton").GetValue<Control>()?.GetParent()?.GetNodeOrNull(CheckName);

    private static void Attach(SellItemOptions options)
    {
        var sell = Traverse.Create(options).Property("SellButton").GetValue<Control>();
        var parent = sell?.GetParent();
        if (parent == null || parent.GetNodeOrNull(CheckName) != null) return;

        var check = new CheckBox
        {
            Name = CheckName,
            Text = "Junk: auto-sell when found",
            AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled,
            FocusMode = Control.FocusModeEnum.None,
        };
        UI.UiKit.AddTooltip(check, "Junk", "Sell this item automatically every time it drops (Melvor2Tweaks).");
        check.Toggled += on =>
        {
            if (s_current.TryGetValue(options, out var box) && box.Value != null) SetJunk(box.Value, on);
        };
        parent.AddChild(check);
        parent.MoveChild(check, sell!.GetIndex() + 1);
    }
}
