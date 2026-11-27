using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using UnityEngine;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Progression: Ammunition by Ferny</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3798845290"/>
    /// <remarks>
    /// FIXES row 535 (27 Nov 2026). The mod keeps its settings in ProgressionAmmunitionMod.settings (one ProgressionAmmunitionSettings object per
    /// process, loaded from the PC's own mod settings file). The simulation reads 12 of them through that static: enableMod, onlyColonistsUseAmmo,
    /// scaleMaxAmmoByBurstShotCount, baselineMaxAmmo, refillBuildingsAreInfinite, autoRefillWithConsumable and the 7 *RefillChance sliders
    /// (RefillUtility, CompAmmo.MaxAmmo, Building_AmmoRecharger, 4 CompRefuelable patches, PawnGenerator gear patch, Verb_LaunchProjectile patch).
    /// WriteSettings also rewrites the tradeability of the 3 refill defs from the local enableMod.
    /// Fix, multiplayer only: a GameComponent keeps the 12 values in the save (the host captures its own; joiners get them in the host's save).
    /// In multiplayer ProgressionAmmunitionMod.settings points to a second settings object that holds those values, so every sim read sees them.
    /// The mod's settings window and WriteSettings work on the local object (swapped in for their duration). A window close with changed values
    /// pushes them through one synced method (last writer wins), which also re-applies the refill tradeability from the synced enableMod; the
    /// mod's own tradeability rewrite in WriteSettings is skipped in multiplayer. Single player is untouched.
    /// </remarks>
    [MpCompatFor("ferny.progressionammunition")]
    public class ProgressionAmmunition
    {
        private const string LogPrefix = "[MPCompat - Progression Ammunition] ";
        internal static readonly string[] Names =
        {
            "enableMod", "onlyColonistsUseAmmo", "refillBuildingsAreInfinite", "autoRefillWithConsumable", "scaleMaxAmmoByBurstShotCount", "baselineMaxAmmo",
            "animalRefillChance", "neolithicRefillChance", "medievalRefillChance", "industrialRefillChance", "spacerRefillChance", "ultraRefillChance", "archotechRefillChance",
        };
        // Settings fields the simulation does not read: copied from the local object to the synced one so the UI/scenario code still sees the PC's own values.
        private static readonly string[] LocalOnly = { "showOnlyDrafted", "addRefillBuildingsToScenarios" };
        internal static FieldInfo[] fields;
        internal static FieldInfo[] localOnlyFields;
        internal static FieldInfo settingsStatic;
        internal static Type settingsType, modType;
        private static MethodInfo applyEnabledState;
        private static object localSettings, syncedSettings;
        private static bool inWrite;
        // Set last: until then the component and every patch method return at once.
        internal static volatile bool Active;

        public ProgressionAmmunition(ModContentPack mod)
        {
            if (LabOff.Row(535))
            {
                Log.Message(LogPrefix + "row 535 switched off by the lab, the settings stay local to each PC");
                return;
            }

            modType = AccessTools.TypeByName("ProgressionAmmunition.ProgressionAmmunitionMod");
            settingsType = AccessTools.TypeByName("ProgressionAmmunition.ProgressionAmmunitionSettings");
            settingsStatic = modType == null ? null : AccessTools.Field(modType, "settings");
            applyEnabledState = AccessTools.Method("ProgressionAmmunition.RefillUtility:ApplyEnabledState");
            var draw = modType == null ? null : AccessTools.Method(modType, "DoSettingsWindowContents");
            var write = modType == null ? null : AccessTools.Method(modType, "WriteSettings");
            bool ok = settingsType != null && settingsStatic != null && settingsStatic.FieldType == settingsType && applyEnabledState != null && draw != null && write != null;
            if (ok)
            {
                fields = new FieldInfo[Names.Length];
                for (int i = 0; i < Names.Length; i++)
                {
                    fields[i] = AccessTools.Field(settingsType, Names[i]);
                    if (fields[i] == null) ok = false;
                }
                localOnlyFields = new FieldInfo[LocalOnly.Length];
                for (int i = 0; i < LocalOnly.Length; i++)
                    localOnlyFields[i] = AccessTools.Field(settingsType, LocalOnly[i]);
            }
            if (!ok)
            {
                Log.Error(LogPrefix + "row 535: ProgressionAmmunition members not found (mod type " + (modType != null) + ", settings type " + (settingsType != null)
                          + ", settings static " + (settingsStatic != null) + ", ApplyEnabledState " + (applyEnabledState != null) + ", draw " + (draw != null) + ", WriteSettings " + (write != null)
                          + "), the settings stay local to each PC");
                return;
            }

            var swapIn = new HarmonyMethod(typeof(ProgressionAmmunition), nameof(UseLocalPrefix));
            var swapOut = new HarmonyMethod(typeof(ProgressionAmmunition), nameof(UseSyncedFinalizer));
            MpCompat.harmony.Patch(draw, prefix: swapIn, finalizer: swapOut);
            MpCompat.harmony.Patch(write, prefix: new HarmonyMethod(typeof(ProgressionAmmunition), nameof(WritePrefix)), finalizer: new HarmonyMethod(typeof(ProgressionAmmunition), nameof(WriteFinalizer)));
            MpCompat.harmony.Patch(applyEnabledState, prefix: new HarmonyMethod(typeof(ProgressionAmmunition), nameof(SkipTradeabilityInWrite)));
            MP.RegisterSyncMethod(typeof(ProgressionAmmunition), nameof(SyncedSet));
            Active = true;
            Log.Message(LogPrefix + "row 535: the 6 sim settings and 7 refill chances are saved with the game, synced on a settings window close, and read from there in multiplayer; the tradeability rewrite in WriteSettings is skipped in multiplayer");
        }

        internal static object[] ReadFrom(object settings)
        {
            var v = new object[fields.Length];
            for (int i = 0; i < fields.Length; i++)
                v[i] = fields[i].GetValue(settings);
            return v;
        }

        internal static object CurrentStatic => settingsStatic.GetValue(null);

        // Remember the PC's own object whenever the static does not point to our synced one.
        private static void Remember()
        {
            var cur = CurrentStatic;
            if (cur != null && !ReferenceEquals(cur, syncedSettings))
                localSettings = cur;
        }

        internal static object[] LocalValues()
        {
            Remember();
            return localSettings == null ? null : ReadFrom(localSettings);
        }

        // Point the mod's static to the object holding the component's values (multiplayer) or back to the PC's own object (single player).
        internal static void Install(ProgressionAmmunitionSyncedSettings comp)
        {
            Remember();
            if (localSettings == null)
                return;
            if (!MP.IsInMultiplayer || comp == null || !comp.captured)
            {
                if (syncedSettings != null && ReferenceEquals(CurrentStatic, syncedSettings))
                    settingsStatic.SetValue(null, localSettings);
                return;
            }
            if (syncedSettings == null)
                syncedSettings = Activator.CreateInstance(settingsType);
            var vals = comp.ToArray();
            for (int i = 0; i < fields.Length; i++)
                fields[i].SetValue(syncedSettings, vals[i]);
            CopyLocalOnly();
            settingsStatic.SetValue(null, syncedSettings);
        }

        private static void CopyLocalOnly()
        {
            if (syncedSettings == null || localSettings == null)
                return;
            foreach (var f in localOnlyFields)
                if (f != null)
                    f.SetValue(syncedSettings, f.GetValue(localSettings));
        }

        // The mod's settings window and WriteSettings work on the PC's own object.
        internal static void UseLocalPrefix(out bool __state)
        {
            __state = false;
            if (!Active || syncedSettings == null || !ReferenceEquals(CurrentStatic, syncedSettings) || localSettings == null)
                return;
            settingsStatic.SetValue(null, localSettings);
            __state = true;
        }

        internal static Exception UseSyncedFinalizer(bool __state, Exception __exception)
        {
            if (__state)
            {
                CopyLocalOnly();
                settingsStatic.SetValue(null, syncedSettings);
            }
            return __exception;
        }

        internal static void WritePrefix(out bool __state)
        {
            UseLocalPrefix(out __state);
            // The mod's WriteSettings calls RefillUtility.ApplyEnabledState from the local enableMod; skipped while in multiplayer with synced values.
            inWrite = __state;
        }

        // Any player closing the settings window pushes the values (last writer wins).
        internal static Exception WriteFinalizer(bool __state, Exception __exception)
        {
            inWrite = false;
            UseSyncedFinalizer(__state, __exception);
            try
            {
                var synced = ProgressionAmmunitionSyncedSettings.Instance;
                if (!Active || !__state || synced == null)
                    return __exception;
                // Outside the interface (a long event) Multiplayer does not sync, the call would run on this PC only.
                if (!MP.IsInMultiplayer || !MP.InInterface)
                    return __exception;
                var local = LocalValues();
                if (local == null || synced.Matches(local))
                    return __exception;
                // 9 parameters at most: Harmony MethodInvoker.GetHandler throws ArgumentException "opcode" at 10+ (D2 cu1/cu3).
                int flags = ((bool)local[0] ? 1 : 0) | ((bool)local[1] ? 2 : 0) | ((bool)local[2] ? 4 : 0) | ((bool)local[3] ? 8 : 0) | ((bool)local[4] ? 16 : 0);
                SyncedSet(flags, (int)local[5],
                    (float)local[6], (float)local[7], (float)local[8], (float)local[9], (float)local[10], (float)local[11], (float)local[12]);
            }
            catch (Exception e)
            {
                Log.Error(LogPrefix + "row 535: settings window hook failed: " + e);
            }
            return __exception;
        }

        internal static bool SkipTradeabilityInWrite()
        {
            return !inWrite;
        }

        // Runs on every PC from the same arguments.
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void SyncedSet(int flags, int baseline,
            float animal, float neolithic, float medieval, float industrial, float spacer, float ultra, float archotech)
        {
            bool enableMod = (flags & 1) != 0, onlyColonists = (flags & 2) != 0, refillInfinite = (flags & 4) != 0,
                autoRefill = (flags & 8) != 0, scale = (flags & 16) != 0;
            var synced = ProgressionAmmunitionSyncedSettings.Instance;
            if (synced == null)
                return;
            synced.Set(new object[] { enableMod, onlyColonists, refillInfinite, autoRefill, scale, baseline, animal, neolithic, medieval, industrial, spacer, ultra, archotech });
            Install(synced);
            ApplyTradeability();
            Messages.Message(LogPrefix + "settings changed by a player: mod enabled " + enableMod + ", max ammo baseline " + baseline, MessageTypeDefOf.NeutralEvent, false);
        }

        // RefillUtility.ApplyEnabledState reads Enabled from the static, which now points to the synced values.
        internal static void ApplyTradeability()
        {
            try { applyEnabledState?.Invoke(null, null); }
            catch (Exception e) { Log.Error(LogPrefix + "row 535: ApplyEnabledState failed: " + e); }
        }
    }

    /// <summary>The Progression: Ammunition values the simulation uses in multiplayer, saved with the game (row 535). Inert unless the module is active.</summary>
    public class ProgressionAmmunitionSyncedSettings : GameComponent
    {
        public static ProgressionAmmunitionSyncedSettings Instance;
        // The mod's own defaults (ProgressionAmmunitionSettings field initializers).
        public bool enableMod = true, onlyColonists = true, refillInfinite = true, autoRefill = false, scale = true;
        public int baseline = 30;
        public float animal = 0f, neolithic = 5f, medieval = 15f, industrial = 15f, spacer = 15f, ultra = 15f, archotech = 15f;
        public bool captured;

        public ProgressionAmmunitionSyncedSettings(Game game) { }

        public object[] ToArray() => new object[] { enableMod, onlyColonists, refillInfinite, autoRefill, scale, baseline, animal, neolithic, medieval, industrial, spacer, ultra, archotech };

        public void Set(object[] v)
        {
            enableMod = (bool)v[0]; onlyColonists = (bool)v[1]; refillInfinite = (bool)v[2]; autoRefill = (bool)v[3]; scale = (bool)v[4]; baseline = (int)v[5];
            animal = (float)v[6]; neolithic = (float)v[7]; medieval = (float)v[8]; industrial = (float)v[9]; spacer = (float)v[10]; ultra = (float)v[11]; archotech = (float)v[12];
            captured = true;
        }

        public bool Matches(object[] v)
        {
            if (!captured)
                return false;
            var mine = ToArray();
            for (int i = 0; i < mine.Length; i++)
                if (!Equals(mine[i], v[i]))
                    return false;
            return true;
        }

        public void CaptureLocal()
        {
            var local = ProgressionAmmunition.LocalValues();
            if (local != null)
                Set(local);
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            Instance = this;
            if (!ProgressionAmmunition.Active)
                return;
            // Single player, or the host: take the local values (a game hosted from a single-player load, as RunAndGun row 395 does).
            // A joiner gets the host's saved values in the host's save and keeps them.
            if (!MP.IsInMultiplayer || (!captured && MP.IsHosting))
                CaptureLocal();
            else if (!captured)
            {
                captured = true;
                Log.Message("[MPCompat - Progression Ammunition] row 535: no saved values in this multiplayer save, using the mod defaults on every PC. A player can push theirs by closing the settings window.");
            }
            ProgressionAmmunition.Install(this);
            // Multiplayer: refill tradeability from the synced enableMod. Single player: back from the local one.
            ProgressionAmmunition.ApplyTradeability();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            // forceSave: a value equal to the default must still be written, or a joiner would fall back to its own local value.
            if (!ProgressionAmmunition.Active)
                return;
            Scribe_Values.Look(ref captured, "captured", false, true);
            Scribe_Values.Look(ref enableMod, "enableMod", true, true);
            Scribe_Values.Look(ref onlyColonists, "onlyColonists", true, true);
            Scribe_Values.Look(ref refillInfinite, "refillInfinite", true, true);
            Scribe_Values.Look(ref autoRefill, "autoRefill", false, true);
            Scribe_Values.Look(ref scale, "scale", true, true);
            Scribe_Values.Look(ref baseline, "baseline", 30, true);
            Scribe_Values.Look(ref animal, "animal", 0f, true);
            Scribe_Values.Look(ref neolithic, "neolithic", 5f, true);
            Scribe_Values.Look(ref medieval, "medieval", 15f, true);
            Scribe_Values.Look(ref industrial, "industrial", 15f, true);
            Scribe_Values.Look(ref spacer, "spacer", 15f, true);
            Scribe_Values.Look(ref ultra, "ultra", 15f, true);
            Scribe_Values.Look(ref archotech, "archotech", 15f, true);
        }
    }
}
