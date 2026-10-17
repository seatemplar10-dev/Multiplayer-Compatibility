using HarmonyLib;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Grazing Lands by avilmask</summary>
    /// <see href="https://github.com/catgirlfighter/RimWorld_GrazingLands"/>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=1770268130"/>
    [MpCompatFor("avilmask.GrazingLands")]
    public class GrazingLands
    {
        public GrazingLands(ModContentPack mod)
        {
            var type = AccessTools.TypeByName("GrazingLands.PlantPropertiesPatch");
            type = AccessTools.Inner(type, "Plant_IngestedCalculateAmounts_GrazingLandsPatch");
            // The 1.6 build that 1.7 loads draws `UnityEngine.Random.Range(1, 100) <= ConsumeChance` (no System.Random),
            // so PatchSystemRand changed nothing (lab mp54: "No System RNG was patched"). FIXES row 45, 17 Oct 2026.
            PatchingUtilities.PatchUnityRand(AccessTools.Method(type, "Prefix"), false);
        }
    }
}