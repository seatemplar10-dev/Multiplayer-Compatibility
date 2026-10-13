using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Smarter Construction by Hultis</summary>
    /// <see href="https://github.com/dhultgren/rimworld-smarter-construction"/>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=2202185773"/>
    [MpCompatFor("dhultgren.smarterconstruction")]
    class SmarterConstruction
    {
        // Patch_WorkGiver_Scanner_GetPriority
        private static AccessTools.FieldRef<IDictionary> workGiverScannerCacheField;
        // EncloseThingsCache (a MapComponent since the 1.7 build; ClosedRegionDetector no longer holds a static cache)
        private static Type encloseThingsCacheType;
        private static FieldInfo encloseThingsCacheField;
        // PawnPositionCache (a MapComponent since the 1.7 build; the fields are per-map instance fields now)
        private static Type pawnPositionCacheType;
        private static FieldInfo positionsCacheField;
        private static FieldInfo lastPositionsCacheCleanupField;
        // WorkGiver_ConstructFinishFrames_JobOnThing
        private static AccessTools.FieldRef<int> constructFinishFrameCurrentTickField;

        public SmarterConstruction(ModContentPack mod)
        {
            var type = AccessTools.TypeByName("SmarterConstruction.Patches.Patch_WorkGiver_Scanner_GetPriority");

            // RNG
            {
                var field = AccessTools.Field(type, "random");

                field.SetValue(null, PatchingUtilities.RandRedirector.Instance);
            }

            // Cache
            {
                workGiverScannerCacheField = AccessTools.StaticFieldRefAccess<IDictionary>(AccessTools.DeclaredField(type, "cache"));

                encloseThingsCacheType = AccessTools.TypeByName("SmarterConstruction.Core.EncloseThingsCache");
                encloseThingsCacheField = AccessTools.DeclaredField(encloseThingsCacheType, "_cache");

                pawnPositionCacheType = AccessTools.TypeByName("SmarterConstruction.Core.PawnPositionCache");
                // Harmony seems to fail to create FieldRef<T> on those 2, so just gonna use FieldInfo instead.
                positionsCacheField = AccessTools.DeclaredField(pawnPositionCacheType, "_positionCache");
                lastPositionsCacheCleanupField = AccessTools.DeclaredField(pawnPositionCacheType, "_lastCacheCleanup");

                constructFinishFrameCurrentTickField = AccessTools.StaticFieldRefAccess<int>(
                    AccessTools.DeclaredField("SmarterConstruction.Patches.WorkGiver_ConstructFinishFrames_JobOnThing:currentTick"));

                MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(GameComponentUtility), nameof(GameComponentUtility.FinalizeInit)),
                    postfix: new HarmonyMethod(typeof(SmarterConstruction), nameof(ClearCache)));
            }
        }

        private static void ClearCache()
        {
            workGiverScannerCacheField().Clear();

            // The enclose and pawn position caches are per-map components now - clear them on every map.
            foreach (var map in Find.Maps)
            {
                var encloseCache = map.GetComponent(encloseThingsCacheType);
                if (encloseCache != null)
                    ((IDictionary)encloseThingsCacheField.GetValue(encloseCache)).Clear();

                var positionCache = map.GetComponent(pawnPositionCacheType);
                if (positionCache != null)
                {
                    ((IDictionary)positionsCacheField.GetValue(positionCache)).Clear();
                    // Reset the last cleanup tick field, as if we load an older save - it'll have to wait until the previously assigned tick to cleanup.
                    lastPositionsCacheCleanupField.SetValue(positionCache, 0);
                }
            }

            // Small chance it could cause issues when the loaded game is on the same tick as the tick stored by this field
            constructFinishFrameCurrentTickField() = -1;
            // Since we set the current tick to -1 (as opposed to 0), there's no chance unless something
            // is very wrong for `thingsUpdatedThisTick` field to be valid and kept by the mod. No need to clean it.
        }
    }
}
