using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Tucked Furniture by Keno</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3808739466"/>
    [MpCompatFor("Keno.TuckedFurniture")]
    public class TuckedFurniture
    {
        // New-mods check (29 Oct): a building's shrink/offset values feed the mod's PathGrid.CalculatedCostAt postfix (sim),
        // and every gizmo that changes them is a plain lambda or instance method, so a click ran on one PC only.
        // Lambdas 1-26 = shrink / offset / altitude steps, 27 = "untuck when used" (writes the mod settings; synced so both
        // PCs flip together, since those settings also feed path cost). Lambda 0 (show/hide the adjust gizmos) is a local
        // UI flag. Copy fills a static local clipboard and stays local; paste sends the copied values.
        // Both PCs must still run the same Tucked Furniture mod settings (zero-path-cost offsets, keep path cost in use).
        private static Type compType;
        private static FieldInfo copiedSettingsField;
        private static Type clipboardType;
        private static MethodInfo pasteMethod;

        public TuckedFurniture(ModContentPack mod)
        {
            compType = AccessTools.TypeByName("TuckedFurniture.CompTuckedFurniture");
            var adjusterType = AccessTools.TypeByName("TuckedFurniture.FurnitureAdjuster");
            clipboardType = AccessTools.Inner(adjusterType, "FurnitureAdjustmentClipboard");
            copiedSettingsField = AccessTools.Field(adjusterType, "CopiedSettings");
            pasteMethod = AccessTools.Method(compType, "PasteSettings");

            var lambdas = new int[27];
            for (var i = 0; i < lambdas.Length; i++)
                lambdas[i] = i + 1;
            MpCompat.RegisterLambdaMethod(compType, "CompGetGizmosExtra", lambdas);
            MP.RegisterSyncMethod(compType, "ResetToDefault");
            MP.RegisterSyncMethod(compType, "ResetSavedRotationOffsets");

            MP.RegisterSyncMethod(typeof(TuckedFurniture), nameof(SyncedPaste));
            MpCompat.harmony.Patch(pasteMethod, prefix: new HarmonyMethod(typeof(TuckedFurniture), nameof(PrePasteSettings)));
        }

        // Local click: read this PC's clipboard and send its values; the synced call then pastes on every PC.
        private static bool PrePasteSettings(ThingComp __instance)
        {
            if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand)
                return true;

            var boxed = copiedSettingsField.GetValue(null);
            if (boxed == null)
                return false;

            var values = new float[5];
            var mirror = new bool[2];
            var offsets = new float[12];
            var hasOffsets = new bool[4];
            values[0] = (float)Field("HorizontalShrink").GetValue(boxed);
            values[1] = (float)Field("VerticalShrink").GetValue(boxed);
            values[2] = (float)Field("HorizontalOffset").GetValue(boxed);
            values[3] = (float)Field("VerticalOffset").GetValue(boxed);
            values[4] = (float)Field("AltitudeOffset").GetValue(boxed);
            mirror[0] = (bool)Field("MirrorGraphicEastWest").GetValue(boxed);
            mirror[1] = (bool)Field("MirrorGraphicSouthNorth").GetValue(boxed);
            var names = new[] { "NorthOffsets", "EastOffsets", "SouthOffsets", "WestOffsets" };
            for (var i = 0; i < names.Length; i++)
            {
                var rot = Field(names[i]).GetValue(boxed);
                if (rot == null) continue;
                hasOffsets[i] = true;
                offsets[i * 3] = Traverse.Create(rot).Field<float>("Horizontal").Value;
                offsets[i * 3 + 1] = Traverse.Create(rot).Field<float>("Vertical").Value;
                offsets[i * 3 + 2] = Traverse.Create(rot).Field<float>("Altitude").Value;
            }

            SyncedPaste(__instance, values, mirror, offsets, hasOffsets);
            return false;
        }

        private static void SyncedPaste(ThingComp comp, float[] values, bool[] mirror, float[] offsets, bool[] hasOffsets)
        {
            if (comp == null || values?.Length != 5 || mirror?.Length != 2 || offsets?.Length != 12 || hasOffsets?.Length != 4)
                return;

            var clipboard = Activator.CreateInstance(clipboardType);
            Field("HorizontalShrink").SetValue(clipboard, values[0]);
            Field("VerticalShrink").SetValue(clipboard, values[1]);
            Field("HorizontalOffset").SetValue(clipboard, values[2]);
            Field("VerticalOffset").SetValue(clipboard, values[3]);
            Field("AltitudeOffset").SetValue(clipboard, values[4]);
            Field("MirrorGraphicEastWest").SetValue(clipboard, mirror[0]);
            Field("MirrorGraphicSouthNorth").SetValue(clipboard, mirror[1]);
            var names = new[] { "NorthOffsets", "EastOffsets", "SouthOffsets", "WestOffsets" };
            var rotType = Field(names[0]).FieldType;
            for (var i = 0; i < names.Length; i++)
            {
                if (!hasOffsets[i]) continue;
                var rot = Activator.CreateInstance(rotType, offsets[i * 3], offsets[i * 3 + 1], offsets[i * 3 + 2]);
                Field(names[i]).SetValue(clipboard, rot);
            }

            // Paste through the mod's own method (snapping, clamping, path-cost refresh), then give each PC its own
            // clipboard back.
            var previous = copiedSettingsField.GetValue(null);
            copiedSettingsField.SetValue(null, clipboard);
            try
            {
                pasteMethod.Invoke(comp, null);
            }
            finally
            {
                copiedSettingsField.SetValue(null, previous);
            }
        }

        private static FieldInfo Field(string name) => AccessTools.Field(clipboardType, name);
    }
}
