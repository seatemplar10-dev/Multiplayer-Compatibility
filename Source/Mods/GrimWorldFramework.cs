using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using UnityEngine;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>GrimWorld 40,000 - Framework (GW4KArmor tri-colour paint)</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3119805903"/>
    /// <remarks>
    /// Paint state lives in GW4KArmor.Comp_TriColorMask (MaskIndex, colorOne/Two/Three; saved with the game) and,
    /// through the ColorOne setter, in the vanilla CompColorable of the same thing (CompColorable.SetColor), which the
    /// Ideology "wearing favourite / ideoligion colour" mood thoughts read. Two UI paths write it with no sync:
    /// - Gizmo_Paintable.GizmoOnGUI paste button -> Comp_TriColorMask.Paste(Color[])        => sync method
    /// - Window_ItemPainter "Save" button (DrawMainButtons, drawn from DoWindowContents)    => watched sync fields
    ///   (the window only opens once GW4K's mask material loads, i.e. on 1.7 with the Linux bundle fix)
    /// The ColorOne setter writes CompColorable only when the comp's private colorComp cache is set. That cache is
    /// never saved and only PostSpawnSetup fills it, so a worn item that was loaded (never spawned in this process)
    /// has it null while an item spawned this session has it set: the same synced Paste (or a Notify_Equipped default
    /// palette) would then change CompColorable on one machine and not on another. The set_ColorOne prefix fills the
    /// cache the way PostSpawnSetup does before every write in multiplayer, so the setter acts the same everywhere.
    /// Not synced on purpose: the window's "Save Palette" (PaletteStorage game component; presets only, never read by
    /// the simulation). A palette saved on a client is lost when the host's save is reloaded.
    /// Command_ActionWithPaintOptions (Copy/Paste float menu) is never instantiated by GW4KArmor, so it is not patched.
    /// Lab evidence: every machine logs one "MPCompat :: GrimWorld paint ..." line when the synced Paste runs and when a
    /// watched paint field is applied. A headless client has no screen, so its log is the only proof the command reached it.
    /// </remarks>
    [MpCompatFor("Grimworld.Framework")]
    internal class GrimWorldFramework
    {
        private static ISyncField[] paintFields;
        private static ISyncField[] colorableFields;
        private static Type compType;
        private static MethodInfo copyMethod;
        private static FieldInfo maskIndexField;

        public GrimWorldFramework(ModContentPack mod)
        {
            compType = AccessTools.TypeByName("GW4KArmor.Comp_TriColorMask");
            var windowType = AccessTools.TypeByName("GW4KArmor.UI.Window_ItemPainter");
            copyMethod = AccessTools.DeclaredMethod(compType, "Copy");
            maskIndexField = AccessTools.DeclaredField(compType, "MaskIndex");

            // Every write through ColorOne (synced Paste, window Save, Notify_Equipped default palette) sees the same
            // colorComp on every machine.
            MpCompat.harmony.Patch(AccessTools.DeclaredPropertySetter(compType, "ColorOne"),
                prefix: new HarmonyMethod(typeof(GrimWorldFramework), nameof(PreSetColorOne)));

            // Gizmo paste: one call carries all three colours (Color[] is syncable: Color worker + array support).
            MP.RegisterSyncMethod(compType, "Paste");
            // Log line on every machine when the synced Paste runs (see remarks). The guard skips the click-time call,
            // which Multiplayer cancels and sends as a command.
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(compType, "Paste"),
                postfix: new HarmonyMethod(typeof(GrimWorldFramework), nameof(PostPaste)));

            // Window "Save": it sets MaskIndex + the 3 colours (ColorOne also calls CompColorable.SetColor), then MarkDirty.
            // Watched fields are reverted locally and applied on every client in the same tick; refresh graphics after.
            paintFields = new[] { "MaskIndex", "colorOne", "colorTwo", "colorThree" }
                .Select(f => MP.RegisterSyncField(compType, f).PostApply((target, value) => AfterFieldSync(target, value, f)))
                .ToArray();
            colorableFields = new[] { "color", "active", "desiredColor" }
                .Select(f => MP.RegisterSyncField(typeof(CompColorable), f).PostApply((target, value) => AfterFieldSync(target, value, f)))
                .ToArray();

            // DoWindowContents is virtual (called through Window), so no inlining question; a finalizer always ends the watch.
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(windowType, "DoWindowContents"),
                prefix: new HarmonyMethod(typeof(GrimWorldFramework), nameof(PreDoWindowContents)),
                finalizer: new HarmonyMethod(typeof(GrimWorldFramework), nameof(FinalizeDoWindowContents)));
        }

        // Same lookup as Comp_TriColorMask.PostSpawnSetup ("colorComp = parent.GetComp<CompColorable>()"). Multiplayer
        // only, so single player keeps GW4K's own behaviour. Every machine in a session is in multiplayer, so the result
        // is the same everywhere.
        private static void PreSetColorOne(ThingComp __instance, ref CompColorable ___colorComp)
        {
            if (MP.IsInMultiplayer)
                FillColorComp(__instance, ref ___colorComp);
        }

        internal static void FillColorComp(ThingComp comp, ref CompColorable colorComp)
        {
            if (colorComp == null)
                colorComp = comp.parent?.GetComp<CompColorable>();
        }

        // ___comp = Window_ItemPainter's private readonly Comp_TriColorMask comp (ThingComp = base class, allowed by Harmony 3).
        private static void PreDoWindowContents(ThingComp ___comp, ref bool __state)
        {
            if (!MP.IsInMultiplayer || ___comp == null)
                return;

            MP.WatchBegin();
            __state = true;
            foreach (var field in paintFields)
                field.Watch(___comp);

            var colorable = ___comp.parent?.GetComp<CompColorable>();
            if (colorable != null)
                foreach (var field in colorableFields)
                    field.Watch(colorable);
        }

        private static void FinalizeDoWindowContents(bool __state)
        {
            if (__state)
                MP.WatchEnd();
        }

        // Same effect as Comp_TriColorMask.MarkDirty(): parent.Notify_ColorChanged() (graphic caches; for worn apparel
        // also Pawn_ApparelTracker.Notify_ApparelChanged dirty flags). Runs inside the synced field command on every machine.
        private static void RefreshGraphics(object target, object value)
            => (target as ThingComp)?.parent?.Notify_ColorChanged();

        private static void AfterFieldSync(object target, object value, string field)
        {
            RefreshGraphics(target, value);
            if (target is ThingComp { parent: { } parent } comp)
                Log.Message(PaintLine($"synced {comp.GetType().Name}.{field}", PaintCompOf(parent), parent));
        }

        private static void PostPaste(ThingComp __instance)
        {
            if (MP.IsInMultiplayer && MP.IsExecutingSyncCommand)
                Log.Message(PaintLine("Paste", __instance, __instance?.parent));
        }

        private static ThingComp PaintCompOf(ThingWithComps parent)
            => parent.AllComps?.FirstOrDefault(c => compType != null && compType.IsInstanceOfType(c));

        // One line, the same on every machine for the same command: thing id, game tick, the 3 GW4K colours, mask and
        // the vanilla CompColorable state (shows whether the colorComp fill reached it). Reads only; no RNG, no writes.
        internal static string PaintLine(string what, ThingComp comp, ThingWithComps parent)
        {
            var colors = comp == null ? null : copyMethod?.Invoke(comp, null) as Color[];
            var colorable = parent?.GetComp<CompColorable>();
            var colorableText = colorable == null ? "none" : colorable.Active ? "active " + Hex(colorable.Color) : "inactive";
            return $"MPCompat :: GrimWorld paint {what} on {parent?.ThingID ?? "null"} tick={Current.Game?.tickManager?.TicksGame}: " +
                   $"A={Hex(colors, 0)} B={Hex(colors, 1)} C={Hex(colors, 2)} mask={(comp == null ? "?" : maskIndexField?.GetValue(comp))} " +
                   $"CompColorable={colorableText}";
        }

        private static string Hex(Color[] colors, int index)
            => colors != null && index < colors.Length ? Hex(colors[index]) : "?";

        private static string Hex(Color c) => "#" + Byte(c.r) + Byte(c.g) + Byte(c.b);

        private static string Byte(float v) => Mathf.RoundToInt(Mathf.Clamp01(v) * 255f).ToString("X2");
    }
}
