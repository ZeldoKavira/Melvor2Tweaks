using System;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Core.SleepDetection;
using Godot;
using HarmonyLib;

namespace Melvor2Tweaks.TimeBank;

/// <summary>
/// Hooks Main.GameState (private nested class) so offline time is banked instead of simulated,
/// and spends banked time through the game's own offline-progress path (Game.RequestOfflineHours).
/// </summary>
internal static class TimeBank
{
    private const double MinRunMs = 10_000;     // Main.GameState.MinOfflineTime (s) * 1000
    private const double MaxCatchupSec = 60;    // Main.GameState.MaxCatchupTime

    internal static readonly Type GameStateType = AccessTools.Inner(typeof(Main), "GameState");

    /// <summary>The live GameState while a save is open (set in OnEntry, cleared in OnExit).</summary>
    internal static object? State { get; set; }

    internal static Game? Game => State == null ? null : Traverse.Create(State).Property("Game").GetValue<Game>();

    /// <summary>The game's Main node (root of the app), or null before it exists.</summary>
    internal static Main? MainNode
    {
        get
        {
            if (Engine.GetMainLoop() is not SceneTree tree) return null;
            foreach (Node n in tree.Root.GetChildren())
                if (n is Main m) return m;
            return null;
        }
    }

    /// <summary>Hot-loaded mid-game: OnEntry already ran, so pick up the current state directly.</summary>
    internal static void AttachToRunningGame()
    {
        Main? main = MainNode;
        object? current = main == null ? null : Traverse.Create(main).Property("InternalState").GetValue();
        State = current != null && GameStateType.IsInstanceOfType(current) ? current : null;
    }

    /// <summary>True when the game is running normally (not offline-simulating, catching up or asleep).</summary>
    internal static bool IsProcessing =>
        State != null && Traverse.Create(State).Property("RunningMode").GetValue()?.ToString() == "Process";

    /// <summary>Splits an absence into (banked, run-normally) per settings.</summary>
    private static (double bank, double normal) Split(double awayMs)
    {
        var s = Settings.Current;
        double ratio = Math.Clamp(s.BankRatio, 0, 1);
        double banked = awayMs * ratio;
        if (s.MaxAwayHours >= 0) banked = Math.Min(banked, s.MaxAwayHours * 3_600_000 * ratio);
        banked *= Math.Max(0, s.OfflineMultiplier);
        return (banked, awayMs * (1 - ratio));
    }

    // ---- loading a save -------------------------------------------------------------------------
    [HarmonyPatch]
    private static class OnLoadPatch
    {
        private static System.Reflection.MethodBase TargetMethod() => AccessTools.Method(GameStateType, "OnLoad");

        private static void Postfix(object __instance)
        {
            if (!Settings.Current.TimeBankEnabled) return;
            var t = Traverse.Create(__instance);
            if (!t.Property("IsRunningLoadOffline").GetValue<bool>()) return;

            Game game = t.Property("Game").GetValue<Game>();
            DateTime now = DateTime.Now;
            DateTime since = game.LoadedSaveTimestamp;
            if (TimeBankStore.CreditedUntil > since) since = TimeBankStore.CreditedUntil;
            double away = Math.Max(0, (now - since).TotalMilliseconds);

            var (bank, normal) = Split(away);
            TimeBankStore.Deposit(bank, now);
            t.Property("LoadOfflineDiff").SetValue(normal);
            t.Property("IsRunningLoadOffline").SetValue(normal >= MinRunMs);
            Plugin.Log($"Away {Format(away)}: banked {Format(bank)}, running {Format(normal)} now. Bank: {Format(TimeBankStore.BankedMs)}");
        }
    }

    // ---- PC woke from sleep while the game was open ----------------------------------------------
    [HarmonyPatch]
    private static class OnSleepWakePatch
    {
        private static System.Reflection.MethodBase TargetMethod() => AccessTools.Method(GameStateType, "OnSleepWake");

        private static bool Prefix(object __instance, SleepWakeEventArgs e)
        {
            // Short gaps keep the game's own silent catch-up; anything else while not processing is left alone.
            if (!Settings.Current.TimeBankEnabled || e.SleepTime <= MaxCatchupSec) return true;
            if (!ReferenceEquals(__instance, State) || !IsProcessing) return true;

            var (bank, normal) = Split(e.SleepTime * 1000);
            TimeBankStore.Deposit(bank, DateTime.Now);
            Plugin.Log($"Slept {Format(e.SleepTime * 1000)}: banked {Format(bank)}. Bank: {Format(TimeBankStore.BankedMs)}");
            if (normal >= MinRunMs) Game?.RequestOfflineHours(normal / 3_600_000);
            return false;
        }
    }

    [HarmonyPatch]
    private static class OnEntryPatch
    {
        private static System.Reflection.MethodBase TargetMethod() => AccessTools.Method(GameStateType, "OnEntry");
        private static void Prefix(object __instance) => State = __instance;
    }

    [HarmonyPatch]
    private static class OnExitPatch
    {
        private static System.Reflection.MethodBase TargetMethod() => AccessTools.Method(GameStateType, "OnExit");
        private static void Prefix(object __instance) { if (ReferenceEquals(State, __instance)) State = null; }
    }

    // ---- spending ---------------------------------------------------------------------------------
    /// <summary>Simulates up to <paramref name="ms"/> of banked time. Returns the amount spent (0 = nothing happened).</summary>
    internal static double Spend(double ms, out string reason)
    {
        Game? game = Game;
        if (game == null || !IsProcessing) { reason = "Not available right now"; return 0; }

        double cap = game.OfflineProgress.Component.MaxOfflineTime; // the game caps one offline run at this
        double amount = Math.Min(Math.Min(ms, TimeBankStore.BankedMs), cap);
        if (amount < MinRunMs) { reason = "Not enough banked time"; return 0; }

        game.RequestOfflineHours(amount / 3_600_000);
        if (IsProcessing) { reason = "The game refused the time skip"; return 0; }

        TimeBankStore.Withdraw(amount);
        reason = "";
        Plugin.Log($"Spent {Format(amount)}. Bank: {Format(TimeBankStore.BankedMs)}");
        return amount;
    }

    internal static string Format(double ms)
    {
        var ts = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes:00}m";
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes:00}m";
        return $"{ts.Minutes}m {ts.Seconds:00}s";
    }
}
