using System;
using System.Collections.Generic;
using GamesByMalcs.Melvor2.CustomControls;
using GamesByMalcs.Melvor2.GameMenus;
using Godot;
using HarmonyLib;
using Melvor2Tweaks.TimeBank;

namespace Melvor2Tweaks.UI;

/// <summary>
/// "Time Bank" entry in the game's header, next to Bank / Shop / Quests / Completion: a clone of the
/// header Statistics button showing banked time (and speed when above 1x), opening a popup with
/// time-skip and speed buttons. A new GameView (and header) is built every time a save is loaded,
/// so we attach in GameView.OnFirstShow, and to the live one when hot-loaded.
/// </summary>
internal static class TopBar
{
    private const string ButtonScene = "res://Components/Statistics/header_statistics_button.tscn";
    private const string ButtonName = "Melvor2TweaksTimeBankButton";
    private const string PopupName = "Melvor2TweaksTimeBankPopup";

    private static readonly (string label, double ms)[] SkipPresets =
    {
        ("5m", 5 * 60_000), ("15m", 15 * 60_000), ("30m", 30 * 60_000),
        ("1h", 3_600_000), ("4h", 4 * 3_600_000), ("8h", 8 * 3_600_000), ("All", double.MaxValue),
    };

    private static PanelButtonContainer? s_button;
    private static Label? s_subLabel;
    private static PanelContainerPopup? s_popup;
    private static PanelContainer? s_panel;
    private static Label? s_bankLabel;
    private static Label? s_status;
    private static readonly List<Button> s_skipButtons = new();
    private static readonly List<(Button button, double speed)> s_speedButtons = new();

    private static readonly Action s_onChanged = () => Callable.From(Refresh).CallDeferred();

    [HarmonyPatch(typeof(GameView), nameof(GameView.OnFirstShow))]
    private static class FirstShowPatch
    {
        private static void Postfix(GameView __instance) => Attach(__instance);
    }

    internal static void AttachToExisting()
    {
        if (Engine.GetMainLoop() is not SceneTree tree) return;
        var view = Find<GameView>(tree.Root);
        if (view != null) Attach(view);
    }

    private static bool Valid(GodotObject? o) => o != null && GodotObject.IsInstanceValid(o);

    private static void Attach(GameView view)
    {
        if (Valid(s_button) && s_button!.IsInsideTree() && view.IsAncestorOf(s_button)) return;
        Detach();

        var category = view.Header.Get("CategoryContainer").As<HBoxContainer>();
        if (category == null) { Plugin.Log("TopBar: CategoryContainer not found"); return; }

        // ---- header button ----
        var button = GD.Load<PackedScene>(ButtonScene).Instantiate<PanelButtonContainer>();
        button.Name = ButtonName;
        // Keep the stock single-label layout: a second line makes the button taller than its
        // neighbours and stretches the whole header. The label shows the banked time instead of a title.
        if (button.FindChild("Title", true, false) is Label title)
        {
            title.AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled;
            title.ClipText = true;
            s_subLabel = title;
        }
        button.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        if (button.FindChildren("*", "TextureRect", true, false) is { Count: > 0 } icons && icons[0] is TextureRect icon)
            icon.Texture = LoadTexture("res://Assets/Items/SandsofTime.png") ?? LoadTexture("res://Assets/Icons/TimeInterval_48.png") ?? icon.Texture;
        UiKit.AddTooltip(button, "Time Bank", "Banked offline time. Click to spend it or change game speed.");
        button.Pressed += Toggle;
        category.AddChild(button);
        s_button = button;

        // ---- popup: full-screen click-catcher (clicking it closes) holding a positioned panel ----
        var popup = new PanelContainerPopup { Name = PopupName };
        popup.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        popup.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        view.AddChild(popup);
        var holder = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        popup.AddChild(holder);
        s_popup = popup;
        s_panel = BuildPanel();
        holder.AddChild(s_panel);

        var timer = new Timer { WaitTime = 1.0, Autostart = true, ProcessMode = Node.ProcessModeEnum.Always };
        timer.Timeout += Refresh;
        timer.Timeout += DevCommands.Poll;
        button.AddChild(timer);

        TimeBankStore.Changed += s_onChanged;
        Settings.Changed += s_onChanged;
        Refresh();
        Plugin.Log("Top bar ready");
    }

    internal static void Detach()
    {
        TimeBankStore.Changed -= s_onChanged;
        Settings.Changed -= s_onChanged;
        foreach (Node? n in new Node?[] { s_button, s_popup })
        {
            if (!Valid(n)) continue;
            n!.GetParent()?.RemoveChild(n);
            n.QueueFree();
        }
        s_button = null; s_subLabel = null; s_popup = null; s_panel = null; s_bankLabel = null; s_status = null;
        s_skipButtons.Clear();
        s_speedButtons.Clear();
    }

    private static PanelContainer BuildPanel()
    {
        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        // The game theme gives a bare PanelContainer no background; match its dark menus with a gold edge.
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.11f, 0.10f, 0.09f, 0.97f),
            BorderColor = new Color(0.55f, 0.45f, 0.25f),
            BorderWidthLeft = 2, BorderWidthTop = 2, BorderWidthRight = 2, BorderWidthBottom = 2,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            ShadowColor = new Color(0, 0, 0, 0.5f),
            ShadowSize = 8,
        });
        var margin = new MarginContainer();
        foreach (var side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 14);
        panel.AddChild(margin);
        var body = new VBoxContainer();
        body.AddThemeConstantOverride("separation", 10);
        margin.AddChild(body);

        s_bankLabel = RawLabel("", 20, new Color(0.98f, 0.8f, 0.3f));
        body.AddChild(s_bankLabel);

        body.AddChild(RawLabel("Skip ahead using banked time", 15, null));
        var skipRow = new HBoxContainer();
        body.AddChild(skipRow);
        foreach (var (label, ms) in SkipPresets)
        {
            var b = GameButton(label);
            double amount = ms;
            b.Pressed += () => OnSkip(amount);
            skipRow.AddChild(b);
            s_skipButtons.Add(b);
        }

        body.AddChild(RawLabel("Game speed", 15, null));
        var speedRow = new HBoxContainer();
        body.AddChild(speedRow);
        foreach (double preset in Settings.Current.SpeedPresets)
        {
            var b = GameButton($"{preset:0.##}x");
            b.ToggleMode = true;
            double value = preset;
            b.Pressed += () => { Settings.Current.Speed = value; Settings.Save(); };
            speedRow.AddChild(b);
            s_speedButtons.Add((b, preset));
        }

        s_status = RawLabel("", 14, new Color(1f, 0.55f, 0.45f));
        s_status.Visible = false;
        body.AddChild(s_status);

        var hint = RawLabel("Banking rules: Settings > Melvor2Tweaks", 12, new Color(1, 1, 1, 0.6f));
        body.AddChild(hint);
        return panel;
    }

    internal static Button GameButton(string text) => new()
    {
        Text = text,
        ThemeTypeVariation = "TexturedButton4x",
        CustomMinimumSize = new Vector2(56, 36),
        AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled,
        FocusMode = Control.FocusModeEnum.None,
    };

    internal static Label RawLabel(string text, int size, Color? color)
    {
        var l = new Label { Text = text, AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled };
        l.AddThemeFontSizeOverride("font_size", size);
        if (color.HasValue) l.AddThemeColorOverride("font_color", color.Value);
        return l;
    }

    internal static void Toggle()
    {
        if (!Valid(s_popup) || !Valid(s_button)) return;
        if (s_popup!.Visible) { s_popup.Close(); return; }

        (s_popup.GetParent() as GameView)?.CloseExclusiveModals();
        s_status!.Visible = false;
        Refresh();
        s_popup.Open();
        // Under the button, kept on screen.
        Callable.From(() =>
        {
            if (!Valid(s_panel) || !Valid(s_button)) return;
            var rect = s_button!.GetGlobalRect();
            var size = s_panel!.GetCombinedMinimumSize();
            float screenW = s_popup.GetViewportRect().Size.X;
            float x = Math.Clamp(rect.Position.X + rect.Size.X / 2 - size.X / 2, 8, Math.Max(8, screenW - size.X - 8));
            s_panel.Position = new Vector2(x, rect.End.Y + 6);
            s_panel.Size = size;
        }).CallDeferred();
    }

    internal static void Close()
    {
        if (Valid(s_popup) && s_popup!.Visible) s_popup.Close();
    }

    private static void OnSkip(double ms)
    {
        double spent = TimeBank.TimeBank.Spend(ms, out string reason);
        if (spent > 0) Close();
        else if (Valid(s_status)) { s_status!.Text = reason; s_status.Visible = true; }
        Refresh();
    }

    private static void Refresh()
    {
        if (!Valid(s_button)) return;
        var s = Settings.Current;
        double banked = TimeBankStore.BankedMs;
        string bank = s.TimeBankEnabled ? ShortFormat(banked) : "Time Bank";
        string speed = s.Speed > 1 ? $" {s.Speed:0.##}x" : "";
        if (Valid(s_subLabel)) s_subLabel!.Text = bank + speed;
        if (Valid(s_bankLabel)) s_bankLabel!.Text = $"Banked: {TimeBank.TimeBank.Format(banked)}";

        bool canSkip = TimeBank.TimeBank.IsProcessing && banked >= 10_000;
        foreach (var b in s_skipButtons) if (Valid(b)) b.Disabled = !canSkip;
        foreach (var (b, preset) in s_speedButtons) if (Valid(b)) b.SetPressedNoSignal(Math.Abs(preset - s.Speed) < 0.001);
    }

    /// <summary>Compact form for the header label, e.g. "3h 12m", "45m", "2d 4h".</summary>
    private static string ShortFormat(double ms)
    {
        var ts = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}d {ts.Hours}h";
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        return $"{ts.Minutes}m";
    }

    private static Texture2D? LoadTexture(string path) =>
        ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;

    private static T? Find<T>(Node root) where T : Node
    {
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n is T t) return t;
            foreach (Node c in n.GetChildren()) stack.Push(c);
        }
        return null;
    }
}
