using System;
using GamesByMalcs.Melvor2;
using HarmonyLib;

namespace Melvor2Tweaks.Speed;

/// <summary>
/// Game speed multiplier. Gameplay advances only through Game.PropagateTick (one per physics frame,
/// driven by GameDriver), so running extra ticks per frame speeds everything up uniformly without
/// touching UI animation speed. The driver is unbound during offline simulation, so this never
/// stacks with offline progress.
/// </summary>
[HarmonyPatch(typeof(GameDriver), "_PhysicsProcess")]
internal static class SpeedPatch
{
    private const double MaxSpeed = 100;
    private static double s_carry;

    private static readonly AccessTools.FieldRef<GameDriver, Game?>? s_gameField =
        AccessTools.FieldRefAccess<GameDriver, Game?>("<Game>k__BackingField");

    private static void Postfix(GameDriver __instance)
    {
        double speed = Math.Clamp(Settings.Current.Speed, 1, MaxSpeed);
        if (speed <= 1) { s_carry = 0; return; }

        Game? game = s_gameField?.Invoke(__instance);
        if (game == null) return;

        s_carry += speed - 1;
        while (s_carry >= 1)
        {
            game.PropagateTick();
            s_carry -= 1;
        }
    }
}
