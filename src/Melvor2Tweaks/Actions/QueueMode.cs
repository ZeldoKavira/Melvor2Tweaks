using System;
using GamesByMalcs.Melvor2.Components.Actions;
using GamesByMalcs.Melvor2.Data;
using Godot;
using HarmonyLib;

namespace Melvor2Tweaks.Actions;

/// <summary>
/// Queue mode: while on, starting any action through the game's own buttons adds it to the queue
/// instead of switching to it. Works for every skill without per-skill code: the action starts for
/// an instant, we read what it is, then put back what was running before (or stop it if nothing was).
/// Neither of those stops counts as "stopped by itself", so the queue doesn't advance.
///   - a different skill: caught in ActionComponent.TryStart
///   - another target in the running skill (switches without a start): caught in the
///     CurrentDescription setter, which every live switch goes through
/// Capture + restore happen once, deferred to the end of the frame, after the game's click handler.
/// </summary>
internal static class QueueMode
{
    internal static bool Enabled { get; set; }

    /// <summary>Set while the mod itself starts actions (queue advance, restore) so they aren't intercepted.</summary>
    internal static bool Internal { get; set; }

    /// <summary>Supplied by the Actions menu: the limit chosen there.</summary>
    internal static Func<(StopKind kind, int value)> PendingRule = () => (StopKind.None, 0);

    /// <summary>Raised on the main thread with a status line for the menu.</summary>
    internal static event Action<string>? Captured;

    private static int s_inTryStart;
    private static ActionTarget? s_restore;
    private static bool s_hadRestore;
    private static ActionComponent? s_started;
    private static bool s_scheduled;

    private static bool Active => Enabled && !Internal && TimeBank.TimeBank.IsProcessing;

    [HarmonyPatch(typeof(ActionComponent), nameof(ActionComponent.TryStart))]
    private static class TryStartPatch
    {
        private static void Prefix(ActionComponent __instance)
        {
            s_inTryStart++;
            if (!Active) return;
            Remember(ActionQueue.RunningAction);
        }

        private static void Postfix(ActionComponent __instance, bool __result)
        {
            if (Active && __result) Schedule(__instance);
        }

        private static void Finalizer() => s_inTryStart--;
    }

    [HarmonyPatch(typeof(ActionComponent), nameof(ActionComponent.CurrentDescription), MethodType.Setter)]
    private static class SwitchPatch
    {
        private static void Prefix(ActionComponent __instance, IDescribedObject? value)
        {
            // Only a live switch between two targets of the running skill (TryStart is handled above).
            if (!Active || s_inTryStart > 0 || !__instance.IsStarted) return;
            if (value == null || __instance.CurrentDescription == null || ReferenceEquals(value, __instance.CurrentDescription)) return;
            Remember(__instance);
            Schedule(__instance);
        }
    }

    private static void Remember(ActionComponent? running)
    {
        if (s_scheduled) return; // keep the first (original) action of this click
        s_hadRestore = running != null && running.IsStarted;
        s_restore = s_hadRestore ? SkillActions.Capture(running!) : null;
    }

    private static void Schedule(ActionComponent started)
    {
        s_started = started;
        if (s_scheduled) return;
        s_scheduled = true;
        Callable.From(Process).CallDeferred();
    }

    private static void Process()
    {
        s_scheduled = false;
        var started = s_started;
        var restore = s_restore;
        bool hadRestore = s_hadRestore;
        s_started = null; s_restore = null; s_hadRestore = false;
        var game = TimeBank.TimeBank.Game;
        if (started == null || game == null) return;

        var target = started.IsStarted ? SkillActions.Capture(started) : null;
        string status;
        Internal = true;
        try
        {
            if (target != null && target.SameAs(restore))
            {
                status = ""; // clicked what was already running: nothing to queue
            }
            else if (target == null)
            {
                status = "Couldn't read that action - not queued.";
            }
            else
            {
                var (kind, value) = PendingRule();
                if (kind == StopKind.Level && game.Entities.TryGetObjectById(target.EntityId, out var entity) && entity != null)
                    value = UI.UiKit.NextMilestoneLevel(entity); // the menu's number was for the running skill
                var entry = new QueueEntry { Target = target, Stop = kind, StopValue = value };
                ActionQueue.Add(entry);
                status = "Queued: " + entry.Describe();
                Plugin.Log("Queue mode: " + status);
            }

            // Put back what was running before the click (or stop the instant start).
            if (hadRestore && restore != null && !restore.SameAs(SkillActions.Capture(started)))
            {
                if (!SkillActions.Start(game, restore, out string why))
                {
                    started.TryStop();
                    status += $" (couldn't resume {restore.Label}: {why})";
                }
            }
            else if (!hadRestore && started.IsStarted)
            {
                started.TryStop();
            }
        }
        finally { Internal = false; }

        if (status.Length > 0) Captured?.Invoke(status);
    }

    internal static void Shutdown()
    {
        Enabled = false;
        s_scheduled = false; s_started = null; s_restore = null;
        Captured = null;
    }
}
