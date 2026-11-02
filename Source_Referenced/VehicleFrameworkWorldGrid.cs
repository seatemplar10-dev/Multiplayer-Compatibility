using System;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using Multiplayer.API;
using RimWorld.Planet;
using SmashTools;
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
    /// Initialized false and Recalculating true until the last step, so the job does not start again. Each step runs in a
    /// prefix of <c>WorldComponentTick</c>, before VF's dev-mode <c>FlashWorldGrid</c>, which reads this PC's debug picks
    /// and can throw on one PC only (a postfix would then be skipped on that PC). Every PC runs the same step on the same
    /// world tick: no Rand, no clock, nothing per PC.
    /// </para>
    /// <para>
    /// Not exact: until a def's turn comes, its grid holds yesterday's costs (single player sees the same during its
    /// worker task), and game code between steps runs as usual - a vehicle caravan that starts a path recalculates its
    /// own defs (<c>EnsureWorldGridInitialized</c>), and a tile changed during the job reaches only the later defs.
    /// VF's "Regen WorldGrid" debug action mid-job does the whole job inline and clears Recalculating: the job then stops.
    /// </para>
    /// <para>
    /// The job state is not saved, and neither is VF's grid. In hosted MP a join point, resync or rejoin reloads every
    /// PC: SmashTools' world-removed event drops the job, and the new grid (allPathCostsRecalculatedDayOfYear -1) still
    /// gets its first fill whole and inline, as before, so a loaded game never reads an empty grid. A standalone server
    /// reloads only the issuing PC; VF's unsaved grid already differs between PCs there, with or without this job.
    /// Single player is unchanged: the MP-only code sits in methods that are compiled only when MP calls them.
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
            // Every VF member the job uses, with its signature: a VF update that changes one fails here, at load,
            // before anything is patched.
            Require(AccessTools.DeclaredMethod(type, nameof(WorldVehiclePathGrid.RecalculateAllPerceivedPathCostsFor), [typeof(VehicleDef), typeof(CancellationToken)]), type, "RecalculateAllPerceivedPathCostsFor(VehicleDef, CancellationToken)");
            Require(AccessTools.DeclaredMethod(type, nameof(WorldVehiclePathGrid.RecalculateWinterPercentAt), [typeof(PlanetTile), typeof(int?)]), type, "RecalculateWinterPercentAt(PlanetTile, int?)");
            Require(AccessTools.DeclaredPropertyGetter(type, nameof(WorldVehiclePathGrid.Recalculating)), type, "get_Recalculating");
            Require(AccessTools.DeclaredPropertySetter(type, nameof(WorldVehiclePathGrid.Recalculating)), type, "set_Recalculating");
            Require(AccessTools.DeclaredPropertySetter(type, nameof(WorldVehiclePathGrid.Initialized)), type, "set_Initialized");
            Require(AccessTools.DeclaredPropertyGetter(type, nameof(WorldVehiclePathGrid.DayOfYearAt0Long)), type, "get_DayOfYearAt0Long");
            if (AccessTools.DeclaredField(type, nameof(WorldVehiclePathGrid.allPathCostsRecalculatedDayOfYear))?.FieldType != typeof(int))
                throw new MissingFieldException(type.FullName, nameof(WorldVehiclePathGrid.allPathCostsRecalculatedDayOfYear));
            var start = Require(AccessTools.DeclaredMethod(type, nameof(WorldVehiclePathGrid.RecalculateAllPathCostsAsync), Type.EmptyTypes), type, "RecalculateAllPathCostsAsync()");
            var tick = Require(AccessTools.DeclaredMethod(type, nameof(WorldVehiclePathGrid.WorldComponentTick), Type.EmptyTypes), type, "WorldComponentTick()");

            GameEvent.OnWorldRemoved += () => slicedGrid = null;
            // The step first: alone it does nothing (no job is ever started), so a failure to patch the start leaves VF as it was.
            MpCompat.harmony.Patch(tick, prefix: new HarmonyMethod(typeof(VehicleFrameworkWorldGrid), nameof(PreWorldComponentTick)));
            MpCompat.harmony.Patch(start, prefix: new HarmonyMethod(typeof(VehicleFrameworkWorldGrid), nameof(PreRecalculateAllPathCosts)));
        }

        private static System.Reflection.MethodInfo Require(System.Reflection.MethodInfo method, Type type, string name)
            => method ?? throw new MissingMethodException(type.FullName, name);

        private static bool PreRecalculateAllPathCosts(WorldVehiclePathGrid __instance)
            => !MP.IsInMultiplayer || !TryStartDailyJob(__instance);

        private static void PreWorldComponentTick(WorldVehiclePathGrid __instance)
        {
            if (slicedGrid != null)
                StepDailyJob(__instance);
        }

        // The first fill after a load and VF's own "already running" check keep VF's code (returns false).
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TryStartDailyJob(WorldVehiclePathGrid grid)
        {
            if (grid.allPathCostsRecalculatedDayOfYear < 0 || grid.Recalculating)
                return false;

            grid.allPathCostsRecalculatedDayOfYear = WorldVehiclePathGrid.DayOfYearAt0Long;
            grid.Initialized = false;
            grid.Recalculating = true;
            slicedGrid = grid;
            nextDef = 0;
            startTicksAbs = GenTicks.TicksAbs;
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void StepDailyJob(WorldVehiclePathGrid grid)
        {
            // A grid of an earlier world, or VF's debug action finished the job inline: drop the job.
            if (slicedGrid != grid || !grid.Recalculating)
            {
                slicedGrid = null;
                return;
            }

            var done = true;
            var defs = DefDatabase<VehicleDef>.AllDefsListForReading;
            try
            {
                if (nextDef < defs.Count)
                {
                    grid.RecalculateAllPerceivedPathCostsFor(defs[nextDef], CancellationToken.None);
                    nextDef++;
                    done = false;
                }
                else
                {
                    for (int i = 0; i < Find.WorldGrid.TilesCount; i++)
                        grid.RecalculateWinterPercentAt(i, startTicksAbs);
                }
            }
            catch (Exception e)
            {
                // VF's worker task logs and ends the job; GridInitializerState still resets the flags.
                var step = nextDef < defs.Count ? defs[nextDef].defName : "winter percents";
                Log.Error($"[MPCompat - Vehicle Framework] Daily world path grid job failed at {step}: {e}");
            }

            if (done)
            {
                grid.Recalculating = false;
                grid.Initialized = true;
                slicedGrid = null;
            }
        }
    }
}
