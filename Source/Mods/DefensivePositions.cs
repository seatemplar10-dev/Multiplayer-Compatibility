using System;
using System.Collections;
using System.Collections.Generic;
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
    // runs on the clicking client only. This module registers the per-pawn part of what the mod's own layer would
    // have, and only when the mod's check is the always-failing kind, so a fixed mod build is not registered twice.
    //
    // Deliberately NOT synced (unlike the mod's own layer): squads (PawnSquadHandler.ReassignSquadMembers/ClearSquad)
    // and the advanced-mode toggle (DefensivePositionsManager.ToggleAdvancedMode). Both live in one global
    // WorldComponent (WorldData.pawnSquads, advancedModeEnabled), not per faction, and squad membership only checks
    // Faction.IsPlayer, which is true for every player faction in MP multifaction. Synced, player B assigning
    // squad 1 would replace player A's squad 1, and one player's alt-click would flip everyone's gizmo mode.
    // Left local, each player keeps their own squads and mode. Nothing in the simulation reads them: activating
    // a squad only selects things and moves the camera (PawnSquadSelector), and the mode only picks which gizmo
    // is drawn, while sending pawns to positions goes through Pawn_JobTracker.TryTakeOrderedJob (synced by MP).
    // Cost: a client's own squads/mode are not in the host's save, so on (re)join the client gets the host's instead.

    // DefensivePositionsManager
    private static FastInvokeHandler managerInstanceGetter;
    private static FastInvokeHandler managerGetHandlerForPawnMethod;
    // PawnSavedPositions
    private static FastInvokeHandler pawnSavedPositionsOwnerGetter;
    private static FastInvokeHandler pawnSavedPositionsShouldBeSavedGetter;
    // WorldData
    private static MethodInfo worldDataHandlerListFromDictionaryMethod;

    public DefensivePositions(ModContentPack mod)
    {
        var savedPositionsType = AccessTools.TypeByName("DefensivePositions.PawnSavedPositions");
        pawnSavedPositionsOwnerGetter = MethodInvoker.GetHandler(AccessTools.PropertyGetter(savedPositionsType, "Owner"));

        // FIXES row 219: in multifaction MP saves every join point (autosave, a player joining) as the first player
        // faction (SaveLoad.SaveAndReload sets Faction.OfPlayer before SaveGameData). DP keeps a pawn's saved
        // positions only if `owner.Faction == Faction.OfPlayer` (PawnSavedPositions.ShouldBeSaved, filtered in
        // WorldData.HandlerListFromDictionary from WorldData.ExposeData), so every other player faction lost its saved
        // positions at each join point, on every PC alike. In MP, keep them for any player faction. Done whoever
        // registers the sync methods below. A transpiler on ExposeData, not a patch on the tiny ShouldBeSaved getter or
        // HandlerListFromDictionary: on 1.7 (CoreCLR) a tier-1 compiled caller can inline a small method and skip its patch.
        try
        {
            var worldDataType = AccessTools.TypeByName("DefensivePositions.WorldData");
            worldDataHandlerListFromDictionaryMethod = AccessTools.DeclaredMethod(worldDataType, "HandlerListFromDictionary");
            pawnSavedPositionsShouldBeSavedGetter = MethodInvoker.GetHandler(AccessTools.DeclaredPropertyGetter(savedPositionsType, "ShouldBeSaved"));
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(worldDataType, "ExposeData"),
                transpiler: new HarmonyMethod(typeof(DefensivePositions), nameof(ReplaceHandlerListFromDictionary)));
        }
        catch (Exception e)
        {
            Log.Error($"MPCompat :: Defensive Positions save fix (other player factions' positions) failed, not applied: {e}");
        }

        var compatType = AccessTools.TypeByName("DefensivePositions.Compat_MultiplayerAPI");
        if (compatType != null && !ModOwnLayerAlwaysFails(AccessTools.DeclaredMethod(compatType, "Initialize")))
        {
            Log.Message("MPCompat :: Defensive Positions registers its own Multiplayer support, skipping");
            return;
        }

        // FIXES row 219: the mod's own layer still runs later (HugsLib StaticInitialize), throws on its version check before
        // it registers anything and logs "Failed to apply Multiplayer API compatibility layer: ... MP API version mismatch"
        // as an ERROR at every start. This module registers the layer's per-pawn syncs in its place, so skip it.
        if (compatType != null)
        {
            try
            {
                MpCompat.harmony.Patch(AccessTools.DeclaredMethod(compatType, "Initialize"),
                    prefix: new HarmonyMethod(typeof(DefensivePositions), nameof(SkipModOwnLayer)));
            }
            catch (Exception e)
            {
                Log.Warning($"MPCompat :: Could not skip Defensive Positions' own Multiplayer layer, its version mismatch error stays: {e}");
            }
        }

        var managerType = AccessTools.TypeByName("DefensivePositions.DefensivePositionsManager");
        managerInstanceGetter = MethodInvoker.GetHandler(AccessTools.DeclaredPropertyGetter(managerType, "Instance"));
        managerGetHandlerForPawnMethod = MethodInvoker.GetHandler(AccessTools.DeclaredMethod(managerType, "GetHandlerForPawn"));

        var handlerType = AccessTools.TypeByName("DefensivePositions.PawnSavedPositionHandler");

        // The per-pawn registrations of Compat_MultiplayerAPI.Initialize (squads and advanced mode stay local, see top)
        MP.RegisterSyncMethod(handlerType, "SetDefensivePosition");
        MP.RegisterSyncMethod(handlerType, "DiscardSavedPosition");
        MP.RegisterSyncWorker<object>(SyncPawnHandler, handlerType);
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

    private static bool loggedSkip;

    // Prefix on Compat_MultiplayerAPI.Initialize: never run the mod's always-failing layer (FIXES row 219)
    private static bool SkipModOwnLayer()
    {
        if (!loggedSkip)
        {
            loggedSkip = true;
            Log.Message("MPCompat :: Defensive Positions' own Multiplayer layer skipped (its MP API check always fails, this module registers its per-pawn syncs instead; FIXES row 219)");
        }

        return false;
    }

    private static IEnumerable<CodeInstruction> ReplaceHandlerListFromDictionary(IEnumerable<CodeInstruction> instr, MethodBase baseMethod)
    {
        var replacement = AccessTools.DeclaredMethod(typeof(DefensivePositions), nameof(HandlerListForSave));
        var replacedCount = 0;

        foreach (var ci in instr)
        {
            if (worldDataHandlerListFromDictionaryMethod != null && ci.Calls(worldDataHandlerListFromDictionaryMethod))
            {
                ci.opcode = OpCodes.Call;
                ci.operand = replacement;
                yield return ci;
                // Back to the List<PawnSavedPositionHandler> that the stfld after the call expects
                yield return new CodeInstruction(OpCodes.Castclass, worldDataHandlerListFromDictionaryMethod.ReturnType);

                replacedCount++;
                continue;
            }

            yield return ci;
        }

        const int expected = 1;
        if (replacedCount != expected)
            Log.Warning($"Patched incorrect number of WorldData.HandlerListFromDictionary calls (patched {replacedCount}, expected {expected}) for method {baseMethod.Name}");
    }

    // DP's HandlerListFromDictionary (`dict.Values.Where(v => v.ShouldBeSaved).ToList()`). Single player: unchanged,
    // same test and same order. In MP: DP's test (owner set, not destroyed) with any player faction instead of only
    // Faction.OfPlayer (MP's hidden Spectator faction is a player faction too, but it has no pawns), sorted by owner id.
    // A gizmo adds an empty handler on the drawing PC only, so the dictionary order can differ per PC. The sort puts
    // the handlers with positions (set by synced commands, so on every PC) in the same order on every PC.
    private static object HandlerListForSave(object dict)
    {
        var list = (IList)Activator.CreateInstance(worldDataHandlerListFromDictionaryMethod.ReturnType);
        if (dict is not IDictionary handlers)
            return list;

        if (!MP.IsInMultiplayer)
        {
            foreach (var handler in handlers.Values)
            {
                if ((bool)pawnSavedPositionsShouldBeSavedGetter(handler))
                    list.Add(handler);
            }

            return list;
        }

        var kept = new List<(int id, object handler)>();
        foreach (var handler in handlers.Values)
        {
            if (pawnSavedPositionsOwnerGetter(handler) is Pawn { Destroyed: false, Faction.IsPlayer: true } owner)
                kept.Add((owner.thingIDNumber, handler));
        }

        kept.Sort((a, b) => a.id.CompareTo(b.id));
        foreach (var (_, handler) in kept)
            list.Add(handler);

        return list;
    }

    private static object Manager => managerInstanceGetter(null);

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
}
