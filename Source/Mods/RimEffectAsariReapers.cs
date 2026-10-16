using System;
using HarmonyLib;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Rim-Effect Renegade: Asari and Reapers by Team Rim-Effect</summary>
    /// <remarks>
    /// Determinism only (pregnancy). Written against RimEffectAsariReapers.dll from
    /// <c>S:/Rimworld MP shiz/lab/game/Mods/Rim-Effect Renegade Asari and Reapers/1.6/Assemblies/RimEffectAsariReapers.dll</c>.
    /// Sweep evidence (waylith/_lanes/clrsem/sweep_il, TOP.md entry 19, token 0x06000138):
    /// <c>RimEffectAR.DomGene_Patches/RE_GetInheritedGenes_Patch::Postfix(Pawn father, Pawn mother, ref List&lt;GeneDef&gt; __result)</c> is a Harmony
    /// postfix on the inherited-gene list; it does <c>Random random = new Random(); ... ranNum = random.Next(0, 2);</c> and the coin flip picks
    /// whose dominant genes the baby inherits. A per-process coin flip means the clients disagree about the baby's genes.
    /// Fix: the <c>newobj System.Random::.ctor</c> becomes <see cref="PatchingUtilities.RandRedirector"/> (no push/pop, runs in the synced
    /// pregnancy code). This module is separate from the RimEffect Core / Drell / ExtendedCut ones: different packageId, different assembly.
    /// Status: [unverified] - compiled only, not run in game yet.
    /// </remarks>
    [MpCompatFor("rimeffectrenegade.asarireapers")]
    public class RimEffectAsariReapers
    {
        private const string LogPrefix = "[MPCompat - Rim-Effect Asari and Reapers] ";

        public RimEffectAsariReapers(ModContentPack mod)
        {
            var type = AccessTools.TypeByName("RimEffectAR.DomGene_Patches+RE_GetInheritedGenes_Patch")
                ?? throw new TypeLoadException($"{LogPrefix}type RimEffectAR.DomGene_Patches+RE_GetInheritedGenes_Patch not found");
            var method = AccessTools.DeclaredMethod(type, "Postfix")
                ?? throw new MissingMethodException(type.FullName, "Postfix");

            PatchingUtilities.PatchSystemRand(method, false);
        }
    }
}
