using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using RimWorld;
using Verse;
using HarmonyLib;
using Multiplayer.API;

namespace Multiplayer.Compat
{
    /// <summary>RunAndGun Continued</summary>
    /// <see href="https://github.com/MemeGoddess/RunAndGun"/>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3562365100"/>
    [MpCompatFor("memegoddess.RunAndGun")]
    class RunandGunContinued
    {
        static FieldInfo settingsField;
        static FieldInfo movementPenaltyHeavyField;
        static FieldInfo movementPenaltyLightField;
        static FieldInfo selectedWeaponsField;
        static Type runAndGunModType;

        public RunandGunContinued(ModContentPack mod)
        {
            MpCompat.RegisterLambdaDelegate("RunAndGun.Harmony.Pawn_DraftController_GetGizmos_Patch", "Postfix", 1);
            PatchingUtilities.PatchUnityRand("RunAndGun.Harmony.MentalStateHandler_TryStartMentalState:shouldRunAndGun", false);

            var runAndGunModType = AccessTools.TypeByName("RunAndGun.RunAndGun");
            var settingsType = AccessTools.TypeByName("RunAndGun.Settings");
            if (runAndGunModType == null || settingsType == null)
            {
                Log.Warning("[MPCompat] RunAndGun: couldn't find RunAndGun.RunAndGun/RunAndGun.Settings, skipping TicksPerMove desync fix");
                return;
            }

            settingsField = AccessTools.Field(runAndGunModType, "settings");
            movementPenaltyHeavyField = AccessTools.Field(settingsType, "movementPenaltyHeavy");
            movementPenaltyLightField = AccessTools.Field(settingsType, "movementPenaltyLight");
            selectedWeaponsField = AccessTools.Field(settingsType, "selectedWeapons");

            if (settingsField == null || movementPenaltyHeavyField == null ||
                movementPenaltyLightField == null || selectedWeaponsField == null)
            {
                Log.Warning("[MPCompat] RunAndGun: couldn't find one or more Settings fields, skipping TicksPerMove desync fix");
                return;
            }

            var ticksPerMoveType = AccessTools.TypeByName("RunAndGun.Harmony.Pawn_TicksPerMove");
            var ticksPerMovePostfix = AccessTools.Method(ticksPerMoveType, "Postfix");
            if (ticksPerMovePostfix == null)
            {
                Log.Warning("[MPCompat] RunAndGun: couldn't find Pawn_TicksPerMove.Postfix, skipping TicksPerMove desync fix");
                return;
            }

            MpCompat.harmony.Patch(ticksPerMovePostfix,
                prefix: new HarmonyMethod(typeof(RunandGunContinued), nameof(UseSyncedSettingsPrefix)),
                postfix: new HarmonyMethod(typeof(RunandGunContinued), nameof(RestoreLocalSettingsPostfix)));

            // Row 395: RunAndGun does not override Mod.WriteSettings; Dialog_ModSettings.PreClose calls it when the settings window closes.
            // The postfix filters on the RunAndGun mod type, so other mods are untouched.
            RunandGunContinued.runAndGunModType = runAndGunModType;
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(Verse.Mod), nameof(Verse.Mod.WriteSettings)),
                postfix: new HarmonyMethod(typeof(RunandGunContinued), nameof(WriteSettingsPostfix)));
            MP.RegisterSyncMethod(typeof(RunandGunContinued), nameof(SyncedSetSettings)).CancelIfAnyArgNull();
        }

        // Row 395: any player closing the RunAndGun settings window pushes their values to the session copy on every PC (last writer wins).
        private static void WriteSettingsPostfix(Verse.Mod __instance)
        {
            if (Current.Game == null || __instance == null || __instance.GetType() != runAndGunModType)
                return;

            var synced = RunAndGunSyncedSettings.Instance;

            // Single player: keep the session copy current, so a change made after an SP load reaches the MP session this game is later hosted as.
            if (!MP.IsInMultiplayer)
            {
                synced?.CaptureFromLocalSettings();
                return;
            }

            // Outside the interface (e.g. a long event) Multiplayer does not sync, so the call would run on this PC only.
            if (!MP.InInterface || synced == null)
                return;

            if (!RunAndGunSyncedSettings.TryReadLocalSettings(out var heavy, out var light, out var weapons))
                return;

            // Only the light weapons are sent (command size cap is 65535 bytes and the dictionary holds every weapon def).
            var lightKeys = RunAndGunSyncedSettings.LightKeys(weapons);
            if (synced.Matches(heavy, light, lightKeys))
                return;

            SyncedSetSettings(heavy, light, lightKeys.ToArray());
        }

        // Runs on every PC from the same arguments. 3 parameters.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SyncedSetSettings(int heavy, int light, string[] lightKeys)
        {
            var synced = RunAndGunSyncedSettings.Instance;
            if (synced == null || lightKeys == null)
                return;

            var changed = !synced.Matches(heavy, light, new List<string>(lightKeys));
            synced.Apply(heavy, light, lightKeys);

            // Shown on every PC, so a look-only close by a client is visible too.
            if (changed)
                Messages.Message($"[MPCompat] RunAndGun session settings changed by a player: heavy {heavy}, light {light}, {lightKeys.Length} light weapons",
                    MessageTypeDefOf.NeutralEvent, false);
        }

        // Swaps the live settings values for the session-synced ones for the
        // duration of RunAndGun's own postfix, so every client computes the same
        // TicksPerMove result regardless of local config.
        private static void UseSyncedSettingsPrefix(out (int heavy, int light, object weapons)? __state)
        {
            __state = null;
            if (!MP.IsInMultiplayer)
                return;

            var synced = RunAndGunSyncedSettings.Instance;
            if (synced == null)
                return;

            var settings = settingsField.GetValue(null);
            if (settings == null)
                return;

            __state = (
                (int)movementPenaltyHeavyField.GetValue(settings),
                (int)movementPenaltyLightField.GetValue(settings),
                selectedWeaponsField.GetValue(settings)
            );

            movementPenaltyHeavyField.SetValue(settings, synced.movementPenaltyHeavy);
            movementPenaltyLightField.SetValue(settings, synced.movementPenaltyLight);
            selectedWeaponsField.SetValue(settings, synced.selectedWeaponsSnapshot);
        }

        private static void RestoreLocalSettingsPostfix((int heavy, int light, object weapons)? __state)
        {
            if (__state == null)
                return;

            var settings = settingsField.GetValue(null);
            if (settings == null)
                return;

            movementPenaltyHeavyField.SetValue(settings, __state.Value.heavy);
            movementPenaltyLightField.SetValue(settings, __state.Value.light);
            selectedWeaponsField.SetValue(settings, __state.Value.weapons);
        }
    }


    public class RunAndGunSyncedSettings : GameComponent
    {
        public static RunAndGunSyncedSettings Instance;

        public int movementPenaltyHeavy = 40;
        public int movementPenaltyLight = 10;

        // defName -> isSelected, mirroring RunAndGun.Settings.selectedWeapons so the
        // light/heavy classification can't diverge between clients either.
        public Dictionary<string, bool> selectedWeaponsRaw = new Dictionary<string, bool>();

        [System.NonSerialized]
        private object cachedSnapshot;
        // Row 395: saved, so a join-point reload keeps the saved values on every PC instead of the host re-reading its local settings.
        private bool captured;

        public object selectedWeaponsSnapshot
        {
            get
            {
                if (cachedSnapshot == null)
                    cachedSnapshot = BuildSnapshotDictionary();
                return cachedSnapshot;
            }
        }

        public RunAndGunSyncedSettings(Game game) { }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            Instance = this;

            // Row 395: nothing to sync when RunAndGun is not loaded (or has changed shape). The component is still saved, just with defaults,
            // and "captured" stays false so no log line and no extra saved key appear.
            if (!EnsureReflection())
                return;

            // Single player: always read the local settings. A host starts MP from an SP-loaded game and this
            // capture is what clients then receive in the host's save.
            if (!MP.IsInMultiplayer)
            {
                CaptureFromLocalSettings();
                captured = true;
                return;
            }

            // In MP a saved component keeps its saved values on every PC, host included. No recapture.
            if (captured)
                return;

            // Component new to this MP save (e.g. mod added mid-game): keep the defaults on every PC and let a player push.
            captured = true;
            Log.Message("[MPCompat] RunAndGun: no saved synced settings in this multiplayer save, using defaults on every PC. " +
                        "A player can push their settings by opening and closing the RunAndGun settings window.");
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref movementPenaltyHeavy, "movementPenaltyHeavy", 40);
            Scribe_Values.Look(ref movementPenaltyLight, "movementPenaltyLight", 10);
            Scribe_Collections.Look(ref selectedWeaponsRaw, "selectedWeaponsRaw", LookMode.Value, LookMode.Value);
            Scribe_Values.Look(ref captured, "captured", false);
            if (selectedWeaponsRaw == null)
                selectedWeaponsRaw = new Dictionary<string, bool>();
            // Row 395: a component loaded from a save always holds saved values. Old saves have the component but no
            // "captured" key (Scribe_Values skips default values), so key presence cannot tell them apart.
            if (Scribe.mode == LoadSaveMode.LoadingVars)
                captured = true;
            cachedSnapshot = null;
        }

        // Row 395: RunAndGun types and fields, looked up once. Any missing member leaves the sync inactive instead of throwing.
        private static bool reflectionInit;
        private static Type weaponRecordType;
        private static FieldInfo modSettingsField, heavyField, lightField, selectedWeaponsField, isSelectedField;

        private static bool EnsureReflection()
        {
            if (!reflectionInit)
            {
                reflectionInit = true;
                var modType = AccessTools.TypeByName("RunAndGun.RunAndGun");
                var settingsType = AccessTools.TypeByName("RunAndGun.Settings");
                weaponRecordType = AccessTools.TypeByName("RunAndGun.WeaponRecord");
                if (modType != null && settingsType != null && weaponRecordType != null)
                {
                    modSettingsField = AccessTools.Field(modType, "settings");
                    heavyField = AccessTools.Field(settingsType, "movementPenaltyHeavy");
                    lightField = AccessTools.Field(settingsType, "movementPenaltyLight");
                    selectedWeaponsField = AccessTools.Field(settingsType, "selectedWeapons");
                    isSelectedField = AccessTools.Field(weaponRecordType, "isSelected");
                }
            }

            return modSettingsField != null && heavyField != null && lightField != null &&
                   selectedWeaponsField != null && isSelectedField != null;
        }

        // Row 395: read-only read of the local RunAndGun settings, shared by the capture and the settings-window postfix.
        public static bool TryReadLocalSettings(out int heavy, out int light, out Dictionary<string, bool> weapons)
        {
            heavy = 0;
            light = 0;
            weapons = null;

            if (!EnsureReflection())
                return false;

            var settings = modSettingsField.GetValue(null);
            if (settings == null)
                return false;

            heavy = (int)heavyField.GetValue(settings);
            light = (int)lightField.GetValue(settings);

            var selectedWeapons = selectedWeaponsField.GetValue(settings) as System.Collections.IDictionary;
            weapons = new Dictionary<string, bool>();
            if (selectedWeapons != null)
            {
                foreach (System.Collections.DictionaryEntry entry in selectedWeapons)
                {
                    var record = entry.Value;
                    if (record != null)
                        weapons[(string)entry.Key] = (bool)isSelectedField.GetValue(record);
                }
            }

            return true;
        }

        // Row 395: only the light weapons matter (TicksPerMove: light = key present and not selected; a missing key counts as heavy).
        public static List<string> LightKeys(Dictionary<string, bool> weapons)
        {
            var keys = new List<string>();
            foreach (var kv in weapons)
            {
                if (!kv.Value)
                    keys.Add(kv.Key);
            }

            return keys;
        }

        internal void CaptureFromLocalSettings()
        {
            if (!TryReadLocalSettings(out var heavy, out var light, out var weapons))
                return;

            movementPenaltyHeavy = heavy;
            movementPenaltyLight = light;
            selectedWeaponsRaw = weapons;
            cachedSnapshot = null;
        }

        // Row 395: true when the given values give the same TicksPerMove result as the session copy (compares heavy, light and the set of light weapons).
        public bool Matches(int heavy, int light, List<string> lightKeys)
        {
            if (heavy != movementPenaltyHeavy || light != movementPenaltyLight)
                return false;

            var own = new HashSet<string>(LightKeys(selectedWeaponsRaw));
            return own.SetEquals(lightKeys);
        }

        // Row 395: writes the pushed values on every PC (called from the synced method). Light weapons only; the rest count as heavy.
        public void Apply(int heavy, int light, string[] lightKeys)
        {
            movementPenaltyHeavy = heavy;
            movementPenaltyLight = light;
            selectedWeaponsRaw = new Dictionary<string, bool>();
            foreach (var key in lightKeys)
            {
                if (key != null)
                    selectedWeaponsRaw[key] = false;
            }

            cachedSnapshot = null;
        }

        private object BuildSnapshotDictionary()
        {
            if (!EnsureReflection())
                return null;

            var dictType = typeof(Dictionary<,>).MakeGenericType(typeof(string), weaponRecordType);
            var dict = Activator.CreateInstance(dictType);
            var addMethod = dictType.GetMethod("Add");

            foreach (var kv in selectedWeaponsRaw)
            {
                var record = Activator.CreateInstance(weaponRecordType);
                isSelectedField.SetValue(record, kv.Value);
                addMethod.Invoke(dict, new object[] { kv.Key, record });
            }

            return dict;
        }
    }
}
