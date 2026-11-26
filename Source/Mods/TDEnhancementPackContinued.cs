using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using Verse;
using Verse.AI;
using RimWorld;
using Multiplayer.API;


namespace Multiplayer.Compat
{
    /// <summary>TD Enhancement Pack - Continued</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3525414162/"/>
    /// <see href="https://github.com/MemeGoddess/RimWorld-EnhancementPack"/>
    [MpCompatFor("memegoddess.tdpack")]
    class TD_Enhancement_Pack
    {
        internal static bool queuePatched;

        public TD_Enhancement_Pack(ModContentPack mod)
        {
            /// Attempt to sync "Allow Harvest" Gizmo
            /// really just copied from other patches until it works X.x
            Type allowHarvest = AccessTools.TypeByName("TD_Enhancement_Pack.Zone_Growing_Extensions");
            MP.RegisterSyncMethod(allowHarvest, "ToggleHarvest");
            /// Add Building Gizmo sync
            Type allowHarvestBuilding = AccessTools.TypeByName("TD_Enhancement_Pack.Building_PlantGrower_Extensions");
            MP.RegisterSyncMethod(allowHarvestBuilding, "ToggleHarvest");

            // FIXES row 459 (26 Nov 2026): TD's QueueJobToFront transpiler makes Pawn_JobTracker.TryTakeOrderedJob call
            // EnqueueLastOrFirst. That reads this PC's Ctrl key (ModifierIncrement_10x.IsDown) while the synced order runs on
            // every PC, so a Ctrl+Shift order goes to the front of the queue on one PC and to the back on the other.
            // In Multiplayer the order always goes to the back, the same on every PC. Single player keeps the feature.
            if (LabOff.Row(459))
            {
                Log.Message("[MPCompat - TD Enhancement Pack] row 459 switched off by the lab, queue patch not installed");
                return;
            }
            var enqueue = AccessTools.Method("TD_Enhancement_Pack.QueueJobToFront:EnqueueLastOrFirst");
            if (enqueue == null)
            {
                Log.Error("[MPCompat - TD Enhancement Pack] row 459: QueueJobToFront.EnqueueLastOrFirst not found, Ctrl+Shift orders can desync");
                return;
            }
            MpCompat.harmony.Patch(enqueue, prefix: new HarmonyMethod(typeof(TD_Enhancement_Pack), nameof(PreEnqueueLastOrFirst)));
            queuePatched = true;
            Log.Message("[MPCompat - TD Enhancement Pack] row 459: Ctrl+Shift queue-to-front set to queue-last in Multiplayer");
        }

        internal static bool PreEnqueueLastOrFirst(JobQueue queue, Job j, JobTag? tag)
        {
            if (!MP.IsInMultiplayer) return true;
            queue.EnqueueLast(j, tag);
            return false;
        }
    }
}
