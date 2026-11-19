using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2.CustomControls;
using GamesByMalcs.Melvor2.GameMenus;
using Godot;
using HarmonyLib;
using Melvor2Tweaks.Actions;

namespace Melvor2Tweaks.UI;

/// <summary>
/// "Actions" header entry: a stop rule for the running action ("until X" for every skill) and the
/// action queue. Same construction as the Time Bank entry (clone of the header Statistics button).
/// </summary>
internal static class ActionsMenu
{
    private const string ButtonScene = "res://Components/Statistics/header_statistics_button.tscn";
    private const string ButtonName = "Melvor2TweaksActionsButton";
    private const string PopupName = "Melvor2TweaksActionsPopup";

    private static PanelButtonContainer? s_button;
    private static Label? s_buttonLabel;
    private static PanelContainerPopup? s_popup;
    private static PanelContainer? s_panel;
    private static Label? s_current;
    private static Label? s_rule;
    private static OptionButton? s_kind;
    private static SpinBox? s_value;
    private static CheckBox? s_enabled;
    private static Button? s_start;
    private static CheckBox? s_loop;
    private static Label? s_total;
    private static LineEdit? s_presetName;
    private static OptionButton? s_presets;
    private static string? s_presetSignature;
    private static VBoxContainer? s_queueBody;
    private static VBoxContainer? s_logBody;
    private static Button? s_logToggle;
    private static OptionButton? s_logPick;
    private static string? s_logPickSignature;
    private static RichTextLabel? s_logText;
    private static Button? s_logReset;
    private static ulong s_resetArmedAt;
    private static readonly List<Label> s_etaLabels = new();
    private static CheckBox? s_queueMode;
    private static VBoxContainer? s_list;
    private static Label? s_status;
    private static string? s_listSignature;

    private static readonly Action s_onChanged = () => Callable.From(Refresh).CallDeferred();

    [HarmonyPatch(typeof(GameView), nameof(GameView.OnFirstShow))]
    private static class FirstShowPatch
    {
        private static void Postfix(GameView __instance)
        {
            Attach(__instance);
            ActionQueue.Rearm(); // a save was just opened
        }
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
            title.ClipText = true;
            title.Text = "Actions";
            s_buttonLabel = title;
        }
        if (button.FindChildren("*", "TextureRect", true, false) is { Count: > 0 } icons && icons[0] is TextureRect icon &&
            ResourceLoader.Exists("res://Assets/Icons/TimeInterval_48.png"))
            icon.Texture = GD.Load<Texture2D>("res://Assets/Icons/TimeInterval_48.png");
        UiKit.AddTooltip(button, "Actions", "Stop the current action at a level or item count, and queue what to do next.");
        button.Pressed += Toggle;
        category.AddChild(button);
        s_button = button;

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
        button.AddChild(timer);

        ActionQueue.Changed += s_onChanged;
        QueueMode.PendingRule = () => (Kind, s_value == null ? 0 : (int)s_value.Value);
        QueueMode.Captured += ShowStatus;
        Refresh();
    }

    internal static void Detach()
    {
        ActionQueue.Changed -= s_onChanged;
        QueueMode.Captured -= ShowStatus;
        QueueMode.Enabled = false;
        foreach (Node? n in new Node?[] { s_button, s_popup })
        {
            if (!Valid(n)) continue;
            n!.GetParent()?.RemoveChild(n);
            n.QueueFree();
        }
        s_button = null; s_buttonLabel = null; s_popup = null; s_panel = null; s_current = null; s_rule = null;
        s_kind = null; s_value = null; s_enabled = null; s_start = null; s_loop = null; s_total = null; s_presetName = null; s_presets = null;
        s_presetSignature = null; s_queueBody = null; s_logBody = null; s_logToggle = null; s_logPick = null; s_logPickSignature = null;
        s_logText = null; s_logReset = null; s_etaLabels.Clear(); s_queueMode = null; s_list = null; s_status = null; s_listSignature = null;
    }

    private static PanelContainer BuildPanel()
    {
        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop, CustomMinimumSize = new Vector2(600, 0) };
        panel.AddThemeStyleboxOverride("panel", PanelStyle());
        var margin = new MarginContainer();
        foreach (var side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 14);
        panel.AddChild(margin);
        var body = new VBoxContainer();
        body.AddThemeConstantOverride("separation", 8);
        margin.AddChild(body);

        // Header: title left; Queue mode + help (?) in the top-right corner.
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 8);
        header.AddChild(TopBar.RawLabel("Current action", 18, Gold));
        header.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        s_logToggle = TopBar.GameButton("Drop log");
        s_logToggle.Pressed += ToggleLog;
        UiKit.AddTooltip(s_logToggle, "Drop log", "Everything each action has produced, for all time: actions, time, XP and items.");
        header.AddChild(s_logToggle);
        s_queueMode = new CheckBox { Text = "Queue mode", FocusMode = Control.FocusModeEnum.None, AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled };
        s_queueMode.Toggled += on => { QueueMode.Enabled = on; Refresh(); };
        UiKit.AddTooltip(s_queueMode, "Queue mode",
            "While on, clicking any skill's normal Start / Cut / Mine / Fight button adds it to the queue (with the limit below) " +
            "instead of switching to it. Your current action keeps going.");
        header.AddChild(s_queueMode);
        var help = UiKit.HintIcon("How the queue works",
            "Add to queue: queues the action running now.\n" +
            "Queue mode: click any skill's normal button to queue it instead; your current action keeps going (its progress bar restarts).\n" +
            "Limits: \"Until it stops\" runs until the game stops it; \"Until level\" uses each skill's next milestone; \"For N items\" counts products, not side drops.\n" +
            "When the running action stops by itself - limit reached, out of materials, bank full, no traps - the next queued action starts. " +
            "Your own Stop does not advance the queue. When nothing is running, press Start (or tick Run queue) to begin it.\n" +
            "Loop: each action goes back on the end when it starts. Save / Load keep named queues.\n" +
            "Crafting entries gather or make missing materials first (setting: Action queue). Time left uses each action's all-time rate from the Drop log, at the current speed.");
        header.AddChild(help);
        body.AddChild(header);
        s_logBody = BuildLogBody();
        s_logBody.Visible = false;
        body.AddChild(s_logBody);
        var outer = body;
        body = new VBoxContainer();
        body.AddThemeConstantOverride("separation", 8);
        outer.AddChild(body);
        s_queueBody = body;

        s_current = TopBar.RawLabel("", 15, null);
        body.AddChild(s_current);
        s_rule = TopBar.RawLabel("", 14, new Color(0.6f, 0.9f, 0.6f));
        body.AddChild(s_rule);

        var ruleRow = new HBoxContainer();
        ruleRow.AddThemeConstantOverride("separation", 6);
        s_kind = new OptionButton { FocusMode = Control.FocusModeEnum.None };
        s_kind.AddItem("Until it stops", (int)StopKind.None);
        s_kind.AddItem("Until level", (int)StopKind.Level);
        s_kind.AddItem("For N items", (int)StopKind.Items);
        s_kind.Select(0);
        s_value = new SpinBox { MinValue = 1, MaxValue = 1_000_000, Value = 100, CustomMinimumSize = new Vector2(120, 0), Visible = false };
        s_kind.ItemSelected += _ => SyncValueToKind();
        ruleRow.AddChild(s_kind);
        ruleRow.AddChild(s_value);
        var setRule = TopBar.GameButton("Set on current");
        setRule.CustomMinimumSize = new Vector2(130, 36);
        setRule.Pressed += () => ActionQueue.SetRuleOnRunning(Kind, (int)s_value!.Value);
        ruleRow.AddChild(setRule);
        var add = TopBar.GameButton("Add to queue");
        add.CustomMinimumSize = new Vector2(130, 36);
        add.Pressed += () =>
        {
            if (!ActionQueue.AddRunning(Kind, (int)s_value!.Value, out string reason)) ShowStatus(reason);
        };
        ruleRow.AddChild(add);
        body.AddChild(ruleRow);

        body.AddChild(new HSeparator());
        var queueHeader = new HBoxContainer();
        queueHeader.AddChild(TopBar.RawLabel("Queue", 18, Gold));
        queueHeader.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        s_enabled = new CheckBox { Text = "Run queue", FocusMode = Control.FocusModeEnum.None, AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled };
        s_enabled.Toggled += on =>
        {
            ActionQueue.SetEnabled(on);
            if (on) StartQueue();
        };
        queueHeader.AddChild(s_enabled);
        s_loop = new CheckBox { Text = "Loop", FocusMode = Control.FocusModeEnum.None, AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled };
        s_loop.Toggled += ActionQueue.SetLoop;
        UiKit.AddTooltip(s_loop, "Loop", "Each action goes back on the end of the queue when it starts, so the queue repeats until you stop it.");
        queueHeader.AddChild(s_loop);
        s_start = TopBar.GameButton("Start");
        s_start.Pressed += StartQueue;
        UiKit.AddTooltip(s_start, "Start queue", "Nothing is running: start the first queued action now.");
        queueHeader.AddChild(s_start);
        var clear = TopBar.GameButton("Clear");
        clear.Pressed += ActionQueue.Clear;
        queueHeader.AddChild(clear);
        body.AddChild(queueHeader);

        s_list = new VBoxContainer();
        body.AddChild(s_list);
        s_total = TopBar.RawLabel("", 14, new Color(0.6f, 0.9f, 0.6f));
        body.AddChild(s_total);

        // Saved queues: name + Save, then pick + Load / Delete.
        var presetRow = new HBoxContainer();
        presetRow.AddThemeConstantOverride("separation", 6);
        s_presetName = new LineEdit { PlaceholderText = "Name this queue...", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        presetRow.AddChild(s_presetName);
        var savePreset = TopBar.GameButton("Save");
        savePreset.Pressed += () =>
        {
            string name = s_presetName!.Text.Trim();
            if (name.Length == 0) name = s_presets is { ItemCount: > 0 } && s_presets.Selected >= 0 ? s_presets.GetItemText(s_presets.Selected) : "";
            if (name.Length == 0) { ShowStatus("Type a name for the queue first."); return; }
            if (ActionQueue.SavePreset(name)) { ShowStatus($"Saved queue \"{name}\"."); s_presetName.Text = ""; SelectPreset(name); }
        };
        UiKit.AddTooltip(savePreset, "Save queue", "Save the queue under the typed name (or over the selected saved queue). Gather steps the queue added itself aren't saved.");
        presetRow.AddChild(savePreset);
        s_presets = new OptionButton { FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(150, 0) };
        presetRow.AddChild(s_presets);
        var load = TopBar.GameButton("Load");
        load.Pressed += () =>
        {
            if (SelectedPreset() is not { } name) return;
            ActionQueue.LoadPreset(name);
            ShowStatus($"Loaded \"{name}\". Press Start if nothing is running.");
        };
        UiKit.AddTooltip(load, "Load queue", "Replace the queue with the selected saved queue.");
        presetRow.AddChild(load);
        var delete = TopBar.GameButton("Delete");
        delete.Pressed += () => { if (SelectedPreset() is { } name) { ActionQueue.DeletePreset(name); ShowStatus($"Deleted \"{name}\"."); } };
        presetRow.AddChild(delete);
        body.AddChild(presetRow);

        s_status = TopBar.RawLabel("", 14, new Color(1f, 0.55f, 0.45f));
        s_status.Visible = false;
        s_status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.AddChild(s_status);
        return panel;
    }

    private static readonly Color Gold = new(0.98f, 0.8f, 0.3f);

    private static StopKind Kind => s_kind == null ? StopKind.None : (StopKind)s_kind.GetSelectedId();

    /// <summary>
    /// "Until it stops" needs no number. "Until level" defaults to the running skill's next milestone
    /// (never at or below the current level); "For N items" keeps the last count.
    /// </summary>
    private static void SyncValueToKind()
    {
        if (!Valid(s_value)) return;
        s_value!.Visible = Kind != StopKind.None;
        if (Kind != StopKind.Level) { s_value.MinValue = 1; return; }
        var running = ActionQueue.RunningAction;
        if (running == null) return;
        int current = running.Entity.Components.Values
            .OfType<GamesByMalcs.Melvor2.Components.Experience.ExperienceComponent>().FirstOrDefault()?.Level ?? 0;
        s_value.MinValue = current + 1;
        s_value.Value = UiKit.NextMilestoneLevel(running.Entity);
    }

    private static StyleBoxFlat PanelStyle() => new()
    {
        BgColor = new Color(0.11f, 0.10f, 0.09f, 0.97f),
        BorderColor = new Color(0.55f, 0.45f, 0.25f),
        BorderWidthLeft = 2, BorderWidthTop = 2, BorderWidthRight = 2, BorderWidthBottom = 2,
        CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
        ShadowColor = new Color(0, 0, 0, 0.5f),
        ShadowSize = 8,
    };

    private static void StartQueue()
    {
        if (ActionQueue.State is { Enabled: false }) ActionQueue.SetEnabled(true);
        string status = ActionQueue.StartIfIdle();
        if (status.Length > 0) ShowStatus(status);
        Refresh();
    }

    private static string? SelectedPreset() =>
        s_presets is { ItemCount: > 0 } p && p.Selected >= 0 && !p.IsItemDisabled(p.Selected) ? p.GetItemText(p.Selected) : null;

    private static void SelectPreset(string name)
    {
        RefreshPresets();
        for (int i = 0; s_presets != null && i < s_presets.ItemCount; i++)
            if (string.Equals(s_presets.GetItemText(i), name, StringComparison.OrdinalIgnoreCase)) s_presets.Select(i);
    }

    private static void RefreshPresets()
    {
        if (!Valid(s_presets)) return;
        string sig = string.Join("|", ActionQueue.Presets.Keys);
        if (sig == s_presetSignature) return;
        s_presetSignature = sig;
        string? keep = SelectedPreset();
        s_presets!.Clear();
        if (ActionQueue.Presets.Count == 0) { s_presets.AddItem("No saved queues"); s_presets.SetItemDisabled(0, true); return; }
        foreach (string name in ActionQueue.Presets.Keys) s_presets.AddItem(name);
        int idx = keep == null ? 0 : Math.Max(0, ActionQueue.Presets.Keys.ToList().FindIndex(k => string.Equals(k, keep, StringComparison.OrdinalIgnoreCase)));
        s_presets.Select(idx);
    }

    // ---- drop log ----------------------------------------------------------------------------------
    private static VBoxContainer BuildLogBody()
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        box.AddChild(new HSeparator());
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        row.AddChild(TopBar.RawLabel("Drop log", 18, Gold));
        s_logPick = new OptionButton { FocusMode = Control.FocusModeEnum.None, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipText = true };
        s_logPick.ItemSelected += _ => RefreshLog();
        row.AddChild(s_logPick);
        s_logReset = TopBar.GameButton("Reset");
        s_logReset.Pressed += () =>
        {
            if (LogKey() is not { } key) return;
            if (Time.GetTicksMsec() - s_resetArmedAt > 4000) { s_resetArmedAt = Time.GetTicksMsec(); s_logReset!.Text = "Confirm reset"; return; }
            s_resetArmedAt = 0;
            s_logReset!.Text = "Reset";
            ActionLog.Reset(key);
            RefreshLog();
        };
        UiKit.AddTooltip(s_logReset, "Reset", "Clear this action's totals (press twice).");
        row.AddChild(s_logReset);
        box.AddChild(row);

        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 360), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        s_logText = new RichTextLabel
        {
            FitContent = true, ScrollActive = false, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled, SelectionEnabled = false,
        };
        s_logText.AddThemeFontSizeOverride("normal_font_size", 14);
        scroll.AddChild(s_logText);
        box.AddChild(scroll);
        return box;
    }

    internal static void ToggleLog()
    {
        if (!Valid(s_logBody) || !Valid(s_queueBody)) return;
        bool showLog = !s_logBody!.Visible;
        s_logBody.Visible = showLog;
        s_queueBody!.Visible = !showLog;
        s_logToggle!.Text = showLog ? "Queue" : "Drop log";
        s_logPickSignature = null;
        Refresh();
        Reposition();
    }

    private static readonly List<string> s_logKeys = new();

    private static string? LogKey() =>
        s_logPick != null && s_logPick.Selected >= 0 && s_logPick.Selected < s_logKeys.Count ? s_logKeys[s_logPick.Selected] : null;

    // Offline catch-up ticks on a background thread and can change the counts mid-read; retry next second.
    private static void RefreshLog()
    {
        try { RefreshLogUnsafe(); }
        catch (InvalidOperationException) { }
    }

    private static void RefreshLogUnsafe()
    {
        if (!Valid(s_logPick) || !Valid(s_logText) || ActionLog.ForSave is not { } log) return;
        // Current action first, then by time spent.
        string? current = ActionLog.CurrentKey;
        var keys = log.OrderByDescending(kv => kv.Key == current).ThenByDescending(kv => kv.Value.GameMs).Select(kv => kv.Key).ToList();
        string sig = string.Join("|", keys.Select(k => k + log[k].Label)) + "|" + current;
        if (sig != s_logPickSignature)
        {
            string? keep = LogKey();
            if (s_logPickSignature == null) keep = current ?? keep; // opening: show the running action
            s_logPickSignature = sig;
            s_logKeys.Clear();
            s_logKeys.AddRange(keys);
            s_logPick!.Clear();
            foreach (string k in keys) s_logPick.AddItem((k == current ? "> " : "") + log[k].Label);
            if (keys.Count > 0) s_logPick.Select(Math.Max(0, keep == null ? 0 : keys.IndexOf(keep)));
        }
        if (Time.GetTicksMsec() - s_resetArmedAt > 4000 && Valid(s_logReset)) s_logReset!.Text = "Reset";

        var t = s_logText!;
        t.Clear();
        if (LogKey() is not { } key || !log.TryGetValue(key, out var e))
        {
            t.AddText("Nothing logged yet. Every action you run from now on is counted here.");
            return;
        }
        double hours = e.GameMs / 3_600_000.0;
        string PerHour(double n) => hours > 0.01 ? $"  ({n / hours:N0}/h)" : "";
        t.PushColor(new Color(0.98f, 0.8f, 0.3f));
        t.AddText(e.Label);
        t.Pop();
        t.AddText($"\nSince {e.Since:d MMM yyyy}  |  {e.Actions:N0} actions{PerHour(e.Actions)}  |  {QueueEta.FormatGame(e.GameMs)} game time");
        if (e.Xp > 0) t.AddText($"  |  {e.Xp:N0} XP{PerHour(e.Xp)}");
        t.AddText("\n");
        var data = TimeBank.TimeBank.Game?.Data;
        foreach (var (id, n) in e.Items.OrderByDescending(kv => kv.Value))
        {
            GamesByMalcs.Melvor2.Data.Items.Item? item = null;
            if (data != null && data.TryGetObject<GamesByMalcs.Melvor2.Data.Items.Item>(id, out var found)) item = found;
            t.AddText("\n");
            if (item?.Icon != null) { t.AddImage(item.Icon, 20, 20); t.AddText(" "); }
            string name = item?.DisplayName.Plural ?? id;
            string pct = e.Actions > 0 ? $"  {(double)n / e.Actions:0.###} per action" : "";
            t.AddText($"{name}  x{n:N0}{PerHour(n)}{pct}");
        }
        if (e.Items.Count == 0) t.AddText("\nNo items yet.");
    }

    private static void Reposition()
    {
        Callable.From(() =>
        {
            if (!Valid(s_panel) || !Valid(s_button) || !Valid(s_popup)) return;
            var rect = s_button!.GetGlobalRect();
            s_panel!.ResetSize();
            var size = s_panel.GetCombinedMinimumSize();
            float screenW = s_popup!.GetViewportRect().Size.X;
            float x = Math.Clamp(rect.Position.X + rect.Size.X / 2 - size.X / 2, 8, Math.Max(8, screenW - size.X - 8));
            s_panel.Position = new Vector2(x, rect.End.Y + 6);
            s_panel.Size = size;
        }).CallDeferred();
    }

    private static void ShowStatus(string text)
    {
        if (!Valid(s_status)) return;
        s_status!.Text = text;
        s_status.Visible = text.Length > 0;
    }

    internal static void Toggle()
    {
        if (!Valid(s_popup) || !Valid(s_button)) return;
        if (s_popup!.Visible) { s_popup.Close(); return; }
        (s_popup.GetParent() as GameView)?.CloseExclusiveModals();
        ShowStatus("");
        s_listSignature = null;
        SyncValueToKind(); // the running skill / level may have changed since last time
        Refresh();
        s_popup.Open();
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

    private static void Refresh()
    {
        if (!Valid(s_button)) return;
        var state = ActionQueue.State;
        int queued = state?.Entries.Count ?? 0;
        if (Valid(s_buttonLabel)) s_buttonLabel!.Text = QueueMode.Enabled ? $"Queue mode ({queued})" : queued > 0 ? $"Actions ({queued})" : "Actions";
        if (Valid(s_queueMode)) s_queueMode!.SetPressedNoSignal(QueueMode.Enabled);
        if (!Valid(s_popup) || !s_popup!.Visible) return;

        var running = ActionQueue.RunningAction;
        var target = running == null ? null : SkillActions.Capture(running);
        s_current!.Text = target?.Label ?? "Idle";

        var rule = state?.Current;
        s_rule!.Text = rule == null ? "Runs until it stops" : rule.Stop switch
        {
            StopKind.Level => $"Stops at level {rule.StopValue} (now {ActionQueue.CurrentLevel?.ToString() ?? "?"})",
            StopKind.Items => $"Stops after {rule.StopValue:N0} items ({ActionQueue.ItemProgress:N0} so far)",
            _ => "Runs until it stops",
        };
        string currentEta = "";
        List<string> etas = new();
        string total = "";
        if (TimeBank.TimeBank.Game is { } game)
        {
            try { (currentEta, etas, total) = QueueEta.All(game); }
            catch (Exception ex) { Plugin.Log("ETA: " + ex.Message); }
        }
        if (running != null && currentEta.Length > 0 && currentEta != "no end") s_rule.Text += $" - {currentEta} left";
        if (Valid(s_total)) { s_total!.Text = queued > 0 && total.Length > 0 ? "Whole queue: " + total : ""; s_total.Visible = s_total.Text.Length > 0; }
        for (int i = 0; i < s_etaLabels.Count && i < etas.Count; i++)
            if (Valid(s_etaLabels[i])) s_etaLabels[i].Text = etas[i];
        s_enabled!.SetPressedNoSignal(state?.Enabled ?? true);
        if (Valid(s_loop)) s_loop!.SetPressedNoSignal(state?.Loop ?? false);
        RefreshPresets();
        if (Valid(s_logBody) && s_logBody!.Visible) RefreshLog();
        if (Valid(s_start)) s_start!.Visible = queued > 0 && running is not { IsStarted: true };

        // Rebuild the list only when it changed (keeps buttons stable under the mouse).
        string sig = string.Join("|", state?.Entries.ConvertAll(e => e.Describe()) ?? new List<string>());
        if (sig == s_listSignature) return;
        s_listSignature = sig;
        foreach (Node c in s_list!.GetChildren()) { s_list.RemoveChild(c); c.QueueFree(); }
        s_etaLabels.Clear();
        if (queued == 0) { s_list.AddChild(TopBar.RawLabel("Nothing queued.", 14, new Color(1, 1, 1, 0.6f))); return; }
        for (int i = 0; i < queued; i++)
        {
            int index = i;
            var row = new HBoxContainer();
            var label = TopBar.RawLabel($"{i + 1}. {state!.Entries[i].Describe()}", 14, null);
            label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            label.ClipText = true;
            row.AddChild(label);
            row.AddThemeConstantOverride("separation", 8);
            var eta = TopBar.RawLabel(i < etas.Count ? etas[i] : "", 13, new Color(0.6f, 0.9f, 0.6f));
            eta.CustomMinimumSize = new Vector2(100, 0);
            eta.HorizontalAlignment = HorizontalAlignment.Right;
            eta.VerticalAlignment = VerticalAlignment.Center;
            s_etaLabels.Add(eta);
            row.AddChild(eta);
            var up = TopBar.GameButton("Up");
            up.Disabled = i == 0;
            up.Pressed += () => ActionQueue.MoveUp(index);
            row.AddChild(up);
            var remove = TopBar.GameButton("X");
            remove.Pressed += () => ActionQueue.RemoveAt(index);
            row.AddChild(remove);
            s_list.AddChild(row);
        }
    }
}
