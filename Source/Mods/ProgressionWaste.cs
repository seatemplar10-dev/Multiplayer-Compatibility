using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Progression: Waste by Ferny</summary>
    /// <remarks>
    /// FIXES rows 531 and 536 (27 Nov 2026).
    /// Row 531: the Garbage Truck module (loads with Vanilla Vehicles Expanded) gives the truck a Compact gizmo, a plain Command_Action whose action is
    /// CompTruckCompactor.Compact. It destroys the trash in the truck and makes bricks on the clicking PC only. Fix: Compact is a synced method.
    /// Row 536: ProgressionWaste.Settings (generateTrash bool, packAfter int; instance fields, loaded from each PC's own mod settings file) is read by the
    /// simulation: the clean-filth toil tick (JobDriver_CleanFilthPacking.MakeNewToils lambda 1: comp.tally++) and CompWasteTally.CanPack (job flow).
    /// Different values on two PCs = a desync. Fix, multiplayer only: a GameComponent keeps the 2 values in the save (the host captures its own values;
    /// joiners get them in the host's save), the two sim methods run with the Settings fields temporarily set to those values, and a player closing the
    /// settings window pushes the values through one synced method (last writer wins). Single player reads the local settings exactly as before.
    /// </remarks>
    [MpCompatFor("ferny.progressionwaste")]
    public class ProgressionWaste
    {
        private const string LogPrefix = "[MPCompat - Progression Waste] ";
        internal static FieldInfo generateField, packField, settingsField;
        internal static Type modType;
        // Set last: until then the component and every patch method return at once.
        internal static volatile bool Active;

        public ProgressionWaste(ModContentPack mod)
        {
            if (LabOff.Row(531))
                Log.Message(LogPrefix + "row 531 switched off by the lab, the Compact gizmo stays local to the clicking PC");
            else
                PatchCompact();

            if (LabOff.Row(536))
                Log.Message(LogPrefix + "row 536 switched off by the lab, the 2 settings stay local to each PC");
            else
                PatchSettings();
        }

        private static void PatchCompact()
        {
            var compact = AccessTools.DeclaredMethod("GarbageTruckCompactor.CompTruckCompactor:Compact");
            if (compact == null)
            {
                // The module loads only with Vanilla Vehicles Expanded.
                Log.Message(LogPrefix + "row 531: GarbageTruckCompactor.CompTruckCompactor not loaded (no Vanilla Vehicles Expanded?), nothing to sync");
                return;
            }
            MP.RegisterSyncMethod(compact);
            Log.Message(LogPrefix + "row 531: CompTruckCompactor.Compact is a synced method");
        }

        private static void PatchSettings()
        {
            modType = AccessTools.TypeByName("ProgressionWaste.ProgressionWasteMod");
            var settingsType = AccessTools.TypeByName("ProgressionWaste.Settings");
            settingsField = modType == null ? null : AccessTools.Field(modType, "Settings");
            generateField = settingsType == null ? null : AccessTools.Field(settingsType, "generateTrash");
            packField = settingsType == null ? null : AccessTools.Field(settingsType, "packAfter");
            var canPack = AccessTools.PropertyGetter("ProgressionWaste.CompWasteTally:CanPack");
            var jobType = AccessTools.TypeByName("ProgressionWaste.JobDriver_CleanFilthPacking");
            MethodInfo tick = null;
            try { tick = jobType == null ? null : MpMethodUtil.GetLambda(jobType, "MakeNewToils", MethodType.Normal, null, 1); }
            catch (Exception e) { Log.Warning(LogPrefix + "row 536: " + e.Message); }
            if (settingsField == null || generateField == null || packField == null || generateField.FieldType != typeof(bool) || packField.FieldType != typeof(int)
                || canPack == null || tick == null)
            {
                Log.Error(LogPrefix + "row 536: ProgressionWaste members not found (Settings " + (settingsField != null && generateField != null && packField != null)
                          + ", CanPack " + (canPack != null) + ", clean toil tick lambda " + (tick != null) + "), the 2 settings stay local to each PC");
                return;
            }

            var swap = new HarmonyMethod(typeof(ProgressionWaste), nameof(UseSyncedPrefix));
            MpCompat.harmony.Patch(canPack, prefix: swap, postfix: new HarmonyMethod(typeof(ProgressionWaste), nameof(RestorePostfix)));
            // The tick lambda returns void, a finalizer restores the settings also when it throws.
            MpCompat.harmony.Patch(tick, prefix: swap, finalizer: new HarmonyMethod(typeof(ProgressionWaste), nameof(RestoreFinalizer)));
            // Dialog_ModSettings.PreClose calls Mod.WriteSettings; ProgressionWasteMod does not override it. The postfix filters on its mod type.
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(Mod), nameof(Mod.WriteSettings)),
                postfix: new HarmonyMethod(typeof(ProgressionWaste), nameof(WriteSettingsPostfix)));
            MP.RegisterSyncMethod(typeof(ProgressionWaste), nameof(SyncedSet));
            Active = true;
            Log.Message(LogPrefix + "row 536: generateTrash and packAfter are saved with the game, synced on a settings window close, and read from there in multiplayer");
        }

        internal static object LocalSettings => settingsField.GetValue(null);
        internal static bool LocalGenerate => (bool)generateField.GetValue(LocalSettings);
        internal static int LocalPack => (int)packField.GetValue(LocalSettings);

        // Prefix of both sim methods. MP off or no component: nothing changes (single player reads the local settings as before).
        internal static void UseSyncedPrefix(out (bool generate, int pack)? __state)
        {
            __state = null;
            if (!Active || !MP.IsInMultiplayer)
                return;
            var synced = ProgressionWasteSyncedSettings.Instance;
            var settings = LocalSettings;
            if (synced == null || !synced.captured || settings == null)
                return;
            __state = (LocalGenerate, LocalPack);
            generateField.SetValue(settings, synced.generate);
            packField.SetValue(settings, synced.pack);
        }

        internal static void RestorePostfix((bool generate, int pack)? __state)
        {
            if (__state == null)
                return;
            var settings = LocalSettings;
            generateField.SetValue(settings, __state.Value.generate);
            packField.SetValue(settings, __state.Value.pack);
        }

        internal static void RestoreFinalizer((bool generate, int pack)? __state) => RestorePostfix(__state);

        // Any player closing the Progression: Waste settings window pushes the values (last writer wins).
        internal static void WriteSettingsPostfix(Mod __instance)
        {
            try
            {
                if (!Active || Current.Game == null || __instance == null || __instance.GetType() != modType)
                    return;
                var synced = ProgressionWasteSyncedSettings.Instance;
                if (!MP.IsInMultiplayer)
                {
                    // Single player: keep the component current so a game hosted later carries the latest values.
                    synced?.CaptureLocal();
                    return;
                }
                // Outside the interface (a long event) Multiplayer does not sync, the call would run on this PC only.
                if (!MP.InInterface || synced == null)
                    return;
                if (synced.captured && synced.generate == LocalGenerate && synced.pack == LocalPack)
                    return;
                SyncedSet(LocalGenerate, LocalPack);
            }
            catch (Exception e)
            {
                Log.Error(LogPrefix + "row 536: settings window hook failed: " + e);
            }
        }

        // Runs on every PC from the same arguments.
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void SyncedSet(bool generate, int pack)
        {
            var synced = ProgressionWasteSyncedSettings.Instance;
            if (synced == null)
                return;
            synced.generate = generate;
            synced.pack = pack;
            synced.captured = true;
            Messages.Message(LogPrefix + "settings changed by a player: generate trash " + generate + ", pack after " + pack,
                MessageTypeDefOf.NeutralEvent, false);
        }
    }

    /// <summary>The 2 Progression: Waste values the simulation uses in multiplayer, saved with the game (row 536). Inert unless the module is active.</summary>
    public class ProgressionWasteSyncedSettings : GameComponent
    {
        public static ProgressionWasteSyncedSettings Instance;
        // The mod's own defaults (Settings field initializers).
        public bool generate = true;
        public int pack = 30;
        public bool captured;

        public ProgressionWasteSyncedSettings(Game game) { }

        public void CaptureLocal()
        {
            generate = ProgressionWaste.LocalGenerate;
            pack = ProgressionWaste.LocalPack;
            captured = true;
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            Instance = this;
            if (!ProgressionWaste.Active)
                return;
            // Single player, or the host: take the local values (a game hosted from a single-player load, as RunAndGun row 395 does).
            // A joiner gets the host's saved values in the host's save and keeps them.
            if (!MP.IsInMultiplayer || (!captured && MP.IsHosting))
                CaptureLocal();
            else if (!captured)
            {
                captured = true;
                Log.Message("[MPCompat - Progression Waste] row 536: no saved values in this multiplayer save, using the mod defaults on every PC. A player can push theirs by closing the settings window.");
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            // forceSave: a value equal to the default must still be written, or a joiner would fall back to its own local value.
            if (!ProgressionWaste.Active)
                return;
            Scribe_Values.Look(ref captured, "captured", false, true);
            Scribe_Values.Look(ref generate, "generate", true, true);
            Scribe_Values.Look(ref pack, "pack", 30, true);
        }
    }
}
