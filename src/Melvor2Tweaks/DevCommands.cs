using System;
using System.Collections.Generic;
using System.IO;
using GamesByMalcs.Melvor2.GameMenus.PauseOverlay;
using Godot;

namespace Melvor2Tweaks;

/// <summary>
/// Development aid: one command per line in Melvor2Tweaks\dev.txt is run (then the file is deleted),
/// checked once a second. Lets a developer drive UI for screenshots without clicking in the game.
///   open-settings   pause menu -> Settings -> Melvor2Tweaks tab
///   close-settings  close the pause menu
/// </summary>
internal static class DevCommands
{
    private static string FilePath => Path.Combine(Settings.DataDir, "dev.txt");
    private static GamesByMalcs.Melvor2.GameMenus.Pages.GamePage? s_previousPage;

    internal static void Poll()
    {
        if (!File.Exists(FilePath)) return;
        string[] lines;
        try { lines = File.ReadAllLines(FilePath); File.Delete(FilePath); }
        catch (IOException) { return; }

        foreach (string raw in lines)
        {
            string cmd = raw.Trim();
            if (cmd.Length == 0) continue;
            try { Run(cmd); Plugin.Log("dev: " + cmd); }
            catch (Exception e) { Plugin.Log($"dev: {cmd} failed: {e.Message}"); }
        }
    }

    private static void Run(string cmd)
    {
        switch (cmd)
        {
            case "open-settings":
                foreach (var menu in FindVisibleTree<PauseMenu>())
                {
                    menu.OpenSettings();
                    if (menu.FindChild("Melvor2TweaksSettingsButton", recursive: true, owned: false) is Button button)
                    {
                        button.ButtonPressed = true; // goes through the ButtonGroup like a click
                        button.EmitSignal(BaseButton.SignalName.Pressed);
                    }
                }
                break;
            case "close-settings":
                foreach (var menu in FindVisibleTree<PauseMenu>()) menu.Close();
                break;
            case "toggle-timebank":
                UI.TopBar.Toggle();
                break;
            case "toggle-actions":
                UI.ActionsMenu.Toggle();
                break;
            case "next-milestone":
                if (Actions.ActionQueue.RunningAction is { } running)
                    Plugin.Log($"next milestone for {running.Entity.Id}: level {UI.UiKit.NextMilestoneLevel(running.Entity)}");
                break;
            case "page-back":
                if (s_previousPage != null)
                    foreach (var view in UI.NodeSearch.All<GamesByMalcs.Melvor2.GameMenus.GameView>()) view.SetCurrentPage(s_previousPage);
                break;
            case var p when p.StartsWith("page:"):
                // e.g. page:MelvorBase:Farming - remembers the current page for page-back
                foreach (var view in UI.NodeSearch.All<GamesByMalcs.Melvor2.GameMenus.GameView>())
                {
                    var current = HarmonyLib.Traverse.Create(view).Property("CurrentPage").GetValue();
                    if (current != null) s_previousPage = HarmonyLib.Traverse.Create(current).Field("Item1").GetValue<GamesByMalcs.Melvor2.GameMenus.Pages.GamePage>();
                    var page = TimeBank.TimeBank.Game!.Data.GetObject<GamesByMalcs.Melvor2.GameMenus.Pages.GamePage>(p.Substring(5));
                    view.SetCurrentPage(page);
                }
                break;
            default:
                throw new ArgumentException("unknown command");
        }
    }

    private static IEnumerable<T> FindVisibleTree<T>() where T : Node
    {
        if (Engine.GetMainLoop() is not SceneTree tree) yield break;
        var stack = new Stack<Node>();
        stack.Push(tree.Root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is T match) yield return match;
            foreach (Node child in node.GetChildren()) stack.Push(child);
        }
    }
}
