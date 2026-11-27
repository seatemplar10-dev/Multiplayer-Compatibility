using HarmonyLib;
using Multiplayer.API;
using System;
using System.Reflection;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Get Out Of My Chair by Aelanna</summary>
    [MpCompatFor("Aelanna.GetOutOfMyChair")]
    internal class GetOutOfMyChairL1
    {
        static Type utilType, ownerCmdType, modeCmdType, tableCmdType;
        static AccessTools.FieldRef<object, Building> ownerChair;
        static MethodInfo clearOwner, clearMode, clearTable;

        public GetOutOfMyChairL1(ModContentPack mod)
        {
            utilType = AccessTools.TypeByName("GetOutOfMyChair.ChairUtility");
            ownerCmdType = AccessTools.TypeByName("GetOutOfMyChair.Command_ChairOwner");
            modeCmdType = AccessTools.TypeByName("GetOutOfMyChair.Command_ChairUseMode");
            tableCmdType = AccessTools.TypeByName("GetOutOfMyChair.Command_TableUseMode");

            // Both write the static ChairUseModes dictionary from UI code: sync the extension methods (Building + enum, both serialisable).
            MP.RegisterSyncMethod(AccessTools.Method(utilType, "SetUseMode"));
            MP.RegisterSyncMethod(AccessTools.Method(utilType, "CycleUseMode"));
            MpCompat.harmony.Patch(AccessTools.Method(utilType, "SetUseMode"), postfix: new HarmonyMethod(typeof(GetOutOfMyChairL1), nameof(ClearCaches)));
            MpCompat.harmony.Patch(AccessTools.Method(utilType, "CycleUseMode"), postfix: new HarmonyMethod(typeof(GetOutOfMyChairL1), nameof(ClearCaches)));
            clearOwner = AccessTools.Method(ownerCmdType, "ClearCachedCommand");
            clearMode = AccessTools.Method(modeCmdType, "ClearCachedCommand");
            clearTable = AccessTools.Method(tableCmdType, "ClearCachedCommand");

            // SetOwner is a private instance method on a Command: redirect it to a synced static wrapper (chair + pawn).
            ownerChair = AccessTools.FieldRefAccess<Building>(ownerCmdType, "chair");
            MP.RegisterSyncMethod(typeof(GetOutOfMyChairL1), nameof(SyncedSetOwner));
            MpCompat.harmony.Patch(AccessTools.Method(ownerCmdType, "SetOwner"), prefix: new HarmonyMethod(typeof(GetOutOfMyChairL1), nameof(PreSetOwner)));

            Log.Message("[MPCompat - FULL1127 L1] GetOutOfMyChair: SetUseMode, CycleUseMode, SetOwner registered");
        }

        static void ClearCaches()
        {
            clearOwner?.Invoke(null, null);
            clearMode?.Invoke(null, null);
            clearTable?.Invoke(null, null);
        }

        static bool PreSetOwner(object __instance, Pawn pawn)
        {
            if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand) return true;
            SyncedSetOwner(ownerChair(__instance), pawn);
            return false;
        }

        static void SyncedSetOwner(Building chair, Pawn pawn)
        {
            if (chair == null) return;
            var owners = (System.Collections.Generic.Dictionary<int, int>)AccessTools.Field(utilType, "ChairOwners").GetValue(null);
            if (pawn == null) owners.Remove(chair.thingIDNumber);
            else owners[chair.thingIDNumber] = pawn.thingIDNumber;
            ClearCaches();
        }
    }
}
