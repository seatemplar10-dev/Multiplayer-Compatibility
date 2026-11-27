using HarmonyLib;
using Multiplayer.API;
using System;
using System.Reflection;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>As Above So Below 2 by Astryl. Column gizmo toggles write the column's saved enabled list; viewBand stays client-only.</summary>
    [MpCompatFor("astryl.AsAboveSoBelow2")]
    internal class AsAboveSoBelow2L1
    {
        static Type colType, netsType, netType;
        static MethodInfo toggle, byId;
        static AccessTools.FieldRef<object, string> netId;

        public AsAboveSoBelow2L1(ModContentPack mod)
        {
            colType = AccessTools.TypeByName("AsAboveSoBelow.Building_ABColumn");
            netsType = AccessTools.TypeByName("AsAboveSoBelow.ABColumnNetworks");
            netType = AccessTools.TypeByName("AsAboveSoBelow.ABNetwork");
            toggle = AccessTools.Method(colType, "Toggle", new[] { netType });
            byId = AccessTools.Method(netsType, "ById");
            netId = AccessTools.FieldRefAccess<string>(netType, "id");
            if (toggle == null || byId == null) throw new Exception("AsAboveSoBelow2: Toggle or ById not found");

            // ABNetwork is not serialisable: sync (column Thing, network id string) and redirect the private Toggle(ABNetwork) to it.
            MP.RegisterSyncMethod(typeof(AsAboveSoBelow2L1), nameof(SyncedToggle));
            MpCompat.harmony.Patch(toggle, prefix: new HarmonyMethod(typeof(AsAboveSoBelow2L1), nameof(PreToggle)));
            Log.Message("[MPCompat - FULL1127 L1] AsAboveSoBelow2: Building_ABColumn.Toggle registered");
        }

        static bool PreToggle(Thing __instance, object n)
        {
            if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand) return true;
            SyncedToggle(__instance, netId(n));
            return false;
        }

        static void SyncedToggle(Thing column, string id)
        {
            var net = byId.Invoke(null, new object[] { id });
            if (column == null || net == null) return;
            toggle.Invoke(column, new[] { net });
        }
    }
}
