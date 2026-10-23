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

            // Plant TickLong throttle off in MP (FIXES row 222, the live 22 Oct 20:19 desync from James's Desync-06): Optimization_Plant_TickLong
            // skips most TickLongs of wild plants through a per-process cache (thingID -> last full TickLong) and makes the ones that run step
            // age/growth by the throttle interval instead of 2000. One dandelion ran its full TickLong ("dying" damage, 2 Rand draws) on the
            // joiner's PC only. In MP the prefix always lets TickLong run and ShouldBeThrottled is false, so ThrottleRateInt/Float give
            // vanilla's 2000 and every plant ticks as in vanilla on every PC. Single player keeps the optimization.
            {
                var plantPrefix = AccessTools.DeclaredMethod("PerformanceOptimizer.Optimization_Plant_TickLong:Prefix");
                var shouldBeThrottled = AccessTools.DeclaredMethod("PerformanceOptimizer.Optimization_Plant_TickLong:ShouldBeThrottled");
                if (plantPrefix != null && shouldBeThrottled != null)
                {
                    MpCompat.harmony.Patch(plantPrefix, prefix: new HarmonyMethod(typeof(PerformanceOptimizer), nameof(PlantTickLongAlwaysRunsInMp)));
                    MpCompat.harmony.Patch(shouldBeThrottled, prefix: new HarmonyMethod(typeof(PerformanceOptimizer), nameof(PlantNeverThrottledInMp)));
                }
                else Log.Error("Could not find Optimization_Plant_TickLong Prefix/ShouldBeThrottled, the plant throttle stays on in MP and can desync (FIXES row 222).");
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

            // Simulation context for the interface/simulation cache split (see IsSimulation)
            {
                var ticking = AccessTools.Property(AccessTools.TypeByName("Multiplayer.Client.Multiplayer"), "Ticking")?.GetGetMethod(true);
                if (ticking != null)
                    tickingGetter = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), ticking);
                else
                    Log.Error("Couldn't find Multiplayer.Client.Multiplayer:Ticking, Performance Optimizer's cache split falls back to MP.InInterface and will likely cause desyncs while windows update outside the interface.");
            }

            // Late patch, needs to run after Performance Optimizer init call.
            // The usual LongEventHandler call won't work here.
            {
                MpCompat.harmony.Patch(
                    AccessTools.DeclaredMethod("PerformanceOptimizer.PerformanceOptimizerSettings:Initialize"),
                    postfix: new HarmonyMethod(typeof(PerformanceOptimizer), nameof(PostPerformanceOptimizerInitialize)));
            }

            // 1.7 lab, FIXES row 146: no overview letter at map load in a session (see PreMapFinalizeInit)
            {
                overviewLetterSentField = AccessTools.DeclaredField("PerformanceOptimizer.PerformanceOptimizerSettings:overviewLetterSent");
                var mapFinalizeInit = AccessTools.DeclaredMethod(typeof(Map), nameof(Map.FinalizeInit));
                if (overviewLetterSentField != null && mapFinalizeInit != null)
                {
                    MpCompat.harmony.Patch(mapFinalizeInit,
                        prefix: new HarmonyMethod(typeof(PerformanceOptimizer), nameof(PreMapFinalizeInit)),
                        finalizer: new HarmonyMethod(typeof(PerformanceOptimizer), nameof(FinalizeMapFinalizeInit)));
                }
                else Log.Error("Couldn't find PerformanceOptimizerSettings:overviewLetterSent or Map:FinalizeInit, a Performance Optimizer config without the flag will make a letter on one PC only and desync.");
            }
        }

        #region Overview letter

        // 1.7 lab, FIXES row 146 (20 Oct 2026, d146_loadsweep finding R-2): Map_FinalizeInit.Postfix (a Harmony postfix of Map.FinalizeInit,
        // which runs inside Game.LoadGame on every PC) does
        //   if (!PerformanceOptimizerSettings.overviewLetterSent) { Find.LetterStack.ReceiveLetter(...); overviewLetterSent = true; WriteSettings(); }
        // The flag lives in the PC's OWN config file (Mod_2664723367_PerformanceOptimizerMod.xml), not in the save. A PC whose config lacks it
        // (a fresh install: the second player's) makes the letter at its first load in a session and takes the next letter id (UniqueIDsManager.nextLetterID)
        // and an archive entry that the other PCs never get -> every later letter has another id on that PC. Load is a long event, so
        // MP does not turn the id into a local one (UniqueIds.UseLocalIds) and Seeds.cs only seeds the Rand, not the id counters.
        // Fix: while a session is on (MP.IsInMultiplayer = Multiplayer.session != null, set by HostUtil/ClientUtil before any session load) the flag reads
        // as "already sent" for the duration of Map.FinalizeInit, so the mod's postfix does nothing, and it is put back afterwards (finalizer, which
        // runs after all postfixes), so the config file is not marked and single player keeps its letter. Outside a session (including the
        // host's first load of its single-player save, before it hosts) nothing changes: that letter, if any, is part of the host's save and reaches
        // everybody with the join-point snapshot. The prefix patch is on Map.FinalizeInit, not on the mod's small Postfix, so JIT inlining of the
        // postfix into the patched method cannot bypass it.
        private static FieldInfo overviewLetterSentField;

        private static void PreMapFinalizeInit(out bool __state)
        {
            __state = false;
            if (!MP.IsInMultiplayer || (bool)overviewLetterSentField.GetValue(null))
                return;
            overviewLetterSentField.SetValue(null, true);
            __state = true;
        }

        private static void FinalizeMapFinalizeInit(bool __state)
        {
            if (__state)
                overviewLetterSentField.SetValue(null, false);
        }

        #endregion

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
        }

        #endregion

        // History (FIXES rows 23/28/34): Optimization_ThoughtHandler_TotalMoodOffset (500-tick per-pawn cache of ThoughtHandler.TotalMoodOffset, the instant
        // mood the mood level seeks towards, which gates mood-dependent Rand draws) was first removed here (ceb74bb) because the host's UI queries reached the
        // simulation cache. The cause was not the transpiler (it runs, ReturnCorrectCache is called from every patched prefix, lab probe mp43p/mp44p) but how
        // ReturnCorrectCache told the simulation from the interface: "not MP.InInterface" is also true while a long event runs or Multiplayer.reloading is set,
        // and windows (Moody's WindowUpdate -> DataCache.UpdateCache) keep running then. See IsSimulation below. With the simulation decided positively the
        // optimization is kept (its interface twin serves the UI), so no Performance Optimizer optimization is removed any more.

        #region Plant throttle (FIXES row 222)

        private static bool PlantTickLongAlwaysRunsInMp(ref bool __result)
        {
            if (!MP.IsInMultiplayer)
                return true;
            __result = true;
            return false;
        }

        private static bool PlantNeverThrottledInMp(ref bool __result)
        {
            if (!MP.IsInMultiplayer)
                return true;
            __result = false;
            return false;
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

        private static Func<bool> tickingGetter;

        // The simulation is decided POSITIVELY: Multiplayer.Ticking (a map, the world or the constant ticker is ticking) or a synced command is
        // executing. MP.InInterface is NOT the opposite of that: it is also false while a long event runs, while Multiplayer.reloading is set and
        // outside ProgramState.Playing, but windows still run their WindowUpdate/OnGUI code then (Moody's DataCache.UpdateCache during the join-point
        // reload: lab runs mp43p/mp44p, FIXES rows 23/28/34). With "!InInterface = simulation" every such UI query was served from the SIMULATION
        // cache on the windowed host only (the headless arbiter has no windows), so the host's caches held UI-time values and refresh ticks the arbiter's
        // did not -> different mood totals, mental break thresholds, ... -> "Wrong random state".
        private static bool IsSimulation()
        {
            if (!MP.IsInMultiplayer)
                return true;
            if (MP.IsExecutingSyncCommand)
                return true;
            // Without the getter fall back to the old rule (every non-interface context counts as simulation)
            return tickingGetter != null ? tickingGetter() : !MP.InInterface;
        }

        private static IDictionary ReturnCorrectCache(IDictionary simulationCache)
        {
            if (simulationCache == null)
                return null;

            // If simulation, return normal cache
            if (IsSimulation())
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