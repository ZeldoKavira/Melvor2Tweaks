using System.Linq;
using GamesByMalcs.Melvor2.Components.Toolbelt;
using GamesByMalcs.Melvor2.Components.ToolbeltEquipping;
using GamesByMalcs.Melvor2.CustomControls;
using GamesByMalcs.Melvor2.GameMenus.Pages;
using GamesByMalcs.Melvor2.GameMenus.Widgets;
using Godot;
using HarmonyLib;
using Melvor2Tweaks.Features;

namespace Melvor2Tweaks.UI;

/// <summary>
/// Warning dots on the toolbelt (per-skill equipment) button at the right edge of each skill page, and on the
/// affected slots inside it: red = a slot is empty although the bank has something for it, yellow = the bank
/// has something better for what the skill is doing. The opened panel also lists what's wrong.
/// Re-checked once a second (the bank, the action and the toolbelt all change it; one cheap pass is simpler
/// than following every event).
/// </summary>
internal static class ToolbeltBadges
{
    private const string TimerName = "M2TToolbeltTimer";
    private const string BadgeName = "M2TToolbeltBadge";
    private const string NotesName = "M2TToolbeltNotes";

    /// <summary>Dev aid: forces the button's dot (None = normal behaviour).</summary>
    internal static ToolbeltCheck.Severity Demo;

    private static readonly Color Red = new(0.86f, 0.16f, 0.14f);
    private static readonly Color Yellow = new(0.98f, 0.76f, 0.12f);

    [HarmonyPatch(typeof(WidgetsView), nameof(WidgetsView._Ready))]
    private static class ReadyPatch
    {
        private static void Postfix(WidgetsView __instance) => Attach(__instance);
    }

    /// <summary>Page switch: re-check straight away instead of up to a second later.</summary>
    [HarmonyPatch(typeof(EntityActiveGamePage), "SetWidgets")]
    private static class SetWidgetsPatch
    {
        private static void Postfix(WidgetsView widgets) => Refresh(widgets);
    }

    internal static void AttachToExisting()
    {
        foreach (var w in NodeSearch.All<WidgetsView>()) Attach(w);
    }

    internal static void DetachAll()
    {
        foreach (var w in NodeSearch.All<WidgetsView>())
        {
            Free(w.GetNodeOrNull(TimerName));
            Free(Header(w)?.GetNodeOrNull(BadgeName));
            Free(NotesParent(w)?.GetNodeOrNull(NotesName));
            var grid = GridContainer(w);
            if (grid != null)
                foreach (Node slot in grid.GetChildren()) Free(slot.GetNodeOrNull(BadgeName));
        }
    }

    private static void Free(Node? n)
    {
        if (n == null) return;
        n.GetParent()?.RemoveChild(n);
        n.QueueFree();
    }

    private static void Attach(WidgetsView widgets)
    {
        if (widgets.GetNodeOrNull(TimerName) != null) return;
        var timer = new Timer { Name = TimerName, WaitTime = 1.0, Autostart = true };
        timer.Timeout += () => Refresh(widgets);
        widgets.AddChild(timer);
        Refresh(widgets);
    }

    private static Control? Header(WidgetsView w) =>
        Traverse.Create(w.ToolbeltWidgetContainer).Property("Header").GetValue<Control>();

    private static GridContainer? GridContainer(WidgetsView w) =>
        Traverse.Create(w.ToolbeltWidget.Toolbelt).Property("ToolbeltGridContainer").GetValue<GridContainer>();

    /// <summary>The box the grid sits in, if it stacks vertically (the notes go under the grid).</summary>
    private static Node? NotesParent(WidgetsView w) => w.ToolbeltWidget.Toolbelt.GetParent() as VBoxContainer;

    internal static void Refresh(WidgetsView widgets)
    {
        if (!GodotObject.IsInstanceValid(widgets)) return;
        var equipping = Traverse.Create(widgets.ToolbeltWidget).Property("ToolbeltEquipping").GetValue<ToolbeltEquippingComponent>();
        ToolbeltCheck.Result? result = null;
        if (Settings.Current.ToolbeltWarnings && equipping != null && widgets.ToolbeltWidgetContainer.Visible)
        {
            try { result = ToolbeltCheck.Check(equipping); }
            catch (System.Exception e) { Plugin.Log("Toolbelt check: " + e.Message); }
            if (Demo != ToolbeltCheck.Severity.None && result != null && result.Issues.Count == 0)
            {
                // Dev aid: fake issues on the first two slots so the slot dots and notes can be looked at.
                var slots = equipping.Toolbelt.Config.ToolbeltSlots;
                var demoItem = equipping.Inventory.Stacks.FirstOrDefault(st => st.Quantity > 0)?.Item;
                if (demoItem != null)
                    for (int i = 0; i < slots.Count && i < 2; i++)
                        result.Issues.Add(new ToolbeltCheck.SlotIssue(slots[i], i == 0 ? ToolbeltCheck.Severity.Empty : ToolbeltCheck.Severity.Upgrade, demoItem, demoItem));
            }
        }

        var header = Header(widgets);
        if (header != null) SetBadge(header, Demo != ToolbeltCheck.Severity.None ? Demo : result?.Worst ?? ToolbeltCheck.Severity.None, new Vector2(15, 15));

        var grid = GridContainer(widgets);
        if (grid != null)
            foreach (Node child in grid.GetChildren())
            {
                if (child is not ToolbeltSlotView view) continue;
                var slot = Traverse.Create(view).Property("Slot").GetValue<IActiveToolbeltSlot>();
                var issue = slot == null ? null : result?.For(slot.Slot);
                SetBadge(view, issue?.Severity ?? ToolbeltCheck.Severity.None, new Vector2(13, 13));
            }

        SetNotes(widgets, result);
    }

    /// <summary>
    /// A coloured "!" dot in the top-right corner of <paramref name="target"/>. The dot lives in a full-size,
    /// click-through holder so a container parent (the button, the slot) stretches the holder, not the dot.
    /// </summary>
    private static void SetBadge(Control target, ToolbeltCheck.Severity severity, Vector2 size)
    {
        var holder = target.GetNodeOrNull<Control>(BadgeName);
        if (severity == ToolbeltCheck.Severity.None)
        {
            if (holder != null) holder.Visible = false;
            return;
        }
        if (holder == null)
        {
            holder = new Control { Name = BadgeName, MouseFilter = Control.MouseFilterEnum.Ignore };
            holder.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            var dot = new PanelContainer { Name = "Dot", MouseFilter = Control.MouseFilterEnum.Ignore };
            dot.AnchorLeft = dot.AnchorRight = 1;
            dot.OffsetLeft = -size.X - 1; dot.OffsetRight = -1;
            dot.OffsetTop = 1; dot.OffsetBottom = size.Y + 1;
            var mark = new Label
            {
                Text = "!",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled,
            };
            mark.AddThemeFontSizeOverride("font_size", (int)size.Y - 3);
            mark.AddThemeConstantOverride("line_spacing", 0);
            dot.AddChild(mark);
            holder.AddChild(dot);
            target.AddChild(holder);
        }
        holder.Visible = true;
        var color = severity == ToolbeltCheck.Severity.Empty ? Red : Yellow;
        var panel = holder.GetNode<PanelContainer>("Dot");
        var style = new StyleBoxFlat
        {
            BgColor = color,
            BorderColor = new Color(0.08f, 0.06f, 0.05f),
            ContentMarginLeft = 0, ContentMarginRight = 0, ContentMarginTop = 0, ContentMarginBottom = 0,
        };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll((int)size.X);
        panel.AddThemeStyleboxOverride("panel", style);
        var label = panel.GetChild<Label>(0);
        label.AddThemeColorOverride("font_color", severity == ToolbeltCheck.Severity.Empty ? Colors.White : new Color(0.1f, 0.08f, 0.05f));
    }

    /// <summary>What's wrong, in words, under the grid of the opened panel.</summary>
    private static void SetNotes(WidgetsView widgets, ToolbeltCheck.Result? result)
    {
        var parent = NotesParent(widgets);
        if (parent == null) return;
        var notes = parent.GetNodeOrNull<RichTextLabel>(NotesName);
        if (result == null || result.Issues.Count == 0)
        {
            if (notes != null) notes.Visible = false;
            return;
        }
        if (notes == null)
        {
            notes = new RichTextLabel
            {
                Name = NotesName,
                BbcodeEnabled = true,
                FitContent = true,
                ScrollActive = false,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(0, 0),
                MouseFilter = Control.MouseFilterEnum.Ignore,
                AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled,
            };
            notes.AddThemeFontSizeOverride("normal_font_size", 13);
            parent.AddChild(notes);
        }
        notes.Visible = true;
        string text = string.Join("\n", result.Issues
            .OrderByDescending(i => i.Severity)
            .Select(i => $"[color=#{(i.Severity == ToolbeltCheck.Severity.Empty ? Red : Yellow).ToHtml(false)}]●[/color] {ToolbeltCheck.Describe(i)}"));
        if (notes.Text != text) notes.Text = text;
    }
}
