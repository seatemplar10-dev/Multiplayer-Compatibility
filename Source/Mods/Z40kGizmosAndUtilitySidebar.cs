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
    /// <summary>[Z]40k Props and Decor (Zombie.40kPropsandDecor): the prop gizmos Delete / Rotate / Resize and the Utility sidebar</summary>
    /// <remarks>
    /// FIXES row 309, Z40K leftovers. Z40kPaintSidebar already syncs the paint window (SetColor from the swatches, the paint window's
    /// Delete button, and Z40K_PlacementController.Place). This module covers what that one lists as NOT covered. All of it is
    /// written from UI code on the clicker's PC only:
    /// <list type="bullet">
    /// <item>CompPropActions gizmo "Delete": Thing.Destroy on the prop.</item>
    /// <item>CompPropActions gizmo "Rotate": Thing.Rotation on the prop (saved as "rot") and a mesh redraw.</item>
    /// <item>CompPropActions gizmo "Resize": Thing.Destroy, Z40K_SpawnHelper.SpawnOwned and CompDecalColor.SetColor, then a new
    /// selection and a new edit session.</item>
    /// <item>Dialog_Z40K_UtilitySidebar, its Delete button (one Thing.Destroy in DrawSharedButtons) and its StartPlacement edit branch
    /// (Thing.Destroy, SpawnOwned, SetColor; reached from the Alt-variant button).</item>
    /// </list>
    /// What this module does (the mod is reached by name only, no reference to its dll):
    /// 1. A postfix on CompPropActions.CompGetGizmosExtra wraps the action of the three gizmos above (found by their label). At
    ///    interface time in a multiplayer game the wrapper decides on the clicker's PC (variant, faction) and sends ONE synced method:
    ///    SyncDelete(Thing), SyncRotateClockwise(Thing) or SyncReplaceProp(Thing, ThingDef, Faction, bool, int). Anywhere else the
    ///    original action runs as before.
    /// 2. A transpiler on Dialog_Z40K_UtilitySidebar.DrawSharedButtons points its one Thing.Destroy call at DestroyOrSync, which sends
    ///    SyncDelete(Thing).
    /// 3. A prefix on Dialog_Z40K_UtilitySidebar.StartPlacement sends SyncReplaceProp for the edit branch (an edit session is active)
    ///    and lets the other branch (only sets window state) run as before.
    /// 4. A postfix on Z40K_WindowTransitionQueue.GameComponentUpdate (runs every frame, outside synced commands) applies the one
    ///    follow-up that cannot run inside a synced command: selecting the new prop after a Resize. Multiplayer swaps the selection
    ///    list during a command and restores it afterwards, so a Select call inside the synced method would be lost.
    /// Each synced method checks on every PC that its Thing is not null, not destroyed and spawned, and that it is one of the mod's
    /// own props, before it touches anything. SyncReplaceProp reads the rotation and colour from the old prop when it runs (the
    /// window's copies are equal by construction), so a Rotate or paint command still in flight is not undone.
    /// Already covered by Z40kPaintSidebar, so not repeated here: SetColor from the paint window, the paint window's Delete button,
    /// and Z40K_PlacementController.Place (also used by the Copy gizmo and the Copy / Apply buttons of the Utility sidebar).
    /// Left local on purpose: the Utility, Copy and Paint gizmos (they only open windows or set Z40K_EditSession / Z40K_ActivePlacement
    /// / Z40K_PlacementState, static per-PC window state that is never saved), the Utility size buttons, Reset, Copy, Apply (placement
    /// branch) and Return buttons (same statics), the prop browser and its favourites (ModSettings, not game state).
    /// Supported build: Z40K 2.0 (Assemblies\Z40KPropsandDecor.dll in the mod root, 223,744 bytes; the 1.6 folder holds Defs only).
    /// Every member is looked up before anything is patched, and the IL of the patched methods is counted (calls of Thing.Destroy,
    /// SpawnOwned, SetColor, set_Rotation, the three gizmo labels). On a mismatch nothing is patched and the gizmos and the window
    /// behave as without the module. <c>active</c> is true only when every lookup, registration and patch worked.
    /// Status: [unverified] compiled only, not run in game.
    /// </remarks>
    [MpCompatFor("Zombie.40kPropsandDecor")]
    internal class Z40kGizmosAndUtilitySidebar
    {
        private const string LogPrefix = "[MPCompat - Z40K Gizmos And Utility Sidebar] ";
        private const string DefNamePrefix = "Z40K_";

        // mod types and members, found by name
        private static Type compDecalColorType, compPropActionsType;
        private static MethodInfo setColorMethod, spawnOwnedMethod, resolveVariantDefMethod, editSessionBeginMethod;
        private static FieldInfo colorField;
        private static FieldInfo editSessionThingField, editSessionDefNameField, editSessionVariantField, editSessionRotationField, lastVariantField;

        // True only when everything was found, registered and patched. While false every hook below passes straight through.
        private static bool active;

        // The prop to select once the next frame update runs outside a synced command (main thread only, clicker's PC only).
        private static Thing pendingSelect;

        private static int failures, syncMethods, patches;

        public Z40kGizmosAndUtilitySidebar(ModContentPack mod)
        {
            MethodBase getGizmos = null, utilityDrawSharedButtons = null, utilityStartPlacement = null, queueUpdate = null;
            Try("look up the Z40K members", () =>
            {
                var utility = TypeNamed("Z40K.Dialog_Z40K_UtilitySidebar");
                compDecalColorType = TypeNamed("Z40K.CompDecalColor");
                compPropActionsType = TypeNamed("Z40K.CompPropActions");
                var editSession = TypeNamed("Z40K.Z40K_EditSession");
                var spawnHelper = TypeNamed("Z40K.Z40K_SpawnHelper");
                var placementUtility = TypeNamed("Z40K.Z40K_PlacementUtility");
                var placementState = TypeNamed("Z40K.Z40K_PlacementState");
                var transitionQueue = TypeNamed("Z40K.Z40K_WindowTransitionQueue");

                if (!typeof(ThingComp).IsAssignableFrom(compDecalColorType) || !typeof(ThingComp).IsAssignableFrom(compPropActionsType))
                    throw new InvalidOperationException("CompDecalColor / CompPropActions are not ThingComps");

                getGizmos = Required(AccessTools.DeclaredMethod(compPropActionsType, "CompGetGizmosExtra", Type.EmptyTypes), "CompPropActions.CompGetGizmosExtra()");
                if (((MethodInfo)getGizmos).ReturnType != typeof(IEnumerable<Gizmo>))
                    throw new MissingMethodException("CompPropActions.CompGetGizmosExtra must return IEnumerable<Gizmo>");

                utilityDrawSharedButtons = Required(AccessTools.DeclaredMethod(utility, "DrawSharedButtons", Type.EmptyTypes), "Dialog_Z40K_UtilitySidebar.DrawSharedButtons()");
                utilityStartPlacement = Required(AccessTools.DeclaredMethod(utility, "StartPlacement", Type.EmptyTypes), "Dialog_Z40K_UtilitySidebar.StartPlacement()");
                queueUpdate = Required(AccessTools.DeclaredMethod(transitionQueue, "GameComponentUpdate", Type.EmptyTypes), "Z40K_WindowTransitionQueue.GameComponentUpdate()");

                colorField = AccessTools.DeclaredField(compDecalColorType, "color") ?? throw new MissingFieldException(compDecalColorType.FullName, "color");
                if (colorField.IsStatic || colorField.FieldType != typeof(Color))
                    throw new MissingFieldException("CompDecalColor.color is not an instance Color");

                setColorMethod = Required(AccessTools.DeclaredMethod(compDecalColorType, "SetColor", new[] { typeof(Color) }), "CompDecalColor.SetColor(Color)");
                if (setColorMethod.IsStatic)
                    throw new MissingMethodException("CompDecalColor.SetColor must be an instance method");

                spawnOwnedMethod = Required(AccessTools.DeclaredMethod(spawnHelper, "SpawnOwned",
                    new[] { typeof(ThingDef), typeof(IntVec3), typeof(Map), typeof(Rot4), typeof(Faction) }), "Z40K_SpawnHelper.SpawnOwned(ThingDef, IntVec3, Map, Rot4, Faction)");
                if (!spawnOwnedMethod.IsStatic || spawnOwnedMethod.ReturnType != typeof(Thing))
                    throw new MissingMethodException("Z40K_SpawnHelper.SpawnOwned must be static and return Thing");

                resolveVariantDefMethod = Required(AccessTools.DeclaredMethod(placementUtility, "ResolveVariantDef", new[] { typeof(string), typeof(int) }), "Z40K_PlacementUtility.ResolveVariantDef(string, int)");
                if (!resolveVariantDefMethod.IsStatic || resolveVariantDefMethod.ReturnType != typeof(ThingDef))
                    throw new MissingMethodException("Z40K_PlacementUtility.ResolveVariantDef must be static and return ThingDef");

                editSessionBeginMethod = Required(AccessTools.DeclaredMethod(editSession, "Begin", new[] { typeof(Thing) }), "Z40K_EditSession.Begin(Thing)");
                if (!editSessionBeginMethod.IsStatic)
                    throw new MissingMethodException("Z40K_EditSession.Begin must be static");

                editSessionThingField = StaticField(editSession, "Thing", typeof(Thing));
                editSessionDefNameField = StaticField(editSession, "DefName", typeof(string));
                editSessionVariantField = StaticField(editSession, "Variant", typeof(int));
                editSessionRotationField = StaticField(editSession, "Rotation", typeof(Rot4));
                lastVariantField = StaticField(placementState, "LastVariant", typeof(int));

                // The IL checks: patch nothing unless the mod still writes saved state in exactly the places this module routes.
                var destroy = DestroyMethod();
                var rotationSetter = AccessTools.PropertySetter(typeof(Thing), nameof(Thing.Rotation))
                                     ?? throw new MissingMethodException("Thing.Rotation setter not found");

                var lambdas = AccessTools.GetDeclaredMethods(compPropActionsType)
                    .Where(m => m.Name.StartsWith("<CompGetGizmosExtra>b__")).Cast<MethodBase>().ToList();
                Expect(lambdas.Count, 6, "gizmo action lambdas in CompPropActions");
                Expect(CountCalls(lambdas, destroy), 2, "Thing.Destroy calls in the gizmo actions (Delete, Resize)");
                Expect(CountCalls(lambdas, spawnOwnedMethod), 1, "SpawnOwned calls in the gizmo actions (Resize)");
                Expect(CountCalls(lambdas, setColorMethod), 1, "SetColor calls in the gizmo actions (Resize)");
                Expect(CountCalls(lambdas, rotationSetter), 1, "Thing.Rotation writes in the gizmo actions (Rotate)");

                var iterator = compPropActionsType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                                   .FirstOrDefault(t => t.Name.StartsWith("<CompGetGizmosExtra>"))
                               ?? throw new TypeLoadException("iterator type of CompGetGizmosExtra not found");
                var moveNext = Required(AccessTools.DeclaredMethod(iterator, "MoveNext", Type.EmptyTypes), "CompGetGizmosExtra iterator MoveNext()");
                var iteratorIl = PatchProcessor.GetOriginalInstructions(moveNext);
                foreach (var label in new[] { "Delete", "Rotate", "Resize" })
                    Expect(iteratorIl.Count(ci => ci.opcode == OpCodes.Ldstr && (ci.operand as string) == label), 1, $"gizmo label \"{label}\"");

                var utilityMethods = AccessTools.GetDeclaredMethods(utility).Cast<MethodBase>().ToList();
                Expect(CountCalls(utilityMethods, destroy), 2, "Thing.Destroy calls in the Utility sidebar (Delete button, StartPlacement)");
                Expect(CountCalls(utilityMethods, spawnOwnedMethod), 1, "SpawnOwned calls in the Utility sidebar (StartPlacement)");
                Expect(CountCalls(utilityMethods, setColorMethod), 1, "SetColor calls in the Utility sidebar (StartPlacement)");
                Expect(CountCalls(utilityMethods, rotationSetter), 0, "Thing.Rotation writes in the Utility sidebar");
                Expect(CountCalls(new[] { utilityDrawSharedButtons }, destroy), 1, "Thing.Destroy calls in Utility DrawSharedButtons (Delete button)");
                Expect(CountCalls(new[] { utilityStartPlacement }, destroy), 1, "Thing.Destroy calls in Utility StartPlacement");
                Expect(CountCalls(new[] { utilityStartPlacement }, spawnOwnedMethod), 1, "SpawnOwned calls in Utility StartPlacement");
            });

            if (failures > 0)
            {
                Log.Error($"{LogPrefix}this Z40K build does not have what the module syncs ({failures} problem(s) above): nothing patched, the gizmos and the Utility sidebar behave as without the module.");
                throw new InvalidOperationException($"{LogPrefix}unsupported Z40K build");
            }

            Try("register the synced methods", () =>
            {
                MP.RegisterSyncMethod(typeof(Z40kGizmosAndUtilitySidebar), nameof(SyncDelete)).CancelIfAnyArgNull();
                MP.RegisterSyncMethod(typeof(Z40kGizmosAndUtilitySidebar), nameof(SyncRotateClockwise)).CancelIfAnyArgNull();
                MP.RegisterSyncMethod(typeof(Z40kGizmosAndUtilitySidebar), nameof(SyncReplaceProp));
                syncMethods += 3;
            });

            if (failures == 0)
            {
                Try("patch CompPropActions.CompGetGizmosExtra", () =>
                {
                    MpCompat.harmony.Patch(getGizmos, postfix: new HarmonyMethod(typeof(Z40kGizmosAndUtilitySidebar), nameof(PostGetGizmos)));
                    patches++;
                });
                Try("patch Utility StartPlacement", () =>
                {
                    MpCompat.harmony.Patch(utilityStartPlacement, prefix: new HarmonyMethod(typeof(Z40kGizmosAndUtilitySidebar), nameof(PreUtilityStartPlacement)));
                    patches++;
                });
                Try("patch Utility DrawSharedButtons (Delete)", () =>
                {
                    MpCompat.harmony.Patch(utilityDrawSharedButtons, transpiler: new HarmonyMethod(typeof(Z40kGizmosAndUtilitySidebar), nameof(TranspileUtilityDelete)));
                    patches++;
                });
                Try("patch Z40K_WindowTransitionQueue.GameComponentUpdate", () =>
                {
                    MpCompat.harmony.Patch(queueUpdate, postfix: new HarmonyMethod(typeof(Z40kGizmosAndUtilitySidebar), nameof(PostQueueUpdate)));
                    patches++;
                });
            }

            if (failures == 0)
                active = true;
            else
                Log.Error($"{LogPrefix}module NOT active because a step failed: the gizmo and Utility sidebar edits stay local, as without the module.");

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

        private static int CountCalls(IEnumerable<MethodBase> methods, MethodInfo target)
            => methods.Sum(m => PatchProcessor.GetOriginalInstructions(m).Count(ci => ci.Calls(target)));

        private static void Expect(int actual, int expected, string what)
        {
            if (actual != expected)
                throw new InvalidOperationException($"{what}: found {actual}, expected {expected}");
        }

        private static ThingComp FindComp(Thing thing, Type compType)
            => thing is ThingWithComps withComps ? withComps.AllComps.FirstOrDefault(c => compType.IsInstanceOfType(c)) : null;

        // The mod's own props only: a synced method never acts on any other thing.
        private static bool IsZ40kProp(Thing thing)
            => FindComp(thing, compPropActionsType) != null || FindComp(thing, compDecalColorType) != null;

        private static bool IsAlive(Thing thing)
            => thing != null && !thing.Destroyed && thing.Spawned;

        private static void LogCommand(string what, Thing thing, string detail)
        {
            if (Prefs.DevMode)
                Log.Message($"{LogPrefix}{what} thing={thing?.ThingID} {detail} tick={Find.TickManager?.TicksGame} bySelf={MP.IsExecutingSyncCommandIssuedBySelf}");
        }

        #endregion

        #region Prop gizmos (Delete / Rotate / Resize)

        // CompGetGizmosExtra is an iterator: wrap its result lazily. Only the three gizmos below are touched.
        private static void PostGetGizmos(ThingComp __instance, ref IEnumerable<Gizmo> __result)
        {
            if (active && __result != null)
                __result = WrapGizmos(__instance, __result);
        }

        private static IEnumerable<Gizmo> WrapGizmos(ThingComp comp, IEnumerable<Gizmo> gizmos)
        {
            foreach (var gizmo in gizmos)
            {
                if (gizmo is Command_Action command && command.action != null)
                {
                    switch (command.defaultLabel)
                    {
                        case "Delete":
                            Route(command, () => DeleteFromGizmo(comp));
                            break;
                        case "Rotate":
                            Route(command, () => RotateFromGizmo(comp));
                            break;
                        case "Resize":
                            Route(command, () => ResizeFromGizmo(comp));
                            break;
                    }
                }

                yield return gizmo;
            }
        }

        // At interface time in a multiplayer game the routed action replaces the original; everywhere else the original runs.
        private static void Route(Command_Action command, Action routed)
        {
            var original = command.action;
            command.action = () =>
            {
                if (!active || !MP.IsInMultiplayer || !MP.InInterface)
                {
                    original();
                    return;
                }

                try
                {
                    routed();
                }
                catch (Exception e)
                {
                    // Do not fall back to the original: a local write would be an unsynced divergence.
                    Log.Error($"{LogPrefix}gizmo action failed, nothing was changed: {e}");
                }
            };
        }

        private static void DeleteFromGizmo(ThingComp comp)
        {
            var thing = comp.parent;
            // Gone or unspawned already on this PC: do nothing (a local destroy would be an unsynced divergence).
            if (IsAlive(thing))
                SyncDelete(thing);
        }

        private static void RotateFromGizmo(ThingComp comp)
        {
            var thing = comp.parent;
            if (!IsAlive(thing))
                return;
            // The gizmo starts an edit session for the prop (window state of this PC only).
            editSessionBeginMethod.Invoke(null, new object[] { thing });
            SyncRotateClockwise(thing);
        }

        private static void ResizeFromGizmo(ThingComp comp)
        {
            var thing = comp.parent;
            if (!IsAlive(thing))
                return;
            editSessionBeginMethod.Invoke(null, new object[] { thing });

            // Same choice as the gizmo: the next size after the one in this PC's window state, the first one that has a def.
            var defName = editSessionDefNameField.GetValue(null) as string;
            var number = (int)editSessionVariantField.GetValue(null) + 1;
            ThingDef newDef = null;
            for (var i = 0; i < 5; i++)
            {
                var candidate = (number + i) % 5;
                newDef = resolveVariantDefMethod.Invoke(null, new object[] { defName, candidate }) as ThingDef;
                if (newDef != null)
                {
                    number = candidate;
                    break;
                }
            }

            if (newDef != null)
                SyncReplaceProp(thing, newDef, Faction.OfPlayer, true, number);
        }

        #endregion

        #region Utility sidebar: Delete button and StartPlacement

        // The Utility sidebar's Delete button calls Thing.Destroy on the edited prop: after the transpiler it calls this instead.
        public static void DestroyOrSync(Thing thing, DestroyMode mode)
        {
            if (active && MP.IsInMultiplayer && MP.InInterface && mode == DestroyMode.Vanish)
            {
                // Gone or unspawned already on this PC: do nothing (a local destroy would be an unsynced divergence).
                if (IsAlive(thing))
                    SyncDelete(thing);
                return;
            }

            thing.Destroy(mode);
        }

        private static IEnumerable<CodeInstruction> TranspileUtilityDelete(IEnumerable<CodeInstruction> instructions)
        {
            var target = DestroyMethod();
            var replacement = AccessTools.DeclaredMethod(typeof(Z40kGizmosAndUtilitySidebar), nameof(DestroyOrSync));
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
                throw new InvalidOperationException($"{LogPrefix}Utility DrawSharedButtons transpiler replaced {replaced} calls, expected 1");
        }

        // Reached from the Alt-variant button while an edit session is active. Without a session the original only sets window state.
        private static bool PreUtilityStartPlacement()
        {
            if (!active || !MP.IsInMultiplayer || !MP.InInterface)
                return true;

            var thing = editSessionThingField.GetValue(null) as Thing;
            if (thing == null)
                return true;

            // Same def choice as the original: the session's def name at the session's variant.
            var defName = editSessionDefNameField.GetValue(null) as string;
            var variant = (int)editSessionVariantField.GetValue(null);
            var newDef = resolveVariantDefMethod.Invoke(null, new object[] { defName, variant }) as ThingDef;
            if (newDef != null && IsAlive(thing))
                SyncReplaceProp(thing, newDef, Faction.OfPlayer, false, -1);
            return false;
        }

        #endregion

        #region Synced methods

        // Delete gizmo and Utility Delete button. Runs on every PC from the same arguments (or directly when not in a multiplayer game).
        private static void SyncDelete(Thing thing)
        {
            if (!IsAlive(thing) || !IsZ40kProp(thing))
                return;
            LogCommand("Delete", thing, $"pos={thing.Position}");
            thing.Destroy(DestroyMode.Vanish);
        }

        // Rotate gizmo. Relative on purpose: two clicks before the first command ran still make two quarter turns.
        private static void SyncRotateClockwise(Thing thing)
        {
            if (!IsAlive(thing) || FindComp(thing, compPropActionsType) == null)
                return;

            var map = thing.Map;
            var rotation = thing.Rotation.Rotated(RotationDirection.Clockwise);
            LogCommand("Rotate", thing, $"rot={thing.Rotation}->{rotation}");
            thing.Rotation = rotation;
            thing.DirtyMapMesh(map);

            // Window state of the clicker only: keep the edit session's rotation equal to the prop's.
            if (MP.IsExecutingSyncCommandIssuedBySelf && ReferenceEquals(editSessionThingField.GetValue(null), thing))
                editSessionRotationField.SetValue(null, rotation);
        }

        // 5 parameters. Resize gizmo (reselect true, lastVariant = the size number) and Utility StartPlacement (false, -1).
        // The faction is decided at click time on the clicker's PC and travels as an argument.
        private static void SyncReplaceProp(Thing thing, ThingDef newDef, Faction faction, bool reselect, int lastVariant)
        {
            if (!IsAlive(thing) || newDef == null || faction == null || thing.Map == null)
                return;
            if (!IsZ40kProp(thing) || !newDef.defName.StartsWith(DefNamePrefix))
                return;

            // everything the original read from the old prop, read before it goes away
            var map = thing.Map;
            var cell = thing.Position;
            var rotation = thing.Rotation;
            var oldComp = FindComp(thing, compDecalColorType);
            var color = oldComp != null ? (Color)colorField.GetValue(oldComp) : Color.white;
            var bySelf = MP.IsExecutingSyncCommandIssuedBySelf;
            var wasEditing = bySelf && ReferenceEquals(editSessionThingField.GetValue(null), thing);

            LogCommand("Replace: destroy", thing, $"pos={cell} newDef={newDef.defName}");
            thing.Destroy(DestroyMode.Vanish);

            var spawned = spawnOwnedMethod.Invoke(null, new object[] { newDef, cell, map, rotation, faction }) as Thing;
            if (spawned == null)
                return;
            LogCommand("Replace: spawn", spawned, $"def={newDef.defName} pos={cell} rot={rotation} color={color}");
            var comp = FindComp(spawned, compDecalColorType);
            if (comp != null)
                setColorMethod.Invoke(comp, new object[] { color });

            // Window state of the clicker only (static, never saved). The selection itself waits for PostQueueUpdate.
            if (!bySelf)
                return;
            if (wasEditing)
                editSessionBeginMethod.Invoke(null, new object[] { spawned });
            if (lastVariant >= 0)
                lastVariantField.SetValue(null, lastVariant);
            if (reselect)
                pendingSelect = spawned;
        }

        #endregion

        #region Follow-up outside synced commands

        // Every frame. Applies the selection of the new prop after a Resize, once this PC is back at interface time.
        private static void PostQueueUpdate()
        {
            var thing = pendingSelect;
            if (thing == null || !MP.InInterface)
                return;

            pendingSelect = null;
            if (!active || !IsAlive(thing) || thing.Map != Find.CurrentMap)
                return;
            Find.Selector.ClearSelection();
            Find.Selector.Select(thing, true, true);
        }

        #endregion
    }
}
