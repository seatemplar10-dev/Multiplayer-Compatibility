using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Tech Advancing by GHXX</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=735268789"/>
    /// <remarks>
    /// FIXES row 61. Written against the decompiled 1.6 TechAdvancing.dll that the 1.7 co-op loads.
    /// <c>TA_ResearchManager.Postfix</c> (Harmony postfix of <c>ResearchManager.ReapplyAllMods</c>, runs on research completion)
    /// sends the "new tech level" letter only when <c>DateTime.Now - 5 s &gt; startedAt</c>, and <c>startedAt</c> is the wall clock
    /// of the first call after <c>FlushCfg</c>. A joiner catches up at full speed, so the same research tick lands on a different
    /// side of the 5 s gate on each PC: the letter (and its letter ID) exists on one PC only.
    /// <c>WorldCompSaveHandler.ExposeData</c> also calls <c>FlushCfg</c> while SAVING, and Multiplayer's host autosave and the save
    /// window save on the host only, so the host's flags (<c>firstNotificationHidden</c>, <c>firstpass</c>) reset and the client's do not.
    /// Fix (MP only): the three <c>DateTime.Now</c> reads in <c>Postfix</c> and <c>FlushCfg</c> read a game clock built from
    /// <c>TicksGame</c> (60 ticks = 1 s), and <c>FlushCfg</c> does nothing while <c>Scribe.mode</c> is Saving. Loading still flushes,
    /// and a join point reloads every PC at the same tick.
    /// Fixup 15 Nov 2026 (review): <c>Postfix</c> also calls <c>FlushCfg()</c> when <c>Find.CurrentMap == null</c>. That is the viewed
    /// map of each PC (null on a spectator or the arbiter), so in MP that call does nothing. The load-time flush still happens on
    /// every PC, because <c>WorldCompSaveHandler.ExposeData</c> ends with <c>FlushCfg()</c> in every load pass. The rest of
    /// <c>Postfix</c> reads game state only: player faction def, finished research, world-saved config values, saved colony people.
    /// The patch log line gives the exact number of replaced calls (expected 2 clock reads and 1 FlushCfg call). Not run in game yet.
    /// </remarks>
    [MpCompatFor("ghxx.techadvancing")]
    public class TechAdvancing
    {
        private static readonly DateTime GameClockEpoch = new(2000, 1, 1);
        private static MethodInfo flushCfgMethod;
        private static Action flushCfg;

        public TechAdvancing(ModContentPack mod)
        {
            // Lab switch row 423 (18 Nov): -mplaboff=423 keeps the stock wall-clock letter gate (no patch at all).
            if (LabOff.Row(423)) return;
            var type = AccessTools.TypeByName("TechAdvancing.TA_ResearchManager");
            flushCfgMethod = AccessTools.DeclaredMethod(type, "FlushCfg");
            flushCfg = AccessTools.MethodDelegate<Action>(flushCfgMethod);
            var transpiler = new HarmonyMethod(typeof(TechAdvancing), nameof(ReplaceWallClock));

            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(type, "Postfix"), transpiler: transpiler);
            MpCompat.harmony.Patch(flushCfgMethod,
                prefix: new HarmonyMethod(typeof(TechAdvancing), nameof(PreFlushCfg)),
                transpiler: transpiler);
        }

        // Postfix reads DateTime.Now twice (startedAt on first pass, the 5 s letter gate) and calls FlushCfg once (CurrentMap == null).
        // FlushCfg reads DateTime.Now once (startedAt).
        private static IEnumerable<CodeInstruction> ReplaceWallClock(IEnumerable<CodeInstruction> instr, MethodBase baseMethod)
        {
            var isPostfix = baseMethod.Name == "Postfix";
            var getNow = AccessTools.DeclaredPropertyGetter(typeof(DateTime), nameof(DateTime.Now));
            var clockReplacement = AccessTools.DeclaredMethod(typeof(TechAdvancing), nameof(GameClockNow));
            var flushReplacement = AccessTools.DeclaredMethod(typeof(TechAdvancing), nameof(PostfixFlushCfg));
            // Looked up from the patched type, so the transpiler does not depend on the constructor having run (Tier 0 dry run).
            var flushTarget = AccessTools.DeclaredMethod(baseMethod.DeclaringType, "FlushCfg");
            var clockCount = 0;
            var flushCount = 0;

            foreach (var ci in instr)
            {
                if (ci.operand is MethodBase method)
                {
                    if (method == getNow)
                    {
                        ci.opcode = OpCodes.Call;
                        ci.operand = clockReplacement;
                        clockCount++;
                    }
                    else if (isPostfix && method == flushTarget)
                    {
                        ci.opcode = OpCodes.Call;
                        ci.operand = flushReplacement;
                        flushCount++;
                    }
                }

                yield return ci;
            }

            var expectedClock = isPostfix ? 2 : 1;
            var expectedFlush = isPostfix ? 1 : 0;
            var text = $"MPCompat :: Tech Advancing: TA_ResearchManager:{baseMethod.Name} replaced {clockCount} DateTime.Now call(s) " +
                       $"(expected {expectedClock}) and {flushCount} FlushCfg call(s) (expected {expectedFlush})";
            if (clockCount == expectedClock && flushCount == expectedFlush)
                Log.Message(text);
            else
                Log.Error(text + ". Mod changed?");
        }

        // Replaces Postfix's "if (Find.CurrentMap == null) FlushCfg();" call. CurrentMap is per PC in MP, so the flush is skipped there.
        private static void PostfixFlushCfg()
        {
            if (!MP.IsInMultiplayer)
                flushCfg();
        }

        // Replaces DateTime.Now. In MP: a fixed epoch plus the game time, the same on every PC at the same tick.
        private static DateTime GameClockNow()
        {
            if (!MP.IsInMultiplayer)
                return DateTime.Now;
            long ticksGame = Current.Game != null ? Find.TickManager.TicksGame : 0;
            return GameClockEpoch.AddTicks(ticksGame * TimeSpan.TicksPerSecond / GenTicks.TicksPerRealSecond);
        }

        // Saving changes nothing in the game, so it must not reset the static letter state on the saving PC only.
        private static bool PreFlushCfg() => !MP.IsInMultiplayer || Scribe.mode != LoadSaveMode.Saving;
    }
}
