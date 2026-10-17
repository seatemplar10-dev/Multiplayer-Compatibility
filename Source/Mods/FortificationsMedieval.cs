using HarmonyLib;
using Verse;

namespace Multiplayer.Compat;

/// <summary>Fortifications - Medieval by AobaKuma</summary>
/// <remarks>
/// FIXES row 51 (17 Oct 2026, REA static sweep). ModExtension_ShootWithFleck.BurstFleck draws Verse.Rand for its flecks
/// only behind <c>GenView.ShouldSpawnMotesAt(pos, caster.Map, false)</c>, which includes a per-player "is this the map I
/// view" test (null on the headless arbiter), so the number of draws per shot differs between PCs. Same fix as the
/// Industrial twin (FortificationsIndustrial: <c>Fortification.CompCastFlecker:BurstFleck</c>): run it in a pushed Rand.
/// </remarks>
[MpCompatFor("Aoba.Fortress.Medieval")]
public class FortificationsMedieval
{
    public FortificationsMedieval(ModContentPack mod)
        => PatchingUtilities.PatchPushPopRand("FortificationMedieval.ModExtension_ShootWithFleck:BurstFleck");
}
