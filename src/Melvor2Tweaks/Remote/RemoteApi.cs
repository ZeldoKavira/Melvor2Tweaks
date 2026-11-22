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

    private static int Cap(ExperienceComponent xp) => xp.LevelCap > 0 ? xp.LevelCap : 99;

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
    private sealed class Target
    {
        public string[] Ids = Array.Empty<string>();
        public string Name = "";
        public string? Icon;
        public bool Unlocked;
        public string? Needs;
        public string Group = "";
        /// <summary>The skill level it needs (for ordering like the game: lowest first).</summary>
        public int Level;
        /// <summary>The game's live object for it (has Stats: interval, XP).</summary>
        public object? Active;
        public List<Item> Products = new();
        public string? Note;
    }

    /// <summary>Game ms per action and XP per action from a target's live Stats (every skill names them the same).</summary>
    private static (double? ms, double? xp) StatsOf(object? active)
    {
        var stats = active == null ? null : Traverse.Create(active).Property("Stats").GetValue();
        if (stats == null) return (null, null);
        var t = Traverse.Create(stats);
        double? ms = t.Property("Interval").Property("Value").GetValue() is double i && i > 0 ? i : null;
        double? xp = t.Property("Experience").Property("XpToAdd").Property("Value").GetValue() is double x && x > 0 ? x : null;
        // Trees: the interval is per hit and the XP per log; a log takes MaxHealth hits.
        if (ms != null && t.Property("MaxHealth").Property("Value").GetValue() is int hits && hits > 1) ms *= hits;
        return (ms, xp);
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
        string? note = null;
        if (entity.Components.TryGetValue("Action", out var c) && c is ActionComponent action)
        {
            switch (action)
            {
                case WoodcuttingActionComponent wc:
                    foreach (var (r, tree) in wc.Trees)
                        targets.Add(new Target { Ids = new[] { r.Id }, Name = r.DisplayName.Text, Icon = Icons.Id(r.Product.Item), Unlocked = r.Requirements.AreMet,
                            Needs = Needs(r.Requirements), Group = "Trees", Level = SelfLevel(r.Requirements, skillXp), Active = tree, Products = { r.Product.Item } });
                    break;
                case MiningActionComponent m:
                    foreach (var (r, rock) in m.Rocks)
                        targets.Add(new Target { Ids = new[] { r.Id }, Name = r.DisplayName.Text, Icon = Icons.Id(r.Product.Item), Unlocked = r.Requirements.AreMet,
                            Needs = Needs(r.Requirements), Group = "Rocks", Level = SelfLevel(r.Requirements, skillXp), Active = rock, Products = { r.Product.Item } });
                    break;
                case FishingActionComponent f:
                    foreach (var (r, active) in f.Recipes)
                    {
                        var t = new Target { Ids = new[] { r.Id }, Name = r.DisplayName.Text, Icon = Icons.Id(r) ?? Icons.Id(r.RandomProducts.Items.FirstOrDefault()?.Item),
                            Unlocked = r.Requirements.AreMet, Needs = Needs(r.Requirements), Group = "Fishing spots", Level = SelfLevel(r.Requirements, skillXp), Active = active };
                        t.Products.AddRange(r.RandomProducts.Items.Select(p => p.Item).Distinct());
                        targets.Add(t);
                    }
                    break;
                case ThievingActionComponent th:
                    foreach (DictionaryEntry kv in (IDictionary)th.ActiveRecipes)
                    {
                        var recipe = Traverse.Create(kv.Value).Property("Recipe").GetValue() ?? kv.Key;
                        var req = RequirementsOf(recipe);
                        bool can = kv.Value is ActiveThievingRecipe atr && th.CanThieveRecipe(atr);
                        targets.Add(new Target { Ids = new[] { IdOf(kv.Key) }, Name = SkillActions.Name(recipe), Icon = Icons.Id(recipe), Unlocked = req?.AreMet ?? true,
                            Needs = Needs(req), Group = "Targets", Level = SelfLevel(req, skillXp), Active = kv.Value,
                            Note = can || !(req?.AreMet ?? true) ? null : "The game can't start this right now" });
                    }
                    break;
                case AgilityActionComponent ag:
                    foreach (DictionaryEntry kv in (IDictionary)ag.Courses)
                    {
                        if (kv.Value is not ActiveAgilityCourse course) continue;
                        foreach (DictionaryEntry pk in (IDictionary)course.ActivePaths)
                            targets.Add(new Target { Ids = new[] { IdOf(kv.Key), IdOf(pk.Key) }, Name = SkillActions.Name(pk.Key), Icon = Icons.Id(kv.Key), Unlocked = true,
                                Group = SkillActions.Name(kv.Key), Active = pk.Value });
                    }
                    break;
                case AstrologyActionComponent s:
                    foreach (var (con, active) in s.ActiveConstellations)
                        targets.Add(new Target { Ids = new[] { con.Id }, Name = con.DisplayName.Text, Icon = Icons.Id(con), Unlocked = con.Requirements.AreMet,
                            Needs = Needs(con.Requirements), Group = "Constellations", Level = SelfLevel(con.Requirements, skillXp), Active = active,
                            Note = con.Requirements.AreMet ? AstrologyGoal.Progress(game, new ActionTarget { EntityId = entity.Id, Ids = new[] { con.Id } }) : null });
                    break;
                case HuntingActionComponent h:
                    bool anyTrackable = false;
                    foreach (DictionaryEntry kv in (IDictionary)h.Animals)
                    {
                        var req = RequirementsOf(kv.Key);
                        bool can = kv.Value is ActiveHuntingAnimal ha && h.CanTrackAnimal(ha);
                        anyTrackable |= can;
                        var loc = Traverse.Create(kv.Value).Property("Location").GetValue();
                        targets.Add(new Target { Ids = new[] { IdOf(kv.Key) }, Name = SkillActions.Name(kv.Key), Icon = Icons.Id(kv.Key), Unlocked = req?.AreMet ?? true,
                            Needs = Needs(req), Group = loc == null ? "Animals" : SkillActions.Name(loc), Level = SelfLevel(req, skillXp), Active = kv.Value });
                    }
                    if (!anyTrackable) note = "The game can't track any animal right now: pick a trap on the Hunting page first. You can still queue them.";
                    break;
                case CombatActionComponent:
                    foreach (var area in game.Data.GetDataRegistryOutput<CombatArea>().GetAllObjects())
                    {
                        if (area.DisplayRequirements != null && !area.DisplayRequirements.AreMet) continue;
                        foreach (var enemy in area.Enemies)
                            targets.Add(new Target { Ids = new[] { area.Id, enemy.Id }, Name = SkillActions.Name(enemy), Icon = Icons.Id(enemy),
                                Unlocked = area.UnlockRequirements.AreMet, Needs = Needs(area.UnlockRequirements), Group = area.DisplayName.Text,
                                Note = Traverse.Create(enemy).Property("CombatLevel").GetValue() is int cl ? $"Combat level {cl}" : null });
                    }
                    foreach (var dungeon in game.Data.GetDataRegistryOutput<Dungeon>().GetAllObjects())
                    {
                        var display = Traverse.Create(dungeon).Property("DisplayRequirements").GetValue<ConditionGroup>();
                        if (display != null && !display.AreMet) continue;
                        targets.Add(new Target { Ids = new[] { dungeon.Id }, Name = SkillActions.Name(dungeon), Icon = Icons.Id(dungeon),
                            Unlocked = dungeon.UnlockRequirements.AreMet, Needs = Needs(dungeon.UnlockRequirements), Group = "Dungeons" });
                    }
                    break;
                default:
                    if (Traverse.Create(action).Property("ActiveRecipes").GetValue() is IDictionary recipes)
                        foreach (var r in recipes.Values.OfType<IActiveCraftingRecipe>())
                        {
                            var (product, _) = Gather.PrimaryProduct(r);
                            var category = Traverse.Create(r.Recipe).Property("Category").GetValue();
                            var t = new Target { Ids = new[] { r.Recipe.Id }, Name = r.Recipe.DisplayName.Text, Icon = Icons.Id(product) ?? Icons.Id(r.Recipe),
                                Unlocked = r.Recipe.Requirements.AreMet, Needs = Needs(r.Recipe.Requirements),
                                Group = category == null ? "Recipes" : SkillActions.Name(category), Level = SelfLevel(r.Recipe.Requirements, skillXp), Active = r };
                            if (product != null) t.Products.Add(product);
                            targets.Add(t);
                        }
                    break;
            }
        }

        var running = ActionQueue.RunningAction is { IsStarted: true } ra ? SkillActions.Capture(ra) : null;
        var queue = ActionQueue.State?.Entries ?? new();
        object Describe(Target t)
        {
            var at = new ActionTarget { EntityId = entity.Id, Ids = t.Ids };
            var (ms, xp) = StatsOf(t.Active);
            var log = ActionLog.Get(at);
            // Rates: the logged all-time rate once it has been done, else the game's own numbers (an estimate).
            double? msPerXp = ActionLog.MsPerXp(log) ?? (ms is double m && xp is double x && x > 0 ? m / x : null);
            double? msPerAction = ActionLog.MsPerAction(log) ?? ms;
            double? msPerItem = ActionLog.MsPerItem(log, null) ?? msPerAction;
            double? xpHour = msPerXp is double px ? 3_600_000 / px : null;
            var costs = Gather.Costs(game, at);
            int? crafts = costs is { Count: > 0 } ? Gather.CraftsAffordable(game, at) : null;
            double? toLevelMs = skillXp != null && skillXp.Level < Cap(skillXp) && msPerXp is double px2
                ? Math.Max(0, skillXp.Table.GetXpForLevel(skillXp.Level + 1) - skillXp.Xp) * px2 : null;
            double? matsMs = crafts is int n && n > 0 && msPerAction is double pa ? n * pa : null;
            return new
            {
                ids = t.Ids, name = t.Name, icon = t.Icon, unlocked = t.Unlocked, needs = t.Needs, level = t.Level, note = t.Note,
                secs = ms / 1000, xp, xpHour, xpHourKnown = log?.Xp > 0,
                msPerXp, msPerItem, toLevelMs, matsMs,
                done = log?.Actions,
                running = running != null && running.EntityId == entity.Id && running.Ids.SequenceEqual(t.Ids),
                queued = queue.Count(e => e.Target.EntityId == entity.Id && e.Target.Ids.SequenceEqual(t.Ids)),
                products = t.Products.Take(6).Select(p => new { name = p.DisplayName.Plural, icon = Icons.Id(p), owned = ItemRules.InBank(p) }),
                costs = costs?.Select(cst => new { name = cst.item.DisplayName.Plural, icon = Icons.Id(cst.item), qty = cst.qty, have = ItemRules.InBank(cst.item) }),
                crafts,
            };
        }

        return Response.Json(new
        {
            id = entity.Id, name = entity.DisplayName.Text, icon = Icons.Id(entity), level = skillXp == null ? null : Xp(skillXp), note,
            groups = targets.GroupBy(t => t.Group).Select(g => new { name = g.Key, targets = g.OrderBy(t => t.Level).Select(Describe).ToList() }).ToList(),
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
            cap = xp == null ? (int?)null : Cap(xp),
            // XP still needed for each level above the current one: the phone times them with the target's rate
            xpTo = xp == null ? null : Enumerable.Range(xp.Level + 1, Math.Max(0, Cap(xp) - xp.Level))
                .ToDictionary(l => l.ToString(), l => Math.Max(0, xp.Table.GetXpForLevel(l) - xp.Xp)),
            replaces = ActionQueue.RunningAction is { IsStarted: true } ra && SkillActions.Capture(ra) is { } rt && !rt.SameAs(target) ? rt.Label : null,
            queueLength = ActionQueue.State?.Entries.Count ?? 0,
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
