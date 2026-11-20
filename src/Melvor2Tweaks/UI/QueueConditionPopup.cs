using System;
using System.Linq;
using GamesByMalcs.Melvor2.Components.Experience;
using GamesByMalcs.Melvor2.Entities;
using Godot;
using Melvor2Tweaks.Actions;

namespace Melvor2Tweaks.UI;

/// <summary>
/// Queue mode's "when should it stop?" popup: shown when a skill's own Start / Cut / Study... button is
/// clicked in Queue mode. One button per stop condition the action supports; clicking one queues it.
/// Cancel (or a click outside) queues nothing.
/// </summary>
internal static class QueueConditionPopup
{
    private static CanvasLayer? s_layer;
    private static int s_lastItems = 100;
    private static readonly Color Gold = new(0.98f, 0.8f, 0.3f);
    private static readonly Color Dim = new(0.75f, 0.72f, 0.65f);

    internal static bool IsOpen => s_layer != null && GodotObject.IsInstanceValid(s_layer);

    internal static void Show(ActionTarget target, Action<StopKind, int> choose, Action? cancelled = null)
    {
        Close();
        var game = TimeBank.TimeBank.Game;
        if (game == null || Engine.GetMainLoop() is not SceneTree tree) return;
        game.Entities.TryGetObjectById(target.EntityId, out GameEntity? entity);
        var xp = entity?.Components.Values.OfType<ExperienceComponent>().FirstOrDefault();

        var layer = new CanvasLayer { Layer = 100, Name = "Melvor2TweaksQueuePopup" };
        s_layer = layer;
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.45f), MouseFilter = Control.MouseFilterEnum.Stop };
        dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(dim);
        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(center);

        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop, CustomMinimumSize = new Vector2(460, 0) };
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.11f, 0.10f, 0.09f, 0.98f), BorderColor = new Color(0.55f, 0.45f, 0.25f),
            BorderWidthLeft = 2, BorderWidthTop = 2, BorderWidthRight = 2, BorderWidthBottom = 2,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            ShadowColor = new Color(0, 0, 0, 0.6f), ShadowSize = 12,
            ContentMarginLeft = 18, ContentMarginRight = 18, ContentMarginTop = 14, ContentMarginBottom = 14,
        });
        center.AddChild(panel);
        var body = new VBoxContainer();
        body.AddThemeConstantOverride("separation", 10);
        panel.AddChild(body);

        body.AddChild(TopBar.RawLabel("Queue " + target.Label, 19, Gold));
        body.AddChild(TopBar.RawLabel("When should it stop?", 15, Dim));

        void Pick(StopKind kind, int value)
        {
            Close();
            choose(kind, value);
        }

        Button Option(string text)
        {
            var b = TopBar.GameButton(text);
            b.CustomMinimumSize = new Vector2(190, 38);
            return b;
        }

        HBoxContainer Row()
        {
            var r = new HBoxContainer();
            r.AddThemeConstantOverride("separation", 8);
            body.AddChild(r);
            return r;
        }

        // Until it stops
        var stops = Option("Until it stops");
        stops.Pressed += () => Pick(StopKind.None, 0);
        var stopsRow = Row();
        stopsRow.AddChild(stops);
        string stopsNote = "until you stop it";
        if (Gather.CraftsAffordable(game, target) is int crafts)
            stopsNote = crafts == 0 ? "until the materials run out (you have none yet)"
                      : $"until the materials run out ({crafts:N0} crafts' worth now)";
        stopsRow.AddChild(Note(stopsNote));

        // Until level: one button per upcoming milestone (with what it unlocks), then any level you type.
        if (xp != null)
        {
            int cap = xp.LevelCap > 0 ? xp.LevelCap : 99;
            if (entity != null)
                foreach (var (lvl, reason) in UiKit.UpcomingMilestones(entity, 4))
                {
                    var mb = Option($"Until level {lvl}");
                    int pick = lvl;
                    mb.Pressed += () => Pick(StopKind.Level, pick);
                    var mr = Row();
                    mr.AddChild(mb);
                    var why = Note(reason);
                    why.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                    why.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                    mr.AddChild(why);
                }
            if (xp.Level < cap)
            {
                var level = new SpinBox
                {
                    MinValue = xp.Level + 1, MaxValue = cap,
                    Value = entity != null ? UiKit.NextMilestoneLevel(entity) : xp.Level + 1, CustomMinimumSize = new Vector2(110, 0),
                };
                var b = Option("Until level...");
                b.Pressed += () => Pick(StopKind.Level, (int)level.Value);
                var r = Row();
                r.AddChild(b);
                r.AddChild(level);
                r.AddChild(Note($"now {xp.Level}"));
            }
        }

        // For N items (not Astrology: studying makes no products to count)
        bool astrology = AstrologyGoal.IsAstrology(game, target);
        if (!astrology)
        {
            var items = new SpinBox { MinValue = 1, MaxValue = 10_000_000, Value = s_lastItems, CustomMinimumSize = new Vector2(110, 0) };
            var b = Option("For N items");
            b.Pressed += () => { s_lastItems = (int)items.Value; Pick(StopKind.Items, (int)items.Value); };
            var r = Row();
            r.AddChild(b);
            r.AddChild(items);
            r.AddChild(Note("items"));
        }

        // Until all nodes (Astrology)
        if (astrology)
        {
            var b = Option("Until all nodes");
            b.Pressed += () => Pick(StopKind.Nodes, 0);
            var r = Row();
            r.AddChild(b);
            string progress = AstrologyGoal.Progress(game, target);
            var eta = QueueEta.ForEntry(game, new QueueEntry { Target = target, Stop = StopKind.Nodes }, new());
            string etaText = QueueEta.Describe(eta);
            var note = Note((AstrologyGoal.Ready(game, target) ? "every node can already be unlocked" : progress) +
                            (etaText.Length > 0 && !AstrologyGoal.Ready(game, target) ? $" ({etaText})" : ""));
            note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            note.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            r.AddChild(note);
            UiKit.AddTooltip(b, "Until all nodes",
                "Studies until there's enough knowledge for every minor node left in this constellation (the major nodes come with them), " +
                "and Astrology is the level those nodes need.");
        }

        var cancelRow = new HBoxContainer();
        cancelRow.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var cancel = TopBar.GameButton("Cancel");
        cancel.Pressed += () => { Close(); cancelled?.Invoke(); };
        cancelRow.AddChild(cancel);
        body.AddChild(cancelRow);

        dim.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true }) { Close(); cancelled?.Invoke(); }
        };
        tree.Root.AddChild(layer);
    }

    private static Label Note(string text)
    {
        var l = TopBar.RawLabel(text, 14, Dim);
        l.VerticalAlignment = VerticalAlignment.Center;
        return l;
    }

    internal static void Close()
    {
        if (s_layer != null && GodotObject.IsInstanceValid(s_layer))
        {
            s_layer.GetParent()?.RemoveChild(s_layer);
            s_layer.QueueFree();
        }
        s_layer = null;
    }

    /// <summary>Dev aid: presses the popup's button whose text starts with <paramref name="text"/>.</summary>
    internal static bool DevPress(string text)
    {
        if (!IsOpen) return false;
        foreach (var n in s_layer!.FindChildren("*", "Button", true, false))
            if (n is Button b && b.Text.StartsWith(text, StringComparison.OrdinalIgnoreCase)) { b.EmitSignal(BaseButton.SignalName.Pressed); return true; }
        return false;
    }
}
