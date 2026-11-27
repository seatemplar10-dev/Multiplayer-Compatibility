using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace Multiplayer.Compat
{
    // FULL1127 L1 batch 4 (FIX_PATCH_2). One class per mod, each logs one line so a boot log shows it.
    // Colored Deep Resources (kikohi.coloreddeepresources): NO-FIX. The 1.7 game dll has DeepResourceGrid.drawer (CellBoolDrawer), so GetField("drawer") is not null.
    // Auto-Extract Genes (nibato.autoextractgenes): NO-FIX. The sim read is a mod setting (class A, same settings file on both PCs)
    // and the extractor loop walks mapPawns.AllPawnsSpawned in list order; the existing AutoExtractGenes module already syncs isEnabled.

    /// <summary>Loading Progress (ilyvion.loadingprogress). Its prefix on LongEventHandler.ExecuteToExecuteWhenFinished returns false and
    /// re-queues the deferred actions as own long events, which displaces Multiplayer's post-load deferred actions. In Multiplayer the
    /// mod's prefix is made to return true so vanilla runs the queue. Outside Multiplayer the mod behaves as before.</summary>
    [MpCompatFor("ilyvion.loadingprogress")]
    public class LoadingProgressFull1127
    {
        public LoadingProgressFull1127(ModContentPack mod)
        {
            var type = AccessTools.TypeByName("ilyvion.LoadingProgress.LongEventHandler_ExecuteToExecuteWhenFinished_Patches");
            var prefix = AccessTools.DeclaredMethod(type, "Prefix");
            MpCompat.harmony.Patch(prefix, prefix: new HarmonyMethod(typeof(LoadingProgressFull1127), nameof(SkipModPrefix)) { priority = Priority.First });
            Log.Message("[MPCompat - FULL1127 L1] Loading Progress: deferred-action replacement disabled in Multiplayer registered");
        }

        private static bool SkipModPrefix(ref bool __result)
        {
            if (!MP.IsInMultiplayer) return true;
            __result = true; // true = run the original ExecuteToExecuteWhenFinished
            return false;
        }
    }

    /// <summary>Discoveries (ferny.discoveries). Selector.Select postfix calls DiscoveryTracker.TryDiscover on the selecting PC only; the
    /// discovered sets gate research and recipes that the sim reads. In Multiplayer the discovery is one synced static call on every PC.
    /// Each PC shows its own discovery window from that call; the selecting PC does not clear its selection.</summary>
    [MpCompatFor("ferny.discoveries")]
    public class DiscoveriesFull1127
    {
        private static MethodInfo tryDiscover;
        private static MethodInfo tryShowNext;

        public DiscoveriesFull1127(ModContentPack mod)
        {
            tryDiscover = AccessTools.Method("Discoveries.DiscoveryTracker:TryDiscover");
            tryShowNext = AccessTools.Method("Discoveries.DiscoveryQueue:TryShowNext");
            if (tryDiscover == null || tryShowNext == null) throw new Exception("Discoveries members not found");
            MP.RegisterSyncMethod(typeof(DiscoveriesFull1127), nameof(SyncDiscover));
            MpCompat.harmony.Patch(AccessTools.Method("Discoveries.Selector_Select_Patch:Postfix"),
                prefix: new HarmonyMethod(typeof(DiscoveriesFull1127), nameof(PreSelectPostfix)) { priority = Priority.First });
            Log.Message("[MPCompat - FULL1127 L1] Discoveries: selection discovery synced");
        }

        private static bool PreSelectPostfix(Selector __instance, object obj)
        {
            if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand) return true;
            if (obj is Thing thing && __instance.SingleSelectedThing == thing && thing.def != null)
                SyncDiscover(thing);
            return false;
        }

        [SyncMethod]
        private static void SyncDiscover(Thing thing)
        {
            if (thing == null) return;
            if ((bool)tryDiscover.Invoke(null, new object[] { thing, false }))
                tryShowNext.Invoke(null, null);
        }
    }

    /// <summary>Keep Converting (linnun.keepconverting). The gizmo actions write comp.convertTarget, add a comp and register the watcher on the
    /// clicking PC only. The actions are replaced with synced static setters taking (guide, target).</summary>
    [MpCompatFor("linnun.keepconverting")]
    public class KeepConvertingFull1127
    {
        private static Type compType, propsType, watcherType;
        private static FieldInfo targetField, ideoField, tickNowField;
        private static MethodInfo registerPawn;
        private static string cancelDesc, setDesc;

        public KeepConvertingFull1127(ModContentPack mod)
        {
            compType = AccessTools.TypeByName("KeepConverting.CompConvertAuto");
            propsType = AccessTools.TypeByName("KeepConverting.CompProperties_ConvertAuto");
            watcherType = AccessTools.TypeByName("KeepConverting.GameComponent_ConvertWatcher");
            targetField = AccessTools.Field(compType, "convertTarget");
            ideoField = AccessTools.Field(compType, "convertIdeo");
            tickNowField = AccessTools.Field(watcherType, "tickNow");
            registerPawn = AccessTools.Method(watcherType, "RegisterPawn");
            if (targetField == null || ideoField == null || tickNowField == null || registerPawn == null) throw new Exception("KeepConverting members not found");
            MP.RegisterSyncMethod(typeof(KeepConvertingFull1127), nameof(SyncSetTarget));
            MP.RegisterSyncMethod(typeof(KeepConvertingFull1127), nameof(SyncCancel));
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(Pawn), nameof(Pawn.GetGizmos)),
                postfix: new HarmonyMethod(typeof(KeepConvertingFull1127), nameof(WrapGizmos)) { priority = Priority.Last });
            Log.Message("[MPCompat - FULL1127 L1] Keep Converting: set and cancel gizmo actions synced");
        }

        private static IEnumerable<Gizmo> WrapGizmos(IEnumerable<Gizmo> __result, Pawn __instance)
        {
            foreach (var g in __result)
            {
                if (MP.IsInMultiplayer && g is Command c)
                {
                    cancelDesc ??= "KC_GizmoKeepConvertingCancelDesc".Translate().ToString();
                    setDesc ??= "KC_GizmoKeepConvertingDesc".Translate().ToString();
                    var pawn = __instance;
                    if (g is Command_Action ca && ca.defaultDesc == cancelDesc)
                        ca.action = () => { SyncCancel(pawn); SoundDefOf.Tick_Low.PlayOneShotOnCamera(); };
                    else if (g is Command_Target ct && ct.defaultDesc == setDesc)
                        ct.action = t => { if (t.Thing is Pawn target) SyncSetTarget(pawn, target); };
                }
                yield return g;
            }
        }

        [SyncMethod]
        private static void SyncSetTarget(Pawn guide, Pawn target)
        {
            if (guide == null || target == null) return;
            var comp = guide.AllComps.FirstOrDefault(x => x.GetType() == compType);
            if (comp == null)
            {
                comp = (ThingComp)Activator.CreateInstance(compType);
                comp.parent = guide;
                comp.Initialize((CompProperties)Activator.CreateInstance(propsType));
                guide.AllComps.Add(comp);
            }
            targetField.SetValue(comp, target);
            ideoField.SetValue(comp, guide.Ideo);
            tickNowField.SetValue(null, true);
            registerPawn.Invoke(Current.Game.components.First(x => x.GetType() == watcherType), new object[] { guide });
        }

        [SyncMethod]
        private static void SyncCancel(Pawn guide)
        {
            var comp = guide?.AllComps.FirstOrDefault(x => x.GetType() == compType);
            if (comp != null) targetField.SetValue(comp, null);
        }
    }

    /// <summary>Naname Floors (oels.nanamefloors). Designator_Build.DesignateSingleCell prefix swaps entDef using the per-client
    /// UI.selectedMask / UI.rotation. The chosen mask name and rotation are sent as one synced call before the designator command
    /// and the UI statics are set from them on every PC while the designator command runs (blended defs are then created in the same
    /// order everywhere). On the issuing PC the mask is hidden while Multiplayer serialises the designator so the plain terrain is sent.</summary>
    [MpCompatFor("oels.nanamefloors")]
    public class NanameFloorsFull1127
    {
        private static object ui;
        private static FieldInfo maskField, rotField, masksField;
        private static string pendingMask;
        private static int pendingRot;

        public NanameFloorsFull1127(ModContentPack mod)
        {
            var main = AccessTools.TypeByName("NanameFloors.NanameFloors");
            uiField = AccessTools.Field(main, "UI");
            var uiType = AccessTools.TypeByName("NanameFloors.UI_SelectTerrainShape");
            maskField = AccessTools.Field(uiType, "selectedMask");
            rotField = AccessTools.Field(uiType, "rotation");
            masksField = AccessTools.Field(uiType, "terrainMasks");
            if (uiField == null || maskField == null || rotField == null || masksField == null) throw new Exception("NanameFloors members not found");
            MP.RegisterSyncMethod(typeof(NanameFloorsFull1127), nameof(SyncPending));
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(Designator_Build), "DesignateSingleCell"),
                prefix: new HarmonyMethod(typeof(NanameFloorsFull1127), nameof(PreDesignate)) { priority = Priority.First, before = new[] { "multiplayer" } },
                finalizer: new HarmonyMethod(typeof(NanameFloorsFull1127), nameof(PostDesignate)) { priority = Priority.Last });
            Log.Message("[MPCompat - FULL1127 L1] Naname Floors: mask and rotation synced with the designator");
        }

        private static FieldInfo uiField;

        private struct Saved { public bool active; public object mask; public Rot4 rot; }

        private static void PreDesignate(Designator_Build __instance, out Saved __state)
        {
            __state = default;
            if (!MP.IsInMultiplayer) return;
            ui = uiField.GetValue(null);
            if (ui == null) return;
            __state = new Saved { active = true, mask = maskField.GetValue(ui), rot = (Rot4)rotField.GetValue(ui) };
            if (MP.IsExecutingSyncCommand)
            {
                // every PC: the issuer's choice, not the local UI
                var masks = (List<Texture2D>)masksField.GetValue(ui);
                maskField.SetValue(ui, pendingMask == null ? null : masks?.FirstOrDefault(m => m.name == pendingMask));
                rotField.SetValue(ui, new Rot4(pendingRot));
            }
            else
            {
                // issuing PC, before the command is sent
                var mask = __state.mask as Texture2D;
                if (mask != null && AccessTools.Field(typeof(Designator_Build), "entDef").GetValue(__instance) is TerrainDef)
                    SyncPending(mask.name, __state.rot.AsInt);
                maskField.SetValue(ui, null); // MP serialises the plain terrain def
            }
        }

        private static Exception PostDesignate(Saved __state, Exception __exception)
        {
            if (__state.active)
            {
                maskField.SetValue(ui, __state.mask);
                rotField.SetValue(ui, __state.rot);
                if (MP.IsExecutingSyncCommand) pendingMask = null;
            }
            return __exception;
        }

        [SyncMethod]
        private static void SyncPending(string maskName, int rot)
        {
            pendingMask = maskName;
            pendingRot = rot;
        }
    }

    /// <summary>I Will Be Back (hailuan.iwbb). IncidentWork_ComeBack keeps a static candidates list (never cleared, not saved) and rolls Rand.Chance
    /// in CanFireNowSub, so a PC that joined later or restarted rolls differently. The list is emptied before every CanFireNow and after every
    /// TryExecuteWorker so the choice depends on game state and the synced RNG only.</summary>
    [MpCompatFor("hailuan.iwbb")]
    public class IwbbFull1127
    {
        private static FieldInfo candidates;

        public IwbbFull1127(ModContentPack mod)
        {
            var type = AccessTools.TypeByName("ComeBackColony.IncidentWork_ComeBack");
            candidates = AccessTools.Field(type, "candidates");
            if (candidates == null) throw new Exception("candidates not found");
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(type, "CanFireNowSub"), prefix: new HarmonyMethod(typeof(IwbbFull1127), nameof(ClearList)));
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(type, "TryExecuteWorker"), finalizer: new HarmonyMethod(typeof(IwbbFull1127), nameof(ClearAfter)));
            Log.Message("[MPCompat - FULL1127 L1] I Will Be Back: static candidates list cleared per call registered");
        }

        private static void ClearList() { (candidates.GetValue(null) as IList)?.Clear(); }
        private static Exception ClearAfter(Exception __exception) { ClearList(); return __exception; }
    }
}
