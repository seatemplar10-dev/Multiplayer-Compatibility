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
using Verse.AI.Group;

namespace Multiplayer.Compat
{
    /// <summary>FULL1127 CU3 shared helpers: push a player faction (MP internal PushFaction / PopFaction by reflection) around a body.</summary>
    internal static class FullCust3
    {
        public const string Log3 = "[MPCompat - FULL1127 CU] ";
        static MethodInfo pushM, popM;

        /// <summary>Lowest loadID player faction that is not the MP spectator; null outside multiplayer or when none.</summary>
        public static Faction LowestRealPlayerIfSpectator(Faction f) => IsSpectator(f) ? LowestRealPlayer() : f;

        static Faction Spectator()
        {
            try { return Traverse.CreateWithType("Multiplayer.Client.Multiplayer").Property("WorldComp").Field("spectatorFaction").GetValue<Faction>(); } catch { return null; }
        }

        public static bool IsSpectator(Faction f) => f != null && f == Spectator();

        public static Faction LowestRealPlayer()
        {
            if (!MP.IsInMultiplayer) return null;
            Faction spectator = Spectator();
            Faction best = null;
            foreach (var f in Find.FactionManager.AllFactionsListForReading)
                if (f.IsPlayer && f != spectator && (best == null || f.loadID < best.loadID)) best = f;
            return best;
        }

        public static void Push(Map map, Faction f)
        {
            if (!MP.IsInMultiplayer || f == null) return;
            if (pushM == null)
            {
                var t = AccessTools.TypeByName("Multiplayer.Client.Factions.FactionExtensions");
                pushM = AccessTools.Method(t, "PushFaction", new[] { typeof(Map), typeof(Faction), typeof(bool) });
                popM = AccessTools.Method(t, "PopFaction", new[] { typeof(Map) });
            }
            pushM.Invoke(null, new object[] { map, f, true });
        }

        public static void Pop(Map map)
        {
            if (!MP.IsInMultiplayer) return;
            popM?.Invoke(null, new object[] { map });
        }

        static Type mapCompType;
        static MethodInfo setFactionM, inInterfaceGetter, mpCompM;
        static bool mapsMissLogged, mapsLogged;
        static bool inInterfaceLooked;

        /// <summary>Multiplayer.InInterface (false during a long event, a reload, a tick, executing commands), read by reflection. Outside MP: true.</summary>
        public static bool InInterface()
        {
            if (!MP.IsInMultiplayer) return true;
            if (!inInterfaceLooked)
            {
                inInterfaceLooked = true;
                var t = AccessTools.TypeByName("Multiplayer.Client.Multiplayer");
                inInterfaceGetter = t == null ? null : AccessTools.PropertyGetter(t, "InInterface");
            }
            if (inInterfaceGetter == null) return false;   // cannot tell: act as not in the interface (no ids handed out)
            try { return (bool)inInterfaceGetter.Invoke(null, null); } catch { return false; }
        }

        static void SetMapsFaction(Faction f)
        {
            if (f == null || Find.Maps == null) return;
            if (mapCompType == null)
            {
                mapCompType = AccessTools.TypeByName("Multiplayer.Client.MultiplayerMapComp");
                setFactionM = mapCompType == null ? null : AccessTools.Method(mapCompType, "SetFaction", new[] { typeof(Faction) });
                // MultiplayerMapComp is not a MapComponent: MP keeps it in Multiplayer.game.mapComps and finds it with the
                // Extensions.MpComp(Map) extension (MP Util/Extensions.cs:49-50). map.GetComponent returns null for it (orchestrator 28 Nov).
                var ext = AccessTools.TypeByName("Multiplayer.Client.Extensions");
                mpCompM = ext == null ? null : AccessTools.Method(ext, "MpComp", new[] { typeof(Map) });
            }
            if (setFactionM == null || mpCompM == null)
            {
                if (!mapsMissLogged) { mapsMissLogged = true; Log.Error("[MPCompat - Simple Warrants] SetMapsFaction: MP MultiplayerMapComp.SetFaction or Extensions.MpComp not found, map faction not switched"); }
                return;
            }
            int n = 0;
            foreach (var map in Find.Maps)
            {
                var comp = mpCompM.Invoke(null, new object[] { map });
                if (comp != null) { setFactionM.Invoke(comp, new object[] { f }); n++; }
            }
            if (!mapsLogged && n > 0) { mapsLogged = true; Log.Message("[MPCompat - Simple Warrants] SetMapsFaction: switched " + n + " map(s) (row 553)"); }
        }

        /// <summary>Push f and switch every map's MP faction data (areaManager, zones) to it, like MP's LongEvents does.</summary>
        public static void PushMaps(Faction f)
        {
            if (!MP.IsInMultiplayer || f == null) return;
            Push(null, f);
            try { SetMapsFaction(f); }
            catch { PopMaps(); throw; }
        }

        /// <summary>Pop the push and restore every map's MP faction data to the faction that is current after the pop.</summary>
        public static void PopMaps()
        {
            if (!MP.IsInMultiplayer) return;
            try { Pop(null); }
            finally { SetMapsFaction(Faction.OfPlayer); }
        }

        /// <summary>The existing RightPlayerFaction.Resolve(pawnFaction) as a reflection-free call for the transpilers.</summary>
        public static readonly MethodInfo ResolveM = AccessTools.Method(typeof(RightPlayerFaction), nameof(RightPlayerFaction.Resolve));
        public static readonly MethodInfo OfPlayerGetter = AccessTools.PropertyGetter(typeof(Faction), nameof(Faction.OfPlayer));
        public static readonly MethodInfo ThingFactionGetter = AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.Faction));
    }

    /// <summary>RimToll (forti.rimtoll), extra rows. ReleaseWorker's pair read is done by FullRimToll in FullQ12Helpers.cs.
    /// 1) ReleaseWorker second read (faction.HostileTo(OfPlayer)) becomes Resolve(worker pawn's faction).
    /// 2) ProcessPendingOpens (tick): HostileTo(OfPlayer) uses the lord's map owner; Open() runs with that faction pushed (letters, goodwill).
    /// 3) The UI answers (DemandToll, RefusePassage, AcceptRecruit [recruit.SetFaction(OfPlayer)], AccusePass, LetPass) were run on one PC only:
    ///    they are sent as one synced static call (string name, two ints) and run under the issuing player's faction.</summary>
    internal static class FullRimTollCu3
    {
        static Type stateType;
        static FieldInfo pendingOpensF;
        static MethodInfo openM, boothForM;
        static PropertyInfo instanceP;
        static FieldInfo workerPawnF;
        static readonly string[] ui = { "DemandToll", "RefusePassage", "AcceptRecruit", "AccusePass", "LetPass" };
        public static int SecondReadSwapped = -1;

        public static void Init()
        {
            stateType = AccessTools.TypeByName("RimToll.GameComponent_TollState");
            pendingOpensF = AccessTools.Field(stateType, "pendingOpens");
            openM = AccessTools.Method(stateType, "Open");
            boothForM = AccessTools.Method("RimToll.TollUtility:BoothFor");
            instanceP = AccessTools.Property(stateType, "Instance");
            workerPawnF = AccessTools.Field(AccessTools.TypeByName("RimToll.TollWorker"), "pawn");
            var h = MpCompat.harmony;

            h.Patch(AccessTools.Method(stateType, "ReleaseWorker"), transpiler: new HarmonyMethod(typeof(FullRimTollCu3), nameof(ReleaseTranspiler)));
            h.Patch(AccessTools.Method(stateType, "ProcessPendingOpens"), prefix: new HarmonyMethod(typeof(FullRimTollCu3), nameof(PendingOpensPrefix)) { priority = Priority.First });
            foreach (var n in ui)
                h.Patch(AccessTools.Method(stateType, n), prefix: new HarmonyMethod(typeof(FullRimTollCu3), nameof(UiPrefix)) { priority = Priority.First });
            MP.RegisterSyncMethod(typeof(FullRimTollCu3), nameof(SyncedUi));
            Log.Message(FullCust3.Log3 + "RimToll: ReleaseWorker second read, ProcessPendingOpens faction push and 5 UI answers registered");
        }

        // ReleaseWorker: the OfPlayer read that is not after Thing.get_Faction (the first pair is done by FullQ12) -> Resolve(w.pawn.Faction).
        static IEnumerable<CodeInstruction> ReleaseTranspiler(IEnumerable<CodeInstruction> instr)
        {
            var list = instr.ToList();
            int n = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (!list[i].Calls(FullCust3.OfPlayerGetter) || (i > 0 && list[i - 1].Calls(FullCust3.ThingFactionGetter))) continue;
                var labels = list[i].labels;
                var load = new CodeInstruction(OpCodes.Ldarg_1) { labels = new List<Label>(labels) };
                labels.Clear();
                list[i] = new CodeInstruction(OpCodes.Call, FullCust3.ResolveM);
                list.InsertRange(i, new[]
                {
                    load,
                    new CodeInstruction(OpCodes.Ldfld, workerPawnF),
                    new CodeInstruction(OpCodes.Callvirt, FullCust3.ThingFactionGetter),
                });
                i += 3;
                n++;
            }
            SecondReadSwapped = n;
            return list;
        }

        static bool PendingOpensPrefix(object __instance)
        {
            if (!MP.IsInMultiplayer || RightPlayerFaction.PlayerFactionCount() < 2) return true;
            var pending = (IList)pendingOpensF.GetValue(__instance);
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var lord = (Lord)pending[i];
                pending.RemoveAt(i);
                if (lord == null || lord.Map == null || lord.ownedPawns.Count == 0 || lord.faction == null || lord.faction.IsPlayer) continue;
                var player = PlayerFactionForTick.ForMap(lord.Map);
                if (lord.faction.HostileTo(player) || lord.LordJob?.GetType().Name == "LordJob_TollVisit") continue;
                var booth = (Building)boothForM.Invoke(null, new object[] { lord });
                if (booth == null) continue;
                bool pushed = player != Faction.OfPlayer;
                if (pushed) FullCust3.Push(lord.Map, player);
                try { openM.Invoke(__instance, new object[] { lord, booth }); }
                finally { if (pushed) FullCust3.Pop(lord.Map); }
            }
            return false;
        }

        static bool UiPrefix(MethodBase __originalMethod, object[] __args)
        {
            if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand) return true;
            SyncedUi(__originalMethod.Name, (int)__args[0], __args.Length > 1 ? (int)__args[1] : 0);
            return false;
        }

        [SyncMethod]
        static void SyncedUi(string name, int id, int level)
        {
            var inst = instanceP.GetValue(null);
            var m = AccessTools.Method(stateType, name);
            m.Invoke(inst, name == "DemandToll" ? new object[] { id, level } : new object[] { id });
        }
    }
}

namespace Multiplayer.Compat
{
    /// <summary>Visit Settlements (ninagoblin.visitsettlements): TriggerRaidEvent(Map) reads Faction.OfPlayer in a closure (HostileTo filter) from a world component tick,
    /// where it is the spectator. The visited settlement map has no player owner, so the visiting player is the owner of the colonists spawned on it:
    /// the lowest-loadID non-spectator player faction with a spawned pawn there is pushed around the call (map passed), popped in a finalizer.
    /// DropFoodPodsForSettlements (same class, per-map loop, pod filter) is not covered.</summary>
    internal static class FullVisitRaidCu3
    {
        public static void Init()
        {
            var m = AccessTools.Method("VS_WorldComponent_SettlementEvents:TriggerRaidEvent");
            if (m == null) { Log.Error(FullCust3.Log3 + "Visit Settlements: TriggerRaidEvent not found"); return; }
            MpCompat.harmony.Patch(m,
                prefix: new HarmonyMethod(typeof(FullVisitRaidCu3), nameof(Pre)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(FullVisitRaidCu3), nameof(Fin)));
            Log.Message(FullCust3.Log3 + "Visit Settlements: TriggerRaidEvent visiting-player faction push registered");
        }

        /// <summary>Visiting player faction of a map: lowest loadID among player-faction pawns spawned there, or null.</summary>
        internal static Faction VisitorFaction(Map map)
        {
            if (map == null) return null;
            Faction best = null;
            foreach (var p in map.mapPawns.AllPawnsSpawned)
            {
                var f = p?.Faction;
                if (f == null || !f.IsPlayer || f.def == null || !f.def.isPlayer) continue;
                if (best == null || f.loadID < best.loadID) best = f;
            }
            return best;
        }

        static void Pre(Map map, out bool __state)
        {
            __state = false;
            if (!MP.IsInMultiplayer || RightPlayerFaction.PlayerFactionCount() < 2) return;
            var f = VisitorFaction(map);
            if (f == null || f == Faction.OfPlayer) return;
            FullCust3.Push(map, f);
            __state = true;
        }

        static void Fin(Map map, bool __state)
        {
            if (__state) FullCust3.Pop(map);
        }
    }
}

namespace Multiplayer.Compat
{
    /// <summary>Worksites Expanded / MiningOutpost (godsfathermixtape.worksitesexpanded).
    /// 1) WorkSiteDefenseTracker methods that read Faction.OfPlayer (IssueOffer, ApplyTruce, RestoreRelation, DetermineAttacker, SettleAttackerRelation,
    ///    OnDefenseSuccess) run from the game component tick under the spectator. The owner is the player faction at the site: lowest loadID of a player pawn
    ///    spawned on the site map, else of a player caravan on the tile, else lowest loadID non-spectator player faction (deterministic). Pushed with a finalizer pop.
    /// 2) WorksiteImpressionTracker.MakeOffer: pushes the leader pawn's faction.
    /// 3) UI float-menu actions (RecruitImpressed, SetEscort, ClearEscort) become synced methods. Ask-for-healing lambda and Dialog_Parley buttons are NOT synced.</summary>
    internal static class FullMiningOutpostCu3
    {
        static FieldInfo worldTileF;
        static MethodInfo siteOnTileM;
        static FieldInfo leaderF;

        public static void Init()
        {
            var tr = AccessTools.TypeByName("MiningOutpost.WorkSiteDefenseTracker");
            var defT = AccessTools.TypeByName("MiningOutpost.WorkSiteDefense");
            worldTileF = AccessTools.Field(defT, "worldTile");
            siteOnTileM = AccessTools.Method(tr, "GetSiteOnTile");
            var h = MpCompat.harmony;
            int n = 0;
            foreach (var name in new[] { "IssueOffer", "ApplyTruce", "RestoreRelation", "DetermineAttacker", "SettleAttackerRelation", "OnDefenseSuccess" })
            {
                var m = AccessTools.Method(tr, name);
                if (m == null) { Log.Error(FullCust3.Log3 + "MiningOutpost: " + name + " not found"); continue; }
                h.Patch(m, prefix: new HarmonyMethod(typeof(FullMiningOutpostCu3), nameof(PreDef)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(FullMiningOutpostCu3), nameof(Fin)));
                n++;
            }
            var imp = AccessTools.TypeByName("MiningOutpost.WorksiteImpressionTracker");
            var mo = AccessTools.Method(imp, "MakeOffer");
            if (mo == null) Log.Error(FullCust3.Log3 + "MiningOutpost: MakeOffer not found");
            else
            {
                leaderF = AccessTools.Field(AccessTools.TypeByName("MiningOutpost.WorksiteImpressionRecord"), "leader");
                h.Patch(mo, prefix: new HarmonyMethod(typeof(FullMiningOutpostCu3), nameof(PreOffer)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(FullMiningOutpostCu3), nameof(Fin)));
                n++;
            }
            int s = 0;
            foreach (var (t, name) in new[] { ("MiningOutpost.WorksiteRecruitUtility", "RecruitImpressed"), ("MiningOutpost.WorksiteEscortTracker", "SetEscort"), ("MiningOutpost.WorksiteEscortTracker", "ClearEscort") })
            {
                var m = AccessTools.Method(t + ":" + name);
                if (m == null) { Log.Error(FullCust3.Log3 + "MiningOutpost: " + name + " not found"); continue; }
                MP.RegisterSyncMethod(m);
                s++;
            }
            Log.Message(FullCust3.Log3 + "MiningOutpost: " + n + " OfPlayer owners pushed, " + s + " UI actions synced registered");
        }

        internal static Faction OwnerOf(object def)
        {
            Faction best = null;
            Map map = null;
            try
            {
                var site = siteOnTileM?.Invoke(null, new object[] { Convert.ToInt32(worldTileF.GetValue(def)) }) as MapParent;
                map = site?.Map;
            }
            catch { }
            if (map != null) best = FullVisitRaidCu3.VisitorFaction(map);
            if (best == null && map == null)
            {
                foreach (var c in Find.WorldObjects.Caravans)
                {
                    var f = c.Faction;
                    if (f == null || !f.IsPlayer || !f.def.isPlayer) continue;
                    if (best == null || f.loadID < best.loadID) best = f;
                }
            }
            return best ?? FullCust3.LowestRealPlayer();
        }

        static void PreDef(object[] __args, out bool __state)
        {
            __state = false;
            if (!MP.IsInMultiplayer || RightPlayerFaction.PlayerFactionCount() < 2) return;
            object def = null;
            foreach (var a in __args) if (a != null && a.GetType() == worldTileF.DeclaringType) { def = a; break; }
            if (def == null) return;
            var f = OwnerOf(def);
            if (f == null || f == Faction.OfPlayer) return;
            FullCust3.Push(null, f);
            __state = true;
        }

        static void PreOffer(object r, out bool __state)
        {
            __state = false;
            if (!MP.IsInMultiplayer || RightPlayerFaction.PlayerFactionCount() < 2) return;
            var f = RightPlayerFaction.Resolve((leaderF.GetValue(r) as Pawn)?.Faction);
            if (f == null || f == Faction.OfPlayer) return;
            FullCust3.Push(null, f);
            __state = true;
        }

        static void Fin(bool __state)
        {
            if (__state) FullCust3.Pop(null);
        }
    }
}

namespace Multiplayer.Compat
{
    /// <summary>Panhandling (acutus.begging), one bundle.
    /// 1) PanhandlingWorldComponent.BeginBegging (called from Dialog_Panhandling) is sent as one synced static call; the alms requests and the breakdown (mod classes)
    ///    travel as parallel lists of primitives / defs / things and are rebuilt on every PC, then the original method runs.
    /// 2) ResolveSession (world tick) runs with session.caravan.Faction pushed (goodwill, hostility, letter, drafting).
    /// 3) StartAssaultEncounterNow (can run in a queued long event) runs with that faction pushed, and CameraJumper.TryJumpAndSelect inside it only jumps on the PC
    ///    whose own faction owns the caravan. Rand inside the long event is NOT touched (row 543).</summary>
    internal static class FullPanhandlingCu3
    {
        static Type almsT, breakdownT, compT, sessionT;
        static FieldInfo[] af;
        static FieldInfo bdBase, bdNeg, sCaravan;
        static MethodInfo beginM;
        [ThreadStatic] static Faction assaultOwner;

        public static void Init()
        {
            compT = AccessTools.TypeByName("Panhandling.PanhandlingWorldComponent");
            almsT = AccessTools.TypeByName("Panhandling.PanhandlingAlmsRequest");
            breakdownT = AccessTools.TypeByName("Panhandling.PanhandlingQualityBreakdown");
            sessionT = AccessTools.TypeByName("Panhandling.PanhandlingSession");
            beginM = AccessTools.Method(compT, "BeginBegging");
            var resolveM = AccessTools.Method(compT, "ResolveSession");
            var assaultM = AccessTools.Method(compT, "StartAssaultEncounterNow");
            var jumpM = AccessTools.Method(typeof(CameraJumper), nameof(CameraJumper.TryJumpAndSelect));
            string[] names = { "sourceThing", "def", "stuff", "quality", "hitPoints", "count", "marketValuePerUnit", "massPerUnit" };
            af = names.Select(n => AccessTools.Field(almsT, n)).ToArray();
            bdBase = AccessTools.Field(breakdownT, "baseQuality");
            bdNeg = AccessTools.Field(breakdownT, "negotiator");
            sCaravan = AccessTools.Field(sessionT, "caravan");
            if (beginM == null || resolveM == null || assaultM == null || jumpM == null || af.Any(x => x == null) || bdBase == null || bdNeg == null || sCaravan == null)
            {
                Log.Error(FullCust3.Log3 + "Panhandling: member missing, nothing patched");
                return;
            }
            var h = MpCompat.harmony;
            h.Patch(beginM, prefix: new HarmonyMethod(typeof(FullPanhandlingCu3), nameof(BeginPrefix)) { priority = Priority.First });
            h.Patch(resolveM, prefix: new HarmonyMethod(typeof(FullPanhandlingCu3), nameof(PushPre)) { priority = Priority.First }, finalizer: new HarmonyMethod(typeof(FullPanhandlingCu3), nameof(PopFin)));
            h.Patch(assaultM, prefix: new HarmonyMethod(typeof(FullPanhandlingCu3), nameof(AssaultPre)) { priority = Priority.First }, finalizer: new HarmonyMethod(typeof(FullPanhandlingCu3), nameof(AssaultFin)));
            h.Patch(jumpM, prefix: new HarmonyMethod(typeof(FullPanhandlingCu3), nameof(JumpPre)) { priority = Priority.First });
            MP.RegisterSyncMethod(typeof(FullPanhandlingCu3), nameof(SyncedBegin));
            Log.Message(FullCust3.Log3 + "Panhandling: BeginBegging synced, ResolveSession/StartAssaultEncounterNow faction push, camera jump guard registered");
        }

        static bool BeginPrefix(Caravan caravan, Settlement settlement, IEnumerable<Pawn> crew, IEnumerable alms, object breakdown)
        {
            if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand) return true;
            var src = new List<Thing>(); var defs = new List<ThingDef>(); var stuffs = new List<string>();
            var ints = new List<int>(); var floats = new List<float>();
            float quality = (float)AccessTools.PropertyGetter(breakdownT, "TotalQuality").Invoke(breakdown, null);
            floats.Add(quality);
            foreach (var a in alms)
            {
                var st = (Thing)af[0].GetValue(a);
                int si = -1;
                if (st != null) { si = src.Count; src.Add(st); }
                defs.Add((ThingDef)af[1].GetValue(a));
                stuffs.Add(((ThingDef)af[2].GetValue(a))?.defName ?? "");
                var qv = (QualityCategory?)af[3].GetValue(a);
                ints.Add(qv.HasValue ? (int)qv.Value : -1); ints.Add((int)af[4].GetValue(a)); ints.Add((int)af[5].GetValue(a)); ints.Add(si);
                floats.Add((float)af[6].GetValue(a)); floats.Add((float)af[7].GetValue(a));
            }
            SyncedBegin(caravan, settlement, crew.ToList(), (Pawn)bdNeg.GetValue(breakdown), src, defs, stuffs, ints, floats);
            return false;
        }

        // 9 parameters on purpose: HarmonyLib MethodInvoker.GetHandler (used by MP's SyncMethod) throws ArgumentException "opcode" at 13 parameters (D2 cu3).
        // Per alms request: defs[i], stuffs[i] (defName or ""), ints[4i..4i+3] = quality(-1 none), hitPoints, count, index into src (-1 none), floats[1+2i], floats[2+2i] = value, mass; floats[0] = base quality.
        [SyncMethod]
        static void SyncedBegin(Caravan caravan, Settlement settlement, List<Pawn> crew, Pawn negotiator, List<Thing> src, List<ThingDef> defs, List<string> stuffs,
            List<int> ints, List<float> floats)
        {
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(almsT));
            for (int i = 0; i < defs.Count; i++)
            {
                var a = Activator.CreateInstance(almsT);
                int si = ints[4 * i + 3];
                af[0].SetValue(a, si < 0 ? null : src[si]); af[1].SetValue(a, defs[i]);
                af[2].SetValue(a, stuffs[i].Length == 0 ? null : DefDatabase<ThingDef>.GetNamedSilentFail(stuffs[i]));
                int qi = ints[4 * i];
                af[3].SetValue(a, qi < 0 ? (QualityCategory?)null : (QualityCategory)qi);
                af[4].SetValue(a, ints[4 * i + 1]); af[5].SetValue(a, ints[4 * i + 2]); af[6].SetValue(a, floats[1 + 2 * i]); af[7].SetValue(a, floats[2 + 2 * i]);
                list.Add(a);
            }
            var bd = Activator.CreateInstance(breakdownT);
            bdBase.SetValue(bd, floats[0]);
            bdNeg.SetValue(bd, negotiator);
            var inst = AccessTools.PropertyGetter(compT, "Instance").Invoke(null, null);
            beginM.Invoke(inst, new object[] { caravan, settlement, crew, list, bd });
        }

        static Faction SessionOwner(object session)
        {
            var f = (sCaravan.GetValue(session) as Caravan)?.Faction;
            return f != null && f.IsPlayer ? f : null;
        }

        static void PushPre(object session, out bool __state)
        {
            __state = false;
            if (!MP.IsInMultiplayer || RightPlayerFaction.PlayerFactionCount() < 2) return;
            var f = SessionOwner(session);
            if (f == null || f == Faction.OfPlayer) return;
            FullCust3.Push(null, f);
            __state = true;
        }

        static void PopFin(bool __state)
        {
            if (__state) FullCust3.Pop(null);
        }

        static void AssaultPre(object session, out bool __state)
        {
            PushPre(session, out __state);
            assaultOwner = MP.IsInMultiplayer ? SessionOwner(session) : null;
        }

        static void AssaultFin(bool __state)
        {
            assaultOwner = null;
            PopFin(__state);
        }

        static bool JumpPre()
        {
            var owner = assaultOwner;
            if (owner == null || !MP.IsInMultiplayer) return true;
            return owner == MP.RealPlayerFaction;
        }
    }
}

namespace Multiplayer.Compat
{
    /// <summary>Panhandling (acutus.begging): see FullPanhandlingCu3.</summary>
    [MpCompatFor("acutus.begging")]
    class FullPanhandling
    {
        public FullPanhandling(ModContentPack mod) => FullPanhandlingCu3.Init();
    }
}

namespace Multiplayer.Compat
{
    /// <summary>Semi Random Research (cruesoe.semirandompfork.continued): ResearchTracker is one WorldComponent with one shared offer list, and GetResearchableProjects
    /// (rolled from the world tick, under the spectator) reads Faction.OfPlayer.def.techLevel twice (plus a lambda capture). Each read becomes TechFaction():
    /// Faction.OfPlayer when it is a real player, else the lowest-loadID non-spectator player faction (same on every PC, no Rand). The shared offer list means one
    /// faction's tech level decides for all; UI buttons of MainTabWindow_NextResearch are NOT synced.</summary>
    [MpCompatFor("cruesoe.semirandompfork.continued")]
    class FullSemiRandomResearch
    {
        static int swapped = -1;

        public FullSemiRandomResearch(ModContentPack mod)
        {
            var m = AccessTools.Method("CM_Semi_Random_Research.ResearchTracker:GetResearchableProjects");
            if (m == null) { Log.Error(FullCust3.Log3 + "Semi Random Research: GetResearchableProjects not found"); return; }
            MpCompat.harmony.Patch(m, transpiler: new HarmonyMethod(typeof(FullSemiRandomResearch), nameof(Tr)));
            Log.Message(FullCust3.Log3 + "Semi Random Research: GetResearchableProjects tech level faction registered (" + swapped + " reads)");
        }

        public static Faction TechFaction()
        {
            var f = Faction.OfPlayer;
            if (!MP.IsInMultiplayer || RightPlayerFaction.PlayerFactionCount() < 2) return f;
            return FullCust3.LowestRealPlayerIfSpectator(f) ?? f;
        }

        static IEnumerable<CodeInstruction> Tr(IEnumerable<CodeInstruction> instr)
        {
            var list = instr.ToList();
            var me = AccessTools.Method(typeof(FullSemiRandomResearch), nameof(TechFaction));
            int n = 0;
            foreach (var ci in list)
                if (ci.Calls(FullCust3.OfPlayerGetter)) { ci.opcode = OpCodes.Call; ci.operand = me; n++; }
            swapped = n;
            return list;
        }
    }
}
