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
    /// </remarks>
    [MpCompatFor("CETeam.CombatExtended")]
    internal class CombatExtendedRng
    {
        public CombatExtendedRng(ModContentPack mod)
        {
            PatchingUtilities.PatchUnityRand("CombatExtended.Verb_ShootMortarCE:ShiftGlobalTarget", false);
            PatchingUtilities.PatchUnityRand("CombatExtended.CE_Utility:RandomGaussian", false);
        }
    }
}
