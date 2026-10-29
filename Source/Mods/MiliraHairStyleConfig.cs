using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using UnityEngine;
using Verse;

namespace Multiplayer.Compat;

/// <summary>Milira Race (Ancot.MiliraRace): the Milian hair style / hair colour window (Milira.Dialog_MilianHairStyleConfig)</summary>
/// <remarks>
/// FIXES row 309. The window is opened from the UI (the Milian's gizmo, or a click on the hair colour column of the Milian work tab)
/// so it exists on the clicking machine only; that stays. What was wrong: its controls wrote saved comp state of the pawn straight
/// from UI code, on that machine only:
///   hair style click  -> CompMilianHairSwitch.ChangeGraphic(i) (front / behind hair path, saved) and CompMilianHairSwitch.num = i (saved)
///   Accept            -> the Action the opener passed in, which sets CompMilianHairSwitch.colorOverride (saved)
/// This module (Milira is reached by name only, no reference to the dll):
/// 1. DrawScrollHairSwitch: a transpiler turns the ChangeGraphic call into a call of SetHairStyle (-> synced SyncSetHairStyle(Pawn, int))
///    and turns the "num = i" store that follows it into a no-op, because SyncSetHairStyle sets num itself on every machine in the
///    same command (a local store would change num on the clicking machine some ticks early).
/// 2. DoWindowContents: a transpiler turns the Accept call "action?.Invoke(color)" into AcceptColor, which calls the synced
///    SyncSetColorOverride(Pawn, Color). Both openers in Milira.dll (gizmo, work tab column) pass an Action that only does
///    "comp.colorOverride = newColor", so the synced method sets that field directly instead of sending a delegate. If the pawn has no
///    CompMilianHairSwitch, or this is not a multiplayer game, the original Action runs as before.
/// 3. Both synced methods end with the graphic refresh on every machine: the pawn's render tree is set dirty (what the mod itself does)
///    and the portrait cache entry of the pawn is dropped. The window's own local SetDirty calls run on the clicker before the command
///    is executed; these run after the change, on every machine.
/// Callers outside the window are not touched: ChangeGraphic from CompMilianHairSwitch.PostPostMake and from HediffComp_Promotion, and
/// the "num" writes in the comp constructor and the promotion, run inside the simulation on every machine already. The transpilers
/// patch only the two window methods, and each rewrite is counted on the original IL before anything is patched.
/// Left local on purpose: the Reset button (restores the window's own colour field and drops caches, no saved state); the colour
/// wheel and R/G/B boxes (window fields); the Copy button (writes MiliraGameComponent_OverallControl.colorClipboard, a per-player
/// clipboard that only the window reads, and a Paste then travels as the colour argument of Accept); the portrait and rotation buttons.
/// There is no live preview of the colour: the portrait only changes after Accept, so Cancel / close has nothing to revert. A hair
/// style click is applied at once and has no Cancel in the mod either.
/// Supported build: Milira Race 1.6 folder Milira.dll (365,056 bytes; this is what loads in the 1.7 co-op copy, LoadFolders has only
/// v1.5 and v1.6). Every member is looked up before anything is patched; on a mismatch nothing is patched and the window behaves as
/// without the module.
/// Status: [unverified] compiled only, not run in game.
/// </remarks>
[MpCompatFor("Ancot.MiliraRace")]
internal class MiliraHairStyleConfig
{
    private const string LogPrefix = "[MPCompat - Milira hair style window] ";

    private static Type compType;
    private static FieldInfo numField, dialogPawnField;
    private static AccessTools.FieldRef<object, int> numRef;
    private static AccessTools.FieldRef<object, Color> colorOverrideRef;
    private static MethodInfo changeGraphicMethod, actionInvokeMethod;
    private static MethodInfo setHairStyleStub, ignoreNumStoreStub, acceptColorStub;

    public MiliraHairStyleConfig(ModContentPack mod)
    {
        // 1. look everything up first; patch nothing unless all of it is there
        MethodInfo doWindowContents, drawScrollHairSwitch;
        try
        {
            compType = AccessTools.TypeByName("Milira.CompMilianHairSwitch");
            var dialogType = AccessTools.TypeByName("Milira.Dialog_MilianHairStyleConfig");
            if (compType == null || dialogType == null)
                throw new InvalidOperationException("Milira.CompMilianHairSwitch or Milira.Dialog_MilianHairStyleConfig not found");

            numField = AccessTools.DeclaredField(compType, "num");
            changeGraphicMethod = AccessTools.DeclaredMethod(compType, "ChangeGraphic", [typeof(int)]);
            dialogPawnField = AccessTools.DeclaredField(dialogType, "milian");
            doWindowContents = AccessTools.DeclaredMethod(dialogType, "DoWindowContents", [typeof(Rect)]);
            drawScrollHairSwitch = AccessTools.DeclaredMethod(dialogType, "DrawScrollHairSwitch", [typeof(Pawn), typeof(Rect)]);
            actionInvokeMethod = AccessTools.DeclaredMethod(typeof(Action<Color>), "Invoke");
            if (numField == null || numField.FieldType != typeof(int))
                throw new InvalidOperationException("CompMilianHairSwitch.num (int) not found");
            if (changeGraphicMethod == null)
                throw new InvalidOperationException("CompMilianHairSwitch.ChangeGraphic(int) not found");
            if (dialogPawnField == null || dialogPawnField.FieldType != typeof(Pawn))
                throw new InvalidOperationException("Dialog_MilianHairStyleConfig.milian (Pawn) not found");
            if (doWindowContents == null || drawScrollHairSwitch == null || actionInvokeMethod == null)
                throw new InvalidOperationException("Dialog_MilianHairStyleConfig.DoWindowContents(Rect) or DrawScrollHairSwitch(Pawn, Rect) not found");

            // FieldRefAccess throws when the field is missing or its type is not the one named here
            numRef = AccessTools.FieldRefAccess<int>(compType, "num");
            colorOverrideRef = AccessTools.FieldRefAccess<Color>(compType, "colorOverride");

            setHairStyleStub = AccessTools.DeclaredMethod(typeof(MiliraHairStyleConfig), nameof(SetHairStyle));
            ignoreNumStoreStub = AccessTools.DeclaredMethod(typeof(MiliraHairStyleConfig), nameof(IgnoreNumStore));
            acceptColorStub = AccessTools.DeclaredMethod(typeof(MiliraHairStyleConfig), nameof(AcceptColor));

            // the rewrites, counted on the original IL of the two window methods (exactly one of each, else the mod build differs)
            RewriteHairClick(PatchProcessor.GetOriginalInstructions(drawScrollHairSwitch), out var swaps, out var stores);
            if (swaps != 1 || stores != 1)
                throw new InvalidOperationException($"DrawScrollHairSwitch: expected 1 ChangeGraphic call and 1 num store, found {swaps} and {stores}");
            RewriteAccept(PatchProcessor.GetOriginalInstructions(doWindowContents), out var accepts);
            if (accepts != 1)
                throw new InvalidOperationException($"DoWindowContents: expected 1 Action<Color>.Invoke call, found {accepts}");
        }
        catch (Exception e)
        {
            Log.Error($"{LogPrefix}this Milira Race build does not have what the module syncs, nothing patched, the hair window behaves as without the module: {e}");
            throw new InvalidOperationException($"{LogPrefix}unsupported Milira Race build", e);
        }

        // 2. register, then patch (both patches or neither)
        MP.RegisterSyncMethod(typeof(MiliraHairStyleConfig), nameof(SyncSetHairStyle));
        MP.RegisterSyncMethod(typeof(MiliraHairStyleConfig), nameof(SyncSetColorOverride));

        MpCompat.harmony.Patch(drawScrollHairSwitch,
            transpiler: new HarmonyMethod(typeof(MiliraHairStyleConfig), nameof(TranspileHairClick)));
        try
        {
            MpCompat.harmony.Patch(doWindowContents,
                transpiler: new HarmonyMethod(typeof(MiliraHairStyleConfig), nameof(TranspileAccept)));
        }
        catch (Exception e)
        {
            MpCompat.harmony.Unpatch(drawScrollHairSwitch, HarmonyPatchType.Transpiler, MpCompat.harmony.Id);
            Log.Error($"{LogPrefix}patching DoWindowContents failed, the hair style patch was removed again, the window behaves as without the module: {e}");
            throw new InvalidOperationException($"{LogPrefix}patch failed", e);
        }

        Log.Message($"{LogPrefix}2 sync methods registered, 2 window methods patched.");
    }

    // ---- transpilers ----------------------------------------------------------------------------------------------------------

    private static IEnumerable<CodeInstruction> TranspileHairClick(IEnumerable<CodeInstruction> instructions)
        => RewriteHairClick(instructions, out _, out _);

    private static IEnumerable<CodeInstruction> TranspileAccept(IEnumerable<CodeInstruction> instructions)
        => RewriteAccept(instructions, out _);

    // [comp, index] on the stack for both: the call of ChangeGraphic(int) and the store into num keep their stack shape when they
    // become static calls taking (object, int). Opcode and operand are changed in place, so labels stay on the instruction.
    private static List<CodeInstruction> RewriteHairClick(IEnumerable<CodeInstruction> instructions, out int swaps, out int stores)
    {
        swaps = stores = 0;
        var result = new List<CodeInstruction>();
        foreach (var ci in instructions)
        {
            if ((ci.opcode == OpCodes.Callvirt || ci.opcode == OpCodes.Call) && ci.operand is MethodBase called && called.Equals(changeGraphicMethod))
            {
                ci.opcode = OpCodes.Call;
                ci.operand = setHairStyleStub;
                swaps++;
            }
            else if (ci.opcode == OpCodes.Stfld && ci.operand is FieldInfo field && field.Equals(numField))
            {
                ci.opcode = OpCodes.Call;
                ci.operand = ignoreNumStoreStub;
                stores++;
            }
            result.Add(ci);
        }
        return result;
    }

    // [action, color] on the stack at "action?.Invoke(color)" (the null check is before it). The dialog (this) is pushed as a third
    // argument; the new ldarg.0 takes over the labels of the call it precedes.
    private static List<CodeInstruction> RewriteAccept(IEnumerable<CodeInstruction> instructions, out int accepts)
    {
        accepts = 0;
        var result = new List<CodeInstruction>();
        foreach (var ci in instructions)
        {
            if (ci.opcode == OpCodes.Callvirt && ci.operand is MethodBase called && called.Name == "Invoke" && called.DeclaringType == typeof(Action<Color>))
            {
                var loadDialog = new CodeInstruction(OpCodes.Ldarg_0);
                loadDialog.labels.AddRange(ci.labels);
                ci.labels.Clear();
                loadDialog.blocks.AddRange(ci.blocks);
                ci.blocks.Clear();
                result.Add(loadDialog);
                result.Add(new CodeInstruction(OpCodes.Call, acceptColorStub));
                accepts++;
            }
            else
                result.Add(ci);
        }
        return result;
    }

    // ---- window side (UI time, this machine only) --------------------------------------------------------------------------------

    // Replaces compMilianHairSwitch.ChangeGraphic(i) in the hair style list. Name the pawn, the comp is found again on every machine.
    private static void SetHairStyle(object comp, int index)
    {
        if ((comp as ThingComp)?.parent is Pawn pawn)
            SyncSetHairStyle(pawn, index);
        else
        {
            // a comp that is not on a pawn: not a case in Milira, keep what the mod did
            changeGraphicMethod.Invoke(comp, [index]);
            numRef(comp) = index;
        }
    }

    // Replaces the "compMilianHairSwitch.num = i" store that follows the ChangeGraphic call: SyncSetHairStyle sets num on every machine.
    private static void IgnoreNumStore(object comp, int index)
    {
    }

    // Replaces action?.Invoke(color) of the Accept button.
    private static void AcceptColor(Action<Color> action, Color color, Window dialog)
    {
        if (MP.IsInMultiplayer && dialogPawnField.GetValue(dialog) is Pawn pawn && FindComp(pawn) != null)
            SyncSetColorOverride(pawn, color);
        else
            action(color);
    }

    // ---- synced (runs on every machine from the same arguments) -----------------------------------------------------------------

    // What a click on a hair style did: ChangeGraphic(i) (front / behind path) and num = i, then the cache refresh.
    private static void SyncSetHairStyle(Pawn pawn, int index)
    {
        var comp = pawn == null ? null : FindComp(pawn);
        if (comp == null || index < 0)
            return;
        changeGraphicMethod.Invoke(comp, [index]);
        numRef(comp) = index;
        RefreshGraphics(pawn);
    }

    // What Accept did through the opener's Action: comp.colorOverride = color, then the cache refresh.
    private static void SyncSetColorOverride(Pawn pawn, Color color)
    {
        var comp = pawn == null ? null : FindComp(pawn);
        if (comp == null)
            return;
        colorOverrideRef(comp) = color;
        RefreshGraphics(pawn);
    }

    private static ThingComp FindComp(Pawn pawn)
    {
        var comps = pawn.AllComps;
        if (comps == null)
            return null;
        for (var i = 0; i < comps.Count; i++)
            if (compType.IsInstanceOfType(comps[i]))
                return comps[i];
        return null;
    }

    // Cache refresh only (no saved state): the Milian hair nodes build their graphic from the comp when the tree is rebuilt.
    private static void RefreshGraphics(Pawn pawn)
    {
        pawn.Drawer?.renderer?.renderTree?.SetDirty();
        PortraitsCache.SetDirty(pawn);
    }
}
