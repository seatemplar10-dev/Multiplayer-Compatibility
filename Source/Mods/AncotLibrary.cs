using System;
using Verse;

namespace Multiplayer.Compat;

/// <summary>Ancot Library by Ancot (Milira Race's turrets and weapons are built on it)</summary>
/// <remarks>
/// 1.7 lab, FIXES row 145 (d143 colony audit gaps 4 and 6, plus the two latent Building_SpinTurretGun gizmos).
/// Building_SpinTurretGun derives from Building_Turret, not Building_TurretGun, so none of MP's own turret gizmo
/// registrations (SyncDelegates.cs: Building_TurretGun.GetGizmos 1 and 2, SyncMethods: ExtractShell) reach it.
/// Each registration is wrapped on its own so one missing lambda cannot take the others down.
/// </remarks>
[MpCompatFor("Ancot.AncotLibrary")]
public class AncotLibrary
{
    public AncotLibrary(ModContentPack mod)
    {
        // 1.7 lab, FIXES row 145: Building_SpinTurretGun.GetGizmos lambdas (iterator <GetGizmos>d__79, all on the building):
        //   0 = Extract shell action  -> ExtractShell(): GenPlace.TryPlaceThing(comp.RemoveShell(), Position, Map, Near ...).
        //       Writes saved state: takes the loaded shell out of the gun's CompChangeableProjectile and spawns it on the map.
        //       (Latent: needs CanExtractShell, i.e. a loaded CompChangeableProjectile; none of the Milira turret guns has one.)
        //   1 = Stop force attack action -> ResetForcedTarget(); PlayOneShotOnCamera(Tick_Low, null).
        //       Writes saved state: forcedTarget = Invalid, burstWarmupTicksLeft = 0, and TryStartShootSomething may pick a
        //       new target / start a burst. The camera sound is local and harmless. (Latent: needs a valid forcedTarget, which
        //       only the mannable "set force attack target" command can set; the Milira turret defs have no CompMannable.)
        //   2 = Hold fire toggle -> "holdFire = !holdFire; if (holdFire) ResetForcedTarget();" (a real gap in the gate save).
        //   3 = Command_Toggle.isActive (read only, not registered).
        TryRegister("Building_SpinTurretGun extract shell / stop force attack / hold fire",
            () => MpCompat.RegisterLambdaMethod("AncotLibrary.Building_SpinTurretGun", nameof(Building.GetGizmos), 0, 1, 2));

        // 1.7 lab, FIXES row 145: verb/mode switch button on a Milira ranged weapon (CompRangeWeaponVerbSwitch). Lambda 0 of
        // the GetWeaponGizmos iterator is the Command_Action.action: it flips 'switched', swaps the primary verb's verbProps,
        // refreshes the verbs and stops the wielder's jobs - all saved/simulated state. The target is the weapon's comp
        // (ThingComp sync worker, weapon found through the wielding pawn).
        TryRegister("CompRangeWeaponVerbSwitch verb switch button",
            () => MpCompat.RegisterLambdaMethod("AncotLibrary.CompRangeWeaponVerbSwitch", "GetWeaponGizmos", 0));
    }

    private static void TryRegister(string what, Action register)
    {
        try
        {
            register();
        }
        catch (Exception e)
        {
            Log.Error($"MPCompat :: Ancot Library: could not sync {what} (FIXES row 145): {e}");
        }
    }
}
