using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Vanilla Quests Expanded - Ancients by Oskar Potocki, Sarg Bjornson, Taranchuk</summary>
    /// <remarks>
    /// FIXES row 500: Building_AncientWonderdoc.GetGizmos cycle buttons write selectedCycleIndex on the clicking PC only (the
    /// simulation reads it in StartProcessing and FinishProcess), and the Cancel / DEV buttons of Building_PawnProcessor and
    /// Building_ArchogenInjector act on the clicking PC only. Insert pawn is already synced by Multiplayer (Building_Enterable.SelectPawn).
    /// </remarks>
    [MpCompatFor("vanillaquestsexpanded.ancients")]
    class VanillaQuestsExpandedAncients
    {
        const string NS = "VanillaQuestsExpandedAncients.";
        static FieldInfo cycleIndexField, compField, thisField, cycleField;

        public VanillaQuestsExpandedAncients(ModContentPack mod)
        {
            if (LabOff.Row(500))
            {
                Log.Message("[MPCompat - VQE Ancients] row 500 switched off by the lab, pawn processor gizmos not synced");
                return;
            }
            // Cancel (ordinal 0, <GetPawnProcessorGizmos>b__29_0) calls the virtual CancelProcess, so the Archogen override is covered.
            MpCompat.RegisterLambdaMethod(NS + "Building_PawnProcessor", "GetPawnProcessorGizmos", 0);
            MpCompat.RegisterLambdaMethod(NS + "Building_PawnProcessor", "GetPawnProcessorGizmos", 1).SetDebugOnly();   // DEV: Complete
            MpCompat.RegisterLambdaMethod(NS + "Building_ArchogenInjector", "GetGizmos", 0).SetDebugOnly();            // DEV: Disable Ingredients
            PatchCycle();
            Log.Message("[MPCompat - VQE Ancients] row 500: VQE Ancients pawn processor gizmos synced");
        }

        static void PatchCycle()
        {
            var type = AccessTools.TypeByName(NS + "Building_AncientWonderdoc");
            cycleIndexField = type == null ? null : AccessTools.Field(type, "selectedCycleIndex");
            compField = type == null ? null : AccessTools.Field(type, "comp");
            // the cycle button lambda: an instance method of a display class that holds the loop variable "cycle" and <>4__this
            MethodInfo lambda = null;
            if (type != null)
                foreach (var nested in type.GetNestedTypes(AccessTools.all))
                {
                    var cf = nested.GetFields(AccessTools.all).FirstOrDefault(f => f.FieldType.Name == "WonderdocCycle");
                    var tf = nested.GetField("<>4__this", AccessTools.all);
                    var m = nested.GetMethods(AccessTools.all).FirstOrDefault(x => x.Name.StartsWith("<GetGizmos>b__") && !x.IsStatic && x.GetParameters().Length == 0);
                    if (cf != null && tf != null && m != null) { cycleField = cf; thisField = tf; lambda = m; break; }
                }
            if (lambda == null || cycleIndexField == null || compField == null)
            {
                Log.Error("[MPCompat - VQE Ancients] row 500: Wonderdoc cycle button lambda not found, the button can desync");
                return;
            }
            MpCompat.harmony.Patch(lambda, prefix: new HarmonyMethod(typeof(VanillaQuestsExpandedAncients), nameof(PrefixCycle)));
            MP.RegisterSyncMethod(typeof(VanillaQuestsExpandedAncients), nameof(SyncedSelectCycle)).CancelIfAnyArgNull();
        }

        static bool PrefixCycle(object __instance)
        {
            if (!MP.IsInMultiplayer) return true;
            var building = thisField.GetValue(__instance) as Thing;
            var cycle = cycleField.GetValue(__instance);
            var comp = compField.GetValue(building);
            var props = comp?.GetType().GetProperty("Props")?.GetValue(comp);
            var cycles = props?.GetType().GetField("cycles")?.GetValue(props) as IList;
            int index = cycles == null ? -1 : cycles.IndexOf(cycle);
            if (building != null && index >= 0) SyncedSelectCycle(building, index);
            return false;
        }

        // Runs on every PC. The first command wins: a later one finds a cycle already selected.
        static void SyncedSelectCycle(Thing building, int index)
        {
            if ((int)cycleIndexField.GetValue(building) >= 0) return;
            cycleIndexField.SetValue(building, index);
        }
    }
}
