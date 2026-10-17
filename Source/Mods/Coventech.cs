using System;
using HarmonyLib;
using Multiplayer.API;
using UnityEngine;
using Verse;

namespace Multiplayer.Compat;

/// <summary>Coventech flight and expansion Core (MstToyBox.dll, ProjectileSFX.dll)</summary>
/// <remarks>
/// FIXES row 54 (17 Oct 2026, REA static sweep).
/// <list type="bullet">
/// <item><c>Mst_BezierProjectile.InitRandOffset</c> (one copy in each dll) draws Rand the first time <c>BPosQuadratic</c>
/// runs after Launch - from DrawAt on a PC that draws the projectile, from Tick on the headless arbiter (FIXES row 38
/// shape). The offset steers the flight path and the impact. In MP it now draws from a state seeded by the projectile's
/// thing id, so every PC gets the same offset whether draw or tick gets there first, and the map Rand stream is untouched.</item>
/// <item><c>Mst_BezierProjectile_ClusterBomb.GetGroundTarget(IntVec3, float)</c> picks each submunition cell with
/// <c>UnityEngine.Random.insideUnitCircle</c> in Tick -> PatchUnityRand (synced Rand; tick code, no push/pop).</item>
/// </list>
/// </remarks>
[MpCompatFor("shunthewitch.cvnmechanoidwarfarecore")]
public class Coventech
{
    private const string LogPrefix = "[MPCompat - Coventech] ";
    private const int SeedSalt = 0x2C0BE7;

    public Coventech(ModContentPack mod)
    {
        foreach (var typeName in new[] { "MstToyBox.Mst_BezierProjectile", "ProjectileSFX.Mst_BezierProjectile" })
        {
            var type = AccessTools.TypeByName(typeName) ?? throw new TypeLoadException($"{LogPrefix}type {typeName} not found");
            var init = AccessTools.DeclaredMethod(type, "InitRandOffset", Type.EmptyTypes)
                       ?? throw new MissingMethodException(type.FullName, "InitRandOffset");
            MpCompat.harmony.Patch(init,
                prefix: new HarmonyMethod(typeof(Coventech), nameof(PreInitRandOffset)),
                finalizer: new HarmonyMethod(typeof(Coventech), nameof(FinalizeInitRandOffset)));
        }

        var clusterBomb = AccessTools.TypeByName("MstToyBox.Mst_BezierProjectile_ClusterBomb")
                          ?? throw new TypeLoadException($"{LogPrefix}type MstToyBox.Mst_BezierProjectile_ClusterBomb not found");
        var groundTarget = AccessTools.DeclaredMethod(clusterBomb, "GetGroundTarget", [typeof(IntVec3), typeof(float)])
                           ?? throw new MissingMethodException(clusterBomb.FullName, "GetGroundTarget(IntVec3, float)");
        PatchingUtilities.PatchUnityRand(groundTarget, false);
    }

    private static void PreInitRandOffset(Thing __instance, ref bool __state)
    {
        if (!MP.IsInMultiplayer)
            return;
        Rand.PushState(Gen.HashCombineInt(__instance.thingIDNumber, SeedSalt));
        __state = true;
    }

    private static Exception FinalizeInitRandOffset(Exception __exception, bool __state)
    {
        if (__state)
            Rand.PopState();
        return __exception;
    }
}
