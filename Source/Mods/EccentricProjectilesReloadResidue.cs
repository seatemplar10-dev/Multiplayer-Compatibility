using System.Collections;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Eccentric Tech - Defense Grid (EccentricProjectiles) by Aelanna: reload residue only. The main module (gizmos, RNG) is Source_Referenced/EccentricTechDefenseGrid.cs.</summary>
    /// <remarks>
    /// FIXES row 244 (26 Oct 2026): <c>ModExtension_ProjectileEffects</c> and <c>ModExtension_WeaponAttachments</c> keep a static barrel
    /// counter per weapon thingIDNumber (<c>originIncrementers</c>), never reset. Multiplayer reloads every PC in-process at a join point,
    /// but a joiner loads in a fresh process: the host keeps its counters, the joiner restarts at 0, and under CE the counter moves the
    /// launch origin. Both counters are cleared when a game loads, so every PC starts a loaded game from 0 (found by the DetScan
    /// reload-residue mode).
    /// </remarks>
    [MpCompatFor("Aelanna.EccentricTech.DefenseGrid")]
    internal class EccentricProjectilesReloadResidue
    {
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
        }

        private static void PreLoadGame()
        {
            foreach (var field in counterFields)
                (field.GetValue(null) as IDictionary)?.Clear();
        }
    }
}
