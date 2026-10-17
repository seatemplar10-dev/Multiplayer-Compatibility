using System;
using HarmonyLib;
using Verse;

namespace Multiplayer.Compat;

/// <summary>Fortifications - Medieval by AobaKuma</summary>
/// <remarks>
/// FIXES row 51 (17 Oct 2026, REA static sweep). ModExtension_ShootWithFleck.BurstFleck draws Verse.Rand for its flecks
/// only behind <c>GenView.ShouldSpawnMotesAt(pos, caster.Map, false)</c>, which includes a per-player "is this the map I
/// view" test (null on the headless arbiter), so the number of draws per shot differs between PCs. Same fix as the
/// Industrial twin (FortificationsIndustrial: <c>Fortification.CompCastFlecker:BurstFleck</c>): run it in a pushed Rand.
/// Lab mp55 (17 Oct): the 1.6 build that 1.7 loads references <c>TerrainDef.holdSnow</c> (renamed holdSnowOrSand in 1.7),
/// so Harmony cannot read BurstFleck ("Token 0x0a000031 is not valid") and the method cannot run on 1.7 at all - no draws,
/// no desync. The patch is then skipped with a warning; it applies once the mod ships a build that loads on 1.7.
/// </remarks>
[MpCompatFor("Aoba.Fortress.Medieval")]
public class FortificationsMedieval
{
    public FortificationsMedieval(ModContentPack mod)
    {
        try
        {
            PatchingUtilities.PatchPushPopRand("FortificationMedieval.ModExtension_ShootWithFleck:BurstFleck");
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or MissingFieldException or BadImageFormatException)
        {
            Log.Warning($"[MPCompat - Fortifications Medieval] BurstFleck not patched: its IL does not load on this game version ({e.GetType().Name}: {e.Message})");
        }
    }
}
