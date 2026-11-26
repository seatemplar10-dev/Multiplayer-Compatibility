using System;
using System.Collections;
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
/// FIXES row 468 (26 Nov 2026): two more one-PC writes. (1) <c>ITab_Bills_TabUpdate_Detour</c> (every frame, mouse over a bill) and
/// <c>BillConfig_DoWindowContents_Patch</c> (every frame, bill dialog open) call <c>ExtendedBillDataStorage.MirrorBillToLinkedBills(bill)</c>
/// on that PC only, so a synced edit of bill A reached A's linked bills only on the PC that hovered A. (2) The
/// <c>BillConfig_PreClose_Detour</c> postfix writes the table's WorktableRestrictionData and every bill's pawn restriction + skill
/// range on the closing PC only.
/// Fix (MP, interface code only): a prefix on MirrorBillToLinkedBills skips the local mirror, compares the bill with its live linked
/// bills (every field MirrorBills writes, under MirrorBills' own conditions; nothing is written) and only when one differs sends ONE
/// synced <c>SyncedMirrorToLinks(bill)</c> that runs BWM's own mirror on every PC. A per-bill latch (hash of the source and linked
/// bills' state) stops a resend every frame while the command is in flight and caps a difference MirrorBills itself leaves at two
/// commands. A prefix on BWM's PreClose postfix does BWM's own "restricted and differs" test without writing and sends one
/// <c>SyncedApplyTableRestriction(table, bill)</c> that does BWM's write on every PC. Single player and synced/tick calls: unchanged.
/// Not covered (row 468): the ColonyGroups pawn group copy inside MirrorBills is not compared (it is still copied when another field
/// differs); BillConfig_DoWindowContents_Patch's own GetOrCreateExtendedDataFor (an empty entry) stays local.
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
        PatchLinkedBills();

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
        // lab switch row 275 (package G, 17 Nov): off = old behaviour
        if (LabOff.Row(275) || !MP.IsInMultiplayer || !MP.InInterface)
            return true;

        var sources = SyncableCopiedBills(__instance);
        if (workTable != null && sources.Count > 0)
            SyncedPasteIntoTable(workTable, sources, link);
        return false;
    }

    private static bool PrePasteIntoBill(object __instance, Bill_Production targetBill)
    {
        // lab switch row 275 (package G, 17 Nov): off = old behaviour
        if (LabOff.Row(275) || !MP.IsInMultiplayer || !MP.InInterface)
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

    // ---------------- FIXES row 468: linked bill mirror + table restriction on dialog close ----------------

    private static MethodInfo mirrorToLinksMethod, getBillSetMethod, canOutputBeFilteredMethod, mainInstance468, getStorageMethod, shouldMirrorSuspendedMethod;
    private static MethodInfo getRestrictionByIdMethod, preCloseDetourMethod;
    private static PropertyInfo setBillsProperty;
    private static FieldInfo storeField, extCountAway, extName, extFilter;
    private static FieldInfo restrictionDictField, rdRestricted, rdPawn, rdSlaves, rdMechs, rdNonMechs, rdSkills, dialogBillField;
    private static Type restrictionStorageType;
    private static AccessTools.FieldRef<ThingFilter, HashSet<ThingDef>> filterAllowedDefs;
    private static AccessTools.FieldRef<ThingFilter, List<SpecialThingFilterDef>> filterDisallowedSpecial;
    private static ThingFilter emptyFilter;
    private static readonly Dictionary<Bill_Production, int> lastSentMirror = new();
    private static bool inCommand;

    private static void PatchLinkedBills()
    {
        if (LabOff.Row(468))
        {
            Log.Message("[MPCompat - Better Workbench Management] row 468 switched off by the lab, linked bill mirroring and table restrictions stay local");
            return;
        }

        var storageType = AccessTools.TypeByName("ImprovedWorkbenches.ExtendedBillDataStorage");
        var setType = AccessTools.TypeByName("ImprovedWorkbenches.LinkedBillsSet");
        var extType = AccessTools.TypeByName("ImprovedWorkbenches.ExtendedBillData");
        var main = AccessTools.TypeByName("ImprovedWorkbenches.Main");
        var rdType = AccessTools.TypeByName("ImprovedWorkbenches.WorktableRestrictionData");
        var preCloseType = AccessTools.TypeByName("ImprovedWorkbenches.BillConfig_PreClose_Detour");
        restrictionStorageType = AccessTools.TypeByName("ImprovedWorkbenches.WorktableRestrictionDataStorage");

        mirrorToLinksMethod = storageType == null ? null : AccessTools.DeclaredMethod(storageType, "MirrorBillToLinkedBills", [typeof(Bill_Production)]);
        getBillSetMethod = storageType == null ? null : AccessTools.DeclaredMethod(storageType, "GetBillSetContaining", [typeof(Bill_Production)]);
        canOutputBeFilteredMethod = storageType == null ? null : AccessTools.DeclaredMethod(storageType, "CanOutputBeFiltered", [typeof(Bill_Production)]);
        storeField = storageType == null ? null : AccessTools.DeclaredField(storageType, "_store");
        setBillsProperty = setType == null ? null : AccessTools.DeclaredProperty(setType, "Bills");
        extCountAway = extType == null ? null : AccessTools.DeclaredField(extType, "CountAway");
        extName = extType == null ? null : AccessTools.DeclaredField(extType, "Name");
        extFilter = extType == null ? null : AccessTools.DeclaredField(extType, "ProductAdditionalFilter");
        mainInstance468 = main == null ? null : AccessTools.PropertyGetter(main, "Instance");
        getStorageMethod = main == null ? null : AccessTools.DeclaredMethod(main, "GetExtendedBillDataStorage");
        shouldMirrorSuspendedMethod = main == null ? null : AccessTools.DeclaredMethod(main, "ShouldMirrorSuspendedStatus");
        restrictionDictField = restrictionStorageType == null ? null : AccessTools.DeclaredField(restrictionStorageType, "_worktableRestrictionDictonary");
        getRestrictionByIdMethod = restrictionStorageType == null ? null : AccessTools.DeclaredMethod(restrictionStorageType, "GetWorktableRestrictionData", [typeof(int)]);
        rdRestricted = rdType == null ? null : AccessTools.DeclaredField(rdType, "isRestricted");
        rdPawn = rdType == null ? null : AccessTools.DeclaredField(rdType, "restrictionPawn");
        rdSlaves = rdType == null ? null : AccessTools.DeclaredField(rdType, "restrictionSlavesOnly");
        rdMechs = rdType == null ? null : AccessTools.DeclaredField(rdType, "restrictionMechsOnly");
        rdNonMechs = rdType == null ? null : AccessTools.DeclaredField(rdType, "restrictionNonMechsOnly");
        rdSkills = rdType == null ? null : AccessTools.DeclaredField(rdType, "restrictionAllowedSkillRange");
        preCloseDetourMethod = preCloseType == null ? null : AccessTools.DeclaredMethod(preCloseType, "Postfix", [typeof(Window)]);
        dialogBillField = AccessTools.DeclaredField(typeof(Dialog_BillConfig), "bill");
        var allowedDefs = AccessTools.DeclaredField(typeof(ThingFilter), "allowedDefs");
        var disallowedSpecial = AccessTools.DeclaredField(typeof(ThingFilter), "disallowedSpecialFilters");

        bool restrictionOk = restrictionDictField != null && rdRestricted != null && rdPawn != null && rdSlaves != null && rdMechs != null
                             && rdNonMechs != null && rdSkills?.FieldType == typeof(IntRange);
        bool mirrorOk = restrictionOk && mirrorToLinksMethod != null && getBillSetMethod != null && canOutputBeFilteredMethod != null && storeField != null
                        && setBillsProperty != null && extCountAway != null && extName != null && extFilter?.FieldType == typeof(ThingFilter) && mainInstance468 != null
                        && getStorageMethod != null && shouldMirrorSuspendedMethod != null && allowedDefs?.FieldType == typeof(HashSet<ThingDef>)
                        && disallowedSpecial?.FieldType == typeof(List<SpecialThingFilterDef>);
        bool closeOk = restrictionOk && getRestrictionByIdMethod != null && preCloseDetourMethod != null && dialogBillField != null;

        if (mirrorOk)
        {
            filterAllowedDefs = AccessTools.FieldRefAccess<ThingFilter, HashSet<ThingDef>>(allowedDefs);
            filterDisallowedSpecial = AccessTools.FieldRefAccess<ThingFilter, List<SpecialThingFilterDef>>(disallowedSpecial);
            MpCompat.harmony.Patch(mirrorToLinksMethod, prefix: new HarmonyMethod(typeof(BetterWorkbenchManagement), nameof(PreMirrorToLinks)));
            MP.RegisterSyncMethod(typeof(BetterWorkbenchManagement), nameof(SyncedMirrorToLinks));
        }
        else
            Log.Error("[MPCompat - Better Workbench Management] row 468: ExtendedBillDataStorage.MirrorBillToLinkedBills or a member it reads is missing, linked bills mirror on the hovering PC only");

        if (closeOk)
        {
            MpCompat.harmony.Patch(preCloseDetourMethod, prefix: new HarmonyMethod(typeof(BetterWorkbenchManagement), nameof(PrePreCloseDetour)));
            MP.RegisterSyncMethod(typeof(BetterWorkbenchManagement), nameof(SyncedApplyTableRestriction));
        }
        else
            Log.Error("[MPCompat - Better Workbench Management] row 468: BillConfig_PreClose_Detour.Postfix or WorktableRestrictionData changed, the table restriction write on bill dialog close stays on one PC");

        if (mirrorOk && closeOk)
            Log.Message("[MPCompat - Better Workbench Management] row 468: linked bill mirror and table restriction on bill dialog close synced");
    }

    // Prefix on ExtendedBillDataStorage.MirrorBillToLinkedBills. MP interface code (hover / open dialog, every frame): no local write;
    // one synced command when a linked bill differs. Inside the command, in ticks and in single player: BWM's own code runs.
    private static bool PreMirrorToLinks(object __instance, Bill_Production sourceBill)
    {
        if (inCommand || !MP.IsInMultiplayer || !MP.InInterface)
            return true;
        if (sourceBill == null || __instance == null)
            return false;

        if (!LinkedBillsDiffer(__instance, sourceBill, out var signature))
        {
            lastSentMirror.Remove(sourceBill);
            return false;
        }
        if (lastSentMirror.TryGetValue(sourceBill, out var sent) && sent == signature)
            return false; // already asked for this exact state, command still in flight (or MirrorBills itself leaves it)
        if (lastSentMirror.Count > 64)
            lastSentMirror.Clear();
        lastSentMirror[sourceBill] = signature;
        SyncedMirrorToLinks(sourceBill);
        return false;
    }

    // Synced: runs BWM's own mirror on every PC.
    private static void SyncedMirrorToLinks(Bill bill)
    {
        if (bill is not Bill_Production source || source.billStack == null || source.DeletedOrDereferenced)
            return;
        var main = mainInstance468.Invoke(null, null);
        var storage = main == null ? null : getStorageMethod.Invoke(main, null);
        if (storage == null)
            return;
        inCommand = true;
        try
        {
            mirrorToLinksMethod.Invoke(storage, [source]);
        }
        finally
        {
            inCommand = false;
        }
    }

    private static IDictionary RestrictionDictionary()
    {
        var storage = restrictionStorageType == null ? null : Find.World?.GetComponent(restrictionStorageType);
        return storage == null ? null : restrictionDictField.GetValue(storage) as IDictionary;
    }

    // Reads only: BWM's GetWorktableRestrictionData(int) adds an entry when there is none.
    private static object RestrictionFor(IDictionary restrictions, Bill bill) =>
        restrictions != null && bill?.billStack?.billGiver is Building_WorkTable table && restrictions.Contains(table.thingIDNumber)
            ? restrictions[table.thingIDNumber]
            : null;

    private static bool IsRestricted(object rd) => rd != null && (bool)rdRestricted.GetValue(rd);

    private static bool RestrictionEqualsBill(object rd, Bill bill) =>
        rdPawn.GetValue(rd) as Pawn == bill.PawnRestriction && (bool)rdSlaves.GetValue(rd) == bill.SlavesOnly && (bool)rdMechs.GetValue(rd) == bill.MechsOnly
        && (bool)rdNonMechs.GetValue(rd) == bill.NonMechsOnly && (IntRange)rdSkills.GetValue(rd) == bill.allowedSkillRange;

    private static bool LinkedBillsDiffer(object storage, Bill_Production source, out int signature)
    {
        signature = 0;
        var set = getBillSetMethod.Invoke(storage, [source]);
        if (set == null || setBillsProperty.GetValue(set, null) is not IEnumerable<Bill_Production> bills)
            return false;

        var store = storeField.GetValue(storage) as IDictionary;
        var restrictions = RestrictionDictionary();
        var main = mainInstance468.Invoke(null, null);
        bool mirrorSuspended = main != null && (bool)shouldMirrorSuspendedMethod.Invoke(main, null);
        var sourceExt = store != null && store.Contains(source) ? store[source] : null;
        bool sourceRestricted = IsRestricted(RestrictionFor(restrictions, source));

        bool differ = false;
        int sig = BillHash(source, sourceExt);
        foreach (var dest in bills)
        {
            if (dest == null || dest == source || dest.DeletedOrDereferenced)
                continue;
            var destExt = store != null && store.Contains(dest) ? store[dest] : null;
            unchecked { sig = sig * 31 + BillHash(dest, destExt); }
            if (!differ && BillDiffers(source, sourceExt, sourceRestricted, dest, destExt, RestrictionFor(restrictions, dest), mirrorSuspended))
                differ = true;
        }
        signature = sig;
        return differ;
    }

    // True when MirrorBills(source, dest, preserveTargetProduct: false, initialCopy: false) would change dest. Same conditions as BWM.
    private static bool BillDiffers(Bill_Production src, object srcExt, bool srcRestricted, Bill_Production dst, object dstExt, object dstRd, bool mirrorSuspended)
    {
        if (src.ingredientFilter != null && dst.ingredientFilter != null && !FiltersEqual(src.ingredientFilter, dst.ingredientFilter))
            return true;
        if (dst.ingredientSearchRadius != src.ingredientSearchRadius || dst.GetStoreMode() != src.GetStoreMode() || dst.GetSlotGroup() != src.GetSlotGroup() || dst.paused != src.paused)
            return true;

        if (IsRestricted(dstRd))
        {
            // WorktableRestrictionData.SetWorktableRestrictionToBill(dst)
            var pawn = rdPawn.GetValue(dstRd) as Pawn;
            bool slaves = (bool)rdSlaves.GetValue(dstRd), mechs = (bool)rdMechs.GetValue(dstRd), nonMechs = (bool)rdNonMechs.GetValue(dstRd);
            var skills = (IntRange)rdSkills.GetValue(dstRd);
            if (pawn != null)
            {
                if (dst.PawnRestriction != pawn || dst.SlavesOnly || dst.MechsOnly || dst.NonMechsOnly)
                    return true;
            }
            else if (slaves || mechs || nonMechs)
            {
                if (dst.PawnRestriction != null || dst.SlavesOnly != slaves || dst.MechsOnly != (!slaves && mechs) || dst.NonMechsOnly != (!slaves && !mechs) ||
                    (!mechs || slaves) && dst.allowedSkillRange != skills)
                    return true;
            }
            else if (dst.PawnRestriction != null || dst.SlavesOnly || dst.MechsOnly || dst.NonMechsOnly || dst.allowedSkillRange != skills)
                return true;
        }
        else if (!srcRestricted)
        {
            if (dst.PawnRestriction != src.PawnRestriction || dst.SlavesOnly != src.SlavesOnly || dst.MechsOnly != src.MechsOnly || dst.NonMechsOnly != src.NonMechsOnly
                || dst.allowedSkillRange != src.allowedSkillRange)
                return true;
        }

        if (mirrorSuspended && dst.suspended != src.suspended)
            return true;

        var counter = dst.recipe?.WorkerCounter;
        bool flag = (bool)canOutputBeFilteredMethod.Invoke(null, [dst]) || counter is RecipeWorkerCounter_MakeStoneBlocks || counter is RecipeWorkerCounter_ButcherAnimals;
        if ((src.repeatMode != BillRepeatModeDefOf.TargetCount || flag) && dst.repeatMode != src.repeatMode)
            return true;
        if (flag)
        {
            if (dst.repeatCount != src.repeatCount || dst.targetCount != src.targetCount || dst.pauseWhenSatisfied != src.pauseWhenSatisfied
                || dst.unpauseWhenYouHave != src.unpauseWhenYouHave || dst.GetIncludeSlotGroup() != src.GetIncludeSlotGroup() || dst.hpRange != src.hpRange)
                return true;
            var a = src.recipe?.ProducedThingDef;
            var b = dst.recipe?.ProducedThingDef;
            if (a != null && b != null)
            {
                if ((a.IsWeapon || a.IsApparel) && (b.IsWeapon || b.IsApparel) && dst.includeEquipped != src.includeEquipped)
                    return true;
                if (a.IsApparel && a.apparel.careIfWornByCorpse && b.IsApparel && b.apparel.careIfWornByCorpse && dst.includeTainted != src.includeTainted)
                    return true;
                if (a.HasComp(typeof(CompQuality)) && b.HasComp(typeof(CompQuality)) && dst.qualityRange != src.qualityRange)
                    return true;
                if (a.MadeFromStuff && b.MadeFromStuff && dst.limitToAllowedStuff != src.limitToAllowedStuff)
                    return true;
            }
        }

        // ExtendedBillData.CloneFrom(source data, cloneName: true); a missing entry reads as a fresh one
        bool srcCount = srcExt != null && (bool)extCountAway.GetValue(srcExt), dstCount = dstExt != null && (bool)extCountAway.GetValue(dstExt);
        var srcName = srcExt == null ? null : extName.GetValue(srcExt) as string;
        var dstName = dstExt == null ? null : extName.GetValue(dstExt) as string;
        return srcCount != dstCount || srcName != dstName
               || !FiltersEqual(srcExt == null ? null : extFilter.GetValue(srcExt) as ThingFilter, dstExt == null ? null : extFilter.GetValue(dstExt) as ThingFilter);
    }

    // The parts of ThingFilter.CopyAllowancesFrom a player can change in the bill UI. A null filter reads as a fresh one.
    private static bool FiltersEqual(ThingFilter a, ThingFilter b)
    {
        emptyFilter ??= new ThingFilter();
        a ??= emptyFilter;
        b ??= emptyFilter;
        if (ReferenceEquals(a, b))
            return true;
        if (a.AllowedDefCount != b.AllowedDefCount || a.AllowedHitPointsPercents != b.AllowedHitPointsPercents || a.AllowedQualityLevels != b.AllowedQualityLevels)
            return false;
        var defsB = filterAllowedDefs(b);
        foreach (var def in filterAllowedDefs(a))
        {
            if (!defsB.Contains(def))
                return false;
        }
        var specialA = filterDisallowedSpecial(a);
        var specialB = filterDisallowedSpecial(b);
        int countA = specialA?.Count ?? 0, countB = specialB?.Count ?? 0;
        if (countA != countB)
            return false;
        for (int i = 0; i < countA; i++)
        {
            if (!specialB.Contains(specialA[i]))
                return false;
        }
        return true;
    }

    private static int FilterHash(ThingFilter f)
    {
        if (f == null)
            return 0;
        unchecked
        {
            int h = f.AllowedDefCount;
            foreach (var def in filterAllowedDefs(f))
                h += def.shortHash * 7 + 1;
            h = h * 31 + f.AllowedHitPointsPercents.GetHashCode();
            h = h * 31 + f.AllowedQualityLevels.GetHashCode();
            return h * 31 + (filterDisallowedSpecial(f)?.Count ?? 0);
        }
    }

    // Local latch key only (never sent): changes when the source or a linked bill changes.
    private static int BillHash(Bill_Production b, object ext)
    {
        unchecked
        {
            int bits = (b.paused ? 1 : 0) | (b.suspended ? 2 : 0) | (b.SlavesOnly ? 4 : 0) | (b.MechsOnly ? 8 : 0) | (b.NonMechsOnly ? 16 : 0)
                       | (b.pauseWhenSatisfied ? 32 : 0) | (b.includeEquipped ? 64 : 0) | (b.includeTainted ? 128 : 0) | (b.limitToAllowedStuff ? 256 : 0)
                       | (ext != null && (bool)extCountAway.GetValue(ext) ? 512 : 0);
            int h = bits;
            h = h * 31 + FilterHash(b.ingredientFilter);
            h = h * 31 + b.ingredientSearchRadius.GetHashCode();
            h = h * 31 + (b.GetStoreMode()?.GetHashCode() ?? 0);
            h = h * 31 + (b.GetSlotGroup()?.GetHashCode() ?? 0);
            h = h * 31 + (b.PawnRestriction?.thingIDNumber ?? -1);
            h = h * 31 + b.allowedSkillRange.GetHashCode();
            h = h * 31 + (b.repeatMode?.GetHashCode() ?? 0);
            h = h * 31 + b.repeatCount;
            h = h * 31 + b.targetCount;
            h = h * 31 + b.unpauseWhenYouHave;
            h = h * 31 + (b.GetIncludeSlotGroup()?.GetHashCode() ?? 0);
            h = h * 31 + b.hpRange.GetHashCode();
            h = h * 31 + b.qualityRange.GetHashCode();
            h = h * 31 + ((ext == null ? null : extName.GetValue(ext) as string)?.GetHashCode() ?? 0);
            return h * 31 + FilterHash(ext == null ? null : extFilter.GetValue(ext) as ThingFilter);
        }
    }

    // Prefix on BWM's Window.PreClose postfix. MP interface code: BWM's own "restricted and differs" test without its writes (BWM's
    // GetWorktableRestrictionData(int) also adds an entry), then one synced call that does BWM's write on every PC.
    // The patched method is static and its own parameter is named __instance, which Harmony reserves: take it by position (__0).
    private static bool PrePreCloseDetour(Window __0)
    {
        if (inCommand || !MP.IsInMultiplayer || !MP.InInterface)
            return true;
        if (__0 == null || __0.GetType() != typeof(Dialog_BillConfig) || dialogBillField.GetValue(__0) is not Bill_Production bill)
            return false;
        if (Find.Selector?.SingleSelectedThing is not Building_WorkTable table)
            return false;
        var restrictions = RestrictionDictionary();
        var rd = restrictions != null && restrictions.Contains(table.thingIDNumber) ? restrictions[table.thingIDNumber] : null;
        if (!IsRestricted(rd) || RestrictionEqualsBill(rd, bill))
            return false;
        SyncedApplyTableRestriction(table, bill);
        return false;
    }

    // Synced: BillConfig_PreClose_Detour.Postfix body (decomp lines 37-101) with the table and bill as arguments.
    private static void SyncedApplyTableRestriction(Building_WorkTable table, Bill bill)
    {
        if (table == null || bill is not Bill_Production)
            return;
        var storage = Find.World?.GetComponent(restrictionStorageType);
        var rd = storage == null ? null : getRestrictionByIdMethod.Invoke(storage, [table.thingIDNumber]);
        if (!IsRestricted(rd) || RestrictionEqualsBill(rd, bill))
            return;

        var pawn = bill.PawnRestriction;
        bool slaves = bill.SlavesOnly, mechs = bill.MechsOnly, nonMechs = bill.NonMechsOnly;
        var skills = bill.allowedSkillRange;
        rdPawn.SetValue(rd, pawn);
        rdSlaves.SetValue(rd, slaves);
        rdMechs.SetValue(rd, mechs);
        rdNonMechs.SetValue(rd, nonMechs);
        rdSkills.SetValue(rd, skills);

        var bills = table.BillStack?.Bills;
        if (bills == null)
            return;
        foreach (var b in bills)
        {
            if (pawn != null)
                b.SetPawnRestriction(pawn);
            else if (slaves)
                b.SetAnySlaveRestriction();
            else if (mechs)
                b.SetAnyMechRestriction();
            else if (nonMechs)
                b.SetAnyNonMechRestriction();
            else
                b.SetAnyPawnRestriction();
            b.allowedSkillRange = skills;
        }
    }
}
