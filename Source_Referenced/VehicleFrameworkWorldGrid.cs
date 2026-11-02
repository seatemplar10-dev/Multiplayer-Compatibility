using System;
using System.Threading;
using HarmonyLib;
using Multiplayer.API;
using Vehicles;
using Vehicles.World;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Vehicle Framework - the daily world path grid recalculation runs one vehicle def per world tick in MP</summary>
    /// <remarks>
    /// FIXES row 178 (2 Nov 2026; the mpperf lab replay of 26 Oct, 4 runs). <c>WorldVehiclePathGrid.WorldComponentTick</c>
    /// recalculates the world path cost of every VehicleDef on every world tile once per game day (when the day of year
    /// at longitude 0 changes). Single player runs that on a worker task. In MP, <c>TaskManager.Run</c> runs inline
    /// (Source/Mods/VehicleFramework.cs step 2), so the whole job ran in one tick on every PC: a 267-289 ms tick in all 4
    /// runs, 113 of its 119 profile samples inside <c>RecalculateAllPerceivedPathCostsFor</c>.
    /// <para>
    /// In MP the daily job now calls VF's own <c>RecalculateAllPerceivedPathCostsFor</c> for one def per world tick, in
    /// VF's def order, then VF's <c>RecalculateWinterPercentAt</c> for every tile with the TicksAbs of the start tick (the
    /// value the inline run used). The flags are set as VF's own <c>GridInitializerState</c> sets them for its worker task:
    /// Initialized false and Recalculating true until the last step, so the job does not start again. Not exact: until a
    /// def's turn comes, its grid still holds yesterday's costs. Single player sees the same during its worker task.
    /// Every PC runs the same step on the same world tick: no Rand, no clock, nothing per PC.
    /// </para>
    /// <para>
    /// The job state is not saved, and neither is VF's grid. A reload (join point, resync) builds a new grid whose
    /// allPathCostsRecalculatedDayOfYear is -1. That first fill still runs whole and inline, as before, so a loaded game
    /// never reads an empty grid. Single player is unchanged.
    /// </para>
    /// </remarks>
    [MpCompatFor("SmashPhil.VehicleFramework")]
    public class VehicleFrameworkWorldGrid
    {
        // The grid whose daily job is running (null = none), its next def index and the TicksAbs of its start tick.
        private static WorldVehiclePathGrid slicedGrid;
        private static int nextDef;
        private static int startTicksAbs;

        public VehicleFrameworkWorldGrid(ModContentPack mod)
        {
            var type = typeof(WorldVehiclePathGrid);
            // The steps call these directly; a VF update that renames one fails here, at load, and patches nothing.
            foreach (var name in new[] { nameof(WorldVehiclePathGrid.RecalculateAllPerceivedPathCostsFor), nameof(WorldVehiclePathGrid.RecalculateWinterPercentAt) })
                _ = AccessTools.DeclaredMethod(type, name) ?? throw new MissingMethodException(type.FullName, name);
            _ = AccessTools.DeclaredField(type, nameof(WorldVehiclePathGrid.allPathCostsRecalculatedDayOfYear))
                ?? throw new MissingFieldException(type.FullName, nameof(WorldVehiclePathGrid.allPathCostsRecalculatedDayOfYear));

            var start = AccessTools.DeclaredMethod(type, nameof(WorldVehiclePathGrid.RecalculateAllPathCostsAsync))
                        ?? throw new MissingMethodException(type.FullName, nameof(WorldVehiclePathGrid.RecalculateAllPathCostsAsync));
            var tick = AccessTools.DeclaredMethod(type, nameof(WorldVehiclePathGrid.WorldComponentTick))
                       ?? throw new MissingMethodException(type.FullName, nameof(WorldVehiclePathGrid.WorldComponentTick));
            MpCompat.harmony.Patch(start, prefix: new HarmonyMethod(typeof(VehicleFrameworkWorldGrid), nameof(StartDailyJob)));
            MpCompat.harmony.Patch(tick, postfix: new HarmonyMethod(typeof(VehicleFrameworkWorldGrid), nameof(StepDailyJob)));
        }

        private static bool StartDailyJob(WorldVehiclePathGrid __instance)
        {
            // The first fill after a load, single player, and VF's own "already running" check keep VF's code.
            if (!MP.IsInMultiplayer || __instance.allPathCostsRecalculatedDayOfYear < 0 || __instance.Recalculating)
                return true;

            __instance.allPathCostsRecalculatedDayOfYear = WorldVehiclePathGrid.DayOfYearAt0Long;
            __instance.Initialized = false;
            __instance.Recalculating = true;
            slicedGrid = __instance;
            nextDef = 0;
            startTicksAbs = GenTicks.TicksAbs;
            return false;
        }

        private static void StepDailyJob(WorldVehiclePathGrid __instance)
        {
            if (slicedGrid == null)
                return;
            // A grid of an earlier world (the game reloaded while the job ran): drop the job.
            if (slicedGrid != __instance)
            {
                slicedGrid = null;
                return;
            }

            var done = true;
            try
            {
                var defs = DefDatabase<VehicleDef>.AllDefsListForReading;
                if (nextDef < defs.Count)
                {
                    __instance.RecalculateAllPerceivedPathCostsFor(defs[nextDef++], CancellationToken.None);
                    done = false;
                }
                else
                {
                    for (int i = 0; i < Find.WorldGrid.TilesCount; i++)
                        __instance.RecalculateWinterPercentAt(i, startTicksAbs);
                }
            }
            catch (Exception e)
            {
                // VF's worker task logs and ends the job; GridInitializerState still resets the flags.
                Log.Error($"[MPCompat - Vehicle Framework] Daily world path grid job failed (next def {nextDef}): {e}");
                done = true;
            }

            if (done)
            {
                __instance.Recalculating = false;
                __instance.Initialized = true;
                slicedGrid = null;
            }
        }
    }
}
