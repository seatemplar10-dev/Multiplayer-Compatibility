using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;
using Verse.AI;

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
    // Caveat: MP writes nothing for a by-ref parameter and reads it back as null, so the mod's own [SyncMethod]
    // local functions that take their closure struct by ref (SyncGetIn, SyncGetInOnePawn) only work when they
    // run inside a command we sync here; the one on a closure class (SyncGetInManyPawns) has no writer at all.

    // Building_MaintenanceBay
    private static FastInvokeHandler maintenanceBayCanAcceptPawnMethod;
    // Building_MaintenanceBay+<>c__DisplayClass22_0, the closure of the "get in (assigned)" gizmo action
    private static AccessTools.FieldRef<object, ThingComp> getInAssignedAssignableField;
    private static AccessTools.FieldRef<object, Building> getInAssignedBayField;
    // Exosuit.JobDefOf.WG_GetInWalkerCore
    private static FieldInfo getInWalkerCoreJobDefField;

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

            // Get in (assigned pilot). The gizmo action (lambda 0, closure 22_0) picks the assigned pilots that can
            // get in; with one pilot it calls SyncGetInOnePawn (closure struct by ref, see caveat above), with more
            // it opens a float menu whose options (lambda 4, closure 22_2: p + bay) call SyncGetInManyPawns.
            var bayType = AccessTools.TypeByName("Exosuit.Building_MaintenanceBay");
            maintenanceBayCanAcceptPawnMethod = MethodInvoker.GetHandler(AccessTools.DeclaredMethod(bayType, "CanAcceptPawn", [typeof(Pawn)]));
            getInWalkerCoreJobDefField = AccessTools.DeclaredField(AccessTools.TypeByName("Exosuit.JobDefOf"), "WG_GetInWalkerCore");
            var getInAssignedAction = MpMethodUtil.GetLambda(bayType, nameof(Building.GetGizmos), lambdaOrdinal: 0);
            getInAssignedAssignableField = AccessTools.FieldRefAccess<object, ThingComp>(AccessTools.DeclaredField(getInAssignedAction.DeclaringType, "assignable"));
            getInAssignedBayField = AccessTools.FieldRefAccess<object, Building>(AccessTools.DeclaredField(getInAssignedAction.DeclaringType, "<>4__this"));
            MpCompat.harmony.Patch(getInAssignedAction, prefix: new HarmonyMethod(PreGetInAssignedGizmo));
            MP.RegisterSyncMethod(MpMethodUtil.MethodOf(SyncedGetInAssigned)).CancelIfAnyArgNull();
            // Pilot float menu option (2+ assigned pilots): sync the closure (pilot + bay).
            MpCompat.RegisterLambdaDelegate(bayType, nameof(Building.GetGizmos), 4);

            // Turret module (Mechsuit.CompTurretGun, instance lambdas): toggle fire at will (1), stop forced attack (2),
            // dev: force shoot (3). Manual attack (Command_VerbTarget) ends in the turret verb's OrderForceTarget,
            // which calls OrderAttack - it sets the forced target and starts the warmup, so sync it.
            MpCompat.RegisterLambdaMethod("Mechsuit.CompTurretGun", "GetGizmos", 1, 2);
            MpCompat.RegisterLambdaMethod("Mechsuit.CompTurretGun", "GetGizmos", 3).SetDebugOnly();
            MP.RegisterSyncMethod(AccessTools.TypeByName("Mechsuit.CompTurretGun"), "OrderAttack");

            // Module weapon worn gizmo options (closure 7_0): bare hands (0), take out the module weapon (1).
            // The closure also holds the gizmo (command), which has no sync worker and isn't used by those 2.
            MpCompat.RegisterLambdaDelegate("Exosuit.CompModuleWeapon", nameof(ThingComp.CompGetWornGizmosExtra), ["wearer", "<>4__this"], 0, 1);

            // Fuel cell (not worn): unload fuel to the ground - gizmo action and right click option both call it.
            MP.RegisterSyncMethod(AccessTools.TypeByName("Exosuit.CompFuelCell"), "UnloadFuelToGround");
        }

        #endregion

        #region Float Menus

        {
            var type = AccessTools.TypeByName("Exosuit.Building_MaintenanceBay");
            // Remove module, called from ITab_Exosuit. Install/replace/cancel install go through the mod's
            // own [SyncMethod]s now; remove calls RequestRemoveModule directly (the old ITab called RemoveModules).
            MP.RegisterSyncMethod(type, "RequestRemoveModule");
            // ITab_Exosuit.GizmoFloatMenu: cancel replace / cancel remove (lambdas 5, 6) call CancelPendingWork,
            // cancel core work (lambda 0) calls CancelAllCoreWork - both edit pendingModuleWork, which the jobs read.
            MP.RegisterSyncMethod(type, "CancelPendingWork");
            MP.RegisterSyncMethod(type, "CancelAllCoreWork");
            // Change module colour (lambda 4, the Dialog_ChooseColor callback): t.SetColor(color). Only the module
            // (t) is needed; the closure's ITab (<>4__this) has no sync worker.
            MpCompat.RegisterLambdaDelegate("Exosuit.ITab_Exosuit", "GizmoFloatMenu", ["t"], 4);

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

    #region Get in (assigned pilot)

    private static bool CanGetIn(Building bay, Pawn pawn)
        => pawn.MapHeld == bay.MapHeld && pawn.Spawned && !pawn.DeadOrDowned &&
           ((AcceptanceReport)maintenanceBayCanAcceptPawnMethod(bay, pawn)).Accepted;

    // Prefix on the gizmo action (closure 22_0). Same candidate filter as the mod.
    private static bool PreGetInAssignedGizmo(object __instance)
    {
        if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand)
            return true;

        var bay = getInAssignedBayField(__instance);
        if (bay == null || getInAssignedAssignableField(__instance) is not CompAssignableToPawn assignable)
            return false;

        var candidates = assignable.AssignedPawnsForReading.Where(p => CanGetIn(bay, p)).ToList();
        // 2+ pilots: let the mod open its float menu, the options are synced (GetGizmos lambda 4).
        if (candidates.Count > 1)
            return true;
        if (candidates.Count == 1)
            SyncedGetInAssigned(bay, candidates[0]);
        return false;
    }

    private static void SyncedGetInAssigned(Building bay, Pawn pawn)
    {
        // The state may have changed between the click and the command.
        if (!CanGetIn(bay, pawn))
            return;

        var jobDef = (JobDef)getInWalkerCoreJobDefField.GetValue(null);
        pawn.jobs.StartJob(JobMaker.MakeJob(jobDef, bay), JobCondition.InterruptForced);
    }

    #endregion
}
