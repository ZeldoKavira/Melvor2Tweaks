using System.Collections.Generic;
using GamesByMalcs.Melvor2.Entities.Farming;
using Godot;
using HarmonyLib;

namespace Melvor2Tweaks.UI;

/// <summary>
/// "Harvest All" / "Plant All" for the selected farming category, in a row of their own under the grain
/// mill button in the Farm Buildings box. (The mill button sits in a MarginContainer, which stacks its
/// children, so the row must not go in there or it covers the button.)
/// </summary>
internal static class FarmingButtons
{
    private const string RowName = "Melvor2TweaksFarmingButtons";

    [HarmonyPatch(typeof(FarmingActionView), nameof(FarmingActionView._Ready))]
    private static class ReadyPatch
    {
        private static void Postfix(FarmingActionView __instance) => Attach(__instance);
    }

    internal static void AttachToExisting()
    {
        foreach (var view in NodeSearch.All<FarmingActionView>()) Attach(view);
    }

    internal static void DetachAll()
    {
        foreach (var view in NodeSearch.All<FarmingActionView>())
        {
            var row = Anchor(view)?.GetParent()?.GetParent()?.GetNodeOrNull(RowName);
            if (row == null) continue;
            row.GetParent().RemoveChild(row);
            row.QueueFree();
        }
    }

    private static Control? Anchor(FarmingActionView view) =>
        Traverse.Create(view).Property("GrainMillButton").GetValue<Control>();

    private static void Attach(FarmingActionView view)
    {
        var millBox = Anchor(view)?.GetParent() as Control; // MarginContainer around the mill button
        var list = millBox?.GetParent();                       // the Farm Buildings VBox
        if (millBox == null || list == null || list.GetNodeOrNull(RowName) != null) return;

        // Same margins as the game's rows in this box.
        var box = new MarginContainer { Name = RowName };
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            box.AddThemeConstantOverride(side, millBox.GetThemeConstant(side));
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        row.AddChild(MakeButton("Harvest All", "Harvest every grown plot in this category.", () => HarvestAll(view)));
        row.AddChild(MakeButton("Plant All", "Plant the selected seed in every empty plot of this category (if you can afford it).", () => PlantAll(view)));
        box.AddChild(row);
        list.AddChild(box);
        list.MoveChild(box, millBox.GetIndex() + 1);
    }

    private static Button MakeButton(string text, string tooltip, System.Action onPressed)
    {
        var b = new Button
        {
            Text = text,
            ThemeTypeVariation = "TexturedButton4x",
            CustomMinimumSize = new Vector2(0, 28),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled,
            FocusMode = Control.FocusModeEnum.None,
        };
        UiKit.AddTooltip(b, null, tooltip);
        b.Pressed += onPressed;
        return b;
    }

    private static IEnumerable<ActiveFarmingPlot> CategoryPlots(FarmingActionView view)
    {
        var passive = Traverse.Create(view).Property("Passive").GetValue<FarmingPassiveComponent>();
        return passive?.SelectedCategory?.Plots ?? (IEnumerable<ActiveFarmingPlot>)System.Array.Empty<ActiveFarmingPlot>();
    }

    private static void HarvestAll(FarmingActionView view)
    {
        int n = 0;
        foreach (var plot in CategoryPlots(view))
            if (plot.CanHarvest) { plot.Harvest(); n++; }
        Plugin.Log($"Harvest All: {n} plot(s)");
    }

    private static void PlantAll(FarmingActionView view)
    {
        int n = 0;
        foreach (var plot in CategoryPlots(view))
            if (plot.CanPlant && plot.SelectedRecipe != null && plot.CanPlantSelectedRecipe()) { plot.Plant(); n++; }
        Plugin.Log($"Plant All: {n} plot(s)");
    }
}

internal static class NodeSearch
{
    internal static IEnumerable<T> All<T>() where T : Node
    {
        if (Engine.GetMainLoop() is not SceneTree tree) yield break;
        var stack = new Stack<Node>();
        stack.Push(tree.Root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n is T t) yield return t;
            foreach (Node c in n.GetChildren()) stack.Push(c);
        }
    }
}
