using System.Linq;
using HarmonyLib;
using Multiplayer.API;
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
    /// </remarks>
    [MpCompatFor("Grimworld.Framework")]
    internal class GrimWorldFramework
    {
        private static ISyncField[] paintFields;
        private static ISyncField[] colorableFields;

        public GrimWorldFramework(ModContentPack mod)
        {
            var compType = AccessTools.TypeByName("GW4KArmor.Comp_TriColorMask");
            var windowType = AccessTools.TypeByName("GW4KArmor.UI.Window_ItemPainter");

            // Every write through ColorOne (synced Paste, window Save, Notify_Equipped default palette) sees the same
            // colorComp on every machine.
            MpCompat.harmony.Patch(AccessTools.DeclaredPropertySetter(compType, "ColorOne"),
                prefix: new HarmonyMethod(typeof(GrimWorldFramework), nameof(PreSetColorOne)));

            // Gizmo paste: one call carries all three colours (Color[] is syncable: Color worker + array support).
            MP.RegisterSyncMethod(compType, "Paste");

            // Window "Save": it sets MaskIndex + the 3 colours (ColorOne also calls CompColorable.SetColor), then MarkDirty.
            // Watched fields are reverted locally and applied on every client in the same tick; refresh graphics after.
            paintFields = new[] { "MaskIndex", "colorOne", "colorTwo", "colorThree" }
                .Select(f => MP.RegisterSyncField(compType, f).PostApply(RefreshGraphics))
                .ToArray();
            colorableFields = new[] { "color", "active", "desiredColor" }
                .Select(f => MP.RegisterSyncField(typeof(CompColorable), f).PostApply(RefreshGraphics))
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
    }
}
