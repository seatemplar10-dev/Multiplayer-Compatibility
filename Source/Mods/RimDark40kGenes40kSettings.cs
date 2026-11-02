using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using UnityEngine;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>RimDark 40k - Mankind's Finest (Genes40k): the per-PC mod settings (Genes40kModSettings) made one session value set</summary>
    /// <remarks>
    /// FIXES row 370 (2 Nov 2026), built on the row 405 pattern (RunandGunContinued). Written against Genes40k.dll 1.1.14 from the folder 1.7 falls back to
    /// (loadFolders.xml of the mod defines v1.5 and v1.6 only; Verse.ModContentPack.InitLoadFolders takes the highest defined version that is not above the
    /// running one, so the 1.6 folder loads). Genes40k is reached by name only, no reference to the dll.
    /// Problem: the simulation reads Genes40kModSettings, a per-PC config file (join compares it once, a "Connect anyway" join or a mid-session edit splits it).
    /// What the simulation reads (all fields of the class are in the list below; chapterShoulderIcon only through the custom preset):
    ///  - Genes40kUtils.ModSettings (static cache): psychicPhenomena, psykerPariahBirth(+Chance), perpetualBirth(+Chance), livingSaintSystem/OnlyBaselinerAscension/Male/
    ///    AddedChanceFromViolenceSkills/Limit/BaseChance, implantationSuccessOffset, implantationCapOffset, matrixGestationTimeFactor, allowFemalePrimarchBirths, and
    ///    CurrentlySelectedPreset (SetupChapterForPawn with randomChapter false). Building_GeneTable.ModSettings (static cache): psychicCrafting.
    ///  - GameComponent_LivingSaint caches the object in its CONSTRUCTOR (livingSaintBigThreat, livingSaintSmallThreat); the constructor runs before any component's ExposeData.
    ///  - GameComponent_MankindFinestUtils (instance cache): CurrentlySelectedPreset / CustomPreset decide the raid chapter list (RandomElement over a list that differs per PC).
    ///  - CompChapterColor.InitialColors (instance cache): chapterColorOne/Two/Three become the SAVED colours of new chapter apparel; CompChapterColorWithShoulderDecoration
    ///    SetUpMisc: CurrentlySelectedPreset.relatedChapterIcon + chapterShoulderIconColor become a saved shoulder icon. Building_DecorativeFlag CONSTRUCTOR: the preset's
    ///    colours and icon path become saved fields of the flag. Building_ChapterBanner/BuildingMulti/BuildingSingle and the two pawn render nodes read colours to draw only.
    /// Why no pointer swap (row 405 style): there is no static field. Every class caches its own reference (two static caches, six instance caches, one constructor cache)
    /// from LoadedModManager.GetMod[Genes40kMod]().GetSettings[Genes40kModSettings](), i.e. Verse.Mod.modSettings. A swap of that field only reaches readers that ask later; the
    /// constructor cache and every instance created before the swap would keep the local object.
    /// Design here, one stable object with swapped content:
    ///  - LIVE object = Verse.Mod.modSettings of the Genes40kMod instance = what every simulation reader holds. Its identity never changes. In MP its VALUES are the session values.
    ///  - LOCAL object = a second Genes40kModSettings (made when the session starts) that holds this PC's own values. It is the object the settings UI edits:
    ///    the private field Genes40kMod.settings (what CoreMod.Settings hands to every ModSettingTab and, through the tab, to Dialog_ChangeDefaultChapterColour) is pointed at it.
    ///    No UI class is patched.
    ///  - Mod.WriteSettings writes Verse.Mod.modSettings, so a prefix points that field at the LOCAL object for the call and a finalizer puts it back (the config file keeps the
    ///    local values; session values are never written to disk). A postfix pushes on an explicit change. Nothing else calls ModSettings.Write (grep of Genes40k and Core40k).
    ///  - Session values live in a GameComponent (saved), are captured from the LIVE object in single player only (so the host's values reach the clients through the save),
    ///    and are written into the LIVE object at ExposeData (LoadingVars) and FinalizeInit in MP, and after every synced push.
    ///  - Leaving MP (Multiplayer.StopMultiplayer, or the next single player FinalizeInit) copies the LOCAL values back into the LIVE object and points the UI field at it again.
    ///  - Push: explicit change only. The local values after the write are compared with the local values this module last saw (session start or the last write), so a
    ///    look-only close sends nothing. The command carries this player's local set plus a mask of the value groups HE changed (each bool, each number, the base
    ///    chance, and the chapter group = preset, three colours, shoulder icon and colour, custom preset). Every PC copies only the masked groups into the session set,
    ///    so a player who moves one slider does not push his stale copies of the other values over another player's changes. Last explicit writer wins per group.
    /// Patches: Verse.Mod.WriteSettings (20 bytes of IL; callers RimWorld.Dialog_ModSettings.PreClose and RimWorld.Dialog_Options.PreClose run once per window close) and
    /// Multiplayer.Client.Multiplayer:StopMultiplayer (optional). No sim method is patched, so CoreCLR inlining cannot skip anything.
    /// Possible later follow-up (NOT done): in multifaction co-op the session set is shared by both player factions. Per-faction chapter settings (Sea's ME/SW faction
    /// vs James's Black Templars) would need the session set keyed by faction id and the readers to know the pawn's or builder's faction; the readers use no faction today.
    /// Status: [unverified] compiled only, not run in game.
    /// </remarks>
    [MpCompatFor("phonicmas.rimdark.mankindsfinest")]
    public class RimDark40kGenes40kSettings
    {
        private const string LogPrefix = "[MPCompat] Genes40k: ";

        // true only when every lookup, the sync registration and the WriteSettings patch worked; nothing in this file changes any object while it is false
        internal static bool Active;

        // Genes40k and Verse members, looked up once (all or nothing)
        private static Type modType, settingsType, chapterDefType, iconDefType;
        private static FieldInfo g40kModSettingsField;       // Genes40k.Genes40kMod.settings (private): the object CoreMod.Settings gives to the settings tabs
        private static FieldInfo verseModSettingsField;      // Verse.Mod.modSettings (private): what GetSettings returns and WriteSettings writes
        private static MethodInfo getSettingsMethod;         // Verse.Mod.GetSettings[Genes40kModSettings]
        private static MethodInfo modPropertySetter;         // Verse.ModSettings.Mod (internal setter)
        private static PropertyInfo selectedProperty, customProperty;
        private static FieldInfo[] flagFields, intFields, colourFields;
        private static FieldInfo baseChanceField, iconField, iconColourField;
        private static FieldInfo cpPrimary, cpSecondary, cpTertiary, cpIcon, cpIconColour;

        // session state (this PC)
        private static Verse.Mod modInstance;
        private static object liveObject, localObject;
        private static bool sessionActive;
        private static G40kValues lastLocal;
        private static bool warnedMissingDef;

        public RimDark40kGenes40kSettings(ModContentPack mod)
        {
            try
            {
                if (!ResolveReflection())
                {
                    Log.Warning(LogPrefix + "couldn't resolve every Genes40k member (see the line above), skipping the settings sync");
                    return;
                }

                MP.RegisterSyncMethod(typeof(RimDark40kGenes40kSettings), nameof(SyncedSetSettings)).CancelIfAnyArgNull();

                // Mod.WriteSettings is called when the settings window closes (Dialog_ModSettings.PreClose, Dialog_Options.PreClose, XML Extensions' menu).
                var write = AccessTools.DeclaredMethod(typeof(Verse.Mod), nameof(Verse.Mod.WriteSettings));
                if (write == null)
                    throw new MissingMethodException("Verse.Mod.WriteSettings");

                MpCompat.harmony.Patch(write,
                    prefix: new HarmonyMethod(typeof(RimDark40kGenes40kSettings), nameof(WriteSettingsPrefix)),
                    postfix: new HarmonyMethod(typeof(RimDark40kGenes40kSettings), nameof(WriteSettingsPostfix)),
                    finalizer: new HarmonyMethod(typeof(RimDark40kGenes40kSettings), nameof(WriteSettingsFinalizer)));

                // Leaving a session: the LIVE object gets the local values back. If this lookup fails the next single player FinalizeInit does it.
                var stopMultiplayer = AccessTools.DeclaredMethod("Multiplayer.Client.Multiplayer:StopMultiplayer");
                if (stopMultiplayer != null)
                    MpCompat.harmony.Patch(stopMultiplayer, finalizer: new HarmonyMethod(typeof(RimDark40kGenes40kSettings), nameof(StopMultiplayerFinalizer)));
                else
                    Log.Warning(LogPrefix + "couldn't find Multiplayer.Client.Multiplayer:StopMultiplayer, the local values come back on the next single player load instead");

                Active = true;
            }
            catch (Exception e)
            {
                Active = false;
                Log.Warning(LogPrefix + "the session settings stay off (the settings remain per PC): " + e);
            }
        }

        #region Reflection

        // All lookups happen here and nothing is changed before all of them worked. Every missing member is listed in one warning.
        private static bool ResolveReflection()
        {
            var missing = new List<string>();

            modType = AccessTools.TypeByName("Genes40k.Genes40kMod");
            settingsType = AccessTools.TypeByName("Genes40k.Genes40kModSettings");
            chapterDefType = AccessTools.TypeByName("Genes40k.ChapterColourDef");
            iconDefType = AccessTools.TypeByName("Genes40k.ShoulderIconDef");
            if (modType == null) missing.Add("type Genes40k.Genes40kMod");
            if (settingsType == null) missing.Add("type Genes40k.Genes40kModSettings");
            if (chapterDefType == null) missing.Add("type Genes40k.ChapterColourDef");
            if (iconDefType == null) missing.Add("type Genes40k.ShoulderIconDef");

            if (missing.Count == 0)
            {
                g40kModSettingsField = Field(modType, "settings", settingsType, missing);
                verseModSettingsField = Field(typeof(Verse.Mod), "modSettings", typeof(ModSettings), missing);
                modPropertySetter = typeof(ModSettings).GetProperty("Mod", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetSetMethod(true);
                if (modPropertySetter == null) missing.Add("Verse.ModSettings.Mod setter");

                var getSettings = typeof(Verse.Mod).GetMethod("GetSettings", BindingFlags.Instance | BindingFlags.Public);
                getSettingsMethod = getSettings == null ? null : getSettings.MakeGenericMethod(settingsType);
                if (getSettingsMethod == null) missing.Add("Verse.Mod.GetSettings[T]");

                selectedProperty = Property(settingsType, "CurrentlySelectedPreset", chapterDefType, missing);
                customProperty = Property(settingsType, "CustomPreset", chapterDefType, missing);

                flagFields = new FieldInfo[G40kValues.FlagNames.Length];
                for (var i = 0; i < flagFields.Length; i++)
                    flagFields[i] = Field(settingsType, G40kValues.FlagNames[i], typeof(bool), missing);

                intFields = new FieldInfo[G40kValues.IntNames.Length];
                for (var i = 0; i < intFields.Length; i++)
                    intFields[i] = Field(settingsType, G40kValues.IntNames[i], typeof(int), missing);

                colourFields = new FieldInfo[G40kValues.ColourNames.Length];
                for (var i = 0; i < colourFields.Length; i++)
                    colourFields[i] = Field(settingsType, G40kValues.ColourNames[i], typeof(Color), missing);

                baseChanceField = Field(settingsType, "livingSaintBaseChance", typeof(float), missing);
                iconField = Field(settingsType, "chapterShoulderIcon", iconDefType, missing);
                iconColourField = Field(settingsType, "chapterShoulderIconColor", typeof(Color?), missing);

                cpPrimary = Field(chapterDefType, "primaryColour", typeof(Color), missing);
                cpSecondary = Field(chapterDefType, "secondaryColour", typeof(Color), missing);
                cpTertiary = Field(chapterDefType, "tertiaryColour", typeof(Color?), missing);
                cpIcon = Field(chapterDefType, "relatedChapterIcon", iconDefType, missing);
                cpIconColour = Field(chapterDefType, "chapterIconColour", typeof(Color), missing);

                if (!typeof(Def).IsAssignableFrom(chapterDefType)) missing.Add("Genes40k.ChapterColourDef is not a Def");
                if (!typeof(Def).IsAssignableFrom(iconDefType)) missing.Add("Genes40k.ShoulderIconDef is not a Def");
                if (!typeof(Verse.Mod).IsAssignableFrom(modType)) missing.Add("Genes40k.Genes40kMod is not a Verse.Mod");
            }

            if (missing.Count > 0)
                Log.Warning(LogPrefix + "couldn't find " + string.Join(", ", missing.ToArray()));

            return missing.Count == 0;
        }

        private static FieldInfo Field(Type type, string name, Type fieldType, List<string> missing)
        {
            var field = AccessTools.Field(type, name);
            if (field == null || field.IsStatic || field.FieldType != fieldType)
            {
                missing.Add($"{type.FullName}.{name} ({fieldType.Name} field)");
                return null;
            }

            return field;
        }

        private static PropertyInfo Property(Type type, string name, Type propertyType, List<string> missing)
        {
            var property = AccessTools.Property(type, name);
            if (property == null || property.PropertyType != propertyType || !property.CanRead)
            {
                missing.Add($"{type.FullName}.{name} ({propertyType.Name} property)");
                return null;
            }

            return property;
        }

        #endregion

        #region The two settings objects

        private static Verse.Mod ModInstance()
        {
            if (modInstance == null)
                modInstance = LoadedModManager.GetMod(modType);
            return modInstance;
        }

        // The object the simulation readers hold: Verse.Mod.modSettings of the Genes40kMod instance (GetSettings loads it when it is not there yet).
        private static object LiveSettings()
        {
            var mod = ModInstance();
            if (mod == null)
                return null;

            return verseModSettingsField.GetValue(mod) ?? getSettingsMethod.Invoke(mod, null);
        }

        // Session start on this PC (idempotent). Makes the LOCAL object from the LIVE object (which still holds this PC's own values at this moment),
        // keeps what it holds as the baseline for "explicit change", and points the settings UI at it.
        internal static bool EnsureSplit()
        {
            if (sessionActive)
                return true;

            var mod = ModInstance();
            var live = LiveSettings();
            if (mod == null || live == null)
                return false;

            var local = Activator.CreateInstance(settingsType);
            modPropertySetter.Invoke(local, new object[] { mod });
            WriteValues(local, ReadValues(live), true);

            lastLocal = ReadValues(local).Normalized();
            g40kModSettingsField.SetValue(mod, local);
            liveObject = live;
            localObject = local;
            sessionActive = true;
            return true;
        }

        // Session end on this PC: the LIVE object gets this PC's own values back (including edits made during the session) and the UI field points at it again.
        internal static void EndSplit()
        {
            if (!sessionActive)
                return;

            sessionActive = false;
            try
            {
                WriteValues(liveObject, ReadValues(localObject), true);
            }
            catch (Exception e)
            {
                Log.Warning(LogPrefix + "couldn't copy the local values back after the session: " + e.Message);
            }

            var mod = ModInstance();
            if (mod != null)
                g40kModSettingsField.SetValue(mod, liveObject);
            localObject = null;
            lastLocal = null;
        }

        // Values of a fresh Genes40kModSettings = the mod's own defaults, the same on every PC.
        internal static G40kValues PristineValues()
        {
            return ReadValues(Activator.CreateInstance(settingsType));
        }

        internal static G40kValues ReadLive()
        {
            var live = LiveSettings();
            return live == null ? null : ReadValues(live);
        }

        internal static void ApplyToLive(G40kValues values)
        {
            // The custom preset object is only written when the session preset is the custom one (its contents are not read otherwise).
            WriteValues(liveObject, values, false);
        }

        #endregion

        #region Reading and writing a Genes40kModSettings object

        private static string DefName(object def) => (def as Def)?.defName ?? "";

        private static Def FindDef(Type defType, string defName)
        {
            if (string.IsNullOrEmpty(defName))
                return null;

            var def = GenDefDatabase.GetDefSilentFail(defType, defName, false);
            if (def == null && !warnedMissingDef)
            {
                warnedMissingDef = true;
                Log.Warning(LogPrefix + $"def {defType.Name} '{defName}' is not loaded on this PC, the value is kept as it was (the mod lists differ)");
            }

            return def;
        }

        // Every value the settings object holds, as plain data. Raw: the custom preset contents are read even when another preset is selected.
        internal static G40kValues ReadValues(object settings)
        {
            var v = new G40kValues();
            for (var i = 0; i < flagFields.Length; i++)
                v.flags[i] = (bool)flagFields[i].GetValue(settings);
            for (var i = 0; i < intFields.Length; i++)
                v.ints[i] = (int)intFields[i].GetValue(settings);

            v.floats[G40kValues.FloatBaseChance] = (float)baseChanceField.GetValue(settings);
            for (var i = 0; i < colourFields.Length; i++)
                G40kValues.SetColour(v.floats, G40kValues.FloatColours + 4 * i, (Color)colourFields[i].GetValue(settings));

            v.names[G40kValues.NameIcon] = DefName(iconField.GetValue(settings));
            var iconColour = iconColourField.GetValue(settings);
            v.flags[G40kValues.FlagIconColour] = iconColour != null;
            if (iconColour != null)
                G40kValues.SetColour(v.floats, G40kValues.FloatIconColour, (Color)iconColour);

            var custom = customProperty.GetValue(settings);
            var selected = selectedProperty.GetValue(settings);
            v.names[G40kValues.NamePreset] = selected == null || ReferenceEquals(selected, custom) ? "" : DefName(selected);

            if (custom != null)
            {
                G40kValues.SetColour(v.floats, G40kValues.FloatCpPrimary, (Color)cpPrimary.GetValue(custom));
                G40kValues.SetColour(v.floats, G40kValues.FloatCpSecondary, (Color)cpSecondary.GetValue(custom));
                var tertiary = cpTertiary.GetValue(custom);
                v.flags[G40kValues.FlagCpTertiary] = tertiary != null;
                if (tertiary != null)
                    G40kValues.SetColour(v.floats, G40kValues.FloatCpTertiary, (Color)tertiary);
                v.names[G40kValues.NameCpIcon] = DefName(cpIcon.GetValue(custom));
                G40kValues.SetColour(v.floats, G40kValues.FloatCpIconColour, (Color)cpIconColour.GetValue(custom));
            }

            return v;
        }

        // Writes the values into the settings object. writeCustom: also write the custom preset contents when another preset is selected
        // (copying a whole object); the contents are always written when the custom preset is the selected one.
        internal static void WriteValues(object settings, G40kValues v, bool writeCustom)
        {
            for (var i = 0; i < flagFields.Length; i++)
                flagFields[i].SetValue(settings, v.flags[i]);
            for (var i = 0; i < intFields.Length; i++)
                intFields[i].SetValue(settings, v.ints[i]);

            baseChanceField.SetValue(settings, v.floats[G40kValues.FloatBaseChance]);
            for (var i = 0; i < colourFields.Length; i++)
                colourFields[i].SetValue(settings, G40kValues.GetColour(v.floats, G40kValues.FloatColours + 4 * i));

            var iconName = v.names[G40kValues.NameIcon];
            if (iconName.Length == 0)
                iconField.SetValue(settings, null);
            else
            {
                var icon = FindDef(iconDefType, iconName);
                if (icon != null)
                    iconField.SetValue(settings, icon);
            }

            iconColourField.SetValue(settings, v.flags[G40kValues.FlagIconColour] ? (object)G40kValues.GetColour(v.floats, G40kValues.FloatIconColour) : null);

            // The getter makes the custom preset from the colours and icon set above when the object has none yet.
            var custom = customProperty.GetValue(settings);
            var presetName = v.names[G40kValues.NamePreset];
            var customSelected = presetName.Length == 0;

            if (custom != null && (writeCustom || customSelected))
            {
                cpPrimary.SetValue(custom, G40kValues.GetColour(v.floats, G40kValues.FloatCpPrimary));
                cpSecondary.SetValue(custom, G40kValues.GetColour(v.floats, G40kValues.FloatCpSecondary));
                cpTertiary.SetValue(custom, v.flags[G40kValues.FlagCpTertiary] ? (object)G40kValues.GetColour(v.floats, G40kValues.FloatCpTertiary) : null);
                var cpIconName = v.names[G40kValues.NameCpIcon];
                if (cpIconName.Length == 0)
                    cpIcon.SetValue(custom, null);
                else
                {
                    var icon = FindDef(iconDefType, cpIconName);
                    if (icon != null)
                        cpIcon.SetValue(custom, icon);
                }

                cpIconColour.SetValue(custom, G40kValues.GetColour(v.floats, G40kValues.FloatCpIconColour));
            }

            if (customSelected)
                selectedProperty.SetValue(settings, custom);
            else
            {
                var preset = FindDef(chapterDefType, presetName);
                if (preset != null)
                    selectedProperty.SetValue(settings, preset);
            }
        }

        #endregion

        #region Push, apply and the Mod.WriteSettings hooks

        // Runs on every PC from the same arguments. Plain arrays only: colours travel as r,g,b,a floats, defs as def names (see G40kValues for the layout).
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SyncedSetSettings(bool[] flags, int[] ints, float[] floats, string[] names, bool[] groups)
        {
            var component = Genes40kSyncedSettings.Instance;
            if (!Active || component == null)
                return;

            if (!G40kValues.ShapeOk(flags, ints, floats, names) || groups == null || groups.Length != G40kValues.GroupCount)
            {
                Log.Warning(LogPrefix + "a pushed settings command has the wrong shape (different MP-Compat builds?), ignored");
                return;
            }

            // Only the groups the sender changed are taken from him; everything else stays as the session has it.
            var incoming = G40kValues.FromArrays(flags, ints, floats, names);
            var merged = component.values.MergedWith(incoming, groups);
            var changed = component.values.Diff(merged);
            component.values.CopyFrom(merged);
            component.captured = true;

            // Rewrites the LIVE object from the session values and logs the compare line, on every PC.
            component.ActivateSession(true);

            // Shown on every PC, so a push by a client is visible to the host too.
            if (changed.Count > 0)
                Messages.Message("[MPCompat] Genes40k session settings changed by a player: " + string.Join(", ", changed.ToArray()),
                    MessageTypeDefOf.NeutralEvent, false);
        }

        // Mod.WriteSettings writes Verse.Mod.modSettings, which holds the SESSION values during a session. For the length of the call it points at the LOCAL object
        // instead, so the config file keeps this PC's own values.
        private static void WriteSettingsPrefix(Verse.Mod __instance, out object __state)
        {
            __state = null;
            if (!sessionActive || localObject == null || __instance == null || __instance.GetType() != modType)
                return;

            var current = verseModSettingsField.GetValue(__instance);
            if (ReferenceEquals(current, localObject))
                return;

            __state = current;
            verseModSettingsField.SetValue(__instance, localObject);
        }

        // Also runs after an exception in the write, so the field never stays on the local object.
        private static void WriteSettingsFinalizer(Verse.Mod __instance, object __state)
        {
            if (__state != null && __instance != null)
                verseModSettingsField.SetValue(__instance, __state);
        }

        private static void WriteSettingsPostfix(Verse.Mod __instance)
        {
            if (!Active || __instance == null || __instance.GetType() != modType)
                return;

            try
            {
                if (!MP.IsInMultiplayer)
                {
                    // Single player: the LIVE object is the local one. Keep the component current, so a change made after an SP load
                    // reaches the MP session this game is later hosted as.
                    if (Current.Game != null)
                    {
                        // A session that ended without the StopMultiplayer hook: the local values go back into the LIVE object first.
                        EndSplit();
                        Genes40kSyncedSettings.Instance?.CaptureFromLive();
                    }
                    return;
                }

                PushIfChangedByThisPlayer();
            }
            catch (Exception e)
            {
                Log.Warning(LogPrefix + "the settings close hook failed: " + e);
            }
        }

        // Pushes the local values when THIS player changed them since the session started or since the last close, never on a look-only close.
        private static void PushIfChangedByThisPlayer()
        {
            var component = Genes40kSyncedSettings.Instance;
            if (!sessionActive || localObject == null || component == null || Current.Game == null)
                return;

            var current = ReadValues(localObject).Normalized();
            if (lastLocal == null)
                return;

            // The groups THIS player changed since the session started or since his last close. None = a look-only close.
            var groups = current.ChangedGroups(lastLocal);
            if (!Array.Exists(groups, g => g))
                return;

            // Outside the interface (e.g. a long event) Multiplayer does not sync, so the call would run on this PC only. lastLocal stays, the next close retries.
            if (!MP.InInterface)
                return;

            lastLocal = current;

            // The session already holds exactly these values (another player pushed the same).
            if (!component.values.MergedWith(current, groups).Differs(component.values))
                return;

            if (current.EstimateCommandBytes() > 60000)
            {
                Log.Warning(LogPrefix + "the settings are too large for one command, not shared");
                Messages.Message("[MPCompat] Genes40k settings are too large to share; the session keeps its current settings.", MessageTypeDefOf.RejectInput, false);
                return;
            }

            SyncedSetSettings(current.flags, current.ints, current.floats, current.names, groups);
        }

        private static void StopMultiplayerFinalizer()
        {
            // Never throws into Multiplayer's disconnect path.
            try
            {
                if (!MP.IsInMultiplayer)
                    EndSplit();
            }
            catch (Exception e)
            {
                Log.Warning(LogPrefix + "couldn't restore the local values after leaving multiplayer: " + e.Message);
            }
        }

        #endregion
    }

    /// <summary>
    /// The session values of Genes40kModSettings, saved with the game. Single player keeps them equal to the local settings; in MP they are what the
    /// LIVE settings object holds on every PC. Present in every game (all GameComponents are); it does nothing and writes no save key unless the module is active.
    /// </summary>
    public class Genes40kSyncedSettings : GameComponent
    {
        internal static Genes40kSyncedSettings Instance;

        internal readonly G40kValues values = new G40kValues();
        // Saved. False = no values were ever stored here (a save made without the module, or a game new to the module), so MP uses the mod's own defaults.
        internal bool captured;

        public Genes40kSyncedSettings(Game game) { }

        public override void FinalizeInit()
        {
            base.FinalizeInit();

            // Nothing to sync when Genes40k is not loaded or has changed shape. The module already logged why; no warning for a game without the mod.
            if (!RimDark40kGenes40kSettings.Active)
                return;

            Instance = this;

            // Single player: the LIVE object holds this PC's own values (a session that ended without the StopMultiplayer hook is undone first).
            // A host starts MP from an SP-loaded game and this capture is what the clients then receive in the host's save.
            if (!MP.IsInMultiplayer)
            {
                RimDark40kGenes40kSettings.EndSplit();
                CaptureFromLive();
                return;
            }

            // In MP a saved component keeps its saved values on every PC, host included. No recapture.
            if (!captured)
            {
                // Component new to this MP save (the mod was added mid-game): the mod's own defaults on every PC, a player can push his values.
                values.CopyFrom(RimDark40kGenes40kSettings.PristineValues());
                captured = true;
                Log.Message("[MPCompat] Genes40k: no saved session settings in this multiplayer save, using the mod's default settings on every PC. " +
                            "A player can push his own by changing them in the mod settings and closing the window.");
            }

            ActivateSession(true);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref captured, "captured", false);
            values.Expose();

            // Components load before the world and the maps, so loading code that reads the settings (the Building_DecorativeFlag constructor, apparel set up
            // during a map load) already sees the session values. FinalizeInit applies them again and logs.
            if (Scribe.mode == LoadSaveMode.LoadingVars && captured && RimDark40kGenes40kSettings.Active && MP.IsInMultiplayer)
            {
                Instance = this;
                ActivateSession(false);
            }
        }

        // Single player: the component follows the LIVE (= local) values.
        internal void CaptureFromLive()
        {
            if (!RimDark40kGenes40kSettings.Active)
                return;

            try
            {
                var live = RimDark40kGenes40kSettings.ReadLive();
                if (live == null)
                    return;

                values.CopyFrom(live);
                captured = true;
            }
            catch (Exception e)
            {
                Log.Warning("[MPCompat] Genes40k: couldn't read the local settings: " + e.Message);
            }
        }

        // Writes the session values into the LIVE object (the one every reader holds) and, when asked, logs the compare line:
        // one line per MP load and per apply, the same text on every PC when the values agree. Used by the lab compare.
        internal void ActivateSession(bool log)
        {
            if (!RimDark40kGenes40kSettings.Active)
                return;

            try
            {
                if (!RimDark40kGenes40kSettings.EnsureSplit())
                {
                    Log.Warning("[MPCompat] Genes40k: couldn't find the settings object, the Genes40k settings stay local");
                    return;
                }

                RimDark40kGenes40kSettings.ApplyToLive(values);
            }
            catch (Exception e)
            {
                Log.Warning("[MPCompat] Genes40k: applying the session settings failed, the Genes40k settings may stay local: " + e);
                return;
            }

            if (log)
                Log.Message("[MPCompat] Genes40k session settings: " + values.Describe());
        }
    }

    /// <summary>
    /// Plain data of Genes40kModSettings (every field the simulation reads), four arrays so one synced method and one save routine cover them.
    /// Layout: flags[0..8] = FlagNames, flags[9] = chapterShoulderIconColor has a value, flags[10] = custom preset tertiary colour has a value;
    /// ints = IntNames; floats[0] = livingSaintBaseChance, then colours as r,g,b,a: chapterColorOne/Two/Three, chapterShoulderIconColor, custom preset primary,
    /// secondary, tertiary, icon colour; names[0] = selected preset def name ("" = the custom preset), names[1] = chapterShoulderIcon def name, names[2] = custom preset icon def name.
    /// All defaults are zero / false / "", so an untouched component writes nothing to a save.
    /// </summary>
    internal sealed class G40kValues
    {
        internal static readonly string[] FlagNames =
        {
            "psychicPhenomena", "psykerPariahBirth", "perpetualBirth", "livingSaintSystem", "livingSaintOnlyBaselinerAscension",
            "livingSaintMale", "livingSaintAddedChanceFromViolenceSkills", "psychicCrafting", "allowFemalePrimarchBirths"
        };

        internal static readonly string[] IntNames =
        {
            "psykerPariahBirthChance", "perpetualBirthChance", "livingSaintLimit", "livingSaintBigThreat", "livingSaintSmallThreat",
            "implantationSuccessOffset", "implantationCapOffset", "matrixGestationTimeFactor"
        };

        internal static readonly string[] ColourNames = { "chapterColorOne", "chapterColorTwo", "chapterColorThree" };

        internal const int FlagIconColour = 9, FlagCpTertiary = 10;
        internal const int FloatBaseChance = 0, FloatColours = 1, FloatIconColour = 13, FloatCpPrimary = 17, FloatCpSecondary = 21, FloatCpTertiary = 25, FloatCpIconColour = 29;
        internal const int NamePreset = 0, NameIcon = 1, NameCpIcon = 2;
        internal const int FlagCount = 11, IntCount = 8, FloatCount = 33, NameCount = 3;

        internal readonly bool[] flags = new bool[FlagCount];
        internal readonly int[] ints = new int[IntCount];
        internal readonly float[] floats = new float[FloatCount];
        internal readonly string[] names = { "", "", "" };

        internal static Color GetColour(float[] f, int at) => new Color(f[at], f[at + 1], f[at + 2], f[at + 3]);

        internal static void SetColour(float[] f, int at, Color c)
        {
            f[at] = c.r;
            f[at + 1] = c.g;
            f[at + 2] = c.b;
            f[at + 3] = c.a;
        }

        internal static bool ShapeOk(bool[] flags, int[] ints, float[] floats, string[] names)
            => flags != null && ints != null && floats != null && names != null &&
               flags.Length == FlagCount && ints.Length == IntCount && floats.Length == FloatCount && names.Length == NameCount;

        internal static G40kValues FromArrays(bool[] flags, int[] ints, float[] floats, string[] names)
        {
            var v = new G40kValues();
            Array.Copy(flags, v.flags, FlagCount);
            Array.Copy(ints, v.ints, IntCount);
            Array.Copy(floats, v.floats, FloatCount);
            for (var i = 0; i < NameCount; i++)
                v.names[i] = names[i] ?? "";
            return v;
        }

        internal void CopyFrom(G40kValues other)
        {
            Array.Copy(other.flags, flags, FlagCount);
            Array.Copy(other.ints, ints, IntCount);
            Array.Copy(other.floats, floats, FloatCount);
            for (var i = 0; i < NameCount; i++)
                names[i] = other.names[i] ?? "";
        }

        // The custom preset contents only count while the custom preset is the selected one (the simulation reads CurrentlySelectedPreset only).
        // So a player who picked custom colours in the dialog but pressed Close instead of Accept is not seen as having changed anything.
        internal G40kValues Normalized()
        {
            var v = new G40kValues();
            v.CopyFrom(this);
            if (v.names[NamePreset].Length != 0)
            {
                Array.Clear(v.floats, FloatCpPrimary, FloatCount - FloatCpPrimary);
                v.flags[FlagCpTertiary] = false;
                v.names[NameCpIcon] = "";
            }
            else if (!v.flags[FlagCpTertiary])
                Array.Clear(v.floats, FloatCpTertiary, 4);

            if (!v.flags[FlagIconColour])
                Array.Clear(v.floats, FloatIconColour, 4);
            return v;
        }

        internal bool Differs(G40kValues other) => Diff(other).Count > 0;

        // Value groups of a push: 0..8 one per bool (FlagNames), 9..16 one per number (IntNames), 17 livingSaintBaseChance,
        // 18 the chapter group (preset, three colours, shoulder icon, shoulder icon colour, custom preset contents). They belong together:
        // the dialog sets preset, colours and icon in one Accept.
        internal const int GroupCount = 19, GroupBaseChance = 17, GroupChapter = 18;

        // Which groups differ between this (the newer values) and before (both normalised).
        internal bool[] ChangedGroups(G40kValues before)
        {
            var a = before.Normalized();
            var b = Normalized();
            var groups = new bool[GroupCount];
            for (var i = 0; i < FlagNames.Length; i++)
                groups[i] = a.flags[i] != b.flags[i];
            for (var i = 0; i < IntNames.Length; i++)
                groups[FlagNames.Length + i] = a.ints[i] != b.ints[i];
            groups[GroupBaseChance] = FloatsDiffer(a, b, FloatBaseChance, 1);
            groups[GroupChapter] = ChapterGroupDiffers(a, b);
            return groups;
        }

        private static bool ChapterGroupDiffers(G40kValues a, G40kValues b)
        {
            for (var i = 0; i < NameCount; i++)
                if (a.names[i] != b.names[i])
                    return true;
            return a.flags[FlagIconColour] != b.flags[FlagIconColour] || a.flags[FlagCpTertiary] != b.flags[FlagCpTertiary] ||
                   FloatsDiffer(a, b, FloatColours, FloatCount - FloatColours);
        }

        // The session values with the given groups taken from incoming. Every PC runs this on the same two sets, so the result is the same everywhere.
        internal G40kValues MergedWith(G40kValues incoming, bool[] groups)
        {
            var result = Normalized();
            var n = incoming.Normalized();
            for (var i = 0; i < FlagNames.Length; i++)
                if (groups[i])
                    result.flags[i] = n.flags[i];
            for (var i = 0; i < IntNames.Length; i++)
                if (groups[FlagNames.Length + i])
                    result.ints[i] = n.ints[i];
            if (groups[GroupBaseChance])
                result.floats[FloatBaseChance] = n.floats[FloatBaseChance];
            if (groups[GroupChapter])
            {
                for (var i = 0; i < NameCount; i++)
                    result.names[i] = n.names[i];
                result.flags[FlagIconColour] = n.flags[FlagIconColour];
                result.flags[FlagCpTertiary] = n.flags[FlagCpTertiary];
                Array.Copy(n.floats, FloatColours, result.floats, FloatColours, FloatCount - FloatColours);
            }

            return result.Normalized();
        }

        // Names of the values that differ (normalised on both sides).
        internal List<string> Diff(G40kValues other)
        {
            var a = Normalized();
            var b = other.Normalized();
            var res = new List<string>();

            for (var i = 0; i < FlagNames.Length; i++)
                if (a.flags[i] != b.flags[i])
                    res.Add(FlagNames[i]);
            for (var i = 0; i < IntNames.Length; i++)
                if (a.ints[i] != b.ints[i])
                    res.Add(IntNames[i]);
            if (FloatsDiffer(a, b, FloatBaseChance, 1))
                res.Add("livingSaintBaseChance");
            for (var i = 0; i < ColourNames.Length; i++)
                if (FloatsDiffer(a, b, FloatColours + 4 * i, 4))
                    res.Add(ColourNames[i]);
            if (a.names[NamePreset] != b.names[NamePreset])
                res.Add("chapter preset");
            if (a.names[NameIcon] != b.names[NameIcon])
                res.Add("chapterShoulderIcon");
            if (a.flags[FlagIconColour] != b.flags[FlagIconColour] || FloatsDiffer(a, b, FloatIconColour, 4))
                res.Add("chapterShoulderIconColor");
            if (a.flags[FlagCpTertiary] != b.flags[FlagCpTertiary] || a.names[NameCpIcon] != b.names[NameCpIcon] ||
                FloatsDiffer(a, b, FloatCpPrimary, FloatCount - FloatCpPrimary))
                res.Add("custom chapter colours");
            return res;
        }

        private static bool FloatsDiffer(G40kValues a, G40kValues b, int from, int count)
        {
            for (var i = from; i < from + count; i++)
                if (BitConverter.SingleToInt32Bits(a.floats[i]) != BitConverter.SingleToInt32Bits(b.floats[i]))
                    return true;
            return false;
        }

        internal int EstimateCommandBytes()
        {
            // 64 for the framing, 4 per number, each string with its UTF-8 bytes plus a generous 6 bytes of length prefix.
            var bytes = 64 + FlagCount + GroupCount + IntCount * 4 + FloatCount * 4;
            foreach (var name in names)
                bytes += Encoding.UTF8.GetByteCount(name ?? "") + 6;
            return bytes;
        }

        // Saved with the game component. Every key has a default of zero / false / "" (Scribe skips default values).
        internal void Expose()
        {
            for (var i = 0; i < FlagCount; i++)
                Scribe_Values.Look(ref flags[i], "f" + i, false);
            for (var i = 0; i < IntCount; i++)
                Scribe_Values.Look(ref ints[i], "i" + i, 0);
            for (var i = 0; i < FloatCount; i++)
                Scribe_Values.Look(ref floats[i], "d" + i, 0f);
            for (var i = 0; i < NameCount; i++)
            {
                var name = names[i] ?? "";
                Scribe_Values.Look(ref name, "n" + i, "");
                names[i] = name ?? "";
            }
        }

        // 32 bit FNV-1a over every value of the normalised set, so the lab compare sees any difference, also one below the 8 bit colour digits of Describe.
        private int Hash()
        {
            unchecked
            {
                var n = Normalized();
                var h = (uint)2166136261;
                foreach (var b in n.flags) h = (h ^ (b ? 1u : 0u)) * 16777619;
                foreach (var i in n.ints) h = (h ^ (uint)i) * 16777619;
                foreach (var f in n.floats) h = (h ^ (uint)BitConverter.SingleToInt32Bits(f)) * 16777619;
                foreach (var s in n.names)
                {
                    foreach (var c in s) h = (h ^ c) * 16777619;
                    h = (h ^ 0xFFu) * 16777619;
                }

                return (int)h;
            }
        }

        private static string Hex(float[] f, int at) => ColorUtility.ToHtmlStringRGBA(GetColour(f, at));

        // "preset=<defName or custom> colors=<RRGGBBAA x3> <other sim fields> hash=<8 hex>"
        internal string Describe()
        {
            var n = Normalized();
            var sb = new StringBuilder();
            var custom = n.names[NamePreset].Length == 0;
            sb.Append("preset=").Append(custom ? "custom" : n.names[NamePreset]);
            sb.Append(" colors=").Append(Hex(n.floats, FloatColours)).Append(',').Append(Hex(n.floats, FloatColours + 4)).Append(',').Append(Hex(n.floats, FloatColours + 8));
            sb.Append(" shoulderIcon=").Append(n.names[NameIcon].Length == 0 ? "none" : n.names[NameIcon]);
            sb.Append(" shoulderIconColor=").Append(n.flags[FlagIconColour] ? Hex(n.floats, FloatIconColour) : "none");
            if (custom)
            {
                sb.Append(" custom=").Append(Hex(n.floats, FloatCpPrimary)).Append(',').Append(Hex(n.floats, FloatCpSecondary)).Append(',')
                    .Append(n.flags[FlagCpTertiary] ? Hex(n.floats, FloatCpTertiary) : "none");
                sb.Append(" customIcon=").Append(n.names[NameCpIcon].Length == 0 ? "none" : n.names[NameCpIcon]);
                sb.Append(" customIconColor=").Append(Hex(n.floats, FloatCpIconColour));
            }

            for (var i = 0; i < FlagNames.Length; i++)
                sb.Append(' ').Append(FlagNames[i]).Append('=').Append(n.flags[i] ? "True" : "False");
            for (var i = 0; i < IntNames.Length; i++)
                sb.Append(' ').Append(IntNames[i]).Append('=').Append(n.ints[i]);
            sb.Append(" livingSaintBaseChance=").Append(n.floats[FloatBaseChance].ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(" hash=").Append(Hash().ToString("X8"));
            return sb.ToString();
        }
    }
}
