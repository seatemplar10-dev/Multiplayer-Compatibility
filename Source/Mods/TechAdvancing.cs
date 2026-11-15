using System;
using System.Collections.Generic;
using System.Reflection;
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
    /// and a join point reloads every PC at the same tick. Not run in game yet.
    /// </remarks>
    [MpCompatFor("ghxx.techadvancing")]
    public class TechAdvancing
    {
        private static readonly DateTime GameClockEpoch = new(2000, 1, 1);

        public TechAdvancing(ModContentPack mod)
        {
            var type = AccessTools.TypeByName("TechAdvancing.TA_ResearchManager");
            var transpiler = new HarmonyMethod(typeof(TechAdvancing), nameof(ReplaceWallClock));

            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(type, "Postfix"), transpiler: transpiler);
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(type, "FlushCfg"),
                prefix: new HarmonyMethod(typeof(TechAdvancing), nameof(PreFlushCfg)),
                transpiler: transpiler);
        }

        // Postfix reads DateTime.Now twice (startedAt on first pass, the 5 s letter gate), FlushCfg once (startedAt).
        private static IEnumerable<CodeInstruction> ReplaceWallClock(IEnumerable<CodeInstruction> instr, MethodBase baseMethod)
            => instr.ReplaceMethod(AccessTools.DeclaredPropertyGetter(typeof(DateTime), nameof(DateTime.Now)),
                AccessTools.DeclaredMethod(typeof(TechAdvancing), nameof(GameClockNow)), baseMethod,
                expectedReplacements: baseMethod.Name == "FlushCfg" ? 1 : 2);

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
