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
    /// <summary>FULL1127 CU4: Simple Warrants (pb3n.simplewarrants) full multiplayer pass. Part of SimpleWarrantsFull1127.
    /// Tab buttons (Accept, Decline, Compensate, Remove, Create) become synced statics keyed by the warrant loadID. The warrant that
    /// Create makes is built inside the synced call from the chosen kind / faction / settings, never from the UI preview object.
    /// Tick reads of the player faction and of the home map use an anchor on the warrant record (issuer / accepteer / target faction),
    /// the lowest loadID real player only as last resort. Threat points are never written from a UI frame.</summary>
    public partial class SimpleWarrantsFull1127
    {
        const string Tag = "[MPCompat - FULL1127 CU] ";
        static Type tWarrant, tManager, tTab, tPawnW, tArtW;
        static FieldInfo fInstance;
        static MethodInfo miGetId;
        static readonly string[] listNames = { "availableWarrants", "acceptedWarrants", "createdWarrants", "takenWarrants", "postponedWarrants" };
        static bool inPreview, inUiCreate;
        static readonly HashSet<Thing> previews = new HashSet<Thing>();
        public static int uiReplaced, tickReplaced;
        /// <summary>Transpiler sites replaced per patched method (D2 compares exact numbers).</summary>
        public static readonly Dictionary<string, int> sites = new Dictionary<string, int>();
        static int genDepth;
        static void Site(MethodBase orig) { string k = orig?.Name ?? "?"; sites[k] = (sites.TryGetValue(k, out var n) ? n : 0) + 1; }

        static void RegisterCu4(Type manager)
        {
            tManager = manager;
            tWarrant = AccessTools.TypeByName("SimpleWarrants.Warrant");
            tTab = AccessTools.TypeByName("SimpleWarrants.MainTabWindow_Warrants");
            tPawnW = AccessTools.TypeByName("SimpleWarrants.Warrant_Pawn");
            tArtW = AccessTools.TypeByName("SimpleWarrants.Warrant_Artifact");
            fInstance = AccessTools.Field(manager, "Instance");
            miGetId = AccessTools.Method(manager, "GetWarrantID");
            var me = typeof(SimpleWarrantsFull1127);
            MP.RegisterSyncMethod(me, nameof(SyncedAccept));
            MP.RegisterSyncMethod(me, nameof(SyncedCompensate));
            MP.RegisterSyncMethod(me, nameof(SyncedRemove));
            MP.RegisterSyncMethod(me, nameof(SyncedCreate));
            var h = MpCompat.harmony;
            // 1. tab buttons: UI calls go through synced statics
            var ui = new HarmonyMethod(me, nameof(TrUi));
            h.Patch(AccessTools.DeclaredMethod(tTab, "DoMenuSection"), transpiler: ui);
            foreach (var n in new[] { "DrawAcceptDeclineButtons", "DrawCompensateButton", "DrawRemoveWarrantButton" })
                h.Patch(AccessTools.DeclaredMethod(tWarrant, n), transpiler: ui);
            // 2. Create: the warrant is made in the synced call
            h.Patch(AccessTools.DeclaredMethod(tTab, "TryAddWarrant"), prefix: new HarmonyMethod(me, nameof(PreTryAdd)));
            var prev = new HarmonyMethod(me, nameof(PreCreationUi));
            var prevFin = new HarmonyMethod(me, nameof(FinCreationUi));
            h.Patch(AccessTools.DeclaredMethod(tTab, "DoWarrantCreation"), prefix: prev, finalizer: prevFin);
            // B2: the animal and artifact pickers make preview things outside DoWarrantCreation: they are previews too
            h.Patch(AccessTools.DeclaredMethod(AccessTools.TypeByName("SimpleWarrants.Dialog_SelectAnimal"), "DoWindowContents"), prefix: prev, finalizer: prevFin);
            h.Patch(AccessTools.DeclaredMethod(AccessTools.TypeByName("SimpleWarrants.Dialog_SelectArtifact"), "SelectArtifact"), prefix: prev, finalizer: prevFin);
            // B1: a preview pawn makes no relatives, redresses no world pawn, and passes nothing to the world
            h.Patch(AccessTools.DeclaredMethod(typeof(PawnGenerator), nameof(PawnGenerator.GeneratePawn), new[] { typeof(PawnGenerationRequest) }), prefix: new HarmonyMethod(me, nameof(PreGenRequest)));
            h.Patch(AccessTools.DeclaredMethod(typeof(RimWorld.Planet.WorldPawns), nameof(RimWorld.Planet.WorldPawns.PassToWorld)), prefix: new HarmonyMethod(me, nameof(PrePassToWorld)));
            // B5: the anchor faction (warrant owner, victim, or lowest real player) is pushed around the tick / world-tick sites
            var pushDlg = new HarmonyMethod(me, nameof(PrePushLowest));
            var pushVictim = new HarmonyMethod(me, nameof(PrePushVictim));
            var pushReturn = new HarmonyMethod(me, nameof(PrePushReturn));
            var popFin = new HarmonyMethod(me, nameof(FinPush));
            h.Patch(AccessTools.DeclaredMethod(manager, "GenerateRandomWarrant"), prefix: pushDlg, finalizer: popFin);
            h.Patch(AccessTools.DeclaredMethod(manager, "HandleAvailableWarrants"), prefix: pushDlg, finalizer: popFin);
            h.Patch(AccessTools.DeclaredMethod(manager, "PutWarrantOn"), prefix: pushVictim, finalizer: popFin);
            h.Patch(AccessTools.DeclaredMethod(AccessTools.TypeByName("SimpleWarrants.TransportersArrivalAction_ReturnWarrant"), "Arrived"), prefix: pushReturn, finalizer: popFin);
            h.Patch(AccessTools.DeclaredMethod(tTab, "CreateWarrant"), prefix: new HarmonyMethod(me, nameof(PreCreateUi)), finalizer: new HarmonyMethod(me, nameof(FinCreateUi)));
            h.Patch(AccessTools.DeclaredMethod(manager, "GetWarrantID"), prefix: new HarmonyMethod(me, nameof(PreGetId)));
            var genPre = new HarmonyMethod(me, nameof(PreGen));
            var genFin = new HarmonyMethod(me, nameof(FinGen));
            foreach (var m in typeof(PawnGenerator).GetMethods(BindingFlags.Static | BindingFlags.Public).Where(m => m.Name == nameof(PawnGenerator.GeneratePawn) && m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(PawnKindDef)))
                h.Patch(m, prefix: genPre, finalizer: genFin);
            foreach (var m in typeof(ThingMaker).GetMethods(BindingFlags.Static | BindingFlags.Public).Where(m => m.Name == nameof(ThingMaker.MakeThing) && m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(ThingDef)))
                h.Patch(m, prefix: genPre, finalizer: genFin);
            // 4. threat points: no write from the UI getter; filled in the tick
            h.Patch(AccessTools.PropertyGetter(tPawnW, "ThreatPoints"), prefix: new HarmonyMethod(me, nameof(PreThreat)));
            h.Patch(AccessTools.DeclaredMethod(tPawnW, "AcceptChance"), prefix: new HarmonyMethod(me, nameof(PreAcceptChance)));
            h.Patch(AccessTools.DeclaredMethod(tArtW, "AcceptChance"), prefix: new HarmonyMethod(me, nameof(PreAcceptChance)));
            h.Patch(AccessTools.DeclaredMethod(manager, "GameComponentTick"), postfix: new HarmonyMethod(me, nameof(PostTickThreat)));
            // 5 + 6. warrant anchored reads in the tick
            var tick = new HarmonyMethod(me, nameof(TrTick));
            foreach (var n in new[] { "HandleAcceptedWarrants", "HandleCreatedWarrants", "HandleFactionsTakenWarrants" })
                h.Patch(AccessTools.DeclaredMethod(manager, n), transpiler: tick);
            h.Patch(AccessTools.DeclaredMethod(manager, "HandleAvailableWarrants"), transpiler: new HarmonyMethod(me, nameof(TrAvailable)));
            h.Patch(AccessTools.DeclaredMethod(manager, "DoWealthScaling"), transpiler: new HarmonyMethod(me, nameof(TrArg0)));
            h.Patch(AccessTools.DeclaredMethod(manager, "MakeWarrantDialog"), transpiler: new HarmonyMethod(me, nameof(TrArg1)));
            h.Patch(AccessTools.DeclaredMethod(tWarrant, "CanPlayerReceive"), transpiler: new HarmonyMethod(me, nameof(TrArg0)));
            h.Patch(AccessTools.DeclaredMethod(tPawnW, "IsThreatForPlayer"), prefix: new HarmonyMethod(me, nameof(PreThreatForPlayer)));
            Log.Message(Tag + "Simple Warrants: tab buttons, Create, owner dialog, threat points and tick anchors registered");
        }

        // ---------- anchors ----------
        static Faction Spectator() => FullCust3.IsSpectator(Faction.OfPlayer) ? Faction.OfPlayer : null;
        static bool Real(Faction f) => f != null && f.IsPlayer && !FullCust3.IsSpectator(f);
        static object Fld(object o, string n) => o == null ? null : AccessTools.Field(o.GetType(), n)?.GetValue(o);

        /// <summary>The player faction a warrant record belongs to: issuer, accepteer, target's faction, then lowest loadID real player.</summary>
        public static Faction PlayerOf(object warrant)
        {
            if (!MP.IsInMultiplayer || warrant == null) return Faction.OfPlayer;
            if (Fld(warrant, "issuer") is Faction i && Real(i)) return i;
            if (Fld(warrant, "accepteer") is Faction a && Real(a)) return a;
            if (Fld(warrant, "thing") is Thing t && Real(t.Faction)) return t.Faction;
            return FullCust3.LowestRealPlayer() ?? Faction.OfPlayer;
        }

        static Faction OwnerOf(object warrant) => warrant != null && Fld(warrant, "issuer") is Faction i && Real(i) ? i : null;

        static Faction LocalRealPlayer()
        {
            try { return MP.RealPlayerFaction; } catch { return null; }
        }

        static Map HomeMapOfFaction(Faction f)
        {
            if (f == null) return null;
            foreach (var m in Find.Maps)
                if (m.ParentFaction == f && m.info?.parent is RimWorld.Planet.Settlement) return m;
            foreach (var m in Find.Maps)
                if (m.ParentFaction == f) return m;
            return null;
        }

        public static Map HomeMapOf(object warrant)
        {
            if (!MP.IsInMultiplayer) return Find.AnyPlayerHomeMap;
            return HomeMapOfFaction(PlayerOf(warrant)) ?? HomeMapLowest();
        }

        public static Map HomeMapLowest()
        {
            if (!MP.IsInMultiplayer) return Find.AnyPlayerHomeMap;
            return HomeMapOfFaction(FullCust3.LowestRealPlayer());
        }

        // ---------- transpilers ----------
        static readonly MethodInfo ofPlayer = AccessTools.PropertyGetter(typeof(Faction), nameof(Faction.OfPlayer));
        static readonly MethodInfo anyHome = AccessTools.PropertyGetter(typeof(Find), nameof(Find.AnyPlayerHomeMap));

        static IEnumerable<CodeInstruction> TrUi(IEnumerable<CodeInstruction> ins, MethodBase orig)
        {
            var mAccept = AccessTools.Method(typeof(SimpleWarrantsFull1127), nameof(UiAccept));
            var mComp = AccessTools.Method(typeof(SimpleWarrantsFull1127), nameof(UiCompensate));
            var mRem = AccessTools.Method(typeof(SimpleWarrantsFull1127), nameof(UiRemove));
            foreach (var i in ins)
            {
                var m = i.operand as MethodInfo;
                if ((i.opcode == OpCodes.Callvirt || i.opcode == OpCodes.Call) && m != null)
                {
                    if (m.Name == "DoAcceptAction" && tWarrant.IsAssignableFrom(m.DeclaringType)) { uiReplaced++; Site(orig); yield return new CodeInstruction(OpCodes.Call, mAccept) { labels = i.labels, blocks = i.blocks }; continue; }
                    if (m.Name == "DoCompensateAction" && tWarrant.IsAssignableFrom(m.DeclaringType)) { uiReplaced++; Site(orig); yield return new CodeInstruction(OpCodes.Call, mComp) { labels = i.labels, blocks = i.blocks }; continue; }
                    if (m.Name == "Remove" && m.DeclaringType.IsGenericType && m.DeclaringType.GetGenericTypeDefinition() == typeof(List<>)
                        && m.DeclaringType.GetGenericArguments()[0] == tWarrant) { uiReplaced++; Site(orig); yield return new CodeInstruction(OpCodes.Call, mRem) { labels = i.labels, blocks = i.blocks }; continue; }
                }
                yield return i;
            }
        }

        static IEnumerable<CodeInstruction> Replace(IEnumerable<CodeInstruction> ins, int arg, MethodBase orig, bool ofPlayerToo, bool homeToo, bool useLocal)
        {
            var mPlayer = AccessTools.Method(typeof(SimpleWarrantsFull1127), nameof(PlayerOf));
            var mHome = AccessTools.Method(typeof(SimpleWarrantsFull1127), nameof(HomeMapOf));
            int local = -1; FieldInfo viaField = null;
            if (useLocal)
            {
                var locals = orig.GetMethodBody().LocalVariables;
                var lv = locals.FirstOrDefault(v => tWarrant.IsAssignableFrom(v.LocalType));
                if (lv != null) local = lv.LocalIndex;
                else
                {
                    // B4: a lambda captures the warrant, so the compiler hoists it into a closure display class: Ldloc closure, Ldfld warrant
                    foreach (var v in locals)
                    {
                        var fld = v.LocalType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).FirstOrDefault(x => tWarrant.IsAssignableFrom(x.FieldType));
                        if (fld != null && v.LocalType.IsNested) { local = v.LocalIndex; viaField = fld; break; }
                    }
                }
            }
            foreach (var i in ins)
            {
                bool p = ofPlayerToo && i.Calls(ofPlayer), hm = homeToo && i.Calls(anyHome);
                if ((p || hm) && (useLocal ? local >= 0 : true))
                {
                    tickReplaced++; Site(orig);
                    var load = useLocal ? new CodeInstruction(OpCodes.Ldloc, local) : new CodeInstruction(arg == 0 ? OpCodes.Ldarg_0 : OpCodes.Ldarg_1);
                    load.labels = i.labels; load.blocks = i.blocks;
                    yield return load;
                    if (viaField != null) yield return new CodeInstruction(OpCodes.Ldfld, viaField);
                    yield return new CodeInstruction(OpCodes.Call, p ? mPlayer : mHome);
                    continue;
                }
                yield return i;
            }
        }

        static IEnumerable<CodeInstruction> TrTick(IEnumerable<CodeInstruction> ins, MethodBase orig) => Replace(ins, 0, orig, true, false, true);
        static IEnumerable<CodeInstruction> TrArg0(IEnumerable<CodeInstruction> ins, MethodBase orig) => Replace(ins, 0, orig, true, true, false);
        static IEnumerable<CodeInstruction> TrArg1(IEnumerable<CodeInstruction> ins, MethodBase orig) => Replace(ins, 1, orig, false, true, false);

        static IEnumerable<CodeInstruction> TrAvailable(IEnumerable<CodeInstruction> ins, MethodBase orig)
        {
            var mLow = AccessTools.Method(typeof(SimpleWarrantsFull1127), nameof(HomeMapLowest));
            foreach (var i in ins)
            {
                if (i.Calls(anyHome)) { tickReplaced++; Site(orig); yield return new CodeInstruction(OpCodes.Call, mLow) { labels = i.labels, blocks = i.blocks }; }
                else yield return i;
            }
        }

        // ---------- 1. tab buttons ----------
        static IList ListOf(int idx) => Fld(fInstance.GetValue(null), listNames[idx]) as IList;

        static object FindWarrant(string id, int onlyList = -1)
        {
            if (id == null) return null;
            for (int l = 0; l < listNames.Length; l++)
            {
                if (onlyList >= 0 && l != onlyList) continue;
                var list = ListOf(l);
                if (list == null) continue;
                foreach (var w in list) if ((string)Fld(w, "loadID") == id) return w;
            }
            return null;
        }

        public static void UiAccept(object w)
        {
            if (!MP.IsInMultiplayer) { AccessTools.Method(tWarrant, "DoAcceptAction").Invoke(w, null); return; }
            SyncedAccept((string)Fld(w, "loadID"));
        }

        public static void UiCompensate(object w)
        {
            if (!MP.IsInMultiplayer) { AccessTools.Method(tWarrant, "DoCompensateAction").Invoke(w, null); return; }
            SyncedCompensate((string)Fld(w, "loadID"));
        }

        public static bool UiRemove(IList list, object w)
        {
            if (!MP.IsInMultiplayer) { bool had = list.Contains(w); list.Remove(w); return had; }
            for (int i = 0; i < listNames.Length; i++)
                if (ReferenceEquals(list, ListOf(i))) { SyncedRemove((string)Fld(w, "loadID"), i); return true; }
            return false;
        }

        static void SyncedAccept(string loadID)
        {
            var w = FindWarrant(loadID, 0);
            if (w != null) AccessTools.Method(tWarrant, "DoAcceptAction").Invoke(w, null);
        }

        static void SyncedCompensate(string loadID)
        {
            var w = FindWarrant(loadID);
            if (w == null) return;
            var f = PlayerOf(w);
            bool push = false;
            if (MP.IsInMultiplayer && f != null && f != Faction.OfPlayer) { FullCust3.PushMaps(f); push = true; }   // B2: maps' Home area of the owner
            try { AccessTools.Method(tWarrant, "DoCompensateAction").Invoke(w, null); }
            finally { if (push) FullCust3.PopMaps(); }
        }

        static void SyncedRemove(string loadID, int listIdx)
        {
            if (listIdx < 0 || listIdx >= listNames.Length) return;
            var w = FindWarrant(loadID, listIdx);
            if (w != null) ListOf(listIdx).Remove(w);
        }

        // ---------- 2. Create ----------
        // B1: a preview only when MP is in the interface; during a long event / reload UniqueIds hands out real ids, so nothing is made then
        static bool PreCreationUi(out bool __state)
        {
            __state = inPreview;
            if (!MP.IsInMultiplayer) return true;
            if (!FullCust3.InInterface()) return false;
            inPreview = true;
            return true;
        }

        // B5: AcceptChance written the unsaved cache from a UI frame (one PC only). In the interface it is computed without caching;
        // in the tick the original runs and fills the cache on every PC.
        static bool PreAcceptChance(object __instance, ref float __result)
        {
            if (!MP.IsInMultiplayer || !FullCust3.InInterface()) return true;
            var cached = Fld(__instance, "acceptChanceCached") as float?;
            if (cached.HasValue) { __result = cached.Value; return false; }
            var thing = Fld(__instance, "thing") as Thing;
            if (thing == null) { __result = 0f; return false; }
            float reward = tPawnW.IsInstanceOfType(__instance)
                ? Math.Max((int)Fld(__instance, "rewardForDead"), (int)Fld(__instance, "rewardForLiving"))
                : Convert.ToSingle(Fld(__instance, "reward"));
            __result = reward / thing.MarketValue;
            return false;
        }
        static void FinCreationUi(bool __state) { inPreview = __state; }
        static void PreCreateUi() { if (MP.IsInMultiplayer) inUiCreate = true; }
        static void FinCreateUi() { inUiCreate = false; }
        static bool PreGetId(ref string __result)
        {
            if (!inUiCreate) return true;
            __result = "UI_PREVIEW";
            return false;
        }
        // only the top-level preview thing is listed (not the apparel and weapons made inside GeneratePawn)
        static void PreGen(out bool __state) { __state = inPreview; if (inPreview) genDepth++; }
        static void FinGen(bool __state, object __result)
        {
            if (!__state) return;
            genDepth--;
            if (genDepth == 0 && __result is Thing t) { if (previews.Count > 256) previews.Clear(); previews.Add(t); }
        }

        // B1: a preview pawn (one PC, UI frame) must leave nothing in synced state
        static void PreGenRequest(ref PawnGenerationRequest request)
        {
            if (!inPreview) return;
            request.CanGeneratePawnRelations = false;   // no generated relatives (they are passed to the world pawns)
            request.ForceGenerateNewPawn = true;         // never pull a world pawn out of the world to redress it
        }
        static bool PrePassToWorld() => !inPreview;

        // B5: push the anchor faction (force) around a body, pop in a finalizer
        static void PushFor(Faction f, out Faction state)
        {
            state = null;
            if (!MP.IsInMultiplayer || f == null || f == Faction.OfPlayer) return;
            FullCust3.Push(null, f);
            state = f;
        }
        // no anchor on the warrant: the lowest loadID real player, the same one HomeMapLowest uses
        static void PrePushLowest(out Faction __state) => PushFor(FullCust3.LowestRealPlayer(), out __state);
        // the victim of a new warrant is the player pawn's faction
        static void PrePushVictim(Pawn __0, out Faction __state) => PushFor(Real(__0?.Faction) ? __0.Faction : null, out __state);
        // the returning player is the warrant's accepteer
        static void PrePushReturn(object __instance, out Faction __state) => PushFor(PlayerOf(Fld(__instance, "warrant")), out __state);
        static void FinPush(Faction __state) { if (__state != null) FullCust3.Pop(null); }

        static bool PreTryAdd(object __instance, object __0, string __1)
        {
            if (!MP.IsInMultiplayer || __0 == null) return true;
            if (!__1.NullOrEmpty()) return true; // fail message path is UI only
            var thing = Fld(__0, "thing") as Thing;
            if (thing == null) return false;
            bool preview = previews.Remove(thing);
            int type = tArtW.IsInstanceOfType(__0) ? 1 : 0;
            int thingId = preview ? -1 : thing.thingIDNumber;
            string defName = type == 1 ? thing.def.defName : (thing as Pawn)?.kindDef?.defName;
            int factionId = thing.Faction?.loadID ?? -1;
            int reward = type == 1 ? (int)Fld(__0, "reward") : 0;
            int death = type == 0 ? (int)Fld(__0, "rewardForDead") : 0;
            int capture = type == 0 ? (int)Fld(__0, "rewardForLiving") : 0;
            SyncedCreate(type, thingId, defName, factionId, type == 0 ? (string)Fld(__0, "reason") : null, (string)Fld(__0, "message"), reward, death, capture);
            Traverse.Create(__instance).Field("curMessage").SetValue("");
            return false;
        }

        static Thing FindThing(int id)
        {
            foreach (var p in Find.WorldPawns.AllPawnsAliveOrDead) if (p.thingIDNumber == id) return p;
            foreach (var p in PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead)
            {
                if (p.thingIDNumber == id) return p;
                if (p.inventory?.innerContainer != null) foreach (var t in p.inventory.innerContainer) if (t.thingIDNumber == id) return t;
            }
            foreach (var m in Find.Maps) foreach (var t in m.listerThings.AllThings) if (t.thingIDNumber == id) return t;
            return null;
        }

        static void SyncedCreate(int type, int thingId, string defName, int factionId, string reason, string message, int reward, int death, int capture)
        {
            var mgr = fInstance.GetValue(null);
            var created = ListOf(2);
            if (created == null || created.Count >= 10) return;
            var issuer = Faction.OfPlayer;
            Thing thing = thingId >= 0 ? FindThing(thingId) : null;
            if (thing == null)
            {
                if (type == 1)
                {
                    var def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
                    if (def == null) return;
                    thing = ThingMaker.MakeThing(def);
                }
                else
                {
                    var kind = DefDatabase<PawnKindDef>.GetNamedSilentFail(defName);
                    if (kind == null) return;
                    var faction = factionId >= 0 ? Find.FactionManager.AllFactionsListForReading.FirstOrDefault(f => f.loadID == factionId) : null;
                    thing = PawnGenerator.GeneratePawn(kind, kind.RaceProps.Animal ? null : faction);
                }
            }
            if (thing == null) return;
            if (type == 1 ? reward <= 0 : (death < 0 || capture < 0)) return;
            var w = Activator.CreateInstance(type == 1 ? tArtW : tPawnW);
            Traverse.Create(w).Field("loadID").SetValue((string)miGetId.Invoke(mgr, null));
            Traverse.Create(w).Field("issuer").SetValue(issuer);
            Traverse.Create(w).Field("createdTick").SetValue(Find.TickManager.TicksGame);
            Traverse.Create(w).Field("thing").SetValue(thing);
            Traverse.Create(w).Field("message").SetValue(message);
            if (type == 1) Traverse.Create(w).Field("reward").SetValue(reward);
            else
            {
                Traverse.Create(w).Field("rewardForLiving").SetValue(capture);
                Traverse.Create(w).Field("rewardForDead").SetValue(death);
                Traverse.Create(w).Field("reason").SetValue(reason);
            }
            AccessTools.Method(tWarrant, "OnCreate").Invoke(w, null);
            created.Add(w);
        }

        // ---------- 4. threat points ----------
        static float ThreatNow(object w)
        {
            var f = PlayerOf(w);
            bool push = MP.IsInMultiplayer && f != null && f != Faction.OfPlayer;
            if (push) FullCust3.Push(null, f);
            try { return StorytellerUtility.DefaultThreatPointsNow(Find.World); }
            finally { if (push) FullCust3.Pop(null); }
        }

        static bool PreThreat(object __instance, ref float __result)
        {
            if (!MP.IsInMultiplayer) return true;
            float tp = (float)Fld(__instance, "threatPoints");
            __result = tp > 0 ? tp : StorytellerUtility.DefaultThreatPointsNow(Find.World); // read only, no write from a UI frame
            return false;
        }

        static void PostTickThreat(object __instance)
        {
            if (!MP.IsInMultiplayer || Find.TickManager.TicksGame % 60 != 7) return;
            for (int l = 0; l < listNames.Length; l++)
            {
                var list = ListOf(l);
                if (list == null) continue;
                foreach (var w in list)
                    if (tPawnW.IsInstanceOfType(w) && (float)Fld(w, "threatPoints") <= 0f)
                    {
                        float v = ThreatNow(w);
                        if (v > 0f) AccessTools.Field(tPawnW, "threatPoints").SetValue(w, v);
                    }
            }
        }

        // ---------- 6. IsThreatForPlayer: "is the target a real player's pawn" ----------
        static bool PreThreatForPlayer(object __instance, ref bool __result)
        {
            if (!MP.IsInMultiplayer) return true;
            var thing = Fld(__instance, "thing") as Thing;
            var pawn = thing is Corpse c ? c.InnerPawn : thing as Pawn;
            __result = pawn != null && Real(pawn.Faction);
            return false;
        }
    }
}
