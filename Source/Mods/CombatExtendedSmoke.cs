using System.Collections;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Combat Extended - black smoke. Proposed r32_probe (16 Oct 2026), lab-found desync source.
    ///
    /// CombatExtended.BlackSmokeTracker.MapComponentTick runs Parallel.ForEach(_smoke, smoke => smoke.ParallelTick()) and
    /// Smoke.ParallelTick -> CalcSpreadToAdjacentCells shuffles the four cardinal directions with GenCollection.InRandomOrder,
    /// i.e. Rand.Int calls made on thread-pool worker threads while the map's synced random state is the active one.
    /// Rand is a static, non-thread-safe state: how the worker threads interleave decides which draw each smoke gets, so the
    /// density transfers that follow (and the gas that DoSpreadToAdjacentCells spawns, each spawn drawing too) differ between two
    /// instances that ran the same tick. Measured in lab run mp41: map 1, map tick 10953, host 123 main-thread + 24 worker
    /// draws, arbiter 126 + 28 (the arbiter spawned one more Gas_BlackSmoke); the map random state split there, the world state
    /// followed 1,550 ticks later (VanillaTraitsExpanded.TraitsManager.TryBreakChairsUnderBigBoneds drew for a pawn that had
    /// moved on one side only).
    ///
    /// Fix: in multiplayer run the same two loops on the ticking thread, in list order (the list is filled by Register/Unregister,
    /// which happen in the simulation, so the order is the same on every instance).</summary>
    [MpCompatFor("CETeam.CombatExtended")]
    internal class CombatExtendedSmoke
    {
        private static FieldInfo smokeListField;
        private static MethodInfo parallelTickMethod, doSpreadMethod;

        public CombatExtendedSmoke(ModContentPack mod)
        {
            var tracker = AccessTools.TypeByName("CombatExtended.BlackSmokeTracker");
            var smoke = AccessTools.TypeByName("CombatExtended.Smoke");
            smokeListField = AccessTools.DeclaredField(tracker, "_smoke");
            parallelTickMethod = AccessTools.DeclaredMethod(smoke, "ParallelTick");
            doSpreadMethod = AccessTools.DeclaredMethod(smoke, "DoSpreadToAdjacentCells");

            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(tracker, "MapComponentTick"),
                prefix: new HarmonyMethod(typeof(CombatExtendedSmoke), nameof(PreMapComponentTick)));
        }

        private static bool PreMapComponentTick(object __instance)
        {
            if (!MP.IsInMultiplayer)
                return true;

            var list = (IList)smokeListField.GetValue(__instance);

            // Same body as the original, minus the Parallel.ForEach: smoke.ParallelTick() on the ticking thread, in list order.
            for (int i = 0; i < list.Count; i++)
                parallelTickMethod.Invoke(list[i], null);

            for (int i = 0; i < list.Count; i++)
                doSpreadMethod.Invoke(list[i], null);

            return false;
        }
    }
}
