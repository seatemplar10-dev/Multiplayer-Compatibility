using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Multiplayer.API;
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
    /// Fix: a transpiler replaces that one <c>stfld Zone.label</c> by a call of <see cref="SyncSetZoneLabel"/>, a sync method that names the
    /// zone and sets the label on every PC when the command runs. The clicking PC writes nothing before the command runs. After the write each
    /// PC marks the mod's floor label cache dirty, as the clicking PC already did at the end of <c>ApplyChanges</c>, so the new name is drawn on
    /// every PC. Single player, and any call made while a command runs, run the plain write.
    /// </para>
    /// <para>
    /// Left as it is on purpose: the mod's own per zone data (<c>CustomZoneData</c> label and colour, a world component that only the floor
    /// label drawing reads), the room rename window, and the rename window itself, which Multiplayer opens on every PC through the designator
    /// command (<c>Designator_Rename.DesignateSingleCell</c>).
    /// </para>
    /// </remarks>
    [MpCompatFor("falconne.LabelsOnFloor")]
    public class LabelsOnFloor
    {
        private const string LogPrefix = "[MPCompat - Labels on Floor] ";

        // Verse.Zone.label (public saved field)
        private static FieldInfo zoneLabelField;
        // LabelsOnFloor.LabelsOnFloorMod.Instance (static getter), .LabelPlacementHandler (getter) and LabelPlacementHandler.SetDirty()
        // Optional: when one of them is missing the label is still synced, only the floor label cache is not refreshed.
        private static MethodInfo modInstanceGetter;
        private static MethodInfo placementHandlerGetter;
        private static MethodInfo setDirty;
        // LabelsOnFloor.SyncSetZoneLabel(Zone, string)
        private static MethodInfo syncSetZoneLabel;

        public LabelsOnFloor(ModContentPack mod)
        {
            MethodInfo applyChanges;

            // 1. look every member up first and check the IL we rewrite: patch and register nothing when one is missing
            try
            {
                var dialogType = AccessTools.TypeByName("LabelsOnFloor.Dialog_RenameZoneWithColor")
                                 ?? throw new TypeLoadException("type LabelsOnFloor.Dialog_RenameZoneWithColor not found");
                applyChanges = AccessTools.DeclaredMethod(dialogType, "ApplyChanges", Type.EmptyTypes)
                               ?? throw new MissingMethodException(dialogType.FullName, "ApplyChanges");
                zoneLabelField = AccessTools.DeclaredField(typeof(Zone), nameof(Zone.label))
                                 ?? throw new MissingFieldException(typeof(Zone).FullName, nameof(Zone.label));
                syncSetZoneLabel = AccessTools.DeclaredMethod(typeof(LabelsOnFloor), nameof(SyncSetZoneLabel), [typeof(Zone), typeof(string)])
                                   ?? throw new MissingMethodException(typeof(LabelsOnFloor).FullName, nameof(SyncSetZoneLabel));

                RewriteLabelStore(PatchProcessor.GetOriginalInstructions(applyChanges), out var stores);
                if (stores != 1)
                    throw new InvalidOperationException($"{dialogType.FullName}.ApplyChanges: expected 1 write of Zone.label, found {stores}");
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

            // 2. patch, then register; when something fails the patch is taken off again and the rename behaves as without the module
            try
            {
                MpCompat.harmony.Patch(applyChanges, transpiler: new HarmonyMethod(typeof(LabelsOnFloor), nameof(TranspileApplyChanges)));
                MP.RegisterSyncMethod(typeof(LabelsOnFloor), nameof(SyncSetZoneLabel)).CancelIfAnyArgNull();
            }
            catch (Exception e)
            {
                MpCompat.harmony.Unpatch(applyChanges, HarmonyPatchType.Transpiler, MpCompat.harmony.Id);
                Log.Error($"{LogPrefix}patching or registering failed, the patch was removed again, the zone rename stays local: {e}");
                throw new InvalidOperationException($"{LogPrefix}patch failed", e);
            }

            Log.Message($"{LogPrefix}installed: the zone rename OK button sets Zone.label through a sync method.");
        }

        // ---- the rename OK button -----------------------------------------------------------------------------------------------------

        private static IEnumerable<CodeInstruction> TranspileApplyChanges(IEnumerable<CodeInstruction> instructions)
            => RewriteLabelStore(instructions, out _);

        // "ldarg.0; ldfld _zone; ldloc label; stfld Zone.label" becomes "...; call SyncSetZoneLabel(Zone, string)".
        // Both end with the zone and the string on the stack, so only the last instruction changes (it keeps the labels and blocks).
        private static List<CodeInstruction> RewriteLabelStore(IEnumerable<CodeInstruction> instructions, out int rewritten)
        {
            rewritten = 0;
            var result = new List<CodeInstruction>();
            foreach (var ci in instructions)
            {
                if (!ci.StoresField(zoneLabelField))
                {
                    result.Add(ci);
                    continue;
                }

                var call = new CodeInstruction(OpCodes.Call, syncSetZoneLabel);
                call.labels.AddRange(ci.labels);
                call.blocks.AddRange(ci.blocks);
                result.Add(call);
                rewritten++;
            }
            return result;
        }

        // ---- synced -------------------------------------------------------------------------------------------------------------------

        // The zone rename, on every PC: the zone is named by the argument (found again by zone id through its map). The write is the one the
        // dialog made, and the same one the synced RenamableLabel setter makes. A call made while MP is not in a game, or while a command runs,
        // only runs the body.
        // NoInlining: Multiplayer patches this method (sync prefix) - its caller (the patched ApplyChanges) must call it, never inline its body.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void SyncSetZoneLabel(Zone zone, string label)
        {
            if (zone == null)
                return;

            zone.label = label;
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
