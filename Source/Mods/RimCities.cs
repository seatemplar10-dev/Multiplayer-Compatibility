using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using Multiplayer.API;
using RimWorld.Planet;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>RimCities</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=1775170117"/>
    /// IncidentWorker_Quest (on the IncidentDef, never saved) keeps one not-started Quest object between checks.
    /// Quest.UpdateHome picks the quest's home map once and keeps it, and falls back to Find.CurrentMap (a per-PC value).
    /// The host picked its home at its first check of the session, the arbiter (or a joining player) at its first check
    /// after the join point, with other colonist counts and another current map. ChooseParts then picks the city by
    /// distance from that home and generates its pawns, so the world Rand drifts (MP tick 655441, FIXES row 301).
    /// In MP: every check starts from a fresh quest, and the home fallback skips Find.CurrentMap.
    /// Quest_Assault.OnTick also reads Find.CurrentMap during the world tick. In MP it reads the target city's map.
    [MpCompatFor("Cabbage.RimCities")]
    public class RimCities
    {
        private static AccessTools.FieldRef<object, object> workerQuestField;
        private static AccessTools.FieldRef<object, Map> questHomeField;
        private static FastInvokeHandler questStartedGetter;
        private static FastInvokeHandler questIsValidHome;
        private static AccessTools.FieldRef<object, object> assaultTargetField;

        public RimCities(ModContentPack mod)
        {
            var workerType = AccessTools.TypeByName("Cities.IncidentWorker_Quest");
            var questType = AccessTools.TypeByName("Cities.Quest");
            var assaultType = AccessTools.TypeByName("Cities.Quest_Assault");

            workerQuestField = AccessTools.FieldRefAccess<object>(workerType, "quest");
            questHomeField = AccessTools.FieldRefAccess<Map>(questType, "home");
            questStartedGetter = MethodInvoker.GetHandler(AccessTools.DeclaredPropertyGetter(questType, "Started"));
            questIsValidHome = MethodInvoker.GetHandler(AccessTools.DeclaredMethod(questType, "IsValidHome"));
            assaultTargetField = AccessTools.FieldRefAccess<object>(assaultType, "target");

            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(workerType, "SetupQuest"),
                prefix: new HarmonyMethod(typeof(RimCities), nameof(PreSetupQuest)));
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(questType, "UpdateHome"),
                prefix: new HarmonyMethod(typeof(RimCities), nameof(PreUpdateHome)));
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(assaultType, "OnTick"),
                transpiler: new HarmonyMethod(typeof(RimCities), nameof(AssaultOnTickTranspiler)));
        }

        // SetupQuest reuses the stored quest when it has not started. In MP, drop it, so the new quest gets its home
        // from the synced game state now and not from this PC's history. A started quest is saved and synced: keep it.
        private static void PreSetupQuest(object __instance)
        {
            if (!MP.IsInMultiplayer)
                return;
            ref var quest = ref workerQuestField(__instance);
            if (quest != null && !(bool)questStartedGetter(quest))
                quest = null;
        }

        // Same as Quest.UpdateHome, without the Find.CurrentMap fallback (MP only).
        private static bool PreUpdateHome(object __instance)
        {
            if (!MP.IsInMultiplayer)
                return true;
            ref var home = ref questHomeField(__instance);
            if (home == null || !Find.Maps.Contains(home))
            {
                home = Find.Maps.Where(m => (bool)questIsValidHome(__instance, m))
                           .MaxByWithFallback(m => m.mapPawns.FreeColonistsSpawnedCount)
                       ?? Find.Maps.MaxByWithFallback(m => m.mapPawns.FreeColonistsSpawnedCount)
                       ?? home;
            }
            return false;
        }

        private static IEnumerable<CodeInstruction> AssaultOnTickTranspiler(IEnumerable<CodeInstruction> instr)
        {
            var currentMap = AccessTools.DeclaredPropertyGetter(typeof(Find), nameof(Find.CurrentMap));
            var replacement = AccessTools.DeclaredMethod(typeof(RimCities), nameof(AssaultCurrentMap));
            var replaced = 0;
            foreach (var ci in instr)
            {
                if (ci.Calls(currentMap))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(ci);
                    yield return new CodeInstruction(OpCodes.Call, replacement);
                    replaced++;
                }
                else
                    yield return ci;
            }
            if (replaced != 1)
                Log.Error($"RimCities compat: expected 1 Find.CurrentMap call in Quest_Assault.OnTick, patched {replaced}");
        }

        private static Map AssaultCurrentMap(object quest)
        {
            if (!MP.IsInMultiplayer)
                return Find.CurrentMap;
            return assaultTargetField(quest) is MapParent target && target.HasMap ? target.Map : null;
        }
    }
}
