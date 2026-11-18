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

/// <summary>What Keep starts at for an item that has no rule yet.</summary>
public enum KeepDefault { Zero, One, AllButOne, All }

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

    /// <summary>Keep amount offered for an item without a rule (setting "Default Keep for new rules").</summary>
    internal static int DefaultKeep(Item item) => Settings.Current.NewRuleKeep switch
    {
        KeepDefault.One => 1,
        KeepDefault.AllButOne => Math.Max(0, InBank(item) - 1),
        KeepDefault.All => InBank(item),
        _ => 0,
    };

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

    private static Node? FindPicker(SellItemOptions options) => options.FindChild(PickerName, recursive: true, owned: false);

    private static void Bind(SellItemOptions options, Item item)
    {
        s_current.GetOrCreateValue(options).Value = item;
        if (FindPicker(options) is not Node line) return;
        var picker = line.GetNode<OptionButton>("Action");
        var keep = line.GetNode<SpinBox>("Keep");
        picker.Clear();
        foreach (var action in ActionsFor(item)) picker.AddItem(action == RuleAction.Off ? "Auto: off" : $"Auto-{action.ToString().ToLowerInvariant()}", (int)action);
        var rule = Get(item);
        picker.Select(picker.GetItemIndex((int)(rule?.Action ?? RuleAction.Off)));
        // No rule yet: Keep starts at the configured default (0 / 1 / all but 1 / all owned).
        keep.SetValueNoSignal(rule?.Keep ?? DefaultKeep(item));
        keep.SetMeta(UntouchedMeta, rule == null);
    }

    // Set while Keep still shows the owned-count prefill; drops can raise the count before an action is picked.
    private const string UntouchedMeta = "melvor2tweaks_untouched";

    private static void AttachPicker(SellItemOptions options)
    {
        if (FindPicker(options) != null) return;
        var sell = Traverse.Create(options).Property("SellButton").GetValue<Control>();
        if (sell?.GetParent() is not Control sellRow) return;
        // Own line under the Sell row when it sits in a vertical list, else beside the Sell button.
        var parent = sellRow.GetParent() is VBoxContainer column ? column : (Node)sellRow;
        var after = parent == sellRow ? sell : sellRow;

        var line = new HBoxContainer { Name = PickerName };
        line.AddThemeConstantOverride("separation", 8);

        var picker = new OptionButton { Name = "Action", FocusMode = Control.FocusModeEnum.None, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        UI.UiKit.AddTooltip(picker, "Item automation",
            "Sell, open or bury this item automatically above the Keep amount: what's in the bank now and every future drop. " +
            "Every item can be managed in the top bar's Items menu.");
        line.AddChild(picker);

        var keepLabel = UI.TopBar.RawLabel("Keep", 14, null);
        keepLabel.VerticalAlignment = VerticalAlignment.Center;
        line.AddChild(keepLabel);

        // The game's own quantity box from this panel, for the native look.
        var template = Traverse.Create(Traverse.Create(options).Property("QuantityOptions").GetValue())
            .Property("QuantitySpinbox").GetValue<SpinBox>();
        var keep = template != null ? (SpinBox)template.Duplicate((int)Node.DuplicateFlags.Groups) : new SpinBox();
        keep.Name = "Keep";
        keep.MinValue = 0; keep.MaxValue = 1_000_000_000; keep.Step = 1;
        keep.AllowGreater = false; keep.AllowLesser = false;
        keep.Prefix = ""; keep.Suffix = "";
        keep.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
        keep.CustomMinimumSize = new Vector2(Math.Max(keep.CustomMinimumSize.X, 110), keep.CustomMinimumSize.Y);
        UI.UiKit.AddTooltip(keep, "Keep", "How many to leave in the bank. Starts at the Default Keep setting; set it before picking an action.");
        line.AddChild(keep);

        picker.ItemSelected += index =>
        {
            if (!s_current.TryGetValue(options, out var box) || box.Value == null) return;
            if (keep.GetMeta(UntouchedMeta, false).AsBool()) keep.SetValueNoSignal(DefaultKeep(box.Value));
            keep.SetMeta(UntouchedMeta, false);
            Set(box.Value, (RuleAction)picker.GetItemId((int)index), (int)keep.Value);
        };
        keep.ValueChanged += value =>
        {
            if (!s_current.TryGetValue(options, out var box) || box.Value == null) return;
            keep.SetMeta(UntouchedMeta, false);
            if (Get(box.Value) is { } rule) Set(box.Value, rule.Action, (int)value); // no rule yet: just staged
        };
        parent.AddChild(line);
        parent.MoveChild(line, after.GetIndex() + 1);
    }
}
