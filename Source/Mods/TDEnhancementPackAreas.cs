using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using UnityEngine;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>TD Enhancement Pack - Continued: the Manage Areas additions (recolour, clear entire area, paste, reorder arrows)</summary>
    /// <remarks>
    /// Status: [unverified] compiled only, not run in game.
    /// <para>
    /// Area cells, colour and order are saved game state. MP core syncs only <c>Area.Invert</c>, <c>Area.Delete</c> and
    /// <c>AreaManager.TryMakeNewAllowed</c>, so four buttons of TD's <c>AreaRowPatch</c> changed the clicking PC only:
    /// </para>
    /// <list type="number">
    /// <item>Recolour. <c>Dialog_RecolorArea.SetName</c> calls the vanilla <c>Area_Allowed.SetColor(Color)</c>, which writes the scribed
    /// <c>colorInt</c>. Fix: <c>Area_Allowed.SetColor</c> is a sync method (target = the area, argument = the colour). The vanilla colour
    /// picker (<c>Dialog_AllowedAreaColorPicker.SaveColor</c>) calls the same method, so it is fixed too. A call made while a command
    /// runs, while the simulation ticks or while a game loads is not an interface call and just runs.</item>
    /// <item>"Clear entire area" (<c>DoOrderButton</c>). TD runs <c>InnerGrid.Invoke(area).Clear()</c> (local) and then <c>Invert()</c>
    /// twice (synced), so only the clicking PC ended with an empty area. Fix: a transpiler replaces those nine instructions with one
    /// call of <see cref="SyncClearArea"/>, a sync method that names the area and runs the vanilla <c>Area.Clear()</c> on every PC.</item>
    /// <item>Paste. <c>PasteArea(copy, paste)</c> ORs the active cells of <c>copy</c> into <c>paste</c> (<c>paste[cell] = true</c>). TD's
    /// clipboard (<c>copiedArea</c>, a static on each PC) is already an argument of <c>PasteArea</c>, never read inside it, so the static
    /// method itself is a sync method. Both arguments are areas, found again by area id through their map on every PC.</item>
    /// <item>The two reorder arrows (FIXES row 374). They call <c>MapComponent_AreaOrder.Swap(Area_Allowed, Area)</c>, which swaps two entries of
    /// the scribed <c>areaIndex</c> and re-sorts <c>AreaManager.areas</c>. A gravship launch copies the areas in the order of that list
    /// (<c>Gravship.CopyAreas</c>) and the landing creates them again in that order (<c>GravshipPlacementUtility.CopyAreasIntoMap</c>), so
    /// after a reorder on one PC the new area ids and names differed per PC.
    /// Fix: the same transpiler of <c>DoOrderButton</c> replaces its two calls of <c>Swap</c> by calls of <see cref="SyncSwapAreas"/>, a sync
    /// method (map component, area, area) that runs <c>Swap</c> on every PC. A NoInlining wrapper is used instead of registering the 77 byte
    /// <c>Swap</c> itself, so no JIT inlining of <c>Swap</c> into <c>DoOrderButton</c> can skip the sync prefix.</item>
    /// </list>
    /// <para>
    /// Single player, and any call made while MP is not in the interface, run the original code. Left local on purpose: the Copy button
    /// (it only sets <c>copiedArea</c>) and the not-for-colonists/mechs/animals checkboxes (<c>MapComponent_AreaOrder</c> flags, read
    /// by drawn lists only). The vanilla Copy button of <c>Dialog_ManageAreas.DoAreaRow</c> (its <c>TryMakeNewAllowed(out area)</c> is
    /// synced, so the local branch never received the new area) is fixed by VanillaAreaCopy.cs (FIXES row 373).
    /// </para>
    /// </remarks>
    [MpCompatFor("memegoddess.tdpack")]
    public class TDEnhancementPackAreas
    {
        private const string LogPrefix = "[MPCompat - TD Enhancement Pack areas] ";

        private static bool active;

        // Verse.Area.Invert(), Verse.BoolGrid.Clear(), FieldRef<Area, BoolGrid>.Invoke(Area): the calls of the clear button
        private static MethodInfo areaInvert;
        private static MethodInfo boolGridClear;
        private static MethodInfo fieldRefInvoke;
        // TD_Enhancement_Pack.AreaRowPatch.InnerGrid (public static FieldRef<Area, BoolGrid>)
        private static FieldInfo innerGridField;
        // TDEnhancementPackAreas.SyncClearArea(Area)
        private static MethodInfo syncClearArea;

        // TD_Enhancement_Pack.MapComponent_AreaOrder.Swap(Area_Allowed, Area): the calls of the two reorder arrows, and a handler to run it
        private static MethodInfo swapMethod;
        private static FastInvokeHandler swapHandler;
        // TDEnhancementPackAreas.SyncSwapAreas(MapComponent, Area_Allowed, Area)
        private static MethodInfo syncSwapAreas;

        public TDEnhancementPackAreas(ModContentPack mod)
        {
            MethodInfo doOrderButton, pasteArea;

            // 1. look every member up first and check the IL we rewrite: patch and register nothing when one is missing
            try
            {
                var areaRowPatch = FindType("TD_Enhancement_Pack.AreaRowPatch");
                var recolorDialog = AccessTools.Inner(areaRowPatch, "Dialog_RecolorArea")
                                    ?? throw new TypeLoadException($"type {areaRowPatch.FullName}.Dialog_RecolorArea not found");
                var recolorSetName = GetDeclaredMethod(recolorDialog, "SetName", [typeof(string)]);
                doOrderButton = GetDeclaredMethod(areaRowPatch, "DoOrderButton", [typeof(WidgetRow), typeof(Area)]);
                pasteArea = GetDeclaredMethod(areaRowPatch, "PasteArea", [typeof(Area), typeof(Area)]);
                innerGridField = AccessTools.DeclaredField(areaRowPatch, "InnerGrid")
                                 ?? throw new MissingFieldException(areaRowPatch.FullName, "InnerGrid");
                if (innerGridField.FieldType != typeof(AccessTools.FieldRef<Area, BoolGrid>))
                    throw new InvalidOperationException($"InnerGrid is {innerGridField.FieldType}, expected FieldRef<Area, BoolGrid>");
                fieldRefInvoke = GetDeclaredMethod(typeof(AccessTools.FieldRef<Area, BoolGrid>), "Invoke", [typeof(Area)]);
                boolGridClear = GetDeclaredMethod(typeof(BoolGrid), "Clear", Type.EmptyTypes);
                areaInvert = GetDeclaredMethod(typeof(Area), "Invert", Type.EmptyTypes);
                GetDeclaredMethod(typeof(Area), "Clear", Type.EmptyTypes); // called by SyncClearArea
                var setColor = GetDeclaredMethod(typeof(Area_Allowed), "SetColor", [typeof(Color)]);
                syncClearArea = GetDeclaredMethod(typeof(TDEnhancementPackAreas), nameof(SyncClearArea), [typeof(Area)]);
                var areaOrderType = FindType("TD_Enhancement_Pack.MapComponent_AreaOrder");
                if (!typeof(MapComponent).IsAssignableFrom(areaOrderType))
                    throw new InvalidOperationException($"{areaOrderType.FullName} is not a MapComponent");
                swapMethod = GetDeclaredMethod(areaOrderType, "Swap", [typeof(Area_Allowed), typeof(Area)]);
                swapHandler = MethodInvoker.GetHandler(swapMethod);
                syncSwapAreas = GetDeclaredMethod(typeof(TDEnhancementPackAreas), nameof(SyncSwapAreas), [typeof(MapComponent), typeof(Area_Allowed), typeof(Area)]);

                // The recolour window must end in the vanilla instance method (not in TD's own reflection based SetColor extension).
                var setColorCalls = PatchProcessor.GetOriginalInstructions(recolorSetName).Count(ci => IsCall(ci, setColor));
                if (setColorCalls != 1)
                    throw new InvalidOperationException($"Dialog_RecolorArea.SetName: expected 1 call of Area_Allowed.SetColor, found {setColorCalls}");
                RewriteClear(PatchProcessor.GetOriginalInstructions(doOrderButton), out var clears);
                if (clears != 1)
                    throw new InvalidOperationException($"DoOrderButton: expected 1 clear button block, found {clears}");
                RewriteSwap(PatchProcessor.GetOriginalInstructions(doOrderButton), out var swaps);
                if (swaps != 2)
                    throw new InvalidOperationException($"DoOrderButton: expected 2 calls of MapComponent_AreaOrder.Swap (up and down arrow), found {swaps}");
            }
            catch (Exception e)
            {
                Log.Error($"{LogPrefix}this TD Enhancement Pack build does not have what the module syncs, nothing patched or registered, the Manage Areas buttons stay local: {e}");
                throw new InvalidOperationException($"{LogPrefix}unsupported TD Enhancement Pack build", e);
            }

            // 2. patch, then register; when something fails the patch is taken off again and the buttons behave as without the module
            try
            {
                MpCompat.harmony.Patch(doOrderButton, transpiler: new HarmonyMethod(typeof(TDEnhancementPackAreas), nameof(TranspileDoOrderButton)));
                MP.RegisterSyncMethod(typeof(Area_Allowed), nameof(Area_Allowed.SetColor));
                MP.RegisterSyncMethod(pasteArea).CancelIfAnyArgNull();
                MP.RegisterSyncMethod(typeof(TDEnhancementPackAreas), nameof(SyncClearArea)).CancelIfAnyArgNull();
                MP.RegisterSyncMethod(typeof(TDEnhancementPackAreas), nameof(SyncSwapAreas)).CancelIfAnyArgNull();
            }
            catch (Exception e)
            {
                MpCompat.harmony.Unpatch(doOrderButton, HarmonyPatchType.Transpiler, MpCompat.harmony.Id);
                Log.Error($"{LogPrefix}patching or registering failed, the DoOrderButton patch was removed again, so clear and reorder stay local (recolour and paste stay synced if they were registered before the failure): {e}");
                throw new InvalidOperationException($"{LogPrefix}patch failed", e);
            }

            active = true;
            Log.Message($"{LogPrefix}installed: Area_Allowed.SetColor, AreaRowPatch.PasteArea, a synced clear and a synced reorder are sync methods, DoOrderButton patched.");
        }

        // ---- the clear button and the reorder arrows (one transpiler for DoOrderButton) ----------------------------------------------

        private static IEnumerable<CodeInstruction> TranspileDoOrderButton(IEnumerable<CodeInstruction> instructions)
            => RewriteSwap(RewriteClear(instructions, out _), out _);

        // The two reorder arrows call "component.Swap(area, neighbour)": ldloc component; ldloc area; ldloc neighbour; callvirt Swap.
        // The call becomes "call SyncSwapAreas(MapComponent, Area_Allowed, Area)": the same three values on the stack, the instance first.
        // The new instruction takes over the labels and blocks of the old one.
        private static List<CodeInstruction> RewriteSwap(IEnumerable<CodeInstruction> instructions, out int rewritten)
        {
            rewritten = 0;
            var result = new List<CodeInstruction>();
            foreach (var ci in instructions)
            {
                if (!IsCall(ci, swapMethod))
                {
                    result.Add(ci);
                    continue;
                }

                var call = new CodeInstruction(OpCodes.Call, syncSwapAreas);
                call.labels.AddRange(ci.labels);
                call.blocks.AddRange(ci.blocks);
                result.Add(call);
                rewritten++;
            }
            return result;
        }

        // The nine instructions of the clear button (area = the Area_Allowed local):
        //   ldsfld InnerGrid; ldloc area; callvirt FieldRef.Invoke; ldind.ref; callvirt BoolGrid.Clear;
        //   ldloc area; callvirt Area.Invert; ldloc area; callvirt Area.Invert
        // become: ldloc area; call SyncClearArea(Area). The new first instruction takes over the labels and blocks of the old one.
        private static List<CodeInstruction> RewriteClear(IEnumerable<CodeInstruction> instructions, out int rewritten)
        {
            rewritten = 0;
            var list = instructions.ToList();
            var result = new List<CodeInstruction>(list.Count);
            for (var i = 0; i < list.Count; i++)
            {
                if (!IsClearBlock(list, i))
                {
                    result.Add(list[i]);
                    continue;
                }

                var loadArea = new CodeInstruction(list[i + 1].opcode, list[i + 1].operand);
                loadArea.labels.AddRange(list[i].labels);
                loadArea.blocks.AddRange(list[i].blocks);
                result.Add(loadArea);
                result.Add(new CodeInstruction(OpCodes.Call, syncClearArea));
                rewritten++;
                i += 8; // the loop adds the ninth
            }
            return result;
        }

        private static bool IsClearBlock(List<CodeInstruction> list, int i)
        {
            if (i + 8 >= list.Count)
                return false;

            var area = list[i + 1];
            return list[i].LoadsField(innerGridField)
                   && area.IsLdloc()
                   && IsCall(list[i + 2], fieldRefInvoke)
                   && list[i + 3].opcode == OpCodes.Ldind_Ref
                   && IsCall(list[i + 4], boolGridClear)
                   && SameLoad(list[i + 5], area)
                   && IsCall(list[i + 6], areaInvert)
                   && SameLoad(list[i + 7], area)
                   && IsCall(list[i + 8], areaInvert)
                   // nothing may jump into the middle of the block
                   && Enumerable.Range(i + 1, 8).All(k => list[k].labels.Count == 0 && list[k].blocks.Count == 0);
        }

        private static bool SameLoad(CodeInstruction ci, CodeInstruction other)
            => ci.IsLdloc() && ci.opcode == other.opcode && Equals(ci.operand, other.operand);

        private static bool IsCall(CodeInstruction ci, MethodInfo method)
            => (ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && ci.operand is MethodBase called && called.Equals(method);

        // ---- synced -------------------------------------------------------------------------------------------------------------------
        // Area_Allowed.SetColor and AreaRowPatch.PasteArea are registered as they are, their own bodies run on every PC.

        // The clear button, on every PC: the area is named by the argument. The vanilla Area.Clear() empties the cell grid and marks the
        // drawer dirty, the same result as TD's own BoolGrid.Clear() followed by two Invert() calls.
        // NoInlining: Multiplayer patches this method (sync prefix) - its caller (the patched DoOrderButton) must call it, never inline its body.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void SyncClearArea(Area area)
        {
            if (!active || area is not Area_Allowed)
                return;

            area.Clear();
        }

        // The reorder arrows, on every PC: TD's component of the map (found again through its map) swaps the two areas' entries of its
        // areaIndex and re-sorts the map's area list, the same call the arrow made. The component is named by the argument, so it is
        // MapComponent here (TD's type is not known at compile time) and checked against TD's type.
        // NoInlining: Multiplayer patches this method (sync prefix) - its caller (the patched DoOrderButton) must call it, never inline its body.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void SyncSwapAreas(MapComponent areaOrder, Area_Allowed area, Area other)
        {
            if (!active || areaOrder == null || area == null || other == null || areaOrder.GetType() != swapMethod.DeclaringType)
                return;

            swapHandler(areaOrder, area, other);
        }

        private static Type FindType(string name)
            => AccessTools.TypeByName(name) ?? throw new TypeLoadException($"type {name} not found");

        private static MethodInfo GetDeclaredMethod(Type type, string name, Type[] args)
            => AccessTools.DeclaredMethod(type, name, args) ?? throw new MissingMethodException(type.FullName, name);
    }
}
