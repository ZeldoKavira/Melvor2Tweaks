using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Inventories;
using GamesByMalcs.Melvor2.Components.ItemBurying;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Bank;
using GamesByMalcs.Melvor2.GameMenus;
using Godot;
using HarmonyLib;

namespace Melvor2Tweaks.Features;

public enum RuleAction { Off, Sell, Open, Bury }

public sealed class ItemRule
{
    public RuleAction Action { get; set; }
    /// <summary>How many to keep in the bank; only the excess is sold / opened / buried.</summary>
    public int Keep { get; set; }
}

/// <summary>
/// Per-item automation: Sell, Open or Bury everything above a "keep" minimum. Runs when a rule is set or
/// its minimum lowered, on every drop (AddItemFlags.Found - not items moved back into the bank), and when
/// a save opens. All three use the game's own bank components (ItemSales / ItemOpening / ItemBurying),
/// so prices, drop tables, prayer points and statistics are exactly the game's. Locked items are skipped.
/// Opening adds the contents to the bank, which runs their rules too (re-entry is queued, not nested).
/// </summary>
internal static class ItemRules
{
    public static event Action? Changed;

    internal static bool CanOpen(Item item) => item.Capabilities.TryGet<OpenableCapability>(out _);
    internal static bool CanBury(Item item) => item.Capabilities.TryGet<BuryCapability>(out _);

    internal static IEnumerable<RuleAction> ActionsFor(Item item)
    {
        yield return RuleAction.Off;
        yield return RuleAction.Sell;
        if (CanOpen(item)) yield return RuleAction.Open;
        if (CanBury(item)) yield return RuleAction.Bury;
    }

    internal static ItemRule? Get(Item item) => Settings.Current.ItemRules.TryGetValue(item.Id, out var r) ? r : null;

    internal static void Set(Item item, RuleAction action, int keep)
    {
        var rules = Settings.Current.ItemRules;
        if (action == RuleAction.Off) rules.Remove(item.Id);
        else rules[item.Id] = new ItemRule { Action = action, Keep = Math.Max(0, keep) };
        Settings.Save();
        Plugin.Log($"Item rule: {item.Id} = {action} keep {keep}");
        Apply(item);
        Changed?.Invoke();
    }

    internal static BankEntity? Bank =>
        TimeBank.TimeBank.Game is Game g ? Traverse.Create(g).Property("Bank").GetValue<BankEntity>() : null;

    internal static int InBank(Item item) => Bank?.Inventory.GetQuantity(item) ?? 0;

    private static bool IsLocked(BankInventoryComponent bank, Item item) =>
        Traverse.Create(bank).Property("LockedItems").GetValue() is ICollection<Item> locked && locked.Contains(item);

    // ---- applying ----------------------------------------------------------------------------------
    private static int s_depth;
    private static readonly HashSet<Item> s_pending = new();

    /// <summary>Applies the item's rule to whatever is above its keep minimum. Returns how many were handled.</summary>
    internal static int Apply(Item item)
    {
        if (s_depth > 0) { s_pending.Add(item); return 0; } // e.g. contents arriving while a chest is opened
        s_depth++;
        int handled = 0;
        try
        {
            handled = ApplyNow(item);
            for (int guard = 0; s_pending.Count > 0 && guard < 50; guard++)
            {
                var next = s_pending.First();
                s_pending.Remove(next);
                handled += ApplyNow(next);
            }
        }
        catch (Exception e) { Plugin.Log($"Item rule {item.Id} failed: {e.Message}"); }
        finally { s_depth--; s_pending.Clear(); }
        return handled;
    }

    private static int ApplyNow(Item item)
    {
        var rule = Get(item);
        if (rule == null || Bank is not BankEntity bank || IsLocked(bank.Inventory, item)) return 0;
        int excess = bank.Inventory.GetQuantity(item) - rule.Keep;
        if (excess <= 0) return 0;

        switch (rule.Action)
        {
            case RuleAction.Sell:
                bank.ItemSales.SellItem(item, bank.ItemSales.GetItemStats(item), excess);
                break;
            case RuleAction.Open:
                if (!CanOpen(item)) return 0;
                excess = bank.ItemOpening.OpenItem(item, excess).Count; // stops early if the bank fills
                break;
            case RuleAction.Bury:
                if (!CanBury(item)) return 0;
                bank.ItemBurying.BuryItem(item, bank.ItemBurying.GetItemStats(item), excess);
                break;
            default:
                return 0;
        }
        if (excess > 0) Plugin.Log($"Item rule: {rule.Action} {excess} x {item.Id}");
        return excess;
    }

    internal static int ApplyAll()
    {
        var game = TimeBank.TimeBank.Game;
        if (game == null) return 0;
        int handled = 0;
        foreach (string id in Settings.Current.ItemRules.Keys.ToList())
            if (game.Data.TryGetObject<Item>(id, out var item)) handled += Apply(item);
        return handled;
    }

    [HarmonyPatch(typeof(BankInventoryComponent), nameof(BankInventoryComponent.AddQuantity))]
    private static class AddPatch
    {
        private static bool Prefix(BankInventoryComponent __instance, Item item, int quantity, AddItemFlags options, ref int __result)
        {
            if (quantity <= 0 || !options.HasFlag(AddItemFlags.Found) || Get(item) is not { Action: RuleAction.Sell }) return true;
            // Bank full and no stack to add to: sell the drop outright instead of losing it.
            if (__instance.GetQuantity(item) <= 0 && __instance.StackCount >= __instance.MaxStacks && Bank is BankEntity bank)
            {
                var wallet = Traverse.Create(bank.ItemSales).Property("Wallet")
                    .GetValue<GamesByMalcs.Melvor2.Components.CurrencyWallets.ICurrencyWallet>();
                if (wallet == null) return true;
                wallet.AddQuantity(item.SellsFor.Currency, bank.ItemSales.GetItemStats(item).PricePerItem.Value * quantity);
                __result = quantity;
                return false;
            }
            return true;
        }

        private static void Postfix(Item item, AddItemFlags options, int __result)
        {
            if (__result > 0 && options.HasFlag(AddItemFlags.Found) && Get(item) != null) Apply(item);
        }
    }

    [HarmonyPatch(typeof(GameView), nameof(GameView.OnFirstShow))]
    private static class SaveOpenedPatch
    {
        private static void Postfix() => ApplyAll();
    }

    // ---- bank: rule picker in the selected item's Sell panel ---------------------------------------
    private const string PickerName = "Melvor2TweaksItemRule";
    private static readonly ConditionalWeakTable<SellItemOptions, StrongBox<Item?>> s_current = new();

    [HarmonyPatch(typeof(SellItemOptions), nameof(SellItemOptions._Ready))]
    private static class ReadyPatch
    {
        private static void Postfix(SellItemOptions __instance) => AttachPicker(__instance);
    }

    [HarmonyPatch(typeof(SellItemOptions), nameof(SellItemOptions.BindItemStack))]
    private static class BindPatch
    {
        private static void Postfix(SellItemOptions __instance, IReadonlyItemStack stack) => Bind(__instance, stack.Item);
    }

    internal static void AttachToExisting()
    {
        ApplyAll();
        foreach (var options in UI.NodeSearch.All<SellItemOptions>())
        {
            AttachPicker(options);
            var stack = Traverse.Create(options).Property("Stack").GetValue<IReadonlyItemStack>();
            if (stack != null) Bind(options, stack.Item);
        }
    }

    internal static void DetachAll()
    {
        Changed = null;
        foreach (var options in UI.NodeSearch.All<SellItemOptions>())
        {
            if (FindPicker(options) is not Node picker) continue;
            picker.GetParent().RemoveChild(picker);
            picker.QueueFree();
        }
    }

    private static Node? FindPicker(SellItemOptions options) =>
        Traverse.Create(options).Property("SellButton").GetValue<Control>()?.GetParent()?.GetNodeOrNull(PickerName);

    private static void Bind(SellItemOptions options, Item item)
    {
        s_current.GetOrCreateValue(options).Value = item;
        if (FindPicker(options) is not OptionButton picker) return;
        picker.Clear();
        foreach (var action in ActionsFor(item)) picker.AddItem(action == RuleAction.Off ? "Auto: off" : $"Auto-{action.ToString().ToLowerInvariant()}", (int)action);
        picker.Select(picker.GetItemIndex((int)(Get(item)?.Action ?? RuleAction.Off)));
    }

    private static void AttachPicker(SellItemOptions options)
    {
        var sell = Traverse.Create(options).Property("SellButton").GetValue<Control>();
        var parent = sell?.GetParent();
        if (parent == null || parent.GetNodeOrNull(PickerName) != null) return;

        var picker = new OptionButton { Name = PickerName, FocusMode = Control.FocusModeEnum.None };
        UI.UiKit.AddTooltip(picker, "Item automation",
            "Sell, open or bury this item automatically: what's in the bank now and every future drop. " +
            "Set how many to keep, and manage every item, in the top bar's Items menu.");
        picker.ItemSelected += index =>
        {
            if (!s_current.TryGetValue(options, out var box) || box.Value == null) return;
            var action = (RuleAction)picker.GetItemId((int)index);
            Set(box.Value, action, Get(box.Value)?.Keep ?? 0);
        };
        parent.AddChild(picker);
        parent.MoveChild(picker, sell!.GetIndex() + 1);
    }
}
