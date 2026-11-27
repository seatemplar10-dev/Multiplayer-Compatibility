using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Toxin Warcasket: FULL1127 class H sweep table (2 rows, helper PatchPushPopRand). Rand drawn on visual-only paths that differ per client.</summary>
    [MpCompatFor("shunter.toxinwarcasket")]
    class FullToxinWarcasket
    {
        private static readonly string[] Methods =
        {
            "ToxinWarcasket.Ability_AbsorbGas:SpawnPuffs",
            "ToxinWarcasket.Ability_AbsorbGas:SpawnGasWisp"
        };

        public FullToxinWarcasket(ModContentPack mod)
        {
            var found = new List<MethodBase>();
            foreach (var method in Methods)
            {
                var m = AccessTools.DeclaredMethod(method) ?? AccessTools.Method(method);
                if (m == null) Log.Error($"[MPCompat - FULL1127 H] Toxin Warcasket: method {method} not found");
                else found.Add(m);
            }
            PatchingUtilities.PatchPushPopRand(found);
            Log.Message($"[MPCompat - FULL1127 H] Toxin Warcasket: {found.Count} of {Methods.Length} methods patched");
        }
    }
}
