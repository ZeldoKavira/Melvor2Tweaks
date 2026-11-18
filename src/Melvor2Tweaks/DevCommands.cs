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
            case var dd when dd.StartsWith("dropdown:"):
            {
                // dropdown:open | dropdown:<index> - our settings dropdowns; logs item counts incl. the game's Screen mode one
                foreach (var d in FindVisibleTree<GamesByMalcs.Melvor2.CustomControls.CustomDropdown>())
                {
                    bool ours = d.GetPath().ToString().Contains("Melvor2TweaksSettingsView");
                    Plugin.Log($"dropdown {(ours ? "ours" : "game")} {d.Name}: items={d.ItemCount} selected={d.SelectedIndex}");
                    if (!ours) continue;
                    string arg = dd.Substring("dropdown:".Length);
                    if (arg == "open") d.Open(); else d.Select(int.Parse(arg));
                }
                Plugin.Log($"NewRuleKeep = {Settings.Current.NewRuleKeep}");
                break;
            }
            case var s when s.StartsWith("items-show:"):
            {
                // items-show:<filter 0|1|2>:<search text>
                var parts = s.Split(':', 3);
                UI.ItemsMenu.DevSet(parts.Length > 2 ? parts[2] : "", int.Parse(parts[1]));
                break;
            }
            case var bs when bs.StartsWith("bank-select:"):
            {
                // bank-select:<itemId> - selects that stack in the current bank tab (bank page must be open)
                string id = bs.Substring("bank-select:".Length);
                var tab = Features.ItemRules.Bank!.Inventory.SelectedTab;
                for (int i = 0; i < tab.Stacks.Count; i++)
                    if (tab.Stacks[i].Item.Id == id) { tab.SelectItemStackAt(i); break; }
                break;
            }
            case var dc when dc.StartsWith("dc-dry:"):
            {
                string id = dc.Substring("dc-dry:".Length);
                foreach (var dcStack in Features.ItemRules.Bank!.Inventory.SelectedTab.Stacks)
                    if (id == "all" || dcStack.Item.Id == id) Features.BankDoubleClick.Equip(dcStack, dryRun: true);
                break;
            }
            case var r when r.StartsWith("rule-test:"):
            {
                // rule-test:<itemId>:<Open|Bury|Sell> - handles exactly one item (keep = count - 1), then turns the rule off.
                var parts = r.Split(':');
                string id = parts[1] + ":" + parts[2];
                var action = Enum.Parse<Features.RuleAction>(parts[3]);
                var game = TimeBank.TimeBank.Game!;
                if (!game.Data.TryGetObject<GamesByMalcs.Melvor2.Data.Items.Item>(id, out var item)) { Plugin.Log("rule-test: no item " + id); break; }
                int before = Features.ItemRules.InBank(item);
                Features.ItemRules.Set(item, action, Math.Max(0, before - 1));
                int after = Features.ItemRules.InBank(item);
                Features.ItemRules.Set(item, Features.RuleAction.Off, 0);
                Plugin.Log($"rule-test: {action} {id}: {before} -> {after}");
                break;
            }
            case "find-hint":
            {
                // Live nodes showing the game's "?" hint icon: how they're built and how their tooltip attaches.
                int found = 0;
                foreach (var tr in FindVisibleTree<TextureRect>())
                {
                    if (tr.Texture?.ResourcePath?.Contains("TooltipHint") != true) continue;
                    Plugin.Log($"hint at {tr.GetPath()}");
                    Dump(tr.GetParent(), 0, 2);
                    if (++found >= 2) break;
                }
                Plugin.Log($"find-hint: {found} found");
                break;
            }
            case "scroll-settings-bottom":
                foreach (var view in FindVisibleTree<VBoxContainer>())
                {
                    if (view.Name != "Melvor2TweaksSettingsView") continue;
                    if (view.FindChildren("*", "ScrollContainer", true, false) is { Count: > 0 } s && s[0] is ScrollContainer sc)
                        sc.ScrollVertical = (int)sc.GetVScrollBar().MaxValue;
                }
                break;
            case "open-settings-game":
                foreach (var menu in FindVisibleTree<PauseMenu>()) menu.OpenSettings();
                break;
            case "dump-settings":
                // Node tree of the game's own "Game" settings view, to copy its layout.
                foreach (var panel in FindVisibleTree<GamesByMalcs.Melvor2.GameMenus.PauseOverlay.SettingsPanel>())
                {
                    var view = HarmonyLib.Traverse.Create(panel).Property("GameSettingsView").GetValue<Node>();
                    if (view == null) continue;
                    var row = view.FindChild("StopActionOnFullBank", true, false);
                    if (row != null) Dump(row, 0, 6);
                    var iface = HarmonyLib.Traverse.Create(panel).Property("InterfaceSettingsView").GetValue<Node>();
                    var list = iface?.FindChild("VBoxContainer", true, false);
                    if (list != null) Dump(list, 0, 4);
                    break;
                }
                break;
            case "close-settings":
                foreach (var menu in FindVisibleTree<PauseMenu>()) menu.Close();
                break;
            case "toggle-timebank":
                UI.TopBar.Toggle();
                break;
            case "toggle-items":
                UI.ItemsMenu.Toggle();
                break;
            case "toggle-actions":
                UI.ActionsMenu.Toggle();
                break;
            case "qm-test-woodcutting":
            {
                // Same call as clicking a tree's Cut button, with Queue mode on.
                var game = TimeBank.TimeBank.Game!;
                game.Entities.TryGetObjectById("MelvorBase:Woodcutting", out var wcEntity);
                var wc = (GamesByMalcs.Melvor2.Entities.Woodcutting.WoodcuttingActionComponent)wcEntity!.Components["Action"];
                GamesByMalcs.Melvor2.Entities.Woodcutting.WoodcuttingTree? tree = null;
                foreach (var t in wc.Trees.Values) if (t.Recipe.Requirements.AreMet) { tree = t; break; }
                Plugin.Log($"qm-test: running={Actions.ActionQueue.RunningAction?.Entity.Id}, clicking tree {tree?.Recipe.Id}");
                Actions.QueueMode.Enabled = true;
                wc.SelectTree(tree!);
                Callable.From(() =>
                {
                    Actions.QueueMode.Enabled = false;
                    Plugin.Log($"qm-test: after, running={Actions.ActionQueue.RunningAction?.Entity.Id} " +
                               $"target={Actions.ActionQueue.RunningAction?.CurrentDescription?.Id}, queued={Actions.ActionQueue.State?.Entries.Count}");
                }).CallDeferred();
                break;
            }
            case "queue-pop-last":
                if (Actions.ActionQueue.State is { Entries.Count: > 0 } st) Actions.ActionQueue.RemoveAt(st.Entries.Count - 1);
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

    private static void Dump(Node node, int depth, int maxDepth)
    {
        string extra = "";
        if (node is Control c)
        {
            extra += $" size={c.Size} min={c.CustomMinimumSize} var='{c.ThemeTypeVariation}'";
            if (c.SizeFlagsHorizontal != Control.SizeFlags.Fill) extra += $" h={c.SizeFlagsHorizontal}";
        }
        if (node is Label l) extra += $" text='{l.Text}' settings={(l.LabelSettings != null ? l.LabelSettings.ResourcePath : "")}";
        if (node is BaseButton b) extra += $" btntext='{(b as Button)?.Text}'";
        if (node is BoxContainer box) extra += $" sep={box.GetThemeConstant("separation")}";
        if (node is MarginContainer m) extra += $" margins={m.GetThemeConstant("margin_left")},{m.GetThemeConstant("margin_top")}";
        if (node is TextureRect tex) extra += $" tex={tex.Texture?.ResourcePath} expand={tex.ExpandMode} stretch={tex.StretchMode} mouse={tex.MouseFilter}";
        if (node is Control ctl && ctl.TooltipText.Length > 0) extra += $" tooltipText='{ctl.TooltipText}'";
        if (node.SceneFilePath.Length > 0) extra += $" scene={node.SceneFilePath}";
        if (node is PanelContainer p) extra += $" panel={p.GetThemeStylebox("panel")?.GetType().Name}:{p.GetThemeStylebox("panel")?.ResourcePath}";
        Plugin.Log($"{new string(' ', depth * 2)}{node.GetType().Name} '{node.Name}'{extra} script={(node.GetScript().VariantType != Variant.Type.Nil ? "yes" : "no")}");
        if (depth >= maxDepth) return;
        foreach (Node child in node.GetChildren()) Dump(child, depth + 1, maxDepth);
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
