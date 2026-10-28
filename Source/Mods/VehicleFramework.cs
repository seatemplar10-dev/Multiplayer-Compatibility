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
using RimWorld;
using RimWorld.Planet;
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
    /// <para>
    /// Step 7 (PatchCaravanDialog):
    /// Caravan forming (err_mf_settle lane c, 22 Oct 2026): VF prefixes <c>Dialog_FormCaravan.TrySend</c> with
    /// <c>Vehicles.Patch_FormCaravanDialog.TryAndSendWithVehicles</c>, whose first line reads <c>CaravanFormation.formation.Reform</c>. That
    /// static is only filled by VF's PostOpen hook, which returns early when <c>thisWindowInstanceEverOpened</c> is true, and Multiplayer's
    /// <c>CaravanFormingSession.PrepareDummyDialog</c> sets that flag before the window opens - so for Multiplayer's
    /// <c>CaravanFormingProxy</c> window <c>formation</c> stays null and every press of Send throws a NullReferenceException (live log 22 Oct,
    /// 19:53-19:54, three times) before MP's synced <c>TryFormAndSendCaravan</c> is ever reached. VF's own vehicle path forms the caravan locally
    /// (<c>VehicleCaravanFormingUtility.StartFormingCaravan</c> / <c>ReformInstantly</c>, nothing synced), so initialising <c>formation</c> would
    /// turn the exception into a desync. Instead, for the proxy window only: a vehicle in the caravan = reject message and no send; no vehicle =
    /// skip VF's body and let vanilla TrySend run (MP's own prefixes then send the synced command). VF's "Change route" replacement
    /// (<c>WorldRoutePannerReroute</c>) has the same dereference on its vehicle branch; for the proxy the plain vanilla route planner starts.
    /// Full vehicle caravans in co-op are NOT supported here (they would need VF's caravan creation synced).
    /// Status: [unverified] - compiled only, not run in game.
    /// </para>
    /// <para>
    /// Step 8 (FIXES row 285, live 28 Oct 2026 22:20): the same null <c>CaravanFormation.formation</c> (and no split dialog, so
    /// <c>CaravanFormation.Current</c> is null) breaks the vehicle cards on VF's Vehicles tab of the proxy window. Ticking a vehicle opens
    /// <c>Dialog_AssignSeats(CaravanFormation.Current, ...)</c>; its Confirm (<c>FinalizeSeats</c>) changes the transferables on this PC only
    /// and then throws at <c>parent.NotifyTransferablesChanged()</c>. Unticking throws at
    /// <c>CaravanFormation.Current.NotifyTransferablesChanged()</c> in <c>TransferableVehicleWidget.DrawCard</c>. In MP with no current
    /// formation: the seat window does not open (same reject message as Send, since step 7 refuses vehicle caravans anyway, and its
    /// unsynced seat writes could make the PCs send different caravans), and the untick skips the null notify.
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

        // Caravan forming through Multiplayer's CaravanFormingProxy (MP-Compat does not reference Multiplayer.dll: compared by name).
        private const string CaravanFormingProxyTypeName = "Multiplayer.Client.CaravanFormingProxy";
        // Vehicles.VehiclePawn
        private static Type vehiclePawnType;
        // Step 8: Vehicles.World.CaravanFormation.get_Current(), Vehicles.World.ICaravanInfo.NotifyTransferablesChanged(),
        // Vehicles.World.Dialog_AssignSeats
        private static MethodInfo caravanFormationCurrent;
        private static MethodInfo notifyTransferablesChanged;
        private static Type assignSeatsType;

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
            //    Steps 5, 6 and 7 come from separate fixes (FIXES rows 216, 217, 204): each in its own try so one failing patch
            //    cannot leave the others uninstalled (live22 merge review, 23 Oct).
            try
            {
                var landingTargeterType = FindType("Vehicles.LandingTargeter");
                clearAllTargeters = MethodInvoker.GetHandler(GetDeclaredMethod(FindType("Vehicles.Targeters"), "ClearAllTargeters", Type.EmptyTypes));
                landingInstanceGetter = AccessTools.DeclaredPropertyGetter(landingTargeterType, "Instance")
                                        ?? throw new MissingMethodException(landingTargeterType.FullName, "get_Instance");
                landingTargeterInstance = MethodInvoker.GetHandler(landingInstanceGetter);
                landingTargeterQueueField = AccessTools.FieldRefAccess<Queue<Action>>(landingTargeterType, "targeterQueue");
                MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(MemoryUtility), nameof(MemoryUtility.ClearAllMapsAndWorld)),
                    prefix: new HarmonyMethod(typeof(VehicleFramework), nameof(StopTargetersBeforeReload)));
            }
            catch (Exception e)
            {
                Log.Error("MPCompat :: Vehicle Framework step 5 (stop targeters before reload, FIXES row 216) not installed: " + e);
            }

            // 6. The landing picker's state is not read by game logic in MP.
            try
            {
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
            catch (Exception e)
            {
                Log.Error("MPCompat :: Vehicle Framework step 6 (picker state out of game logic, FIXES row 217) not installed: " + e);
            }

            // 7. Caravan forming through Multiplayer's CaravanFormingProxy window: VF's Send / Change route code reads a static that is
            // null for that window.
            try
            {
                PatchCaravanDialog();
            }
            catch (Exception e)
            {
                Log.Error("MPCompat :: Vehicle Framework step 7 (caravan forming window, FIXES row 204) not installed: " + e);
            }

            // 8. Vehicle cards in Multiplayer's caravan window: seat window and untick read the null CaravanFormation.Current.
            try
            {
                PatchVehicleCards();
            }
            catch (Exception e)
            {
                Log.Error("MPCompat :: Vehicle Framework step 8 (vehicle seat window in caravan forming, FIXES row 285) not installed: " + e);
            }
        }

        private static void PatchVehicleCards()
        {
            var caravanFormationType = FindType("Vehicles.World.CaravanFormation");
            caravanFormationCurrent = AccessTools.DeclaredPropertyGetter(caravanFormationType, "Current")
                                      ?? throw new MissingMethodException(caravanFormationType.FullName, "get_Current");
            notifyTransferablesChanged = GetDeclaredMethod(FindType("Vehicles.World.ICaravanInfo"), "NotifyTransferablesChanged", Type.EmptyTypes);
            assignSeatsType = FindType("Vehicles.World.Dialog_AssignSeats");

            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(WindowStack), nameof(WindowStack.Add)),
                prefix: new HarmonyMethod(typeof(VehicleFramework), nameof(NoSeatWindowWithoutFormation)));
            MpCompat.harmony.Patch(GetDeclaredMethod(FindType("Vehicles.World.TransferableVehicleWidget"), "DrawCard"),
                transpiler: new HarmonyMethod(typeof(VehicleFramework), nameof(NullSafeCurrentNotify)));
        }

        private static void PatchCaravanDialog()
        {
            vehiclePawnType = FindType("Vehicles.VehiclePawn");
            var formDialogPatchType = FindType("Vehicles.Patch_FormCaravanDialog");

            // VF's own prefix on Dialog_FormCaravan.TrySend. It is static and its first parameter is called __instance (it is a patch
            // itself): in a prefix of ours the name __instance would mean "instance of a static method" = null, so the dialog is read by
            // position (__0) in both prefixes below.
            MpCompat.harmony.Patch(GetDeclaredMethod(formDialogPatchType, "TryAndSendWithVehicles", [typeof(Dialog_FormCaravan)]),
                prefix: new HarmonyMethod(typeof(VehicleFramework), nameof(PreTryAndSendWithVehicles)));
            MpCompat.harmony.Patch(
                GetDeclaredMethod(formDialogPatchType, "WorldRoutePannerReroute",
                    [typeof(WorldRoutePlanner), typeof(Dialog_FormCaravan), typeof(Map), typeof(bool)]),
                prefix: new HarmonyMethod(typeof(VehicleFramework), nameof(PreWorldRoutePannerReroute)));

            Log.Message($"{LogPrefix}caravan window guard installed (Send / Change route in Multiplayer's caravan window no longer read VF's null CaravanFormation.formation)");
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

        private static bool IsCaravanFormingProxy(Dialog_FormCaravan dialog)
            => dialog != null && dialog.GetType().FullName == CaravanFormingProxyTypeName;

        // The same test as VF's VehiclesSelected: a transferable whose thing is a VehiclePawn with a count to send.
        private static bool AnyVehicleSelected(Dialog_FormCaravan dialog)
        {
            var transferables = dialog.transferables;
            if (transferables == null)
                return false;
            foreach (var transferable in transferables)
            {
                if (transferable != null && transferable.CountToTransfer > 0 && vehiclePawnType.IsInstanceOfType(transferable.AnyThing))
                    return true;
            }

            return false;
        }

        // Prefix on Vehicles.Patch_FormCaravanDialog.TryAndSendWithVehicles(Dialog_FormCaravan __instance). That method's result is VF's own
        // convention for the prefix it is: true = the vanilla Dialog_FormCaravan.TrySend runs next, false = it is skipped.
        // Only for Multiplayer's CaravanFormingProxy; every other dialog, and single player, runs VF's body unchanged.
        private static bool PreTryAndSendWithVehicles(Dialog_FormCaravan __0, ref bool __result)
        {
            if (!MP.IsInMultiplayer || !IsCaravanFormingProxy(__0))
                return true;

            if (AnyVehicleSelected(__0))
            {
                // VF's vehicle path builds the caravan on this PC only (nothing syncs it): refuse, change nothing.
                Messages.Message("Vehicle caravans are not supported in multiplayer yet", MessageTypeDefOf.RejectInput, false);
                __result = false;
                return false;
            }

            __result = true; // vanilla TrySend runs; MP's prefixes on TryFormAndSendCaravan / TryReformCaravan send the synced command
            return false;    // VF's body would dereference the null CaravanFormation.formation
        }

        // Prefix on Vehicles.Patch_FormCaravanDialog.WorldRoutePannerReroute(WorldRoutePlanner routePlanner, Dialog_FormCaravan formCaravan, ...),
        // which VF's transpiler puts in place of the vanilla routePlanner.Start(this) call. Its vehicle branch sets
        // CaravanFormation.formation.ChoosingRoute (null for the proxy); for the proxy always start the vanilla route planner, which is what
        // VF itself does when no vehicle is selected (MP syncs the chosen route through the proxy's Notify_ChoseRoute).
        private static bool PreWorldRoutePannerReroute(WorldRoutePlanner __0, Dialog_FormCaravan __1)
        {
            if (!MP.IsInMultiplayer || !IsCaravanFormingProxy(__1))
                return true;

            __0.Start(__1);
            return false;
        }

        // Prefix on WindowStack.Add. VF opens Dialog_AssignSeats only from the vehicle card checkbox, with CaravanFormation.Current as the
        // window's parent; in MP that is null for Multiplayer's caravan window and the window's Confirm throws after writing unsynced counts.
        // The window is not added (nothing changed yet: its constructor only reads). UI only, no game state.
        private static bool NoSeatWindowWithoutFormation(Window window)
        {
            if (!MP.IsInMultiplayer || window == null || window.GetType() != assignSeatsType)
                return true;
            if (caravanFormationCurrent.Invoke(null, null) != null)
                return true;

            Messages.Message("Vehicle caravans are not supported in multiplayer yet", MessageTypeDefOf.RejectInput, false);
            return false;
        }

        // Transpiler on TransferableVehicleWidget.DrawCard: "CaravanFormation.Current.NotifyTransferablesChanged()" (untick branch) becomes
        // NotifyCurrentIfAny(). Single player: Current is never null there, so the behaviour is unchanged.
        private static IEnumerable<CodeInstruction> NullSafeCurrentNotify(IEnumerable<CodeInstruction> instr, MethodBase baseMethod)
        {
            var list = instr.ToList();
            var replaced = 0;
            for (var i = 0; i < list.Count - 1; i++)
            {
                if (list[i].Calls(caravanFormationCurrent) && list[i + 1].Calls(notifyTransferablesChanged))
                {
                    list[i].opcode = OpCodes.Call;
                    list[i].operand = AccessTools.DeclaredMethod(typeof(VehicleFramework), nameof(NotifyCurrentIfAny));
                    list[i + 1].opcode = OpCodes.Nop;
                    list[i + 1].operand = null;
                    replaced++;
                }
            }

            if (replaced != 1)
                Log.Error($"{LogPrefix}DrawCard: expected 1 'CaravanFormation.Current.NotifyTransferablesChanged()', patched {replaced} ({baseMethod?.FullDescription()})");
            return list;
        }

        private static void NotifyCurrentIfAny()
        {
            var current = caravanFormationCurrent.Invoke(null, null);
            if (current != null)
                notifyTransferablesChanged.Invoke(current, null);
        }

        private static Type FindType(string name)
            => AccessTools.TypeByName(name) ?? throw new TypeLoadException($"{LogPrefix}type {name} not found");

        private static MethodInfo GetDeclaredMethod(Type type, string name, Type[] args = null)
            => AccessTools.DeclaredMethod(type, name, args) ?? throw new MissingMethodException(type.FullName, name);
    }
}
