using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using Verse;
using RimWorld;

namespace Multiplayer.Compat
{
    /// <summary>Replace Stuff by Memegoddess: bridge cancel ignores the selection in Multiplayer (FIXES row 463)</summary>
    /// <remarks>
    /// PlaceBridges.CancelAboveBridges.CancelAbove (run from the Frame.Destroy / Blueprint.DeSpawn patches, in the simulation) destroys
    /// the blueprints and frames above a new bridge with a lambda (&lt;CancelAbove&gt;b__0_0):
    /// if (!t.Destroyed &amp;&amp; !Find.Selector.IsSelected(t)) t.Destroy(Cancel). Selection is per PC, so the blueprint that one PC has
    /// selected survives on that PC only. A transpiler on the lambda replaces the Selector.IsSelected call with a helper that returns
    /// false in Multiplayer, so every PC destroys the same things. Single player keeps the original call.
    /// Status: [unverified] compiled only, not run in game.
    /// </remarks>
    [MpCompatFor("Memegoddess.ReplaceStuff")]
    internal class ReplaceStuff
    {
        private const string LogPrefix = "[MPCompat - Replace Stuff] ";
        private static readonly MethodInfo isSelected = AccessTools.Method(typeof(Selector), nameof(Selector.IsSelected), [typeof(object)]);

        public ReplaceStuff(ModContentPack mod)
        {
            if (LabOff.Row(463))
            {
                Log.Message(LogPrefix + "row 463 switched off by the lab, bridge cancel still reads the selection");
                return;
            }
            PatchCancelAbove();
        }

        private static void PatchCancelAbove()
        {
            var type = AccessTools.TypeByName("Replace_Stuff.PlaceBridges.CancelAboveBridges");
            var closure = type == null ? null : AccessTools.Inner(type, "<>c");
            var lambda = closure == null ? null : AccessTools.Method(closure, "<CancelAbove>b__0_0");
            if (lambda == null || isSelected == null)
            {
                Log.Error(LogPrefix + "row 463: CancelAboveBridges.<CancelAbove>b__0_0 not found, the bridge cancel can desync");
                return;
            }
            MpCompat.harmony.Patch(lambda, transpiler: new HarmonyMethod(typeof(ReplaceStuff), nameof(IgnoreSelection)));
            Log.Message(LogPrefix + "row 463: bridge cancel ignores the selection in Multiplayer");
        }

        internal static IEnumerable<CodeInstruction> IgnoreSelection(IEnumerable<CodeInstruction> instr)
        {
            var helper = AccessTools.Method(typeof(ReplaceStuff), nameof(IsSelectedSynced));
            var found = false;
            foreach (var ci in instr)
            {
                if (ci.Calls(isSelected))
                {
                    ci.opcode = System.Reflection.Emit.OpCodes.Call;
                    ci.operand = helper;
                    found = true;
                }
                yield return ci;
            }
            if (!found) Log.Error(LogPrefix + "row 463: Selector.IsSelected call not found in the lambda, the bridge cancel can desync");
        }

        internal static bool IsSelectedSynced(Selector selector, object obj) => !MP.IsInMultiplayer && selector.IsSelected(obj);
    }
}
