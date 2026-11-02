using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;
using Verse.Sound;

namespace Multiplayer.Compat
{
    /// <summary>Vehicle Framework by SmashPhil: the mod settings the simulation reads (FIXES row 366)</summary>
    /// <remarks>
    /// Status: [unverified] compiled only, not run in game.
    /// <para>
    /// Problem: the simulation reads <c>Vehicles.VehicleMod.settings</c> (a static <c>VehiclesModSettings</c>, loaded from each player's own
    /// config file) in 105 methods. Two PCs with different config files give different results (damage multipliers, pathing flags,
    /// per-vehicle stats, river costs, ...). Same design as row 405 (RunAndGun): a GameComponent saves the values, and in a multiplayer
    /// session the static points at a SESSION settings object built from them. Nothing in VF's simulation code is patched.
    /// </para>
    /// <para>
    /// Pointer rules: the static points at the player's LOCAL object (the one in <c>Verse.Mod.modSettings</c>, which
    /// <c>Mod.WriteSettings</c> saves) in single player, in the menus, and while VF's own settings UI draws. In a session it points at the
    /// session object. The UI draw methods switch it to local for the duration of the draw (prefix + finalizer), so edits always land
    /// in the local object. The session object has no <c>Mod</c>, so a stray <c>Write()</c> throws instead of overwriting the config.
    /// </para>
    /// <para>
    /// Push: <c>VehicleMod.WriteSettings</c> (the override, 70 bytes of IL, called virtually) gets a postfix. If this player changed a
    /// synced value since the settings window opened (snapshot at the first draw, compare at WriteSettings), one synced command
    /// <c>SyncedSetSettings</c> sends the local values and every PC applies them (last writer wins). Look-only closes send nothing.
    /// </para>
    /// <para>
    /// Baked values: <c>customRiverCosts</c> and <c>empStuns</c> are computed once in <c>VehicleProperties.ResolveReferences</c> from the
    /// settings, so a swap alone does not reach them. <see cref="RefreshBaked"/> rebuilds both whenever the pointer changes (activation,
    /// apply, leaving the session) and re-caches turret state. XML-defined river costs equal to <c>riverCost</c> cannot be told apart from
    /// generated ones and may be removed when the river multiplier is lowered (see the module notes in the lane report).
    /// </para>
    /// <para>
    /// showAllCargoItems: the load cargo dialog is not an owned window, so it draws with the static on the SESSION object and watches that object's field
    /// (<c>showAllCargoItemsField.Watch(VehicleMod.settings)</c>, Source_Referenced/VehicleFramework.cs:1769). The registered sync worker reads
    /// <c>VehicleMod.settings</c> on each PC when the field change arrives, which is the session object everywhere, so every PC sets the same field on its
    /// own session object. The session object is built once per process and refreshed in place at each apply.
    /// </para>
    /// <para>
    /// showAllCargoItems is also a SIM value (review N1): the synced <c>LoadVehicleCargoSession</c> constructor calls AddItems, which reads it through
    /// Dialog_LoadCargo.AddItemsToTransferables (Dialog_LoadCargo.cs:248). So it is a session value, not a per-PC one: the component saves it with the mod default
    /// (false), captures it from the local object in single player, and in a session reads it from the session object when saving (the synced field's toggles
    /// live there). <see cref="BuildSession"/> sets it on the session object at EVERY build, from the saved value, never from this PC's local file. A synced
    /// apply first pulls the live session value into the component (<c>PullShowAllFromSession</c>), so an apply never reverts a toggle. Every join point
    /// (save, reload) gives every PC the same value. The existing sync field and its PostApply are untouched. It is not part of the pushed set.
    /// </para>
    /// <para>
    /// Review fixes (Opus review of the first version): allowDiagonalRendering is a synced member (VehiclePawn.Angle feeds turret aim). The "Reset all"
    /// confirmation is replaced in a session by the same steps on the local object (the Enter key runs it outside any draw), without releasing vehicle
    /// reservations. The UI-triggered turret recaches are skipped while the settings UI draws in a session, because they would change this PC's vehicles
    /// before the push. A synced apply also resets the per-pawn stat caches (vehicleStats).
    /// </para>
    /// <para>
    /// Residuals: (1) useCustomShaders is local to each PC (see <see cref="WarnLocalOnlyValues"/>). (2) A multiplayer save made without this component
    /// (an older save, or Vehicle Framework added mid-game) uses the mod defaults on every PC, not the host's values, until a player pushes. (3) The apply
    /// message is hidden on the other PCs by row 305 (a message of another player's command); the "VF session settings" log line is written on every PC.
    /// (4) Aerial vehicles in flight keep their stat caches until their next dirty event (same on every PC).
    /// (5) Review N2: the world reachability grouping (GridOwners.World.Init: customRiverCosts key sets, MoveSpeed) and VehicleHarmony.AllMoveableVehicleDefs
    /// (MoveSpeed != 0) are built once at startup from this PC's LOCAL settings and are not regrouped mid-game (the grids are keyed by owner). They can differ
    /// between PCs only with a different river multiplier or a zero MoveSpeed edit; this is covered by Multiplayer's join config check, and
    /// <see cref="WarnStartupDerived"/> logs a warning when the session values could cause it. The XML river-cost note above is the same kind of residual.
    /// </para>
    /// <para>
    /// Push scope (review N3): a push always sends the player's WHOLE local set (last writer wins), so one changed value also carries the rest of that player's
    /// local file and resets values another player pushed earlier that differ from it. Look-only closes send nothing. Review N4: the stat editor window
    /// (<c>Dialog_StatSettings</c>, a separate non-modal window) is closed together with the settings window in a session, so its edits are always pushed.
    /// </para>
    /// </remarks>
    [MpCompatFor("SmashPhil.VehicleFramework")]
    public class VehicleFrameworkSettings
    {
        // Set last, after every lookup, registration and patch. Every patch method and the GameComponent return at once while false.
        internal static bool Active;

        // Simulation members of SectionMain / SectionDebug that are pushed (checked against the 105-method read scan, row 366 lane).
        internal static readonly string[] MainSim =
        {
            "beachMultiplier", "riverMultiplier", "adjustSettlementRadius", "adjustCoastWeight", "adjustRiverWeight", "modifiableSettings",
            "fullVehiclePathing", "smoothVehiclePaths", "ignoreBiomeCostOnRoads", "multiplePawnsPerJob", "passiveWaterWaves",
            "aerialVehicleEffects", "overheatMechanics", "fishingMultiplier", "fishingSkillIncrease", "deployOnLanding",
            "delayDeployOnLanding", "airDefenses", "meleeDamageMultiplier", "rangedDamageMultiplier", "explosiveDamageMultiplier",
            "reduceExplosionsOnWater", "runOverPawns", "friendlyFire", "friendlyFireChance",
            // Review F1: VehiclePawn.Angle (VehiclePawn.cs:448) reads it, and turret aim (TurretRotation, AngleBetween) uses Angle, so it is a sim read.
            "allowDiagonalRendering"
        };

        internal static readonly string[] DebugSim = { "debugDraftAnyVehicle", "debugShootAnyTurret", "debugSpawnVehicleBuildingGodMode" };

        internal static int SimCount => MainSim.Length + DebugSim.Length;

        // Types
        private static Type modType, settingsType, mainType, vehiclesType, debugType, vehicleDefType, propsType, vehiclePawnType, sectionType;
        private static Type saveableFieldType, savedFieldObjType;

        // Fields of VehiclesModSettings and the sections
        private static FieldInfo staticSettingsField, currentSectionField, selectedDefField, modInstanceSettingsField;
        private static FieldInfo fMain, fVehicles, fUpgrades, fDebug, fColor, fShowAll, fPrompt;
        private static FieldInfo fFieldSettings, fVehicleStats, fDefaultValues, fDefaultGraphics;
        private static FieldInfo[] simMain, simDebug, copyMain, copyDebug;

        // SaveableField / SavedField<object>
        private static FieldInfo sfName, sfClassType;
        private static PropertyInfo savedFirst, savedEnd;
        private static ConstructorInfo saveableFieldCtor, savedFieldCtor;

        // Baked values
        private static FieldInfo propertiesField, riverCostField, customRiverCostsField, empStunsField, componentsField, empSeverityField;
        private static MethodInfo tryGetBool;
        private static PropertyInfo turretsProp;
        private static MethodInfo recacheDeployment, recacheTurretPermissions;
        private static float sqrt2 = 1.41421354f;
        private static float bakedRiverMult = float.NaN;

        // Section helpers for the CurrentSection fix-up
        private static MethodInfo sectionOnOpen, sectionVehicleSelected;

        // Review F2: "Reset all" (VehicleMod.ResetAllSettingsConfirmed) is replaced in multiplayer by the same steps on the local object.
        private static FieldInfo cachedFieldsField, useCustomShadersField;
        private static MethodInfo populateCachedFields, sectionResetSettings;

        // Review F4: the per-pawn stat cache is reset after a synced apply.
        private static FieldInfo statHandlerField;
        private static MethodInfo markAllDirty;

        // Review F5: while the settings UI draws in a session (static switched to local), sim recaches that the UI triggers are skipped; the synced apply recaches.
        private static int ownedDrawDepth;
        private static bool warnedShaders;

        // Review N2: the river multiplier this PC started with (the world reachability grouping was built from it at startup), and one-time warnings.
        private static float startupRiverMult = float.NaN;
        private static bool warnedStartupRiver, warnedZeroMoveSpeed;

        // Review N4: the stat editor is a separate window that can outlive the settings window.
        private static Type statSettingsType;
        private static bool defaultShowAllKnown, defaultShowAll;

        // Windows that always draw on the local object (they edit settings, or write the config through VehicleMod.settings)
        private static Type[] alwaysLocalTypes;
        private static readonly Dictionary<Type, bool> alwaysLocalCache = new Dictionary<Type, bool>();
        private static readonly ConditionalWeakTable<Window, object> ownedWindows = new ConditionalWeakTable<Window, object>();

        private static object cachedLocal, cachedMod;

        // The session object. One per process, rebuilt in place at every activation.
        internal static object SessionObject;
        private static bool onSession;

        // Snapshot of the local values at the first draw of the settings window (row 366: push only when this player changed something)
        private static Values openSnapshot;
        private static int lastDrawFrame;

        public VehicleFrameworkSettings(ModContentPack mod)
        {
            try
            {
                if (!Resolve())
                {
                    Log.Warning("[MPCompat] Vehicle Framework settings: couldn't resolve every Vehicle Framework member (see the line above), skipping the settings sync");
                    return;
                }

                // Every patch target is looked up first. Nothing is registered or patched unless all of them exist.
                var doSettings = AccessTools.DeclaredMethod(modType, "DoSettingsWindowContents");
                var writeSettings = AccessTools.DeclaredMethod(modType, "WriteSettings");
                var innerGui = AccessTools.DeclaredMethod(typeof(Window), "InnerWindowOnGUI", new[] { typeof(int) });
                if (doSettings == null || writeSettings == null || innerGui == null)
                {
                    Log.Warning("[MPCompat] Vehicle Framework settings: couldn't find VehicleMod.DoSettingsWindowContents, VehicleMod.WriteSettings or Verse.Window.InnerWindowOnGUI(int), skipping the settings sync");
                    return;
                }

                // Review F2 / F5 targets: the "Reset all" action, the reflection-invoked turret recache, the virtual per-turret recache.
                var resetAll = AccessTools.DeclaredMethod(modType, "ResetAllSettingsConfirmed");
                var recacheAll = AccessTools.DeclaredMethod("Vehicles.CompProperties_VehicleTurrets:RecacheAllTurrets");
                var recacheManned = AccessTools.DeclaredMethod("Vehicles.VehicleTurret:RecacheMannedStatus");
                if (resetAll == null || recacheAll == null || recacheManned == null)
                {
                    Log.Warning("[MPCompat] Vehicle Framework settings: couldn't find VehicleMod.ResetAllSettingsConfirmed, CompProperties_VehicleTurrets.RecacheAllTurrets or VehicleTurret.RecacheMannedStatus, skipping the settings sync");
                    return;
                }

                var local = LocalSettings();
                if (local == null)
                {
                    Log.Warning("[MPCompat] Vehicle Framework settings: the Vehicle Framework mod instance has no settings object yet, skipping the settings sync");
                    return;
                }

                bakedRiverMult = RiverMultiplierOf(local);
                startupRiverMult = bakedRiverMult;
                MP.RegisterSyncMethod(typeof(VehicleFrameworkSettings), nameof(SyncedSetSettings)).CancelIfAnyArgNull();

                // IL sizes (live 1.7 build of Vehicle Framework / RimWorld): VehicleMod.DoSettingsWindowContents 201, VehicleMod.WriteSettings 70 (an
                // override that every caller reaches through a virtual call), Window.InnerWindowOnGUI 853 (reached through a delegate), StopMultiplayer 215.
                // All are far from the always-inline size. No tiny method is patched.
                var harmony = MpCompat.harmony;
                harmony.Patch(doSettings,
                    prefix: new HarmonyMethod(typeof(VehicleFrameworkSettings), nameof(DoSettingsPrefix)),
                    finalizer: new HarmonyMethod(typeof(VehicleFrameworkSettings), nameof(DoSettingsFinalizer)));
                harmony.Patch(writeSettings,
                    postfix: new HarmonyMethod(typeof(VehicleFrameworkSettings), nameof(WriteSettingsPostfix)));
                harmony.Patch(innerGui,
                    prefix: new HarmonyMethod(typeof(VehicleFrameworkSettings), nameof(WindowPrefix)),
                    finalizer: new HarmonyMethod(typeof(VehicleFrameworkSettings), nameof(WindowFinalizer)));

                // IL sizes: ResetAllSettingsConfirmed 152 (try/finally, reached through a delegate), RecacheAllTurrets 135 (reached by MethodInfo.Invoke),
                // VehicleTurret.RecacheMannedStatus 159 (virtual). None of them can be inlined past the patch.
                harmony.Patch(resetAll, prefix: new HarmonyMethod(typeof(VehicleFrameworkSettings), nameof(ResetAllPrefix)));
                harmony.Patch(recacheAll, prefix: new HarmonyMethod(typeof(VehicleFrameworkSettings), nameof(RecacheAllTurretsPrefix)));
                harmony.Patch(recacheManned, prefix: new HarmonyMethod(typeof(VehicleFrameworkSettings), nameof(RecacheMannedStatusPrefix)));

                // Leaving a session: the static goes back to the local object. If this lookup fails, the next single-player load resets it.
                var stopMultiplayer = AccessTools.DeclaredMethod("Multiplayer.Client.Multiplayer:StopMultiplayer");
                if (stopMultiplayer != null)
                    harmony.Patch(stopMultiplayer, finalizer: new HarmonyMethod(typeof(VehicleFrameworkSettings), nameof(StopMultiplayerFinalizer)));
                else
                    Log.Warning("[MPCompat] Vehicle Framework settings: couldn't find Multiplayer.Client.Multiplayer:StopMultiplayer, the settings pointer is reset on the next single-player load instead");

                Active = true;
            }
            catch (Exception e)
            {
                Active = false;
                Log.Warning($"[MPCompat] Vehicle Framework settings: setup failed, the settings sync stays off: {e}");
            }
        }

        // All or nothing: every type, field, property, constructor and method the sync uses is looked up here, and one miss means no patch at all.
        private static bool Resolve()
        {
            var missing = new List<string>();

            Type T(string name)
            {
                var t = AccessTools.TypeByName(name);
                if (t == null) missing.Add("type " + name);
                return t;
            }

            FieldInfo F(Type t, string name)
            {
                if (t == null) return null;
                var f = AccessTools.Field(t, name);
                if (f == null) missing.Add(t.Name + "." + name);
                return f;
            }

            modType = T("Vehicles.VehicleMod");
            settingsType = T("Vehicles.VehiclesModSettings");
            mainType = T("Vehicles.SectionMain");
            vehiclesType = T("Vehicles.SectionVehicles");
            debugType = T("Vehicles.SectionDebug");
            sectionType = T("Vehicles.SettingsSection");
            vehicleDefType = T("Vehicles.VehicleDef");
            propsType = T("Vehicles.VehicleProperties");
            vehiclePawnType = T("Vehicles.VehiclePawn");
            saveableFieldType = T("Vehicles.SaveableField");
            var savedFieldGeneric = T("SmashTools.SavedField`1");
            var upgradesType = T("Vehicles.SectionUpgrades");
            var componentPropsType = T("Vehicles.VehicleComponentProperties");
            var settingsCacheType = T("Vehicles.SettingsCache");
            var turretCompType = T("Vehicles.CompVehicleTurrets");
            var windowTypes = new[] { T("Vehicles.Dialog_StatSettings"), T("Vehicles.Dialog_NodeSettings"), T("Vehicles.Dialog_VehiclePainter") };
            if (missing.Count > 0)
            {
                Log.Warning("[MPCompat] Vehicle Framework settings: couldn't find " + string.Join(", ", missing.ToArray()));
                return false;
            }

            savedFieldObjType = savedFieldGeneric.MakeGenericType(typeof(object));
            alwaysLocalTypes = windowTypes;
            statSettingsType = windowTypes[0];

            staticSettingsField = F(modType, "settings");
            currentSectionField = F(modType, "currentSection");
            selectedDefField = F(modType, "selectedDef");
            modInstanceSettingsField = AccessTools.Field(typeof(Verse.Mod), "modSettings");
            if (modInstanceSettingsField == null) missing.Add("Verse.Mod.modSettings");
            fMain = F(settingsType, "main");
            fVehicles = F(settingsType, "vehicles");
            fUpgrades = F(settingsType, "upgrades");
            fDebug = F(settingsType, "debug");
            fColor = F(settingsType, "colorStorage");
            fShowAll = F(settingsType, "showAllCargoItems");
            fPrompt = F(settingsType, "promptVehicleCollision");
            fFieldSettings = F(vehiclesType, "fieldSettings");
            fVehicleStats = F(vehiclesType, "vehicleStats");
            fDefaultValues = F(vehiclesType, "defaultValues");
            fDefaultGraphics = F(vehiclesType, "defaultGraphics");

            simMain = new FieldInfo[MainSim.Length];
            for (var i = 0; i < MainSim.Length; i++)
                simMain[i] = F(mainType, MainSim[i]);
            simDebug = new FieldInfo[DebugSim.Length];
            for (var i = 0; i < DebugSim.Length; i++)
                simDebug[i] = F(debugType, DebugSim[i]);
            foreach (var f in simMain.Concat(simDebug))
            {
                if (f != null && f.FieldType != typeof(bool) && f.FieldType != typeof(int) && f.FieldType != typeof(float) && !f.FieldType.IsEnum)
                    missing.Add(f.DeclaringType.Name + "." + f.Name + " has the unsupported type " + f.FieldType.Name);
            }

            sfName = F(saveableFieldType, "name");
            sfClassType = F(saveableFieldType, "classType");
            savedFirst = savedFieldObjType.GetProperty("First", BindingFlags.Instance | BindingFlags.Public);
            savedEnd = savedFieldObjType.GetProperty("EndValue", BindingFlags.Instance | BindingFlags.Public);
            savedFieldCtor = savedFieldObjType.GetConstructor(new[] { typeof(object), typeof(object) });
            saveableFieldCtor = saveableFieldType.GetConstructor(new[] { typeof(Def), typeof(FieldInfo) });
            if (savedFirst == null) missing.Add("SavedField.First");
            if (savedEnd == null) missing.Add("SavedField.EndValue");
            if (savedFieldCtor == null) missing.Add("SavedField(object, object)");
            if (saveableFieldCtor == null) missing.Add("SaveableField(Def, FieldInfo)");

            propertiesField = F(vehicleDefType, "properties");
            componentsField = F(vehicleDefType, "components");
            riverCostField = F(propsType, "riverCost");
            customRiverCostsField = F(propsType, "customRiverCosts");
            empStunsField = F(propsType, "empStuns");
            empSeverityField = F(componentPropsType, "empSeverity");
            if (customRiverCostsField != null && !typeof(Dictionary<RiverDef, float>).IsAssignableFrom(customRiverCostsField.FieldType))
                missing.Add("VehicleProperties.customRiverCosts is not a Dictionary<RiverDef, float>");
            if (riverCostField != null && riverCostField.FieldType != typeof(float)) missing.Add("VehicleProperties.riverCost is not a float");
            if (empStunsField != null && empStunsField.FieldType != typeof(bool)) missing.Add("VehicleProperties.empStuns is not a bool");
            if (fFieldSettings != null && !typeof(IDictionary).IsAssignableFrom(fFieldSettings.FieldType)) missing.Add("SectionVehicles.fieldSettings is not a dictionary");
            if (fVehicleStats != null && fVehicleStats.FieldType != typeof(Dictionary<string, Dictionary<string, float>>)) missing.Add("SectionVehicles.vehicleStats has another type");

            // SettingsCache.TryGetValue<T>(VehicleDef, Type, string, T): the one generic overload with 4 parameters.
            foreach (var m in settingsCacheType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name == "TryGetValue" && m.IsGenericMethodDefinition && m.GetParameters().Length == 4)
                    tryGetBool = m.MakeGenericMethod(typeof(bool));
            }
            if (tryGetBool == null) missing.Add("SettingsCache.TryGetValue<T>(VehicleDef, Type, string, T)");

            turretsProp = vehiclePawnType.GetProperty("CompVehicleTurrets", BindingFlags.Instance | BindingFlags.Public);
            recacheDeployment = turretCompType.GetMethod("RecacheDeployment", BindingFlags.Instance | BindingFlags.Public);
            recacheTurretPermissions = turretCompType.GetMethod("RecacheTurretPermissions", BindingFlags.Instance | BindingFlags.Public);
            sectionOnOpen = sectionType.GetMethod("OnOpen", BindingFlags.Instance | BindingFlags.Public);
            sectionVehicleSelected = sectionType.GetMethod("VehicleSelected", BindingFlags.Instance | BindingFlags.Public);
            if (turretsProp == null) missing.Add("VehiclePawn.CompVehicleTurrets");
            if (recacheDeployment == null) missing.Add("CompVehicleTurrets.RecacheDeployment");
            if (recacheTurretPermissions == null) missing.Add("CompVehicleTurrets.RecacheTurretPermissions");
            if (sectionOnOpen == null) missing.Add("SettingsSection.OnOpen");
            if (sectionVehicleSelected == null) missing.Add("SettingsSection.VehicleSelected");

            // Review F2 / F3 / F4 lookups.
            cachedFieldsField = F(modType, "CachedFields");
            populateCachedFields = modType.GetMethod("PopulateCachedFields", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
            sectionResetSettings = sectionType.GetMethod("ResetSettings", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            useCustomShadersField = F(mainType, "useCustomShaders");
            statHandlerField = F(vehiclePawnType, "statHandler");
            markAllDirty = statHandlerField?.FieldType.GetMethod("MarkAllDirty", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            if (cachedFieldsField != null && !typeof(IDictionary).IsAssignableFrom(cachedFieldsField.FieldType)) missing.Add("VehicleMod.CachedFields is not a dictionary");
            if (populateCachedFields == null) missing.Add("VehicleMod.PopulateCachedFields");
            if (sectionResetSettings == null) missing.Add("SettingsSection.ResetSettings");
            if (statHandlerField != null && markAllDirty == null) missing.Add("VehicleStatHandler.MarkAllDirty");
            if (useCustomShadersField != null && useCustomShadersField.FieldType != typeof(bool)) missing.Add("SectionMain.useCustomShaders is not a bool");

            // Ext_Math.Sqrt2 is read when present; otherwise the same float constant (Mathf.Sqrt(2f)) is used.
            var sqrtField = AccessTools.Field("SmashTools.Ext_Math:Sqrt2");
            if (sqrtField != null && sqrtField.FieldType == typeof(float))
                sqrt2 = (float)sqrtField.GetValue(null);

            // Every instance field of the two sections that is not a synced member is copied from the local object into the session copy.
            copyMain = CopyableFields(mainType);
            copyDebug = CopyableFields(debugType);

            if (missing.Count > 0)
            {
                Log.Warning("[MPCompat] Vehicle Framework settings: couldn't find " + string.Join(", ", missing.ToArray()));
                return false;
            }

            return true;
        }

        private static FieldInfo[] CopyableFields(Type type)
        {
            var list = new List<FieldInfo>();
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!field.IsInitOnly)
                    list.Add(field);
            }

            return list.ToArray();
        }

        #region Pointer

        // The Vehicle Framework mod instance's own settings object = this PC's values. VehicleMod.settings is assigned once (VehicleMod constructor,
        // one stsfld in the IL scan), so this object never changes.
        internal static object LocalSettings()
        {
            if (cachedLocal != null)
                return cachedLocal;
            if (modType == null || modInstanceSettingsField == null)
                return null;

            if (cachedMod == null)
                cachedMod = LoadedModManager.GetMod(modType);

            return cachedLocal = cachedMod == null ? null : modInstanceSettingsField.GetValue(cachedMod);
        }

        private static void PutPointer(object target)
        {
            if (target == null)
                return;
            if (!ReferenceEquals(staticSettingsField.GetValue(null), target))
                staticSettingsField.SetValue(null, target);
            onSession = !ReferenceEquals(target, LocalSettings());
        }

        // Points the static at the session object and rebuilds the baked values.
        internal static void UseSession(object session, bool recacheTurrets)
        {
            PutPointer(session);
            RefreshBaked(recacheTurrets);
        }

        // Outside a session (single player, main menu, after leaving multiplayer) the static points at the local object again.
        internal static void UseLocal(bool recacheTurrets)
        {
            if (!Active)
                return;
            var local = LocalSettings();
            if (local == null)
                return;

            var changed = !ReferenceEquals(staticSettingsField.GetValue(null), local);
            PutPointer(local);
            if (changed)
                RefreshBaked(recacheTurrets);
        }

        #endregion

        #region Values of one read

        internal sealed class Values
        {
            public float[] nums;
            public List<string> fields = new List<string>();
            public List<string> stats = new List<string>();
            public int skipped;

            public bool SameAs(float[] otherNums, IList<string> otherFields, IList<string> otherStats)
            {
                if (otherNums == null || otherNums.Length != nums.Length || otherFields.Count != fields.Count || otherStats.Count != stats.Count)
                    return false;
                for (var i = 0; i < nums.Length; i++)
                {
                    if (nums[i] != otherNums[i])
                        return false;
                }

                for (var i = 0; i < fields.Count; i++)
                {
                    if (fields[i] != otherFields[i])
                        return false;
                }

                for (var i = 0; i < stats.Count; i++)
                {
                    if (stats[i] != otherStats[i])
                        return false;
                }

                return true;
            }
        }

        private static float ToFloat(object value)
        {
            if (value is bool b) return b ? 1f : 0f;
            if (value is float f) return f;
            if (value is int i) return i;
            if (value is Enum e) return Convert.ToInt32(e);
            return 0f;
        }

        // The sim values of the local object, plus fieldSettings and vehicleStats as sorted entry strings. Null when something is missing.
        internal static Values ReadLocal()
        {
            try
            {
                var local = LocalSettings();
                if (local == null)
                    return null;
                var main = fMain.GetValue(local);
                var debug = fDebug.GetValue(local);
                var vehicles = fVehicles.GetValue(local);
                if (main == null || debug == null || vehicles == null)
                    return null;

                var values = new Values { nums = new float[SimCount] };
                for (var i = 0; i < simMain.Length; i++)
                    values.nums[i] = ToFloat(simMain[i].GetValue(main));
                for (var i = 0; i < simDebug.Length; i++)
                    values.nums[simMain.Length + i] = ToFloat(simDebug[i].GetValue(debug));

                if (fFieldSettings.GetValue(vehicles) is IDictionary outer)
                {
                    foreach (DictionaryEntry defEntry in outer)
                    {
                        var defName = defEntry.Key as string;
                        if (defName == null || !(defEntry.Value is IDictionary inner))
                            continue;
                        foreach (DictionaryEntry item in inner)
                        {
                            var entry = FormatField(defName, item.Key, item.Value);
                            if (entry == null)
                                values.skipped++;
                            else
                                values.fields.Add(entry);
                        }
                    }
                }

                if (fVehicleStats.GetValue(vehicles) is Dictionary<string, Dictionary<string, float>> stats)
                {
                    foreach (var pair in stats)
                    {
                        if (pair.Key == null || pair.Value == null)
                            continue;
                        foreach (var stat in pair.Value)
                            values.stats.Add(pair.Key + "|" + stat.Key + "|" + stat.Value.ToString("R", CultureInfo.InvariantCulture));
                    }
                }

                values.fields.Sort(StringComparer.Ordinal);
                values.stats.Sort(StringComparer.Ordinal);
                return values;
            }
            catch (Exception e)
            {
                Log.Warning($"[MPCompat] Vehicle Framework settings: reading the local settings failed: {e}");
                return null;
            }
        }

        // defName|declaringTypeFullName|fieldName|code|first|end. Types without a code (strings, defs, lists) return null and are not synced.
        private static string FormatField(string defName, object key, object saved)
        {
            if (key == null || saved == null)
                return null;
            var name = sfName.GetValue(key) as string;
            var type = sfClassType.GetValue(key) as Type;
            if (name == null || type == null || type.FullName == null)
                return null;

            if (!TryFormat(savedFirst.GetValue(saved), out var code, out var first) ||
                !TryFormat(savedEnd.GetValue(saved), out var codeEnd, out var end) || code != codeEnd)
                return null;

            return defName + "|" + type.FullName + "|" + name + "|" + code + "|" + first + "|" + end;
        }

        private static bool TryFormat(object value, out string code, out string text)
        {
            code = null;
            text = null;
            var inv = CultureInfo.InvariantCulture;
            switch (value)
            {
                case bool b: code = "b"; text = b ? "1" : "0"; return true;
                case int i: code = "i"; text = i.ToString(inv); return true;
                case float f: code = "f"; text = f.ToString("R", inv); return true;
                case double d: code = "d"; text = d.ToString("R", inv); return true;
                case long l: code = "l"; text = l.ToString(inv); return true;
                case Enum e when e.GetType().FullName != null:
                    code = "e:" + e.GetType().FullName;
                    text = Convert.ToInt64(e).ToString(inv);
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryParse(string code, string text, out object value)
        {
            value = null;
            var inv = CultureInfo.InvariantCulture;
            switch (code)
            {
                case "b": value = text == "1"; return true;
                case "i": { var ok = int.TryParse(text, NumberStyles.Integer, inv, out var i); value = i; return ok; }
                case "f": { var ok = float.TryParse(text, NumberStyles.Float, inv, out var f); value = f; return ok; }
                case "d": { var ok = double.TryParse(text, NumberStyles.Float, inv, out var d); value = d; return ok; }
                case "l": { var ok = long.TryParse(text, NumberStyles.Integer, inv, out var l); value = l; return ok; }
            }

            if (code != null && code.StartsWith("e:", StringComparison.Ordinal))
            {
                var enumType = AccessTools.TypeByName(code.Substring(2));
                if (enumType != null && enumType.IsEnum && long.TryParse(text, NumberStyles.Integer, inv, out var n))
                {
                    value = Enum.ToObject(enumType, n);
                    return true;
                }
            }

            return false;
        }

        // Rough size of the sync command against Multiplayer's 65535 byte cap: UTF-8 bytes of each entry plus a generous length prefix.
        internal static int EstimateCommandBytes(Values values)
        {
            var bytes = 64 + values.nums.Length * 4;
            foreach (var entry in values.fields)
                bytes += Encoding.UTF8.GetByteCount(entry) + 6;
            foreach (var entry in values.stats)
                bytes += Encoding.UTF8.GetByteCount(entry) + 6;
            return bytes;
        }

        #endregion

        #region Local-only residual

        // Review F3: VehiclePawn.ExposeData drops patternToPaint (and the queued paint job) when useCustomShaders is off on THIS PC. The flag also decides
        // whether the custom shaders are used to draw, so it is not synced (a synced "on" could meet a PC that has no shaders). Both players must keep
        // "Use custom shaders" on; a PC that has it off says so once per run.
        internal static void WarnLocalOnlyValues()
        {
            if (warnedShaders)
                return;

            try
            {
                var local = LocalSettings();
                var main = local == null ? null : fMain.GetValue(local);
                if (main != null && !(bool)useCustomShadersField.GetValue(main))
                {
                    warnedShaders = true;
                    Log.Warning("[MPCompat] Vehicle Framework settings: 'Use custom shaders' is off on this PC. It is not synced, and loading a save drops queued vehicle paint jobs on a PC that has it off, " +
                                "so the PCs can differ. Turn it on again on every PC.");
                }
            }
            catch (Exception e)
            {
                Log.Warning($"[MPCompat] Vehicle Framework settings: checking the custom shaders setting failed: {e.Message}");
            }
        }

        // Review N2: two values are derived from this PC's LOCAL settings once at startup and are NOT rebuilt when a session starts: the world
        // reachability grouping (GridOwners.World.Init, which compares customRiverCosts key sets and MoveSpeed) and VehicleHarmony.AllMoveableVehicleDefs
        // (MoveSpeed != 0). Regrouping mid-game is not safe (the reachability grids are keyed by owner), so this stays a residual covered by Multiplayer's
        // join config check. This only warns, once per run each, when the session values could make the PCs' groupings differ.
        internal static void WarnStartupDerived(float[] nums, IList<string> statEntries)
        {
            try
            {
                var riverIndex = Array.IndexOf(MainSim, "riverMultiplier");
                if (!warnedStartupRiver && riverIndex >= 0 && riverIndex < nums.Length && !float.IsNaN(startupRiverMult))
                {
                    var session = 1f + (nums[riverIndex] == 0f ? 0f : nums[riverIndex]);
                    if (session != startupRiverMult)
                    {
                        warnedStartupRiver = true;
                        Log.Warning($"[MPCompat] Vehicle Framework settings: the session river multiplier ({session.ToString("R", CultureInfo.InvariantCulture)}) differs from the one this PC started with " +
                                    $"({startupRiverMult.ToString("R", CultureInfo.InvariantCulture)}). The world reachability grouping was built at startup and is not rebuilt, so it can differ between PCs. " +
                                    "Keep the river multiplier equal on every PC (Multiplayer's join config check covers it).");
                    }
                }

                if (!warnedZeroMoveSpeed)
                {
                    foreach (var entry in statEntries)
                    {
                        // defName|statDefName|value
                        var parts = entry == null ? null : entry.Split('|');
                        if (parts != null && parts.Length == 3 && parts[1] == "MoveSpeed" &&
                            float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value == 0f)
                        {
                            warnedZeroMoveSpeed = true;
                            Log.Warning($"[MPCompat] Vehicle Framework settings: the session sets MoveSpeed of {parts[0]} to 0. The list of vehicles that create regions was built at startup " +
                                        "from this PC's local settings and is not rebuilt, so it can differ between PCs. Avoid a zero MoveSpeed edit.");
                            break;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warning($"[MPCompat] Vehicle Framework settings: checking the startup-derived values failed: {e.Message}");
            }
        }

        #endregion

        #region Baked values

        // VehicleProperties.ResolveReferences bakes two things from the settings: customRiverCosts (rivers wide enough for the vehicle get riverCost,
        // judged with the river multiplier) and empStuns (SettingsCache lookup). Both are rebuilt whenever the pointer changes, against the object it
        // points at now. Row 366 note: vehicles that are already path-planning keep their cached grids; the rebuild runs at activation (before maps
        // load) and from synced commands, so every PC rebuilds at the same point.
        internal static void RefreshBaked(bool recacheTurrets)
        {
            try { RefreshRivers(); }
            catch (Exception e) { Log.Warning($"[MPCompat] Vehicle Framework settings: rebuilding the river costs failed: {e}"); }

            try { RefreshEmpStuns(); }
            catch (Exception e) { Log.Warning($"[MPCompat] Vehicle Framework settings: rebuilding empStuns failed: {e}"); }

            if (!recacheTurrets)
                return;
            try { RecacheTurrets(); }
            catch (Exception e) { Log.Warning($"[MPCompat] Vehicle Framework settings: re-caching the vehicle turrets failed: {e}"); }
        }

        // ModSettingsHelper.RiverMultiplier: 1 + (riverMultiplier is 0 ? 0 : riverMultiplier), read from the given settings object.
        private static float RiverMultiplierOf(object settings)
        {
            var main = settings == null ? null : fMain.GetValue(settings);
            if (main == null)
                return 1f;
            var raw = (float)simMain[Array.IndexOf(MainSim, "riverMultiplier")].GetValue(main);
            return 1f + (raw == 0f ? 0f : raw);
        }

        private static void RefreshRivers()
        {
            var now = RiverMultiplierOf(staticSettingsField.GetValue(null));
            var before = float.IsNaN(bakedRiverMult) ? RiverMultiplierOf(LocalSettings()) : bakedRiverMult;
            bakedRiverMult = now;
            if (now == before)
                return;

            var rivers = DefDatabase<RiverDef>.AllDefsListForReading;
            int added = 0, removed = 0;
            foreach (var def in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (!vehicleDefType.IsInstanceOfType(def))
                    continue;
                var props = propertiesField.GetValue(def);
                if (props == null)
                    continue;
                var riverCost = (float)riverCostField.GetValue(props);
                if (riverCost <= 0f)
                    continue;
                var costs = customRiverCostsField.GetValue(props) as Dictionary<RiverDef, float>;
                if (costs == null)
                    continue;

                // Same arithmetic as VehicleProperties.ResolveReferences: (float)Size.x * Sqrt2 against widthOnMap * multiplier.
                var needed = (float)def.Size.x * sqrt2;
                foreach (var river in rivers)
                {
                    var was = river.widthOnMap * before >= needed;
                    var will = river.widthOnMap * now >= needed;
                    if (was == will)
                        continue;

                    if (will)
                    {
                        if (!costs.ContainsKey(river))
                        {
                            costs[river] = riverCost;
                            added++;
                        }
                    }
                    else if (costs.TryGetValue(river, out var value) && value == riverCost)
                    {
                        costs.Remove(river);
                        removed++;
                    }
                }
            }

            Log.Message($"[MPCompat] Vehicle Framework settings: river multiplier {before.ToString("R", CultureInfo.InvariantCulture)} -> {now.ToString("R", CultureInfo.InvariantCulture)}, river costs added {added}, removed {removed}");
        }

        private static void RefreshEmpStuns()
        {
            foreach (var def in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (!vehicleDefType.IsInstanceOfType(def))
                    continue;
                var props = propertiesField.GetValue(def);
                if (props == null)
                    continue;

                // Fallback = VehicleProperties.ResolveReferences: any component with an EMP severity above None.
                var fallback = false;
                if (componentsField.GetValue(def) is IList components)
                {
                    foreach (var component in components)
                    {
                        if (component != null && Convert.ToInt32(empSeverityField.GetValue(component)) > 0)
                        {
                            fallback = true;
                            break;
                        }
                    }
                }

                var value = (bool)tryGetBool.Invoke(null, new object[] { def, propsType, "empStuns", fallback });
                if ((bool)empStunsField.GetValue(props) != value)
                    empStunsField.SetValue(props, value);
            }
        }

        // The turret flags come from debugShootAnyTurret (RecacheTurretPermissions) and the settings deployTime (RecacheDeployment). Review F4: the per-pawn
        // stat caches (VehicleStatHandler.statCache) hold values computed from vehicleStats, so they are reset too (MarkAllDirty, the same call VF makes on
        // cargo and damage events). Runs inside the synced apply (or the local switch back when leaving the session), the same on every PC.
        private static void RecacheTurrets()
        {
            if (Current.ProgramState != ProgramState.Playing || Find.Maps == null)
                return;

            foreach (var map in Find.Maps)
            {
                foreach (var pawn in map.mapPawns.AllPawns)
                    RecacheVehicle(pawn);
            }

            // Vehicles in caravans. Vehicles in flight (aerial vehicles) keep their caches until the next event that marks them dirty; same on every PC.
            var caravans = Find.WorldObjects?.Caravans;
            if (caravans != null)
            {
                foreach (var caravan in caravans)
                {
                    foreach (var pawn in caravan.PawnsListForReading)
                        RecacheVehicle(pawn);
                }
            }
        }

        private static void RecacheVehicle(Pawn pawn)
        {
            if (pawn == null || !vehiclePawnType.IsInstanceOfType(pawn))
                return;

            var statHandler = statHandlerField.GetValue(pawn);
            if (statHandler != null)
                markAllDirty.Invoke(statHandler, null);

            if (!pawn.Spawned)
                return;
            var comp = turretsProp.GetValue(pawn);
            if (comp == null)
                return;
            recacheDeployment.Invoke(comp, null);
            recacheTurretPermissions.Invoke(comp, null);
        }

        #endregion

        #region Session object

        private static void SetNum(FieldInfo field, object target, float value)
        {
            var type = field.FieldType;
            if (type == typeof(bool)) field.SetValue(target, value != 0f);
            else if (type == typeof(int)) field.SetValue(target, (int)Math.Round(value));
            else if (type.IsEnum) field.SetValue(target, Enum.ToObject(type, (int)Math.Round(value)));
            else field.SetValue(target, value);
        }

        // Defaults of a component that never captured or received values = the mod's own defaults (a fresh section object).
        internal static float[] DefaultNums()
        {
            var main = Activator.CreateInstance(mainType);
            var debug = Activator.CreateInstance(debugType);
            var nums = new float[SimCount];
            for (var i = 0; i < simMain.Length; i++)
                nums[i] = ToFloat(simMain[i].GetValue(main));
            for (var i = 0; i < simDebug.Length; i++)
                nums[simMain.Length + i] = ToFloat(simDebug[i].GetValue(debug));
            return nums;
        }

        // Review N1: showAllCargoItems is a sim value. The synced LoadVehicleCargoSession constructor calls AddItems, which reads it through
        // Dialog_LoadCargo.AddItemsToTransferables, so every PC must start from the same value. The component saves it (and reads it from the session
        // object when saving in a session), BuildSession puts it on the session object every time, and the existing sync field keeps changing it.
        internal static bool DefaultShowAll()
        {
            if (!defaultShowAllKnown)
            {
                defaultShowAll = Active && (bool)fShowAll.GetValue(Activator.CreateInstance(settingsType));
                defaultShowAllKnown = Active;
            }

            return defaultShowAll;
        }

        internal static bool LocalShowAll()
        {
            var local = LocalSettings();
            return local != null && (bool)fShowAll.GetValue(local);
        }

        // The live value of the session object (null while there is none), which the synced field toggles change.
        internal static bool? SessionShowAll()
        {
            return SessionObject == null ? (bool?)null : (bool)fShowAll.GetValue(SessionObject);
        }

        // Builds (first call) or refreshes (later calls) the session VehiclesModSettings in place from the given values. Returns null on failure.
        // Sections are new objects: every field of the local section is copied first (UI state, render options), then the synced members are set.
        // Shared with the local object, never copied: colorStorage, the upgrades section, vehicles.defaultValues and vehicles.defaultGraphics
        // (render and UI data; the painter writes them through the always-local windows).
        internal static object BuildSession(float[] nums, bool showAllCargo, IList<string> fieldEntries, IList<string> statEntries, out int badEntries)
        {
            badEntries = 0;
            try
            {
                var local = LocalSettings();
                if (local == null || nums == null || nums.Length != SimCount)
                    return null;

                // Built completely before it is installed, so a failure leaves the previous session object untouched.
                var main = Activator.CreateInstance(mainType);
                var localMain = fMain.GetValue(local);
                foreach (var field in copyMain)
                    field.SetValue(main, field.GetValue(localMain));
                for (var i = 0; i < simMain.Length; i++)
                    SetNum(simMain[i], main, nums[i]);

                var debug = Activator.CreateInstance(debugType);
                var localDebug = fDebug.GetValue(local);
                foreach (var field in copyDebug)
                    field.SetValue(debug, field.GetValue(localDebug));
                for (var i = 0; i < simDebug.Length; i++)
                    SetNum(simDebug[i], debug, nums[simMain.Length + i]);

                var vehicles = Activator.CreateInstance(vehiclesType);
                var localVehicles = fVehicles.GetValue(local);
                fDefaultValues.SetValue(vehicles, fDefaultValues.GetValue(localVehicles));
                fDefaultGraphics.SetValue(vehicles, fDefaultGraphics.GetValue(localVehicles));
                fFieldSettings.SetValue(vehicles, BuildFieldSettings(fieldEntries, ref badEntries));
                fVehicleStats.SetValue(vehicles, BuildStats(statEntries, ref badEntries));

                if (SessionObject == null)
                {
                    SessionObject = Activator.CreateInstance(settingsType);
                    fPrompt.SetValue(SessionObject, fPrompt.GetValue(local));
                }

                // Review N1: set on EVERY build (not only the first), from the saved session value, never from this PC's local file.
                // showAllCargoItems is changed from the load cargo dialog and synced as a field of THIS object (see the class notes).
                fShowAll.SetValue(SessionObject, showAllCargo);

                fColor.SetValue(SessionObject, fColor.GetValue(local));
                fUpgrades.SetValue(SessionObject, fUpgrades.GetValue(local));
                fMain.SetValue(SessionObject, main);
                fDebug.SetValue(SessionObject, debug);
                fVehicles.SetValue(SessionObject, vehicles);
                return SessionObject;
            }
            catch (Exception e)
            {
                Log.Warning($"[MPCompat] Vehicle Framework settings: building the session settings failed: {e}");
                return null;
            }
        }

        private static object BuildFieldSettings(IList<string> entries, ref int bad)
        {
            var innerType = typeof(Dictionary<,>).MakeGenericType(saveableFieldType, savedFieldObjType);
            var outerType = typeof(Dictionary<,>).MakeGenericType(typeof(string), innerType);
            var outer = (IDictionary)Activator.CreateInstance(outerType);
            foreach (var entry in entries)
            {
                // defName|declaringTypeFullName|fieldName|code|first|end
                var parts = entry == null ? null : entry.Split('|');
                if (parts == null || parts.Length != 6)
                {
                    bad++;
                    continue;
                }

                var def = GenDefDatabase.GetDef(vehicleDefType, parts[0], false);
                var type = def == null ? null : AccessTools.TypeByName(parts[1]);
                var field = type == null ? null : AccessTools.Field(type, parts[2]);
                if (field == null || !TryParse(parts[3], parts[4], out var first) || !TryParse(parts[3], parts[5], out var end))
                {
                    bad++;
                    continue;
                }

                var key = saveableFieldCtor.Invoke(new object[] { def, field });
                var value = savedFieldCtor.Invoke(new[] { first, end });
                if (!(outer[parts[0]] is IDictionary inner))
                {
                    inner = (IDictionary)Activator.CreateInstance(innerType);
                    outer[parts[0]] = inner;
                }

                inner[key] = value;
            }

            return outer;
        }

        private static Dictionary<string, Dictionary<string, float>> BuildStats(IList<string> entries, ref int bad)
        {
            var result = new Dictionary<string, Dictionary<string, float>>();
            foreach (var entry in entries)
            {
                // defName|statDefName|value
                var parts = entry == null ? null : entry.Split('|');
                if (parts == null || parts.Length != 3 || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    bad++;
                    continue;
                }

                if (!result.TryGetValue(parts[0], out var inner))
                    result[parts[0]] = inner = new Dictionary<string, float>();
                inner[parts[1]] = value;
            }

            return result;
        }

        #endregion

        #region UI patches (keep the settings UI on the local object)

        private sealed class DrawState
        {
            public object previous;
            public Window[] windowsBefore;
            // True when this draw counted itself in ownedDrawDepth (multiplayer only).
            public bool counted;
        }

        private static Window[] SnapshotWindows()
        {
            var windows = Find.WindowStack?.Windows;
            if (windows == null)
                return Array.Empty<Window>();
            var copy = new Window[windows.Count];
            for (var i = 0; i < copy.Length; i++)
                copy[i] = windows[i];
            return copy;
        }

        // A window that appears while the settings UI draws, or while an owned window draws, is owned too: the reset menu (a FloatMenu), its
        // confirmation (a Dialog_MessageBox, opened from inside the FloatMenu), the stat and node dialogs. Their actions then run on the local object.
        private static void MarkNewWindowsOwned(Window[] before)
        {
            var windows = Find.WindowStack?.Windows;
            if (windows == null)
                return;
            for (var i = 0; i < windows.Count; i++)
            {
                var window = windows[i];
                if (window != null && Array.IndexOf(before, window) < 0 && !ownedWindows.TryGetValue(window, out _))
                    ownedWindows.Add(window, null);
            }
        }

        private static bool DrawsOnLocal(Window window)
        {
            var type = window.GetType();
            if (!alwaysLocalCache.TryGetValue(type, out var always))
            {
                always = false;
                foreach (var t in alwaysLocalTypes)
                    always |= t.IsAssignableFrom(type);
                alwaysLocalCache[type] = always;
            }

            return always || ownedWindows.TryGetValue(window, out _);
        }

        // VehicleMod.DoSettingsWindowContents(Rect): the entry point of VF's settings window (Dialog_ModSettings and the XML Extensions menu both call it).
        private static void DoSettingsPrefix(out object __state)
        {
            __state = null;
            if (!Active)
                return;

            try
            {
                TrackWindowOpen();

                var local = LocalSettings();
                var current = staticSettingsField.GetValue(null);
                if (local == null)
                    return;

                // Single player with the static already on the local object: nothing to switch or count.
                var inMp = MP.IsInMultiplayer;
                if (!inMp && ReferenceEquals(current, local))
                    return;

                var state = new DrawState { previous = current, windowsBefore = SnapshotWindows(), counted = inMp };
                if (state.counted)
                    ownedDrawDepth++;
                __state = state;

                if (!ReferenceEquals(current, local))
                {
                    PutPointer(local);
                    FixCurrentSection(local);
                }
            }
            catch (Exception e)
            {
                Log.Warning($"[MPCompat] Vehicle Framework settings: switching to the local settings failed: {e.Message}");
            }
        }

        private static void DoSettingsFinalizer(object __state)
        {
            if (!(__state is DrawState state))
                return;

            try
            {
                MarkNewWindowsOwned(state.windowsBefore);
            }
            catch (Exception e)
            {
                Log.Warning($"[MPCompat] Vehicle Framework settings: marking the new windows failed: {e.Message}");
            }

            if (state.counted && ownedDrawDepth > 0)
                ownedDrawDepth--;

            // In a session the static goes back to the session object, also after an exception in the draw.
            if (MP.IsInMultiplayer)
                PutPointer(state.previous);
        }

        // Window.InnerWindowOnGUI(int) runs once per window per GUI event. Only windows that edit settings (or were opened from them) are switched.
        private static void WindowPrefix(Window __instance, out object __state)
        {
            __state = null;
            if (!Active || !onSession || __instance == null)
                return;

            try
            {
                if (!DrawsOnLocal(__instance))
                    return;
                var local = LocalSettings();
                if (local == null)
                    return;

                var state = new DrawState { previous = staticSettingsField.GetValue(null), windowsBefore = SnapshotWindows(), counted = true };
                ownedDrawDepth++;
                __state = state;
                PutPointer(local);
            }
            catch (Exception e)
            {
                Log.Warning($"[MPCompat] Vehicle Framework settings: switching a window to the local settings failed: {e.Message}");
            }
        }

        private static void WindowFinalizer(object __state)
        {
            if (!(__state is DrawState state))
                return;

            try
            {
                MarkNewWindowsOwned(state.windowsBefore);
            }
            catch (Exception e)
            {
                Log.Warning($"[MPCompat] Vehicle Framework settings: marking the new windows failed: {e.Message}");
            }

            if (state.counted && ownedDrawDepth > 0)
                ownedDrawDepth--;

            if (MP.IsInMultiplayer)
                PutPointer(state.previous);
        }

        // Review F2: "Reset all" confirmed with the Enter key runs from Dialog_MessageBox.OnAcceptKeyPressed, called by WindowStack.HandleEventsHighPriority
        // BEFORE any window draws, so the static still points at the session object and the original would reset the SESSION on this PC only (and then
        // release the vehicle reservations of this PC's maps only). A click on the button runs inside the window draw and is safe. In a session this replaces
        // the whole method with the same steps on the LOCAL object, minus ReleaseAllClaims (reservations are sim state, so no single PC may touch them).
        // The section resets read VehicleMod.settings themselves (ModifiableSettings, PopulateSaveableFields), so the static is switched to local around them.
        // The reset values reach the other PCs through the normal push when the settings window closes (they differ from the snapshot taken at its first draw).
        private static bool ResetAllPrefix()
        {
            if (!Active || !MP.IsInMultiplayer)
                return true;
            var local = LocalSettings();
            if (local == null)
                return true;

            var previous = staticSettingsField.GetValue(null);
            try
            {
                PutPointer(local);
                SoundDefOf.Click.PlayOneShotOnCamera();
                ((IDictionary)cachedFieldsField.GetValue(null)).Clear();
                populateCachedFields.Invoke(null, null);
                foreach (var field in new[] { fMain, fVehicles, fUpgrades, fDebug })
                    sectionResetSettings.Invoke(field.GetValue(local), null);
            }
            catch (Exception e)
            {
                Log.Warning($"[MPCompat] Vehicle Framework settings: resetting the local settings failed: {e}");
            }
            finally
            {
                PutPointer(previous);
            }

            return false;
        }

        // Review F5: CompProperties_VehicleTurrets.RecacheAllTurrets is the ActionOnSettingsInput target of deployTime. It is invoked by reflection from the
        // settings listing (the UI), and it recaches the live vehicles of this PC only. In a session the synced apply does the same recache on every PC.
        private static bool RecacheAllTurretsPrefix()
        {
            return !(Active && MP.IsInMultiplayer);
        }

        // Review F5: SectionDebug toggles debugShootAnyTurret inside the draw and recaches the turret permissions of this PC's vehicles at once. While the
        // settings UI draws in a session this is skipped (the synced apply recaches every PC: RecacheTurrets). Ticks never run inside a draw.
        private static bool RecacheMannedStatusPrefix()
        {
            return !(Active && MP.IsInMultiplayer && ownedDrawDepth > 0);
        }

        // VehicleMod.CurrentSection is a static that VehiclesModSettings.OpenWithContext sets (from the vehicle inspector) while the static still points
        // at the session object, so it can hold a SESSION section. Map it back to the local section before the tabs and OnGUI use it.
        private static void FixCurrentSection(object local)
        {
            if (SessionObject == null)
                return;
            var current = currentSectionField.GetValue(null);
            if (current == null)
                return;

            foreach (var field in new[] { fMain, fVehicles, fDebug })
            {
                if (!ReferenceEquals(current, field.GetValue(SessionObject)))
                    continue;

                var target = field.GetValue(local);
                currentSectionField.SetValue(null, target);
                sectionOnOpen.Invoke(target, null);
                if (field == fVehicles && selectedDefField.GetValue(null) != null)
                    sectionVehicleSelected.Invoke(target, null);
                return;
            }
        }

        // Snapshot of the local values at the first draw of the settings window; WriteSettings compares against it.
        private static void TrackWindowOpen()
        {
            if (!MP.IsInMultiplayer)
                return;

            var frame = UnityEngine.Time.frameCount;
            if (openSnapshot == null || frame - lastDrawFrame > 30)
                openSnapshot = ReadLocal();
            lastDrawFrame = frame;
        }

        private static void StopMultiplayerFinalizer()
        {
            // Never throws into Multiplayer's disconnect path.
            try
            {
                openSnapshot = null;
                if (!MP.IsInMultiplayer)
                    UseLocal(true);
            }
            catch (Exception e)
            {
                Log.Warning($"[MPCompat] Vehicle Framework settings: couldn't reset the settings pointer after leaving multiplayer: {e.Message}");
            }
        }

        #endregion

        #region Push

        // VehicleMod.WriteSettings runs when VF's settings window closes (Dialog_ModSettings.PreClose; the XML Extensions menu calls it too) and base.WriteSettings
        // has already saved the LOCAL object to the config file. The postfix is declared on the VehicleMod override: its base.WriteSettings() call is a direct
        // call to a 20-byte method that the JIT may inline, so a patch on Mod.WriteSettings could be skipped for VehicleMod.
        private static void WriteSettingsPostfix()
        {
            var snapshot = openSnapshot;
            openSnapshot = null;
            if (!Active || Current.Game == null)
                return;

            try
            {
                // Review N4: Dialog_StatSettings is a separate window that could stay open after this close. Edits made in it afterwards would reach no
                // WriteSettings and never be pushed, so it closes with the settings window (Vehicle Framework does the same for Dialog_NodeSettings).
                if (MP.IsInMultiplayer)
                    CloseStatSettingsWindows();

                var synced = VehicleFrameworkSyncedSettings.Instance;
                if (synced == null)
                    return;

                // Single player: keep the component current, so a change made after an SP load reaches the MP session this game is later hosted as.
                if (!MP.IsInMultiplayer)
                {
                    synced.CaptureFromLocal();
                    return;
                }

                // Outside the interface Multiplayer does not sync, so the call would run on this PC only.
                if (!MP.InInterface)
                    return;

                // Row 366: a look-only close (nothing changed since the window opened) pushes nothing. A close without a snapshot cannot prove a change.
                var local = ReadLocal();
                if (local == null || snapshot == null || local.SameAs(snapshot.nums, snapshot.fields, snapshot.stats))
                    return;

                // The session already holds exactly these values.
                if (synced.Matches(local.nums, local.fields, local.stats))
                    return;

                if (EstimateCommandBytes(local) > 60000)
                {
                    Log.Warning($"[MPCompat] Vehicle Framework settings: {local.fields.Count} vehicle fields and {local.stats.Count} vehicle stats are too many for one command, settings not shared");
                    Messages.Message("[MPCompat] Vehicle Framework settings are too large to share; the session keeps its current settings.",
                        MessageTypeDefOf.RejectInput, false);
                    return;
                }

                SyncedSetSettings(local.nums, local.fields.ToArray(), local.stats.ToArray());
            }
            catch (Exception e)
            {
                Log.Warning($"[MPCompat] Vehicle Framework settings: pushing the settings failed: {e}");
            }
        }

        private static void CloseStatSettingsWindows()
        {
            var windows = Find.WindowStack?.Windows;
            if (windows == null || statSettingsType == null)
                return;

            foreach (var window in windows.ToArray())
            {
                if (window != null && statSettingsType.IsInstanceOfType(window))
                    window.Close();
            }
        }

        // Runs on every PC from the same arguments.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SyncedSetSettings(float[] nums, string[] fieldEntries, string[] statEntries)
        {
            var synced = VehicleFrameworkSyncedSettings.Instance;
            if (!Active || synced == null || nums == null || fieldEntries == null || statEntries == null)
                return;
            if (nums.Length != SimCount)
            {
                Log.Warning($"[MPCompat] Vehicle Framework settings: got {nums.Length} values, expected {SimCount}; another Multiplayer Compatibility version? Ignored.");
                return;
            }

            var changedValues = synced.CountChangedValues(nums);
            var changed = !synced.Matches(nums, fieldEntries, statEntries);
            synced.Apply(nums, fieldEntries, statEntries);
            // Review N1: showAllCargoItems is not part of the pushed set; a toggle through the synced field lives on the session object, so keep it.
            synced.PullShowAllFromSession();
            synced.ActivateSession(true, true);

            // Shown on every PC, so a client sees a change that another player pushed.
            if (changed)
                Messages.Message($"[MPCompat] Vehicle Framework session settings changed by a player: {changedValues} values differ, {fieldEntries.Length} vehicle fields, {statEntries.Length} vehicle stats",
                    MessageTypeDefOf.NeutralEvent, false);
        }

        #endregion
    }

    /// <summary>
    /// Saves the Vehicle Framework settings values the simulation reads, so every PC in a session uses the same ones (FIXES row 366).
    /// The component exists in every game, with or without Vehicle Framework; without it (<see cref="VehicleFrameworkSettings.Active"/> false) it
    /// does nothing and writes nothing.
    /// </summary>
    public class VehicleFrameworkSyncedSettings : GameComponent
    {
        public static VehicleFrameworkSyncedSettings Instance;

        // The sim values in the order of VehicleFrameworkSettings.MainSim then DebugSim. Null = never captured or received = the mod defaults.
        private List<float> nums;
        // Sorted entry strings: defName|declaringType|field|code|first|end and defName|statDef|value.
        private List<string> fieldEntries = new List<string>();
        private List<string> statEntries = new List<string>();
        // Saved, so a join-point reload keeps the saved values on every PC instead of the host re-reading its local settings.
        private bool captured;
        // Review N1: showAllCargoItems, a sim value (it picks the items of the synced load-cargo session). Saved with the mod's own default.
        private bool showAllCargo;
        [NonSerialized]
        private bool activatedByLoad;
        [NonSerialized]
        private bool warnedSkipped;

        public VehicleFrameworkSyncedSettings(Game game) { }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            Instance = this;

            // Vehicle Framework not loaded (or changed shape): nothing to sync, nothing logged.
            if (!VehicleFrameworkSettings.Active)
                return;

            // Single player: always read the local settings. A host starts multiplayer from an SP-loaded game and this capture is what
            // clients then receive in the host's save.
            if (!MP.IsInMultiplayer)
            {
                CaptureFromLocal();
                captured = true;
                VehicleFrameworkSettings.UseLocal(false);
                return;
            }

            // In MP a saved component keeps its saved values on every PC, host included. No recapture.
            if (!captured)
            {
                // Component new to this MP save (e.g. mod added mid-game): the mod defaults on every PC, and a player can push.
                captured = true;
                showAllCargo = VehicleFrameworkSettings.DefaultShowAll();
                Log.Message("[MPCompat] Vehicle Framework settings: no saved synced settings in this multiplayer save, using the mod defaults on every PC. " +
                            "A player can push theirs by changing a value in the Vehicle Framework settings window and closing it.");
                ActivateSession(true, false);
            }
            else
            {
                // Already activated (and logged) while the component loaded; this pass only makes sure the pointer is in place after the maps loaded.
                ActivateSession(!activatedByLoad, false);
            }

            activatedByLoad = false;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            // Review N1: in a session the live value is on the session object (the synced field changes it), so a save reads it from there; otherwise
            // from this PC's local object. Every PC saves the same value because the toggle is synced.
            if (Scribe.mode == LoadSaveMode.Saving && VehicleFrameworkSettings.Active)
            {
                try
                {
                    showAllCargo = (MP.IsInMultiplayer ? VehicleFrameworkSettings.SessionShowAll() : null) ?? VehicleFrameworkSettings.LocalShowAll();
                }
                catch (Exception e)
                {
                    Log.Warning($"[MPCompat] Vehicle Framework settings: reading showAllCargoItems for the save failed: {e.Message}");
                }
            }

            Scribe_Values.Look(ref showAllCargo, "showAllCargo", VehicleFrameworkSettings.DefaultShowAll());
            Scribe_Values.Look(ref captured, "captured", false);
            // A save that never captured anything gets no list nodes (a null list would still write one).
            if (Scribe.mode != LoadSaveMode.Saving || captured)
            {
                Scribe_Collections.Look(ref nums, "nums", LookMode.Value);
                Scribe_Collections.Look(ref fieldEntries, "fieldEntries", LookMode.Value);
                Scribe_Collections.Look(ref statEntries, "statEntries", LookMode.Value);
            }

            if (fieldEntries == null) fieldEntries = new List<string>();
            if (statEntries == null) statEntries = new List<string>();

            if (Scribe.mode != LoadSaveMode.LoadingVars)
                return;

            // A component loaded from a save always holds saved values (Scribe_Values skips default values, so key presence cannot tell).
            captured = true;
            if (nums != null && nums.Count != VehicleFrameworkSettings.SimCount)
            {
                if (VehicleFrameworkSettings.Active)
                    Log.Warning($"[MPCompat] Vehicle Framework settings: the save holds {nums.Count} synced values, this build expects {VehicleFrameworkSettings.SimCount}; using the mod defaults.");
                nums = null;
            }

            // Row 366: the session is active as soon as the values are read, before the maps and world load, so everything they load reads the session
            // values (the component loads before them in Game.ExposeData).
            if (VehicleFrameworkSettings.Active && MP.IsInMultiplayer)
            {
                try
                {
                    ActivateSession(true, false);
                    activatedByLoad = true;
                }
                catch (Exception e)
                {
                    Log.Warning($"[MPCompat] Vehicle Framework settings: activating the session settings while loading failed: {e}");
                }
            }
        }

        private float[] NumsOrDefault()
        {
            if (nums != null && nums.Count == VehicleFrameworkSettings.SimCount)
                return nums.ToArray();
            return VehicleFrameworkSettings.DefaultNums();
        }

        internal void CaptureFromLocal()
        {
            var values = VehicleFrameworkSettings.ReadLocal();
            if (values == null)
                return;

            nums = new List<float>(values.nums);
            fieldEntries = values.fields;
            statEntries = values.stats;
            showAllCargo = VehicleFrameworkSettings.LocalShowAll();
            captured = true;
            if (values.skipped > 0 && !warnedSkipped)
            {
                warnedSkipped = true;
                Log.Warning($"[MPCompat] Vehicle Framework settings: {values.skipped} per-vehicle field values have a type that is not synced (not a number, bool or enum) and stay local.");
            }
        }

        // True when the given values equal the session values.
        internal bool Matches(float[] otherNums, IList<string> otherFields, IList<string> otherStats)
        {
            var own = NumsOrDefault();
            if (otherNums == null || otherNums.Length != own.Length)
                return false;
            for (var i = 0; i < own.Length; i++)
            {
                if (own[i] != otherNums[i])
                    return false;
            }

            return new HashSet<string>(fieldEntries).SetEquals(otherFields) && new HashSet<string>(statEntries).SetEquals(otherStats);
        }

        internal int CountChangedValues(float[] otherNums)
        {
            var own = NumsOrDefault();
            var count = 0;
            for (var i = 0; i < own.Length && i < otherNums.Length; i++)
            {
                if (own[i] != otherNums[i])
                    count++;
            }

            return count;
        }

        // After a synced apply: the session object holds the live showAllCargoItems (toggled through the synced field), so the next build keeps it.
        internal void PullShowAllFromSession()
        {
            var live = VehicleFrameworkSettings.SessionShowAll();
            if (live.HasValue)
                showAllCargo = live.Value;
        }

        // Writes the pushed values on every PC (called from the synced method).
        internal void Apply(float[] otherNums, string[] otherFields, string[] otherStats)
        {
            nums = new List<float>(otherNums);
            fieldEntries = new List<string>();
            foreach (var entry in otherFields)
            {
                if (entry != null)
                    fieldEntries.Add(entry);
            }

            statEntries = new List<string>();
            foreach (var entry in otherStats)
            {
                if (entry != null)
                    statEntries.Add(entry);
            }

            fieldEntries.Sort(StringComparer.Ordinal);
            statEntries.Sort(StringComparer.Ordinal);
            captured = true;
        }

        // Builds the session settings from the saved values, points Vehicle Framework's static at them, rebuilds the baked values, and logs the
        // compare line (the same text on every PC when the settings agree): once per multiplayer load and once per apply.
        internal void ActivateSession(bool log, bool recacheTurrets)
        {
            if (!VehicleFrameworkSettings.Active)
                return;

            var values = NumsOrDefault();
            var session = VehicleFrameworkSettings.BuildSession(values, showAllCargo, fieldEntries, statEntries, out var bad);
            if (session == null)
            {
                Log.Warning("[MPCompat] Vehicle Framework settings: couldn't build the session settings, the Vehicle Framework settings stay local on this PC");
                return;
            }

            if (bad > 0)
                Log.Warning($"[MPCompat] Vehicle Framework settings: {bad} synced entries name a def, type or field that this PC does not have and were skipped");

            VehicleFrameworkSettings.UseSession(session, recacheTurrets);

            if (log)
            {
                Log.Message(CompactLine(values));
                VehicleFrameworkSettings.WarnLocalOnlyValues();
                VehicleFrameworkSettings.WarnStartupDerived(values, statEntries);
            }
        }

        private string CompactLine(float[] values)
        {
            var parts = new List<string>();
            for (var i = 0; i < VehicleFrameworkSettings.MainSim.Length; i++)
                parts.Add(VehicleFrameworkSettings.MainSim[i] + "=" + values[i].ToString("R", CultureInfo.InvariantCulture));
            for (var i = 0; i < VehicleFrameworkSettings.DebugSim.Length; i++)
                parts.Add(VehicleFrameworkSettings.DebugSim[i] + "=" + values[VehicleFrameworkSettings.MainSim.Length + i].ToString("R", CultureInfo.InvariantCulture));

            return "[MPCompat] VF session settings: " + string.Join(" ", parts.ToArray()) + " showAllCargoItems=" + (showAllCargo ? "1" : "0") +
                   ", fieldSettings=" + fieldEntries.Count + ", vehicleStats=" + statEntries.Count;
        }
    }
}
