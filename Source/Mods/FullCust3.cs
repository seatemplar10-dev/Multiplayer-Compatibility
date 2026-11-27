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
        public static Faction LowestRealPlayer()
        {
            if (!MP.IsInMultiplayer) return null;
            Faction spectator = null;
            try { spectator = Traverse.CreateWithType("Multiplayer.Client.Multiplayer").Property("WorldComp").Field("spectatorFaction").GetValue<Faction>(); } catch { }
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
