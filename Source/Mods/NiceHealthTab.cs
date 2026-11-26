using System;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Nice Health Tab by Andromeda (FIXES row 458)</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3328729902"/>
    /// Its 1.6 dll synced its own health tab buttons (NHTSync Broadcast* methods with [SyncMethod], MP.RegisterAll). The 1.7 dll has
    /// no Multiplayer reference: NHTSync.SetSelfTend, SetFoodPolicy, SetBillSuspended, ReorderMedicalBills and SetAutoExtract write
    /// on the clicking PC only, and OverviewTabWidget.DrawMedicineRow passes playerSettings.medCare by ref to
    /// MedicalCareUtility.MedicalCareSetter. Its prefix on HealthCardUtility.DrawPawnHealthCard skips the vanilla health card, so
    /// Multiplayer's own DrawOverviewTab watch on selfTend and medCare never runs. Live co-op 26 Nov 21:47 and 21:49: James ticked
    /// self-tend in this tab, his PC let the pawn self-tend, the host's JobDriver_TendPatient failed the job (selfTend false there).
    /// Fix: the 5 NHTSync methods become sync methods; a medicine row change is undone locally and sent as one synced command.
    [MpCompatFor("andromeda.nicehealthtab")]
    public class NiceHealthTab
    {
        internal static readonly string[] SyncedNames =
            { "SetSelfTend", "SetFoodPolicy", "SetBillSuspended", "ReorderMedicalBills", "SetAutoExtract" };

        private static AccessTools.FieldRef<object, Pawn> widgetPawn;
        internal static int registered;
        internal static bool medicineRowPatched;

        public NiceHealthTab(ModContentPack mod)
        {
            if (LabOff.Row(458))
            {
                Log.Message("[MPCompat - Nice Health Tab] row 458 switched off by the lab, nothing registered");
                return;
            }

            var syncType = AccessTools.TypeByName("NiceHealthTab.NHTSync");
            if (syncType == null)
            {
                Log.Error("[MPCompat - Nice Health Tab] row 458: type NiceHealthTab.NHTSync not found, its health tab buttons stay unsynced");
                return;
            }
            // The 1.6 build syncs itself (private Broadcast* sync methods). Registering its public setters too would send twice.
            if (AccessTools.Method(syncType, "BroadcastSetSelfTend") != null)
            {
                Log.Message("[MPCompat - Nice Health Tab] row 458: this build has its own Multiplayer sync, module skipped");
                return;
            }

            foreach (var name in SyncedNames)
            {
                var method = AccessTools.Method(syncType, name);
                if (method == null)
                {
                    Log.Error($"[MPCompat - Nice Health Tab] row 458: NHTSync.{name} not found, that button stays unsynced");
                    continue;
                }
                MP.RegisterSyncMethod(method);
                registered++;
            }

            var widgetType = AccessTools.TypeByName("NiceHealthTab.OverviewTabWidget");
            var drawMedicineRow = widgetType == null ? null : AccessTools.Method(widgetType, "DrawMedicineRow");
            var pawnField = widgetType == null ? null : AccessTools.Field(widgetType, "Pawn");
            if (drawMedicineRow == null || pawnField == null || pawnField.FieldType != typeof(Pawn))
            {
                Log.Error("[MPCompat - Nice Health Tab] row 458: OverviewTabWidget.DrawMedicineRow or its Pawn field not found, the medicine row stays unsynced");
            }
            else
            {
                widgetPawn = AccessTools.FieldRefAccess<object, Pawn>(pawnField);
                MP.RegisterSyncMethod(typeof(NiceHealthTab), nameof(SyncedSetMedCare));
                MpCompat.harmony.Patch(drawMedicineRow,
                    prefix: new HarmonyMethod(typeof(NiceHealthTab), nameof(PreDrawMedicineRow)),
                    postfix: new HarmonyMethod(typeof(NiceHealthTab), nameof(PostDrawMedicineRow)));
                medicineRowPatched = true;
            }

            Log.Message($"[MPCompat - Nice Health Tab] row 458: {registered} of {SyncedNames.Length} NHTSync methods synced, medicine row synced={medicineRowPatched}");
        }

        // -1 = nothing to compare (not in Multiplayer, or no pawn settings).
        internal static void PreDrawMedicineRow(object __instance, out int __state)
        {
            __state = -1;
            if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand)
                return;
            var pawn = widgetPawn(__instance);
            if (pawn?.playerSettings == null)
                return;
            __state = (int)pawn.playerSettings.medCare;
        }

        internal static void PostDrawMedicineRow(object __instance, int __state)
        {
            if (__state < 0)
                return;
            var pawn = widgetPawn(__instance);
            if (pawn?.playerSettings == null)
                return;
            var chosen = pawn.playerSettings.medCare;
            if ((int)chosen == __state)
                return;
            // Undo the local write, then send the choice: every PC sets it in the same tick.
            pawn.playerSettings.medCare = (MedicalCareCategory)__state;
            SyncedSetMedCare(pawn, chosen);
        }

        internal static void SyncedSetMedCare(Pawn pawn, MedicalCareCategory care)
        {
            if (pawn?.playerSettings != null)
                pawn.playerSettings.medCare = care;
        }
    }
}
