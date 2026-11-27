using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Map Preview by m00nl1ght (FIXES row 513)</summary>
    /// <remarks>
    /// Two hazards, both read in the 1.6 Lunar/Components MapPreview.dll and MapPreviewMod.dll that our co-op copy loads.
    /// 1. Seed reroll: MapPreviewToolbar and MapSeedRerollWindow call SeedRerollData.Commit / Reset, which write the saved WorldComponent
    ///    dictionary _mapSeeds on the clicking PC only; the real map generation reads it (Patch_Verse_MapGenerator AdjustSeedIfNeeded).
    ///    Fix: in Multiplayer the final Commit / Reset sends ONE synced static call that applies the write on every PC. The scrub call
    ///    (notifyWorldChanged false, one per thumbnail) writes only a local overlay that TryGet returns to the preview worker thread, never
    ///    to the main thread, so real map generation never reads it.
    /// 2. MapPreviewGenerator's worker thread calls TickManager.Pause, sets TickManager.CurTimeSpeed back, SustainerManager.EndAllInMap and
    ///    TickManager.RemoveAllFromMap on the live objects. Fix: those four are skipped when called from the preview worker thread in
    ///    Multiplayer. The worker is recognised by a thread static set in a prefix of MapPreviewGenerator.DoThreadWork.
    /// Status: [unverified] - zero-boot D2 test only, not run in game.
    /// </remarks>
    [MpCompatFor("m00nl1ght.MapPreview")]
    public class MapPreviewCompat
    {
        private const string LogPrefix = "[MPCompat - Map Preview] ";
        [ThreadStatic] private static bool isPreviewWorker;
        private static readonly Dictionary<int, int> overlay = new Dictionary<int, int>();
        private static Type dataType;
        private static MethodInfo getFor;
        private static MethodInfo commit3;
        private static MethodInfo reset2;

        public MapPreviewCompat(ModContentPack mod) => Patch();

        internal static void Patch()
        {
            if (LabOff.Row(513))
            {
                Log.Message(LogPrefix + "row 513 switched off by the lab, seed reroll not synced and the preview worker not kept off the live TickManager");
                return;
            }
            dataType = AccessTools.TypeByName("MapPreview.SeedRerollData");
            getFor = dataType == null ? null : AccessTools.DeclaredMethod(dataType, "GetFor");
            commit3 = dataType == null ? null : AccessTools.DeclaredMethod(dataType, "Commit", new[] { typeof(int), typeof(int), typeof(bool) });
            reset2 = dataType == null ? null : AccessTools.DeclaredMethod(dataType, "Reset", new[] { typeof(int), typeof(bool) });
            var tryGet = dataType == null ? null : AccessTools.DeclaredMethod(dataType, "TryGet");
            var work = AccessTools.Method("MapPreview.MapPreviewGenerator:DoThreadWork");
            if (commit3 == null || reset2 == null || tryGet == null || getFor == null || work == null)
            {
                Log.Warning(LogPrefix + "row 513: a Map Preview method was not found, nothing patched");
                return;
            }
            MP.RegisterSyncMethod(typeof(MapPreviewCompat), nameof(SyncedCommit));
            MP.RegisterSyncMethod(typeof(MapPreviewCompat), nameof(SyncedReset));
            MpCompat.harmony.Patch(commit3, prefix: new HarmonyMethod(typeof(MapPreviewCompat), nameof(PreCommit)));
            MpCompat.harmony.Patch(reset2, prefix: new HarmonyMethod(typeof(MapPreviewCompat), nameof(PreReset)));
            MpCompat.harmony.Patch(tryGet, prefix: new HarmonyMethod(typeof(MapPreviewCompat), nameof(PreTryGet)));
            MpCompat.harmony.Patch(work, prefix: new HarmonyMethod(typeof(MapPreviewCompat), nameof(PreWorker)));
            var pause = AccessTools.DeclaredMethod(typeof(TickManager), nameof(TickManager.Pause));
            var speedSet = AccessTools.PropertySetter(typeof(TickManager), nameof(TickManager.CurTimeSpeed));
            var removeAll = AccessTools.DeclaredMethod(typeof(TickManager), nameof(TickManager.RemoveAllFromMap));
            var endAll = AccessTools.DeclaredMethod(typeof(Verse.Sound.SustainerManager), "EndAllInMap");
            foreach (var m in new[] { pause, speedSet, removeAll, endAll })
                if (m != null)
                    MpCompat.harmony.Patch(m, prefix: new HarmonyMethod(typeof(MapPreviewCompat), nameof(SkipOnPreviewWorker)));
            Log.Message(LogPrefix + "row 513: Map Preview seed reroll synced, preview worker kept off the live TickManager");
        }

        private static bool PreWorker()
        {
            isPreviewWorker = true;
            return true;
        }

        private static bool SkipOnPreviewWorker() => !(MP.IsInMultiplayer && isPreviewWorker);

        private static object Data() => getFor.Invoke(null, new object[] { Find.World });

        private static bool PreCommit(int tileId, int seed, bool notifyWorldChanged)
        {
            if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand) return true;
            if (!notifyWorldChanged)
            {
                lock (overlay) overlay[tileId] = seed;
                return false;
            }
            SyncedCommit(tileId, seed);
            return false;
        }

        private static bool PreReset(int tileId, bool notifyWorldChanged)
        {
            if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand) return true;
            if (!notifyWorldChanged)
            {
                lock (overlay) overlay.Remove(tileId);
                return false;
            }
            SyncedReset(tileId);
            return false;
        }

        private static void SyncedCommit(int tileId, int seed)
        {
            lock (overlay) overlay.Remove(tileId);
            commit3.Invoke(Data(), new object[] { tileId, seed, true });
        }

        private static void SyncedReset(int tileId)
        {
            lock (overlay) overlay.Remove(tileId);
            reset2.Invoke(Data(), new object[] { tileId, true });
        }

        // The preview worker sees the scrub seed; the main thread (real map generation) never does.
        private static bool PreTryGet(int tileId, ref int seed, ref bool __result)
        {
            if (!MP.IsInMultiplayer || !isPreviewWorker) return true;
            lock (overlay)
            {
                if (!overlay.TryGetValue(tileId, out seed)) return true;
            }
            __result = true;
            return false;
        }
    }
}
