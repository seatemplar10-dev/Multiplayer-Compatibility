using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Vehicles;
using Vehicles.World;
using Verse;
using Verse.Profile;

namespace Multiplayer.Compat
{
    /// <remarks>
    /// FIXES row 358 (2 Nov 2026): vehicle caravans through Multiplayer's caravan window (CaravanFormingProxy /
    /// CaravanFormingSession). State map: notes\row358_map.md. In MP the window and the session share one transferables
    /// list, so every count VF writes from its Vehicles tab is game state, and VF's seat list (CaravanHelper.assignedSeats,
    /// static) is read by game logic (forming lord job, caravan mass / capacity / visibility). Here:
    /// <list type="bullet">
    /// <item>seat window Confirm and vehicle untick become synced commands; nothing in the window writes counts or seats;</item>
    /// <item>Send (form), Send (reform) and "leave vehicles behind" run VF's own code inside one synced command, on MP's dummy
    /// dialog of the session, with CaravanFormation.formation set to that dialog for the command only;</item>
    /// <item>assignedSeats is written only by those commands (VF's close-time clear is skipped in MP), kept in one fixed order,
    /// saved in the session (joiners load it) and emptied when the session ends and at every game load; during a send, the seats
    /// of other maps' sessions are set aside;</item>
    /// <item>VF's seat window and vehicle untick in MP's caravan SPLIT window are refused (they wrote seats and counts on one PC);
    /// a vehicle card of another session (VF's widget is static) or of an ended session changes nothing.</item>
    /// </list>
    /// The main assembly's VehicleFramework (steps 7, 8, 9) reads <see cref="VehicleFrameworkCaravanSync.Installed"/>: only when
    /// this installed does it keep VF's formation for the window and let VF's send code run. Single player unchanged except that
    /// VF's seat list is emptied at a game load. Status: [unverified] - compiled only, not run in game.
    /// </remarks>
    public partial class VehicleFramework
    {
        #region Caravan forming with vehicles (FIXES row 358)

        private const string MpVehicleCaravanRejectMessage = "Vehicle caravans are not supported in multiplayer yet";
        private const string MpVehicleSplitRejectMessage = "Vehicle seats in a caravan split are not supported in multiplayer yet";
        private const string MpVehicleOtherWindowMessage = "This vehicle belongs to another caravan window: close and reopen this one";
        private const string VehicleSeatsSaveLabel = "mpCompatVehicleSeats";

        // Send modes of SyncedSendVehicleCaravan
        private const int SendForm = 0;          // CaravanFormation.TrySendVehicleCaravan's local TryFormAndSendCaravan
        private const int SendReform = 1;        // CaravanFormation.ReformInstantly
        private const int SendLeaveVehicles = 2; // the confirmation lambda of CaravanFormation.TryShowConfirmLeaveVehiclesDialog

        // Multiplayer (not referenced): Multiplayer.Client.CaravanFormingProxy / CaravanFormingSession
        private static Type caravanFormingProxyType;
        private static Type caravanFormingSessionType;
        private static FieldInfo proxyDrawingField;
        private static MethodInfo proxySessionGetter;
        private static FieldInfo proxyOriginalSessionIdField;
        // Multiplayer.Client.SyncSessionWithTransferablesMarker.DrawnSessionWithTransferables (set while MP's split window draws too),
        // Multiplayer.Client.Persistent.CaravanSplittingSession
        private static MethodInfo drawnSessionGetter;
        private static Type caravanSplittingSessionType;
        private static FieldInfo sessionMapField;
        private static FieldInfo sessionReformField;
        private static FieldInfo sessionTransferablesField;
        private static MethodInfo sessionPrepareDummyDialog;
        private static MethodInfo sessionRemove;
        private static MethodInfo sessionTryFormAndSendCaravan;
        private static MethodInfo sessionTryReformCaravan;

        // Vehicles: compiler-made members of CaravanFormation (Vehicles.dll cceb7173: g__TryFormAndSendCaravan|5_2,
        // <>c__DisplayClass4_0.<TryShowConfirmLeaveVehiclesDialog>b__1 with field formCaravan)
        private static MethodInfo vfTryFormAndSendCaravan;
        private static MethodInfo vfLeaveVehiclesConfirmed;
        private static FieldInfo vfLeaveVehiclesDialogField;

        // Seats read from a save, applied in PostLoadInit (after cross references resolved)
        private static readonly ConditionalWeakTable<object, List<AssignedSeat>> loadedVehicleSeats = new();

        private static void PatchCaravanFormingWithVehicles()
        {
            caravanFormingProxyType = TypeOrThrow("Multiplayer.Client.CaravanFormingProxy");
            caravanFormingSessionType = TypeOrThrow("Multiplayer.Client.CaravanFormingSession");
            proxyDrawingField = FieldOrThrow(caravanFormingProxyType, "drawing");
            proxySessionGetter = AccessTools.DeclaredPropertyGetter(caravanFormingProxyType, "Session")
                                 ?? throw new MissingMethodException(caravanFormingProxyType.FullName, "get_Session");
            proxyOriginalSessionIdField = FieldOrThrow(caravanFormingProxyType, "originalSessionId");
            var markerType = TypeOrThrow("Multiplayer.Client.SyncSessionWithTransferablesMarker");
            drawnSessionGetter = AccessTools.DeclaredPropertyGetter(markerType, "DrawnSessionWithTransferables")
                                 ?? throw new MissingMethodException(markerType.FullName, "get_DrawnSessionWithTransferables");
            caravanSplittingSessionType = TypeOrThrow("Multiplayer.Client.Persistent.CaravanSplittingSession");
            sessionMapField = FieldOrThrow(caravanFormingSessionType, "map");
            sessionReformField = FieldOrThrow(caravanFormingSessionType, "reform");
            sessionTransferablesField = FieldOrThrow(caravanFormingSessionType, "transferables");
            sessionPrepareDummyDialog = MethodOrThrow(caravanFormingSessionType, "PrepareDummyDialog");
            sessionRemove = MethodOrThrow(caravanFormingSessionType, "Remove");
            sessionTryFormAndSendCaravan = MethodOrThrow(caravanFormingSessionType, "TryFormAndSendCaravan");
            sessionTryReformCaravan = MethodOrThrow(caravanFormingSessionType, "TryReformCaravan");

            vfTryFormAndSendCaravan = MpMethodUtil.GetLocalFunc(typeof(CaravanFormation), nameof(CaravanFormation.TrySendVehicleCaravan),
                localFunc: "TryFormAndSendCaravan");
            if (!vfTryFormAndSendCaravan.IsStatic || vfTryFormAndSendCaravan.ReturnType != typeof(bool) || vfTryFormAndSendCaravan.GetParameters().Length != 0)
                throw new Exception($"unexpected signature of {vfTryFormAndSendCaravan.FullDescription()}");
            vfLeaveVehiclesConfirmed = MpMethodUtil.GetLambda(typeof(CaravanFormation), nameof(CaravanFormation.TryShowConfirmLeaveVehiclesDialog),
                lambdaOrdinal: 1);
            if (vfLeaveVehiclesConfirmed.IsStatic || vfLeaveVehiclesConfirmed.GetParameters().Length != 0)
                throw new Exception($"unexpected signature of {vfLeaveVehiclesConfirmed.FullDescription()}");
            vfLeaveVehiclesDialogField = AccessTools.DeclaredField(vfLeaveVehiclesConfirmed.DeclaringType, "formCaravan");
            if (vfLeaveVehiclesDialogField?.FieldType != typeof(Dialog_FormCaravan))
                throw new MissingFieldException(vfLeaveVehiclesConfirmed.DeclaringType!.FullName, "formCaravan");

            MP.RegisterSyncMethod(typeof(VehicleFramework), nameof(SyncedSetVehicleSeats));
            MP.RegisterSyncMethod(typeof(VehicleFramework), nameof(SyncedUntickVehicle));
            MP.RegisterSyncMethod(typeof(VehicleFramework), nameof(SyncedSendVehicleCaravan));

            var self = typeof(VehicleFramework);
            // Seat window: Confirm and the window guard
            MpCompat.harmony.Patch(MethodOrThrow(typeof(Dialog_AssignSeats), nameof(Dialog_AssignSeats.FinalizeSeats)),
                prefix: new HarmonyMethod(self, nameof(PreFinalizeSeats)));
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(WindowStack), nameof(WindowStack.Add)),
                prefix: new HarmonyMethod(self, nameof(PreAddSeatWindow)));
            // Vehicle card checkbox: an untick becomes a command
            MpCompat.harmony.Patch(MethodOrThrow(typeof(TransferableVehicleWidget), nameof(TransferableVehicleWidget.DrawCard)),
                transpiler: new HarmonyMethod(self, nameof(SyncVehicleCardCheckbox)));
            // Send: the three game-state steps of VF's send code become one command
            MpCompat.harmony.Patch(vfTryFormAndSendCaravan, prefix: new HarmonyMethod(self, nameof(PreVfTryFormAndSendCaravan)));
            MpCompat.harmony.Patch(MethodOrThrow(typeof(CaravanFormation), nameof(CaravanFormation.ReformInstantly)),
                prefix: new HarmonyMethod(self, nameof(PreVfReformInstantly)));
            MpCompat.harmony.Patch(vfLeaveVehiclesConfirmed, prefix: new HarmonyMethod(self, nameof(PreVfLeaveVehiclesConfirmed)));
            // VF statics: no seat clear from one PC's window close; no UI formation inside MP's synced debug instant form
            MpCompat.harmony.Patch(MethodOrThrow(typeof(Patch_FormCaravanDialog), nameof(Patch_FormCaravanDialog.FormCaravanPostClose)),
                prefix: new HarmonyMethod(self, nameof(PreVfFormCaravanPostClose)));
            MpCompat.harmony.Patch(MethodOrThrow(typeof(Patch_FormCaravanDialog), nameof(Patch_FormCaravanDialog.TryFormCaravanInstantly)),
                prefix: new HarmonyMethod(self, nameof(PreVfTryFormCaravanInstantly)),
                finalizer: new HarmonyMethod(self, nameof(FinalVfTryFormCaravanInstantly)));
            // Seat list lifetime: saved in the session, emptied when it ends and at every load
            MpCompat.harmony.Patch(MethodOrThrow(caravanFormingSessionType, "ExposeData"),
                postfix: new HarmonyMethod(self, nameof(PostCaravanSessionExposeData)));
            MpCompat.harmony.Patch(sessionRemove, postfix: new HarmonyMethod(self, nameof(PostCaravanSessionEnded)));
            MpCompat.harmony.Patch(MethodOrThrow(caravanFormingSessionType, "Reset"), postfix: new HarmonyMethod(self, nameof(PostCaravanSessionEnded)));
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(Game), nameof(Game.LoadGame)),
                prefix: new HarmonyMethod(self, nameof(ClearVehicleSeatsBeforeLoad)));
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(MemoryUtility), nameof(MemoryUtility.ClearAllMapsAndWorld)),
                prefix: new HarmonyMethod(self, nameof(ClearVehicleSeatsBeforeLoad)));

            VehicleFrameworkCaravanSync.Installed = true;
            Log.Message("[MPCompat - Vehicle Framework] vehicle caravans in Multiplayer's caravan window installed (seats, untick and send synced, FIXES row 358)");
        }

        private static Type TypeOrThrow(string name)
            => AccessTools.TypeByName(name) ?? throw new TypeLoadException($"type {name} not found");

        private static FieldInfo FieldOrThrow(Type type, string name)
            => AccessTools.DeclaredField(type, name) ?? throw new MissingFieldException(type.FullName, name);

        private static MethodInfo MethodOrThrow(Type type, string name)
            => AccessTools.DeclaredMethod(type, name) ?? throw new MissingMethodException(type.FullName, name);

        // UI side of MP: a click, not a command being executed.
        private static bool InMpInterface => MP.IsInMultiplayer && !MP.IsExecutingSyncCommand;

        // In a command, MP's dummy dialog is a CaravanFormingProxy too: callers check InMpInterface first.
        private static bool IsCaravanFormingProxy(Dialog_FormCaravan dialog)
            => dialog != null && dialog.GetType() == caravanFormingProxyType;

        // The session this window was opened for, null once it ended. CaravanFormingProxy.Session is the map's first caravan forming
        // session, which can be a newer one (an old confirmation box or seat window must not act on it): its id must match.
        private static ISessionWithTransferables SessionOfProxy(Dialog_FormCaravan proxy)
        {
            try
            {
                var session = proxySessionGetter.Invoke(proxy, null) as ISessionWithTransferables;
                if (session is not Session { } mpSession || proxyOriginalSessionIdField.GetValue(proxy) is not int id || mpSession.SessionId != id)
                    return null;
                return session;
            }
            catch (Exception e)
            {
                Log.ErrorOnce($"[MPCompat - Vehicle Framework] could not read the caravan forming session of {proxy}: {e}", 35835801);
                return null;
            }
        }

        private static TransferableOneWay TransferableOf(ISessionWithTransferables session, Thing thing)
            => thing == null ? null : session.GetTransferableByThingId(thing.thingIDNumber) as TransferableOneWay;

        // A seat pawn from its thing id (synced as an int, see PreFinalizeSeats): aboard this vehicle, else in the session.
        private static Pawn ResolveSeatPawn(ISessionWithTransferables session, List<Pawn> aboard, int pawnId)
        {
            if (pawnId < 0)
                return null;
            var pawn = aboard?.Find(p => p != null && p.thingIDNumber == pawnId);
            if (pawn != null)
                return pawn;
            return (session.GetTransferableByThingId(pawnId) as TransferableOneWay)?.things.Find(t => t.thingIDNumber == pawnId) as Pawn;
        }

        private static bool AnyVehicleSelected(List<TransferableOneWay> transferables)
            => transferables.Any(t => t != null && t.CountToTransfer > 0 && t.AnyThing is VehiclePawn);

        // MP's split window is drawing (VF's Vehicles tab there writes seats and counts on one PC; not synced by row 358).
        private static bool SplitWindowDrawing()
            => drawnSessionGetter.Invoke(null, null) is { } drawn && drawn.GetType() == caravanSplittingSessionType;

        #region Seat window

        // Prefix on WindowStack.Add: a seat window opened from Multiplayer's caravan window must have that window's FormationInfo as
        // parent (else its Confirm would run VF's unsynced FinalizeSeats). Nothing has changed yet when it is refused.
        private static bool PreAddSeatWindow(Window window, bool __runOriginal)
        {
            if (!__runOriginal)
                return false;
            if (!MP.IsInMultiplayer || window is not Dialog_AssignSeats seatWindow)
                return true;
            if (proxyDrawingField.GetValue(null) == null)
            {
                if (!SplitWindowDrawing())
                    return true;
                Messages.Message(MpVehicleSplitRejectMessage, MessageTypeDefOf.RejectInput, false);
                return false;
            }
            if (seatWindow.parent is FormationInfo info && IsCaravanFormingProxy(info.Dialog))
                return true;

            Messages.Message(MpVehicleCaravanRejectMessage, MessageTypeDefOf.RejectInput, false);
            return false;
        }

        // Prefix on Dialog_AssignSeats.FinalizeSeats (Confirm). For Multiplayer's caravan window: VF's own check runs here (read
        // only), the seats go out as a command, the window closes. Counts and CaravanHelper.assignedSeats change in the command only.
        private static bool PreFinalizeSeats(Dialog_AssignSeats __instance, ref bool __result, ref string failReason)
        {
            if (!InMpInterface || __instance.parent is not FormationInfo info || !IsCaravanFormingProxy(info.Dialog))
                return true;

            var vehicle = __instance.vehicle;
            failReason = string.Empty;
            __result = false;
            foreach (var handler in vehicle.handlers)
            {
                if (handler.role.RequiredForCaravan && __instance.PreAssignedCount(handler) < handler.role.SlotsToOperate)
                {
                    failReason = "VF_CantAssignVehicle".Translate(vehicle.LabelCap);
                    return false;
                }
            }

            var session = SessionOfProxy(info.Dialog);
            if (session == null)
            {
                failReason = MpVehicleCaravanRejectMessage;
                return false;
            }

            // Pawns go as thing ids: a pawn already aboard is despawned inside a VehicleRoleHandler (see SyncedDisembarkPawn).
            var pawnIds = new List<int>();
            var handlerIndices = new List<int>();
            foreach (var seat in __instance.Assignments)
            {
                pawnIds.Add(seat.pawn?.thingIDNumber ?? -1);
                handlerIndices.Add(vehicle.handlers.IndexOf(seat.handler));
            }

            SyncedSetVehicleSeats(session, vehicle, pawnIds, handlerIndices);
            __result = true;
            return false;
        }

        // Synced. What VF's FinalizeSeats does after its check, on the session's transferables, on every PC. The list is checked
        // again here (the other player may have seated a pawn or ended the session in between): pawns not in this window, seated in
        // another vehicle, doubled or over a role's slots are dropped; a required role left short = no change.
        // Pawns are resolved from their thing ids on every PC: this vehicle's pawns aboard, else the session's pawn transferables
        // (which also hold the pawns aboard other vehicles). Ids that do not resolve are dropped.
        private static void SyncedSetVehicleSeats(ISessionWithTransferables session, VehiclePawn vehicle, List<int> pawnIds, List<int> handlerIndices)
        {
            if (session == null || vehicle == null || pawnIds == null || handlerIndices == null || pawnIds.Count != handlerIndices.Count)
                return;
            var vehicleTransferable = TransferableOf(session, vehicle);
            if (vehicleTransferable == null)
                return;

            var seats = CaravanHelper.assignedSeats;
            var aboard = vehicle.AllPawnsAboard;
            var newSeats = new List<AssignedSeat>();
            for (var i = 0; i < pawnIds.Count; i++)
            {
                var pawn = ResolveSeatPawn(session, aboard, pawnIds[i]);
                var index = handlerIndices[i];
                if (pawn == null || pawn is VehiclePawn || index < 0 || index >= vehicle.handlers.Count)
                    continue;
                if (!aboard.Contains(pawn))
                {
                    if (TransferableOf(session, pawn) == null)
                        continue;
                    var current = seats.GetAssignment(pawn);
                    if (current != null && current.Vehicle != vehicle)
                        continue;
                }

                var handler = vehicle.handlers[index];
                if (newSeats.Any(s => s.pawn == pawn) || newSeats.Count(s => s.handler == handler) >= handler.role.Slots)
                    continue;
                newSeats.Add(new AssignedSeat(pawn, handler));
            }

            foreach (var handler in vehicle.handlers)
            {
                if (handler.role.RequiredForCaravan && newSeats.Count(s => s.handler == handler) < handler.role.SlotsToOperate)
                {
                    if (MP.IsExecutingSyncCommandIssuedBySelf)
                        Messages.Message("VF_AssignFailure".Translate("VF_CantAssignVehicle".Translate(vehicle.LabelCap)), MessageTypeDefOf.RejectInput, false);
                    return;
                }
            }

            foreach (var seat in seats.GetAssignments(vehicle).ToList())
                TransferableOf(session, seat.pawn)?.ForceTo(0);
            foreach (var seat in newSeats)
                TransferableOf(session, seat.pawn)?.ForceTo(1);
            seats.SetAssignments(vehicle, newSeats);
            vehicleTransferable.AdjustTo(newSeats.Count > 0 ? vehicleTransferable.GetMaximumToTransfer() : 0);
            SortVehicleSeats();
            session.Notify_CountChanged(vehicleTransferable);
        }

        #endregion

        #region Vehicle card checkbox

        // Transpiler on TransferableVehicleWidget.DrawCard(Rect, Section, TransferableOneWay): right after
        // "Widgets.Checkbox(pos, ref checkOn, ...)" insert "VehicleCardCheckboxChanged(ref checkOn, transferable)". In Multiplayer's
        // caravan window an untick is sent as a command and checkOn is set back, so VF's local untick branch does not run.
        private static IEnumerable<CodeInstruction> SyncVehicleCardCheckbox(IEnumerable<CodeInstruction> instr, MethodBase baseMethod)
        {
            var list = instr.ToList();
            var helper = AccessTools.DeclaredMethod(typeof(VehicleFramework), nameof(VehicleCardCheckboxChanged));
            var patched = 0;
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i].operand is not MethodInfo { Name: nameof(Widgets.Checkbox) } checkbox || checkbox.DeclaringType != typeof(Widgets))
                    continue;
                var parameters = checkbox.GetParameters();
                if (parameters.Length < 2 || parameters[0].ParameterType != typeof(UnityEngine.Vector2) || parameters[1].ParameterType != typeof(bool).MakeByRefType())
                    continue;

                // The bool local passed by ref: the nearest "ldloca <bool local>" before the call.
                CodeInstruction loadCheckOn = null;
                for (var j = i - 1; j >= 0 && loadCheckOn == null; j--)
                {
                    if ((list[j].opcode == OpCodes.Ldloca || list[j].opcode == OpCodes.Ldloca_S) && list[j].operand is LocalBuilder { LocalType: var type } && type == typeof(bool))
                        loadCheckOn = list[j];
                }

                if (loadCheckOn == null)
                    break;
                list.InsertRange(i + 1, [
                    new CodeInstruction(loadCheckOn.opcode, loadCheckOn.operand),
                    new CodeInstruction(OpCodes.Ldarg_3), // transferable
                    new CodeInstruction(OpCodes.Call, helper),
                ]);
                patched++;
                i += 3;
            }

            if (patched != 1)
                throw new Exception($"{baseMethod?.FullDescription()}: patched {patched} vehicle card checkboxes, expected 1");
            return list;
        }

        private static void VehicleCardCheckboxChanged(ref bool checkOn, TransferableOneWay transferable)
        {
            if (!InMpInterface || transferable == null)
                return;
            var wasOn = transferable.CountToTransfer > 0;
            if (checkOn == wasOn)
                return;
            if (proxyDrawingField.GetValue(null) is not Dialog_FormCaravan proxy)
            {
                // MP's split window: VF would write seats and counts on this PC only. Nothing changes.
                if (SplitWindowDrawing())
                {
                    checkOn = wasOn;
                    Messages.Message(MpVehicleSplitRejectMessage, MessageTypeDefOf.RejectInput, false);
                }
                return;
            }

            // VF's Vehicles tab widget is static and rebuilt whenever a caravan window's transferables are made (also by another
            // player's start-forming command): a card of another session's vehicle changes nothing.
            var session = SessionOfProxy(proxy);
            if (transferable.AnyThing is not VehiclePawn vehicle || session == null || TransferableOf(session, vehicle) != transferable)
            {
                checkOn = wasOn;
                Messages.Message(session == null ? MpVehicleCaravanRejectMessage : MpVehicleOtherWindowMessage, MessageTypeDefOf.RejectInput, false);
                return;
            }

            // Tick: VF opens the seat window, its Confirm is a command. Untick: a command, VF's local branch does not run.
            if (checkOn)
                return;
            checkOn = wasOn;
            SyncedUntickVehicle(session, vehicle);
        }

        // Synced. VF's untick branch of DrawCard on the session's transferables: the vehicle, its seated pawns that are not in a
        // vehicle and the pawns aboard it leave the caravan, and its seats are cleared.
        private static void SyncedUntickVehicle(ISessionWithTransferables session, VehiclePawn vehicle)
        {
            if (session == null || vehicle == null)
                return;
            var vehicleTransferable = TransferableOf(session, vehicle);
            if (vehicleTransferable == null)
                return;

            vehicleTransferable.ForceTo(0);
            foreach (var seat in CaravanHelper.assignedSeats.GetAssignments(vehicle))
            {
                if (!seat.pawn.InVehicle())
                    TransferableOf(session, seat.pawn)?.ForceTo(0);
            }
            foreach (var pawn in vehicle.AllPawnsAboard)
                TransferableOf(session, pawn)?.ForceTo(0);
            CaravanHelper.assignedSeats.RemoveAssignments(vehicle);
            SortVehicleSeats();
            session.Notify_CountChanged(vehicleTransferable);
        }

        #endregion

        #region Send

        // The UI part of VF's send code (warnings, "are you sure", "leave vehicles behind?") runs on the clicking PC as in single
        // player. Each of its three game-state steps is replaced by one command. The window stays open; MP closes it on every PC
        // when the command ends the session.

        // Prefix on CaravanFormation.TrySendVehicleCaravan's local TryFormAndSendCaravan (also reached from its confirmation box).
        private static bool PreVfTryFormAndSendCaravan(ref bool __result)
        {
            if (!InMpInterface)
                return true;
            var dialog = CaravanFormation.formation?.Dialog;
            if (dialog != null && !IsCaravanFormingProxy(dialog))
                return true;
            __result = false;
            SendVehicleCaravanFromUI(dialog, SendForm);
            return false;
        }

        // Prefix on CaravanFormation.ReformInstantly.
        private static bool PreVfReformInstantly()
        {
            if (!InMpInterface)
                return true;
            var dialog = CaravanFormation.formation?.Dialog;
            if (dialog != null && !IsCaravanFormingProxy(dialog))
                return true;
            SendVehicleCaravanFromUI(dialog, SendReform);
            return false;
        }

        // Prefix on the "leave vehicles behind" confirmation lambda (instance method of the closure that holds formCaravan).
        private static bool PreVfLeaveVehiclesConfirmed(object __instance)
        {
            if (!InMpInterface)
                return true;
            var dialog = vfLeaveVehiclesDialogField.GetValue(__instance) as Dialog_FormCaravan;
            if (dialog != null && !IsCaravanFormingProxy(dialog))
                return true;
            SendVehicleCaravanFromUI(dialog, SendLeaveVehicles);
            return false;
        }

        private static void SendVehicleCaravanFromUI(Dialog_FormCaravan proxy, int mode)
        {
            var session = proxy == null ? null : SessionOfProxy(proxy);
            if (session == null)
            {
                Messages.Message(MpVehicleCaravanRejectMessage, MessageTypeDefOf.RejectInput, false);
                return;
            }

            SyncedSendVehicleCaravan(session, mode);
        }

        // Synced. Runs VF's own game-state step on MP's dummy dialog of the session (built from the session's synced state, as MP's own
        // TryFormAndSendCaravan / TryReformCaravan commands do), with CaravanFormation.formation pointing at that dialog for this call
        // only (the PC's own window formation is put back after). Success ends the session, as MP does.
        private static void SyncedSendVehicleCaravan(ISessionWithTransferables session, int mode)
        {
            if (session == null || session.GetType() != caravanFormingSessionType)
                return;
            var reform = (bool)sessionReformField.GetValue(session);
            if (mode is not (SendForm or SendReform or SendLeaveVehicles) || (mode == SendForm) == reform)
                return;
            var map = sessionMapField.GetValue(session) as Map;
            if (map == null || sessionTransferablesField.GetValue(session) is not List<TransferableOneWay> transferables)
                return;

            // A vehicle unticked after Send was pressed: MP's own send of a caravan without vehicles (a command run inside a command
            // executes directly).
            if (mode != SendLeaveVehicles && !AnyVehicleSelected(transferables))
            {
                (reform ? sessionTryReformCaravan : sessionTryFormAndSendCaravan).Invoke(session, null);
                return;
            }

            var dialog = (Dialog_FormCaravan)sessionPrepareDummyDialog.Invoke(session, null);
            var windowFormation = CaravanFormation.formation;
            var sent = false;
            // VF's seat list holds every map's seats, and its send code uses all of them (BoardAllAssignedPawns boards and clears all,
            // the lord job copies all). Seats of vehicles not in this session are set aside for the call and put back after.
            var otherSeats = CaravanHelper.assignedSeats.AllAssignments.Values.Where(s => s.Vehicle == null || TransferableOf(session, s.Vehicle) == null).ToList();
            if (otherSeats.Count > 0)
            {
                var otherPawns = otherSeats.Select(s => s.pawn).ToHashSet();
                CaravanHelper.assignedSeats.RemoveAll(otherPawns.Contains);
            }
            try
            {
                CaravanFormation.formation = new FormationInfo(dialog, map);
                CaravanFormation.formation.RecacheTransferables();
                switch (mode)
                {
                    case SendForm:
                        sent = (bool)vfTryFormAndSendCaravan.Invoke(null, null);
                        break;
                    case SendReform:
                        if (CaravanFormation.CheckForErrors())
                        {
                            CaravanFormation.ReformInstantly();
                            sent = true;
                        }
                        break;
                    case SendLeaveVehicles:
                        if (CaravanFormation.CheckForErrors())
                        {
                            var closure = Activator.CreateInstance(vfLeaveVehiclesConfirmed.DeclaringType!);
                            vfLeaveVehiclesDialogField.SetValue(closure, dialog);
                            vfLeaveVehiclesConfirmed.Invoke(closure, null);
                            sent = true;
                        }
                        break;
                }
            }
            finally
            {
                CaravanFormation.formation = windowFormation;
                foreach (var seat in otherSeats)
                {
                    if (seat.pawn != null && seat.Vehicle != null && !CaravanHelper.assignedSeats.IsAssigned(seat.pawn))
                        CaravanHelper.assignedSeats.SetAssignment(seat);
                }
                SortVehicleSeats();
            }

            if (sent)
                sessionRemove.Invoke(session, null);
        }

        #endregion

        #region VF statics

        // Prefix on Patch_FormCaravanDialog.FormCaravanPostClose(List<TabRecord> ___tabsList, bool ___choosingRoute), a static
        // postfix of Dialog_FormCaravan.PostClose (arguments read by position). In MP only one PC's window closes, and
        // CaravanHelper.assignedSeats is game state there: VF's UI resets run, the seat clear does not (PostCaravanSessionEnded
        // clears the session's seats on every PC).
        private static bool PreVfFormCaravanPostClose(List<TabRecord> __0, bool __1)
        {
            if (!MP.IsInMultiplayer)
                return true;
            if (!__1)
            {
                CaravanFormation.formation = null;
                __0?.Clear();
                Patch_FormCaravanDialog.selectedTab = Patch_FormCaravanDialog.TabVehicles;
            }

            return false;
        }

        // Prefix + finalizer on Patch_FormCaravanDialog.TryFormCaravanInstantly (VF's prefix of DebugTryFormCaravanInstantly): it reads
        // CaravanFormation.formation, which is one PC's window, and with a formation it creates the caravan right there. In MP it reads
        // null everywhere: on the clicking PC (MP's own prefix then sends its debug command) and inside that command on every PC
        // (vanilla instant form, as before row 358).
        private static void PreVfTryFormCaravanInstantly(out (bool swapped, FormationInfo formation) __state)
        {
            __state = (false, null);
            if (!MP.IsInMultiplayer)
                return;
            __state = (true, CaravanFormation.formation);
            CaravanFormation.formation = null;
        }

        private static void FinalVfTryFormCaravanInstantly((bool swapped, FormationInfo formation) __state)
        {
            if (__state.swapped)
                CaravanFormation.formation = __state.formation;
        }

        #endregion

        #region Seat list lifetime

        // CaravanHelper.assignedSeats in one order on every PC, whatever the history of adds and removes: vehicle id, seat (handler)
        // index, pawn id. VF iterates it (BoardAllAssignedPawns, the lord job's copy), and dictionary order depends on that history.
        private static void SortVehicleSeats()
        {
            var seats = CaravanHelper.assignedSeats;
            var all = SortedVehicleSeats();
            seats.Clear();
            foreach (var group in all.GroupBy(s => s.Vehicle))
                seats.SetAssignments(group.Key, group.ToList());
        }

        // A sorted copy; changes nothing.
        private static List<AssignedSeat> SortedVehicleSeats()
        {
            var all = CaravanHelper.assignedSeats.AllAssignments.Values.Where(s => s?.pawn != null && s.handler?.vehicle != null).ToList();
            all.Sort((a, b) =>
            {
                var result = a.Vehicle.thingIDNumber.CompareTo(b.Vehicle.thingIDNumber);
                if (result == 0)
                    result = a.Vehicle.handlers.IndexOf(a.handler).CompareTo(b.Vehicle.handlers.IndexOf(b.handler));
                return result != 0 ? result : a.pawn.thingIDNumber.CompareTo(b.pawn.thingIDNumber);
            });
            return all;
        }

        // Seats whose vehicle is in this session's transferables, in the fixed order. Read only (it runs while saving, which only the
        // host does).
        private static List<AssignedSeat> SeatsOfSession(ISessionWithTransferables session)
            => SortedVehicleSeats().Where(s => TransferableOf(session, s.Vehicle) != null).ToList();

        // Postfix on CaravanFormingSession.ExposeData: the session's seats go into the save (host autosave, join point), so a joiner
        // starts with the same seat list as everyone else. Applied in PostLoadInit, when the pawn / seat references are resolved.
        private static void PostCaravanSessionExposeData(object __instance)
        {
            if (__instance is not ISessionWithTransferables session)
                return;

            switch (Scribe.mode)
            {
                case LoadSaveMode.Saving:
                {
                    var seats = SeatsOfSession(session);
                    if (seats.Count > 0)
                        Scribe_Collections.Look(ref seats, VehicleSeatsSaveLabel, LookMode.Deep);
                    break;
                }
                case LoadSaveMode.LoadingVars:
                {
                    List<AssignedSeat> seats = null;
                    Scribe_Collections.Look(ref seats, VehicleSeatsSaveLabel, LookMode.Deep);
                    loadedVehicleSeats.Remove(__instance);
                    if (seats != null)
                        loadedVehicleSeats.Add(__instance, seats);
                    break;
                }
                case LoadSaveMode.PostLoadInit:
                {
                    if (!loadedVehicleSeats.TryGetValue(__instance, out var seats))
                        break;
                    loadedVehicleSeats.Remove(__instance);
                    foreach (var seat in seats)
                    {
                        if (seat?.pawn != null && seat.handler?.vehicle != null)
                            CaravanHelper.assignedSeats.SetAssignment(seat);
                    }
                    SortVehicleSeats();
                    break;
                }
            }
        }

        // Postfix on CaravanFormingSession.Remove and Reset (both run only inside MP commands; Reset is a sync method, whose postfix
        // also runs on the clicking PC when MP only sends the command - hence the IsExecutingSyncCommand check). The session's
        // vehicles lose their seats on every PC, as VF clears them when its window closes in single player. After a send the lord
        // job already holds its own copy.
        private static void PostCaravanSessionEnded(object __instance)
        {
            if (!MP.IsInMultiplayer || !MP.IsExecutingSyncCommand)
                return;
            if (sessionTransferablesField.GetValue(__instance) is not List<TransferableOneWay> transferables)
                return;

            var seats = CaravanHelper.assignedSeats;
            var changed = false;
            foreach (var transferable in transferables)
            {
                if (transferable?.AnyThing is VehiclePawn vehicle && seats.GetAssignments(vehicle).Count > 0)
                {
                    seats.RemoveAssignments(vehicle);
                    changed = true;
                }
            }

            if (changed)
                SortVehicleSeats();
        }

        // Prefix on Game.LoadGame and MemoryUtility.ClearAllMapsAndWorld: the seats belong to the game being left (they hold its
        // pawns); a loaded game gets its seats from its sessions only. MP's join, resync and rejoin reloads all pass here.
        // VF's window statics are dropped too: they hold the old game's dialogs (an MP reload replaces the UI without closing them).
        private static void ClearVehicleSeatsBeforeLoad()
        {
            CaravanHelper.assignedSeats.Clear();
            loadedVehicleSeats.Clear();
            CaravanFormation.formation = null;
            CaravanFormation.splitter = null;
        }

        #endregion

        #endregion
    }
}
