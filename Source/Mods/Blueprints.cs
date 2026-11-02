using HarmonyLib;
using Multiplayer.API;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using UnityEngine;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Blueprints by Fluffy, and the fork "Blueprints Forked - 1.6" by Defi</summary>
    /// <see href="https://github.com/fluffy-mods/Blueprints"/>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=708455313"/>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3525001145"/>
    /// <remarks>
    /// FIXES row 398. Status: [unverified] compiled and checked by Tier 0 only, not run in game.
    /// <para>
    /// Id. The live mod is the fork (Workshop 3525001145, packageId Defi.Blueprints.fork). The old id Fluffy.Blueprints stays for the
    /// original mod. The fork renamed Blueprint.contents to Blueprint._contents, which was the only target of the old module that moved
    /// (decompiled 1.6\Assemblies\Blueprints.dll, sha256 91aeabd5...0701). The two names are tried in turn, so one build serves both mods.
    /// The old name is tried first, so the original mod is read exactly as before this row ([unverified]: no copy of the original mod
    /// was available to run it). The two mods are not meant to be loaded together (both use the namespace Blueprints).
    /// </para>
    /// <para>
    /// What is synced. Multiplayer patches DesignateSingleCell, DesignateMultiCell and DesignateThing of every Designator subclass
    /// (MultiplayerStatic.cs "General designation patches"), so the two designators of this mod are already one synced command each:
    /// (1) Designator_Blueprint.DesignateSingleCell, placing or planning a blueprint. It has no parameterless ctor, so Multiplayer cannot
    /// build it on the other PCs and it needs the sync workers below (BuildableInfo, Blueprint, Designator_Blueprint). The inner
    /// Designator_Build.DesignateSingleCell calls run inside that command (Multiplayer.InInterface is false there), so they are not
    /// sent a second time.
    /// (2) Designator_CreateBlueprint.DesignateMultiCell, creating a blueprint from an area. It has a parameterless ctor, so Multiplayer
    /// builds it itself. It adds a Blueprint to BlueprintController, a WorldComponent that is saved with the game, so every PC runs it.
    /// Nothing else of this module is synced on purpose, because nothing else touches the simulation: the rename dialog, remove, export
    /// and import (files), the edit window (stuff and "dropdown" choices), rotate and flip, Ctrl+C / "create from selection" (a
    /// temporary blueprint on this PC) only change the list or the blueprint on the PC where they are clicked, and the blueprint is
    /// sent whole with the next placement. The only calls into the simulation in the mod are BuildableInfo.Designate (a
    /// Designator_Build) and BuildableInfo.Plan (a Plan designation), and both are reached only from Designator_Blueprint.DesignateSingleCell.
    /// </para>
    /// <para>
    /// Fix A, Shift. Designator_Blueprint.DesignateSingleCell reads Event.current.shift to choose between planning (Plan designations)
    /// and designating (blueprint things). In the command that runs on every PC at the same tick, the Event is whatever this PC drew
    /// last: Shift held on one PC and not on the other gives different designations (a desync), and with no Event (outside OnGUI) it
    /// throws. The sync worker now sends the Shift state of the click, and a transpiler makes the method use it.
    /// </para>
    /// <para>
    /// Fix B, Create. The command of (2) also runs on the PCs of the other players, where Blueprint.Create opens the rename dialog (a
    /// modal, pausing window) and selects the new designator. Both are done only on the PC that sent the command. The other PCs get the
    /// blueprint in their list with the default name. The list of blueprints is therefore per PC: a rename, remove, edit, import or
    /// "create from selection" stays on the PC where it was done, the names can differ, and on a rejoin the host's list replaces the
    /// client's. Nothing in the simulation reads the list, so this is not a desync.
    /// </para>
    /// </remarks>
    [MpCompatFor("Defi.Blueprints.fork")]
    [MpCompatFor("Fluffy.Blueprints")]
    class Blueprints
    {
        private const string LogPrefix = "MPCompat :: Blueprints ";

        public Blueprints(ModContentPack mod) => LongEventHandler.ExecuteWhenFinished(LatePatch);

        //All used types, fields and methods for BuildableInfo
        readonly static Type buildableInfo = AccessTools.TypeByName("Blueprints.BuildableInfo");
        readonly static FieldInfo buildableInfoTerrainDef = AccessTools.Field(buildableInfo, "_terrainDef");
        readonly static FieldInfo buildableInfoThingDef = AccessTools.Field(buildableInfo, "_thingDef");
        readonly static FieldInfo buildableInfoPosition = AccessTools.Field(buildableInfo, "_position");
        readonly static FieldInfo buildableInfoRotation = AccessTools.Field(buildableInfo, "_rotation");
        readonly static FieldInfo buildableInfoStuff = AccessTools.Field(buildableInfo, "_stuff");
        readonly static ConstructorInfo buildableInfoConstructorTerrain = AccessTools.Constructor(buildableInfo, new Type[] { typeof(TerrainDef), typeof(IntVec3), typeof(IntVec3) });
        readonly static ConstructorInfo buildableInfoConstructorThing = AccessTools.Constructor(buildableInfo, new Type[] { typeof(Thing), typeof(IntVec3) });

        //SyncWorker for BuildableInfo
        private static void SyncBuildableInfo(SyncWorker sync, ref IExposable obj)
        {
            if(sync.isWriting)
            {
                TerrainDef terrain = (TerrainDef) buildableInfoTerrainDef.GetValue(obj);
                bool is_terrain = (terrain != null);
                sync.Write(is_terrain);
                if (is_terrain)
                {
                    sync.Write(terrain);
                    IntVec3 position = (IntVec3)buildableInfoPosition.GetValue(obj);
                    sync.Write(position.x);
                    sync.Write(position.y);
                    sync.Write(position.z);
                } else {
                    sync.Write((ThingDef)buildableInfoThingDef.GetValue(obj));
                    IntVec3 position = (IntVec3)buildableInfoPosition.GetValue(obj);
                    sync.Write(position.x);
                    sync.Write(position.y);
                    sync.Write(position.z);
                    sync.Write((Rot4)buildableInfoRotation.GetValue(obj));
                    sync.Write((ThingDef)buildableInfoStuff.GetValue(obj));

                }
            } else {
                bool is_terrain = sync.Read<bool>();
                if (is_terrain)
                {
                    TerrainDef terrain = sync.Read<TerrainDef>();
                    IntVec3 position = new IntVec3(sync.Read<int>(), sync.Read<int>(), sync.Read<int>());
                    obj = (IExposable)buildableInfoConstructorTerrain.Invoke(new object[] { terrain, position, IntVec3.Zero });
                } else {
                    ThingDef def = sync.Read<ThingDef>();
                    IntVec3 position = new IntVec3(sync.Read<int>(), sync.Read<int>(), sync.Read<int>());
                    Rot4 rotation = sync.Read<Rot4>();
                    ThingDef stuff = sync.Read<ThingDef>();
                    Thing thing = new Thing();
                    thing.def = def;
                    thing.Position = position;
                    thing.Rotation = rotation;
                    thing.SetStuffDirect(stuff);
                    obj = (IExposable)buildableInfoConstructorThing.Invoke(new object[] { thing, IntVec3.Zero });
                }

            }
        }

        //All types, fields and constructors used for syncing Blueprint
        readonly static Type listBuildableInfo = typeof(List<>).MakeGenericType(buildableInfo);
        readonly static Type blueprint = AccessTools.TypeByName("Blueprints.Blueprint");
        // The original mod calls the list contents, the fork (Defi.Blueprints.fork) _contents. Only one of them exists in a given mod.
        readonly static FieldInfo contentsField = AccessTools.Field(blueprint, "contents") ?? AccessTools.Field(blueprint, "_contents");
        readonly static FieldInfo _sizeField = AccessTools.Field(blueprint, "_size");
        readonly static ConstructorInfo blueprintConstructor = AccessTools.Constructor(blueprint, new Type[] { listBuildableInfo, typeof(IntVec2), typeof(string), typeof(bool) });

        //SyncWorker for Blueprint
        private static void SyncBlueprint(SyncWorker sync, ref IExposable obj)
        {
            if (sync.isWriting)
            {
                sync.Write((IList)contentsField.GetValue(obj), listBuildableInfo);
                sync.Write(((IntVec2)_sizeField.GetValue(obj)).x);
                sync.Write(((IntVec2)_sizeField.GetValue(obj)).z);
            } else
            {
                obj = (IExposable)blueprintConstructor.Invoke(new object[] { sync.Read<IList>(listBuildableInfo), new IntVec2(sync.Read<int>(), sync.Read<int>()), null, true });
            }
        }

        //All types, fields and constructors used to sync Designator_Blueprint
        readonly static Type designator_Blueprint = AccessTools.TypeByName("Blueprints.Designator_Blueprint");
        readonly static MethodInfo blueprintGetter = AccessTools.DeclaredPropertyGetter(designator_Blueprint, "Blueprint");
        readonly static ConstructorInfo designator_BlueprintConstructor = AccessTools.Constructor(designator_Blueprint, new Type[] { blueprint });

        // Fix A: the Shift state sent with each Designator_Blueprint, kept for the lifetime of the designator that was read from it.
        private static readonly ConditionalWeakTable<Designator, StrongBox<bool>> shiftHeldWhenSent = new ConditionalWeakTable<Designator, StrongBox<bool>>();

        //SyncWorker for Designator_Blueprint
        private static void SyncDesignator_Blueprint(SyncWorker sync, ref Designator obj)
        {
            if (sync.isWriting)
            {
                sync.Write<IExposable>((IExposable)blueprintGetter.Invoke(obj, null), blueprint);
                // Written while the click is handled, so Event.current is that click. No Event = no Shift.
                Event current = Event.current;
                sync.Write(current != null && current.shift);
            }
            else
            {
                //Designator_Blueprint(Blueprint) constructor
                obj = (Designator)designator_BlueprintConstructor.Invoke(new[]{ sync.Read<IExposable>(blueprint) });
                shiftHeldWhenSent.GetOrCreateValue(obj).Value = sync.Read<bool>();
            }
        }

        // Fix A: replaces Event.get_shift in Designator_Blueprint.DesignateSingleCell. Stack in: the Event. Stack out: bool.
        private static bool ShiftOrSent(Event ev, Designator designator)
        {
            if (designator != null && shiftHeldWhenSent.TryGetValue(designator, out StrongBox<bool> sent))
                return sent.Value;
            // A designator that did not come from the sync worker: single player. Event.current can be null outside OnGUI.
            return ev != null && ev.shift;
        }

        private static IEnumerable<CodeInstruction> ShiftFromSender(IEnumerable<CodeInstruction> instr)
        {
            MethodInfo replacement = AccessTools.DeclaredMethod(typeof(Blueprints), nameof(ShiftOrSent));
            int replaced = 0;
            foreach (CodeInstruction ci in instr)
            {
                if ((ci.opcode == OpCodes.Callvirt || ci.opcode == OpCodes.Call)
                    && ci.operand is MethodInfo mi && mi.Name == "get_shift" && mi.DeclaringType?.FullName == "UnityEngine.Event")
                {
                    replaced++;
                    // Keeps the labels and exception blocks of the replaced instruction. The Event is already on the stack.
                    ci.opcode = OpCodes.Ldarg_0;
                    ci.operand = null;
                    yield return ci;
                    yield return new CodeInstruction(OpCodes.Call, replacement);
                    continue;
                }
                yield return ci;
            }
            if (replaced != 1)
                Log.Error($"{LogPrefix}Shift fix: expected 1 use of Event.shift in Designator_Blueprint.DesignateSingleCell, replaced {replaced}. Planning with Shift may differ between PCs.");
        }

        // Fix B: true while Blueprint.Create(cells, map) runs for a command that another player sent.
        private static bool creatingForOtherPlayer;

        private static void CreateStart() =>
            creatingForOtherPlayer = MP.IsInMultiplayer && MP.IsExecutingSyncCommand && !MP.IsExecutingSyncCommandIssuedBySelf;

        private static Exception CreateEnd(Exception __exception)
        {
            creatingForOtherPlayer = false;
            return __exception;
        }

        // Blueprint(contents, size, defaultName, temporary): temporary = no rename dialog.
        private static void NoRenameDialogForOthers(ref bool __3)
        {
            if (creatingForOtherPlayer)
                __3 = true;
        }

        // BlueprintController.Add selects the new designator.
        private static bool NoSelectForOthers(Designator des) =>
            !(creatingForOtherPlayer && designator_Blueprint.IsInstanceOfType(des));

        // The lookups are the arguments of Patch (no null check in between) so that Tier 0 can follow them to the patch. A lookup that
        // finds nothing makes Harmony throw ArgumentNullException ("original"), which LatePatch logs with the name of the fix.
        private static void PatchShift()
        {
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(designator_Blueprint, "DesignateSingleCell", new[] { typeof(IntVec3) }),
                transpiler: new HarmonyMethod(typeof(Blueprints), nameof(ShiftFromSender)));
        }

        private static void PatchCreate()
        {
            // The three patches only do something while creatingForOtherPlayer is set, so a missing one cannot do harm to the others.
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(blueprint, "Create", new[] { typeof(IEnumerable<IntVec3>), typeof(Map) }),
                prefix: new HarmonyMethod(typeof(Blueprints), nameof(CreateStart)),
                finalizer: new HarmonyMethod(typeof(Blueprints), nameof(CreateEnd)));
            MpCompat.harmony.Patch(blueprintConstructor, prefix: new HarmonyMethod(typeof(Blueprints), nameof(NoRenameDialogForOthers)));
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(DesignatorManager), nameof(DesignatorManager.Select), new[] { typeof(Designator) }),
                prefix: new HarmonyMethod(typeof(Blueprints), nameof(NoSelectForOthers)));
        }

        // The loader runs the ctor once per matching MpCompatFor id (MpCompatLoader.InitCompatInAsm). With both mods active the second
        // run would register every worker a second time and patch DesignateSingleCell again, so only the first run does anything.
        private static bool patched;

        private static void LatePatch()
        {
            if (patched)
                return;
            patched = true;

            // Not a patch, a check: a null here would register the workers for all IExposable / Designator, so stop before that.
            var missing = new List<string>();
            void Need(object found, string what) { if (found == null) missing.Add(what); }
            Need(buildableInfo, "Blueprints.BuildableInfo");
            Need(buildableInfoTerrainDef, "BuildableInfo._terrainDef");
            Need(buildableInfoThingDef, "BuildableInfo._thingDef");
            Need(buildableInfoPosition, "BuildableInfo._position");
            Need(buildableInfoRotation, "BuildableInfo._rotation");
            Need(buildableInfoStuff, "BuildableInfo._stuff");
            Need(buildableInfoConstructorTerrain, "BuildableInfo(TerrainDef, IntVec3, IntVec3)");
            Need(buildableInfoConstructorThing, "BuildableInfo(Thing, IntVec3)");
            Need(blueprint, "Blueprints.Blueprint");
            Need(contentsField, "Blueprint._contents / contents");
            Need(_sizeField, "Blueprint._size");
            Need(blueprintConstructor, "Blueprint(List<BuildableInfo>, IntVec2, string, bool)");
            Need(designator_Blueprint, "Blueprints.Designator_Blueprint");
            Need(blueprintGetter, "Designator_Blueprint.Blueprint getter");
            Need(designator_BlueprintConstructor, "Designator_Blueprint(Blueprint)");
            if (missing.Count > 0)
                throw new MissingMemberException($"{LogPrefix}not found in the mod, no sync workers registered: {string.Join(", ", missing)}");

            //Register the SyncWorkers : ready to build blueprints !
            MP.RegisterSyncWorker<IExposable>(SyncBuildableInfo, buildableInfo);
            MP.RegisterSyncWorker<IExposable>(SyncBlueprint, blueprint);
            MP.RegisterSyncWorker<Designator>(SyncDesignator_Blueprint, designator_Blueprint);

            // One try per fix: a failed lookup in one must not skip the other.
            try
            {
                PatchShift();
            }
            catch (Exception e)
            {
                Log.Error($"{LogPrefix}Shift fix (planning follows the Shift key of the clicking player) not applied: {e}");
            }

            try
            {
                PatchCreate();
            }
            catch (Exception e)
            {
                Log.Error($"{LogPrefix}Create fix (no rename dialog and no selection on the other players' PCs) not applied: {e}");
            }
        }
    }
}
