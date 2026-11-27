using System;
using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Dubs Mint Menus by Dubwise</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=1446523594"/>
    [MpCompatFor("Dubwise.DubsMintMenus")]
    internal class DubsMintMenu
    {
        public DubsMintMenu(ModContentPack mod)
        {
            LongEventHandler.ExecuteWhenFinished(() => Patch("DubsMintMenus.MP_Util"));

            // FIXES row 469 (26 Nov 2026): MainTabWindow_MintBillMenu.DoInterface is Dubs' own copy of Bill.DoInterface. Its
            // suspend button writes bill.suspended on the clicking PC only; Multiplayer syncs that field only inside the vanilla
            // Bill.DoInterface. The other writes in it are already synced vanilla calls (BillStack.Reorder, BillStack.Delete,
            // Bill.DoConfigInterface opens Dialog_BillConfig) or local (Clipboard). So watch suspended around Dubs' DoInterface.
            if (LabOff.Row(469))
                Log.Message("[MPCompat - Dubs Mint Menus] row 469 switched off by the lab, bill suspend button not synced");
            else
                PatchSuspend();
        }

        private static ISyncField suspendedField;

        internal static void PatchSuspend()
        {
            var type = AccessTools.TypeByName("DubsMintMenus.MainTabWindow_MintBillMenu");
            var method = type == null ? null : AccessTools.DeclaredMethod(type, "DoInterface");
            if (method == null || AccessTools.Field(typeof(RimWorld.Bill), "suspended") == null)
            {
                Log.Error("[MPCompat - Dubs Mint Menus] row 469: MainTabWindow_MintBillMenu.DoInterface or Bill.suspended not found, the suspend button can desync");
                return;
            }
            suspendedField = SharedSyncFields.Get(typeof(RimWorld.Bill), "suspended"); // shared: Nice Bill Tab watches it too (27 Nov)
            MpCompat.harmony.Patch(method,
                prefix: new HarmonyMethod(typeof(DubsMintMenu), nameof(PreDoInterface)),
                finalizer: new HarmonyMethod(typeof(DubsMintMenu), nameof(PostDoInterface)));
            Log.Message("[MPCompat - Dubs Mint Menus] row 469: bill suspend button synced");
        }

        // 27 Nov (row 469b, Neon review): WatchEnd only when this prefix ran WatchBegin. DoInterface runs with bill == null too;
        // an unmatched WatchEnd pops the next frame of Multiplayer's watch stack (SyncFieldUtil.FieldWatchPostfix), so an outer
        // watch would lose its later field changes.
        internal static void PreDoInterface(RimWorld.Bill bill, out bool __state)
        {
            __state = false;
            if (!MP.IsInMultiplayer || bill == null) return;
            MP.WatchBegin();
            __state = true;
            suspendedField.Watch(bill);
        }

        internal static void PostDoInterface(bool __state)
        {
            if (__state) MP.WatchEnd();
        }

        public static void Patch(string name)
        {
            var modType = AccessTools.TypeByName(name);

            var mpSyncMethod = AccessTools.TypeByName("Multiplayer.Client.SyncMethod");
            AccessTools.Field(modType, "RegisterRef").SetValue(null, AccessTools.Method(mpSyncMethod, "Register"));

            var mpSyncUtil = AccessTools.TypeByName("Multiplayer.Client.SyncFieldUtil");
            AccessTools.Field(modType, "FieldWatchPrefixRef").SetValue(null, AccessTools.Method(mpSyncUtil, "FieldWatchPrefix"));
            AccessTools.Field(modType, "FieldWatchPostfixRef").SetValue(null, AccessTools.Method(mpSyncUtil, "FieldWatchPostfix"));

            var mpSyncField = AccessTools.TypeByName("Multiplayer.Client.SyncField");
            AccessTools.Field(modType, "WatchRef").SetValue(null, AccessTools.Method(mpSyncField, "Watch"));
            AccessTools.Field(modType, "SetBufferChangesRef").SetValue(null, AccessTools.Method(mpSyncField, "SetBufferChanges"));

            var mpSync = AccessTools.TypeByName("Multiplayer.Client.Sync");
            AccessTools.Field(modType, "FieldRef").SetValue(null, AccessTools.Method(mpSync, "Field", new[] { typeof(Type), typeof(string) }));

            AccessTools.Method(modType, "asshai").Invoke(null, Array.Empty<object>());
        }
    }
}
