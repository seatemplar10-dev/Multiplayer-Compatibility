using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat;

/// <summary>Better Workbench Management by falconne</summary>
/// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=935982361"/>
/// <remarks>
/// FIXES row 275 (28 Oct 2026, live co-op): BWM's bill paste ran on the clicking PC only.
/// <c>BillCopyPaste.DoPasteInto(Building_WorkTable workTable, bool link)</c> makes a new bill per copied bill, calls
/// <c>workTable.BillStack.AddBill(val)</c> (Multiplayer syncs that call with the bill exposed AS IT IS at that moment, i.e. before BWM
/// changed it) and then <c>MirrorBills(copiedBill, val, ...)</c> / <c>LinkBills(copiedBill, val)</c> on the local copy only. So every PC
/// got a plain bill, and the clicking PC's ExtendedBillDataStorage / linked-bill sets (a saved WorldComponent) got entries for a bill
/// no stack holds. <c>DoPasteInto(Bill_Production targetBill)</c> ran <c>MirrorBills(copied, targetBill, true)</c> on one PC only.
/// Nice Bill Tab calls both through reflection (<c>BetterWorkbenchesIntegration.DoPasteInto</c> -> MethodInfo.Invoke), which runs these
/// patches too.
/// Fix: in interface code both overloads send a synced command carrying the target and the copied bills BY REFERENCE (BWM's clipboard
/// holds the live bills, not clones; the Bill sync worker writes the bill's stack owner + loadID). The command puts those bills into
/// BWM's clipboard for the call, runs BWM's own DoPasteInto on every PC, and puts each PC's own clipboard back. A copied bill that
/// is deleted before the command runs reads as null and is skipped (BWM skips deleted bills itself).
/// Not covered: BWM's own settings (max bills per table, mirror suspended status) are read inside the command; PCs with different BWM
/// settings can still get different results.
/// </remarks>
[MpCompatFor("falconne.BWM")]
public class BetterWorkbenchManagement
{
    private static AccessTools.FieldRef<object, List<Bill_Production>> copiedBillsField;
    private static Func<object> mainInstanceGetter;
    private static FieldInfo copyPasteHandlerField;
    private static MethodInfo pasteIntoTableMethod, pasteIntoBillMethod;

    public BetterWorkbenchManagement(ModContentPack mod)
    {
        var copyPaste = AccessTools.TypeByName("ImprovedWorkbenches.BillCopyPaste");
        var main = AccessTools.TypeByName("ImprovedWorkbenches.Main");
        pasteIntoTableMethod = copyPaste == null ? null : AccessTools.DeclaredMethod(copyPaste, "DoPasteInto", [typeof(Building_WorkTable), typeof(bool)]);
        pasteIntoBillMethod = copyPaste == null ? null : AccessTools.DeclaredMethod(copyPaste, "DoPasteInto", [typeof(Bill_Production)]);
        var copied = copyPaste == null ? null : AccessTools.DeclaredField(copyPaste, "_copiedBills");
        var instance = main == null ? null : AccessTools.PropertyGetter(main, "Instance");
        copyPasteHandlerField = main == null ? null : AccessTools.DeclaredField(main, "BillCopyPasteHandler");

        if (pasteIntoTableMethod == null || pasteIntoBillMethod == null || copied?.FieldType != typeof(List<Bill_Production>) || instance == null || copyPasteHandlerField == null)
        {
            Log.Error("[MPCompat] Better Workbench Management row 275: BillCopyPaste / Main changed (a member is missing), bill pastes stay unsynced");
            return;
        }

        copiedBillsField = AccessTools.FieldRefAccess<List<Bill_Production>>(copyPaste, "_copiedBills");
        mainInstanceGetter = () => instance.Invoke(null, null);

        MpCompat.harmony.Patch(pasteIntoTableMethod, prefix: new HarmonyMethod(typeof(BetterWorkbenchManagement), nameof(PrePasteIntoTable)));
        MpCompat.harmony.Patch(pasteIntoBillMethod, prefix: new HarmonyMethod(typeof(BetterWorkbenchManagement), nameof(PrePasteIntoBill)));
        MP.RegisterSyncMethod(typeof(BetterWorkbenchManagement), nameof(SyncedPasteIntoTable));
        MP.RegisterSyncMethod(typeof(BetterWorkbenchManagement), nameof(SyncedPasteIntoBill));
    }

    // The live copied bills that can travel by reference (still in a stack).
    private static List<Bill> SyncableCopiedBills(object handler) =>
        copiedBillsField(handler)
            .Where(b => b != null && b.billStack != null && !b.DeletedOrDereferenced)
            .Cast<Bill>()
            .ToList();

    private static bool PrePasteIntoTable(object __instance, Building_WorkTable workTable, bool link)
    {
        if (!MP.IsInMultiplayer || !MP.InInterface)
            return true;

        var sources = SyncableCopiedBills(__instance);
        if (workTable != null && sources.Count > 0)
            SyncedPasteIntoTable(workTable, sources, link);
        return false;
    }

    private static bool PrePasteIntoBill(object __instance, Bill_Production targetBill)
    {
        if (!MP.IsInMultiplayer || !MP.InInterface)
            return true;

        var source = SyncableCopiedBills(__instance).FirstOrDefault();
        if (targetBill != null && source != null && source != targetBill)
            SyncedPasteIntoBill(targetBill, source);
        return false;
    }

    // Synced: runs on every PC inside the command.
    private static void SyncedPasteIntoTable(Building_WorkTable workTable, List<Bill> sources, bool link)
    {
        if (workTable == null || sources == null)
            return;
        RunWithClipboard(sources, handler => pasteIntoTableMethod.Invoke(handler, [workTable, link]));
    }

    // Synced: runs on every PC inside the command.
    private static void SyncedPasteIntoBill(Bill targetBill, Bill source)
    {
        if (targetBill is not Bill_Production target || source is not Bill_Production)
            return;
        RunWithClipboard([source], handler => pasteIntoBillMethod.Invoke(handler, [target]));
    }

    private static void RunWithClipboard(List<Bill> sources, Action<object> paste)
    {
        var handler = copyPasteHandlerField.GetValue(mainInstanceGetter());
        if (handler == null)
            return;

        var clipboard = copiedBillsField(handler);
        var own = clipboard.ToList();
        clipboard.Clear();
        clipboard.AddRange(sources.OfType<Bill_Production>());
        try
        {
            if (clipboard.Count > 0)
                paste(handler);
        }
        finally
        {
            clipboard.Clear();
            clipboard.AddRange(own);
        }
    }
}
