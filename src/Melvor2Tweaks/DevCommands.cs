using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            case "guard-check":
            {
                var game = TimeBank.TimeBank.Game!;
                foreach (string skill in new[] { "MelvorBase:Agility", "MelvorBase:Thieving" })
                {
                    game.Entities.TryGetObjectById(skill, out var skillEntity);
                    if (skillEntity?.Components.Values.OfType<GamesByMalcs.Melvor2.Components.Actions.ActionComponent>().FirstOrDefault() is { } act)
                        Plugin.Log(Features.DeathGuard.Describe(act));
                    else Plugin.Log("guard-check: no action component for " + skill);
                }
                break;
            }
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
            case var gp when gp.StartsWith("gather-plan:"):
            {
                // gather-plan:<entityId>:<recipeId>:<crafts> - dry run, changes nothing
                var parts = gp.Split(':');
                if (parts.Length >= 6 && TimeBank.TimeBank.Game is { } gpGame)
                {
                    var entry = new Actions.QueueEntry { Target = new Actions.ActionTarget { EntityId = parts[1] + ":" + parts[2], Ids = new[] { parts[3] + ":" + parts[4] }, Label = parts[4] } };
                    var plan = Actions.Gather.Plan(gpGame, entry, int.Parse(parts[5]), out string note);
                    Plugin.Log($"gather-plan {parts[4]} x{parts[5]}: crafting={Actions.Gather.IsCrafting(gpGame, entry.Target)} affordable={Actions.Gather.CraftsAffordable(gpGame, entry.Target)} " +
                               $"plan [{string.Join("; ", plan.Select(p => p.Describe()))}] {note}");
                }
                break;
            }
            case var bq when bq.StartsWith("bank:"):
            {
                string q = bq.Substring("bank:".Length);
                foreach (var i in TimeBank.TimeBank.Game?.Data.GetDataRegistryOutput<GamesByMalcs.Melvor2.Data.Items.Item>().GetAllObjects()
                             .Where(i => i.DisplayName.Singular.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(15)
                         ?? Enumerable.Empty<GamesByMalcs.Melvor2.Data.Items.Item>())
                    Plugin.Log($"bank {i.Id}: {Features.ItemRules.InBank(i):N0}");
                break;
            }
            case var us when us.StartsWith("uses:"):
            {
                // uses:<itemId> - crafting recipes and shop purchases that cost the item
                var ug = TimeBank.TimeBank.Game;
                string id = us.Substring("uses:".Length);
                if (ug == null) break;
                foreach (var e in ug.Entities.GetAllObjects())
                    if (e.Components.TryGetValue("Action", out var ac) && ac is GamesByMalcs.Melvor2.Components.Actions.ActionComponent a)
                        foreach (var r in Actions.Gather.AllCraftingRecipes(a))
                            foreach (var v in r.Stats.ItemCost.Values)
                                if (v.Value.Item.Id == id) Plugin.Log($"uses: {e.DisplayName.Text}: {r.Recipe.DisplayName.Text} x{v.Value.Quantity} (unlocked {r.Recipe.Requirements.AreMet})");
                foreach (var sp in ug.Data.GetDataRegistryOutput<GamesByMalcs.Melvor2.Entities.Shop.ShopPurchase>().GetAllObjects())
                    foreach (var c in sp.Costs.Items)
                        if (c.Item.Id == id) Plugin.Log($"uses: shop {sp.Id} x{c.Quantity}");
                break;
            }
            case "shop-bought":
            {
                var sg = TimeBank.TimeBank.Game;
                if (sg == null) break;
                foreach (var e in sg.Entities.GetAllObjects())
                    foreach (var comp in e.Components.Values)
                        if (comp is GamesByMalcs.Melvor2.Components.Shop.ShopComponent shop)
                            foreach (var kv in shop.UpgradesPurchased)
                                if (kv.Value > 0 && kv.Key.Costs.Items.Count > 0)
                                    Plugin.Log($"bought {kv.Key.Id} x{kv.Value}: " + string.Join(", ", kv.Key.Costs.Items.Select(c => c.Quantity + " " + c.Item.Id)));
                break;
            }
            case var it when it.StartsWith("item-tags:"):
            {
                string q = it.Substring("item-tags:".Length);
                var items = TimeBank.TimeBank.Game?.Data.GetDataRegistryOutput<GamesByMalcs.Melvor2.Data.Items.Item>().GetAllObjects()
                    .Where(i => i.DisplayName.Singular.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(15);
                foreach (var i in items ?? Enumerable.Empty<GamesByMalcs.Melvor2.Data.Items.Item>())
                    Plugin.Log($"tags {i.Id}: " + string.Join(", ", i.BankTags.Select(t => $"{t.BankTag.DisplayName.Text}{(t.IsProduct ? " P" : "")}{(t.IsIngredient ? " I" : "")}")));
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
            case "dump-farm":
                foreach (var fv in FindVisibleTree<GamesByMalcs.Melvor2.Entities.Farming.FarmingActionView>())
                {
                    var mill = HarmonyLib.Traverse.Create(fv).Property("GrainMillButton").GetValue<Node>();
                    var top = mill?.GetParent()?.GetParent()?.GetParent();
                    if (top != null) Dump(top, 0, 5);
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
                bool qmWas = Actions.QueueMode.Enabled; Actions.QueueMode.Enabled = true;
                wc.SelectTree(tree!);
                Callable.From(() =>
                {
                    Actions.QueueMode.Enabled = qmWas;
                    Plugin.Log($"qm-test: after, running={Actions.ActionQueue.RunningAction?.Entity.Id} " +
                               $"target={Actions.ActionQueue.RunningAction?.CurrentDescription?.Id}, queued={Actions.ActionQueue.State?.Entries.Count}");
                }).CallDeferred();
                break;
            }
            case "qm-test-mining":
            {
                // Same call as clicking a rock's Mine button, with Queue mode on (a different skill than the running one).
                var game = TimeBank.TimeBank.Game!;
                game.Entities.TryGetObjectById("MelvorBase:Mining", out var mEntity);
                var m = (GamesByMalcs.Melvor2.Entities.Mining.MiningActionComponent)mEntity!.Components["Action"];
                GamesByMalcs.Melvor2.Entities.Mining.MiningRock? rock = null;
                foreach (var r in m.Rocks.Values) if (r.Recipe.Requirements.AreMet) { rock = r; break; }
                Plugin.Log($"qm-test: running={Actions.ActionQueue.RunningAction?.Entity.Id}, rule={Actions.ActionQueue.State?.Current?.Describe()}, clicking rock {rock?.Recipe.Id}");
                bool qmWas = Actions.QueueMode.Enabled; Actions.QueueMode.Enabled = true;
                m.SelectRock(rock!);
                Callable.From(() =>
                {
                    Actions.QueueMode.Enabled = qmWas;
                    Plugin.Log($"qm-test: after, running={Actions.ActionQueue.RunningAction?.Entity.Id} " +
                               $"rule={Actions.ActionQueue.State?.Current?.Describe()} progress={Actions.ActionQueue.ItemProgress}, queued={Actions.ActionQueue.State?.Entries.Count}");
                }).CallDeferred();
                break;
            }
            case var rl when rl.StartsWith("rule:"):
            {
                // rule:<0 none|1 level|2 items>:<value> on the running action
                var parts = rl.Split(':');
                Actions.ActionQueue.SetRuleOnRunning((Actions.StopKind)int.Parse(parts[1]), int.Parse(parts[2]));
                Plugin.Log($"rule: now {Actions.ActionQueue.State?.Current?.Describe() ?? "none"}");
                break;
            }
            case "toggle-droplog":
                UI.ActionsMenu.ToggleLog();
                break;
            case var qc when qc.StartsWith("qm-test-craft:"):
            {
                // qm-test-craft:<ns>:<entity>:<ns>:<recipe> - select the recipe and press the skill's Start button with Queue mode on
                var parts = qc.Split(':');
                var game = TimeBank.TimeBank.Game!;
                game.Entities.TryGetObjectById(parts[1] + ":" + parts[2], out var ent);
                var action = (GamesByMalcs.Melvor2.Components.Actions.ActionComponent)ent!.Components["Action"];
                var recipes = HarmonyLib.Traverse.Create(action).Property("ActiveRecipes").GetValue() as System.Collections.IDictionary;
                object? recipe = null;
                foreach (System.Collections.DictionaryEntry e in recipes!)
                    if ((e.Key as GamesByMalcs.Melvor2.Data.INamespacedObject)?.Id == parts[3] + ":" + parts[4]) recipe = e.Value;
                HarmonyLib.AccessTools.Method(action.GetType(), "SelectRecipe")!.Invoke(action, new[] { recipe });
                int before = Actions.ActionQueue.State?.Entries.Count ?? 0;
                bool qmWas = Actions.QueueMode.Enabled; Actions.QueueMode.Enabled = true;
                bool pressed = false;
                foreach (var view in UI.NodeSearch.All<Godot.Control>())
                {
                    if (view.GetType().Name is not ("ArtisanSelectedRecipeContainer" or "CookingSelectedRecipeContainer" or "HerbloreSelectedRecipeContainer")) continue;
                    if (!ReferenceEquals(HarmonyLib.Traverse.Create(view).Property("Action").GetValue(), action)) continue;
                    HarmonyLib.AccessTools.Method(view.GetType(), "CreateRecipeButtonPressedHandler")!.Invoke(view, null);
                    pressed = true;
                    break;
                }
                Actions.QueueMode.Enabled = qmWas;
                Plugin.Log($"qm-test-craft: pressed={pressed} running={Actions.ActionQueue.RunningAction?.Entity.Id} queued {before} -> {Actions.ActionQueue.State?.Entries.Count}, last={Actions.ActionQueue.State?.Entries.LastOrDefault()?.Describe()}");
                break;
            }
            case "timing":
                UI.ActionsMenu.s_timing = !UI.ActionsMenu.s_timing;
                break;
            case var qmc when qmc.StartsWith("queue-mode:"):
                Actions.QueueMode.Enabled = qmc.EndsWith("on");
                Plugin.Log($"queue-mode: {Actions.QueueMode.Enabled}");
                break;
            case "queue-mode?":
                Plugin.Log($"queue-mode is {(Actions.QueueMode.Enabled ? "on" : "off")}");
                break;
            case var sel when sel.StartsWith("select-recipe:"):
            {
                var parts = sel.Split(':');
                TimeBank.TimeBank.Game!.Entities.TryGetObjectById(parts[1] + ":" + parts[2], out var ent);
                var action = ent!.Components["Action"];
                var recipes = HarmonyLib.Traverse.Create(action).Property("ActiveRecipes").GetValue() as System.Collections.IDictionary;
                foreach (System.Collections.DictionaryEntry e in recipes!)
                    if ((e.Key as GamesByMalcs.Melvor2.Data.INamespacedObject)?.Id == parts[3] + ":" + parts[4])
                        HarmonyLib.AccessTools.Method(action.GetType(), "SelectRecipe")!.Invoke(action, new[] { e.Value });
                break;
            }
            case "bump":
                Actions.ActionQueue.BumpCurrent();
                Plugin.Log("bump: running " + (Actions.ActionQueue.RunningAction is { } ra ? Actions.SkillActions.Capture(ra)?.Label : "nothing") + "; queue " + string.Join(" | ", Actions.ActionQueue.State?.Entries.ConvertAll(e => e.Describe()) ?? new()));
                break;
            case "astro-info":
            {
                var agame = TimeBank.TimeBank.Game;
                if (agame == null) break;
                foreach (var e in agame.Entities.GetAllObjects())
                    if (e.Components.TryGetValue("Action", out var ac) && ac is GamesByMalcs.Melvor2.Entities.Astrology.AstrologyActionComponent astro)
                    {
                        Plugin.Log($"astro: studied {astro.StudiedConstellation?.Constellation.Id}");
                        foreach (var (c, active) in astro.ActiveConstellations)
                        {
                            Plugin.Log($"astro {c.Id}: knowledge {active.Knowledge:N0} (+{active.Stats.Knowledge.Value}/study), minor {active.MinorNodeUnlockCount}/{active.AvailableMinorNodeCount} avail of {c.MinorNodes.Count}, " +
                                       $"major {active.MajorNodeUnlockCount}/{active.AvailableMajorNodeCount} avail of {c.MajorNodes.Count}, per major {c.MinorNodesPerMajorNode}, thresholds [{string.Join(",", c.MinorNodeUnlockKnowledge)}]");
                            foreach (var n in c.MinorNodes.Concat(c.MajorNodes))
                                if (n.UnlockRequirements.Conditions.Count > 0)
                                    Plugin.Log($"   node {n.Id} {n.NodeType}: " + string.Join(" & ", n.UnlockRequirements.Conditions.Select(x => x.GetType().Name + (x is GamesByMalcs.Melvor2.Components.Experience.LevelCondition lc ? $" {lc.Component?.Entity?.Id} {lc.Level}" : "") + (x.IsMet ? "(met)" : "(unmet)"))));
                        }
                    }
                break;
            }
            case var pp when pp.StartsWith("popup-press:"):
                Plugin.Log("popup-press: " + (UI.QueueConditionPopup.DevPress(pp.Substring("popup-press:".Length)) ? "pressed" : "no popup / button"));
                break;
            case var po when po.StartsWith("popup-show:"):
            {
                // popup-show:<ns>:<entity>:<ns>:<id> - shows the Queue mode popup; picking only logs (nothing queued)
                var p = po.Split(':');
                var t = new Actions.ActionTarget { EntityId = p[1] + ":" + p[2], Ids = new[] { p[3] + ":" + p[4] }, Label = "TEST " + p[4] };
                UI.QueueConditionPopup.Show(t, (k, v) => Plugin.Log($"popup-show: picked {k} {v}"), () => Plugin.Log("popup-show: cancelled"));
                break;
            }
            case "dump-data":
            {
                using var f = Godot.FileAccess.Open("res://Data/Packages/MelvorBase.json", Godot.FileAccess.ModeFlags.Read);
                string outPath = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                    "Games By Malcs", "Melvor Idle 2", "Melvor2Tweaks", "MelvorBase.dump.json");
                if (f == null) { Plugin.Log("dump-data: can't open package"); break; }
                System.IO.File.WriteAllText(outPath, f.GetAsText());
                Plugin.Log("dump-data: wrote " + outPath);
                break;
            }
            case "update-check":
                _ = Updater.CheckAndInstall(System.Threading.CancellationToken.None);
                break;
            case "update-force":
                _ = Updater.CheckAndInstall(System.Threading.CancellationToken.None, force: true);
                break;
            case "remote:on":
            case "remote:off":
                Settings.Current.RemoteEnabled = cmd == "remote:on";
                Settings.Save();
                Plugin.Log($"remote: {(Remote.RemoteServer.Running ? "listening on " + Remote.RemoteServer.Port : "off " + Remote.RemoteServer.LastError)}");
                break;
            case var rl when rl.StartsWith("remote-lan:"):
                // remote-lan:<allowed addresses, comma separated> (empty = off)
                Settings.Current.RemoteAllowedClients = rl.Substring("remote-lan:".Length);
                Settings.Current.RemoteLan = Settings.Current.RemoteAllowedClients.Length > 0;
                Settings.Save();
                break;
            case var qrc when qrc.StartsWith("qr-png:"):
            {
                // qr-png:<text> - writes the QR code for <text> to qr-test.png beside settings.json
                string path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Settings.FilePath)!, "qr-test.png");
                var code = UI.QrCode.Encode(qrc.Substring("qr-png:".Length));
                code.ToTexture(8).GetImage().SavePng(path);
                Plugin.Log($"qr-png: version size {code.Size} -> {path}");
                break;
            }
            case "cost-buttons":
                Features.QueueFromCost.LogButtons();
                break;
            case "btn-state":
                foreach (var view in UI.NodeSearch.All<Godot.Control>())
                    if (view.GetType().Name == "ArtisanSelectedRecipeContainer" && view.IsVisibleInTree())
                    {
                        var btn = HarmonyLib.Traverse.Create(view).Property("CreateRecipeButton").GetValue<Godot.Button>();
                        var act = HarmonyLib.Traverse.Create(view).Property("Action").GetValue();
                        var selr = HarmonyLib.Traverse.Create(act).Property("SelectedRecipe").Property("Recipe").GetValue() as GamesByMalcs.Melvor2.Data.INamespacedObject;
                        Plugin.Log($"btn-state: {selr?.Id} disabled={btn?.Disabled} queueMode={Actions.QueueMode.Enabled}");
                    }
                break;
            case var ba when ba.StartsWith("bank-add:"):
                // bank-add:<minutes> - put time back in the time bank (e.g. refund a skip)
                TimeBank.TimeBankStore.Deposit(double.Parse(ba.Substring("bank-add:".Length)) * 60_000, TimeBank.TimeBankStore.CreditedUntil);
                Plugin.Log($"bank-add: bank now {TimeBank.TimeBank.Format(TimeBank.TimeBankStore.BankedMs)}");
                break;
            case var rh when rh.StartsWith("run-hours:"):
                // run-hours:<h> - run game time through the game's offline progress, without touching the bank
                TimeBank.TimeBank.Game?.RequestOfflineHours(double.Parse(rh.Substring("run-hours:".Length)));
                break;
            case "override-test":
            {
                // Like the player clicking Mine on a rock while a queue entry runs, then Stop, then Start queue.
                bool qmWas = Actions.QueueMode.Enabled;
                Actions.QueueMode.Enabled = false;
                var game = TimeBank.TimeBank.Game!;
                Plugin.Log($"override-test: before, running {Actions.ActionQueue.State?.Current?.Describe()} progress {Actions.ActionQueue.ItemProgress}, front {Actions.ActionQueue.State?.Entries.FirstOrDefault()?.Describe()}");
                if (Actions.ActionQueue.RunningAction is GamesByMalcs.Melvor2.Entities.Woodcutting.WoodcuttingActionComponent wcSame)
                {
                    foreach (var t in wcSame.Trees.Values) if (t.Recipe.Requirements.AreMet && t != wcSame.CuttingTree) { wcSame.SelectTree(t); break; }
                }
                else
                {
                    game.Entities.TryGetObjectById("MelvorBase:Mining", out var mEntity);
                    var m = (GamesByMalcs.Melvor2.Entities.Mining.MiningActionComponent)mEntity!.Components["Action"];
                    foreach (var r in m.Rocks.Values) if (r.Recipe.Requirements.AreMet) { m.SelectRock(r); break; }
                }
                Plugin.Log($"override-test: after override, running {Actions.ActionQueue.RunningAction?.Entity.Id}, front {Actions.ActionQueue.State?.Entries.FirstOrDefault()?.Describe()}");
                Actions.ActionQueue.RunningAction?.TryStop();
                string why = Actions.ActionQueue.StartIfIdle();
                Plugin.Log($"override-test: resumed [{why}] running {Actions.ActionQueue.State?.Current?.Describe()}");
                Actions.QueueMode.Enabled = qmWas;
                break;
            }
            case var rf when rf.StartsWith("run-first:"):
            {
                // run-first:<ns>:<entity>:<ns>:<id>:<stop 0|1|2>:<value>
                var p = rf.Split(':');
                Actions.ActionQueue.RunFirstNow(new Actions.QueueEntry
                {
                    Target = new Actions.ActionTarget { EntityId = p[1] + ":" + p[2], Ids = new[] { p[3] + ":" + p[4] }, Label = "" },
                    Stop = (Actions.StopKind)int.Parse(p[5]), StopValue = int.Parse(p[6]),
                });
                break;
            }
            case var qs when qs.StartsWith("queue-set:"):
                // queue-set:<index>:<stopValue>
                if (Actions.ActionQueue.State is { } qst && qs.Split(':') is { Length: 3 } qp && int.Parse(qp[1]) < qst.Entries.Count)
                { qst.Entries[int.Parse(qp[1])].StopValue = int.Parse(qp[2]); Actions.ActionQueue.Save(); }
                break;
            case var dt when dt.StartsWith("drag-test:"):
                UI.ActionsMenu.DevDrag(int.Parse(dt.Split(':')[1]), int.Parse(dt.Split(':')[2]), dt.Split(':').Length > 3 ? double.Parse(dt.Split(':')[3]) : 0);
                break;
            case var uc when uc.StartsWith("upgrades-costing:"):
            {
                string costId = uc.Substring("upgrades-costing:".Length);
                var g2 = TimeBank.TimeBank.Game!;
                int shown = 0;
                foreach (var up in g2.Data.GetDataRegistryOutput<GamesByMalcs.Melvor2.Components.ItemUpgrading.ItemUpgrade>().GetAllObjects())
                {
                    if (!up.Costs.Items.Any(c => c.Item.Id == costId) || shown++ > 12) continue;
                    Plugin.Log($"upgrade-costing: -> {up.UpgradedItem.Id} roots [{string.Join(",", up.RootItems.Select(r => r.Id + "(" + Features.ItemRules.InBank(r) + ")"))}] costs " +
                               string.Join(", ", up.Costs.Items.Select(c => $"{c.Quantity} {c.Item.Id}")));
                }
                break;
            }
            case var pq when pq.StartsWith("press-queue-cost:"):
            {
                string itemId = pq.Substring("press-queue-cost:".Length);
                foreach (var view in UI.NodeSearch.All<GamesByMalcs.Melvor2.Core.CostTracking.ItemCostTrackerView>())
                {
                    var tr = HarmonyLib.Traverse.Create(view).Property("Tracker").GetValue<GamesByMalcs.Melvor2.Core.CostTracking.ItemCostTracker>();
                    if (tr?.Item.Id != itemId || view.FindChild("Melvor2TweaksQueueCost", true, false) is not Godot.Button qb || !qb.Visible) continue;
                    int before = Actions.ActionQueue.State?.Entries.Count ?? 0;
                    qb.EmitSignal(Godot.BaseButton.SignalName.Pressed);
                    var added = Actions.ActionQueue.State is { } qs2 && qs2.Entries.Count > before ? qs2.Entries[^1] : null;
                    Plugin.Log("press-queue-cost: added " + (added?.Describe() ?? "nothing"));
                    if (added != null) { Actions.ActionQueue.State!.Entries.Remove(added); Actions.ActionQueue.Save(); Plugin.Log("press-queue-cost: removed the test entry again"); }
                    break;
                }
                break;
            }
            case var qa when qa.StartsWith("queue-add:"):
            {
                // queue-add:<ns>:<entity>:<ns>:<id>:<stop 0|1|2>:<value>:<label>
                var p = qa.Split(':', 8);
                Actions.ActionQueue.Add(new Actions.QueueEntry
                {
                    Target = new Actions.ActionTarget { EntityId = p[1] + ":" + p[2], Ids = new[] { p[3] + ":" + p[4] }, Label = p.Length > 7 ? p[7] : "" },
                    Stop = (Actions.StopKind)int.Parse(p[5]), StopValue = int.Parse(p[6]),
                });
                break;
            }
            case var pr when pr.StartsWith("producer:"):
                if (TimeBank.TimeBank.Game is { } pg && pg.Data.TryGetObject<GamesByMalcs.Melvor2.Data.Items.Item>(pr.Substring(9), out var pit))
                    Plugin.Log($"producer {pit.Id}: {(Actions.Gather.Producer(pg, pit) is { } t ? t.EntityId + " / " + string.Join(",", t.Ids) : "none")}");
                break;
            case var qr when qr.StartsWith("queue-remove:"):
            {
                // queue-remove:<label>|<stopValue> - removes the last entry matching exactly
                var parts = qr.Substring("queue-remove:".Length).Split('|');
                if (Actions.ActionQueue.State is { } rs)
                {
                    int i = rs.Entries.FindLastIndex(e => e.Target.Label == parts[0] && e.StopValue == int.Parse(parts[1]));
                    if (i >= 0) { Plugin.Log("queue-remove: " + rs.Entries[i].Describe()); Actions.ActionQueue.RemoveAt(i); }
                    else Plugin.Log("queue-remove: no match");
                }
                break;
            }
            case "upgrade-list":
            {
                // Bank items with an upgrade, its costs, and whether an unlocked action makes each cost item.
                var bank = Features.ItemRules.Bank;
                var g = TimeBank.TimeBank.Game;
                if (bank == null || g == null) break;
                foreach (var ust in bank.Inventory.Stacks.ToList())
                {
                    if (!bank.ItemUpgrading.ItemHasUpgrade(ust.Item)) continue;
                    foreach (var up in bank.ItemUpgrading.GetUpgradesForItem(ust.Item))
                        Plugin.Log($"upgrade {ust.Item.Id} -> {up.UpgradedItem.Id}: " + string.Join(", ", up.Costs.Items.Select(c =>
                            $"{c.Quantity} {c.Item.Id} (have {Features.ItemRules.InBank(c.Item)}, made by {Actions.Gather.Producer(g, c.Item)?.Label ?? "nothing unlocked"})")));
                }
                break;
            }
            case "tick-rate":
                Plugin.Log($"tick-rate: {GamesByMalcs.Melvor2.Core.Constants.TicksPerSecond}/s, interval {GamesByMalcs.Melvor2.Core.Constants.TickInterval} ms");
                break;
            case "queue-eta":
                if (TimeBank.TimeBank.Game is { } etaGame)
                {
                    var (cur, list, total) = Actions.QueueEta.All(etaGame);
                    Plugin.Log($"queue-eta: progress {Actions.ActionQueue.ItemProgress}; current {cur}; entries [{string.Join(", ", list)}]; total {total}");
                }
                break;
            case "log-dump":
                foreach (var (k, e) in Actions.ActionLog.ForSave ?? new())
                    Plugin.Log($"log: {k} '{e.Label}' actions {e.Actions} ms {e.GameMs:N0} xp {e.Xp:N0} products {e.Products} items {e.Items.Count}");
                break;
            case "cat-list":
                foreach (var cat in Features.ItemRules.Categories())
                    Plugin.Log($"category {cat.Key} '{cat.Name}': {TimeBank.TimeBank.Game?.Data.GetDataRegistryOutput<GamesByMalcs.Melvor2.Data.Items.Item>().GetAllObjects().Count(cat.Matches)} items");
                break;
            case "queue-start":
                Plugin.Log("queue-start: [" + Actions.ActionQueue.StartIfIdle() + "] running " + (Actions.ActionQueue.RunningAction?.Entity.Id ?? "none"));
                break;
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
