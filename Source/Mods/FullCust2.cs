using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;
using Verse.AI;

namespace Multiplayer.Compat
{
    /// <summary>FULL1127 CU2 (P3CUST group B2): Perspective Shift, Vehicle Map Framework, Force Do Job (the Force Do Job part is called from FullForceDoJob in FullKE.cs).</summary>
    internal static class FullCust2
    {
        private const string Tag = "[MPCompat - FULL1127 CU]";

        // ---------- Vehicle Map Framework ----------
        // VehiclePawnWithMap.DrawPos / Tick / RecacheDrawPos pick a branch on Find.CurrentMap == CurrentLevel. The branch decides cachedExactPos/cachedDrawPos,
        // and IntVec3.ToBaseMapCoord(vehicle) turns cachedExactPos into a cell used by the sim. Always take the "not viewing" branch, which is built from sim state.
        private static readonly MethodInfo CurrentMapGetter = AccessTools.PropertyGetter(typeof(Find), nameof(Find.CurrentMap));
        public static Map NoViewMap() => null;

        public static IEnumerable<CodeInstruction> NoViewMapTranspiler(IEnumerable<CodeInstruction> instr)
        {
            var repl = AccessTools.Method(typeof(FullCust2), nameof(NoViewMap));
            foreach (var ci in instr)
            {
                if (ci.Calls(CurrentMapGetter)) { ci.opcode = OpCodes.Call; ci.operand = repl; }
                yield return ci;
            }
        }

        // Find.CurrentMap -> the thing's own map (a view-independent value): the zipline projectile always ticks at rate 1, the same on every PC.
        public static Map OwnMap(Thing t) => t?.Map;
        public static IEnumerable<CodeInstruction> ThisMapTranspiler(IEnumerable<CodeInstruction> instr)
        {
            var repl = AccessTools.Method(typeof(FullCust2), nameof(OwnMap));
            foreach (var ci in instr)
            {
                if (ci.Calls(CurrentMapGetter)) { yield return new CodeInstruction(OpCodes.Ldarg_0).WithLabels(ci.labels); ci.labels = new List<Label>(); ci.opcode = OpCodes.Call; ci.operand = repl; }
                yield return ci;
            }
        }

        public static int PatchVehicleMap(string typeName)
        {
            var type = AccessTools.TypeByName(typeName);
            var found = 0;
            if (type == null) { Log.Error($"{Tag} Vehicle Map Framework: type {typeName} not found"); return 0; }
            foreach (var m in new[] { AccessTools.DeclaredPropertyGetter(type, "DrawPos"), AccessTools.DeclaredMethod(type, "Tick"), AccessTools.DeclaredMethod(type, "RecacheDrawPos") })
            {
                if (m == null) { Log.Error($"{Tag} Vehicle Map Framework: a DrawPos/Tick/RecacheDrawPos method of {typeName} not found"); continue; }
                MpCompat.harmony.Patch(m, transpiler: new HarmonyMethod(typeof(FullCust2), nameof(NoViewMapTranspiler)));
                found++;
            }
            // cu9: the hover subclass DrawPos (feeds the same cache) and the zipline projectile tick rate (sim) read Find.CurrentMap too
            var hover = AccessTools.TypeByName(typeName + "_Hover");
            var hoverGet = hover == null ? null : AccessTools.DeclaredPropertyGetter(hover, "DrawPos");
            if (hoverGet != null) { MpCompat.harmony.Patch(hoverGet, transpiler: new HarmonyMethod(typeof(FullCust2), nameof(NoViewMapTranspiler))); found++; }
            else Log.Error($"{Tag} Vehicle Map Framework: hover DrawPos not found");
            var zip = AccessTools.TypeByName("VehicleMapFramework.Bullet_ZiplineBase");
            var zipGet = zip == null ? null : AccessTools.DeclaredPropertyGetter(zip, "UpdateRateTicks");
            if (zipGet != null) { MpCompat.harmony.Patch(zipGet, transpiler: new HarmonyMethod(typeof(FullCust2), nameof(ThisMapTranspiler))); found++; }
            else Log.Error($"{Tag} Vehicle Map Framework: zipline UpdateRateTicks not found");
            Log.Message($"{Tag} Vehicle Map Framework: view-independent draw position and zipline rate, {found} of 5 methods patched");
            return found;
        }

        // ---------- Force Do Job ----------
        private static FieldInfo choicesField;
        private static FieldInfo selfTendTimerField;
        private static FieldInfo allowAllJobsField;
        private static FieldInfo allowSelfTendField;

        public static void ClearChoices() { if (choicesField != null) choicesField.SetValue(null, null); }

        // Float menu prefix of the mod, MP version: set the UI-only static, send the self-tend write (selfTend + timer) as one synced call.
        private static bool FloatMenuPrefix(List<Pawn> selectedPawns)
        {
            if (!MP.IsInMultiplayer) return true;
            var pawn = selectedPawns != null && selectedPawns.Count > 0 ? selectedPawns[0] : null;
            choicesField.SetValue(null, pawn);
            if (pawn != null && pawn.playerSettings != null && !pawn.playerSettings.selfTend && (bool)allowSelfTendField.GetValue(null))
            {
                // the value is computed here, from this PC's settings, and sent: the other PC applies the same bool
                var value = (bool)allowAllJobsField.GetValue(null) || (pawn.story != null && !pawn.WorkTypeIsDisabled(WorkTypeDefOf.Doctor));
                SyncedSelfTend(pawn, value);
            }
            return false;
        }

        [SyncMethod]
        public static void SyncedSelfTend(Pawn pawn, bool value)
        {
            if (pawn?.playerSettings == null) return;
            var timer = (Dictionary<Pawn, long>)selfTendTimerField.GetValue(null);
            timer[pawn] = Find.TickManager.TicksGame * (10000000L / 60L); // same units as the KE transpiler
            pawn.playerSettings.selfTend = value;
        }

        public static void ForceDoJobFinalizer() => ClearChoices();
        public static void ForceDoJobTickPrefix() => ClearChoices();

        public static int PatchForceDoJob()
        {
            var main = AccessTools.TypeByName("ForceDoJob.Main");
            var settings = AccessTools.TypeByName("ForceDoJob.Settings");
            var prefixM = AccessTools.Method("ForceDoJob.Patch_FloatMenuMakerMap_ChoicesAtFor:Prefix");
            var getOptions = AccessTools.Method(typeof(FloatMenuMakerMap), nameof(FloatMenuMakerMap.GetOptions));
            if (main == null || settings == null || prefixM == null || getOptions == null)
            {
                Log.Error($"{Tag} Force Do Job: member not found (Main {main != null}, Settings {settings != null}, Prefix {prefixM != null}, GetOptions {getOptions != null})");
                return 0;
            }
            choicesField = AccessTools.Field(main, "ChoicesForPawn");
            selfTendTimerField = AccessTools.Field(main, "SelfTendTimer");
            allowAllJobsField = AccessTools.Field(settings, "allowPawnsToDoAllJobs");
            allowSelfTendField = AccessTools.Field(settings, "allowPawnsToSelfTend");
            if (choicesField == null || selfTendTimerField == null || allowAllJobsField == null || allowSelfTendField == null)
            {
                Log.Error($"{Tag} Force Do Job: static field not found");
                return 0;
            }
            var tick = AccessTools.Method(typeof(TickManager), nameof(TickManager.DoSingleTick));
            MpCompat.harmony.Patch(prefixM, prefix: new HarmonyMethod(typeof(FullCust2), nameof(FloatMenuPrefix)));
            MpCompat.harmony.Patch(getOptions, finalizer: new HarmonyMethod(typeof(FullCust2), nameof(ForceDoJobFinalizer)));
            MpCompat.harmony.Patch(tick, prefix: new HarmonyMethod(typeof(FullCust2), nameof(ForceDoJobTickPrefix)));
            MP.RegisterSyncMethod(typeof(FullCust2), nameof(SyncedSelfTend));
            Log.Message($"{Tag} Force Do Job: ChoicesForPawn cleared on exit/tick, self-tend write synced, registered");
            return 3;
        }

        // ---------- Perspective Shift ----------
        // Avatar input handlers call pawn.jobs.StartJob / EndCurrentJob on the clicking PC only. Each call site in the mod's Avatar type goes through a shim that
        // sends a synced call from the UI; inside a tick or a running command MP runs the synced method directly, so Avatar.Tick sites still work.
        private static readonly MethodInfo StartJobM = AccessTools.Method(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.StartJob));
        private static readonly MethodInfo EndJobM = AccessTools.Method(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.EndCurrentJob));
        public static int ShimmedStart, ShimmedEnd;

        [SyncMethod]
        public static void SyncedStartJob(Pawn pawn, Job job, JobCondition cond, bool resumeCur, bool cancelBusy)
        {
            if (pawn?.jobs == null || job == null) return;
            pawn.jobs.StartJob(job, cond, null, resumeCur, cancelBusy);
        }

        [SyncMethod]
        public static void SyncedEndJob(Pawn pawn, JobCondition cond, bool startNew, bool canReturnToPool)
        {
            if (pawn?.jobs == null) return;
            pawn.jobs.EndCurrentJob(cond, startNew, canReturnToPool);
        }

        public static void StartJobShim(Pawn_JobTracker jobs, Job job, JobCondition cond, bool resumeCur, bool cancelBusy) => SyncedStartJob(jobs.pawn, job, cond, resumeCur, cancelBusy);
        public static void EndJobShim(Pawn_JobTracker jobs, JobCondition cond, bool startNew, bool canReturnToPool) => SyncedEndJob(jobs.pawn, cond, startNew, canReturnToPool);

        public static IEnumerable<CodeInstruction> JobShimTranspiler(IEnumerable<CodeInstruction> instr, ILGenerator gen)
        {
            var startShim = AccessTools.Method(typeof(FullCust2), nameof(StartJobShim));
            var endShim = AccessTools.Method(typeof(FullCust2), nameof(EndJobShim));
            foreach (var ci in instr)
            {
                if (ci.Calls(StartJobM))
                {
                    // StartJob(job, cond, ThinkNode jobGiver, bool resume, bool cancelBusy, ...defaults): keep jobs, job, cond, resume, cancelBusy; drop the rest (defaults at every Avatar call site)
                    var seq = KeepArgs(StartJobM, new[] { 0, 1, 2, 4, 5 }, gen).ToList();
                    seq.Add(new CodeInstruction(OpCodes.Call, startShim));
                    seq[0].labels.AddRange(ci.labels); seq[0].blocks.AddRange(ci.blocks); // jumps land on the first stloc, not on the call
                    foreach (var x in seq) yield return x;
                    ShimmedStart++;
                    continue;
                }
                if (ci.Calls(EndJobM))
                {
                    ci.opcode = OpCodes.Call; ci.operand = endShim; // same parameter list with the tracker first
                    ShimmedEnd++;
                }
                yield return ci;
            }
        }

        // Stack holds [this, p1..pn]. Store all into locals, then reload the chosen ones (index 0 = this).
        private static IEnumerable<CodeInstruction> KeepArgs(MethodInfo m, int[] keep, ILGenerator gen)
        {
            var types = new List<Type> { m.DeclaringType };
            types.AddRange(m.GetParameters().Select(p => p.ParameterType));
            var locals = types.Select(t => gen.DeclareLocal(t)).ToList();
            for (var i = types.Count - 1; i >= 0; i--) yield return new CodeInstruction(OpCodes.Stloc, locals[i]);
            foreach (var k in keep) yield return new CodeInstruction(OpCodes.Ldloc, locals[k]);
        }

        public static int PatchAvatar(string typeName)
        {
            var type = AccessTools.TypeByName(typeName);
            if (type == null) { Log.Error($"{Tag} Perspective Shift: type {typeName} not found"); return 0; }
            var found = 0;
            var methods = new List<MethodBase>();
            // every type of the mod's assembly (Avatar, State, Dialog_StorageMenu, nested lambda classes)
            foreach (var t in type.Assembly.GetTypes()) methods.AddRange(t.GetMethods(AccessTools.allDeclared));
            foreach (var m in methods)
            {
                if (m.IsAbstract || m.GetMethodBody() == null) continue;
                if (!CallsJobTracker(m)) continue;
                MpCompat.harmony.Patch(m, transpiler: new HarmonyMethod(typeof(FullCust2), nameof(JobShimTranspiler)));
                found++;
            }
            return found;
        }

        // cu9: Avatar.Tick runs from State.Tick only on the PC that has the avatar active, and writes sim state (needs, drafter, carried thing, doors, stance, pather, job expiry).
        // A per-PC sim write desyncs, so in MP the tick body is skipped; the synced job paths above still work.
        public static bool SkipAvatarTick() => !MP.IsInMultiplayer;

        public static void PatchAvatarTick(string typeName)
        {
            var m = AccessTools.DeclaredMethod(AccessTools.TypeByName(typeName), "Tick");
            if (m == null) { Log.Error($"{Tag} Perspective Shift: Avatar.Tick not found"); return; }
            MpCompat.harmony.Patch(m, prefix: new HarmonyMethod(typeof(FullCust2), nameof(SkipAvatarTick)) { priority = Priority.First });
            Log.Message($"{Tag} Perspective Shift: Avatar.Tick skipped in multiplayer");
        }

        private static bool CallsJobTracker(MethodBase m)
        {
            try { return PatchProcessor.ReadMethodBody(m).Any(p => p.Key == OpCodes.Callvirt && p.Value is MethodInfo mi && (mi == StartJobM || mi == EndJobM)); }
            catch { return false; }
        }
    }

    /// <summary>Vehicle Map Framework by oels (draw position cache).</summary>
    [MpCompatFor("oels.vehiclemapframework")]
    class FullVehicleMapFramework
    {
        public FullVehicleMapFramework(ModContentPack mod) => FullCust2.PatchVehicleMap("VehicleMapFramework.VehiclePawnWithMap");
    }

    /// <summary>Perspective Shift by ferny: StartJob / EndCurrentJob from the avatar input handlers become synced calls. TryTakeOrderedJob is already synced by MP.</summary>
    [MpCompatFor("ferny.perspectiveshift")]
    class FullPerspectiveShift
    {
        public FullPerspectiveShift(ModContentPack mod)
        {
            var methods = FullCust2.PatchAvatar("PerspectiveShift.Avatar");
            FullCust2.PatchAvatarTick("PerspectiveShift.Avatar");
            // orchestrator review: MP has no sync worker for Job; it serializes it only as an exposed (IExposable) parameter, like TryTakeOrderedJob
            MP.RegisterSyncMethod(typeof(FullCust2), nameof(FullCust2.SyncedStartJob)).ExposeParameter(1);
            MP.RegisterSyncMethod(typeof(FullCust2), nameof(FullCust2.SyncedEndJob));
            Log.Message($"[MPCompat - FULL1127 CU] Perspective Shift: {methods} Avatar methods patched, StartJob/EndCurrentJob shims registered");
        }
    }
}
