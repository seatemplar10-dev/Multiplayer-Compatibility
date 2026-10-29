using HarmonyLib;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Combat Extended - non-snap turrets no longer log "Negative weight in selector"</summary>
    /// <remarks>
    /// FIXES row 307 (29 Oct 2026, Sea's host log from 22:17: 1,078 errors in about 3 minutes, against Megaspider,
    /// VFEI2_Durapod, VFEI2_Swarmling, VFEI2_Queen and Spelopede, all through
    /// <c>Building_TurretGunCE.TryFindNewTarget</c> -> <c>NonSnapAttackTargetFinder.BestAttackTarget</c>).
    /// <para>
    /// Cause: <c>NonSnapAttackTargetFinder.GetShootingTargetScore</c> starts at 60, subtracts the distance (at most 40),
    /// then subtracts <c>NonCombatantScore</c> = 50 for a pawn that is not a combatant (an animal that has a faction, like
    /// every insect of an insectoid raid, or a juvenile, and <c>mindState.CombatantRecently</c> is false) and divides by
    /// the angle. Beyond about 10 cells the score is below 0. Vanilla's <c>AttackTargetFinder</c> never feeds such a
    /// score to the selector (best score below 1 -> the best target gets weight 1); CE's non-snap copy hands the raw
    /// score to <c>TryRandomElementByWeight</c>, which logs the error once per call.
    /// </para>
    /// <para>
    /// Effect without this patch: the selector replaces a negative weight with 0 (first pass), skips it in the draw
    /// (second pass), and when every weight is 0 it returns false without drawing from Rand. So the turret finds no
    /// target and asks again every 10 ticks - an error line each time. This patch makes the score 0 where it was
    /// negative. The selector already treated it as 0, so the chosen target and the number of Rand draws stay exactly the
    /// same on every PC (a clamp of a pure function of synced state); only the log line disappears. Not gated on
    /// MP.IsInMultiplayer, so single player and multiplayer give the same result. Inert when Combat Extended is absent
    /// (MpCompatFor).
    /// </para>
    /// </remarks>
    [MpCompatFor("CETeam.CombatExtended")]
    internal class CombatExtendedTargetWeight
    {
        public CombatExtendedTargetWeight(ModContentPack mod)
        {
            var score = AccessTools.DeclaredMethod("CombatExtended.NonSnapAttackTargetFinder:GetShootingTargetScore")
                        ?? throw new System.MissingMethodException("CombatExtended.NonSnapAttackTargetFinder", "GetShootingTargetScore");
            MpCompat.harmony.Patch(score,
                postfix: new HarmonyMethod(typeof(CombatExtendedTargetWeight), nameof(ClampScore)));
        }

        // A NaN stays NaN (the comparison is false), exactly as before.
        private static void ClampScore(ref float __result)
        {
            if (__result < 0f)
                __result = 0f;
        }
    }
}
