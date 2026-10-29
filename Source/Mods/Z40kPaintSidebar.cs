using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using UnityEngine;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>[Z]40k Props and Decor (Zombie.40kPropsandDecor): the paint sidebar</summary>
    /// <remarks>
    /// FIXES row 309. The paint sidebar (Dialog_Z40K_PaintSidebar) is opened from the "Paint" gizmo of a prop (a click, so it exists on
    /// the clicker's PC only; that stays). Its controls write saved game state straight from window code, on that one PC only:
    /// <list type="bullet">
    /// <item>a palette swatch (ApplyColor) and the Apply button in an edit session (StartPlacement) call CompDecalColor.SetColor, which
    /// sets the saved comp field and redraws the prop;</item>
    /// <item>the Delete button calls Thing.Destroy on the prop in DrawSharedButtons;</item>
    /// <item>the Copy / Apply buttons start placement mode, and the next map click makes Z40K_PlacementController.Place destroy the prop
    /// being edited (if it stands on the clicked cell), spawn a new prop through Z40K_SpawnHelper.SpawnOwned and set its colour, all
    /// from GameComponentUpdate (input, not a tick). Place is shared with the other Z40K windows' placement mode, so they gain the
    /// sync too.</item>
    /// </list>
    /// This module (the mod is reached by name only, no reference to its dll):
    /// 1. SetColor: a prefix lets the call go through ONE synced method SyncSetColor(Thing, Color) while a paint-window button handler
    ///    (ApplyColor or StartPlacement, tracked by a prefix / finalizer pair) is on the stack at interface time. Every other caller of
    ///    SetColor (the Utility sidebar, gizmo Resize, Place, the synced method itself) runs as before.
    /// 2. Delete: a transpiler on DrawSharedButtons points its one Thing.Destroy call at DestroyOrSync, which sends SyncDestroy(Thing).
    /// 3. Placement: a prefix on Place sends SyncPlace(Map, ThingDef, cell, rotation, colour, faction, replaced Thing). The map, faction
    ///    and the thing to replace are decided at click time on the clicker's PC and travel as arguments (Faction.OfPlayer and
    ///    Find.CurrentMap are not read inside the synced method).
    /// Each synced method checks on every PC that its Thing is not null, not destroyed and spawned (and, for placement, still on the
    /// map and cell) before it touches anything. There is no preview or Cancel in this window: a swatch click is final, so nothing
    /// previewed is left unreverted.
    /// Left local on purpose: Z40K_EditSession / Z40K_ActivePlacement / Z40K_PlacementState (static per-PC window state, not saved),
    /// the palette category and scroll, the window transition queue, the map-mesh dirtying that follows a swatch click.
    /// Not covered (not this window): the prop gizmos Delete / Rotate / Resize in CompPropActions and the Utility sidebar
    /// (Destroy + SpawnOwned in its StartPlacement and its own Delete button).
    /// Supported build: Z40K 2.0 (Assemblies\Z40KPropsandDecor.dll in the mod root, 223,744 bytes; the 1.6 folder holds Defs only).
    /// Every member is looked up before anything is patched; on a mismatch nothing is patched and the window behaves as without the
    /// module. <c>active</c> is true only when every lookup, registration and patch worked.
    /// Status: [unverified] compiled only, not run in game.
    /// </remarks>
    [MpCompatFor("Zombie.40kPropsandDecor")]
    internal class Z40kPaintSidebar
    {
        private const string LogPrefix = "[MPCompat - Z40K Paint Sidebar] ";

        // mod types and members, found by name
        private static Type compDecalColorType, compPropActionsType;
        private static MethodInfo setColorMethod, spawnOwnedMethod, editSessionEndMethod;
        private static FieldInfo editSessionThingField, placementDefField, placementRotationField, placementColorField;

        // True only when everything was found, registered and patched. While false every hook below passes straight through.
        private static bool active;

        // > 0 while ApplyColor / StartPlacement of the paint window runs (main thread only)
        private static int paintUiDepth;

        private static int failures, syncMethods, patches;

        public Z40kPaintSidebar(ModContentPack mod)
        {
            MethodBase drawSharedButtons = null, applyColor = null, startPlacement = null, setColor = null, place = null;
            Try("look up the Z40K members", () =>
            {
                var dialog = TypeNamed("Z40K.Dialog_Z40K_PaintSidebar");
                compDecalColorType = TypeNamed("Z40K.CompDecalColor");
                compPropActionsType = TypeNamed("Z40K.CompPropActions");
                var controller = TypeNamed("Z40K.Z40K_PlacementController");
                var spawnHelper = TypeNamed("Z40K.Z40K_SpawnHelper");
                var editSession = TypeNamed("Z40K.Z40K_EditSession");
                var activePlacement = TypeNamed("Z40K.Z40K_ActivePlacement");

                if (!typeof(ThingComp).IsAssignableFrom(compDecalColorType) || !typeof(ThingComp).IsAssignableFrom(compPropActionsType))
                    throw new InvalidOperationException("CompDecalColor / CompPropActions are not ThingComps");

                drawSharedButtons = Required(AccessTools.DeclaredMethod(dialog, "DrawSharedButtons", Type.EmptyTypes), "Dialog_Z40K_PaintSidebar.DrawSharedButtons()");
                applyColor = Required(AccessTools.DeclaredMethod(dialog, "ApplyColor", new[] { typeof(Color) }), "Dialog_Z40K_PaintSidebar.ApplyColor(Color)");
                startPlacement = Required(AccessTools.DeclaredMethod(dialog, "StartPlacement", Type.EmptyTypes), "Dialog_Z40K_PaintSidebar.StartPlacement()");

                setColorMethod = Required(AccessTools.DeclaredMethod(compDecalColorType, "SetColor", new[] { typeof(Color) }), "CompDecalColor.SetColor(Color)");
                if (setColorMethod.IsStatic || setColorMethod.GetParameters()[0].Name != "newColor")
                    throw new MissingMethodException("CompDecalColor.SetColor must be an instance method with a parameter named newColor");
                setColor = setColorMethod;

                place = Required(AccessTools.DeclaredMethod(controller, "Place", new[] { typeof(IntVec3) }), "Z40K_PlacementController.Place(IntVec3)");
                if (place.IsStatic || place.GetParameters()[0].Name != "cell")
                    throw new MissingMethodException("Z40K_PlacementController.Place must be an instance method with a parameter named cell");

                spawnOwnedMethod = Required(AccessTools.DeclaredMethod(spawnHelper, "SpawnOwned",
                    new[] { typeof(ThingDef), typeof(IntVec3), typeof(Map), typeof(Rot4), typeof(Faction) }), "Z40K_SpawnHelper.SpawnOwned(ThingDef, IntVec3, Map, Rot4, Faction)");
                if (!spawnOwnedMethod.IsStatic || spawnOwnedMethod.ReturnType != typeof(Thing))
                    throw new MissingMethodException("Z40K_SpawnHelper.SpawnOwned must be static and return Thing");

                editSessionEndMethod = Required(AccessTools.DeclaredMethod(editSession, "End", Type.EmptyTypes), "Z40K_EditSession.End()");
                editSessionThingField = StaticField(editSession, "Thing", typeof(Thing));
                placementDefField = StaticField(activePlacement, "Def", typeof(ThingDef));
                placementRotationField = StaticField(activePlacement, "Rotation", typeof(Rot4));
                placementColorField = StaticField(activePlacement, "Color", typeof(Color));

                // the Delete button is the one Thing.Destroy call in DrawSharedButtons: patch nothing unless that is still true
                var destroy = DestroyMethod();
                var count = PatchProcessor.GetOriginalInstructions(drawSharedButtons).Count(ci => ci.Calls(destroy));
                if (count != 1)
                    throw new InvalidOperationException($"DrawSharedButtons has {count} calls of Thing.Destroy, expected 1");
            });

            if (failures > 0)
            {
                Log.Error($"{LogPrefix}this Z40K build does not have what the module syncs ({failures} problem(s) above): nothing patched, the paint window behaves as without the module.");
                throw new InvalidOperationException($"{LogPrefix}unsupported Z40K build");
            }

            Try("register the synced methods", () =>
            {
                MP.RegisterSyncMethod(typeof(Z40kPaintSidebar), nameof(SyncSetColor)).CancelIfAnyArgNull();
                MP.RegisterSyncMethod(typeof(Z40kPaintSidebar), nameof(SyncDestroy)).CancelIfAnyArgNull();
                MP.RegisterSyncMethod(typeof(Z40kPaintSidebar), nameof(SyncPlace));
                syncMethods += 3;
            });

            if (failures == 0)
            {
                Try("patch ApplyColor", () => Patch(applyColor, prefix: nameof(PreWindowButton), finalizer: nameof(FinalizerWindowButton)));
                Try("patch StartPlacement", () => Patch(startPlacement, prefix: nameof(PreWindowButton), finalizer: nameof(FinalizerWindowButton)));
                Try("patch CompDecalColor.SetColor", () => Patch(setColor, prefix: nameof(PreSetColor)));
                Try("patch Z40K_PlacementController.Place", () => Patch(place, prefix: nameof(PrePlace)));
                Try("patch DrawSharedButtons (Delete)", () =>
                {
                    MpCompat.harmony.Patch(drawSharedButtons, transpiler: new HarmonyMethod(typeof(Z40kPaintSidebar), nameof(TranspileDelete)));
                    patches++;
                });
            }

            if (failures == 0)
                active = true;
            else
                Log.Error($"{LogPrefix}module NOT active because a step failed: the paint window's edits stay local, as without the module.");

            Log.Message($"{LogPrefix}{syncMethods} sync method(s) registered, {patches} patch(es) installed, {failures} failure(s), active={active}.");
            if (failures > 0)
                throw new InvalidOperationException($"{LogPrefix}{failures} registration step(s) failed, see the errors above.");
        }

        #region Lookup helpers

        private static void Try(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                failures++;
                Log.Error($"{LogPrefix}could not {what}: {e.GetType().Name}: {e.Message}");
            }
        }

        private static Type TypeNamed(string name)
            => AccessTools.TypeByName(name) ?? throw new TypeLoadException($"type {name} not found");

        private static MethodInfo Required(MethodInfo method, string what)
            => method ?? throw new MissingMethodException($"{what} not found");

        private static FieldInfo StaticField(Type type, string name, Type fieldType)
        {
            var field = AccessTools.DeclaredField(type, name) ?? throw new MissingFieldException(type.FullName, name);
            if (!field.IsStatic || field.FieldType != fieldType)
                throw new MissingFieldException($"{type.FullName}.{name} is not a static {fieldType.Name}");
            return field;
        }

        private static MethodInfo DestroyMethod()
            => AccessTools.DeclaredMethod(typeof(Thing), nameof(Thing.Destroy), new[] { typeof(DestroyMode) })
               ?? throw new MissingMethodException("Thing.Destroy(DestroyMode) not found");

        private static void Patch(MethodBase target, string prefix = null, string finalizer = null)
        {
            MpCompat.harmony.Patch(target,
                prefix: prefix == null ? null : new HarmonyMethod(typeof(Z40kPaintSidebar), prefix),
                finalizer: finalizer == null ? null : new HarmonyMethod(typeof(Z40kPaintSidebar), finalizer));
            patches++;
        }

        private static ThingComp FindComp(Thing thing, Type compType)
            => thing is ThingWithComps withComps ? withComps.AllComps.FirstOrDefault(c => compType.IsInstanceOfType(c)) : null;

        private static void LogCommand(string what, Thing thing, string detail)
        {
            if (Prefs.DevMode)
                Log.Message($"{LogPrefix}{what} thing={thing?.ThingID} {detail} tick={Find.TickManager?.TicksGame} bySelf={MP.IsExecutingSyncCommandIssuedBySelf}");
        }

        #endregion

        #region Colour: swatch click and Apply button

        // ApplyColor / StartPlacement are the paint window's button handlers. SetColor is only routed to the sync while one is running.
        private static void PreWindowButton() => paintUiDepth++;

        private static void FinalizerWindowButton()
        {
            if (paintUiDepth > 0)
                paintUiDepth--;
        }

        // Interface time, from the paint window: send the colour change instead of writing the comp on this PC only.
        private static bool PreSetColor(ThingComp __instance, Color newColor)
        {
            if (!active || paintUiDepth == 0 || !MP.IsInMultiplayer || !MP.InInterface)
                return true;

            var thing = __instance.parent;
            // A prop that is gone or unspawned on this PC: write nothing (a local write would be an unsynced divergence).
            if (thing != null && !thing.Destroyed && thing.Spawned)
                SyncSetColor(thing, newColor);
            return false;
        }

        // Runs on every PC from the same arguments (or directly when not in a multiplayer game).
        private static void SyncSetColor(Thing thing, Color color)
        {
            if (thing == null || thing.Destroyed || !thing.Spawned)
                return;
            var comp = FindComp(thing, compDecalColorType);
            if (comp == null)
                return;
            LogCommand("SetColor", thing, $"color={color}");
            setColorMethod.Invoke(comp, new object[] { color });
        }

        #endregion

        #region Delete button

        // DrawSharedButtons calls Thing.Destroy on the edited prop: after the transpiler it calls this instead.
        public static void DestroyOrSync(Thing thing, DestroyMode mode)
        {
            if (active && MP.IsInMultiplayer && MP.InInterface && mode == DestroyMode.Vanish)
            {
                // Gone or unspawned already on this PC: do nothing (a local destroy would be an unsynced divergence).
                if (thing != null && thing.Spawned && !thing.Destroyed)
                    SyncDestroy(thing);
                return;
            }

            thing.Destroy(mode);
        }

        private static IEnumerable<CodeInstruction> TranspileDelete(IEnumerable<CodeInstruction> instructions)
        {
            var target = DestroyMethod();
            var replacement = AccessTools.DeclaredMethod(typeof(Z40kPaintSidebar), nameof(DestroyOrSync));
            var replaced = 0;
            foreach (var ci in instructions)
            {
                if (ci.Calls(target))
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = replacement;
                    replaced++;
                }

                yield return ci;
            }

            if (replaced != 1)
                throw new InvalidOperationException($"{LogPrefix}DrawSharedButtons transpiler replaced {replaced} calls, expected 1");
        }

        private static void SyncDestroy(Thing thing)
        {
            if (thing == null || thing.Destroyed || !thing.Spawned)
                return;
            // only the mod's own props (the paint window works on a prop with these comps)
            if (FindComp(thing, compPropActionsType) == null && FindComp(thing, compDecalColorType) == null)
                return;
            LogCommand("Destroy", thing, $"pos={thing.Position}");
            thing.Destroy();
        }

        #endregion

        #region Placement (Copy / Apply buttons, then a map click)

        // GameComponentUpdate, interface time: the click on the map. Everything the original reads from this PC's state is decided
        // here and sent as arguments.
        private static bool PrePlace(IntVec3 cell)
        {
            if (!active || !MP.IsInMultiplayer || !MP.InInterface)
                return true;

            var def = placementDefField.GetValue(null) as ThingDef;
            var map = Find.CurrentMap;
            if (def == null || map == null)
                return true;

            var rotation = (Rot4)placementRotationField.GetValue(null);
            var color = (Color)placementColorField.GetValue(null);

            // the original destroys the prop being edited when it stands on the clicked cell, and ends the edit session
            Thing replaced = null;
            var editing = editSessionThingField.GetValue(null) as Thing;
            var endSession = editing != null && editing.Position == cell;
            if (endSession && editing.Spawned && !editing.Destroyed && editing.Map == map)
                replaced = editing;

            SyncPlace(map, def, cell, rotation, color, Faction.OfPlayer, replaced);

            if (endSession)
                editSessionEndMethod.Invoke(null, null);
            return false;
        }

        // 7 parameters. Runs on every PC from the same arguments (or directly when not in a multiplayer game).
        private static void SyncPlace(Map map, ThingDef def, IntVec3 cell, Rot4 rotation, Color color, Faction faction, Thing replaced)
        {
            if (map == null || def == null || !cell.InBounds(map))
                return;

            if (replaced != null && !replaced.Destroyed && replaced.Spawned && replaced.Map == map && replaced.Position == cell
                && (FindComp(replaced, compPropActionsType) != null || FindComp(replaced, compDecalColorType) != null))
            {
                LogCommand("Place: replace", replaced, $"pos={cell}");
                replaced.Destroy();
            }

            var spawned = spawnOwnedMethod.Invoke(null, new object[] { def, cell, map, rotation, faction }) as Thing;
            if (spawned == null)
                return;
            LogCommand("Place: spawn", spawned, $"def={def.defName} pos={cell} rot={rotation} color={color}");
            var comp = FindComp(spawned, compDecalColorType);
            if (comp != null)
                setColorMethod.Invoke(comp, new object[] { color });
        }

        #endregion
    }
}
