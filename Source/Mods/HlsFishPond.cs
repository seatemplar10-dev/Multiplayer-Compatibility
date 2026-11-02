using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using UnityEngine;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>[HLS] Fish pond by Araneid</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3769179960"/>
    [MpCompatFor("LBY.FishPond")]
    public class HlsFishPond
    {
        // New-mods check (29 Oct):
        // RNG: Comp_FishPondAnimator.CompTick -> EnsureFramesLoaded draws Rand.Range twice, only on a PC where the per-PC
        // "enable animation" setting is on AND the frame textures load (none load on a headless arbiter). The frame is
        // cosmetic, so keep it out of the simulation's Rand stream.
        // Unsynced writes: the "allow fishing" toggle (Building_FishPond.GetGizmos lambda 1) and the ITab fish choice
        // (both rows assign Building_FishPond.selectedFishDef).
        // Per-PC mod settings in the simulation: fishDurationHours (job length), yieldMultiplier (stack count) and
        // enableModFishCompatibility (fish list). Multiplayer's join check compares the config files, so the PCs start equal (unless a joiner clicks "Connect anyway"); a mid-session
        // change in the settings window would split them. FishPondMod.DoSettingsWindowContents therefore shows a note instead of the
        // controls while Multiplayer runs (same idiom as CleaningAreaRelaunched remarks (2)). That also hides enableAnimation, which is
        // cosmetic. The window's close still calls WriteSettings: it rewrites the unchanged values, harmless.
        private static ISyncField selectedFishField;
        private static MethodInfo pondGetter;

        public HlsFishPond(ModContentPack mod)
        {
            PatchingUtilities.PatchPushPopRand("LBY.Comp_FishPondAnimator:EnsureFramesLoaded");

            MpCompat.RegisterLambdaMethod("LBY.Building_FishPond", "GetGizmos", 1);

            selectedFishField = MP.RegisterSyncField(AccessTools.TypeByName("LBY.Building_FishPond"), "selectedFishDef");
            var tabType = AccessTools.TypeByName("LBY.ITab_FishSelect");
            pondGetter = AccessTools.PropertyGetter(tabType, "Pond");
            MpCompat.harmony.Patch(AccessTools.Method(tabType, "FillTab"),
                prefix: new HarmonyMethod(typeof(HlsFishPond), nameof(PreFillTab)),
                finalizer: new HarmonyMethod(typeof(HlsFishPond), nameof(FinalFillTab)));

            var modType = AccessTools.TypeByName("LBY.FishPondMod");
            var settingsWindow = modType == null ? null : AccessTools.DeclaredMethod(modType, "DoSettingsWindowContents", new[] { typeof(Rect) });
            if (settingsWindow == null)
                Log.Warning("MPCompat :: HLS Fish pond: FishPondMod.DoSettingsWindowContents not found, the settings are not locked in Multiplayer");
            else
                MpCompat.harmony.Patch(settingsWindow, prefix: new HarmonyMethod(typeof(HlsFishPond), nameof(PreDoSettingsWindowContents)));
        }

        // The mod's settings (job length, yield, fish list) feed the simulation and are per PC. Locked while Multiplayer runs.
        private static bool PreDoSettingsWindowContents(Rect inRect)
        {
            if (!MP.IsInMultiplayer)
                return true;

            Widgets.Label(inRect, "Multiplayer: these settings feed the simulation. Change them in single player, then restart the session with the same config on every PC.");
            return false;
        }

        private static void PreFillTab(ITab __instance)
        {
            if (!MP.IsInMultiplayer)
                return;

            MP.WatchBegin();
            var pond = pondGetter.Invoke(__instance, null);
            if (pond != null)
                selectedFishField.Watch(pond);
        }

        private static Exception FinalFillTab(Exception __exception)
        {
            if (MP.IsInMultiplayer)
                MP.WatchEnd();
            return __exception;
        }
    }
}
