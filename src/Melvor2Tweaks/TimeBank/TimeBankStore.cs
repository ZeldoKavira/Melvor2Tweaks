using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using GamesByMalcs.Melvor2.Saves;
using HarmonyLib;

namespace Melvor2Tweaks.TimeBank;

/// <summary>
/// Banked time per save slot, kept beside (not inside) the game's save so the save format is untouched.
/// <see cref="SaveBank.CreditedUntil"/> stops the same absence being banked twice, e.g. when the game
/// is closed before it autosaves after a load.
/// </summary>
internal static class TimeBankStore
{
    internal sealed class SaveBank
    {
        public double BankedMs { get; set; }
        public DateTime CreditedUntil { get; set; }
    }

    private static Dictionary<string, SaveBank> s_banks = new();
    private static string FilePath => Path.Combine(Settings.DataDir, "timebank.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static event Action? Changed;

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                s_banks = JsonSerializer.Deserialize<Dictionary<string, SaveBank>>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch (Exception e)
        {
            Plugin.Log("timebank.json unreadable: " + e.Message);
            // Keep the unreadable file for recovery instead of overwriting it on the next save.
            try { File.Copy(FilePath, FilePath + ".bad", overwrite: true); } catch { }
            s_banks = new();
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Settings.DataDir);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s_banks, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception e)
        {
            Plugin.Log("Could not save time bank: " + e.Message);
        }
        Changed?.Invoke();
    }

    private static readonly Func<Guid> s_activeSaveId =
        AccessTools.MethodDelegate<Func<Guid>>(AccessTools.PropertyGetter(typeof(SaveManager), "ActiveSaveId"));

    private static SaveBank? Current
    {
        get
        {
            Guid id = s_activeSaveId();
            if (id == Guid.Empty) return null;
            string key = id.ToString();
            if (!s_banks.TryGetValue(key, out var bank)) s_banks[key] = bank = new SaveBank();
            return bank;
        }
    }

    public static double BankedMs => Current?.BankedMs ?? 0;

    public static DateTime CreditedUntil => Current?.CreditedUntil ?? DateTime.MinValue;

    public static void Deposit(double ms, DateTime creditedUntil)
    {
        var bank = Current;
        if (bank == null) return;
        bank.BankedMs += Math.Max(0, ms);
        if (creditedUntil > bank.CreditedUntil) bank.CreditedUntil = creditedUntil;
        Save();
    }

    public static void Withdraw(double ms)
    {
        var bank = Current;
        if (bank == null) return;
        bank.BankedMs = Math.Max(0, bank.BankedMs - ms);
        Save();
    }
}
