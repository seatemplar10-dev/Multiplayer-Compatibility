using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
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
    /// 16 Oct (r25): the 1.7 Workshop build (Genes40k 1.1.95, <c>1.7/Assemblies/Genes40k.dll</c> 454,144 bytes, .NET 10) has no System.Random either (IL listing: 0 constructors,
    /// 0 <c>Next</c>; the 1.6 build above: 10/10) - all nine sites call Verse.Rand now and WeightedSelection has no <c>rand</c> field. The module then installs nothing for
    /// them (see <see cref="RimDark40kRng.PatchSite"/>) and still works unchanged against the 1.6 build. NEW desync risk in 1.1.95, not covered here: custom chapter genes
    /// (<c>CustomChapterGeneDefBuilder.NewDefName</c> = prefix + <c>Guid.NewGuid()</c>, called from <c>GameComponent_CustomChapterGenes.Add</c> by the chapter dialogs) create
    /// GeneDefs at runtime with a per-machine name and unsynced game-component state - that needs a sync module of its own.
    /// 22 Oct (err_mf_settle lane e, not a determinism fix): Genes40k's Harmony postfix <c>OffworldMarineMimicPlayerRelation.Postfix(Faction __instance, Faction other)</c>
    /// on <c>Faction.Notify_RelationKindChanged</c> copies "the player's" relation with the changed faction onto its Offworld Marines faction and assumes only one
    /// side of the change is a player faction. With two player factions (multifaction) both are: <c>Faction.OfPlayer</c> is then one of them, so
    /// <c>RelationWith(itself)</c> logs "Tried to get relation between faction ... and itself." / "Relation is missing faction." and a player-vs-player change
    /// would be mirrored onto the marines (the 20:32 autosave had them Hostile to the second faction). <see cref="SkipPlayerVersusPlayerMimic"/> skips the postfix
    /// when both factions are player factions; single player never has that case.
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
                ("Natural birth: Psyker / Pariah", () => PatchBirth("Genes40k.NaturalBirthPsykerPariah")),
                ("Offworld Marines relation mirror: skip player-vs-player (multifaction)", PatchOffworldMimicRelation));
        }

        private static void PatchOffworldMimicRelation()
        {
            // Throws (-> logged by RunSteps as a failed step, the other steps still ran) when the type / method is missing in this build of the mod.
            var method = RimDark40kRng.FindMethod(LogPrefix, "Genes40k.OffworldMarineMimicPlayerRelation", "Postfix");
            var parameters = method.GetParameters();
            if (!method.IsStatic || parameters.Length != 2 || parameters[0].ParameterType != typeof(Faction) || parameters[1].ParameterType != typeof(Faction))
                throw new MissingMethodException($"{LogPrefix}{method.DeclaringType?.FullName}.Postfix is not static Postfix(Faction, Faction): {method}");

            MpCompat.harmony.Patch(method, prefix: new HarmonyMethod(typeof(RimDark40kMankindsFinest), nameof(SkipPlayerVersusPlayerMimic)));
        }

        // Prefix on the Genes40k postfix. The patched method is static and its first parameter is called __instance (it is a postfix itself):
        // in a prefix of ours the name __instance would mean "instance of a static method" = null, so both factions are read by position.
        private static bool SkipPlayerVersusPlayerMimic(Faction __0, Faction __1)
            => !(__0 != null && __1 != null && __0.IsPlayer && __1.IsPlayer);

        private static void PatchSystemRand(string typeName, string methodName)
            => RimDark40kRng.PatchSite(LogPrefix, RimDark40kRng.FindMethod(LogPrefix, typeName, methodName), false);

        private static void PatchBirth(string typeName)
        {
            var method = RimDark40kRng.FindMethod(LogPrefix, typeName, "Postfix");
            RimDark40kRng.PatchSite(LogPrefix, method, false);
            RimDark40kRng.PatchWeightedSelectionCaller(LogPrefix, method);
        }
    }
}
