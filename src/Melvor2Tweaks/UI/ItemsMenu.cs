using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2.CustomControls;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.GameMenus;
using Godot;
using HarmonyLib;
using Melvor2Tweaks.Features;

namespace Melvor2Tweaks.UI;

/// <summary>
/// "Items" header entry: per-item automation (Sell / Open / Bury above a keep minimum) for any number
/// of items. Search + filter (in bank / with a rule / all items) and only the first matches are built
/// as rows, so it stays fast with the game's full item list. Rows aren't rebuilt when a rule changes,
/// only when the search or filter does.
/// </summary>
internal static class ItemsMenu
{
    private const string ButtonScene = "res://Components/Statistics/header_statistics_button.tscn";
    private const string ButtonName = "Melvor2TweaksItemsButton";
    private const string PopupName = "Melvor2TweaksItemsPopup";
    private const int MaxRows = 80;

    private enum Filter { Mine, WithRule, All, Categories }

    private static PanelButtonContainer? s_button;
    private static PanelContainerPopup? s_popup;
    private static PanelContainer? s_panel;
    private static LineEdit? s_search;
    private static OptionButton? s_filter;
    private static VBoxContainer? s_rows;
    private static Label? s_footer;
    private static Label? s_status;
    private static Timer? s_debounce;

    [HarmonyPatch(typeof(GameView), nameof(GameView.OnFirstShow))]
    private static class FirstShowPatch
    {
        private static void Postfix(GameView __instance) => Attach(__instance);
    }

    internal static void AttachToExisting()
    {
        foreach (var view in NodeSearch.All<GameView>()) { Attach(view); break; }
    }

    private static bool Valid(GodotObject? o) => o != null && GodotObject.IsInstanceValid(o);

    private static void Attach(GameView view)
    {
        if (Valid(s_button) && s_button!.IsInsideTree() && view.IsAncestorOf(s_button)) return;
        Detach();
        var category = view.Header.Get("CategoryContainer").As<HBoxContainer>();
        if (category == null) return;

        var button = GD.Load<PackedScene>(ButtonScene).Instantiate<PanelButtonContainer>();
        button.Name = ButtonName;
        button.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        if (button.FindChild("Title", true, false) is Label title)
        {
            title.AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled;
            title.Text = "Items";
        }
        if (button.FindChildren("*", "TextureRect", true, false) is { Count: > 0 } icons && icons[0] is TextureRect icon &&
            ResourceLoader.Exists("res://Assets/Icons/Recycle_32.png"))
            icon.Texture = GD.Load<Texture2D>("res://Assets/Icons/Recycle_32.png");
        UiKit.AddTooltip(button, "Items", "Automatically sell, open or bury items above a minimum you keep.");
        button.Pressed += Toggle;
        category.AddChild(button);
        s_button = button;

        var popup = new PanelContainerPopup { Name = PopupName };
        popup.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        popup.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.35f) });
        view.AddChild(popup);
        var holder = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        popup.AddChild(holder);
        s_popup = popup;
        s_panel = BuildPanel();
        holder.AddChild(s_panel);

        ItemRules.Changed += OnRulesChanged;
    }

    internal static void Detach()
    {
        ItemRules.Changed -= OnRulesChanged;
        foreach (Node? n in new Node?[] { s_button, s_popup })
        {
            if (!Valid(n)) continue;
            n!.GetParent()?.RemoveChild(n);
            n.QueueFree();
        }
        s_button = null; s_popup = null; s_panel = null; s_search = null; s_filter = null;
        s_rows = null; s_footer = null; s_status = null; s_debounce = null;
    }

    // Only the "with a rule" view changes membership when a rule changes.
    private static void OnRulesChanged()
    {
        if (Valid(s_filter) && (Filter)s_filter!.GetSelectedId() == Filter.WithRule)
            Callable.From(Rebuild).CallDeferred();
    }

    private static PanelContainer BuildPanel()
    {
        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop, CustomMinimumSize = new Vector2(860, 640) };
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.11f, 0.10f, 0.09f, 0.98f),
            BorderColor = new Color(0.55f, 0.45f, 0.25f),
            BorderWidthLeft = 2, BorderWidthTop = 2, BorderWidthRight = 2, BorderWidthBottom = 2,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            ShadowColor = new Color(0, 0, 0, 0.5f), ShadowSize = 10,
        });
        var margin = new MarginContainer();
        foreach (var side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 16);
        panel.AddChild(margin);
        var body = new VBoxContainer();
        body.AddThemeConstantOverride("separation", 10);
        margin.AddChild(body);

        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 8);
        header.AddChild(TopBar.RawLabel("Item automation", 20, new Color(0.98f, 0.8f, 0.3f)));
        header.AddChild(UiKit.HintIcon("Item automation",
            "Pick Sell, Open or Bury for an item and how many to keep. Everything above that is handled right away, " +
            "on every drop, and whenever you load your save. Opened contents follow their own rules. Locked items are never touched.\n" +
            "For a new rule, Keep starts at the Default Keep setting (Settings > Melvor2Tweaks > Items); set Keep first to use another amount.\n" +
            "\"Bank & rules\" lists what you own plus every item with a rule; searching there covers every item in the game, " +
            "so you can set up items you haven't found yet.\n" +
            "Also available from an item's Sell panel in the bank.\n" +
            "Categories: a rule for a type of item (food, bones, chests...) or for everything a skill produces covers every item in it, including ones you find later. " +
            "An item's own rule wins; set an item to Never to leave it out of its category's rule."));
        header.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var close = TopBar.GameButton("Close");
        close.Pressed += () => s_popup?.Close();
        header.AddChild(close);
        body.AddChild(header);

        var toolbar = new HBoxContainer();
        toolbar.AddThemeConstantOverride("separation", 8);
        s_search = new LineEdit { PlaceholderText = "Search any item...", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClearButtonEnabled = true };
        s_debounce = new Timer { OneShot = true, WaitTime = 0.25 };
        s_debounce.Timeout += Rebuild;
        s_search.AddChild(s_debounce);
        s_search.TextChanged += _ => s_debounce.Start();
        toolbar.AddChild(s_search);
        s_filter = new OptionButton { FocusMode = Control.FocusModeEnum.None };
        s_filter.AddItem("Bank & rules", (int)Filter.Mine);
        s_filter.AddItem("With a rule", (int)Filter.WithRule);
        s_filter.AddItem("All items", (int)Filter.All);
        s_filter.AddItem("Categories", (int)Filter.Categories);
        s_filter.ItemSelected += _ => Rebuild();
        toolbar.AddChild(s_filter);
        var apply = TopBar.GameButton("Apply rules now");
        apply.CustomMinimumSize = new Vector2(150, 36);
        apply.Pressed += () =>
        {
            int handled = ItemRules.ApplyAll();
            SetStatus(handled > 0 ? $"Handled {handled:N0} item(s)." : "Nothing above the keep amounts.");
            Rebuild();
        };
        toolbar.AddChild(apply);
        body.AddChild(toolbar);

        // Column captions
        var captions = new HBoxContainer();
        captions.AddThemeConstantOverride("separation", 10);
        captions.AddChild(new Control { CustomMinimumSize = new Vector2(36, 0) });
        captions.AddChild(Caption("Item", expand: true));
        captions.AddChild(Caption("In bank", width: 90));
        captions.AddChild(Caption("Automation", width: 150));
        captions.AddChild(Caption("Keep", width: 130));
        body.AddChild(captions);

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        s_rows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        s_rows.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(s_rows);
        body.AddChild(scroll);

        s_footer = TopBar.RawLabel("", 13, new Color(1, 1, 1, 0.6f));
        body.AddChild(s_footer);
        s_status = TopBar.RawLabel("", 14, new Color(0.6f, 0.9f, 0.6f));
        body.AddChild(s_status);
        return panel;
    }

    private static Label Caption(string text, bool expand = false, float width = 0)
    {
        var l = TopBar.RawLabel(text, 13, new Color(1, 1, 1, 0.6f));
        if (expand) l.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        if (width > 0) l.CustomMinimumSize = new Vector2(width, 0);
        return l;
    }

    private static void SetStatus(string text)
    {
        if (Valid(s_status)) s_status!.Text = text;
    }

    /// <summary>Dev aid: set the search text and filter (0 bank &amp; rules, 1 with a rule, 2 all).</summary>
    internal static void DevSet(string search, int filter)
    {
        if (!Valid(s_search) || !Valid(s_filter)) return;
        s_filter!.Select(s_filter.GetItemIndex(filter));
        s_search!.Text = search;
        Rebuild();
    }

    internal static void Toggle()
    {
        if (!Valid(s_popup)) return;
        if (s_popup!.Visible) { s_popup.Close(); return; }
        (s_popup.GetParent() as GameView)?.CloseExclusiveModals();
        SetStatus("");
        Rebuild();
        s_popup.Open();
        Callable.From(() =>
        {
            if (!Valid(s_panel)) return;
            var screen = s_popup.GetViewportRect().Size;
            var size = s_panel!.GetCombinedMinimumSize();
            s_panel.Size = size;
            s_panel.Position = ((screen - size) / 2).Max(new Vector2(8, 8));
        }).CallDeferred();
    }

    /// <summary>
    /// Bank &amp; rules: everything in the bank plus every item with a rule (even at 0 owned); a search there
    /// covers every item in the game, so items not found yet can be set up in advance.
    /// </summary>
    private static IEnumerable<Item> Candidates(string query)
    {
        var game = TimeBank.TimeBank.Game;
        if (game == null) return Array.Empty<Item>();
        var filter = Valid(s_filter) ? (Filter)s_filter!.GetSelectedId() : Filter.Mine;
        var ruled = Settings.Current.ItemRules.Keys
            .Select(id => game.Data.TryGetObject<Item>(id, out var i) ? i : null).OfType<Item>();
        IEnumerable<Item> all = game.Data.GetDataRegistryOutput<Item>().GetAllObjects();
        IEnumerable<Item> owned = ItemRules.Bank?.Inventory.Stacks.Select(s => s.Item) ?? Array.Empty<Item>();
        return filter switch
        {
            Filter.Mine when query.Length > 0 => all,
            Filter.Mine => owned.Concat(ruled).Distinct(),
            Filter.WithRule => ruled,
            _ => all,
        };
    }

    private static void Rebuild()
    {
        if (!Valid(s_rows)) return;
        foreach (Node c in s_rows!.GetChildren()) { s_rows.RemoveChild(c); c.QueueFree(); }
        if (Valid(s_filter) && (Filter)s_filter!.GetSelectedId() == Filter.Categories) { RebuildCategories(); return; }

        string q = s_search?.Text.Trim() ?? "";
        var matches = Candidates(q)
            .Where(i => q.Length == 0 || i.DisplayName.Singular.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => ItemRules.Get(i) == null && ItemRules.CategoryRuleFor(i) == null) // configured first
            .ThenBy(i => ItemRules.InBank(i) <= 0)  // then owned
            .ThenBy(i => i.DisplayName.Singular)
            .ToList();

        foreach (var item in matches.Take(MaxRows)) s_rows.AddChild(Row(item));
        if (Valid(s_footer))
            s_footer!.Text = matches.Count == 0 ? "No items match."
                : matches.Count > MaxRows ? $"Showing {MaxRows} of {matches.Count:N0} - type to narrow the list."
                : $"{matches.Count:N0} item(s).";
    }

    private static Control Row(Item item)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        row.AddChild(new TextureRect
        {
            Texture = item.Icon,
            CustomMinimumSize = new Vector2(36, 36),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        });

        var names = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Alignment = BoxContainer.AlignmentMode.Center };
        names.AddThemeConstantOverride("separation", 0);
        var name = TopBar.RawLabel(item.DisplayName.Singular, 15, null);
        name.ClipText = true;
        names.AddChild(name);
        var caps = new List<string>();
        if (ItemRules.CanOpen(item)) caps.Add("Openable");
        if (ItemRules.CanBury(item)) caps.Add("Buryable");
        caps.AddRange(item.BankTags.Select(t => t.BankTag.DisplayName.Text));
        if (caps.Count > 0) names.AddChild(TopBar.RawLabel(string.Join(" · ", caps), 12, new Color(1, 1, 1, 0.55f)));
        row.AddChild(names);

        var count = TopBar.RawLabel(ItemRules.InBank(item).ToString("N0"), 14, null);
        count.CustomMinimumSize = new Vector2(90, 0);
        count.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(count);

        var rule = ItemRules.Get(item);
        var picker = new OptionButton { FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(150, 0), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        foreach (var action in ItemRules.ActionsFor(item)) picker.AddItem(ItemRules.Label(item, action, bank: false), (int)action);
        picker.Select(picker.GetItemIndex((int)(rule?.Action ?? RuleAction.Off)));
        row.AddChild(picker);

        var keep = new SpinBox
        {
            MinValue = 0, MaxValue = 1_000_000_000, Step = 1,
            CustomMinimumSize = new Vector2(130, 0),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        // Without a rule, Keep starts at the configured default; set it before picking an action to override.
        keep.SetValueNoSignal(rule?.Keep ?? ItemRules.DefaultKeep(item));
        row.AddChild(keep);

        void Changed()
        {
            var action = (RuleAction)picker.GetItemId(picker.Selected);
            int before = ItemRules.InBank(item);
            ItemRules.Set(item, action, (int)keep.Value);
            int after = ItemRules.InBank(item);
            count.Text = after.ToString("N0");
            if (before != after) SetStatus($"{action}: {before - after:N0} x {item.DisplayName.Singular}");
        }
        bool prefilled = rule == null; // Keep still shows the owned count from when the row was built
        picker.ItemSelected += _ =>
        {
            if (prefilled) keep.SetValueNoSignal(ItemRules.DefaultKeep(item));
            prefilled = false;
            Changed();
        };
        keep.ValueChanged += _ =>
        {
            prefilled = false;
            if (picker.GetItemId(picker.Selected) is not ((int)RuleAction.Off or (int)RuleAction.Never)) Changed();
        };
        return row;
    }

    // ---- categories --------------------------------------------------------------------------------
    private static void RebuildCategories()
    {
        string q = s_search?.Text.Trim() ?? "";
        var all = TimeBank.TimeBank.Game?.Data.GetDataRegistryOutput<Item>().GetAllObjects().ToList() ?? new List<Item>();
        var cats = ItemRules.Categories()
            .Where(c => q.Length == 0 || c.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => ItemRules.GetCategory(c) == null).ToList(); // then list order: types, then skills
        foreach (var cat in cats)
            if (all.Where(cat.Matches).ToList() is { Count: > 0 } members) s_rows!.AddChild(CategoryRow(cat, members));
        if (Valid(s_footer))
            s_footer!.Text = cats.Count == 0 ? "No categories match." :
                $"{cats.Count} categories. A category rule covers every item in it; an item's own rule (or Never) wins.";
    }

    private static Control CategoryRow(ItemCategory cat, List<Item> items)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        row.AddChild(new TextureRect
        {
            Texture = cat.Icon ?? items.FirstOrDefault()?.Icon,
            CustomMinimumSize = new Vector2(36, 36),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        });
        var names = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Alignment = BoxContainer.AlignmentMode.Center };
        names.AddThemeConstantOverride("separation", 0);
        var name = TopBar.RawLabel(cat.Name, 15, null);
        name.MouseFilter = Control.MouseFilterEnum.Pass;
        UiKit.AddTooltip(name, cat.Name, cat.Description);
        names.AddChild(name);
        var sample = string.Join(", ", items.Take(6).Select(i => i.DisplayName.Singular)) + (items.Count > 6 ? ", ..." : "");
        var sub = TopBar.RawLabel($"{items.Count} items: {sample}", 12, new Color(1, 1, 1, 0.55f));
        sub.ClipText = true;
        names.AddChild(sub);
        row.AddChild(names);

        int owned = items.Sum(ItemRules.InBank);
        var count = TopBar.RawLabel(owned.ToString("N0"), 14, null);
        count.CustomMinimumSize = new Vector2(90, 0);
        count.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(count);

        var rule = ItemRules.GetCategory(cat);
        var picker = new OptionButton { FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(150, 0), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        picker.AddItem("Off", (int)RuleAction.Off);
        picker.AddItem("Sell", (int)RuleAction.Sell);
        if (items.Any(ItemRules.CanOpen)) picker.AddItem("Open", (int)RuleAction.Open);
        if (items.Any(ItemRules.CanBury)) picker.AddItem("Bury", (int)RuleAction.Bury);
        picker.Select(Math.Max(0, picker.GetItemIndex((int)(rule?.Action ?? RuleAction.Off))));
        row.AddChild(picker);

        var keep = new SpinBox { MinValue = 0, MaxValue = 1_000_000_000, Step = 1, CustomMinimumSize = new Vector2(130, 0), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        keep.SetValueNoSignal(rule?.Keep ?? 0);
        UiKit.AddTooltip(keep, "Keep", "How many of each item in this category to leave in the bank.");
        row.AddChild(keep);

        void Changed()
        {
            int before = items.Sum(ItemRules.InBank);
            ItemRules.SetCategory(cat, (RuleAction)picker.GetItemId(picker.Selected), (int)keep.Value);
            int after = items.Sum(ItemRules.InBank);
            count.Text = after.ToString("N0");
            if (before != after) SetStatus($"{cat.Name}: handled {before - after:N0} item(s)");
        }
        picker.ItemSelected += _ => Changed();
        keep.ValueChanged += _ => { if (picker.GetItemId(picker.Selected) != (int)RuleAction.Off) Changed(); };
        return row;
    }
}
