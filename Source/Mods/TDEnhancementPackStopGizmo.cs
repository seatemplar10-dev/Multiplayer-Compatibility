using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>TD Enhancement Pack - Continued, 'Stop' gizmo</summary>
    /// <remarks>
    /// FIXES row 502: StopGizmo.Postfix adds a Command_Action whose lambda calls jobs.StopAll(false, true) on every selected pawn
    /// on the clicking PC only. In Multiplayer the click sends one synced call with the selected pawns.
    /// </remarks>
    [MpCompatFor("memegoddess.tdpack")]
    class TDEnhancementPackStopGizmo
    {
        public TDEnhancementPackStopGizmo(ModContentPack mod)
        {
            if (LabOff.Row(502))
            {
                Log.Message("[MPCompat - TD Enhancement Pack] row 502 switched off by the lab, Stop gizmo not synced");
                return;
            }
            var type = AccessTools.TypeByName("TD_Enhancement_Pack.StopGizmo");
            MethodInfo lambda = null;
            if (type != null)
                foreach (var nested in type.GetNestedTypes(AccessTools.all))
                {
                    lambda = nested.GetMethods(AccessTools.all).FirstOrDefault(m => m.Name.StartsWith("<Postfix>b__") && m.GetParameters().Length == 0 && m.ReturnType == typeof(void));
                    if (lambda != null) break;
                }
            if (lambda == null)
            {
                Log.Error("[MPCompat - TD Enhancement Pack] row 502: Stop gizmo lambda not found, the button can desync");
                return;
            }
            MpCompat.harmony.Patch(lambda, prefix: new HarmonyMethod(typeof(TDEnhancementPackStopGizmo), nameof(PrefixStop)));
            MP.RegisterSyncMethod(typeof(TDEnhancementPackStopGizmo), nameof(SyncedStopAll)).CancelIfAnyArgNull();
            Log.Message("[MPCompat - TD Enhancement Pack] row 502: TD Stop gizmo synced");
        }

        static bool PrefixStop()
        {
            if (!MP.IsInMultiplayer) return true;
            var pawns = Find.Selector.SelectedObjects.OfType<Pawn>().ToList();
            if (pawns.Count > 0) SyncedStopAll(pawns);
            return false;
        }

        // Runs on every PC.
        static void SyncedStopAll(List<Pawn> pawns)
        {
            foreach (var pawn in pawns)
                pawn?.jobs?.StopAll(false, true);
        }
    }
}
