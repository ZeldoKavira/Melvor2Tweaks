using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Inventories;
using GamesByMalcs.Melvor2.Entities.Bank;
using Godot;
using HarmonyLib;
using Item = GamesByMalcs.Melvor2.Data.Items.Item;

namespace Melvor2Tweaks.Features;

/// <summary>
/// Junk list: each junk item has a "keep" minimum; anything in the bank above it is sold - right away
/// when the item is marked or its minimum lowered, and on every drop after that. Sales go through the
/// game's own ItemSaleComponent, so price, stats and "item found" stay correct. Only drops
/// (AddItemFlags.Found) trigger sales, not items moved back into the bank; locked items are never sold.
/// If the bank is full and the item has no stack yet, the drop is sold directly instead of being lost.
/// Managed from the bank's sell panel (checkbox) and Settings > Melvor2Tweaks > Junk.
/// </summary>
internal static class JunkSell
{
    private const string CheckName = "Melvor2TweaksJunkCheck";

    public static event Action? ListChanged;

    internal static bool IsJunk(Item item) => Settings.Current.Junk.ContainsKey(item.Id);

    internal static int Keep(Item item) => Settings.Current.Junk.TryGetValue(item.Id, out int keep) ? keep : 0;

    internal static void SetJunk(Item item, bool junk)
    {
        var junkMap = Settings.Current.Junk;
        if (junk) junkMap.TryAdd(item.Id, 0);
        else junkMap.Remove(item.Id);
        Settings.Save();
        Plugin.Log($"Junk {(junk ? "+" : "-")} {item.Id}");
        if (junk) SellExcess(item);
        ListChanged?.Invoke();
    }

    internal static void SetKeep(Item item, int keep)
    {
        if (!IsJunk(item)) return;
        Settings.Current.Junk[item.Id] = Math.Max(0, keep);
        Settings.Save();
        SellExcess(item);
    }

    private static BankEntity? Bank =>
        TimeBank.TimeBank.Game is Game g ? Traverse.Create(g).Property("Bank").GetValue<BankEntity>() : null;

    internal static int InBank(Item item) => Bank?.Inventory.GetQuantity(item) ?? 0;

    private static bool IsLocked(BankInventoryComponent bank, Item item) =>
        Traverse.Create(bank).Property("LockedItems").GetValue() is ICollection<Item> locked && locked.Contains(item);

    /// <summary>Sells whatever is in the bank above the item's keep minimum. Returns the number sold.</summary>
    internal static int SellExcess(Item item)
    {
        if (!IsJunk(item) || Bank is not BankEntity bank || IsLocked(bank.Inventory, item)) return 0;
        int excess = bank.Inventory.GetQuantity(item) - Keep(item);
        if (excess <= 0) return 0;
        bank.ItemSales.SellItem(item, bank.ItemSales.GetItemStats(item), excess);
        Plugin.Log($"Junk: sold {excess} x {item.Id}");
        return excess;
    }

    internal static int SellAllExcess()
    {
        int sold = 0;
        foreach (var item in JunkItems()) sold += SellExcess(item);
        return sold;
    }

    internal static IEnumerable<Item> JunkItems()
    {
        var game = TimeBank.TimeBank.Game;
        if (game == null) yield break;
        foreach (string id in Settings.Current.Junk.Keys.ToList())
            if (game.Data.TryGetObject<Item>(id, out var item)) yield return item;
    }

    /// <summary>Finds an item by name: exact match first, then the first name containing the text.</summary>
    internal static Item? FindItem(string text)
    {
        var game = TimeBank.TimeBank.Game;
        text = text.Trim();
        if (game == null || text.Length == 0) return null;
        var all = game.Data.GetDataRegistryOutput<Item>().GetAllObjects().ToList();
        return all.FirstOrDefault(i => string.Equals(i.DisplayName.Singular, text, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(i => i.DisplayName.Singular.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    [HarmonyPatch(typeof(BankInventoryComponent), nameof(BankInventoryComponent.AddQuantity))]
    private static class AddPatch
    {
        private static bool Prefix(BankInventoryComponent __instance, Item item, int quantity, AddItemFlags options, ref int __result)
        {
            if (quantity <= 0 || !options.HasFlag(AddItemFlags.Found) || !IsJunk(item)) return true;
            // No room for a new stack: sell it outright instead of losing it (nothing can be kept anyway).
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

        private static void Postfix(Item item, AddItemFlags options, int __result)
        {
            if (__result > 0 && options.HasFlag(AddItemFlags.Found) && IsJunk(item)) SellExcess(item);
        }
    }

    // ---- bank sell panel checkbox ------------------------------------------------------------------
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

    // Stock already above the minimum (e.g. bought, or marked before a game update) is sold when a save opens.
    [HarmonyPatch(typeof(GamesByMalcs.Melvor2.GameMenus.GameView), nameof(GamesByMalcs.Melvor2.GameMenus.GameView.OnFirstShow))]
    private static class SaveOpenedPatch
    {
        private static void Postfix() => SellAllExcess();
    }

    internal static void AttachToExisting()
    {
        SellAllExcess();
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
        ListChanged = null;
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
            Text = "Junk: auto-sell",
            AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled,
            FocusMode = Control.FocusModeEnum.None,
        };
        UI.UiKit.AddTooltip(check, "Junk",
            "Sell this item automatically: what's in the bank now and every future drop. " +
            "Set a minimum to keep in Settings > Melvor2Tweaks > Junk.");
        check.Toggled += on =>
        {
            if (s_current.TryGetValue(options, out var box) && box.Value != null) SetJunk(box.Value, on);
        };
        parent.AddChild(check);
        parent.MoveChild(check, sell!.GetIndex() + 1);
    }
}
