using System.Collections;
using System.Collections.Generic;
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

    /// <summary>
    /// The game's "?" hint icon with a hover tooltip, built like its own (e.g. combat stats' HitChanceHintIcon):
    /// 16x16 TextureRect of TooltipHint.png, mouse pass-through, with a TextTooltip child.
    /// </summary>
    internal static TextureRect HintIcon(string? title, string content)
    {
        var icon = new TextureRect
        {
            Texture = GD.Load<Texture2D>("res://Assets/Icons/TooltipHint.png"),
            CustomMinimumSize = new Vector2(16, 16),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = Control.MouseFilterEnum.Pass,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        AddTooltip(icon, title, content);
        return icon;
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

    /// <summary>
    /// The next few levels above the current one that unlock something in this skill, each with what it
    /// unlocks, e.g. (30, "can mine Mithril Rock").
    /// </summary>
    internal static List<(int level, string reason)> UpcomingMilestones(GameEntity entity, int count)
    {
        var result = new List<(int, string)>();
        var xp = entity.Components.Values.OfType<ExperienceComponent>().FirstOrDefault();
        var milestones = entity.Components.Values.OfType<MilestonesComponent>().FirstOrDefault();
        if (xp == null || milestones == null ||
            Traverse.Create(milestones).Field("_cachedSelfLevelMilestones").GetValue() is not IDictionary byLevel) return result;
        int cap = xp.LevelCap > 0 ? xp.LevelCap : 99;
        string verb = UnlockVerb(entity.Id);
        foreach (int level in byLevel.Keys.OfType<int>().Where(l => l > xp.Level && l <= cap).OrderBy(l => l).Take(count))
        {
            var names = (byLevel[level] as System.Collections.IEnumerable)?.OfType<GamesByMalcs.Melvor2.Components.Milestones.IMilestone>()
                .Select(m => m.DisplayName.Text).Where(n => n.Length > 0).Distinct().ToList() ?? new List<string>();
            string what = names.Count switch
            {
                0 => "something new",
                <= 3 => string.Join(", ", names),
                _ => string.Join(", ", names.Take(2)) + $" and {names.Count - 2} more",
            };
            result.Add((level, $"{verb} {what}"));
        }
        return result;
    }

    private static string UnlockVerb(string skillId) => skillId.Split(':').Last() switch
    {
        "Woodcutting" => "can cut",
        "Mining" => "can mine",
        "Fishing" => "can fish",
        "Hunting" => "can catch",
        "Thieving" => "can steal from",
        "Cooking" => "can cook",
        "Firemaking" => "can burn",
        "Farming" => "can grow",
        "Ranching" => "can raise",
        "Astrology" => "can study",
        "Agility" => "can build",
        "Smithing" or "Fletching" or "Crafting" or "Runecrafting" or "Herblore" or "Beastcrafting" => "can make",
        _ => "unlocks",
    };
}
