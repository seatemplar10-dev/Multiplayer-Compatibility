using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Vanilla Factions Expanded - Tribals by Oskar Potocki, xrushha, Taranchuk, Sarg Bjornson</summary>
    /// <see href="https://github.com/Vanilla-Expanded/VanillaFactionsExpanded-Tribals"/>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3079786283"/>
    [MpCompatFor("OskarPotocki.VFE.Tribals")]
    public class VanillaFactionsTribal
    {
        #region Wild men relations (row 520)

        // FIXES row 520 (27 Nov 2026): the wild-men quest node and the raid helper read Faction.OfPlayer from the world tick / quest part
        // signal, where multifaction MP has the spectator faction as OfPlayer, so the hostile relation went to the spectator, not to the
        // player faction that owns the quest map. QuestNode_Root_WildMen.GenerateSite(points, aroundTile) has one read (relation list seed);
        // its caller RunInt passes map.Tile of the quest map, so the map is found by tile. Utils.MakeWildmenRaid(wildmen, map, ...) has three
        // (hostile test, SetRelationDirect, controllerPawn test), map is argument 1. PlayerFactionForTick.ForMap fits both: it returns the
        // map's own player faction when it differs from OfPlayer in MP, else OfPlayer. GenStep_WildMen and MapGenerator_GenerateMap_Patch
        // (row 521) are NOT patched: they run under the acting faction (see SLICE1_FIX.md Part 1 step 3), and a site map's ParentFaction is
        // the wild-men faction, so ForMap would return OfPlayer there anyway.
        private static void WildMenFaction520()
        {
            const string logPrefix = "[MPCompat - VFE Tribals] ";
            if (LabOff.Row(520))
            {
                Log.Message(logPrefix + "row 520 switched off by the lab, wild men relations stay on OfPlayer");
                return;
            }

            var patched = 0;
            var quest = AccessTools.DeclaredMethod(AccessTools.TypeByName("VFETribals.QuestNode_Root_WildMen"), "GenerateSite");
            if (quest != null)
            {
                MpCompat.harmony.Patch(quest, transpiler: new HarmonyMethod(typeof(VanillaFactionsTribal), nameof(SwapQuestNodeOfPlayer)));
                patched++;
            }
            else Log.Error(logPrefix + "row 520: QuestNode_Root_WildMen.GenerateSite not found");

            var raid = AccessTools.DeclaredMethod(AccessTools.TypeByName("VFETribals.Utils"), "MakeWildmenRaid");
            if (raid != null)
            {
                MpCompat.harmony.Patch(raid, transpiler: new HarmonyMethod(typeof(VanillaFactionsTribal), nameof(SwapRaidOfPlayer)));
                patched++;
            }
            else Log.Error(logPrefix + "row 520: Utils.MakeWildmenRaid not found");

            if (patched > 0)
                Log.Message(logPrefix + "row 520: wild men quest relation and raid relation use the map's player faction in multifaction (" + patched + " methods)");
        }

        // GenerateSite(float points, int aroundTile): static, tile is argument 1; one OfPlayer read.
        private static IEnumerable<CodeInstruction> SwapQuestNodeOfPlayer(IEnumerable<CodeInstruction> instr)
        {
            var forTile = AccessTools.Method(typeof(VanillaFactionsTribal), nameof(ForTile));
            var count = 0;
            foreach (var ci in instr)
            {
                if (ci.Calls(PlayerFactionForTick.OfPlayerGetter))
                {
                    var load = new CodeInstruction(OpCodes.Ldarg_1);
                    load.labels.AddRange(ci.labels);
                    ci.labels.Clear();
                    count++;
                    yield return load;
                    yield return new CodeInstruction(OpCodes.Call, forTile);
                }
                else yield return ci;
            }
            if (count != 1)
                Log.Error("[MPCompat - VFE Tribals] row 520: QuestNode_Root_WildMen.GenerateSite has " + count + " Faction.OfPlayer reads, expected 1");
        }

        // MakeWildmenRaid(List<Pawn> wildmen, Map map, TaggedString letterLabel, TaggedString letterText): static, map is argument 1; three reads.
        private static IEnumerable<CodeInstruction> SwapRaidOfPlayer(IEnumerable<CodeInstruction> instr) =>
            PlayerFactionForTick.SwapOfPlayer(instr, () => new CodeInstruction(OpCodes.Ldarg_1), 3, "VFETribals.Utils.MakeWildmenRaid");

        /// <summary>The player faction for the quest map found by its world tile; Faction.OfPlayer when there is none.</summary>
        public static Faction ForTile(int tile)
        {
            if (!MP.IsInMultiplayer) return Faction.OfPlayer;
            var map = Find.Maps.FirstOrDefault(m => m.Tile.tileId == tile);
            return PlayerFactionForTick.ForMap(map);
        }

        #endregion

        #region Main patch

        public VanillaFactionsTribal(ModContentPack mod)
        {
            // Needs to be done late or will cause issues with resource loading from non-main thread.
            LongEventHandler.ExecuteWhenFinished(() => MpCompatPatchLoader.LoadPatch(this));

            WildMenFaction520();

            #region Gizmos

            {
                // Light (0)/extinguish (2) large fire, cancel: lighting(1)/extinguishing(3)
                MpCompat.RegisterLambdaDelegate("VFETribals.LargeFire", "GetGizmos", 0, 1, 2, 3);
            }

            #endregion

            #region Choice Letters

            {
                // Neither of the choice letters has a timeout, so we don't need to deal with that.
                // Accept (0), reject with a chance of raid (1)
                var type = AccessTools.TypeByName("VFETribals.ChoiceLetter_WildMenJoin");
                foreach (var method in MpMethodUtil.GetLambda(type, "Choices", MethodType.Getter, null, 0, 1))
                    MP.RegisterSyncMethod(method);
            }

            #endregion

            #region Cornerstones

            {
                var type = customizeCornerstonesWindowType = AccessTools.TypeByName("VFETribals.Window_CustomizeCornerstones");
                fillCornerstoneDefsMethod = MethodInvoker.GetHandler(
                    AccessTools.DeclaredMethod(type, "FillCornerstoneDefs"));

                type = AccessTools.TypeByName("VFETribals.GameComponent_Tribals");
                tribalsGameCompInstance = AccessTools.StaticFieldRefAccess<GameComponent>(AccessTools.DeclaredField(type, "Instance"));
                MP.RegisterSyncMethod(type, "AddCornerstone")
                    .SetPostInvoke(PostAddCornerstone);
            }

            #endregion
        }

        #endregion

        #region Cornerstones

        // GameComponent_Tribals
        private static AccessTools.FieldRef<GameComponent> tribalsGameCompInstance;
        [MpCompatSyncField("VFETribals.GameComponent_Tribals", "ethos")]
        protected static ISyncField tribalsGameCompEthosField;
        [MpCompatSyncField("VFETribals.GameComponent_Tribals", "ethosLocked")]
        protected static ISyncField tribalsGameCompEthosLockedField;

        // Window_CustomizeCornerstones
        private static Type customizeCornerstonesWindowType;
        private static FastInvokeHandler fillCornerstoneDefsMethod;

        [MpCompatPrefix("VFETribals.GameComponent_Tribals", "AddCornerstone")]
        private static bool PreAddCornerstone(Def def, int ___availableCornerstonePoints, IList ___cornerstones)
        {
            if (!MP.IsInMultiplayer || !MP.IsExecutingSyncCommand)
                return true;

            // Make sure that after syncing the cornerstone selection we're not in a situation where
            // either two players pick a cornerstone at a similar time, or a repeated sync happened,
            // by ensuring that there's still free points to select a cornerstone, and that it
            // wasn't selected by the player yet.
            return ___availableCornerstonePoints > 0 && !___cornerstones.Contains(def);
        }

        private static void PostAddCornerstone(object instance, object[] args)
        {
            // Ensure the list of cornerstones the player sees is up-to-date after picking one
            foreach (var window in Find.WindowStack.Windows)
            {
                if (customizeCornerstonesWindowType.IsInstanceOfType(window))
                    fillCornerstoneDefsMethod(window);
            }
        }

        [MpCompatPrefix("VFETribals.Window_CustomizeCornerstones", "DoWindowContents")]
        [MpCompatPrefix("VFETribals.Dialog_EditEthos", "ApplyChanges")]
        private static void PreEthosFieldWatch()
        {
            if (!MP.IsInMultiplayer)
                return;

            var gameComp = tribalsGameCompInstance();
            MP.WatchBegin();
            tribalsGameCompEthosField.Watch(gameComp);
            tribalsGameCompEthosLockedField.Watch(gameComp);
        }

        [MpCompatPostfix("VFETribals.Window_CustomizeCornerstones", "DoWindowContents")]
        [MpCompatPostfix("VFETribals.Dialog_EditEthos", "ApplyChanges")]
        private static void PostEthosFieldWatch()
        {
            if (MP.IsInMultiplayer)
                MP.WatchEnd();
        }

        #endregion

        #region Configure ideo

        // After several days of work on this, I realized that trying to get this to work
        // would most likely require patching a bunch of stuff in vanilla classes like
        // Page_ConfigureIdeo, Page_ConfigureFluidIdeo, as well as their related helper
        // classes. Leaving this as-is due to having issues implementing an MP-safe way
        // to handle this. The host can use the menu and then let create a joinpoint.
        // Shouldn't be that bad, especially since it won't happen more than once per game*.
        // *assuming no multifaction - in multifaction it would need extra patches to work,
        // which would raise the complexity even further.

        #endregion

        #region Game Components

        // This class will cause quite a bit of issues with multifaction, basically
        // everything it does assumes one player faction - stuff like keeping track
        // of player tech level, researched technologies, unlocked cornerstones...
        // Will need a lot of patching to be fully compatible, especially introducing
        // data like that for every single faction and properly handling current faction.

        #endregion
    }
}
