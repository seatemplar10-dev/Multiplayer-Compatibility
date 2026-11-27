using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Hive Lord Lib: FULL1127 class H sweep table (13 rows, helper PatchPushPopRand). Rand drawn on visual-only paths that differ per client.</summary>
    [MpCompatFor("chezhou.creature.hivelord")]
    class FullHiveLordLib
    {
        private static readonly string[] Methods =
        {
            "HiveLordLib.HiveLordCombatEffects:SpawnEmergenceShockwave",
            "HiveLordLib.HiveLordCombatEffects:TickEmergenceDebris",
            "HiveLordLib.HiveLordCombatEffects:SpawnSubmergingDebris",
            "HiveLordLib.HiveLordCombatEffects:SpawnSoilSmoke",
            "HiveLordLib.HiveLordCombatEffects:SpawnSoilDustPuff",
            "HiveLordLib.HiveLordCombatEffects:SpawnWaveFront",
            "HiveLordLib.HiveLordCombatEffects:SpawnDeathImpact",
            "HiveLordLib.HiveLordCombatEffects:SpawnSeismicPulse",
            "HiveLordLib.HiveLordSubmergingClouds:Spawn",
            "HiveLordLib.SporeSpewer.SporeVisualEffects:EmitSource",
            "HiveLordLib.SporeSpewer.SporeVisualEffects:EmitGroundMist",
            "HiveLordLib.SporeSpewer.SporeVisualEffects:CollapseImpact",
            "HiveLordLib.SporeSpewer.SporeVisualEffects:Burst"
        };

        public FullHiveLordLib(ModContentPack mod)
        {
            var found = new List<MethodBase>();
            foreach (var method in Methods)
            {
                var m = AccessTools.DeclaredMethod(method) ?? AccessTools.Method(method);
                if (m == null) Log.Error($"[MPCompat - FULL1127 H] Hive Lord Lib: method {method} not found");
                else found.Add(m);
            }
            PatchingUtilities.PatchPushPopRand(found);
            Log.Message($"[MPCompat - FULL1127 H] Hive Lord Lib: {found.Count} of {Methods.Length} methods patched");
        }
    }
}
