using System;
using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Progression: Waste by Ferny</summary>
    /// <remarks>
    /// FIXES rows 531 and 536 (27 Nov 2026).
    /// Row 531: the Garbage Truck module (loads with Vanilla Vehicles Expanded) gives the truck a Compact gizmo, a plain Command_Action whose action is
    /// CompTruckCompactor.Compact. It destroys the trash in the truck and makes bricks on the clicking PC only. Fix: Compact is a synced method.
    /// </remarks>
    [MpCompatFor("ferny.progressionwaste")]
    public class ProgressionWaste
    {
        private const string LogPrefix = "[MPCompat - Progression Waste] ";

        public ProgressionWaste(ModContentPack mod)
        {
            if (LabOff.Row(531))
                Log.Message(LogPrefix + "row 531 switched off by the lab, the Compact gizmo stays local to the clicking PC");
            else
                PatchCompact();
        }

        private static void PatchCompact()
        {
            var compact = AccessTools.DeclaredMethod("GarbageTruckCompactor.CompTruckCompactor:Compact");
            if (compact == null)
            {
                // The module loads only with Vanilla Vehicles Expanded.
                Log.Message(LogPrefix + "row 531: GarbageTruckCompactor.CompTruckCompactor not loaded (no Vanilla Vehicles Expanded?), nothing to sync");
                return;
            }
            MP.RegisterSyncMethod(compact);
            Log.Message(LogPrefix + "row 531: CompTruckCompactor.Compact is a synced method");
        }
    }
}
