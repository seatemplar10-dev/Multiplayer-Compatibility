using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>
    /// Row 523 (27 Nov 2026), VFE Tribals: one era (tech level, cornerstone points, cornerstones, ethos, large fire counters, research bookkeeping)
    /// per non-spectator player faction in multifaction MP. Design: pack_tools/ERA_DESIGN.md option (c), changed by the Twin review (q_era523).
    /// The mod keeps ONE set of fields in GameComponent_Tribals and writes the era into the shared FactionDef.techLevel. Here the store is the
    /// truth: a record per faction id, bound to the GameComponent_Tribals instance (a new game or load makes a new instance, so nothing leaks
    /// through a static). Swap(faction) writes the live fields into the record of the faction that was current, loads the record of the new
    /// one into the live fields (in place, so no list is shared between records) and mirrors the defs.
    /// Def mirror (desync 1): every def used by a non-spectator player faction gets the era of the lowest-loadID faction using it, then the new
    /// faction's own def gets its own era. The result depends only on the current faction, so every PC holds the same def values at every tick.
    /// def.techLevel is never copied back into the store (desync 2). The spectator never owns a record: it sees a pristine one each time.
    /// Part 1 only: save/load, old saves and joiners are part 2 (see TODO(part 2)); looped tick, FinalizeInit, Apply and LargeFire are part 3.
    /// </summary>
    public static class TribalsEraStore
    {
        const string logPrefix = "[MPCompat - VFE Tribals] ";

        public sealed class Record
        {
            public TechLevel? playerTechLevel;
            public int availableCornerstonePoints;
            public List<Def> cornerstones = new List<Def>();
            public string ethos;
            public bool ethosLocked;
            public int largeFireActiveTicks;
            public int lastLargeFireUpdate;
            public int lastTickResearchFinished;
            public HashSet<ResearchProjectDef> finishedResearchProjects = new HashSet<ResearchProjectDef>();
        }

        /// <summary>The store of one GameComponent_Tribals instance.</summary>
        public sealed class Store
        {
            public readonly SortedDictionary<int, Record> records = new SortedDictionary<int, Record>();
            /// <summary>eraCurrentFactionId: the faction whose record is in the live fields; -1 before the first Swap.</summary>
            public int eraCurrentFactionId = -1;
            /// <summary>Faction that owned the live fields when the store was created (the one seeded from them).</summary>
            public int seedFactionId = -1;
            /// <summary>False until the first Swap has run the def mirror.</summary>
            public bool mirrored;
        }

        static readonly ConditionalWeakTable<object, Store> stores = new ConditionalWeakTable<object, Store>();

        // ---- environment seams (default: the real Multiplayer internals; the zero-boot D2 replaces them) ----
        internal static Func<bool> IsActive = DefaultIsActive;
        internal static Func<Faction> Spectator = DefaultSpectator;
        internal static Func<Faction> RealPlayer = DefaultRealPlayer;
        internal static Func<object> CompInstance = DefaultCompInstance;
        internal static Func<FactionDef, TechLevel?> Pristine = DefaultPristine;
        /// <summary>Known-false switch for the D2 only: mirror just the new faction's def (the Twin's "weak form"). Never set in the game.</summary>
        internal static bool testMirrorNewOnly;
        internal static int swapCount;

        static FieldInfo fPoints, fTech, fEthos, fEthosLocked, fCorner, fFireTicks, fFireUpdate, fLastResearch, fFinished;
        static bool fieldsBound, hooksInstalled, errorLogged;
        static FieldInfo multifactionField, spectatorField;
        static Func<object> gameCompGetter, worldCompGetter;
        static Func<Faction> realPlayerGetter;
        static FieldInfo instanceField;
        static FieldInfo originalTechLevelsField;

        // ---- install (module constructor) ----
        public static void Install(Harmony harmony)
        {
            if (LabOff.Row(523))
            {
                Log.Message(logPrefix + "row 523 switched off by the lab, one era per game as before");
                return;
            }
            var compType = AccessTools.TypeByName("VFETribals.GameComponent_Tribals");
            if (compType == null || !BindFields(compType))
            {
                Log.Error(logPrefix + "row 523: GameComponent_Tribals or one of its era fields not found, era store not installed");
                return;
            }
            var startup = AccessTools.TypeByName("VFETribals.Startup");
            originalTechLevelsField = startup == null ? null : AccessTools.DeclaredField(startup, "originalTechLevels");
            instanceField = AccessTools.DeclaredField(compType, "Instance");

            var push = AccessTools.DeclaredMethod("Multiplayer.Client.FactionContext:Push");
            var pop = AccessTools.DeclaredMethod("Multiplayer.Client.FactionContext:Pop");
            var change = AccessTools.DeclaredMethod("Multiplayer.Client.MultiplayerGame:ChangeRealPlayerFaction", new[] { typeof(Faction), typeof(bool) });
            if (push == null || pop == null || change == null)
            {
                Log.Error(logPrefix + "row 523: FactionContext.Push/Pop or MultiplayerGame.ChangeRealPlayerFaction not found, era store not installed");
                return;
            }
            // NOT FactionContext.Set: it is a one-line method that Mono can inline into its callers, so a patch there may never run.
            harmony.Patch(push, postfix: new HarmonyMethod(typeof(TribalsEraStore), nameof(PushPostfix)));
            harmony.Patch(pop, postfix: new HarmonyMethod(typeof(TribalsEraStore), nameof(PopPostfix)));
            harmony.Patch(change, postfix: new HarmonyMethod(typeof(TribalsEraStore), nameof(ChangePostfix)));
            hooksInstalled = true;
            Log.Message(logPrefix + "row 523: era, points, cornerstones and ethos are kept per player faction in multifaction");
        }

        static bool BindFields(Type t)
        {
            fPoints = AccessTools.DeclaredField(t, "availableCornerstonePoints");
            fTech = AccessTools.DeclaredField(t, "playerTechLevel");
            fEthos = AccessTools.DeclaredField(t, "ethos");
            fEthosLocked = AccessTools.DeclaredField(t, "ethosLocked");
            fCorner = AccessTools.DeclaredField(t, "cornerstones");
            fFireTicks = AccessTools.DeclaredField(t, "largeFireActiveTicks");
            fFireUpdate = AccessTools.DeclaredField(t, "lastLargeFireUpdate");
            fLastResearch = AccessTools.DeclaredField(t, "lastTickResearchFinished");
            fFinished = AccessTools.DeclaredField(t, "finishedResearchProjects");
            return fieldsBound = new[] { fPoints, fTech, fEthos, fEthosLocked, fCorner, fFireTicks, fFireUpdate, fLastResearch, fFinished }.All(f => f != null);
        }

        /// <summary>Binds the fields on a type without installing hooks (D2 harness).</summary>
        internal static bool BindForTest(Type compType)
        {
            instanceField = AccessTools.DeclaredField(compType, "Instance");
            var startup = AccessTools.TypeByName("VFETribals.Startup");
            originalTechLevelsField = startup == null ? null : AccessTools.DeclaredField(startup, "originalTechLevels");
            return BindFields(compType);
        }

        // ---- hooks ----
        static void PushPostfix(Faction __result) { if (__result != null) SafeSwap(); }
        static void PopPostfix(Faction __result) { if (__result != null) SafeSwap(); }
        static void ChangePostfix() { SafeSwap(); }

        static void SafeSwap()
        {
            try { Swap(Faction.OfPlayer); }
            catch (Exception e)
            {
                if (errorLogged) return;
                errorLogged = true;
                Log.Error(logPrefix + "row 523: era swap failed (logged once): " + e);
            }
        }

        /// <summary>Start of every MPC sim entry of this module: the raw ofPlayer write of SidebarPatch.cs:14 does not go through Push/Pop.</summary>
        public static void Ensure()
        {
            if (!fieldsBound || !IsActive()) return;
            var of = Faction.OfPlayer;
            if (of == null) return;
            var comp = CompInstance();
            if (comp != null && stores.TryGetValue(comp, out var store) && store.eraCurrentFactionId == of.loadID && store.mirrored) return;
            SafeSwap();
        }

        public static Store StoreOf(object comp) => comp != null && stores.TryGetValue(comp, out var s) ? s : null;

        // ---- the swap ----
        public static void Swap(Faction nw)
        {
            if (nw == null || !fieldsBound || !IsActive()) return;
            var comp = CompInstance();
            if (comp == null) return;
            var spectator = Spectator();
            var store = GetOrCreate(comp, nw, spectator);
            if (nw.loadID == store.eraCurrentFactionId && store.mirrored) return;
            swapCount++;

            // 1. write the live fields into the record of the faction that was current (never for the spectator, never def.techLevel)
            if (store.eraCurrentFactionId != -1 && !IsSpectatorId(store.eraCurrentFactionId, spectator))
                store.records[store.eraCurrentFactionId] = Snapshot(comp, null);

            // 2. load the record of the new faction into the live fields
            Record rec;
            if (IsSpectator(nw, spectator)) rec = PristineRecord(nw.def);
            else
            {
                if (!store.records.TryGetValue(nw.loadID, out rec))
                {
                    // TODO(part 2): old saves (copy of the stock values) and joiners (pristine of the joiner's def) are decided in part 2.
                    // Part 1: any faction without a record, other than the seeded one, gets the pristine record of its def.
                    store.records[nw.loadID] = rec = PristineRecord(nw.def);
                }
            }
            Load(comp, rec);
            store.eraCurrentFactionId = nw.loadID;

            // 3. mirror the defs: a function of the current faction only
            Mirror(store, nw, rec, spectator);
            store.mirrored = true;
        }

        static Store GetOrCreate(object comp, Faction nw, Faction spectator)
        {
            if (stores.TryGetValue(comp, out var existing)) return existing;
            var store = new Store();
            // The live fields belong to the faction that was current when the store is first created: the local player faction (HostUtil.cs:83
            // hosts a single player save: that colony keeps its era). Falls back to the new faction when it is not the spectator.
            Faction owner = null;
            try { owner = RealPlayer(); } catch (Exception) { }
            if (owner == null || IsSpectator(owner, spectator)) owner = nw;
            if (!IsSpectator(owner, spectator))
            {
                store.seedFactionId = owner.loadID;
                store.records[owner.loadID] = Snapshot(comp, owner.def);
            }
            stores.Add(comp, store);
            return store;
        }

        static void Mirror(Store store, Faction nw, Record rec, Faction spectator)
        {
            if (!testMirrorNewOnly)
            {
                var factions = Find.FactionManager.AllFactionsListForReading;
                var byDef = new Dictionary<FactionDef, Faction>();
                for (var i = 0; i < factions.Count; i++)
                {
                    var f = factions[i];
                    if (f == null || f.def == null || !f.IsPlayer || IsSpectator(f, spectator)) continue;
                    if (!byDef.TryGetValue(f.def, out var c) || f.loadID < c.loadID) byDef[f.def] = f;
                }
                foreach (var kv in byDef)
                {
                    var c = kv.Value;
                    if (!store.records.TryGetValue(c.loadID, out var cr)) store.records[c.loadID] = cr = PristineRecord(c.def);
                    SetDef(c.def, cr.playerTechLevel);
                }
            }
            SetDef(nw.def, rec.playerTechLevel);
        }

        static void SetDef(FactionDef def, TechLevel? era)
        {
            var level = era ?? Pristine(def);
            if (level.HasValue) def.techLevel = level.Value;
        }

        // ---- records ----
        static Record PristineRecord(FactionDef def) => new Record { playerTechLevel = Pristine(def) ?? def.techLevel };

        /// <summary>Copies the live fields (own list and set copies). <paramref name="techFallbackDef"/>: for the seed, a null live era becomes that def's current era.</summary>
        static Record Snapshot(object comp, FactionDef techFallbackDef)
        {
            var r = new Record
            {
                playerTechLevel = (TechLevel?)fTech.GetValue(comp),
                availableCornerstonePoints = (int)fPoints.GetValue(comp),
                ethos = (string)fEthos.GetValue(comp),
                ethosLocked = (bool)fEthosLocked.GetValue(comp),
                largeFireActiveTicks = (int)fFireTicks.GetValue(comp),
                lastLargeFireUpdate = (int)fFireUpdate.GetValue(comp),
                lastTickResearchFinished = (int)fLastResearch.GetValue(comp),
            };
            if (r.playerTechLevel == null && techFallbackDef != null) r.playerTechLevel = techFallbackDef.techLevel;
            if (fCorner.GetValue(comp) is IEnumerable live) foreach (var o in live) r.cornerstones.Add((Def)o);
            if (fFinished.GetValue(comp) is HashSet<ResearchProjectDef> fin) r.finishedResearchProjects = new HashSet<ResearchProjectDef>(fin);
            return r;
        }

        /// <summary>Writes the record into the live fields. Lists and the set are refilled in place so the live object never aliases a record.</summary>
        static void Load(object comp, Record r)
        {
            fTech.SetValue(comp, r.playerTechLevel);
            fPoints.SetValue(comp, r.availableCornerstonePoints);
            fEthos.SetValue(comp, r.ethos);
            fEthosLocked.SetValue(comp, r.ethosLocked);
            fFireTicks.SetValue(comp, r.largeFireActiveTicks);
            fFireUpdate.SetValue(comp, r.lastLargeFireUpdate);
            fLastResearch.SetValue(comp, r.lastTickResearchFinished);
            var list = fCorner.GetValue(comp) as IList;
            if (list == null) fCorner.SetValue(comp, list = (IList)Activator.CreateInstance(fCorner.FieldType));
            list.Clear();
            foreach (var d in r.cornerstones) list.Add(d);
            var set = fFinished.GetValue(comp) as HashSet<ResearchProjectDef>;
            if (set == null) fFinished.SetValue(comp, set = new HashSet<ResearchProjectDef>());
            set.Clear();
            foreach (var p in r.finishedResearchProjects) set.Add(p);
        }

        // ---- helpers ----
        static bool IsSpectator(Faction f, Faction spectator) => spectator != null && f != null && f.loadID == spectator.loadID;
        static bool IsSpectatorId(int id, Faction spectator) => spectator != null && spectator.loadID == id;

        static bool DefaultIsActive()
        {
            if (!MP.IsInMultiplayer) return false;
            try
            {
                if (multifactionField == null)
                {
                    multifactionField = AccessTools.Field("Multiplayer.Client.MultiplayerGameComp:multifaction");
                    gameCompGetter = (Func<object>)Delegate.CreateDelegate(typeof(Func<object>), AccessTools.PropertyGetter("Multiplayer.Client.Multiplayer:GameComp"));
                }
                return (bool)multifactionField.GetValue(gameCompGetter());
            }
            catch (Exception) { return false; }
        }

        static Faction DefaultSpectator()
        {
            try
            {
                if (spectatorField == null)
                {
                    spectatorField = AccessTools.Field("Multiplayer.Client.MultiplayerWorldComp:spectatorFaction");
                    worldCompGetter = (Func<object>)Delegate.CreateDelegate(typeof(Func<object>), AccessTools.PropertyGetter("Multiplayer.Client.Multiplayer:WorldComp"));
                }
                return spectatorField.GetValue(worldCompGetter()) as Faction;
            }
            catch (Exception) { return null; }
        }

        static Faction DefaultRealPlayer()
        {
            if (realPlayerGetter == null)
                realPlayerGetter = (Func<Faction>)Delegate.CreateDelegate(typeof(Func<Faction>), AccessTools.PropertyGetter("Multiplayer.Client.Multiplayer:RealPlayerFaction"));
            return realPlayerGetter();
        }

        static object DefaultCompInstance() => instanceField?.GetValue(null);

        static TechLevel? DefaultPristine(FactionDef def)
        {
            if (def == null || originalTechLevelsField == null) return null;
            return originalTechLevelsField.GetValue(null) is Dictionary<FactionDef, TechLevel> table && table.TryGetValue(def, out var level) ? level : (TechLevel?)null;
        }
    }
}
