using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Nice Bill Tab by Andromeda</summary>
    /// <remarks>
    /// FIXES row 143 (20 Oct 2026, lab R7): <c>StatMechanoidWorker.GetMechStats</c> builds a real mech pawn (with its gear,
    /// CE ammo and turret guns) per craftable mech kind to read its DPS. It runs once per process from
    /// <c>ModIntegration.LoadedGame</c> -> <c>StatRequestWorker.PostScan</c> (static <c>PostScanFinished</c>) and again from
    /// the bill UI for kinds not cached yet. The host ran it at its first load before hosting, a joiner after loading the join
    /// point: 334 thing ids (plus hediff and other ids) taken on the joiner only, so the next new thing got another id on
    /// each PC (R7: a wild MonitorLizard 31742 on the host, 32076 on the arbiter) and the map split 545 ticks after the join.
    /// The stat pawns are never spawned or saved, so every unique-id counter and the random state are put back after the
    /// call, on every PC and in single player too (the host's first load is not in a session yet).
    /// FIXES row 159 (21 Oct 2026, live save-state checksum run SS-A1): the stat pawns also leave state in other mods on the
    /// PC that made them - Big and Small (a saved <c>BS_scribedCache</c> entry per pawn, and the static caches / queues its
    /// refresh pass walks, so DelayedUpdate ran on ghost pawns on one PC) and PsiTech (a tracker per pawn in the manager's
    /// <c>trackers</c>, and <c>nextTrackerId</c> +1 each: the tracker id sets its refresh phase, so every later tracker got
    /// another id and phase on that PC). The pawns made inside the call are collected and removed from both mods afterwards,
    /// and <c>nextTrackerId</c> is put back. Each mod is optional (reflection, skipped when not loaded).
    /// </remarks>
    [MpCompatFor("Andromeda.NiceBillTab")]
    internal class NiceBillTab
    {
        private sealed class State
        {
            public int[] ids;
            public int psiNextTrackerId = int.MinValue;
        }

        private static FieldInfo[] idFields;
        private static int depth;
        private static bool logged, otherModsLogged, otherModsResolved;
        private static readonly List<Pawn> ghosts = new();

        // PsiTech
        private static Type psiManagerType;
        private static FieldInfo psiNextTrackerIdField, psiTrackersField;

        // Big and Small
        private static PropertyInfo bsScribedCacheProp, bsDictCacheProp;
        private static FieldInfo bsJunkCacheField, bsRefreshQueueField, bsCurrentlyQueuedField, bsSchedulePostField, bsScheduleFullField, bsCachePawnField;

        public NiceBillTab(ModContentPack mod)
        {
            idFields = AccessTools.GetDeclaredFields(typeof(UniqueIDsManager))
                .Where(f => !f.IsStatic && f.FieldType == typeof(int))
                .ToArray();

            var getMechStats = AccessTools.DeclaredMethod("NiceBillTab.StatMechanoidWorker:GetMechStats")
                ?? throw new MissingMethodException("NiceBillTab.StatMechanoidWorker", "GetMechStats");
            MpCompat.harmony.Patch(getMechStats,
                prefix: new HarmonyMethod(typeof(NiceBillTab), nameof(SaveIds)),
                finalizer: new HarmonyMethod(typeof(NiceBillTab), nameof(RestoreIds)));

            var generatePawn = AccessTools.DeclaredMethod(typeof(PawnGenerator), nameof(PawnGenerator.GeneratePawn), [typeof(PawnGenerationRequest)])
                ?? throw new MissingMethodException(nameof(PawnGenerator), "GeneratePawn(PawnGenerationRequest)");
            MpCompat.harmony.Patch(generatePawn, postfix: new HarmonyMethod(typeof(NiceBillTab), nameof(CollectGhost)));
        }

        private static void CollectGhost(Pawn __result)
        {
            if (depth > 0 && __result != null)
                ghosts.Add(__result);
        }

        private static void SaveIds(out State __state)
        {
            __state = null;
            if (depth++ > 0) return;
            ghosts.Clear();
            var ids = Current.Game?.uniqueIDsManager;
            if (ids == null) return;
            __state = new State { ids = idFields.Select(f => (int)f.GetValue(ids)).ToArray() };
            ResolveOtherMods();
            var psiManager = PsiManager();
            if (psiManager != null)
                __state.psiNextTrackerId = (int)psiNextTrackerIdField.GetValue(psiManager);
            Rand.PushState();
        }

        private static void RestoreIds(State __state)
        {
            depth--;
            if (__state == null) return;
            Rand.PopState();
            var ids = Current.Game?.uniqueIDsManager;
            if (ids == null) return;
            int thingIdsTaken = 0;
            for (int i = 0; i < idFields.Length; i++)
            {
                if (idFields[i].Name == "nextThingID")
                    thingIdsTaken = (int)idFields[i].GetValue(ids) - __state.ids[i];
                idFields[i].SetValue(ids, __state.ids[i]);
            }

            if (!logged && thingIdsTaken > 0)
            {
                logged = true;
                Log.Message($"MPCompat - Nice Bill Tab: a mech stat pawn took {thingIdsTaken} thing id(s); every unique-id counter and the random state were put back (FIXES row 143)");
            }

            if (ghosts.Count == 0) return;
            try
            {
                var ghostSet = new HashSet<Pawn>(ghosts, ReferenceEqualityComparer<Pawn>.Instance);
                int psiTrackers = 0, psiIds = 0, bsEntries = 0;
                var psiManager = PsiManager();
                if (psiManager != null)
                {
                    if (psiTrackersField.GetValue(psiManager) is IDictionary trackers)
                        psiTrackers = RemoveKeys(trackers, ghostSet);
                    if (__state.psiNextTrackerId != int.MinValue)
                    {
                        psiIds = (int)psiNextTrackerIdField.GetValue(psiManager) - __state.psiNextTrackerId;
                        psiNextTrackerIdField.SetValue(psiManager, __state.psiNextTrackerId);
                    }
                }

                if (bsCachePawnField != null)
                    bsEntries = RemoveBigAndSmall(ghostSet);

                if (!otherModsLogged && (psiTrackers > 0 || psiIds != 0 || bsEntries > 0))
                {
                    otherModsLogged = true;
                    Log.Message($"MPCompat - Nice Bill Tab: {ghosts.Count} mech stat pawn(s) removed from other mods: PsiTech {psiTrackers} tracker(s), nextTrackerId put back by {psiIds}; Big and Small {bsEntries} cache / queue entr(ies) (FIXES row 159)");
                }
            }
            catch (Exception e)
            {
                if (!otherModsLogged)
                {
                    otherModsLogged = true;
                    Log.Error($"MPCompat - Nice Bill Tab: removing the mech stat pawns from other mods failed (FIXES row 159): {e}");
                }
            }
            finally
            {
                ghosts.Clear();
            }
        }

        private static void ResolveOtherMods()
        {
            if (otherModsResolved) return;
            otherModsResolved = true;

            psiManagerType = AccessTools.TypeByName("PsiTech.Utility.PsiTechManager");
            if (psiManagerType != null)
            {
                psiNextTrackerIdField = AccessTools.Field(psiManagerType, "nextTrackerId");
                psiTrackersField = AccessTools.Field(psiManagerType, "trackers");
                if (psiNextTrackerIdField?.FieldType != typeof(int) || psiTrackersField == null)
                {
                    Log.Error("MPCompat - Nice Bill Tab: PsiTech.Utility.PsiTechManager has no int nextTrackerId / trackers - PsiTech part of FIXES row 159 off");
                    psiManagerType = null;
                }
            }

            var bsCacheType = AccessTools.TypeByName("BigAndSmall.BSCache");
            var bsComponentType = AccessTools.TypeByName("BigAndSmall.BigAndSmallCache");
            var dictCacheOpen = AccessTools.TypeByName("BigAndSmall.DictCache`2");
            if (bsCacheType != null && bsComponentType != null && dictCacheOpen != null)
            {
                var dictCacheType = dictCacheOpen.MakeGenericType(typeof(Pawn), bsCacheType);
                bsCachePawnField = AccessTools.Field(bsCacheType, "pawn");
                bsScribedCacheProp = AccessTools.Property(bsComponentType, "ScribedCache");
                bsDictCacheProp = AccessTools.Property(dictCacheType, "Cache");
                bsJunkCacheField = AccessTools.Field(dictCacheType, "JunkCache");
                bsRefreshQueueField = AccessTools.Field(bsComponentType, "refreshQueue");
                bsCurrentlyQueuedField = AccessTools.Field(bsComponentType, "currentlyQueued");
                bsSchedulePostField = AccessTools.Field(bsComponentType, "schedulePostUpdate");
                bsScheduleFullField = AccessTools.Field(bsComponentType, "scheduleFullUpdate");
                if (bsCachePawnField == null || bsScribedCacheProp == null || bsDictCacheProp == null || bsJunkCacheField == null
                    || bsRefreshQueueField == null || bsCurrentlyQueuedField == null || bsSchedulePostField == null || bsScheduleFullField == null)
                {
                    Log.Error("MPCompat - Nice Bill Tab: a Big and Small member is missing - Big and Small part of FIXES row 159 off");
                    bsCachePawnField = null;
                }
            }
        }

        private static object PsiManager()
        {
            if (psiManagerType == null || Current.Game?.components == null) return null;
            foreach (var component in Current.Game.components)
                if (psiManagerType.IsInstanceOfType(component))
                    return component;
            return null;
        }

        private static int RemoveKeys(IDictionary dict, HashSet<Pawn> ghostSet)
        {
            var remove = dict.Keys.OfType<Pawn>().Where(ghostSet.Contains).ToList();
            foreach (var pawn in remove)
                dict.Remove(pawn);
            return remove.Count;
        }

        private static bool IsGhostCache(object cache, HashSet<Pawn> ghostSet) =>
            cache != null && bsCachePawnField.GetValue(cache) is Pawn p && ghostSet.Contains(p);

        private static int RemoveFromSet(object set, HashSet<Pawn> ghostSet)
        {
            if (set == null) return 0;
            var remove = ((IEnumerable)set).Cast<object>().Where(c => IsGhostCache(c, ghostSet)).ToList();
            var removeMethod = set.GetType().GetMethod("Remove");
            foreach (var cache in remove)
                removeMethod.Invoke(set, [cache]);
            return remove.Count;
        }

        private static int RemoveBigAndSmall(HashSet<Pawn> ghostSet)
        {
            int n = 0;
            n += RemoveFromSet(bsScribedCacheProp.GetValue(null), ghostSet);
            if (bsDictCacheProp.GetValue(null) is IDictionary cache) n += RemoveKeys(cache, ghostSet);
            if (bsJunkCacheField.GetValue(null) is IDictionary junk) n += RemoveKeys(junk, ghostSet);
            n += RemoveFromSet(bsCurrentlyQueuedField.GetValue(null), ghostSet);
            foreach (var field in new[] { bsSchedulePostField, bsScheduleFullField })
                if (field.GetValue(null) is IDictionary byTick)
                    foreach (var set in byTick.Values)
                        n += RemoveFromSet(set, ghostSet);
            if (bsRefreshQueueField.GetValue(null) is Queue<Pawn> queue)
            {
                var keep = queue.Where(p => !ghostSet.Contains(p)).ToList();
                n += queue.Count - keep.Count;
                queue.Clear();
                foreach (var p in keep) queue.Enqueue(p);
            }
            return n;
        }

        private sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T> where T : class
        {
            public static readonly ReferenceEqualityComparer<T> Instance = new();
            public bool Equals(T x, T y) => ReferenceEquals(x, y);
            public int GetHashCode(T obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
