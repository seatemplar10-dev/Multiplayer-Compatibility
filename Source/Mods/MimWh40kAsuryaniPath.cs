using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>MIM - WH40k Core: Asuryani path choice dialogs (FIXES row 465)</summary>
    /// <remarks>
    /// Hediff_AsuryaniPath.DailyPathReview (hediff tick) and Utility_AsuryaniPath.TryPromoteToExarch call
    /// Utility_AsuryaniPath.ShowConsumedChoiceDialog / ShowExarchChoiceDialog, which add a Dialog_MessageBox through
    /// Utility_AsuryaniPath.ShowChoiceDialog. That runs in the simulation, so every PC shows the dialog, but the accept / decline
    /// lambdas (accept: currentTracker.level = Consumed or Exarch + ApplyLevelEffects; decline: progress, level, path end, thought,
    /// next path or outcast) ran only on the PC that clicked. Fix: every ShowChoiceDialog call made in the simulation in MP records
    /// its two delegates under (tick, order in that tick), the same on every PC. A click on accept or decline sends one synced
    /// SyncedAsuryaniChoice(tick, seq, accept); the command runs that PC's own lambda (its closure holds the same pawn, hediff, path,
    /// old level and threshold) and closes the dialog on every PC. The first command wins; a later command for the same dialog (the
    /// other PC clicked before the first arrived) and any later click on a dialog already decided do nothing.
    /// Not synced: a dialog opened before a player joined (the joiner has no record for the key).
    /// </remarks>
    [MpCompatFor("emitbreaker.MIM.WH40k.Core")]
    internal class MimWh40kAsuryaniPath
    {
        private sealed class ChoiceEntry
        {
            public Game game;
            public int tick;
            public int seq;
            public Action onAccept;
            public Action onDecline;
            public bool resolved;
        }

        private static readonly List<ChoiceEntry> entries = new List<ChoiceEntry>();
        private static Game entryGame;
        private static int entryTick = int.MinValue;
        private static int entrySeq;

        public MimWh40kAsuryaniPath(ModContentPack mod) => Patch();

        internal static void Patch()
        {
            if (LabOff.Row(465))
            {
                Log.Message("[MPCompat - MIM WH40k Core] row 465 switched off by the lab, Asuryani path choice dialogs not synced");
                return;
            }
            var utilType = AccessTools.TypeByName("MIM40kFactions.Aeldari.Utility_AsuryaniPath");
            var show = utilType == null ? null : AccessTools.DeclaredMethod(utilType, "ShowChoiceDialog");
            var lambdas = new List<MethodInfo>();
            if (utilType != null)
            {
                foreach (var parent in new[] { "ShowConsumedChoiceDialog", "ShowExarchChoiceDialog" })
                {
                    for (var i = 0; i < 2; i++)
                    {
                        MethodInfo lambda = null;
                        try { lambda = MpMethodUtil.GetLambda(utilType, parent, lambdaOrdinal: i); }
                        catch (Exception) { }
                        if (lambda != null && lambda.ReturnType == typeof(void) && lambda.GetParameters().Length == 0 && !lambda.IsStatic) lambdas.Add(lambda);
                    }
                }
            }
            var showParams = show?.GetParameters();
            if (show == null || showParams.Length < 2 || showParams[showParams.Length - 1].ParameterType != typeof(Action)
                || showParams[showParams.Length - 2].ParameterType != typeof(Action) || lambdas.Count != 4)
            {
                Log.Error("[MPCompat - MIM WH40k Core] row 465: Utility_AsuryaniPath.ShowChoiceDialog or its accept/decline lambdas not found, the Asuryani path choice can desync");
                return;
            }
            MP.RegisterSyncMethod(typeof(MimWh40kAsuryaniPath), nameof(SyncedAsuryaniChoice));
            MpCompat.harmony.Patch(show, prefix: new HarmonyMethod(typeof(MimWh40kAsuryaniPath), nameof(PreShowChoiceDialog)));
            foreach (var lambda in lambdas)
                MpCompat.harmony.Patch(lambda, prefix: new HarmonyMethod(typeof(MimWh40kAsuryaniPath), nameof(PreChoiceLambda)));
            Log.Message("[MPCompat - MIM WH40k Core] row 465: Asuryani path choice dialogs (Consumed, Exarch) synced");
        }

        internal static void PreShowChoiceDialog(Action onAccept, Action onDecline)
        {
            if (!MP.IsInMultiplayer || MP.InInterface || onAccept == null || onDecline == null) return;
            var game = Current.Game;
            var tick = game?.tickManager?.TicksGame ?? -1;
            if (!ReferenceEquals(game, entryGame) || tick != entryTick)
            {
                entryGame = game;
                entryTick = tick;
                entrySeq = 0;
            }
            entries.RemoveAll(e => !ReferenceEquals(e.game, game));
            entries.Add(new ChoiceEntry { game = game, tick = tick, seq = entrySeq++, onAccept = onAccept, onDecline = onDecline });
            if (entries.Count > 64) entries.RemoveAt(0);
        }

        private static ChoiceEntry EntryOf(object target, MethodBase method, out bool accept)
        {
            accept = false;
            foreach (var e in entries)
            {
                if (!ReferenceEquals(e.game, Current.Game)) continue;
                if (ReferenceEquals(e.onAccept.Target, target) && e.onAccept.Method == method) { accept = true; return e; }
                if (ReferenceEquals(e.onDecline.Target, target) && e.onDecline.Method == method) return e;
            }
            return null;
        }

        internal static bool PreChoiceLambda(object __instance, MethodBase __originalMethod)
        {
            if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand) return true;
            var entry = EntryOf(__instance, __originalMethod, out var accept);
            if (entry == null) return true;          // not from a simulation dialog: unchanged
            if (entry.resolved) return false;        // already decided by a command
            SyncedAsuryaniChoice(entry.tick, entry.seq, accept);
            return false;
        }

        internal static void SyncedAsuryaniChoice(int tick, int seq, bool accept)
        {
            ChoiceEntry entry = null;
            foreach (var e in entries)
                if (e.tick == tick && e.seq == seq && ReferenceEquals(e.game, Current.Game)) { entry = e; break; }
            if (entry == null || entry.resolved) return;
            entry.resolved = true;
            (accept ? entry.onAccept : entry.onDecline)();
            var windows = Current.Root?.uiRoot?.windows?.Windows;
            if (windows == null) return;
            foreach (var window in new List<Window>(windows))
            {
                if (window is Dialog_MessageBox box && (box.buttonAAction == entry.onAccept || box.buttonBAction == entry.onDecline))
                    box.Close(false);
            }
        }
    }
}
