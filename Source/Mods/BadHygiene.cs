using System;
using System.Collections.Generic;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Dubs Bad Hygiene and Dubs Bad Hygiene Lite by Dubwise</summary>
    /// <see href="https://github.com/Dubwise56/Dubs-Bad-Hygiene"/>
    /// <see href="https://github.com/Dubwise56/Dubs-Bad-Hygiene-Lite"/>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=836308268"/>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=2570319432"/>
    [MpCompatFor("Dubwise.DubsBadHygiene")]
    [MpCompatFor("Dubwise.DubsBadHygiene.Lite")]
    public class BadHygiene
    {
        private static AccessTools.FieldRef<Designator, ThingDef> removePlumbingRemovalModeField;
        private static AccessTools.FieldRef<Designator, bool> placeFertilizerAddingField;
        private static AccessTools.FieldRef<object, Zone_Growing> fertilizeToggleZone;
        private static Type hygieneComp;
        private static System.Reflection.FieldInfo fertilizeZonesField;
        internal static bool fertilizeTogglePatched;

        public BadHygiene(ModContentPack mod)
        {
            // RNG
            {
                var type = AccessTools.TypeByName("DubsBadHygiene.Comp_SaunaHeater");
                var methods = new[]
                {
                    AccessTools.Method(type, "SteamyNow"),
                    AccessTools.Method(type, "CompTick"),
                };

                PatchingUtilities.PatchPushPopRand(methods);
            }

            // Designators
            {
                var type = AccessTools.TypeByName("DubsBadHygiene.Designator_AreaPlaceFertilizer");
                placeFertilizerAddingField = AccessTools.FieldRefAccess<bool>(type, "Adding");
                MP.RegisterSyncWorker<Designator>(SyncFertilizerAreaDesignator, type, shouldConstruct: true);

                type = AccessTools.TypeByName("DubsBadHygiene.Designator_RemovePlumbing");
                removePlumbingRemovalModeField = AccessTools.FieldRefAccess<ThingDef>(type, "RemovalMode");
                MP.RegisterSyncWorker<Designator>(SyncRemovePlumbingDesignator, type, shouldConstruct: true);
            }

            // FIXES row 460 (26 Nov 2026): the 21 Nov update added a "Fertilize zone" toggle on growing zones
            // (HarmonyPatch_FertilizeZone, a Zone_Growing.GetGizmos postfix). Its toggleAction adds or removes the zone in
            // MapComponent_Hygiene.FertilizeZones on the clicking PC only, and WorkGiver_PlaceFertilizer reads that list.
            // The click now runs as one synced SyncedToggleFertilizeZone on every PC.
            if (LabOff.Row(460))
                Log.Message("[MPCompat - Dubs Bad Hygiene] row 460 switched off by the lab, fertilize zone toggle not synced");
            else
                PatchFertilizeToggle();
        }

        private static void PatchFertilizeToggle()
        {
            var patchType = AccessTools.TypeByName("DubsBadHygiene.HarmonyPatch_FertilizeZone");
            if (patchType == null)
            {
                Log.Message("[MPCompat - Dubs Bad Hygiene] row 460: no fertilize zone toggle in this version, nothing to sync");
                return;
            }
            var display = AccessTools.Inner(patchType, "<>c__DisplayClass1_0");
            var toggle = display == null ? null : AccessTools.Method(display, "<Postfix>b__1");
            var zoneField = display == null ? null : AccessTools.Field(display, "__instance");
            hygieneComp = AccessTools.TypeByName("DubsBadHygiene.MapComponent_Hygiene");
            fertilizeZonesField = hygieneComp == null ? null : AccessTools.Field(hygieneComp, "FertilizeZones");
            if (toggle == null || toggle.ReturnType != typeof(void) || toggle.GetParameters().Length != 0 || zoneField == null
                || zoneField.FieldType != typeof(Zone_Growing) || fertilizeZonesField == null || fertilizeZonesField.FieldType != typeof(List<Zone>))
            {
                Log.Error("[MPCompat - Dubs Bad Hygiene] row 460: fertilize zone toggle lambda or MapComponent_Hygiene.FertilizeZones not found, the toggle can desync");
                return;
            }
            fertilizeToggleZone = AccessTools.FieldRefAccess<object, Zone_Growing>(zoneField);
            MP.RegisterSyncMethod(typeof(BadHygiene), nameof(SyncedToggleFertilizeZone));
            MpCompat.harmony.Patch(toggle, prefix: new HarmonyMethod(typeof(BadHygiene), nameof(PreToggleFertilizeZone)));
            fertilizeTogglePatched = true;
            Log.Message("[MPCompat - Dubs Bad Hygiene] row 460: fertilize zone toggle synced");
        }

        internal static bool PreToggleFertilizeZone(object __instance)
        {
            if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand) return true;
            var zone = fertilizeToggleZone(__instance);
            if (zone == null) return true;
            SyncedToggleFertilizeZone(zone);
            return false;
        }

        internal static void SyncedToggleFertilizeZone(Zone_Growing zone)
        {
            var map = zone?.Map;
            if (map?.components == null) return;
            foreach (var comp in map.components)
            {
                if (comp == null || !hygieneComp.IsInstanceOfType(comp)) continue;
                var zones = (List<Zone>)fertilizeZonesField.GetValue(comp);
                if (zones == null) return;
                if (!zones.Remove(zone)) zones.Add(zone);
                return;
            }
        }

        private static void SyncRemovePlumbingDesignator(SyncWorker sync, ref Designator designator)
        {
            if (sync.isWriting)
                sync.Write(removePlumbingRemovalModeField(designator));
            else
                removePlumbingRemovalModeField(designator) = sync.Read<ThingDef>();
        }

        private static void SyncFertilizerAreaDesignator(SyncWorker sync, ref Designator designator)
        {
            if (sync.isWriting)
                sync.Write(placeFertilizerAddingField(designator));
            else
                placeFertilizerAddingField(designator) = sync.Read<bool>();
        }
    }
}
