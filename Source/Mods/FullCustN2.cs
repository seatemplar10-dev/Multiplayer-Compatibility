using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>FULL1127 wave N2 (cu17): class N rows judged REAL by N3, a mod UI action writes sim state outside an MP-synced call.</summary>
    internal static class FullCustN2
    {
        public const string Tag = "[MPCompat - FULL1127 N2] ";

        public static Type FindType(params string[] names)
        {
            foreach (var n in names)
            {
                var t = AccessTools.TypeByName(n);
                if (t != null) return t;
            }
            return null;
        }

        /// <summary>True when the delegate compiler generated method lives inside a type named typeName (lambda, iterator or closure).</summary>
        public static bool FromName(Delegate d, string typeName)
        {
            if (d == null) return false;
            for (var x = d.Method?.DeclaringType; x != null; x = x.DeclaringType)
                if (x.Name == typeName) return true;
            return false;
        }

        public static void Ok(string mod, string what) => Log.Message(Tag + mod + ": " + what + " registered");
        public static void Fail(string mod, string what, Exception e) => Log.Error(Tag + mod + ": " + what + " failed " + e);

        public static void SyncStatic(string mod, Type t, params string[] names)
        {
            if (t == null) { Log.Error(Tag + mod + ": type not found for " + string.Join(",", names)); return; }
            foreach (var n in names)
            {
                if (AccessTools.Method(t, n) == null) { Log.Error(Tag + mod + ": " + t.Name + "." + n + " not found"); continue; }
                MP.RegisterSyncMethod(t, n);
            }
            Ok(mod, t.Name + " sync methods " + string.Join(",", names));
        }
    }

    // True RPG Inventory: GearCommands is called from the replaced gear tab (TrueGearTab.Draw) on the clicking PC only.
    [MpCompatFor("astryl.truerpginventory")]
    class FullN2TrueRpg
    {
        public FullN2TrueRpg(ModContentPack mod)
        {
            try
            {
                FullCustN2.SyncStatic("True RPG Inventory", FullCustN2.FindType("TrueRPGInventory.GearCommands", "GearCommands"),
                    "Wear", "Equip", "UnequipToInventory", "DropAtFeet", "DropNearby", "ToggleForced");
            }
            catch (Exception e) { FullCustN2.Fail("True RPG Inventory", "GearCommands sync", e); }
        }
    }

    // Nice Inventory Tab: CommandUtility holds the gear tab click handlers, each starting jobs or moving things directly.
    [MpCompatFor("andromeda.niceinventorytab")]
    class FullN2NiceInventoryTab
    {
        public FullN2NiceInventoryTab(ModContentPack mod)
        {
            try
            {
                FullCustN2.SyncStatic("Nice Inventory Tab", FullCustN2.FindType("NiceInventoryTab.CommandUtility", "CommandUtility"),
                    "CommandWear", "CommandRemoveToInventory", "CommandWearFromInventory", "CommandDrop",
                    "CommandEquipWeaponFromInventory", "CommandMoveWeaponToInventory");
            }
            catch (Exception e) { FullCustN2.Fail("Nice Inventory Tab", "CommandUtility sync", e); }
        }
    }

    // Door Direction: the float menu lambdas call private static SetAll(List<Building_Door>, Rot4?) on the clicking PC only.
    // Rot4? is replaced by an int (-1 = two way) so the synced signature is plain.
    [MpCompatFor("toraisamu.doordirection")]
    class FullN2DoorDirection
    {
        static MethodInfo setAll;

        public FullN2DoorDirection(ModContentPack mod)
        {
            try
            {
                var cmd = FullCustN2.FindType("DoorDirection.Command_DoorDirection");
                setAll = cmd == null ? null : AccessTools.Method(cmd, "SetAll");
                if (setAll == null) { Log.Error(FullCustN2.Tag + "Door Direction: Command_DoorDirection.SetAll not found"); return; }
                MP.RegisterSyncMethod(typeof(FullN2DoorDirection), nameof(SyncSetAll));
                MpCompat.harmony.Patch(setAll, prefix: new HarmonyMethod(typeof(FullN2DoorDirection), nameof(PreSetAll)));
                FullCustN2.Ok("Door Direction", "SetAll sync method (doors, direction)");
            }
            catch (Exception e) { FullCustN2.Fail("Door Direction", "SetAll sync", e); }
        }

        static bool PreSetAll(List<Building_Door> doors, Rot4? dir)
        {
            if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand || doors == null) return true;
            SyncSetAll(doors, dir.HasValue ? dir.Value.AsInt : -1);
            return false;
        }

        static void SyncSetAll(List<Building_Door> doors, int dir)
        {
            Rot4? r = dir < 0 ? (Rot4?)null : new Rot4(dir);
            setAll.Invoke(null, new object[] { doors, r });
        }
    }

    // Fish Until: the gizmo drag and Dialog_FishUntil slider write Zone_Fishing.targetPopulationPct from the UI.
    [MpCompatFor("axolki.fishuntil")]
    class FullN2FishUntil
    {
        static ISyncField pctField;
        static FieldInfo gizmoZone, dialogZone;

        public FullN2FishUntil(ModContentPack mod)
        {
            try
            {
                var giz = FullCustN2.FindType("FishUntil.Gizmo_FishUntil");
                var dlg = FullCustN2.FindType("FishUntilGizmo.Dialog_FishUntil");
                if (giz == null || dlg == null) { Log.Error(FullCustN2.Tag + "Fish Until: gizmo/dialog type not found"); return; }
                gizmoZone = AccessTools.Field(giz, "zone");
                dialogZone = AccessTools.Field(dlg, "zone");
                pctField = MP.RegisterSyncField(typeof(Zone_Fishing), "targetPopulationPct");
                pctField.SetBufferChanges();
                MpCompat.harmony.Patch(AccessTools.Method(giz, "GizmoOnGUI"),
                    prefix: new HarmonyMethod(typeof(FullN2FishUntil), nameof(PreGizmo)), postfix: new HarmonyMethod(typeof(FullN2FishUntil), nameof(Stop)));
                MpCompat.harmony.Patch(AccessTools.Method(dlg, "DoWindowContents"),
                    prefix: new HarmonyMethod(typeof(FullN2FishUntil), nameof(PreDialog)), postfix: new HarmonyMethod(typeof(FullN2FishUntil), nameof(Stop)));
                FullCustN2.Ok("Fish Until", "targetPopulationPct sync field + watch on gizmo and dialog");
            }
            catch (Exception e) { FullCustN2.Fail("Fish Until", "targetPopulationPct sync", e); }
        }

        static void PreGizmo(object __instance) => Begin(gizmoZone?.GetValue(__instance) as Zone_Fishing);
        static void PreDialog(object __instance) => Begin(dialogZone?.GetValue(__instance) as Zone_Fishing);

        static void Begin(Zone_Fishing z)
        {
            if (!MP.IsInMultiplayer) return;
            MP.WatchBegin();
            if (z != null) pctField.Watch(z);
        }

        static void Stop()
        {
            if (MP.IsInMultiplayer) MP.WatchEnd();
        }
    }

    // Mechanoid Skins: Corpse gizmo toggle writes Comp_MechanoidSkin.PrevOverseerResurrection locally.
    // cu24 switched this class off for FIXES 569. cu26 (28 Nov 04:4x) switches it on again: the clean dll outwfull12 also desyncs on the
    // first compare (Q1w, Q1y), so the 569 bisect was map luck (quicktest maps are random per run), not this class.
    [MpCompatFor("veltaris.mechanoidskins")]
    class FullN2MechanoidSkins
    {
        static FieldInfo flag;

        public FullN2MechanoidSkins(ModContentPack mod)
        {
            try
            {
                var comp = FullCustN2.FindType("AV_MechanoidSkins.Comp_MechanoidSkin");
                flag = comp == null ? null : AccessTools.Field(comp, "PrevOverseerResurrection");
                if (flag == null) { Log.Error(FullCustN2.Tag + "Mechanoid Skins: PrevOverseerResurrection not found"); return; }
                MP.RegisterSyncMethod(typeof(FullN2MechanoidSkins), nameof(SyncToggle));
                MpCompat.harmony.Patch(AccessTools.Method(typeof(Corpse), nameof(Corpse.GetGizmos)),
                    postfix: new HarmonyMethod(typeof(FullN2MechanoidSkins), nameof(Post)) { priority = Priority.Last });
                FullCustN2.Ok("Mechanoid Skins", "overseer resurrection toggle sync method (corpse)");
            }
            catch (Exception e) { FullCustN2.Fail("Mechanoid Skins", "toggle sync", e); }
        }

        static IEnumerable<Gizmo> Post(IEnumerable<Gizmo> __result, Corpse __instance)
        {
            foreach (var g in __result)
            {
                if (g is Command_Toggle t && FullCustN2.FromName(t.toggleAction, "AddOnlyOverseerResurrectionGizmo"))
                {
                    var corpse = __instance;
                    t.toggleAction = () =>
                    {
                        if (MP.IsInMultiplayer && !MP.IsExecutingSyncCommand) SyncToggle(corpse);
                        else DoToggle(corpse);
                    };
                }
                yield return g;
            }
        }

        static void SyncToggle(Corpse corpse) => DoToggle(corpse);

        static void DoToggle(Corpse corpse)
        {
            var comp = corpse?.InnerPawn?.AllComps?.FirstOrDefault(c => c.GetType() == flag.DeclaringType);
            if (comp != null) flag.SetValue(comp, !(bool)flag.GetValue(comp));
        }
    }

    // Forgetful Mech Resurrection: Corpse gizmo calls GameComponent.RemoveMech(mech) on the clicking PC only.
    [MpCompatFor("mrhydralisk.forgetfulmechresurrection")]
    class FullN2ForgetfulMech
    {
        static FieldInfo instanceF;
        static MethodInfo remove;

        public FullN2ForgetfulMech(ModContentPack mod)
        {
            try
            {
                var gc = FullCustN2.FindType("ForgetfulMechResurrection.GameComponent_ForgetfulMechResurrection");
                instanceF = gc == null ? null : AccessTools.Field(gc, "instance");
                remove = gc == null ? null : AccessTools.Method(gc, "RemoveMech");
                if (instanceF == null || remove == null) { Log.Error(FullCustN2.Tag + "Forgetful Mech Resurrection: instance/RemoveMech not found"); return; }
                MP.RegisterSyncMethod(typeof(FullN2ForgetfulMech), nameof(SyncRemoveMech));
                MpCompat.harmony.Patch(AccessTools.Method(typeof(Corpse), nameof(Corpse.GetGizmos)),
                    postfix: new HarmonyMethod(typeof(FullN2ForgetfulMech), nameof(Post)) { priority = Priority.Last });
                FullCustN2.Ok("Forgetful Mech Resurrection", "forget mech gizmo sync method (corpse)");
            }
            catch (Exception e) { FullCustN2.Fail("Forgetful Mech Resurrection", "forget gizmo sync", e); }
        }

        static IEnumerable<Gizmo> Post(IEnumerable<Gizmo> __result, Corpse __instance)
        {
            foreach (var g in __result)
            {
                // The mod postfix adds two Command_Action gizmos; only the Forget one (label key match) is redirected.
                if (g is Command_Action c && FullCustN2.FromName(c.action, "HarmonyPatches")
                    && c.defaultLabel == Translator.Translate("ForgetfulMechResurrection.Gizmo.Forget.Label").ToString())
                {
                    var corpse = __instance;
                    var orig = c.action;
                    c.action = () =>
                    {
                        if (MP.IsInMultiplayer && !MP.IsExecutingSyncCommand) SyncRemoveMech(corpse);
                        else orig();
                    };
                }
                yield return g;
            }
        }

        static void SyncRemoveMech(Corpse corpse)
        {
            var mech = corpse?.InnerPawn;
            if (mech != null) remove.Invoke(instanceF.GetValue(null), new object[] { mech });
        }
    }

    // ChezhouLib: ThingComp_SwitchMode.SwitchToMode(int) is private and only called by the mode Command_Toggle lambdas.
    [MpCompatFor("chezhou.chezhoulib.lib")]
    class FullN2ChezhouLib
    {
        public FullN2ChezhouLib(ModContentPack mod)
        {
            try
            {
                var t = FullCustN2.FindType("ChezhouLib.ThingComp_SwitchMode", "ThingComp_SwitchMode");
                if (t == null || AccessTools.Method(t, "SwitchToMode") == null) { Log.Error(FullCustN2.Tag + "ChezhouLib: ThingComp_SwitchMode.SwitchToMode not found"); return; }
                MP.RegisterSyncMethod(t, "SwitchToMode");
                FullCustN2.Ok("ChezhouLib", "ThingComp_SwitchMode.SwitchToMode(int) sync method");
            }
            catch (Exception e) { FullCustN2.Fail("ChezhouLib", "SwitchToMode sync", e); }
        }
    }

    // Pause Other Settlements Simulation: each PC decides per Find.CurrentMap which maps and pawns tick. Under MP all maps simulate.
    [MpCompatFor("esvn.pauseothersettlementssimulation")]
    class FullN2PauseOthers
    {
        public FullN2PauseOthers(ModContentPack mod)
        {
            try
            {
                var sm = FullCustN2.FindType("PauseOtherSettlementsSimulation.SimulationManager");
                var a = sm == null ? null : AccessTools.Method(sm, "ShouldSimulateMap");
                var b = sm == null ? null : AccessTools.Method(sm, "ShouldSimulatePawn");
                if (a == null || b == null) { Log.Error(FullCustN2.Tag + "Pause Other Settlements: SimulationManager.ShouldSimulateMap/Pawn not found"); return; }
                var pre = new HarmonyMethod(typeof(FullN2PauseOthers), nameof(AlwaysTrue)) { priority = Priority.First };
                MpCompat.harmony.Patch(a, prefix: pre);
                MpCompat.harmony.Patch(b, prefix: pre);
                FullCustN2.Ok("Pause Other Settlements Simulation", "ShouldSimulateMap/ShouldSimulatePawn forced true in MP");
            }
            catch (Exception e) { FullCustN2.Fail("Pause Other Settlements Simulation", "force simulate", e); }
        }

        static bool AlwaysTrue(ref bool __result)
        {
            if (!MP.IsInMultiplayer) return true;
            __result = true;
            return false;
        }
    }

    // Worksites Expanded: two Caravan gizmos run CaravanArrivalAction_*.Arrived(caravan) directly. The existing modules for this
    // packageId (FullCust3 FullMiningOutpostCu3, FullCustG1 FullG1Worksites) cover the defense tracker and cache buckets, not these.
    [MpCompatFor("godsfathermixtape.worksitesexpanded")]
    class FullN2Worksites
    {
        static Type aidT, orbitT;

        public FullN2Worksites(ModContentPack mod)
        {
            try
            {
                aidT = FullCustN2.FindType("MiningOutpost.CaravanArrivalAction_AidWorksiteDefense", "CaravanArrivalAction_AidWorksiteDefense");
                orbitT = FullCustN2.FindType("MiningOutpost.CaravanArrivalAction_EnterOrbitalPlatform", "MiningOutpost.Orbital.CaravanArrivalAction_EnterOrbitalPlatform", "CaravanArrivalAction_EnterOrbitalPlatform");
                if (aidT == null || orbitT == null) { Log.Error(FullCustN2.Tag + "Worksites Expanded: arrival action types not found"); return; }
                MP.RegisterSyncMethod(typeof(FullN2Worksites), nameof(SyncArrive));
                MpCompat.harmony.Patch(AccessTools.Method(typeof(Caravan), nameof(Caravan.GetGizmos)),
                    postfix: new HarmonyMethod(typeof(FullN2Worksites), nameof(Post)) { priority = Priority.Last });
                FullCustN2.Ok("Worksites Expanded", "aid / enter orbital platform caravan gizmo sync method (caravan, site, orbital)");
            }
            catch (Exception e) { FullCustN2.Fail("Worksites Expanded", "caravan gizmo sync", e); }
        }

        static IEnumerable<Gizmo> Post(IEnumerable<Gizmo> __result, Caravan __instance)
        {
            foreach (var g in __result)
            {
                if (g is Command_Action c && c.action != null)
                {
                    bool aid = FullCustN2.FromName(c.action, "Patch_Caravan_GetGizmos_AidWorksite");
                    bool orbit = !aid && FullCustN2.FromName(c.action, "Patch_Caravan_GetGizmos_OrbitalRelabel");
                    Site site = null;
                    if ((aid || orbit) && c.action.Target != null)
                        foreach (var f in c.action.Target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                            if (typeof(Site).IsAssignableFrom(f.FieldType)) { site = f.GetValue(c.action.Target) as Site; break; }
                    if (site != null)
                    {
                        var caravan = __instance;
                        var s = site;
                        var o = orbit;
                        var orig = c.action;
                        c.action = () =>
                        {
                            if (MP.IsInMultiplayer && !MP.IsExecutingSyncCommand) SyncArrive(caravan, s, o);
                            else orig();
                        };
                    }
                }
                yield return g;
            }
        }

        static void SyncArrive(Caravan caravan, Site site, bool orbital)
        {
            if (caravan == null || caravan.Destroyed || site == null || site.Destroyed) return;
            var action = (CaravanArrivalAction)Activator.CreateInstance(orbital ? orbitT : aidT, site);
            action.Arrived(caravan);
        }
    }

    // Psycast Synergies: Pawn gizmo toggle writes SpecData.aurasDisabled (saved, read by the aura sim) locally.
    [MpCompatFor("astryl.psycasts")]
    class FullN2PsycastSynergies
    {
        static PropertyInfo instP;
        static FieldInfo instF;
        static MethodInfo getSpec;
        static FieldInfo auras;

        public FullN2PsycastSynergies(ModContentPack mod)
        {
            try
            {
                var gc = FullCustN2.FindType("PsycastSynergies.GameComponent_PsycastSynergies", "GameComponent_PsycastSynergies");
                var spec = FullCustN2.FindType("PsycastSynergies.SpecData", "SpecData");
                if (gc == null || spec == null) { Log.Error(FullCustN2.Tag + "Psycast Synergies: GameComponent/SpecData not found"); return; }
                instP = AccessTools.Property(gc, "Instance");
                instF = AccessTools.Field(gc, "Instance");
                getSpec = AccessTools.Method(gc, "GetSpec");
                auras = AccessTools.Field(spec, "aurasDisabled");
                if (getSpec == null || auras == null || (instP == null && instF == null)) { Log.Error(FullCustN2.Tag + "Psycast Synergies: Instance/GetSpec/aurasDisabled not found"); return; }
                MP.RegisterSyncMethod(typeof(FullN2PsycastSynergies), nameof(SyncToggleAuras));
                MpCompat.harmony.Patch(AccessTools.Method(typeof(Pawn), nameof(Pawn.GetGizmos)),
                    postfix: new HarmonyMethod(typeof(FullN2PsycastSynergies), nameof(Post)) { priority = Priority.Last });
                FullCustN2.Ok("Psycast Synergies", "aura toggle gizmo sync method (pawn)");
            }
            catch (Exception e) { FullCustN2.Fail("Psycast Synergies", "aura toggle sync", e); }
        }

        static IEnumerable<Gizmo> Post(IEnumerable<Gizmo> __result, Pawn __instance)
        {
            foreach (var g in __result)
            {
                if (g is Command_Toggle t && FullCustN2.FromName(t.toggleAction, "Patch_AuraToggleGizmo"))
                {
                    var pawn = __instance;
                    t.toggleAction = () =>
                    {
                        if (MP.IsInMultiplayer && !MP.IsExecutingSyncCommand) SyncToggleAuras(pawn);
                        else DoToggle(pawn);
                    };
                }
                yield return g;
            }
        }

        static void SyncToggleAuras(Pawn pawn) => DoToggle(pawn);

        static void DoToggle(Pawn pawn)
        {
            var inst = instP != null ? instP.GetValue(null) : instF.GetValue(null);
            if (inst == null || pawn == null) return;
            var args = getSpec.GetParameters().Length == 2 ? new object[] { pawn, true } : new object[] { pawn };
            var d = getSpec.Invoke(inst, args);
            if (d != null) auras.SetValue(d, !(bool)auras.GetValue(d));
        }
    }
}
