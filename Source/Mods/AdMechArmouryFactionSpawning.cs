using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Adeptus Mechanicus Armoury by Ogliss - load-time faction spawning dialog</summary>
    /// <remarks>
    /// FIXES row 480. GameComponentUtility_LoadedGame_ManageFactionsInWorld_Patch.LoadedGame.Postfix queues OnGameLoaded, which opens
    /// AdeptusMechanicus.Dialog_FactionSpawning for every OG_ faction def with no faction in the world that is not ignored. There is no
    /// "shown" flag: it opens on every load (MP join, MP reload) on every PC that loads. Its buttons ran FactionSpawningUtility
    /// .SpawnWithoutSettlements / SpawnWithSettlements (new Faction, relations, settlements) or FactionSpawningState.Ignore (world
    /// component) on the clicking PC only. Fix: in MP the Add / Add with bases (callback) / Ignore buttons send one
    /// SyncedFactionChoice(defName, choice, amount, minDistance) and close the local dialog; the command does the write and closes the
    /// dialog for that def on every PC. First command wins: a def already decided in this game, already present in the world, or
    /// ignored is not written again. Skip writes nothing and is unchanged. Single player unchanged.
    /// Status: [unverified] - zero-boot D2 test only, not run in game.
    /// </remarks>
    [MpCompatFor("Ogliss.AdMech.Armoury")]
    public class AdMechArmouryFactionSpawning
    {
        private const string LogPrefix = "[MPCompat - AdMech Armoury] ";

        public AdMechArmouryFactionSpawning(ModContentPack mod) => PatchFactionSpawningDialog();

        private static Type dialogType;
        private static Type stateType;
        private static FieldInfo factionDefField;
        private static MethodInfo spawnWithout;
        private static MethodInfo spawnWith;
        private static MethodInfo ignore;
        private static MethodInfo isIgnored;
        private static Game decidedGame;
        private static readonly HashSet<string> decided = new HashSet<string>();

        internal static void PatchFactionSpawningDialog()
        {
            if (LabOff.Row(480))
            {
                Log.Message(LogPrefix + "row 480 switched off by the lab, the load-time faction spawning dialog is not synced");
                return;
            }
            dialogType = AccessTools.TypeByName("AdeptusMechanicus.Dialog_FactionSpawning");
            stateType = AccessTools.TypeByName("AdeptusMechanicus.FactionSpawningState");
            var util = AccessTools.TypeByName("AdeptusMechanicus.FactionSpawningUtility");
            factionDefField = dialogType == null ? null : AccessTools.DeclaredField(dialogType, "factionDef");
            spawnWithout = util == null ? null : AccessTools.DeclaredMethod(util, "SpawnWithoutSettlements", new[] { typeof(FactionDef) });
            spawnWith = util == null ? null : AccessTools.DeclaredMethod(util, "SpawnWithSettlements", new[] { typeof(FactionDef), typeof(int), typeof(int), typeof(int).MakeByRefType() });
            ignore = stateType == null ? null : AccessTools.DeclaredMethod(stateType, "Ignore", new[] { typeof(FactionDef) });
            isIgnored = stateType == null ? null : AccessTools.DeclaredMethod(stateType, "IsIgnored", new[] { typeof(FactionDef) });
            var withoutButton = dialogType == null ? null : AccessTools.DeclaredMethod(dialogType, "SpawnWithoutBases");
            var ignoreButton = dialogType == null ? null : AccessTools.DeclaredMethod(dialogType, "Ignore");
            MethodInfo callback = null;
            if (dialogType != null)
                foreach (var m in dialogType.GetMethods(AccessTools.allDeclared))
                    if (m.Name.Contains("g__SpawnCallback") && !m.IsStatic && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(int), typeof(int) }))
                        callback = callback == null ? m : throw new Exception("more than one SpawnCallback in " + dialogType.FullName);
            if (factionDefField == null || spawnWithout == null || spawnWith == null || ignore == null || isIgnored == null
                || withoutButton == null || ignoreButton == null || callback == null)
            {
                Log.Error(LogPrefix + "row 480: Dialog_FactionSpawning buttons, SpawnCallback, FactionSpawningUtility or FactionSpawningState members not found, the faction spawning dialog can desync");
                return;
            }
            Multiplayer.API.MP.RegisterSyncMethod(typeof(AdMechArmouryFactionSpawning), nameof(SyncedFactionChoice));
            MpCompat.harmony.Patch(withoutButton, prefix: new HarmonyMethod(typeof(AdMechArmouryFactionSpawning), nameof(PreSpawnWithoutBases)));
            MpCompat.harmony.Patch(ignoreButton, prefix: new HarmonyMethod(typeof(AdMechArmouryFactionSpawning), nameof(PreIgnore)));
            MpCompat.harmony.Patch(callback, prefix: new HarmonyMethod(typeof(AdMechArmouryFactionSpawning), nameof(PreSpawnCallback)));
            Log.Message(LogPrefix + "row 480: faction spawning dialog (add / add with bases / ignore) synced");
        }

        private static bool Send(Window dialog, int choice, int amount, int minDistance)
        {
            if (!Multiplayer.API.MP.IsInMultiplayer || Multiplayer.API.MP.IsExecutingSyncCommand) return true;
            if (factionDefField.GetValue(dialog) is not FactionDef def) return true;
            SyncedFactionChoice(def.defName, choice, amount, minDistance);
            dialog.Close(true);
            return false;
        }

        internal static bool PreSpawnWithoutBases(Window __instance) => Send(__instance, 0, 0, 0);

        internal static bool PreSpawnCallback(Window __instance, int __0, int __1) => Send(__instance, 1, __0, __1);

        internal static bool PreIgnore(Window __instance) => Send(__instance, 2, 0, 0);

        internal static void SyncedFactionChoice(string defName, int choice, int amount, int minDistance)
        {
            var game = Current.Game;
            if (!ReferenceEquals(game, decidedGame))
            {
                decidedGame = game;
                decided.Clear();
            }
            var def = DefDatabase<FactionDef>.GetNamedSilentFail(defName);
            if (def == null || choice < 0 || choice > 2 || !decided.Add(defName)) return;
            var world = Find.World;
            var state = world == null ? null : world.GetComponent(stateType);
            var present = world?.factionManager?.AllFactionsListForReading?.Any(f => f.def == def) == true;
            if (present || (state != null && (bool)isIgnored.Invoke(state, new object[] { def }))) return;
            try
            {
                if (choice == 0) spawnWithout.Invoke(null, new object[] { def });
                else if (choice == 1) spawnWith.Invoke(null, new object[] { def, amount, minDistance, 0 });
                else if (state != null) ignore.Invoke(state, new object[] { def });
            }
            catch (Exception e)
            {
                Log.Error(LogPrefix + $"row 480: faction choice {choice} for {defName} failed: {e.InnerException ?? e}");
            }
            var windows = Current.Root?.uiRoot?.windows?.Windows;
            if (windows == null) return;
            foreach (var window in new List<Window>(windows))
                if (dialogType.IsInstanceOfType(window) && factionDefField.GetValue(window) == def)
                    window.Close(true);
        }
    }
}
