using System;
using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat;

/// <summary>Automatic Parking by rabiosus</summary>
/// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3365473553"/>
[MpCompatFor("rabiosus.vfautoparking")]
public class AutomaticParking
{
    public AutomaticParking(ModContentPack mod)
    {
        // The separate ParkingController class was merged into CompParkingController (a ThingComp),
        // so the methods are synced on the comp and MP's built-in ThingComp sync worker handles the instance.
        var type = AccessTools.TypeByName("VehicleAutoParking.Core.CompParkingController");
        MP.RegisterSyncMethod(type, "SaveParkingPosition");
        MP.RegisterSyncMethod(type, "ResetParkingPosition");
        MP.RegisterSyncMethod(type, "MoveToParkingPosition");
    }
}