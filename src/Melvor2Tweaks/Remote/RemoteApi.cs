using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Actions;
using GamesByMalcs.Melvor2.Components.CombatAreas;
using GamesByMalcs.Melvor2.Components.CraftingAction;
using GamesByMalcs.Melvor2.Components.Experience;
using GamesByMalcs.Melvor2.Conditions;
using GamesByMalcs.Melvor2.Data;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Agility;
using GamesByMalcs.Melvor2.Entities.Astrology;
using GamesByMalcs.Melvor2.Entities.Combat;
using GamesByMalcs.Melvor2.Entities.Fishing;
using GamesByMalcs.Melvor2.Entities.Hunting;
using GamesByMalcs.Melvor2.Entities.Mining;
using GamesByMalcs.Melvor2.Entities.Thieving;
using GamesByMalcs.Melvor2.Entities.Woodcutting;
using HarmonyLib;
using Melvor2Tweaks.Actions;
using Melvor2Tweaks.Features;

namespace Melvor2Tweaks.Remote;

/// <summary>
/// The phone app's JSON API. Every call runs on the main thread. Reads: state, skill (targets of one skill),
/// options (stop conditions for a target), bank, droplog. Writes: action (start now / queue), stop, queue
/// (edit), skip (time bank), speed, sell.
/// </summary>
internal static class RemoteApi
{
    internal static Task<Response> Handle(Request r)
    {
        string route = r.Path["/api/".Length..];
        JsonElement body = r.Method == "POST" ? r.Json() : default;
        return RemoteServer.OnMain(() =>
        {
            var game = TimeBank.TimeBank.Game;
            if (game == null) return Response.Error("No save is loaded in the game right now.", 503);
            return (r.Method, route) switch
            {
                ("GET", "state") => Response.Json(State(game)),
                ("GET", "skill") => Skill(game, r.Q("id")),
                ("GET", "options") => Options(game, r.Q("entity"), r.Q("ids").Split(',', StringSplitOptions.RemoveEmptyEntries), r.Q("label")),
                ("GET", "bank") => Response.Json(Bank(game)),
                ("GET", "droplog") => Response.Json(DropLog(game)),
                ("POST", "action") => StartOrQueue(game, body),
                ("POST", "stop") => Stop(),
                ("POST", "queue") => Queue(body),
                ("POST", "skip") => Skip(body),
                ("POST", "speed") => Speed(body),
                ("POST", "sell") => Sell(game, body),
                ("POST", "droplog-reset") => ResetLog(body),
                _ => Response.Error("Unknown call", 404),
            };
        });
    }

    // ---- reads -------------------------------------------------------------------------------------
    private static object State(Game game)
    {
        var running = ActionQueue.RunningAction;
        var target = running is { IsStarted: true } ? SkillActions.Capture(running) : null;
        var state = ActionQueue.State;
        var rule = state?.Current;
        string currentEta = "", total = "";
        List<string> etas = new();
        try { (currentEta, etas, total) = QueueEta.All(game); } catch { /* estimates are best effort */ }

        string ruleText = rule == null || target == null || !rule.Target.SameAs(target) ? "Runs until it stops" : rule.Stop switch
        {
            StopKind.Level => $"Stops at level {rule.StopValue} (now {ActionQueue.CurrentLevel?.ToString() ?? "?"})",
            StopKind.Items => $"Stops after {rule.StopValue:N0} items ({ActionQueue.ItemProgress:N0} so far)",
            StopKind.Nodes => "Stops when every node can be unlocked (" + AstrologyGoal.Progress(game, rule.Target) + ")",
            _ => "Runs until it stops",
        };

        var bank = ItemRules.Bank;
        return new
        {
            version = Plugin.Version,
            processing = TimeBank.TimeBank.IsProcessing,
            speed = Settings.Current.Speed,
            speedPresets = Settings.Current.SpeedPresets,
            timeBank = new { ms = TimeBank.TimeBankStore.BankedMs, text = TimeBank.TimeBank.Format(TimeBank.TimeBankStore.BankedMs) },
            running = target == null ? null : new
            {
                label = target.Label,
                entity = target.EntityId,
                ids = target.Ids,
                icon = EntityIcon(game, target.EntityId),
                rule = ruleText,
                eta = currentEta,
                // the phone animates the action bar from these: game ms per action, how far along now
                interval = running?.ActionTimer is { IsRunning: true } timer ? timer.WaitTime : (double?)null,
                progress = running?.ActionTimer is { IsRunning: true } t2 ? t2.Progress : (double?)null,
                xpHour = ActionLog.MsPerXp(ActionLog.Get(target)) is double msXp ? 3_600_000 / msXp : (double?)null,
                itemsHour = ActionLog.MsPerItem(ActionLog.Get(target), null) is double msItem ? 3_600_000 / msItem : (double?)null,
                actions = ActionLog.Get(target)?.Actions,
                level = running?.Entity.Components.Values.OfType<ExperienceComponent>().FirstOrDefault() is { } xp ? Xp(xp) : null,
            },
            queue = new
            {
                enabled = state?.Enabled ?? false,
                loop = state?.Loop ?? false,
                modeOn = QueueMode.Enabled,
                total,
                entries = (state?.Entries ?? new()).Select((e, i) => new
                {
                    label = e.Describe(),
                    eta = i < etas.Count ? etas[i] : "",
                    icon = EntityIcon(game, e.Target.EntityId),
                    auto = e.Auto,
                }).ToList(),
                presets = ActionQueue.Presets.Keys.ToList(),
            },
            skills = Skills(game),
            bank = bank == null ? null : new { used = bank.Inventory.StackCount, max = bank.Inventory.MaxStacks },
            currencies = bank == null ? null : bank.Wallet.Stacks.Where(kv => kv.Value.Quantity > 0).Select(kv => new
            {
                name = kv.Key.DisplayName.Text, quantity = kv.Value.Quantity, icon = Icons.Id(kv.Key),
            }).ToList(),
        };
    }

    private static object Xp(ExperienceComponent xp)
    {
        double from = xp.Table.GetXpForLevel(xp.Level), to = xp.Table.GetXpForLevel(xp.Level + 1);
        double pct = to > from ? Math.Clamp((xp.Xp - from) / (to - from), 0, 1) : 1;
        return new { level = xp.Level, cap = xp.LevelCap, xp = Math.Floor(xp.Xp), next = to, pct };
    }

    private static List<object> Skills(Game game)
    {
        var list = new List<object>();
        var running = ActionQueue.RunningAction;
        foreach (var e in game.Entities.GetAllObjects())
        {
            var xp = e.Components.Values.OfType<ExperienceComponent>().FirstOrDefault();
            bool hasAction = e.Components.TryGetValue("Action", out var a) && a is ActionComponent;
            if (xp == null && !hasAction) continue; // Combat has an action but no XP of its own
            list.Add(new
            {
                id = e.Id, name = e.DisplayName.Text, icon = Icons.Id(e), startable = hasAction,
                active = running != null && ReferenceEquals(running.Entity, e) && running.IsStarted,
                xp = xp == null ? null : Xp(xp),
            });
        }
        return list;
    }

    private static string? EntityIcon(Game game, string entityId) =>
        game.Entities.TryGetObjectById(entityId, out GameEntity? e) && e != null ? Icons.Id(e) : null;

    /// <summary>A thing a skill can do, as the app lists it.</summary>
    private sealed record Target(string[] Ids, string Name, string? Icon, bool Unlocked, string? Needs, string Group, string? Detail = null)
    {
        /// <summary>The skill level it needs (for ordering like the game: lowest first).</summary>
        public int Level { get; init; }
    }

    private static int SelfLevel(ConditionGroup? requirements, ExperienceComponent? xp)
    {
        if (requirements == null || xp == null) return 0;
        foreach (var c in requirements.Conditions)
            if (c is LevelCondition lc && ReferenceEquals(lc.Component, xp)) return lc.Level;
        return 0;
    }

    private static ConditionGroup? RequirementsOf(object? o) =>
        o == null ? null : Traverse.Create(o).Property("Requirements").GetValue() as ConditionGroup
                           ?? Traverse.Create(o).Property("UnlockRequirements").GetValue() as ConditionGroup;

    private static string? Needs(ConditionGroup? requirements)
    {
        if (requirements == null || requirements.AreMet) return null;
        var unmet = requirements.Conditions.Where(c => !c.IsMet).Select(c => Gather.PlainText(c.GetDescription(false, false))).Where(s => s.Length > 0);
        return string.Join(", ", unmet);
    }

    private static string IdOf(object? o) => (o as INamespacedObject)?.Id ?? "";

    private static Response Skill(Game game, string id)
    {
        if (!game.Entities.TryGetObjectById(id, out GameEntity? entity) || entity == null) return Response.Error("Unknown skill", 404);
        var targets = new List<Target>();
        var skillXp = entity.Components.Values.OfType<ExperienceComponent>().FirstOrDefault();
        if (entity.Components.TryGetValue("Action", out var c) && c is ActionComponent action)
        {
            switch (action)
            {
                case WoodcuttingActionComponent wc:
                    foreach (var r in wc.Trees.Keys)
                        targets.Add(new Target(new[] { r.Id }, r.DisplayName.Text, Icons.Id(r.Product.Item), r.Requirements.AreMet, Needs(r.Requirements), "Trees") { Level = SelfLevel(r.Requirements, skillXp) });
                    break;
                case MiningActionComponent m:
                    foreach (var r in m.Rocks.Keys)
                        targets.Add(new Target(new[] { r.Id }, r.DisplayName.Text, Icons.Id(r.Product.Item), r.Requirements.AreMet, Needs(r.Requirements), "Rocks") { Level = SelfLevel(r.Requirements, skillXp) });
                    break;
                case FishingActionComponent f:
                    foreach (var r in f.Recipes.Keys)
                        targets.Add(new Target(new[] { r.Id }, r.DisplayName.Text, Icons.Id(r) ?? Icons.Id(r.RandomProducts.Items.FirstOrDefault()?.Item),
                            r.Requirements.AreMet, Needs(r.Requirements), "Fishing spots",
                            string.Join(", ", r.RandomProducts.Items.Select(p => p.Item.DisplayName.Plural).Distinct().Take(4))) { Level = SelfLevel(r.Requirements, skillXp) });
                    break;
                case ThievingActionComponent t:
                    foreach (DictionaryEntry kv in (IDictionary)t.ActiveRecipes)
                    {
                        var recipe = Traverse.Create(kv.Value).Property("Recipe").GetValue() ?? kv.Key;
                        bool can = kv.Value is ActiveThievingRecipe atr && t.CanThieveRecipe(atr);
                        targets.Add(new Target(new[] { IdOf(kv.Key) }, SkillActions.Name(recipe), Icons.Id(recipe), can,
                            can ? null : Needs(RequirementsOf(recipe)) ?? "unavailable", "Targets") { Level = SelfLevel(RequirementsOf(recipe), skillXp) });
                    }
                    break;
                case AgilityActionComponent ag:
                    foreach (DictionaryEntry kv in (IDictionary)ag.Courses)
                    {
                        if (kv.Value is not ActiveAgilityCourse course) continue;
                        foreach (DictionaryEntry pk in (IDictionary)course.ActivePaths)
                            targets.Add(new Target(new[] { IdOf(kv.Key), IdOf(pk.Key) }, SkillActions.Name(pk.Key), Icons.Id(kv.Key), true, null, SkillActions.Name(kv.Key)));
                    }
                    break;
                case AstrologyActionComponent s:
                    foreach (var (con, active) in s.ActiveConstellations)
                        targets.Add(new Target(new[] { con.Id }, con.DisplayName.Text, Icons.Id(con), con.Requirements.AreMet, Needs(con.Requirements), "Constellations",
                            con.Requirements.AreMet ? AstrologyGoal.Progress(game, new ActionTarget { EntityId = entity.Id, Ids = new[] { con.Id } }) : null)
                            { Level = SelfLevel(con.Requirements, skillXp) });
                    break;
                case HuntingActionComponent h:
                    foreach (DictionaryEntry kv in (IDictionary)h.Animals)
                    {
                        bool can = kv.Value is ActiveHuntingAnimal ha && h.CanTrackAnimal(ha);
                        var loc = Traverse.Create(kv.Value).Property("Location").GetValue();
                        targets.Add(new Target(new[] { IdOf(kv.Key) }, SkillActions.Name(kv.Key), Icons.Id(kv.Key), can,
                            can ? null : Needs(RequirementsOf(kv.Key)) ?? "can't hunt yet",
                            loc == null ? "Animals" : SkillActions.Name(loc)) { Level = SelfLevel(RequirementsOf(kv.Key), skillXp) });
                    }
                    break;
                case CombatActionComponent:
                    foreach (var area in game.Data.GetDataRegistryOutput<CombatArea>().GetAllObjects())
                    {
                        if (area.DisplayRequirements != null && !area.DisplayRequirements.AreMet) continue;
                        foreach (var enemy in area.Enemies)
                            targets.Add(new Target(new[] { area.Id, enemy.Id }, SkillActions.Name(enemy), Icons.Id(enemy), area.UnlockRequirements.AreMet,
                                Needs(area.UnlockRequirements), area.DisplayName.Text));
                    }
                    foreach (var dungeon in game.Data.GetDataRegistryOutput<Dungeon>().GetAllObjects())
                    {
                        var display = Traverse.Create(dungeon).Property("DisplayRequirements").GetValue<ConditionGroup>();
                        if (display != null && !display.AreMet) continue;
                        targets.Add(new Target(new[] { dungeon.Id }, SkillActions.Name(dungeon), Icons.Id(dungeon), dungeon.UnlockRequirements.AreMet,
                            Needs(dungeon.UnlockRequirements), "Dungeons"));
                    }
                    break;
                default:
                    if (Traverse.Create(action).Property("ActiveRecipes").GetValue() is IDictionary recipes)
                        foreach (var r in recipes.Values.OfType<IActiveCraftingRecipe>())
                        {
                            var (product, _) = Gather.PrimaryProduct(r);
                            var t = new ActionTarget { EntityId = entity.Id, Ids = new[] { r.Recipe.Id } };
                            var costs = Gather.Costs(game, t) ?? new();
                            string detail = string.Join(", ", costs.Select(cst => $"{cst.qty} {cst.item.DisplayName.Plural} ({ItemRules.InBank(cst.item):N0})"));
                            int? crafts = Gather.CraftsAffordable(game, t);
                            if (crafts is int n && costs.Count > 0) detail += n > 0 ? $" - {n:N0} crafts" : " - none on hand";
                            var category = Traverse.Create(r.Recipe).Property("Category").GetValue();
                            targets.Add(new Target(new[] { r.Recipe.Id }, r.Recipe.DisplayName.Text, Icons.Id(product) ?? Icons.Id(r.Recipe),
                                r.Recipe.Requirements.AreMet, Needs(r.Recipe.Requirements), category == null ? "Recipes" : SkillActions.Name(category), detail)
                                { Level = SelfLevel(r.Recipe.Requirements, skillXp) });
                        }
                    break;
            }
        }
        return Response.Json(new
        {
            id = entity.Id, name = entity.DisplayName.Text, icon = Icons.Id(entity), level = skillXp == null ? null : Xp(skillXp),
            groups = targets.GroupBy(t => t.Group).Select(g => new
            {
                name = g.Key,
                targets = g.OrderBy(t => t.Level).Select(t => new { ids = t.Ids, name = t.Name, icon = t.Icon, unlocked = t.Unlocked, needs = t.Needs, detail = t.Detail, level = t.Level }),
            }),
        });
    }

    /// <summary>The stop conditions for a target, as the Queue mode popup offers them.</summary>
    private static Response Options(Game game, string entityId, string[] ids, string label)
    {
        if (!game.Entities.TryGetObjectById(entityId, out GameEntity? entity) || entity == null) return Response.Error("Unknown skill", 404);
        var target = new ActionTarget { EntityId = entityId, Ids = ids, Label = label };
        var xp = entity.Components.Values.OfType<ExperienceComponent>().FirstOrDefault();
        bool astrology = AstrologyGoal.IsAstrology(game, target);
        string stopsNote = "until you stop it";
        if (Gather.CraftsAffordable(game, target) is int crafts)
            stopsNote = crafts == 0 ? "until the materials run out (you have none yet)" : $"until the materials run out ({crafts:N0} crafts' worth now)";
        object? nodes = null;
        if (astrology)
        {
            string eta = QueueEta.Describe(QueueEta.ForEntry(game, new QueueEntry { Target = target, Stop = StopKind.Nodes }, new()));
            nodes = new { ready = AstrologyGoal.Ready(game, target), progress = AstrologyGoal.Progress(game, target), eta };
        }
        return Response.Json(new
        {
            stopsNote,
            level = xp?.Level,
            cap = xp == null ? (int?)null : xp.LevelCap > 0 ? xp.LevelCap : 99,
            nextMilestone = UI.UiKit.NextMilestoneLevel(entity),
            milestones = UI.UiKit.UpcomingMilestones(entity, 5).Select(m => new { level = m.level, reason = m.reason }),
            items = !astrology,
            nodes,
        });
    }

    private static object Bank(Game game)
    {
        var bank = ItemRules.Bank;
        if (bank == null) return new { items = Array.Empty<object>() };
        return new
        {
            used = bank.Inventory.StackCount,
            max = bank.Inventory.MaxStacks,
            items = bank.Inventory.Stacks.Where(s => !s.IsPlaceholder && s.Quantity > 0).Select(s => new
            {
                id = s.Item.Id, name = s.Item.DisplayName.Singular, qty = s.Quantity, value = s.StackValue,
                each = s.Quantity > 0 ? s.StackValue / s.Quantity : 0, icon = Icons.Id(s.Item), locked = s.IsLocked,
                rule = ItemRules.Effective(s.Item) is { } rule && rule.Action != RuleAction.Off ? $"{rule.Action} (keep {rule.Keep:N0})" : null,
            }).ToList(),
        };
    }

    private static object DropLog(Game game)
    {
        var log = ActionLog.ForSave ?? new();
        return log.OrderByDescending(kv => kv.Key == ActionLog.CurrentKey).ThenByDescending(kv => kv.Value.GameMs).Select(kv => new
        {
            key = kv.Key,
            label = kv.Value.Label,
            current = kv.Key == ActionLog.CurrentKey,
            actions = kv.Value.Actions,
            time = QueueEta.FormatGame(kv.Value.GameMs),
            xp = Math.Floor(kv.Value.Xp),
            since = kv.Value.Since,
            icon = EntityIcon(game, kv.Key.Split('|')[0]),
            items = kv.Value.Items.OrderByDescending(i => i.Value).Select(i =>
            {
                game.Data.TryGetObject<Item>(i.Key, out var item);
                return new { name = item?.DisplayName.Plural ?? i.Key, qty = i.Value, icon = Icons.Id(item) };
            }).ToList(),
        }).ToList();
    }

    // ---- writes ------------------------------------------------------------------------------------
    private static string Str(JsonElement b, string name) => b.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static int Int(JsonElement b, string name) => b.TryGetProperty(name, out var v) && v.TryGetInt32(out int i) ? i : 0;
    private static double Num(JsonElement b, string name) => b.TryGetProperty(name, out var v) && v.TryGetDouble(out double d) ? d : 0;
    private static bool Bool(JsonElement b, string name) => b.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static Response StartOrQueue(Game game, JsonElement b)
    {
        var target = new ActionTarget
        {
            EntityId = Str(b, "entity"),
            Ids = b.TryGetProperty("ids", out var ids) ? ids.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : Array.Empty<string>(),
            Label = Str(b, "label"),
        };
        var kind = Enum.TryParse<StopKind>(Str(b, "stop"), true, out var k) ? k : StopKind.None;
        int value = Int(b, "value");
        if (kind == StopKind.Level && value <= 0) return Response.Error("Pick a level");
        if (kind == StopKind.Items && value <= 0) return Response.Error("Pick how many items");

        if (Str(b, "mode") == "queue")
        {
            var entry = new QueueEntry { Target = target, Stop = kind, StopValue = value };
            ActionQueue.Add(entry);
            string why = "";
            if (ActionQueue.RunningAction is not { IsStarted: true })
            {
                QueueMode.Internal = true;
                try { why = ActionQueue.StartIfIdle(); }
                finally { QueueMode.Internal = false; }
            }
            Plugin.Log("Phone: queued " + entry.Describe());
            return Response.Json(new { ok = true, message = ("Queued: " + entry.Describe() + " " + why).Trim() });
        }

        // Start now: like pressing the skill's own button with Queue mode off (an overridden queue entry goes back in the queue).
        QueueMode.Bypass = true;
        bool ok;
        string reason;
        try { ok = SkillActions.Start(game, target, out reason); }
        finally { QueueMode.Bypass = false; }
        if (!ok) return Response.Error("Couldn't start: " + reason);
        if (kind != StopKind.None) ActionQueue.SetRuleOnRunning(kind, value);
        Plugin.Log($"Phone: started {target.Label} ({kind} {value})");
        return Response.Json(new { ok = true, message = "Started " + (ActionQueue.RunningAction is { } ra ? SkillActions.Capture(ra)?.Label : target.Label) });
    }

    private static Response Stop()
    {
        if (ActionQueue.RunningAction is not { IsStarted: true } a) return Response.Json(new { ok = true, message = "Nothing is running" });
        a.TryStop(); // what the game's own Stop button does: the queue doesn't advance
        Plugin.Log("Phone: stopped the current action");
        return Response.Json(new { ok = true, message = a.IsStarted ? "The game didn't stop it yet" : "Stopped" });
    }

    private static Response Queue(JsonElement b)
    {
        string op = Str(b, "op");
        string message = "";
        switch (op)
        {
            case "remove": ActionQueue.RemoveAt(Int(b, "i")); break;
            case "move": ActionQueue.Move(Int(b, "i"), Int(b, "to")); break;
            case "bump": ActionQueue.BumpCurrent(); break;
            case "clear": ActionQueue.Clear(); break;
            case "loop": ActionQueue.SetLoop(Bool(b, "on")); break;
            case "mode": QueueMode.Enabled = Bool(b, "on"); break;
            case "enabled":
                ActionQueue.SetEnabled(Bool(b, "on"));
                if (Bool(b, "on")) message = ActionQueue.StartIfIdle();
                break;
            case "start":
                if (ActionQueue.State is { Enabled: false }) ActionQueue.SetEnabled(true);
                message = ActionQueue.StartIfIdle();
                break;
            case "load": ActionQueue.LoadPreset(Str(b, "name")); message = $"Loaded \"{Str(b, "name")}\""; break;
            case "save": message = ActionQueue.SavePreset(Str(b, "name")) ? $"Saved \"{Str(b, "name")}\"" : "Couldn't save"; break;
            case "delete": ActionQueue.DeletePreset(Str(b, "name")); break;
            default: return Response.Error("Unknown queue change");
        }
        return Response.Json(new { ok = true, message });
    }

    private static Response Skip(JsonElement b)
    {
        double minutes = Num(b, "minutes");
        if (minutes <= 0) return Response.Error("Pick how long to skip");
        double spent = TimeBank.TimeBank.Spend(minutes * 60_000, out string reason);
        return spent > 0
            ? Response.Json(new { ok = true, message = $"Skipping {TimeBank.TimeBank.Format(spent)} of banked time at {TimeBank.TimeBank.SkipSpeed:0.##}x" })
            : Response.Error(reason);
    }

    private static Response Speed(JsonElement b)
    {
        double v = Num(b, "value");
        if (v < 1 || v > 100) return Response.Error("Speed must be 1-100");
        Settings.Current.Speed = v;
        Settings.Save();
        return Response.Json(new { ok = true, message = $"Speed {v:0.##}x" });
    }

    private static Response Sell(Game game, JsonElement b)
    {
        if (ItemRules.Bank is not { } bank || !game.Data.TryGetObject<Item>(Str(b, "id"), out var item)) return Response.Error("Unknown item");
        int have = bank.Inventory.GetQuantity(item);
        int qty = Math.Min(Int(b, "qty"), have);
        if (qty <= 0) return Response.Error("Nothing to sell");
        if (bank.Inventory.Stacks.Any(s => s.Item == item && s.IsLocked)) return Response.Error("That item is locked in the bank");
        bank.ItemSales.SellItem(item, bank.ItemSales.GetItemStats(item), qty);
        Plugin.Log($"Phone: sold {qty} x {item.Id}");
        return Response.Json(new { ok = true, message = $"Sold {qty:N0} {(qty == 1 ? item.DisplayName.Singular : item.DisplayName.Plural)}" });
    }

    private static Response ResetLog(JsonElement b)
    {
        ActionLog.Reset(Str(b, "key"));
        return Response.Json(new { ok = true });
    }
}
