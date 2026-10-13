using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Vanilla Social Interactions Expanded by Oskar Potocki, Taranchuk</summary>
    /// <see href="https://github.com/Vanilla-Expanded/VanillaSocialInteractionsExpanded"/>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=2439736083"/>
    /// <remarks>
    /// Waylith lane VSIE (13 Oct): VSIE_Utils.sManager is read when a toil is BUILT (WithProgressBar/WithEffect
    /// postfixes capture it in their WorkerTick / Notify_AspirationProgress closures). On load, toils are built in
    /// PostLoadInit (JobDriver.ExposeData -> SetupToils), before Game.FinalizeInit where the cache was cleared,
    /// so a process that reloads in place (host at every join point/autosave: SaveAndReload) wires the current
    /// jobs to the previous game's dead manager while a fresh joiner/arbiter wires them to its live one. The
    /// joiner then gains pawnsWithWorkers keys the host lacks, and GameComponentTick draws one world Rand value
    /// per key every 300 ticks -> "Wrong random state for the world".
    /// Fix: (1) clear the cache before a game is built, (2) redirect calls made on a stale manager to the live
    /// one, (3) draw the per-pawn discord chance from a seeded pushed Rand state, so the world RNG never depends
    /// on how many keys the dictionary holds.
    /// r4 (13 Oct): (2) checks the cached live manager of the current Game first, so the common case (a live
    /// instance) costs two reference compares instead of a Game.GetComponent scan on every WorkerTick call.
    /// </remarks>
    [MpCompatFor("VanillaExpanded.VanillaSocialInteractionsExpanded")]
    public class VanillaSocialInteractions
    {
        private static AccessTools.FieldRef<IDictionary> averageOpinionCacheField;
        private static AccessTools.FieldRef<object> socialInteractionsManagerField;
        private static AccessTools.FieldRef<IDictionary> checkedPawnsField;

        // Waylith lane VSIE
        private static Type managerType;
        private static AccessTools.FieldRef<object, IDictionary> pawnsWithWorkersField;
        private static AccessTools.FieldRef<bool> enableDiscordField;
        private static FastInvokeHandler tryCauseGroupFightsMethod;
        private static FastInvokeHandler workerTickMethod;
        private static FastInvokeHandler notifyAspirationProgressMethod;

        // r4: the live manager of liveGame, cached for the WorkerTick prefix (it runs every tick of every chew
        // toil, twice). Emptied by ClearManager/ClearCache, i.e. before a game is built and after FinalizeInit,
        // the only window in which a game's component list is replaced (Scribe in Game.ExposeSmallComponents).
        private static Game liveGame;
        private static GameComponent liveManager;

        public VanillaSocialInteractions(ModContentPack mod)
        {
            // Clear static caches between games
            // VSIE_Utils.averageOpinionOfCache - cached opinion values per pawn
            var field = AccessTools.DeclaredField("VanillaSocialInteractionsExpanded.VSIE_Utils:averageOpinionOfCache");
            averageOpinionCacheField = AccessTools.StaticFieldRefAccess<IDictionary>(field);

            // VSIE_Utils.sManager - cached GameComponent reference, stale between games
            var sManagerField = AccessTools.DeclaredField("VanillaSocialInteractionsExpanded.VSIE_Utils:sManager");
            socialInteractionsManagerField = AccessTools.StaticFieldRefAccess<object>(sManagerField);

            // ImmunityRecord_ImmunityTickInterval_Patch.checkedPawns - tracks pawns checked for trait development
            var checkedField = AccessTools.DeclaredField("VanillaSocialInteractionsExpanded.ImmunityRecord_ImmunityTickInterval_Patch:checkedPawns");
            checkedPawnsField = AccessTools.StaticFieldRefAccess<IDictionary>(checkedField);

            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(GameComponentUtility), nameof(GameComponentUtility.FinalizeInit)),
                postfix: new HarmonyMethod(typeof(VanillaSocialInteractions), nameof(ClearCache)));

            // (1) Also clear the cached manager BEFORE a game is built. Components load in
            // Game.ExposeSmallComponents, before the maps, so toils built in PostLoadInit fetch the new one.
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(Game), nameof(Game.LoadGame)),
                prefix: new HarmonyMethod(typeof(VanillaSocialInteractions), nameof(ClearManager)));
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(Game), nameof(Game.InitNewGame)),
                prefix: new HarmonyMethod(typeof(VanillaSocialInteractions), nameof(ClearManager)));

            managerType = AccessTools.TypeByName("VanillaSocialInteractionsExpanded.SocialInteractionsManager");

            // (2) Toils that still hold an old manager (built before this ran, or any other stale capture):
            // send their calls to the live component instead.
            var workerTick = AccessTools.DeclaredMethod(managerType, "WorkerTick");
            var notifyAspiration = AccessTools.DeclaredMethod(managerType, "Notify_AspirationProgress");
            workerTickMethod = MethodInvoker.GetHandler(workerTick);
            notifyAspirationProgressMethod = MethodInvoker.GetHandler(notifyAspiration);
            MpCompat.harmony.Patch(workerTick,
                prefix: new HarmonyMethod(typeof(VanillaSocialInteractions), nameof(PreWorkerTick)));
            MpCompat.harmony.Patch(notifyAspiration,
                prefix: new HarmonyMethod(typeof(VanillaSocialInteractions), nameof(PreNotifyAspirationProgress)));

            // (3) Discord check: same 0.1% per pawn every 300 ticks, drawn from a seeded pushed state.
            pawnsWithWorkersField = AccessTools.FieldRefAccess<IDictionary>(managerType, "pawnsWithWorkers");
            enableDiscordField = AccessTools.StaticFieldRefAccess<bool>(AccessTools.DeclaredField(
                "VanillaSocialInteractionsExpanded.VanillaSocialInteractionsExpandedSettings:EnableDiscord"));
            tryCauseGroupFightsMethod = MethodInvoker.GetHandler(
                AccessTools.DeclaredMethod("VanillaSocialInteractionsExpanded.Workers:TryCauseGroupFights"));
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(managerType, "GameComponentTick"),
                prefix: new HarmonyMethod(typeof(VanillaSocialInteractions), nameof(PreGameComponentTick)));
        }

        private static void ClearCache()
        {
            averageOpinionCacheField().Clear();
            socialInteractionsManagerField() = null;
            checkedPawnsField().Clear();
            ClearLiveCache();
        }

        private static void ClearManager()
        {
            socialInteractionsManagerField() = null;
            ClearLiveCache();
        }

        private static void ClearLiveCache()
        {
            liveGame = null;
            liveManager = null;
        }

        // True = let the original run on this instance (it is the live one, or there is no live one).
        private static bool IsLiveOrNone(object instance, out GameComponent live)
        {
            var game = Current.Game;
            // Fast path: the instance is the cached live manager of this same Game (no component scan).
            if (game != null && ReferenceEquals(game, liveGame) && ReferenceEquals(instance, liveManager))
            {
                live = liveManager;
                return true;
            }

            live = game?.GetComponent(managerType);
            liveGame = game;
            liveManager = live;
            return live == null || ReferenceEquals(live, instance);
        }

        private static bool PreWorkerTick(object __instance, Pawn pawn)
        {
            if (IsLiveOrNone(__instance, out var live))
                return true;
            workerTickMethod(live, pawn);
            return false;
        }

        private static bool PreNotifyAspirationProgress(object __instance, Pawn pawn)
        {
            if (IsLiveOrNone(__instance, out var live))
                return true;
            notifyAspirationProgressMethod(live, pawn);
            return false;
        }

        // Replaces SocialInteractionsManager.GameComponentTick (its base is the empty GameComponent one).
        private static bool PreGameComponentTick(object __instance)
        {
            var workers = pawnsWithWorkersField(__instance);
            if (!enableDiscordField() || workers == null)
                return false;

            var ticks = Find.TickManager.TicksGame;
            List<Pawn> fought = null;
            foreach (DictionaryEntry entry in workers)
            {
                var pawn = (Pawn)entry.Key;
                if (!pawn.IsHashIntervalTick(300))
                    continue;

                Rand.PushState(Gen.HashCombineInt(pawn.thingIDNumber, ticks));
                bool hit;
                try { hit = Rand.Chance(0.001f); }
                finally { Rand.PopState(); }

                if (hit && (bool)tryCauseGroupFightsMethod(entry.Value, pawn))
                    (fought ??= new List<Pawn>()).Add(pawn);
            }

            if (fought != null)
                foreach (var pawn in fought)
                    workers.Remove(pawn);
            return false;
        }
    }
}
