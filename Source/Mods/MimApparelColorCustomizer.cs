using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using UnityEngine;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>MIM - WH40k Core: the "Customize apparel colors" window (FIXES row 309)</summary>
    /// <remarks>
    /// The window (MIM40kFactions.Dialog_ApparelColorCustomizer) is opened from a float-menu entry of CompApparelColorCustomizer
    /// (a click, so it exists only on the clicker's PC; that stays). Everything the player does inside it is a preview kept in the
    /// dialog's own dictionaries (apparelColors, undo / redo stacks) and in PortraitsCache.Get(..., apparelColors) renders: local, never
    /// saved. The only writes to saved state are in ApplyColors (the "Apply all" button, then Close):
    ///   Thing.SetColor + Notify_ColorChanged on each changed worn apparel (CompColorable colour, saved),
    ///   pawn.Drawer.renderer.SetAllGraphicsDirty (visual only), pawn.mindState.Notify_OutfitChanged (saved nextApparelOptimizeTick).
    /// Run on the clicker's PC only, the pawn's apparel was another colour there than on the other PCs.
    /// This module (the mod is reached by name only, no reference to its dll):
    /// 1. ApplyColors: a prefix (UI time, in multiplayer, window open) reads the dialog's fields, keeps the original not-enough-dye
    ///    check and message local, collects (apparel thingIDNumber, colour) of every changed worn apparel and calls ONE synced static
    ///    method, SyncApplyColors(pawn, apparelIds, colors, dyeGate, dyeRequired), then returns false. The synced method looks the apparel
    ///    up on the pawn by id, re-checks the dye count, and does SetColor, Notify_ColorChanged, SetAllGraphicsDirty, Notify_OutfitChanged
    ///    on every PC. Apparel are sent as ids (not as Thing arguments) because a worn item is only resolvable through its spawned holder.
    /// 2. Dye: in the loaded build (1.6 folder, MIM40kFactions1.6.dll) ApplyColors only CHECKS the dye count
    ///    (colorChanger.Map.resourceCounter.GetCount(Dye) against totalDyeRequired) and consumes nothing. The one place that destroys dye
    ///    (TryApplyToSelected, non-preview branch: listerThings.ThingsOfDef(Dye).FirstOrDefault().SplitOff(1).Destroy()) has no caller:
    ///    all three call sites in DoWindowContents pass isPreview: true. That branch is blocked in multiplayer (an unsynced destroy would
    ///    desync) with a one-time warning, instead of being re-routed, because nothing can reach it from the UI.
    ///    The dye check is repeated inside the synced method (counts are simulation state, so all PCs decide the same); if it fails
    ///    nothing is applied and one line is logged.
    /// Left local on purpose: the preview (Preview buttons, Randomize, Reset, Undo / Redo, drag-copy, Reset all, Randomize all) edits
    /// only the dialog's dictionary and the portrait cache; Cancel (Close) writes nothing, so there is nothing to revert; PostClose
    /// (SetAllGraphicsDirty) is a cache reset. Rand.Value in the randomize buttons only fills the local dictionary.
    /// Supported build: MIM - WH40k Core, MIM40kFactions1.6.dll 616,960 bytes. Every member is looked up before anything is patched;
    /// if one is missing nothing is patched (the window then behaves as without the module) and the constructor throws.
    /// Status: [unverified] compiled only, not run in game.
    /// </remarks>
    [MpCompatFor("emitbreaker.MIM.WH40k.Core")]
    internal class MimApparelColorCustomizer
    {
        private const string LogPrefix = "[MPCompat - MIM Apparel Color Customizer] ";

        // dialog members, looked up by name
        private static FieldInfo pawnField, colorChangerField, apparelColorsField, originalColorsField, totalDyeRequiredField;

        // False until every lookup, registration and patch worked: the prefixes then leave the window as it is without the module.
        private static bool active;
        private static bool warnedDeadBranch;

        public MimApparelColorCustomizer(ModContentPack mod)
        {
            var dialogType = AccessTools.TypeByName("MIM40kFactions.Dialog_ApparelColorCustomizer");
            var applyColors = dialogType == null ? null : AccessTools.DeclaredMethod(dialogType, "ApplyColors", Type.EmptyTypes);
            var tryApply = dialogType == null ? null : AccessTools.DeclaredMethod(dialogType, "TryApplyToSelected", [typeof(Color), typeof(bool)]);
            if (dialogType != null)
            {
                pawnField = AccessTools.DeclaredField(dialogType, "pawn");
                colorChangerField = AccessTools.DeclaredField(dialogType, "colorChanger");
                apparelColorsField = AccessTools.DeclaredField(dialogType, "apparelColors");
                originalColorsField = AccessTools.DeclaredField(dialogType, "originalColors");
                totalDyeRequiredField = AccessTools.DeclaredField(dialogType, "totalDyeRequired");
            }

            if (dialogType == null || applyColors == null || tryApply == null || pawnField == null || colorChangerField == null
                || apparelColorsField == null || originalColorsField == null || totalDyeRequiredField == null
                || !typeof(IDictionary).IsAssignableFrom(apparelColorsField.FieldType)
                || !typeof(IDictionary).IsAssignableFrom(originalColorsField.FieldType)
                || pawnField.FieldType != typeof(Pawn) || !typeof(Thing).IsAssignableFrom(colorChangerField.FieldType)
                || totalDyeRequiredField.FieldType != typeof(int))
            {
                Log.Error($"{LogPrefix}this MIM - WH40k Core build does not have Dialog_ApparelColorCustomizer.ApplyColors / TryApplyToSelected " +
                          "and the fields pawn, colorChanger, apparelColors, originalColors, totalDyeRequired with the expected types: " +
                          "nothing patched, the colour window behaves as without the module.");
                throw new InvalidOperationException($"{LogPrefix}unsupported MIM - WH40k Core build");
            }

            MP.RegisterSyncMethod(typeof(MimApparelColorCustomizer), nameof(SyncApplyColors));
            MpCompat.harmony.Patch(applyColors, prefix: new HarmonyMethod(typeof(MimApparelColorCustomizer), nameof(PreApplyColors)));
            MpCompat.harmony.Patch(tryApply, prefix: new HarmonyMethod(typeof(MimApparelColorCustomizer), nameof(PreTryApplyToSelected)));
            active = true;
            Log.Message($"{LogPrefix}1 sync method registered, 2 patches installed, active={active}.");
        }

        // UI time with the window open (the "Apply all" button): collect, check the dye like the mod does, send one command.
        private static bool PreApplyColors(Window __instance)
        {
            if (!active || !MP.IsInMultiplayer || !MP.InInterface || MP.IsExecutingSyncCommand)
                return true;
            try
            {
                var pawn = pawnField.GetValue(__instance) as Pawn;
                if (pawn == null)
                    return true;

                // the mod's own check, local (a message on this PC only); the same check runs again inside the synced method
                var changer = colorChangerField.GetValue(__instance) as Thing;
                var dyeRequired = (int)totalDyeRequiredField.GetValue(__instance);
                Thing dyeGate = null;
                if (!DebugSettings.godMode && changer != null && changer.Spawned)
                {
                    dyeGate = changer;
                    if (changer.Map.resourceCounter.GetCount(ThingDefOf.Dye) < dyeRequired)
                    {
                        Messages.Message("EMWH_ApparelColorCustomizer_NotEnoughDye".Translate(), MessageTypeDefOf.RejectInput);
                        return false;
                    }
                }

                var current = (IDictionary)apparelColorsField.GetValue(__instance);
                var original = (IDictionary)originalColorsField.GetValue(__instance);
                var ids = new List<int>();
                var colors = new List<Color>();
                foreach (DictionaryEntry entry in current)
                {
                    if (entry.Key is not Thing apparel || !original.Contains(entry.Key))
                        continue;
                    var color = (Color)entry.Value;
                    if (!color.IndistinguishableFrom((Color)original[entry.Key]))
                    {
                        ids.Add(apparel.thingIDNumber);
                        colors.Add(color);
                    }
                }

                // sent even when nothing changed: the mod still calls Notify_OutfitChanged then (it writes a saved tick)
                SyncApplyColors(pawn, ids.ToArray(), colors.ToArray(), dyeGate, dyeGate == null ? 0 : dyeRequired);
            }
            catch (Exception e)
            {
                // never fall back to the local writes: that is the desync this module removes
                Log.Error($"{LogPrefix}could not send the colour change, nothing applied: {e}");
            }
            return false;
        }

        // The branch that destroys a dye (SplitOff(1).Destroy()) has no caller in the loaded build (see the remarks); if another mod ever
        // calls it, an unsynced destroy is worse than no change.
        private static bool PreTryApplyToSelected(bool isPreview)
        {
            if (!active || isPreview || !MP.IsInMultiplayer || MP.IsExecutingSyncCommand)
                return true;
            if (!warnedDeadBranch)
            {
                warnedDeadBranch = true;
                Log.Warning($"{LogPrefix}Dialog_ApparelColorCustomizer.TryApplyToSelected(isPreview: false) was called; it destroys a dye from window code " +
                            "and is blocked in multiplayer (use Apply all). Nothing was changed.");
            }
            return false;
        }

        // Runs on every PC with the same arguments. The apparel are found on the pawn by thingIDNumber.
        private static void SyncApplyColors(Pawn pawn, int[] apparelIds, Color[] colors, Thing dyeGate, int dyeRequired)
        {
            if (pawn?.apparel == null || apparelIds == null || colors == null || apparelIds.Length != colors.Length)
            {
                Log.Warning($"{LogPrefix}SyncApplyColors: bad arguments (pawn {pawn?.ThingID ?? "null"}), nothing applied.");
                return;
            }

            if (dyeGate is { Spawned: true } && dyeRequired > 0)
            {
                var dye = dyeGate.Map.resourceCounter.GetCount(ThingDefOf.Dye);
                if (dye < dyeRequired)
                {
                    Log.Message($"{LogPrefix}pawn {pawn.ThingID}: not enough dye ({dye} < {dyeRequired}), nothing applied.");
                    return;
                }
            }

            var applied = 0;
            var worn = pawn.apparel.WornApparel;
            for (var i = 0; i < apparelIds.Length; i++)
            {
                Apparel target = null;
                foreach (var apparel in worn)
                {
                    if (apparel.thingIDNumber == apparelIds[i])
                    {
                        target = apparel;
                        break;
                    }
                }

                if (target == null || target.Destroyed)
                {
                    Log.Message($"{LogPrefix}pawn {pawn.ThingID}: apparel id {apparelIds[i]} is no longer worn, skipped.");
                    continue;
                }

                // No "already that colour" skip: DrawColor is not always the comp colour (composite apparel), and the mod does not skip either.
                CompColorableUtility.SetColor(target, colors[i], true);
                target.Notify_ColorChanged();
                applied++;
            }

            pawn.Drawer.renderer.SetAllGraphicsDirty();
            pawn.mindState.Notify_OutfitChanged();
            Log.Message($"{LogPrefix}pawn {pawn.ThingID}: {applied}/{apparelIds.Length} apparel recoloured, tick={Find.TickManager.TicksGame}" +
                        (dyeGate is { Spawned: true } ? $", dye on map={dyeGate.Map.resourceCounter.GetCount(ThingDefOf.Dye)}" : "") + ".");
        }
    }
}
