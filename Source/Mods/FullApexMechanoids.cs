using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Apex Mechanoids: FULL1127 class H sweep table (8 rows, helper PatchPushPopRand). Rand drawn on visual-only paths that differ per client.</summary>
    [MpCompatFor("mortis.apexmechanoids")]
    class FullApexMechanoids
    {
        private static readonly string[] Methods =
        {
            "ApexMechanoids.Mote_PulseWaveEmitter:SpawnRingFlecks",
            "ApexMechanoids.Mote_PulseWaveEmitter:ApplyWaveToPawn",
            "ApexMechanoids.CompAbility_ShockwaveWarmup:SpawnChargeFlecks",
            "ApexMechanoids.Mote_ShockwaveEmitter:EmitRing",
            "ApexMechanoids.TerminusOverdriveCapeUtility:SpawnBurst",
            "ApexMechanoids.CompToxicPurifier:ThrowToxicAirPuffUp",
            "ApexMechanoids.Mote_ShockwaveBlastWave:EmitElectricalPulseFront",
            "ApexMechanoids.CompAbility_SpawnSirenSongNotes:CompTick"
        };

        public FullApexMechanoids(ModContentPack mod)
        {
            var found = new List<MethodBase>();
            foreach (var method in Methods)
            {
                var m = AccessTools.DeclaredMethod(method) ?? AccessTools.Method(method);
                if (m == null) Log.Error($"[MPCompat - FULL1127 H] Apex Mechanoids: method {method} not found");
                else found.Add(m);
            }
            PatchingUtilities.PatchPushPopRand(found);
            Log.Message($"[MPCompat - FULL1127 H] Apex Mechanoids: {found.Count} of {Methods.Length} methods patched");
        }
    }
}
