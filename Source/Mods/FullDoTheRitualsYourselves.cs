using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Do The Rituals Yourselves: FULL1127 class J3 sweep table (1 rows, helper PatchUnityRand). Rand drawn on visual-only paths that differ per client.</summary>
    [MpCompatFor("woolyung.dotheritualsyourselves")]
    class FullDoTheRitualsYourselves
    {
        private static readonly string[] Methods =
        {
            "DoTheRitualsYourselves.WorldComponents.WorldComponent_AutoRituals:WorldComponentTick"
        };

        public FullDoTheRitualsYourselves(ModContentPack mod)
        {
            var found = new List<MethodBase>();
            foreach (var method in Methods)
            {
                var m = AccessTools.DeclaredMethod(method) ?? AccessTools.Method(method);
                if (m == null) Log.Error($"[MPCompat - FULL1127 J3] Do The Rituals Yourselves: method {method} not found");
                else found.Add(m);
            }
            PatchingUtilities.PatchUnityRand(found);
            Log.Message($"[MPCompat - FULL1127 J3] Do The Rituals Yourselves: {found.Count} of {Methods.Length} methods patched");
        }
    }
}
