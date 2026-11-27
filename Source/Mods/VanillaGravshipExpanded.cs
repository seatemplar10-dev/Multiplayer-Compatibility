using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Vanilla Gravship Expanded by Oskar Potocki, Taranchuk and others</summary>
    /// <remarks>
    /// FIXES row 483. VanillaGravshipExpanded.WorldComponent_GravshipController_TakeoffEnded_Patch.Prefix (Harmony prefix of
    /// WorldComponent_GravshipController.TakeoffEnded) adds a Dialog_MessageBox for the map the gravship left
    /// (TakeoffEnded_Patch.cs:39 "settle?" with SettleTile, :45 "keep / discard" with AbandonTile and SettleTile). TakeoffEnded is called
    /// from WorldComponentUpdate when the cutscene ends, on every PC (Multiplayer only freezes time and closes its session there), so every
    /// PC shows the dialog, and the button ran SettleInExistingMapUtility.Settle / MapParent.Abandon / DeinitAndRemoveMap on the clicking PC
    /// only. Fix: a message box built in MP with one of those two local functions is recorded under (map uniqueID, order for that map); the
    /// button sends SyncedMapDecision(mapId, seq, choice); the command runs that PC's own local function and closes the dialog on every PC.
    /// First command wins, later commands and later clicks on a decided dialog do nothing. The key is the map id, not the tick: the dialog
    /// opens from a frame update, not a tick. The prefix's direct AbandonTile() call (no dialog) is not recorded: unchanged.
    /// Row 483b: the dialog opens from WorldComponentUpdate per PC frame, so a command can arrive on a PC whose dialog is not open yet.
    /// Such a command is kept as a pending decision under the same key (map id, order) with the tick it arrived; when that PC's dialog
    /// with the key is built, the pending choice is applied once like a command and the dialog is closed when it is added to the window
    /// stack. First command still wins. A pending decision older than 2500 ticks is dropped with one warning; the lists clear on a new game.
    /// Status: [unverified] - zero-boot D2 test only, not run in game.
    /// </remarks>
    [MpCompatFor("vanillaexpanded.gravship")]
    public class VanillaGravshipExpanded
    {
        private const string LogPrefix = "[MPCompat - Vanilla Gravship Expanded] ";

        public VanillaGravshipExpanded(ModContentPack mod) => PatchMapDecision();

        private sealed class DecisionEntry
        {
            public Game game;
            public int mapId;
            public int seq;
            public object closure;
            public bool resolved;
            public Dialog_MessageBox box;
            public bool closeOnOpen;
        }

        private sealed class PendingDecision
        {
            public Game game;
            public int mapId;
            public int seq;
            public int choice;
            public int tick;
        }

        private const int PendingMaxAgeTicks = 2500;
        private static readonly List<DecisionEntry> entries = new List<DecisionEntry>();
        private static readonly List<PendingDecision> pending = new List<PendingDecision>();
        private static bool applyingPending;
        private static MethodInfo abandonTile;
        private static MethodInfo settleTile;
        private static FieldInfo mapField;

        internal static void PatchMapDecision()
        {
            if (LabOff.Row(483))
            {
                Log.Message(LogPrefix + "row 483 switched off by the lab, the takeoff map decision dialog is not synced");
                return;
            }
            abandonTile = settleTile = null;
            mapField = null;
            var patchType = AccessTools.TypeByName("VanillaGravshipExpanded.WorldComponent_GravshipController_TakeoffEnded_Patch");
            if (patchType != null)
            {
                foreach (var nested in patchType.GetNestedTypes(AccessTools.all))
                {
                    foreach (var m in nested.GetMethods(AccessTools.all))
                    {
                        if (m.IsStatic || m.ReturnType != typeof(void) || m.GetParameters().Length != 0 || m.DeclaringType != nested) continue;
                        if (m.Name.Contains("g__AbandonTile")) abandonTile = abandonTile == null ? m : throw new Exception("more than one AbandonTile in " + nested.FullName);
                        else if (m.Name.Contains("g__SettleTile")) settleTile = settleTile == null ? m : throw new Exception("more than one SettleTile in " + nested.FullName);
                    }
                }
            }
            if (abandonTile != null && settleTile != null && abandonTile.DeclaringType == settleTile.DeclaringType)
            {
                foreach (var f in abandonTile.DeclaringType.GetFields(AccessTools.all))
                    if (!f.IsStatic && f.FieldType == typeof(Map)) mapField = mapField == null ? f : throw new Exception("more than one Map field in " + f.DeclaringType.FullName);
            }
            var ctor = AccessTools.DeclaredConstructor(typeof(Dialog_MessageBox), new[]
            {
                typeof(TaggedString), typeof(string), typeof(Action), typeof(string), typeof(Action), typeof(string), typeof(bool),
                typeof(Action), typeof(Action), typeof(WindowLayer),
            });
            var windowAdd = AccessTools.DeclaredMethod(typeof(WindowStack), nameof(WindowStack.Add), new[] { typeof(Window) });
            if (abandonTile == null || settleTile == null || mapField == null || ctor == null || windowAdd == null)
            {
                Log.Error(LogPrefix + "row 483: TakeoffEnded_Patch AbandonTile/SettleTile closure, its Map field, the Dialog_MessageBox constructor or WindowStack.Add not found, the takeoff map decision can desync");
                return;
            }
            entries.Clear();
            pending.Clear();
            Multiplayer.API.MP.RegisterSyncMethod(typeof(VanillaGravshipExpanded), nameof(SyncedMapDecision));
            MpCompat.harmony.Patch(ctor, prefix: new HarmonyMethod(typeof(VanillaGravshipExpanded), nameof(PreMessageBoxCtor)));
            MpCompat.harmony.Patch(abandonTile, prefix: new HarmonyMethod(typeof(VanillaGravshipExpanded), nameof(PreAbandonTile)));
            MpCompat.harmony.Patch(settleTile, prefix: new HarmonyMethod(typeof(VanillaGravshipExpanded), nameof(PreSettleTile)));
            MpCompat.harmony.Patch(windowAdd, postfix: new HarmonyMethod(typeof(VanillaGravshipExpanded), nameof(PostWindowAdd)));
            Log.Message(LogPrefix + "row 483: takeoff map decision dialog (keep / discard / settle) synced, a command that arrives before this PC's dialog opens is kept until it opens");
        }

        private static bool IsOurs(Action a) => a != null && (a.Method == abandonTile || a.Method == settleTile);

        private static int TicksNow() => Current.Game?.tickManager?.TicksGame ?? 0;

        // Drops entries and pending decisions of another game, and pending decisions older than PendingMaxAgeTicks (one warning each).
        private static void Prune(Game game)
        {
            entries.RemoveAll(e => !ReferenceEquals(e.game, game));
            pending.RemoveAll(p => !ReferenceEquals(p.game, game));
            var now = TicksNow();
            for (var i = pending.Count - 1; i >= 0; i--)
            {
                var p = pending[i];
                if (now - p.tick <= PendingMaxAgeTicks) continue;
                pending.RemoveAt(i);
                Log.Warning(LogPrefix + $"row 483: takeoff map decision for map {p.mapId} #{p.seq} (choice {p.choice}, tick {p.tick}) dropped, this PC's dialog did not open within {PendingMaxAgeTicks} ticks");
            }
        }

        internal static void PreMessageBoxCtor(Dialog_MessageBox __instance, Action buttonAAction, Action buttonBAction)
        {
            if (!Multiplayer.API.MP.IsInMultiplayer) return;
            var closure = IsOurs(buttonAAction) ? buttonAAction.Target : IsOurs(buttonBAction) ? buttonBAction.Target : null;
            if (closure == null) return;
            var game = Current.Game;
            Prune(game);
            if (entries.Exists(e => ReferenceEquals(e.closure, closure))) return;
            var mapId = (mapField.GetValue(closure) as Map)?.uniqueID ?? -1;
            var seq = entries.FindAll(e => e.mapId == mapId).Count;
            var entry = new DecisionEntry { game = game, mapId = mapId, seq = seq, closure = closure, box = __instance };
            entries.Add(entry);
            var late = pending.Find(p => p.mapId == mapId && p.seq == seq);
            if (late == null) return;
            pending.Remove(late);
            entry.closeOnOpen = true;
            applyingPending = true;
            try
            {
                Apply(entry, late.choice);
            }
            catch (Exception e)
            {
                Log.Error(LogPrefix + $"row 483: applying the pending takeoff map decision for map {mapId} #{seq} failed: {e}");
            }
            finally
            {
                applyingPending = false;
            }
        }

        // The dialog whose pending decision was applied while it was being built is closed as soon as it is on the window stack.
        internal static void PostWindowAdd(Window window)
        {
            if (!(window is Dialog_MessageBox box)) return;
            var entry = entries.Find(e => e.closeOnOpen && ReferenceEquals(e.box, box));
            if (entry == null) return;
            entry.closeOnOpen = false;
            box.Close(false);
        }

        internal static bool PreAbandonTile(object __instance) => PreChoice(__instance, 0);

        internal static bool PreSettleTile(object __instance) => PreChoice(__instance, 1);

        private static bool PreChoice(object closure, int choice)
        {
            if (!Multiplayer.API.MP.IsInMultiplayer || Multiplayer.API.MP.IsExecutingSyncCommand || applyingPending) return true;
            var entry = entries.Find(e => ReferenceEquals(e.closure, closure) && ReferenceEquals(e.game, Current.Game));
            if (entry == null) return true;          // direct AbandonTile() from the prefix (no dialog): unchanged
            if (!entry.resolved) SyncedMapDecision(entry.mapId, entry.seq, choice);
            return false;
        }

        internal static void SyncedMapDecision(int mapId, int seq, int choice)
        {
            if (choice != 0 && choice != 1) return;
            var game = Current.Game;
            Prune(game);
            var entry = entries.Find(e => e.mapId == mapId && e.seq == seq);
            if (entry == null)
            {
                // This PC's dialog is not open yet (it opens from a frame update): keep the first command until it opens.
                if (!pending.Exists(p => p.mapId == mapId && p.seq == seq))
                    pending.Add(new PendingDecision { game = game, mapId = mapId, seq = seq, choice = choice, tick = TicksNow() });
                return;
            }
            Apply(entry, choice);
        }

        private static void Apply(DecisionEntry entry, int choice)
        {
            if (entry.resolved) return;
            entry.resolved = true;
            (choice == 0 ? abandonTile : settleTile).Invoke(entry.closure, null);
            var windows = Current.Root?.uiRoot?.windows?.Windows;
            if (windows == null) return;
            foreach (var window in new List<Window>(windows))
                if (window is Dialog_MessageBox box && (ReferenceEquals(box.buttonAAction?.Target, entry.closure) || ReferenceEquals(box.buttonBAction?.Target, entry.closure)))
                    box.Close(false);
        }
    }
}
