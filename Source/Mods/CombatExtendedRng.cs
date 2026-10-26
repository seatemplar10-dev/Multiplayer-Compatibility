using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Combat Extended - world artillery randomness</summary>
    /// <remarks>
    /// FIXES row 57 (17 Oct 2026, REA static sweep; same code in waylith\src\CombatExtended and in the copy bundled with
    /// Ancient Amorphous Threat):
    /// <list type="bullet">
    /// <item><c>Verb_ShootMortarCE.ShiftGlobalTarget</c> (Verbs/Verb_ShootMortarCE.cs:306) spreads a world-target mortar
    /// shot with <c>UnityEngine.Random.insideUnitCircle</c>.</item>
    /// <item><c>CE_Utility.RandomGaussian</c> (CE_Utility.cs:36-37) uses <c>UnityEngine.Random.value</c>; its only callers
    /// pick the impact cell of a world shell (<c>ShellingUtility</c>:206, <c>WorldObjectDamageWorker</c>:168), each
    /// calling it twice - so no push/pop, the draws must advance the synced stream.</item>
    /// </list>
    /// Both run on every PC in simulation code -> draw from Verse.Rand instead.
    /// FIXES row 63 (lab mp58, likely also row 43): <c>CE_Utility.MakeCasingFilth</c> spawns casing filth inside
    /// Rand.PushState/PopState (so no trace in the Rand stream) at a cell taken from <c>Verb_ShootCE.drawPos</c>, which only
    /// the pawn renderer patch writes (the headless arbiter never draws), gated by <c>position.Walkable</c> and by the
    /// per-PC setting <c>CreateCasingsFilth</c> -> one PC makes an extra filth thing and every later thing id shifts.
    /// In MP no casing filth is made (the visual casing flecks are unchanged).
    /// FIXES row 192 lead (26 Oct 2026, lab round 1 run B): <c>WorldObjectTrackerCE.TryRegister</c> (WorldObjects/WorldObjectTrackerCE.cs:119)
    /// puts a new world object into one of 15 throttled tick buckets, breaking ties with <c>Rand.Chance(0.5f)</c> on the shared stream.
    /// The settlement of a new multifaction player faction landed in different buckets on host and joiner (its HostilityRaider ticksToRaid
    /// -4036 vs -4021 at the next checkpoint), and every draw there moves the stream for what follows. In MP the tie-breaks draw from a state
    /// seeded by the object's synced ID inside Push/PopState: same bucket on every PC, shared stream untouched.
    /// </remarks>
    [MpCompatFor("CETeam.CombatExtended")]
    internal class CombatExtendedRng
    {
        public CombatExtendedRng(ModContentPack mod)
        {
            PatchingUtilities.PatchUnityRand("CombatExtended.Verb_ShootMortarCE:ShiftGlobalTarget", false);
            PatchingUtilities.PatchUnityRand("CombatExtended.CE_Utility:RandomGaussian", false);

            var makeCasingFilth = HarmonyLib.AccessTools.DeclaredMethod("CombatExtended.CE_Utility:MakeCasingFilth")
                ?? throw new System.MissingMethodException("CombatExtended.CE_Utility", "MakeCasingFilth");
            MpCompat.harmony.Patch(makeCasingFilth,
                prefix: new HarmonyLib.HarmonyMethod(typeof(CombatExtendedRng), nameof(NoCasingFilthInMp)));

            var tryRegister = HarmonyLib.AccessTools.DeclaredMethod("CombatExtended.WorldObjects.WorldObjectTrackerCE:TryRegister",
                                  new[] { typeof(RimWorld.Planet.WorldObject) })
                              ?? throw new System.MissingMethodException("CombatExtended.WorldObjects.WorldObjectTrackerCE", "TryRegister(WorldObject)");
            MpCompat.harmony.Patch(tryRegister,
                prefix: new HarmonyLib.HarmonyMethod(typeof(CombatExtendedRng), nameof(PreTryRegister)),
                finalizer: new HarmonyLib.HarmonyMethod(typeof(CombatExtendedRng), nameof(FinalTryRegister)));
        }

        private static bool NoCasingFilthInMp() => !Multiplayer.API.MP.IsInMultiplayer;

        private static void PreTryRegister(RimWorld.Planet.WorldObject worldObject, out bool __state)
        {
            __state = Multiplayer.API.MP.IsInMultiplayer && worldObject != null;
            if (__state)
                Rand.PushState(Gen.HashCombineInt(worldObject.ID, 0x5EA192));
        }

        private static void FinalTryRegister(bool __state)
        {
            if (__state)
                Rand.PopState();
        }
    }
}
