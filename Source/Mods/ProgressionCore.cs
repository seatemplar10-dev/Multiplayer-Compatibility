using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Progression: Core by Ferny</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3301868772"/>
    /// <remarks>
    /// FIXES row 522 (27 Nov 2026). ProgressionCoreSettings.allPointsToSelectedProject (bool) and researchComplectionPercent (float) are static fields
    /// loaded from each PC's own mod settings file. The simulation reads them: RitualOutcomeEffectWorker_TribalGathering_Apply_Patch.GatheringProjects
    /// (allPointsToSelectedProject, tribal gathering Apply) and RitualObligationTargetWorker_AnyGatherSpotForAdvancement_CanUseTargetInternal_Patch
    /// .NoEnoughResearchProjectsFinished (researchComplectionPercent, advancement gather spot target check). Different values on two PCs = a desync.
    /// Multiplayer ships the host's settings file at join only (Fix and restart, optional), and a mid-game edit is never synced (row 512 only warns).
    /// Fix, multiplayer only: a GameComponent keeps the 2 values in the save (the host captures its own values; joiners get them in the host's
    /// save), both sim methods run with the static fields temporarily set to those values (prefix/finalizer), and a player closing the settings
    /// window pushes the values through one synced method that sets the component on every PC. Single player reads the local statics exactly as before.
    /// </remarks>
    [MpCompatFor("ferny.progressioncore")]
    public class ProgressionCore
    {
        private const string LogPrefix = "[MPCompat - Progression Core] ";
        internal static FieldInfo percentField, allField;
        internal static Type modType;
        // Set last: until then the component and every patch method return at once.
        internal static volatile bool Active;

        public ProgressionCore(ModContentPack mod)
        {
            if (LabOff.Row(522))
            {
                Log.Message(LogPrefix + "row 522 switched off by the lab, the 2 settings stay local to each PC");
                return;
            }

            var settingsType = AccessTools.TypeByName("ProgressionCore.ProgressionCoreSettings");
            modType = AccessTools.TypeByName("ProgressionCore.ProgressionCoreMod");
            percentField = settingsType == null ? null : AccessTools.Field(settingsType, "researchComplectionPercent");
            allField = settingsType == null ? null : AccessTools.Field(settingsType, "allPointsToSelectedProject");
            var gather = AccessTools.Method("ProgressionCore.RitualOutcomeEffectWorker_TribalGathering_Apply_Patch:GatheringProjects");
            var target = AccessTools.Method("ProgressionCore.RitualObligationTargetWorker_AnyGatherSpotForAdvancement_CanUseTargetInternal_Patch:NoEnoughResearchProjectsFinished");
            if (percentField == null || allField == null || percentField.FieldType != typeof(float) || allField.FieldType != typeof(bool)
                || modType == null || gather == null || target == null)
            {
                Log.Error(LogPrefix + "row 522: ProgressionCore members not found (settings fields " + (percentField != null && allField != null)
                          + ", mod type " + (modType != null) + ", GatheringProjects " + (gather != null) + ", NoEnoughResearchProjectsFinished " + (target != null)
                          + "), the 2 settings stay local to each PC");
                return;
            }

            var swap = new HarmonyMethod(typeof(ProgressionCore), nameof(UseSyncedPrefix));
            var restore = new HarmonyMethod(typeof(ProgressionCore), nameof(RestorePostfix));
            MpCompat.harmony.Patch(gather, prefix: swap, postfix: restore);
            MpCompat.harmony.Patch(target, prefix: swap, postfix: restore);
            // Dialog_ModSettings.PreClose calls Mod.WriteSettings; ProgressionCoreMod does not override it. The postfix filters on its mod type.
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(Mod), nameof(Mod.WriteSettings)),
                postfix: new HarmonyMethod(typeof(ProgressionCore), nameof(WriteSettingsPostfix)));
            MP.RegisterSyncMethod(typeof(ProgressionCore), nameof(SyncedSet));
            Active = true;
            Log.Message(LogPrefix + "row 522: allPointsToSelectedProject and researchComplectionPercent are saved with the game, synced on a settings window close, and read from there in multiplayer");
        }

        internal static float LocalPercent => (float)percentField.GetValue(null);
        internal static bool LocalAll => (bool)allField.GetValue(null);

        // Prefix of both sim methods. MP off or no component: nothing changes (single player reads the local statics as before).
        internal static void UseSyncedPrefix(out (float percent, bool all)? __state)
        {
            __state = null;
            if (!Active || !MP.IsInMultiplayer)
                return;
            var synced = ProgressionCoreSyncedSettings.Instance;
            if (synced == null || !synced.captured)
                return;
            __state = (LocalPercent, LocalAll);
            percentField.SetValue(null, synced.percent);
            allField.SetValue(null, synced.all);
        }

        // Postfix: the two sim methods only read the fields and do not throw in normal play; the next call sets the fields again anyway.
        // (A Harmony finalizer here dropped the original return value in the D2 run, so a postfix is used.)
        internal static void RestorePostfix((float percent, bool all)? __state)
        {
            if (__state == null)
                return;
            percentField.SetValue(null, __state.Value.percent);
            allField.SetValue(null, __state.Value.all);
        }

        // Any player closing the Progression: Core settings window pushes the values (last writer wins).
        internal static void WriteSettingsPostfix(Mod __instance)
        {
            try
            {
                if (!Active || Current.Game == null || __instance == null || __instance.GetType() != modType)
                    return;
                var synced = ProgressionCoreSyncedSettings.Instance;
                if (!MP.IsInMultiplayer)
                {
                    // Single player: keep the component current so a game hosted later carries the latest values.
                    synced?.CaptureLocal();
                    return;
                }
                // Outside the interface (a long event) Multiplayer does not sync, the call would run on this PC only.
                if (!MP.InInterface || synced == null)
                    return;
                if (synced.captured && synced.percent == LocalPercent && synced.all == LocalAll)
                    return;
                SyncedSet(LocalPercent, LocalAll);
            }
            catch (Exception e)
            {
                Log.Error(LogPrefix + "row 522: settings window hook failed: " + e);
            }
        }

        // Runs on every PC from the same arguments.
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void SyncedSet(float percent, bool all)
        {
            var synced = ProgressionCoreSyncedSettings.Instance;
            if (synced == null)
                return;
            synced.percent = percent;
            synced.all = all;
            synced.captured = true;
            Messages.Message(LogPrefix + "settings changed by a player: " + GenText.ToStringPercent(percent) + " research needed, all gathering points to the selected project: " + all,
                MessageTypeDefOf.NeutralEvent, false);
        }
    }

    /// <summary>The 2 Progression: Core values the simulation uses in multiplayer, saved with the game (row 522). Inert unless the module is active.</summary>
    public class ProgressionCoreSyncedSettings : GameComponent
    {
        public static ProgressionCoreSyncedSettings Instance;
        // The mod's own defaults (ProgressionCoreSettings field initializers).
        public float percent = 1f;
        public bool all = true;
        public bool captured;

        public ProgressionCoreSyncedSettings(Game game) { }

        public void CaptureLocal()
        {
            percent = ProgressionCore.LocalPercent;
            all = ProgressionCore.LocalAll;
            captured = true;
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            Instance = this;
            if (!ProgressionCore.Active)
                return;
            // Single player, or the host: take the local values (a game hosted from a single-player load, as RunAndGun row 395 does).
            // A joiner gets the host's saved values in the host's save and keeps them.
            if (!MP.IsInMultiplayer || (!captured && MP.IsHosting))
                CaptureLocal();
            else if (!captured)
            {
                captured = true;
                Log.Message("[MPCompat - Progression Core] row 522: no saved values in this multiplayer save, using the mod defaults on every PC. A player can push theirs by closing the settings window.");
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            // forceSave: a value equal to the default must still be written, or a joiner would fall back to its own local value.
            if (!ProgressionCore.Active)
                return;
            Scribe_Values.Look(ref captured, "captured", false, true);
            Scribe_Values.Look(ref percent, "percent", 1f, true);
            Scribe_Values.Look(ref all, "all", true, true);
        }
    }
}
