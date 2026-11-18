using System.Collections;
using System.Linq;
using GamesByMalcs.Melvor2.Components.Experience;
using GamesByMalcs.Melvor2.Components.Milestones;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Tooltips;
using Godot;
using HarmonyLib;

namespace Melvor2Tweaks.UI;

internal static class UiKit
{
    /// <summary>
    /// Hover tooltip in the game's own style. Plain Control.TooltipText renders without a background
    /// in this game's theme, so attach the game's TextTooltip scene the way its own views do.
    /// </summary>
    internal static void AddTooltip(Control target, string? title, string content)
    {
        var tip = TextTooltip.Scene.Instantiate<TextTooltip>();
        if (title != null) tip.SetTitle(title);
        tip.SetContent(content);
        tip.Labelling = target;
        tip.Placement = PopupPlacement.Bottom;
        target.AddChild(tip);
    }

    /// <summary>Next level above the current one that unlocks a milestone for this skill (else level + 1).</summary>
    internal static int NextMilestoneLevel(GameEntity entity)
    {
        var xp = entity.Components.Values.OfType<ExperienceComponent>().FirstOrDefault();
        if (xp == null) return 1;
        int current = xp.Level;
        int cap = xp.LevelCap > 0 ? xp.LevelCap : 99;
        var milestones = entity.Components.Values.OfType<MilestonesComponent>().FirstOrDefault();
        if (milestones != null &&
            Traverse.Create(milestones).Field("_cachedSelfLevelMilestones").GetValue() is IDictionary byLevel)
        {
            int? next = byLevel.Keys.OfType<int>().Where(l => l > current && l <= cap).OrderBy(l => l).Cast<int?>().FirstOrDefault();
            if (next.HasValue) return next.Value;
        }
        return System.Math.Min(current + 1, cap);
    }
}
