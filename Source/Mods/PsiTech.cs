using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>PsiTech by K</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=2078892294"/>
    /// <remarks>
    /// FIXES row 145 (20 Oct 2026). Written against <c>lab/game/Mods/2078892294/v1.7/Assemblies/PsiTech.dll</c> (the loadFolders v1.7 copy
    /// overrides <c>Assemblies/PsiTech.dll</c>). Everything is registered late (<see cref="LongEventHandler.ExecuteWhenFinished"/>).
    ///
    /// A. Lab gate audit gaps 31 and 32 - float-menu options in a psycast's autocast settings (pawn Psi tab, <c>PsiTech.Interface.AbilityWindow</c>)
    /// that edit the filter owned by <c>PsiTechAbility.AutocastFilter</c> (<c>Scribe_Deep</c>, read by the autocast sim): 'Add additional target
    /// filter' (local function <c>Action()</c> in <c>AutocastFilter.GenerateAdditionalFilterOption</c>) and 'Target selector' (<c>Action()</c> in
    /// <c>AutocastFilter_SingleTarget.GenerateSelectorOption</c>). Their closures capture <c>this</c> (the filter) and <c>entry</c> (a Def), so the
    /// closure is a synced delegate and the filter needs a SyncWorker.
    ///
    /// B. Same window, the other edits (row 145 follow-up). Everything the window changes is written straight into saved objects, so:
    /// - Edits made by widgets inside <c>AbilityWindow.DoWindowContents</c> (tracker <c>HideAutocastToggleGizmo</c>, ability <c>Autocast</c>, every
    ///   condition's <c>Inverted</c>/<c>Threshold</c>, the filter's <c>TargetRange</c>/<c>MinSuccessChance</c>/<c>InvertSelector</c>/<c>MinTargetsInRange</c>,
    ///   every additional filter's <c>Inverted</c>/<c>Threshold</c>): MP's watch pattern. <c>MP.WatchBegin</c> + <c>ISyncField.Watch</c> on those objects
    ///   in a prefix of <c>DoWindowContents</c>, <c>MP.WatchEnd</c> in a finalizer. A changed field is put back at once and sent as a sync field
    ///   command; the numeric ones are buffered (200 ms) so typing/dragging sends the last value only.
    /// - Edits made by float-menu options (<c>Priority</c>, <c>FilterTargetType</c>): the option's closure is a synced delegate (the float menu is a
    ///   separate window, so a watch around <c>DoWindowContents</c> cannot see these).
    /// - 'Add condition' (a local function on the window, <c>this</c> = the window, which cannot be sent): a prefix replaces it with the synced static
    ///   <c>SyncAddCondition</c> (ability, condition def), which builds the condition exactly as the original does.
    /// - 'Remove' on a condition (<c>AbilityWindow.ScheduleConditionForRemoval</c>, resolved by the window next frame) and on an additional filter
    ///   (<c>AutocastFilter.MarkAdditionalFilterForRemoval</c>, resolved by <c>ResolveRemovedFilters</c> inside <c>Draw</c>): the condition one becomes the
    ///   synced static <c>SyncRemoveCondition</c>; the filter one is MP-synced as it is, and a postfix runs <c>ResolveRemovedFilters</c> right after the
    ///   synced call so the list changes on every PC without waiting for a window to draw.
    ///
    /// How a PsiTech object is found again on the other PC: every worker and static helper identifies the OWNING ABILITY as (pawn =
    /// <c>PsiTechAbility.User</c>, ability def, <c>Slot</c>, <c>LoadId</c>). The reader takes the pawn's PsiTechTracker from the PsiTechManager game
    /// component's private <c>trackers</c> dictionary (never through <c>Pawn.PsiTracker()</c>, which creates) and picks the ability in
    /// <c>tracker.Abilities</c> with that def, slot and LoadId (LoadId = the manager's saved <c>nextAbilityId</c> counter). From the ability:
    /// the filter = <c>AutocastFilter</c>; a condition = the entry of <c>AutocastConditions</c> with the same Def; an additional filter = the entry of
    /// <c>AdditionalFilters</c> with the same Def (the window offers each def once per list, so the Def is a stable key and no list index travels:
    /// a double click on Remove finds nothing the second time and is dropped). No match gives null and the command is dropped.
    ///
    /// C. UI code must not create trackers. <c>PsiTechManager[Pawn]</c> creates a PsiTechTracker on first use and takes a number from the saved
    /// <c>nextTrackerId</c>; <c>PsiTechTracker.TrackerTick</c> runs its stat refresh when <c>(TicksGame + loadId) % 60 == 0</c>, so a tracker created
    /// by UI code on one PC only (tab visibility <c>ITab_Pawn_Psi.IsVisible</c>, <c>PawnGizmosPatch</c>, tooltips, stat explanations) gives every later
    /// tracker another id and another tick phase there. The sim creates trackers for practically every pawn anyway (<c>HediffSet.DirtyCache</c>,
    /// <c>PawnCapacityPatch</c>, <c>StatWorker.GetValueUnfinalized</c> and the AI think nodes all call <c>PsiTracker()</c>). A prefix on the manager indexer,
    /// only while <c>MP.InInterface</c> (so ticks, synced commands and loading keep the mod's behaviour), hands UI callers a PLACEHOLDER tracker
    /// (same constructor, <c>InitializeCaches</c> and <c>Notify_EssenceDirty</c> as the original, id -1, kept in a private dictionary of this module, not in
    /// the manager and not counted). When the sim needs the pawn's tracker it gets the real one made by the original code, and the placeholder is dropped.
    /// Same idea and shape as the Big and Small module's <c>PreGetCache</c>.
    ///
    /// Per-PC reads inside the synced bodies: none (they use only the objects found above, Defs and DefDatabase lists, no selection, no
    /// <c>Find.CurrentMap</c>, no settings, no Rand).
    /// NOT covered: the rest of <c>PawnPsiCard</c> / <c>ITab_Pawn_Psi</c> (energy and focus nodes, training queue, ability slots) edits the tracker
    /// directly as well.
    /// Status: [unverified] - compiled, names and Harmony patch binding checked offline, not run in game.
    /// </remarks>
    [MpCompatFor("K.PsiTech")]
    public class PsiTech
    {
        private const string LogPrefix = "[MPCompat - PsiTech] ";
        private const string NS = "PsiTech.AutocastManagement.";

        // types
        private static Type managerType, trackerType, abilityType, windowType;
        private static Type filterType, singleTargetType, burstType;
        private static Type conditionType, condThresholdIntType, condThresholdPercentType;
        private static Type additionalType, addThresholdIntType, addThresholdPercentType;

        // PsiTechManager / PsiTechTracker
        private static FieldInfo managerTrackersField, trackerAbilitiesField, trackerPawnField;
        private static ConstructorInfo trackerCtor;
        private static MethodInfo trackerInitializeCaches, trackerNotifyEssenceDirty;

        // PsiTechAbility
        private static FieldInfo abilityDefField, abilitySlotField, abilityLoadIdField, abilityUserField;
        private static FieldInfo abilityFilterField, abilityConditionsField;

        // AutocastFilter, AutocastCondition, AdditionalTargetFilter
        private static FieldInfo filterAbilityField, filterAdditionalFiltersField;
        private static FieldInfo conditionAbilityField, conditionDefField, conditionUserField, conditionDefClassField;
        private static FieldInfo additionalAbilityField, additionalDefField;
        private static MethodInfo filterResolveRemoved;

        // AbilityWindow
        private static FieldInfo windowPawnField, windowCurAbilityField;
        private static MethodInfo addConditionMethod;
        private static FieldInfo addConditionEntryField, addConditionThisField;

        // sync fields watched around AbilityWindow.DoWindowContents
        private static ISyncField trackerHideSync, abilityAutocastSync;
        private static ISyncField conditionInvertedSync, condThresholdIntSync, condThresholdPercentSync;
        private static ISyncField filterTargetRangeSync, singleMinSuccessSync, singleInvertSelectorSync, burstMinTargetsSync;
        private static ISyncField additionalInvertedSync, addThresholdIntSync, addThresholdPercentSync;

        // UI placeholders (point C)
        private static readonly Dictionary<Pawn, object> uiTrackers = new();
        private static Game uiTrackersGame;
        private static bool loggedWatchError, loggedPlaceholderError, loggedPatchError;

        public PsiTech(ModContentPack mod) => LongEventHandler.ExecuteWhenFinished(LatePatch);

        #region Registration

        private static void LatePatch()
        {
            try
            {
                Resolve();
            }
            catch (Exception e)
            {
                Log.Error($"{LogPrefix}FIXES row 145: PsiTech types/fields did not resolve, nothing was registered: {e}");
                return;
            }

            // 1.7 lab, FIXES row 145: the PsiTech objects the window edits (found again from their owning ability, see the class remarks). Implicit, so
            // the subclasses (AutocastFilter_SingleTarget/_Burst, PsiTechAbilityTargeted..., the condition and filter classes) use them too.
            if (!Try("sync workers", () =>
                {
                    MP.RegisterSyncWorker<object>(SyncAutocastFilter, filterType, isImplicit: true);
                    MP.RegisterSyncWorker<object>(SyncAbility, abilityType, isImplicit: true);
                    MP.RegisterSyncWorker<object>(SyncTracker, trackerType, isImplicit: true);
                    MP.RegisterSyncWorker<object>(SyncCondition, conditionType, isImplicit: true);
                    MP.RegisterSyncWorker<object>(SyncAdditionalFilter, additionalType, isImplicit: true);
                }))
                return;

            // 1.7 lab, FIXES row 145: gap 31 - 'Add additional target filter' = local function Action() in AutocastFilter.GenerateAdditionalFilterOption,
            // closure AutocastFilter+<>c__DisplayClass27_0 (fields: entry = AdditionalTargetFilterDef, <>4__this = the filter), IL name
            // `<GenerateAdditionalFilterOption>g__Action|0`: creates the AdditionalTargetFilter, sets User/Ability/Def, `AdditionalFilters.Add(...)`, RebuildFilterCache().
            Try("gap 31 add additional target filter",
                () => MP.RegisterSyncDelegateLocalFunc(filterType, "GenerateAdditionalFilterOption", "Action").CancelIfAnyFieldNull());

            // 1.7 lab, FIXES row 145: gap 32 - 'Target selector' = local function Action() in AutocastFilter_SingleTarget.GenerateSelectorOption,
            // closure AutocastFilter_SingleTarget+<>c__DisplayClass24_0 (fields: entry = AutocastFilterSelectorDef, <>4__this = the filter), IL name
            // `<GenerateSelectorOption>g__Action|0`: creates the AutocastFilterSelector, sets its Def, `Selector = autocastFilterSelector`.
            Try("gap 32 choose target selector",
                () => MP.RegisterSyncDelegateLocalFunc(singleTargetType, "GenerateSelectorOption", "Action").CancelIfAnyFieldNull());

            // 1.7 lab, FIXES row 145: priority dropdown - option closure AbilityWindow+<>c__DisplayClass36_1 `<GeneratePriorityOptions>b__0` =
            // `ability.Priority = priorityType;` (fields: priorityType = AbilityPriority, CS$<>8__locals1/ability = the ability).
            Try("priority dropdown", () =>
            {
                foreach (var del in MpCompat.RegisterLambdaDelegate(windowType, "GeneratePriorityOptions", 0))
                    del.CancelIfAnyFieldNull();
            });

            // 1.7 lab, FIXES row 145: FilterTargetType dropdown (single target and burst filters) - option closure AutocastFilter+<>c__DisplayClass30_0
            // `<GenerateTargetTypeOptions>b__0` = `FilterTargetType = targetType;` (fields: targetType, <>4__this = the filter).
            Try("filter target type dropdown", () =>
            {
                foreach (var del in MpCompat.RegisterLambdaDelegate(filterType, "GenerateTargetTypeOptions", 0))
                    del.CancelIfAnyFieldNull();
            });

            // 1.7 lab, FIXES row 145: 'Add condition' / 'Remove' on a condition. The window cannot be sent, so the clicks go through these static methods.
            Try("SyncAddCondition", () => MP.RegisterSyncMethod(typeof(PsiTech), nameof(SyncAddCondition)));
            Try("SyncRemoveCondition", () => MP.RegisterSyncMethod(typeof(PsiTech), nameof(SyncRemoveCondition)));

            // 1.7 lab, FIXES row 145: 'Remove' on an additional filter: `AutocastFilter.MarkAdditionalFilterForRemoval(filter)` (only queues the filter in
            // `filtersToRemove`; `ResolveRemovedFilters` inside Draw removes it) is synced as it is (target = the filter, arg = the additional filter), and the
            // postfix below resolves the queue right after the synced call.
            Try("MarkAdditionalFilterForRemoval", () =>
            {
                MP.RegisterSyncMethod(filterType, "MarkAdditionalFilterForRemoval");
                MpCompat.harmony.Patch(AccessTools.DeclaredMethod(filterType, "MarkAdditionalFilterForRemoval"),
                    postfix: new HarmonyMethod(typeof(PsiTech), nameof(PostMarkAdditionalFilterForRemoval)));
            });

            // 1.7 lab, FIXES row 145: the fields the window's widgets edit directly (watched around DoWindowContents). Numeric ones are buffered.
            Try("sync fields", () =>
            {
                trackerHideSync = MP.RegisterSyncField(trackerType, "HideAutocastToggleGizmo");
                abilityAutocastSync = MP.RegisterSyncField(abilityType, "Autocast");
                conditionInvertedSync = MP.RegisterSyncField(conditionType, "Inverted");
                condThresholdIntSync = MP.RegisterSyncField(condThresholdIntType, "Threshold").SetBufferChanges();
                condThresholdPercentSync = MP.RegisterSyncField(condThresholdPercentType, "Threshold").SetBufferChanges();
                filterTargetRangeSync = MP.RegisterSyncField(filterType, "TargetRange").SetBufferChanges();
                singleMinSuccessSync = MP.RegisterSyncField(singleTargetType, "MinSuccessChance").SetBufferChanges();
                singleInvertSelectorSync = MP.RegisterSyncField(singleTargetType, "InvertSelector");
                burstMinTargetsSync = MP.RegisterSyncField(burstType, "MinTargetsInRange").SetBufferChanges();
                additionalInvertedSync = MP.RegisterSyncField(additionalType, "Inverted");
                addThresholdIntSync = MP.RegisterSyncField(addThresholdIntType, "Threshold").SetBufferChanges();
                addThresholdPercentSync = MP.RegisterSyncField(addThresholdPercentType, "Threshold").SetBufferChanges();
            });

            Try("watch + condition patches", () =>
            {
                MpCompat.harmony.Patch(AccessTools.DeclaredMethod(windowType, "DoWindowContents"),
                    prefix: new HarmonyMethod(typeof(PsiTech), nameof(PreDoWindowContents)),
                    finalizer: new HarmonyMethod(typeof(PsiTech), nameof(PostDoWindowContents)));
                MpCompat.harmony.Patch(AccessTools.DeclaredMethod(windowType, "ScheduleConditionForRemoval"),
                    prefix: new HarmonyMethod(typeof(PsiTech), nameof(PreScheduleConditionForRemoval)));
                MpCompat.harmony.Patch(addConditionMethod, prefix: new HarmonyMethod(typeof(PsiTech), nameof(PreAddCondition)));
            });

            // 1.7 lab, FIXES row 145 (point C): UI code gets a placeholder instead of a created tracker.
            Try("tracker placeholder", () =>
                MpCompat.harmony.Patch(AccessTools.DeclaredMethod(managerType, "get_Item", [typeof(Pawn)]),
                    prefix: new HarmonyMethod(typeof(PsiTech), nameof(PreGetTracker))));
        }

        private static void Resolve()
        {
            managerType = Req("PsiTech.Utility.PsiTechManager");
            trackerType = Req("PsiTech.Psionics.PsiTechTracker");
            abilityType = Req("PsiTech.Psionics.PsiTechAbility");
            windowType = Req("PsiTech.Interface.AbilityWindow");
            filterType = Req(NS + "AutocastFilter");
            singleTargetType = Req(NS + "AutocastFilter_SingleTarget");
            burstType = Req(NS + "AutocastFilter_Burst");
            conditionType = Req(NS + "AutocastCondition");
            condThresholdIntType = Req(NS + "AutocastCondition_ThresholdInt");
            condThresholdPercentType = Req(NS + "AutocastCondition_ThresholdPercent");
            additionalType = Req(NS + "AdditionalTargetFilter");
            addThresholdIntType = Req(NS + "AdditionalTargetFilter_ThresholdInt");
            addThresholdPercentType = Req(NS + "AdditionalTargetFilter_ThresholdPercent");
            var conditionDefType = Req(NS + "AutocastConditionDef");

            managerTrackersField = Fld(managerType, "trackers");
            trackerAbilitiesField = Fld(trackerType, "Abilities");
            trackerPawnField = Fld(trackerType, "pawn");
            trackerCtor = AccessTools.DeclaredConstructor(trackerType, [typeof(Pawn), typeof(int)])
                ?? throw new MissingMethodException(trackerType.FullName, ".ctor(Pawn, int)");
            trackerInitializeCaches = Meth(trackerType, "InitializeCaches");
            trackerNotifyEssenceDirty = Meth(trackerType, "Notify_EssenceDirty");

            abilityDefField = Fld(abilityType, "Def");
            abilitySlotField = Fld(abilityType, "Slot");
            abilityLoadIdField = Fld(abilityType, "LoadId");
            abilityUserField = Fld(abilityType, "User");
            abilityFilterField = Fld(abilityType, "AutocastFilter");
            abilityConditionsField = Fld(abilityType, "AutocastConditions");

            filterAbilityField = Fld(filterType, "Ability");
            filterAdditionalFiltersField = Fld(filterType, "AdditionalFilters");
            filterResolveRemoved = Meth(filterType, "ResolveRemovedFilters");
            conditionAbilityField = Fld(conditionType, "Ability");
            conditionDefField = Fld(conditionType, "Def");
            conditionUserField = Fld(conditionType, "User");
            conditionDefClassField = Fld(conditionDefType, "ConditionClass");
            additionalAbilityField = Fld(additionalType, "Ability");
            additionalDefField = Fld(additionalType, "Def");

            // the fields registered as sync fields in LatePatch (checked here too, so a renamed field stops the whole registration with one clear error)
            Fld(trackerType, "HideAutocastToggleGizmo");
            Fld(abilityType, "Autocast");
            Fld(conditionType, "Inverted");
            Fld(condThresholdIntType, "Threshold");
            Fld(condThresholdPercentType, "Threshold");
            Fld(filterType, "TargetRange");
            Fld(singleTargetType, "MinSuccessChance");
            Fld(singleTargetType, "InvertSelector");
            Fld(burstType, "MinTargetsInRange");
            Fld(additionalType, "Inverted");
            Fld(addThresholdIntType, "Threshold");
            Fld(addThresholdPercentType, "Threshold");

            windowPawnField = Fld(windowType, "pawn");
            windowCurAbilityField = Fld(windowType, "curAbility");

            // the 'Add condition' option: local function Action() in GenerateAutocastConditionOption, closure <>c__DisplayClass37_0 (entry, <>4__this = the window)
            addConditionMethod = MpMethodUtil.GetLocalFunc(windowType, "GenerateAutocastConditionOption", localFunc: "Action");
            addConditionEntryField = Fld(addConditionMethod.DeclaringType, "entry");
            addConditionThisField = Fld(addConditionMethod.DeclaringType, "<>4__this");
        }

        private static Type Req(string name)
            => AccessTools.TypeByName(name) ?? throw new TypeLoadException($"type {name} not found");

        private static FieldInfo Fld(Type type, string name)
            => AccessTools.DeclaredField(type, name) ?? throw new MissingFieldException(type.FullName, name);

        private static MethodInfo Meth(Type type, string name)
            => AccessTools.DeclaredMethod(type, name) ?? throw new MissingMethodException(type.FullName, name);

        private static bool Try(string what, Action register)
        {
            try
            {
                register();
                return true;
            }
            catch (Exception e)
            {
                Log.Error($"{LogPrefix}FIXES row 145: {what} was not registered: {e}");
                return false;
            }
        }

        #endregion

        #region Finding PsiTech objects again

        private static object FindManager()
        {
            if (Current.Game == null)
                return null;

            foreach (var component in Current.Game.components)
            {
                if (managerType.IsInstanceOfType(component))
                    return component;
            }

            return null;
        }

        // The manager's own dictionary, never PsiTechManager[Pawn]: that indexer creates a tracker for a pawn that has none.
        private static object FindTracker(Pawn pawn)
        {
            if (pawn == null || FindManager() is not { } manager)
                return null;

            return managerTrackersField.GetValue(manager) is IDictionary trackers && trackers.Contains(pawn) ? trackers[pawn] : null;
        }

        private static object FindAbility(Pawn pawn, Def def, int slot, int loadId)
        {
            if (def == null || FindTracker(pawn) is not { } tracker || trackerAbilitiesField.GetValue(tracker) is not IList abilities)
                return null;

            foreach (var ability in abilities)
            {
                if (ability != null
                    && ReferenceEquals(abilityDefField.GetValue(ability), def)
                    && (int)abilitySlotField.GetValue(ability) == slot
                    && (int)abilityLoadIdField.GetValue(ability) == loadId)
                    return ability;
            }

            return null;
        }

        private static object FindByDef(IList list, FieldInfo defField, Def def)
        {
            if (list == null || def == null)
                return null;

            foreach (var item in list)
            {
                if (item != null && ReferenceEquals(defField.GetValue(item), def))
                    return item;
            }

            return null;
        }

        private static int AbilitySlot(object ability) => (int)abilitySlotField.GetValue(ability);
        private static int AbilityLoadId(object ability) => (int)abilityLoadIdField.GetValue(ability);

        #endregion

        #region Sync workers

        // Writes a 'present' flag and, if present, the ability's (pawn, def, slot, LoadId). Returns whether it was present.
        private static bool WriteAbility(SyncWorker sync, object ability)
        {
            var user = ability == null ? null : abilityUserField.GetValue(ability) as Pawn;
            var def = ability == null ? null : abilityDefField.GetValue(ability) as Def;
            var present = user != null && def != null;
            sync.Write(present);
            if (!present)
                return false;

            sync.Write(user);
            sync.Write(def);
            sync.Write(AbilitySlot(ability));
            sync.Write(AbilityLoadId(ability));
            return true;
        }

        // Returns whether the writer wrote an ability; 'ability' is null if it is gone on this side.
        private static bool ReadAbility(SyncWorker sync, out object ability)
        {
            ability = null;
            if (!sync.Read<bool>())
                return false;

            var pawn = sync.Read<Pawn>();
            var def = sync.Read<Def>();
            var slot = sync.Read<int>();
            var loadId = sync.Read<int>();
            ability = FindAbility(pawn, def, slot, loadId);
            return true;
        }

        private static void SyncAutocastFilter(SyncWorker sync, ref object filter)
        {
            if (sync.isWriting)
                WriteAbility(sync, filter == null ? null : filterAbilityField.GetValue(filter));
            else if (ReadAbility(sync, out var ability) && ability != null)
                filter = abilityFilterField.GetValue(ability);
        }

        private static void SyncAbility(SyncWorker sync, ref object ability)
        {
            if (sync.isWriting)
                WriteAbility(sync, ability);
            else if (ReadAbility(sync, out var found))
                ability = found;
        }

        private static void SyncTracker(SyncWorker sync, ref object tracker)
        {
            if (sync.isWriting)
            {
                var pawn = tracker == null ? null : trackerPawnField.GetValue(tracker) as Pawn;
                sync.Write(pawn != null);
                if (pawn != null)
                    sync.Write(pawn);
            }
            else if (sync.Read<bool>())
            {
                tracker = FindTracker(sync.Read<Pawn>());
            }
        }

        // A condition is its ability + its Def (the window offers each condition class once per ability).
        private static void SyncCondition(SyncWorker sync, ref object condition)
        {
            if (sync.isWriting)
            {
                var ability = condition == null ? null : conditionAbilityField.GetValue(condition);
                var def = condition == null ? null : conditionDefField.GetValue(condition) as Def;
                var listed = ability != null && def != null && ((IList)abilityConditionsField.GetValue(ability)).Contains(condition);
                if (WriteAbility(sync, listed ? ability : null))
                    sync.Write(def);
            }
            else if (ReadAbility(sync, out var ability))
            {
                var def = sync.Read<Def>();
                if (ability != null)
                    condition = FindByDef(abilityConditionsField.GetValue(ability) as IList, conditionDefField, def);
            }
        }

        // An additional filter is its ability's filter + its Def (the window offers each additional filter class once per filter).
        private static void SyncAdditionalFilter(SyncWorker sync, ref object additional)
        {
            if (sync.isWriting)
            {
                var ability = additional == null ? null : additionalAbilityField.GetValue(additional);
                var def = additional == null ? null : additionalDefField.GetValue(additional) as Def;
                var filter = ability == null ? null : abilityFilterField.GetValue(ability);
                var listed = filter != null && def != null && ((IList)filterAdditionalFiltersField.GetValue(filter)).Contains(additional);
                if (WriteAbility(sync, listed ? ability : null))
                    sync.Write(def);
            }
            else if (ReadAbility(sync, out var ability))
            {
                var def = sync.Read<Def>();
                var filter = ability == null ? null : abilityFilterField.GetValue(ability);
                if (filter != null)
                    additional = FindByDef(filterAdditionalFiltersField.GetValue(filter) as IList, additionalDefField, def);
            }
        }

        #endregion

        #region Synced conditions

        // Same steps as the local function in AbilityWindow.GenerateAutocastConditionOption: create the condition class, set User/Ability/Def, add it.
        // The window's own cache (conditionsForAdd) rebuilds by itself when it sees the condition count change in DoWindowContents.
        private static void SyncAddCondition(Pawn pawn, Def abilityDef, int slot, int loadId, Def conditionDef)
        {
            if (conditionDef == null || FindAbility(pawn, abilityDef, slot, loadId) is not { } ability)
                return;

            if (conditionDefClassField.GetValue(conditionDef) is not Type conditionClass
                || abilityConditionsField.GetValue(ability) is not IList conditions)
                return;

            // The menu only offers classes the ability does not have yet; a second click that was already in flight finds it here.
            foreach (var existing in conditions)
            {
                if (existing != null && existing.GetType() == conditionClass)
                    return;
            }

            var condition = Activator.CreateInstance(conditionClass);
            if (!conditionType.IsInstanceOfType(condition))
            {
                Log.Error($"{LogPrefix}PsiTech tried to instantiate an autocast condition of class {conditionClass} and failed.");
                return;
            }

            conditionUserField.SetValue(condition, pawn);
            conditionAbilityField.SetValue(condition, ability);
            conditionDefField.SetValue(condition, conditionDef);
            conditions.Add(condition);
        }

        // Same as the window's own removal (`curAbility.AutocastConditions.Remove(condition)` at the top of DoWindowContents), found by Def.
        private static void SyncRemoveCondition(Pawn pawn, Def abilityDef, int slot, int loadId, Def conditionDef)
        {
            if (FindAbility(pawn, abilityDef, slot, loadId) is not { } ability || abilityConditionsField.GetValue(ability) is not IList conditions)
                return;

            if (FindByDef(conditions, conditionDefField, conditionDef) is { } condition)
                conditions.Remove(condition);
        }

        #endregion

        #region Harmony callbacks

        private static bool InUi() => MP.IsInMultiplayer && MP.InInterface;

        // AbilityWindow.<>c__DisplayClass37_0.<GenerateAutocastConditionOption>g__Action|0 : the 'Add condition' option. `this` of the closure is the window.
        private static bool PreAddCondition(object __instance)
        {
            if (!InUi())
                return true;

            try
            {
                var entry = addConditionEntryField.GetValue(__instance) as Def;
                var window = addConditionThisField.GetValue(__instance);
                var ability = window == null ? null : windowCurAbilityField.GetValue(window);
                var user = ability == null ? null : abilityUserField.GetValue(ability) as Pawn;
                if (entry != null && user != null)
                    SyncAddCondition(user, abilityDefField.GetValue(ability) as Def, AbilitySlot(ability), AbilityLoadId(ability), entry);
            }
            catch (Exception e)
            {
                LogPatchError("add condition", e);
            }

            return false;
        }

        // AbilityWindow.ScheduleConditionForRemoval(AutocastCondition condition): the 'Remove' button of a condition.
        private static bool PreScheduleConditionForRemoval(object __instance, object condition)
        {
            if (!InUi())
                return true;

            try
            {
                var ability = windowCurAbilityField.GetValue(__instance);
                var user = ability == null ? null : abilityUserField.GetValue(ability) as Pawn;
                var conditionDef = condition == null ? null : conditionDefField.GetValue(condition) as Def;
                if (user != null && conditionDef != null)
                    SyncRemoveCondition(user, abilityDefField.GetValue(ability) as Def, AbilitySlot(ability), AbilityLoadId(ability), conditionDef);
            }
            catch (Exception e)
            {
                LogPatchError("remove condition", e);
            }

            return false;
        }

        // AutocastFilter.MarkAdditionalFilterForRemoval is MP-synced; when it runs as a command, remove the queued filter now (Draw would do it).
        private static void PostMarkAdditionalFilterForRemoval(object __instance)
        {
            if (MP.IsInMultiplayer && MP.IsExecutingSyncCommand)
                filterResolveRemoved.Invoke(__instance, null);
        }

        // Watch pattern around AbilityWindow.DoWindowContents (every widget there writes straight into the objects below).
        private static void PreDoWindowContents(object __instance, ref bool __state)
        {
            __state = false;
            if (!MP.IsInMultiplayer)
                return;

            try
            {
                var ability = windowCurAbilityField.GetValue(__instance);
                if (ability == null)
                    return;

                MP.WatchBegin();
                __state = true;

                trackerHideSync?.Watch(FindTracker(windowPawnField.GetValue(__instance) as Pawn));
                abilityAutocastSync?.Watch(ability);

                if (abilityConditionsField.GetValue(ability) is IList conditions)
                {
                    foreach (var condition in conditions)
                    {
                        conditionInvertedSync?.Watch(condition);
                        if (condThresholdIntType.IsInstanceOfType(condition))
                            condThresholdIntSync?.Watch(condition);
                        else if (condThresholdPercentType.IsInstanceOfType(condition))
                            condThresholdPercentSync?.Watch(condition);
                    }
                }

                var filter = abilityFilterField.GetValue(ability);
                if (filter == null)
                    return;

                filterTargetRangeSync?.Watch(filter);
                if (singleTargetType.IsInstanceOfType(filter))
                {
                    singleMinSuccessSync?.Watch(filter);
                    singleInvertSelectorSync?.Watch(filter);
                }
                else if (burstType.IsInstanceOfType(filter))
                {
                    burstMinTargetsSync?.Watch(filter);
                }

                if (filterAdditionalFiltersField.GetValue(filter) is IList additionalFilters)
                {
                    foreach (var additional in additionalFilters)
                    {
                        additionalInvertedSync?.Watch(additional);
                        if (addThresholdIntType.IsInstanceOfType(additional))
                            addThresholdIntSync?.Watch(additional);
                        else if (addThresholdPercentType.IsInstanceOfType(additional))
                            addThresholdPercentSync?.Watch(additional);
                    }
                }
            }
            catch (Exception e)
            {
                if (!loggedWatchError)
                {
                    loggedWatchError = true;
                    Log.Error($"{LogPrefix}watching the Psi tab fields failed (logged once): {e}");
                }
            }
        }

        private static void PostDoWindowContents(bool __state)
        {
            if (__state)
                MP.WatchEnd();
        }

        // PsiTechManager[Pawn] (point C). Only while MP.InInterface; ticks, synced commands and loading run the original, which creates the real tracker.
        private static bool PreGetTracker(object __instance, Pawn pawn, ref object __result)
        {
            if (pawn == null || !MP.IsInMultiplayer)
                return true;

            if (!MP.InInterface)
            {
                // The real tracker is (or is about to be) made by the original on every PC; drop the placeholder of this pawn.
                if (uiTrackers.Count != 0)
                    uiTrackers.Remove(pawn);
                return true;
            }

            try
            {
                if (managerTrackersField.GetValue(__instance) is IDictionary trackers && trackers.Contains(pawn))
                    return true; // exists: the original just returns it

                if (!ReferenceEquals(uiTrackersGame, Current.Game))
                {
                    uiTrackers.Clear();
                    uiTrackersGame = Current.Game;
                }

                if (!uiTrackers.TryGetValue(pawn, out var placeholder))
                {
                    // Same order as the original (registered before InitializeCaches, which re-enters through the capacity patch), minus the
                    // saved id counter and the manager's dictionary.
                    placeholder = trackerCtor.Invoke([pawn, -1]);
                    uiTrackers[pawn] = placeholder;
                    trackerInitializeCaches.Invoke(placeholder, null);
                    trackerNotifyEssenceDirty.Invoke(placeholder, null);
                }

                __result = placeholder;
                return false;
            }
            catch (Exception e)
            {
                uiTrackers.Remove(pawn);
                if (!loggedPlaceholderError)
                {
                    loggedPlaceholderError = true;
                    Log.Error($"{LogPrefix}placeholder tracker failed, falling back to the mod's own behaviour (logged once): {e}");
                }

                return true;
            }
        }

        private static void LogPatchError(string what, Exception e)
        {
            if (loggedPatchError)
                return;

            loggedPatchError = true;
            Log.Error($"{LogPrefix}{what} (logged once): {e}");
        }

        #endregion
    }
}
