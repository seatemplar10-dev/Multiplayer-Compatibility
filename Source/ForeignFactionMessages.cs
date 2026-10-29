using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;
using Verse.Sound;

namespace Multiplayer.Compat
{
    /// <summary>
    /// FIXES row 305: the joiner sees the host's tick- and command-driven messages ("Auto Fabrication Complete", "Bill complete",
    /// "X has finished processing", ...). A mod that calls Messages.Message from the simulation (a bill or process finishing in
    /// CompTick / Tick, a building's Notify_FormingCompleted) or from a command handler runs that call on every PC. MP drops only the
    /// non-historical messages of a command issued by another player (SilenceMessagesNotTargetedAtMe, Feedback.cs). Letters get a
    /// per-faction filter (LetterStackReceiveOnlyMyFaction), live messages get none.
    /// </summary>
    /// <remarks>
    /// Rule: a message created in the simulation or while a command runs is hidden on this PC when the context is another
    /// player's: a command issued by another player, or a tick / command whose Faction.OfPlayer is a player faction other than this
    /// PC's own (MP sets Faction.OfPlayer to the owning faction around a map tick, the same test MP uses for letters).
    /// Interface code, world ticks (Faction.OfPlayer is the Spectator) and single player are not touched.
    ///
    /// Desync safety: the message is NOT dropped, it is only not drawn and not played. Messages.Message runs as in vanilla on every
    /// PC: the same duplicate check (AcceptsMessage reads liveMessages), the same Message constructor (GetNextMessageID, which MP's
    /// stack tracer records), the same Archive.Add, the same liveMessages entry. The patch changes only the draw of a hidden
    /// message and the sound call. Dropping the call in a prefix (MP's own pattern for command messages) would let a repeat of the
    /// same text and targets inside the 13 s message lifetime pass AcceptsMessage on this PC but not on the owner's, so one PC
    /// would call GetNextMessageID and the other would not. Message ids, the archive and liveMessages never feed Rand, Thing ids or
    /// any def, so a hidden message changes no simulation state.
    /// </remarks>
    internal static class ForeignFactionMessages
    {
        // UI only: messages created in another player's context. Weak keys, so an expired message is collected.
        private static readonly ConditionalWeakTable<Message, object> hidden = new ConditionalWeakTable<Message, object>();
        private static readonly object marker = new object();

        // MessagesDoGUI calls DrawOrSkip then adds the step; a hidden message adds none, so the visible stack has no gap.
        private static bool lastSkipped;

        internal static void Patch(Harmony harmony)
        {
            try
            {
                var messagesMessage = AccessTools.Method(typeof(Messages), nameof(Messages.Message), new[] { typeof(Message), typeof(bool) });
                var acceptsMessage = AccessTools.Method(typeof(Messages), "AcceptsMessage", new[] { typeof(string), typeof(LookTargets) });
                var doGui = AccessTools.Method(typeof(Messages), nameof(Messages.MessagesDoGUI));
                if (messagesMessage == null || acceptsMessage == null || doGui == null)
                {
                    Log.Warning($"[MPCompat] row 305: Messages API not as expected (Message {messagesMessage != null}, AcceptsMessage {acceptsMessage != null}, MessagesDoGUI {doGui != null}); foreign-faction messages stay visible");
                    return;
                }

                harmony.Patch(messagesMessage,
                    postfix: new HarmonyMethod(typeof(ForeignFactionMessages), nameof(MessagePostfix)),
                    transpiler: new HarmonyMethod(typeof(ForeignFactionMessages), nameof(SoundTranspiler)));
                harmony.Patch(acceptsMessage, transpiler: new HarmonyMethod(typeof(ForeignFactionMessages), nameof(SoundTranspiler)));
                harmony.Patch(doGui, transpiler: new HarmonyMethod(typeof(ForeignFactionMessages), nameof(DoGuiTranspiler)));
            }
            catch (Exception e)
            {
                Log.Error($"[MPCompat] row 305: could not patch Messages, foreign-faction messages stay visible: {e}");
            }
        }

        /// <summary>True while the code that runs belongs to another player: their command, or a tick / command in their faction.</summary>
        internal static bool IsForeignContext()
        {
            if (!MP.IsInMultiplayer || MP.InInterface)
                return false;

            // A command that another player issued (it runs on every PC with the issuer's faction as Faction.OfPlayer).
            if (MP.IsExecutingSyncCommand && !MP.IsExecutingSyncCommandIssuedBySelf)
                return true;

            var mine = MP.RealPlayerFaction;
            var context = Faction.OfPlayerSilentFail;
            if (mine == null || context == null || context == mine || !context.IsPlayer)
                return false;

            // World ticks run as the Spectator faction. Its messages (caravans, quests, incidents) stay visible to everybody.
            return context.Name != "Spectator";
        }

        private static void MessagePostfix(Message msg)
        {
            if (msg != null && IsForeignContext() && Messages.IsLive(msg))
            {
                try { hidden.Add(msg, marker); } catch (ArgumentException) { }
            }
        }

        private static bool IsHidden(Message msg) => msg != null && hidden.TryGetValue(msg, out _);

        // Replaces SoundStarter.PlayOneShotOnCamera(SoundDef, Map): same signature, silent for a foreign message.
        private static void PlaySound(SoundDef sound, Map onlyThisMap)
        {
            if (!IsForeignContext())
                sound.PlayOneShotOnCamera(onlyThisMap);
        }

        private static IEnumerable<CodeInstruction> SoundTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var target = AccessTools.Method(typeof(SoundStarter), nameof(SoundStarter.PlayOneShotOnCamera), new[] { typeof(SoundDef), typeof(Map) });
            var replacement = AccessTools.Method(typeof(ForeignFactionMessages), nameof(PlaySound));
            int replaced = 0;
            foreach (var instruction in instructions)
            {
                if (target != null && instruction.operand is MethodInfo method && method == target)
                {
                    replaced++;
                    yield return new CodeInstruction(OpCodes.Call, replacement) { labels = instruction.labels, blocks = instruction.blocks };
                }
                else
                {
                    yield return instruction;
                }
            }

            if (replaced == 0)
                Log.Warning("[MPCompat] row 305: no PlayOneShotOnCamera call found in a Messages method; its sound is not gated");
        }

        private static void DrawOrSkip(Message msg, int xOffset, int yOffset)
        {
            lastSkipped = IsHidden(msg);
            if (!lastSkipped)
                msg.Draw(xOffset, yOffset);
        }

        private static int StepAfterDraw() => lastSkipped ? 0 : 26;

        private static IEnumerable<CodeInstruction> DoGuiTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var draw = AccessTools.Method(typeof(Message), nameof(Message.Draw));
            var drawOrSkip = AccessTools.Method(typeof(ForeignFactionMessages), nameof(DrawOrSkip));
            var step = AccessTools.Method(typeof(ForeignFactionMessages), nameof(StepAfterDraw));
            bool afterDraw = false;
            int drawsReplaced = 0, stepsReplaced = 0;
            foreach (var instruction in instructions)
            {
                if (draw != null && instruction.operand is MethodInfo method && method == draw)
                {
                    drawsReplaced++;
                    afterDraw = true;
                    yield return new CodeInstruction(OpCodes.Call, drawOrSkip) { labels = instruction.labels, blocks = instruction.blocks };
                }
                else if (afterDraw && instruction.LoadsConstant(26))
                {
                    stepsReplaced++;
                    afterDraw = false;
                    yield return new CodeInstruction(OpCodes.Call, step) { labels = instruction.labels, blocks = instruction.blocks };
                }
                else
                {
                    yield return instruction;
                }
            }

            if (drawsReplaced != 1 || stepsReplaced != 1)
                Log.Warning($"[MPCompat] row 305: MessagesDoGUI shape differs (Draw calls {drawsReplaced}, steps {stepsReplaced}); hidden messages may leave a gap");
        }
    }
}
