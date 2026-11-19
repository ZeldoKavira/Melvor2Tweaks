using System;
using System.Collections.Generic;
using GamesByMalcs.Melvor2;

namespace Melvor2Tweaks.Actions;

/// <summary>
/// Time-left estimates for the running limit and each queued entry, from the action log's all-time rates
/// (items per game second, XP per game second). Game time is converted to real time at the current speed.
/// </summary>
internal static class QueueEta
{
    /// <summary>GameMs null = unknown (never run, so no rate yet); Endless = runs until stopped by hand.</summary>
    internal readonly record struct Eta(double? GameMs, bool Endless)
    {
        internal static readonly Eta Unknown = new(null, false);
        internal static readonly Eta Forever = new(null, true);
    }

    internal static Eta ForRunning(Game game, Dictionary<string, double> simXp)
    {
        var running = ActionQueue.RunningAction;
        if (running == null || !running.IsStarted || SkillActions.Capture(running) is not { } target) return new Eta(0, false);
        var rule = ActionQueue.State?.Current;
        if (rule == null || !rule.Target.SameAs(target)) return Estimate(game, target, StopKind.None, 0, null, simXp);
        int value = rule.Stop == StopKind.Items ? rule.StopValue - ActionQueue.ItemProgress : rule.StopValue;
        return Estimate(game, target, rule.Stop, value, rule.CountItemId, simXp);
    }

    internal static Eta ForEntry(Game game, QueueEntry e, Dictionary<string, double> simXp) =>
        Estimate(game, e.Target, e.Stop, e.StopValue, e.CountItemId, simXp);

    private static Eta Estimate(Game game, ActionTarget target, StopKind stop, int value, string? itemId, Dictionary<string, double> simXp)
    {
        var log = ActionLog.Get(target);
        switch (stop)
        {
            case StopKind.Items:
                return ActionLog.MsPerItem(log, itemId) is double perItem ? new Eta(Math.Max(0, value) * perItem, false) : Eta.Unknown;

            case StopKind.Level:
            {
                if (ActionQueue.XpOf(game, target) is not { } xp) return Eta.Unknown;
                double from = simXp.TryGetValue(target.EntityId, out double sim) ? sim : xp.Xp;
                double to = xp.Table.GetXpForLevel(value);
                simXp[target.EntityId] = Math.Max(from, to);
                if (to <= from) return new Eta(0, false);
                return ActionLog.MsPerXp(log) is double perXp ? new Eta((to - from) * perXp, false) : Eta.Unknown;
            }

            default:
                // Crafting runs until the materials run out; everything else until stopped.
                if (Gather.CraftsAffordable(game, target) is not int crafts) return Eta.Forever;
                if (crafts == 0) return new Eta(0, false);
                return ActionLog.MsPerAction(log) is double perAction ? new Eta(crafts * perAction, false) : Eta.Unknown;
        }
    }

    /// <summary>"3h 10m" in real time at the current speed.</summary>
    internal static string Format(double gameMs)
    {
        double speed = Math.Max(0.01, Settings.Current.Speed);
        var t = TimeSpan.FromMilliseconds(gameMs / speed);
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m {t.Seconds}s";
        return $"{Math.Max(0, t.Seconds)}s";
    }

    /// <summary>Game time, not adjusted for speed (drop log totals).</summary>
    internal static string FormatGame(double gameMs)
    {
        var t = TimeSpan.FromMilliseconds(gameMs);
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        return $"{t.Minutes}m {t.Seconds}s";
    }

    internal static string Describe(Eta eta) =>
        eta.Endless ? "no end" : eta.GameMs is double ms ? "~" + Format(ms) : ""; // unknown: show nothing

    /// <summary>Running limit + every queued entry; text such as "~9h 30m", "at least ~2h", "no end".</summary>
    internal static (string current, List<string> entries, string total) All(Game game)
    {
        var simXp = new Dictionary<string, double>();
        var list = new List<string>();
        var cur = ForRunning(game, simXp);
        double sum = cur.GameMs ?? 0;
        bool unknown = cur.GameMs == null && !cur.Endless, endless = cur.Endless;
        foreach (var e in ActionQueue.State?.Entries ?? new List<QueueEntry>())
        {
            var eta = ForEntry(game, e, simXp);
            list.Add(Describe(eta));
            if (eta.Endless) endless = true;
            else if (eta.GameMs is double ms) sum += ms;
            else unknown = true;
        }
        // Only when every part is known (an "until it stops" gatherer means there's no end).
        string total = endless ? "no end (an entry runs until stopped)" : unknown ? "" : "~" + Format(sum);
        return (Describe(cur), list, total);
    }
}
