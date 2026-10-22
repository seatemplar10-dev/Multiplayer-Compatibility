using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Multiplayer.API;
using Verse;
using Verse.Profile;

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
    /// <para>
    /// Live co-op session 22 Oct 2026 (err-vf-landing): VF's targeters (landing / strafe / launch pickers, the world
    /// targeter) are process-wide singletons that VF stops only on Game.Dispose. MP's join-point, resync and rejoin reloads
    /// never dispose the Game, so a picker open at an autosave kept the old game's MapParent and threw a
    /// NullReferenceException every frame (6,148 times), and a click to land threw too. Step 5 stops them when MP clears
    /// the old maps. Step 6: VF also reads the landing picker's state inside game logic (map removal, game over), which is
    /// one player's UI in MP; it reads as "not targeting" in MP so every PC decides the same.
    /// </para>
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
        // Vehicles.Targeters.ClearAllTargeters() (private static), Vehicles.LandingTargeter.Instance / .targeterQueue
        private static FastInvokeHandler clearAllTargeters;
        private static FastInvokeHandler landingTargeterInstance;
        private static AccessTools.FieldRef<object, Queue<Action>> landingTargeterQueueField;
        // Vehicles.LandingTargeter.get_Instance(), Vehicles.BaseTargeter.get_IsTargeting() (step 6 transpiler)
        private static MethodInfo landingInstanceGetter;
        private static MethodInfo isTargetingGetter;
        private static FastInvokeHandler isTargeting;

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

            // 5. Stop VF's targeters before MP clears the old game (every MP reload path goes through ClearAllMapsAndWorld),
            //    as vanilla's load does through Game.Dispose (Vehicles.Targeters: GameEvent.OnGameDisposing += ClearAllTargeters).
            var landingTargeterType = FindType("Vehicles.LandingTargeter");
            clearAllTargeters = MethodInvoker.GetHandler(GetDeclaredMethod(FindType("Vehicles.Targeters"), "ClearAllTargeters", Type.EmptyTypes));
            landingInstanceGetter = AccessTools.DeclaredPropertyGetter(landingTargeterType, "Instance")
                                    ?? throw new MissingMethodException(landingTargeterType.FullName, "get_Instance");
            landingTargeterInstance = MethodInvoker.GetHandler(landingInstanceGetter);
            landingTargeterQueueField = AccessTools.FieldRefAccess<Queue<Action>>(landingTargeterType, "targeterQueue");
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(MemoryUtility), nameof(MemoryUtility.ClearAllMapsAndWorld)),
                prefix: new HarmonyMethod(typeof(VehicleFramework), nameof(StopTargetersBeforeReload)));

            // 6. The landing picker's state is not read by game logic in MP.
            var baseTargeterType = FindType("Vehicles.BaseTargeter");
            isTargetingGetter = AccessTools.DeclaredPropertyGetter(baseTargeterType, "IsTargeting")
                                ?? throw new MissingMethodException(baseTargeterType.FullName, "get_IsTargeting");
            isTargeting = MethodInvoker.GetHandler(isTargetingGetter);
            // Postfixes on MapPawns.AnyPawnBlockingMapRemoval and GameEnder.CheckOrUpdateGameOver
            MpCompat.harmony.Patch(GetDeclaredMethod(FindType("Vehicles.Patch_MapHandling"), "AnyVehicleBlockingMapRemoval"),
                transpiler: new HarmonyMethod(typeof(VehicleFramework), nameof(PickerStateNotInSim)));
            MpCompat.harmony.Patch(GetDeclaredMethod(FindType("Vehicles.Patch_WorldHandling"), "GameEnderWithVehicles"),
                transpiler: new HarmonyMethod(typeof(VehicleFramework), nameof(PickerStateNotInSim)));
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

        // UI-only: the targeters hold no saved state and stopping one sends no command. Runs on every PC at the same point
        // of the reload. The landing picker's queue is emptied first: StopTargeting starts the next queued picker, which
        // would hold old-game objects too.
        private static void StopTargetersBeforeReload()
        {
            if (!MP.IsInMultiplayer)
                return;
            try
            {
                var landing = landingTargeterInstance(null);
                if (landing != null)
                    landingTargeterQueueField(landing)?.Clear();
                clearAllTargeters(null);
            }
            catch (Exception e)
            {
                Log.Error($"{LogPrefix}could not stop the vehicle targeters before a reload: {e}");
            }
        }

        // `LandingTargeter.Instance.IsTargeting` -> PickerOpenForSim(LandingTargeter.Instance); VF reads it once per method.
        private static IEnumerable<CodeInstruction> PickerStateNotInSim(IEnumerable<CodeInstruction> instr, MethodBase baseMethod)
        {
            var helper = AccessTools.DeclaredMethod(typeof(VehicleFramework), nameof(PickerOpenForSim));
            var list = instr.ToList();
            var replaced = 0;
            for (var i = 1; i < list.Count; i++)
            {
                if ((list[i].opcode != OpCodes.Callvirt && list[i].opcode != OpCodes.Call) || list[i].operand is not MethodInfo method || method != isTargetingGetter)
                    continue;
                if (list[i - 1].opcode != OpCodes.Call || list[i - 1].operand is not MethodInfo previous || previous != landingInstanceGetter)
                    continue;
                list[i].opcode = OpCodes.Call;
                list[i].operand = helper;
                replaced++;
            }

            if (replaced != 1)
                throw new Exception($"{LogPrefix}{baseMethod.DeclaringType?.FullName}.{baseMethod.Name}: replaced LandingTargeter.Instance.IsTargeting x{replaced}, expected 1");

            return list;
        }

        // In MP the landing picker is one player's UI, so game logic must not depend on it.
        private static bool PickerOpenForSim(object targeter)
            => !MP.IsInMultiplayer && (bool)isTargeting(targeter);

        private static Type FindType(string name)
            => AccessTools.TypeByName(name) ?? throw new TypeLoadException($"{LogPrefix}type {name} not found");

        private static MethodInfo GetDeclaredMethod(Type type, string name, Type[] args = null)
            => AccessTools.DeclaredMethod(type, name, args) ?? throw new MissingMethodException(type.FullName, name);
    }
}
