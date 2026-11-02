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
    /// <summary>FIXES row 373: the vanilla Manage Areas "Copy" button makes an EMPTY new area in a multiplayer game.</summary>
    /// <remarks>
    /// Status: [unverified] compiled only, not run in game.
    /// <para>
    /// Cause. Dialog_ManageAreas.DoAreaRow does, on the click: "if (map.areaManager.TryMakeNewAllowed(out var area3))", then
    /// "foreach (IntVec3 activeCell in area.ActiveCells) { area3[activeCell] = true; }", else the "MaxAreasReached" message. Multiplayer
    /// syncs AreaManager.TryMakeNewAllowed itself (SyncMethods.cs: "SyncMethod.Register(typeof(AreaManager), nameof(AreaManager.TryMakeNewAllowed));").
    /// In the interface that sync template sends the command and cancels the call, with the out parameter set to null and the result
    /// to false (SyncTemplates.cs: CreateDefaultReturnValue). So on the clicking PC the branch never gets the new area: nothing is
    /// copied there, and the "else" branch shows a false "max areas" message. The command still makes a new, empty area on every PC
    /// (same ID, order, label and colour everywhere), so nothing desyncs: the copy is just never filled in.
    /// </para>
    /// <para>
    /// Fix. In a multiplayer game, when the click happens in the interface, the whole copy is ONE synced method, <see cref="SyncCopyArea"/>
    /// (argument: the source area, found again by area id and map on every PC). On every PC, inside the command, it runs the vanilla
    /// steps unchanged: AreaManager.TryMakeNewAllowed (next area ID, Rand colour, label, list sort: all deterministic inside a
    /// command), then "copy[cell] = true" for every active cell of the source. A transpiler on DoAreaRow (a 612 byte method with
    /// exception blocks, never inlined) puts a call behind the Copy button's WidgetRow.ButtonIcon: <see cref="CopyButtonHook"/> takes
    /// the click, so the vanilla block behind it (the local TryMakeNewAllowed, the loop, the message) does not run. Single player, and
    /// any call made outside the interface, get the click back unchanged and run the vanilla block.
    /// </para>
    /// <para>
    /// Determinism: the hook is interface code. It only reads (AreaManager.CanMakeNewAllowed) and sends the command; it makes no area,
    /// no ID, no Rand call. The "max areas reached" message is shown by the hook as before (interface messages get a local negative id,
    /// UniqueIds.cs), so it never reaches the other PCs. If two players press at once and the second command finds ten areas, its
    /// TryMakeNewAllowed answers false on every PC and nothing is made, the same on all of them.
    /// </para>
    /// </remarks>
    internal static class VanillaAreaCopy
    {
        private const string LogPrefix = "[MPCompat] row 373: ";

        private static FieldInfo copyIcon;           // Verse.TexButton.Copy
        private static MethodInfo tryMakeNewAllowed; // Verse.AreaManager.TryMakeNewAllowed(out Area_Allowed)
        private static MethodInfo hook;              // CopyButtonHook(bool, Area)
        private static bool installed;

        internal static void Install(Harmony harmony)
        {
            var doAreaRow = AccessTools.DeclaredMethod(typeof(Dialog_ManageAreas), "DoAreaRow", new[] { typeof(Rect), typeof(Area), typeof(int) });
            copyIcon = AccessTools.Field(typeof(TexButton), nameof(TexButton.Copy));
            tryMakeNewAllowed = AccessTools.DeclaredMethod(typeof(AreaManager), nameof(AreaManager.TryMakeNewAllowed));
            hook = AccessTools.DeclaredMethod(typeof(VanillaAreaCopy), nameof(CopyButtonHook));
            if (doAreaRow == null || copyIcon == null || tryMakeNewAllowed == null || hook == null)
            {
                Log.Warning($"{LogPrefix}Dialog_ManageAreas.DoAreaRow (found {doAreaRow != null}), TexButton.Copy ({copyIcon != null}) or " +
                            $"AreaManager.TryMakeNewAllowed ({tryMakeNewAllowed != null}) not as expected, the Copy button stays local");
                return;
            }

            // Check the IL we rewrite first: patch and register nothing unless exactly one Copy button block is found.
            Rewrite(PatchProcessor.GetOriginalInstructions(doAreaRow), out var found);
            if (found != 1)
            {
                Log.Warning($"{LogPrefix}Dialog_ManageAreas.DoAreaRow: expected 1 Copy button block that calls TryMakeNewAllowed, found {found}, " +
                            "nothing patched, the Copy button stays local");
                return;
            }

            // Register first, patch second: the hook is the only caller of SyncCopyArea, so a failed patch leaves the method unused.
            MP.RegisterSyncMethod(typeof(VanillaAreaCopy), nameof(SyncCopyArea)).CancelIfAnyArgNull();
            harmony.Patch(doAreaRow, transpiler: new HarmonyMethod(typeof(VanillaAreaCopy), nameof(TranspileDoAreaRow)) { priority = Priority.First });
            installed = true;
            Log.Message($"{LogPrefix}Manage Areas Copy button installed: one synced SyncCopyArea(Area) on every PC, DoAreaRow patched.");
        }

        // ---- the button ---------------------------------------------------------------------------------------------------------------

        // Wanted only to run before other mods' transpilers of DoAreaRow (TD Enhancement Pack adds its own buttons there), so the
        // Copy button is still the vanilla block when we look for it. The inserted call keeps the stack as it was (bool in, bool out).
        private static IEnumerable<CodeInstruction> TranspileDoAreaRow(IEnumerable<CodeInstruction> instructions)
        {
            var result = Rewrite(instructions, out var found);
            if (found != 1)
                Log.Warning($"{LogPrefix}Dialog_ManageAreas.DoAreaRow transpiler found {found} Copy button blocks (expected 1); " +
                            (found == 0 ? "the Copy button stays local" : "all of them were patched"));
            return result;
        }

        // The Copy button block (IL of the 1.7 game dll):
        //   ldloc.0 (widgetRow); ldsfld TexButton.Copy; ...; callvirt WidgetRow.ButtonIcon; brfalse.s <delete button>;
        //   ldarg.0; ldfld map; ldfld areaManager; ldloca area3; callvirt AreaManager.TryMakeNewAllowed; brfalse.s <message>; ...
        // After the ButtonIcon call we add: ldarg.2 (the Area argument of DoAreaRow); call CopyButtonHook(bool, Area).
        private static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, out int rewritten)
        {
            var list = instructions.ToList();
            var result = new List<CodeInstruction>(list.Count + 2);
            rewritten = 0;
            for (var i = 0; i < list.Count; i++)
            {
                result.Add(list[i]);
                if (!IsCopyButtonCall(list, i))
                    continue;

                result.Add(new CodeInstruction(OpCodes.Ldarg_2));
                result.Add(new CodeInstruction(OpCodes.Call, hook));
                rewritten++;
            }
            return result;
        }

        private static bool IsCopyButtonCall(List<CodeInstruction> list, int i)
        {
            if (!IsButtonIcon(list[i]) || i + 1 >= list.Count)
                return false;

            // the result is tested at once: if (widgetRow.ButtonIcon(...))
            var next = list[i + 1].opcode;
            if (next != OpCodes.Brfalse && next != OpCodes.Brfalse_S)
                return false;

            // TexButton.Copy is loaded as an argument of THIS ButtonIcon call (no other ButtonIcon call in between)
            var loadsIcon = false;
            for (var j = i - 1; j >= 0 && j >= i - 24; j--)
            {
                if (IsButtonIcon(list[j]))
                    break;
                if (list[j].LoadsField(copyIcon))
                {
                    loadsIcon = true;
                    break;
                }
            }
            if (!loadsIcon)
                return false;

            // and the block behind it makes its area with the (synced) TryMakeNewAllowed
            for (var k = i + 2; k < list.Count && k <= i + 14; k++)
            {
                if (IsButtonIcon(list[k]))
                    return false;
                if (IsCall(list[k], tryMakeNewAllowed))
                    return true;
            }
            return false;
        }

        private static bool IsButtonIcon(CodeInstruction ci)
            => (ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt)
               && ci.operand is MethodInfo m && m.DeclaringType == typeof(WidgetRow) && m.Name == "ButtonIcon" && m.ReturnType == typeof(bool);

        private static bool IsCall(CodeInstruction ci, MethodInfo method)
            => (ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && ci.operand is MethodBase called && called.Equals(method);

        // Runs in the interface, once per frame the button is drawn. Returns the click for the vanilla block behind the button.
        private static bool CopyButtonHook(bool clicked, Area area)
        {
            if (!clicked || !installed || area == null || !MP.IsInMultiplayer || !MP.InInterface)
                return clicked;

            var manager = area.areaManager;
            if (manager == null)
                return clicked;

            // The vanilla "else" branch, for the clicking PC only. AreaManager.CanMakeNewAllowed only reads.
            if (!manager.CanMakeNewAllowed())
            {
                Messages.Message("MaxAreasReached".Translate(AreaManager.MaxAllowedAreas), MessageTypeDefOf.RejectInput);
                return false;
            }

            SyncCopyArea(area);
            return false; // the vanilla block (local TryMakeNewAllowed, copy loop, message) must not run
        }

        // ---- synced -------------------------------------------------------------------------------------------------------------------

        // On every PC, inside the command, from the same argument (the source area, found again by area id and map).
        // The same steps the vanilla block does, in the same order: make the new area, then set every active cell of the source.
        // AreaManager.TryMakeNewAllowed is itself a sync method; inside a command it just runs.
        // NoInlining: Multiplayer patches this method (sync template), its caller must call it, never inline its body.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SyncCopyArea(Area source)
        {
            var manager = source?.areaManager;
            if (manager == null)
                return;

            if (!manager.TryMakeNewAllowed(out var copy))
                return; // ten areas already (another command was first): the same answer on every PC, nothing is made

            foreach (var cell in source.ActiveCells.ToList())
                copy[cell] = true;
        }
    }
}
