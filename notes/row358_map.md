# FIXES row 358 - VF vehicle caravans in MP's caravan window: state map

Sources read (2 Nov 2026): VF decompile `_lanes\d180_devmode_audit\decomp\3014915404__Vehicles__cceb717375d6`
(Vehicles.dll cceb7173 = References\Vehicles.dll), MP `src\Multiplayer` (CaravanFormingSession.cs, CaravanFormingProxy.cs,
CaravanFormingPatches.cs, SyncFields.cs:478), RimWorld 1.7 decomp (Dialog_FormCaravan, WorldRoutePlanner, Scribe_Collections).

MP facts: the proxy window and the session share ONE `transferables` list (PrepareDummyDialog / OpenWindow), so any
count write from the UI is a sim write. MP syncs counts only around `TransferableUIUtility.DoCountAdjustInterface`
(SyncTradeableCount watch) and through its own sync methods (Reset, ChooseRoute, TryFormAndSendCaravan, TryReformCaravan,
Cancel, SetAutoSelectTravelSupplies). VF's vehicle tab never calls DoCountAdjustInterface.

| # | State | Where (VF) | UI / sim | Synced before | Synced now (row 358) |
|---|---|---|---|---|---|
| 1 | `CaravanFormation.formation` (FormationInfo of the open window) | static; set in `CreateTabListPostOpen`, cleared in `FormCaravanPostClose` | UI (per PC) | n/a (step 9 forced it null) | stays UI; kept for the proxy while open; swapped to a FormationInfo of MP's dummy dialog only inside our synced commands; forced null inside MP's synced debug instant-form |
| 2 | `Patch_FormCaravanDialog.selectedTab`, tab list, `vehiclesTransfer` widget | statics | UI | n/a | unchanged |
| 3 | Vehicle tick on a card -> opens `Dialog_AssignSeats(CaravanFormation.Current, ...)` | `TransferableVehicleWidget.DrawCard` | UI (window only) | refused in MP (step 8) | allowed when Current is the proxy's FormationInfo |
| 4 | Seat window's own `assigner` (drag, auto-assign, clear) | `Dialog_AssignSeats` | UI | n/a | unchanged (local until Confirm) |
| 5 | Confirm (`FinalizeSeats`): pawn counts ForceTo(0/1), vehicle count AdjustTo(max/0), `CaravanHelper.assignedSeats.SetAssignments` | `Dialog_AssignSeats.FinalizeSeats` | SIM (shared transferables + static seats) | NO (and NRE at parent) | synced command `SyncedSetVehicleSeats(session, vehicle, pawn thing ids, handler indices)`; checks run on every PC |
| 6 | Vehicle untick: vehicle + seated/aboard pawn counts ForceTo(0), `assignedSeats.RemoveAssignments` | `DrawCard` untick branch | SIM | NO (row 285 made only the null notify safe) | synced command `SyncedUntickVehicle(session, vehicle)`; local branch never runs in MP's window |
| 7 | `CaravanHelper.assignedSeats` (pawn -> seat) | static `VehicleAssignment`, read by sim: LordJob_FormAndSendVehicles ctor (copies it), GatherItemsForVehicleCaravanUtility.IsUsableCarrier, caravan mass / capacity / visibility / forage patches | SIM, client-local static, not saved | NO; cleared on one PC by its own window close | written only by synced commands (5, 6, 9, 10) and by VF inside them; VF's close-time clear skipped in MP; kept in a fixed order (vehicle id, seat index, pawn id); saved in the session (`mpCompatVehicleSeats`), restored on load; emptied at every game load / MP reload |
| 8 | Change route with vehicles (VF `VehicleRoutePlanner`, writes the window's tiles) | `WorldRoutePannerReroute` | UI writes to proxy fields, never reaches the session | step 7: vanilla planner, MP-synced `ChooseRoute` | unchanged (vanilla planner; start tile is vanilla's, not VF's vehicle-aware one) |
| 9 | Send, form: warnings dialog (UI), then `TryFormAndSendCaravan` local func: DisembarkAll, exit/packing spot (Rand), `VehicleCaravanFormingUtility.StartFormingCaravan` (lord job, copies seats) | `CaravanFormation.TrySendVehicleCaravan` | warnings UI, local func SIM | NO (step 7 refused vehicle caravans) | UI part runs locally; the local func is replaced by synced `SyncedSendVehicleCaravan(session, 0)`, which runs VF's local func on every PC on MP's dummy dialog, then removes the session |
| 10 | Send, reform: `ReformInstantly` (board seats, items, `ExitMapAndCreateVehicleCaravan`, map removal) and the "leave vehicles behind" confirmation lambda | `CaravanFormation` | SIM | NO | synced `SyncedSendVehicleCaravan(session, 1 / 2)` |
| 11 | Session end (Send, Cancel, Reset, reform) | MP `CaravanFormingSession.Remove` / `Reset` | SIM | MP syncs; VF seats not cleared on other PCs | postfix: the session's vehicles lose their seats on every PC |
| 12 | Debug instant form (`TryFormCaravanInstantly` reads `formation`; with one, it creates the caravan on the spot) | VF prefix on `DebugTryFormCaravanInstantly` | SIM, on the clicking PC and inside MP's synced command | read one PC's UI formation | formation forced null for the call in MP, UI side and command (vanilla path on every PC, as before row 358) |
| 13 | Seat window / untick in MP's caravan SPLIT window (`Current` = SplitInfo) | `Dialog_AssignSeats`, `DrawCard` | SIM (seats + counts) | NO (before and after row 358) | refused in MP (message), nothing changes |
| 14 | Seats of other maps' open sessions during a send | `BoardAllAssignedPawns` (boards + clears all), lord job copy (all) | SIM | n/a | set aside for the command, put back after |
| 15 | Vehicles tab widget built from another session (static `vehiclesTransfer`, rebuilt by any start-forming command) or a window of an ended session | `Patch_FormCaravanDialog.CreateTransferableVehicleWidget` | UI | n/a | a card that is not in the window's own session (session id = proxy's `originalSessionId`) changes nothing, message shown |

Review (Opus 5.5, read-only, 2 Nov): found rows 12 (desync), 13 (desync), 14, 15 and the stale-session case; all fixed above.
Known limit, not fixed: "Change route" uses the vanilla planner, so the start tile is vanilla's, not VF's vehicle-aware one
(boats / impassable edges may give "could not find exit spot" on every PC - same on all PCs, not a desync).

Joiner / late join: the joiner loads the host's save. Seats of an open session are in that save (#7), so the joiner has
the same `assignedSeats` before its first tick. A joiner has no window open, so its `formation` is null (UI only).
