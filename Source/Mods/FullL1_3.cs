using System;
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
    // FULL1127 L1 batch 3 (FIX_PATCH_1). One class per mod, each logs one line so a boot log shows it.

    /// <summary>Choose Where To Land (kearril.choosewheretoland). TravellingTransportersArrived is called from the world tick on every PC
    /// and opens a Targeter on each; the chosen cell is applied by a local callback only. Simplest shape that makes both PCs run the same
    /// code on the same data: in multiplayer skip the targeter and use the mod's own "cancel" fallback spot (close raid drop centre).</summary>
    [MpCompatFor("kearril.choosewheretoland")]
    public class ChooseWhereToLandFull1127
    {
        public ChooseWhereToLandFull1127(ModContentPack mod)
        {
            var type = AccessTools.TypeByName("ChooseWhereToLand.PawnsArrivalModeWorker_ChooseWhereToLand");
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(type, "TravellingTransportersArrived"),
                prefix: new HarmonyMethod(typeof(ChooseWhereToLandFull1127), nameof(PreArrived)));
            Log.Message("[MPCompat - FULL1127 L1] Choose Where To Land: TravellingTransportersArrived uses the default spot in multiplayer, registered");
        }

        private static bool PreArrived(List<ActiveTransporterInfo> transporters, Map map)
        {
            if (!MP.IsInMultiplayer) return true;
            if (transporters.IsShuttle())
            {
                var transporter = transporters.FirstOrDefault();
                if (!DropCellFinder.TryFindRaidDropCenterClose(out var spot, map))
                    spot = DropCellFinder.FindRaidDropCenterDistant(map, allowRoofed: true, allowWater: false);
                TransportersArrivalActionUtility.DropShuttle(transporter, map, spot);
            }
            else
            {
                if (!DropCellFinder.TryFindRaidDropCenterClose(out var spot, map))
                    spot = DropCellFinder.FindRaidDropCenterDistant(map);
                TransportersArrivalActionUtility.DropTravellingDropPods(new List<ActiveTransporterInfo>(transporters), spot, map);
            }
            return false;
        }
    }

    /// <summary>Even Storage (custom.evenstorage). The gizmo toggleAction is a lambda over an iterator closure and writes the map component
    /// (SetEnabled) and runs InstantRedistribute on one PC. The toggle is replaced by one that calls a synced static method taking the
    /// storage building: it flips the same state on every PC, then runs the mod's own InstantRedistribute and TidyStorage notification.</summary>
    [MpCompatFor("custom.evenstorage")]
    public class EvenStorageFull1127
    {
        private static Type patchType;
        private static Type compType;
        private static Type modConfigType;
        private static MethodInfo isEnabled, setEnabled, instantRedistribute, notifyTidy;

        public EvenStorageFull1127(ModContentPack mod)
        {
            patchType = AccessTools.TypeByName("EvenStorage.Patch_GetGizmos");
            compType = AccessTools.TypeByName("EvenStorage.EvenStorageMapComponent");
            modConfigType = AccessTools.TypeByName("EvenStorage.EvenStorageModConfig");
            isEnabled = AccessTools.DeclaredMethod(compType, "IsEnabled");
            setEnabled = AccessTools.DeclaredMethod(compType, "SetEnabled");
            instantRedistribute = AccessTools.DeclaredMethod(patchType, "InstantRedistribute");
            notifyTidy = AccessTools.DeclaredMethod(patchType, "NotifyTidyStorageToggle");

            MP.RegisterSyncMethod(typeof(EvenStorageFull1127), nameof(SyncedToggle));
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(patchType, "Postfix"),
                postfix: new HarmonyMethod(typeof(EvenStorageFull1127), nameof(PostGizmos)));
            Log.Message("[MPCompat - FULL1127 L1] Even Storage: distribute-evenly toggle synced");
        }

        private static void PostGizmos(ref IEnumerable<Gizmo> __result, Building_Storage __1)
        {
            if (!MP.IsInMultiplayer) return;
            __result = Wrap(__result, __1);
        }

        private static IEnumerable<Gizmo> Wrap(IEnumerable<Gizmo> gizmos, Building_Storage storage)
        {
            string label = "ES_DistributeEvenly".Translate();
            foreach (var g in gizmos)
            {
                if (g is Command_Toggle toggle && toggle.defaultLabel == label)
                    toggle.toggleAction = () => SyncedToggle(storage);
                yield return g;
            }
        }

        private static void SyncedToggle(Building_Storage storage)
        {
            if (storage?.Map == null) return;
            var comp = storage.Map.components.FirstOrDefault(c => c.GetType() == compType);
            if (comp == null) return;
            // same relative toggle as the mod: new state = not the state of the clicked building
            bool on = !(bool)isEnabled.Invoke(comp, new object[] { storage.thingIDNumber });
            var group = ((IStorageGroupMember)storage).Group;
            if (group == null) return;
            foreach (var m in group.members)
                if (m is Building_Storage { Spawned: true } b)
                    setEnabled.Invoke(comp, new object[] { b.thingIDNumber, on });
            var settings = AccessTools.Property(modConfigType, "Settings")?.GetValue(null);
            var mode = settings == null ? null : AccessTools.Field(settings.GetType(), "distributeMode")?.GetValue(settings);
            if (on && mode != null && mode.ToString() == "Instant")
                instantRedistribute.Invoke(null, new object[] { group, storage.Map });
            try { notifyTidy.Invoke(null, new object[] { storage, on }); }
            catch (Exception e) { Log.Warning("[MPCompat - FULL1127 L1] Even Storage: TidyStorage notify failed: " + e.GetType().Name); }
        }
    }

    /// <summary>Simple Warrants (pb3n.simplewarrants). The warrant settings are class A (same files on both PCs). MakeWarrantDialog (called
    /// from the game component tick, on every PC) builds a dialog whose option actions destroy silver, kill the target, start a raid or
    /// spawn visitors. P3SW2 (B3): the open dialog is a saved record (SwDlg: the taken warrant, scribed in the manager's ExposeData),
    /// keyed by the warrant loadID. Every PC records it in the tick and closes the mod's window. The warrant OWNER's PC (issuer, else the
    /// lowest real player) opens the dialog from the UI frame, also after a join or reload. A click sends SyncedChoice(loadID, index);
    /// every PC rebuilds the options by running the original MakeWarrantDialog again under the same Rand seed (same Dead / reward roll)
    /// and the owner faction, and runs the chosen action. Owner offline: the record stays in the save, no PC resolves it, the dialog
    /// opens when the owner is back. The same on every PC, nothing is dropped.</summary>
    [MpCompatFor("pb3n.simplewarrants")]
    public partial class SimpleWarrantsFull1127
    {
        public class SwDlg : IExposable
        {
            public IExposable warrant;
            public void ExposeData() { Scribe_Deep.Look(ref warrant, "warrant"); }
            public string LoadID => warrant == null ? null : (string)Fld(warrant, "loadID");
        }

        private sealed class MakeCtx { public Faction pushed; public bool rand; }

        private static readonly Dictionary<string, Window> windows = new Dictionary<string, Window>();
        private static List<SwDlg> dlgs = new List<SwDlg>();
        private static Game dlgGame;
        // B3: record ids whose choice was clicked on this PC and not yet resolved; PostUiRoot does not reopen those (local, UI only)
        private static readonly HashSet<string> clicked = new HashSet<string>();
        private static int makeMode;  // 0 tick (the real dialog), 1 UI show, 2 capture for a synced choice
        private static List<DiaOption> captured;
        private static FieldInfo curNodeField;
        private static MethodInfo miMake;

        public SimpleWarrantsFull1127(ModContentPack mod)
        {
            curNodeField = AccessTools.Field(typeof(Dialog_NodeTree), "curNode");
            var type = AccessTools.TypeByName("SimpleWarrants.WarrantsManager");
            miMake = AccessTools.DeclaredMethod(type, "MakeWarrantDialog");
            MP.RegisterSyncMethod(typeof(SimpleWarrantsFull1127), nameof(SyncedChoice));
            var me = typeof(SimpleWarrantsFull1127);
            MpCompat.harmony.Patch(miMake, prefix: new HarmonyMethod(me, nameof(PreMake)),
                postfix: new HarmonyMethod(me, nameof(PostMakeDialog)), finalizer: new HarmonyMethod(me, nameof(FinMake)));
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(type, "ExposeData"), postfix: new HarmonyMethod(me, nameof(PostExpose)));
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(UIRoot_Play), nameof(UIRoot_Play.UIRootOnGUI)), postfix: new HarmonyMethod(me, nameof(PostUiRoot)));
            Log.Message("[MPCompat - FULL1127 L1] Simple Warrants: MakeWarrantDialog is a saved record, choices synced by warrant loadID");
            RegisterCu4(type);
        }

        private static List<SwDlg> Dlgs()
        {
            if (dlgGame != Current.Game) { dlgGame = Current.Game; dlgs = new List<SwDlg>(); windows.Clear(); }
            return dlgs;
        }

        private static void PostExpose()
        {
            // B5: a record whose warrant or target is gone (world pawn vanished) is dropped: it would throw on save and in the UI
            if (Scribe.mode == LoadSaveMode.Saving || Scribe.mode == LoadSaveMode.PostLoadInit) PruneDead();
            var list = Dlgs();
            if (Scribe.mode == LoadSaveMode.Saving && list.Count == 0) return;
            var l = Scribe.mode == LoadSaveMode.Saving ? list : null;
            Scribe_Collections.Look(ref l, "mpcSwDialogs", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.LoadingVars) { dlgs = l ?? new List<SwDlg>(); windows.Clear(); }
        }

        private static void PruneDead()
        {
            if (dlgs == null) return;
            dlgs.RemoveAll(d => d == null || d.warrant == null || Fld(d.warrant, "thing") == null);
        }

        private static void PreMake(object __0, out MakeCtx __state)
        {
            __state = null;
            if (!MP.IsInMultiplayer || __0 == null) return;
            var c = new MakeCtx();
            __state = c;   // B4: set before any push, so FinMake always sees the context
            var f = PlayerOf(__0);
            if (f != null && f != Faction.OfPlayer) { FullCust3.PushMaps(f); c.pushed = f; }
            Rand.PushState(GenText.StableStringHash((string)Fld(__0, "loadID") ?? "") ^ 1127);
            c.rand = true;
        }

        private static void FinMake(MakeCtx __state)
        {
            if (__state == null) return;
            try { if (__state.rand) Rand.PopState(); }
            finally { if (__state.pushed != null) FullCust3.PopMaps(); }
        }

        private static void PostMakeDialog(object __0)
        {
            if (!MP.IsInMultiplayer || __0 == null) return;
            var win = Find.WindowStack.Windows.LastOrDefault(w => w is Dialog_NodeTree);
            if (win == null) return;
            var node = curNodeField.GetValue(win) as DiaNode;
            if (node == null) return;
            string id = (string)Fld(__0, "loadID");
            if (makeMode == 2) { captured = node.options.ToList(); Find.WindowStack.TryRemove(win, false); return; }
            if (makeMode == 1)
            {
                for (int i = 0; i < node.options.Count; i++)
                {
                    int idx = i;
                    if (node.options[idx].action != null) node.options[idx].action = () => { clicked.Add(id); SyncedChoice(id, idx); };
                }
                windows[id] = win;
                return;
            }
            // tick, every PC: record the open dialog, the owner's PC shows it from the UI frame
            var list = Dlgs();
            list.RemoveAll(d => d.LoadID == id);
            list.Add(new SwDlg { warrant = (IExposable)__0 });
            win.Close(false);
        }

        private static void WithArchiveTrim(Action a)
        {
            var arch = Find.Archive.ArchivablesListForReading;
            int n = arch.Count;
            try { a(); }
            finally { while (arch.Count > n) arch.RemoveAt(arch.Count - 1); }
        }

        private static void PostUiRoot()
        {
            if (!MP.IsInMultiplayer || dlgs == null || dlgs.Count == 0 || Current.ProgramState != ProgramState.Playing) return;
            if (UnityEngine.Event.current == null || UnityEngine.Event.current.type != UnityEngine.EventType.Repaint || UnityEngine.Time.frameCount % 20 != 0) return;
            // B1: only in the interface (not in a long event, not reloading): else UniqueIds hands out real ids on this PC only
            if (!FullCust3.InInterface()) return;
            var local = LocalRealPlayer();
            if (local == null) return;
            if (clicked.Count > 0) clicked.RemoveWhere(cid => !dlgs.Any(d => d != null && d.LoadID == cid));
            foreach (var st in Dlgs().ToList())
            {
                string id = st.LoadID;
                if (id == null || PlayerOf(st.warrant) != local) continue;
                if (clicked.Contains(id)) continue;
                if (windows.TryGetValue(id, out var w) && Find.WindowStack.IsOpen(w)) continue;
                makeMode = 1;
                try { WithArchiveTrim(() => miMake.Invoke(fInstance.GetValue(null), new object[] { st.warrant })); }
                catch (Exception e) { Log.Warning("[MPCompat - FULL1127 L1] Simple Warrants: could not open the dialog for " + id + ": " + e.GetType().Name); }
                finally { makeMode = 0; }
            }
        }

        private static void SyncedChoice(string loadID, int index)
        {
            var st = Dlgs().FirstOrDefault(d => d.LoadID == loadID);
            if (st == null) return;
            var f = PlayerOf(st.warrant);
            bool push = false;
            if (f != null && f != Faction.OfPlayer) { FullCust3.PushMaps(f); push = true; }
            try
            {
                makeMode = 2; captured = null;
                try { WithArchiveTrim(() => miMake.Invoke(fInstance.GetValue(null), new object[] { st.warrant })); }
                finally { makeMode = 0; }
                var opts = captured; captured = null;
                if (opts == null || index < 0 || index >= opts.Count) return;
                var opt = opts[index];
                if (opt.disabled) { clicked.Remove(loadID); return; }   // e.g. not enough silver: same answer on every PC, the record stays
                Dlgs().Remove(st);
                // B3: no close sound (it would run on one PC only, inside the command)
                if (windows.TryGetValue(loadID, out var w)) { windows.Remove(loadID); if (Find.WindowStack.IsOpen(w)) Find.WindowStack.TryRemove(w, false); }
                opt.action?.Invoke();
            }
            finally
            {
                if (push) FullCust3.PopMaps();
                if (Dlgs().Contains(st)) clicked.Remove(loadID);
            }
        }
    }

    /// <summary>Fireflies Two (scurvyez.firefliestwo). StateHandler.IsActive gates ParticlesSpawned on WorldRendererUtility.DrawingMap and
    /// Find.CurrentMap == map (the camera), and its static _ext is read from Find.CurrentMap once. JoyGiver_ChaseFireflies (sim) needs
    /// ParticlesSpawned. In multiplayer the gate is dropped: the camera tests are replaced by helpers and _ext is read from the map's biome.</summary>
    [MpCompatFor("scurvyez.firefliestwo")]
    public class FirefliesTwoFull1127
    {
        private static Type extType;
        private static MethodInfo getExt;
        public static int replacedDrawing, replacedCurrentMap;

        public FirefliesTwoFull1127(ModContentPack mod)
        {
            var type = AccessTools.TypeByName("FirefliesTwoO.StateHandler");
            extType = AccessTools.TypeByName("FirefliesTwoO.NightlySpawningExtension");
            getExt = typeof(Def).GetMethod("GetModExtension").MakeGenericMethod(extType);
            var target = AccessTools.DeclaredMethod(type, "IsActive");
            MpCompat.harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(FirefliesTwoFull1127), nameof(PreIsActive)),
                transpiler: new HarmonyMethod(typeof(FirefliesTwoFull1127), nameof(Transpile)));
            extField = AccessTools.DeclaredField(type, "_ext");
            Log.Message("[MPCompat - FULL1127 L1] Fireflies Two: IsActive camera gate removed in multiplayer (DrawingMap x" + replacedDrawing + ", CurrentMap x" + replacedCurrentMap + ")");
        }

        private static FieldInfo extField;

        private static void PreIsActive(Map map)
        {
            if (!MP.IsInMultiplayer || map?.Biome == null) return;
            extField.SetValue(null, getExt.Invoke(map.Biome, null));
        }

        public static bool DrawingMapMp() => MP.IsInMultiplayer || WorldRendererUtility.DrawingMap;
        public static Map CurrentMapFor(Map map) => MP.IsInMultiplayer ? map : Find.CurrentMap;

        private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            var drawing = AccessTools.PropertyGetter(typeof(WorldRendererUtility), "DrawingMap");
            var cur = AccessTools.PropertyGetter(typeof(Find), "CurrentMap");
            foreach (var ins in instructions)
            {
                if (ins.Calls(drawing)) { replacedDrawing++; yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(FirefliesTwoFull1127), nameof(DrawingMapMp))) { labels = ins.labels, blocks = ins.blocks }; }
                else if (ins.Calls(cur)) { replacedCurrentMap++; yield return new CodeInstruction(OpCodes.Ldarg_0) { labels = ins.labels, blocks = ins.blocks }; yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(FirefliesTwoFull1127), nameof(CurrentMapFor))); }
                else yield return ins;
            }
        }
    }

    /// <summary>Allies Are Helpful (ninagoblin.alliesarehelpful). PawnTendAndRescuePatch.Postfix (Pawn.TickRare, sim) fills static caches
    /// from Find.CurrentMap and throttles them by static tick counters, so the result depends on what each PC is viewing and on when
    /// each PC last refreshed. In multiplayer the cache is built for the ticking pawn's map and refreshed once per map change per tick.
    /// Settings are class A (same files on both PCs).</summary>
    [MpCompatFor("ninagoblin.alliesarehelpful")]
    public class AlliesAreHelpfulFull1127
    {
        private static Map ctxMap;
        private static Map lastMap;
        private static int lastTick = -1;
        private static AccessTools.FieldRef<int> lastCache, lastEnemy;
        public static int replacedCurrentMap, patchedMethods;

        public AlliesAreHelpfulFull1127(ModContentPack mod)
        {
            var type = AccessTools.TypeByName("PawnTendAndRescuePatch");
            lastCache = AccessTools.StaticFieldRefAccess<int>(AccessTools.DeclaredField(type, "_lastCacheUpdateTick"));
            lastEnemy = AccessTools.StaticFieldRefAccess<int>(AccessTools.DeclaredField(type, "_lastEnemyCacheUpdateTick"));
            // CU1 (27 Nov): UpdateCache calls GetTendTargets / GetRescueTargets, which read Find.CurrentMap themselves (and in lambdas):
            // transpile every method of the patch class and its nested closure classes that reads CurrentMap.
            var curGetter = AccessTools.PropertyGetter(typeof(Find), "CurrentMap");
            var types = new List<Type> { type };
            types.AddRange(type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic));
            foreach (var t in types)
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (m.IsAbstract || m.Name == "Postfix") continue;
                    bool reads = false;
                    foreach (var ins in PatchProcessor.GetOriginalInstructions(m)) if (ins.Calls(curGetter)) { reads = true; break; }
                    if (!reads) continue;
                    MpCompat.harmony.Patch(m, transpiler: new HarmonyMethod(typeof(AlliesAreHelpfulFull1127), nameof(Transpile)));
                    patchedMethods++;
                }
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(type, "Postfix"),
                prefix: new HarmonyMethod(typeof(AlliesAreHelpfulFull1127), nameof(PrePostfix)),
                finalizer: new HarmonyMethod(typeof(AlliesAreHelpfulFull1127), nameof(Fin)));
            Log.Message("[MPCompat - FULL1127 L1] Allies Are Helpful: cache built per ticking pawn map (methods " + patchedMethods + ")");
        }

        private static void PrePostfix(Pawn __0)
        {
            if (!MP.IsInMultiplayer || __0?.Map == null) return;
            ctxMap = __0.Map;
            int now = Find.TickManager.TicksGame;
            if (lastMap != ctxMap || lastTick != now)
            {
                lastMap = ctxMap; lastTick = now;
                lastCache() = int.MinValue / 2;   // force one refresh per (map, tick): no dependence on the PC's refresh history
                lastEnemy() = int.MinValue / 2;
            }
        }

        private static void Fin() { ctxMap = null; }

        public static Map CurMap() => ctxMap ?? Find.CurrentMap;

        private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            var cur = AccessTools.PropertyGetter(typeof(Find), "CurrentMap");
            foreach (var ins in instructions)
            {
                if (ins.Calls(cur)) { replacedCurrentMap++; yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(AlliesAreHelpfulFull1127), nameof(CurMap))) { labels = ins.labels, blocks = ins.blocks }; }
                else yield return ins;
            }
        }
    }
}
