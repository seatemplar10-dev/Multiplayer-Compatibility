using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Vanilla Factions Expanded - Deserters by Oskar Potocki and others</summary>
    /// <remarks>
    /// FIXES row 489. IncidentWorker_ImperialPatrol.TryExecuteWorker (incident on a caravan, run in the simulation on every PC) adds a
    /// Dialog_NodeTreeWithFactionInfo with three options: Escape (Utilities.ChangeVisibility(10)), Attack (long event:
    /// CaravanIncidentUtility.SetupCaravanAttackMap + assault lord) and Give up a pawn (caravan.RemovePawn + empire.kidnapped.Kidnap).
    /// The option actions ran on the clicking PC only, so each PC ran its own choice. Multiplayer syncs node tree options only for the
    /// methods registered with RegisterSyncDialogNodeTree; this one is not. Fix: in MP a Dialog_NodeTree built in the simulation whose
    /// root options use the TryExecuteWorker lambdas is recorded under (tick, order in that tick), the same on every PC. A click on any of
    /// its options runs nothing and sends one SyncedPatrolOption(tick, seq, step, index); the command activates option `index` of the
    /// dialog's current node on every PC (that PC's own closure: the same caravan, pawn and incident parms). step = options applied to
    /// this dialog so far: a command for a node the dialog has already left (the other PC clicked first) does nothing, and so does any
    /// click or command once the tree is resolved. Single player and dialogs from other mods are unchanged.
    /// Not synced: a dialog opened before a player joined (the joiner has no record for the key).
    /// Status: [unverified] - zero-boot D2 test only, not run in game.
    /// </remarks>
    [MpCompatFor("OskarPotocki.VFE.Deserters")]
    public class VanillaFactionsDeserters
    {
        private const string LogPrefix = "[MPCompat - VFE Deserters] ";

        public VanillaFactionsDeserters(ModContentPack mod) => PatchPatrolDialog();

        private sealed class DialogEntry
        {
            public Game game;
            public int tick;
            public int seq;
            public object dialog;
            public int step;
            public bool resolved;
        }

        private static readonly List<DialogEntry> entries = new List<DialogEntry>();
        private static readonly List<MethodInfo> lambdas = new List<MethodInfo>();
        private static Game entryGame;
        private static int entryTick = int.MinValue;
        private static int entrySeq;
        private static FieldInfo optionDialog;
        private static FieldInfo curNode;
        private static MethodInfo activate;

        internal static void PatchPatrolDialog()
        {
            if (LabOff.Row(489))
            {
                Log.Message(LogPrefix + "row 489 switched off by the lab, the imperial patrol caravan dialog is not synced");
                return;
            }
            lambdas.Clear();
            var type = AccessTools.TypeByName("VFED.IncidentWorker_ImperialPatrol");
            if (type != null)
            {
                foreach (var t in new[] { type }.Concat(type.GetNestedTypes(AccessTools.all)))
                    foreach (var m in t.GetMethods(AccessTools.all))
                        if (m.DeclaringType == t && m.Name.Contains("<TryExecuteWorker>") && m.ReturnType == typeof(void) && m.GetParameters().Length == 0)
                            lambdas.Add(m);
            }
            optionDialog = AccessTools.Field(typeof(DiaOption), "dialog");
            curNode = AccessTools.Field(typeof(Dialog_NodeTree), "curNode");
            activate = AccessTools.Method(typeof(DiaOption), "Activate", Type.EmptyTypes);
            var ctors = AccessTools.GetDeclaredConstructors(typeof(Dialog_NodeTree))
                .FindAll(c => c.GetParameters().Length > 0 && c.GetParameters()[0].ParameterType == typeof(DiaNode));
            if (lambdas.Count < 2 || optionDialog == null || curNode == null || activate == null || ctors.Count == 0)
            {
                Log.Error(LogPrefix + "row 489: IncidentWorker_ImperialPatrol.TryExecuteWorker option lambdas, DiaOption.dialog/Activate or the Dialog_NodeTree constructor not found, the imperial patrol dialog can desync");
                return;
            }
            MP.RegisterSyncMethod(typeof(VanillaFactionsDeserters), nameof(SyncedPatrolOption));
            foreach (var c in ctors)
                MpCompat.harmony.Patch(c, postfix: new HarmonyMethod(typeof(VanillaFactionsDeserters), nameof(PostNodeTreeCtor)));
            MpCompat.harmony.Patch(activate, prefix: new HarmonyMethod(typeof(VanillaFactionsDeserters), nameof(PreActivate)));
            Log.Message(LogPrefix + "row 489: imperial patrol caravan dialog (escape / attack / give up a pawn) synced");
        }

        private static bool IsOurLambda(Action action)
        {
            var m = action?.Method;
            return m != null && lambdas.Exists(l => l.MetadataToken == m.MetadataToken && l.Module == m.Module);
        }

        internal static void PostNodeTreeCtor(Dialog_NodeTree __instance, DiaNode nodeRoot)
        {
            if (!MP.IsInMultiplayer || MP.InInterface || nodeRoot?.options == null) return;
            if (!nodeRoot.options.Exists(o => o != null && IsOurLambda(o.action))) return;
            var game = Current.Game;
            var tick = game?.tickManager?.TicksGame ?? -1;
            if (!ReferenceEquals(game, entryGame) || tick != entryTick)
            {
                entryGame = game;
                entryTick = tick;
                entrySeq = 0;
            }
            entries.RemoveAll(e => !ReferenceEquals(e.game, game));
            entries.Add(new DialogEntry { game = game, tick = tick, seq = entrySeq++, dialog = __instance });
            if (entries.Count > 64) entries.RemoveAt(0);
        }

        internal static bool PreActivate(DiaOption __instance)
        {
            if (!MP.IsInMultiplayer) return true;
            var dialog = optionDialog.GetValue(__instance);
            if (dialog == null) return true;
            var entry = entries.Find(e => ReferenceEquals(e.dialog, dialog) && ReferenceEquals(e.game, Current.Game));
            if (entry == null) return true;          // not the patrol dialog: unchanged
            if (entry.resolved) return false;        // already decided by a command
            if (MP.IsExecutingSyncCommand)
            {
                entry.step++;
                if (__instance.resolveTree) entry.resolved = true;
                return true;
            }
            var index = (curNode.GetValue(dialog) as DiaNode)?.options?.IndexOf(__instance) ?? -1;
            if (index >= 0) SyncedPatrolOption(entry.tick, entry.seq, entry.step, index);
            return false;
        }

        internal static void SyncedPatrolOption(int tick, int seq, int step, int index)
        {
            var entry = entries.Find(e => e.tick == tick && e.seq == seq && ReferenceEquals(e.game, Current.Game));
            if (entry == null)
            {
                Log.Warning(LogPrefix + $"row 489: no imperial patrol dialog {tick} #{seq} on this PC, option {index} not applied");
                return;
            }
            if (entry.resolved || entry.step != step) return;
            var options = (curNode.GetValue(entry.dialog) as DiaNode)?.options;
            if (options == null || index < 0 || index >= options.Count) return;
            activate.Invoke(options[index], null);
        }
    }
}
