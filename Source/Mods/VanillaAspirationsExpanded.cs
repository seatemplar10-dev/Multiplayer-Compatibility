using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Multiplayer.Compat;

/// <summary>Vanilla Aspirations Expanded by Oskar Potocki, Legodude17, Sarg Bjornson</summary>
/// <see href="https://github.com/Vanilla-Expanded/VanillaAspirationsExpanded"/>
/// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3268401022"/>
[MpCompatFor("VanillaExpanded.VanillaAspirationsExpanded")]
public class VanillaAspirationsExpanded
{
    #region Fields

    // MP Compat
    private static bool isDrawingDialog = false;

    // List<AspirationDef>
    private static Type aspirationDefListType;

    // SyncDelegates
    private static FastInvokeHandler pickRandomTraitAndPassionsMethod;

    // ChoiceLetter_GrowthMoment_Aspirations
    private static AccessTools.FieldRef<ChoiceLetter_GrowthMoment, IList> aspirationChoicesField;
    private static AccessTools.FieldRef<ChoiceLetter_GrowthMoment, int> aspirationGainsCountField;
    private static FastInvokeHandler trySetAspirationChoicesMethod;
    private static FastInvokeHandler makeAspirationChoicesMethod;

    // WorldComponent_PawnList.colonists_with_fulfillment_need, read only (FIXES row 161 diagnostics)
    private static FieldInfo pawnListField;

    #endregion

    #region Main Patch

    public VanillaAspirationsExpanded(ModContentPack mod)
    {
        // Run the patches
        MpCompatPatchLoader.LoadPatch(this);

        // Setup list type with a generic we can't reference
        var type = AccessTools.TypeByName("VAspirE.AspirationDef");
        aspirationDefListType = typeof(List<>).MakeGenericType(type);

        // Access MP method
        type = AccessTools.TypeByName("Multiplayer.Client.SyncDelegates");
        var method = AccessTools.DeclaredMethod(type, "PickRandomTraitAndPassions");
        pickRandomTraitAndPassionsMethod = MethodInvoker.GetHandler(method);

        // Handle aspirations fulfilled letter/dialog
        type = AccessTools.TypeByName("VAspirE.ChoiceLetter_AspirationsFulfilled");
        // This letter/dialog have different type, but are otherwise mostly the
        // same as vanilla growth moment ones. MP should handle those already,
        // and we only need to register the default choice for the letter.
        MP.RegisterDefaultLetterChoice(method, type);

        // Handle growth moment with aspirations letter/dialog
        type = AccessTools.TypeByName("VAspirE.ChoiceLetter_GrowthMoment_Aspirations");
        // Setup fields
        aspirationChoicesField = AccessTools.FieldRefAccess<IList>(type, "aspirationChoices");
        aspirationGainsCountField = AccessTools.FieldRefAccess<int>(type, "aspirationGainsCount");
        // Setup methods
        trySetAspirationChoicesMethod = MethodInvoker.GetHandler(AccessTools.DeclaredMethod(type, "TrySetAspirationChoices"));
        var makeAspirationChoices = AccessTools.DeclaredMethod(type, "MakeAspirationChoices");
        makeAspirationChoicesMethod = MethodInvoker.GetHandler(makeAspirationChoices);
        // Sync methods
        MP.RegisterSyncMethod(makeAspirationChoices);
        // Letter timeout handling
        MP.RegisterDefaultLetterChoice(MpMethodUtil.MethodOf(PickRandomAspirationsTraitPassions), type);

        // Diagnostics only: the list VAspirE tests ShowOnNeedList against
        pawnListField = AccessTools.Field(AccessTools.TypeByName("VAspirE.WorldComponent_PawnList"), "colonists_with_fulfillment_need");
    }

    #endregion

    #region Choice letter expiry handling

    private static void PickRandomAspirationsTraitPassions(ChoiceLetter_GrowthMoment letter)
    {
        // Make sure the aspirations are prepared
        trySetAspirationChoicesMethod(letter);

        var aspirationChoices = aspirationChoicesField(letter);
        var aspirationCount = aspirationGainsCountField(letter);

        // If there's any aspirations to pick from, pick some random ones.
        if (aspirationCount > 0 && aspirationChoices != null)
            makeAspirationChoicesMethod(letter,
                Activator.CreateInstance(aspirationDefListType, aspirationChoices.Cast<Def>().InRandomOrder().Take(aspirationCount)));
        // This is technically no-op due to null check, but include it just in case
        // the mod decides to do something with it at some point (or a different mod uses it).
        else
            makeAspirationChoicesMethod(letter, null);

        // MP method to pick traits and passions, which handles normal, vanilla growth moment.
        // This will also handle closing the letter.
        pickRandomTraitAndPassionsMethod(letter);
    }

    #endregion

    #region Seed aspiration generation RNG

    // The letter tries to generate the options when opened. Make the picks seeded, so all players will get the same ones.
    [MpCompatPrefix("VAspirE.ChoiceLetter_GrowthMoment_Aspirations", "TrySetAspirationChoices")]
    private static void PreTrySetAspirationChoices(ChoiceLetter_GrowthMoment __instance)
        => Rand.PushState(Gen.HashCombineInt(__instance.pawn.thingIDNumber, __instance.arrivalTick));

    [MpCompatPostfix("VAspirE.ChoiceLetter_GrowthMoment_Aspirations", "TrySetAspirationChoices")]
    private static void PostTrySetAspirationChoices()
        => Rand.PopState();

    #endregion

    #region Aspiration checks only in the simulation

    // 1.7 lab (FIXES rows 110, 114): VAspirE runs Need_Fulfillment.CheckCompletion - which can complete an aspiration: need level,
    // completedTicks, a message - as a postfix on ~15 methods, one of them SituationalThoughtHandler.UpdateAllMoodThoughts.
    // Multiplayer skips that method in interface code with a prefix, but Harmony still runs postfixes, so every mood readout on
    // one PC completed aspirations on that PC only (lab mp84 join-point diff; mp86 desync on the same pawn's inspiration roll).
    // In MP the checks run in ticks and commands only, where every PC makes them.
    [MpCompatPrefix("VAspirE.SatisfactionPatches", "CheckGeneral")]
    [MpCompatPrefix("VAspirE.SatisfactionPatches", "CheckGeneralDaily")]
    [MpCompatPrefix("VAspirE.SatisfactionPatches", "CheckArgP")]
    private static bool CheckOnlyInSimulation() => !MP.IsInMultiplayer || !MP.InInterface;

    // 1.7 lab (FIXES row 161): the same rule at the two points every route into an aspiration passes through, so that a hook
    // that is not in the list above (or a new one in a later VAspirE) cannot change the need level, completedTicks or send the
    // message from interface code. Interface code never completes an aspiration, whatever called it.
    [MpCompatPrefix("VAspirE.Need_Fulfillment", "CheckCompletion")]
    [MpCompatPrefix("VAspirE.Need_Fulfillment", "Complete")]
    private static bool CompleteOnlyInSimulation() => !MP.IsInMultiplayer || !MP.InInterface;

    #endregion

    #region The need's population is a function of the pawn, not of a shared list

    // 1.7 lab (FIXES row 161, SS-A3 at MP 12,500: Human1929 completed "Get some lovin'" on the host only).
    //
    // Need_Fulfillment.CheckCompletion starts with `if (!ShowOnNeedList) return;` and
    // `ShowOnNeedList => WorldComponent_PawnList.colonists_with_fulfillment_need.Contains(pawn)`. That static field is
    // ASSIGNED (not copied) the list returned by PawnsFinder.AllMapsCaravansAndTravellingTransporters_AliveSpawned_FreeColonistsAndPrisoners,
    // which is one of PawnsFinder's scratch lists: cleared and refilled by every call from anybody, filtered by IsFreeColonist,
    // which Multiplayer's PawnIsColonistPatch turns into "faction == Find.FactionManager.OfPlayer" (the current faction context).
    // In the game's own code (decompile 4981) the only reader of that getter chain outside PawnsFinder is
    // Alert_LifeThreateningHediff (through ..._FreeColonistsAndPrisoners_NoCryptosleep) - an alert, i.e. interface code - so the
    // host's windowed PC refills the list in its own faction context every few frames while a PC that draws no alerts keeps
    // whatever the last FinalizeInit / StoryWatcher_PopAdaptation event (in whatever faction context that ran) left in it. The
    // need's population can therefore differ per PC, and so can every completion check behind it.
    //
    // The population is now computed from the pawn alone, the same on every PC, and without the faction context: alive, on a
    // map or in a caravan, and a free colonist of ANY player faction or a prisoner of one (what the list was meant to hold).
    // The list itself stays in VAspirE (nothing reads it any more).
    [MpCompatPrefix("VAspirE.Need_Fulfillment", "ShowOnNeedList", MethodType.Getter)]
    private static bool ShowOnNeedListFromThePawn(Pawn ___pawn, ref bool __result)
    {
        if (!MP.IsInMultiplayer)
            return true;

        var result = InFulfillmentPopulation(___pawn);

        if (LogShowOnNeedListMismatch)
            NoteListMismatch(___pawn, result);

        __result = result;
        return false;
    }

    private static bool InFulfillmentPopulation(Pawn pawn)
    {
        if (pawn == null || pawn.Dead || pawn.Destroyed)
            return false;

        // PawnsFinder.AllMapsCaravansAndTravellingTransporters_AliveSpawned: pawns spawned on a map, or in a caravan.
        if (!pawn.Spawned && !pawn.IsCaravanMember())
            return false;

        // Prisoners of a player faction (PawnsFinder.AllMapsCaravansAndTravellingTransporters_AliveSpawned_PrisonersOfColony).
        // Pawn.IsPrisonerOfColony reads guest.HostFaction.IsPlayer, the faction's own def flag: no faction context in it.
        if (pawn.IsPrisonerOfColony)
            return true;

        // Free colonists. Pawn.IsFreeColonist / IsColonist would compare the faction with Faction.OfPlayer, which is the
        // current faction context (the viewing player's outside ticks, the map's during them) - spell the rest out with the
        // same conditions as Pawn.IsColonist (Verse/Pawn.cs:518) but "any player faction".
        var faction = pawn.Faction;
        return faction is { IsPlayer: true }
               && pawn.HostFaction == null
               && pawn.RaceProps.Humanlike
               && (!pawn.IsSlave || pawn.guest.SlaveIsSecure)
               && !pawn.IsSubhuman;
    }

    // Diagnostics (no effect on the simulation): the first time a pawn's answer from VAspirE's own list differs from the
    // answer above, say so once per (pawn, list answer, answer) with the numbers that explain it. Run this on host and arbiter
    // and compare the lines: a pawn the arbiter's list lacks and the host's has is row 161's per-PC input.
    private const bool LogShowOnNeedListMismatch = true;
    private static readonly HashSet<(int, bool, bool)> loggedMismatches = new();

    private static void NoteListMismatch(Pawn pawn, bool deterministic)
    {
        try
        {
            if (pawn == null || pawnListField == null)
                return;

            var list = pawnListField.GetValue(null) as List<Pawn>;
            var inList = list != null && list.Contains(pawn);
            if (inList == deterministic || !loggedMismatches.Add((pawn.thingIDNumber, inList, deterministic)))
                return;

            Log.Message($"[MPCompat] VAspirE row 161: ShowOnNeedList of {pawn.ThingID} would be {inList} from VAspirE's list " +
                        $"({list?.Count ?? -1} pawns, offered by the pawn's own state: {deterministic}) at tick {Find.TickManager?.TicksGame}, " +
                        $"InInterface {MP.InInterface}, Faction.OfPlayer {Faction.OfPlayer?.loadID}");
        }
        catch (Exception e)
        {
            Log.Warning($"[MPCompat] VAspirE row 161 diagnostics failed: {e.Message}");
        }
    }

    #endregion

    #region Don't close dialog when drawing it

    // Patches to not remove the letter if it's in the process of being drawn.
    // Rather than prefixing LetterStack.RemoveLetter we could instead change
    // Multiplayer.IsDrawingGrowthMomentDialog.isDrawing to true/false, and let
    // MP handle this itself. However, it's safer to do a new patch for this.

    [MpCompatPrefix("VAspirE.Dialog_GrowthMomentChoices_Aspirations", nameof(Dialog_GrowthMomentChoices.DoWindowContents))]
    private static void PreDoWindowContents() => isDrawingDialog = true;

    [MpCompatFinalizer("VAspirE.Dialog_GrowthMomentChoices_Aspirations", nameof(Dialog_GrowthMomentChoices.DoWindowContents))]
    private static void PostDoWindowContents() => isDrawingDialog = false;

    [MpCompatPrefix(typeof(LetterStack), nameof(LetterStack.RemoveLetter))]
    private static bool DontRemoveChoiceLetter() => !MP.IsInMultiplayer || !isDrawingDialog;

    #endregion
}
