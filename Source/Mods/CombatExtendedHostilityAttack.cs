using System;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace Multiplayer.Compat
{
    /// <summary>Combat Extended - hostility response "Attack" no longer restarts a dead AttackStatic job every tick</summary>
    /// <remarks>
    /// FIXES row 302 (29 Oct 2026, seen several times in live co-op): an undrafted ranged colonist on hostility "Attack" stood
    /// still and threw the attack mote every tick, with no error. Vanilla <c>AttackTargetFinder.BestAttackTarget</c> first looks
    /// for a target the verb can hit from the current cell (<c>CanShootAtFromCurrentPosition</c> = <c>verb.CanHitTargetFrom</c>).
    /// If there is none, the flag <c>NeedReachableIfCantHitFromMyPos</c> makes it return the closest REACHABLE target instead.
    /// <c>JobGiver_ConfigurableHostilityResponse.TryGetAttackNearbyEnemyJob</c> then makes an <c>AttackStatic</c> job with
    /// <c>endIfCantShootTargetFromCurPos</c>. <c>JobDriver_AttackStatic</c> throws the mote, calls <c>StopDead</c>, and on its first
    /// tick ends Incompletable because <c>verb.CanHitTargetFrom</c> is false. The giver sits in the constant think tree, so
    /// <c>DetermineNextJob</c> gives the same job again at once: one job per tick, under vanilla's 10-jobs guard, no log.
    /// Vanilla rarely gets there (its <c>CanSee</c> and <c>CanHitTargetFrom</c> agree). CE's <c>Verb_LaunchProjectileCE.CanHitTargetFrom</c>
    /// refuses far more lines (a non-adjacent friendly pawn in the line, cover whose bounds cut the shot ray).
    /// This postfix (after CE's own reload postfix) gives the giver the driver's exact check: same verb call
    /// (<c>TryGetAttackVerb(target, !IsColonist)</c>), same cell, same tick. If the job would end on its first tick, the giver
    /// returns no job and the pawn keeps working. The 30-tick constant think tree check gives the job once the line is clear.
    /// No new state, nothing saved, no camera or UI reads, no Rand. Same result on every PC and in single player.
    /// </remarks>
    [MpCompatFor("CETeam.CombatExtended")]
    internal class CombatExtendedHostilityAttack
    {
        public CombatExtendedHostilityAttack(ModContentPack mod)
        {
            var method = AccessTools.DeclaredMethod(typeof(JobGiver_ConfigurableHostilityResponse), "TryGetAttackNearbyEnemyJob")
                         ?? throw new MissingMethodException(nameof(JobGiver_ConfigurableHostilityResponse), "TryGetAttackNearbyEnemyJob");
            MpCompat.harmony.Patch(method,
                postfix: new HarmonyMethod(typeof(CombatExtendedHostilityAttack), nameof(Postfix)) { priority = Priority.Last });
        }

        private static void Postfix(Pawn pawn, ref Job __result)
        {
            if (__result == null || __result.def != JobDefOf.AttackStatic || !__result.endIfCantShootTargetFromCurPos)
                return;
            var target = __result.targetA;
            if (!target.IsValid || pawn == null || !pawn.Spawned)
                return;
            // Same call and arguments as JobDriver_AttackStatic's tick check.
            var verb = pawn.TryGetAttackVerb(target.Thing, !pawn.IsColonist);
            if (verb == null || !verb.CanHitTargetFrom(pawn.Position, target))
                __result = null;
        }
    }
}
