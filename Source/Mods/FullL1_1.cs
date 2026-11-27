using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    // FULL1127 L1 batch 1 (FIX_MPC_1). One class per mod, each logs one line so a boot log shows it.

    /// <summary>Toggle Harvest (jaxe.toggleharvest). The gizmo toggleAction flips a static HashSet in the mod on one PC only,
    /// and HasJobOnCell reads that set in the sim. The lambda runs on every PC (relative toggle on the same data).
    /// SetAllowed is NOT synced as a method: the mod's ExposeData postfix also calls it on save and load.</summary>
    [MpCompatFor("jaxe.toggleharvest")]
    public class ToggleHarvestFull1127
    {
        public ToggleHarvestFull1127(ModContentPack mod)
        {
            MpCompat.RegisterLambdaDelegate("ToggleHarvest.Patch.RimWorld_Zone_Growing_GetGizmos", "Postfix", 1);
            MpCompat.RegisterLambdaDelegate("ToggleHarvest.Patch.RimWorld_Building_PlantGrower_GetGizmos", "Postfix", 1);
            Log.Message("[MPCompat - FULL1127 L1] Toggle Harvest: zone and plant grower toggle lambdas registered");
        }
    }

    /// <summary>Shuttle Dock (salvador143.shuttledock). Both roof gizmos call ToggleRoof, which flips the saved roofOpen.</summary>
    [MpCompatFor("salvador143.shuttledock")]
    public class ShuttleDockFull1127
    {
        public ShuttleDockFull1127(ModContentPack mod)
        {
            MP.RegisterSyncMethod(AccessTools.TypeByName("MThings.ShuttleDock.Building_ShuttleDock"), "ToggleRoof");
            Log.Message("[MPCompat - FULL1127 L1] Shuttle Dock: ToggleRoof registered");
        }
    }

    /// <summary>Follow Target (chaoticenrico.followtarget). followAction / stopAction are local functions of the float menu provider.</summary>
    [MpCompatFor("chaoticenrico.followtarget")]
    public class FollowTargetFull1127
    {
        public FollowTargetFull1127(ModContentPack mod)
        {
            var type = AccessTools.TypeByName("FollowTarget.FloatMenuOptionProvider_Follow");
            foreach (var name in new[] { "followAction", "stopAction" })
            {
                var method = MpMethodUtil.GetLocalFunc(type, "GetOptionsFor", localFunc: name);
                MP.RegisterSyncDelegate(type, method.DeclaringType.Name, method.Name);
            }
            Log.Message("[MPCompat - FULL1127 L1] Follow Target: followAction and stopAction registered");
        }
    }

    /// <summary>Power Poles (co.uk.epicguru.rimforgepoles). Link edits from the gizmos and the float menu run on one PC only.
    /// Synced as methods: they are only issued from the UI. Calls from the tick or from inside a synced command are not re-synced by Multiplayer.
    /// Not covered: cable colour / slack visuals.</summary>
    [MpCompatFor("co.uk.epicguru.rimforgepoles")]
    public class PowerPolesFull1127
    {
        public PowerPolesFull1127(ModContentPack mod)
        {
            var type = AccessTools.TypeByName("RimForge.Buildings.Building_LongDistancePower");
            MP.RegisterSyncMethod(type, "TryAddLink");
            MP.RegisterSyncMethod(type, "TryRemoveLink");
            MP.RegisterSyncMethod(type, "DisconnectAll");
            Log.Message("[MPCompat - FULL1127 L1] Power Poles: TryAddLink, TryRemoveLink, DisconnectAll registered");
        }
    }

    /// <summary>Multi-Reinstall (oels.multireinstall). Designator_MultiReinstall keeps the buildings and target cells in per-PC
    /// fields filled by the local mouse, so a replayed DesignateSingleCell on the other PC has empty lists. The placement is
    /// moved into one synced static method that gets the buildings, cells and rotations as arguments.</summary>
    [MpCompatFor("oels.multireinstall")]
    public class MultiReinstallFull1127
    {
        private static AccessTools.FieldRef<object, List<Building>> cachedBuildings;
        private static AccessTools.FieldRef<object, List<IntVec3>> offsetPositions;
        private static AccessTools.FieldRef<object, List<Rot4>> flippedRotations;
        private static FastInvokeHandler place;

        public MultiReinstallFull1127(ModContentPack mod)
        {
            var type = AccessTools.TypeByName("MultiReinstall.Designator_MultiReinstall");
            cachedBuildings = AccessTools.FieldRefAccess<List<Building>>(type, "cachedBuildings");
            offsetPositions = AccessTools.FieldRefAccess<List<IntVec3>>(type, "offsetPositions");
            flippedRotations = AccessTools.FieldRefAccess<List<Rot4>>(type, "flippedRotations");
            place = MethodInvoker.GetHandler(AccessTools.Method("MultiReinstall.Multi_GenConstruct:PlaceBlueprintForReinstall"));

            MP.RegisterSyncMethod(typeof(MultiReinstallFull1127), nameof(SyncedPlace));
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(type, "DesignateSingleCell"),
                prefix: new HarmonyMethod(typeof(MultiReinstallFull1127), nameof(PreDesignateSingleCell)) { priority = Priority.First });
            Log.Message("[MPCompat - FULL1127 L1] Multi-Reinstall: DesignateSingleCell placement synced");
        }

        private static bool PreDesignateSingleCell(Designator __instance)
        {
            if (!MP.IsInMultiplayer) return true;
            var buildings = cachedBuildings(__instance);
            // A replayed designator on another PC has no cached buildings: the synced placement below does the work.
            if (buildings == null || buildings.Count == 0 || MP.IsExecutingSyncCommand) return false;
            SyncedPlace(buildings.ToList(), offsetPositions(__instance).ToList(), flippedRotations(__instance).ToList(), __instance.Map, Faction.OfPlayer);
            Find.DesignatorManager.Deselect();
            return false;
        }

        [SyncMethod]
        private static void SyncedPlace(List<Building> buildings, List<IntVec3> positions, List<Rot4> rotations, Map map, Faction faction)
        {
            for (int i = 0; i < buildings.Count; i++)
            {
                place(null, buildings[i], positions[i], map, rotations[i], faction, true);
                if (ModsConfig.IsActive("erdelf.MinifyEverything") && ModsConfig.IsActive("Mlie.SmarterDeconstructionAndMining") && buildings[i].def.IsEdifice())
                    map.designationManager.AddDesignation(new Designation(buildings[i], DesignationDefOf.Haul));
            }
        }
    }

    /// <summary>Giddy-Up 2 - Continued fork (memegoddess.giddyup). The existing GiddyUp2 module is for Owlchemist.GiddyUp and would throw on
    /// this fork (its types sit in other namespaces), so this is a separate guarded module. Covers the forced mount / dismount float menu
    /// lambdas (4) and the release-animal gizmo lambda. Not covered: caravan rider widget, PawnColumnWorker, ExtendedPawnData sync worker.</summary>
    [MpCompatFor("memegoddess.giddyup")]
    public class GiddyUpForkFull1127
    {
        public GiddyUpForkFull1127(ModContentPack mod)
        {
            Try(() => MpCompat.RegisterLambdaDelegate("GiddyUp.Harmony.FloatMenuUtility", "AddMountingOptions", 0, 1, 2, 3), "FloatMenuUtility lambdas 0-3");
            Try(() => MpCompat.RegisterLambdaDelegate("GiddyUp.Harmony.Patch_PawnGetGizmos", "Postfix", 0), "Patch_PawnGetGizmos lambda 0");
            Try(() => MP.RegisterSyncMethod(AccessTools.TypeByName("GiddyUpCore.RideAndRoll.Harmony.Pawn_GetGizmos"), "PawnEndCurrentJob"), "Pawn_GetGizmos.PawnEndCurrentJob");
            Log.Message("[MPCompat - FULL1127 L1] Giddy-Up 2 fork: float menu mount lambdas and release gizmo registered");
        }

        private static void Try(Action a, string what)
        {
            try { a(); }
            catch (Exception e) { Log.Warning("[MPCompat - FULL1127 L1] Giddy-Up 2 fork: " + what + " not registered: " + e.Message); }
        }
    }
}
