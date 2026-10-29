using System.Collections.Generic;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat;

/// <summary>Nice Bill Tab by Andromeda: the bill list follows the bill stack in MP</summary>
/// <remarks>
/// FIXES rows 268 + 275 (29 Oct 2026, live co-op, package D: "the copy paste of bills" still does not work after the row 275 sync).
/// Nice Bill Tab draws its bill list from a static cache, <c>TabBillsDrawer.filteredBills</c>. <c>DrawLeftPart</c> rebuilds it from
/// <c>SelTable.billStack.Bills</c> only when <c>filteredBills == null || shouldRefreshFilter</c>, then sets <c>shouldRefreshFilter = false</c>.
/// Every paste / add / delete button sets <c>shouldRefreshFilter = true</c> in the same click. Without MP the stack changed in that click,
/// so the next draw showed it. In MP the change (BillStack.AddBill / Delete, the row 275 BWM and InsertBill commands) runs later inside
/// a synced command, so the draw right after the click rebuilt the cache from the OLD stack and cleared the flag. The new bill exists on
/// every PC (it is in the save) but the list never shows it until the bench is selected again; on the other PC nothing refreshes at all.
/// Fix: before each DrawLeftPart in MP, compare the selected stack with what the cache was built from (stack, count, bill ids in order);
/// when it changed, set shouldRefreshFilter so Nice Bill Tab rebuilds its own cache. UI only: no game state, no Rand, no sync.
/// Kept in its own class so it does not touch NiceBillTab.cs (row 277 edits that file).
/// </remarks>
[MpCompatFor("Andromeda.NiceBillTab")]
public class NiceBillTabListRefresh
{
    private static AccessTools.FieldRef<bool> shouldRefreshFilter;
    private static BillStack lastStack;
    private static int lastSignature;

    public NiceBillTabListRefresh(ModContentPack mod)
    {
        var drawLeftPart = AccessTools.DeclaredMethod("NiceBillTab.TabBillsDrawer:DrawLeftPart");
        var refreshField = AccessTools.DeclaredField("NiceBillTab.TabBillsDrawer:shouldRefreshFilter");
        if (drawLeftPart == null || refreshField == null || refreshField.FieldType != typeof(bool) || !refreshField.IsStatic)
        {
            Log.Error("[MPCompat] Nice Bill Tab rows 268/275: TabBillsDrawer.DrawLeftPart or shouldRefreshFilter changed - the bill list will not show synced pastes until the bench is selected again");
            return;
        }

        shouldRefreshFilter = AccessTools.StaticFieldRefAccess<bool>(refreshField);
        MpCompat.harmony.Patch(drawLeftPart, prefix: new HarmonyMethod(typeof(NiceBillTabListRefresh), nameof(PreDrawLeftPart)));
    }

    private static void PreDrawLeftPart(Building_WorkTable SelTable)
    {
        if (!MP.IsInMultiplayer)
            return;

        var stack = SelTable?.billStack;
        if (stack == null)
        {
            lastStack = null;
            return;
        }

        var signature = Signature(stack.Bills);
        if (stack == lastStack && signature != lastSignature)
            shouldRefreshFilter() = true;

        lastStack = stack;
        lastSignature = signature;
    }

    private static int Signature(List<Bill> bills)
    {
        unchecked
        {
            var hash = 17 * 31 + bills.Count;
            foreach (var bill in bills)
                hash = hash * 31 + (bill == null ? 0 : bill.loadID);
            return hash;
        }
    }
}
