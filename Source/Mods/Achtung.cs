using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace Multiplayer.Compat
{
    /// <summary>Achtung! by Andreas Pardeike (Brrainz)</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=730936602"/>
    /// <see href="https://github.com/pardeike/Achtung"/>
    /// <remarks>
    /// Written against the decompiled 1.6 Achtung.dll (4.1.8.0, the dll the 1.7 lab loads). Not run in game yet.
    /// Achtung had no multiplayer support. The [Force] float-menu option (ForcedMultiFloatMenuOption.ForceAction) builds
    /// the ForcedWork registry (WorldComponent: Dictionary Pawn -> ForcedJobs, the "preparing" set, ForcedJob targets
    /// and cellRadius) in UI code on the clicking machine only. Simulation code reads that registry:
    /// ForbidUtility.IsForbidden, Pawn_JobTracker.ShouldStartJobFromThinkTree, Pawn_WorkSettings.WorkIsActive,
    /// Pawn_PlayerSettings.RespectsAllowedArea, HaulAIUtility.PawnCanAutomaticallyHaulFast, EndCurrentJob (ContinueJob) and
    /// ForcedWork.WorldComponentTick. The job start itself already goes through MP's synced
    /// TryTakeOrderedJobPrioritizedWork, so after any [Force] use the clicking machine and the others disagree.
    /// What this module does (all MP only; outside a session nothing changes):
    /// 1. SYNCED: ForceAction. Its simulation half (Prepare, Tools.CancelWorkOn, AddForcedJob, ExpandJob,
    ///    GetNextNonConflictingJob, Unprepare, RemoveForcedJob on failure) now runs as ONE synced method
    ///    (pawn, WorkGiverDef, clicked cell, selected-pawn count) on every machine, so Add/Prepare/Unprepare/Remove
    ///    and the CancelWorkOn side effects (EndCurrentJob + Remove on the pawns working the item) are made inside the
    ///    command, with the command's seeded Rand (Tools.Expand draws Rand.Range). The original is replaced by a prefix
    ///    when called from the interface; the "selected pawns" count that it read from the local Selector is an argument
    ///    now. The UI half stays local: the drag ring (MouseTracker.StartDragging) and the "could not find more forced
    ///    work" message (only on the machine that issued the command).
    /// 2. SYNCED: the drag radius (ForcedJob.cellRadius, written on every ring step by the MouseTracker callback) through
    ///    a synced setter that finds the job the last synced ForceAction created for that pawn. ForcedJob.Start()
    ///    (started = true) is not replayed: nothing in the dll reads that field.
    /// 3. DISABLED in MP: the background expander, Game_UpdatePlay_Patch.Postfix. It runs per FRAME (up to 800
    ///    iterations a frame, throttled by the local fps) and adds/removes ForcedJob targets (ExpandThingTargets,
    ///    ExpandCellTargets, ContractTargets) on whichever machine draws, so targets (which feed GetSortedTargets and
    ///    so the next job) diverge at once. Replacement, deterministic: before ContinueJob looks for the next job
    ///    the current forced job is grown by up to 4 targets (ExpandJob(4), what the dll already does for the next
    ///    queued job and on InterruptForced). Loss: no continuous flood-fill and no ContractTargets cleanup in MP.
    /// 4. FIXED: ForcedWork.WorldComponentTick picks the pawn to check with a private counter that is not saved, so a
    ///    machine that reloads (join point) and one that does not walk the pawns in a different order. The counter is
    ///    set from TicksGame / 139 before each pass, so every peer picks (TicksGame / 139) % pawnCount.
    /// 5. DISABLED in MP: Achtung's own "fight fire" and "clean room" float-menu options. JobDriver_Thoroughly.StartJob
    ///    builds the job with MakeDef(), a NEW JobDef object each call (not the one Controller.InstallDefs added to
    ///    DefDatabase), and MP writes a Def as its shortHash; a def made at runtime is not known to the other peers by
    ///    hash. Vanilla fire fighting and cleaning stay.
    /// 6. Formation drag / multi-pawn orders (Controller, Colonist, MultiActions, Tools). Colonist.OrderTo ->
    ///    Tools.OrderTo and the Achtung float-menu option actions only call Pawn_JobTracker.TryTakeOrderedJob (the job
    ///    is made in the interface, the same pattern as the vanilla right-click goto) and the vanilla TryTakeOrderedJob
    ///    wrappers: fine, MP syncs those. Two bypasses, both fixed:
    ///    a) Tools.SetDraftStatus writes pawn.drafter.draftedInt directly (DraftWithSound, ShowMenu "everyone has goto",
    ///    MultiAction.GetAction, CancelDrafting). In MP it goes through the synced Pawn_DraftController.Drafted setter
    ///    instead (the pawn is drafted when the command runs, a few ticks later; the ordered job command is queued
    ///    behind it). Exception kept local: MultiActions.AddColonist flips the draft state of a pawn for a moment and
    ///    flips it back, only to ask the vanilla menu what a drafted pawn could do; that stays a direct write.
    ///    b) Tools.CancelDrafting (Escape while dragging) called priorityWork.Clear() and EndCurrentJob(InterruptForced)
    ///    directly. It is a synced method now (pawn, original draft state), sounds stay local.
    /// 7. FIXED (all modes): the "Rescuing" work type. DynamicWorkTypes.AddWorkTypeDef puts a WorkTypeDef made at runtime
    ///    (Tools.RescuingWorkTypeDef = new WorkTypeDef) into DefDatabase on World.ExposeData, after the game handed out
    ///    short hashes, so its shortHash stays 0; MP writes a Def as its shortHash and no peer can read 0 back, so every
    ///    priority click in the Rescuing column (MP's synced Pawn_WorkSettings.SetPriority) threw on every PC (row 207).
    ///    Every work type left at hash 0 now gets the hash ShortHashGiver would give it, after every game load
    ///    (Game.FinalizeInit) and after AddWorkTypeDef (finalizer: the settings toggle mid-game); a log line per def lets
    ///    the peers be compared. Needs Achtung's "rescueEnabled" equal on every PC (a PC without the column has no such
    ///    def, so the click would run on one PC and throw on the other).
    /// Not handled here (a lower bound of what may still desync):
    /// - Achtung settings (Achtung.Settings: ignoreForbidden, ignoreRestrictions, ignoreAssignments, buildingSmart,
    ///   maxForcedItems, breakLevel, healthLevel, forcedEndedLetter) are read by simulation code from each machine's own
    ///   mod settings; MP copies the host's mod configs at join, a change made during the session is local.
    /// - ForcedWork.WorldComponentTick walks allForcedJobs.Keys, a Dictionary whose order depends on its history; equal
    ///   on all peers after a reload from the same save, not forced to be equal otherwise.
    /// - The float menu builder temporarily Prepare()s the pawn (FloatMenuOptionProvider_WorkGivers.GetWorkGiverOption
    ///   prefix/postfix); it is undone in the same call on the interface, nothing ticks in between.
    /// - Achtung.usedThingsPerTick is a process-lifetime static reset per tick.
    /// - Per ring step the drag sends a command; per frame of a held mod key the draft command may repeat (idempotent).
    /// Version note (James runs 4.1.9.3, Sea 4.1.8.0, both.tsv:17): the lookups below throw if missing, except the
    /// drafting patches (Colonist, Tools.SetDraftStatus/CancelDrafting/GetDraftingStatus, MultiActions.AddColonist),
    /// which log an error and leave drafting unsynced when the positioning code was reshaped. What may differ in
    /// 4.1.9.3 and is unchecked: member names/signatures of ForcedWork (Instance, Prepare, Unprepare, RemoveForcedJob,
    /// GetForcedJob, GetForcedJobsInstance, AddForcedJob, GetCombinedDefs, HasJobItem, counter),
    /// ForcedJob (cellRadius, cancelled, initialized, ExpandJob, GetNextNonConflictingJob, ContinueJob), Tools.CancelWorkOn,
    /// MouseTracker.StartDragging, ForcedMultiFloatMenuOption.ForceAction (parameters are bound by position),
    /// Game_UpdatePlay_Patch.Postfix and FloatMenuOptionProvider_FightFire/CleanRoom.GetSingleOption. If 4.1.9.3 syncs
    /// or reshapes any of these itself, this module can double up.
    /// </remarks>
    [MpCompatFor("brrainz.achtung")]
    public class Achtung
    {
        private const string LogPrefix = "[MPCompat - Achtung] ";

        // The ForcedJob the last synced ForceAction created per pawn (for the drag radius). Process lifetime; a stale
        // entry (reload) points at an object that is not in the registry any more and is ignored.
        private static readonly Dictionary<Pawn, object> lastAddedJob = new();

        // True only while MultiActions.AddColonist runs (a local what-if flip of the draft state).
        private static bool inAddColonist;

        // AchtungMod.ForcedWork / ForcedJob / ForcedJobs / Tools
        private static MethodInfo instanceGetter;
        private static MethodInfo prepareMethod;
        private static MethodInfo unprepareMethod;
        private static MethodInfo removeForcedJobMethod;
        private static MethodInfo getForcedJobMethod;
        private static MethodInfo getForcedJobsInstanceMethod;
        private static MethodInfo addForcedJobMethod;
        private static MethodInfo getCombinedDefsMethod;
        private static MethodInfo hasJobItemMethod;
        private static MethodInfo cancelWorkOnMethod;
        private static MethodInfo expandJobMethod;
        private static MethodInfo getNextNonConflictingJobMethod;
        private static FieldInfo cellRadiusField;
        private static FieldInfo cancelledField;
        private static FieldInfo initializedField;
        private static FieldInfo jobsField;
        private static FieldInfo counterField;
        private static Action<Pawn, IntVec3, Action<int>, Action> startDragging;

        // Drafting (optional part)
        private static FieldInfo colonistPawnField;
        private static FieldInfo colonistOriginalDraftField;
        private static MethodInfo getDraftingStatusMethod;

        public Achtung(ModContentPack mod)
        {
            LookUpCore();

            // Game.LoadGame prefix first: it only clears the static above, before anything of the new game exists.
            MpCompat.harmony.Patch(GetDeclaredMethod(typeof(Game), nameof(Game.LoadGame)),
                prefix: new HarmonyMethod(typeof(Achtung), nameof(PreLoadGame)));

            PatchForceAction();
            PatchTargetExpansion();
            PatchWorldTick();
            PatchDisabledOptions();
            PatchRuntimeWorkType(mod);

            try
            {
                PatchDrafting();
            }
            catch (Exception e)
            {
                Log.Error($"{LogPrefix}drafting from Achtung's formation/multi-pawn orders is NOT synced (Tools.SetDraftStatus writes the drafted flag directly, Tools.CancelDrafting ends jobs directly): {e}");
            }
        }

        #region Lookups

        private static void LookUpCore()
        {
            var forcedWorkType = FindType("AchtungMod.ForcedWork");
            var forcedJobType = FindType("AchtungMod.ForcedJob");
            var forcedJobsType = FindType("AchtungMod.ForcedJobs");
            var toolsType = FindType("AchtungMod.Tools");
            var mouseTrackerType = FindType("AchtungMod.MouseTracker");

            instanceGetter = AccessTools.PropertyGetter(forcedWorkType, "Instance")
                             ?? throw new MissingMethodException(forcedWorkType.FullName, "get_Instance");
            if (!instanceGetter.IsStatic || instanceGetter.ReturnType != forcedWorkType)
                throw new MissingMethodException($"{LogPrefix}{forcedWorkType.FullName}.Instance is not a static {forcedWorkType.Name} property");

            prepareMethod = GetDeclaredMethod(forcedWorkType, "Prepare", typeof(Pawn));
            unprepareMethod = GetDeclaredMethod(forcedWorkType, "Unprepare", typeof(Pawn));
            removeForcedJobMethod = GetDeclaredMethod(forcedWorkType, "RemoveForcedJob", typeof(Pawn));
            getForcedJobMethod = GetDeclaredMethod(forcedWorkType, "GetForcedJob", typeof(Pawn));
            getForcedJobsInstanceMethod = GetDeclaredMethod(forcedWorkType, "GetForcedJobsInstance", typeof(Pawn));
            addForcedJobMethod = GetDeclaredMethod(forcedWorkType, "AddForcedJob",
                typeof(Pawn), typeof(List<WorkGiverDef>), typeof(LocalTargetInfo), forcedJobType.MakeByRefType());
            getCombinedDefsMethod = GetDeclaredMethod(forcedWorkType, "GetCombinedDefs", typeof(WorkGiver));
            if (!getCombinedDefsMethod.IsStatic || getCombinedDefsMethod.ReturnType != typeof(List<WorkGiverDef>))
                throw new MissingMethodException($"{LogPrefix}{forcedWorkType.FullName}.GetCombinedDefs is not a static List<WorkGiverDef> method");
            hasJobItemMethod = GetDeclaredMethod(forcedWorkType, "HasJobItem",
                typeof(Pawn), typeof(WorkGiver_Scanner), typeof(IntVec3), typeof(bool));
            if (!hasJobItemMethod.IsStatic || hasJobItemMethod.ReturnType != typeof(LocalTargetInfo))
                throw new MissingMethodException($"{LogPrefix}{forcedWorkType.FullName}.HasJobItem is not a static LocalTargetInfo method");

            cancelWorkOnMethod = GetDeclaredMethod(toolsType, "CancelWorkOn", typeof(Pawn), typeof(LocalTargetInfo));
            expandJobMethod = GetDeclaredMethod(forcedJobType, "ExpandJob", typeof(int));
            getNextNonConflictingJobMethod = GetDeclaredMethod(forcedJobType, "GetNextNonConflictingJob", forcedWorkType);
            if (getNextNonConflictingJobMethod.ReturnType != typeof(bool))
                throw new MissingMethodException($"{LogPrefix}{forcedJobType.FullName}.GetNextNonConflictingJob does not return bool");

            cellRadiusField = GetInstanceField(forcedJobType, "cellRadius", typeof(int));
            cancelledField = GetInstanceField(forcedJobType, "cancelled", typeof(bool));
            initializedField = GetInstanceField(forcedJobType, "initialized", typeof(bool));
            jobsField = GetInstanceField(forcedJobsType, "jobs", null);
            if (!typeof(IList).IsAssignableFrom(jobsField.FieldType))
                throw new MissingFieldException($"{LogPrefix}{forcedJobsType.FullName}.jobs is {jobsField.FieldType}, expected a list");
            counterField = GetInstanceField(forcedWorkType, "counter", typeof(int));

            var startDraggingMethod = GetDeclaredMethod(mouseTrackerType, "StartDragging",
                typeof(Pawn), typeof(IntVec3), typeof(Action<int>), typeof(Action));
            if (!startDraggingMethod.IsStatic)
                throw new MissingMethodException($"{LogPrefix}{mouseTrackerType.FullName}.StartDragging is not static");
            startDragging = (Action<Pawn, IntVec3, Action<int>, Action>)Delegate.CreateDelegate(
                typeof(Action<Pawn, IntVec3, Action<int>, Action>), startDraggingMethod);

            MP.RegisterSyncMethod(typeof(Achtung), nameof(SyncedForceAction)).CancelIfAnyArgNull();
            MP.RegisterSyncMethod(typeof(Achtung), nameof(SyncedSetCellRadius)).CancelIfAnyArgNull();
        }

        private static void PreLoadGame() => lastAddedJob.Clear();

        #endregion

        #region [Force] option (point 1 and 2)

        private static void PatchForceAction()
        {
            var forceAction = GetDeclaredMethod(FindType("AchtungMod.ForcedMultiFloatMenuOption"), "ForceAction",
                typeof(Pawn), typeof(WorkGiver_Scanner), typeof(IntVec3));
            if (forceAction.ReturnType != typeof(bool) || !forceAction.IsStatic)
                throw new MissingMethodException($"{LogPrefix}ForceAction is not a static bool method");

            MpCompat.harmony.Patch(forceAction, prefix: new HarmonyMethod(typeof(Achtung), nameof(PreForceAction)));
        }

        // Parameters by position (__0..__2): pawn, forceWorkgiver, clickedCell. Called from the float menu's extra part
        // (interface). Inside a synced command the original runs unchanged (nothing calls it there).
        private static bool PreForceAction(Pawn __0, WorkGiver_Scanner __1, IntVec3 __2, ref bool __result)
        {
            if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand)
                return true;

            var pawn = __0;
            var cell = __2;
            if (pawn == null || __1?.def == null)
            {
                __result = false;
                return false;
            }

            // The local Selector count was read inside ExpandJob's argument; it is an argument of the command now.
            SyncedForceAction(pawn, __1.def, cell, Find.Selector.SelectedPawns.Count);

            // The drag ring is local; every ring step sets the radius of the job the command created.
            startDragging(pawn, cell, radius => SyncedSetCellRadius(pawn, radius), () => { });

            __result = true;
            return false;
        }

        // The simulation half of ForcedMultiFloatMenuOption.ForceAction, same order of calls.
        private static void SyncedForceAction(Pawn pawn, WorkGiverDef workGiverDef, IntVec3 clickedCell, int selectedCount)
        {
            if (workGiverDef.Worker is not WorkGiver_Scanner worker)
                return;

            var instance = instanceGetter.Invoke(null, null);
            prepareMethod.Invoke(instance, new object[] { pawn });
            var combinedDefs = (List<WorkGiverDef>)getCombinedDefsMethod.Invoke(null, new object[] { worker });

            foreach (var expandSearch in new[] { false, true })
            {
                foreach (var def in combinedDefs)
                {
                    var scanner = def.giverClass == null ? null : def.Worker as WorkGiver_Scanner;
                    if (scanner == null)
                        continue;

                    var target = (LocalTargetInfo)hasJobItemMethod.Invoke(null, new object[] { pawn, scanner, clickedCell, expandSearch });
                    if (!target.IsValid)
                        continue;

                    cancelWorkOnMethod.Invoke(null, new object[] { pawn, target });
                    var addArgs = new object[] { pawn, combinedDefs, target, null };
                    var first = (bool)addForcedJobMethod.Invoke(instance, addArgs);
                    var forcedJob = addArgs[3];

                    if (first)
                    {
                        expandJobMethod.Invoke(forcedJob, new object[] { 1 + selectedCount * 2 });
                        if (!(bool)getNextNonConflictingJobMethod.Invoke(forcedJob, new[] { instance }))
                            continue;
                        unprepareMethod.Invoke(instance, new object[] { pawn });
                    }

                    lastAddedJob[pawn] = forcedJob;
                    return;
                }
            }

            removeForcedJobMethod.Invoke(instance, new object[] { pawn });
            if (MP.IsExecutingSyncCommandIssuedBySelf)
                Messages.Message("CouldNotFindMoreForcedWork".Translate(pawn.LabelShort), MessageTypeDefOf.RejectInput);
        }

        private static void SyncedSetCellRadius(Pawn pawn, int radius)
        {
            if (!lastAddedJob.TryGetValue(pawn, out var job) || job == null || (bool)cancelledField.GetValue(job))
                return;

            // Only a job that is still in this pawn's registry list (it can finish, be cancelled or be reloaded away).
            var instance = instanceGetter.Invoke(null, null);
            var forcedJobs = getForcedJobsInstanceMethod.Invoke(instance, new object[] { pawn });
            if (forcedJobs == null || jobsField.GetValue(forcedJobs) is not IList list || !list.Contains(job))
                return;

            cellRadiusField.SetValue(job, Math.Max(0, Math.Min(radius, (int)GenRadial.MaxRadialPatternRadius)));
        }

        #endregion

        #region Target expansion (point 3)

        private static void PatchTargetExpansion()
        {
            // Achtung's own Harmony postfix of Game.UpdatePlay; patching the postfix method itself skips its body in MP.
            var looper = GetDeclaredMethod(FindType("AchtungMod.Game_UpdatePlay_Patch"), "Postfix");
            MpCompat.harmony.Patch(looper, prefix: new HarmonyMethod(typeof(Achtung), nameof(PreFrameExpander)));

            var continueJob = GetDeclaredMethod(FindType("AchtungMod.ForcedJob"), "ContinueJob", typeof(Pawn), typeof(JobCondition));
            if (!continueJob.IsStatic || continueJob.ReturnType != typeof(bool))
                throw new MissingMethodException($"{LogPrefix}ForcedJob.ContinueJob is not a static bool method");
            MpCompat.harmony.Patch(continueJob, prefix: new HarmonyMethod(typeof(Achtung), nameof(PreContinueJob)));
        }

        private static bool PreFrameExpander() => !MP.IsInMultiplayer;

        // Runs inside EndCurrentJob (simulation). Same guards as ContinueJob itself; InterruptForced already expands there.
        private static void PreContinueJob(Pawn __0, JobCondition __1)
        {
            if (!MP.IsInMultiplayer || __0 == null || __1 == JobCondition.InterruptForced || __0.Map == null
                || !PawnAttackGizmoUtility.CanOrderPlayerPawn(__0))
                return;

            var instance = instanceGetter.Invoke(null, null);
            var forcedJob = getForcedJobMethod.Invoke(instance, new object[] { __0 });
            // The first call only marks the job initialized; ContinueJob returns false for it.
            if (forcedJob == null || !(bool)initializedField.GetValue(forcedJob))
                return;

            expandJobMethod.Invoke(forcedJob, new object[] { 4 });
        }

        #endregion

        #region WorldComponentTick counter (point 4)

        private static void PatchWorldTick()
        {
            var tick = GetDeclaredMethod(FindType("AchtungMod.ForcedWork"), "WorldComponentTick");
            MpCompat.harmony.Patch(tick, prefix: new HarmonyMethod(typeof(Achtung), nameof(PreWorldComponentTick)));
        }

        // The original returns unless TicksGame % 139 == 0, then picks array[++counter % num].
        private static void PreWorldComponentTick(object __instance)
        {
            if (!MP.IsInMultiplayer)
                return;

            var ticks = Find.TickManager.TicksGame;
            if (ticks % 139 == 0)
                counterField.SetValue(__instance, ticks / 139 - 1);
        }

        #endregion

        #region Disabled options (point 5)

        private static void PatchDisabledOptions()
        {
            foreach (var name in new[] { "AchtungMod.FloatMenuOptionProvider_FightFire", "AchtungMod.FloatMenuOptionProvider_CleanRoom" })
            {
                var getOption = GetDeclaredMethod(FindType(name), "GetSingleOption");
                if (getOption.ReturnType != typeof(FloatMenuOption))
                    throw new MissingMethodException($"{LogPrefix}{name}.GetSingleOption does not return FloatMenuOption");
                MpCompat.harmony.Patch(getOption, postfix: new HarmonyMethod(typeof(Achtung), nameof(PostGetSingleOption)));
            }
        }

        private static void PostGetSingleOption(ref FloatMenuOption __result)
        {
            if (MP.IsInMultiplayer)
                __result = null;
        }

        #endregion

        #region Runtime work type short hash (point 7)

        // Two triggers for one pass, so a PC never depends on Achtung's own call completing: Game.FinalizeInit (after every
        // load; Achtung adds the def in World.ExposeData during the load) and a finalizer on DynamicWorkTypes.AddWorkTypeDef
        // (the settings toggle adds it mid-game; a finalizer also runs when the original throws after DefDatabase.Add).
        private static void PatchRuntimeWorkType(ModContentPack mod)
        {
            MpCompat.harmony.Patch(GetDeclaredMethod(typeof(Game), nameof(Game.FinalizeInit)),
                postfix: new HarmonyMethod(typeof(Achtung), nameof(GiveRuntimeWorkTypesShortHashes)));

            try
            {
                // Achtung's own copy: Brrainz helper classes are shared source, other mods can carry a type of the same name
                var dynamicWorkTypes = mod.assemblies.loadedAssemblies
                                           .Select(assembly => assembly.GetType("Brrainz.DynamicWorkTypes"))
                                           .FirstOrDefault(type => type != null)
                                       ?? throw new TypeLoadException($"{LogPrefix}Brrainz.DynamicWorkTypes not found in Achtung's assemblies");
                var addWorkTypeDef = GetDeclaredMethod(dynamicWorkTypes, "AddWorkTypeDef", typeof(WorkTypeDef), typeof(WorkTypeDef), typeof(WorkGiverDef));
                MpCompat.harmony.Patch(addWorkTypeDef, finalizer: new HarmonyMethod(typeof(Achtung), nameof(GiveRuntimeWorkTypesShortHashes)));
            }
            catch (Exception e)
            {
                Log.Error($"{LogPrefix}a Rescuing work type turned on in Achtung's settings during a game gets its short hash only at the next load: {e}");
            }
        }

        // Every WorkTypeDef still at short hash 0 (made after startup hashing) gets the hash Verse.ShortHashGiver.GiveShortHash
        // would give it, in defName order like the game: the same work type list on every peer gives the same hashes, and no
        // def is left at 0 (InitializeShortHashDictionary adds every def, so one left at 0 would answer GetByShortHash(0)).
        private static void GiveRuntimeWorkTypesShortHashes()
        {
            var defs = DefDatabase<WorkTypeDef>.AllDefsListForReading;
            var unhashed = defs.Where(def => def.shortHash == 0).OrderBy(def => def.defName, StringComparer.Ordinal).ToList();
            if (unhashed.Count == 0)
                return;

            var taken = new HashSet<ushort>(defs.Select(def => def.shortHash).Where(hash => hash != 0));
            foreach (var def in unhashed)
            {
                var hash = (ushort)(GenText.StableStringHash(def.defName) % 65535);
                while (hash == 0 || taken.Contains(hash))
                    hash++;

                def.shortHash = hash;
                taken.Add(hash);
                Log.Message($"{LogPrefix}runtime work type {def.defName} given short hash {hash} ({defs.Count} work types); every PC must log the same");
            }

            DefDatabase<WorkTypeDef>.InitializeShortHashDictionary();
        }

        #endregion

        #region Drafting (point 6)

        private static void PatchDrafting()
        {
            var toolsType = FindType("AchtungMod.Tools");
            var colonistType = FindType("AchtungMod.Colonist");
            var multiActionsType = FindType("AchtungMod.MultiActions");

            colonistPawnField = GetInstanceField(colonistType, "pawn", typeof(Pawn));
            colonistOriginalDraftField = GetInstanceField(colonistType, "originalDraftStatus", typeof(bool));
            getDraftingStatusMethod = GetDeclaredMethod(toolsType, "GetDraftingStatus", typeof(Pawn));
            var setDraftStatus = GetDeclaredMethod(toolsType, "SetDraftStatus", typeof(Pawn), typeof(bool));
            var cancelDrafting = GetDeclaredMethod(toolsType, "CancelDrafting", typeof(List<>).MakeGenericType(colonistType));
            var addColonist = GetDeclaredMethod(multiActionsType, "AddColonist", colonistType);
            if (setDraftStatus.ReturnType != typeof(bool) || getDraftingStatusMethod.ReturnType != typeof(bool))
                throw new MissingMethodException($"{LogPrefix}Tools.SetDraftStatus/GetDraftingStatus do not return bool");

            MP.RegisterSyncMethod(typeof(Achtung), nameof(SyncedCancelDrafting)).CancelIfAnyArgNull();

            MpCompat.harmony.Patch(addColonist,
                prefix: new HarmonyMethod(typeof(Achtung), nameof(PreAddColonist)),
                finalizer: new HarmonyMethod(typeof(Achtung), nameof(FinalizeAddColonist)));
            MpCompat.harmony.Patch(setDraftStatus, prefix: new HarmonyMethod(typeof(Achtung), nameof(PreSetDraftStatus)));
            MpCompat.harmony.Patch(cancelDrafting, prefix: new HarmonyMethod(typeof(Achtung), nameof(PreCancelDrafting)));
        }

        private static void PreAddColonist() => inAddColonist = true;

        private static void FinalizeAddColonist() => inAddColonist = false;

        // Returns the previous drafted state, like the original. Interface only; the what-if flip in AddColonist and
        // everything inside a synced command keep the direct write.
        private static bool PreSetDraftStatus(Pawn __0, bool __1, ref bool __result)
        {
            if (!MP.IsInMultiplayer || inAddColonist || MP.IsExecutingSyncCommand || __0 == null)
                return true;

            var was = (bool)getDraftingStatusMethod.Invoke(null, new object[] { __0 });
            if (was != __1)
                __0.drafter.Drafted = __1; // synced by MP: sends the command, nothing is applied here yet
            __result = was;
            return false;
        }

        private static bool PreCancelDrafting(object __0)
        {
            if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand || __0 is not IEnumerable colonists)
                return true;

            var gotDrafted = false;
            var gotUndrafted = false;
            foreach (var colonist in colonists)
            {
                var pawn = (Pawn)colonistPawnField.GetValue(colonist);
                if (pawn == null)
                    continue;

                var original = (bool)colonistOriginalDraftField.GetValue(colonist);
                var was = pawn.Drafted;
                if (original && !was)
                    gotDrafted = true;
                if (!original && was)
                    gotUndrafted = true;

                SyncedCancelDrafting(pawn, original);
            }

            if (gotDrafted)
                SoundDefOf.DraftOn.PlayOneShotOnCamera();
            if (gotUndrafted)
                SoundDefOf.DraftOff.PlayOneShotOnCamera();
            return false;
        }

        // Tools.CancelDrafting for one colonist, minus the sounds.
        private static void SyncedCancelDrafting(Pawn pawn, bool originalDrafted)
        {
            if (pawn.drafter != null && pawn.drafter.Drafted != originalDrafted)
                pawn.drafter.Drafted = originalDrafted;

            pawn.mindState?.priorityWork?.Clear();
            if (pawn.jobs?.curJob != null && pawn.jobs.IsCurrentJobPlayerInterruptible())
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
        }

        #endregion

        #region Helpers

        private static Type FindType(string name)
            => AccessTools.TypeByName(name) ?? throw new TypeLoadException($"{LogPrefix}type {name} not found");

        private static MethodInfo GetDeclaredMethod(Type type, string name)
            => AccessTools.DeclaredMethod(type, name) ?? throw new MissingMethodException(type.FullName, name);

        private static MethodInfo GetDeclaredMethod(Type type, string name, params Type[] parameters)
            => AccessTools.DeclaredMethod(type, name, parameters) ?? throw new MissingMethodException(type.FullName, name);

        private static FieldInfo GetInstanceField(Type type, string name, Type expectedType)
        {
            var field = AccessTools.DeclaredField(type, name);
            if (field == null || field.IsStatic)
                throw new MissingFieldException(type.FullName, name);
            if (expectedType != null && field.FieldType != expectedType)
                throw new MissingFieldException($"{LogPrefix}{type.FullName}.{name} is {field.FieldType}, expected {expectedType}");
            return field;
        }

        #endregion
    }
}
