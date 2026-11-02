using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace Multiplayer.Compat
{
    /// <summary>RimDark 40k - Framework (Core40k): the armour / weapon customization window</summary>
    /// <remarks>
    /// FIXES row 174. Core40k opens Dialog_CustomizeApparel / Dialog_CustomizeWeapon from the toil of an ordered job
    /// (JobDriver_OpenStylingStationDialogToCustomize*), i.e. inside the simulation, so the window opened on every machine, and its tabs
    /// wrote the SAVED comp state (CompMultiColor, CompDecorativeBase, CompAlternateTexture) straight from UI code on one machine only.
    /// Live session 21 Oct: a joiner's pawn was black on his PC and blue on the host.
    /// This module (Core40k is reached by name only, no reference to the dll):
    /// 1. Window: WindowStack.Add of those two dialogs is refused unless this machine ordered the job (Pawn_JobTracker.TryTakeOrderedJob
    ///    postfix records pawn + Job.loadID while MP.IsExecutingSyncCommandIssuedBySelf; the id survives a save reload, the Job object
    ///    does not). The dialog constructor still runs everywhere (SetOriginals, tab Setup, TickManager.Pause is a no-op in MP).
    /// 2. Every comp mutator the tabs call is a synced method: colour / mask setters, SetOriginals / Reset (Accept and Cancel),
    ///    decoration add / remove / preset / colour / mask, alternate base form, draw-data reset. Overrides of the virtual ones in other
    ///    mods (Genes40k) are found by reflection and registered too. A prefix lets the sync happen only while a customization dialog is
    ///    open for the comp's pawn; any other UI-time call keeps the old local behaviour (Multiplayer.dontSync, as ZiTools does).
    /// 3. Precision mode (debug option) edits DecorationDrawData in place every frame and calls SetDrawData every frame: those edits are
    ///    detected per frame and sent as ONE command after 0.3 s of no change (float[] copy, SyncApplyDrawData), and before any other
    ///    command on the same comp.
    /// 4. Genes40k shoulder-icon tab (CompChapterColorWithShoulderDecoration setters), if that mod is loaded.
    /// Not synced on purpose: the colour / decoration PRESET lists (Core40kModSettings, per-machine mod settings; the colours a preset
    /// applies travel as arguments), Notify_GraphicChanged (cache reset only), the window's own scroll / tab / mask-page state.
    /// Supported builds (FIXES row 369): Core40k 1.2.32 (the 1.6 folder, 253,952 bytes, what the lab and the frozen co-op copy load, the live
    /// one) and the Workshop 1.7 build 1.2.117 (1.7 folder, 683,520 bytes, for a later update). The build is detected by what exists, never by
    /// a version string: every member is looked up before anything is patched, each one under the names of both builds, and on a mismatch
    /// the module patches NOTHING (the window then opens on every machine as without it) - a window gate with unsynced edits, or a gate on
    /// a driver that waits for the window, would be worse.
    /// 1.2.117 differences handled here (found by name, so 1.2.32 never touches them): (a) signatures AddOrRemoveDecoration(def, bool free),
    /// RemoveAllDecorations(bool), LoadFromPreset(preset, bool free) and LoadFromPreset(preset, Pawn, List skipped), SetDecorationColours;
    /// (b) JobDriver_CustomizeAtStation: the toil that opened the window now WAITS on dialog.Closed, a flag the local Close click sets - on
    /// the one machine that has the window. Close() is therefore sent as ONE synced command and runs the real Close on the dialog object
    /// every machine holds (the dialog is built inside the simulation everywhere, only its WindowStack.Add is gated), so Closed flips on
    /// the same tick everywhere and the tabs' OnClose / Reset run everywhere; (c) DecorationWorkUtility.TryAccept does the paid / timed
    /// commit from the Accept click: its three state writes (CommitImmediately, CaptureAll, SettleFromMap) are redirected - at their call
    /// sites in TryAccept and its confirm lambda, because the callees are small enough for the JIT to inline - to synced commands.
    /// 29 Oct (FIXES rows 174 + 195, branch waylith-1.7-styling-fix, independent review F1-F8): (a) the module is all-or-nothing at RUN
    /// time too: <c>active</c> is set only when every lookup, registration and patch worked (Genes40k shoulder tab included, before the
    /// gate); while it is false the window is never blocked and every UI-time call stays local, as without the module. (b) The order
    /// command's own Job object is remembered in a prefix, so the "pawn already at the station" case matches only that job.
    /// (c) ResetDrawData(def, Rot4) goes through a wrapper with an int rotation (MP's Rot4 serializer turns Rot4.Invalid into North).
    /// (d) After SetAlternateBaseForm / LoadFromPreset every CompGraphicParent of the item drops its stat cache on every machine.
    /// (e) Precision mode: an entry that is the same object as its original gets a local value-equal copy before it is edited in place.
    /// (f) Pending UI bookkeeping is cleared on game load.
    /// Re-review follow-up: (g) F3 precision edits are held locally while this machine's SetOriginalDrawData is in flight; (h) F6a the
    /// ordering Job is dropped in a finalizer; (i) F10 DecorationBaseTab.Setup (simulation) drops the comps' stat caches; (j) F8 the
    /// per-command log is off unless built with MPCOMPAT_RIMDARK_LOG.
    /// 2 Nov (FIXES row 369, Opus review of the 1.2.117 port; these changes act on the 1.2.117 path only, the 1.2.32 path is untouched):
    /// (a) a Close of a dialog that is NOT the one the pawn's waiting job holds (a confirm box that outlived the job) only removes the
    /// window: its real Close would run OnClose / Reset of the tabs on this machine alone. (b) Windows the dialog opens from its own draw
    /// (colour picker, float menus, confirm boxes) are removed together with it, so a click in one of them cannot write comp state on one
    /// machine after the job ended. Only direct children are tracked; GuardPrefix still lets other UI-time callers (pawn generation, other
    /// mods) run locally, so it cannot just drop every call that comes without a dialog. (c) Genes40k 1.1.102's SetChapterIcon /
    /// SetRankIcon / SetSlotColour / SwapShoulderIcons are small and not virtual, so the tab's call sites are redirected to NoInlining
    /// wrappers (as for TryAccept). (d) The Close command carries the job's loadID and acts only on that job. (e) SetDecorationColours is
    /// required, an unknown Genes40k shoulder comp fails the module, and an accept without a pawn is ignored.
    /// Hosted MP only: on a standalone server only the issuer reloads at a join point, Core40k does not save the station dialog field, so
    /// the waiting toil can diverge.
    /// Status: [unverified] compiled only, not run in game.
    /// </remarks>
    [MpCompatFor("phonicmas.rimdark.frameworkcore")]
    internal class RimDark40kCustomization
    {
        private const string LogPrefix = "[MPCompat - RimDark 40k Customization] ";
        // One log line per synced customization command on every machine: lab evidence only. Off for play; a lab build sets the
        // compile constant MPCOMPAT_RIMDARK_LOG (static readonly, not const, so the off branch is not "unreachable code").
#if MPCOMPAT_RIMDARK_LOG
        private static readonly bool LogCommands = true;
#else
        private static readonly bool LogCommands = false;
#endif
        private const float DrawDataIdleSeconds = 0.3f;

        // Core40k types, found by name
        private static Type compGraphicParentType, compMultiColorType, compDecorativeBaseType, compAlternateTextureType;
        private static Type decorationDefType, maskDefType, alternateBaseFormDefType, decorationPresetType, decorationPresetDefType;
        private static Type[] dialogTypes = Array.Empty<Type>();
        private static readonly Dictionary<Type, FieldInfo> dialogPawnFields = new();
        private static readonly Dictionary<Type, FieldInfo> dialogTabDrawersFields = new();
        private static readonly HashSet<Type> customizeDriverTypes = new();
        private static FieldInfo dontSyncField;
        private static List<Type> compSubclasses;

        // Core40k 1.2.117 only (null / false on 1.2.32): the job driver that waits for the window and the accept / close members
        private static bool waitFlow;
        private static Type customizeAtStationType, workUtilityType;
        private static FieldInfo stationDialogField;
        private static readonly Dictionary<Type, MethodInfo> dialogCloseMethods = new();
        private static MethodInfo captureAllMethod, commitImmediatelyMethod, settleFromMapMethod, notifySkippedMethod;
        private static readonly List<MethodBase> acceptCallers = new();
        private static readonly Dictionary<MethodBase, int> redirectHits = new();
        // pawns (thingIDNumber) whose Close command is on its way: Accept / Close clicked again meanwhile are ignored
        private static readonly HashSet<int> closing = new();
        // Windows a customization dialog opened from its own draw (colour picker, float menus, confirm boxes) -> that dialog; they are removed
        // when the dialog leaves the window stack. drawingDialogs = the dialogs whose DoWindowContents is running now (a stack).
        private static readonly List<(Window child, Window owner)> dialogChildren = new();
        private static readonly List<Window> drawingDialogs = new();

        // registration bookkeeping
        private static readonly HashSet<MethodBase> registered = new();
        private static int failures, syncMethods, patches;

        // True only when everything below was found, registered and patched. While false: no window is blocked and every UI-time call
        // of a registered method runs locally (the behaviour without the module), so a half-installed module cannot make it worse.
        private static bool active;
        private static MethodInfo resetDrawDataRotMethod, notifyGraphicChangedMethod;

        // what will be registered, looked up BEFORE anything is patched (see the constructor)
        private sealed class Plan
        {
            public string what;
            public MethodInfo root;
            public bool includeOverrides, includeRoot;
            public int exposeParameter;
        }

        private static readonly List<Plan> plans = new();

        // jobs this machine ordered: (pawn thingIDNumber, Job.loadID) -> insertion number (oldest dropped past MaxJobRecords)
        private static readonly Dictionary<(int pawnId, int jobId), int> myJobs = new();
        private static int jobRecordCounter;
        private const int MaxJobRecords = 32;

        // The Job object of the customize order this machine is executing right now (set in a TryTakeOrderedJob prefix, cleared in its
        // postfix): when the pawn already stands at the station the toil opens the window INSIDE TryTakeOrderedJob, before the record.
        private static Job orderingJob;

        // All-or-nothing for Core40k: the member lookups come first and a build that does not match (the Workshop 1.7 build of Core40k
        // has other signatures and another job driver) gets NOTHING patched, so its window keeps opening everywhere as without the module.
        // The window gate goes in last and only when every registration worked.
        public RimDark40kCustomization(ModContentPack mod)
        {
            Try("find Core40k types", FindTypes);
            if (failures == 0)
                Try("look up the comp mutators", PlanCompMutators);
            if (failures == 0)
                Try("look up the precision mode members", PlanPrecisionMode);
            if (failures == 0 && waitFlow)
                Try("look up the accept / close flow (job driver that waits for the window)", PlanWaitFlow);
            if (failures > 0)
            {
                Log.Error($"{LogPrefix}this Core40k build does not have what the module syncs ({failures} problem(s) above): nothing patched, the customization window behaves as without the module.");
                throw new InvalidOperationException($"{LogPrefix}unsupported Core40k build");
            }

            Log.Message($"{LogPrefix}Core40k flow: " + (waitFlow
                ? "the customize job waits for the window (1.2.117 style): accept and close are synced commands."
                : "the customize job only opens the window (1.2.32 style)."));
            Try("register the comp mutators", RegisterPlans);
            Try("register draw data (precision mode)", RegisterPrecisionMode);
            if (waitFlow)
                Try("register the accept / close flow", RegisterWaitFlow);
            Try("Genes40k shoulder-icon tab", RegisterShoulderIconTab);
            Try("clear UI bookkeeping on game load", () =>
                Patch(AccessTools.DeclaredMethod(typeof(Game), nameof(Game.FinalizeInit)), postfix: nameof(PostGameFinalizeInit)));
            if (failures == 0)
                Try("window gating", PatchWindowGating);
            if (failures == 0)
                active = true;
            else
                Log.Error($"{LogPrefix}module NOT active because a step failed: the window opens on every machine and its edits stay local, as without the module.");

            Log.Message($"{LogPrefix}{syncMethods} sync method(s) registered, {patches} patch(es) installed, {failures} failure(s), active={active}.");
            if (failures > 0)
                throw new InvalidOperationException($"{LogPrefix}{failures} registration step(s) failed, see the errors above.");
        }

        // One log line per failed step / registration; the rest of the module keeps going.
        private static void Try(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                failures++;
                Log.Error($"{LogPrefix}could not {what}: {e.GetType().Name}: {e.Message}");
            }
        }

        private static Type TypeNamed(string name)
            => AccessTools.TypeByName(name) ?? throw new TypeLoadException($"type {name} not found");

        private static HarmonyMethod Hm(string name, int priority = Priority.First)
            => name == null ? null : new HarmonyMethod(typeof(RimDark40kCustomization), name) { priority = priority };

        private static void Patch(MethodBase target, string prefix = null, string postfix = null, string finalizer = null, int prefixPriority = Priority.First,
            string transpiler = null)
        {
            if (target == null)
                throw new MissingMethodException("patch target not found");
            MpCompat.harmony.Patch(target, prefix: Hm(prefix, prefixPriority), postfix: Hm(postfix), finalizer: Hm(finalizer), transpiler: Hm(transpiler));
            patches++;
        }

        private static void FindTypes()
        {
            compGraphicParentType = TypeNamed("Core40k.CompGraphicParent");
            compMultiColorType = TypeNamed("Core40k.CompMultiColor");
            compDecorativeBaseType = TypeNamed("Core40k.CompDecorativeBase");
            compAlternateTextureType = TypeNamed("Core40k.CompAlternateTexture");
            decorationDefType = TypeNamed("Core40k.DecorationDef");
            maskDefType = TypeNamed("Core40k.MaskDef");
            alternateBaseFormDefType = TypeNamed("Core40k.AlternateBaseFormDef");
            decorationPresetType = TypeNamed("Core40k.DecorationPreset");
            decorationPresetDefType = TypeNamed("Core40k.DecorationPresetDef");

            dialogTypes = new[] { TypeNamed("Core40k.Dialog_CustomizeApparel"), TypeNamed("Core40k.Dialog_CustomizeWeapon") };
            foreach (var dialog in dialogTypes)
            {
                dialogPawnFields[dialog] = AccessTools.DeclaredField(dialog, "pawn") ?? throw new MissingFieldException(dialog.FullName, "pawn");
                dialogTabDrawersFields[dialog] = AccessTools.DeclaredField(dialog, "tabDrawers");
            }

            customizeDriverTypes.Add(TypeNamed("Core40k.JobDriver_OpenStylingStationDialogToCustomizeApparel"));
            customizeDriverTypes.Add(TypeNamed("Core40k.JobDriver_OpenStylingStationDialogToCustomizeWeapon"));

            // The build is told apart by what exists: 1.2.117 has an abstract base driver (JobDriver_CustomizeAtStation) whose toil waits for
            // dialog.Closed; 1.2.32 has only the two drivers above, which open the window and end.
            customizeAtStationType = AccessTools.TypeByName("Core40k.JobDriver_CustomizeAtStation");
            waitFlow = customizeAtStationType != null;

            // Same field ZiTools uses: while true, synced methods run locally instead of being sent. Required: without it the module
            // could not leave other interface-time callers of these methods alone.
            dontSyncField = AccessTools.Field(AccessTools.TypeByName("Multiplayer.Client.Multiplayer"), "dontSync")
                            ?? throw new MissingFieldException("Multiplayer.Client.Multiplayer", "dontSync");
        }

        #region Window: open only on the machine that ordered the job

        private static void PatchWindowGating()
        {
            Try("patch WindowStack.Add", () =>
                Patch(AccessTools.DeclaredMethod(typeof(WindowStack), nameof(WindowStack.Add), new[] { typeof(Window) }), prefix: nameof(PreWindowStackAdd)));
            Try("patch Pawn_JobTracker.TryTakeOrderedJob", () =>
                Patch(AccessTools.DeclaredMethod(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.TryTakeOrderedJob)),
                    prefix: nameof(PreTryTakeOrderedJob), postfix: nameof(PostTryTakeOrderedJob), finalizer: nameof(FinalizeTryTakeOrderedJob),
                    prefixPriority: Priority.Normal));
        }

        private static bool IsCustomizeJob(Job job)
        {
            var driver = job?.def?.driverClass;
            return driver != null && customizeDriverTypes.Contains(driver);
        }

        // Runs inside the synced TryTakeOrderedJob command on every machine; only the machine that issued it records the job.
        // MP's own TryTakeOrderedJob prefix has given the job its real loadID by now (ids made in the interface are negative).
        // No tick clock in the bookkeeping (it is the map's clock under async time): the oldest record goes when there are too many.
        // Object identity, not loadID, so it does not matter whether MP's own prefix has run yet.
        private static void PreTryTakeOrderedJob(Job job)
        {
            orderingJob = MP.IsInMultiplayer && MP.IsExecutingSyncCommandIssuedBySelf && IsCustomizeJob(job) ? job : null;
        }

        // Finalizer, so the reference is dropped also when TryTakeOrderedJob throws (review F6a).
        private static void FinalizeTryTakeOrderedJob()
        {
            orderingJob = null;
        }

        private static void PostTryTakeOrderedJob(Pawn_JobTracker __instance, Job job, bool __result)
        {
            if (!__result || !MP.IsInMultiplayer || !MP.IsExecutingSyncCommandIssuedBySelf)
                return;
            if (!IsCustomizeJob(job) || __instance?.pawn == null)
                return;

            myJobs[(__instance.pawn.thingIDNumber, job.loadID)] = ++jobRecordCounter;
            while (myJobs.Count > MaxJobRecords)
                myJobs.Remove(myJobs.OrderBy(kv => kv.Value).First().Key);
        }

        private static bool TakeJobRecord(Pawn pawn)
        {
            var job = pawn?.CurJob;
            return job != null && myJobs.Remove((pawn.thingIDNumber, job.loadID));
        }

        // Only adds made by the simulation are judged (the job toil, or the order command itself when the pawn already stands at the
        // station). A window added from the interface was clicked on this machine, so it is always allowed.
        private static bool PreWindowStackAdd(Window window)
        {
            if (window == null || !active || !MP.IsInMultiplayer || MP.InInterface)
                return true;
            if (!TryGetDialogPawn(window, out var pawn))
                return true;

            // 1) the toil of a job this machine ordered (record); 2) the order command itself when the pawn already stood at the
            // station and the toil ran inside TryTakeOrderedJob (the record is written after it returns).
            bool mine = TakeJobRecord(pawn) || (orderingJob != null && pawn?.CurJob == orderingJob);
            Log.Message($"{LogPrefix}{window.GetType().Name} for {pawn?.LabelShort ?? "null"} (tick {Find.TickManager?.TicksGame}): " +
                        (mine ? "ordered by this machine, window opened." : "ordered by another player, window not opened here."));
            return mine;
        }

        private static bool TryGetDialogPawn(Window window, out Pawn pawn)
        {
            pawn = null;
            foreach (var type in dialogTypes)
            {
                if (!type.IsInstanceOfType(window))
                    continue;
                pawn = dialogPawnFields[type].GetValue(window) as Pawn;
                return true;
            }

            return false;
        }

        private static Pawn PawnOf(ThingComp comp)
        {
            var holder = comp?.parent?.ParentHolder;
            return (holder as Pawn_ApparelTracker)?.pawn ?? (holder as Pawn_EquipmentTracker)?.pawn;
        }

        // True while a customization window of this machine is open for the pawn that wears / holds the comp. A comp that has left its
        // pawn since the tab captured it (apparel dropped or swapped while the window is open) counts as "in the window" whenever any
        // customization window is open: the tab can only still be editing it, and a local-only edit would be a silent divergence.
        private static bool CustomizeDialogOpenFor(ThingComp comp)
        {
            var pawn = PawnOf(comp);
            var windows = Find.WindowStack?.Windows;
            if (windows == null)
                return false;
            foreach (var window in windows)
                if (window != null && TryGetDialogPawn(window, out var dialogPawn) && (pawn == null || dialogPawn == pawn))
                    return true;
            return false;
        }

        #endregion

        #region Core40k 1.2.117: the customize job waits for the window; Accept and Close are synced commands

        // 1.2.117 JobDriver_CustomizeAtStation, per machine: toil 1 builds the dialog (every machine) and adds the window (gated above, so only
        // the machine that ordered the job gets one); toil 2 ends when dialog.Closed - which only the local Close() click sets. The commit
        // that follows (QueueIngredients, hauling, work, SettleFromInventory) reads comp state written by Accept. So:
        // - Accept: DecorationWorkUtility.TryAccept ends in CommitImmediately / CaptureAll / SettleFromMap. Each is redirected, at its call
        //   site, to a synced command that runs the original on every machine with the comps of the dialog (the arguments travel).
        // - Close: the real Close() runs INSIDE one synced command on the dialog object the job driver holds on every machine. Closed flips
        //   on the same tick everywhere; the tabs' OnClose / Reset run everywhere (Genes40k's shoulder tab ends its editing flag there).
        //   The local click only queues the command (the window closes when it comes back).

        // Lookup only (nothing patched yet).
        private static void PlanWaitFlow()
        {
            stationDialogField = AccessTools.DeclaredField(customizeAtStationType, "dialog")
                                 ?? throw new MissingFieldException(customizeAtStationType.FullName, "dialog");
            foreach (var dialog in dialogTypes)
            {
                // the flag the waiting toil reads (a private setter: only Close() writes it)
                if (AccessTools.DeclaredProperty(dialog, "Closed") == null)
                    throw new MissingMemberException(dialog.FullName, "Closed");
                dialogCloseMethods[dialog] = AccessTools.DeclaredMethod(dialog, "Close", new[] { typeof(bool) })
                                             ?? throw new MissingMethodException(dialog.FullName, "Close(bool)");
            }

            workUtilityType = TypeNamed("Core40k.DecorationWorkUtility");
            var compList = typeof(List<>).MakeGenericType(compGraphicParentType);
            captureAllMethod = Declared(workUtilityType, "CaptureAll", compList)
                               ?? throw new MissingMethodException(workUtilityType.FullName, "CaptureAll(List<CompGraphicParent>)");
            commitImmediatelyMethod = Declared(workUtilityType, "CommitImmediately", typeof(Pawn), compList)
                                      ?? throw new MissingMethodException(workUtilityType.FullName, "CommitImmediately(Pawn, List<CompGraphicParent>)");
            settleFromMapMethod = Declared(workUtilityType, "SettleFromMap", typeof(Pawn))
                                  ?? throw new MissingMethodException(workUtilityType.FullName, "SettleFromMap(Pawn)");
            // optional: the "some preset parts were not available" message, shown on the clicking machine only
            var notifySkipped = AccessTools.DeclaredMethod(precisionTabType, "NotifySkippedPresetDecorations", new[] { typeof(List<>).MakeGenericType(decorationDefType) });
            notifySkippedMethod = notifySkipped is { IsStatic: true } ? notifySkipped : null;

            redirects.Clear();
            redirects.Add((captureAllMethod, AccessTools.DeclaredMethod(typeof(RimDark40kCustomization), nameof(RedirectCaptureAll))));
            redirects.Add((commitImmediatelyMethod, AccessTools.DeclaredMethod(typeof(RimDark40kCustomization), nameof(RedirectCommitImmediately))));
            redirects.Add((settleFromMapMethod, AccessTools.DeclaredMethod(typeof(RimDark40kCustomization), nameof(RedirectSettleFromMap))));

            // The callers: TryAccept and the lambda of its confirm window. Found by what their code calls, not by compiler-made names.
            acceptCallers.Clear();
            var found = new HashSet<MethodBase>();
            foreach (var type in new[] { workUtilityType }.Concat(workUtilityType.GetNestedTypes(AccessTools.all)))
                foreach (var method in AccessTools.GetDeclaredMethods(type))
                {
                    if (method.IsAbstract || method.GetMethodBody() == null)
                        continue;
                    try
                    {
                        foreach (var instruction in PatchProcessor.GetOriginalInstructions(method))
                            if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && RedirectFor(instruction.operand as MethodBase).redirect != null)
                            {
                                if (acceptCallers.All(m => m != method))
                                    acceptCallers.Add(method);
                                found.Add(RedirectFor(instruction.operand as MethodBase).callee);
                            }
                    }
                    catch (Exception e)
                    {
                        Log.Warning($"{LogPrefix}could not read the code of {type.Name}.{method.Name}: {e.GetType().Name}: {e.Message}");
                    }
                }

            foreach (var (callee, _) in redirects)
                if (!found.Contains(callee))
                    throw new MissingMethodException($"no call of {workUtilityType.Name}.{callee.Name} found in {workUtilityType.Name} or its lambdas");
            redirectHits.Clear();
        }

        private static (MethodInfo callee, MethodInfo redirect) RedirectFor(MethodBase operand)
        {
            if (operand == null)
                return default;
            foreach (var pair in redirects)
                if (operand == pair.callee || (operand.MetadataToken == pair.callee.MetadataToken && operand.Module == pair.callee.Module))
                    return pair;
            return default;
        }

        private static readonly List<(MethodInfo callee, MethodInfo redirect)> redirects = new();

        private static void RegisterWaitFlow()
        {
            foreach (var name in new[] { nameof(SyncCloseDialog), nameof(SyncCaptureAll), nameof(SyncCommitImmediately), nameof(SyncSettleFromMap) })
                Try("sync " + name, () =>
                {
                    var method = AccessTools.DeclaredMethod(typeof(RimDark40kCustomization), name);
                    MP.RegisterSyncMethod(method).SetPostInvoke((target, args) => AfterAcceptCommand(method, args));
                    syncMethods++;
                });

            foreach (var dialog in dialogTypes)
                Try("patch " + dialog.Name + ".Close", () =>
                    Patch(dialogCloseMethods[dialog], prefix: nameof(PreDialogClose), prefixPriority: Priority.Normal));

            // the windows a dialog opens from its draw are removed with it (the job's finish action removes the dialog without Close)
            foreach (var dialog in dialogTypes)
                Try("patch " + dialog.Name + ".DoWindowContents (child windows)", () =>
                    Patch(AccessTools.DeclaredMethod(dialog, "DoWindowContents"), prefix: nameof(PreDialogDraw), finalizer: nameof(FinalizeDialogDraw),
                        prefixPriority: Priority.Normal));
            Try("patch WindowStack.Add (child windows)", () =>
                Patch(AccessTools.DeclaredMethod(typeof(WindowStack), nameof(WindowStack.Add), new[] { typeof(Window) }), postfix: nameof(PostWindowStackAddTrack)));
            Try("patch WindowStack.TryRemove (child windows)", () =>
                Patch(AccessTools.DeclaredMethod(typeof(WindowStack), nameof(WindowStack.TryRemove), new[] { typeof(Window), typeof(bool) }),
                    postfix: nameof(PostWindowStackTryRemove)));

            Try("redirect the accept calls of DecorationWorkUtility.TryAccept", () =>
            {
                foreach (var caller in acceptCallers)
                    Patch(caller, transpiler: nameof(RedirectAcceptCalls));
                foreach (var (callee, _) in redirects)
                    if (!redirectHits.TryGetValue(callee, out var hits) || hits == 0)
                        throw new MissingMethodException($"the transpilers changed no call of {callee.Name}");
            });
        }

        // Harmony transpiler on TryAccept and its confirm lambda: same stack, only the callee differs. Changing the call at the call site
        // (and not patching CaptureAll / SettleFromMap) matters: they are small and may be inlined into their callers, skipping a patch.
        private static IEnumerable<CodeInstruction> RedirectAcceptCalls(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var instruction in instructions)
            {
                if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && instruction.operand is MethodBase callee)
                {
                    var (original, redirect) = RedirectFor(callee);
                    if (redirect != null)
                    {
                        redirectHits[original] = (redirectHits.TryGetValue(original, out var hits) ? hits : 0) + 1;
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = redirect;
                    }
                }

                yield return instruction;
            }
        }

        private static object CurrentStationDialog(Pawn pawn)
        {
            var driver = pawn?.jobs?.curDriver;
            return driver != null && customizeAtStationType.IsInstanceOfType(driver) ? stationDialogField.GetValue(driver) : null;
        }

        private static void InvokeOriginal(MethodInfo method, params object[] args)
        {
            try
            {
                method.Invoke(null, args);
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            }
        }

        // Same for an instance method (the redirect wrappers of the Genes40k shoulder tab).
        private static void InvokeOn(MethodInfo method, object target, params object[] args)
        {
            if (target == null)
                throw new NullReferenceException();
            try
            {
                method.Invoke(target, args);
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            }
        }

        private static bool CustomizeDialogOpenForPawn(Pawn pawn)
        {
            var windows = Find.WindowStack?.Windows;
            if (windows == null)
                return false;
            foreach (var window in windows)
                if (window != null && TryGetDialogPawn(window, out var dialogPawn) && dialogPawn == pawn)
                    return true;
            return false;
        }

        private enum AcceptMode { Local, Sync, Ignore }

        // The pawn of the customization window this machine has open, when there is exactly one (fallback for comps that left their pawn).
        private static Pawn OpenDialogPawn()
        {
            Pawn found = null;
            var windows = Find.WindowStack?.Windows;
            if (windows == null)
                return null;
            foreach (var window in windows)
                if (window != null && TryGetDialogPawn(window, out var dialogPawn) && dialogPawn != null)
                {
                    if (found != null && found != dialogPawn)
                        return null;
                    found = dialogPawn;
                }
            return found;
        }

        private static bool warnedStaleAccept;

        // Interface time with this pawn's customization window open = the Accept click: send it. Singleplayer, a simulation call or the module
        // off keeps the original, local behaviour. A Close command already on its way: the click is a double click, ignored. Interface time
        // WITHOUT the window (the job ended while the confirm window was up; the only callers are the two dialogs' Accept) is ignored too:
        // running the write here would change this machine only. So is an accept with no pawn at all (no command can be tied to a pawn).
        private static AcceptMode ModeForAccept(Pawn pawn)
        {
            if (!active || !MP.IsInMultiplayer || !MP.InInterface)
                return AcceptMode.Local;
            if (pawn == null)
            {
                if (!warnedStaleAccept)
                {
                    warnedStaleAccept = true;
                    Log.Warning($"{LogPrefix}an accept came in with no pawn to tie it to (the comps left their pawn): ignored, nothing was changed.");
                }

                return AcceptMode.Ignore;
            }

            if (!CustomizeDialogOpenForPawn(pawn))
            {
                if (!warnedStaleAccept)
                {
                    warnedStaleAccept = true;
                    Log.Warning($"{LogPrefix}an accept for {pawn.LabelShort} came in without an open customization window (the job probably ended): ignored, nothing was changed.");
                }

                return AcceptMode.Ignore;
            }

            return closing.Contains(pawn.thingIDNumber) ? AcceptMode.Ignore : AcceptMode.Sync;
        }

        private static List<ThingComp> CompsOf(object comps)
            => (comps as IEnumerable)?.OfType<ThingComp>().Where(c => c != null).ToList() ?? new List<ThingComp>();

        private static IList ToCompGraphicParentList(IEnumerable<ThingComp> comps)
        {
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(compGraphicParentType));
            foreach (var comp in comps)
                if (comp != null && compGraphicParentType.IsInstanceOfType(comp))
                    list.Add(comp);
            return list;
        }

        // Precision edits that rest locally go out before the accept (same order the user acted in).
        private static void FlushAllDrawData()
        {
            try
            {
                foreach (var key in pendingDrawData.Keys.ToList())
                {
                    pendingDrawData.Remove(key);
                    SendDrawData(key.comp, key.def);
                }
            }
            catch (Exception e)
            {
                Log.Warning($"{LogPrefix}could not send pending precision edits: {e.GetType().Name}: {e.Message}");
            }
        }

        // The callers below are called from TryAccept (and its lambda) with the original's signature; the list argument is typed object
        // because Core40k is reached by name only.
        private static void RedirectCaptureAll(object comps)
        {
            var list = CompsOf(comps);
            var pawn = list.Select(PawnOf).FirstOrDefault(p => p != null) ?? (list.Count > 0 && MP.IsInMultiplayer && MP.InInterface ? OpenDialogPawn() : null);
            switch (ModeForAccept(pawn))
            {
                case AcceptMode.Sync:
                    FlushAllDrawData();
                    SyncCaptureAll(pawn, list);
                    break;
                case AcceptMode.Ignore:
                    break;
                default:
                    InvokeOriginal(captureAllMethod, comps);
                    break;
            }
        }

        private static void RedirectCommitImmediately(Pawn pawn, object comps)
        {
            switch (ModeForAccept(pawn))
            {
                case AcceptMode.Sync:
                    FlushAllDrawData();
                    SyncCommitImmediately(pawn, CompsOf(comps));
                    break;
                case AcceptMode.Ignore:
                    break;
                default:
                    InvokeOriginal(commitImmediatelyMethod, pawn, comps);
                    break;
            }
        }

        private static void RedirectSettleFromMap(Pawn pawn)
        {
            switch (ModeForAccept(pawn))
            {
                case AcceptMode.Sync:
                    SyncSettleFromMap(pawn);
                    break;
                case AcceptMode.Ignore:
                    break;
                default:
                    InvokeOriginal(settleFromMapMethod, pawn);
                    break;
            }
        }

        // The synced commands. The pawn comes first in each so the command is tied to the pawn's map. NoInlining: MP patches these methods and
        // a body inlined into the redirect above would skip the patch.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SyncCaptureAll(Pawn pawn, List<ThingComp> comps)
            => InvokeOriginal(captureAllMethod, ToCompGraphicParentList(comps));

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SyncCommitImmediately(Pawn pawn, List<ThingComp> comps)
            => InvokeOriginal(commitImmediatelyMethod, pawn, ToCompGraphicParentList(comps));

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SyncSettleFromMap(Pawn pawn)
            => InvokeOriginal(settleFromMapMethod, pawn);

        // Prefix of both dialogs' Close(bool). Interface time, and this window is the dialog the pawn's waiting job holds: queue ONE command
        // that closes it on every machine and skip the local close. Interface time and ANY OTHER dialog (a window left over from an ended
        // job, e.g. the confirm box of Accept outlived the job and its confirm now closes the dialog): the real Close would run OnClose /
        // Reset of every tab, i.e. comp.Reset(), on this machine only. Only remove the window, nothing else. The command itself running the
        // original, and singleplayer, run as before.
        private static bool PreDialogClose(Window __instance, bool doCloseSound)
        {
            if (!active || !MP.IsInMultiplayer || !MP.InInterface || !TryGetDialogPawn(__instance, out var pawn) || pawn == null)
                return true;
            if (!ReferenceEquals(CurrentStationDialog(pawn), __instance))
            {
                Find.WindowStack.TryRemove(__instance, doCloseSound);
                return false;
            }

            if (!closing.Add(pawn.thingIDNumber))
                return false;

            FlushAllDrawData();
            try
            {
                SyncCloseDialog(pawn, doCloseSound, pawn.CurJob?.loadID ?? -1);
                return false;
            }
            catch (Exception e)
            {
                closing.Remove(pawn.thingIDNumber);
                Log.Error($"{LogPrefix}could not send the close of the customization window, closing it locally: {e.GetType().Name}: {e.Message}");
                return true;
            }
        }

        // Runs on every machine: the real Close() of the dialog object the pawn's job holds. It sets Closed, runs OnClose / Reset of the tabs
        // and removes the window where there is one. The job's own waiting toil then moves on at the same tick everywhere.
        // jobId = loadID of the pawn's job when the close was clicked: a command that arrives after that job ended (and maybe after
        // another customize job began) must not close the new job's dialog.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SyncCloseDialog(Pawn pawn, bool doCloseSound, int jobId)
        {
            if (pawn != null)
                closing.Remove(pawn.thingIDNumber);
            if (pawn?.CurJob == null || pawn.CurJob.loadID != jobId)
            {
                Log.Warning($"{LogPrefix}a close for {pawn?.LabelShort ?? "null"} (job {jobId}) came in after that job ended (now {pawn?.CurJob?.loadID.ToString() ?? "none"}): ignored.");
                return;
            }

            var dialog = CurrentStationDialog(pawn);
            if (dialog == null)
                return;
            foreach (var type in dialogTypes)
                if (type.IsInstanceOfType(dialog))
                {
                    try
                    {
                        dialogCloseMethods[type].Invoke(dialog, new object[] { doCloseSound });
                    }
                    catch (Exception e)
                    {
                        Log.Warning($"{LogPrefix}closing {type.Name} for {pawn?.LabelShort ?? "null"} failed: {(e.InnerException ?? e).GetType().Name}: {(e.InnerException ?? e).Message}");
                    }

                    return;
                }
        }

        // The job's finish action removes the dialog with WindowStack.TryRemove, not Close, so a colour picker or a float menu the dialog had
        // opened stays on screen, and the click that finishes it writes comp state (ColoringBaseTab: multiColor.DrawColor = newColour) on this
        // machine only. Windows added while a dialog is drawing (= opened by one of its tabs) are therefore remembered with that dialog and
        // removed when it leaves the window stack, by whatever path. GuardPrefix cannot do this by dropping every call that comes without a
        // dialog: pawn generation (Core40kUtils.SetupCustomizationForThing / ForPawn, Genes40kUtils.ApplyChapterColours, the starting chapter
        // scenario part, CompChapterColor.InitialColors) and other mods call the same setters at interface time on purpose, locally.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PreDialogDraw(Window __instance, ref bool __state)
        {
            __state = false;
            if (!active || !MP.IsInMultiplayer || !MP.InInterface)
                return;
            __state = true;
            drawingDialogs.Add(__instance);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void FinalizeDialogDraw(bool __state)
        {
            if (__state && drawingDialogs.Count > 0)
                drawingDialogs.RemoveAt(drawingDialogs.Count - 1);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PostWindowStackAddTrack(Window window)
        {
            if (drawingDialogs.Count == 0 || window == null || !active || !MP.IsInMultiplayer || !MP.InInterface || TryGetDialogPawn(window, out _))
                return;
            try
            {
                var windows = Find.WindowStack?.Windows;
                if (windows == null || !windows.Contains(window))
                    return;
                dialogChildren.RemoveAll(c => !windows.Contains(c.child));
                dialogChildren.Add((window, drawingDialogs[drawingDialogs.Count - 1]));
            }
            catch (Exception e)
            {
                Log.Warning($"{LogPrefix}could not note a window opened by the customization window: {e.GetType().Name}: {e.Message}");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PostWindowStackTryRemove(Window window, bool __result)
        {
            if (!__result || dialogChildren.Count == 0 || window == null || !active || !MP.IsInMultiplayer || !TryGetDialogPawn(window, out _))
                return;
            try
            {
                var children = dialogChildren.Where(c => c.owner == window).Select(c => c.child).ToList();
                dialogChildren.RemoveAll(c => c.owner == window);
                int removed = 0;
                foreach (var child in children)
                    if (Find.WindowStack.TryRemove(child, false))
                        removed++;
                if (removed > 0)
                    Log.Message($"{LogPrefix}{window.GetType().Name} left the window stack: {removed} window(s) it had opened were closed with it.");
            }
            catch (Exception e)
            {
                Log.Warning($"{LogPrefix}could not close the windows opened by the customization window: {e.GetType().Name}: {e.Message}");
            }
        }

        // After each of the four commands, on every machine.
        private static void AfterAcceptCommand(MethodBase method, object[] args)
        {
            try
            {
                if (args != null && args.Length > 0 && args[0] is Pawn pawn)
                    pawn.Drawer?.renderer?.SetAllGraphicsDirty();
                if (MP.IsExecutingSyncCommandIssuedBySelf)
                    MarkTabsForRecache();
            }
            catch (Exception e)
            {
                Log.Warning($"{LogPrefix}graphics refresh after {method.Name} failed: {e.GetType().Name}: {e.Message}");
            }

            if (LogCommands)
                Log.Message($"MPCompat :: RimDark40k customization {method.Name} (pawn {(args != null && args.Length > 0 ? (args[0] as Pawn)?.ThingID : null)}) " +
                            $"tick={Find.TickManager?.TicksGame} bySelf={MP.IsExecutingSyncCommandIssuedBySelf}");
        }

        #endregion

        #region Comp mutators

        // Prefix on every synced Core40k method. Interface code with the window open: flush pending precision edits of the comp, then
        // let Multiplayer send the call. Interface code without it (pawn generation, other mods): keep the old local behaviour.
        private static void GuardPrefix(ThingComp __instance, ref bool __state)
        {
            __state = false;
            if (!MP.IsInMultiplayer || !MP.InInterface)
                return;
            if (active && CustomizeDialogOpenFor(__instance))
            {
                try
                {
                    FlushDrawData(__instance);
                }
                catch (Exception e)
                {
                    Log.Warning($"{LogPrefix}could not send pending precision edits: {e.GetType().Name}: {e.Message}");
                }

                return;
            }

            if (!(bool)dontSyncField.GetValue(null))
            {
                dontSyncField.SetValue(null, true);
                __state = true;
            }
        }

        private static void GuardFinalizer(bool __state)
        {
            if (__state)
                dontSyncField.SetValue(null, false);
        }

        // Step 1 (lookup only): remember root (unless includeRoot is false) and, for virtual methods, the overrides declared in a
        // CompGraphicParent subclass of any loaded mod. exposeParameter >= 0: that argument travels through ExposeData.
        private static void PlanSync(string what, MethodInfo root, bool includeOverrides, bool includeRoot = true, int exposeParameter = -1)
        {
            Try("find " + what, () =>
            {
                if (root == null)
                    throw new MissingMethodException(what);
                plans.Add(new Plan { what = what, root = root, includeOverrides = includeOverrides, includeRoot = includeRoot, exposeParameter = exposeParameter });
            });
        }

        // Step 2: register every planned method as a synced method with the guard patch.
        private static void RegisterPlans()
        {
            foreach (var plan in plans.ToList())
            {
                var targets = new List<MethodInfo>();
                if (plan.includeRoot)
                    targets.Add(plan.root);
                if (plan.includeOverrides && plan.root.IsVirtual)
                    targets.AddRange(FindOverrides(plan.root));

                foreach (var method in targets)
                    Try($"sync {method.DeclaringType?.Name}.{method.Name}", () =>
                    {
                        if (!registered.Add(method))
                            return;
                        var sync = MP.RegisterSyncMethod(method).SetPostInvoke((target, args) => AfterCommand(method, target, args));
                        if (plan.exposeParameter >= 0)
                            sync.ExposeParameter(plan.exposeParameter);
                        Patch(method, prefix: nameof(GuardPrefix), finalizer: nameof(GuardFinalizer), prefixPriority: Priority.High);
                        syncMethods++;
                    });
            }

            plans.Clear();
        }

        private static IEnumerable<MethodInfo> FindOverrides(MethodInfo root)
        {
            compSubclasses ??= AccessTools.AllTypes()
                .Where(t => t != compGraphicParentType && compGraphicParentType.IsAssignableFrom(t))
                .OrderBy(t => t.FullName, StringComparer.Ordinal)
                .ToList();
            var parameterTypes = root.GetParameters().Select(p => p.ParameterType).ToArray();
            foreach (var type in compSubclasses)
            {
                var method = type.GetMethod(root.Name, AccessTools.allDeclared, null, parameterTypes, null);
                if (method != null && method != root && method.IsVirtual && method.GetBaseDefinition() == root.GetBaseDefinition())
                    yield return method;
            }
        }

        // Exact parameter list, also when it is empty: 1.2.117 has RemoveAllDecorations() and RemoveAllDecorations(bool), and a lookup by
        // name only would be ambiguous.
        private static MethodInfo Declared(Type type, string name, params Type[] parameters)
            => AccessTools.DeclaredMethod(type, name, parameters.Length == 0 ? Type.EmptyTypes : parameters);

        // One plan per candidate that exists (the same member under the names / signatures of each Core40k build); at least one must exist.
        private static void PlanSyncAny(string what, bool includeOverrides, int exposeParameter, params MethodInfo[] candidates)
        {
            var found = candidates.Where(c => c != null).ToList();
            if (found.Count == 0)
            {
                PlanSync(what, null, includeOverrides);
                return;
            }

            foreach (var candidate in found)
                PlanSync(what + " " + ParameterText(candidate), candidate, includeOverrides, exposeParameter: exposeParameter);
        }

        // A member only some builds have (the other build's UI does not call it): planned when found, skipped when not.
        private static void PlanSyncOptional(string what, MethodInfo root, bool includeOverrides)
        {
            if (root != null)
                PlanSync(what, root, includeOverrides);
        }

        private static string ParameterText(MethodBase method)
            => "(" + string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name)) + ")";

        private static void PlanCompMutators()
        {
            var colour = typeof(Color);

            // CompMultiColor: the three colour swatches (picker, presets) and the mask page
            foreach (var property in new[] { "DrawColor", "DrawColorTwo", "DrawColorThree", "MaskDef" })
                PlanSync("CompMultiColor." + property, AccessTools.DeclaredPropertySetter(compMultiColorType, property), false);

            // SetOriginals (Accept) and Reset (Cancel / Close / the Reset button): every override, base class excluded (empty)
            PlanSync("CompGraphicParent.SetOriginals", Declared(compGraphicParentType, "SetOriginals"), true, includeRoot: false);
            PlanSync("CompGraphicParent.Reset", Declared(compGraphicParentType, "Reset"), true, includeRoot: false);

            // CompDecorativeBase: decorations tab (icons, presets, per-decoration colours / mask) and the precision buttons
            var deco = compDecorativeBaseType;
            var decorationList = typeof(List<>).MakeGenericType(decorationDefType);
            // Signatures per build: AddOrRemoveDecoration(def) 1.2.32 / (def, bool free) 1.2.117; RemoveAllDecorations() both, plus (bool upgrades)
            // 1.2.117; LoadFromPreset(preset) 1.2.32 / (preset, bool free) and (preset, Pawn, List skipped) 1.2.117 - the dialog calls the
            // 3-argument one, whose skipped list is only the "some were unavailable" message of the clicking machine.
            PlanSyncAny("AddOrRemoveDecoration", true, -1,
                Declared(deco, "AddOrRemoveDecoration", decorationDefType),
                Declared(deco, "AddOrRemoveDecoration", decorationDefType, typeof(bool)));
            PlanSyncAny("RemoveAllDecorations", true, -1,
                Declared(deco, "RemoveAllDecorations"),
                Declared(deco, "RemoveAllDecorations", typeof(bool)));
            PlanSyncAny("LoadFromPreset(DecorationPreset)", true, 0,
                Declared(deco, "LoadFromPreset", decorationPresetType),
                Declared(deco, "LoadFromPreset", decorationPresetType, typeof(bool)),
                Declared(deco, "LoadFromPreset", decorationPresetType, typeof(Pawn), decorationList));
            PlanSyncAny("LoadFromPreset(DecorationPresetDef)", false, -1,
                Declared(deco, "LoadFromPreset", decorationPresetDefType),
                Declared(deco, "LoadFromPreset", decorationPresetDefType, typeof(bool)),
                Declared(deco, "LoadFromPreset", decorationPresetDefType, typeof(Pawn), decorationList));
            PlanSync("SetDecorationColourOne", Declared(deco, "SetDecorationColourOne", decorationDefType, colour), true);
            PlanSync("SetDecorationColourTwo", Declared(deco, "SetDecorationColourTwo", decorationDefType, colour), true);
            PlanSync("SetDecorationColourThree", Declared(deco, "SetDecorationColourThree", decorationDefType, colour), true);
            // 1.2.117 only: the preset menu sets the three colours in one call. Required there (a build that waits for the window and has no
            // such member is not the one this module was written for); 1.2.32 does not have it and does not need it.
            var setColours = Declared(deco, "SetDecorationColours", decorationDefType, colour, colour, colour);
            if (waitFlow)
                PlanSync("SetDecorationColours", setColours, false);
            else
                PlanSyncOptional("SetDecorationColours", setColours, false);
            PlanSync("SetDecorationMask", Declared(deco, "SetDecorationMask", decorationDefType, maskDefType), false);
            PlanSync("SetDecorationToParentColors", Declared(deco, "SetDecorationToParentColors", decorationDefType), false);
            PlanSync("SetDefaultColors", Declared(deco, "SetDefaultColors", decorationDefType, typeof(bool)), true);
            PlanSync("SetOriginalDrawData", Declared(deco, "SetOriginalDrawData", decorationDefType), false);
            PlanSync("ResetDrawData(def)", Declared(deco, "ResetDrawData", decorationDefType), false);
            // ResetDrawData(def, Rot4) is NOT registered as is: MP's Rot4 serializer turns Rot4.Invalid (the weapon's default data) into
            // North. A prefix sends SyncResetDrawDataRot (rotation as int, -1 = Invalid) instead, see RegisterPrecisionMode.
            resetDrawDataRotMethod = Declared(deco, "ResetDrawData", decorationDefType, typeof(Rot4))
                                     ?? throw new MissingMethodException("CompDecorativeBase.ResetDrawData(DecorationDef, Rot4)");
            notifyGraphicChangedMethod = Declared(compGraphicParentType, "Notify_GraphicChanged")
                                         ?? throw new MissingMethodException("CompGraphicParent.Notify_GraphicChanged");

            // CompAlternateTexture: the alternate base form tab (also removes decorations, resets colours, swaps abilities)
            PlanSync("SetAlternateBaseForm", Declared(compAlternateTextureType, "SetAlternateBaseForm", alternateBaseFormDefType, typeof(bool)), false);
        }

        // Runs on EVERY machine right after a synced call: the host / arbiter / other clients get their graphics refreshed too
        // (the window's own Reset does SetAllGraphicsDirty on the issuer only). One log line per command = evidence in the lab logs.
        private static void AfterCommand(MethodBase method, object target, object[] args)
        {
            var comp = target as ThingComp ?? (args != null && args.Length > 0 ? args[0] as ThingComp : null);
            if (comp == null)
                return;

            var pawn = PawnOf(comp);
            try
            {
                // every machine: these two change decorations of the item without the decorative comp dropping its cached stat
                // offsets / factors (filled by stat reads, which can be UI reads on one machine only)
                if (method.Name is "SetAlternateBaseForm" or "LoadFromPreset" && comp.parent is ThingWithComps item)
                    foreach (var other in item.AllComps.Where(c => compGraphicParentType.IsInstanceOfType(c)).ToList())
                        notifyGraphicChangedMethod.Invoke(other, null);
                pawn?.Drawer?.renderer?.SetAllGraphicsDirty();
                if (MP.IsExecutingSyncCommandIssuedBySelf)
                {
                    // 1.2.117 preset menu: the clicked machine's own "skipped" list stayed empty (its call was sent, not run); the list the
                    // command filled is the last argument, so the "some parts were unavailable" message is shown here, on this machine only
                    if (notifySkippedMethod != null && method.Name == "LoadFromPreset" && args is { Length: 3 } && args[2] is IList { Count: > 0 } skipped)
                        notifySkippedMethod.Invoke(null, new object[] { skipped });
                    // F3: the original this machine sent has landed; precision edits of that decoration may go out again
                    if (method.Name == "SetOriginalDrawData" && args is { Length: > 0 } && args[0] is Def originalDef)
                        awaitingOriginal.Remove((comp, originalDef));
                    MarkTabsForRecache();
                    if (method.Name is "ResetDrawData" or nameof(SyncResetDrawDataRot))
                        ClearPrecisionBuffers();
                }
            }
            catch (Exception e)
            {
                Log.Warning($"{LogPrefix}graphics refresh after {method.Name} failed: {e.GetType().Name}: {e.Message}");
            }

            if (LogCommands)
                Log.Message($"MPCompat :: RimDark40k customization {method.DeclaringType?.Name}.{method.Name} on {comp.parent?.ThingID} " +
                            $"(pawn {pawn?.ThingID}) tick={Find.TickManager?.TicksGame} bySelf={MP.IsExecutingSyncCommandIssuedBySelf}");
        }

        // The colour / mask thumbnails of the open window are cached under a private "recache" flag that the UI sets when you click,
        // i.e. BEFORE the command comes back; set it again once the new colours are really in.
        private static void MarkTabsForRecache()
        {
            var windows = Find.WindowStack?.Windows;
            if (windows == null)
                return;
            foreach (var window in windows)
            {
                if (window == null || !TryGetDialogPawn(window, out _))
                    continue;
                foreach (var type in dialogTypes)
                {
                    if (!type.IsInstanceOfType(window) || dialogTabDrawersFields[type]?.GetValue(window) is not IDictionary drawers)
                        continue;
                    foreach (DictionaryEntry entry in drawers)
                        AccessTools.Field(entry.Value.GetType(), "recache")?.SetValue(entry.Value, true);
                }
            }
        }

        #endregion

        #region Precision mode (debug option): draw data edited in place, SetDrawData called every frame

        private sealed class PrecisionState
        {
            public ThingComp comp;
            public Def def;
            public bool existed;
            public float[] before;
        }

        private static Type drawDataType;
        private static FieldInfo selectedCompField, selectedDefField, compDrawDatasField, compOriginalDrawDatasField;
        private static MethodInfo drawDataCopyFromMethod;
        private static FieldInfo[] rotationFields;
        private static FieldInfo rotationalRotation, rotationalOffset, rotationalLayer, rotationalScale;
        private static MethodInfo setDrawDataMethod;
        private static float[] defaultDrawDataDto;
        private static readonly Dictionary<(ThingComp comp, Def def), float> pendingDrawData = new();

        private static FieldInfo Field(Type type, string name)
            => AccessTools.DeclaredField(type, name) ?? throw new MissingFieldException(type.FullName, name);

        private static Type precisionTabType;
        private static FieldInfo[] precisionBufferFields;
        private static MethodInfo drawPrecisionModeMethod, precisionResetMethod;

        // Lookup only (nothing patched yet): every member precision mode needs.
        private static void PlanPrecisionMode()
        {
            var tab = precisionTabType = TypeNamed("Core40k.DecorationBaseTab");
            drawDataType = TypeNamed("Core40k.DecorationDrawData");
            var rotational = drawDataType.GetNestedType("RotationalData", AccessTools.all) ?? throw new TypeLoadException("DecorationDrawData.RotationalData");
            selectedCompField = Field(tab, "selectedPrecisionComp");
            selectedDefField = Field(tab, "selectedPrecisionDef");
            compDrawDatasField = Field(compDecorativeBaseType, "drawDatas");
            compOriginalDrawDatasField = Field(compDecorativeBaseType, "originalDrawDatas");
            drawDataCopyFromMethod = AccessTools.DeclaredMethod(drawDataType, "CopyFrom", new[] { drawDataType }) ?? throw new MissingMethodException("DecorationDrawData.CopyFrom");
            rotationFields = new[] { "defaultData", "dataNorth", "dataEast", "dataSouth", "dataWest" }.Select(n => Field(drawDataType, n)).ToArray();
            rotationalRotation = Field(rotational, "rotation");
            rotationalOffset = Field(rotational, "offset");
            rotationalLayer = Field(rotational, "layer");
            rotationalScale = Field(rotational, "scale");
            setDrawDataMethod = Declared(compDecorativeBaseType, "SetDrawData", decorationDefType, drawDataType) ?? throw new MissingMethodException("SetDrawData");
            defaultDrawDataDto = ToDto(Activator.CreateInstance(drawDataType));
            drawPrecisionModeMethod = AccessTools.DeclaredMethod(tab, "DrawPrecisionMode") ?? throw new MissingMethodException("DecorationBaseTab.DrawPrecisionMode");
            precisionResetMethod = AccessTools.DeclaredMethod(tab, "PrecisionReset") ?? throw new MissingMethodException("DecorationBaseTab.PrecisionReset");
            precisionBufferFields = new[] { "xStringBuffers", "zStringBuffers", "drawSizeStringBuffers", "layerStringBuffers" }.Select(n => Field(tab, n)).ToArray();
            foreach (var dialog in dialogTypes)
                if (AccessTools.DeclaredMethod(dialog, "DoWindowContents") == null)
                    throw new MissingMethodException(dialog.FullName + ".DoWindowContents");
        }

        private static void RegisterPrecisionMode()
        {
            Try("sync SyncApplyDrawData", () =>
            {
                var apply = AccessTools.DeclaredMethod(typeof(RimDark40kCustomization), nameof(SyncApplyDrawData));
                MP.RegisterSyncMethod(apply).SetPostInvoke((target, args) => AfterCommand(apply, target, args));
                syncMethods++;
            });
            Try("sync SyncResetDrawDataRot", () =>
            {
                var reset = AccessTools.DeclaredMethod(typeof(RimDark40kCustomization), nameof(SyncResetDrawDataRot));
                MP.RegisterSyncMethod(reset).SetPostInvoke((target, args) => AfterCommand(reset, target, args));
                syncMethods++;
            });
            Try("patch CompDecorativeBase.ResetDrawData(def, rot)", () =>
                Patch(resetDrawDataRotMethod, prefix: nameof(PreResetDrawDataRot)));
            Try("patch DecorationBaseTab.DrawPrecisionMode", () =>
                Patch(drawPrecisionModeMethod, prefix: nameof(PrecisionPrefix), finalizer: nameof(PrecisionFinalizer)));
            Try("patch DecorationBaseTab.PrecisionReset", () =>
                Patch(precisionResetMethod, prefix: nameof(PrecisionResetPrefix)));
            Try("patch CompDecorativeBase.SetOriginalDrawData (hold edits)", () =>
                Patch(Declared(compDecorativeBaseType, "SetOriginalDrawData", decorationDefType), prefix: nameof(PreSetOriginalDrawData),
                    prefixPriority: Priority.Normal));
            Try("patch DecorationBaseTab.Setup (stat cache)", () =>
            {
                decorativeCompsField = Field(precisionTabType, "decorativeComps");
                Patch(AccessTools.DeclaredMethod(precisionTabType, "Setup", new[] { typeof(Pawn) }), postfix: nameof(PostDecorationTabSetup));
            });
            // every frame the window is drawn (also on other tabs): send precision edits that have rested long enough
            foreach (var dialog in dialogTypes)
                Try("patch " + dialog.Name + ".DoWindowContents", () =>
                    Patch(AccessTools.DeclaredMethod(dialog, "DoWindowContents"), prefix: nameof(DialogFramePrefix)));
        }

        // DecorationDrawData <-> float[30]: per rotation (default, north, east, south, west) offset xyz, layer, scale, Rot4 byte.
        private static float[] ToDto(object drawData)
        {
            var data = new float[rotationFields.Length * 6];
            int i = 0;
            foreach (var field in rotationFields)
            {
                var rotational = field.GetValue(drawData);
                var offset = (Vector3)rotationalOffset.GetValue(rotational);
                data[i++] = offset.x;
                data[i++] = offset.y;
                data[i++] = offset.z;
                data[i++] = (float)rotationalLayer.GetValue(rotational);
                data[i++] = (float)rotationalScale.GetValue(rotational);
                data[i++] = ((Rot4)rotationalRotation.GetValue(rotational)).AsByte;
            }

            return data;
        }

        private static object FromDto(float[] data)
        {
            var drawData = Activator.CreateInstance(drawDataType);
            int i = 0;
            foreach (var field in rotationFields)
            {
                var rotational = field.GetValue(drawData);
                rotationalOffset.SetValue(rotational, new Vector3(data[i], data[i + 1], data[i + 2]));
                rotationalLayer.SetValue(rotational, data[i + 3]);
                rotationalScale.SetValue(rotational, data[i + 4]);
                // data[i + 5] (rotation) is NOT applied: the fresh object already has the right one per slot (defaultData = Rot4.Invalid,
                // which new Rot4(byte) would turn into North)
                i += 6;
            }

            return drawData;
        }

        private static object GetDrawData(ThingComp comp, Def def)
            => compDrawDatasField.GetValue(comp) is IDictionary dictionary && dictionary.Contains(def) ? dictionary[def] : null;

        // Before the tab draws: remember what the draw data looked like. DrawPrecisionMode edits it in place through ref parameters.
        private static void PrecisionPrefix(object __instance, ref PrecisionState __state)
        {
            __state = null;
            if (!active || !MP.IsInMultiplayer || !MP.InInterface)
                return;
            if (selectedCompField.GetValue(__instance) is not ThingComp comp || selectedDefField.GetValue(__instance) is not Def def)
                return;
            var current = GetDrawData(comp, def);
            // After ResetDrawData(def) / ResetDrawDatas the entry IS the original object; an in-place edit would then change this machine's
            // original too. Swap in a value-equal copy (local only, values unchanged, SetDrawData replaces the entry on every machine anyway).
            if (current != null && compOriginalDrawDatasField.GetValue(comp) is IDictionary originals && originals.Contains(def) &&
                ReferenceEquals(originals[def], current))
            {
                var copy = Activator.CreateInstance(drawDataType);
                drawDataCopyFromMethod.Invoke(copy, new[] { current });
                ((IDictionary)compDrawDatasField.GetValue(comp))[def] = copy;
                current = copy;
            }
            __state = new PrecisionState { comp = comp, def = def, existed = current != null, before = current != null ? ToDto(current) : null };
        }

        // After it drew (finalizer: also on an exception): did the user change a value this frame? Then the comp has a local-only
        // edit; it goes out once the sliders have rested for DrawDataIdleSeconds, or earlier when another command touches the comp.
        private static void PrecisionFinalizer(object __instance, PrecisionState __state)
        {
            if (__state == null)
                return;
            try
            {
                var current = GetDrawData(__state.comp, __state.def);
                var after = current != null ? ToDto(current) : null;
                bool changed = after != null && (!__state.existed || !after.SequenceEqual(__state.before));
                // F3: this machine's SetOriginalDrawData is still in flight; there the original is copied from drawDatas[def], so an
                // edit made now would become this machine's original only. Put the synced values back (in place, no notify, local
                // only) and do not mark it pending; the tab's text buffers re-apply it once the command has landed.
                if (changed && IsAwaitingOriginal(__state.comp, __state.def))
                {
                    if (__state.existed)
                        drawDataCopyFromMethod.Invoke(current, new[] { FromDto(__state.before) });
                    else
                        ((IDictionary)compDrawDatasField.GetValue(__state.comp)).Remove(__state.def);
                }
                else if (current != null && !__state.existed && after.SequenceEqual(defaultDrawDataDto))
                    ((IDictionary)compDrawDatasField.GetValue(__state.comp)).Remove(__state.def); // tab added a default entry: keep the saved structure equal
                else if (after != null && (!__state.existed || !after.SequenceEqual(__state.before)))
                    pendingDrawData[(__state.comp, __state.def)] = Time.realtimeSinceStartup;
                // the tab's own Accept button ended precision mode during this very call: PrecisionResetPrefix ran before this frame's edit
                // was known, so send it now
                if (selectedCompField.GetValue(__instance) == null)
                    FlushDrawData(__state.comp);
                FlushIdleDrawData();
            }
            catch (Exception e)
            {
                Log.Warning($"{LogPrefix}precision mode bookkeeping failed: {e.GetType().Name}: {e.Message}");
            }
        }

        private static void FlushIdleDrawData()
        {
            if (pendingDrawData.Count == 0)
                return;
            float now = Time.realtimeSinceStartup;
            foreach (var pair in pendingDrawData.Where(p => now - p.Value >= DrawDataIdleSeconds).ToList())
            {
                pendingDrawData.Remove(pair.Key);
                SendDrawData(pair.Key.comp, pair.Key.def);
            }
        }

        // Called before any other command on the comp goes out (GuardPrefix) so the commands arrive in the order the user acted.
        private static void FlushDrawData(ThingComp comp)
        {
            if (pendingDrawData.Count == 0)
                return;
            foreach (var key in pendingDrawData.Keys.Where(k => k.comp == comp).ToList())
            {
                pendingDrawData.Remove(key);
                SendDrawData(key.comp, key.def);
            }
        }

        private static void SendDrawData(ThingComp comp, Def def)
        {
            var current = GetDrawData(comp, def);
            if (current != null)
                SyncApplyDrawData(comp, def, ToDto(current));
        }

        // Both the "Accept" and the "Exit" button of precision mode end in PrecisionReset; Exit has already queued ResetDrawData.
        private static void PrecisionResetPrefix(object __instance)
        {
            try
            {
                if (active && MP.IsInMultiplayer && MP.InInterface && selectedCompField.GetValue(__instance) is ThingComp comp)
                    FlushDrawData(comp);
            }
            catch (Exception e)
            {
                Log.Warning($"{LogPrefix}could not send pending precision edits: {e.GetType().Name}: {e.Message}");
            }
        }

        // Prefix of both dialogs' DoWindowContents: the precision tab only runs its own idle flush while it is the visible tab.
        private static void DialogFramePrefix()
        {
            try
            {
                if (active && MP.IsInMultiplayer && MP.InInterface)
                    FlushIdleDrawData();
            }
            catch (Exception e)
            {
                Log.Warning($"{LogPrefix}could not send pending precision edits: {e.GetType().Name}: {e.Message}");
            }
        }

        // The precision "Reset" button clears the text buffers and re-seeds them from the draw data BEFORE its command has come back;
        // the text field is the authority every frame, so the stale text would be written over the reset values. Clear them again once
        // the reset has really happened.
        private static void ClearPrecisionBuffers()
        {
            var windows = Find.WindowStack?.Windows;
            if (windows == null)
                return;
            foreach (var window in windows)
            {
                if (window == null || !TryGetDialogPawn(window, out _))
                    continue;
                foreach (var type in dialogTypes)
                {
                    if (!type.IsInstanceOfType(window) || dialogTabDrawersFields[type]?.GetValue(window) is not IDictionary drawers)
                        continue;
                    foreach (DictionaryEntry entry in drawers)
                        if (precisionTabType.IsInstanceOfType(entry.Value))
                            foreach (var field in precisionBufferFields)
                                (field.GetValue(entry.Value) as IDictionary)?.Clear();
                }
            }
        }

        // UI call of ResetDrawData(def, Rot4) with the window open: send the wrapper instead (the original then runs inside the command).
        // Without the window (or module not active): the original runs locally, as without the module.
        private static bool PreResetDrawDataRot(ThingComp __instance, object[] __args)
        {
            if (!active || !MP.IsInMultiplayer || !MP.InInterface || !CustomizeDialogOpenFor(__instance))
                return true;
            if (__args[0] is not Def def || __args[1] is not Rot4 rot)
                return true;
            try
            {
                FlushDrawData(__instance);
            }
            catch (Exception e)
            {
                Log.Warning($"{LogPrefix}could not send pending precision edits: {e.GetType().Name}: {e.Message}");
            }

            SyncResetDrawDataRot(__instance, def, rot.IsValid ? rot.AsInt : -1);
            return false;
        }

        // The synced command: ResetDrawData(def, rotation) on every machine; rotation -1 = Rot4.Invalid (the default data).
        private static void SyncResetDrawDataRot(ThingComp comp, Def def, int rotation)
        {
            if (comp == null || def == null || rotation < -1 || rotation > 3)
                return;
            resetDrawDataRotMethod.Invoke(comp, new object[] { def, rotation < 0 ? Rot4.Invalid : new Rot4(rotation) });
        }

        // A loaded game (host load, join, resync) has new comp objects: drop the UI-side bookkeeping that points at the old ones.
        // F3 bookkeeping: (comp, def) whose SetOriginalDrawData this machine sent and that has not come back yet -> send time. A record
        // older than AwaitingOriginalSeconds is ignored (a lost command must not freeze the sliders for good).
        private const float AwaitingOriginalSeconds = 5f;
        private static readonly Dictionary<(ThingComp comp, Def def), float> awaitingOriginal = new();
        private static FieldInfo decorativeCompsField;

        private static bool IsAwaitingOriginal(ThingComp comp, Def def)
            => awaitingOriginal.TryGetValue((comp, def), out var sent) && Time.realtimeSinceStartup - sent < AwaitingOriginalSeconds;

        // UI call with the window open = the Precision button: the call is about to be sent (GuardPrefix lets MP send it).
        private static void PreSetOriginalDrawData(ThingComp __instance, object[] __args)
        {
            if (!active || !MP.IsInMultiplayer || !MP.InInterface || __args[0] is not Def def || !CustomizeDialogOpenFor(__instance))
                return;
            awaitingOriginal[(__instance, def)] = Time.realtimeSinceStartup;
        }

        // F10 (Core40k itself): Setup runs from the dialog constructor, i.e. in the simulation on every machine, and removes invalid
        // decorations without the comps dropping their cached stat offsets / factors. Drop them there, on every machine. A UI-time
        // Setup (none known) is left alone: a cache reset on one machine only would itself be a per-machine call.
        private static void PostDecorationTabSetup(object __instance)
        {
            if (!active || (MP.IsInMultiplayer && MP.InInterface))
                return;
            try
            {
                if (decorativeCompsField.GetValue(__instance) is IList comps)
                    foreach (var comp in comps.Cast<object>().ToList())
                        if (comp != null)
                            notifyGraphicChangedMethod.Invoke(comp, null);
            }
            catch (Exception e)
            {
                Log.Warning($"{LogPrefix}stat cache reset after the decoration tab setup failed: {e.GetType().Name}: {e.Message}");
            }
        }

        private static void PostGameFinalizeInit()
        {
            awaitingOriginal.Clear();
            pendingDrawData.Clear();
            shoulderColourSent.Clear();
            closing.Clear();
            orderingJob = null;
        }

        // The synced command: replaces the draw data of one decoration with a copy built from the float array, on every machine.
        private static void SyncApplyDrawData(ThingComp comp, Def def, float[] data)
        {
            if (comp == null || def == null || data == null || data.Length != rotationFields.Length * 6)
                return;
            setDrawDataMethod.Invoke(comp, new[] { def, FromDto(data) });
        }

        #endregion

        #region Genes40k (Mankind's Finest): shoulder-icon tab

        private static FieldInfo decorationSettingsColour;
        private static readonly Dictionary<(ThingComp comp, string side), (Color colour, float time)> shoulderColourSent = new();

        // CompChapterColorWithShoulderDecoration is a CompMultiColor whose tab (ShoulderPadIconTab) writes these setters. Its SetOriginals /
        // Reset overrides were already registered by the generic scan above. Missing mod = nothing to do.
        // Two shapes, told apart by what exists (never by a version string):
        // - Genes40k 1.1.14 (pairs with Core40k 1.2.32): property setters RightShoulderIcon / ...Colour / LeftShoulderIcon / ...Colour /
        //   FlipShoulderIcons. Synced as before.
        // - Genes40k 1.1.102 (pairs with Core40k 1.2.117): SetChapterIcon / SetRankIcon / SetSlotColour(bool, Color) / SwapShoulderIcons, which
        //   edit the decorations of the item's CompDecorative. Synced the same way (a prefix lets the call out only while the window is open).
        //   The comp's per-machine BeginEditing / EndEditing flag is set by the tab's Setup / OnClose, which run inside the simulation on every
        //   machine (the dialog is built there, and Close is a synced command, see the wait-flow region), so it needs no sync of its own.
        //   With the 1.2.117 flow the tab's call sites of the four methods are also redirected to NoInlining wrappers (they are small enough
        //   to be inlined past the patch); a call site that is not found fails the module.
        // - Neither shape found: with the 1.2.32 flow the Genes40k tab part stays OFF with a warning (its edits are then local, as without the
        //   module); the rest of the module keeps working. With the 1.2.117 flow it fails the module (the all-or-nothing rule).
        private static void RegisterShoulderIconTab()
        {
            var comp = AccessTools.TypeByName("Genes40k.CompChapterColorWithShoulderDecoration");
            if (comp == null)
            {
                Log.Message($"{LogPrefix}Genes40k shoulder-icon comp not loaded, its tab is not synced.");
                return;
            }

            var colour = typeof(Color);
            var oldNames = new[] { "RightShoulderIcon", "RightShoulderIconColour", "LeftShoulderIcon", "LeftShoulderIconColour", "FlipShoulderIcons" };
            var oldSetters = oldNames.Select(n => AccessTools.DeclaredPropertySetter(comp, n)).ToList();
            var shoulderIconType = AccessTools.TypeByName("Genes40k.ShoulderIconDef");
            var newMethods = new List<(string name, MethodInfo method)>();
            if (shoulderIconType != null)
            {
                newMethods.Add(("SetChapterIcon", Declared(comp, "SetChapterIcon", shoulderIconType)));
                newMethods.Add(("SetRankIcon", Declared(comp, "SetRankIcon", shoulderIconType)));
                newMethods.Add(("SetSlotColour", Declared(comp, "SetSlotColour", typeof(bool), colour)));
                newMethods.Add(("SwapShoulderIcons", Declared(comp, "SwapShoulderIcons")));
            }

            if (oldSetters.All(s => s != null))
            {
                decorationSettingsColour = Field(TypeNamed("Core40k.DecorationSettings"), "Color");
                for (int i = 0; i < oldNames.Length; i++)
                    PlanSync("CompChapterColorWithShoulderDecoration." + oldNames[i], oldSetters[i], false);
                RegisterPlans();
                foreach (var property in new[] { "RightShoulderIconColour", "LeftShoulderIconColour" })
                    Try("patch " + property, () => Patch(AccessTools.DeclaredPropertySetter(comp, property), prefix: nameof(SkipUnchangedShoulderColour)));
            }
            else if (newMethods.Count > 0 && newMethods.All(m => m.method != null))
            {
                // Core40k 1.2.117 flow only: the four methods are small and not virtual, so the JIT may inline them into the tab's draw code,
                // past the sync patch. Look up the call sites first (throws when one is missing), register, then redirect them.
                if (waitFlow)
                    PlanShoulderRedirects(newMethods);
                foreach (var (name, method) in newMethods)
                    PlanSync("CompChapterColorWithShoulderDecoration." + name, method, false);
                RegisterPlans();
                if (waitFlow)
                    PatchShoulderRedirects();
            }
            else
            {
                var unknown = $"the Genes40k shoulder-icon comp has neither the setters nor the methods this module knows " +
                              $"(missing: {string.Join(", ", oldNames.Zip(oldSetters, (n, s) => s == null ? n : null).Where(n => n != null))} / " +
                              $"{string.Join(", ", newMethods.Where(m => m.method == null).Select(m => m.name))}). ";
                // The all-or-nothing rule: with the 1.2.117 flow an unsynced tab would write on one machine only, so the module stays off.
                if (waitFlow)
                    throw new MissingMemberException(unknown + "Its shoulder-icon tab would not be synced.");
                Log.Warning($"{LogPrefix}{unknown}Its shoulder-icon tab is NOT synced: edits made there stay local to one machine.");
            }
        }

        // The Genes40k 1.1.102 methods the shoulder tab calls (Core40k 1.2.117 flow), redirected at their call sites in the tab (its draw
        // methods and the lambdas of those) to the wrappers below. Same technique as the TryAccept calls: a call site that is changed cannot
        // be inlined past the sync patch, and the wrapper reaches the original through reflection.
        private static readonly Dictionary<string, MethodInfo> shoulderMethods = new();
        private static readonly List<(MethodInfo callee, MethodInfo redirect)> shoulderRedirects = new();
        private static readonly List<MethodBase> shoulderCallers = new();
        private static readonly Dictionary<MethodInfo, int> shoulderSites = new();
        private static readonly Dictionary<MethodInfo, int> shoulderHits = new();

        private static (MethodInfo callee, MethodInfo redirect) ShoulderRedirectFor(MethodBase operand)
        {
            if (operand == null)
                return default;
            foreach (var pair in shoulderRedirects)
                if (operand == pair.callee || (operand.MetadataToken == pair.callee.MetadataToken && operand.Module == pair.callee.Module))
                    return pair;
            return default;
        }

        // Lookup only (nothing patched yet). A call site that is not found is a failure of the feature (the caller's Try counts it).
        private static void PlanShoulderRedirects(List<(string name, MethodInfo method)> methods)
        {
            shoulderMethods.Clear();
            shoulderRedirects.Clear();
            shoulderCallers.Clear();
            shoulderSites.Clear();
            shoulderHits.Clear();
            foreach (var (name, method) in methods)
            {
                shoulderMethods[name] = method;
                shoulderRedirects.Add((method, AccessTools.DeclaredMethod(typeof(RimDark40kCustomization), "Redirect" + name)
                                               ?? throw new MissingMethodException(typeof(RimDark40kCustomization).FullName, "Redirect" + name)));
            }

            var tabType = TypeNamed("Genes40k.ShoulderPadIconTab");
            foreach (var type in new[] { tabType }.Concat(tabType.GetNestedTypes(AccessTools.all)))
                foreach (var method in AccessTools.GetDeclaredMethods(type))
                {
                    if (method.IsAbstract || method.GetMethodBody() == null)
                        continue;
                    try
                    {
                        foreach (var instruction in PatchProcessor.GetOriginalInstructions(method))
                        {
                            if (instruction.opcode != OpCodes.Call && instruction.opcode != OpCodes.Callvirt)
                                continue;
                            var callee = ShoulderRedirectFor(instruction.operand as MethodBase).callee;
                            if (callee == null)
                                continue;
                            if (shoulderCallers.All(m => m != method))
                                shoulderCallers.Add(method);
                            shoulderSites[callee] = (shoulderSites.TryGetValue(callee, out var sites) ? sites : 0) + 1;
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Warning($"{LogPrefix}could not read the code of {type.Name}.{method.Name}: {e.GetType().Name}: {e.Message}");
                    }
                }

            var missing = shoulderRedirects.Where(r => !shoulderSites.ContainsKey(r.callee)).Select(r => r.callee.Name).ToList();
            if (missing.Count > 0)
            {
                Log.Error($"{LogPrefix}no call site of {string.Join(", ", missing)} found in {tabType.Name} or its lambdas.");
                throw new MissingMethodException($"no call site of {string.Join(", ", missing)} found in {tabType.Name} or its lambdas");
            }
        }

        private static void PatchShoulderRedirects()
        {
            foreach (var caller in shoulderCallers)
                Patch(caller, transpiler: nameof(RedirectShoulderCalls));
            foreach (var (callee, _) in shoulderRedirects)
                if (!shoulderHits.TryGetValue(callee, out var hits) || hits < shoulderSites[callee])
                {
                    Log.Error($"{LogPrefix}the transpilers changed {hits} of {shoulderSites[callee]} call(s) of {callee.Name}.");
                    throw new MissingMethodException($"the transpilers did not change every call of {callee.Name}");
                }

            Log.Message($"{LogPrefix}Genes40k shoulder tab: {shoulderSites.Values.Sum()} call site(s) in {shoulderCallers.Count} method(s) redirected to NoInlining wrappers.");
        }

        // Harmony transpiler on the shoulder tab's draw methods and their lambdas: same stack, only the callee differs (the object receiver
        // and the arguments are passed on as they are).
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static IEnumerable<CodeInstruction> RedirectShoulderCalls(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var instruction in instructions)
            {
                if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && instruction.operand is MethodBase operand)
                {
                    var (original, redirect) = ShoulderRedirectFor(operand);
                    if (redirect != null)
                    {
                        shoulderHits[original] = (shoulderHits.TryGetValue(original, out var hits) ? hits : 0) + 1;
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = redirect;
                    }
                }

                yield return instruction;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RedirectSetChapterIcon(object comp, object def)
            => InvokeOn(shoulderMethods["SetChapterIcon"], comp, def);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RedirectSetRankIcon(object comp, object def)
            => InvokeOn(shoulderMethods["SetRankIcon"], comp, def);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RedirectSetSlotColour(object comp, bool chapterSlot, Color colour)
            => InvokeOn(shoulderMethods["SetSlotColour"], comp, chapterSlot, colour);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RedirectSwapShoulderIcons(object comp)
            => InvokeOn(shoulderMethods["SwapShoulderIcons"], comp);

        // ShoulderPadIconTab.DrawTab sets Left/RightShoulderIconColour = white EVERY FRAME while the chosen icon takes no colour. With the
        // window open: skip the call when the saved colour already is that value, or when the same value went out in the last second and
        // its command has not come back yet (one command instead of one per frame). Not skipped when the settings object is still null,
        // because the setter creates it.
        private static bool SkipUnchangedShoulderColour(ThingComp __instance, MethodBase __originalMethod, Color value)
        {
            if (!active || !MP.IsInMultiplayer || !MP.InInterface || !CustomizeDialogOpenFor(__instance))
                return true;
            string side = __originalMethod.Name.Contains("Left") ? "leftShoulder" : "rightShoulder";
            var settings = AccessTools.Field(__instance.GetType(), side)?.GetValue(__instance);
            if (settings != null && (Color)decorationSettingsColour.GetValue(settings) == value)
                return false;

            float now = Time.realtimeSinceStartup;
            if (shoulderColourSent.TryGetValue((__instance, side), out var sent) && sent.colour == value && now - sent.time < 1f)
                return false;
            shoulderColourSent[(__instance, side)] = (value, now);
            return true;
        }

        #endregion
    }
}
