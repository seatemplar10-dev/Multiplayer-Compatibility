using System;
using System.Collections;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Hospitality by Orion</summary>
    /// <see href="https://github.com/OrionFive/Hospitality"/>
    /// <see href="https://steamcommunity.com/workshop/filedetails/?id=753498552"/>
    [MpCompatFor("Orion.Hospitality")]
    public class Hospitality
    {
        // CompUtility
        private static AccessTools.FieldRef<object> guestCompsField;
        // RelationUtility
        private static AccessTools.FieldRef<object> relationCacheField;
        // GenericUtility
        private static AccessTools.FieldRef<object> travelDaysCacheField;
        // GuestUtility
        private static AccessTools.FieldRef<object> relatedCacheField;
        private static AccessTools.FieldRef<int> relatedCacheNextClearTickField;

        // CompUtility
        private static FastInvokeHandler compGuestMethod;
        // Multiplayer
        private static AccessTools.FieldRef<ISyncField[]> syncFields;

        public Hospitality(ModContentPack mod)
        {
            // TODO: Check Hospitality.Patches.MinifiedThing_Patch.DrawAt:Prefix, specifically the HospitalityModBase.RegisterTickAction call

            LongEventHandler.ExecuteWhenFinished(LatePatch);

            // Alerts
            {
                MpCompat.harmony.Patch(AccessTools.DeclaredMethod("Hospitality.Alert_Guest:GetReport"),
                    prefix: new HarmonyMethod(typeof(Hospitality), nameof(NoAlertInMp)));
            }

            // Gizmos
            {
                MP.RegisterSyncMethod(AccessTools.DeclaredMethod("Hospitality.CompVendingMachine:SetEmptyThreshold"));
            }
        }

        private static void LatePatch()
        {
            // Cache
            {
                var type = AccessTools.TypeByName("Hospitality.Utilities.CompUtility");
                guestCompsField = CacheRef(type, "guestComps");

                type = AccessTools.TypeByName("Hospitality.Utilities.RelationUtility");
                relationCacheField = CacheRef(type, "relationCache");

                type = AccessTools.TypeByName("Hospitality.Utilities.GenericUtility");
                travelDaysCacheField = CacheRef(type, "travelDaysCache", "travelTicksCache");

                type = AccessTools.TypeByName("Hospitality.Utilities.GuestUtility");
                relatedCacheField = CacheRef(type, "relatedCache");
                relatedCacheNextClearTickField = AccessTools.StaticFieldRefAccess<int>(AccessTools.DeclaredField(type, "relatedCacheNextClearTick"));

                MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(GameComponentUtility), nameof(GameComponentUtility.FinalizeInit)),
                    postfix: new HarmonyMethod(typeof(Hospitality), nameof(ClearCache)));
            }

            // Sync fields
            {
                // Hospitality starts watching the fields way too late (inside of PawnColumnWorker_Relationship.DoCell),
                // causing it to fail syncing in some situations. For example, shift-clicking on the header of make friends/entertain guests checkboxes.
                syncFields = AccessTools.StaticFieldRefAccess<ISyncField[]>(AccessTools.DeclaredField("Hospitality.Multiplayer:guestFields"));

                // Basically an extension method to pawn.GetComp<CompGuest>, but with extra caching and null.
                compGuestMethod = MethodInvoker.GetHandler(AccessTools.DeclaredMethod("Hospitality.Utilities.CompUtility:CompGuest"));

                // Hospitality starts watching in this DoHeader is called before DoCell, so it should be good enough of a place to put it in.
                var type = AccessTools.TypeByName("Hospitality.MainTab.PawnColumnWorker_Relationship");

                MpCompat.harmony.Patch(AccessTools.DeclaredMethod(type, nameof(PawnColumnWorker.DoHeader)),
                    prefix: new HarmonyMethod(typeof(Hospitality), nameof(PreHeader)));
                MpCompat.harmony.Patch(AccessTools.DeclaredMethod(type, nameof(PawnColumnWorker.DoCell)),
                    prefix: new HarmonyMethod(typeof(Hospitality), nameof(PreCell)),
                    finalizer: new HarmonyMethod(typeof(Hospitality), nameof(PostCell)));
            }
        }

        private static bool NoAlertInMp(ref AlertReport __result)
        {
            if (!MP.IsInMultiplayer)
                return true;

            __result = AlertReport.Inactive;
            return false;
        }

        // Hospitality 1.2.0 keeps some caches in a ConditionalWeakTable (guestComps, relationCache), older versions in an IDictionary,
        // and renamed travelDaysCache to travelTicksCache. The first name that exists is used, a missing field is skipped.
        private static AccessTools.FieldRef<object> CacheRef(Type type, params string[] names)
        {
            foreach (var name in names)
            {
                var field = AccessTools.DeclaredField(type, name);
                if (field != null) return AccessTools.StaticFieldRefAccess<object>(field);
            }
            Log.Warning($"[MPCompat] Hospitality: {type?.Name}.{names[0]} not found, that cache will not be cleared on load");
            return null;
        }

        private static void ClearCacheObject(AccessTools.FieldRef<object> field)
        {
            var cache = field?.Invoke();
            if (cache is IDictionary dict) dict.Clear();
            else if (cache != null)
            {
                var clear = AccessTools.DeclaredMethod(cache.GetType(), "Clear", Type.EmptyTypes);
                if (clear != null) clear.Invoke(cache, null);
                else field() = Activator.CreateInstance(cache.GetType());
            }
        }

        private static void ClearCache()
        {
            // CompUtility
            ClearCacheObject(guestCompsField);

            // RelationUtility
            ClearCacheObject(relationCacheField);

            // GenericUtility
            // This one probably doesn't need clearing, but it will just keep on filling up with
            // more data each time someone desyncs, or a game is loaded, etc.
            ClearCacheObject(travelDaysCacheField);

            // GuestUtility
            ClearCacheObject(relatedCacheField);
            relatedCacheNextClearTickField() = 0;

            // Hospitality.Patches.ThingFilter_Patch.Allows:bedNameToThing doesn't look like it needs clearing
            // Hospitality.Utilities.ThoughtResultCache clears the caches on load (but not when hosting new game).
        }

        // When making the PR to Hospitality, put it after MP WatchBegin in Hospitality.MainTab.MainTabWindow_Hospitality:DoWindowContents.
        // Also remember to remove the sync field watch from Hospitality.MainTab.PawnColumnWorker_Relationship:DoCell.
        private static void PreHeader(PawnTable table)
        {
            if (!MP.IsInMultiplayer)
                return;
            ref var fields = ref syncFields();
            if (fields == null)
                return;

            foreach (var pawn in table.PawnsListForReading)
            {
                var comp = compGuestMethod(null, pawn);

                if (comp != null)
                {
                    foreach (var field in fields)
                        field.Watch(comp);
                }
            }
        }

        // Stop watching sync fields for the second time, as it breaks syncing area syncing.
        private static void PreCell(ref ISyncField[] __state)
        {
            if (MP.IsInMultiplayer)
            {
                // Hospitality doesn't sync those if the field is null, so temporarily change it to null
                __state = syncFields();
                syncFields() = null;
            }
        }

        private static void PostCell(ISyncField[] __state)
        {
            // Restore data to the state it was beforehand
            if (__state != null)
                syncFields() = __state;
        }
    }
}