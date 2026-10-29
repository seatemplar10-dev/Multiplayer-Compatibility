using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using UnityEngine;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Vehicle Framework by SmashPhil: the vehicle painter window (FIXES row 309)</summary>
    /// <remarks>
    /// Status: [unverified] compiled only, not run in game.
    /// <para>
    /// The window: the inspect pane's recolour button calls <c>VehiclePawn.ChangeColor()</c>, which opens
    /// <c>Dialog_VehiclePainter</c> with a save callback. The window itself writes nothing to the game: its colours, pattern, tiling
    /// and displacement live in its own fields, its preview is a <c>BlitRequest</c> with a standalone <c>PatternData</c>, and its palette
    /// buttons write VF's mod settings (<c>VehicleMod.settings.colorStorage</c>, not saved game state). Cancel and the close button only
    /// close the window. The only write is the save callback that <c>ChangeColor()</c> hands over, run when Apply is clicked:
    /// <c>VehiclePawn.patternToPaint = new PatternData(...)</c> (a saved field, <c>Scribe_Deep</c>; it makes <c>CanPaintNow</c> true,
    /// so <c>WorkGiver_PaintVehicle</c> offers the paint job), and, when <c>DebugSettings.godMode</c> is on, an immediate
    /// <c>VehiclePawn.SetColor()</c> (copies it into <c>patternData</c>, sets <c>DrawColor</c> / <c>DrawColorTwo</c> / <c>DrawColorThree</c>,
    /// <c>Notify_ColorChanged()</c>, clears <c>patternToPaint</c>).
    /// </para>
    /// <para>
    /// Unpatched in MP the callback runs only on the PC that clicked, so <c>patternToPaint</c> exists on that PC alone: the other PCs
    /// never offer the paint job (<c>CanPaintNow</c> false), and the job queue and the saved state diverge. The job itself
    /// (<c>JobDriver_PaintVehicle</c>: work per tick from the pawn's WorkSpeedGlobal and the vehicle def size, then <c>SetColor()</c>)
    /// runs in the simulation on every PC and stays as it is; it is deterministic once <c>patternToPaint</c> is equal everywhere.
    /// </para>
    /// <para>
    /// Fix: a prefix on <c>VehiclePawn.ChangeColor()</c>, only for a UI-time call in a multiplayer game, opens the same
    /// window through <c>Dialog_VehiclePainter.OpenColorPicker(VehiclePawn, SaveColor)</c> with a callback of our own. That callback
    /// calls <see cref="SyncedPaint"/>, a synced method naming the vehicle and carrying the three colours, the pattern def, the
    /// displacement, the tiling and the god-mode flag read at click time (<c>DebugSettings.godMode</c> is per PC, so it is an argument,
    /// never read in synced code). Inside it runs the original callback body on every PC. Single player, and any call that is not a UI
    /// click, run the original <c>ChangeColor()</c>. Nothing here touches <c>SetColor()</c> itself, so the job driver's call is unchanged.
    /// </para>
    /// <para>
    /// Left alone (not part of this window): the vehicle rename window (<c>Dialog_GiveVehicleName</c>), vehicle upgrades
    /// (<c>SetRetexture</c>, called from upgrade jobs) and the settings-screen painter (<c>SectionVehicles.SetDefaultColor</c>, mod
    /// settings only).
    /// </para>
    /// </remarks>
    [MpCompatFor("SmashPhil.VehicleFramework")]
    public class VehicleFrameworkPainter
    {
        private const string LogPrefix = "[MPCompat - Vehicle Framework painter] ";

        private static bool active;

        // Vehicles.VehiclePawn / Vehicles.PatternDef
        private static Type vehiclePawnType;
        private static Type patternDefType;
        // Vehicles.Dialog_VehiclePainter.SaveColor (public nested delegate) and OpenColorPicker(VehiclePawn, SaveColor)
        private static Type saveColorType;
        private static MethodInfo openColorPicker;
        // Vehicles.PatternData(Color, Color, Color, PatternDef, Vector2, float)
        private static ConstructorInfo patternDataCtor;
        // Vehicles.VehiclePawn.patternToPaint (public field, PatternData)
        private static AccessTools.FieldRef<object, object> patternToPaintField;
        // Vehicles.VehiclePawn.SetColor()
        private static MethodInfo setColor;

        public VehicleFrameworkPainter(ModContentPack mod)
        {
            try
            {
                vehiclePawnType = FindType("Vehicles.VehiclePawn");
                patternDefType = FindType("Vehicles.PatternDef");
                var dialogType = FindType("Vehicles.Dialog_VehiclePainter");
                var patternDataType = FindType("Vehicles.PatternData");

                saveColorType = AccessTools.Inner(dialogType, "SaveColor")
                                ?? throw new TypeLoadException($"{LogPrefix}type Vehicles.Dialog_VehiclePainter.SaveColor not found");
                openColorPicker = GetDeclaredMethod(dialogType, "OpenColorPicker", [vehiclePawnType, saveColorType]);
                var changeColor = GetDeclaredMethod(vehiclePawnType, "ChangeColor", Type.EmptyTypes);
                setColor = GetDeclaredMethod(vehiclePawnType, "SetColor", Type.EmptyTypes);
                patternDataCtor = AccessTools.DeclaredConstructor(patternDataType,
                                      [typeof(Color), typeof(Color), typeof(Color), patternDefType, typeof(Vector2), typeof(float)])
                                  ?? throw new MissingMethodException(patternDataType.FullName, ".ctor(Color, Color, Color, PatternDef, Vector2, float)");
                var patternField = AccessTools.DeclaredField(vehiclePawnType, "patternToPaint")
                                   ?? throw new MissingFieldException(vehiclePawnType.FullName, "patternToPaint");
                if (patternField.FieldType != patternDataType)
                    throw new InvalidOperationException($"{LogPrefix}patternToPaint is {patternField.FieldType}, expected {patternDataType}");
                patternToPaintField = AccessTools.FieldRefAccess<object>(vehiclePawnType, "patternToPaint");

                // Every member exists: only now patch / register.
                MP.RegisterSyncMethod(typeof(VehicleFrameworkPainter), nameof(SyncedPaint)); // no CancelIfAnyArgNull: pattern may be null (Reset); the body checks the vehicle
                MpCompat.harmony.Patch(changeColor, prefix: new HarmonyMethod(typeof(VehicleFrameworkPainter), nameof(PreChangeColor)));
                active = true;
                Log.Message($"{LogPrefix}painter installed (Apply in the vehicle painter window now sets patternToPaint on all PCs)");
            }
            catch (Exception e)
            {
                Log.Error($"{LogPrefix}painter sync not installed, the vehicle painter stays local to the clicking PC: {e}");
                throw new InvalidOperationException($"{LogPrefix}painter sync not installed", e);
            }
        }

        // VehiclePawn.ChangeColor(): UI-time click in a multiplayer game -> same window, MP-aware save callback.
        // Any other call (single player, run from a synced command) keeps the original body.
        private static bool PreChangeColor(Pawn __instance)
        {
            if (!active || !MP.IsInMultiplayer || !MP.InInterface)
                return true;

            var callback = new SaveCallback(__instance);
            var save = Delegate.CreateDelegate(saveColorType, callback, SaveCallback.SaveMethod);
            openColorPicker.Invoke(null, [__instance, save]);
            return false;
        }

        // The original callback body, run on every PC from the same arguments.
        private static void SyncedPaint(Pawn vehicle, Color colorOne, Color colorTwo, Color colorThree, Def pattern,
            Vector2 displacement, float tiles, bool applyNow)
        {
            // A null pattern is legal: the painter's Reset button selects the vehicle's own PatternData.patternDef, which can be null.
            if (!active || !vehiclePawnType.IsInstanceOfType(vehicle) || (pattern != null && !patternDefType.IsInstanceOfType(pattern)))
                return;

            patternToPaintField(vehicle) = patternDataCtor.Invoke([colorOne, colorTwo, colorThree, pattern, displacement, tiles]);
            // godMode was read on the clicking PC (DebugSettings.godMode differs per PC), so it comes in as an argument.
            if (applyNow)
                setColor.Invoke(vehicle, null);
        }

        // Bound to Dialog_VehiclePainter.SaveColor(Color, Color, Color, PatternDef, Vector2, float); the PatternDef parameter is
        // taken as Def (delegate parameter contravariance) so this assembly needs no reference to the mod.
        private sealed class SaveCallback(Pawn vehicle)
        {
            public static readonly MethodInfo SaveMethod = AccessTools.Method(typeof(SaveCallback), nameof(Save));

            // Runs inside the Apply click (UI), on the clicking PC only.
            public void Save(Color colorOne, Color colorTwo, Color colorThree, Def pattern, Vector2 displacement, float tiles)
                => SyncedPaint(vehicle, colorOne, colorTwo, colorThree, pattern, displacement, tiles, DebugSettings.godMode);
        }

        private static Type FindType(string name)
            => AccessTools.TypeByName(name) ?? throw new TypeLoadException($"{LogPrefix}type {name} not found");

        private static MethodInfo GetDeclaredMethod(Type type, string name, Type[] args = null)
            => AccessTools.DeclaredMethod(type, name, args) ?? throw new MissingMethodException(type.FullName, name);
    }
}
