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

    /// <summary>Simple Warrants (pb3n.simplewarrants). The warrant settings are class A (same files on both PCs). The dialog that
    /// MakeWarrantDialog opens (from the game component tick, on every PC) has option actions that destroy silver, kill the target,
    /// start a raid or spawn visitors on the clicking PC only. After the original builds the dialog, every option action is replaced by
    /// a call to a synced method that runs the original action on every PC and closes the dialog elsewhere.</summary>
    [MpCompatFor("pb3n.simplewarrants")]
    public partial class SimpleWarrantsFull1127
    {
        private static readonly Dictionary<int, Action[]> pending = new Dictionary<int, Action[]>();
        private static readonly Dictionary<int, Window> windows = new Dictionary<int, Window>();
        private static int nextId;
        private static FieldInfo curNodeField;

        public SimpleWarrantsFull1127(ModContentPack mod)
        {
            curNodeField = AccessTools.Field(typeof(Dialog_NodeTree), "curNode");
            var type = AccessTools.TypeByName("SimpleWarrants.WarrantsManager");
            MP.RegisterSyncMethod(typeof(SimpleWarrantsFull1127), nameof(SyncedChoice));
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(type, "MakeWarrantDialog"),
                postfix: new HarmonyMethod(typeof(SimpleWarrantsFull1127), nameof(PostMakeDialog)));
            Log.Message("[MPCompat - FULL1127 L1] Simple Warrants: MakeWarrantDialog option actions synced");
            RegisterCu4(type);
        }

        private static void PostMakeDialog(object __0)
        {
            if (!MP.IsInMultiplayer) return;
            var win = Find.WindowStack.Windows.LastOrDefault(w => w is Dialog_NodeTree);
            if (win == null) return;
            var node = curNodeField.GetValue(win) as DiaNode;
            if (node == null) return;
            int id = nextId++;
            var originals = node.options.Select(o => o.action).ToArray();
            pending[id] = originals;
            // The dialog belongs to the warrant owner (issuer). Other PCs keep the pending actions (so the synced choice still finds
            // them) but never show the dialog. Opening or closing a window is UI only.
            var owner = OwnerOf(__0);
            var local = LocalRealPlayer();
            if (owner != null && local != null && owner != local) { win.Close(false); }
            else windows[id] = win;
            for (int i = 0; i < node.options.Count; i++)
            {
                int idx = i;
                if (originals[idx] != null) node.options[idx].action = () => SyncedChoice(id, idx);
            }
        }

        private static void SyncedChoice(int id, int index)
        {
            if (!pending.TryGetValue(id, out var actions)) return;
            pending.Remove(id);
            if (windows.TryGetValue(id, out var w)) { windows.Remove(id); if (Find.WindowStack.IsOpen(w)) w.Close(); }
            if (index >= 0 && index < actions.Length) actions[index]?.Invoke();
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
