using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using UnityEngine;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>TD Enhancement Pack - Continued: the Manage Areas additions (recolour, clear entire area, paste)</summary>
    /// <remarks>
    /// Status: [unverified] compiled only, not run in game.
    /// <para>
    /// Area cells and colour are saved game state. MP core syncs only <c>Area.Invert</c>, <c>Area.Delete</c> and
    /// <c>AreaManager.TryMakeNewAllowed</c>, so three buttons of TD's <c>AreaRowPatch</c> changed the clicking PC only:
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
    /// </list>
    /// <para>
    /// Single player, and any call made while MP is not in the interface, run the original code. Left local on purpose: the Copy button
    /// (it only sets <c>copiedArea</c>) and the not-for-colonists/mechs/animals checkboxes (<c>MapComponent_AreaOrder</c> flags, read
    /// by drawn lists only). Known gaps, not fixed here: the reorder arrows (<c>MapComponent_AreaOrder.Swap</c> also reorders
    /// <c>AreaManager.areas</c>, which <c>Gravship.CopyAreas</c> iterates) and the vanilla Copy button of
    /// <c>Dialog_ManageAreas.DoAreaRow</c> (its <c>TryMakeNewAllowed(out area)</c> is synced, so the local branch never receives the
    /// new area and copies nothing).
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

                // The recolour window must end in the vanilla instance method (not in TD's own reflection based SetColor extension).
                var setColorCalls = PatchProcessor.GetOriginalInstructions(recolorSetName).Count(ci => IsCall(ci, setColor));
                if (setColorCalls != 1)
                    throw new InvalidOperationException($"Dialog_RecolorArea.SetName: expected 1 call of Area_Allowed.SetColor, found {setColorCalls}");
                RewriteClear(PatchProcessor.GetOriginalInstructions(doOrderButton), out var clears);
                if (clears != 1)
                    throw new InvalidOperationException($"DoOrderButton: expected 1 clear button block, found {clears}");
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
            }
            catch (Exception e)
            {
                MpCompat.harmony.Unpatch(doOrderButton, HarmonyPatchType.Transpiler, MpCompat.harmony.Id);
                Log.Error($"{LogPrefix}patching or registering failed, the clear button patch was removed again, the Manage Areas buttons stay local: {e}");
                throw new InvalidOperationException($"{LogPrefix}patch failed", e);
            }

            active = true;
            Log.Message($"{LogPrefix}installed: Area_Allowed.SetColor, AreaRowPatch.PasteArea and a synced clear are sync methods, DoOrderButton patched.");
        }

        // ---- the clear button -------------------------------------------------------------------------------------------------------

        private static IEnumerable<CodeInstruction> TranspileDoOrderButton(IEnumerable<CodeInstruction> instructions)
            => RewriteClear(instructions, out _);

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
        public static void SyncClearArea(Area area)
        {
            if (!active || area is not Area_Allowed)
                return;

            area.Clear();
        }

        private static Type FindType(string name)
            => AccessTools.TypeByName(name) ?? throw new TypeLoadException($"type {name} not found");

        private static MethodInfo GetDeclaredMethod(Type type, string name, Type[] args)
            => AccessTools.DeclaredMethod(type, name, args) ?? throw new MissingMethodException(type.FullName, name);
    }
}
