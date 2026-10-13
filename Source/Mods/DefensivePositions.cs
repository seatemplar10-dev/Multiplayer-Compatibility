using System;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat;

/// <summary>Defensive Positions by UnlimitedHugs</summary>
/// <see href="https://github.com/UnlimitedHugs/RimworldDefensivePositions"/>
/// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=761219125"/>
[MpCompatFor("UnlimitedHugs.DefensivePositions")]
public class DefensivePositions
{
    // The mod ships its own MP layer (DefensivePositions.Compat_MultiplayerAPI.Initialize, run from HugsLib's
    // StaticInitialize), but it starts with `if (MP.API != "0.1") throw ...` and MP.API is a const, so the compiler
    // baked in the API version the dll was built against: the 1.6 dll compares "0.5" with "0.1", the root dll "0.3".
    // The check always throws ("MP API version mismatch. This mod is designed to work with MPAPI version 0.1"),
    // nothing gets registered and every Defensive Positions click (save/clear position, advanced mode, squads)
    // runs on the clicking client only. This module registers exactly what the mod's own layer would have,
    // and only when the mod's check is the always-failing kind, so a fixed mod build is not registered twice.

    // DefensivePositionsManager
    private static FastInvokeHandler managerInstanceGetter;
    private static FastInvokeHandler managerGetHandlerForPawnMethod;
    private static AccessTools.FieldRef<object, object> managerSquadHandlerField;
    // PawnSavedPositions
    private static FastInvokeHandler pawnSavedPositionsOwnerGetter;

    public DefensivePositions(ModContentPack mod)
    {
        var compatType = AccessTools.TypeByName("DefensivePositions.Compat_MultiplayerAPI");
        if (compatType != null && !ModOwnLayerAlwaysFails(AccessTools.DeclaredMethod(compatType, "Initialize")))
        {
            Log.Message("MPCompat :: Defensive Positions registers its own Multiplayer support, skipping");
            return;
        }

        var managerType = AccessTools.TypeByName("DefensivePositions.DefensivePositionsManager");
        managerInstanceGetter = MethodInvoker.GetHandler(AccessTools.DeclaredPropertyGetter(managerType, "Instance"));
        managerGetHandlerForPawnMethod = MethodInvoker.GetHandler(AccessTools.DeclaredMethod(managerType, "GetHandlerForPawn"));
        managerSquadHandlerField = AccessTools.FieldRefAccess<object>(managerType, "squadHandler");

        pawnSavedPositionsOwnerGetter = MethodInvoker.GetHandler(
            AccessTools.PropertyGetter(AccessTools.TypeByName("DefensivePositions.PawnSavedPositions"), "Owner"));

        var handlerType = AccessTools.TypeByName("DefensivePositions.PawnSavedPositionHandler");
        var squadHandlerType = AccessTools.TypeByName("DefensivePositions.PawnSquadHandler");

        // Same registrations as Compat_MultiplayerAPI.Initialize
        MP.RegisterSyncMethod(managerType, "ToggleAdvancedMode");
        MP.RegisterSyncMethod(handlerType, "SetDefensivePosition");
        MP.RegisterSyncMethod(handlerType, "DiscardSavedPosition");
        MP.RegisterSyncMethod(squadHandlerType, "ReassignSquadMembers");
        MP.RegisterSyncMethod(squadHandlerType, "ClearSquad");
        MP.RegisterSyncWorker<object>(SyncManager, managerType);
        MP.RegisterSyncWorker<object>(SyncPawnHandler, handlerType);
        MP.RegisterSyncWorker<object>(SyncSquadHandler, squadHandlerType);
    }

    // True if the method compares two different string constants with string.Equals - the const-folded
    // `MP.API.Equals("0.1")` check that throws on every MP API other than 0.1.
    private static bool ModOwnLayerAlwaysFails(MethodBase initialize)
    {
        if (initialize == null)
            return false;

        try
        {
            var equals = AccessTools.DeclaredMethod(typeof(string), nameof(string.Equals), [typeof(string)]);
            var instr = PatchProcessor.GetOriginalInstructions(initialize);
            for (var i = 2; i < instr.Count; i++)
            {
                if (instr[i].Calls(equals) &&
                    instr[i - 2].opcode == OpCodes.Ldstr && instr[i - 1].opcode == OpCodes.Ldstr)
                    return !Equals(instr[i - 2].operand, instr[i - 1].operand);
            }
        }
        catch (Exception e)
        {
            Log.Warning($"MPCompat :: Could not read Defensive Positions' own Multiplayer layer, not registering: {e}");
        }

        return false;
    }

    private static object Manager => managerInstanceGetter(null);

    private static void SyncManager(SyncWorker sync, ref object manager)
    {
        if (!sync.isWriting)
            manager = Manager;
    }

    private static void SyncPawnHandler(SyncWorker sync, ref object handler)
    {
        if (sync.isWriting)
        {
            sync.Write(handler == null ? null : pawnSavedPositionsOwnerGetter(handler) as Pawn);
        }
        else
        {
            var pawn = sync.Read<Pawn>();
            if (pawn != null)
                handler = managerGetHandlerForPawnMethod(Manager, pawn);
        }
    }

    private static void SyncSquadHandler(SyncWorker sync, ref object squadHandler)
    {
        if (!sync.isWriting)
            squadHandler = managerSquadHandlerField(Manager);
    }
}
