using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Real Ruins by mr.teterew (Woolstrand), Steam 1552146295</summary>
    /// <remarks>
    /// FIXES row 304 (29 Oct 2026, caravan Camp desync, host Player.log 21:55:42 "Skipping ruins gerenation due to low
    /// blueprints count", James reported the desync 2 s later).
    /// Real Ruins swaps the vanilla ruins GenStep in MapCommonBase for <c>GenStep_ScatterRealRuins</c>. That step, the medium and
    /// large ruins steps, the two incidents and the quest all read blueprint files from the per-PC folder
    /// <c>&lt;SaveData&gt;\RealRuins</c> (<c>SnapshotStoreManager.GetSnapshotsFolderPath</c>). The folder is different on every PC (the mod
    /// downloads into it at startup, writes "local-*.bp" files on save, and the cached count is only recomputed when it changes), so:
    /// <list type="bullet">
    /// <item><c>GenStep_ScatterRealRuins.Generate</c> returns early while <c>StoredSnapshotsCount() &lt; 10</c>. One PC with a full folder
    /// scatters ruins, one with an empty folder does not -> two different camp maps.</item>
    /// <item>With 10 or more files every PC would pick its own random file (<c>DoGetRandomFilenameFromRootFolder</c>: Directory.GetFiles
    /// order, Rand.Range over its own file count) -> different ruins even if both folders are full.</item>
    /// <item><c>CanFireMediumEvent</c> (more than 30 files) / <c>CanFireLargeEvent</c> (more than 250) gate the two incidents; the quest
    /// <c>RealRuins_AbandonedBase</c> picks its blueprint with <c>BlueprintFinder</c> at generation time.</item>
    /// </list>
    /// In MP this module keeps every one of those paths off, on every PC, whatever is in the folder:
    /// <list type="bullet">
    /// <item><c>StoredSnapshotsCount</c> is 0, the two <c>CanFire*Event</c> are false, <c>RandomSnapshotFilename</c> is null,
    /// <c>BlueprintFinder.FindRandomBlueprintWithParameters</c> finds nothing, and the three <c>BlueprintLoader</c> entry points find
    /// nothing (so a stored absolute path in a save, from a ruined-base world object, loads nothing either).</item>
    /// <item><c>GenStep_ScatterRealRuins.Generate</c> is replaced: it keeps the vanilla-ruins head of the original (when the setting
    /// <c>preserveStandardRuins</c> is on) and then stops, which is what the original does on a host with an empty folder. This does
    /// not rely on the small <c>StoredSnapshotsCount</c> method staying un-inlined into <c>Generate</c>.</item>
    /// <item><c>GenStep_ScatterPOIRuins.Generate</c> (world "points of interest" built from blueprint files at world creation) is skipped,
    /// as with no blueprint it would throw on the first <c>blueprint.width</c>.</item>
    /// <item><c>QuestNode_FindBlueprint.TestRunInt</c> fails, so the quest is not offered.</item>
    /// </list>
    /// Single player is untouched. Each GenStep seeds its own Rand (SeedPart), so skipping one does not move the others.
    /// Real Ruins may be loaded from the mod root and from the 1.6 folder; every loaded assembly that has the type is patched.
    /// </remarks>
    [MpCompatFor("Woolstrand.RealRuins")]
    internal class RealRuinsPerPcFiles
    {
        private const string ns = "RealRuins.";
        private static bool announcedMapGen;

        public RealRuinsPerPcFiles(ModContentPack mod)
        {
            var assemblies = mod.assemblies.loadedAssemblies
                .Where(a => a.GetType(ns + "SnapshotStoreManager", false) != null)
                .ToList();
            if (assemblies.Count == 0)
                throw new TypeLoadException("Real Ruins: RealRuins.SnapshotStoreManager not found in the mod's assemblies");

            // The core gate. Missing = the module is useless, so this one throws.
            if (Gate(assemblies, "SnapshotStoreManager", "StoredSnapshotsCount", nameof(PreCount)) == 0)
                throw new MissingMethodException(ns + "SnapshotStoreManager", "StoredSnapshotsCount");

            // The other per-PC file readers. A missing one is logged and left off, the rest still apply.
            Gate(assemblies, "SnapshotStoreManager", "CanFireMediumEvent", nameof(PreFalse));
            Gate(assemblies, "SnapshotStoreManager", "CanFireLargeEvent", nameof(PreFalse));
            Gate(assemblies, "SnapshotStoreManager", "RandomSnapshotFilename", nameof(PreNullString));
            Gate(assemblies, "BlueprintFinder", "FindRandomBlueprintWithParameters", nameof(PreFindBlueprint));
            Gate(assemblies, "BlueprintLoader", "CanLoadBlueprintAtPath", nameof(PreFalse));
            Gate(assemblies, "BlueprintLoader", "LoadWholeBlueprintAtPath", nameof(PreNullObject));
            Gate(assemblies, "BlueprintLoader", "LoadRandomBlueprintPartAtPath", nameof(PreNullObject));
            Gate(assemblies, "GenStep_ScatterRealRuins", "Generate", nameof(PreScatterRealRuins));
            Gate(assemblies, "GenStep_ScatterPOIRuins", "Generate", nameof(PreSkipGenStep));
            Gate(assemblies, "QuestNode_FindBlueprint", "TestRunInt", nameof(PreQuestTestRun));
        }

        private static int Gate(List<Assembly> assemblies, string typeName, string methodName, string prefixName)
        {
            var count = 0;
            foreach (var asm in assemblies)
            {
                var type = asm.GetType(ns + typeName, false);
                var method = type == null ? null : AccessTools.DeclaredMethod(type, methodName);
                if (method == null) continue;

                try
                {
                    MpCompat.harmony.Patch(method, prefix: new HarmonyMethod(typeof(RealRuinsPerPcFiles), prefixName));
                    count++;
                }
                catch (Exception e)
                {
                    // e.g. an older Real Ruins assembly in the mod root with other parameter names: skip that copy only
                    Log.Warning($"MPCompat :: Real Ruins: could not patch {asm.GetName().Name}.{typeName}:{methodName} ({e.GetType().Name}: {e.Message})");
                }
            }

            if (count == 0)
                Log.Warning($"MPCompat :: Real Ruins: {ns}{typeName}:{methodName} not found in any loaded assembly, this gate is off");
            return count;
        }

        private static bool PreCount(ref int __result)
        {
            if (!MP.IsInMultiplayer) return true;
            __result = 0;
            return false;
        }

        // CanFireMediumEvent, CanFireLargeEvent, BlueprintLoader.CanLoadBlueprintAtPath
        private static bool PreFalse(ref bool __result)
        {
            if (!MP.IsInMultiplayer) return true;
            __result = false;
            return false;
        }

        private static bool PreNullString(ref string __result)
        {
            if (!MP.IsInMultiplayer) return true;
            __result = null;
            return false;
        }

        // BlueprintLoader.LoadWholeBlueprintAtPath, LoadRandomBlueprintPartAtPath: return type is the internal Blueprint class
        private static bool PreNullObject(ref object __result)
        {
            if (!MP.IsInMultiplayer) return true;
            __result = null;
            return false;
        }

        // static Blueprint FindRandomBlueprintWithParameters(out string filename, int minArea, float minDensity, int minCost, int maxAttemptsCount, bool removeNonQualified)
        private static bool PreFindBlueprint(ref string filename, ref object __result)
        {
            if (!MP.IsInMultiplayer)
                return true;
            filename = null;
            __result = null;
            return false;
        }

        // The original is: preserveStandardRuins -> vanilla ScatterRuinsSimple; StoredSnapshotsCount() < 10 -> log and return; ...the real scatter.
        private static bool PreScatterRealRuins(GenStep __instance, Map map, GenStepParams parms)
        {
            if (!MP.IsInMultiplayer) return true;

            var settings = __instance.GetType().Assembly.GetType(ns + "RealRuins_ModSettings", false);
            var preserve = settings != null && AccessTools.Field(settings, "preserveStandardRuins")?.GetValue(null) is true;
            if (preserve)
                new GenStep_ScatterRuinsSimple().Generate(map, parms);

            if (!announcedMapGen)
            {
                announcedMapGen = true;
                Log.Message("MPCompat :: Real Ruins: ruin scattering is off in multiplayer (its blueprint folder is different on every PC)");
            }
            return false;
        }

        private static bool PreSkipGenStep() => !MP.IsInMultiplayer;

        private static bool PreQuestTestRun(ref bool __result)
        {
            if (!MP.IsInMultiplayer) return true;
            __result = false;
            return false;
        }
    }
}
