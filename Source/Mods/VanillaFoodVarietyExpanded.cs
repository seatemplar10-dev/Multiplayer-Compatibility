using System.Collections;
using System.Collections.Generic;
using System.Reflection;
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
    /// <para/>
    /// FIXES row 240 (26 Oct 2026): the save keeps entries the next load drops. <c>ExposeData</c> removes only discarded pawns and dead pawns
    /// without a corpse; <c>PawnDiet</c> saves a live pawn by reference, written as null when the pawn is destroyed (an exploded AAT walker
    /// bomb), and the load skips entries whose pawn is null. A PC that never reloaded (the host) keeps them, a PC that loaded the join point
    /// does not: lab C2 soak, host 38 entries vs joiner 33 at the joiner's first checkpoint. Removals also leave holes in the dictionary that
    /// later adds fill, so the save order can differ after a load. Nothing in the game reads these entries (lookups are by a live pawn), so
    /// only saves differed. In MP, before the save, the entries a load would not get back are dropped and the rest are put back sorted by
    /// thing id: every PC writes what a PC that just loaded writes.
    /// </remarks>
    [MpCompatFor("VanillaExpanded.VanillaFoodVarietyExpanded")]
    internal class VanillaFoodVarietyExpanded
    {
        private static FieldInfo pawnsAndDietField;

        public VanillaFoodVarietyExpanded(ModContentPack mod)
        {
            var refresh = AccessTools.DeclaredMethod("VanillaCookingExpandedVariety.GameComponent_FoodVariety:RefreshList", new[] { typeof(Pawn) })
                          ?? throw new System.MissingMethodException("VanillaCookingExpandedVariety.GameComponent_FoodVariety", "RefreshList(Pawn)");
            MpCompat.harmony.Patch(refresh, prefix: new HarmonyMethod(typeof(VanillaFoodVarietyExpanded), nameof(PreRefreshList)));

            pawnsAndDietField = AccessTools.DeclaredField("VanillaCookingExpandedVariety.GameComponent_FoodVariety:pawns_and_diet")
                                ?? throw new System.MissingFieldException("VanillaCookingExpandedVariety.GameComponent_FoodVariety", "pawns_and_diet");
            var expose = AccessTools.DeclaredMethod("VanillaCookingExpandedVariety.GameComponent_FoodVariety:ExposeData")
                         ?? throw new System.MissingMethodException("VanillaCookingExpandedVariety.GameComponent_FoodVariety", "ExposeData");
            MpCompat.harmony.Patch(expose, prefix: new HarmonyMethod(typeof(VanillaFoodVarietyExpanded), nameof(PreExposeData)));
        }

        private static bool PreRefreshList(Pawn pawnJustAdded)
            => !(MP.IsInMultiplayer && pawnJustAdded != null && pawnJustAdded.thingIDNumber < 0);

        private static void PreExposeData()
        {
            if (!MP.IsInMultiplayer || Scribe.mode != LoadSaveMode.Saving)
                return;
            if (pawnsAndDietField.GetValue(null) is not IDictionary dict || dict.Count == 0)
                return;

            var keep = new List<DictionaryEntry>(dict.Count);
            foreach (DictionaryEntry e in dict)
            {
                if (e.Key is Pawn p && LoadGetsBack(p))
                    keep.Add(e);
            }
            keep.Sort((a, b) => ((Pawn)a.Key).thingIDNumber.CompareTo(((Pawn)b.Key).thingIDNumber));
            dict.Clear();   // Dictionary.Clear also resets the free list: adds after it enumerate in insertion order
            foreach (var e in keep)
                dict.Add(e.Key, e.Value);
        }

        // The mod's own save filter (discarded, or dead without a usable corpse), plus a live pawn that is destroyed: its reference is saved as null.
        private static bool LoadGetsBack(Pawn p)
        {
            if (p.Discarded)
                return false;
            if (p.Dead)
                return p.Corpse is { Bugged: false, Destroyed: false };
            return !p.Destroyed;
        }
    }
}
