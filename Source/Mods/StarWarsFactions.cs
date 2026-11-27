using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Star Wars - Factions (Continued) by Mlie</summary>
    /// <remarks>
    /// FIXES row 488. ImperialTax_Data.WorldComponentTick (every 1000 ticks, on every PC) -> resolveMeetingGalacticEmpire ->
    /// sendFirstMeetingDialog adds a Dialog_NodeTree. Its options' actions (Accept: taxedColony = true; Refuse:
    /// resolveDeclarationOfHostility = Galactic Empire goodwill -100, then an OK node) ran on the clicking PC only, so the tax deal and
    /// the goodwill differed between PCs. Fix: in MP a Dialog_NodeTree built in the simulation whose root options use the
    /// sendFirstMeetingDialog lambdas is recorded under (tick, order in that tick), the same on every PC. A click on any of its options
    /// runs nothing and sends one SyncedMeetingOption(tick, seq, step, index); the command activates option `index` of the dialog's
    /// current node on every PC (that PC's own closure, the same imperial pawn and world component). step = options applied to this
    /// dialog so far: a command for a node the dialog has already left (the other PC clicked first) does nothing, and so does any click
    /// or command once the tree is resolved. Single player and dialogs from other mods are unchanged.
    /// Not synced: a dialog opened before a player joined (the joiner has no record for the key).
    /// Status: [unverified] - zero-boot D2 test only, not run in game.
    /// </remarks>
    [MpCompatFor("Mlie.StarWarsFactions")]
    public class StarWarsFactions
    {
        private const string LogPrefix = "[MPCompat - Star Wars Factions] ";

        public StarWarsFactions(ModContentPack mod) => PatchMeetingDialog();

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

        internal static void PatchMeetingDialog()
        {
            if (LabOff.Row(488))
            {
                Log.Message(LogPrefix + "row 488 switched off by the lab, the first meeting dialog with the Galactic Empire is not synced");
                return;
            }
            lambdas.Clear();
            var type = AccessTools.TypeByName("SWFactions.ImperialTax_Data");
            if (type != null)
            {
                foreach (var t in new[] { type }.Concat(type.GetNestedTypes(AccessTools.all)))
                    foreach (var m in t.GetMethods(AccessTools.all))
                        if (m.DeclaringType == t && m.Name.Contains("<sendFirstMeetingDialog>") && m.ReturnType == typeof(void) && m.GetParameters().Length == 0)
                            lambdas.Add(m);
            }
            optionDialog = AccessTools.Field(typeof(DiaOption), "dialog");
            curNode = AccessTools.Field(typeof(Dialog_NodeTree), "curNode");
            activate = AccessTools.Method(typeof(DiaOption), "Activate", Type.EmptyTypes);
            var ctors = AccessTools.GetDeclaredConstructors(typeof(Dialog_NodeTree))
                .FindAll(c => c.GetParameters().Length > 0 && c.GetParameters()[0].ParameterType == typeof(DiaNode));
            if (lambdas.Count < 2 || optionDialog == null || curNode == null || activate == null || ctors.Count == 0)
            {
                Log.Error(LogPrefix + "row 488: sendFirstMeetingDialog option lambdas, DiaOption.dialog/Activate or the Dialog_NodeTree constructor not found, the first meeting dialog can desync");
                return;
            }
            MP.RegisterSyncMethod(typeof(StarWarsFactions), nameof(SyncedMeetingOption));
            foreach (var c in ctors)
                MpCompat.harmony.Patch(c, postfix: new HarmonyMethod(typeof(StarWarsFactions), nameof(PostNodeTreeCtor)));
            MpCompat.harmony.Patch(activate, prefix: new HarmonyMethod(typeof(StarWarsFactions), nameof(PreActivate)));
            Log.Message(LogPrefix + "row 488: first meeting dialog with the Galactic Empire (accept tax / refuse) synced");
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
            if (entry == null) return true;          // not the meeting dialog: unchanged
            if (entry.resolved) return false;        // already decided by a command
            if (MP.IsExecutingSyncCommand)
            {
                entry.step++;
                if (__instance.resolveTree) entry.resolved = true;
                return true;
            }
            var index = (curNode.GetValue(dialog) as DiaNode)?.options?.IndexOf(__instance) ?? -1;
            if (index >= 0) SyncedMeetingOption(entry.tick, entry.seq, entry.step, index);
            return false;
        }

        internal static void SyncedMeetingOption(int tick, int seq, int step, int index)
        {
            var entry = entries.Find(e => e.tick == tick && e.seq == seq && ReferenceEquals(e.game, Current.Game));
            if (entry == null)
            {
                Log.Warning(LogPrefix + $"row 488: no first meeting dialog {tick} #{seq} on this PC, option {index} not applied");
                return;
            }
            if (entry.resolved || entry.step != step) return;
            var options = (curNode.GetValue(entry.dialog) as DiaNode)?.options;
            if (options == null || index < 0 || index >= options.Count) return;
            activate.Invoke(options[index], null);
        }
    }
}
