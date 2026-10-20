using System;
using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Gene Extractor Tiers by RedMattis</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3016454783"/>
    /// <remarks>
    /// FIXES row 145 (20 Oct 2026, lab gate audit gaps 13, 14, 17, 18, 20). Written against the lab copy of
    /// <c>GeneExtractorTiers.dll</c> (<c>lab/game/Mods/3016454783/1.6/Assemblies</c>; the <c>1.6/Mods/VNPE</c> copy is byte-identical, md5 4e08449b...).
    /// <c>Building_GeneExtractorTier</c> derives from <c>RimWorld.Building_Enterable</c>, not from <c>Building_GeneExtractor</c>, so the
    /// vanilla registrations in Multiplayer (SyncMethods.cs: <c>Building_GeneExtractor.Cancel</c> and its <c>GetGizmos</c> lambda 2) do not
    /// reach any of these clicks. "Insert person..." already works: its option calls <c>Building_Enterable.SelectPawn</c>, which Multiplayer syncs.
    /// None of the synced bodies reads per-PC state: they only touch saved fields (<c>overchargeActive</c>, <c>ticksRemaining</c>,
    /// <c>targetGene</c>, <c>startTick</c>, <c>selectedPawn</c>, <c>innerContainer</c>) and <c>Position</c>/<c>Map</c>/<c>InteractionCell</c> of the building.
    /// The type is registered late (<see cref="LongEventHandler.ExecuteWhenFinished"/>) because it is <c>beforefieldinit</c> and its static
    /// fields call <c>ContentFinder&lt;Texture2D&gt;.Get</c>: patching a method of it during mod loading could run that initializer too early.
    /// Status: [unverified] - compiled only, not run in game.
    /// </remarks>
    [MpCompatFor("RedMattis.GeneExtractor")]
    public class GeneExtractorTiers
    {
        private const string LogPrefix = "[MPCompat - Gene Extractor Tiers] ";

        public GeneExtractorTiers(ModContentPack mod) => LongEventHandler.ExecuteWhenFinished(LatePatch);

        private static void LatePatch()
        {
            var type = AccessTools.TypeByName("GeneExtractorTiers.Building_GeneExtractorTier");
            if (type == null)
            {
                Log.Error($"{LogPrefix}type GeneExtractorTiers.Building_GeneExtractorTier not found, nothing was registered");
                return;
            }

            // Each registration is separate, so one name that does not resolve does not hide the others.

            // 1.7 lab, FIXES row 145: gaps 13 + 14 - 'Overdrive' toggle gizmo. The two lambdas live on the building itself (the iterator only
            // captures `this`), named <GetGizmos>b__67_0 / b__67_1. By the IL, ordinal 0 is the DEACTIVATE action (overchargeActive = false;
            // ticksRemaining *= 2) and ordinal 1 the ACTIVATE action (overchargeActive = true; ticksRemaining /= 2): the audit table has the two
            // labels the other way round, which does not matter here because both are registered.
            Try(13, "Activate/Deactivate overdrive", () => MpCompat.RegisterLambdaMethod(type, "GetGizmos", 0, 1));

            // 1.7 lab, FIXES row 145: gap 17 - 'Cancel load' (pull the inserted pawn out before the extraction starts), <GetGizmos>b__67_4.
            Try(17, "Cancel load", () => MpCompat.RegisterLambdaMethod(type, "GetGizmos", 4));

            // 1.7 lab, FIXES row 145: gap 18 - 'Cancel extraction' (gizmo `action = Cancel`, a private instance method with no callers except the gizmo).
            Try(18, "Cancel extraction", () => MP.RegisterSyncMethod(type, "Cancel"));

            // 1.7 lab, FIXES row 145: gap 20 - 'Select gene'. OpenFloatMenuGenePicker only builds a float menu (UI), every option runs the closure
            // <>c__DisplayClass66_0.<OpenFloatMenuGenePicker>b__1 = `targetGene = gene;` with the fields `gene` (a GeneDef) and `<>4__this` (the building).
            // The menu is built from saved state only (selectedPawn.genes, the genepacks on the building's map).
            Try(20, "Select gene", () => MpCompat.RegisterLambdaDelegate(type, "OpenFloatMenuGenePicker", 1));
        }

        private static void Try(int gap, string what, Action register)
        {
            try
            {
                register();
            }
            catch (Exception e)
            {
                Log.Error($"{LogPrefix}FIXES row 145 gap {gap} ({what}) was not registered: {e}");
            }
        }
    }
}
