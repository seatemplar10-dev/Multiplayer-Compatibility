using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Vanilla Food Variety Expanded (Vanilla Cooking Expanded - Variety) by Oskar Potocki, Sarg Bjornson</summary>
    /// <remarks>
    /// FIXES row 192 (26 Oct 2026): <c>GameComponent_FoodVariety.RefreshList(pawnJustAdded)</c> runs from the mod's postfix on
    /// <c>StoryWatcher_PopAdaptation.Notify_PawnEvent</c> (GainedColonist). Vanilla raises that event in <c>PawnGenerator</c> for every
    /// starting pawn, also for the multifaction starting-pawn page's pawns, which exist on the joiner only (negative thing ids). Each one got a
    /// <c>pawns_and_diet</c> entry (a static dictionary, saved, never pruned for pawns that are not discarded): lab round 1 had 37 diet entries on
    /// the joiner vs 29 on the host at the first checkpoint after a Settle (the 29 shared entries were equal). In MP a pawn with a negative
    /// thing id is not added. The pawns the joiner keeps are rebuilt with real ids by the synced SendPawn, the same on every PC.
    /// </remarks>
    [MpCompatFor("VanillaExpanded.VanillaFoodVarietyExpanded")]
    internal class VanillaFoodVarietyExpanded
    {
        public VanillaFoodVarietyExpanded(ModContentPack mod)
        {
            var refresh = AccessTools.DeclaredMethod("VanillaCookingExpandedVariety.GameComponent_FoodVariety:RefreshList", new[] { typeof(Pawn) })
                          ?? throw new System.MissingMethodException("VanillaCookingExpandedVariety.GameComponent_FoodVariety", "RefreshList(Pawn)");
            MpCompat.harmony.Patch(refresh, prefix: new HarmonyMethod(typeof(VanillaFoodVarietyExpanded), nameof(PreRefreshList)));
        }

        private static bool PreRefreshList(Pawn pawnJustAdded)
            => !(MP.IsInMultiplayer && pawnJustAdded != null && pawnJustAdded.thingIDNumber < 0);
    }
}
