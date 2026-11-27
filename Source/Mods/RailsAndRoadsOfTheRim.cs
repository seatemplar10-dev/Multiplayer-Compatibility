using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Rails and Roads of the Rim by Mlie</summary>
    /// <remarks>
    /// FIXES row 508: the caravan buttons act on the clicking PC only. Work on site / stop working call
    /// WorldObjectComp_Caravan.StartWorking / StopWorking; Add construction site makes a RoadConstructionSite world object (and its
    /// legs through the world targeter, RoadConstructionLeg.ActionOnTile) locally; Remove goes through DeleteConstructionSiteConfirmed;
    /// the comms "help with road" option calls StartHelping (Rand). Here each of those sends one synced call. The menu and the
    /// world targeter stay local to the clicking PC (RoadConstructionLeg.Target is skipped on the other PCs).
    /// Status: [unverified] - zero-boot D2 test only, not run in game.
    /// </remarks>
    [MpCompatFor("mlie.railsandroadsoftherim")]
    public class RailsAndRoadsOfTheRim
    {
        const string NS = "RailsAndRoadsOfTheRim.";
        static Type siteType, menuType, stateType, helpType;
        static FieldInfo roadDefField;
        static MethodInfo actionOnTile, finalise, startHelping, countRoads;
        static PropertyInfo stateCaravan;

        public RailsAndRoadsOfTheRim(ModContentPack mod)
        {
            if (LabOff.Row(508))
            {
                Log.Message("[MpCompat] Rails and Roads caravan commands: row 508 off");
                return;
            }

            var main = AccessTools.TypeByName(NS + "RailsAndRoadsOfTheRim");
            var compType = AccessTools.TypeByName(NS + "WorldObjectComp_Caravan");
            var legType = AccessTools.TypeByName(NS + "RoadConstructionLeg");
            siteType = AccessTools.TypeByName(NS + "RoadConstructionSite");
            menuType = AccessTools.TypeByName(NS + "ConstructionMenu");
            stateType = AccessTools.TypeByName(NS + "WorldComponent_RoadBuildingState");
            helpType = AccessTools.TypeByName(NS + "WorldComponent_FactionRoadConstructionHelp");
            if (main == null || compType == null || legType == null || siteType == null || menuType == null || stateType == null || helpType == null)
            {
                Log.Error("Rails and Roads: a type was not found, the caravan commands can desync");
                return;
            }

            roadDefField = AccessTools.Field(siteType, "roadDef");
            actionOnTile = AccessTools.DeclaredMethod(legType, "ActionOnTile");
            finalise = AccessTools.DeclaredMethod(main, "FinaliseConstructionSite");
            startHelping = AccessTools.DeclaredMethod(helpType, "StartHelping");
            countRoads = AccessTools.DeclaredMethod(menuType, "CountBuildableRoads");
            stateCaravan = AccessTools.Property(stateType, "Caravan");
            var deleteConfirmed = AccessTools.DeclaredMethod(main, "DeleteConstructionSiteConfirmed");
            var start = AccessTools.DeclaredMethod(compType, "StartWorking");
            var stop = AccessTools.DeclaredMethod(compType, "StopWorking");
            var target = AccessTools.DeclaredMethod(legType, "Target");
            var addLambda = AccessTools.GetDeclaredMethods(main).Concat(main.GetNestedTypes(AccessTools.all).SelectMany(t => AccessTools.GetDeclaredMethods(t)))
                .FirstOrDefault(m => m.Name.StartsWith("<AddConstructionSite>b__"));
            if (roadDefField == null || actionOnTile == null || finalise == null || startHelping == null || countRoads == null || stateCaravan == null
                || deleteConfirmed == null || start == null || stop == null || target == null || addLambda == null)
            {
                Log.Error("Rails and Roads: a method was not found, the caravan commands can desync");
                return;
            }

            int n = 0;
            // comp methods: MP syncs a WorldObjectComp target through its parent world object
            MP.RegisterSyncMethod(start); n++;
            MP.RegisterSyncMethod(stop); n++;
            MP.RegisterSyncMethod(deleteConfirmed).CancelIfAnyArgNull(); n++;
            MP.RegisterSyncMethod(typeof(RailsAndRoadsOfTheRim), nameof(SyncedCreateSite)).CancelIfAnyArgNull(); n++;
            MP.RegisterSyncMethod(typeof(RailsAndRoadsOfTheRim), nameof(SyncedActionOnTile)).CancelIfAnyArgNull(); n++;
            MP.RegisterSyncMethod(typeof(RailsAndRoadsOfTheRim), nameof(SyncedFinalise)).CancelIfAnyArgNull(); n++;
            MP.RegisterSyncMethod(typeof(RailsAndRoadsOfTheRim), nameof(SyncedStartHelping)).CancelIfAnyArgNull(); n++;

            var h = MpCompat.harmony;
            var me = typeof(RailsAndRoadsOfTheRim);
            h.Patch(addLambda, prefix: new HarmonyMethod(me, nameof(PrefixAddSite)));
            h.Patch(actionOnTile, prefix: new HarmonyMethod(me, nameof(PrefixActionOnTile)));
            h.Patch(target, prefix: new HarmonyMethod(me, nameof(PrefixTarget)));
            h.Patch(finalise, prefix: new HarmonyMethod(me, nameof(PrefixFinalise)));
            h.Patch(startHelping, prefix: new HarmonyMethod(me, nameof(PrefixStartHelping)));
            Log.Message($"[MpCompat] row 508: Rails and Roads caravan commands synced ({n} methods)");
        }

        static bool Local => !MP.IsInMultiplayer || MP.IsExecutingSyncCommand;

        static WorldComponent WorldComp(Type type) => Find.World.components.FirstOrDefault(c => c.GetType() == type);

        static WorldComponent State() => WorldComp(stateType);

        // ---- Add construction site: the button lambda (display class with the captured "caravan")
        static bool PrefixAddSite(object __instance)
        {
            if (!MP.IsInMultiplayer) return true;
            var caravan = __instance.GetType().GetFields(AccessTools.all).Where(f => typeof(Caravan).IsAssignableFrom(f.FieldType))
                .Select(f => f.GetValue(__instance)).FirstOrDefault() as Caravan;
            if (caravan == null) return true;
            SyncedCreateSite(caravan);
            return false;
        }

        // Runs on every PC: the lambda body, with the menu opened for the clicking PC only. A second command finds the site and does nothing.
        static void SyncedCreateSite(Caravan caravan)
        {
            var def = DefDatabase<WorldObjectDef>.GetNamed("RoadConstructionSite");
            if (Find.WorldObjects.AnyWorldObjectOfDefAt(def, caravan.Tile)) return;
            var site = WorldObjectMaker.MakeWorldObject(def);
            site.Tile = caravan.Tile;
            Find.WorldObjects.Add(site);
            var menu = (Window)Activator.CreateInstance(menuType, site, caravan);
            if ((int)countRoads.Invoke(menu, null) == 0)
            {
                Find.WorldObjects.Remove(site);
                if (MP.IsExecutingSyncCommandIssuedBySelf)
                    Messages.Message("RoadsOfTheRim_NoBetterRoadCouldBeBuilt".Translate(), MessageTypeDefOf.RejectInput, true);
            }
            else if (MP.IsExecutingSyncCommandIssuedBySelf)
            {
                menu.closeOnClickedOutside = true;
                menu.forcePause = true;
                Find.WindowStack.Add(menu);
            }
        }

        // ---- the world targeter: a tile click adds or removes legs
        static bool PrefixActionOnTile(WorldObject site, PlanetTile tile, ref bool __result)
        {
            if (Local) return true;
            // clicking the site itself ends the targeting and changes nothing
            bool onSite = Find.WorldObjects.ObjectsAt(tile).Any(o => o == site);
            __result = onSite;
            if (!onSite && site != null) SyncedActionOnTile(site, (RoadDef)roadDefField.GetValue(site), tile);
            return false;
        }

        static void SyncedActionOnTile(WorldObject site, RoadDef roadDef, PlanetTile tile)
        {
            roadDefField.SetValue(site, roadDef);
            actionOnTile.Invoke(null, new object[] { site, tile });
        }

        // The other PCs do not open the targeter.
        static bool PrefixTarget() => !(MP.IsExecutingSyncCommand && !MP.IsExecutingSyncCommandIssuedBySelf);

        // ---- end of targeting (also Escape): finalise or delete the site
        static bool PrefixFinalise(WorldObject site)
        {
            if (Local) return true;
            if (site == null) return true;
            var state = State();
            var caravan = state == null ? null : stateCaravan.GetValue(state) as Caravan;
            SyncedFinalise(site, caravan, (RoadDef)roadDefField.GetValue(site));
            return false;
        }

        static void SyncedFinalise(WorldObject site, Caravan caravan, RoadDef roadDef)
        {
            roadDefField.SetValue(site, roadDef);
            var state = State();
            if (state != null) stateCaravan.SetValue(state, caravan);
            finalise.Invoke(null, new object[] { site });
        }

        // ---- comms: ask a faction to help build (uses Rand)
        static bool PrefixStartHelping(Faction faction, WorldObject site, Pawn negotiator)
        {
            if (Local) return true;
            SyncedStartHelping(faction, site, negotiator);
            return false;
        }

        static void SyncedStartHelping(Faction faction, WorldObject site, Pawn negotiator)
        {
            var comp = WorldComp(helpType);
            startHelping.Invoke(comp, new object[] { faction, site, negotiator });
        }
    }
}
