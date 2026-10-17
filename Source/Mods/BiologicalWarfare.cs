using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace Multiplayer.Compat;

/// <summary>Ushanka's Biological Warfare</summary>
/// <remarks>
/// FIXES rows 53 and 16 (17 Oct 2026, REA static sweep), lab copy <c>Assemblies\BiologicalWarfare.dll</c>:
/// <list type="bullet">
/// <item><c>HediffCompParasiteReproduction.RandomOrganicPart</c> picks the perforated body part with
/// <c>UnityEngine.Random.Range</c> inside the hediff tick -> PatchUnityRand (synced Rand, no push/pop: tick code).</item>
/// <item><c>BiologicalUtils.Shuffle&lt;T&gt;(List&lt;T&gt;, int)</c> starts with an unseeded <c>new System.Random()</c> and
/// orders the gas cells of <c>CompGasVent.FloodAreaWithGas</c> (cell order = gas delay). It is generic and called with
/// the struct IntVec3, whose instantiation a patch on the generic definition may not reach, so the one call in
/// FloodAreaWithGas is pointed at <see cref="RandShuffle{T}"/> instead: same steps, drawn from Verse.Rand.</item>
/// </list>
/// </remarks>
[MpCompatFor("ushanka.biologicalwarfare")]
public class BiologicalWarfare
{
    private const string LogPrefix = "[MPCompat - Biological Warfare] ";
    private static MethodInfo modShuffle;

    public BiologicalWarfare(ModContentPack mod)
    {
        PatchingUtilities.PatchUnityRand("USH_BW.HediffCompParasiteReproduction:RandomOrganicPart", false);

        var utils = AccessTools.TypeByName("USH_BW.BiologicalUtils")
                    ?? throw new System.TypeLoadException($"{LogPrefix}type USH_BW.BiologicalUtils not found");
        modShuffle = AccessTools.DeclaredMethod(utils, "Shuffle")
                     ?? throw new System.MissingMethodException(utils.FullName, "Shuffle");
        var gasVent = AccessTools.TypeByName("USH_BW.CompGasVent")
                      ?? throw new System.TypeLoadException($"{LogPrefix}type USH_BW.CompGasVent not found");
        var flood = AccessTools.DeclaredMethod(gasVent, "FloodAreaWithGas")
                    ?? throw new System.MissingMethodException(gasVent.FullName, "FloodAreaWithGas");
        MpCompat.harmony.Patch(flood, transpiler: new HarmonyMethod(typeof(BiologicalWarfare), nameof(UseRandShuffle)));
    }

    private static IEnumerable<CodeInstruction> UseRandShuffle(IEnumerable<CodeInstruction> instr, MethodBase original)
    {
        var replaced = 0;
        foreach (var ci in instr)
        {
            if (ci.operand is MethodInfo m && m.IsGenericMethod && m.GetGenericMethodDefinition() == modShuffle)
            {
                ci.operand = AccessTools.Method(typeof(BiologicalWarfare), nameof(RandShuffle))
                    .MakeGenericMethod(m.GetGenericArguments());
                replaced++;
            }
            yield return ci;
        }

        if (replaced != 1)
            Log.Error($"{LogPrefix}expected 1 BiologicalUtils.Shuffle call in {original?.FullDescription()}, replaced {replaced}");
    }

    // Same steps as BiologicalUtils.Shuffle: Random.Next(min, max) and Rand.Range(int, int) both exclude max.
    private static void RandShuffle<T>(List<T> list, int shuffleSteps)
    {
        for (var i = 0; i < shuffleSteps; i++)
        {
            var num = Rand.Range(0, list.Count - 1);
            var num2 = Rand.Range(num, list.Count);
            (list[num], list[num2]) = (list[num2], list[num]);
        }
    }
}
