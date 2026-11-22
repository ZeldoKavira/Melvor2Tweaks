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
            title.ClipText = false;
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
        QueueMode.Captured += ShowStatus;
        Refresh();
    }

    internal static void Detach()
    {
        ActionQueue.Changed -= s_onChanged;
        QueueMode.Captured -= ShowStatus;
        foreach (Node? n in new Node?[] { s_button, s_popup })
        {
            if (!Valid(n)) continue;
            n!.GetParent()?.RemoveChild(n);
            n.QueueFree();
        }
        s_button = null; s_buttonLabel = null; s_popup = null; s_panel = null; s_current = null; s_rule = null;
        s_kind = null; s_value = null; s_enabled = null; s_start = null; s_loop = null; s_total = null; s_presetName = null; s_presets = null;
        s_presetSignature = null; s_queueBody = null; s_logBody = null; s_logToggle = null; s_logPick = null; s_logPickSignature = null;
        s_logText = null; s_logReset = null; s_etaLabels.Clear(); s_queueMode = null; s_list = null; s_status = null; s_listSignature = null; s_phoneStatus = null; s_phoneOn = null; s_phoneLan = null;
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
        var phone = TopBar.GameButton("Phone");
        UiKit.AddTooltip(phone, "Phone access", "Control this game from your phone's browser: copy the login link, set the tunnel address.");
        header.AddChild(phone);
        s_queueMode = new CheckBox { Text = "Queue mode", FocusMode = Control.FocusModeEnum.None, AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled };
        s_queueMode.Toggled += on => { QueueMode.Enabled = on; Refresh(); };
        UiKit.AddTooltip(s_queueMode, "Queue mode",
            "While on, clicking any skill's normal Start / Cut / Mine / Fight button asks when it should stop, then adds it to the queue " +
            "instead of switching to it. Your current action keeps going.");
        header.AddChild(s_queueMode);
        var help = UiKit.HintIcon("How the queue works",
            "Add to queue: queues the action running now.\n" +
            "Queue mode: click any skill's normal button, then pick when it should stop, to queue it instead; your current action keeps going (its progress bar restarts).\n" +
            "Limits: \"Until it stops\" runs until the game stops it; \"Until level\"; \"For N items\" counts products, not side drops; " +
            "\"Until all nodes\" (Astrology) studies until every node left in the constellation can be unlocked.\n" +
            "When the running action stops by itself - limit reached, out of materials, bank full, no traps - the next queued action starts. " +
            "Your own Stop does not advance the queue. When nothing is running, press Start (or tick Run queue) to begin it.\n" +
            "Loop: each action goes back on the end when it starts. Save / Load keep named queues.\n" +
            "Crafting entries gather or make missing materials first (setting: Action queue). Time left is game time, from each action's all-time rate in the Drop log: speed makes it pass faster, and a 1h time-bank skip takes 1h off.");
        header.AddChild(help);
        body.AddChild(header);
        var phoneBody = BuildPhoneBody();
        phoneBody.Visible = false;
        phone.Pressed += () => { phoneBody.Visible = !phoneBody.Visible; RefreshPhone(); };
        body.AddChild(phoneBody);
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
        s_kind.AddItem("Until all nodes", (int)StopKind.Nodes);
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

    // ---- phone access ------------------------------------------------------------------------------
    private static Label? s_phoneStatus;
    private static CheckBox? s_phoneOn;
    private static CheckBox? s_phoneLan;

    /// <summary>Phone access: on/off, the tunnel's public address, copy the login link, make a new key.</summary>
    private static VBoxContainer BuildPhoneBody()
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", 6);
        s_phoneOn = new CheckBox { Text = "Phone access", FocusMode = Control.FocusModeEnum.None, AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled };
        s_phoneOn.Toggled += on => { Settings.Current.RemoteEnabled = on; Settings.Save(); RefreshPhone(); };
        top.AddChild(s_phoneOn);
        s_phoneStatus = TopBar.RawLabel("", 14, new Color(0.6f, 0.9f, 0.6f));
        s_phoneStatus.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        s_phoneStatus.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        top.AddChild(s_phoneStatus);
        box.AddChild(top);

        var urlRow = new HBoxContainer();
        urlRow.AddThemeConstantOverride("separation", 6);
        var url = new LineEdit
        {
            PlaceholderText = "Tunnel address, e.g. https://melvor.example.com",
            Text = Settings.Current.RemotePublicUrl ?? "",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        void SaveUrl() { Settings.Current.RemotePublicUrl = url.Text.Trim(); Settings.Save(); RefreshPhone(); }
        url.TextSubmitted += _ => SaveUrl();
        url.FocusExited += SaveUrl;
        urlRow.AddChild(url);
        var copy = TopBar.GameButton("Copy link");
        copy.Pressed += () =>
        {
            SaveUrl();
            DisplayServer.ClipboardSet(Remote.RemoteServer.LoginLink());
            ShowStatus("Phone link copied. Send it to your phone and open it there once; it keeps you logged in.");
        };
        UiKit.AddTooltip(copy, "Copy link", "Copies the login link (tunnel address + your secret key). Anyone with it can control this game, so only send it to yourself.");
        urlRow.AddChild(copy);
        var reset = TopBar.GameButton("New key");
        reset.Pressed += () => { Remote.RemoteServer.ResetKey(); ShowStatus("New key made: every phone is logged out until it opens the new link."); };
        UiKit.AddTooltip(reset, "New key", "Logs every phone out. Use it if the link got out.");
        urlRow.AddChild(reset);
        box.AddChild(urlRow);

        // A reverse proxy on another machine (e.g. Traefik on a home server) connects over the network.
        var lanRow = new HBoxContainer();
        lanRow.AddThemeConstantOverride("separation", 6);
        s_phoneLan = new CheckBox { Text = "Let your server connect", FocusMode = Control.FocusModeEnum.None, AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled };
        s_phoneLan.Toggled += on => { Settings.Current.RemoteLan = on; Settings.Save(); RefreshPhone(); };
        UiKit.AddTooltip(s_phoneLan, "Let your server connect",
            "Also listen on the network, accepting only the addresses typed here (your reverse proxy). Windows asks once whether to allow it.");
        lanRow.AddChild(s_phoneLan);
        var allowed = new LineEdit
        {
            PlaceholderText = "Server address, e.g. 192.168.1.10",
            Text = Settings.Current.RemoteAllowedClients ?? "",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        void SaveAllowed() { Settings.Current.RemoteAllowedClients = allowed.Text.Trim(); Settings.Save(); RefreshPhone(); }
        allowed.TextSubmitted += _ => SaveAllowed();
        allowed.FocusExited += SaveAllowed;
        lanRow.AddChild(allowed);
        box.AddChild(lanRow);
        box.AddChild(new HSeparator());
        return box;
    }

    private static void RefreshPhone()
    {
        if (Valid(s_phoneOn)) s_phoneOn!.SetPressedNoSignal(Settings.Current.RemoteEnabled);
        if (Valid(s_phoneLan)) s_phoneLan!.SetPressedNoSignal(Settings.Current.RemoteLan);
        if (!Valid(s_phoneStatus)) return;
        s_phoneStatus!.Text = !Settings.Current.RemoteEnabled ? "Off"
            : Remote.RemoteServer.Running ? $"On: http://localhost:{Remote.RemoteServer.Port}" +
                                            (Settings.Current.RemoteLan ? $" + network ({Settings.Current.RemoteAllowedClients})" : "")
            : $"Couldn't start: {Remote.RemoteServer.LastError}";
    }

    private static StopKind Kind => s_kind == null ? StopKind.None : (StopKind)s_kind.GetSelectedId();

    /// <summary>
    /// "Until it stops" needs no number. "Until level" defaults to the running skill's next milestone
    /// (never at or below the current level); "For N items" keeps the last count.
    /// </summary>
    private static void SyncValueToKind()
    {
        if (!Valid(s_value)) return;
        s_value!.Visible = Kind is StopKind.Level or StopKind.Items;
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

    // ---- drag and drop -----------------------------------------------------------------------------
    private static readonly Color DropGold = new(0.98f, 0.8f, 0.3f);
    private static readonly List<PanelContainer> s_dragRows = new();
    private static Panel? s_dropBar;          // glowing bar floating over the list where the entry will land
    private static StyleBoxFlat? s_dropBarStyle;
    private static Tween? s_barTween, s_pulseTween;
    private static int s_dropSlot = -1;
    private static PanelContainer? s_dragged;
    private static Timer? s_dragWatch;
    private static int s_flashIndex = -1;

    /// <summary>
    /// Wraps a queue row so it can be dragged onto another row, taking that row's place (so it lands below
    /// it when dragged down, above it when dragged up). Uses Control.SetDragForwarding, so no Godot
    /// subclass is needed. While dragging: the row fades, a card follows the mouse, and a glowing gold bar
    /// glides to where it will land. After the drop the moved row flashes gold.
    /// </summary>
    private static PanelContainer DragRow(HBoxContainer row, int index, string text)
    {
        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop, MouseDefaultCursorShape = Control.CursorShape.Move };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(DropGold, 0f), ContentMarginTop = 2, ContentMarginBottom = 2, ContentMarginLeft = 4, ContentMarginRight = 0,
            CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4,
        };
        panel.AddThemeStyleboxOverride("panel", style);
        panel.AddChild(row);
        s_dragRows.Add(panel);
        if (index == s_flashIndex) { s_flashIndex = -1; Flash(panel, style); }

        panel.SetDragForwarding(
            Callable.From((Vector2 _) =>
            {
                panel.SetDragPreview(DragCard(text, panel.Size.X));
                BeginDrag(panel);
                return Variant.From(index);
            }),
            Callable.From((Vector2 at, Variant data) =>
            {
                if (data.VariantType != Variant.Type.Int) return false;
                int from = data.AsInt32();
                // Moving up lands above the row, moving down below it; over itself: nowhere.
                MoveDropBar(from == index ? -1 : from > index ? index : index + 1);
                return true;
            }),
            Callable.From((Vector2 at, Variant data) =>
            {
                int from = data.AsInt32();
                EndDrag();
                if (s_timing) Plugin.Log($"drop: from {from} onto row {index}");
                if (from == index) return;
                s_flashIndex = index;
                ActionQueue.Move(from, index);
            }));
        return panel;
    }

    /// <summary>The card that follows the mouse: the entry on a dark panel with a gold border.</summary>
    private static Control DragCard(string text, float width)
    {
        var card = new PanelContainer { Modulate = new Color(1, 1, 1, 0.92f), Rotation = -0.02f };
        card.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.14f, 0.12f, 0.10f, 0.97f), BorderColor = DropGold,
            BorderWidthLeft = 2, BorderWidthTop = 2, BorderWidthRight = 2, BorderWidthBottom = 2,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 6, ContentMarginBottom = 6,
            ShadowColor = new Color(0, 0, 0, 0.55f), ShadowSize = 10, ShadowOffset = new Vector2(3, 4),
        });
        card.CustomMinimumSize = new Vector2(Math.Max(240, width * 0.75f), 0);
        card.AddChild(TopBar.RawLabel(text, 15, DropGold));
        return card;
    }

    private static void BeginDrag(PanelContainer source)
    {
        s_dragged = source;
        source.CreateTween().TweenProperty(source, "modulate:a", 0.3f, 0.12f);
        s_dropSlot = -1;
        if (!Valid(s_dragWatch) && Valid(s_panel))
        {
            s_dragWatch = new Timer { WaitTime = 0.05, ProcessMode = Node.ProcessModeEnum.Always };
            s_dragWatch.Timeout += () => { if (Valid(s_panel) && !s_panel!.GetViewport().GuiIsDragging()) EndDrag(); };
            s_panel!.AddChild(s_dragWatch);
        }
        s_dragWatch?.Start();
    }

    private static void EndDrag()
    {
        if (Valid(s_dragWatch)) s_dragWatch!.Stop();
        if (Valid(s_dragged)) s_dragged!.CreateTween().TweenProperty(s_dragged, "modulate:a", 1f, 0.15f);
        s_dragged = null;
        s_dropSlot = -1;
        s_barTween?.Kill();
        s_pulseTween?.Kill();
        if (Valid(s_dropBar)) s_dropBar!.Visible = false;
    }

    private static Panel? DropBar()
    {
        if (Valid(s_dropBar)) return s_dropBar;
        if (!Valid(s_panel)) return null;
        s_dropBarStyle = new StyleBoxFlat
        {
            BgColor = DropGold,
            CornerRadiusTopLeft = 3, CornerRadiusTopRight = 3, CornerRadiusBottomLeft = 3, CornerRadiusBottomRight = 3,
            ShadowColor = new Color(DropGold, 0.75f), ShadowSize = 6,
        };
        // Top level: floats over the list without pushing rows around (which would move the target under the mouse).
        s_dropBar = new Panel { TopLevel = true, MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false, ZIndex = 10 };
        s_dropBar.AddThemeStyleboxOverride("panel", s_dropBarStyle);
        s_panel!.AddChild(s_dropBar);
        return s_dropBar;
    }

    /// <summary>Glides the bar to the gap before row <paramref name="slot"/> (Count = after the last row); -1 hides it.</summary>
    private static void MoveDropBar(int slot)
    {
        if (slot == s_dropSlot) return;
        var bar = DropBar();
        if (bar == null || s_dragRows.Count == 0) return;
        bool wasHidden = !bar.Visible || s_dropSlot < 0;
        s_dropSlot = slot;
        if (slot < 0) { s_barTween?.Kill(); s_pulseTween?.Kill(); bar.Visible = false; return; }

        var first = s_dragRows[0].GetGlobalRect();
        float y = slot < s_dragRows.Count
            ? s_dragRows[slot].GetGlobalRect().Position.Y - 2
            : s_dragRows[^1].GetGlobalRect().End.Y + 2;
        var size = new Vector2(first.Size.X, 4);
        var target = new Vector2(first.Position.X, y - size.Y / 2);
        bar.Size = size;
        bar.PivotOffset = size / 2;

        s_barTween?.Kill();
        s_barTween = bar.CreateTween().SetParallel().SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
        if (wasHidden)
        {
            bar.GlobalPosition = target;
            bar.Scale = new Vector2(0.1f, 1);
            bar.Visible = true;
            s_barTween.TweenProperty(bar, "scale", Vector2.One, 0.18f);
        }
        else
        {
            s_barTween.TweenProperty(bar, "global_position", target, 0.12f);
        }

        if (s_pulseTween == null || !s_pulseTween.IsValid() || !s_pulseTween.IsRunning())
        {
            s_pulseTween = bar.CreateTween().SetLoops().SetTrans(Tween.TransitionType.Sine);
            s_pulseTween.TweenProperty(s_dropBarStyle, "shadow_size", 12, 0.45f);
            s_pulseTween.Parallel().TweenProperty(bar, "modulate:a", 0.65f, 0.45f);
            s_pulseTween.TweenProperty(s_dropBarStyle, "shadow_size", 4, 0.45f);
            s_pulseTween.Parallel().TweenProperty(bar, "modulate:a", 1f, 0.45f);
        }
    }

    /// <summary>The moved row lights up gold and fades back.</summary>
    private static void Flash(PanelContainer panel, StyleBoxFlat style)
    {
        style.BgColor = new Color(DropGold, 0.45f);
        panel.CreateTween().SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Quad)
            .TweenProperty(style, "bg_color", new Color(DropGold, 0f), 0.9f);
    }

    /// <summary>Dev aid: drags row <paramref name="from"/> onto the top half of row <paramref name="onto"/> with real mouse events.</summary>
    internal static async void DevDrag(int from, int onto, double holdSeconds = 0)
    {
        if (from >= s_dragRows.Count || onto >= s_dragRows.Count || !Valid(s_dragRows[from])) { Plugin.Log("drag-test: rows not there (open the Actions menu)"); return; }
        var vp = s_dragRows[from].GetViewport();
        var tree = (SceneTree)Engine.GetMainLoop();
        Vector2 start = s_dragRows[from].GetGlobalRect().GetCenter() with { X = s_dragRows[from].GetGlobalRect().Position.X + 6 };
        var end = s_dragRows[onto].GetGlobalRect();
        Plugin.Log($"drag-test: from rect {s_dragRows[from].GetGlobalRect()} onto rect {end}");
        Vector2 target = new(end.Position.X + 6, end.Position.Y + end.Size.Y * 0.25f);
        var canvas = vp.GetFinalTransform(); // GUI coords -> window coords
        void Push(InputEvent e) => vp.PushInput(e, false);
        Push(new InputEventMouseMotion { Position = canvas * start, GlobalPosition = canvas * start });
        await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        Push(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = canvas * start, GlobalPosition = canvas * start, ButtonMask = MouseButtonMask.Left });
        for (int i = 1; i <= 10; i++)
        {
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            var p = canvas * start.Lerp(target, i / 10f);
            Push(new InputEventMouseMotion { Position = p, GlobalPosition = p, ButtonMask = MouseButtonMask.Left, Relative = (target - start) / 10 });
        }
        await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        Plugin.Log($"drag-test: dragging={vp.GuiIsDragging()}");
        if (holdSeconds > 0) await tree.ToSignal(tree.CreateTimer(holdSeconds), SceneTreeTimer.SignalName.Timeout);
        Push(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = canvas * target, GlobalPosition = canvas * target });
        await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        Plugin.Log("drag-test: queue now " + string.Join(" | ", ActionQueue.State?.Entries.ConvertAll(e => e.Target.Label + " " + e.StopValue) ?? new()));
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

    internal static bool s_timing;

    private static void Refresh()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        RefreshInner(sw);
        if (s_timing) Plugin.Log($"timing: refresh total {sw.Elapsed.TotalMilliseconds:0.0} ms");
    }

    private static void RefreshInner(System.Diagnostics.Stopwatch sw)
    {
        if (!Valid(s_button)) return;
        var state = ActionQueue.State;
        int queued = state?.Entries.Count ?? 0;
        if (Valid(s_buttonLabel)) s_buttonLabel!.Text = QueueMode.Enabled ? $"Queuing ({queued})" : queued > 0 ? $"Actions ({queued})" : "Actions";
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
            StopKind.Nodes => "Stops when every node can be unlocked" +
                              (TimeBank.TimeBank.Game is { } g && AstrologyGoal.Progress(g, rule.Target) is { Length: > 0 } p ? $" ({p})" : ""),
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
        if (s_timing) Plugin.Log($"timing: eta done at {sw.Elapsed.TotalMilliseconds:0.0} ms");
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
        s_dragRows.Clear();
        if (queued == 0) { s_list.AddChild(TopBar.RawLabel("Nothing queued.", 14, new Color(1, 1, 1, 0.6f))); return; }
        for (int i = 0; i < queued; i++)
        {
            int index = i;
            var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
            var grip = TopBar.RawLabel("::", 16, new Color(1, 1, 1, 0.45f));
            grip.VerticalAlignment = VerticalAlignment.Center;
            grip.MouseFilter = Control.MouseFilterEnum.Pass;
            grip.MouseDefaultCursorShape = Control.CursorShape.Move;
            row.AddChild(grip);
            var label = TopBar.RawLabel($"{i + 1}. {state!.Entries[i].Describe()}", 14, null);
            label.MouseFilter = Control.MouseFilterEnum.Pass;
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
            if (i == 0)
            {
                // The top entry moves up past the running action: it starts now, the current action
                // goes back in the queue behind it.
                up.Disabled = ActionQueue.RunningAction is not { IsStarted: true };
                up.Pressed += () => ActionQueue.BumpCurrent();
                UiKit.AddTooltip(up, "Start now", "Stops the current action and starts this one. The current action goes back " +
                                                   "in the queue right after it, with what it had left.");
            }
            else up.Pressed += () => ActionQueue.MoveUp(index);
            row.AddChild(up);
            var remove = TopBar.GameButton("X");
            remove.Pressed += () => ActionQueue.RemoveAt(index);
            row.AddChild(remove);
            s_list.AddChild(DragRow(row, index, label.Text));
        }
    }
}
