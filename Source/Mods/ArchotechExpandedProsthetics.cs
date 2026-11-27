using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Archotech Expanded Prosthetics by teok25</summary>
    [MpCompatFor("teok25.archotechexpanded")]
    internal class ArchotechExpandedProsthetics
    {
        static readonly FieldInfo bleedRateField = AccessTools.Field(typeof(BodyPartDef), nameof(BodyPartDef.bleedRate));

        public ArchotechExpandedProsthetics(ModContentPack mod)
        {
            // row 509: RegenerationMatrix.Tick does "def.bleedRate -= regeneration * 2f" on the shared BodyPartDef every tick
            // for each bleeding injury; the Def drifts on a running PC and starts at the XML value on a PC that loads.
            if (LabOff.Row(509)) { Log.Message("[MPCompat - Archotech Expanded Prosthetics] row 509 off (lab switch)"); return; }
            var tick = AccessTools.DeclaredMethod("ArchotechExpandedProsthetics.RegenerationMatrix:Tick");
            if (tick == null) { Log.Warning("[MPCompat - Archotech Expanded Prosthetics] row 509: RegenerationMatrix.Tick not found"); return; }
            MpCompat.harmony.Patch(tick, transpiler: new HarmonyMethod(typeof(ArchotechExpandedProsthetics), nameof(RemoveDefWrite)));
            Log.Message("[MPCompat - Archotech Expanded Prosthetics] row 509: Archotech regeneration matrix Def write removed");
        }

        // stfld BodyPartDef::bleedRate (stack: def, value) -> pop, pop. The injury severity regeneration is kept.
        // The bleed-rate bonus is dropped: it was a cumulative, unbounded Def write, with no per-pawn equivalent.
        internal static IEnumerable<CodeInstruction> RemoveDefWrite(IEnumerable<CodeInstruction> instr)
        {
            foreach (var ci in instr)
            {
                if (ci.opcode == OpCodes.Stfld && ci.operand is FieldInfo f && f == bleedRateField)
                {
                    yield return new CodeInstruction(OpCodes.Pop) { labels = ci.labels, blocks = ci.blocks };
                    yield return new CodeInstruction(OpCodes.Pop);
                }
                else yield return ci;
            }
        }
    }
}
