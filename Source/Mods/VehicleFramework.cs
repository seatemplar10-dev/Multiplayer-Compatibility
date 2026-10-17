using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Vehicle Framework by SmashPhil</summary>
    /// <remarks>
    /// FIXES row 46 (17 Oct 2026, found by the REA static sweep, row 44). The 1.6 build that 1.7 loads has no MP code and
    /// does vehicle work off the main thread, so what a tick sees depends on thread timing on each PC:
    /// <list type="bullet">
    /// <item>region / reachability / pathing updates go to a per-map SmashTools.DedicatedThread whenever
    /// <c>VehiclePathingSystem.ThreadAvailable</c> (settings <c>debugUseMultithreading</c> defaults to true);</item>
    /// <item>vehicle paths and the world path grid run through <c>SmashTools.TaskManager.Run</c> (Task.Run); the pather
    /// skips ticks while a path is "Calculating";</item>
    /// <item><c>VehicleRegionGrid.GetAllRegions</c> fills a shared list from Parallel.ForEach (racy Add, order per run),
    /// <c>VehiclePathingSystem.GenerateRegionsParallel</c> builds the grids of all vehicle defs in parallel.</item>
    /// </list>
    /// In MP: ThreadAvailable reports false (VF's own synchronous branches run), TaskManager.Run executes the action
    /// inline and returns a finished task, GetAllRegions runs the same loop in cell order on the main thread, and
    /// GenerateRegionsParallel calls VF's own sequential GenerateRegions. Single player is unchanged.
    /// </remarks>
    [MpCompatFor("SmashPhil.VehicleFramework")]
    public class VehicleFramework
    {
        private const string LogPrefix = "[MPCompat - Vehicle Framework] ";

        // Vehicles.VehicleRegionGrid
        private static AccessTools.FieldRef<object, object> regionUpdaterField;
        private static AccessTools.FieldRef<object, object> mappingField;
        private static FastInvokeHandler getRegionAt;
        // Vehicles.VehicleRegionAndRoomUpdater.Enabled
        private static FastInvokeHandler updaterEnabled;
        // Vehicles.VehicleRegion.valid
        private static AccessTools.FieldRef<object, bool> regionValidField;
        // Vehicles.VehiclePathingSystem.GenerateRegions() (sequential twin of GenerateRegionsParallel)
        private static FastInvokeHandler generateRegions;

        public VehicleFramework(ModContentPack mod)
        {
            var pathingSystemType = FindType("Vehicles.VehiclePathingSystem");
            var regionGridType = FindType("Vehicles.VehicleRegionGrid");
            var regionType = FindType("Vehicles.VehicleRegion");
            var updaterType = FindType("Vehicles.VehicleRegionAndRoomUpdater");
            var taskManagerType = FindType("SmashTools.TaskManager");

            // 1. No dedicated thread work in MP.
            var threadAvailable = AccessTools.DeclaredPropertyGetter(pathingSystemType, "ThreadAvailable")
                                  ?? throw new MissingMethodException(pathingSystemType.FullName, "get_ThreadAvailable");
            MpCompat.harmony.Patch(threadAvailable, prefix: new HarmonyMethod(typeof(VehicleFramework), nameof(NoThreadInMp)));

            // 2. Tasks run inline in MP.
            var run = AccessTools.DeclaredMethod(taskManagerType, "Run", [typeof(Action), typeof(CancellationToken)])
                      ?? throw new MissingMethodException(taskManagerType.FullName, "Run(Action, CancellationToken)");
            MpCompat.harmony.Patch(run, prefix: new HarmonyMethod(typeof(VehicleFramework), nameof(RunInlineInMp)));

            // 3. Region list in a fixed order.
            regionUpdaterField = AccessTools.FieldRefAccess<object>(regionGridType, "regionUpdater");
            mappingField = AccessTools.FieldRefAccess<object>(regionGridType, "mapping");
            getRegionAt = MethodInvoker.GetHandler(GetDeclaredMethod(regionGridType, "GetRegionAt", [typeof(int)]));
            updaterEnabled = MethodInvoker.GetHandler(AccessTools.DeclaredPropertyGetter(updaterType, "Enabled")
                                                      ?? throw new MissingMethodException(updaterType.FullName, "get_Enabled"));
            regionValidField = AccessTools.FieldRefAccess<bool>(regionType, "valid");
            MpCompat.harmony.Patch(GetDeclaredMethod(regionGridType, "GetAllRegions"),
                prefix: new HarmonyMethod(typeof(VehicleFramework), nameof(SequentialGetAllRegions)));

            // 4. Grids of all vehicle defs built one after the other.
            generateRegions = MethodInvoker.GetHandler(GetDeclaredMethod(pathingSystemType, "GenerateRegions", Type.EmptyTypes));
            MpCompat.harmony.Patch(GetDeclaredMethod(pathingSystemType, "GenerateRegionsParallel", Type.EmptyTypes),
                prefix: new HarmonyMethod(typeof(VehicleFramework), nameof(SequentialGenerateRegions)));
        }

        private static bool NoThreadInMp(ref bool __result)
        {
            if (!MP.IsInMultiplayer)
                return true;
            __result = false;
            return false;
        }

        private static bool RunInlineInMp(Action action, CancellationToken token, ref Task __result)
        {
            if (!MP.IsInMultiplayer)
                return true;
            if (!token.IsCancellationRequested)
            {
                // TaskManager.Run's own wrapper logs and swallows exceptions; keep that behaviour.
                try { action(); }
                catch (Exception e) { Log.Error($"{LogPrefix}exception in an inline vehicle task: {e}"); }
            }
            __result = Task.CompletedTask;
            return false;
        }

        // Same loop as VF's GetAllRegions (valid regions in cell-index order, each once), on the main thread.
        private static bool SequentialGetAllRegions(object __instance, object regions)
        {
            if (!MP.IsInMultiplayer)
                return true;
            var updater = regionUpdaterField(__instance);
            if (updater == null || !(bool)updaterEnabled(updater))
                return false;
            var list = (IList)regions;
            var map = ((MapComponent)mappingField(__instance)).map;
            var seen = new HashSet<object>();
            var args = new object[1];
            var cells = map.cellIndices.NumGridCells;
            for (var i = 0; i < cells; i++)
            {
                args[0] = i;
                var region = getRegionAt(__instance, args);
                if (region != null && regionValidField(region) && seen.Add(region))
                    list.Add(region);
            }
            return false;
        }

        private static bool SequentialGenerateRegions(object __instance)
        {
            if (!MP.IsInMultiplayer)
                return true;
            generateRegions(__instance);
            return false;
        }

        private static Type FindType(string name)
            => AccessTools.TypeByName(name) ?? throw new TypeLoadException($"{LogPrefix}type {name} not found");

        private static MethodInfo GetDeclaredMethod(Type type, string name, Type[] args = null)
            => AccessTools.DeclaredMethod(type, name, args) ?? throw new MissingMethodException(type.FullName, name);
    }
}
