using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Experience;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Astrology;

namespace Melvor2Tweaks.Actions;

/// <summary>
/// "Until all nodes" for Astrology: study a constellation until every node it has left could be unlocked -
/// enough knowledge for all its minor nodes (the major nodes follow from those, one per
/// MinorNodesPerMajorNode minors), and the Astrology level each remaining node asks for. Requirements on
/// anything other than the Astrology level (studying can't raise those) don't hold it up.
/// </summary>
internal static class AstrologyGoal
{
    internal static AstrologyActionComponent? ActionOf(Game game, ActionTarget t) =>
        game.Entities.TryGetObjectById(t.EntityId, out GameEntity? e) && e != null &&
        e.Components.TryGetValue("Action", out var c) ? c as AstrologyActionComponent : null;

    internal static bool IsAstrology(Game game, ActionTarget t) => ActionOf(game, t) != null;

    internal static ActiveAstrologyConstellation? Constellation(Game game, ActionTarget t) =>
        ActionOf(game, t) is { } astro && t.Ids.FirstOrDefault() is { } id
            ? astro.ActiveConstellations.FirstOrDefault(kv => kv.Key.Id == id).Value : null;

    internal static ExperienceComponent? Xp(AstrologyActionComponent astro) =>
        astro.Entity.Components.Values.OfType<ExperienceComponent>().FirstOrDefault();

    /// <summary>Knowledge still to earn before every minor node is affordable (0 when there).</summary>
    internal static double KnowledgeLeft(ActiveAstrologyConstellation c)
    {
        var thresholds = c.Constellation.MinorNodeUnlockKnowledge;
        int needed = thresholds.Count >= c.Constellation.MinorNodes.Count && c.Constellation.MinorNodes.Count > 0
            ? thresholds[c.Constellation.MinorNodes.Count - 1] : thresholds.LastOrDefault();
        return Math.Max(0, needed - c.Knowledge);
    }

    /// <summary>Highest Astrology level a still-locked node asks for (0 if none).</summary>
    internal static int LevelNeeded(ActiveAstrologyConstellation c, ExperienceComponent? astroXp)
    {
        int level = 0;
        foreach (var node in c.Constellation.MinorNodes.Concat(c.Constellation.MajorNodes))
        {
            if (c.IsMinorNodeUnlocked(node) || c.IsMajorNodeUnlocked(node)) continue;
            foreach (var cond in node.UnlockRequirements.Conditions)
                if (cond is LevelCondition lc && ReferenceEquals(lc.Component, astroXp)) level = Math.Max(level, lc.Level);
        }
        return level;
    }

    internal static bool Ready(ActiveAstrologyConstellation c, ExperienceComponent? astroXp) =>
        KnowledgeLeft(c) <= 0 && (astroXp?.Level ?? 0) >= LevelNeeded(c, astroXp);

    internal static bool Ready(Game game, ActionTarget t) =>
        Constellation(game, t) is { } c && ActionOf(game, t) is { } astro && Ready(c, Xp(astro));

    /// <summary>"Knowledge 984 / 65,896, level 22 / 40" for the menu.</summary>
    internal static string Progress(Game game, ActionTarget t)
    {
        if (Constellation(game, t) is not { } c || ActionOf(game, t) is not { } astro) return "";
        var xp = Xp(astro);
        double total = c.Knowledge + KnowledgeLeft(c);
        int need = LevelNeeded(c, xp);
        string others = OtherSkillNeeds(c, xp);
        return $"knowledge {c.Knowledge:N0} / {total:N0}" + (need > (xp?.Level ?? 0) ? $", level {xp?.Level ?? 0} / {need}" : "") +
               (others.Length > 0 ? $"; nodes also need {others}" : "");
    }

    /// <summary>Other skills' levels still-locked nodes ask for and you don't have yet, e.g. "Mining 80, Strength 80".</summary>
    internal static string OtherSkillNeeds(ActiveAstrologyConstellation c, ExperienceComponent? astroXp)
    {
        var need = new Dictionary<string, int>();
        foreach (var node in c.Constellation.MinorNodes.Concat(c.Constellation.MajorNodes))
        {
            if (c.IsMinorNodeUnlocked(node) || c.IsMajorNodeUnlocked(node)) continue;
            foreach (var cond in node.UnlockRequirements.Conditions)
                if (cond is LevelCondition lc && !lc.IsMet && !ReferenceEquals(lc.Component, astroXp) && lc.Component?.Entity is { } e)
                    need[e.DisplayName.Text] = Math.Max(need.GetValueOrDefault(e.DisplayName.Text), lc.Level);
        }
        return string.Join(", ", need.Select(kv => $"{kv.Key} {kv.Value}"));
    }
}
