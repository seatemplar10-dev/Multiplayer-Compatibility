using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Multiplayer.API;
using UnityEngine;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Labels on Floor (continued)</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3546003256"/>
    /// <remarks>
    /// Status: [unverified] compiled only, not run in game.
    /// <para>
    /// The mod replaces the vanilla zone rename window with <c>Dialog_RenameZoneWithColor</c>. Its OK button (<c>ApplyChanges</c>) writes the
    /// vanilla saved field <c>Zone.label</c> directly: <c>_zone.label = label;</c>. Multiplayer syncs only the <c>IRenameable.RenamableLabel</c>
    /// setter (what the vanilla rename window calls), so the new name existed on the clicking PC only. The saved zone names then differ, and so
    /// does the next automatic zone name, because <c>ZoneManager.NewZoneName</c> reads the labels of all zones.
    /// </para>
    /// <para>
    /// Fix: a transpiler on the dialog's <c>DoWindowContents</c> replaces its one <c>call ApplyChanges</c> by <see cref="ApplyZoneChangesHook"/>.
    /// In MP the hook reads the dialog's name and colour and sends <see cref="SyncApplyZoneChanges"/>, which on every PC sets <c>Zone.label</c>
    /// and the mod's saved <c>CustomZoneData</c> label and colour (a world component, found again from the zone), then marks the floor label
    /// cache dirty. The clicking PC writes nothing before the command runs. Single player runs the mod's own <c>ApplyChanges</c>.
    /// The caller is patched, not <c>ApplyChanges</c> itself: that one is 95 bytes of IL, and a tier-1 <c>DoWindowContents</c> could inline its
    /// original body past a patch on it.
    /// </para>
    /// <para>
    /// Multiplayer syncs <c>Designator_Rename.DesignateSingleCell</c>, so the rename window opened on every PC, and an OK or Enter in any copy
    /// renamed the zone again. A prefix on <c>WindowStack.Add</c> now drops both rename windows while another player's command runs, so only the
    /// issuing PC gets the window. The command still runs the rest of <c>DesignateSingleCell</c> on every PC (it creates the empty custom data
    /// entry everywhere). Left local on purpose: the room rename window's data (<c>CustomRoomData</c>, read by the floor label drawing only).
    /// </para>
    /// </remarks>
    [MpCompatFor("falconne.LabelsOnFloor")]
    public class LabelsOnFloor
    {
        private const string LogPrefix = "[MPCompat - Labels on Floor] ";

        // LabelsOnFloor.LabelsOnFloorMod.Instance (static getter), .LabelPlacementHandler (getter) and LabelPlacementHandler.SetDirty()
        // Optional: when one of them is missing the rename is still synced, only the floor label cache is not refreshed.
        private static MethodInfo modInstanceGetter;
        private static MethodInfo placementHandlerGetter;
        private static MethodInfo setDirty;

        // Dialog_RenameZoneWithColor: the method we redirect, the mod's own ApplyChanges (single player), and the fields the hook reads
        private static MethodInfo applyChanges;
        private static MethodInfo applyZoneChangesHook;
        private static FastInvokeHandler applyChangesInvoker;
        private static AccessTools.FieldRef<object, Zone> dialogZoneField;
        private static AccessTools.FieldRef<object, string> dialogNameField;
        private static AccessTools.FieldRef<object, Color?> dialogColorField;
        // CustomZoneLabelManagerComponent (world component) .GetOrCreateCustomData(Zone), and CustomZoneData.Label / .CustomColor
        private static Type zoneLabelManagerType;
        private static FastInvokeHandler getOrCreateCustomData;
        private static AccessTools.FieldRef<object, string> customDataLabelField;
        private static AccessTools.FieldRef<object, Color?> customDataColorField;
        // The two rename windows, kept off the PCs that did not issue the rename command
        private static Type zoneDialogType;
        private static Type roomDialogType;

        public LabelsOnFloor(ModContentPack mod)
        {
            MethodInfo doWindowContents;

            // 1. look every member up first and check the IL we rewrite: patch and register nothing when one is missing
            try
            {
                zoneDialogType = AccessTools.TypeByName("LabelsOnFloor.Dialog_RenameZoneWithColor")
                                 ?? throw new TypeLoadException("type LabelsOnFloor.Dialog_RenameZoneWithColor not found");
                roomDialogType = AccessTools.TypeByName("LabelsOnFloor.Dialog_RenameRoomWithColor");
                doWindowContents = AccessTools.DeclaredMethod(zoneDialogType, nameof(Window.DoWindowContents), [typeof(Rect)])
                                   ?? throw new MissingMethodException(zoneDialogType.FullName, nameof(Window.DoWindowContents));
                applyChanges = AccessTools.DeclaredMethod(zoneDialogType, "ApplyChanges", Type.EmptyTypes)
                               ?? throw new MissingMethodException(zoneDialogType.FullName, "ApplyChanges");
                applyChangesInvoker = MethodInvoker.GetHandler(applyChanges);
                applyZoneChangesHook = AccessTools.DeclaredMethod(typeof(LabelsOnFloor), nameof(ApplyZoneChangesHook), [typeof(Window)])
                                       ?? throw new MissingMethodException(typeof(LabelsOnFloor).FullName, nameof(ApplyZoneChangesHook));
                dialogZoneField = AccessTools.FieldRefAccess<Zone>(zoneDialogType, "_zone");
                dialogNameField = AccessTools.FieldRefAccess<string>(zoneDialogType, "curName");
                dialogColorField = AccessTools.FieldRefAccess<Color?>(zoneDialogType, "selectedColor");

                zoneLabelManagerType = AccessTools.TypeByName("LabelsOnFloor.CustomZoneLabelManagerComponent")
                                       ?? throw new TypeLoadException("type LabelsOnFloor.CustomZoneLabelManagerComponent not found");
                getOrCreateCustomData = MethodInvoker.GetHandler(
                    AccessTools.DeclaredMethod(zoneLabelManagerType, "GetOrCreateCustomData", [typeof(Zone)])
                    ?? throw new MissingMethodException(zoneLabelManagerType.FullName, "GetOrCreateCustomData"));
                var customDataType = AccessTools.TypeByName("LabelsOnFloor.CustomZoneData")
                                     ?? throw new TypeLoadException("type LabelsOnFloor.CustomZoneData not found");
                customDataLabelField = AccessTools.FieldRefAccess<string>(customDataType, "Label");
                customDataColorField = AccessTools.FieldRefAccess<Color?>(customDataType, "CustomColor");

                RewriteApplyCall(PatchProcessor.GetOriginalInstructions(doWindowContents), out var calls);
                if (calls != 1)
                    throw new InvalidOperationException($"{zoneDialogType.FullName}.DoWindowContents: expected 1 call of ApplyChanges, found {calls}");
            }
            catch (Exception e)
            {
                Log.Error($"{LogPrefix}this Labels on Floor build does not have what the module syncs, nothing patched or registered, the zone rename stays local: {e}");
                throw new InvalidOperationException($"{LogPrefix}unsupported Labels on Floor build", e);
            }

            // The floor label cache refresh is optional.
            try
            {
                var modType = AccessTools.TypeByName("LabelsOnFloor.LabelsOnFloorMod");
                var handlerType = AccessTools.TypeByName("LabelsOnFloor.LabelPlacementHandler");
                modInstanceGetter = AccessTools.DeclaredPropertyGetter(modType, "Instance");
                placementHandlerGetter = AccessTools.DeclaredPropertyGetter(modType, "LabelPlacementHandler");
                setDirty = AccessTools.DeclaredMethod(handlerType, "SetDirty", Type.EmptyTypes);
                if (modInstanceGetter == null || placementHandlerGetter == null || setDirty == null)
                    throw new MissingMemberException("LabelsOnFloorMod.Instance, LabelsOnFloorMod.LabelPlacementHandler or LabelPlacementHandler.SetDirty not found");
            }
            catch (Exception e)
            {
                modInstanceGetter = placementHandlerGetter = setDirty = null;
                Log.Warning($"{LogPrefix}cannot refresh the floor labels after a synced rename, other PCs show the new zone name after their next label refresh: {e.Message}");
            }

            // 2. register, then patch; when the patch fails it is taken off again and the rename behaves as without the module
            try
            {
                MP.RegisterSyncMethod(typeof(LabelsOnFloor), nameof(SyncApplyZoneChanges)).CancelIfAnyArgNull();
                MpCompat.harmony.Patch(doWindowContents, transpiler: new HarmonyMethod(typeof(LabelsOnFloor), nameof(TranspileDoWindowContents)));
            }
            catch (Exception e)
            {
                MpCompat.harmony.Unpatch(doWindowContents, HarmonyPatchType.Transpiler, MpCompat.harmony.Id);
                Log.Error($"{LogPrefix}patching or registering failed, the patch was removed again, the zone rename stays local: {e}");
                throw new InvalidOperationException($"{LogPrefix}patch failed", e);
            }

            // The window gate is separate: without it the rename is still synced, only every PC gets the window
            try
            {
                MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(WindowStack), nameof(WindowStack.Add), [typeof(Window)]),
                    prefix: new HarmonyMethod(typeof(LabelsOnFloor), nameof(PreWindowStackAdd)));
            }
            catch (Exception e)
            {
                Log.Warning($"{LogPrefix}cannot keep the rename window to the issuing PC, every PC opens it: {e.Message}");
            }

            Log.Message($"{LogPrefix}installed: the zone rename OK button sets the zone label and its colour through a sync method.");
        }

        // ---- the rename window ---------------------------------------------------------------------------------------------------------

        // Designator_Rename.DesignateSingleCell runs as a synced command on every PC and opens the rename window there.
        private static bool PreWindowStackAdd(Window window)
            => window == null || !MP.IsInMultiplayer || !MP.IsExecutingSyncCommand || MP.IsExecutingSyncCommandIssuedBySelf
               || !(zoneDialogType.IsInstanceOfType(window) || (roomDialogType != null && roomDialogType.IsInstanceOfType(window)));

        // ---- the rename OK button -----------------------------------------------------------------------------------------------------

        private static IEnumerable<CodeInstruction> TranspileDoWindowContents(IEnumerable<CodeInstruction> instructions)
            => RewriteApplyCall(instructions, out _);

        // "ldarg.0; call instance ApplyChanges()" becomes "ldarg.0; call ApplyZoneChangesHook(Window)".
        // Both take the dialog from the stack and leave nothing, so only the call changes (it keeps the labels and blocks).
        private static List<CodeInstruction> RewriteApplyCall(IEnumerable<CodeInstruction> instructions, out int rewritten)
        {
            rewritten = 0;
            var result = new List<CodeInstruction>();
            foreach (var ci in instructions)
            {
                if (!ci.Calls(applyChanges))
                {
                    result.Add(ci);
                    continue;
                }

                var call = new CodeInstruction(OpCodes.Call, applyZoneChangesHook);
                call.labels.AddRange(ci.labels);
                call.blocks.AddRange(ci.blocks);
                result.Add(call);
                rewritten++;
            }
            return result;
        }

        // In the interface: single player runs the mod's own ApplyChanges, MP sends what the dialog holds as one command.
        // The mod trims the name, the label and the custom data get the trimmed name (ApplyChanges: "curName?.Trim() ?? """).
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ApplyZoneChangesHook(Window dialog)
        {
            if (!MP.IsInMultiplayer)
            {
                applyChangesInvoker(dialog);
                return;
            }

            var color = dialogColorField(dialog);
            SyncApplyZoneChanges(dialogZoneField(dialog), dialogNameField(dialog)?.Trim() ?? "", color.HasValue, color ?? default);
        }

        // ---- synced -------------------------------------------------------------------------------------------------------------------

        // The zone rename, on every PC: the zone is named by the argument (found again by zone id through its map). The write is the one the
        // dialog made, and the same one the synced RenamableLabel setter makes. A call made while MP is not in a game, or while a command runs,
        // only runs the body.
        // NoInlining: Multiplayer patches this method (sync prefix) - its caller (the hook) must call it, never inline its body.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void SyncApplyZoneChanges(Zone zone, string label, bool hasColor, Color color)
        {
            if (zone == null)
                return;

            zone.label = label;

            // The mod's saved per zone data, the same writes as ApplyChanges; GetOrCreateCustomData finds the entry by zone ID
            var manager = Find.World?.GetComponent(zoneLabelManagerType);
            var data = manager == null ? null : getOrCreateCustomData(manager, zone);
            if (data != null)
            {
                customDataLabelField(data) = label;
                customDataColorField(data) = hasColor ? color : null;
            }

            RefreshFloorLabels();
        }

        // Unsaved drawing cache of the mod, not simulation state: the clicking PC marked it dirty at the end of ApplyChanges.
        private static void RefreshFloorLabels()
        {
            if (setDirty == null)
                return;

            try
            {
                var mod = modInstanceGetter.Invoke(null, null);
                var handler = mod == null ? null : placementHandlerGetter.Invoke(mod, null);
                if (handler != null)
                    setDirty.Invoke(handler, null);
            }
            catch (Exception e)
            {
                // cosmetic only: never let it stop the command
                Log.Warning($"{LogPrefix}refreshing the floor labels failed: {e.Message}");
            }
        }
    }
}
