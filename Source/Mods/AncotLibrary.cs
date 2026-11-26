using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
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
    private static FieldInfo aerocraftFlightStateField;

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

        // FIXES row 473 (26 Nov 2026): Building_Aerocraft.GetGizmos has three click lambdas, all instance methods of the building
        // (they capture only "this"), all writing sim state on the clicking PC only:
        //   take off (Command_Action)       -> FlightState = TakingOff, targetPosition, turningTick, RefuelableComp.ConsumeFuel,
        //                                      allowAutoRefuelCache = allowAutoRefuel, allowAutoRefuel = false
        //   land (Command_Land.action)      -> FlightState = Landing, landRotation, SetTargetDestination(pos)
        //   retarget (Command_Land right click on the map) -> SetTargetDestination(pos): targetPosition, targetDirection, turningTick
        // Tick() switches on FlightState, so one PC flies while the others stay grounded. Sync the three lambdas (args IntVec3 / Rot4).
        // A second take off / land command (two players clicked) is dropped when the state no longer allows it.
        if (LabOff.Row(473))
            Log.Message("[MPCompat - Ancot Library] row 473 switched off by the lab, aerocraft take off / land buttons not synced");
        else
            PatchAerocraftGizmos();
    }

    private static void PatchAerocraftGizmos()
    {
        var type = AccessTools.TypeByName("AncotLibrary.Building_Aerocraft");
        aerocraftFlightStateField = type == null ? null : AccessTools.DeclaredField(type, "FlightState");
        var lambdas = type == null
            ? new List<MethodInfo>()
            : AccessTools.GetDeclaredMethods(type).Where(m => m.Name.StartsWith("<GetGizmos>b__") && !m.IsStatic && m.ReturnType == typeof(void)).ToList();
        var takeOff = lambdas.FirstOrDefault(m => ParamsAre(m) && UsesMember(m, "FlightState") && UsesMember(m, "allowAutoRefuelCache"));
        var land = lambdas.FirstOrDefault(m => ParamsAre(m, typeof(IntVec3), typeof(Rot4)) && UsesMember(m, "FlightState") && UsesMember(m, "landRotation"));
        var retarget = lambdas.FirstOrDefault(m => ParamsAre(m, typeof(IntVec3)) && UsesMember(m, "SetTargetDestination"));

        int synced = 0;
        foreach (var (method, what, guard) in new[] { (takeOff, "take off", nameof(PreSyncedTakeOff)), (land, "land", nameof(PreSyncedLand)), (retarget, "retarget", null) })
        {
            if (method == null || aerocraftFlightStateField == null)
            {
                Log.Error($"[MPCompat - Ancot Library] row 473: Building_Aerocraft {what} gizmo lambda not found, that button can desync");
                continue;
            }
            try
            {
                MP.RegisterSyncMethod(method);
                if (guard != null)
                    MpCompat.harmony.Patch(method, prefix: new HarmonyMethod(typeof(AncotLibrary), guard));
                synced++;
            }
            catch (Exception e)
            {
                Log.Error($"[MPCompat - Ancot Library] row 473: could not sync Building_Aerocraft {what}: {e}");
            }
        }
        Log.Message($"[MPCompat - Ancot Library] row 473: {synced} of 3 aerocraft take off / land / retarget lambdas synced");
    }

    private static string FlightStateOf(object aerocraft) => aerocraftFlightStateField.GetValue(aerocraft)?.ToString();

    // Runs before the lambda body. Only acts inside the synced command: a second take off for a craft already off the ground is dropped.
    private static bool PreSyncedTakeOff(object __instance)
        => !(MP.IsInMultiplayer && MP.IsExecutingSyncCommand) || FlightStateOf(__instance) == "Grounded";

    // The land button is only shown when flying / taking off: drop a land command once the craft is landing or grounded.
    private static bool PreSyncedLand(object __instance)
    {
        if (!(MP.IsInMultiplayer && MP.IsExecutingSyncCommand))
            return true;
        var state = FlightStateOf(__instance);
        return state != "Grounded" && state != "Landing";
    }

    private static bool ParamsAre(MethodInfo method, params Type[] types)
    {
        var ps = method.GetParameters();
        return ps.Length == types.Length && ps.Select(p => p.ParameterType).SequenceEqual(types);
    }

    // true when the method's IL stores the named field (stfld) or calls the named method (call / callvirt)
    private static bool UsesMember(MethodInfo method, string member)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il == null) return false;
        for (int i = 0; i < il.Length - 4; i++)
        {
            if (il[i] != 0x7D && il[i] != 0x28 && il[i] != 0x6F) continue; // stfld, call, callvirt
            try
            {
                if (method.Module.ResolveMember(BitConverter.ToInt32(il, i + 1))?.Name == member)
                    return true;
            }
            catch { }
        }
        return false;
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
