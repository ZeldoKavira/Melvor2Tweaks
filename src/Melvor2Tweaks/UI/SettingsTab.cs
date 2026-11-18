using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GamesByMalcs.Melvor2.GameMenus.PauseOverlay;
using Godot;
using HarmonyLib;
using Melvor2Tweaks.Features;

namespace Melvor2Tweaks.UI;

/// <summary>
/// "Melvor2Tweaks" category in the game's Settings panel. To look exactly like the game's own pages it
/// is assembled from copies of the game's nodes (none of them carry scripts besides the click sound):
///   - the Game page's frame (inner panel, title tab, scroll, row list)
///   - its "Stop Action on Full Bank" row (full-width tick button + bold name + grey description)
///   - the Interface page's tooltip-delay SpinBox for number rows
/// Rows come from the [Setting] attributes on <see cref="Settings"/>, plus a Junk list manager.
/// Each application view (cloud menu, main menu, game) has its own SettingsPanel, created fresh on
/// every mode change, so we hook its _Ready and also attach to panels that exist when hot-loaded.
/// </summary>
internal static class SettingsTab
{
    private const string ButtonName = "Melvor2TweaksSettingsButton";
    private const string ViewName = "Melvor2TweaksSettingsView";

    private static readonly MethodInfo s_showView = AccessTools.Method(typeof(SettingsPanel), "ShowSettingsView");

    [HarmonyPatch(typeof(SettingsPanel), nameof(SettingsPanel._Ready))]
    private static class ReadyPatch
    {
        private static void Postfix(SettingsPanel __instance) => Attach(__instance);
    }

    // The game's HideAllSettingsViews only knows its own 8 views.
    [HarmonyPatch(typeof(SettingsPanel), "HideAllSettingsViews")]
    private static class HideAllPatch
    {
        private static void Postfix(SettingsPanel __instance)
        {
            FindView(__instance)?.Hide();
            FindButton(__instance)?.SetPressedNoSignal(false); // another page was chosen
        }
    }

    internal static void AttachToExisting()
    {
        foreach (var panel in NodeSearch.All<SettingsPanel>()) Attach(panel);
    }

    internal static void DetachAll()
    {
        foreach (var panel in NodeSearch.All<SettingsPanel>())
        {
            var t = Traverse.Create(panel);
            foreach (var node in new Node?[] { FindButton(panel), FindView(panel) })
            {
                if (node == null) continue;
                bool wasVisible = node is CanvasItem ci && ci.Visible;
                node.GetParent()?.RemoveChild(node);
                node.QueueFree();
                if (wasVisible && node.Name == ViewName)
                    s_showView.Invoke(panel, new object[] { t.Property("GameSettingsView").GetValue<VBoxContainer>() });
            }
        }
    }

    private static Button? FindButton(SettingsPanel panel) =>
        Traverse.Create(panel).Property("GameSettingsButton").GetValue<Button>()?.GetParent()?.GetNodeOrNull<Button>(ButtonName);

    private static VBoxContainer? FindView(SettingsPanel panel) =>
        Traverse.Create(panel).Property("GameSettingsView").GetValue<VBoxContainer>()?.GetParent()?.GetNodeOrNull<VBoxContainer>(ViewName);

    private const int CopyFlags = (int)(Node.DuplicateFlags.Groups | Node.DuplicateFlags.Scripts);

    private static void Attach(SettingsPanel panel)
    {
        if (FindButton(panel) != null) return;
        var t = Traverse.Create(panel);
        var refButton = t.Property("GameSettingsButton").GetValue<Button>();
        var refView = t.Property("GameSettingsView").GetValue<VBoxContainer>();
        var refInterface = t.Property("InterfaceSettingsView").GetValue<Node>();
        var frameTemplate = refView?.GetChildOrNull<Control>(0);
        if (refButton == null || refView == null || frameTemplate == null) { Plugin.Log("SettingsTab: panel layout not as expected"); return; }

        // Sidebar button: same theme, toggle mode and ButtonGroup as the game's.
        var button = (Button)refButton.Duplicate((int)Node.DuplicateFlags.Groups);
        button.Name = ButtonName;
        button.Text = "Melvor2Tweaks";
        button.AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled;
        button.Icon = null;
        refButton.GetParent().AddChild(button);

        var view = new VBoxContainer
        {
            Name = ViewName,
            Visible = false,
            SizeFlagsHorizontal = refView.SizeFlagsHorizontal,
            SizeFlagsVertical = refView.SizeFlagsVertical,
        };
        view.AddThemeConstantOverride("separation", 8);
        refView.GetParent().AddChild(view);

        var frame = (Control)frameTemplate.Duplicate(CopyFlags);
        view.AddChild(frame);
        if (frame.FindChild("Title", true, false) is Label title)
        {
            title.Text = "Melvor2Tweaks";
            title.AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled;
        }

        var rowTemplate = frame.FindChild("StopActionOnFullBank", true, false) as Control;
        var list = rowTemplate?.GetParent() as VBoxContainer;
        var spinTemplate = refInterface == null ? null : Traverse.Create(refInterface).Property("TooltipDelay").GetValue<SpinBox>();
        if (rowTemplate == null || list == null) { Plugin.Log("SettingsTab: row template missing"); return; }
        foreach (Node c in list.GetChildren()) list.RemoveChild(c); // rowTemplate kept (detached) as the template

        var kit = new RowKit(rowTemplate, spinTemplate);
        BuildRows(list, kit);

        button.Pressed += () =>
        {
            if (!GodotObject.IsInstanceValid(panel)) return;
            s_showView.Invoke(panel, new object[] { view }); // hides the game's pages (and un-highlights us)
            foreach (Node sibling in button.GetParent().GetChildren())
                if (sibling is Button b && b != button) b.SetPressedNoSignal(false);
            button.SetPressedNoSignal(true);
        };
    }

    // ---- rows ------------------------------------------------------------------------------------
    private sealed class RowKit(Control row, SpinBox? spin)
    {
        public Control Row { get; } = row;
        public SpinBox? Spin { get; } = spin;
        public LabelSettings? NameStyle { get; } = (row.FindChild("DisplayName", true, false) as Label)?.LabelSettings;
        public LabelSettings? DescStyle { get; } = (row.FindChild("Description", true, false) as Label)?.LabelSettings;
    }

    private static void BuildRows(VBoxContainer list, RowKit kit)
    {
        var groups = typeof(Settings).GetProperties()
            .Select(p => (prop: p, attr: p.GetCustomAttribute<SettingAttribute>()))
            .Where(x => x.attr != null)
            .GroupBy(x => x.attr!.Category);

        foreach (var group in groups)
        {
            list.AddChild(Header(group.Key, kit));
            foreach (var (prop, attr) in group)
                list.AddChild(prop.PropertyType == typeof(bool) ? ToggleRow(prop, attr!, kit) : NumberRow(prop, attr!, kit));
        }

        list.AddChild(Header("Junk", kit));
        var junk = new VBoxContainer();
        junk.AddThemeConstantOverride("separation", 8);
        list.AddChild(junk);
        BuildJunk(junk, kit);
        JunkSell.ListChanged += () => Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(junk)) BuildJunk(junk, kit);
        }).CallDeferred();

        list.AddChild(Text($"Melvor2Tweaks v{Plugin.Version} - changes apply immediately.", kit.DescStyle));
    }

    private static Label Header(string text, RowKit kit)
    {
        var style = (LabelSettings?)kit.NameStyle?.Duplicate() ?? new LabelSettings();
        style.FontSize = 20;
        style.FontColor = new Color(0.98f, 0.8f, 0.3f);
        return new Label { Text = text, LabelSettings = style, AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled };
    }

    private static Label Text(string text, LabelSettings? style) => new()
    {
        Text = text,
        LabelSettings = style,
        AutowrapMode = TextServer.AutowrapMode.WordSmart,
        AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled,
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
    };

    /// <summary>Copy of the game's row with its texts replaced and the extra warning line removed.</summary>
    private static Control CopyRow(RowKit kit, string name, string description)
    {
        var row = (Control)kit.Row.Duplicate(CopyFlags);
        if (row.FindChild("DisplayName", true, false) is Label n) { n.Text = name; n.AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled; }
        if (row.FindChild("Description", true, false) is Label d) { d.Text = description; d.AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled; }
        if (row.FindChild("Description2", true, false) is Node extra) { extra.GetParent().RemoveChild(extra); extra.QueueFree(); }
        return row;
    }

    private static Control ToggleRow(PropertyInfo prop, SettingAttribute attr, RowKit kit)
    {
        var row = CopyRow(kit, attr.Label, attr.Description);
        if (row.FindChildren("*", "CheckButton", true, false).FirstOrDefault() is CheckButton check)
        {
            check.SetPressedNoSignal((bool)prop.GetValue(Settings.Current)!);
            check.Toggled += on => Set(prop, on);
        }
        return row;
    }

    private static Control NumberRow(PropertyInfo prop, SettingAttribute attr, RowKit kit)
    {
        var row = CopyRow(kit, attr.Label, attr.Description);
        // Swap the full-width tick button for a number box at the right, beside the texts.
        foreach (var cb in row.FindChildren("*", "CheckButton", true, false)) { cb.GetParent().RemoveChild(cb); cb.QueueFree(); }
        if (row.FindChild("DisplayName", true, false)?.GetParent() is Control texts && texts.GetParent() is Node holder)
        {
            var line = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            line.AddThemeConstantOverride("separation", 16);
            holder.RemoveChild(texts);
            line.AddChild(texts);
            holder.AddChild(line);

            bool isInt = prop.PropertyType == typeof(int);
            double scale = attr.Percent ? 100 : 1;
            var spin = NewSpin(kit, attr.Min * scale, attr.Max * scale, attr.Step * scale, Convert.ToDouble(prop.GetValue(Settings.Current)) * scale);
            spin.Suffix = attr.Percent ? "%" : "";
            spin.ValueChanged += v => Set(prop, isInt ? (object)(int)Math.Round(v / scale) : v / scale);
            line.AddChild(spin);
        }
        return row;
    }

    private static SpinBox NewSpin(RowKit kit, double min, double max, double step, double value)
    {
        var spin = kit.Spin != null ? (SpinBox)kit.Spin.Duplicate((int)Node.DuplicateFlags.Groups) : new SpinBox();
        spin.MinValue = min;
        spin.MaxValue = max;
        spin.Step = step;
        spin.SetValueNoSignal(value);
        spin.Prefix = "";
        spin.Suffix = "";
        spin.CustomMinimumSize = new Vector2(Math.Max(spin.CustomMinimumSize.X, 120), 0);
        spin.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
        spin.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        return spin;
    }

    private static void Set(PropertyInfo prop, object value)
    {
        prop.SetValue(Settings.Current, value);
        Settings.Save();
    }

    // ---- junk manager ------------------------------------------------------------------------------
    private static void BuildJunk(VBoxContainer box, RowKit kit)
    {
        foreach (Node c in box.GetChildren()) { box.RemoveChild(c); c.QueueFree(); }

        box.AddChild(Text("Junk items are sold as they drop. Anything above \"Keep\" is sold, including what's in the bank now. " +
                          "Mark items here or with the checkbox in the bank's Sell panel. Locked items are never sold.", kit.DescStyle));

        if (TimeBank.TimeBank.Game == null)
        {
            box.AddChild(Text("Load a save to manage junk items.", kit.DescStyle));
            return;
        }

        var status = Text("", kit.DescStyle);

        var add = new HBoxContainer();
        add.AddThemeConstantOverride("separation", 8);
        var search = new LineEdit { PlaceholderText = "Item name...", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var addButton = TopBar.GameButton("Add");
        void DoAdd()
        {
            var item = JunkSell.FindItem(search.Text);
            if (item == null) { status.Text = $"No item called \"{search.Text}\"."; return; }
            int before = JunkSell.InBank(item);
            JunkSell.SetJunk(item, true); // rebuilds this list
            Plugin.Log($"Junk manager: added {item.Id} (had {before})");
        }
        addButton.Pressed += DoAdd;
        search.TextSubmitted += _ => DoAdd();
        add.AddChild(search);
        add.AddChild(addButton);
        var sellNow = TopBar.GameButton("Sell excess now");
        sellNow.CustomMinimumSize = new Vector2(150, 36);
        sellNow.Pressed += () =>
        {
            int sold = JunkSell.SellAllExcess();
            status.Text = sold > 0 ? $"Sold {sold:N0} item(s)." : "Nothing above the keep amounts.";
        };
        add.AddChild(sellNow);
        box.AddChild(add);
        box.AddChild(status);

        var items = JunkSell.JunkItems().OrderBy(i => i.DisplayName.Singular).ToList();
        if (items.Count == 0) { box.AddChild(Text("No junk items yet.", kit.DescStyle)); return; }

        foreach (var item in items)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            row.AddChild(new TextureRect
            {
                Texture = item.Icon,
                CustomMinimumSize = new Vector2(32, 32),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            });
            var name = new Label
            {
                Text = item.DisplayName.Singular,
                LabelSettings = kit.NameStyle,
                AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                VerticalAlignment = VerticalAlignment.Center,
                ClipText = true,
            };
            row.AddChild(name);
            row.AddChild(new Label
            {
                Text = $"{JunkSell.InBank(item):N0} in bank   Keep",
                LabelSettings = kit.DescStyle,
                AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var keep = NewSpin(kit, 0, 1_000_000_000, 1, JunkSell.Keep(item));
            var it = item;
            keep.ValueChanged += v => JunkSell.SetKeep(it, (int)v);
            row.AddChild(keep);
            var remove = TopBar.GameButton("Remove");
            remove.CustomMinimumSize = new Vector2(80, 36);
            remove.Pressed += () => JunkSell.SetJunk(it, false);
            row.AddChild(remove);
            box.AddChild(row);
        }
    }
}
