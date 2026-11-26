using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Centralized Climate Control by Mlie (and Aelanna)</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=2007268173"/>
    /// <remarks>
    /// FIXES row 476 (26 Nov 2026): the air priority gizmo (Command_Action in CentralizedClimateControlUtility.GetPipeSwitchToggle)
    /// calls CompAirFlowConsumer.SetPriority on the clicking PC only. SetPriority writes AirTypePriority (saved, read by
    /// validateBuildingPriority when the air net is rebuilt in the tick) and clears the air flow net. The gizmo is the only caller,
    /// so the method itself is synced.
    /// </remarks>
    [MpCompatFor("Mlie.CentralizedClimateControl")]
    public class ClimateControl
    {
        public ClimateControl(ModContentPack mod)
        {
            if (LabOff.Row(476))
            {
                Log.Message("[MPCompat - CentralizedClimateControl] row 476 switched off by the lab, air priority gizmo not synced");
                return;
            }
            PatchRow476();
        }

        private static void PatchRow476()
        {
            var method = AccessTools.DeclaredMethod(AccessTools.TypeByName("CentralizedClimateControl.CompAirFlowConsumer"), "SetPriority");
            if (method == null || method.IsStatic || method.GetParameters().Length != 1)
            {
                Log.Error("[MPCompat - CentralizedClimateControl] row 476: CompAirFlowConsumer.SetPriority not found, the air priority gizmo can desync");
                return;
            }
            MP.RegisterSyncMethod(method);
            Log.Message("[MPCompat - CentralizedClimateControl] row 476: CompAirFlowConsumer.SetPriority synced");
        }
    }
}
