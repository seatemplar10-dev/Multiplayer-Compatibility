using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Alpha Bees (RimBees) by Sarg Bjornson, Ben Lubar</summary>
    /// <remarks>
    /// 1.7 lab, FIXES row 145 (audit d143 colony rows 33, 34): the "Extract bees" and "Extract queens" gizmos of
    /// <c>RimBees.Building_Beehouse</c> call <c>EjectContents</c> / <c>EjectContentsQueens</c> on the clicking PC only
    /// (TryDropAll of the held drones/queens at the interaction cell, <c>contentsKnown</c>, <c>tickCounter = 0</c>, a manual
    /// <c>TickRare</c>). The class is a plain <c>Building</c>, not <c>Building_Casket</c>, so MP's synced
    /// <c>Building_Casket.EjectContents</c> does not cover it. The click lambdas are synced rather than the virtual
    /// Eject methods so only the two clicks are touched (<c>Destroy</c> also calls the Eject methods).
    /// Nothing in the two bodies reads per-PC state; <c>Map</c> and <c>InteractionCell</c> are the building's own.
    ///
    /// 1.7 lab, FIXES row 145 (lead from the same audit): the "Insert bees" / "Insert queens" gizmos
    /// (<c>Command_SetBeeList</c> / <c>Command_SetQueenList</c>) open a float menu; the chosen entry runs
    /// <c>TryInsertDrone</c> / <c>TryInsertQueen</c>, which write the scribed <c>BeehouseIsExpectingBees</c> /
    /// <c>BeehouseIsExpectingQueens</c> and <c>theDroneIAmGoingToInsert</c> / <c>theQueenIAmGoingToInsert</c> on the beehouse
    /// (read by WorkGiver_InsertDroneInBeehouse / WorkGiver_InsertQueenInBeehouse). The command is a UI object (the picked
    /// <c>drone</c> / <c>queen</c> Thing is chosen with a UI-side RandomElement), so the setter is replaced by a synced static
    /// method taking the beehouse and the chosen def name; only the def name is ever stored, never the picked Thing.
    /// </remarks>
    [MpCompatFor("sarg.rimbees")]
    internal class RimBeesCompat
    {
        private static System.Type beehouseType;
        private static FieldInfo droneCmdBeehouse, droneCmdDrone, queenCmdBeehouse, queenCmdQueen;
        private static FieldInfo expectingBees, expectingQueens, droneToInsert, queenToInsert;

        public RimBeesCompat(ModContentPack mod)
        {
            var type = AccessTools.TypeByName("RimBees.Building_Beehouse");
            beehouseType = type;

            // 1.7 lab, FIXES row 145: 'Extract bees' = <GetGizmos>b__34_1 (EjectContents), 'Extract queens' = <GetGizmos>b__34_3 (EjectContentsQueens).
            // b__34_0 / b__34_2 are the empty 'waiting for bees' actions (in <>c), b__34_4 is the dev-mode 'Finish operation'.
            MpCompat.RegisterLambdaMethod(type, "GetGizmos", 1, 3);

            // 1.7 lab, FIXES row 145: 'Insert bees' / 'Insert queens' choice. The float-menu lambda calls the private
            // Command_SetBeeList.TryInsertDrone() / Command_SetQueenList.TryInsertQueen(); a prefix hands the chosen def name to a synced static.
            expectingBees = AccessTools.Field(type, "BeehouseIsExpectingBees");
            expectingQueens = AccessTools.Field(type, "BeehouseIsExpectingQueens");
            droneToInsert = AccessTools.Field(type, "theDroneIAmGoingToInsert");
            queenToInsert = AccessTools.Field(type, "theQueenIAmGoingToInsert");

            var droneCmd = AccessTools.TypeByName("RimBees.Command_SetBeeList");
            droneCmdBeehouse = AccessTools.Field(droneCmd, "beehouse");
            droneCmdDrone = AccessTools.Field(droneCmd, "drone");
            var queenCmd = AccessTools.TypeByName("RimBees.Command_SetQueenList");
            queenCmdBeehouse = AccessTools.Field(queenCmd, "beehouse");
            queenCmdQueen = AccessTools.Field(queenCmd, "queen");

            MP.RegisterSyncMethod(typeof(RimBeesCompat), nameof(SyncedInsertDrone)).CancelIfAnyArgNull();
            MP.RegisterSyncMethod(typeof(RimBeesCompat), nameof(SyncedInsertQueen)).CancelIfAnyArgNull();

            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(droneCmd, "TryInsertDrone"),
                prefix: new HarmonyMethod(typeof(RimBeesCompat), nameof(PreTryInsertDrone)));
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(queenCmd, "TryInsertQueen"),
                prefix: new HarmonyMethod(typeof(RimBeesCompat), nameof(PreTryInsertQueen)));
        }

        private static bool PreTryInsertDrone(object __instance)
        {
            if (!MP.IsInMultiplayer) return true;

            var beehouse = droneCmdBeehouse.GetValue(__instance) as Thing;
            var picked = droneCmdDrone.GetValue(__instance) as Thing;
            if (beehouse == null || picked?.def == null) return true; // let the original run (and fail) exactly as before

            SyncedInsertDrone(beehouse, picked.def.defName);
            return false;
        }

        private static bool PreTryInsertQueen(object __instance)
        {
            if (!MP.IsInMultiplayer) return true;

            var beehouse = queenCmdBeehouse.GetValue(__instance) as Thing;
            var picked = queenCmdQueen.GetValue(__instance) as Thing;
            if (beehouse == null || picked?.def == null) return true;

            SyncedInsertQueen(beehouse, picked.def.defName);
            return false;
        }

        // Same two writes as Command_SetBeeList.TryInsertDrone.
        private static void SyncedInsertDrone(Thing beehouse, string defName)
        {
            if (!beehouseType.IsInstanceOfType(beehouse)) return;

            expectingBees.SetValue(beehouse, true);
            droneToInsert.SetValue(beehouse, defName);
        }

        // Same two writes as Command_SetQueenList.TryInsertQueen.
        private static void SyncedInsertQueen(Thing beehouse, string defName)
        {
            if (!beehouseType.IsInstanceOfType(beehouse)) return;

            expectingQueens.SetValue(beehouse, true);
            queenToInsert.SetValue(beehouse, defName);
        }
    }
}
