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
            h.Patch(AccessTools.DeclaredMethod(tTab, "DoWarrantCreation"), prefix: new HarmonyMethod(me, nameof(PreCreationUi)), finalizer: new HarmonyMethod(me, nameof(FinCreationUi)));
            h.Patch(AccessTools.DeclaredMethod(tTab, "CreateWarrant"), prefix: new HarmonyMethod(me, nameof(PreCreateUi)), finalizer: new HarmonyMethod(me, nameof(FinCreateUi)));
            h.Patch(AccessTools.DeclaredMethod(manager, "GetWarrantID"), prefix: new HarmonyMethod(me, nameof(PreGetId)));
            var preview = new HarmonyMethod(me, nameof(PostPreviewMade));
            foreach (var m in typeof(PawnGenerator).GetMethods(BindingFlags.Static | BindingFlags.Public).Where(m => m.Name == nameof(PawnGenerator.GeneratePawn) && m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(PawnKindDef)))
                h.Patch(m, postfix: preview);
            foreach (var m in typeof(ThingMaker).GetMethods(BindingFlags.Static | BindingFlags.Public).Where(m => m.Name == nameof(ThingMaker.MakeThing) && m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(ThingDef)))
                h.Patch(m, postfix: preview);
            // 4. threat points: no write from the UI getter; filled in the tick
            h.Patch(AccessTools.PropertyGetter(tPawnW, "ThreatPoints"), prefix: new HarmonyMethod(me, nameof(PreThreat)));
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
            try { return Traverse.CreateWithType("Multiplayer.Client.Multiplayer").Property("RealPlayerFaction").GetValue<Faction>(); } catch { return null; }
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

        static IEnumerable<CodeInstruction> TrUi(IEnumerable<CodeInstruction> ins)
        {
            var mAccept = AccessTools.Method(typeof(SimpleWarrantsFull1127), nameof(UiAccept));
            var mComp = AccessTools.Method(typeof(SimpleWarrantsFull1127), nameof(UiCompensate));
            var mRem = AccessTools.Method(typeof(SimpleWarrantsFull1127), nameof(UiRemove));
            foreach (var i in ins)
            {
                var m = i.operand as MethodInfo;
                if ((i.opcode == OpCodes.Callvirt || i.opcode == OpCodes.Call) && m != null)
                {
                    if (m.Name == "DoAcceptAction" && tWarrant.IsAssignableFrom(m.DeclaringType)) { uiReplaced++; yield return new CodeInstruction(OpCodes.Call, mAccept) { labels = i.labels, blocks = i.blocks }; continue; }
                    if (m.Name == "DoCompensateAction" && tWarrant.IsAssignableFrom(m.DeclaringType)) { uiReplaced++; yield return new CodeInstruction(OpCodes.Call, mComp) { labels = i.labels, blocks = i.blocks }; continue; }
                    if (m.Name == "Remove" && m.DeclaringType.IsGenericType && m.DeclaringType.GetGenericTypeDefinition() == typeof(List<>)
                        && m.DeclaringType.GetGenericArguments()[0] == tWarrant) { uiReplaced++; yield return new CodeInstruction(OpCodes.Call, mRem) { labels = i.labels, blocks = i.blocks }; continue; }
                }
                yield return i;
            }
        }

        static IEnumerable<CodeInstruction> Replace(IEnumerable<CodeInstruction> ins, int arg, MethodBase orig, bool ofPlayerToo, bool homeToo, bool useLocal)
        {
            var mPlayer = AccessTools.Method(typeof(SimpleWarrantsFull1127), nameof(PlayerOf));
            var mHome = AccessTools.Method(typeof(SimpleWarrantsFull1127), nameof(HomeMapOf));
            int local = -1;
            if (useLocal)
            {
                var lv = orig.GetMethodBody().LocalVariables.FirstOrDefault(v => tWarrant.IsAssignableFrom(v.LocalType));
                if (lv != null) local = lv.LocalIndex;
            }
            foreach (var i in ins)
            {
                bool p = ofPlayerToo && i.Calls(ofPlayer), hm = homeToo && i.Calls(anyHome);
                if ((p || hm) && (useLocal ? local >= 0 : true))
                {
                    tickReplaced++;
                    var load = useLocal ? new CodeInstruction(OpCodes.Ldloc, local) : new CodeInstruction(arg == 0 ? OpCodes.Ldarg_0 : OpCodes.Ldarg_1);
                    load.labels = i.labels; load.blocks = i.blocks;
                    yield return load;
                    yield return new CodeInstruction(OpCodes.Call, p ? mPlayer : mHome);
                    continue;
                }
                yield return i;
            }
        }

        static IEnumerable<CodeInstruction> TrTick(IEnumerable<CodeInstruction> ins, MethodBase orig) => Replace(ins, 0, orig, true, false, true);
        static IEnumerable<CodeInstruction> TrArg0(IEnumerable<CodeInstruction> ins, MethodBase orig) => Replace(ins, 0, orig, true, true, false);
        static IEnumerable<CodeInstruction> TrArg1(IEnumerable<CodeInstruction> ins, MethodBase orig) => Replace(ins, 1, orig, false, true, false);

        static IEnumerable<CodeInstruction> TrAvailable(IEnumerable<CodeInstruction> ins)
        {
            var mLow = AccessTools.Method(typeof(SimpleWarrantsFull1127), nameof(HomeMapLowest));
            foreach (var i in ins)
            {
                if (i.Calls(anyHome)) { tickReplaced++; yield return new CodeInstruction(OpCodes.Call, mLow) { labels = i.labels, blocks = i.blocks }; }
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
            if (w != null) AccessTools.Method(tWarrant, "DoCompensateAction").Invoke(w, null);
        }

        static void SyncedRemove(string loadID, int listIdx)
        {
            if (listIdx < 0 || listIdx >= listNames.Length) return;
            var w = FindWarrant(loadID, listIdx);
            if (w != null) ListOf(listIdx).Remove(w);
        }

        // ---------- 2. Create ----------
        static void PreCreationUi() { if (MP.IsInMultiplayer) inPreview = true; }
        static void FinCreationUi() { inPreview = false; }
        static void PreCreateUi() { if (MP.IsInMultiplayer) inUiCreate = true; }
        static void FinCreateUi() { inUiCreate = false; }
        static bool PreGetId(ref string __result)
        {
            if (!inUiCreate) return true;
            __result = "UI_PREVIEW";
            return false;
        }
        static void PostPreviewMade(object __result) { if (inPreview && __result is Thing t) previews.Add(t); }

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
