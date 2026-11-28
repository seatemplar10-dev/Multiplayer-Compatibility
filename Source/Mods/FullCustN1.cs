using System;
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
    /// <summary>FULL1127 wave N1 (cu15): class N rows, a mod patch changes sim state outside an MP-synced call.</summary>
    internal static class FullCustN1
    {
        public const string Tag = "[MPCompat - FULL1127 N1] ";

        public static Type FindType(params string[] names)
        {
            foreach (var n in names)
            {
                var t = AccessTools.TypeByName(n);
                if (t != null) return t;
            }
            return null;
        }

        /// <summary>True when the delegate's compiler generated method lives inside type t (lambda, iterator or closure of t).</summary>
        public static bool FromType(Delegate d, Type t)
        {
            if (d == null || t == null) return false;
            for (var x = d.Method?.DeclaringType; x != null; x = x.DeclaringType)
                if (x == t) return true;
            return false;
        }

        public static void Ok(string mod, string what) => Log.Message(Tag + mod + ": " + what + " registered");
        public static void Fail(string mod, string what, Exception e) => Log.Error(Tag + mod + ": " + what + " failed " + e);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Animal Biosculpter: its float menu option calls CompBiosculpterPod.PrepareCycleJob on the clicking PC only.
    // MP syncs the vanilla lambda (SyncDelegates.cs:23) but not the mod's own action. Sync the whole call, like the vanilla lambda does.
    // ---------------------------------------------------------------------------------------------------------------
    [MpCompatFor("bodilpwnz.animalbiosculpter")]
    class FullN1AnimalBiosculpter
    {
        public FullN1AnimalBiosculpter(ModContentPack mod)
        {
            try
            {
                MP.RegisterSyncMethod(typeof(FullN1AnimalBiosculpter), nameof(SyncPrepareCycleJob));
                MpCompat.harmony.Patch(AccessTools.Method(typeof(CompBiosculpterPod), nameof(CompBiosculpterPod.PrepareCycleJob)),
                    prefix: new HarmonyMethod(typeof(FullN1AnimalBiosculpter), nameof(PrePrepareCycleJob)));
                FullCustN1.Ok("Animal Biosculpter", "PrepareCycleJob sync method (pod, hauler, pawn, cycle index, job def + 3 targets)");
            }
            catch (Exception e) { FullCustN1.Fail("Animal Biosculpter", "PrepareCycleJob sync", e); }
        }

        // Only the UI path: a mouse event is current and no sync command is executing (the vanilla lambda runs inside one).
        static bool PrePrepareCycleJob(CompBiosculpterPod __instance, Pawn hauler, Pawn biosculptee, CompBiosculpterPod_Cycle cycle, Job job)
        {
            if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand || Event.current == null) return true;
            if (job == null || job.def == null || hauler == null || biosculptee == null || cycle == null) return true;
            int idx = __instance.AvailableCycles.IndexOf(cycle);
            if (idx < 0) return true;
            SyncPrepareCycleJob(__instance, hauler, biosculptee, idx, job.def, job.targetA, job.targetB, job.targetC);
            return false;
        }

        static void SyncPrepareCycleJob(CompBiosculpterPod pod, Pawn hauler, Pawn biosculptee, int cycleIndex, JobDef def, LocalTargetInfo a, LocalTargetInfo b, LocalTargetInfo c)
        {
            var cycles = pod.AvailableCycles;
            if (cycleIndex < 0 || cycleIndex >= cycles.Count) return;
            var job = JobMaker.MakeJob(def, a, b, c);
            pod.PrepareCycleJob(hauler, biosculptee, cycles[cycleIndex], job);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Toddlers: Pawn_CarryTracker.GetGizmos postfix adds a Drop pawn gizmo whose lambda calls TryDropCarriedThing locally.
    // ---------------------------------------------------------------------------------------------------------------
    [MpCompatFor("cyanobot.toddlers")]
    class FullN1Toddlers
    {
        static Type modPatch;

        public FullN1Toddlers(ModContentPack mod)
        {
            try
            {
                modPatch = FullCustN1.FindType("Toddlers.Pawn_CarryTracker_GetGizmos_Patch");
                if (modPatch == null) { Log.Error(FullCustN1.Tag + "Toddlers: Pawn_CarryTracker_GetGizmos_Patch not found"); return; }
                MP.RegisterSyncMethod(typeof(FullN1Toddlers), nameof(SyncDropCarriedPawn));
                MpCompat.harmony.Patch(AccessTools.Method(typeof(Pawn_CarryTracker), nameof(Pawn_CarryTracker.GetGizmos)),
                    postfix: new HarmonyMethod(typeof(FullN1Toddlers), nameof(PostGetGizmos)) { priority = Priority.Last });
                FullCustN1.Ok("Toddlers", "drop carried pawn gizmo sync method (pawn)");
            }
            catch (Exception e) { FullCustN1.Fail("Toddlers", "drop gizmo sync", e); }
        }

        static IEnumerable<Gizmo> PostGetGizmos(IEnumerable<Gizmo> __result, Pawn_CarryTracker __instance)
        {
            foreach (var g in __result)
            {
                if (g is Command_Action cmd && FullCustN1.FromType(cmd.action, modPatch))
                {
                    var pawn = __instance.pawn;
                    cmd.action = () =>
                    {
                        if (pawn != null && MP.IsInMultiplayer && !MP.IsExecutingSyncCommand) SyncDropCarriedPawn(pawn);
                        else if (pawn != null) DoDrop(pawn);
                    };
                }
                yield return g;
            }
        }

        static void SyncDropCarriedPawn(Pawn pawn) => DoDrop(pawn);

        static void DoDrop(Pawn pawn)
        {
            if (pawn?.carryTracker == null || !(pawn.carryTracker.CarriedThing is Pawn)) return;
            pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out _);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Bunny Framework: its prefix replaces CompPlantable.CompGetGizmosExtra, so the vanilla synced "cancel planting" lambda is
    // skipped and the replacement lambda does PlantCells.Clear() locally.
    // ---------------------------------------------------------------------------------------------------------------
    [MpCompatFor("romyashi.bunnyframework")]
    class FullN1BunnyFramework
    {
        static Type modPatch;

        public FullN1BunnyFramework(ModContentPack mod)
        {
            try
            {
                modPatch = FullCustN1.FindType("BunnyFramework.Patch_CompPlantable_CompGetGizmosExtra", "Patch_CompPlantable_CompGetGizmosExtra");
                if (modPatch == null) { Log.Error(FullCustN1.Tag + "Bunny Framework: Patch_CompPlantable_CompGetGizmosExtra not found"); return; }
                MP.RegisterSyncMethod(typeof(FullN1BunnyFramework), nameof(SyncClearPlantCells));
                MpCompat.harmony.Patch(AccessTools.Method(typeof(CompPlantable), nameof(CompPlantable.CompGetGizmosExtra)),
                    postfix: new HarmonyMethod(typeof(FullN1BunnyFramework), nameof(PostGizmos)) { priority = Priority.Last });
                FullCustN1.Ok("Bunny Framework", "cancel planting gizmo sync method (comp)");
            }
            catch (Exception e) { FullCustN1.Fail("Bunny Framework", "cancel planting sync", e); }
        }

        static IEnumerable<Gizmo> PostGizmos(IEnumerable<Gizmo> __result, CompPlantable __instance)
        {
            foreach (var g in __result)
            {
                // The Plant gizmo of the mod only starts targeting (synced by MP); only the Cancel one (Designator_Cancel hotkey) writes.
                if (g is Command_Action cmd && cmd.hotKey == KeyBindingDefOf.Designator_Cancel && FullCustN1.FromType(cmd.action, modPatch))
                {
                    var comp = __instance;
                    cmd.action = () =>
                    {
                        if (MP.IsInMultiplayer && !MP.IsExecutingSyncCommand) SyncClearPlantCells(comp);
                        else comp.PlantCells.Clear();
                    };
                }
                yield return g;
            }
        }

        static void SyncClearPlantCells(CompPlantable comp) => comp?.PlantCells.Clear();
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Nice Plants Menu: its prefix skips vanilla Command_SetPlantToGrow.ProcessInput (lambda 2 is synced by MP); the replacement
    // dialog calls Dialog_PlantBrowser.SetPlantDefToGrow(zones, plant), which writes on this PC only.
    // ---------------------------------------------------------------------------------------------------------------
    [MpCompatFor("andromeda.niceplantsmenu")]
    class FullN1NicePlantsMenu
    {
        public FullN1NicePlantsMenu(ModContentPack mod)
        {
            try
            {
                var type = AccessTools.TypeByName("NicePlantsMenu.Dialog_PlantBrowser");
                var mi = type == null ? null : AccessTools.Method(type, "SetPlantDefToGrow");
                if (mi == null) { Log.Error(FullCustN1.Tag + "Nice Plants Menu: Dialog_PlantBrowser.SetPlantDefToGrow not found"); return; }
                MP.RegisterSyncMethod(mi);
                FullCustN1.Ok("Nice Plants Menu", "Dialog_PlantBrowser.SetPlantDefToGrow sync method (zones list + plant def)");
            }
            catch (Exception e) { FullCustN1.Fail("Nice Plants Menu", "SetPlantDefToGrow sync", e); }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Mech Group Rename Caravan List: its prefix replaces Designator_MechControlGroup.ProcessInput (MP syncs vanilla lambda 1
    // with MapSelected context); the replacement option lambda calls selected.Assign(pawn) locally. Sync that lambda the same way.
    // ---------------------------------------------------------------------------------------------------------------
    [MpCompatFor("firework.mechgrouprenamecaravanlist")]
    class FullN1MechGroupRename
    {
        // Ordinal of the option action in AssignMenu_Patch.Prefix (ordinal 0 is the All(...) predicate); checked in D2 by IL token of Assign.
        public const int ActionOrdinal = 1;

        public FullN1MechGroupRename(ModContentPack mod)
        {
            try
            {
                var type = AccessTools.TypeByName("MechGroupRenameCaravanList.AssignMenu_Patch");
                if (type == null) { Log.Error(FullCustN1.Tag + "Mech Group Rename: AssignMenu_Patch not found"); return; }
                var method = MpMethodUtil.GetLambda(type, "Prefix", MethodType.Normal, null, ActionOrdinal);
                MP.RegisterSyncDelegate(type, method.DeclaringType.Name, method.Name, null).SetContext(SyncContext.MapSelected);
                FullCustN1.Ok("Mech Group Rename Caravan List", "assign to group menu lambda sync delegate (MapSelected)");
            }
            catch (Exception e) { FullCustN1.Fail("Mech Group Rename Caravan List", "assign lambda sync", e); }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Gravship Fleet: the CompPilotConsole.StartChoosingDestination_NewTemp prefix launches a queued jump through FleetJump.Fly,
    // whose confirm lambda (fuel + InitiateTakeoff) then runs on the clicking PC only. Vanilla lambda 5 (synced by MP) is bypassed.
    // ---------------------------------------------------------------------------------------------------------------
    [MpCompatFor("asunib.gravshipfleet")]
    class FullN1GravshipFleet
    {
        // Ordinal of the confirm action in FleetJump.Fly (ordinal 1 is the cancel action that reopens the picker); checked in D2.
        public const int ConfirmOrdinal = 0;

        public FullN1GravshipFleet(ModContentPack mod)
        {
            try
            {
                var type = AccessTools.TypeByName("GravshipFleet.FleetJump");
                if (type == null) { Log.Error(FullCustN1.Tag + "Gravship Fleet: FleetJump not found"); return; }
                var method = MpMethodUtil.GetLambda(type, "Fly", MethodType.Normal, null, ConfirmOrdinal);
                MP.RegisterSyncDelegate(type, method.DeclaringType.Name, method.Name, new[] { "engine", "tile" });
                FullCustN1.Ok("Gravship Fleet", "FleetJump.Fly confirm (fuel + takeoff) lambda sync delegate (engine, tile)");
            }
            catch (Exception e) { FullCustN1.Fail("Gravship Fleet", "Fly confirm sync", e); }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // UI Rethemed Quests: the mod prefix on Dialog_RewardPrefsConfig.DoWindowContents has priority 800 and returns false, which skips
    // MP's watch prefix (SyncFields.cs:294, MpPrefix at MpPriority.MpFirst = Priority.First + 1 = 799, handler priority 400 for the
    // Watch calls). RewardPrefs.Draw then writes Faction.allowRoyalFavorRewards / allowGoodwillRewards raw.
    // Our prefix runs at 900 (before the mod) and starts the watch. MP's own postfix (MpLast, always runs even if a prefix skipped
    // the original) ends it. If the mod prefix returns true (original runs, MP's prefix runs too and has its own marker) our postfix,
    // running after MP's, ends ours: __runOriginal tells the two cases apart.
    // ---------------------------------------------------------------------------------------------------------------
    [MpCompatFor("lazyfridaystudio.uirethemed.quests")]
    class FullN1UIRethemedQuests
    {
        static ISyncField royal, goodwill;

        public FullN1UIRethemedQuests(ModContentPack mod)
        {
            try
            {
                var mpFields = AccessTools.TypeByName("Multiplayer.Client.SyncFields");
                royal = mpFields == null ? null : AccessTools.Field(mpFields, "SyncFactionAcceptRoyalFavor")?.GetValue(null) as ISyncField;
                goodwill = mpFields == null ? null : AccessTools.Field(mpFields, "SyncFactionAcceptGoodwill")?.GetValue(null) as ISyncField;
                if (royal == null || goodwill == null) { Log.Error(FullCustN1.Tag + "UI Rethemed Quests: MP SyncFactionAccept* fields not found"); return; }
                MpCompat.harmony.Patch(AccessTools.Method(typeof(Dialog_RewardPrefsConfig), nameof(Dialog_RewardPrefsConfig.DoWindowContents)),
                    prefix: new HarmonyMethod(typeof(FullN1UIRethemedQuests), nameof(PreDoWindowContents)) { priority = 900 },
                    postfix: new HarmonyMethod(typeof(FullN1UIRethemedQuests), nameof(PostDoWindowContents)) { priority = -5 });
                FullCustN1.Ok("UI Rethemed Quests", "reward prefs faction fields watch (prefix priority 900, before the mod's 800)");
            }
            catch (Exception e) { FullCustN1.Fail("UI Rethemed Quests", "reward prefs watch", e); }
        }

        static void PreDoWindowContents(ref bool __state)
        {
            __state = false;
            if (!MP.IsInMultiplayer) return;
            MP.WatchBegin();
            foreach (var faction in Find.FactionManager.AllFactionsVisibleInViewOrder)
            {
                royal.Watch(faction);
                goodwill.Watch(faction);
            }
            __state = true;
        }

        static void PostDoWindowContents(bool __state, bool __runOriginal)
        {
            if (__state && __runOriginal) MP.WatchEnd();
        }
    }
}
