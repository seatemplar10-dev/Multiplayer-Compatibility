using System.Linq;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat;

/// <summary>Exosuit Framework by AobaKuma</summary>
/// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3352894993"/>
/// <see href="https://github.com/AobaKuma/MechsuitFramework"/>
[MpCompatFor("Aoba.Exosuit.Framework")]
public class ExosuitFramework
{
    // The mod renamed its namespace from WalkerGear to Exosuit (see Exosuit.BackCompat_Exosuit_1_6) and
    // now registers some of its own sync methods (MP.RegisterAll in Exosuit.MultiplayerCompatibility).
    // ModuleComp_EmergencyEject and WalkerGear_Core:Eject were removed, there is nothing left to sync for them.
    public ExosuitFramework(ModContentPack mod)
    {
        #region Gizmos

        {
            // Eject to location (0), launch to map (1), release (6)
            MpCompat.RegisterLambdaMethod("Exosuit.Building_EjectorBay", nameof(Building.GetGizmos), 0, 1, 6)
                // The first 2 gizmos use current map, the last one doesn't care
                .SkipLast(1).SetContext(SyncContext.CurrentMap);
            // Get in (7), toggle auto repair (10)
            MpCompat.RegisterLambdaMethod("Exosuit.Building_MaintenanceBay", nameof(Building.GetGizmos), 7, 10);
        }

        #endregion

        #region Float Menus

        {
            var type = AccessTools.TypeByName("Exosuit.Building_MaintenanceBay");
            // Remove module, called from ITab_Exosuit. Install/replace/cancel install go through the mod's
            // own [SyncMethod]s now; remove calls RequestRemoveModule directly (the old ITab called RemoveModules).
            MP.RegisterSyncMethod(type, "RequestRemoveModule");

            type = AccessTools.TypeByName("Exosuit.FloatMenuOptionProvider_ExosuitDown");
            // Take to maintenance bay. Needs to be synced due to the method
            // modifying a field after starting a job - `job.count = 1`.
            // GetOptionsFor is overloaded (Pawn/Thing), the option is in the Pawn one.
            var lambda = MpMethodUtil.GetLambda(type, "GetOptionsFor", MethodType.Normal, [typeof(Pawn), typeof(FloatMenuContext)], 1);
            MP.RegisterSyncDelegate(type, lambda.DeclaringType!.Name, lambda.Name, null);
        }

        #endregion

        // Once it's included in a mod/implemented, also patch Building_EjectorBay and WG_PawnFlyer.
        // The building (and thus the flyer) aren't used by this mod or any of its addons yet.
    }
}
