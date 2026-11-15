using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Eccentric Tech - Defense Grid (EccentricProjectiles) by Aelanna: reload residue and party-mode clock. The main module (gizmos, RNG) is Source_Referenced/EccentricTechDefenseGrid.cs.</summary>
    /// <remarks>
    /// FIXES row 244 (26 Oct 2026): <c>ModExtension_ProjectileEffects</c> and <c>ModExtension_WeaponAttachments</c> keep a static barrel
    /// counter per weapon thingIDNumber (<c>originIncrementers</c>), never reset. Multiplayer reloads every PC in-process at a join point,
    /// but a joiner loads in a fresh process: the host keeps its counters, the joiner restarts at 0, and under CE the counter moves the
    /// launch origin. Both counters are cleared when a game loads, so every PC starts a loaded game from 0 (found by the DetScan
    /// reload-residue mode).
    /// FIXES row 61 (15 Nov 2026): <c>CompDefenseProjector.NetworkTick</c> (simulation) sets the field colour, the saved
    /// <c>fieldColor</c> and the glower colour from <c>UIAssets.PartyModeColor()</c> while party mode is on, and that picks the colour
    /// from <c>DateTime.Now.Ticks</c>. Fix (MP only): the <c>DateTime.Now</c> read becomes a game clock from <c>TicksGame</c>, so the
    /// colours cycle at the same speed in game time and match on every PC.
    /// </remarks>
    [MpCompatFor("Aelanna.EccentricTech.DefenseGrid")]
    internal class EccentricProjectilesReloadResidue
    {
        private static readonly DateTime GameClockEpoch = new(2000, 1, 1);
        private static FieldInfo[] counterFields;

        public EccentricProjectilesReloadResidue(ModContentPack mod)
        {
            counterFields = new[]
            {
                AccessTools.DeclaredField("EccentricProjectiles.ModExtension_ProjectileEffects:originIncrementers")
                ?? throw new System.MissingFieldException("EccentricProjectiles.ModExtension_ProjectileEffects", "originIncrementers"),
                AccessTools.DeclaredField("EccentricProjectiles.ModExtension_WeaponAttachments:originIncrementers")
                ?? throw new System.MissingFieldException("EccentricProjectiles.ModExtension_WeaponAttachments", "originIncrementers"),
            };
            // Game.LoadGame: Multiplayer's reloads and a joiner's load both go through it (as in ResearchReinvented.cs, row 173)
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(Game), nameof(Game.LoadGame)),
                prefix: new HarmonyMethod(typeof(EccentricProjectilesReloadResidue), nameof(PreLoadGame)));

            MpCompat.harmony.Patch(AccessTools.DeclaredMethod("EccentricDefenseGrid.UIAssets:PartyModeColor"),
                transpiler: new HarmonyMethod(typeof(EccentricProjectilesReloadResidue), nameof(ReplacePartyClock)));
        }

        private static void PreLoadGame()
        {
            foreach (var field in counterFields)
                (field.GetValue(null) as IDictionary)?.Clear();
        }

        private static IEnumerable<CodeInstruction> ReplacePartyClock(IEnumerable<CodeInstruction> instr, MethodBase baseMethod)
            => instr.ReplaceMethod(AccessTools.DeclaredPropertyGetter(typeof(DateTime), nameof(DateTime.Now)),
                AccessTools.DeclaredMethod(typeof(EccentricProjectilesReloadResidue), nameof(GameClockNow)), baseMethod,
                expectedReplacements: 1);

        // Replaces DateTime.Now. In MP: a fixed epoch plus the game time, the same on every PC at the same tick.
        private static DateTime GameClockNow()
        {
            if (!MP.IsInMultiplayer)
                return DateTime.Now;
            long ticksGame = Current.Game != null ? Find.TickManager.TicksGame : 0;
            return GameClockEpoch.AddTicks(ticksGame * TimeSpan.TicksPerSecond / GenTicks.TicksPerRealSecond);
        }
    }
}
