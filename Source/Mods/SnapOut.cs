using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace Multiplayer.Compat;

/// <summary>Snap Out! by weilbyte</summary>
/// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=1319782555"/>
/// <remarks>
/// FIXES row 272 (28 Oct 2026, live co-op host log 20:42:55, after desync 4): a loaded job builds its toils in Scribe PostLoadInit
/// (vanilla JobDriver.ExposeData: <c>if (Scribe.mode == LoadSaveMode.PostLoadInit) SetupToils();</c>). The pawn is not spawned yet,
/// so <c>pawn.Map</c> is null, and <c>JobDriver_SnappingOut.MakeNewToils</c> calls <c>RCellFinder.RandomWanderDestFor(rpawn, rpawn.Position, ...)</c>
/// while it builds the toils. That reads <c>root.GetRegion(pawn.Map)</c> -> <c>c.InBounds(null)</c> -> NRE before any Rand draw, the
/// error recovery fails too and the pawn is left jobless. Multiplayer reloads at every join point and resync, so this hit every one.
/// Fix: with no map, RandomWanderDestFor returns <c>root</c> and skips the original (which would throw at that point, so no Rand
/// draw is skipped). The value only feeds a GotoCell toil's initAction; a resumed toil does not run its initAction again and the
/// pather keeps its saved destination.
/// <c>JobDriver_GoToSafety.MakeNewToils</c> has the same load-time problem: <c>pawn.ownership.OwnedRoom.Cells</c> is null while the
/// bed is not spawned (OwnedBed.GetRoom() -> SpawnedParentOrMe null). With no map it gets the same two toils with the pawn's own
/// cell as the goto target (the wait length is the original's state-neutral <c>Rand.RangeSeeded</c>).
/// </remarks>
[MpCompatFor("weilbyte.snapout")]
public class SnapOut
{
    private static bool loggedWander, loggedSafety;

    public SnapOut(ModContentPack mod)
    {
        var wander = AccessTools.DeclaredMethod(typeof(RCellFinder), nameof(RCellFinder.RandomWanderDestFor),
            [typeof(Pawn), typeof(IntVec3), typeof(float), typeof(Func<Pawn, IntVec3, IntVec3, bool>), typeof(Danger), typeof(bool)]);
        if (wander != null)
            MpCompat.harmony.Patch(wander, prefix: new HarmonyMethod(typeof(SnapOut), nameof(PreRandomWanderDestFor)));
        else
            Log.Error("[MPCompat] Snap Out row 272: RCellFinder.RandomWanderDestFor not found, a loaded Snapping Out job still leaves the pawn jobless");

        var safety = AccessTools.DeclaredMethod("SnapOut.JobDriver_GoToSafety:MakeNewToils");
        if (safety != null)
            MpCompat.harmony.Patch(safety, prefix: new HarmonyMethod(typeof(SnapOut), nameof(PreGoToSafetyToils)));
        else
            Log.Error("[MPCompat] Snap Out row 272: JobDriver_GoToSafety.MakeNewToils not found, a loaded Go To Safety job still leaves the pawn jobless");
    }

    private static bool PreRandomWanderDestFor(Pawn pawn, IntVec3 root, ref IntVec3 __result)
    {
        if (pawn?.Map != null)
            return true;

        __result = root;
        if (!loggedWander)
        {
            loggedWander = true;
            Log.Message($"[MPCompat] Snap Out row 272: RandomWanderDestFor for {pawn?.LabelShort ?? "null pawn"} with no map (job toils built during a load), returned its own cell");
        }
        return false;
    }

    private static bool PreGoToSafetyToils(JobDriver __instance, ref IEnumerable<Toil> __result)
    {
        if (__instance.pawn?.Map != null)
            return true;

        __result = GoToSafetyLoadToils(__instance);
        if (!loggedSafety)
        {
            loggedSafety = true;
            Log.Message($"[MPCompat] Snap Out row 272: Go To Safety toils for {__instance.pawn?.LabelShort ?? "null pawn"} built with no map (load), goto target is its own cell");
        }
        return false;
    }

    // Same toils as the original (GotoCell ClosestTouch, then Wait with social mode off), target = the pawn's cell.
    private static IEnumerable<Toil> GoToSafetyLoadToils(JobDriver driver)
    {
        var pawn = driver.pawn;
        driver.TargetThingB = pawn;
        yield return Toils_Goto.GotoCell(pawn.Position, PathEndMode.ClosestTouch);
        var wait = Toils_General.Wait(Rand.RangeSeeded(1750, 3500, Find.TickManager.TicksAbs));
        wait.socialMode = RandomSocialMode.Off;
        yield return wait;
    }
}
