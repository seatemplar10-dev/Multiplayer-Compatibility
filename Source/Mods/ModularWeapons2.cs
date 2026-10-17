using HarmonyLib;
using Verse;

namespace Multiplayer.Compat;

/// <summary>ModularWeapons 2 by kaitorisenkou</summary>
/// <remarks>
/// FIXES row 56 (17 Oct 2026, REA static sweep). <c>CompModularWeapon.RandomizePartsForPawn</c> picks a non-player
/// pawn's optional gun parts with <c>UnityEngine.Random.Range</c> inside a lambda
/// (<c>&lt;&gt;c__DisplayClass44_1.&lt;RandomizePartsForPawn&gt;b__1</c> in the lab's ModularWeapons2.dll); it runs from
/// Notify_Equipped on every PC -> draw from the synced Verse.Rand (sim code, no push/pop).
/// </remarks>
[MpCompatFor("kaitorisenkou.modularweapons2")]
public class ModularWeapons2
{
    public ModularWeapons2(ModContentPack mod)
    {
        var type = AccessTools.TypeByName("ModularWeapons2.CompModularWeapon")
                   ?? throw new System.TypeLoadException("[MPCompat - ModularWeapons 2] type ModularWeapons2.CompModularWeapon not found");
        PatchingUtilities.PatchUnityRand(MpMethodUtil.GetLambda(type, "RandomizePartsForPawn", lambdaOrdinal: 1), false);
    }
}
