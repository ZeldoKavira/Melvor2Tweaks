using System.Linq;
using GamesByMalcs.Melvor2.Components.Trackers;
using Godot;
using HarmonyLib;

namespace Melvor2Tweaks.Features;

/// <summary>
/// The footer already shows "Xh until &lt;item&gt; depleted"; append how many actions that is,
/// using the running action's interval (same estimate the game uses for the time).
/// </summary>
[HarmonyPatch(typeof(TrackerBankView), "UpdateDepletionLabelText")]
internal static class CraftsLeft
{
    private static void Postfix(TrackerBankView __instance)
    {
        var t = Traverse.Create(__instance);
        double hours = t.Property("TimeUntilDepleted").GetValue<double>();
        if (hours <= 0 || hours > 23976) return;
        var label = t.Property("TimeUntilDepletionLabel").GetValue<Label>();
        var action = TimeBank.TimeBank.Game?.ActionManager.ActiveActions.FirstOrDefault();
        double interval = action?.ActionTimer?.WaitTime ?? 0;
        if (label == null || interval <= 0) return;
        long count = (long)(hours * 3_600_000 / interval);
        label.Text += $" (~{count:N0} actions)";
    }
}
