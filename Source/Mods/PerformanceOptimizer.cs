using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Performance Optimizer by Taranchuk</summary>
    /// <see href="https://github.com/Taranchuk/PerformanceOptimizer"/>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=2664723367"/>
    [MpCompatFor("Taranchuk.PerformanceOptimizer")]
    public class PerformanceOptimizer
    {
        #region Init

        public PerformanceOptimizer(ModContentPack mod)
        {
            // Time controls
            {
                var doTimeControlsHotkeys = AccessTools.DeclaredMethod("Multiplayer.Client.AsyncTime.TimeControlPatch:DoTimeControlsHotkeys");
                if (doTimeControlsHotkeys != null)
                {
                    doTimeControlsHotkeysMethod = MethodInvoker.GetHandler(doTimeControlsHotkeys);
                    MpCompat.harmony.Patch(AccessTools.DeclaredMethod("PerformanceOptimizer.Optimization_DoPlaySettings_DoTimespeedControls:DoTimeControlsGUI"),
                        prefix: new HarmonyMethod(typeof(PerformanceOptimizer), nameof(PreDoTimeControlsGUI)));
                }
                else Log.Error("Could not find TimeControlPatch:DoTimeControlsHotkeys, speed control hot keys won't work with disabled/hidden speed control UI.");
            }

            // Clear cache on join, etc.
            {
                var resetDataMethod = AccessTools.DeclaredMethod("PerformanceOptimizer.PerformanceOptimizerMod:ResetStaticData");
                MpCompat.harmony.Patch(resetDataMethod,
                    prefix: new HarmonyMethod(typeof(PerformanceOptimizer), nameof(CancelIfAutosaving)));
                refreshCache = MethodInvoker.GetHandler(resetDataMethod);

                // MP (1.7) has 2 overloads: (string, bool) only forwards to (string, GameDataSnapshot), which does
                // the saving and is also called directly by Autosaving.DoAutosave - so patch the non-bool overload.
                // A by-name lookup throws AmbiguousMatchException with 2 overloads.
                MethodInfo method = null;
                var autosavingType = AccessTools.TypeByName("Multiplayer.Client.Autosaving");
                if (autosavingType != null)
                {
                    var overloads = AccessTools.GetDeclaredMethods(autosavingType).Where(m => m.Name == "SaveGameToFile_Overwrite").ToArray();
                    method = overloads.Length == 1
                        ? overloads[0]
                        : overloads.FirstOrDefault(m => m.GetParameters().All(p => p.ParameterType != typeof(bool)));
                }
                // Backwards compat
                method ??= AccessTools.DeclaredMethod("Multiplayer.Client.MultiplayerSession:SaveGameToFile_Overwrite");
                // Even more backwards compat
                method ??= AccessTools.DeclaredMethod("Multiplayer.Client.MultiplayerSession:SaveGameToFile");
                if (method != null)
                {
                    MpCompat.harmony.Patch(method,
                        prefix: new HarmonyMethod(typeof(PerformanceOptimizer), nameof(PreSaveToFile)),
                        postfix: new HarmonyMethod(typeof(PerformanceOptimizer), nameof(PostSaveToFile)));
                }
                else Log.Error("Couldn't find MP SaveGameToFile method, PerformanceOptimizer will now likely cause desyncs because the patch failed.");

                // MP 1.7: Autosaving.DoAutosave builds its snapshot with SaveLoad.SaveGameData() BEFORE it calls
                // SaveGameToFile_Overwrite(string, GameDataSnapshot) (Autosaving.cs:18-20), so isAutoSaving is still false
                // while the save runs World.ExposeData -> ExposeComponents -> World.FillComponents (also in Saving mode),
                // which PerformanceOptimizer prefixes with ResetStaticData: the HOST alone cleared every cache, incl.
                // Optimization_Plant_TickLong.cachedResults -> host-only TickLong bodies (age +6000, Dying -> Rand)
                // -> "Wrong random state on map 0" a few hundred ticks after each autosave (FIXES row 18, lab runs mp21/mp22).
                // Flag every save that is not part of a save-and-reload (join points reload on all clients, so clearing
                // there stays symmetric).
                var saveGameData = AccessTools.DeclaredMethod("Multiplayer.Client.SaveLoad:SaveGameData");
                reloadingField = AccessTools.DeclaredField("Multiplayer.Client.Multiplayer:reloading");
                if (saveGameData != null && reloadingField != null)
                {
                    MpCompat.harmony.Patch(saveGameData,
                        prefix: new HarmonyMethod(typeof(PerformanceOptimizer), nameof(PreSaveGameData)),
                        finalizer: new HarmonyMethod(typeof(PerformanceOptimizer), nameof(FinalizeSaveGameData)));
                }
                else Log.Error("Couldn't find MP SaveLoad.SaveGameData or Multiplayer.reloading, PerformanceOptimizer will now likely cause desyncs on autosave.");

                // Big shoutout to NotFood for pointing me to the correct method to clear the cache in.
                // I spent hours trying to find a correct method where to clear the cache but failed.
                MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(GameComponentUtility), nameof(GameComponentUtility.FinalizeInit)),
                    postfix: new HarmonyMethod(typeof(PerformanceOptimizer), nameof(RefreshCachePrefix)));
            }

            // Late patch, needs to run after Performance Optimizer init call.
            // The usual LongEventHandler call won't work here.
            {
                MpCompat.harmony.Patch(
                    AccessTools.DeclaredMethod("PerformanceOptimizer.PerformanceOptimizerSettings:Initialize"),
                    postfix: new HarmonyMethod(typeof(PerformanceOptimizer), nameof(PostPerformanceOptimizerInitialize)));
            }
        }

        private static void PostPerformanceOptimizerInitialize()
        {
            // Separate caches from simulation and interface
            // Those caches can be called from both interface and simulation code. This causes issues
            // as the cache could end up getting recalculated from interface code and then accessed
            // from simulation code, causing a desync. Our patches add another cache for all of them,
            // and patches the methods so they access the proper cache based if it's simulation or interface.
            // This ensures that those methods benefit from caching no matter the context, while also making
            // sure that the game doesn't desync due to simulation relevant caches don't get modified from interface.
            {
                var transpiler = new HarmonyMethod(typeof(PerformanceOptimizer), nameof(SeparateCachesTranspiler));
                var optimizationsList = AccessTools.DeclaredField("PerformanceOptimizer.PerformanceOptimizerSettings:throttles").GetValue(null) as IList;

                if (optimizationsList != null)
                {
                    foreach (var optimization in optimizationsList)
                    {
                        var type = optimization.GetType();
                        var (prefixName, postfixName) = type.Name switch
                        {
                            // Exceptions to default:
                            // May seem pointless, but let's not risk issues
                            "Optimization_InspectGizmoGrid_DrawInspectGizmoGridFor" => ("GetGizmosFast", null),
                            // No postfix method
                            "Optimization_Precept_RoleMulti_RecacheActivity" => ("Prefix", null),
                            "Optimization_Precept_RoleSingle_RecacheActivity" => ("Prefix", null),
                            "Optimization_Plant_TickLong" => ("Prefix", null),
                            "Optimization_JobGiver_ConfigurableHostilityResponse" => ("Prefix", null),
                            // Different name for prefix/postfix
                            "Optimization_Building_Door_DoorRotationAt" => ("DoorRotationAtPrefix", "DoorRotationAtPostfix"),
                            // Default methods to patch
                            _ => ("Prefix", "Postfix")
                        };

                        if (prefixName != null)
                        {
                            var prefix = AccessTools.DeclaredMethod(type, prefixName);
                            if (prefix != null)
                                MpCompat.harmony.Patch(prefix, transpiler: transpiler);
                            else
                                Log.Error($"Type {type.FullName} is missing {prefixName} method, patching failed.");
                        }

                        if (postfixName != null)
                        {
                            var postfix = AccessTools.DeclaredMethod(type, postfixName);
                            if (postfix != null)
                                MpCompat.harmony.Patch(postfix, transpiler: transpiler);
                            else
                                Log.Error($"Type {type.FullName} is missing {postfixName} method, patching failed.");
                        }
                    }
                }
                else Log.Error("PerformanceOptimizer.PerformanceOptimizerSettings:throttles was null or empty, the patch was likely called too early.");
            }

            RemoveMoodCache();
        }

        #endregion

        #region Mood cache

        // Optimization_ThoughtHandler_TotalMoodOffset caches ThoughtHandler.TotalMoodOffset() per pawn for 500 ticks and refreshes it on the FIRST
        // QUERY after the expiry, whoever asks. That total is the instant mood Need_Mood.NeedInterval seeks towards, so it decides when a thought
        // change reaches the mood level, and the mood level gates Rand draws (InspirationHandler.CheckStartRandomInspiration needs mood >= 0.5,
        // mental breaks need their thresholds). A client that draws a mood widget (colonist bar mood bar: Need.CurInstantLevelPercentage ->
        // Need_Mood.CurInstantLevel -> TotalMoodOffset, every ~60 ticks) refreshes that cache at other ticks than the headless arbiter, which only
        // asks from the 150-tick NeedInterval.
        // Lab runs mp29/mp31/mp32 (FIXES row 23): same cached situational thoughts, same tick, instant mood 0.5556 on the host and 0.6157 on the
        // arbiter (NeedFood -6 counted by one side only), mood level one 0.0048 step apart; the arbiter then draws the inspiration MTB at mood
        // 0.5068, the host (0.5020, then 0.4972) does not -> "Wrong random state".
        // The interface/simulation split above is present in the IL (ReturnCorrectCache after both ldsfld cachedResults in Prefix, checked in the
        // 1.7 lab) but the simulation still saw the interface refreshes, so this module does not rely on it for the mood total: it removes the
        // optimization's Harmony patches from TotalMoodOffset. Every other Performance Optimizer optimization is untouched.
        private static void RemoveMoodCache()
        {
            var totalMoodOffset = AccessTools.DeclaredMethod(typeof(ThoughtHandler), nameof(ThoughtHandler.TotalMoodOffset));
            if (totalMoodOffset == null)
            {
                Log.Error("Couldn't find ThoughtHandler:TotalMoodOffset, Performance Optimizer's mood cache will cause desyncs.");
                return;
            }

            MpCompat.harmony.Unpatch(totalMoodOffset, HarmonyPatchType.All, "PerformanceOptimizer.Main");
        }

        #endregion

        #region Cache clearing patch

        private static FastInvokeHandler refreshCache;
        private static bool isAutoSaving = false;

        // While the game is saving, PerformanceOptimizer clears the cache.
        // However, in MP only the host is saving, but not the clients - we need to either clear for all, or for none.
        private static bool CancelIfAutosaving()
        {
            if (MP.IsInMultiplayer && MP.IsHosting)
            {
#if DEBUG
                if (Find.CurrentMap != null) 
                    Log.Message($"{(isAutoSaving ? "Autosaving" : "Refreshing cache")} at: {Find.TickManager?.TicksGame}");
#endif
                // Stop cache clearing when auto saving
                if (isAutoSaving)
                    return false;

                // Clear our custom caches
                ClearInterfaceCaches();
                return true;
            }

#if DEBUG
            if (MP.IsInMultiplayer && Find.CurrentMap != null) 
                Log.Message($"Refreshing cache at: {Find.TickManager?.TicksGame}");
#endif
            isAutoSaving = false;
            // Clear our custom caches
            ClearInterfaceCaches();
            return true;
        }

        private static void RefreshCachePrefix() => refreshCache(null);

        private static void PreSaveToFile() => isAutoSaving = true;
        private static void PostSaveToFile() => isAutoSaving = false;

        private static FieldInfo reloadingField;

        // A plain save (autosave, save window) runs on the host only; a save inside SaveAndReload runs on every client.
        private static void PreSaveGameData(out bool __state)
        {
            __state = isAutoSaving;
            if (!(bool)reloadingField.GetValue(null))
                isAutoSaving = true;
        }

        private static void FinalizeSaveGameData(bool __state) => isAutoSaving = __state;

        #endregion

        #region Context-sensitive caches

        private static readonly Dictionary<IDictionary, IDictionary> SimulationToInterfaceCaches = new();

        // Should the cache clearing get postfixed to the Clear() method call of the class they inject into?
        private static void ClearInterfaceCaches()
        {
            foreach (var cache in SimulationToInterfaceCaches.Values)
                cache.Clear();
        }

        private static IDictionary ReturnCorrectCache(IDictionary simulationCache)
        {
            if (simulationCache == null)
                return null;

            // If simulation, return normal cache
            if (!MP.InInterface)
                return simulationCache;

            // If interface, try to return the cache from our dictionary
            if (SimulationToInterfaceCaches.TryGetValue(simulationCache, out var interfaceCache))
                return interfaceCache;

            // This shouldn't ever run, but is here just in case something breaks.
            Log.Warning($"Trying to get interface cache for dictionary of type: {simulationCache.GetType()}");
            interfaceCache = Activator.CreateInstance(simulationCache.GetType()) as IDictionary;
            SimulationToInterfaceCaches[simulationCache] = interfaceCache;
            return interfaceCache;
        }

        private static IEnumerable<CodeInstruction> SeparateCachesTranspiler(IEnumerable<CodeInstruction> instr)
        {
            var replacementCall = AccessTools.DeclaredMethod(typeof(PerformanceOptimizer), nameof(ReturnCorrectCache));

            foreach (var ci in instr)
            {
                yield return ci;

                // Check for static is a bit pointless since the opcode is Ldsfld, but keeping it for safety anyway.
                // Checking for the field name is a bit less unreliable in case mod changes, but so far all caches use the same name.
                if (ci.opcode == OpCodes.Ldsfld && ci.operand is FieldInfo { IsStatic: true, Name: "cachedResults" } field)
                {
                    if (field.GetValue(null) is not IDictionary dict)
                        continue;

                    // Prepare the simulation-to-interface dictionary
                    if (!SimulationToInterfaceCaches.ContainsKey(dict))
                        SimulationToInterfaceCaches[dict] = Activator.CreateInstance(dict.GetType()) as IDictionary;

                    // After the field with cache was accessed, call the method that'll replace it (if needed).
                    yield return new CodeInstruction(OpCodes.Call, replacementCall);
                }
            }

            // A lot of postfixes don't access the cache (they have the value out of cache passed as __state from prefix),
            // so don't really bother logging if we haven't patched anything (unless debugging).
        }

        #endregion

        #region Time controls

        private static FastInvokeHandler doTimeControlsHotkeysMethod;

        private static bool PreDoTimeControlsGUI()
        {
            if (!MP.IsInMultiplayer)
                return true;

            doTimeControlsHotkeysMethod(null);
            return false;
        }

        #endregion
    }
}