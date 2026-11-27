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
    /// <summary>
    /// Rows 525-529 (27 Nov 2026), VFE Tribals, part 3 of the per-faction era store (TribalsEraStore.cs). The store swaps the live fields of
    /// GameComponent_Tribals on every FactionContext Push/Pop; this file makes the mod's own entry points run under the right faction:
    /// 525 GameComponentTick looped over the non-spectator player factions (the world tick runs under the spectator, MP AsyncWorldTimeComp.cs:154),
    /// 526 FinalizeInit looped, 527 the five AdvanceTo* ritual Apply and the research-finish patch, 528 one clock for the large fire counter,
    /// 529 a faction created mid-game (MP FactionCreator.InitNewGame skips the VFET game start path).
    /// </summary>
    public static class TribalsEraTick
    {
        const string logPrefix = "[MPCompat - VFE Tribals] ";

        // ---- seams (default: real MP internals; the D2 replaces them) ----
        internal static Func<List<Faction>> TickFactions = DefaultFactions;
        internal static Action<Faction> PushF = DefaultPush;
        internal static Action PopF = DefaultPop;
        internal static Func<int> MpClock = DefaultClock;
        /// <summary>Known-false switch for the D2 only: the loop also runs the body for the spectator (the 525 arm must FAIL with it). Never set in the game.</summary>
        internal static bool testIncludeSpectator;
        /// <summary>Row 528: the component tick resets the fire counter only when the last fire tick is more than this many world ticks old.</summary>
        internal const int FireTolerance = 60;

        static bool inLoop;
        static bool loopStarted;     // set by RunPerFaction once the faction list is known: an error before it runs the stock body, never skips it
        static bool errorLogged;
        static MethodInfo researchAll, tryRegister;
        static FieldInfo cultureField, fFireUpdate, instanceField;
        static Type compType;
        static FieldInfo classicField;

        public static void Install(Harmony harmony, Type comp)
        {
            compType = comp;
            fFireUpdate = AccessTools.DeclaredField(comp, "lastLargeFireUpdate");
            instanceField = AccessTools.DeclaredField(comp, "Instance");
            researchAll = AccessTools.DeclaredMethod(comp, "ResearchAllAnimalProjects");
            tryRegister = AccessTools.DeclaredMethod(comp, "TryRegisterAdvancementObligation");
            cultureField = AccessTools.Field("VFETribals.VFET_DefOf:VFET_Culture");
            var tick = AccessTools.DeclaredMethod(comp, "GameComponentTick");
            var fin = AccessTools.DeclaredMethod(comp, "FinalizeInit");
            var incr = AccessTools.DeclaredMethod(comp, "IncrementLargeFireCounter");

            if (!LabOff.Row(525) && tick != null)
            {
                harmony.Patch(tick, prefix: new HarmonyMethod(typeof(TribalsEraTick), nameof(TickPrefix)));
                Log.Message(logPrefix + "row 525: the component tick runs once per player faction in multifaction, never for the spectator");
            }
            else if (tick == null) Log.Error(logPrefix + "row 525: GameComponent_Tribals.GameComponentTick not found");

            if (!LabOff.Row(526) && fin != null)
            {
                harmony.Patch(fin, prefix: new HarmonyMethod(typeof(TribalsEraTick), nameof(FinalizePrefix)));
                Log.Message(logPrefix + "row 526: FinalizeInit runs once per player faction in multifaction (animal research, obligations, classicMode if no faction has Culture)");
            }

            if (!LabOff.Row(527))
            {
                var n = 0;
                foreach (var era in new[] { "Neolithic", "Medieval", "Industrial", "Spacer", "Ultra" })
                {
                    var apply = AccessTools.DeclaredMethod(AccessTools.TypeByName("VFETribals.RitualOutcomeEffectWorker_AdvanceTo" + era), "Apply");
                    if (apply == null) continue;
                    harmony.Patch(apply, prefix: new HarmonyMethod(typeof(TribalsEraTick), nameof(ApplyPrefix)),
                        finalizer: new HarmonyMethod(typeof(TribalsEraTick), nameof(ApplyFinalizer)));
                    n++;
                }
                var fp = AccessTools.DeclaredMethod(AccessTools.TypeByName("VFETribals.ResearchManager_FinishProject_Patch"), "Postfix");
                if (fp != null) { harmony.Patch(fp, prefix: new HarmonyMethod(typeof(TribalsEraTick), nameof(FinishPrefix))); n++; }
                Log.Message(logPrefix + "row 527: ritual Apply runs under the ritual's player faction and research bookkeeping skips the spectator (" + n + " methods)");
            }

            if (!LabOff.Row(528))
            {
                if (incr != null) harmony.Patch(incr, transpiler: new HarmonyMethod(typeof(TribalsEraTick), nameof(FireTranspiler)));
                if (tick != null) harmony.Patch(tick, transpiler: new HarmonyMethod(typeof(TribalsEraTick), nameof(TickTranspiler)));
                Log.Message(logPrefix + "row 528: the large fire counter uses the world tick (AsyncWorldTimeComp.worldTicks) on both sides in multiplayer");
            }

            if (!LabOff.Row(529))
            {
                var init = AccessTools.DeclaredMethod("Multiplayer.Client.FactionCreator:InitNewGame");
                if (init != null) harmony.Patch(init, postfix: new HarmonyMethod(typeof(TribalsEraTick), nameof(NewFactionPostfix)));
                else Log.Warning(logPrefix + "row 529: FactionCreator.InitNewGame not found, a new faction gets no animal research from this module");
                Log.Message(logPrefix + "row 529: a new faction starts at its pristine era with its animal research and obligations");
            }
        }

        static void LogOnce(string text)
        {
            if (errorLogged) return;
            errorLogged = true;
            Log.Error(logPrefix + text);
        }

        // ---------------- row 525 ----------------
        /// <summary>The loadIDs of MultiplayerWorldComp.factionData (all player factions incl. the spectator).</summary>
        internal static Func<List<int>> FactionDataIds = DefaultIds;
        static List<int> DefaultIds()
        {
            var ids = new List<int>();
            var world = AccessTools.Method("Multiplayer.Client.Multiplayer:get_WorldComp")?.Invoke(null, null);
            var data = world == null ? null : AccessTools.Field("Multiplayer.Client.MultiplayerWorldComp:factionData")?.GetValue(world) as IDictionary;
            if (data != null) foreach (var k in data.Keys) ids.Add((int)k);
            return ids;
        }

        static List<Faction> DefaultFactions()
        {
            var result = new List<Faction>();
            var ids = FactionDataIds();
            ids.Sort();
            var spectator = TribalsEraStore.Spectator();
            foreach (var id in ids)
            {
                if (spectator != null && spectator.loadID == id && !testIncludeSpectator) continue;
                var f = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(x => x.loadID == id);
                if (f != null && f.IsPlayer) result.Add(f);
            }
            return result;
        }

        static MethodInfo pushM, popM;
        static void DefaultPush(Faction f)
        {
            if (pushM == null)
            {
                var t = AccessTools.TypeByName("Multiplayer.Client.Factions.FactionExtensions");
                pushM = AccessTools.Method(t, "PushFaction", new[] { typeof(Map), typeof(Faction), typeof(bool) });
                popM = AccessTools.Method(t, "PopFaction", new[] { typeof(Map) });
            }
            pushM.Invoke(null, new object[] { null, f, true });
        }
        static void DefaultPop() => popM.Invoke(null, new object[] { null });

        /// <summary>Runs <paramref name="body"/> once per faction, each under PushFaction(force) / PopFaction. Returns false when there is nothing to loop.</summary>
        internal static bool RunPerFaction(Action<Faction> body)
        {
            loopStarted = false;
            if (!MP.IsInMultiplayer || !TribalsEraStore.IsActive()) return false;
            var list = TickFactions();
            if (list == null || list.Count == 0) return false;
            loopStarted = true;
            foreach (var f in list)
            {
                PushF(f);
                try { body(f); }
                finally { PopF(); }
            }
            return true;
        }

        static bool TickPrefix(GameComponent __instance)
        {
            if (inLoop) return true;                    // the looped call below: run the stock body
            try
            {
                inLoop = true;
                try { return !RunPerFaction(f => __instance.GameComponentTick()); }
                finally { inLoop = false; }
            }
            catch (Exception e) { LogOnce("row 525: looped tick failed (logged once): " + e); return !loopStarted; }   // failed before the loop (single player, MP check): run stock
        }

        // ---------------- row 526 ----------------
        static bool FinalizePrefix(GameComponent __instance)
        {
            if (inLoop) return true;
            try
            {
                var anyCulture = false;
                inLoop = true;
                bool ran;
                try
                {
                    ran = RunPerFaction(f =>
                    {
                        // stock body, under f: FinalizeInit :221-233 without the def write (the def mirror of part 1 owns def.techLevel)
                        if (Faction.OfPlayer.def.techLevel > TechLevel.Animal) researchAll?.Invoke(null, null);
                        if (cultureField?.GetValue(null) is ResearchProjectDef culture && culture.IsFinished) anyCulture = true;
                        tryRegister?.Invoke(__instance, null);
                    });
                }
                finally { inLoop = false; }
                if (!ran) return true;
                if (!anyCulture && Find.IdeoManager != null) Find.IdeoManager.classicMode = true;
                return false;
            }
            catch (Exception e) { LogOnce("row 526: looped FinalizeInit failed (logged once): " + e); return !loopStarted; }   // failed before the loop: run stock
        }

        // ---------------- row 527 ----------------
        static void ApplyPrefix(object jobRitual, ref bool __state)
        {
            __state = false;
            try
            {
                if (!MP.IsInMultiplayer || !TribalsEraStore.IsActive()) return;
                var f = RitualFaction(jobRitual);
                if (f == null) return;
                PushF(f);
                __state = true;
            }
            catch (Exception e) { LogOnce("row 527: ritual push failed (logged once): " + e); }
        }

        static void ApplyFinalizer(bool __state)
        {
            if (!__state) return;
            try { PopF(); } catch (Exception e) { LogOnce("row 527: ritual pop failed (logged once): " + e); }
        }

        internal static Func<object, Faction> RitualFactionSeam;
        static Faction RitualFaction(object jobRitual)
        {
            if (RitualFactionSeam != null) return RitualFactionSeam(jobRitual);
            var lj = jobRitual as LordJob_Ritual;
            var spectator = TribalsEraStore.Spectator();
            var org = lj?.Organizer?.Faction;
            if (org != null && org.IsPlayer && (spectator == null || org.loadID != spectator.loadID)) return org;
            var f = PlayerFactionForTick.ForMap(lj?.Map);
            return f != null && f.IsPlayer && (spectator == null || f.loadID != spectator.loadID) ? f : null;
        }

        static bool FinishPrefix()
        {
            if (!MP.IsInMultiplayer || !TribalsEraStore.IsActive()) return true;
            var spectator = TribalsEraStore.Spectator();
            var of = Faction.OfPlayer;
            return !(spectator != null && of != null && of.loadID == spectator.loadID);
        }

        // ---------------- row 528 ----------------
        // MP AsyncTimeComp.cs:123 `Find.TickManager.ticksGameInt = mapTicks;` makes TicksGame the MAP clock inside a map tick (LargeFire.Tick calls
        // IncrementLargeFireCounter there), while GameComponentTick runs in the world tick where TicksGame is the world clock. One clock for both:
        // AsyncWorldTimeComp.worldTicks (AsyncWorldTimeComp.cs:69, the world's own count, ++ at :111 after DoSingleTick; the same value in both places).
        static int DefaultClock()
        {
            var w = AccessTools.Method("Multiplayer.Client.Multiplayer:get_AsyncWorldTime")?.Invoke(null, null);
            var f = w == null ? null : AccessTools.Field("Multiplayer.Client.AsyncWorldTimeComp:worldTicks");
            return f == null ? Find.TickManager.TicksGame : (int)f.GetValue(w);
        }

        public static int FireClock() => MP.IsInMultiplayer ? MpClock() : Find.TickManager.TicksGame;

        /// <summary>Value the stock `lastLargeFireUpdate != TicksGame` compares with in the component tick: the stored value while the fire ticked
        /// within FireTolerance world ticks (map and world clocks run at different speeds and in any order), the clock otherwise (reset).</summary>
        public static int CompClock(object comp)
        {
            if (!MP.IsInMultiplayer) return Find.TickManager.TicksGame;
            var now = MpClock();
            var last = (int)fFireUpdate.GetValue(comp);
            return last <= now && now - last <= FireTolerance ? last : now;
        }

        static IEnumerable<CodeInstruction> FireTranspiler(IEnumerable<CodeInstruction> instr)
        {
            var get = AccessTools.PropertyGetter(typeof(TickManager), nameof(TickManager.TicksGame));
            var clock = AccessTools.Method(typeof(TribalsEraTick), nameof(FireClock));
            var n = 0;
            foreach (var ci in instr)
            {
                if (ci.Calls(get)) { n++; yield return new CodeInstruction(OpCodes.Pop) { labels = ci.labels }; yield return new CodeInstruction(OpCodes.Call, clock); }
                else yield return ci;
            }
            if (n != 2) Log.Error(logPrefix + "row 528: IncrementLargeFireCounter has " + n + " TicksGame reads, expected 2");
        }

        static IEnumerable<CodeInstruction> TickTranspiler(IEnumerable<CodeInstruction> instr)
        {
            var get = AccessTools.PropertyGetter(typeof(TickManager), nameof(TickManager.TicksGame));
            var clock = AccessTools.Method(typeof(TribalsEraTick), nameof(CompClock));
            var n = 0;
            foreach (var ci in instr)
            {
                if (ci.Calls(get))
                {
                    n++;
                    yield return new CodeInstruction(OpCodes.Pop) { labels = ci.labels };
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, clock);
                }
                else yield return ci;
            }
            if (n != 1) Log.Error(logPrefix + "row 528: GameComponentTick has " + n + " TicksGame reads, expected 1");
        }

        // ---------------- row 529 ----------------
        /// <summary>Under the new faction (FactionCreator.cs:95-98 pushes it with force around InitNewGame): the push seeded its store entry from the
        /// pristine era of its def (TribalsEraStore.Swap, missing record), here the game start part of VFET that MP skipped.</summary>
        static void NewFactionPostfix()
        {
            try
            {
                if (!MP.IsInMultiplayer || !TribalsEraStore.IsActive()) return;
                TribalsEraStore.Ensure();
                var spectator = TribalsEraStore.Spectator();
                var of = Faction.OfPlayer;
                if (of == null || (spectator != null && of.loadID == spectator.loadID)) return;
                if (of.def.techLevel > TechLevel.Animal) researchAll?.Invoke(null, null);
                var comp = instanceField?.GetValue(null);
                if (comp != null) tryRegister?.Invoke(comp, null);
            }
            catch (Exception e) { LogOnce("row 529: new faction setup failed (logged once): " + e); }
        }
    }
}
