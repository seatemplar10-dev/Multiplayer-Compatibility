using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using UnityEngine;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Melee Animation by Epicguru</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=2944488802"/>
    /// <see href="https://github.com/Epicguru/AnimationMod"/>
    /// <remarks>
    /// Written against zAnimationMod.dll BuildDate 20260413080237, commit c9d1525 (the 1.6 build).
    /// Why the mod desyncs as shipped:
    /// 1. Every animation (executions, duels, lasso pulls, skills, idle/attack anims) is advanced in
    ///    AnimationManager.MapComponentUpdate by Time.deltaTime, i.e. per FRAME. Animation events
    ///    (KillPawn, DamageEffect, GoreSplash, PuntPawn, DuelEvent, Mote...) and the end of an animation
    ///    (teleport, job end, duel winner execution) therefore run at a client-dependent tick.
    ///    Fix: in MP the clock moves to AnimationManager.MapComponentTick (1/60 s per tick); frames only draw.
    /// 2. MapPawnProcessor (auto execute/lasso scan) runs on worker threads (Task.Run, slice count from
    ///    the CPU count) and calls Verse.Rand there. Fix: single slice in MP.
    /// 3. IdleControllerComp.PreDraw restarts idle animations from the render path and its attack
    ///    "hit pause" reads render matrices. Fix: no restart from PreDraw, no hit pause in MP.
    /// 4. Camera/current-map dependent Rand use (MoteWorker, KnockbackFlyer.Tick, Gilgamesh visuals) and a
    ///    knockback flight time computed from the tweened draw position. Fix: push/pop Rand, cell based start.
    /// 5. Player actions (float menu execute/lasso/skills, Assign tab auto options, duel spot gizmos) run
    ///    locally, and PawnMeleeData is created lazily from UI code. Fix: synced commands, UI never creates data.
    /// 6. Mod settings change simulation (chances, MTBs, gore, scan interval). Fix: settings window locked in MP.
    /// </remarks>
    [MpCompatFor("co.uk.epicguru.meleeanimation")]
    public class MeleeAnimation
    {
        private const float SecondsPerTick = 1f / 60f;
        private const string LogPrefix = "[MPCompat - Melee Animation] ";

        // Types
        private static Type animRendererType;
        private static Type pawnMeleeDataType;
        private static Type idleControllerCompType;
        private static Type executionRequestType;
        private static Type executionReportType;
        private static Type grappleRequestType;
        private static Type animDefType;

        // Clock (tick driven animation time)
        private static IAnimClock clock;
        private static Action addFromPostLoad;

        // AnimationManager (frame side)
        private static MethodInfo renderHeadsMethod;
        private static MethodInfo drawLabelMethod;
        private static FieldInfo labelsField;
        private static FieldInfo cullingPaddingField;
        private static AccessTools.FieldRef<object, List<Action>> toDrawField;
        private static readonly ConditionalWeakTable<object, Action<Pawn, Vector2>> labelDelegates = new();

        // Settings
        private static FieldInfo coreSettingsField;
        private static AccessTools.FieldRef<object, bool> drawNamesInAnimationField;
        private static AccessTools.FieldRef<object, bool> warnOfFriendlyExecutionField;

        // Idle controller
        [ThreadStatic] private static int preDrawDepth;
        [ThreadStatic] private static bool skippedIdleRestart;

        // Knockback
        [ThreadStatic] private static IntVec3? pendingKnockbackStart;
        private static AccessTools.FieldRef<object, Vector3> knockbackStartPosField;

        // Melee data
        private static FieldInfo gameCompCurrentField;
        private static FieldInfo pawnMeleeDataDictField;
        private static MethodInfo getOrCreateDataMethod;
        private static AccessTools.FieldRef<object, Pawn> meleeDataPawnField;
        private static FieldInfo timeSinceGrappledField;
        private static ISyncField autoExecuteSyncField;
        private static ISyncField autoGrappleSyncField;
        private static readonly ConditionalWeakTable<Pawn, object> detachedMeleeData = new();

        // Float menu actions
        private static FieldInfo uiControllerField;
        private static MethodInfo executionEnabledOnClickMethod;
        private static MethodInfo getExecutionReportMethod;
        private static MethodInfo getGrappleReportMethod;
        private static MethodInfo makeOccupiedMaskMethod;
        private static MethodInfo getBitMethod;
        private static MethodInfo tryGetLassoMethod;
        private static MethodInfo giveGrappleJobMethod;
        private static FieldInfo grappleReportCanGrapple;
        private static FieldInfo grappleReportDestination;
        private static FieldInfo grappleReportError;
        private static object grappleSpotPreferAdjacent;
        private static object grappleSpotClosest;
        private static FieldInfo lassoClosureGrappler;
        private static FieldInfo lassoClosureTarget;

        // Skills
        private static MethodInfo getSkillsMethod;
        private static MethodInfo skillIsEnabledMethod;
        private static MethodInfo skillCanTriggerOnMethod;
        private static MethodInfo skillTryTriggerMethod;
        private static FieldInfo skillDefField;
        private static FieldInfo skillDefAnimationField;
        private static FieldInfo skillDefTypeField;

        public MeleeAnimation(ModContentPack mod) => LongEventHandler.ExecuteWhenFinished(LatePatch);

        private static void LatePatch()
        {
            // Order matters: the frame clock is only disabled once the tick clock exists.
            if (!TryRun("tick-driven animation clock", PatchAnimationClock))
            {
                Log.Error(LogPrefix + "Animation clock could not be set up; animations stay frame driven and WILL desync in co-op.");
                return;
            }

            var sections = new (string what, Action run)[]
            {
                ("single threaded pawn processor", PatchPawnProcessor),
                ("idle controller", PatchIdleController),
                ("visual RNG isolation", PatchVisualRng),
                ("knockback flyer", PatchKnockback),
                ("melee data", PatchMeleeData),
                ("float menu actions", PatchFloatMenu),
                ("duel spot", PatchDuelSpot),
                ("settings lock", PatchSettings),
            };
            var failed = 0;
            foreach (var (what, run) in sections)
            {
                if (!TryRun(what, run))
                    failed++;
            }

            // Startup evidence that LatePatch ran to the end. MpCompatLoader's "Initialized compatibility for" line is
            // written before ExecuteWhenFinished runs this, so it does not prove the patches went in.
            if (failed == 0)
                Log.Message(LogPrefix + $"all {sections.Length + 1} sections patched");
            else
                Log.Warning(LogPrefix + $"{failed} of {sections.Length + 1} sections failed to patch (errors above); co-op may desync.");
        }

        private static bool TryRun(string what, Action action)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception e)
            {
                Log.Error(LogPrefix + $"Failed to patch {what}: {e}");
                return false;
            }
        }

        #region Reflection helpers

        private static Type FindType(string name)
            => AccessTools.TypeByName(name) ?? throw new TypeLoadException($"Type {name} not found");

        private static FieldInfo GetField(Type type, string name)
            => AccessTools.Field(type, name) ?? throw new MissingFieldException(type.FullName, name);

        private static MethodInfo GetMethod(Type type, string name)
            => AccessTools.Method(type, name) ?? throw new MissingMethodException(type.FullName, name);

        private static MethodInfo GetDeclaredMethod(Type type, string name)
            => AccessTools.DeclaredMethod(type, name) ?? throw new MissingMethodException(type.FullName, name);

        private static HarmonyMethod Hm(string name) => new(typeof(MeleeAnimation), name);

        private static object Settings => coreSettingsField.GetValue(null);

        #endregion

        #region 1. Tick-driven animation clock

        private static void PatchAnimationClock()
        {
            animRendererType = FindType("AM.AnimRenderer");
            var managerType = FindType("AM.AnimationManager");
            var eventType = FindType("AM.Events.EventBase");
            var eventHelperType = FindType("AM.Events.EventHelper");
            var coreType = FindType("AM.Core");
            var settingsType = FindType("AM.AMSettings.Settings");

            var clockType = typeof(AnimClock<,>).MakeGenericType(animRendererType, eventType);
            clock = (IAnimClock)Activator.CreateInstance(clockType,
                AccessTools.PropertyGetter(animRendererType, "ActiveRenderers") ?? throw new MissingMethodException(animRendererType.FullName, "get_ActiveRenderers"),
                GetField(animRendererType, "Map"),
                AccessTools.PropertyGetter(animRendererType, "IsDestroyed") ?? throw new MissingMethodException(animRendererType.FullName, "get_IsDestroyed"),
                GetMethod(animRendererType, "Seek"),
                GetMethod(eventHelperType, "Handle"),
                GetMethod(animRendererType, "DrawAll"),
                GetMethod(animRendererType, "RemoveDestroyed"));

            addFromPostLoad = AccessTools.MethodDelegate<Action>(GetMethod(animRendererType, "AddFromPostLoad"));

            renderHeadsMethod = GetMethod(managerType, "RenderHeads");
            drawLabelMethod = GetMethod(managerType, "DrawLabel");
            labelsField = GetField(managerType, "labels");
            cullingPaddingField = GetField(managerType, "CullingPadding");
            toDrawField = AccessTools.FieldRefAccess<List<Action>>(managerType, "toDraw");

            coreSettingsField = GetField(coreType, "Settings");
            drawNamesInAnimationField = AccessTools.FieldRefAccess<bool>(settingsType, "DrawNamesInAnimation");
            warnOfFriendlyExecutionField = AccessTools.FieldRefAccess<bool>(settingsType, "WarnOfFriendlyExecution");

            // All three or none (every lookup above and in the argument list runs before the first patch): a tick
            // postfix left behind without the frame-clock prefix would advance animations on BOTH clocks in co-op.
            PatchAllOrNone(
                (GetDeclaredMethod(managerType, nameof(MapComponent.MapComponentTick)), null, Hm(nameof(PostMapComponentTick))),
                // Loaded animations register on the first frame OR tick, whichever comes first on that client.
                // Register them while loading instead, identically on every client.
                (GetDeclaredMethod(typeof(Game), nameof(Game.FinalizeInit)), null, Hm(nameof(PostGameFinalizeInit))),
                // Last: only stop the frame clock once the tick clock is in place.
                (GetDeclaredMethod(managerType, nameof(MapComponent.MapComponentUpdate)), Hm(nameof(PreMapComponentUpdate)), null));
        }

        // Applies every patch or none: when a Patch call throws, the patches this call already applied are removed
        // again (newest first) before the exception goes on to TryRun.
        private static void PatchAllOrNone(params (MethodBase original, HarmonyMethod prefix, HarmonyMethod postfix)[] patches)
        {
            var applied = new List<(MethodBase original, MethodInfo patch)>();
            try
            {
                foreach (var (original, prefix, postfix) in patches)
                {
                    MpCompat.harmony.Patch(original, prefix: prefix, postfix: postfix);
                    if (prefix != null)
                        applied.Add((original, prefix.method));
                    if (postfix != null)
                        applied.Add((original, postfix.method));
                }
            }
            catch
            {
                for (var i = applied.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        MpCompat.harmony.Unpatch(applied[i].original, applied[i].patch);
                    }
                    catch (Exception e)
                    {
                        Log.Error(LogPrefix + $"Could not remove {applied[i].patch.Name} from {applied[i].original.DeclaringType?.Name}.{applied[i].original.Name}: {e}");
                    }
                }

                throw;
            }
        }

        // Replaces AnimationManager.MapComponentUpdate in MP: draw only, never advance time, fire events or end animations.
        private static bool PreMapComponentUpdate(MapComponent __instance)
        {
            if (!MP.IsInMultiplayer)
                return true;

            renderHeadsMethod.Invoke(__instance, null);

            ((IList)labelsField.GetValue(__instance)).Clear();
            var padding = (int)cullingPaddingField.GetValue(null);
            var viewBounds = Find.CameraDriver.CurrentViewRect.ExpandedBy(padding);
            var labelDraw = drawNamesInAnimationField(Settings) ? GetLabelDelegate(__instance) : null;
            clock.DrawMap(__instance.map, viewBounds, labelDraw);

            var toDraw = toDrawField(__instance);
            foreach (var action in toDraw)
                action?.Invoke();
            toDraw.Clear();

            return false;
        }

        private static Action<Pawn, Vector2> GetLabelDelegate(object manager)
            => labelDelegates.GetValue(manager, m => (Action<Pawn, Vector2>)Delegate.CreateDelegate(typeof(Action<Pawn, Vector2>), m, drawLabelMethod));

        // Advances every animation on this map by one tick and runs its events and endings inside the tick.
        private static void PostMapComponentTick(MapComponent __instance)
        {
            if (!MP.IsInMultiplayer)
                return;

            addFromPostLoad();
            clock.TickMap(__instance.map);
        }

        private static void PostGameFinalizeInit()
        {
            if (MP.IsInMultiplayer)
                addFromPostLoad();
        }

        private interface IAnimClock
        {
            void TickMap(Map map);
            void DrawMap(Map map, CellRect viewBounds, Action<Pawn, Vector2> labelDraw);
        }

        private sealed class AnimClock<TRenderer, TEvent> : IAnimClock where TRenderer : class where TEvent : class
        {
            private delegate void SeekFn(TRenderer renderer, float? atTime, float dt, Action<TEvent> eventsOutput, bool delayedDestroy);

            private readonly Func<IReadOnlyList<TRenderer>> activeRenderers;
            private readonly AccessTools.FieldRef<TRenderer, Map> map;
            private readonly Func<TRenderer, bool> isDestroyed;
            private readonly SeekFn seek;
            private readonly Action<TEvent, TRenderer> handleEvent;
            private readonly MethodInfo drawAll; // has an 'in CellRect' parameter: called through reflection, once per map per frame
            private readonly Action removeDestroyed;
            private readonly Action<TEvent> eventOutput;
            private readonly List<TRenderer> buffer = new();
            private TRenderer current;

            public AnimClock(MethodInfo activeRenderersGetter, FieldInfo mapField, MethodInfo isDestroyedGetter,
                MethodInfo seekMethod, MethodInfo handleMethod, MethodInfo drawAllMethod, MethodInfo removeDestroyedMethod)
            {
                activeRenderers = AccessTools.MethodDelegate<Func<IReadOnlyList<TRenderer>>>(activeRenderersGetter);
                map = AccessTools.FieldRefAccess<TRenderer, Map>(mapField);
                isDestroyed = AccessTools.MethodDelegate<Func<TRenderer, bool>>(isDestroyedGetter);
                seek = AccessTools.MethodDelegate<SeekFn>(seekMethod);
                handleEvent = AccessTools.MethodDelegate<Action<TEvent, TRenderer>>(handleMethod);
                drawAll = drawAllMethod;
                removeDestroyed = AccessTools.MethodDelegate<Action>(removeDestroyedMethod);
                eventOutput = OnEvent;
            }

            public void TickMap(Map tickedMap)
            {
                // Snapshot first: events and animation endings start new animations (duel winner, job toils).
                var list = activeRenderers();
                buffer.Clear();
                for (var i = 0; i < list.Count; i++)
                {
                    var renderer = list[i];
                    if (renderer != null && map(renderer) == tickedMap && !isDestroyed(renderer))
                        buffer.Add(renderer);
                }

                for (var i = 0; i < buffer.Count; i++)
                {
                    var renderer = buffer[i];
                    if (isDestroyed(renderer))
                        continue;

                    var previous = current;
                    current = renderer;
                    try
                    {
                        seek(renderer, null, SecondsPerTick, eventOutput, false);
                    }
                    catch (Exception e)
                    {
                        Log.Error(LogPrefix + $"Exception advancing animation {renderer}: {e}");
                    }
                    finally
                    {
                        current = previous;
                    }
                }

                buffer.Clear();
                // Same as the end of AnimationManager.Draw, but inside the tick: OnEnd teleports pawns and ends jobs.
                removeDestroyed();
            }

            private void OnEvent(TEvent e)
            {
                try
                {
                    handleEvent(e, current);
                }
                catch (Exception ex)
                {
                    Log.Error(LogPrefix + $"Exception handling animation event {e} for {current}: {ex}");
                }
            }

            // onEvent null = AnimRenderer.Draw skips Seek: no time change, no events, no destroy at the end.
            public void DrawMap(Map drawnMap, CellRect viewBounds, Action<Pawn, Vector2> labelDraw)
                => drawAll.Invoke(null, new object[] { 0f, drawnMap, null, viewBounds, labelDraw });
        }

        #endregion

        #region 2. Pawn processor threads

        private static void PatchPawnProcessor()
        {
            var type = FindType("AM.Processing.MapPawnProcessor");
            MpCompat.harmony.Patch(GetDeclaredMethod(type, "MakeProcessingSlices"),
                prefix: Hm(nameof(PreMakeProcessingSlices)));
        }

        // One slice = MapPawnProcessor.Tick processes attackers on the main thread, in list order.
        private static bool PreMakeProcessingSlices(int todo, ref IEnumerable<IntRange> __result)
        {
            if (!MP.IsInMultiplayer)
                return true;

            __result = todo <= 0 ? Array.Empty<IntRange>() : new[] { new IntRange(0, todo - 1) };
            return false;
        }

        #endregion

        #region 3. Idle controller

        private static void PatchIdleController()
        {
            idleControllerCompType = FindType("AM.Idle.IdleControllerComp");
            MpCompat.harmony.Patch(GetDeclaredMethod(idleControllerCompType, "PreDraw"),
                prefix: Hm(nameof(PrePreDraw)),
                postfix: Hm(nameof(PostPreDraw)),
                finalizer: Hm(nameof(FinalizePreDraw)));
            MpCompat.harmony.Patch(GetDeclaredMethod(idleControllerCompType, "TickActive"),
                prefix: Hm(nameof(PreTickActive)));
            MpCompat.harmony.Patch(GetDeclaredMethod(idleControllerCompType, "UpdateAttackAnimation"),
                prefix: Hm(nameof(PreUpdateAttackAnimation)));
        }

        private static void PrePreDraw()
        {
            preDrawDepth++;
            skippedIdleRestart = false;
        }

        // The idle animation ended and its restart was skipped: let vanilla draw the weapon this frame
        // (PreDraw's result is "wants vanilla draw" for Patch_PawnRenderer_DrawEquipment).
        private static void PostPreDraw(ref bool __result)
        {
            if (!skippedIdleRestart)
                return;
            skippedIdleRestart = false;
            __result = true;
        }

        private static Exception FinalizePreDraw(Exception __exception)
        {
            preDrawDepth--;
            return __exception;
        }

        // PreDraw calls TickActive when the idle animation ended: that starts animations and rolls Rand from the
        // render path, on the client that happens to draw the pawn. CompTick restarts it on the next tick anyway.
        private static bool PreTickActive()
        {
            if (preDrawDepth <= 0 || !MP.IsInMultiplayer)
                return true;
            skippedIdleRestart = true;
            return false;
        }

        // The attack "hit pause" compares the weapon angle in RootTransform, which PreDraw fills from render
        // matrices. Its outcome changes the animation length and with it the idle state machine.
        private static bool PreUpdateAttackAnimation() => !MP.IsInMultiplayer;

        #endregion

        #region 4. Visual RNG isolation

        private static void PatchVisualRng()
        {
            // MoteWorker loops "while ShouldSpawnMotesAt" (false when the map is not the viewed one) rolling Rand.
            // KnockbackFlyer.Tick: same check around its dust Rand. Gilgamesh: random weapon visuals, may run at load.
            var methods = new[]
            {
                GetDeclaredMethod(FindType("AM.Events.Workers.MoteWorker"), "Run"),
                GetDeclaredMethod(FindType("AM.Grappling.KnockbackFlyer"), "Tick"),
                GetDeclaredMethod(FindType("AM.RendererWorkers.GilgameshRendererWorker"), "SetupRenderer"),
            };
            foreach (var method in methods)
                MpCompat.harmony.Patch(method, prefix: Hm(nameof(PrePushRand)), finalizer: Hm(nameof(FinalizePopRand)));
        }

        private static void PrePushRand(ref bool __state)
        {
            if (!MP.IsInMultiplayer)
                return;
            Rand.PushState();
            __state = true;
        }

        private static Exception FinalizePopRand(Exception __exception, bool __state)
        {
            if (__state)
                Rand.PopState();
            return __exception;
        }

        #endregion

        #region 4b. Knockback flight time

        private static void PatchKnockback()
        {
            var type = FindType("AM.Grappling.KnockbackFlyer");
            knockbackStartPosField = AccessTools.FieldRefAccess<Vector3>(type, "StartPos");
            MpCompat.harmony.Patch(GetDeclaredMethod(type, "MakeKnockbackFlyer"),
                prefix: Hm(nameof(PreMakeKnockbackFlyer)),
                finalizer: Hm(nameof(FinalizeMakeKnockbackFlyer)));
            MpCompat.harmony.Patch(GetDeclaredMethod(type, nameof(Thing.SpawnSetup)),
                prefix: Hm(nameof(PreKnockbackSpawnSetup)));
        }

        private static void PreMakeKnockbackFlyer(Pawn victim)
        {
            if (MP.IsInMultiplayer && victim != null)
                pendingKnockbackStart = victim.Position;
        }

        private static Exception FinalizeMakeKnockbackFlyer(Exception __exception)
        {
            pendingKnockbackStart = null;
            return __exception;
        }

        // StartPos was victim.DrawPos (tweened per frame, client local); SpawnSetup turns it into ticksFlightTime.
        private static void PreKnockbackSpawnSetup(Thing __instance, bool respawningAfterLoad)
        {
            if (respawningAfterLoad || !MP.IsInMultiplayer || pendingKnockbackStart is not { } cell)
                return;

            ref var start = ref knockbackStartPosField(__instance);
            start = cell.ToVector3ShiftedWithAltitude(start.y);
        }

        #endregion

        #region 5a. Melee data

        private static void PatchMeleeData()
        {
            var gameCompType = FindType("AM.GameComp");
            pawnMeleeDataType = FindType("AM.PawnData.PawnMeleeData");
            gameCompCurrentField = GetField(gameCompType, "Current");
            pawnMeleeDataDictField = GetField(gameCompType, "pawnMeleeData");
            getOrCreateDataMethod = GetDeclaredMethod(gameCompType, "GetOrCreateData");
            meleeDataPawnField = AccessTools.FieldRefAccess<Pawn>(pawnMeleeDataType, "Pawn");
            timeSinceGrappledField = GetField(pawnMeleeDataType, "TimeSinceGrappled");

            MpCompat.harmony.Patch(getOrCreateDataMethod, prefix: Hm(nameof(PreGetOrCreateData)));

            MP.RegisterSyncWorker<object>(SyncPawnMeleeData, pawnMeleeDataType);
            autoExecuteSyncField = MP.RegisterSyncField(pawnMeleeDataType, "AutoExecute");
            autoGrappleSyncField = MP.RegisterSyncField(pawnMeleeDataType, "AutoGrapple");

            var columnType = FindType("AM.ColumnWorkers.PawnColumnWorker_Base");
            MpCompat.harmony.Patch(GetDeclaredMethod(columnType, nameof(PawnColumnWorker.DoCell)),
                prefix: Hm(nameof(PreDoCell)),
                finalizer: Hm(nameof(FinalizeDoCell)));
        }

        // UI code (Assign tab, float menu cooldown icons, skill checks, duel spot menu) calls GetMeleeData and
        // would add a PawnMeleeData only on that client; its timers then differ from a later, tick-created entry.
        private static bool PreGetOrCreateData(object __instance, Pawn pawn, ref object __result)
        {
            if (!MP.IsInMultiplayer || !MP.InInterface)
                return true;

            if (pawn == null || pawn.Destroyed)
            {
                __result = null;
                return false;
            }

            var existing = (IDictionary)pawnMeleeDataDictField.GetValue(__instance);
            __result = existing.Contains(pawn) ? existing[pawn] : detachedMeleeData.GetValue(pawn, CreateDetachedMeleeData);
            return false;
        }

        private static object CreateDetachedMeleeData(Pawn pawn)
        {
            var data = Activator.CreateInstance(pawnMeleeDataType);
            meleeDataPawnField(data) = pawn;
            return data;
        }

        private static object GetMeleeData(Pawn pawn)
        {
            var gameComp = gameCompCurrentField?.GetValue(null);
            return gameComp == null || pawn == null ? null : getOrCreateDataMethod.Invoke(gameComp, new object[] { pawn });
        }

        private static void SyncPawnMeleeData(SyncWorker sync, ref object data)
        {
            if (sync.isWriting)
                sync.Write(data == null ? null : meleeDataPawnField(data));
            else
                data = GetMeleeData(sync.Read<Pawn>());
        }

        private static void PreDoCell(Pawn pawn, ref bool __state)
        {
            if (!MP.IsInMultiplayer)
                return;

            MP.WatchBegin();
            __state = true;
            var data = GetMeleeData(pawn);
            if (data == null)
                return;
            autoExecuteSyncField.Watch(data);
            autoGrappleSyncField.Watch(data);
        }

        private static Exception FinalizeDoCell(Exception __exception, bool __state)
        {
            if (__state)
                MP.WatchEnd();
            return __exception;
        }

        #endregion

        #region 5b. Float menu: execute, lasso, skills

        private static void PatchFloatMenu()
        {
            var uiType = FindType("AM.UI.DraftedFloatMenuOptionsUI");
            var controllerType = FindType("AM.Controller.ActionController");
            var spaceCheckerType = FindType("AM.SpaceChecker");
            var extensionsType = FindType("AM.Extensions");
            var grappleJobType = FindType("AM.Grappling.JobDriver_GrapplePawn");
            var grappleReportType = FindType("AM.Controller.Reports.GrappleAttemptReport");
            var spotBehaviourType = FindType("AM.Controller.GrappleSpotPickingBehaviour");
            var skillType = FindType("AM.UniqueSkills.UniqueSkillInstance");
            var skillDefType = FindType("AM.UniqueSkillDef");

            executionRequestType = FindType("AM.Controller.Requests.ExecutionAttemptRequest");
            executionReportType = FindType("AM.Controller.Reports.ExecutionAttemptReport");
            grappleRequestType = FindType("AM.Controller.Requests.GrappleAttemptRequest");
            animDefType = FindType("AM.AnimDef");
            idleControllerCompType ??= FindType("AM.Idle.IdleControllerComp");

            uiControllerField = GetField(uiType, "controller");
            executionEnabledOnClickMethod = GetDeclaredMethod(uiType, "ExecutionEnabledOnClick");
            getExecutionReportMethod = GetMethod(controllerType, "GetExecutionReport");
            getGrappleReportMethod = GetMethod(controllerType, "GetGrappleReport");
            makeOccupiedMaskMethod = GetMethod(spaceCheckerType, "MakeOccupiedMask");
            getBitMethod = GetMethod(spaceCheckerType, "GetBit");
            tryGetLassoMethod = GetMethod(extensionsType, "TryGetLasso");
            giveGrappleJobMethod = GetMethod(grappleJobType, "GiveJob");
            grappleReportCanGrapple = GetField(grappleReportType, "CanGrapple");
            grappleReportDestination = GetField(grappleReportType, "DestinationCell");
            grappleReportError = GetField(grappleReportType, "ErrorMessage");
            grappleSpotPreferAdjacent = Enum.Parse(spotBehaviourType, "PreferAdjacent");
            grappleSpotClosest = Enum.Parse(spotBehaviourType, "Closest");

            getSkillsMethod = GetMethod(idleControllerCompType, "GetSkills");
            skillIsEnabledMethod = GetMethod(skillType, "IsEnabledForPawn");
            skillCanTriggerOnMethod = GetMethod(skillType, "CanTriggerOn");
            skillTryTriggerMethod = GetMethod(skillType, "TryTrigger");
            skillDefField = GetField(skillType, "Def");
            skillDefAnimationField = GetField(skillDefType, "animation");
            skillDefTypeField = GetField(skillDefType, "type");

            // Execute: the option lambda calls ExecutionEnabledOnClick(target, grappler, report, request).
            MpCompat.harmony.Patch(executionEnabledOnClickMethod, prefix: Hm(nameof(PreExecutionEnabledOnClick)));
            MP.RegisterSyncMethod(typeof(MeleeAnimation), nameof(SyncedExecute));

            // Lasso: local function OnClick in GetEnabledLassoOption (captures grappler, target, request, report).
            var lassoClick = MpMethodUtil.GetLocalFunc(uiType, "GetEnabledLassoOption", localFunc: "OnClick");
            lassoClosureGrappler = GetField(lassoClick.DeclaringType, "grappler");
            lassoClosureTarget = GetField(lassoClick.DeclaringType, "target");
            MpCompat.harmony.Patch(lassoClick, prefix: Hm(nameof(PreLassoClick)));
            MP.RegisterSyncMethod(typeof(MeleeAnimation), nameof(SyncedLasso));

            // Skills: non-execution skills (GenerateSkillOptions lambda 0) and execution skills (GenAllOptions lambda 2).
            MpCompat.harmony.Patch(MpMethodUtil.GetLambda(uiType, "GenerateSkillOptions", lambdaOrdinal: 0),
                prefix: Hm(nameof(PreCastSkillOption)));
            MpCompat.harmony.Patch(MpMethodUtil.GetLambda(uiType, "GenAllOptions", lambdaOrdinal: 2),
                prefix: Hm(nameof(PreCastExecutionSkillOption)));
            MP.RegisterSyncMethod(typeof(MeleeAnimation), nameof(SyncedCastSkill));
        }

        // Mirrors the mod's "hold Shift to confirm" check. It reads the local keyboard, so it must run before syncing.
        private static bool PassesFriendlyCheck(Pawn target, string rejectMessage)
        {
            if (!warnOfFriendlyExecutionField(Settings))
                return true;
            if (target.HostileTo(Faction.OfPlayer) || target.def.race is { Animal: true })
                return true;
            if (Input.GetKey(KeyCode.LeftShift))
                return true;
            Messages.Message(rejectMessage, MessageTypeDefOf.RejectInput, false);
            return false;
        }

        private static bool PreExecutionEnabledOnClick(Pawn target, Pawn attacker)
        {
            if (!MP.IsInMultiplayer || !MP.InInterface)
                return true;

            if (target != null && attacker != null && PassesFriendlyCheck(target, "Tried to execute friendly: hold the Shift key when selecting to confirm!"))
                SyncedExecute(attacker, target);
            return false;
        }

        private static void SyncedExecute(Pawn attacker, Pawn target)
        {
            if (!ValidPair(attacker, target))
                return;

            var request = MakeExecutionRequest(attacker, target, null);
            RunExecutionEnabledOnClick(target, attacker, Activator.CreateInstance(executionReportType), request);
        }

        private static bool PreLassoClick(object __instance)
        {
            if (!MP.IsInMultiplayer || !MP.InInterface)
                return true;

            var grappler = (Pawn)lassoClosureGrappler.GetValue(__instance);
            var target = (Pawn)lassoClosureTarget.GetValue(__instance);
            if (grappler != null && target != null)
                SyncedLasso(grappler, target);
            return false;
        }

        // Same steps as the mod's OnClick, with the request rebuilt the way GenAllOptions builds it.
        private static void SyncedLasso(Pawn grappler, Pawn target)
        {
            if (!ValidPair(grappler, target))
                return;

            var maskArgs = new object[] { grappler.Map, grappler.Position, null };
            makeOccupiedMaskMethod.Invoke(null, maskArgs);

            var request = Activator.CreateInstance(grappleRequestType);
            SetField(request, "Grappler", grappler);
            SetField(request, "Target", target);
            SetField(request, "DoNotCheckLasso", false);
            SetField(request, "GrappleSpotPickingBehaviour", target.HostileTo(Faction.OfPlayer) ? grappleSpotPreferAdjacent : grappleSpotClosest);
            SetField(request, "OccupiedMask", (uint)maskArgs[2]);

            var report = getGrappleReportMethod.Invoke(uiControllerField.GetValue(null), new[] { request });
            if (!(bool)grappleReportCanGrapple.GetValue(report))
            {
                if (MP.IsExecutingSyncCommandIssuedBySelf)
                    Messages.Message((string)grappleReportError.GetValue(report), MessageTypeDefOf.RejectInput, false);
                return;
            }

            var destination = (IntVec3)grappleReportDestination.GetValue(report);
            if (!(bool)giveGrappleJobMethod.Invoke(null, new object[] { grappler, target, destination, true, null }))
                Log.Warning(LogPrefix + $"Failed to give grapple job to {grappler}.");
            else if (GetMeleeData(grappler) is { } data)
                timeSinceGrappledField.SetValue(data, 0f);
        }

        private static bool PreCastSkillOption(object __instance)
        {
            if (!MP.IsInMultiplayer || !MP.InInterface)
                return true;

            // <>c__DisplayClass5_2 -> CS$<>8__locals2 (skill) -> CS$<>8__locals1 (target, attacker)
            var locals2 = ReadField(__instance, "CS$<>8__locals2");
            var locals1 = ReadField(locals2, "CS$<>8__locals1");
            CastSkillFromUi((Pawn)ReadField(locals1, "attacker"), (Pawn)ReadField(locals1, "target"), ReadField(locals2, "skill"));
            return false;
        }

        private static bool PreCastExecutionSkillOption(object __instance)
        {
            if (!MP.IsInMultiplayer || !MP.InInterface)
                return true;

            // <>c__DisplayClass4_3 -> CS$<>8__locals3 (skill) -> CS$<>8__locals2 (target) -> CS$<>8__locals1 (pawn)
            var locals3 = ReadField(__instance, "CS$<>8__locals3");
            var locals2 = ReadField(locals3, "CS$<>8__locals2");
            var locals1 = ReadField(locals2, "CS$<>8__locals1");
            CastSkillFromUi((Pawn)ReadField(locals1, "pawn"), (Pawn)ReadField(locals2, "target"), ReadField(locals3, "skill"));
            return false;
        }

        private static void CastSkillFromUi(Pawn caster, Pawn target, object skill)
        {
            if (caster == null || target == null || skill == null)
                return;
            if (!PassesFriendlyCheck(target, "Tried to cast skill on friendly: hold the Shift key when selecting to confirm!"))
                return;

            var skills = GetSkills(caster);
            var index = skills?.IndexOf(skill) ?? -1;
            if (index < 0)
                return;
            SyncedCastSkill(caster, target, index);
        }

        private static void SyncedCastSkill(Pawn caster, Pawn target, int skillIndex)
        {
            if (!ValidPair(caster, target))
                return;
            var skills = GetSkills(caster);
            if (skills == null || skillIndex < 0 || skillIndex >= skills.Count || skills[skillIndex] is not { } skill)
                return;

            var def = (Def)skillDefField.GetValue(skill);
            LocalTargetInfo targetInfo = target;
            var enabledArgs = new object[] { null };
            var enabled = (bool)skillIsEnabledMethod.Invoke(skill, enabledArgs);
            var cantReason = (string)skillCanTriggerOnMethod.Invoke(skill, new object[] { targetInfo });
            if (!enabled || cantReason != null || !(bool)skillTryTriggerMethod.Invoke(skill, new object[] { targetInfo }))
            {
                if (MP.IsExecutingSyncCommandIssuedBySelf)
                    Messages.Message("AM.Skill.CantCastGeneric".Translate(def?.label, target), MessageTypeDefOf.RejectInput, false);
                return;
            }

            if (def == null || skillDefTypeField.GetValue(def)?.ToString() != "Execution")
                return;

            // Execution skills: the mod's lambda follows TryTrigger with an execution limited to the skill's animation.
            var onlyAnims = Array.CreateInstance(animDefType, 1);
            onlyAnims.SetValue(skillDefAnimationField.GetValue(def), 0);
            var request = MakeExecutionRequest(caster, target, onlyAnims);
            var report = FirstOrDefault((IEnumerable)getExecutionReportMethod.Invoke(uiControllerField.GetValue(null), new[] { request }))
                         ?? Activator.CreateInstance(executionReportType);
            RunExecutionEnabledOnClick(target, caster, report, request);
        }

        private static IList GetSkills(Pawn pawn)
        {
            if (pawn?.AllComps == null)
                return null;
            foreach (var comp in pawn.AllComps)
            {
                if (idleControllerCompType.IsInstanceOfType(comp))
                    return getSkillsMethod.Invoke(comp, null) as IList;
            }
            return null;
        }

        private static bool ValidPair(Pawn a, Pawn b)
            => a != null && b != null && a.Spawned && b.Spawned && a.Map == b.Map && !a.Dead && !a.Downed;

        // Built like GetExecutionAttemptOption builds it, at execution time instead of menu time.
        private static object MakeExecutionRequest(Pawn attacker, Pawn target, Array onlyTheseAnimations)
        {
            var maskArgs = new object[] { attacker.Map, attacker.Position, null };
            var occupied = (ulong)makeOccupiedMaskMethod.Invoke(null, maskArgs);

            var request = Activator.CreateInstance(executionRequestType);
            SetField(request, "CanUseLasso", tryGetLassoMethod.Invoke(null, new object[] { attacker }) != null);
            SetField(request, "CanWalk", true);
            SetField(request, "EastCell", !(bool)getBitMethod.Invoke(null, new object[] { occupied, 1, 0 }));
            SetField(request, "WestCell", !(bool)getBitMethod.Invoke(null, new object[] { occupied, -1, 0 }));
            SetField(request, "Executioner", attacker);
            SetField(request, "OccupiedMask", occupied);
            SetField(request, "SmallOccupiedMask", (uint)maskArgs[2]);
            SetField(request, "Target", target);
            if (onlyTheseAnimations != null)
                SetField(request, "OnlyTheseAnimations", onlyTheseAnimations);
            return request;
        }

        // The mod's own execution logic, minus its Shift-key check (already done before syncing, and the
        // keyboard state differs per client).
        private static void RunExecutionEnabledOnClick(Pawn target, Pawn attacker, object report, object request)
        {
            var settings = Settings;
            var warn = warnOfFriendlyExecutionField(settings);
            warnOfFriendlyExecutionField(settings) = false;
            try
            {
                executionEnabledOnClickMethod.Invoke(null, new[] { target, attacker, report, request });
            }
            finally
            {
                warnOfFriendlyExecutionField(settings) = warn;
            }
        }

        private static void SetField(object boxedStruct, string name, object value)
            => GetField(boxedStruct.GetType(), name).SetValue(boxedStruct, value);

        private static object ReadField(object obj, string name)
            => GetField(obj.GetType(), name).GetValue(obj);

        private static object FirstOrDefault(IEnumerable enumerable)
        {
            if (enumerable == null)
                return null;
            foreach (var item in enumerable)
                return item;
            return null;
        }

        #endregion

        #region 5c. Duel spot gizmos

        private static void PatchDuelSpot()
        {
            var type = FindType("AM.Buildings.Building_DuelSpot");
            MP.RegisterSyncMethod(type, "TryStartDuel");
            // Command_Toggle.toggleAction: IsHidden = !IsHidden
            MpCompat.RegisterLambdaMethod(type, "GetGizmos", 1);
        }

        #endregion

        #region 6. Settings lock

        private static void PatchSettings()
        {
            MpCompat.harmony.Patch(GetDeclaredMethod(FindType("AM.Core"), nameof(Mod.DoSettingsWindowContents)),
                prefix: Hm(nameof(PreDoSettingsWindowContents)));
        }

        private static bool PreDoSettingsWindowContents(Rect inRect)
        {
            if (!MP.IsInMultiplayer)
                return true;

            Widgets.Label(inRect, "Melee Animation settings change the simulation (chances, timings, gore) and are not synced. " +
                                  "They are locked during a Multiplayer session: change them in single player, identically on every client.");
            return false;
        }

        #endregion
    }
}
