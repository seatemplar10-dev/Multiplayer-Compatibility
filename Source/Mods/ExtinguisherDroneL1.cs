using HarmonyLib;
using Multiplayer.API;
using System;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Extinguisher Drone by Rabiosus. The home-area gizmo toggle writes a saved field read by the sim.</summary>
    [MpCompatFor("rabiosus.ExtinguisherDrone")]
    internal class ExtinguisherDroneL1
    {
        public ExtinguisherDroneL1(ModContentPack mod)
        {
            var spawner = AccessTools.TypeByName("ExtinguisherDrone.CompProximityFireDroneSpawner");
            // lambda ordinal 0 = isActive, 1 = toggleAction (the `_homeAreaOnly = !_homeAreaOnly` write)
            MpCompat.RegisterLambdaMethod(spawner, "CompGetGizmosExtra", 1);
            Log.Message("[MPCompat - FULL1127 L1] ExtinguisherDrone: home-area toggle registered");
        }
    }
}
