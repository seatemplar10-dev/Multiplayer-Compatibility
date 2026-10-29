using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Hegemony - Faction Territories and Vassalage 2 by Jaeger972 (+ Jaeger's Utility Framework)</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3825320105"/>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3825311621"/>
    [MpCompatFor("jaeger972.factionterritories2")]
    public class Hegemony
    {
        // Stage 1 "AI world" (FIXES row 299, REPORT-2026-10-29-new-mods.md). Hegemony is deterministic inside the tick
        // (seeded hashes, no System.Random, no threads), but it plans in the background per frame:
        // - 5 WorldComponentUpdate overrides drain JUF WorkQueues (campaigns, armies, trade, supply, upgrades, territory,
        //   site assignment, worksites, market) under a Stopwatch budget, with Rand, outside the synced tick.
        // - TerritorySolver refreshes on Time.realtimeSinceStartup. PartyRoutes cuts native world paths on a Stopwatch
        //   and waits for Time.frameCount. FactionPowers is a 2500-tick cache that a UI read can rebuild.
        // In MP the Update bodies run from the world tick instead, every budget is a fixed step count, the timers are
        // tick based, and UI reads cannot rebuild sim caches. Not needed: the static failed-route cache and ~70 unsaved
        // scheduling fields, because the host saves and reloads at every join point and those reset on a world change.
        // Both PCs need identical Hegemony mod settings (the sim reads its 461 rules).

        private static readonly string[] UpdateDrivenComponents =
        {
            "FactionTerritories2.Component",
            "FactionTerritories2.LandmarkHoldings",
            "FactionTerritories2.WorksiteSupport",
            "FactionTerritories2.Market",
            "FactionTerritories2.Operations",
        };

        private static Type[] updateTypes;
        private static bool runningFromTick;
        private static FastInvokeHandler workQueueProcess;

        private const int TerritoryRefreshTicks = 300;
        private static AccessTools.FieldRef<object, float> solverNextCheck;
        private static WeakReference lastSolver = new WeakReference(null);
        private static int nextTerritoryTick;

        private static MethodInfo frameCountReplacement;
        private static MethodInfo millisecondsReplacement;
        private static int replacedFrameReads;
        private static int replacedMillisecondReads;

        private static FieldInfo powersWorld;
        private static FieldInfo powersUntil;
        private static FieldInfo powersValues;

        public Hegemony(ModContentPack mod)
        {
            // F1: frame-driven work queues run from the world tick, with a fixed step count and no wall clock.
            updateTypes = UpdateDrivenComponents.Select(AccessTools.TypeByName).ToArray();
            foreach (var type in updateTypes)
                MpCompat.harmony.Patch(AccessTools.DeclaredMethod(type, nameof(WorldComponent.WorldComponentUpdate)),
                    prefix: new HarmonyMethod(typeof(Hegemony), nameof(SkipFrameUpdate)));
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod("FactionTerritories2.Component:WorldComponentTick"),
                postfix: new HarmonyMethod(typeof(Hegemony), nameof(RunUpdatesFromTick)));

            workQueueProcess = MethodInvoker.GetHandler(AccessTools.DeclaredMethod("JaegerUtilityFramework.WorkQueue:Process", new[] { typeof(int), typeof(double) }));
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod("FactionTerritories2.WorldWorkBudget:Process"),
                prefix: new HarmonyMethod(typeof(Hegemony), nameof(FixedStepBudget)));

            // The tutorial offer opens a window and writes saved world flags on one PC.
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod("FactionTerritories2.SettlementTutorial:WorldComponentUpdate"),
                prefix: new HarmonyMethod(typeof(Hegemony), nameof(SkipInMultiplayer)));

            // F2: territory refresh on a tick gate in place of Time.realtimeSinceStartup (5 s).
            var solverType = AccessTools.TypeByName("FactionTerritories2.TerritorySolver");
            solverNextCheck = AccessTools.FieldRefAccess<float>(solverType, "nextCheck");
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(solverType, "Update"),
                prefix: new HarmonyMethod(typeof(Hegemony), nameof(PreTerritoryUpdate)),
                postfix: new HarmonyMethod(typeof(Hegemony), nameof(PostTerritoryUpdate)));

            // F3: native world route search. Drop the Stopwatch cut-off and wait for the next tick, not the next frame.
            frameCountReplacement = AccessTools.DeclaredMethod(typeof(Hegemony), nameof(TickInsteadOfFrame));
            millisecondsReplacement = AccessTools.DeclaredMethod(typeof(Hegemony), nameof(NoElapsedTimeInMultiplayer));
            var routesType = AccessTools.TypeByName("FactionTerritories2.PartyRoutes");
            var searchCoreMethods = routesType.GetNestedTypes(AccessTools.all)
                .SelectMany(AccessTools.GetDeclaredMethods)
                .Where(m => m.Name.Contains("<SearchCore>") || (m.Name == "MoveNext" && m.DeclaringType.Name.Contains("<SearchCore>")));
            foreach (var method in searchCoreMethods)
                MpCompat.harmony.Patch(method, transpiler: new HarmonyMethod(typeof(Hegemony), nameof(ReplaceWallClock)));
            if (replacedFrameReads == 0 || replacedMillisecondReads == 0)
                Log.Error($"[MP Compat] Hegemony: PartyRoutes.SearchCore wall-clock reads not found (frame {replacedFrameReads}, ms {replacedMillisecondReads}). World routes will desync.");

            // F5: a UI read must not rebuild the FactionPowers cache (keys and timing would differ per PC).
            var powersType = AccessTools.TypeByName("FactionTerritories2.FactionPowers");
            powersWorld = AccessTools.DeclaredField(powersType, "world");
            powersUntil = AccessTools.DeclaredField(powersType, "until");
            powersValues = AccessTools.DeclaredField(powersType, "values");
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(powersType, "For"),
                prefix: new HarmonyMethod(typeof(Hegemony), nameof(PreFactionPowers)),
                finalizer: new HarmonyMethod(typeof(Hegemony), nameof(RestoreFactionPowers)));

            // F6 + diagnostics: a feature switched off after an exception is per PC. Diagnostics change sim-side calls.
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod("FactionTerritories2.CompatibilityGuard:Report"),
                postfix: new HarmonyMethod(typeof(Hegemony), nameof(WarnFeatureDisabled)));
            MpCompat.harmony.Patch(AccessTools.DeclaredPropertyGetter("FactionTerritories2.Diagnostics:Enabled"),
                prefix: new HarmonyMethod(typeof(Hegemony), nameof(NoDiagnostics)));

            // C1: AI decisions never target a player faction. In MP world ticks Faction.OfPlayer is the Spectator, so
            // without these the AI courts, taxes, invades and raids "the player" through the Spectator.
            // Targets: REPORT-2026-10-29-new-mods_audits/heg_stage1_targets.md Part 1 (row ids in brackets).
            var self = typeof(Hegemony);
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod("FactionTerritories2.ApiLifecycle:Block"), // [4b] Hegemony's own veto choke point
                prefix: new HarmonyMethod(self, nameof(VetoPlayerTargets)));
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod("FactionTerritories2.FactionFealty:TryAI"), // [1a, 2] diplomacy counterpart
                prefix: new HarmonyMethod(self, nameof(PreFealtyTryAI)));
            // Stage 2 (co-op fork, F5c): these run per realm. Blocked only while realms are not active.
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod("FactionTerritories2.EnvoyMissions:SendAvailable"), // [3] envoys
                prefix: new HarmonyMethod(self, nameof(FalseUnlessRealms)));
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod("FactionTerritories2.MechanoidCrisis:Offer"), // [9f] letter that starts the endgame crisis
                prefix: new HarmonyMethod(self, nameof(SkipUnlessRealms)));
            foreach (var method in new[]
            {
                "FactionTerritories2.Operations:Threatened", // [4e] allied reinforcement of a colony
                "FactionTerritories2.Conquest:Hold", // [9g] vassalise/raze/plunder letter after a player win
            })
                MpCompat.harmony.Patch(AccessTools.DeclaredMethod(method), prefix: new HarmonyMethod(self, nameof(FalseInMultiplayer)));
            // [5] The Workshop PlayerDomain.Sync copies the Spectator's name, colour and relations. The co-op fork (it has
            // FactionTerritories2.Realm) syncs each domain from its own realm, so it must run there.
            if (AccessTools.TypeByName("FactionTerritories2.Realm") == null)
                MpCompat.harmony.Patch(AccessTools.DeclaredMethod("FactionTerritories2.PlayerDomain:Sync"), prefix: new HarmonyMethod(self, nameof(SkipInMultiplayer)));
            foreach (var method in new[]
            {
                "FactionTerritories2.Stewardship:TickCare", // [6] mayor recall gives the pawn to the Spectator
                "FactionTerritories2.TerritoryIncursions:TryIncursion", // [9c] raids on player maps from AI territory
            })
                MpCompat.harmony.Patch(AccessTools.DeclaredMethod(method), prefix: new HarmonyMethod(self, nameof(SkipInMultiplayer)));
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod("FactionTerritories2.Operations:InvasionTargets"), // [4a]
                postfix: new HarmonyMethod(self, nameof(PostInvasionTargets)));
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod("FactionTerritories2.Operations:ArmyHostileWorldTarget"), // [4c]
                prefix: new HarmonyMethod(self, nameof(PreArmyHostileWorldTarget)));
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod("FactionTerritories2.Operations:AiInterceptTarget"), // [4d]
                prefix: new HarmonyMethod(self, nameof(PreAiInterceptTarget)));
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod("FactionTerritories2.TerritorialTaxes:ControllerFor"), // [7]
                postfix: new HarmonyMethod(self, nameof(PostTaxControllerFor)));
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod("FactionTerritories2.SettlementRetaliation:Attacked"), // [9a]
                prefix: new HarmonyMethod(self, nameof(PreRetaliationAttacked)));
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod("FactionTerritories2.TerritoryEncounters:Owner"), // [9e]
                prefix: new HarmonyMethod(self, nameof(PreEncounterOwner)));
        }

        #region F1 work queues

        private static bool SkipFrameUpdate() => !MP.IsInMultiplayer || runningFromTick;

        private static bool SkipInMultiplayer() => !MP.IsInMultiplayer;

        private static void RunUpdatesFromTick()
        {
            var world = Find.World;
            if (!MP.IsInMultiplayer || world == null)
                return;

            runningFromTick = true;
            try
            {
                foreach (var type in updateTypes)
                    world.components.Find(c => c.GetType() == type)?.WorldComponentUpdate();
            }
            finally
            {
                runningFromTick = false;
            }
        }

        // WorldWorkBudget.Process(WorkQueue queue, int steps, double milliseconds) -> queue.Process(steps, no time limit)
        private static bool FixedStepBudget(object[] __args, ref int __result)
        {
            if (!MP.IsInMultiplayer)
                return true;

            __result = (int)workQueueProcess(__args[0], Math.Max(1, (int)__args[1]), 1e9);
            return false;
        }

        #endregion

        #region F2 territory refresh

        // nextCheck == 0 means "refresh now" (initial value, and TerritorySolver.RefreshFealty sets it). Otherwise the
        // original refreshes every 5 real seconds. Here: -inf = due this call, +inf = not due, refresh every 300 ticks.
        private static void PreTerritoryUpdate(object __instance, out bool __state)
        {
            __state = false;
            if (!MP.IsInMultiplayer)
                return;

            if (!ReferenceEquals(lastSolver.Target, __instance))
            {
                lastSolver = new WeakReference(__instance);
                nextTerritoryTick = 0;
            }

            ref var nextCheck = ref solverNextCheck(__instance);
            __state = nextCheck == 0f || Find.TickManager.TicksGame >= nextTerritoryTick;
            nextCheck = __state ? float.NegativeInfinity : float.PositiveInfinity;
        }

        private static void PostTerritoryUpdate(object __instance, bool __state)
        {
            if (!__state)
                return;

            ref var nextCheck = ref solverNextCheck(__instance);
            if (float.IsNegativeInfinity(nextCheck))
            {
                // A refresh was still running, so none started. Stay due.
                nextCheck = 0f;
            }
            else
            {
                nextCheck = float.PositiveInfinity;
                nextTerritoryTick = Find.TickManager.TicksGame + TerritoryRefreshTicks;
            }
        }

        #endregion

        #region F3 route search

        private static IEnumerable<CodeInstruction> ReplaceWallClock(IEnumerable<CodeInstruction> instr)
        {
            foreach (var ci in instr)
            {
                if (ci.operand is MethodInfo { Name: "get_frameCount" } frame && frame.DeclaringType?.FullName == "UnityEngine.Time")
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = frameCountReplacement;
                    replacedFrameReads++;
                }
                else if (ci.operand is MethodInfo { Name: "get_TotalMilliseconds" } ms && ms.DeclaringType == typeof(TimeSpan))
                {
                    // call instance float64 TimeSpan::get_TotalMilliseconds() takes a TimeSpan address, same as a ref parameter.
                    ci.opcode = OpCodes.Call;
                    ci.operand = millisecondsReplacement;
                    replacedMillisecondReads++;
                }

                yield return ci;
            }
        }

        private static int TickInsteadOfFrame() => MP.IsInMultiplayer ? Find.TickManager.TicksGame : UnityEngine.Time.frameCount;

        private static double NoElapsedTimeInMultiplayer(ref TimeSpan span) => MP.IsInMultiplayer ? 0.0 : span.TotalMilliseconds;

        #endregion

        #region F5 FactionPowers, F6 guard, diagnostics

        private sealed class PowersBackup(object world, object until, List<DictionaryEntry> entries)
        {
            public readonly object world = world;
            public readonly object until = until;
            public readonly List<DictionaryEntry> entries = entries;
        }

        // A sim read rebuilds in the same tick on every PC. A UI read of a stale cache may rebuild it, with the local
        // player's faction as key, so put the sim cache back after the call.
        private static void PreFactionPowers(out PowersBackup __state)
        {
            __state = null;
            if (!MP.IsInMultiplayer || !MP.InInterface)
                return;

            var world = powersWorld.GetValue(null);
            var until = powersUntil.GetValue(null);
            if (ReferenceEquals(world, Find.World) && Find.TickManager.TicksGame < (int)until)
                return;

            var values = (IDictionary)powersValues.GetValue(null);
            var entries = new List<DictionaryEntry>(values.Count);
            foreach (DictionaryEntry entry in values)
                entries.Add(entry);
            __state = new PowersBackup(world, until, entries);
        }

        private static Exception RestoreFactionPowers(Exception __exception, PowersBackup __state)
        {
            if (__state == null)
                return __exception;

            powersWorld.SetValue(null, __state.world);
            powersUntil.SetValue(null, __state.until);
            var values = (IDictionary)powersValues.GetValue(null);
            values.Clear();
            foreach (var entry in __state.entries)
                values.Add(entry.Key, entry.Value);
            return __exception;
        }

        private static void WarnFeatureDisabled(string text)
        {
            if (MP.IsInMultiplayer)
                Log.Error($"[MP Compat] Hegemony switched a feature off on this PC only. Expect a desync. Reload the game on every PC. ({text})");
        }

        private static bool NoDiagnostics(ref bool __result)
        {
            if (!MP.IsInMultiplayer)
                return true;

            __result = false;
            return false;
        }

        #endregion

        #region C1 AI paths never target a player

        private static bool IsPlayerPlace(MapParent place)
            => place != null && (place.Faction is { IsPlayer: true } || (place.HasMap && place.Map.IsPlayerHome));

        private static bool IsPlayerTarget(WorldObject target)
            => target is Caravan { Faction.IsPlayer: true } || (target is MapParent place && IsPlayerPlace(place));

        private static bool FalseInMultiplayer(ref bool __result)
        {
            if (!MP.IsInMultiplayer)
                return true;

            __result = false;
            return false;
        }

        private static bool FalseUnlessRealms(ref bool __result) => HegemonyRealms.RealmsActive || FalseInMultiplayer(ref __result);

        private static bool SkipUnlessRealms() => !MP.IsInMultiplayer || HegemonyRealms.RealmsActive;

        // With realms active, a player faction is a valid party only when it owns a realm. MP's Spectator never is.
        private static bool BlockedPlayerFaction(object faction, bool realmsLifted)
            => faction is Faction { IsPlayer: true } player && !(realmsLifted && HegemonyRealms.IsRealmFaction(player));

        // ApiLifecycle.Block(HegemonyVetoKind kind, Faction actor, Faction other, MapParent target, PlanetTile? tile, terms)
        // Kinds 0 AIWar, 1 AIPeaceOrTreaty, 2 InvasionTarget, 3 SettlementSite (left alone).
        // Stage 2: kinds 0 and 1 against a realm run when realms are active (F5c). Kind 2 stays blocked until F5b.
        private static bool VetoPlayerTargets(object[] __args, ref string __result)
        {
            var kind = Convert.ToInt32(__args[0]);
            if (!MP.IsInMultiplayer || kind > 2)
                return true;

            var lifted = kind < 2 && HegemonyRealms.RealmsActive;
            if (BlockedPlayerFaction(__args[1], lifted) || BlockedPlayerFaction(__args[2], lifted) || (!lifted && IsPlayerPlace(__args[3] as MapParent)))
            {
                __result = "Multiplayer: Hegemony runs as an AI-only world";
                return false;
            }

            return true;
        }

        // true = "handled": the diplomacy tick returns before its war, peace, letter and envoy code for this pair.
        // Stage 2: a realm candidate runs the real code when realms are active (F5c).
        private static bool PreFealtyTryAI(Faction candidate, ref bool __result)
        {
            if (!MP.IsInMultiplayer || candidate is not { IsPlayer: true } || HegemonyRealms.IsRealmFaction(candidate))
                return true;

            __result = true;
            return false;
        }

        private static void PostInvasionTargets(List<MapParent> __result)
        {
            if (MP.IsInMultiplayer)
                __result?.RemoveAll(place => place == null || IsPlayerPlace(place));
        }

        private static bool PreArmyHostileWorldTarget(WorldObject target, ref double distance, ref bool __result)
        {
            if (!MP.IsInMultiplayer || !IsPlayerTarget(target))
                return true;

            distance = 0.0;
            __result = false;
            return false;
        }

        private static bool PreAiInterceptTarget(WorldObject target, ref double distance, ref double power, ref double loot, ref bool __result)
        {
            if (!MP.IsInMultiplayer || !IsPlayerTarget(target))
                return true;

            distance = power = loot = 0.0;
            __result = false;
            return false;
        }

        // null controller = "no foreign territorial tax" for that place, and nothing is collected.
        private static void PostTaxControllerFor(MapParent settlement, ref Faction __result)
        {
            if (MP.IsInMultiplayer && (__result is { IsPlayer: true } || IsPlayerPlace(settlement)))
                __result = null;
        }

        private static bool PreRetaliationAttacked(Settlement victim, Faction aggressor)
            => !MP.IsInMultiplayer || (aggressor is not { IsPlayer: true } && victim?.Faction is not { IsPlayer: true });

        private static bool PreEncounterOwner(ref Faction __result)
        {
            if (!MP.IsInMultiplayer)
                return true;

            __result = null;
            return false;
        }

        #endregion
    }
}
