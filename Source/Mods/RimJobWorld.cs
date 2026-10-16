using System;
using HarmonyLib;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>RimJobWorld by Ed86</summary>
    /// <remarks>
    /// Determinism only (births). Written against RJW.dll from <c>S:/Rimworld MP shiz/lab/game/Mods/rjw-master/1.6/Assemblies/RJW.dll</c>.
    /// Sweep evidence (waylith/_lanes/clrsem/sweep_il, TOP.md entry 13, token 0x0600050C): <c>rjw.Hediff_BasePregnancy::GenerateBabies(DnaGivingParent)</c>
    /// is <c>[SyncMethod(SyncContext.None)]</c> (it runs on every client) but starts with <c>Random random = new Random();</c> and uses
    /// <c>random.Next(0, list2.Count)</c> / <c>random.Next(0, list3.Count)</c> to pick the baby's inherited traits (four sites). SyncContext.None
    /// only means "no map/world seed is forced", it does not touch System.Random, so each client drew its own traits for the same baby.
    /// Fix: the <c>newobj System.Random::.ctor</c> becomes <see cref="PatchingUtilities.RandRedirector"/> (no push/pop: the method runs
    /// from the synced hediff tick, where Verse.Rand is the synced state; the other randomness in the method already uses Rand / RandomElement).
    /// The only other System.Random in RJW.dll that the sweep found is this one. Status: [unverified] - compiled only, not run in game yet.
    /// </remarks>
    [MpCompatFor("rim.job.world")]
    public class RimJobWorld
    {
        private const string LogPrefix = "[MPCompat - RimJobWorld] ";

        public RimJobWorld(ModContentPack mod)
        {
            var type = AccessTools.TypeByName("rjw.Hediff_BasePregnancy")
                ?? throw new TypeLoadException($"{LogPrefix}type rjw.Hediff_BasePregnancy not found");
            var method = AccessTools.DeclaredMethod(type, "GenerateBabies")
                ?? throw new MissingMethodException(type.FullName, "GenerateBabies");

            PatchingUtilities.PatchSystemRand(method, false);
        }
    }
}
