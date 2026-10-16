using System.Reflection;
using HarmonyLib;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>RimDark 40k - Framework (Core40k) by Phonicmas</summary>
    /// <remarks>
    /// Determinism only: every site below builds a <c>new System.Random()</c> (time seeded, different on every machine) and
    /// lets it decide simulation state. Written against Core40k.dll from the folder 1.7 falls back to
    /// (<c>S:/Rimworld MP shiz/lab/game/Mods/RimDark 40k - Framework/1.6/Assemblies/Core40k.dll</c>, 253,952 bytes, compiled for
    /// .NET Framework 4.8 (TargetFrameworkAttribute read from the dll bytes) - so no Random.Shared, NextSingle, NextInt64 or NextBytes(Span)
    /// exist for it to call, and Sample() is protected; an IL listing of the whole dll (ilspycmd -il) references only System.Random::.ctor (7),
    /// ::Next (7) and ::NextDouble (2), which is all <see cref="PatchingUtilities.RandRedirector"/> overrides). Sweep evidence (waylith/_lanes/clrsem/sweep_il, TOP.md):
    /// <list type="bullet">
    /// <item>Core40k.DamageWorker_WarpFlame::Apply (0x06000361) - <c>int num = new Random().Next(1, 3);</c> extra hits per damage, then Rand.Chance fire attach.</item>
    /// <item>Core40k.DamageWorker_Holy::Apply (0x06000106) - one Random for hit count and ignite chance (<c>random.Next(0, 100) &lt; modExtension.chanceToIgnite</c>).</item>
    /// <item>Core40k.Gene_AddRandomGeneAndOrTraitByWeight::SelectGeneToGive (0x0600012E) and ::SelectTraitToGive - chance roll
    /// <c>new Random().Next(0, 100) &gt; GeneDefMod.chanceToGrantGene</c> in Gene.PostMake (Chapter / Primarch genes), then a pick from
    /// WeightedSelection&lt;T&gt;, which owns its own <c>new Random()</c> (0x0600035D, see <see cref="RimDark40kRng"/>). The chosen gene/trait is saved in the gene.</item>
    /// <item>Core40k.Gene_ChanceToAddEachGene::SelectGeneToGive (0x06000139) and Core40k.GenderDistributionPatch::Postfix (0x06000351,
    /// Harmony postfix of PawnGenerator.GenerateGenes) - DORMANT in the lab list (no def uses them) but cheap to fix; the gender one would
    /// re-roll the gender of every generated pawn that carries a DefModExtension_GenderDistribution gene.</item>
    /// </list>
    /// Fix: the <c>newobj System.Random::.ctor</c> in each method becomes <see cref="PatchingUtilities.RandRedirector"/>
    /// (<see cref="PatchingUtilities.PatchSystemRand(MethodBase, bool)"/>), so the roll comes from Verse.Rand, which MP seeds per synced
    /// context. The damage workers run in the synced simulation and are patched without push/pop; the gene and pawn-generation sites
    /// can also be reached from UI code (gene previews, pawn generation for dialogs) and are patched WITH Rand.PushState/PopState, so
    /// a draw made on one client only cannot move the shared state. Callers of WeightedSelection also get its <c>rand</c> field redirected.
    /// 16 Oct (r25): the 1.7 Workshop build (Core40k 1.2.112, <c>1.7/Assemblies/Core40k.dll</c> 678,400 bytes, .NET 10) has NO System.Random any more - an IL
    /// listing of the whole dll has 0 <c>System.Random::.ctor</c>/<c>Next</c>/<c>NextDouble</c> (the 1.6 build above: 7/7/2), every site above calls Verse.Rand and
    /// WeightedSelection&lt;T&gt; (now <c>Rand.Range</c> in PickIndex, no <c>rand</c> field) has been rewritten. So <see cref="RimDark40kRng.PatchSite"/> only installs the
    /// System.Random transpiler on methods that still have the constructor and <see cref="RimDark40kRng.PatchWeightedSelectionCaller"/> only when the field exists;
    /// the sites that were patched with push/pop keep that wrapper either way. Both builds work; the module logs how many sites it replaced / found already deterministic.
    /// Status: [unverified] - compiled only, not run in game yet; the transpilers log a warning when a method has nothing to replace.
    /// Everything is MP-agnostic and harmless outside a session (Rand is just the normal game RNG there).
    /// </remarks>
    [MpCompatFor("phonicmas.rimdark.frameworkcore")]
    public class RimDark40kFramework
    {
        private const string LogPrefix = "[MPCompat - RimDark 40k Framework] ";

        public RimDark40kFramework(ModContentPack mod)
        {
            RimDark40kRng.RunSteps(LogPrefix,
                ("WarpFlame damage", () => PatchSystemRand("Core40k.DamageWorker_WarpFlame", "Apply", false)),
                ("Holy damage", () => PatchSystemRand("Core40k.DamageWorker_Holy", "Apply", false)),
                ("Random gene / trait by weight", PatchRandomGeneAndTrait),
                ("Chance to add each gene (dormant)", () => PatchSystemRand("Core40k.Gene_ChanceToAddEachGene", "SelectGeneToGive", true)),
                ("Gender distribution (dormant)", () => PatchSystemRand("Core40k.GenderDistributionPatch", "Postfix", true)));
        }

        private static void PatchSystemRand(string typeName, string methodName, bool pushPop)
            => RimDark40kRng.PatchSite(LogPrefix, RimDark40kRng.FindMethod(LogPrefix, typeName, methodName), pushPop);

        private static void PatchRandomGeneAndTrait()
        {
            const string typeName = "Core40k.Gene_AddRandomGeneAndOrTraitByWeight";

            // Both methods build a WeightedSelection<T> locally; in the 1.6 builds each also has `new Random().Next(0, 100)` for the chance roll
            // and WeightedSelection owns a System.Random. Core40k 1.2.112+ (1.7 Workshop build) uses Rand for both, see RimDark40kRng.
            foreach (var name in new[] { "SelectGeneToGive", "SelectTraitToGive" })
            {
                var method = RimDark40kRng.FindMethod(LogPrefix, typeName, name);
                RimDark40kRng.PatchSite(LogPrefix, method, true);
                RimDark40kRng.PatchWeightedSelectionCaller(LogPrefix, method);
            }
        }
    }
}
