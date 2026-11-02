using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
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

        // Row 405: true only when every patch that keeps RunAndGun's own settings UI on the LOCAL settings object is in place.
        // Until then RunAndGun.RunAndGun.settings is never pointed at the session copy (see PatchSessionSettingsSwap).
        internal static bool SessionSwapEnabled;

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

            // Row 405: every other member the sync needs (enableForAI, enableForFleeChance, accuracyPenalty, forbiddenWeapons, Verse.Mod.modSettings, the copy fields).
            // All or nothing: a half-resolved sync would swap null dictionaries into RunAndGun's settings. EnsureReflection logs what is missing.
            if (!RunAndGunSyncedSettings.EnsureReflection())
            {
                Log.Warning("[MPCompat] RunAndGun: couldn't resolve every RunAndGun settings member (see the line above), skipping the settings sync");
                return;
            }

            var ticksPerMoveType = AccessTools.TypeByName("RunAndGun.Harmony.Pawn_TicksPerMove");
            var ticksPerMovePostfix = AccessTools.Method(ticksPerMoveType, "Postfix");
            if (ticksPerMovePostfix == null)
            {
                Log.Warning("[MPCompat] RunAndGun: couldn't find Pawn_TicksPerMove.Postfix, skipping TicksPerMove desync fix");
                return;
            }

            // Row 395 swap, kept unchanged. Row 405: in MP the static field already points at the session copy, which holds the same synced values,
            // so this swap writes the values a second time and restores them; it stays as the guard for the case that the session copy is not installed.
            MpCompat.harmony.Patch(ticksPerMovePostfix,
                prefix: new HarmonyMethod(typeof(RunandGunContinued), nameof(UseSyncedSettingsPrefix)),
                postfix: new HarmonyMethod(typeof(RunandGunContinued), nameof(RestoreLocalSettingsPostfix)));

            // Row 395: RunAndGun does not override Mod.WriteSettings; Dialog_ModSettings.PreClose calls it when the settings window closes
            // (XML Extensions' mod-settings menu calls it too, when the player switches mod or closes the menu).
            // The postfix filters on the RunAndGun mod type, so other mods are untouched.
            RunandGunContinued.runAndGunModType = runAndGunModType;
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(Verse.Mod), nameof(Verse.Mod.WriteSettings)),
                postfix: new HarmonyMethod(typeof(RunandGunContinued), nameof(WriteSettingsPostfix)));
            MP.RegisterSyncMethod(typeof(RunandGunContinued), nameof(SyncedSetSettings)).CancelIfAnyArgNull();

            PatchSessionSettingsSwap();
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

            // Row 405: reads the mod instance's own (local) settings object, not the static, which points at the session copy in MP.
            if (!RunAndGunSyncedSettings.TryReadLocalSettings(out var local))
                return;

            // Only the light weapons and the forbidden weapons are sent (command size cap is 65535 bytes and the dictionaries hold every weapon def).
            var lightKeys = RunAndGunSyncedSettings.LightKeys(local.weapons);
            if (synced.Matches(local.heavy, local.light, lightKeys, local.enableForAI, local.enableForFleeChance, local.accuracyPenalty, local.forbidden))
                return;

            // Row 405: the command carries two key lists now. Over the cap Multiplayer drops the packet, so say so instead of sending it.
            if (RunAndGunSyncedSettings.EstimateCommandBytes(lightKeys, local.forbidden) > 60000)
            {
                Log.Warning($"[MPCompat] RunAndGun: {lightKeys.Count} light and {local.forbidden.Count} forbidden weapons are too many for one command, settings not shared");
                // Row 405 review F2: in MP the simulation reads the session copy on every PC, so nothing "stays on this PC" when the push is refused.
                Messages.Message("[MPCompat] RunAndGun settings are too large to share; the session keeps its current settings.",
                    MessageTypeDefOf.RejectInput, false);
                return;
            }

            SyncedSetSettings(local.heavy, local.light, lightKeys.ToArray(), local.enableForAI, local.enableForFleeChance, local.accuracyPenalty, local.forbidden.ToArray());
        }

        // Runs on every PC from the same arguments. 7 parameters (row 405 added enableForAI, enableForFleeChance, accuracyPenalty, forbiddenKeys).
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SyncedSetSettings(int heavy, int light, string[] lightKeys, bool enableForAI, int enableForFleeChance, int accuracyPenalty, string[] forbiddenKeys)
        {
            var synced = RunAndGunSyncedSettings.Instance;
            if (synced == null || lightKeys == null || forbiddenKeys == null)
                return;

            var changed = !synced.Matches(heavy, light, new List<string>(lightKeys), enableForAI, enableForFleeChance, accuracyPenalty, new List<string>(forbiddenKeys));
            synced.Apply(heavy, light, lightKeys, enableForAI, enableForFleeChance, accuracyPenalty, forbiddenKeys);

            // Row 405: rebuild the session Settings copy that the simulation reads and log the compare line, on every PC.
            synced.ActivateSession();

            // Shown on every PC, so a look-only close by a client is visible too.
            if (changed)
                Messages.Message($"[MPCompat] RunAndGun session settings changed by a player: heavy {heavy}, light {light}, {lightKeys.Length} light weapons, " +
                                 $"enableForAI {enableForAI}, flee chance {enableForFleeChance}, accuracy penalty {accuracyPenalty}, {forbiddenKeys.Length} forbidden weapons",
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

        #region Row 405: session copy of the settings behind RunAndGun.RunAndGun.settings

        // Row 405 (2 Nov 2026). RunAndGun's simulation code reads enableForAI (CompRunAndGun.IsValid and Initialize, MentalStateHandler postfix),
        // enableForFleeChance (shouldRunAndGun, which calls Rand only for 1..99), accuracyPenalty (VerbProperties.AdjustedAccuracy postfix) and
        // forbiddenWeapons (CompRunAndGun.IsForbiddenWeapon) from the static field RunAndGun.RunAndGun.settings. That is 23 ldsfld sites in 20 methods,
        // all behind small or private methods (isEnabled getter, IsValid, IsForbiddenWeapon, shouldRunAndGun) that CoreCLR can inline into callers,
        // so a prefix/postfix swap per method (row 395 style) would be skipped at random.
        // Design instead: in MP the static field points at a session Settings object built from the synced values (RunAndGunSyncedSettings.ActivateSession),
        // so every read, in every method, sees the same values and no sim method is patched. The Mod instance keeps the LOCAL Settings object in its
        // own modSettings field; Mod.WriteSettings writes that one to disk (Verse.Mod.WriteSettings: modSettings.Write()), so config files are untouched.
        // The static field points at the local object only while RunAndGun's own settings UI runs and outside a multiplayer session.
        // RunAndGun writes the static field once, in the mod constructor (IL scan: one stsfld), so nothing overwrites our pointer.
        // Review F1: the UI is kept on the local object by patching RunAndGun's OWN draw method, Settings.DoWindowContents(Rect), not a window class.
        // Every caller ends there (Dialog_ModSettings via RunAndGun.DoSettingsWindowContents, XML Extensions' own mod-settings menu, any other mod menu),
        // and the IL scan of the 1.7 dll shows no static read inside it or inside Initialize and DrawUtility, which it calls.

        // Points the static field at the given Settings object. No-op when it already points there.
        internal static void SetPointer(object target)
        {
            if (target == null || settingsField == null)
                return;
            if (!ReferenceEquals(settingsField.GetValue(null), target))
                settingsField.SetValue(null, target);
        }

        // Outside a session (single player, main menu, after leaving MP) the static field points at the local object again.
        internal static void RestoreLocalPointer()
        {
            if (!SessionSwapEnabled)
                return;
            SetPointer(RunAndGunSyncedSettings.LocalSettings());
        }

        // Row 405 review F1: RunAndGun.Settings.DoWindowContents(Rect) (the method that draws RunAndGun's settings window) and a delegate that runs it on
        // the LOCAL Settings object. The delegate is built once per target object, after the load-time checks in PatchSessionSettingsSwap.
        private static MethodInfo settingsDrawMethod;
        private static Action<UnityEngine.Rect> localDraw;
        private static object localDrawTarget;

        private static void PatchSessionSettingsSwap()
        {
            // RunAndGun's settings window is drawn by Settings.DoWindowContents(Rect). RunAndGun.RunAndGun.DoSettingsWindowContents calls it on the object the
            // static field points at, which is the session copy in MP, and that copy must never be edited. Callers of Mod.DoSettingsWindowContents: the vanilla
            // Dialog_ModSettings and XML Extensions' own mod-settings menu (XmlExtensionsMenuModSettings), which has no Dialog_ModSettings. Patching the draw
            // method itself covers every caller. Inlining cannot skip it: IL size 1156 bytes (live 1.7 dll), non-virtual, no AggressiveInlining flag,
            // far over the JIT's inline limit. RunAndGun.RunAndGun.DoSettingsWindowContents (12 bytes of IL) is NOT patched, because a small virtual method could be inlined.
            var settingsType = AccessTools.TypeByName("RunAndGun.Settings");
            var settingsDraw = settingsType == null ? null : AccessTools.DeclaredMethod(settingsType, "DoWindowContents", new[] { typeof(UnityEngine.Rect) });
            if (settingsDraw != null && (settingsDraw.IsStatic || settingsDraw.ReturnType != typeof(void)))
                settingsDraw = null;

            // Dialog_CE (shown on the first open of the settings with Combat Extended active): its confirm button calls ResetForbidden and sets
            // dialogCEShown through the static field, then WriteSettings. IL size of DoWindowContents is 418 bytes, far over the inline limit.
            // Both patches are needed together: if either is missing the swap stays off, otherwise the UI would edit the session copy.
            var ceDialogType = AccessTools.TypeByName("RunAndGun.Dialog_CE");
            var ceDialogDoContents = ceDialogType == null ? null : AccessTools.DeclaredMethod(ceDialogType, "DoWindowContents");

            if (settingsDraw == null || ceDialogDoContents == null)
            {
                Log.Warning("[MPCompat] RunAndGun: couldn't find RunAndGun.Settings.DoWindowContents(Rect) or RunAndGun.Dialog_CE.DoWindowContents, " +
                            "the session copy of the RunAndGun settings stays off (enableForAI, flee chance, accuracy penalty and forbidden weapons stay local)");
                return;
            }

            settingsDrawMethod = settingsDraw;
            MpCompat.harmony.Patch(settingsDraw,
                prefix: new HarmonyMethod(typeof(RunandGunContinued), nameof(DrawOnLocalSettingsPrefix)));
            MpCompat.harmony.Patch(ceDialogDoContents,
                prefix: new HarmonyMethod(typeof(RunandGunContinued), nameof(UseLocalSettingsForCeDialogPrefix)),
                finalizer: new HarmonyMethod(typeof(RunandGunContinued), nameof(RestorePointerFinalizer)));

            // Leaving a session: the static field goes back to the local object, so the menus and a later single-player game never see session values.
            // Multiplayer.StopMultiplayer has 215 bytes of IL, over the inline limit. If this lookup fails the field is still reset by the next
            // single-player FinalizeInit and by the settings UI; only the time in the menu is affected.
            var stopMultiplayer = AccessTools.DeclaredMethod("Multiplayer.Client.Multiplayer:StopMultiplayer");
            if (stopMultiplayer != null)
                MpCompat.harmony.Patch(stopMultiplayer, finalizer: new HarmonyMethod(typeof(RunandGunContinued), nameof(StopMultiplayerFinalizer)));
            else
                Log.Warning("[MPCompat] RunAndGun: couldn't find Multiplayer.Client.Multiplayer:StopMultiplayer, the settings pointer is reset on the next single-player load instead");

            SessionSwapEnabled = true;
        }

        // The settings UI edits the object it is drawn on, and Mod.WriteSettings writes the mod instance's own (local) object. So the UI must always
        // draw on the local object. Runs before RunAndGun.Settings.DoWindowContents(Rect). When that call is about to draw on any other Settings object
        // (the session copy, which the static field points at in MP), this draws on the local object instead and skips the original. Edits then land in the
        // object that Mod.WriteSettings saves and WriteSettingsPostfix pushes, never in the session copy the simulation reads. For the local object
        // (also the nested call below) it does nothing. The static field points at the local object during the draw, in case a later RunAndGun version
        // reads it there, and goes back to the session copy afterwards, also after an exception.
        // Params: __instance is typed object because RunAndGun.Settings is not referenced at compile time; __0 is the Rect argument.
        private static bool DrawOnLocalSettingsPrefix(object __instance, UnityEngine.Rect __0)
        {
            var local = RunAndGunSyncedSettings.LocalSettings();
            if (__instance == null || local == null || ReferenceEquals(__instance, local))
                return true;

            SwitchToLocal(out var previous);
            try
            {
                LocalDraw(local)(__0);
            }
            finally
            {
                RestorePointerFinalizer(previous);
            }

            return false;
        }

        private static Action<UnityEngine.Rect> LocalDraw(object local)
        {
            if (localDraw == null || !ReferenceEquals(localDrawTarget, local))
            {
                localDraw = (Action<UnityEngine.Rect>)Delegate.CreateDelegate(typeof(Action<UnityEngine.Rect>), local, settingsDrawMethod);
                localDrawTarget = local;
            }

            return localDraw;
        }

        // Dialog_CE draws and acts through the static field (ResetForbidden, dialogCEShown), so the field points at the local object while it draws;
        // the finalizer puts the session pointer back, also after an exception.
        private static void UseLocalSettingsForCeDialogPrefix(out object __state)
        {
            SwitchToLocal(out __state);
        }

        private static void SwitchToLocal(out object previous)
        {
            previous = null;
            var local = RunAndGunSyncedSettings.LocalSettings();
            if (local == null)
                return;

            var current = settingsField.GetValue(null);
            if (ReferenceEquals(current, local))
                return;

            previous = current;
            settingsField.SetValue(null, local);
        }

        private static void RestorePointerFinalizer(object __state)
        {
            // In a session the field goes back to the session copy. Outside a session (e.g. the main menu after a session ended without the
            // StopMultiplayer hook) it stays on the local object that the prefix set.
            if (__state != null && MP.IsInMultiplayer)
                settingsField.SetValue(null, __state);
        }

        private static void StopMultiplayerFinalizer()
        {
            // Never throws into Multiplayer's disconnect path.
            try
            {
                if (!MP.IsInMultiplayer)
                    RestoreLocalPointer();
            }
            catch (Exception e)
            {
                Log.Warning($"[MPCompat] RunAndGun: couldn't reset the settings pointer after leaving multiplayer: {e.Message}");
            }
        }

        #endregion
    }


    public class RunAndGunSyncedSettings : GameComponent
    {
        public static RunAndGunSyncedSettings Instance;

        public int movementPenaltyHeavy = 40;
        public int movementPenaltyLight = 10;

        // defName -> isSelected, mirroring RunAndGun.Settings.selectedWeapons so the
        // light/heavy classification can't diverge between clients either.
        public Dictionary<string, bool> selectedWeaponsRaw = new Dictionary<string, bool>();

        // Row 405: the other settings RunAndGun's simulation code reads. Defaults are RunAndGun.Settings' own field initializers
        // (enableForAI = true, enableForFleeChance = 100, accuracyPenalty = 10), so a component saved before row 405 (keys absent)
        // gives the same values on every PC.
        public bool enableForAI = true;
        public int enableForFleeChance = 100;
        public int accuracyPenalty = 10;
        // defName of every weapon that RunAndGun.Settings.forbiddenWeapons marks isSelected (= forbidden). A missing key is not forbidden
        // (CompRunAndGun.IsForbiddenWeapon). RunAndGun starts that dictionary empty and fills it only when its Forbidden tab is drawn or its
        // CE dialog is confirmed (then it is saved in the player's config), so the default here is the empty list.
        public List<string> forbiddenKeys = new List<string>();

        [System.NonSerialized]
        private object cachedSnapshot;
        // Row 405: the Settings object RunAndGun.RunAndGun.settings points at during a multiplayer session. Built from the fields above.
        [System.NonSerialized]
        private object sessionSettings;
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
                // Row 405: single player reads the local Settings object again (a session ended without Multiplayer's StopMultiplayer hook running).
                RunandGunContinued.RestoreLocalPointer();
                return;
            }

            // In MP a saved component keeps its saved values on every PC, host included. No recapture.
            if (!captured)
            {
                // Component new to this MP save (e.g. mod added mid-game): keep the defaults on every PC and let a player push.
                captured = true;
                Log.Message("[MPCompat] RunAndGun: no saved synced settings in this multiplayer save, using defaults on every PC. " +
                            "A player can push their settings by opening and closing the RunAndGun settings window.");
            }

            // Row 405: the simulation reads the session copy of the settings from here on, on every PC.
            ActivateSession();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref movementPenaltyHeavy, "movementPenaltyHeavy", 40);
            Scribe_Values.Look(ref movementPenaltyLight, "movementPenaltyLight", 10);
            Scribe_Collections.Look(ref selectedWeaponsRaw, "selectedWeaponsRaw", LookMode.Value, LookMode.Value);
            Scribe_Values.Look(ref captured, "captured", false);
            // Row 405: absent keys (a save from before row 405) load RunAndGun's own defaults.
            Scribe_Values.Look(ref enableForAI, "enableForAI", true);
            Scribe_Values.Look(ref enableForFleeChance, "enableForFleeChance", 100);
            Scribe_Values.Look(ref accuracyPenalty, "accuracyPenalty", 10);
            Scribe_Collections.Look(ref forbiddenKeys, "forbiddenKeys", LookMode.Value);
            if (forbiddenKeys == null)
                forbiddenKeys = new List<string>();
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
        // Row 405: the other synced settings, the mod instance's own (local) settings field, and the Settings fields that are copied unchanged.
        private static Type modClassType, settingsClassType;
        private static FieldInfo enableForAIField, enableForFleeChanceField, accuracyPenaltyField, forbiddenWeaponsField, modInstanceSettingsField;
        private static FieldInfo[] copiedFields;
        private static Verse.Mod cachedMod;

        internal static bool EnsureReflection()
        {
            if (!reflectionInit)
            {
                reflectionInit = true;
                var missing = new List<string>();
                modClassType = AccessTools.TypeByName("RunAndGun.RunAndGun");
                settingsClassType = AccessTools.TypeByName("RunAndGun.Settings");
                weaponRecordType = AccessTools.TypeByName("RunAndGun.WeaponRecord");
                if (modClassType == null) missing.Add("type RunAndGun.RunAndGun");
                if (settingsClassType == null) missing.Add("type RunAndGun.Settings");
                if (weaponRecordType == null) missing.Add("type RunAndGun.WeaponRecord");

                if (modClassType != null && settingsClassType != null && weaponRecordType != null)
                {
                    modSettingsField = AccessTools.Field(modClassType, "settings");
                    heavyField = AccessTools.Field(settingsClassType, "movementPenaltyHeavy");
                    lightField = AccessTools.Field(settingsClassType, "movementPenaltyLight");
                    selectedWeaponsField = AccessTools.Field(settingsClassType, "selectedWeapons");
                    isSelectedField = AccessTools.Field(weaponRecordType, "isSelected");
                    enableForAIField = AccessTools.Field(settingsClassType, "enableForAI");
                    enableForFleeChanceField = AccessTools.Field(settingsClassType, "enableForFleeChance");
                    accuracyPenaltyField = AccessTools.Field(settingsClassType, "accuracyPenalty");
                    forbiddenWeaponsField = AccessTools.Field(settingsClassType, "forbiddenWeapons");
                    // Verse.Mod.modSettings is private; Mod.GetSettings<T> stores the object there and Mod.WriteSettings writes it.
                    modInstanceSettingsField = AccessTools.Field(typeof(Verse.Mod), "modSettings");

                    if (modSettingsField == null) missing.Add("RunAndGun.RunAndGun.settings");
                    if (heavyField == null) missing.Add("Settings.movementPenaltyHeavy");
                    if (lightField == null) missing.Add("Settings.movementPenaltyLight");
                    if (selectedWeaponsField == null) missing.Add("Settings.selectedWeapons");
                    if (isSelectedField == null) missing.Add("WeaponRecord.isSelected");
                    if (enableForAIField == null) missing.Add("Settings.enableForAI");
                    if (enableForFleeChanceField == null) missing.Add("Settings.enableForFleeChance");
                    if (accuracyPenaltyField == null) missing.Add("Settings.accuracyPenalty");
                    if (forbiddenWeaponsField == null) missing.Add("Settings.forbiddenWeapons");
                    if (modInstanceSettingsField == null) missing.Add("Verse.Mod.modSettings");

                    // Every other instance field of Settings (UI state, DualWieldInstalled excluded because the constructor sets it) is copied
                    // from the local object into the session copy, so the copy only differs in the synced values.
                    var syncedNames = new HashSet<string> { "movementPenaltyHeavy", "movementPenaltyLight", "selectedWeapons", "enableForAI",
                        "enableForFleeChance", "accuracyPenalty", "forbiddenWeapons" };
                    var copy = new List<FieldInfo>();
                    foreach (var field in settingsClassType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (!field.IsInitOnly && !syncedNames.Contains(field.Name))
                            copy.Add(field);
                    }

                    copiedFields = copy.ToArray();
                }

                if (missing.Count > 0)
                    Log.Warning("[MPCompat] RunAndGun: couldn't find " + string.Join(", ", missing.ToArray()));
            }

            return modSettingsField != null && heavyField != null && lightField != null &&
                   selectedWeaponsField != null && isSelectedField != null &&
                   enableForAIField != null && enableForFleeChanceField != null && accuracyPenaltyField != null &&
                   forbiddenWeaponsField != null && modInstanceSettingsField != null && copiedFields != null;
        }

        // Row 405: the Settings object of the RunAndGun mod instance = the LOCAL values of this PC. This is the object RunAndGun's settings window edits
        // (while the pointer swap is off the static field) and the object Mod.WriteSettings saves. In MP the static field points at the session copy
        // instead, so every read of "the local settings" goes through here.
        internal static object LocalSettings()
        {
            if (!EnsureReflection())
                return null;

            if (cachedMod == null)
                cachedMod = LoadedModManager.GetMod(modClassType);

            return cachedMod == null ? null : modInstanceSettingsField.GetValue(cachedMod);
        }

        // Row 405: the local values of one read, so the capture and the settings-window postfix share one shape.
        internal sealed class LocalValues
        {
            public int heavy, light;
            public Dictionary<string, bool> weapons;
            public bool enableForAI;
            public int enableForFleeChance, accuracyPenalty;
            public List<string> forbidden;
        }

        // Row 395: read-only read of the local RunAndGun settings, shared by the capture and the settings-window postfix.
        // Row 405: reads LocalSettings() (the mod instance's object), not the static field, which points at the session copy in MP.
        internal static bool TryReadLocalSettings(out LocalValues values)
        {
            values = null;

            if (!EnsureReflection())
                return false;

            var settings = LocalSettings();
            if (settings == null)
                return false;

            var read = new LocalValues
            {
                heavy = (int)heavyField.GetValue(settings),
                light = (int)lightField.GetValue(settings),
                enableForAI = (bool)enableForAIField.GetValue(settings),
                enableForFleeChance = (int)enableForFleeChanceField.GetValue(settings),
                accuracyPenalty = (int)accuracyPenaltyField.GetValue(settings),
                weapons = new Dictionary<string, bool>(),
                forbidden = new List<string>()
            };

            var selectedWeapons = selectedWeaponsField.GetValue(settings) as System.Collections.IDictionary;
            if (selectedWeapons != null)
            {
                foreach (System.Collections.DictionaryEntry entry in selectedWeapons)
                {
                    var record = entry.Value;
                    if (record != null)
                        read.weapons[(string)entry.Key] = (bool)isSelectedField.GetValue(record);
                }
            }

            // Row 405: only the forbidden entries (isSelected = true) are kept; a missing key means not forbidden (CompRunAndGun.IsForbiddenWeapon).
            // The dictionary can be null for a moment (RunAndGun.ResetForbidden).
            var forbiddenWeapons = forbiddenWeaponsField.GetValue(settings) as System.Collections.IDictionary;
            if (forbiddenWeapons != null)
            {
                foreach (System.Collections.DictionaryEntry entry in forbiddenWeapons)
                {
                    var record = entry.Value;
                    if (record != null && entry.Key is string key && (bool)isSelectedField.GetValue(record))
                        read.forbidden.Add(key);
                }
            }

            values = read;
            return true;
        }

        // Row 405: rough size of the sync command, to stay under Multiplayer's 65535 byte command cap (CommandPacket maxLength). Each string
        // is counted with its UTF-8 bytes plus a generous 6 bytes of length prefix.
        internal static int EstimateCommandBytes(List<string> lightKeys, List<string> forbiddenKeys)
        {
            var bytes = 64;
            foreach (var key in lightKeys)
                bytes += Encoding.UTF8.GetByteCount(key) + 6;
            foreach (var key in forbiddenKeys)
                bytes += Encoding.UTF8.GetByteCount(key) + 6;
            return bytes;
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
            if (!TryReadLocalSettings(out var local))
                return;

            movementPenaltyHeavy = local.heavy;
            movementPenaltyLight = local.light;
            selectedWeaponsRaw = local.weapons;
            enableForAI = local.enableForAI;
            enableForFleeChance = local.enableForFleeChance;
            accuracyPenalty = local.accuracyPenalty;
            forbiddenKeys = local.forbidden;
            cachedSnapshot = null;
        }

        // Row 395: true when the given values give the same TicksPerMove result as the session copy (compares heavy, light and the set of light weapons).
        // Row 405: also compares enableForAI, enableForFleeChance, accuracyPenalty and the set of forbidden weapons.
        public bool Matches(int heavy, int light, List<string> lightKeys, bool ai, int fleeChance, int accuracy, List<string> forbidden)
        {
            if (heavy != movementPenaltyHeavy || light != movementPenaltyLight)
                return false;
            if (ai != enableForAI || fleeChance != enableForFleeChance || accuracy != accuracyPenalty)
                return false;

            var own = new HashSet<string>(LightKeys(selectedWeaponsRaw));
            if (!own.SetEquals(lightKeys))
                return false;

            return new HashSet<string>(forbiddenKeys).SetEquals(forbidden);
        }

        // Row 395: writes the pushed values on every PC (called from the synced method). Light weapons only; the rest count as heavy.
        // Row 405: also the three other values and the forbidden weapons (a weapon that is not listed is not forbidden).
        public void Apply(int heavy, int light, string[] lightKeys, bool ai, int fleeChance, int accuracy, string[] forbidden)
        {
            movementPenaltyHeavy = heavy;
            movementPenaltyLight = light;
            selectedWeaponsRaw = new Dictionary<string, bool>();
            foreach (var key in lightKeys)
            {
                if (key != null)
                    selectedWeaponsRaw[key] = false;
            }

            enableForAI = ai;
            enableForFleeChance = fleeChance;
            accuracyPenalty = accuracy;
            forbiddenKeys = new List<string>();
            foreach (var key in forbidden)
            {
                if (key != null)
                    forbiddenKeys.Add(key);
            }

            cachedSnapshot = null;
        }

        private object BuildSnapshotDictionary()
        {
            if (!EnsureReflection())
                return null;

            return BuildRecordDictionary(selectedWeaponsRaw);
        }

        // Dictionary<string, RunAndGun.WeaponRecord> with isSelected set from the given pairs (the other WeaponRecord fields are UI only).
        private static object BuildRecordDictionary(IEnumerable<KeyValuePair<string, bool>> items)
        {
            var dictType = typeof(Dictionary<,>).MakeGenericType(typeof(string), weaponRecordType);
            var dict = Activator.CreateInstance(dictType);
            var addMethod = dictType.GetMethod("Add");

            foreach (var kv in items)
            {
                var record = Activator.CreateInstance(weaponRecordType);
                isSelectedField.SetValue(record, kv.Value);
                addMethod.Invoke(dict, new object[] { kv.Key, record });
            }

            return dict;
        }

        // Row 405: builds (first call) or refreshes (later calls) the session Settings object from the saved values and points
        // RunAndGun.RunAndGun.settings at it, then logs the compare line. Called at FinalizeInit when MP is active and after each synced apply.
        // The session object is a new RunAndGun.Settings (its constructor sets DualWieldInstalled the same way as for the local one), the UI-only
        // fields are copied from the local object, and the synced fields come from this component, so every PC builds the same simulation values.
        internal void ActivateSession()
        {
            if (!RunandGunContinued.SessionSwapEnabled)
            {
                Log.Warning("[MPCompat] RunAndGun: the settings pointer swap is off, enableForAI, flee chance, accuracy penalty and forbidden weapons stay local");
            }
            else
            {
                var session = RefreshSessionObject();
                if (session != null)
                    RunandGunContinued.SetPointer(session);
                else
                    Log.Warning("[MPCompat] RunAndGun: couldn't build the session copy of the settings, enableForAI, flee chance, accuracy penalty and forbidden weapons stay local");
            }

            // One line per load / apply, the same text on every PC when the settings agree. Used by the lab compare.
            Log.Message($"[MPCompat] RunAndGun session settings: heavy={movementPenaltyHeavy} light={movementPenaltyLight} " +
                        $"lightWeapons={LightKeys(selectedWeaponsRaw).Count} enableForAI={enableForAI} flee={enableForFleeChance} " +
                        $"accuracyPenalty={accuracyPenalty} forbidden={forbiddenKeys.Count}");
        }

        private object RefreshSessionObject()
        {
            try
            {
                var local = LocalSettings();
                if (local == null || !EnsureReflection())
                    return null;

                if (sessionSettings == null)
                    sessionSettings = Activator.CreateInstance(settingsClassType);

                foreach (var field in copiedFields)
                    field.SetValue(sessionSettings, field.GetValue(local));

                heavyField.SetValue(sessionSettings, movementPenaltyHeavy);
                lightField.SetValue(sessionSettings, movementPenaltyLight);
                enableForAIField.SetValue(sessionSettings, enableForAI);
                enableForFleeChanceField.SetValue(sessionSettings, enableForFleeChance);
                accuracyPenaltyField.SetValue(sessionSettings, accuracyPenalty);
                selectedWeaponsField.SetValue(sessionSettings, selectedWeaponsSnapshot);

                var forbidden = new List<KeyValuePair<string, bool>>();
                foreach (var key in forbiddenKeys)
                    forbidden.Add(new KeyValuePair<string, bool>(key, true));
                forbiddenWeaponsField.SetValue(sessionSettings, BuildRecordDictionary(forbidden));

                return sessionSettings;
            }
            catch (Exception e)
            {
                Log.Warning($"[MPCompat] RunAndGun: building the session settings failed: {e}");
                return null;
            }
        }
    }
}
