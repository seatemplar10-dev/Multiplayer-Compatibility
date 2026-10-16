using System;
using HarmonyLib;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>EBSG Framework by alite</summary>
    /// <remarks>
    /// Determinism only. Written against EBSGFramework.dll from <c>S:/Rimworld MP shiz/lab/game/Mods/3112549163/1.6/Assemblies/EBSGFramework.dll</c>.
    /// Sweep evidence (waylith/_lanes/clrsem/sweep_il, TOP.md entry 20 and HITS.tsv):
    /// <list type="bullet">
    /// <item><c>EBSGFramework.EBSGUtilities::GainRandomGeneSet</c> (0x060002F0): <c>double num2 = new Random().NextDouble() * (double)num;</c> picks which
    /// RandomXenoGenes set a pawn gains (weighted). No def of the lab mod list mentions RandomXenoGenes, so exposure is unverified - cheap to fix anyway.</item>
    /// <item><c>EBSGFramework.JobGiver_AICastAnyOfAbilityOnEnemyTarget</c>: <c>private static Random rnd = new Random();</c> (static ctor, 0x06000701) and
    /// <c>chosenAbility = presentAbilities[rnd.Next(presentAbilities.Count)];</c> in TryGiveJob - an AI think-tree node, i.e. simulation: which ability an
    /// AI pawn casts differed per machine.</item>
    /// </list>
    /// Fix: GainRandomGeneSet's <c>newobj System.Random::.ctor</c> becomes <see cref="PatchingUtilities.RandRedirector"/> (no push/pop);
    /// the static field <c>rnd</c> is not readonly, so it is set to <see cref="PatchingUtilities.RandRedirector.Instance"/> (same approach as the
    /// SmarterConstruction module). FieldInfo.SetValue runs the type initializer first if it has not run yet, so the mod's own initializer cannot overwrite it.
    /// Both uses are Next(n) / NextDouble(), which the redirector overrides (the dll targets .NET Framework 4.8, and an IL listing of the whole dll
    /// references only System.Random::.ctor (2: GainRandomGeneSet and the static ctor), ::Next (1) and ::NextDouble (1): no Random.Shared, NextSingle, NextInt64).
    /// Status: [unverified] - compiled only, not run in game yet.
    /// </remarks>
    [MpCompatFor("ebsg.framework")]
    public class EBSGFramework
    {
        private const string LogPrefix = "[MPCompat - EBSG Framework] ";

        public EBSGFramework(ModContentPack mod)
        {
            RimDark40kRng.RunSteps(LogPrefix,
                ("GainRandomGeneSet", () =>
                {
                    var method = RimDark40kRng.FindMethod(LogPrefix, "EBSGFramework.EBSGUtilities", "GainRandomGeneSet");
                    PatchingUtilities.PatchSystemRand(method, false);
                }),
                ("AI cast ability rnd field", () =>
                {
                    var type = RimDark40kRng.FindType(LogPrefix, "EBSGFramework.JobGiver_AICastAnyOfAbilityOnEnemyTarget");
                    var field = AccessTools.DeclaredField(type, "rnd")
                        ?? throw new MissingFieldException(type.FullName, "rnd");
                    if (!field.IsStatic || field.IsInitOnly || field.FieldType != typeof(Random))
                        throw new MissingFieldException($"{LogPrefix}{type.FullName}.rnd is {field}, expected a writable static System.Random");

                    field.SetValue(null, PatchingUtilities.RandRedirector.Instance);
                }));
        }
    }
}
