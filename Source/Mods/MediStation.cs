using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>MediStation by Asiimoves</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3791910009"/>
    [MpCompatFor("Asiimoves.MediStation")]
    public class MediStation
    {
        // New-mods check (29 Oct): two gizmos change state outside a synced call. "Eject medicine" (CompMediStationFuel,
        // no subclass overrides it) and the "allow guests" toggle (CompMediStationSharing lambda 1). The refuel gizmos are
        // vanilla CompRefuelable lambdas (MP core syncs them), and the float menu only calls TryTakeOrderedJob (synced).
        public MediStation(ModContentPack mod)
        {
            MP.RegisterSyncMethod(AccessTools.TypeByName("MediStation.CompMediStationFuel"), "EjectMedicine");
            MpCompat.RegisterLambdaMethod("MediStation.CompMediStationSharing", "CompGetGizmosExtra", 1);
        }
    }
}
