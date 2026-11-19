# Melvor Idle 2 - Modding Reference

> Game version **0.11.6-alpha.1** (save version `SaveVersion.Alpha0_11_5_5`) - written **2026-11-19** from the ILSpy decompile in `decompiled\`.
> Engine: Godot 4.7 .NET (C#, CoreCLR). All game logic is in `GamesByMalcs.Melvor2.dll`. Root namespace `GamesByMalcs.Melvor2` (abbreviated `GBM.` below; e.g. `GBM.Entities.Woodcutting` = folder `decompiled\GamesByMalcs.Melvor2.Entities.Woodcutting\`).
> Obfuscated folders `A\`, `B\` (Nakama/Satori login, cloud) are deliberately not covered.

**Legend**
- **[M2T]** = API already used (called, read, or Harmony-patched) by `src\Melvor2Tweaks` - verified working in game unless noted.
- **[patch]** = Melvor2Tweaks patches this method.
- *(inferred)* = behaviour read from code but not observed in game.
- Methods named `XxxHandler` are private event handlers - good Harmony targets (patch by string name).

---

## 0. Quick start for a new session

| Need | How |
|---|---|
| The `Game` object | Mod uses `Main` node → private `InternalState` (a private nested `Main+GameState`) → `Game` property. See `Melvor2Tweaks.TimeBank.TimeBank.Game` **[M2T]**. `GameDriver` also holds it in private `Game` (backing field `<Game>k__BackingField`) **[M2T]**. |
| A skill entity | `game.Entities.TryGetObjectById("MelvorBase:Woodcutting", out GameEntity e)` **[M2T]** |
| Its action | `e.Components["Action"]` cast to the skill's `ActionComponent` subtype **[M2T]** (or typed property, e.g. `WoodcuttingEntity.Action`) |
| A data object | `game.Data.TryGetObject<Item>("MelvorBase:Bone", out var item)` **[M2T]**; all of a type: `game.Data.GetDataRegistryOutput<Item>().GetAllObjects()` **[M2T]** |
| Bank | `Game.Bank` is **private** → `Traverse.Create(game).Property("Bank").GetValue<BankEntity>()` **[M2T]** |
| What's running | `game.ActionManager.ActiveActions` (0 or 1 `ActionComponent`) **[M2T]** |
| Per tick hook | Harmony on `Game.PropagateTick` **[M2T patch]** (note: also runs on a worker thread during offline progress - see gotchas) |
| After a save opens (UI ready) | Postfix `GameView.OnFirstShow` **[M2T patch]**; game-side: `Game.SaveLoaded` event |

---

## 1. Architecture

### 1.1 Application states (`GBM.Main`, Godot root node)
`Main` is a state machine of private nested classes deriving `Main+ApplicationState`: `NoneState`, `MelvorCloudState` (login), `MainMenuState`, `GameState`, `DataEditorState`, `FirstTimeSetupState`. Enum `GBM.ApplicationMode { None, MelvorCloud, MainMenu, Game, DataEditor, FirstTimeSetup }`. Current state: private property `Main.InternalState` **[M2T]**.

`Main+GameState` (private) - the one that owns a game:
| Member | Notes |
|---|---|
| `Game Game` | the game instance **[M2T]** |
| `OnLoad(Main)` | loads data (`Game.LoadForSave()`), loads save via `SaveManager.LoadSaveInActiveSlotAsync`, computes offline time: `IsRunningLoadOffline = (now - Game.LoadedSaveTimestamp) >= 10 s`, `LoadOfflineDiff` (ms). Instantiates `res://GameMenus/game_view.tscn` → `GameView`. **[M2T patch]** (time bank) |
| `OnEntry(Main)` | `GameDriver.Bind(Game)`, `RunningMode = Process`, subscribes `Game.OfflineRequested`, calls `_view.OnFirstShow()`, then runs load-offline if needed **[M2T patch]** |
| `OnExit(Main)` | unbinds driver, `SaveManager.ClearActiveSaveId()` **[M2T patch]** |
| `OnSleepWake(SleepWakeEventArgs)` | OS sleep detected: ≤60 s → silent "Catchup", >60 s → offline progress with summary **[M2T patch]** |
| `RunningMode` (private enum `RunningModeEnum { None, Offline, Process, Catchup, Sleeping }`) | `Process` = normal play **[M2T]** |
| `RunOfflineProgress(ms, fromSleepWake)` / `RunOfflineTicks()` | see 1.8 |
| consts `MinOfflineTime = 10.0` s, `MaxCatchupTime = 60.0` s | |

`Main` also owns `GameDriver`, `Modals` (`ModalManager`), `Notifications`, `Offline` (offline progress overlay). It forwards each `ApplicationView`'s `Notifications.*Requested` events and `Modals.ModalRequested` to the global `Notifications` / `ModalManager` (`Main.HandleViewEvents`).

### 1.2 Tick loop
- `GBM.GameDriver : Node` - `_PhysicsProcess(delta) => Game?.PropagateTick()` **[M2T patch: speed multiplier calls PropagateTick extra times]**. Also owns the autosave timers: local every `10 s` (`Constants.AutoSaveInterval`), cloud interval from obfuscated config. `Bind(Game)` / `Unbind()`.
- `Game.PropagateTick()` **[M2T patch]** - `foreach (ITickingEntity e in TickingEntities) e.Tick();` (registration order, see 1.3).
- Each ticking entity's `Tick()` calls its components, e.g. `WoodcuttingEntity.Tick() { Action.Tick(); Tracker.Tick(); RareEvents.Tick(); }`.
- `ActionComponent.Tick()` raises `Ticked` (if started) then `OnTick()`, which ticks the skill's `TickTimer ActionTimer`; when it reaches 0 it raises `Fired` → the skill's private `ActionTimerFiredHandler`.
- **Tick interval**: `GBM.Core.Constants.TickInterval = 1000.0 / Engine.PhysicsTicksPerSecond` (ms); `Constants.TicksPerSecond` has a setter but nothing in the code calls it, and the project settings in the pck contain no `physics/common/physics_ticks_per_second` override → *(inferred)* Godot's default **60 ticks/s ≈ 16.67 ms**. NOTE: `Melvor2Tweaks.Features.FarmingTweaks` assumes 100 ticks ≈ 5 s (20/s). Verify at runtime by logging `Constants.TickInterval`.
- `GBM.Core.TickTimer.TickTimer`: tick-counted timer. `WaitTime` (ms, rounded to ticks; **throws if < TickInterval**), `WaitTicks`, `TicksLeft`, `Progress`, `State`, `Start()`, `StartWithInterval(ms)`, `Pause()`, `Resume()`, `Stop()`, `Tick()`, events `Fired`, `Progressed`, `Started`, `Paused`, `Resumed`, `Stopped`. Static `MSToTicks`, `TicksToMS`. Other timer types: `Core.FillableTimers.FillableTimer` (bonfire), `Core.QueuedTimers.QueuedTimer`/`TimerQueue` (farming, ranching).

### 1.3 `GBM.Game` (plain C# class, not a Node)
| Member | Notes |
|---|---|
| `DataRegistry<GameEntity> Entities` | all entities by id **[M2T]** |
| `DataManager Data` | all data objects **[M2T]** |
| `ActiveActionManager ActionManager` | **[M2T]** |
| `PassiveActionManager PassiveActionManager` | |
| `ReadOnlyCollection<ILeveledEntity<ExperienceComponent>> LeveledEntities`, `MasteryGuildEntities`, `PetEntities` | |
| `OfflineProgressEntity OfflineProgress` | `.Component.MaxOfflineTime` **[M2T]** |
| `TimeAndWeatherEntity TimeAndWeather`, `LayoutEntity Layout`, `QuestsEntity? Quests`, `CharacterStatisticsEntity CharacterStatistics` | public |
| `BankEntity Bank`, `CombatEntity? Combat`, `ModifierSystem Modifiers`, `GameEventBus EventBus`, `CoreSystems Core`, `Xoshiro256StarStarRandom RNG`, `List<ITickingEntity> TickingEntities` | **private** - use Traverse |
| `EventLogManager EventLog` | public |
| `IGameFlags Flags` | `.IsOffline` true during offline sim |
| `DateTime LoadedSaveTimestamp`, `bool IsSaveLoaded`, `string CharacterName`, `bool OnboardingSkipped` | **[M2T]** (timestamp) |
| `PropagateTick()` | **[M2T patch]** |
| `RequestOfflineHours(double hours)` | raises `OfflineRequested` → GameState runs the normal offline sim with summary. Used by the hidden debug menu and **[M2T]** time bank |
| `LocalSave()`, `CloudSave()`, `SaveOnExitAsync()` | |
| `IsAnySkillAboveLevel(int)`, `ResetSkillLevelsToLevel(int)` | |
| events `SaveLoaded`, `OfflineRequested`, `LoadingProgressed`, `CharacterNameChanged`, `OnboardingSkippedChanged` | |

Load sequence `LoadForSave()`: `RegisterNamespaces` (only `MelvorBase`) → `RegisterEntities` → `ClassifyEntities` (fills TickingEntities, leveled, mastery, pets, configured entities; toolbelt + food components registered with the bank; `ActionManager.ClassifyComponents()`) → resources → `RegisterDataTypes` → `StartLoadingData` (`MelvorBase.json`) → `FinishLoadingData` → `OnDataLoaded` on every entity/component. After save read: per entity `OnSaveLoaded` → `LoadBonuses` → `OnBonusesLoaded`, then `SaveLoaded`.

Entity registration order (= tick order): OfflineProgress, TimeAndWeather, Layout, EventBus, Bank, CharacterStatistics, Shop, Quests, Woodcutting, Mining, Fishing, Firemaking, Smithing, Cooking, Crafting, Runecrafting, Fletching, Farming, Ranching, Hunting, Beastcrafting, Attack, Strength, Defence, Ranged, Magic, Hitpoints, Prayer, Combat, CompletionLog, Agility, Herblore, Thieving, Slayer, Astrology.

### 1.4 Entity / component model
- `GBM.Entities.GameEntity` (abstract): `Id` (e.g. `MelvorBase:Woodcutting`), `LocalId`, `Namespace`, `DisplayName` (`LocalizedString`, `.Text`), `IconPath`, `Icon`, `ReadOnlyDictionary<string, GameComponent> Components` **[M2T]**, `EventLog`, `RequiresAttention` (+ `RequiresAttentionChanged`), `GetActiveView()` / `GetPassiveView()` (UI factories, e.g. `res://Entities/Woodcutting/woodcutting_active_view.tscn`), `GetHeaderOptions()`, lifecycle virtuals (`OnDataLoaded`, `OnSaveCreated`, `OnSaveLoaded`, `RecordOfflineSnapshot`, `GetOfflineMessages`). Most skill entities also expose typed properties (`Action`, `Experience`, `Config`, ...).
- `GBM.Components.GameComponent` (abstract): `Entity`, `Id` (the dictionary key), `RNG`, `EventLog`; same lifecycle virtuals; abstract `ReadFrom/WriteTo`.
- Entity marker interfaces (`GBM.Entities`): `ITickingEntity` (`Tick()`), `ILeveledEntity<ExperienceComponent>` (`Experience`, `LevelUpNotificationSound`), `IMasteryGuildEntity`, `IPetsEntity`, `IToolbeltEntity`, `IFoodEntity`, `IRareEventsEntity`, `IStatisticsEntity`, `ITrackedEntity`, `IMilestoneEntity<T>`, `IQuickBuyEntity`, `ITutorialEntity` (`bool FirstTime` - first page visit shows the skill tutorial), `IConfiguredEntity<TConfig>` (`Config`, loaded from the data package's `EntityConfig` section).

**Component keys per entity** (from constructors; local entity id = `MelvorBase:<Name>`):

| Entity | Component keys → types |
|---|---|
| Woodcutting / Mining | Experience, Toolbelt, ToolbeltEquipping, Milestones, Pets, MasteryGuild, **Action** (`WoodcuttingActionComponent` / `MiningActionComponent`), Tracker, Statistics, RareEvents |
| Fishing | as above + **BaitComponent** (`FishingBaitComponent`); Action = `FishingActionComponent` |
| Firemaking | ... Action = `FiremakingActionComponent`, **Bonfire** (`FiremakingBonfireComponent`) |
| Smithing, Crafting, Fletching, Runecrafting, Beastcrafting | Action = `ArtisanActionComponent`, **UntilX** = `ArtisanActionUntilXComponent`, Statistics, RareEvents ... |
| Cooking | Action = `CookingActionComponent`, UntilX = `CookingActionUntilXComponent` |
| Herblore | Action = `HerbloreActionComponent`, UntilX = `HerbloreActionUntilXComponent` |
| Thieving | **Food** (`FoodComponent`), Action = `ThievingActionComponent`, LootHistory, ... |
| Agility | **Food**, **Techniques** (`AgilityTechniquesComponent`), Action = `AgilityActionComponent` |
| Astrology | Action = `AstrologyActionComponent` |
| Hunting | **Journal** (`HuntingJournalComponent`), Action = `HuntingActionComponent` |
| Farming | **Passive** (`FarmingPassiveComponent`), **GrainMill** (`FarmingGrainMillComponent`), **GrainMillInventory** - no Action |
| Ranching | **Passive** (`RanchingPassiveComponent`) - no Action |
| Combat | PlayerCombatStats, EnemyCombatStats, PlayerCombatLevels, EnemyCombatLevels, Equipment, ItemEquipping, Toolbelt, ToolbeltEquipping, MagicSpells, CombatEffects, CombatPassives, **Areas** (`CombatAreasComponent`), **Player**, **Enemy**, **Action** (`CombatActionComponent`), **Food** (`CombatFoodComponent`), ExperienceGain (`CombatExperienceComponent`), Loot, LootHistory, **Prayer** (`CombatPrayerComponent`), **Loadouts**, Tracker, RareEvents (+ `SlayerTasks` property) |
| Attack, Strength, Defence, Ranged, Magic, Hitpoints, Prayer | Experience, Pets, MasteryGuild only (no action - trained via combat) |
| Slayer | Experience, **Tasks** (`SlayerTasksComponent`), Pets, MasteryGuild |
| Bank | Inventory (`BankInventoryComponent`), Wallet (`CurrencyWalletComponent`), ItemSales, ItemOpening, ItemUpgrading, ItemBurying, ToolbeltComponents (`ToolbeltEquippingRegistry`) |
| Shop | Shop (`ShopComponent`) - the same `ShopComponent` instance is injected into skills for quick-buy |
| Quests | Quests (`QuestsComponent`) |
| CompletionLog | SkillCompletion, ItemCompletion, MasteryGuildCompletion, PetsCompletion, EnemyCompletion, QuestsCompletion, Completion |
| OfflineProgress | Component (`OfflineProgressComponent`) |
| TimeAndWeather | TimeOfDay (`TimeOfDayComponent`), Weather (`WeatherComponent`) |
| Layout | Sidebar (`SidebarComponent`: `Options`, `LegacyOptions`, `MoveOption`) |

`ICoreSystems` (passed to every entity/component): `Data`, `Modifiers`, `ActionManager`, `PassiveActionManager`, `RNG`, `Flags`, `EventLog`, `EventBus`.

### 1.5 Data registries and ids
- Ids: `<Namespace>:<LocalId>`, regex `^[A-Z][A-Za-z0-9]*:[A-Z][A-Za-z0-9]*$`. Base namespace `MelvorBase` (`GBM.Data.DataNamespaceIds.BaseGame`). Helpers `NamespacedObject.ComputeFullId`, `SplitFullId`. Currency ids: `GBM.Data.Currencies.CurrencyIds.GoldPieces = "MelvorBase:GoldPieces"`, `SlayerCoins = "MelvorBase:SlayerCoins"`.
- `GBM.Data.DataRegistry<T>`: `GetObjectById(id)` (throws `UnregisteredObjectException`), `TryGetObjectById`, `GetAllObjects()`, `GetObjectsInNamespace`, `Count`.
- `GBM.Data.DataManager` (`game.Data`): `GetObject<T>(id)`, `TryGetObject<T>(id, out T)` **[M2T]**, `GetDataRegistryOutput<T>()` (→ `IReadOnlyDataRegistry<T>`, `.GetAllObjects()`) **[M2T]**, `Database` (`ObjectDatabase`: `Registries`, `DataClassRegistries`, `GetRegistry<T>()`), `Namespaces`, `ConfiguredEntities` (entity id → `IConfiguredEntity`), `TryGetComponentOfType<T>(entityId, componentName, out T)`, `GetComponentsOfType<T>(predicate)`, `ModifierDescriptions`, `StatClasses`.
- Registered data classes (`ObjectDatabase` ctor): Item, Currency, ArtisanRecipe, CookingRecipe, WoodcuttingRecipe(+Category), MiningRecipe(+Category), FishingRecipe/FishingLocation, FiremakingRecipe/Bonfire/Event, FarmingRecipe/Category/Plot/GrainMill/GrainMillRecipe/MillCategory, HerbloreRecipe/PotionGroup/Potency/Class, ThievingRecipe/Location/RecipeType/Distraction/HeistBonus, AgilityCourse/Path/Obstacle/Location/Technique, AstrologyConstellation/Node/StarNode/EventType + AstrologyEvent subclasses, HuntingAnimal/Species/Location/Tool/TrapType/JournalBonus, RanchingAnimal/Variant/Enclosure/Upgrade/Land/Category, ShopPurchase/Category/Subcategory, ItemUpgrade, Enemy, BaseCombatArea (CombatArea, Dungeon), CombatAreaCategory, SlayerMaster, Prayer/PrayerSlot/Type, BaseMagicSpell (MagicSpell, Imbuement), SpellBook, EquipmentSlot/Type, ToolbeltSlot/Type/Category, Pet, Quest/QuestCategory/NPC, MasteryGuildTask/Rank/Category/Bonus, RareEvent subclasses, RecipeCategory/Subcategory/GoalCategory, GamePage (EntityPage), PageNavigationOption subclasses, CombatEffect subclasses, statistics types, etc.
- Each skill's config lists **its** objects in display order (`IReadOnlyPriorityList<T>`, an `IReadOnlyList<T>`): e.g. `SmithingEntityConfig.Recipes` (`ArtisanRecipe`), `.Categories`, `.RewardModes`, `.MasteryGuildTasks`, `.Pets`, `.ToolbeltSlots`, `.GlobalActionEvents`, `.GlobalTimedEvents`. Full list at the end of §4.4.

### 1.6 Data packages
- `DataManager.StartLoadingMelvorDataPackage("MelvorBase.json")` loads `res://Data/Packages/MelvorBase.json` (inside the pck), **or** `user://local_data/MelvorBase.json` if `LocalSettingsManager.Settings.Debug.LoadLocalDataPackages` is true and the file exists (`deviceSettings.json` → `Debug.LoadLocalDataPackages`). This is the cleanest content-mod route (edit a copy of the JSON); not yet tried.
- JSON root: `Namespace`, `GlobalModifierDescriptions`, `ComponentModifierDescriptions`, `Objects` (keyed by C# type name, e.g. `"Item"`, `"ArtisanRecipe"`; dependency-sorted via `DataSorter`), `EntityConfig` (keyed by entity id → `Config.RegisterData`).
- `StartLoadingDataPackage(path)` is public and takes any Godot path, but the namespace must already be registered (`RegisterDataNamespace(new DataNamespace(id, name, isModded))`) and it must run inside the load sequence (before `FinishLoadingDataPackages`) - a mod loaded in `Main._Ready` would need to patch `Game.LoadForSave`/`StartLoadingData`.
- A runtime dump is possible: `Godot.FileAccess.Open("res://Data/Packages/MelvorBase.json", Read).GetAsText()` from a mod.
- Hidden **Data Editor** application mode (`ApplicationMode.DataEditor`, `GBM.DataEditor` namespace); main-menu button `MainMenuView.DataEditorButton` is forced invisible.

### 1.7 Save system (`GBM.Saves`)
- `SaveManager` (static): `ActiveSaveId` (**private** getter, used by **[M2T]** to key per-save mod data), `HasSaveInActiveId`, `StoredSaves`, `BackupSaves`, `WriteLocalSaveToActiveId(Game)`, `WriteLocalActiveIdToCloud()`, `LoadSaveInActiveSlotAsync(Game)`, `GetLocalSaveStringFromActiveId()` (the menu's "copy save to clipboard"), `CopySaveStringAsync`, `DeleteSaveWithIdAsync`; events `LocalSaved`, `CloudSaved`, `SaveAdded/Changed/Deleted`. `Constants.MaxSaveFiles = 5`.
- Sources: `LocalSaveSource`/`LocalSaveStore` (`%APPDATA%\Games By Malcs\Melvor Idle 2\steam\<id>\saves\`, synced by Steam Cloud), `CloudSaveSource` (Melvor Cloud), backups (`BackupSaves`, `SaveBackup`), `LegacySaveMigrator`.
- Format: binary `SaveWriter`/`SaveReader` with `SaveHeader` (char name, timestamp, total level, GP, slayer coins, active action name/icon). `Game.WriteTo`: entity count, then per entity `Id` + region (each entity writes its components as `id` + region); then `ActionManager`, RNG, `EventLog`, `PassiveActionManager`, `OnboardingSkipped`. **Unknown entity/component ids are skipped on read** (`SkipRegion`). Data objects are saved by id (`WriteNamespacedObject`).
- Mod data should live outside the save (Melvor2Tweaks uses JSON in `%APPDATA%\...\Melvor Idle 2\Melvor2Tweaks\`, keyed by `ActiveSaveId`).

### 1.8 Offline progress
- `OfflineProgressComponent.MaxOfflineTime` = `Stats?.MaxOfflineTime.Value ?? BaseMaxOfflineTime` (**86,400,000 ms = 24 h**, modifiable stat) **[M2T]**.
- `GameState.RunOfflineProgress`: caps via `ProgressCounter.SetMaxCap`, `Game.RecordOfflineSnapshot()`, `DisableUIForOffline()` (unhooks all UI events, unbinds driver, pauses the tree, shows `Main.Offline` overlay), then **`await Task.Run(RunOfflineTicks)` - ticks run on a thread-pool thread**: `while (timeLeft >= TickInterval) { Game.PropagateTick(); ... }`. Afterwards `Game.OnOfflineProgressEnd(ms)` → `OfflineProgress.ShowOfflineSummary` (modal `QueuedOfflineSnapshotModal` + event log entry). `Game.Flags.IsOffline` is true between `OnOfflineProgressStart/End` (not set for short catch-ups).
- Snapshots: components implement `RecordOfflineSnapshot()` / `GetOfflineMessages(ms)` (`OfflineSnapshotMessage`, `OfflineSnapshotCategory`).

### 1.9 Event log (`GBM.EventLog.EventLogManager`, `game.EventLog` / `component.EventLog`)
`Entries`, `UnreadCount`, `LogEvent(IEventLogEntry)` **[M2T: transpiler raises the hard-coded 100-entry cap]**, `MarkAllAsRead()`, `ClearEvents()`, events `EntryAdded/Removed/Moved/EntriesCleared`, filtering (`EventLogEntryTypeEnum`, `ToggleEventFilter`). Helpers: `LogActionStoppedWithReasonEvent(ActionComponent, string)` **[M2T]**, `LogEntityComponentNoticeEvent(component, msg)`, `LogFailToAddItemToBank`, `LogNewItemFoundEvent`, `LogSkillLevelUpEvent`, `LogMasteryGuildRankUp/TaskProgressed/TaskCompleted`, `LogPetFoundEvent`, ranching/hunting/astrology/rare-event loggers, `LogOfflineProgressEvent`.

### 1.10 Event bus (`GBM.EventBus.GameEventBus`, private on Game; `ActionComponent.EventBus` is protected)
`EventChannel<T>` with `Subscribe/Unsubscribe/Publish`: `ActionPerformed` (`ActionPerformedEventArgs`, `isSubAction`), `FoodEaten`, `PetUnlocked`, `ToolbeltItemConsumed`. Used by data-driven triggers (`GlobalActionPerformedTrigger`, ...).

### 1.11 Modals (`GBM.Modals`)
- `ModalManager : Node` (`Main.Modals`): `QueueModal(QueuedModal)` - queues and shows one at a time. Single choke point for **all** popups (good place to suppress/redirect).
- `ModalRequestor` (`ApplicationView.Modals`): `RequestModal(QueuedModal)` → `ModalRequested` → Main → `ModalManager.QueueModal`.
- `QueuedModal` subclasses: `QueuedPetLocatedModal`, `QueuedRareEventObservedModal` (Confirm → `RareEvents.ActiveBonuses.Activate()`), `QueuedMaxSkillLevelModal`, `QueuedNavigationOptionUnlockedModal` (sidebar option unlocked), `QueuedSkillCompletionModal`, `QueuedOfflineSnapshotModal`, `QueuedSimpleModal`, `QueuedSaveErrorModal`, `QueuedPlatformInitializationModal`. `QueuedModal.Closed` → `ModalClosedEventArgs.Reason` (`Modal.CloseReason.Confirmed`, ...).
- **Gameplay modals are raised by UI views**, not game logic: `GBM.Entities.Views.PassiveEntityView` subscribes to `ExperienceComponent.LevelChanged` (max-level modal / level-up announcement), `PetsComponent.PetUnlocked` (pet modal), `RareEventsComponent.EventObserved` (rare bonus modal); `CompletionLogPassiveView` (pets), `GameSidebar` (unlocks). Hence none during offline ticks (UI unhooked).
- Notifications (toasts) are separate: `ApplicationView.Notifications.*Requested` → `Main.Notifications.Add*` (item, currency, announcement, skill level-up, quest step, mastery task, new item found), gated by `LocalSettingsManager.Settings.Notifications.*`.

### 1.12 Pages, navigation and UI scene structure (`GBM.GameMenus`)
- `GameView` (`res://GameMenus/game_view.tscn`, an `ApplicationView`): public `Header` (`GameHeader`) **[M2T: `Header.Get("CategoryContainer")` to add header buttons]**, `Footer` (`GameFooter`), `Widgets`, `EntityViewContainer`, `Milestones`, `MasteryGuild`, `Statistics`; private `Sidebar` (`GameSidebar`), `SidebarLegacy`, `EventLog` (`EventLogView`), `DebugPopup` (`DebugMenu` - exists but nothing opens it in release), `PauseOverlay`, `CurrentPage` (tuple `(GamePage Data, ActiveGamePage Active)`, private) **[M2T]**. Methods: `OnFirstShow()` **[M2T patch]**, `SetCurrentPage(GamePage)` **[M2T]** (no-op if already current), `OpenBank()`, `OpenShop()`, `CloseExclusiveModals()`, `Bind(Game)`, `HandleEvents/UnhandleEvents`, `Update()`.
- Pages are data: `GameMenus.Pages.GamePage` (abstract, namespaced) → `EntityPage` (`Entity`, `TimeOfDayBackgrounds`, `ApplyBackgroundBlur`); `game.Data.GetObject<GamePage>("MelvorBase:Farming")` works **[M2T dev command `page:`]**. Navigation options: `PageNavigationOption` (`DisplayName`, `Icon`, `Page`, `Requirements`, `DisplayRequirements`) with subclasses `SkillNavigationOption` (`Entity`), `BankNavigationOption`, `ShopNavigationOption`, `CombatNavigationOption`, `QuestsNavigationOption`, `SimpleNavigationOption`, `LegacySkillNavigationOption`. Layout lists them in `LayoutEntityConfig.SidebarOptions / LegacySidebarOptions / HeaderOptions`. Skill "unlocks" = the option's `Requirements` (`ConditionGroup`).
- `GameHeader` (private members): `Title`, `ExperienceView`, `MilestonesButton`, `MasteryGuildButton`, `StatisticsButton`, `TutorialButton`, `OptionsContainer`, `CategoryContainer`, public `MenuButton`, `MenuDropdown` (ids: 0 rename, 1 character stats, 2 copy save string), `CharacterNameLabel`.
- `GameFooter`: private `TrackerView` (`TrackerComponentFooterView`), `MusicButton`, `EventLog` button.
- `GameSidebar` (`GameMenus.Sidebar`): option buttons + `ActiveActionButton`.
- Entity views: `GBM.Entities.Views.BaseActiveEntityView` / `ActiveEntityView` (skill page; shows first-time tutorial when `ITutorialEntity.FirstTime`), `BasePassiveEntityView` / `PassiveEntityView` (always-alive per-entity listeners). Settings page: `GBM.Settings.SettingsPanel` (`_Ready`, private `HideAllSettingsViews`, `ShowSettingsView`, `GameSettingsView`, `InterfaceSettingsView`) **[M2T patch]**.

### 1.13 Local settings (`GBM.Settings.LocalSettingsManager.Settings`)
`Game.StopActionOnBankFull` (used by `ActionComponent.TryAddItem`), `Notifications.ShowItemNotifications / ShowSkillLevelUpNotifications / ...`, `Interface.SkipLoginPage`, `Interface.LegacySidebar`, `Interface.HideFooterTracker`, `Interface.ArachnophobiaMode`, `Display.*`, `Debug.LoadLocalDataPackages`. Stored in `%APPDATA%\...\device_settings\deviceSettings.json` (+ `override.cfg` for window settings) and user settings.

---

## 2. Actions (common to all active skills)

### 2.1 `GBM.Components.Actions.ActionComponent : BaseActionComponent`
| Member | Notes |
|---|---|
| `TrackableInventory Inventory`, `TrackableWallet Wallet` | wrap the bank; `Inventory.ItemAdded` (`ItemAddedEventArgs`: `Item`, `Quantity`) fires for items **this action** adds **[M2T]** |
| `bool IsStarted` **[M2T]**, `bool QueueStop` **[M2T]** (protected set), `IsPerformingAction` (protected; backing field `<IsPerformingAction>k__BackingField` **[M2T]**) | |
| `IDescribedObject? CurrentDescription` | the running recipe/target data object; protected setter **[M2T patch]**; `CurrentDescriptionChanged` |
| `abstract TickTimer? ActionTimer` | null for combat (`IsTimerIndeterminate`) **[M2T: `.WaitTime`]** |
| `bool TryStart()` **[M2T patch]** | `CanStart()` → `ActionManager.TryStopActive()` (stops whatever else runs) → `IsStarted = true; OnStart(); Started` |
| `bool TryStop(bool force = false)` **[M2T patch]** | `CanStop() || force` → `OnStop(); Stopped` |
| `void QueueOrTryStop()` **[M2T patch]** | sets `QueueStop` if mid-action, else `TryStop()`. Use this from event handlers. |
| `protected int TryAddItem(Item, int)` **[M2T patch]** | adds to bank; on failure logs `LogFailToAddItemToBank` and sets `QueueStop` if `StopActionOnBankFull` |
| `SelectCurrentAction()` / `OnSelectCurrentAction()` | navigates UI to the current target |
| `InvokeCustomItemAddedEvent(Item, int)` | |
| events `Started`, `Stopped` (`ActionEventArgs`), `Ticked` (`ActionTickedEventArgs.IsOffline`), `CurrentActionSelected`, `ItemAdded`, `CurrentDescriptionChanged` | |

### 2.2 Managers
- `ActiveActionManager` (`game.ActionManager`): `ActiveActions` (IReadOnlyList; effectively 0 or 1), `HasActiveAction`, `TryStopActive(bool force=false)` **[M2T patch]**, events `ActionStarted`, `ActionStopped`. **Only one active action at a time.**
- `PassiveActionManager`: `PassiveActions` (all `PassiveComponent`s: farming, grain mill, ranching).
- `PassiveComponent : BaseActionComponent`: `Tick()`, `bool DisableTicking` (debug menu toggle), `Ticked`, `TryAddItem(Item, int)` → bool.

### 2.3 Crafting family (generic; Cooking, Herblore, Firemaking, all Artisan skills)
`GBM.Components.CraftingAction.CraftingActionComponent<TRecipe, TActiveRecipe>` → `GBM.Components.CategorizedCraftingAction.CategorizedCraftingActionComponent<TCategory, TActiveCategory, TRecipe, TActiveRecipe>`.

| Member | Notes |
|---|---|
| `ReadOnlyDictionary<TRecipe, TActiveRecipe> ActiveRecipes` | keyed by data recipe **[M2T]** |
| `ReadOnlyDictionary<TCategory, TActiveCategory> ActiveCategories`, `SelectedCategory`, `SelectCategory(TActiveCategory)` | **[M2T]** |
| `TActiveRecipe? SelectedRecipe` / `SelectRecipe(r)` / `DeselectRecipe()` | **not** a toggle **[M2T]** |
| `TActiveRecipe? CreatingRecipe` | currently crafting **[M2T]** |
| `void StartCreating()` | **toggle**: SelectedRecipe == CreatingRecipe → stop; not started → `TryStart()`; else switch if `CanCreateRecipe` **[M2T]** |
| `bool CanCreateRecipe(r)` | costs affordable AND products fit **[M2T]** |
| `InventoryWalletTransaction GetRecipeCosts(r)` | from modifier-adjusted `r.Stats.ItemCost` / `CurrencyCost` |
| `List<Item> GetProducedRecipeItems(r)`, `List<Currency> GetProducedRecipeCurrencies(r)` | |
| `ShopComponent Shop` | quick-buy |
| events `RecipeCreated` (`CraftingRecipeCreatedEventArgs<T>`: recipe, wait, isSuccess), `PrimaryProductCreated`, `ItemPreserved`, `SelectedRecipeChanged`, `CreatingRecipeChanged`, `SelectedCategoryChanged` | |
| protected virtual `ActionTimerFiredHandler`, `OnRecipeCreated` (stops with "lack of resources" log when it can't continue), `IsCreationSuccessful`, `AddRecipeRewards` | patch targets |

Per action: validate costs (stop if invalid) → roll `Stats.PreservationChance` (preserve = no cost) → success roll → rewards → `RecipeCreated`.

"Until X" (`GBM.Components.ActionUntilX`): `CraftingActionUntilXComponent<,>` (entity component `UntilX`): `StopOptions`, `SelectedStopOption`, `StartCreating()` (what the UI's Create button calls when present), stop options `LevelStopOption`, `ItemCurrencyQuantityStopOption`, `BaseRecipeCreationsStopOption` (+ `Components.Actions.StopOptions.*`). Base `ActionUntilXComponent`: `StopCondition`, `StopGoal`.

---

## 3. Skills

All gathering/artisan skills: entity typed props `Experience`, `Action`, `Config`, `MasteryGuild`, `Pets`, `Toolbelt`, `ToolbeltEquipping`, `Tracker`, `RareEvents`, `Statistics`, `Milestones`, `FirstTime`. Active wrapper objects (`WoodcuttingTree`, `MiningRock`, `Active*Recipe`) hold per-save state and a modifier-adjusted `Stats` object; data objects (`*Recipe`) are immutable. Start calls below are exactly what the UI calls (and what `Melvor2Tweaks.Actions.SkillActions` uses **[M2T]**).

### 3.1 Summary table

| Skill (entity id) | Action type | Target collection → wrapper | Start (UI call) | Toggle? |
|---|---|---|---|---|
| Woodcutting | `WoodcuttingActionComponent` | `Trees: ReadOnlyDictionary<WoodcuttingRecipe, WoodcuttingTree>`; running `CuttingTree` | `SelectTree(tree)` | yes |
| Mining | `MiningActionComponent` | `Rocks: <MiningRecipe, MiningRock>`; `CurrentRock` | `SelectRock(rock)` | yes |
| Fishing | `FishingActionComponent` | `Recipes: <FishingRecipe, ActiveFishingRecipe>`, `Locations`; `CurrentRecipe` | `SelectRecipe(r)` (+ `SetCurrentLocation(loc)` for UI) | yes |
| Thieving | `ThievingActionComponent` | `ActiveRecipes: <ThievingRecipe, ActiveThievingRecipe>`, `ActiveLocations`; `CurrentRecipe` | `TryStartActiveRecipe(r)` (check `CanThieveRecipe(r)`) | yes |
| Agility | `AgilityActionComponent` | `Courses: <AgilityCourse, ActiveAgilityCourse>` → `ActivePaths`; `CurrentPath` | `course.SelectPath(path); action.SelectCourse(course)` | yes |
| Astrology | `AstrologyActionComponent` | `ActiveConstellations: <AstrologyConstellation, ActiveAstrologyConstellation>`; `StudiedConstellation` | `StudyConstellation(c)` | yes |
| Hunting | `HuntingActionComponent` | `Animals: <HuntingAnimal, ActiveHuntingAnimal>`, `Locations`; `CurrentAnimal` | `SelectLocation(loc); SelectAnimal(a); StartTracking()` | yes (`StartTracking`) |
| Cooking | `CookingActionComponent` | `ActiveRecipes: <CookingRecipe, ActiveCookingRecipe>` | `SelectCategory; SelectRecipe; StartCreating()` | `StartCreating` |
| Herblore | `HerbloreActionComponent` | `ActiveRecipes: <HerbloreRecipe, ActiveHerbloreRecipe>`, `ActivePotionGroups` | `SelectPotionGroup(g); g.SelectPotion(p)` (→ `SelectRecipe`); `StartCreating()` | `StartCreating` |
| Firemaking | `FiremakingActionComponent` | `ActiveRecipes: <FiremakingRecipe, ActiveFiremakingRecipe>` | `SelectRecipe; StartCreating()` | `StartCreating` |
| Smithing / Crafting / Fletching / Runecrafting / Beastcrafting | `ArtisanActionComponent` | `ActiveRecipes: <ArtisanRecipe, ActiveArtisanRecipe>`, `ActiveCategories` | `SelectCategory; SelectRecipe; StartCreating()` | `StartCreating` |
| Farming | `FarmingPassiveComponent` (passive) | `ActivePlots: <FarmingPlot, ActiveFarmingPlot>` | per plot `SelectRecipe(recipe); Plant()` / `Harvest()` | n/a |
| Ranching | `RanchingPassiveComponent` (passive) | lands → `ActiveRanchingEnclosure` → `ActiveRanchingAnimal` | `enclosure.AddNewAnimal(animal, isFree:false)` | n/a |
| Combat (+ Attack, Strength, Defence, Ranged, Magic, Hitpoints, Prayer, Slayer XP) | `CombatActionComponent` | `CombatEntity.Areas` | `Areas.SelectEnemy(area, enemy)` / `Areas.SelectDungeon(d)` | **no** - reselecting restarts the spawn |

All gathering select methods **do not check requirements** (the UI does); the action stops itself via `Requirements.AllMet` handlers if requirements later fail. Check `recipe.Requirements.AreMet` (and `DisplayRequirements`) first **[M2T]**.

### 3.2 Woodcutting (`GBM.Entities.Woodcutting`)
- `WoodcuttingActionComponent`: `Trees`, `CuttingTree` (+`CuttingTreeChanged`), `SelectedCategory`, `ShowHiddenTrees`, `HiddenTreeCount`, `SelectTree(WoodcuttingTree)` **[M2T]**, `GetAvailableRecipeCount(category)`; events `TreeCut` (`TreeCutEventArgs`), `TreeDamaged` (damage, doubleDamage, isWeakSpot), `TreeRequirementsChanged`.
- `WoodcuttingTree`: `Recipe`, `Stats` (`WoodcuttingActionStats`), `Health` (`RecipeHealth`: Current/Max/Damage/SetMax/IsDead), `ActiveEffects` (overgrowth), `OnCut()`.
- `WoodcuttingRecipe` (a "tree"): `BaseExperience`, `BaseInterval`, `BaseMaxHealth` (hits to cut), `Product` (`ItemQuantity`), `RandomProductsPerCut`, `RandomProductsPerHit` (`ItemQuantityChance`), `Requirements`, `DisplayRequirements`, `LeafMedia`; category `WoodcuttingRecipeCategory`.
- Mechanics (`ActionTimerFiredHandler`): per hit roll per-hit drops; damage 1, or instant cut (`InstantCutChance`), weak spot (3 + 10% max HP), double damage; when HP hits 0: per-cut drops, `Product × Stats.ProductQuantity` (doubling chance ×2), XP, reset HP, `TreeCut`. **Overgrowth effects** (`OvergrowthEffect`: `AppliesToCategory`, `Bonuses`) rolled each hit, applied to trees.

### 3.3 Mining (`GBM.Entities.Mining`)
- `MiningActionComponent`: `Rocks`, `CurrentRock`, `SelectedCagtegory` (sic), `StoneItems`, `EssenceItems` (from config), `SelectRock(MiningRock)` **[M2T]**, events `RockMined`, `RockRequirementsChanged`, `CurrentRockChanged`.
- `MiningRock`: `Recipe`, `Stats` (`MiningActionStats`), `Progress` (0..1 prospecting), `IsHidden`, `Hit` event.
- `MiningRecipe`: `BaseExperience`, `BaseInterval`, `BaseProductChance` (progress per hit), `Product`, `RandomProductsPerHit`, `BarItem?`, `StoneItem?`, `EssenceItem?`, `Category`.
- Mechanics: each hit rolls per-hit drops + every stone item (`StoneItemChance`) + every essence item (`EssenceItemChance`); progress += product chance; on rock "break" (progress ≥ 1 or early `RockBreakChance`) progress resets to `MinimumProspecting`, gives `ProductQuantity` (+1 `AdditionalProductQuantityChance`, ×2 doubling, ×3 and 2× XP on **Shatter**), chance of `BarItem`.

### 3.4 Fishing (`GBM.Entities.Fishing`)
- `FishingActionComponent`: `Recipes`, `Locations` (`FishingLocation` = category), `CurrentLocation`/`SetCurrentLocation(loc)`, `CurrentRecipe`, `SelectRecipe(ActiveFishingRecipe)` **[M2T]**; events `RecipeFished`, `SpecialItemFished`.
- `ActiveFishingRecipe`: `Recipe`, `Stats` (`SpecialProductChances` **[M2T]**), `ActiveLocation`, `PriorityProduct` / `SetPriorityProduct(item)` (target one fish).
- `FishingRecipe`: `BaseMinInterval`/`BaseMaxInterval` (random interval), `FishChance` (else `JunkItem`), `RandomProducts` (`IReadOnlyWeightedTable<FishingProduct>`), `SpecialProducts` (`ItemQuantityChance`).
- **Bait**: `FishingBaitComponent` (entity key `BaitComponent`): `BaitItems`, `SelectedBait`, `SelectBait(item?)`, consumed per catch (`BaitConsumed`/`BaitPreserved`); item has `FishingBaitCapability.Bonuses`.

### 3.5 Thieving (`GBM.Entities.Thieving`)
- `ThievingActionComponent`: `ActiveLocations` (`ThievingLocation` = category), `ActiveRecipes`, `CurrentRecipe`, `SelectedCategory`/`SelectCategory`, `SelectRecipe` (UI selection only), `TryStartActiveRecipe(r)` **[M2T]**, `CanThieveRecipe(r)` **[M2T]**, `GetSuccessChance(r)`, `StunState` (`ThievingStunState.None/Stunned`) **[M2T]**, `Health` (`ICharacterHealth` = **combat player's `GlobalHealth`**, shared HP) **[M2T]**, `RecipeTypes`; events `ThievingActionPerformed`, `ThievingActionComplete`, `Stunned`, `StunAvoided`, `DamageTaken`, `DamageAvoided`, `StunEnded`, `Died`.
- `ThievingRecipe`: `RecipeType` (pickpocket vs heist), `Perception`, `Damage` (fail hit 1..Damage) **[M2T]**, `Stages` (XP only after all stages), `CurrencyReward` + `BaseCurrencyLootChance`, `CommonDropTable`/`BaseCommonLootChance`, `RareDropTable`/`BaseRareLootChance`, `CompletionDropTable`, `HeistBonuses`, `AccessRequirements`. `ThievingDistraction` (per location bonuses), `ThievingHeistBonus`.
- Mechanics: fail → damage (avoidable) + stun unless `StunAvoidanceChance`; a stunned tick only clears the stun; success → loot, stage +1 (or +2 skip), `SameAreaPickpocketChance` pickpockets siblings. **Death** (HP 0): HP refilled, **random toolbelt slot item lost** (10% of stack if stackable), `QueueStop`, `Died`. Own `FoodComponent` (entity key `Food`) with auto-eat at 25%.

### 3.6 Agility (`GBM.Entities.Agility`)
- `AgilityActionComponent`: `Courses`, `Locations`, `SelectedLocation`, `CurrentPath`, `SelectCourse(ActiveAgilityCourse)` **[M2T]** (uses `course.SelectedPath`; returns silently if course requirements unmet), `Health` (shared global HP), `TechniquesComponent`, `TotalShotcutsBuilt`, `BaseTrapSuccessChance = 0.8`, `TrapSuccessChanceCap = 0.95`, `DamagePlayer(obstacle)`, `GetCostTracker(costs)`; events `ObstacleAttempted`, `ObstacleFailed`, `CourseCompleted`, `DamagedPlayer`, `DamageAvoided`, `Died`.
- `ActiveAgilityCourse`: `Course`, `ActivePaths`, `SelectedPath`, `SelectPath(path)` **[M2T]**, `GetShortcutsCreatedCount()`.
- `ActiveAgilityCoursePath`: `Path`, `Obstacles` (`ActiveAgilityObstacle`), `CurrentObstacleIndex`, `CurrentObstacle` **[M2T]**, `CurrentObstacleReward` (random obstacle loot, collected when that obstacle is passed), `GetTotalSuccessChance()`, `GetTotalInterval()`, `GetMaxDamageOnFail()`, `GetTechniqueExperience()`.
- `ActiveAgilityObstacle`: `Obstacle`, `Stats` (`.Damage.Value` **[M2T]**), `ShortcutCreated`, `CreateShortcut()` (removes obstacle, costs `AgilityObstacle.ShortcutCosts`), `GetSuccessChance()`, `Status`.
- Data: `AgilityCourse` (`Paths`, `EntryCost`, `CompletionRewards`, `TimedCompletionRewards`, `GivesTechniqueXP`), `AgilityCoursePath` (`Obstacles`, `BaseExperience`, `BaseTechniqueExperience`, `RandomObstacleProducts`), `AgilityObstacle` (`BaseInterval`, `Technique`, `Difficulty`, `Damage`, `IsTrap`, `ShortcutCosts`). Techniques have their own levels (`AgilityTechniqueExperienceTable`).
- Death works like thieving (random toolbelt item lost).

### 3.7 Astrology (`GBM.Entities.Astrology`)
- `AstrologyActionComponent`: `ActiveConstellations`, `SelectedConstellation`, `StudiedConstellation`, `StudyConstellation(c)` **[M2T]**, `ActiveBonuses` (`ActiveBonusesEvent`: `Activate()` **[M2T]**, `IsActivated`, `ChargesLeft`, `Remove()`), `SetActiveBonuses(bonusesEvent, constellation)` **[M2T patch]**, `RemoveActiveBonuses()`, `ExecuteEvent`, `GrantRandomItems`, `RollTableEvent`, `GetConstellationKnowledge`, `IsMinor/MajorNodeUnlocked`, `GetConstellationMinor/MajorNodeUnlockCount`; events `MinorNodeUnlocked`, `MajorNodeUnlocked`, `ConstellationStudied`, `EventObserved`, `EventActivated`.
- `ActiveAstrologyConstellation`: `Knowledge`, `AvailableMinorNodeCount`, `AvailableMajorNodeCount`, `UnlockMinorNode(node)`, `UnlockMajorNode(node)`, `RecentEvents`.
- `AstrologyConstellation`: `BaseExperience`, `BaseInterval`, `BaseKnowledge`, `MinorNodeUnlockKnowledge`, `MinorNodes`, `MajorNodes`, `MinorNodesPerMajorNode`, `PerMinorNodeBonuses`, `Events` (rolled per study), `Entities` (skills it buffs), `SecondaryRequirements`. Events: `AstrologyItemEvent` (`Item`, `BaseQuantity`, `LevelScalingQuantity`), `AstrologyTableEvent`, `AstrologyBonusesEvent`, `AstrologyObservationEvent`. A new bonuses event replaces (`RemoveActiveBonuses`) the existing one.

### 3.8 Hunting (`GBM.Entities.Hunting`)
- `HuntingActionComponent`: `Animals`, `Locations`, `SelectedLocation`/`SelectLocation`, `SelectedAnimal`/`SelectAnimal`, `CurrentAnimal`, `StartTracking()` **[M2T]** (toggle), `CanTrackAnimal(a)`, `CurrentTrap`, `AutoSwapToNextTrap`, `Journal`; events `AnimalTrapped`, `AnimalEscaped`.
- `ActiveHuntingAnimal`: `Animal`, `Location` **[M2T]**, `State` (`HuntState { Idle, Surveying, Tracking, Trapping }`), `Tracks`, `CurrentStep`/`RequiredSteps`, `SelectedTrap`/`SelectTrap(item)`, `CompatibleTraps`, `UseBait`/`ToggleBaitUsage`, `TraplessEnabled`/`EnableTrapless()`, `CurrentSpecies`, `JournalEntry` (per-animal journal level/XP), `TrapFailCount`.
- `HuntingAnimal`: `Location`, `TrapType`, `Tool`, `PreferredBait` + `BaitModifierTemplate`, `BaseExperience`, journal XP values, tracking steps/intervals, `BaseTrackSuccessChance`, `BaseTrapSuccessChance`, `Products`, `SurveyingProducts`, `RareSpecies` (weighted `HuntingAnimalSpecies` with `ExtraProducts`), trapless settings. `HuntingLocation : RecipeCategory` (`BaseSurveyInterval`, `BaseSurveyingExperience`, `BaseInsightsChance`).
- Loop: Survey (roll track; survey products) → Track (steps) → Trap. Each trap attempt **consumes 1 trap item** (unless preserved or trapless) and bait; fail can re-attempt; stops with an event-log reason when out of traps. Trap item = `HuntingTrapCapability` (`TrapType`, `TrapBonuses`, `FailBonuses`).

### 3.9 Cooking (`GBM.Entities.Cooking`)
`CookingActionComponent : CategorizedCraftingActionComponent<RecipeCategory, ActiveCookingCategory, CookingRecipe, ActiveCookingRecipe>`. `CookingRecipe : ArtisanRecipe` + `Difficulty` → `Stats.SuccessChance`. On success one product is chosen from `Products` by cumulative `Chance` (e.g. quality tiers), ×2 on doubling. Has `UntilX`.

### 3.10 Herblore (`GBM.Entities.Herblore`)
`HerbloreActionComponent`: `ActivePotionGroups` (`ActiveHerblorePotionGroup`: `ActivePotions`, `ActivePotionsByPotency`, `SelectedPotion`, `SelectPotion(p)`), `SelectedPotionGroup`, `SelectPotionGroup(g)`, `GetIntervalForRecipeState(r)`, event `PotionBrewed`. `ActiveHerbloreRecipe.State` (`HerbloreRecipeState { Idle, Preparing, Heating, Stirring, Bottling }`), `BottlesRemaining`, `MaxBottledCount = 10`: Preparing → Heating → Stirring → Bottling ×10 (each bottling consumes ingredients and yields a potion), then back to Preparing. `HerbloreRecipe`: `PotionGroup`, `Potency`, `PotionClass`, four stage intervals, `Products`, `Ingredients`.

### 3.11 Firemaking (`GBM.Entities.Firemaking`)
- `FiremakingActionComponent` (categorized crafting): burning a log = `FiremakingRecipe` (`Ingredients`, `Products` with chance, `CurrencyReward`); `FiremakingEvent` (`Chance`, `Reward` scaled by interval, `Category`) rolled per action, event `EventObserved`.
- **Bonfire** - `FiremakingBonfireComponent` (entity key `Bonfire`): `CurrentBonfire`, `NextBonfire`, `PurchaseNextBonfire()`, `IsActive`, `DurationTimer` (`FillableTimer`: `TimeLeft`, `MaxTime`) **[M2T]**, `Tick()` **[M2T patch]**, `GetLogsToAdd(double fillRatio)` **[M2T]**, `AddLogs(int)` **[M2T]** (takes the selected recipe's logs from the bank), `GetBonfireTimeForRecipe(r)`; events `FireTimeAdded`, `LogsAdded`, `IsActiveChanged`. *(inferred)* Every log burnt adds 5× its bonfire time; the duration timer is **paused while Firemaking itself runs** and burns down (providing `FiremakingBonfire.Bonuses`) while you do other things. `FiremakingBonfire`: `MaxDuration` (h), `Bonuses`, `Costs`, `PreviousBonfire`.

### 3.12 Artisan skills: Smithing, Crafting, Fletching, Runecrafting, Beastcrafting (`GBM.Components.ArtisanAction`)
- One class for all five: `ArtisanActionComponent` (entity class differs: `SmithingEntity`, ...). Recipes `ArtisanRecipe` listed per skill in `<Skill>EntityConfig.Recipes`.
- `ArtisanRecipe`: `Category` (`RecipeCategory`), `Subcategory`, `BaseExperience`, `BaseInterval`, `Products` (`ItemQuantityChance`), `Ingredients` (`RecipeIngredients`), `Steps` (multi-step crafts), requirements.
- `ActiveArtisanRecipe`: `Health` (steps left), `CurrentRewardMode` / `ToggleRewardMode(mode)`.
- Mechanics: each timer fire = one step (`SkipStepChance` = 2 steps); on the last step costs are paid (unless preserved) and products given. **Alternative reward modes** (`AlternativeRewardMode`, `Config.RewardModes`): `AstralOfferingsRewardMode` (destroy for level-scaled `LevelReward` items), `SacrificialAnvilRewardMode` (bonus XP / bonus sale value / collectables). Events `StepSkipped`, `StepPerformed`.
- Runecrafting essence comes from Mining `EssenceItems`; Beastcrafting presumably from hunting/ranching products *(inferred)*.

### 3.13 Farming (`GBM.Entities.Farming`) - passive
- `FarmingPassiveComponent` (key `Passive`): `ActivePlots: IReadOnlyDictionary<FarmingPlot, ActiveFarmingPlot>` **[M2T]**, `ActiveCategories`, `SelectedCategory`, `CompostItems`, `OnTick` **[M2T patch]**, bulk setters `SetSelectedRecipeForAllPurchasedPlots(recipe)`, `SetSelectedCompostForAllPurchasedPlots(item)`, `SetAutoHarvest/AutoWater/AutoPlant/AutoCompostForAllPurchasedPlotsInCategory(category, bool)`, cost helpers (`GetRecipeCostsTransaction`, `GetRecipeAutomationCostsTransaction`, `GetCostTracker`), `GetWineUsageEstimate()`; events `RecipePlanted`, `RecipeWatered`, `RecipeGrown`, `PlotComposted`, `RecipeHarvested`, `AutomationCostsConsumed`.
- `ActiveFarmingPlot`: state machine of private nested `PlotState`s (`LockedState`, `EmptyState`, `GrowingState`, `GrownState`, `RegrowingState`, `ProvidingBonusesState`); `State`, `CanPlant/CanHarvest/CanWater/CanCompost/CanReset` **[M2T]**, `SelectedRecipe`/`SelectRecipe(FarmingRecipe)`, `PlantedRecipe`, `ReadyToHarvestProduct` **[M2T]**, `IsWatered`, `SelectedCompost`/`SelectCompost(item)`, `CurrentCompost`, `AutoPlant/AutoHarvest/AutoWater/AutoCompost` **[M2T]**, `Plant()`, `Harvest()` (bank full → event-log entry, plot stays grown), `Water()`, `Compost()`, `Purchase()` (unlock plot, `FarmingPlot.Costs`), `Reset()`, `CanAffordSelectedRecipe()`, `CanPlantSelectedRecipe()` **[M2T]**, private `PerformStateChangeAutomation()` **[M2T]** / `PerformAutoHarvest()` **[M2T patch]** / `PerformAutoPlant/AutoWater/AutoCompost` (automation **only runs on a state change**; each costs `FarmingRecipe.AutomationCosts`).
- `FarmingRecipe`: `Category`, `BaseInterval`, `Regrows`, `RegrowInterval`, `GrownInterval` (0 = forever), `Product`, `Ingredients` (seeds), `AutomationCosts`, `AutomationRequirements`, `FreeAutoWateringRequirements`, `DiseaseValueAdjuster`, `PestValueAdjuster`, `BonusesWhenGrown`. Compost item = `CompostCapability`.
- **Grain mill** - `FarmingGrainMillComponent` (key `GrainMill`): own storage `GrainMillInventory`; `SelectedRecipe`/`SelectRecipe(r)`, `ActiveRecipes`, `IsActive`, `ToggleGrainMillTimer(bool)`, `CanCreateRecipe(r)` **[M2T]**, `DepositItemToGrainMill(item, qty)` **[M2T]**, `DepositAllToGrainMill()`, `WithdrawItemFromGrainMill`, `WithdrawAllFromGrainMill`, `ReadyToCollect`/`CollectReadyItems()`, `GrainTimer`; private `GrainTimerFiredHandler` / `InventoryItemQuantityChangedHandler` **[M2T patch]**. Each fire takes `Stats.ItemCost` from the **mill storage** (not the bank), puts the product in the bank; switches off (`IsActive=false`, notice in event log) when out of grain. `GrainMillRecipe`: `Product`, `Ingredients`.
- UI: `FarmingActionView._Ready` / `GrainMillButton` **[M2T patch]**.

### 3.14 Ranching (`GBM.Entities.Ranching`) - passive
- `RanchingPassiveComponent` (key `Passive`): lands (`ActiveRanchingLand`, `SelectedLand`, `SelectLand/SelectNextLand/SelectPreviousLand`, `MoveLand`), `AnimalsWantingPets`, `PetAllAnimals()`, `RepairAllEnclosures()`, `ToggleTreat(item)`, timers `MaintenanceTimer`, `ProductTimer`, `FoodConsumptionTimer` (all hourly), `TreatTimer` (daily), `VariantLog`, `GetTotalItemsProduced`, `TimeOfDay`.
- `ActiveRanchingEnclosure`: `CurrentEnclosure`, `NextEnclosure`, `UpgradeTo(e)`, `Maintenance` (0..1) **[M2T]**, `Repair()` **[M2T]**, `OnMaintenanceTimerFired()` **[M2T patch]**, `AvgHunger/Safety/Satisfaction/Happiness`, `AddNewAnimal(animal, isFree)`, `CanAddNewAnimal`, `RemoveAnimal`, `SellAnimal`, `PetAllAnimals()`, `AutoBuyAnimals` + `ToggleAutoBuyForAnimal` (native auto-restock), `PurchaseEnclosureUpgrade(upgrade)`, `BreedTimer`.
- `ActiveRanchingAnimal`: `Recipe` (`RanchingAnimal`), `WantsPets` **[M2T]**, `Pet()` **[M2T]**, `OnPetTicksDepleted()` / private `PetTimerFiredHandler` **[M2T patch]**, `Hunger`, `Safety`, `Satisfaction`, `Happiness` (scales XP: `XpToAdd`), `IsSick`/`CureIllness()`, `CurrentVariant`, `LifespanTicks`.
- `RanchingAnimal`: `PurchaseCosts`, `Products` (per `ProductInterval`), `Eats`, `Treats`, `RequiresPets` (every 2 days) **[M2T]**, `LifespanInterval`, variants (`RanchingAnimalVariant`: `SelfBonuses`, `GlobalBonuses`), `PackSatisfactionBonuses`. `RanchingEnclosure`: `Animals`, `AnimalLimit`, `Costs`, `UpgradesTo`.

### 3.15 Combat (`GBM.Entities.Combat`, `GBM.Components.*`)
- `CombatEntity` typed props: `Action`, `Areas`, `Player`, `Enemy`, `Food`, `Equipment`, `ItemEquipping`, `Toolbelt`, `MagicSpells`, `Prayer`, `Loadouts`, `Loot`, `LootHistory`, `ExperienceGain`, `SlayerTasks`, `Statistics`, `Tracker`, `RareEvents`, `Config`.
- `CombatActionComponent`: `State` (`CombatActionState { Idle, Spawning, Fighting }`) **[M2T]**, `IsPaused`, `Pause()`, `Unpause()` **[M2T]**, `Player` **[M2T]**, `Enemy`, `SpawnTimer` (`BaseSpawnInterval = 3000` ms), `Stats` (`NoDeathPenalty` etc.), private `Areas` **[M2T via Traverse]**; `OnTick` **[M2T patch]**, private `SpawnTimerFiredHandler` **[M2T patch]** (pauses if `Enemy.PauseOnSpawn`), private `PlayerDiedHandler` **[M2T patch]** (unless NoDeathPenalty: **random equipment slot item lost**, 10% of stack if stackable; then `TryStop()`), `AreasAreaSelectedHandler` (start or swap); events `EnemySpawnStarted`, `RanFromEnemy`, `PlayerDied`, `StateChanged`, `IsPausedChanged`. Combat triangle / elemental helpers.
- `CombatAreasComponent` (`Areas`): `Config`, `CurrentArea` (`ActiveCombatArea`, `.Area`) **[M2T]**, `SelectedEnemy` **[M2T]**, `SelectEnemy(CombatArea, Enemy)` **[M2T]**, `SelectDungeon(Dungeon)` **[M2T]**, `UnselectArea()`, `MoveNextEnemy()`, `CurrentProgress`/`MaxProgress` (dungeon), events `AreaSelected`, `DungeonCompleted`. Neither select checks `UnlockRequirements` **[M2T checks them]**. Run = `CombatEntity.Action.TryStop()`.
- `PlayerComponent`: `Health` / `GlobalHealth` (`CharacterHealth`; `Current`, `Max`, `IsFull`, `Damage`, `Set`) **[M2T]**, `Equipment`, `AmmoStats`, `SetSelectedStance(weaponType, stance)`, `GetSelectedStance`, protected `CanAttack(int)` **[M2T patch]** (false when out of ammo/runes).
- Food: `FoodComponent` (base for `CombatFoodComponent`; also Agility/Thieving `Food`): `SelectedFood` (`FoodItem`: `.Item`), `SelectFood(item)` **[M2T]**, `UnselectFood()`, `ManualEat()` **[M2T]**, `Inventory` **[M2T]**, `IsItemFavourited`, `IsItemHidden` **[M2T]**, `BaseAutoEatThreshold = 0.25`, private `AutoEat()` **[M2T patch]**, event `FoodEaten`. Food item = `FoodCapability` (`BaseHealing` **[M2T]**, `Bonuses`).
- Equipment: `EquipmentComponent`: `TryGetActiveSlot(slot, out IActiveEquipmentSlot)`, `GetAvailableSlotsForItem(item)` **[M2T]** (`AvailableEquipmentSlot`: `.Slot`, `.Replaces`), `TryEquipItem`, `TryUnequipItem`, `TryAddQuantity`, `IsItemEquipped`, `GetEquippedQuantity`, `EquippedItems`, `CurrentWeapon`, `IsAmmoEquipped`, `ConsumeAmmo`, set bonuses; events `ItemEquipped/Unequipped/QuantityChanged`, `AmmoConsumed`, `CurrentWeaponChanged`. Bank-side: `ItemEquippingComponent.EquipItem(item, qty, EquipmentSlot)` **[M2T]**, `UnequipSlot(slot)`.
- Magic: `MagicSpellsComponent`: `SelectedSpell`/`SelectSpell(MagicSpell)`, `SelectedSpellBook`, `SelectedImbuement`/`SelectImbuement`/`DeselectImbuement`, `CanUseSpells`/`ToggleSpellUse`, `TryConsumeAttackRuneCosts()`. `BaseMagicSpell.RuneCosts`, `MagicSpell.Book/Stats/AttackOverride`, `Imbuement.Bonuses`.
- Prayer: `PrayerComponent` (`CombatPrayerComponent`): `TogglePrayer(prayer)`, `SelectPrayerInSlot`, `IsPrayerActive`, `ActivatedPrayers`; `Prayer.Cost` is a **currency** (`CurrencyQuantity`, prayer points) consumed on `ConsumesOn`; `Prayer.Bonuses`, `FitsInSlot`. Prayer points come from burying (`BuryCapability.CurrencyGain`).
- Loot goes straight to the bank (`CombatLootComponent.Inventory` = bank; events `EnemyLooted`, `DungeonLooted`). `Enemy.Drops` / `Dungeon.Rewards` are `CombatDrops`.
- XP: `CombatExperienceComponent` routes XP to Attack/Strength/Defence/Hitpoints/Ranged/Magic/Slayer/Prayer `ExperienceComponent`s (`Game.RegisterEntities` → `Combat.AddCombatExperienceComponent`).
- Slayer: `SlayerTasksComponent` (`SlayerEntity.Tasks`): `SelectedMaster`, `RecentMaster`, `HasTaskForEnemy(enemy)`, events `TaskCompleted/Progressed/Rerolled/Extended`, `EnemyBlocked`. `ActiveSlayerMaster`: `CurrentTask` (Enemy), `TaskLength`, `TaskProgress`, `StartInitialTask()`, `RerollTask()`, `ExtendTask()`, `BlockEnemy/UnblockEnemy`, **`AutoSlayerEnabled`** (native), `GetCurrentTaskArea(enemy)`. `SlayerMaster`: `Areas`, `BaseTaskLength`, `RerollCost`, `BlockCost`, `RewardCurrency`.
- Loadouts: `LoadoutComponent.SwapToLoadout(LoadoutSlot)`, `CurrentLoadout`; participants (`ILoadoutable`: equipment, toolbelt, food, spells, prayers, player stance) implement `CaptureLoadout/SwapLoadout`.

### 3.16 Mastery (Mastery Guild), milestones, pets
- **Mastery = Mastery Guild per skill** (not per-item mastery like Melvor 1). `MasteryGuildComponent` (key `MasteryGuild`): `CurrentRank` (`MasteryGuildRank`, `PointPercentRequired`), `CurrentPoints`, `TotalTaskPoints`, `ProgressToNextRank`, `AddPoints(int)`, `GetRankForPoints`, `GetNextRank`; events `TaskCompleted`, `TaskProgressed`, `CurrentRankChanged`, `CurrentPointsChanged`. Tasks `MasteryGuildTask` (`Category`, `Goal` / `ConditionGoal` / `TriggerGoal`, `LinkedTask`); rewards `MasteryGuildBonus` (`Requirements`, `Rewards: ShopPurchaseProvides`). UI `MasteryGuildMenu`.
- Milestones: `MilestonesComponent` (`GetMilestonesInLevelRange`), `IMilestone` implemented by recipes (level-gated unlock list).
- Pets: `PetsComponent` (key `Pets`): `UnlockedPetCount`, events `PetUnlocked` (`PetUnlockedEventArgs.ActivePet`), `PetGoalProgressed`. `Pet`: `Goal` or `Trigger` to unlock, `Bonuses`.

---

## 4. Items, bank, recipes

### 4.1 `GBM.Data.Items.Item` (data class, registry `Item`)
| Property | Type / notes |
|---|---|
| `Id`, `LocalId`, `Namespace` | e.g. `MelvorBase:BirdNest` |
| `DisplayName` | `PluralString` (`.Singular` **[M2T]**, `GetPluralText(n)`) |
| `Description` | `LocalizedString?` |
| `Icon` | `Texture2D` |
| `SellsFor` | `CurrencyQuantity` (`Currency`, `long Quantity`) **[M2T]**; actual price = `ItemSales.GetItemStats(item).PricePerItem.Value` |
| `Capabilities` | `IReadOnlyTypeDictionary<ItemCapability>`; `item.Capabilities.TryGet<T>(out T)` **[M2T]** |
| `BankTags` | `ItemBankTag` (`BankTag: BankTagCategory`, `IsProduct`, `IsIngredient`) |
| `IgnoreCompletion` | bool |

There is no item "type"/"category" enum: behaviour comes from **capabilities**:
| Capability (namespace) | Key properties |
|---|---|
| `FoodCapability` (`Components.CombatFood`) | `BaseHealing`, `Bonuses` |
| `EquipmentCapability` (`Components.Equipment`) | `Type` (`EquipmentType`), `OccupiesSlots`, `Requirements`, `Stats` (`CombatStatValues`), `Bonuses`, `CanEquipQuantity` (ammo-like), `ConsumesOn`, `Attacks`, `AmmoType` **[M2T]** |
| `WeaponCapability` (`Components.Equipment`) | `Type` (`WeaponType`), `DamageType`, `Elements`, `RequiredAmmoType` |
| `ToolbeltCapability` (`Components.Toolbelt`) | `FitsInSlot` (`ToolbeltSlotType`), `Requirements`, `Bonuses`, `CanEquipQuantity`, `ConsumesOn` |
| `BuryCapability` (`Components.ItemBurying`) | `CurrencyGain` (prayer points) **[M2T]** |
| `OpenableCapability` (`Data.Items`) | `AlwaysDrops`, `DropTable` (`IReadOnlyWeightedTable<WeightedItemDrop>`), `Requirements` **[M2T]** |
| `CompostCapability` | `CompostModifierTemplate` |
| `FishingBaitCapability` | `Bonuses` |
| `HuntingTrapCapability` | `TrapType`, `TrapBonuses`, `FailBonuses` |

Quantity types: `ItemQuantity` (`Item`, `int Quantity`), `ItemQuantityChance : ItemQuantity` (`double Chance`), `ItemQuantityRange` (`Item`, `MinQuantity`, `MaxQuantity`), `WeightedItemDrop : ItemQuantityRange` (`Weight`), `ItemDropTableChance` (`Chance`, `Table`), `CurrencyQuantity` (`Currency`, `long Quantity`), `CurrencyQuantityRange`. Weighted tables: `GBM.Random.IReadOnlyWeightedTable<T>` (`Items`, `TotalWeight`, `GetItemChance(x)`, `RollForItem(rng)`, enumerable).

### 4.2 Bank (`GBM.Entities.Bank.BankEntity`, id `MelvorBase:Bank`)
| Member | Notes |
|---|---|
| `Inventory` (`BankInventoryComponent : InventoryComponent`) **[M2T]** | `AddQuantity(item, qty, AddItemFlags = Found)` **[M2T patch]** (returns added count; 0 when full), `RemoveQuantity`, `GetQuantity` **[M2T]**, `GetStackCount`, `StackCount`/`MaxStacks` (`BaseMaxStacks = 100`, modifiable) **[M2T]**, `Stacks`, tabs (`SelectedTab`, `GetTabForItem`, `AddNewTab`, `MoveStackToTab`, `SortItemsInAllTabs`, ...), `SetItemLocked(item, bool)` / private `LockedItems` **[M2T]**, placeholders (`SetItemAsPlaceholder`, `AutomaticItemPlaceholder`), selection (`SelectItemsWithBankTag`, `TotalSelectedItemCount`), `GetTotalCurrencyValue(currencyId)`, `ExportConfiguration`/`ImportConfiguration` (`BankConfiguration`); events `ItemQuantityChanged`, `StacksChanged`, `StackCountChanged`, `TabAdded/Removed/Moved`, `SelectedTabChanged`, `ItemStackSelected` |
| `Wallet` (`CurrencyWalletComponent`) | `GetQuantity(currency)`, `AddQuantity`, `RemoveQuantity`, `AddGoldPieces`, event `CurrencyQuantityChanged` |
| `ItemSales` (`ItemSaleComponent`) | `GetItemStats(item)` → `ItemSaleComponentStats.PricePerItem.Value` **[M2T]**, `SellItem(item, stats, qty)` **[M2T]** (no modal, **ignores locks**), `SellSelectedItems()`, private `Wallet` **[M2T]**, events `ItemSold`, `ItemsSold` |
| `ItemOpening` (`ItemOpeningComponent`) | `OpenItem(item, qty)` → `ItemOpenedEventArgs` (`Drops`, `Count` **[M2T]**, `WasStopped` when bank fills); event `ItemsOpened` |
| `ItemBurying` (`ItemBuryingComponent`) | `GetItemStats(item)`, `BuryItem(item, stats, qty)` **[M2T]**; event `ItemBuried` |
| `ItemUpgrading` (`ItemUpgradingComponent`) | `ItemHasUpgrade(item)`, `GetSelectedUpgrade`, `SelectUpgrade`, `CanAfford(upgrade, qty)`, `GetUpgradeCosts`, `ExecuteUpgrade(upgrade, qty)`; event `ItemUpgraded`. Data `ItemUpgrade` (`UpgradedItem`, `Costs`, `QuantityPerUpgrade`, `RootItems`) |
| `ItemEquipping` (`ItemEquippingComponent?`, = Combat's) **[M2T]** | `EquipItem(item, qty, EquipmentSlot)`, `Equipment` |
| `ToolbeltComponents` (`ToolbeltEquippingRegistry`) | every skill's `ToolbeltEquippingComponent` |
| `FoodComponents` | combat, agility, thieving food |
| `Statistics` (`BankStatistics`) | `ItemFound` (dictionary statistic: item → times found; drives "new item" notification and completion) |

- `AddItemFlags { None = 0, Found = 1, Locked = 2, Placeholder = 4 }` - `Found` = genuine drop/gain (default); moves/withdrawals use `None` **[M2T relies on this]**.
- `IInventory` interface (all inventories): `AddQuantity`, `RemoveQuantity`, `GetQuantity`, `GetStackCount`, `MaxStacks`, `StackCount`, `Stacks` (`IReadonlyItemStack`: `Item`, `Quantity`), `ItemQuantityChanged`, `IsTransactionValid`, `CreateTransaction()`.
- Transactions (`GBM.Core.InventoryWalletTransactions`): `InventoryWalletTransaction(inv, wallet)` with `.Items.Add/Remove`, `.Currencies`, `RemoveIngredients(RecipeIngredients, multiplier)`, `Validate()`, `Execute()` → `InventoryWalletTransactionResult { Success, NotEnoughInventorySpace, NotEnoughCurrencySpace, ... }`. Cost display: `GBM.Core.CostTracking.CostTracker`.
- UI: `ItemStackGridScroller` (private `StackViewSelectedHandler`, `Query`, `Inventory`) **[M2T patch]**, `ItemStackButton.Stack` **[M2T]**, `BankTabInventory` **[M2T]**, `SellItemOptions` (`_Ready`, `BindItemStack`, private `SellButton`, `QuantityOptions.QuantitySpinbox`) **[M2T patch]**, `SelectedItemView`, `ItemInfoPanel`, `BankActiveView`.

### 4.3 Toolbelt and equipment slots
- `ToolbeltComponent` (per skill entity, key `Toolbelt`; combat has one too): `Config.ToolbeltSlots`, `TryGetActiveSlot(slot, out IActiveToolbeltSlot)` (`.Equipped`, `.Quantity`), `GetAvailableSlotsForItem`, `TryEquipItem`, `TryUnequipItem`, `TryAddQuantity`, `IsItemEquipped`, `GetEquippedQuantity`, set bonuses; events `ItemEquipped/Unequipped/QuantityChanged`, `ItemConsumed`. Equip from bank: `ToolbeltEquippingComponent.EquipItem(item, qty, ToolbeltSlot)` / `UnequipSlot(slot)`.
- Data: `ToolbeltSlot` (`FitsType: ToolbeltSlotType`, `UnlockRequirements`, `GridPosition`), `ToolbeltSlotType` (`Category`), `EquipmentSlot` (`FitsType: EquipmentType`, `UnlockRequirements`, `GridPosition`), equipment slots listed in `CombatEntityConfig.EquipmentSlots`.

### 4.4 Recipes - how costs and products are represented

**Shared building blocks** (`GBM.Data.Recipes`): `RecipeIngredients` (`ReadOnlyCollection<ItemQuantity> Items`, `ReadOnlyCollection<CurrencyQuantity> Currencies`), `RecipeCategory` (`HuntingLocation` etc. derive from it), `RecipeSubcategory`, `RecipeGoalCategory`; `ConditionGroup Requirements` (`AreMet`, `Conditions`, `AllMet` event). Interfaces (`GBM.Components.CraftingAction`): `ICraftingRecipe` (implemented by every recipe-like class: Artisan, Cooking, Herblore, Firemaking, Farming, GrainMill, Woodcutting, Mining, Fishing, Thieving, AgilityCourse, RanchingAnimal), `IActiveCraftingRecipe` (`Recipe`, `Stats`), **`ICraftingActionStats`** (`ItemCost: ValuesStatNode<ItemQuantity>`, `CurrencyCost`, `Product?`, `Products?: ValuesStatNode<ItemQuantityChance>`, `Interval`, `PreservationChance`, `Experience`) - the **modifier-adjusted** costs/products, implemented by `ArtisanActionStats`, `CookingActionStats`, `HerbloreActionStats`, `FiremakingActionStats`, `FarmingActionStats`, `GrainMillStats`. Read values: `stats.ItemCost.Values` → `ValueStatNode<ItemQuantity>.Value` **[M2T]**; scalar stats `ValueStatNode<double>.Value`; chances `ModifierStatNodes.Total.Value`.

**Item PRODUCERS** (to build "item X ← which action gives it"):
| Source | Where the item is |
|---|---|
| Woodcutting `WoodcuttingRecipe` | `Product.Item`; `RandomProductsPerCut[]`, `RandomProductsPerHit[]` (`ItemQuantityChance`) |
| Mining `MiningRecipe` | `Product.Item`; `RandomProductsPerHit[]`; `BarItem`; (`StoneItem`, `EssenceItem` are informational); every rock can also give `MiningEntityConfig.StoneItems` / `EssenceItems` |
| Fishing `FishingRecipe` | `RandomProducts` (weighted `FishingProduct.Item`); `JunkItem`; `SpecialProducts[]` |
| Artisan (Smithing/Crafting/Fletching/Runecrafting/Beastcrafting) `ArtisanRecipe` | `Products[]` (`ItemQuantityChance`); skill = which `<Skill>EntityConfig.Recipes` lists it; alt modes: `AstralOfferingsRewardMode.Rewards`, `SacrificialAnvilRewardMode.Collectables` |
| Cooking `CookingRecipe` | `Products[]` (one chosen by `Chance`) |
| Herblore `HerbloreRecipe` | `Products[]` |
| Firemaking `FiremakingRecipe` | `Products[]`, `CurrencyReward`; `FiremakingEvent.Reward` |
| Farming `FarmingRecipe` | `Product` |
| Grain mill `GrainMillRecipe` | `Product` |
| Ranching `RanchingAnimal` | `Products[]` (`ItemQuantity`) |
| Hunting `HuntingAnimal` | `Products[]`, `SurveyingProducts[]`, `RareSpecies` → `HuntingAnimalSpecies.ExtraProducts` |
| Thieving `ThievingRecipe` | `CommonDropTable`, `RareDropTable`, `CompletionDropTable` (weighted), `CurrencyReward` |
| Agility | `AgilityCoursePath.RandomObstacleProducts`, `AgilityCourse.CompletionRewards` / `TimedCompletionRewards` (`RecipeIngredients`-shaped rewards) |
| Combat `Enemy.Drops` / `Dungeon.Rewards` (`CombatDrops`) | `FixedItems[]` (`ItemQuantityRange`), `ItemTables[]` (`ItemDropTableChance.Table`), `FixedCurrencies[]` |
| Openables | `OpenableCapability.AlwaysDrops`, `.DropTable` |
| Shop `ShopPurchase` | `Provides.Items` / `Currencies` / `Bonuses` (`ShopPurchaseProvides`) |
| Item upgrades `ItemUpgrade` | `UpgradedItem` × `QuantityPerUpgrade` |
| Rare events (every skill) | `RareItemEvent.Item`, `RareTableEvent.Events`; per skill `Config.GlobalActionEvents` / `GlobalTimedEvents` |
| Astrology | `AstrologyItemEvent.Item`, `AstrologyTableEvent.Events` (in `AstrologyConstellation.Events` / `AstrologyEntityConfig.GlobalEvents`) |
| Quests / mastery | `Quest.Rewards.Items` (`Rewards`), `MasteryGuildBonus.Rewards.Items` |

**Item CONSUMERS**:
| Consumer | Where |
|---|---|
| Artisan, Cooking, Herblore, Firemaking, Farming, Grain mill | `Ingredients` (`RecipeIngredients`); farming also `AutomationCosts` |
| Ranching | `RanchingAnimal.PurchaseCosts`, `.Eats`, `.Treats`; `RanchingEnclosure.Costs`; `RanchingEnclosureUpgrade` |
| Agility | `AgilityCourse.EntryCost`, `AgilityObstacle.ShortcutCosts` |
| Hunting | traps (`HuntingTrapCapability`, 1 per trap attempt), `HuntingAnimal.PreferredBait` |
| Fishing | bait items (`FishingBaitCapability`, `FishingBaitComponent.BaitItems`) |
| Magic | `BaseMagicSpell.RuneCosts` |
| Combat | ammo (`EquipmentCapability.AmmoType`), food, items with `ConsumesOn` |
| Shop / upgrades / plots / bonfires | `ShopPurchase.Costs`, `ItemUpgrade.Costs`, `FarmingPlot.Costs`, `FiremakingBonfire.Costs`, `SlayerMaster.RerollCost/BlockCost` (currency) |
| Quests | `QuestTask.Costs` |
| Farming compost | `CompostCapability` items |

Skill ↔ recipe list: iterate `game.Data.ConfiguredEntities` or the typed `Config` of each entity (`WoodcuttingEntityConfig.Recipes`, `MiningEntityConfig.Recipes`, `FishingEntityConfig.Recipes/Locations`, `<Artisan>EntityConfig.Recipes/Categories/RewardModes`, `CookingEntityConfig.Recipes`, `HerbloreEntityConfig.Recipes/PotionGroups`, `FiremakingEntityConfig.Recipes/Bonfires/Events`, `FarmingEntityConfig.Recipes/Plots/GrainMill/GrainMillRecipes`, `RanchingEntityConfig.Animals/Enclosures/Land`, `HuntingEntityConfig.Animals/Locations`, `ThievingEntityConfig.Recipes/Categories`, `AgilityEntityConfig.Courses/Obstacles`, `AstrologyEntityConfig.Constellations/GlobalEvents`, `CombatEntityConfig.Areas/Spells/Prayers/EquipmentSlots`, `ShopEntityConfig.Purchases`, `SlayerEntityConfig.SlayerMasters`, `QuestsEntityConfig.Quests`, `CompletionLogEntityConfig.ItemCompletionOrder`). **There is no built-in reverse index** (item → sources/uses): the only "used for" hint is `Item.BankTags[].IsProduct/IsIngredient`.

---

## 5. Progression systems

### 5.1 Experience / levels (`GBM.Components.Experience`)
- `ExperienceComponent` (key `Experience`): `Xp`, `Level` **[M2T]**, `LevelCap` (**99**, `new BaseExperience(table, 99)`), `IsAtLevelCap`, `XpToNextLevel`, `ProgressToNextLevel`, `Table`, `AddStatsXp(ExperienceStats[, multiplier])` (modifier-adjusted), `AddRawXp(double)`, `SetXp(double)`, `SetLevelCap(int)`, `ToggleXpGain(bool)`; events `XpChanged`, **`LevelChanged`** (`ValueChangedEventArgs<int>`: `OldValue`, `NewValue`) **[M2T]**, `LevelCapChanged`.
- `StandardExperienceTable.Default = new(0.25, 300.0, 2^(1/7))` - the classic RuneScape curve (`GetXpForLevel`, `GetLevelFromXp`). Agility techniques use `AgilityTechniqueExperienceTable`; hunting journal entries have their own `Experience`.
- Level-up side effects (UI-driven): `PassiveEntityView.ExperienceLevelChangedHandler` → announcement or `QueuedMaxSkillLevelModal` at cap; `EventLogManager.LogSkillLevelUpEvent`. Conditions/goals: `LevelCondition`, `ExperienceCondition`, `ExperienceGoal`, triggers `GainLevelTrigger`, `GainExperienceTrigger`.

### 5.2 Completion log (`CompletionLogEntity`, id `MelvorBase:CompletionLog`)
`CompletionComponent` (`TotalProgressDictionary["MelvorBase"]`, `TotalProgressChanged`) aggregating `SkillCompletion`, `ItemCompletion` (items found; `Item.IgnoreCompletion`), `MasteryGuildCompletion`, `PetsCompletion`, `EnemyCompletion`, `QuestsCompletion`.

### 5.3 Quests (`GBM.Entities.Quests`)
`QuestsEntity.Quests` (`QuestsComponent`): `SelectedQuest` (`ActiveQuest`), `CompletedQuestCount`, `ExecuteTaskCosts(activeTask)`, `ClaimRewards(quest)`, `CompleteQuest(quest)`, `CompleteAllQuests()` (debug), NPC customisation; events `QuestStarted`, `QuestStepCompleted`, `QuestTaskGoalCompleted`, `QuestCompleted`, `QuestRewardsGiven`, `QuestStepCostsConsumed`. Data: `Quest` (`Category`, `Steps: QuestStep` → `QuestTask` (`Goals`, `ConditionGoals`, `Costs`), `Rewards`, `Requirements`), `NPC`, `QuestCategory`. Onboarding tasks: `GBM.Components.Onboarding` (`OnboardingTask`, `OnboardingComponent`).

### 5.4 Shop (`ShopEntity`, `ShopComponent`)
`Config` (`Categories`, `Subcategories`, `Purchases`), `IsPurchased(p)`, `GetUpgradesPurchased(p)`, `CapPurchaseQuantity(p, qty)`, `GetPurchaseCostTracker(p)`, **`ProcessPurchase(ShopPurchase, int qty)`** → `InventoryWalletTransactionResult`, `RequestQuickBuyForPurchase/ForItem` (raises `RequestQuickBuy` → UI popup), `PurchaseMode`, `HoldToBuy`; event `PurchaseBought`. `ShopPurchase`: `Provides` (items/currencies/permanent `Bonuses`), `Costs`, `DefaultBuyLimit` (0 = unlimited), `AllowBulkPurchase`, `Requirements`, `Subcategory`.

### 5.5 Statistics
`StatisticsComponent` per entity (`IStatisticsEntity.Statistics`, e.g. `WoodcuttingStatistics`), `CharacterStatisticsEntity.Statistics` (`PlaytimeStatistic`, ...), `Bank.Statistics.ItemFound`. Types: `ValueStatistic` (`SimpleStatistic`, `AggregateStatistic`), `ItemStatistic`, `CurrencyStatistic`, `DictionaryStatistic`; action stats `ItemGainedStatistic`, `ItemSpentStatistic`, `CurrencyGainedStatistic`, `TotalTimeSpentStatistic` (+Online/Offline). UI `StatisticsMenu`.

### 5.6 Rare events (all skills) and astrology bonus events
- `RareEventsComponent` (key `RareEvents`): rolls `Config.GlobalActionEvents` per action and `GlobalTimedEvents` per minute (`TimedEventTimer`); `ActiveBonuses` (`ActiveRareBonusesEvent`: `Activate()` **[M2T]**, `IsActivated`, charges), `SetActiveBonuses(RareBonusesEvent)` **[M2T patch]**, `RemoveActiveBonuses()`, `ExecuteEvent`, `GrantRandomItems`; events `EventObserved`, `RareBonusesEventActivated`, `TimedEventTimerFired`.
- `RareBonusesEvent.CanOccur` = `component.ActiveBonuses == null` → **an un-activated (or still active) bonus blocks all new bonus events** for that skill. The UI's modal "Confirm" calls `Activate()`.
- Event types: `RareItemEvent` (`Item`, `BaseQuantity`, `LevelScalingQuantity`), `RareTableEvent` (weighted sub-events), `RareBonusesEvent` (`Bonuses`, `Charges`, `ConsumesOn`). Astrology equivalents in §3.7.

### 5.7 Modifiers / stats (brief)
`GBM.Modifiers.ModifierSystem` (private `Game.Modifiers`, also `ICoreSystems.Modifiers`): `Add(INamedObject source, ModifierValues, multiplier)`, `AddOrUpdate`, `Update`, `Remove(source[, values])`, `CreateQueryForStat`. Stats objects (`*ActionStats`) expose `ModifierStatNodes` (`.Total.Value`) and `ValueStatNode<T>` (`.Value`) computed from data + modifiers; stat ids look like `"MelvorBase:WoodcuttingAction"` stat class + stat name (e.g. `FlatPrayerCost`). `ModifierValues` come from data (bonuses on items, pets, shop, astrology...). Constructing `ModifierValues` in code has not been tried.

### 5.8 Time & weather, tracker, cloud
- `TimeAndWeatherEntity`: `TimeOfDay` (`TimeOfDayComponent`, `DaySegment`s) and `Weather` (`WeatherType`); affects ranching, combat backgrounds/areas (`Darkness`).
- **Tracker footer**: `TrackerComponent` (key `Tracker` on each skill; combat `CombatTrackerComponent`): samples XP/items/currency per period (`SelectedPeriodIndex`, `LevelGoal`, `IsPaused`, `CurrentTick`, `TrackInventory/Wallet/Experience/Action/Equipment/Toolbelt`), offline snapshot categories. UI: `TrackerComponentFooterView`, `TrackerBankView` (private `TimeUntilDepleted`, `TimeUntilDepletionLabel`, `UpdateDepletionLabelText()` **[M2T patch: "~N actions"]**), `TrackerExperienceView`, `TrackerEquipableView`.
- **Cloud / online**: Melvor Cloud login (Nakama + Satori, obfuscated `A`/`B` classes, token in `%APPDATA%\...\data\` - never touch), `CloudSaveSource`, cloud autosave timer in `GameDriver`, Steam Cloud for local saves, `GBM.RichPresence`, `Platforms.Steam`. Login page skippable (`Interface.SkipLoginPage`). Leave alone.

---

## 6. Hooks and gotchas

### 6.1 Useful events (subscribe, no patch needed)
| Event | Owner |
|---|---|
| `ActionStarted` / `ActionStopped` | `game.ActionManager` |
| `Started`, `Stopped`, `Ticked`, `ItemAdded`, `CurrentDescriptionChanged` | any `ActionComponent` |
| `Inventory.ItemAdded` | the action's `TrackableInventory` **[M2T]** |
| `ItemQuantityChanged`, `StacksChanged` | bank `Inventory` |
| `CurrencyQuantityChanged` | bank `Wallet` |
| `LevelChanged`, `XpChanged` | `ExperienceComponent` **[M2T]** |
| `RecipeCreated`, `PrimaryProductCreated`, `ItemPreserved` | crafting actions |
| `TreeCut`, `RockMined`, `RecipeFished`, `ThievingActionPerformed`, `CourseCompleted`, `ConstellationStudied`, `AnimalTrapped`, `RecipeHarvested`, `PotionBrewed` | per skill |
| `PlayerDied`, `EnemySpawnStarted`, `StateChanged` (combat); `Died` (thieving/agility) | |
| `EnemyLooted`, `DungeonCompleted`, `TaskCompleted` (slayer) | combat |
| `PetUnlocked`, `TaskCompleted`/`CurrentRankChanged` (mastery), `QuestCompleted` | |
| `EventObserved` (rare/astrology), `RareBonusesEventActivated` | |
| `ItemSold`, `ItemsOpened`, `ItemBuried`, `ItemUpgraded`, `PurchaseBought` | bank / shop |
| `EntryAdded` | `game.EventLog` |
| `Game.SaveLoaded`, `SaveManager.LocalSaved` | |
| `game.EventBus.ActionPerformed.Subscribe(...)` (via Traverse) | global per-action hook |

### 6.2 Good Harmony targets
| Target | Why |
|---|---|
| `Game.PropagateTick` | per-tick logic **[M2T]** |
| `GameDriver._PhysicsProcess` | frame-level (not run offline) **[M2T]** |
| `<Skill>ActionComponent.ActionTimerFiredHandler` (private) | before/after each action (prefix returning false skips it) **[M2T: Agility, Thieving]** |
| `ActionComponent.TryStart/TryStop/QueueOrTryStop`, `ActiveActionManager.TryStopActive` | action lifecycle **[M2T]** |
| `ActionComponent.TryAddItem` (protected) | every item an action gives **[M2T]** |
| `BankInventoryComponent.AddQuantity` | every item into the bank (`AddItemFlags.Found` = new drop) **[M2T]** |
| `ModalManager.QueueModal` | suppress/redirect any popup |
| `RareEventsComponent.SetActiveBonuses`, `AstrologyActionComponent.SetActiveBonuses` | bonus found **[M2T]** |
| `FoodComponent.AutoEat` (private) | auto-eat **[M2T]** |
| `CombatActionComponent.OnTick` / `SpawnTimerFiredHandler` / `PlayerDiedHandler` | combat **[M2T]** |
| `ActiveFarmingPlot.PerformStateChangeAutomation` / `PerformAutoHarvest` | farming automation **[M2T]** |
| `EventLogManager.LogEvent` | event log **[M2T]** |
| `GameView.OnFirstShow`, `SettingsPanel._Ready`, `SellItemOptions._Ready/BindItemStack`, `FarmingActionView._Ready`, `TrackerBankView.UpdateDepletionLabelText` | UI injection **[M2T]** |
| `Main+GameState.OnLoad/OnEntry/OnExit/OnSleepWake` (via `AccessTools.Inner(typeof(Main), "GameState")`) | session lifecycle, offline **[M2T]** |

### 6.3 Gotchas
1. **Toggle selects**: `SelectTree`, `SelectRock`, Fishing `SelectRecipe`, `TryStartActiveRecipe`, `SelectCourse`, `StudyConstellation`, `StartTracking`, crafting `StartCreating()` all **stop** the action when called with the target that is already running. Check `IsStarted` + current target first **[M2T does]**. Crafting `SelectRecipe` itself is not a toggle.
2. **Combat select is not a toggle and not gated**: `Areas.SelectEnemy/SelectDungeon` restart spawning every call and ignore `UnlockRequirements`.
3. **One action at a time**: `TryStart` stops the current action first (`TryStopActive`); failures call `OnStartFailed`.
4. **Stop from handlers with `QueueOrTryStop()`**: during `ActionTimerFiredHandler` (`IsPerformingAction`) the stop is deferred via `QueueStop`; handlers then call `TryStop(force: true)`.
5. **Bank full**: `ActionComponent.TryAddItem` sets `QueueStop` (if `StopActionOnBankFull`) even for side drops (gems, nests, junk) **[M2T works around]**. Farming harvest on a full bank just logs and waits; farming automation never retries without a state change **[M2T retries]**.
6. **Offline ticks run on a worker thread** (`Task.Run(RunOfflineTicks)`) with UI events unhooked: anything patched on the tick path must not touch Godot nodes/scene tree; modal-raising UI handlers don't fire offline. `Game.Flags.IsOffline` tells you.
7. **Private core objects**: `Game.Bank`, `Game.Combat`, `Game.Modifiers`, `Game.EventBus`, `CombatActionComponent.Areas`, `BankInventoryComponent.LockedItems`, `ItemSaleComponent.Wallet`, `SaveManager.ActiveSaveId`, `GameView.CurrentPage`/`DebugPopup` - use `Traverse`/`AccessTools`.
8. **Requirements are not checked by select/start methods** - check `Requirements.AreMet` (and `DisplayRequirements`, `AccessRequirements` for thieving) yourself.
9. **Use `Stats`, not raw data, for real numbers** (costs, intervals, product quantities are modifier-adjusted): `activeRecipe.Stats.ItemCost`, `.Interval.Value`, etc.
10. **Selling ignores locked items** - `ItemSaleComponent.SellItem` does not check locks; check `LockedItems` **[M2T does]**.
11. **Death penalties**: combat death loses a random equipped item (10% of a stack); thieving/agility death loses a random toolbelt item. Auto-eat triggers at 25% HP *after* the hit **[M2T DeathGuard]**.
12. **First page visit pops a tutorial** (`ITutorialEntity.FirstTime`) - don't navigate to unopened skills from automation.
13. **Data dictionaries are keyed by data objects** (`Trees[WoodcuttingRecipe]`, `ActiveRecipes[ArtisanRecipe]`) - look up by `Id` when you only have a string **[M2T Lookup]**.
14. **`TickTimer.WaitTime` < `TickInterval` throws**; intervals are rounded to whole ticks.
15. **Event log cap** is a hard-coded 100 in `LogEvent` **[M2T transpiles]**.
16. **Hot reload / mid-game load**: `GameView.OnFirstShow` has already run; attach to existing nodes too and undo everything in `ModUnload` **[M2T pattern]**.
17. The UI node tree must be matched by duplicating game nodes (stock Godot controls look wrong in this theme) - see memory notes in the Melvor2 setup file.

---

## 7. Ideas not yet built

| Idea | Hooks |
|---|---|
| **Item sources / uses browser** ("where do I get X", "what uses X") - none exists in game | §4.4 producer/consumer tables; build an index at `OnFirstShow` from all entity `Config`s + `Enemy.Drops` + `OpenableCapability` + `ShopPurchase` |
| **Recursive "craft N of X" planner** that queues the whole chain (ore → bar → item) in the existing action queue | §4.4 + `ICraftingActionStats` + `Melvor2Tweaks.Actions.ActionQueue` |
| **Popup suppression**: pet found, rare event, max level, navigation unlocked → event log/toast only | prefix `ModalManager.QueueModal`, filter by `QueuedModal` subtype |
| **Astrology auto-unlock nodes** when `AvailableMinorNodeCount/AvailableMajorNodeCount > 0` | `ActiveAstrologyConstellation.UnlockMinorNode/UnlockMajorNode`, `MinorNodeUnlockKnowledge` |
| **Agility auto-shortcut** builder when affordable | `ActiveAgilityObstacle.CreateShortcut()`, `ShortcutCosts` |
| **Auto-buy consumables** (bait, traps, seeds, runes, food) when low | `ShopComponent.ProcessPurchase`, `RequestQuickBuyForItem` source data |
| **Auto-upgrade items** above a keep amount (extend item rules) | `ItemUpgradingComponent.ExecuteUpgrade` |
| **Best-target advisor** (GP/XP per hour for thieving targets, trees, rocks, fishing spots) | `Stats.Interval`, `GetSuccessChance`, drop tables, `ItemSales.GetItemStats` |
| **Loadout per combat area / auto prayer** | `LoadoutComponent.SwapToLoadout`, `PrayerComponent.TogglePrayer` |
| **Slayer helper** (auto-reroll unwanted tasks, jump to task area) | `ActiveSlayerMaster.RerollTask`, `GetCurrentTaskArea`; native `AutoSlayerEnabled` exists |
| **Herblore/cooking chain**: switch recipe when ingredients run out instead of stopping | patch `CraftingActionComponent.OnRecipeCreated` (stop branch) |
| **Ranching**: auto-cure sick animals, treat management (pet/repair done) | `ActiveRanchingAnimal.CureIllness`, `RanchingPassiveComponent.ToggleTreat` |
| **Grain mill auto-collect / auto-select next grain** | `FarmingGrainMillComponent.CollectReadyItems`, `SelectRecipe`, `ToggleGrainMillTimer(true)` |
| **Open the hidden debug menu** for testing (give items, levels, run offline) | `GameView.DebugPopup` (`DebugMenu : PopupPanel`) → `.Popup()` |
| **Data dump / content mod**: export `res://Data/Packages/MelvorBase.json`; edited copy via `Debug.LoadLocalDataPackages` + `user://local_data/MelvorBase.json` | §1.6 |
| **Mastery guild task tracker** widget (closest tasks per skill) | `MasteryGuildComponent`, `ActiveMasteryGuildTask`, `TaskProgressed` |
| **XP/hr + time-to-level** in the header for the running skill | `TrackerComponent`, `ExperienceComponent.XpToNextLevel` |
| **Offline cap tweak** (beyond 24 h) | `OfflineProgressComponent.MaxOfflineTime` getter patch (time bank already sidesteps it) |
