using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GamesByMalcs.Melvor2.GameMenus.PauseOverlay;
using Godot;
using HarmonyLib;

namespace Melvor2Tweaks.UI;

/// <summary>
/// Adds a "Melvor2Tweaks" category to the game's Settings panel (pause menu), generated from the
/// [Setting] attributes on <see cref="Settings"/>. Each application view (cloud menu, main menu, game)
/// has its own SettingsPanel, created fresh on every mode change, so we hook its _Ready and also attach
/// to panels that already exist when the mod is (hot-)loaded.
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
        private static void Postfix(SettingsPanel __instance) => FindView(__instance)?.Hide();
    }

    internal static void AttachToExisting()
    {
        if (Engine.GetMainLoop() is not SceneTree tree) return;
        foreach (var panel in FindAll<SettingsPanel>(tree.Root)) Attach(panel);
    }

    internal static void DetachAll()
    {
        if (Engine.GetMainLoop() is not SceneTree tree) return;
        foreach (var panel in FindAll<SettingsPanel>(tree.Root))
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

    private static void Attach(SettingsPanel panel)
    {
        if (FindButton(panel) != null) return;
        var t = Traverse.Create(panel);
        var refButton = t.Property("GameSettingsButton").GetValue<Button>();
        var refView = t.Property("GameSettingsView").GetValue<VBoxContainer>();
        if (refButton == null || refView == null) { Plugin.Log("SettingsTab: panel layout not as expected"); return; }

        // Duplicate without signals/scripts: same theme, toggle mode and ButtonGroup as the game's buttons.
        var button = (Button)refButton.Duplicate((int)(Node.DuplicateFlags.Groups));
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
        refView.GetParent().AddChild(view);
        // The game's own settings toggle, copied without script/signals, so our rows match its size and look.
        var template = Traverse.Create(refView).Property("StopActionOnBankFull").GetValue<CheckButton>();
        BuildRows(view, template);

        button.Pressed += () =>
        {
            if (GodotObject.IsInstanceValid(panel)) s_showView.Invoke(panel, new object[] { view });
        };
    }

    private static Button? FindButton(SettingsPanel panel) =>
        Traverse.Create(panel).Property("GameSettingsButton").GetValue<Button>()?.GetParent()?.GetNodeOrNull<Button>(ButtonName);

    private static VBoxContainer? FindView(SettingsPanel panel) =>
        Traverse.Create(panel).Property("GameSettingsView").GetValue<VBoxContainer>()?.GetParent()?.GetNodeOrNull<VBoxContainer>(ViewName);

    // ---- rows ------------------------------------------------------------------------------------
    private static void BuildRows(VBoxContainer view, CheckButton? template)
    {
        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            CustomMinimumSize = new Vector2(0, 400),
        };
        view.AddChild(scroll);
        var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(list);

        var props = typeof(Settings).GetProperties()
            .Select(p => (prop: p, attr: p.GetCustomAttribute<SettingAttribute>()))
            .Where(x => x.attr != null)
            .GroupBy(x => x.attr!.Category);

        foreach (var group in props)
        {
            var header = RawLabel(group.Key, "fs16_bold_yellow_label");
            header.AddThemeFontSizeOverride("font_size", 20);
            header.AddThemeColorOverride("font_color", new Color(0.98f, 0.8f, 0.3f));
            list.AddChild(header);
            foreach (var (prop, attr) in group) list.AddChild(BuildRow(prop, attr!, template));
            list.AddChild(new HSeparator());
        }
        list.AddChild(RawLabel($"Melvor2Tweaks v{Plugin.Version} - changes apply immediately.", "fs12_medium_green_secondary_label"));
    }

    private static Control BuildRow(PropertyInfo prop, SettingAttribute attr, CheckButton? template)
    {
        // No hover tooltip: the description is shown under the label (plain tooltips have no background here).
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 16);

        var text = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        text.AddChild(RawLabel(attr.Label, "fs16_regular_paper_label"));
        var desc = RawLabel(attr.Description, "fs14_regular_label");
        desc.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        desc.Modulate = new Color(1, 1, 1, 0.7f);
        text.AddChild(desc);
        row.AddChild(text);

        object? value = prop.GetValue(Settings.Current);
        if (prop.PropertyType == typeof(bool))
        {
            var check = template != null
                ? (CheckButton)template.Duplicate((int)Node.DuplicateFlags.Groups)
                : new CheckButton();
            check.Text = "";
            check.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
            check.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            check.SetPressedNoSignal((bool)value!);
            check.Toggled += on => Set(prop, on);
            row.AddChild(check);
        }
        else
        {
            bool isInt = prop.PropertyType == typeof(int);
            double scale = attr.Percent ? 100 : 1;
            var spin = new SpinBox
            {
                MinValue = attr.Min * scale,
                MaxValue = attr.Max * scale,
                Step = attr.Step * scale,
                Value = Convert.ToDouble(value) * scale,
                Suffix = attr.Percent ? "%" : "",
                CustomMinimumSize = new Vector2(140, 0),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            };
            spin.ValueChanged += v => Set(prop, isInt ? (object)(int)Math.Round(v / scale) : v / scale);
            row.AddChild(spin);
        }
        return row;
    }

    private static void Set(PropertyInfo prop, object value)
    {
        prop.SetValue(Settings.Current, value);
        Settings.Save();
    }

    private static Label RawLabel(string text, string variation) => new()
    {
        Text = text,
        ThemeTypeVariation = variation,
        AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled,
    };

    private static IEnumerable<T> FindAll<T>(Node root) where T : Node
    {
        foreach (Node child in root.GetChildren())
        {
            if (child is T match) yield return match;
            foreach (var deeper in FindAll<T>(child)) yield return deeper;
        }
    }
}
