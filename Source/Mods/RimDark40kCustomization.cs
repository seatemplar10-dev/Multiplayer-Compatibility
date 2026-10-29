using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
    /// Supported build: Core40k 1.2.32 (the 1.6 folder, 253,952 bytes, what the lab and the frozen co-op copy load). The Workshop 1.7 build
    /// (1.2.112) has other signatures (AddOrRemoveDecoration(def, bool free), LoadFromPreset(preset, Pawn, List skipped), ...) and another job
    /// driver: every member is looked up before anything is patched and on a mismatch the module patches NOTHING (the window then opens on
    /// every machine as without it) - a window gate with unsynced edits, or a gate on a driver that waits for the window, would be worse.
    /// 29 Oct (FIXES rows 174 + 195, branch waylith-1.7-styling-fix, independent review F1-F8): (a) the module is all-or-nothing at RUN
    /// time too: <c>active</c> is set only when every lookup, registration and patch worked (Genes40k shoulder tab included, before the
    /// gate); while it is false the window is never blocked and every UI-time call stays local, as without the module. (b) The order
    /// command's own Job object is remembered in a prefix, so the "pawn already at the station" case matches only that job.
    /// (c) ResetDrawData(def, Rot4) goes through a wrapper with an int rotation (MP's Rot4 serializer turns Rot4.Invalid into North).
    /// (d) After SetAlternateBaseForm / LoadFromPreset every CompGraphicParent of the item drops its stat cache on every machine.
    /// (e) Precision mode: an entry that is the same object as its original gets a local value-equal copy before it is edited in place.
    /// (f) Pending UI bookkeeping is cleared on game load.
    /// Status: [unverified] compiled only, not run in game.
    /// </remarks>
    [MpCompatFor("phonicmas.rimdark.frameworkcore")]
    internal class RimDark40kCustomization
    {
        private const string LogPrefix = "[MPCompat - RimDark 40k Customization] ";
        private const bool LogCommands = true;
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
            if (failures > 0)
            {
                Log.Error($"{LogPrefix}this Core40k build does not have what the module syncs ({failures} problem(s) above): nothing patched, the customization window behaves as without the module.");
                throw new InvalidOperationException($"{LogPrefix}unsupported Core40k build");
            }

            Try("register the comp mutators", RegisterPlans);
            Try("register draw data (precision mode)", RegisterPrecisionMode);
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

        private static void Patch(MethodBase target, string prefix = null, string postfix = null, string finalizer = null, int prefixPriority = Priority.First)
        {
            if (target == null)
                throw new MissingMethodException("patch target not found");
            MpCompat.harmony.Patch(target, prefix: Hm(prefix, prefixPriority), postfix: Hm(postfix), finalizer: Hm(finalizer));
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
                    prefix: nameof(PreTryTakeOrderedJob), postfix: nameof(PostTryTakeOrderedJob), prefixPriority: Priority.Normal));
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

        private static void PostTryTakeOrderedJob(Pawn_JobTracker __instance, Job job, bool __result)
        {
            orderingJob = null;
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

        private static MethodInfo Declared(Type type, string name, params Type[] parameters)
            => AccessTools.DeclaredMethod(type, name, parameters.Length == 0 ? null : parameters);

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
            PlanSync("AddOrRemoveDecoration", Declared(deco, "AddOrRemoveDecoration", decorationDefType), true);
            PlanSync("RemoveAllDecorations", Declared(deco, "RemoveAllDecorations"), true);
            PlanSync("LoadFromPreset(DecorationPreset)", Declared(deco, "LoadFromPreset", decorationPresetType), true, exposeParameter: 0);
            PlanSync("LoadFromPreset(DecorationPresetDef)", Declared(deco, "LoadFromPreset", decorationPresetDefType), false);
            PlanSync("SetDecorationColourOne", Declared(deco, "SetDecorationColourOne", decorationDefType, colour), true);
            PlanSync("SetDecorationColourTwo", Declared(deco, "SetDecorationColourTwo", decorationDefType, colour), true);
            PlanSync("SetDecorationColourThree", Declared(deco, "SetDecorationColourThree", decorationDefType, colour), true);
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
                if (current != null && !__state.existed && after.SequenceEqual(defaultDrawDataDto))
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
        private static void PostGameFinalizeInit()
        {
            pendingDrawData.Clear();
            shoulderColourSent.Clear();
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
        private static void RegisterShoulderIconTab()
        {
            var comp = AccessTools.TypeByName("Genes40k.CompChapterColorWithShoulderDecoration");
            if (comp == null)
            {
                Log.Message($"{LogPrefix}Genes40k shoulder-icon comp not loaded, its tab is not synced.");
                return;
            }

            decorationSettingsColour = Field(TypeNamed("Core40k.DecorationSettings"), "Color");
            foreach (var property in new[] { "RightShoulderIcon", "RightShoulderIconColour", "LeftShoulderIcon", "LeftShoulderIconColour", "FlipShoulderIcons" })
                PlanSync("CompChapterColorWithShoulderDecoration." + property, AccessTools.DeclaredPropertySetter(comp, property), false);
            RegisterPlans();
            foreach (var property in new[] { "RightShoulderIconColour", "LeftShoulderIconColour" })
                Try("patch " + property, () => Patch(AccessTools.DeclaredPropertySetter(comp, property), prefix: nameof(SkipUnchangedShoulderColour)));
        }

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
