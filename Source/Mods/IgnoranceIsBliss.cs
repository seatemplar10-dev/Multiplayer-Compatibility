using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Ignorance Is Bliss</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=2554423472"/>
    /// IgnoranceBase keeps the player's tech level in an unsaved static field (cachedTechLevel). It is computed once
    /// from the research of whichever player faction is current at that moment, and any faction's FinishProject overwrites it.
    /// With per-faction research (multifaction) and a player who joined later, every PC holds a different value.
    /// FactionCanBeGroupSource then differs (visitor group fired on the host only, map tick 775000, FIXES row 300).
    /// In MP: recompute the value from the current faction's synced research before each eligibility check.
    [MpCompatFor("dame.ignorance")]
    public class IgnoranceIsBliss
    {
        private static FastInvokeHandler getPlayerTech;
        private static AccessTools.FieldRef<TechLevel> cachedTechLevel;

        public IgnoranceIsBliss(ModContentPack mod)
        {
            var type = AccessTools.TypeByName("DIgnoranceIsBliss.Core_Patches.IgnoranceBase");
            getPlayerTech = MethodInvoker.GetHandler(AccessTools.DeclaredMethod(type, "GetPlayerTech"));
            cachedTechLevel = AccessTools.StaticFieldRefAccess<TechLevel>(AccessTools.DeclaredField(type, "cachedTechLevel"));

            // TechIsEligibleForIncident is the only simulation reader of PlayerTechLevel (the other readers are the settings UI).
            // Patch it, not the small PlayerTechLevel getter, which the JIT may inline into its callers.
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(type, "TechIsEligibleForIncident"),
                prefix: new HarmonyMethod(typeof(IgnoranceIsBliss), nameof(PreTechIsEligibleForIncident)));
        }

        private static void PreTechIsEligibleForIncident()
        {
            if (!MP.IsInMultiplayer || Current.Game == null)
                return;
            cachedTechLevel() = (TechLevel)getPlayerTech(null);
        }
    }
}
