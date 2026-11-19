using System;
using System.Collections.Generic;
using System.Reflection;
using GamesByMalcs.Melvor2.Components.CraftingAction;
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
    // Kept on the SceneTree so a hot reload of the mod doesn't silently switch Queue mode off.
    private const string EnabledMeta = "melvor2tweaks_queue_mode";
    private static bool s_enabled = Godot.Engine.GetMainLoop() is { } loop && loop.HasMeta(EnabledMeta) && loop.GetMeta(EnabledMeta).AsBool();

    internal static bool Enabled
    {
        get => s_enabled;
        set
        {
            bool changed = s_enabled != value;
            s_enabled = value;
            Godot.Engine.GetMainLoop()?.SetMeta(EnabledMeta, value);
            if (changed) RefreshCraftButtons();
        }
    }

    private static readonly (string type, string handler, string update, string button)[] CraftViews =
    {
        ("GamesByMalcs.Melvor2.Components.ArtisanAction.ArtisanSelectedRecipeContainer", "CreateRecipeButtonPressedHandler", "UpdateCreateRecipeButton", "CreateRecipeButton"),
        ("GamesByMalcs.Melvor2.Entities.Cooking.CookingSelectedRecipeContainer", "CreateRecipeButtonPressedHandler", "UpdateCreateRecipeButton", "CreateRecipeButton"),
        ("GamesByMalcs.Melvor2.Entities.Herblore.HerbloreSelectedRecipeContainer", "CreateRecipeButtonPressedHandler", "UpdateCreateRecipeButton", "CreateRecipeButton"),
        ("GamesByMalcs.Melvor2.Entities.Firemaking.FiremakingActionView", "StartButtonPressedHandler", "UpdateStartButton", "StartButton"),
    };

    /// <summary>
    /// The game greys out a crafting Start button when you can't afford the recipe. In Queue mode it's
    /// re-enabled (queuing needs no materials) - except for the recipe already being made.
    /// </summary>
    [HarmonyPatch]
    private static class CraftButtonStatePatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var v in CraftViews)
                if (typeof(ActionComponent).Assembly.GetType(v.type) is { } t && AccessTools.Method(t, v.update) is { } m) yield return m;
        }

        private static void Postfix(object __instance, MethodBase __originalMethod)
        {
            if (!Enabled) return;
            var view = Traverse.Create(__instance);
            string buttonName = Array.Find(CraftViews, v => v.update == __originalMethod.Name).button ?? "CreateRecipeButton";
            if (view.Property(buttonName).GetValue() is not Godot.Button button || !button.Disabled) return;
            var action = view.Property("Action").GetValue();
            if (action == null) return;
            var a = Traverse.Create(action);
            var selected = a.Property("SelectedRecipe").GetValue();
            if (selected == null || ReferenceEquals(selected, a.Property("CreatingRecipe").GetValue())) return;
            button.Disabled = false;
        }
    }

    /// <summary>Re-runs the game's button update on open crafting views (Queue mode switched on/off).</summary>
    private static void RefreshCraftButtons()
    {
        try
        {
            foreach (var node in UI.NodeSearch.All<Godot.Control>())
            {
                string? name = node.GetType().FullName;
                foreach (var v in CraftViews)
                    if (name == v.type) AccessTools.Method(node.GetType(), v.update)?.Invoke(node, null);
            }
        }
        catch (Exception e) { Plugin.Log("Queue mode: button refresh failed: " + e.Message); }
    }

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
    private static QueueEntry? s_savedRule;
    private static int s_savedProgress;

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

    /// <summary>
    /// Crafting Start buttons: queue the selected recipe straight away, without trying to start it - so a
    /// recipe can be queued before you have the materials (earlier entries, or gather-first, get them).
    /// Clicking Stop on the recipe being made still stops it.
    /// </summary>
    [HarmonyPatch]
    private static class CraftButtonPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var (type, name, _, _) in CraftViews)
            {
                var t = typeof(ActionComponent).Assembly.GetType(type);
                var m = t == null ? null : AccessTools.Method(t, name);
                if (m != null) yield return m;
                else Plugin.Log($"Queue mode: {type}.{name} not found");
            }
        }

        private static bool Prefix(object __instance)
        {
            if (Enabled) Plugin.Log($"Queue mode: crafting Start pressed (active {Active})");
            if (!Active) return true;
            return !QueueCraft(Traverse.Create(__instance).Property("Action").GetValue() as ActionComponent);
        }
    }

    /// <summary>True when the click was turned into a queue entry (skip the game's handler).</summary>
    private static bool QueueCraft(ActionComponent? action)
    {
        var game = TimeBank.TimeBank.Game;
        if (action == null || game == null) return false;
        var t = Traverse.Create(action);
        if (t.Property("SelectedRecipe").GetValue() is not IActiveCraftingRecipe selected) return false;
        if (action.IsStarted && ReferenceEquals(selected, t.Property("CreatingRecipe").GetValue())) return false; // Stop
        if (!selected.Recipe.Requirements.AreMet) { Plugin.Log("Queue mode: locked, not queued"); Captured?.Invoke("That recipe is locked - not queued."); return true; }

        var target = new ActionTarget
        {
            EntityId = action.Entity.Id,
            Ids = new[] { selected.Recipe.Id },
            Label = $"{action.Entity.DisplayName.Text}: {selected.Recipe.DisplayName.Text}",
        };
        var (kind, value) = PendingRule();
        if (kind == StopKind.Level) value = UI.UiKit.NextMilestoneLevel(action.Entity);
        var entry = new QueueEntry { Target = target, Stop = kind, StopValue = value };
        ActionQueue.Add(entry);
        string status = "Queued: " + entry.Describe();
        Plugin.Log("Queue mode: " + status);
        if (ActionQueue.RunningAction is not { IsStarted: true })
        {
            Internal = true;
            try { string why = ActionQueue.StartIfIdle(); if (why.Length > 0) status += " " + why; }
            finally { Internal = false; }
        }
        Captured?.Invoke(status);
        return true;
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
        // The instant start stops the running action, which clears its limit: keep it to put back.
        s_savedRule = ActionQueue.State?.Current;
        s_savedProgress = ActionQueue.ItemProgress;
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
        var savedRule = s_savedRule;
        int savedProgress = s_savedProgress;
        s_savedRule = null;
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
                if (SkillActions.Start(game, restore, out string why))
                {
                    if (savedRule != null && savedRule.Target.SameAs(restore)) ActionQueue.RestoreRule(savedRule, savedProgress);
                }
                else
                {
                    started.TryStop();
                    status += $" (couldn't resume {restore.Label}: {why})";
                }
            }
            else if (!hadRestore && started.IsStarted)
            {
                started.TryStop();
                // Nothing was running: rather than sit idle, start the queue (its first entry).
                string why = ActionQueue.StartIfIdle();
                if (why.Length > 0) status += " " + why;
            }
        }
        finally { Internal = false; }

        if (status.Length > 0) Captured?.Invoke(status);
    }

    internal static void Shutdown()
    {
        // Enabled is left as it is: it survives a hot reload (see EnabledMeta).
        s_scheduled = false; s_started = null; s_restore = null;
        Captured = null;
    }
}
