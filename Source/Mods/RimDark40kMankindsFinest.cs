using System.Reflection;
using HarmonyLib;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>RimDark 40k - Mankind's Finest (Genes40k) by Phonicmas</summary>
    /// <remarks>
    /// Determinism only: every site below builds a <c>new System.Random()</c> (time seeded, different on every machine) and lets it
    /// decide simulation state, so host, joiner and arbiter disagree about who dies, who is resurrected, which genes a baby gets, and so on.
    /// Written against Genes40k.dll from the folder 1.7 falls back to
    /// (<c>S:/Rimworld MP shiz/lab/game/Mods/RimDark 40k - Mankinds Finest/1.6/Assemblies/Genes40k.dll</c>, 259,072 bytes, compiled for
    /// .NET Framework 4.8 (TargetFrameworkAttribute read from the dll bytes) - no Random.Shared / NextSingle / NextInt64 / NextBytes(Span) exist
    /// for it to call, and Sample() is protected; an IL listing of the whole dll (ilspycmd -il) references only System.Random::.ctor (10) and
    /// ::Next (10), all overridden by <see cref="PatchingUtilities.RandRedirector"/>, and each of the 10 constructor calls is in a method patched below
    /// (the WeightedSelection ctor in Core40k is covered by the redirect in <see cref="RimDark40kRng"/>).
    /// Sweep evidence (waylith/_lanes/clrsem/sweep_il, TOP.md entries 2, 3, 7, 8, 9, 14, 17, 18, plus James's report):
    /// <list type="bullet">
    /// <item>WorkerClass_ImplantGeneseed::ImplantGeneseed (0x0600033B): <c>if (new Random().Next(0, 100) &lt; geneseedImplantationSuccessChance)</c> - on failure the patient is killed (<c>Kill</c>), otherwise xenotype and genes change (Space Marine surgery).</item>
    /// <item>CompAbilityEffect_GeneseedHarvest::Apply (0x06000101) - James's "harvest roll": <c>Random random = new Random(); ... if (random.Next(0, 100) &gt; num)</c> decides failure letter vs MakeGeneseedVial.</item>
    /// <item>MentalBreakdownPsyker::Postfix (0x06000349, Harmony postfix of MentalStateHandler.TryStartMentalState): <c>int num = new Random().Next(1, 100);</c> decides daemon summon, Kill and explosion; ::SummonDaemons (<c>new Random().Next(1, (int)GetStatValue(PsychicSensitivity))</c>).</item>
    /// <item>LivingSaintAscension::Postfix (0x06000255, postfix of Pawn.Kill): <c>Random random = new Random(); if ((float)random.Next(0, 100) &gt; num5)</c> decides whether TryResurrect runs.</item>
    /// <item>Gene_InduceFear::AffectPawns (0x06000169): <c>new Random().Next(0, 100) &lt;= 50</c> per hostile pawn, then StopAll / flee job / mental state.</item>
    /// <item>Gene_UnstableOrgans::TickInterval (0x0600013D): <c>Random random = new Random(); if (num > (float)random.Next(0, 100))</c> after the thingID-gated interval, decides OrganDecay.</item>
    /// <item>GameComponent_LivingSaint::TrySpawnSaint (0x060000CB): <c>if (new Random().Next(0, 100) &lt;= num) SpawnSaint();</c>.</item>
    /// <item>NaturalBirthPerpetual::Postfix (0x060001A3) and NaturalBirthPsykerPariah::Postfix (0x0600034D): birth outcome roll, then a pick from Core40k.WeightedSelection&lt;GeneDef&gt; (own unseeded Random, see <see cref="RimDark40kRng"/>).</item>
    /// </list>
    /// Fix: the <c>newobj System.Random::.ctor</c> in each method becomes <see cref="PatchingUtilities.RandRedirector"/>
    /// (<see cref="PatchingUtilities.PatchSystemRand(MethodBase, bool)"/>, without push/pop: all of these run in the synced simulation -
    /// ticks, surgery jobs, ability casts, mental states, Pawn.Kill, births - where Verse.Rand is the synced state). The two birth postfixes
    /// also get the WeightedSelection redirect, so the gene pick that follows the roll is deterministic too.
    /// Status: [unverified] - compiled only, not run in game yet; PatchingUtilities.FixRNG logs a warning when a method has nothing to replace.
    /// Not done here: the Core40k side (damage workers, gene PostMake) is in <see cref="RimDark40kFramework"/>.
    /// </remarks>
    [MpCompatFor("phonicmas.rimdark.mankindsfinest")]
    public class RimDark40kMankindsFinest
    {
        private const string LogPrefix = "[MPCompat - RimDark 40k Mankind's Finest] ";

        public RimDark40kMankindsFinest(ModContentPack mod)
        {
            RimDark40kRng.RunSteps(LogPrefix,
                ("Implant geneseed (surgery kill roll)", () => PatchSystemRand("Genes40k.WorkerClass_ImplantGeneseed", "ImplantGeneseed")),
                ("Geneseed harvest roll", () => PatchSystemRand("Genes40k.CompAbilityEffect_GeneseedHarvest", "Apply")),
                ("Psyker mental breakdown", () =>
                {
                    PatchSystemRand("Genes40k.MentalBreakdownPsyker", "Postfix");
                    PatchSystemRand("Genes40k.MentalBreakdownPsyker", "SummonDaemons");
                }),
                ("Living saint ascension", () => PatchSystemRand("Genes40k.LivingSaintAscension", "Postfix")),
                ("Living saint spawn", () => PatchSystemRand("Genes40k.GameComponent_LivingSaint", "TrySpawnSaint")),
                ("Induce fear", () => PatchSystemRand("Genes40k.Gene_InduceFear", "AffectPawns")),
                ("Unstable organs", () => PatchSystemRand("Genes40k.Gene_UnstableOrgans", "TickInterval")),
                ("Natural birth: Perpetual", () => PatchBirth("Genes40k.NaturalBirthPerpetual")),
                ("Natural birth: Psyker / Pariah", () => PatchBirth("Genes40k.NaturalBirthPsykerPariah")));
        }

        private static void PatchSystemRand(string typeName, string methodName)
            => PatchingUtilities.PatchSystemRand(RimDark40kRng.FindMethod(LogPrefix, typeName, methodName), false);

        private static void PatchBirth(string typeName)
        {
            var method = RimDark40kRng.FindMethod(LogPrefix, typeName, "Postfix");
            PatchingUtilities.PatchSystemRand(method, false);
            MpCompat.harmony.Patch(method, transpiler: new HarmonyMethod(typeof(RimDark40kRng), nameof(RimDark40kRng.RedirectWeightedSelectionRandom)));
        }
    }
}
