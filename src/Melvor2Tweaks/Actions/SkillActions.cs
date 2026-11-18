using System;
using System.Collections;
using System.Linq;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Actions;
using GamesByMalcs.Melvor2.Components.CombatAreas;
using GamesByMalcs.Melvor2.Data;
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

namespace Melvor2Tweaks.Actions;

/// <summary>What an action is doing, as data ids that survive a save/load.</summary>
public sealed class ActionTarget
{
    public string EntityId { get; set; } = "";
    /// <summary>Target ids in skill-specific order (e.g. course + path, area + enemy).</summary>
    public string[] Ids { get; set; } = Array.Empty<string>();
    public string Label { get; set; } = "";

    public bool SameAs(ActionTarget? other) =>
        other != null && other.EntityId == EntityId && other.Ids.SequenceEqual(Ids);
}

/// <summary>
/// Per-skill capture and restart of actions, following the exact calls the game's own UI makes.
/// Every skill's select/start call is a toggle (same target again = stop), so Start checks first.
/// </summary>
internal static class SkillActions
{
    internal static string Name(object? o)
    {
        if (o == null) return "?";
        if (o is INamedObject named) return named.DisplayName.Text;
        var dn = Traverse.Create(o).Property("DisplayName").GetValue();
        return dn switch
        {
            GamesByMalcs.Melvor2.Localization.ILocalizedString ls => ls.Text,
            _ => o.ToString() ?? "?",
        };
    }

    private static string Id(object? o) => (o as INamespacedObject)?.Id ?? "";

    internal static ActionTarget? Capture(ActionComponent action)
    {
        var entity = action.Entity;
        string[]? ids = action switch
        {
            WoodcuttingActionComponent wc => wc.CuttingTree == null ? null : new[] { Id(wc.CuttingTree.Recipe) },
            MiningActionComponent m => m.CurrentRock == null ? null : new[] { Id(m.CurrentRock.Recipe) },
            FishingActionComponent f => f.CurrentRecipe == null ? null : new[] { Id(f.CurrentRecipe.Recipe) },
            ThievingActionComponent t => t.CurrentRecipe == null ? null : new[] { Id(t.CurrentRecipe.Recipe) },
            AgilityActionComponent a => a.CurrentPath == null ? null : new[] { Id(a.CurrentPath.Course.Course), Id(a.CurrentPath.Path) },
            AstrologyActionComponent s => s.StudiedConstellation == null ? null : new[] { Id(s.StudiedConstellation.Constellation) },
            HuntingActionComponent h => h.CurrentAnimal == null ? null : new[] { Id(h.CurrentAnimal.Animal) },
            CombatActionComponent c => CaptureCombat(c),
            _ => CaptureCrafting(action),
        };
        if (ids == null || ids.Any(string.IsNullOrEmpty)) return null;
        return new ActionTarget
        {
            EntityId = Id(entity),
            Ids = ids,
            Label = $"{entity.DisplayName.Text}: {Name(action.CurrentDescription)}",
        };
    }

    private static string[]? CaptureCrafting(ActionComponent action)
    {
        var recipe = Traverse.Create(action).Property("CreatingRecipe").Property("Recipe").GetValue();
        return recipe == null ? null : new[] { Id(recipe) };
    }

    private static string[]? CaptureCombat(CombatActionComponent combat)
    {
        var areas = Traverse.Create(combat).Property("Areas").GetValue<CombatAreasComponent>();
        var area = areas?.CurrentArea?.Area;
        if (area == null) return null;
        var enemy = areas!.SelectedEnemy;
        return enemy == null ? new[] { Id(area) } : new[] { Id(area), Id(enemy) };
    }

    /// <summary>Starts the target. Returns false (with a reason) if it can't be started right now.</summary>
    internal static bool Start(Game game, ActionTarget target, out string reason)
    {
        reason = "";
        if (!game.Entities.TryGetObjectById(target.EntityId, out GameEntity? entity) || entity == null)
        { reason = "skill not found"; return false; }
        if (!entity.Components.TryGetValue("Action", out var component) || component is not ActionComponent action)
        { reason = "no action"; return false; }

        // Already doing exactly this: nothing to do (calling the toggle would stop it).
        if (action.IsStarted && target.SameAs(Capture(action))) return true;

        try
        {
            switch (action)
            {
                case WoodcuttingActionComponent wc:
                {
                    var tree = Lookup(wc.Trees, target.Ids[0]) as WoodcuttingTree;
                    if (tree == null || !tree.Recipe.Requirements.AreMet) { reason = "tree locked"; return false; }
                    wc.SelectTree(tree);
                    break;
                }
                case MiningActionComponent m:
                {
                    var rock = Lookup(m.Rocks, target.Ids[0]) as MiningRock;
                    if (rock == null || !rock.Recipe.Requirements.AreMet) { reason = "rock locked"; return false; }
                    m.SelectRock(rock);
                    break;
                }
                case FishingActionComponent f:
                {
                    var recipe = Lookup(f.Recipes, target.Ids[0]) as ActiveFishingRecipe;
                    if (recipe == null || !recipe.Recipe.Requirements.AreMet) { reason = "fishing spot locked"; return false; }
                    f.SelectRecipe(recipe);
                    break;
                }
                case ThievingActionComponent t:
                {
                    var recipe = Lookup(t.ActiveRecipes, target.Ids[0]) as ActiveThievingRecipe;
                    if (recipe == null || !t.CanThieveRecipe(recipe)) { reason = "target unavailable"; return false; }
                    t.TryStartActiveRecipe(recipe);
                    break;
                }
                case AgilityActionComponent a:
                {
                    var course = Lookup(a.Courses, target.Ids[0]) as ActiveAgilityCourse;
                    var path = course == null || target.Ids.Length < 2 ? null : Lookup(course.ActivePaths, target.Ids[1]) as ActiveAgilityCoursePath;
                    if (course == null || path == null) { reason = "course not found"; return false; }
                    course.SelectPath(path);
                    a.SelectCourse(course);
                    break;
                }
                case AstrologyActionComponent s:
                {
                    var c = Lookup(s.ActiveConstellations, target.Ids[0]) as ActiveAstrologyConstellation;
                    if (c == null || !c.Constellation.Requirements.AreMet) { reason = "constellation locked"; return false; }
                    s.StudyConstellation(c);
                    break;
                }
                case HuntingActionComponent h:
                {
                    var animal = Lookup(h.Animals, target.Ids[0]) as ActiveHuntingAnimal;
                    if (animal == null) { reason = "animal not found"; return false; }
                    if (animal.Location != null) h.SelectLocation(animal.Location);
                    h.SelectAnimal(animal);
                    h.StartTracking();
                    break;
                }
                case CombatActionComponent c:
                    return StartCombat(c, target, out reason);
                default:
                    return StartCrafting(action, target, out reason);
            }
        }
        catch (Exception e)
        {
            reason = e.Message;
            return false;
        }

        if (!action.IsStarted) { reason = "the game refused to start it"; return false; }
        return true;
    }

    private static bool StartCrafting(ActionComponent action, ActionTarget target, out string reason)
    {
        var t = Traverse.Create(action);
        var recipe = Lookup(t.Property("ActiveRecipes").GetValue() as IDictionary, target.Ids[0]);
        if (recipe == null) { reason = "recipe not found"; return false; }

        // Artisan skills / cooking / herblore pick a category first, like the UI.
        var category = Traverse.Create(recipe).Property("Recipe").Property("Category").GetValue();
        if (category != null && t.Property("ActiveCategories").GetValue() is IDictionary cats && cats.Contains(category))
            AccessTools.Method(action.GetType(), "SelectCategory")?.Invoke(action, new[] { cats[category] });

        AccessTools.Method(action.GetType(), "SelectRecipe")!.Invoke(action, new[] { recipe });
        if (AccessTools.Method(action.GetType(), "CanCreateRecipe")?.Invoke(action, new[] { recipe }) is false)
        { reason = "missing materials"; return false; }
        AccessTools.Method(action.GetType(), "StartCreating", Type.EmptyTypes)!.Invoke(action, null);
        reason = action.IsStarted ? "" : "the game refused to start it";
        return action.IsStarted;
    }

    private static bool StartCombat(CombatActionComponent combat, ActionTarget target, out string reason)
    {
        reason = "";
        var areas = Traverse.Create(combat).Property("Areas").GetValue<CombatAreasComponent>();
        var data = TimeBank.TimeBank.Game!.Data;
        if (target.Ids.Length >= 2 && data.TryGetObject<CombatArea>(target.Ids[0], out var area) &&
            data.TryGetObject<GamesByMalcs.Melvor2.Entities.Combat.Enemy>(target.Ids[1], out var enemy))
        {
            if (!area.UnlockRequirements.AreMet) { reason = "area locked"; return false; }
            areas.SelectEnemy(area, enemy);
        }
        else if (data.TryGetObject<Dungeon>(target.Ids[0], out var dungeon))
        {
            if (!dungeon.UnlockRequirements.AreMet) { reason = "dungeon locked"; return false; }
            areas.SelectDungeon(dungeon);
        }
        else { reason = "area not found"; return false; }

        if (!combat.IsStarted) { reason = "the game refused to start it"; return false; }
        return true;
    }

    /// <summary>Finds the value whose data key has the given id (the dictionaries are keyed by data objects).</summary>
    private static object? Lookup(object? dictionary, string id)
    {
        if (dictionary is not IDictionary dict) return null;
        foreach (DictionaryEntry e in dict)
            if (Id(e.Key) == id) return e.Value;
        return null;
    }
}
