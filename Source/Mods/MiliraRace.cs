using System.Collections.Generic;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat;

/// <summary>Milira Race by Ancot</summary>
[MpCompatFor("Ancot.MiliraRace")]
public class MiliraRace
{
    // 1.7 lab, FIXES row 126 (class C17): Milira_MilianPawnGenerator_Patch.Postfix (a postfix on PawnGenerator.GeneratePawn) adds
    // the apparel it rolls for each apparel tag to the PawnKindDef's OWN apparelRequired list on every Milian it generates, makes
    // one item per entry of that list, and leaves weaponStuffOverride changed. Defs are not saved, so the list length is the
    // number of Milians this process has generated: a PC that joined after a Milira raid (or restarted) gives the next raid's
    // Milians less gear than the others (lab mp91: an extra MakeThing on the host only). In MP the kind def is put back after
    // every call, so each Milian starts from the def as loaded.
    public MiliraRace(ModContentPack mod)
    {
        var postfix = AccessTools.DeclaredMethod("Milira.Milira_MilianPawnGenerator_Patch:Postfix");
        if (postfix == null)
        {
            Log.Warning("MPCompat: Milira_MilianPawnGenerator_Patch.Postfix not found - Milira raid gear is not guarded (FIXES row 126)");
            return;
        }
        MpCompat.harmony.Patch(postfix,
            prefix: new HarmonyMethod(typeof(MiliraRace), nameof(PreMilianGear)),
            finalizer: new HarmonyMethod(typeof(MiliraRace), nameof(PostMilianGear)));
    }

    private sealed class KindState
    {
        public PawnKindDef kind;
        public List<ThingDef> apparelRequired;
        public List<ThingDef> apparelRequiredCopy;
        public ThingDef weaponStuffOverride;
        public QualityCategory? forceWeaponQuality;
        public bool forceNormalGearQuality;
    }

    private static void PreMilianGear(PawnGenerationRequest request, ref KindState __state)
    {
        if (!MP.IsInMultiplayer) return;
        var kind = request.KindDef;
        if (kind == null) return;
        __state = new KindState
        {
            kind = kind,
            apparelRequired = kind.apparelRequired,
            apparelRequiredCopy = kind.apparelRequired == null ? null : new List<ThingDef>(kind.apparelRequired),
            weaponStuffOverride = kind.weaponStuffOverride,
            forceWeaponQuality = kind.forceWeaponQuality,
            forceNormalGearQuality = kind.forceNormalGearQuality,
        };
    }

    private static void PostMilianGear(KindState __state)
    {
        if (__state == null) return;
        var kind = __state.kind;
        // the same list object (other code may hold it), with its contents as before the call
        if (__state.apparelRequired != null)
        {
            __state.apparelRequired.Clear();
            __state.apparelRequired.AddRange(__state.apparelRequiredCopy);
        }
        kind.apparelRequired = __state.apparelRequired;
        kind.weaponStuffOverride = __state.weaponStuffOverride;
        kind.forceWeaponQuality = __state.forceWeaponQuality;
        kind.forceNormalGearQuality = __state.forceNormalGearQuality;
    }
}
