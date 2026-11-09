using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace Multiplayer.Compat
{
    /// <summary>Colony Groups (TacticalGroups) by Derek Bickley and Troy_Alias, package <c>DerekBickley.LTOColonyGroupsFinal</c></summary>
    /// <remarks>
    /// Status: [unverified] not run in game. Compiled, and checked offline against the installed Colony Groups dll and the 1.7 game
    /// assemblies: every member is found, every rewrite finds its instruction, every patch binds its parameters.
    /// <para>
    /// TacticalGroups keeps groups in a world component (<c>pawnGroups</c>, <c>colonyGroups</c>, <c>caravanGroups</c>) and reads them
    /// in the simulation: group colours in the apparel and hair jobs, group work states and temporary workers in
    /// <c>Pawn_JobTracker.EndCurrentJob</c>, group bills in <c>Bill.PawnAllowedToStartAnew</c>, group policies in
    /// <c>ColonistGroup.SyncPoliciesFor</c>. Every button of the mod changed the clicking PC only. This module makes every player
    /// action that changes such a value a sync call, so all PCs change it in the same command.
    /// </para>
    /// <para>
    /// Group identity. A group object exists once per PC, so a group is sent as <see cref="GroupRef"/> (kind, key, hint):
    /// pawn group = its <c>groupID</c> (+ list index as hint), colony group = map unique id, caravan group = caravan id. In
    /// multiplayer a click that cannot be sent (a group that is not in the world component, an exception while sending) is
    /// dropped with a Log.Error, never run on the clicking PC alone: a local run is a desync.
    /// </para>
    /// <para>
    /// Mechanisms. (1) <see cref="GroupCallPrefix"/>: one Priority.First prefix for every TacticalGroups method that takes a group
    /// as target (Add, Disband, work states, ...). In the interface it sends <see cref="SyncGroupCall"/> and skips the method.
    /// Outside the interface (command, tick, load) it does nothing. A group call is a map command (the clicker's map, or the map of
    /// its pawns, one command per map): Multiplayer sets <c>Find.CurrentMap</c> to the command's map, so <c>PawnGroup.Map</c> is the
    /// map the player clicked in. (2) One IL rewrite for the buttons that write a field or a dictionary directly (<c>stfld</c> /
    /// <c>Dictionary.set_Item</c> / <c>Remove</c> / <c>Clear</c> / list reorder) to a hook that sends a sync call, and for the call
    /// sites of tiny methods the JIT can inline past a prefix (<c>AddGroup</c>, <c>InitSort</c>, <c>ResetGroupPolicies</c>,
    /// <c>SearchForWork</c>), which stay unpatched. (3) Prefixes that replace a whole UI method (colour removal, manual priorities).
    /// TG's own group sort is kept (en-US culture on every PC, synced list order). (4) Priority.First prefixes that rebuild the per-pawn group cache in a fixed order before the simulation
    /// reads it. (5) A last-priority postfix on the bill dialog menu (see <c>BillOptionsPostfix</c>). Every registered sync method
    /// and every patch method carries <c>NoInlining</c>.
    /// </para>
    /// <para>
    /// Known gaps, not fixed here: the attack menu creates Lords from the interface (<c>LordMaker.MakeNewLord</c>, six paths);
    /// code that changes the group structure while drawing (<c>RecacheEntries</c> creating caravan groups, <c>GetPawnRowsInt</c>
    /// removing dead pawns, the <c>AllCaravanGroups</c> getter); the "enabled" check boxes of the policies (they only decide which
    /// PC sends a field change); formations and battle stations; display order in the colonist bar; presets (blocked in
    /// multiplayer, the preset objects are per PC); the area drag sends one command per area crossed; <c>ColonistGroup.SetName</c>
    /// calls <c>Text.CalcHeight</c> inside the command (a display cache); <c>NormalizePawn</c> runs on every job end.
    /// </para>
    /// <para>
    /// Interim guard 65 (Ares <c>SeaWaylithCompat/Source/ColonyGroupsSimFix.cs</c>) removes the TacticalGroups patches on
    /// <c>EndCurrentJob</c>, <c>Bill.PawnAllowedToStartAnew</c>, <c>TryCreateRecolorJob</c>, <c>DyeHair.TryGiveJob</c> and skips
    /// <c>SyncPoliciesFor</c>. The guard removes only patches owned by <c>Troy_Alias.TacticalGroups</c>. This module patches under its
    /// own owner (<c>rimworld.multiplayer.compat</c>) and never patches <c>SyncPoliciesFor</c>, so it works with the guard active (its
    /// sync calls work, the group Work / bill / colour effects stay off) and with the guard removed (the effects work too).
    /// </para>
    /// </remarks>
    [MpCompatFor("DerekBickley.LTOColonyGroupsFinal")]
    public class ColonyGroups
    {
        private const string LogPrefix = "[MPCompat - Colony Groups] ";
        private const string TgNamespace = "TacticalGroups.";

        private static bool active;

        // ---- TacticalGroups members, found by reflection (the module has no compile time reference) ----------------------------------
        private static Type tGroup, tPawnGroup, tColonyGroup, tCaravanGroup, tTacticalGroups, tUtils, tBodyColor, tColorOption,
            tGroupColor, tGroupBills, tSortBy;
        private static FieldInfo fPawns, fGroupId, fWorkPriorities, fGroupColor, fBodyColors, fGroupArea, fDrugPolicy, fApparelPolicy,
            fFoodPolicy, fAutoDisband, fActiveSortBy, fSkillDefSort, fColorOptionColor, fColorOptionFavorite, fMenuGroup, fPickerMode,
            fPawnGroupsList, fCaravanGroupsDict, fColonyGroupsDict, fPawnsWithGroups, fCachedComps;
        private static MethodInfo mAddGroup, mColorOptionGetColor, mBillsSelectedGroupGetter, mMarkColonistsDirty;
        private static MethodInfo mInitSort, mResetPolicies, mSearchDef, mSearchEnum, mUpdateData;
        private static FieldInfo fColonistBar;
        private static Func<ThingDef, bool> isHeadgear, isFeetGear, isLegsGear, isTorsoGear, isArmsGear;
        private static AccessTools.FieldRef<Pawn_PlayerSettings, Pawn> playerSettingsPawn;

        // ---- group identity ---------------------------------------------------------------------------------------------------------

        /// <summary>A group named in a sync call. kind 0 = no group, 1 = pawn group, 2 = colony group, 3 = caravan group.</summary>
        public struct GroupRef
        {
            public int kind;
            public int key;
            public int hint;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SyncGroupRef(SyncWorker sync, ref GroupRef value)
        {
            sync.Bind(ref value.kind);
            sync.Bind(ref value.key);
            sync.Bind(ref value.hint);
        }

        private static bool ShouldSync() => active && MP.IsInMultiplayer && MP.InInterface;

        // In multiplayer a click that cannot be sent is dropped, never run on this PC alone: a local run is a desync.
        private static void Dropped(string what, Exception e = null)
            => Log.Error($"{LogPrefix}{what}: the click is dropped (running it on this PC alone would desync){(e == null ? "" : ": " + e)}");

        // A group call is a map command: Multiplayer sets Find.CurrentMap to the command's map while it runs, so PawnGroup.Map (which
        // is Find.CurrentMap) is the map of the player who clicked. A call without a Map argument is a world command, and a world
        // command pins the first player colony, which is the wrong map for a second faction. The map of a call is the map its pawns
        // stand on, else the map the player views.
        private static List<(Map map, List<Pawn> pawns)> SplitByMap(IEnumerable<Pawn> source)
        {
            var viewed = Find.CurrentMap;
            var batches = new List<(Map map, List<Pawn> pawns)>();
            foreach (var pawn in source)
            {
                if (pawn == null)
                    continue;

                // MapHeld: a carried pawn, a pawn in a casket or a corpse goes with the map MP's Thing worker gives it
                var map = pawn.MapHeld ?? viewed;
                var index = batches.FindIndex(b => b.map == map);
                if (index < 0)
                {
                    batches.Add((map, [pawn]));
                    continue;
                }

                batches[index].pawns.Add(pawn);
            }

            if (batches.Count == 0)
                batches.Add((viewed, []));
            return batches;
        }

        private static object Comp => tTacticalGroups == null ? null : Find.World?.GetComponent(tTacticalGroups);

        private static IList PawnGroupList => Comp is { } c ? fPawnGroupsList.GetValue(c) as IList : null;
        private static IDictionary CaravanDict => Comp is { } c ? fCaravanGroupsDict.GetValue(c) as IDictionary : null;
        private static IDictionary ColonyDict => Comp is { } c ? fColonyGroupsDict.GetValue(c) as IDictionary : null;

        private static int GroupId(object group) => group == null ? int.MinValue : (int) fGroupId.GetValue(group);
        private static List<Pawn> GroupPawns(object group) => group == null ? null : fPawns.GetValue(group) as List<Pawn>;

        private static bool TryEncode(object group, out GroupRef result)
        {
            result = default;
            if (group == null)
                return false;

            try
            {
                if (tPawnGroup.IsInstanceOfType(group))
                {
                    var list = PawnGroupList;
                    var index = list?.IndexOf(group) ?? -1;
                    if (index < 0)
                        return false;

                    result = new GroupRef { kind = 1, key = GroupId(group), hint = index };
                    return true;
                }

                if (tColonyGroup.IsInstanceOfType(group) && ColonyDict is { } colonies)
                {
                    foreach (DictionaryEntry entry in colonies)
                    {
                        if (ReferenceEquals(entry.Value, group) && entry.Key is Map map)
                        {
                            result = new GroupRef { kind = 2, key = map.uniqueID };
                            return true;
                        }
                    }
                }
                else if (tCaravanGroup.IsInstanceOfType(group) && CaravanDict is { } caravans)
                {
                    foreach (DictionaryEntry entry in caravans)
                    {
                        if (ReferenceEquals(entry.Value, group) && entry.Key is Caravan caravan)
                        {
                            result = new GroupRef { kind = 3, key = caravan.ID };
                            return true;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warning($"{LogPrefix}could not name a group: {e.Message}");
            }

            return false;
        }

        private static object Decode(GroupRef r)
        {
            try
            {
                switch (r.kind)
                {
                    case 1:
                        var list = PawnGroupList;
                        if (list == null)
                            return null;
                        if (r.hint >= 0 && r.hint < list.Count && GroupId(list[r.hint]) == r.key)
                            return list[r.hint];
                        foreach (var item in list)
                            if (GroupId(item) == r.key)
                                return item;
                        return null;
                    case 2:
                        if (ColonyDict is { } colonies)
                            foreach (DictionaryEntry entry in colonies)
                                if (entry.Key is Map map && map.uniqueID == r.key)
                                    return entry.Value;
                        return null;
                    case 3:
                        if (CaravanDict is { } caravans)
                            foreach (DictionaryEntry entry in caravans)
                                if (entry.Key is Caravan caravan && caravan.ID == r.key)
                                    return entry.Value;
                        return null;
                }
            }
            catch (Exception e)
            {
                Log.Warning($"{LogPrefix}could not find a group: {e.Message}");
            }

            return null;
        }

        // ---- group calls: one prefix, one sync method ---------------------------------------------------------------------------------

        private sealed class CallEntry
        {
            public MethodInfo method;
            public bool isStatic;
            public char[] kinds;
            public Type[] paramTypes;
        }

        private static readonly List<CallEntry> callEntries = [];
        private static readonly Dictionary<string, int> callIds = [];

        private static string MethodKey(MethodBase m) => m.DeclaringType?.FullName + "::" + m;

        private static char KindOf(Type t)
        {
            if (t == typeof(Pawn)) return 'P';
            if (t == typeof(List<Pawn>)) return 'L';
            if (t == typeof(WorkTypeDef)) return 'D';
            if (t.IsEnum) return 'E';
            if (t == typeof(int)) return 'I';
            if (t == typeof(string)) return 'S';
            if (t == typeof(bool)) return 'B';
            throw new NotSupportedException($"parameter type {t} cannot be sent as a group call");
        }

        // invoke = the method the sync call runs (a base declaration, so an override runs by virtual dispatch);
        // patched = the methods that get the prefix (the base and every override)
        // patch = false: the method is tiny, so the JIT can inline it into its callers and past a prefix. Its callers are rewritten
        // instead (the rewrite hooks send the call through SendGroupCall), and the method itself stays unpatched.
        private static void AddCall(MethodInfo invoke, IEnumerable<MethodInfo> patched, List<MethodInfo> patchTargets, bool patch = true)
        {
            var isStatic = invoke.IsStatic;
            var parms = invoke.GetParameters().Skip(isStatic ? 1 : 0).ToArray();
            var entry = new CallEntry
            {
                method = invoke,
                isStatic = isStatic,
                paramTypes = parms.Select(p => p.ParameterType).ToArray(),
                kinds = parms.Select(p => KindOf(p.ParameterType)).ToArray()
            };
            if (isStatic && invoke.GetParameters()[0].ParameterType != tGroup)
                throw new InvalidOperationException($"{invoke.Name}: the first parameter is not a group");

            var id = callEntries.Count;
            callEntries.Add(entry);
            foreach (var m in patched)
            {
                callIds[MethodKey(m)] = id;
                if (patch)
                    patchTargets.Add(m);
            }
        }

        private static MethodInfo Method(Type type, string name, params Type[] args)
            => AccessTools.DeclaredMethod(type, name, args) ?? throw new MissingMethodException(type.FullName, name);

        private static void BuildCallTable(List<MethodInfo> patchTargets)
        {
            var workTypeEnum = FindType("WorkTypeEnum");
            var subclasses = new[] { tPawnGroup, tColonyGroup, tCaravanGroup };
            var baseCalls = new (Type type, string name, Type[] args)[]
            {
                (tGroup, "Add", [typeof(Pawn)]),
                (tGroup, "Add", [typeof(List<Pawn>)]),
                (tGroup, "Disband", Type.EmptyTypes),
                (tGroup, "Disband", [typeof(Pawn)]),
                (tGroup, "SetName", [typeof(string)]),
                (tGroup, "InitSort", [tSortBy]),
                (tGroup, "ChangeWorkState", [workTypeEnum]),
                (tGroup, "ChangeWorkState", [typeof(WorkTypeDef)]),
                (tGroup, "RemoveWorkState", [workTypeEnum]),
                (tGroup, "RemoveWorkState", [typeof(WorkTypeDef)]),
                (tGroup, "SetGroupWorkPriorityFor", [typeof(WorkTypeDef), typeof(int)]),
                (tGroup, "AssignTemporaryWorkers", [typeof(WorkTypeDef)]),
                (tGroup, "AssignTemporaryWorkers", [workTypeEnum]),
                (tGroup, "ResetGroupPolicies", Type.EmptyTypes),
                // declared by one subclass only
                (tPawnGroup, "Disband", [typeof(List<Pawn>), typeof(bool)]),
                (tColonyGroup, "Disband", [typeof(List<Pawn>)]),
            };
            foreach (var (type, name, args) in baseCalls)
            {
                try
                {
                    var baseCall = Method(type, name, args);
                    var patched = new List<MethodInfo> { baseCall };
                    if (type == tGroup)
                        foreach (var sub in subclasses)
                            if (AccessTools.DeclaredMethod(sub, name, args) is { } overrideMethod && overrideMethod != baseCall)
                                patched.Add(overrideMethod);
                    // InitSort (14 bytes) and ResetGroupPolicies are called from known places: those call sites are rewritten
                    var callSiteOnly = type == tGroup && (name == "InitSort" || name == "ResetGroupPolicies");
                    AddCall(baseCall, patched, patchTargets, !callSiteOnly);
                    if (name == "SetName" && type == tGroup)
                        setNameId = callEntries.Count - 1;
                    if (name == "InitSort" && type == tGroup)
                        mInitSort = baseCall;
                    if (name == "ResetGroupPolicies" && type == tGroup)
                        mResetPolicies = baseCall;
                }
                catch (Exception e)
                {
                    Log.Warning($"{LogPrefix}group call {type.Name}.{name} is not synced: {e.Message}");
                }
            }

            // static helpers of the group menus (extension methods, the group is the first argument)
            foreach (var name in new[] { "Draft", "Undraft", "SwitchToAttackMode", "RemoveOldLord", "SearchForJob" })
            {
                try
                {
                    var helper = Method(tUtils, name, tGroup);
                    AddCall(helper, [helper], patchTargets);
                }
                catch (Exception e)
                {
                    Log.Warning($"{LogPrefix}group helper {name} is not synced: {e.Message}");
                }
            }
        }

        // two prefixes, so no prefix asks Harmony for __instance of a static method
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool GroupCallPrefix(object __instance, object[] __args, MethodBase __originalMethod)
            => SendGroupCall(__instance, __args, __originalMethod);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool GroupCallPrefixStatic(object[] __args, MethodBase __originalMethod)
            => SendGroupCall(null, __args, __originalMethod);

        // Dialog_RenameColonistGroup.SetName: a tiny caller of ColonistGroup.SetName, which the JIT may inline into the dialog
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool RenameDialogPrefix(object __instance, string name)
        {
            if (!ShouldSync() || setNameId < 0)
                return true;

            try
            {
                if (!TryEncode(fMenuGroup.GetValue(__instance), out var groupRef))
                {
                    Dropped("rename: the group is not in the group lists");
                    return false;
                }

                SyncGroupCall(groupRef, setNameId, [], name ?? "", 0, false, Find.CurrentMap);
                return false;
            }
            catch (Exception e)
            {
                Dropped("the rename could not be sent", e);
                return false;
            }
        }

        private static int setNameId = -1;

        private static bool SendGroupCall(object __instance, object[] __args, MethodBase __originalMethod)
        {
            if (!ShouldSync() || __originalMethod == null || !callIds.TryGetValue(MethodKey(__originalMethod), out var id))
                return true;

            try
            {
                var entry = callEntries[id];
                var group = entry.isStatic ? __args[0] : __instance;
                if (!TryEncode(group, out var groupRef))
                {
                    // A group in no list is a group under construction: the CaravanGroup, ColonyGroup and PawnGroup constructors call Add before the
                    // group is stored (FIXES row 408). Nobody can click such a group and Add only fills the new object. Dropping the call left an
                    // empty group that the colonist bar removed and rebuilt on every recache until the stack overflowed.
                    if (__originalMethod.Name == "Add")
                        return true;

                    Dropped($"group call {__originalMethod.Name}: the group is not in the group lists");
                    return false;
                }

                var offset = entry.isStatic ? 1 : 0;
                var pawns = new List<Pawn>();
                var text = "";
                var number = 0;
                var flag = false;
                for (var k = 0; k < entry.kinds.Length; k++)
                {
                    var arg = __args[offset + k];
                    switch (entry.kinds[k])
                    {
                        case 'P':
                            if (arg == null) return DroppedArg(__originalMethod);
                            pawns.Add((Pawn) arg);
                            break;
                        case 'L':
                            if (arg is not List<Pawn> list) return DroppedArg(__originalMethod);
                            pawns.AddRange(list.Where(p => p != null));
                            break;
                        case 'D':
                            if (arg is not WorkTypeDef def) return DroppedArg(__originalMethod);
                            text = def.defName;
                            break;
                        case 'E':
                            number = Convert.ToInt32(arg);
                            break;
                        case 'I':
                            number = (int) arg;
                            break;
                        case 'S':
                            text = (string) arg ?? "";
                            break;
                        case 'B':
                            flag = (bool) arg;
                            break;
                    }
                }

                // one command per map: a command has one map, and Multiplayer throws when the pawns of one call stand on two maps
                foreach (var (map, batch) in SplitByMap(pawns))
                    SyncGroupCall(groupRef, id, batch, text, number, flag, map);
                return false;
            }
            catch (Exception e)
            {
                Dropped($"group call {__originalMethod?.Name} could not be sent", e);
                return false;
            }
        }

        private static bool DroppedArg(MethodBase method)
        {
            Dropped($"group call {method.Name} has an argument that cannot be sent");
            return false;
        }

        // Runs the TacticalGroups method on every PC. A group or a def that does not exist on this PC cancels the call.
        // map: not read. Writing it makes this a map command, so Find.CurrentMap is that map while the call runs.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void SyncGroupCall(GroupRef group, int id, List<Pawn> pawns, string text, int number, bool flag, Map map)
        {
            if (!active || id < 0 || id >= callEntries.Count)
                return;

            var entry = callEntries[id];
            var target = Decode(group);
            if (target == null)
                return;

            var args = new List<object>();
            if (entry.isStatic)
                args.Add(target);
            for (var k = 0; k < entry.kinds.Length; k++)
            {
                switch (entry.kinds[k])
                {
                    case 'P':
                        if (pawns == null || pawns.Count == 0 || pawns[0] == null) return;
                        args.Add(pawns[0]);
                        break;
                    case 'L':
                        args.Add(pawns == null ? new List<Pawn>() : pawns.Where(p => p != null).ToList());
                        break;
                    case 'D':
                        var def = DefDatabase<WorkTypeDef>.GetNamedSilentFail(text);
                        if (def == null) return;
                        args.Add(def);
                        break;
                    case 'E':
                        args.Add(Enum.ToObject(entry.paramTypes[k], number));
                        break;
                    case 'I':
                        args.Add(number);
                        break;
                    case 'S':
                        args.Add(text ?? "");
                        break;
                    case 'B':
                        args.Add(flag);
                        break;
                }
            }

            try
            {
                entry.method.Invoke(entry.isStatic ? null : target, args.ToArray());
            }
            catch (TargetInvocationException e)
            {
                Log.Error($"{LogPrefix}group call {entry.method.Name} failed: {e.InnerException}");
            }
        }

        // ---- group structure ---------------------------------------------------------------------------------------------------------

        // TacticalGroups.AddGroup(List<Pawn>) is 29 bytes, so the JIT can inline it into the colonist bar and past a prefix. The one
        // call site (the "create group" button of TacticalColonistBar) is rewritten to this hook. In multiplayer the vanilla code runs
        // in a command on every PC.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void HookAddGroup(object comp, List<Pawn> pawns)
        {
            if (!ShouldSync())
            {
                CallUnwrapped(mAddGroup, comp, pawns);
                return;
            }

            try
            {
                var list = pawns?.Where(p => p != null).ToList();
                if (list == null || list.Count == 0)
                    return;

                var batches = SplitByMap(list);
                if (batches.Count > 1)
                {
                    Messages.Message("A group cannot be made from colonists of different maps in multiplayer.", MessageTypeDefOf.RejectInput, false);
                    return;
                }

                SyncAddGroup(list, batches[0].map);
            }
            catch (Exception e)
            {
                Dropped("creating the group could not be sent", e);
            }
        }

        // calls a Colony Groups method by reflection and throws what it threw, not a TargetInvocationException
        private static void CallUnwrapped(MethodInfo method, object target, params object[] args)
        {
            try
            {
                method.Invoke(target, args);
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void SyncAddGroup(List<Pawn> pawns, Map map)
        {
            if (!active || pawns == null || Comp is not { } comp)
                return;

            var list = pawns.Where(p => p != null).ToList();
            if (list.Count == 0)
                return;

            try
            {
                mAddGroup.Invoke(comp, [list]);
            }
            catch (TargetInvocationException e)
            {
                Log.Error($"{LogPrefix}creating a group failed: {e.InnerException}");
            }
        }

        // The group id of a new pawn group is "pawn group count + 1", which repeats after a disband. The id names the group in every
        // sync call, so a new group gets an id that no pawn group or former group has.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CreateGroupIdPostfix(ref int __result)
        {
            if (!active || !MP.IsInMultiplayer)
                return;

            try
            {
                var used = new HashSet<int>();
                if (PawnGroupList is { } groups)
                    foreach (var g in groups)
                        used.Add(GroupId(g));
                if (CaravanDict is { } caravans)
                    foreach (DictionaryEntry entry in caravans)
                        if (FormerGroups(entry.Value) is { } former)
                            foreach (var formerGroup in former.Values)
                                if (fFormerPawnGroups.GetValue(formerGroup) is IList formerList)
                                    foreach (var g in formerList)
                                        used.Add(GroupId(g));
                while (used.Contains(__result))
                    __result++;
            }
            catch (Exception e)
            {
                Log.Warning($"{LogPrefix}could not make the group id unique: {e.Message}");
            }
        }

        private static FieldInfo fFormerGroups, fFormerPawnGroups;

        private static IDictionary FormerGroups(object caravanGroup)
            => caravanGroup == null ? null : fFormerGroups.GetValue(caravanGroup) as IDictionary;

        // PawnGroup.Map is Find.CurrentMap, which differs on each PC. The simulation reads it (CreateOrJoinColony, the group
        // helpers), so while the game ticks it is the map of the spawned pawn of the group with the lowest thing id (the order of
        // the group's pawn list is not the same on every PC). In the interface it is the map the player views. In a sync command
        // it is the command's map: every group call is a map command (see SplitByMap), and Multiplayer sets Find.CurrentMap to the
        // command's map, the same on every PC, so the postfix leaves it alone there.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PawnGroupMapPostfix(object __instance, ref Map __result)
        {
            if (!active || !MP.IsInMultiplayer || MP.InInterface || MP.IsExecutingSyncCommand)
                return;

            Pawn first = null;
            if (GroupPawns(__instance) is { } pawns)
                foreach (var pawn in pawns)
                    if (pawn is { Spawned: true, Map: not null } && (first == null || pawn.thingIDNumber < first.thingIDNumber))
                        first = pawn;
            __result = first?.Map;
        }

        // Reorder arrows (PawnGroup.DrawOverlays): "RemoveAt(i); Insert(j, this)" on the pawn group list becomes one sync call.
        private static object pendingMove;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void HookListRemoveAt(IList list, int index)
        {
            if (ShouldSync() && ReferenceEquals(list, PawnGroupList))
            {
                pendingMove = index >= 0 && index < list.Count ? list[index] : null;
                return;
            }

            list.RemoveAt(index);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void HookListInsert(IList list, int index, object group)
        {
            if (ShouldSync() && ReferenceEquals(list, PawnGroupList))
            {
                if (pendingMove != null && ReferenceEquals(pendingMove, group) && TryEncode(group, out var groupRef))
                    SyncMoveGroup(groupRef, index);
                else
                    Dropped("moving the group could not be sent");
                pendingMove = null;
                return;
            }

            list.Insert(index, group);
        }

        // In-group pawn arrows (ColonistGroup.DrawPawnArrows): "RemoveAt(i); Insert(j, pawn)" on the pawn list of a group. The order of
        // that list decides which pawn gives the group its map, the order group commands walk the pawns in and the order of ties in a
        // sort, so it is the same on every PC.
        private static (List<Pawn> list, Pawn pawn) pendingPawnMove;

        private static object GroupOfPawnList(List<Pawn> list) => FindGroup(g => ReferenceEquals(GroupPawns(g), list));

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void HookPawnListRemoveAt(List<Pawn> list, int index)
        {
            if (ShouldSync() && GroupOfPawnList(list) != null)
            {
                pendingPawnMove = (list, index >= 0 && index < list.Count ? list[index] : null);
                return;
            }

            list.RemoveAt(index);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void HookPawnListInsert(List<Pawn> list, int index, Pawn pawn)
        {
            if (ShouldSync() && GroupOfPawnList(list) is { } group)
            {
                if (ReferenceEquals(pendingPawnMove.list, list) && pendingPawnMove.pawn == pawn && pawn != null && TryEncode(group, out var groupRef))
                    SyncMovePawn(groupRef, pawn, index);
                else
                    Dropped("moving the pawn in the group could not be sent");
                pendingPawnMove = default;
                return;
            }

            list.Insert(index, pawn);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void SyncMovePawn(GroupRef group, Pawn pawn, int index)
        {
            if (!active || pawn == null || Decode(group) is not { } target || GroupPawns(target) is not { } list || !list.Contains(pawn))
                return;

            list.Remove(pawn);
            list.Insert(Math.Max(0, Math.Min(index, list.Count)), pawn);
            MarkColonistsDirty();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void SyncMoveGroup(GroupRef group, int index)
        {
            if (!active || Decode(group) is not { } target || PawnGroupList is not { } list)
                return;

            list.Remove(target);
            list.Insert(Math.Max(0, Math.Min(index, list.Count)), target);
            MarkColonistsDirty();
        }

        private static void MarkColonistsDirty()
        {
            try
            {
                if (fColonistBar?.GetValue(null) is { } bar)
                    mMarkColonistsDirty?.Invoke(bar, null);
            }
            catch (Exception)
            {
                // the bar redraws on the next change anyway
            }
        }

        // Presets are objects of the settings file of one PC, the other PCs do not have them.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool ActivatePresetPrefix()
        {
            if (!ShouldSync())
                return true;

            Messages.Message("Colony Groups presets are not available in multiplayer (the presets belong to one PC).",
                MessageTypeDefOf.RejectInput, false);
            return false;
        }

        // ---- IL rewrites: field stores and dictionary calls of the buttons ---------------------------------------------------------------

        private sealed class Rewrite
        {
            public string name;
            public string[] types;
            public Func<CodeInstruction, bool> match;
            public MethodInfo hook;
            public int count;
        }

        private static readonly List<Rewrite> rewrites = [];

        // the rewritten methods and the rewrites that apply to each (key = MethodKey)
        private static readonly Dictionary<string, List<Rewrite>> rewritesByMethod = [];

        private static void AddRewrite(string name, string[] types, Func<CodeInstruction, bool> match, string hookName)
            => rewrites.Add(new Rewrite
            {
                name = name, types = types, match = match,
                hook = AccessTools.DeclaredMethod(typeof(ColonyGroups), hookName) ?? throw new MissingMethodException(nameof(ColonyGroups), hookName)
            });

        private static bool SameField(object operand, FieldInfo field)
            => operand is FieldInfo f && (f == field || (f.Module == field.Module && f.MetadataToken == field.MetadataToken));

        private static bool SameMethod(object operand, MethodInfo method)
            => operand is MethodInfo m && (m == method || (m.DeclaringType == method.DeclaringType && m.Name == method.Name
                                                           && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(method.GetParameters().Select(p => p.ParameterType))));

        private static bool IsStore(CodeInstruction ci, FieldInfo field) => ci.opcode == OpCodes.Stfld && SameField(ci.operand, field);

        private static bool IsCall(CodeInstruction ci, MethodInfo method)
            => (ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && SameMethod(ci.operand, method);

        // one transpiler for every rewritten method: the instruction is replaced by a call of the hook with the same stack shape
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static IEnumerable<CodeInstruction> RewriteTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            if (original == null || !rewritesByMethod.TryGetValue(MethodKey(original), out var applicable))
                return instructions;
            return ApplyRewrites(instructions, applicable, null);
        }

        // counts: hits per rewrite, filled when given (the install check counts on the original instructions)
        private static List<CodeInstruction> ApplyRewrites(IEnumerable<CodeInstruction> instructions, List<Rewrite> applicable, Dictionary<Rewrite, int> counts)
        {
            var result = new List<CodeInstruction>();
            foreach (var ci in instructions)
            {
                Rewrite hit = null;
                foreach (var rewrite in applicable)
                {
                    if (rewrite.match(ci))
                    {
                        hit = rewrite;
                        break;
                    }
                }

                if (hit == null)
                {
                    result.Add(ci);
                    continue;
                }

                if (counts != null)
                    counts[hit] = counts.GetValueOrDefault(hit) + 1;
                var replacement = new CodeInstruction(OpCodes.Call, hit.hook);
                replacement.labels.AddRange(ci.labels);
                replacement.blocks.AddRange(ci.blocks);
                result.Add(replacement);
            }

            return result;
        }

        private static IEnumerable<MethodBase> DeclaredMethodsDeep(Type type)
        {
            foreach (var m in AccessTools.GetDeclaredMethods(type))
                yield return m;
            foreach (var c in AccessTools.GetDeclaredConstructors(type))
                yield return c;
            foreach (var nested in type.GetNestedTypes(AccessTools.all))
                foreach (var m in DeclaredMethodsDeep(nested))
                    yield return m;
        }

        // For each rewrite, the methods of its types that contain an instruction it matches. Fills rewritesByMethod and returns the
        // methods to patch. Each rewrite's hit count is checked: a rewrite with no hit means that button stays local.
        private static List<MethodBase> FindRewriteTargets()
        {
            var methods = new Dictionary<string, MethodBase>();
            var counts = new Dictionary<Rewrite, int>();
            foreach (var rewrite in rewrites)
            {
                foreach (var name in rewrite.types)
                {
                    var type = AccessTools.TypeByName(TgNamespace + name);
                    if (type == null)
                    {
                        Log.Warning($"{LogPrefix}type {name} not found, the buttons of rewrite '{rewrite.name}' stay local");
                        continue;
                    }

                    foreach (var method in DeclaredMethodsDeep(type))
                    {
                        try
                        {
                            if (method.GetMethodBody() == null)
                                continue;
                            var instructions = PatchProcessor.GetOriginalInstructions(method);
                            var hits = instructions.Count(ci => rewrite.match(ci));
                            if (hits == 0)
                                continue;

                            var key = MethodKey(method);
                            if (!rewritesByMethod.TryGetValue(key, out var list))
                                rewritesByMethod[key] = list = [];
                            if (!list.Contains(rewrite))
                                list.Add(rewrite);
                            methods[key] = method;
                            counts[rewrite] = counts.GetValueOrDefault(rewrite) + hits;
                        }
                        catch (Exception)
                        {
                            // a method Harmony cannot read is not one we rewrite
                        }
                    }
                }
            }

            foreach (var rewrite in rewrites)
            {
                if (counts.GetValueOrDefault(rewrite) == 0)
                    Log.Error($"{LogPrefix}rewrite '{rewrite.name}' found nothing to change, its buttons stay local (they desync)");
                else
                    rewrite.count = counts[rewrite];
            }

            return methods.Values.ToList();
        }

        // ---- hooks: stores of group fields ---------------------------------------------------------------------------------------------

        private static void Set(FieldInfo field, object target, object value) => field.SetValue(target, value);

        // The hooks below replace a store into a group field. In multiplayer (interface) the store becomes a command, and it is never
        // made on this PC alone: a value the command cannot carry (no group name, a null policy) drops the click.
        // No PC keeps a value until the command has run on all of them, so a store of the value the group already has sends nothing.
        private static bool SendField(object group, int field, int a, int b, string text, bool flag)
        {
            if (!TryEncode(group, out var groupRef))
            {
                Dropped("a group setting could not be sent: the group is not in the group lists");
                return false;
            }

            SyncSetField(groupRef, field, a, b, text, flag);
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void HookGroupArea(object group, Area area)
        {
            if (ShouldSync())
            {
                if (fGroupArea.GetValue(group) != area)
                    SendField(group, FieldArea, area == null ? -1 : area.Map.uniqueID, area == null ? -1 : area.ID, "", false);
                return;
            }

            Set(fGroupArea, group, area);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void HookGroupDrugPolicy(object group, DrugPolicy policy)
        {
            if (ShouldSync())
            {
                if (policy != null && fDrugPolicy.GetValue(group) != policy)
                    SendField(group, FieldDrug, policy.id, 0, "", false);
                return;
            }

            Set(fDrugPolicy, group, policy);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void HookGroupApparelPolicy(object group, ApparelPolicy policy)
        {
            if (ShouldSync())
            {
                if (policy != null && fApparelPolicy.GetValue(group) != policy)
                    SendField(group, FieldApparel, policy.id, 0, "", false);
                return;
            }

            Set(fApparelPolicy, group, policy);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void HookGroupFoodPolicy(object group, FoodPolicy policy)
        {
            if (ShouldSync())
            {
                if (policy != null && fFoodPolicy.GetValue(group) != policy)
                    SendField(group, FieldFood, policy.id, 0, "", false);
                return;
            }

            Set(fFoodPolicy, group, policy);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void HookAutoDisband(object group, bool value)
        {
            if (ShouldSync())
            {
                if ((bool) fAutoDisband.GetValue(group) != value)
                    SendField(group, FieldAutoDisband, 0, 0, "", value);
                return;
            }

            Set(fAutoDisband, group, value);
        }

        // The sort menu: "Name" stores activeSortBy and then calls InitSort(group.activeSortBy). The store is a command, so the local
        // field still has the old value when InitSort reads it. The value is remembered for the InitSort call that follows in the same
        // click (HookInitSort), which sends one InitSort(Name).
        private static object pendingSortGroup;
        private static int pendingSortValue;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void HookActiveSortBy(object group, int value)
        {
            if (ShouldSync())
            {
                pendingSortGroup = Enum.ToObject(tSortBy, value).ToString() == "Name" ? group : null;
                pendingSortValue = value;
                SendField(group, FieldSortBy, value, 0, "", false);
                return;
            }

            Set(fActiveSortBy, group, Enum.ToObject(tSortBy, value));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void HookSkillDefSort(object group, SkillDef skill)
        {
            if (ShouldSync())
            {
                pendingSortGroup = null;
                SendField(group, FieldSkill, 0, 0, skill?.defName ?? "", false);
                return;
            }

            Set(fSkillDefSort, group, skill);
        }

        // SortMenu calls InitSort(SortBy) in two lambdas. InitSort is 14 bytes, so the JIT can inline it past a prefix: the two call
        // sites are rewritten to this hook, and InitSort itself is not patched.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void HookInitSort(object group, int sortBy)
        {
            if (ShouldSync())
            {
                if (ReferenceEquals(pendingSortGroup, group))
                    sortBy = pendingSortValue;
                pendingSortGroup = null;
                SendGroupCall(group, [Enum.ToObject(tSortBy, sortBy)], mInitSort);
                return;
            }

            CallUnwrapped(mInitSort, group, Enum.ToObject(tSortBy, sortBy));
        }

        // ColonistGroup.ResetGroupPolicies (79 bytes): called from one dialog button, rewritten the same way.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void HookResetGroupPolicies(object group)
        {
            if (ShouldSync())
            {
                SendGroupCall(group, [], mResetPolicies);
                return;
            }

            CallUnwrapped(mResetPolicies, group);
        }

        private const int FieldArea = 0, FieldDrug = 1, FieldApparel = 2, FieldFood = 3, FieldAutoDisband = 4, FieldSortBy = 5, FieldSkill = 6;

        // a, b: ids (area: map id and area id; policy: policy id; sort: enum value); -1 = none
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void SyncSetField(GroupRef group, int field, int a, int b, string text, bool flag)
        {
            if (!active || Decode(group) is not { } target)
                return;

            switch (field)
            {
                case FieldArea:
                    Area area = null;
                    if (a >= 0 && b >= 0)
                    {
                        area = Find.Maps.Find(m => m.uniqueID == a)?.areaManager.AllAreas.Find(x => x.ID == b);
                        if (area == null)
                            return;
                    }
                    Set(fGroupArea, target, area);
                    break;
                case FieldDrug:
                    if (Current.Game.drugPolicyDatabase.AllPolicies.Find(p => p.id == a) is { } drug)
                        Set(fDrugPolicy, target, drug);
                    break;
                case FieldApparel:
                    if (Current.Game.outfitDatabase.AllOutfits.Find(p => p.id == a) is { } apparel)
                        Set(fApparelPolicy, target, apparel);
                    break;
                case FieldFood:
                    if (Current.Game.foodRestrictionDatabase.AllFoodRestrictions.Find(p => p.id == a) is { } food)
                        Set(fFoodPolicy, target, food);
                    break;
                case FieldAutoDisband:
                    Set(fAutoDisband, target, flag);
                    break;
                case FieldSortBy:
                    Set(fActiveSortBy, target, Enum.ToObject(tSortBy, a));
                    break;
                case FieldSkill:
                    Set(fSkillDefSort, target, string.IsNullOrEmpty(text) ? null : DefDatabase<SkillDef>.GetNamedSilentFail(text));
                    break;
            }
        }

        // ---- hooks: hostility response and medical care of a pawn ---------------------------------------------------------------------

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void HookHostilityResponse(Pawn_PlayerSettings settings, HostilityResponseMode mode)
        {
            if (ShouldSync())
            {
                if (playerSettingsPawn(settings) is not { } pawn)
                    Dropped("hostility response: the settings have no pawn");
                else if (settings.hostilityResponse != mode)
                    SyncHostilityResponse(pawn, mode);
                return;
            }

            settings.hostilityResponse = mode;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void HookMedCare(Pawn_PlayerSettings settings, MedicalCareCategory care)
        {
            if (ShouldSync())
            {
                if (playerSettingsPawn(settings) is not { } pawn)
                    Dropped("medical care: the settings have no pawn");
                else if (settings.medCare != care)
                    SyncMedCare(pawn, care);
                return;
            }

            settings.medCare = care;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void SyncHostilityResponse(Pawn pawn, HostilityResponseMode mode)
        {
            if (active && pawn?.playerSettings != null)
                pawn.playerSettings.hostilityResponse = mode;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void SyncMedCare(Pawn pawn, MedicalCareCategory care)
        {
            if (active && pawn?.playerSettings != null)
                pawn.playerSettings.medCare = care;
        }

        // ---- group colours (path 1) ------------------------------------------------------------------------------------------------

        private static IEnumerable<object> AllGroups()
        {
            if (PawnGroupList is { } pawnGroups)
                foreach (var g in pawnGroups)
                    yield return g;
            if (CaravanDict is { } caravans)
                foreach (DictionaryEntry entry in caravans)
                    yield return entry.Value;
            if (ColonyDict is { } colonies)
                foreach (DictionaryEntry entry in colonies)
                    yield return entry.Value;
        }

        private static object FindGroup(Func<object, bool> test)
        {
            foreach (var g in AllGroups())
                if (g != null && test(g))
                    return g;
            return null;
        }

        // Dictionary<BodyColor, ColorOption>.set_Item(mode, option) in the colour picker (colour, ideology colour, pawn favourite)
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void HookBodyColorSet(object dict, int mode, object option)
        {
            // a dictionary that belongs to no group is not group state: it stays local
            if (ShouldSync() && FindGroup(g => fGroupColor.GetValue(g) is { } gc && ReferenceEquals(fBodyColors.GetValue(gc), dict)) is { } group)
            {
                if (option == null || !TryEncode(group, out var groupRef))
                    Dropped("the group colour could not be sent");
                else
                    SyncSetColor(groupRef, mode, (Color) fColorOptionColor.GetValue(option), (bool) fColorOptionFavorite.GetValue(option));
                return;
            }

            ((IDictionary) dict)[Enum.ToObject(tBodyColor, mode)] = option;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void SyncSetColor(GroupRef group, int mode, Color color, bool favorite)
        {
            if (!active || Decode(group) is not { } target)
                return;

            var groupColor = fGroupColor.GetValue(target);
            if (groupColor == null)
            {
                groupColor = Activator.CreateInstance(tGroupColor);
                fGroupColor.SetValue(target, groupColor);
            }

            if (fBodyColors.GetValue(groupColor) is not IDictionary dict)
            {
                dict = (IDictionary) Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(tBodyColor, tColorOption));
                fBodyColors.SetValue(groupColor, dict);
            }

            var option = Activator.CreateInstance(tColorOption);
            fColorOptionColor.SetValue(option, color);
            fColorOptionFavorite.SetValue(option, favorite);
            dict[Enum.ToObject(tBodyColor, mode)] = option;
        }

        // Dialog_ColorPicker.RemoveColor: also resets the wanted colour of apparel and hair and stops the recolour job of every pawn
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool RemoveColorPrefix(object __instance)
        {
            if (!ShouldSync())
                return true;

            try
            {
                var group = fMenuGroup.GetValue(__instance);
                if (!TryEncode(group, out var groupRef))
                {
                    Dropped("the colour removal: the group is not in the group lists");
                    return false;
                }

                SyncRemoveColor(groupRef, Convert.ToInt32(fPickerMode.GetValue(__instance)));
                return false;
            }
            catch (Exception e)
            {
                Dropped("the colour removal could not be sent", e);
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void SyncRemoveColor(GroupRef group, int mode)
        {
            if (!active || Decode(group) is not { } target)
                return;

            var key = Enum.ToObject(tBodyColor, mode);
            var groupColor = fGroupColor.GetValue(target);
            if (groupColor == null || fBodyColors.GetValue(groupColor) is not IDictionary dict || !dict.Contains(key))
                return;

            var option = dict[key];
            var modeName = Enum.GetName(tBodyColor, mode);
            foreach (var pawn in (GroupPawns(target) ?? []).ToList())
            {
                if (pawn == null)
                    continue;

                var color = (Color?) mColorOptionGetColor.Invoke(option, [pawn]);
                if (modeName == "Hair")
                {
                    if (pawn.style?.nextHairColor is { } next && color.HasValue && next == color.Value)
                    {
                        pawn.style.nextHairColor = null;
                        if (pawn.CurJobDef == JobDefOf.DyeHair)
                            pawn.jobs.StopAll();
                    }
                }
                else if (pawn.apparel?.WornApparel != null)
                {
                    foreach (var apparel in pawn.apparel.WornApparel)
                    {
                        if (CoversApparel(modeName, apparel.def) && apparel.DesiredColor is { } desired && color.HasValue && desired == color.Value)
                        {
                            apparel.DesiredColor = null;
                            if (pawn.CurJobDef == JobDefOf.RecolorApparel)
                                pawn.jobs.StopAll();
                        }
                    }
                }
            }

            dict.Remove(key);
        }

        private static bool CoversApparel(string mode, ThingDef def) => mode switch
        {
            "Head" => isHeadgear(def),
            "Feet" => isFeetGear(def),
            "Legs" => isLegsGear(def),
            "Torso" => isTorsoGear(def),
            "Hands" => isArmsGear(def),
            "All" => true,
            _ => false
        };

        // ---- group bills (path 3) --------------------------------------------------------------------------------------------------

        // The bill dialog: Multiplayer wraps the options of Dialog_BillConfig.GeneratePawnRestrictionOptions in a field watch (a pick
        // of the pawn restriction is sent as a field change). Colony Groups adds one option per pawn group to that menu in its own
        // postfix, after the watch was made. That option runs bill.SetAnyPawnRestriction() (a change of the restriction on this PC
        // alone, nobody watches it) and stores the group in a dictionary of this PC. And when a player picks "anyone" or a pawn later,
        // nothing removes the stored group, because a field command sets the field without calling SetPawnRestriction. This postfix
        // runs after both and changes every option of the menu:
        //  - a Colony Groups option sends SyncBillGroup(bill, group): the command clears the restriction and stores the group on every PC.
        //  - every other option runs as before (Multiplayer's watched field change) and then sends SyncBillGroup(bill, no group), so
        //    the stored group is removed on every PC (the order of the two commands is the order of the clicks).
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void BillOptionsPostfix(Bill ___bill, ref IEnumerable<Widgets.DropdownMenuElement<Pawn>> __result)
        {
            if (!active || !MP.IsInMultiplayer || __result == null || ___bill == null)
                return;

            try
            {
                var bill = ___bill;
                var list = __result.ToList();
                foreach (var element in list)
                {
                    var option = element.option;
                    if (option?.action == null)
                        continue;

                    var original = option.action;
                    if (ColonyGroupOfOption(original) is { } group)
                        option.action = () => PickBillGroup(bill, group, original);
                    else
                        option.action = () =>
                        {
                            original();
                            // only a bill that has a group needs the "no group" command
                            if (ShouldSync() && bill is Bill_Production billWithGroup &&
                                mBillsSelectedGroupGetter.Invoke(null, null) is IDictionary groupsOfBills && groupsOfBills.Contains(billWithGroup))
                                SendBillGroup(bill, default);
                        };
                }

                __result = list;
            }
            catch (Exception e)
            {
                Log.Error($"{LogPrefix}could not change the bill restriction menu, the group options stay local: {e}");
            }
        }

        // The Colony Groups option is a lambda of HarmonyPatches_GroupBills that captured the pawn group
        private static object ColonyGroupOfOption(Action action)
        {
            foreach (var d in action.GetInvocationList())
            {
                var declaring = d.Method.DeclaringType;
                var inGroupBills = false;
                for (var t = declaring; t != null; t = t.DeclaringType)
                    if (t == tGroupBills)
                        inGroupBills = true;
                if (!inGroupBills || d.Target == null)
                    continue;

                foreach (var field in d.Target.GetType().GetFields(AccessTools.all))
                    if (tPawnGroup.IsAssignableFrom(field.FieldType) && field.GetValue(d.Target) is { } group)
                        return group;
            }

            return null;
        }

        private static void PickBillGroup(Bill bill, object group, Action original)
        {
            if (!ShouldSync())
            {
                original();
                return;
            }

            if (!TryEncode(group, out var groupRef))
            {
                Dropped("the bill group: the group is not in the group lists");
                return;
            }

            SendBillGroup(bill, groupRef);
        }

        private static void SendBillGroup(Bill bill, GroupRef group)
        {
            try
            {
                SyncBillGroup(bill, group);
            }
            catch (Exception e)
            {
                Dropped("the bill group could not be sent", e);
            }
        }

        // group.kind 0 = no group. A group also clears the pawn restriction, as the Colony Groups option does.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void SyncBillGroup(Bill bill, GroupRef group)
        {
            if (!active || bill is not Bill_Production production || mBillsSelectedGroupGetter.Invoke(null, null) is not IDictionary dict)
                return;

            if (group.kind == 0)
            {
                if (dict.Contains(production))
                    dict.Remove(production);
                return;
            }

            if (Decode(group) is not { } target)
                return;

            production.SetAnyPawnRestriction();
            dict[production] = target;
        }

        // Colony Groups removes the selected group of a bill in a prefix of Bill.SetPawnRestriction / SetAnySlaveRestriction /
        // SetAnyPawnRestriction. That prefix is taken off (LateSetup) and replaced by this postfix, which skips the interface call:
        // the prefix ran in the interface too and changed the clicking PC alone. The postfix covers calls from code, not from the
        // menu (the menu is BillOptionsPostfix).
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void BillRestrictionPostfix(Bill __instance)
        {
            if (!active || (MP.IsInMultiplayer && MP.InInterface))
                return;

            if (__instance is Bill_Production production && mBillsSelectedGroupGetter.Invoke(null, null) is IDictionary dict && dict.Contains(production))
                dict.Remove(production);
        }

        // ---- group work priorities and manual priorities (path 2) -----------------------------------------------------------

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void HookWorkPrioritiesClear(Dictionary<WorkTypeDef, int> dict)
        {
            // a dictionary that belongs to no group is not group state: it stays local
            if (ShouldSync() && FindGroup(g => ReferenceEquals(fWorkPriorities.GetValue(g), dict)) is { } group)
            {
                if (!TryEncode(group, out var groupRef))
                    Dropped("clearing the group work priorities: the group is not in the group lists");
                else
                    SyncWorkPriorityClear(groupRef, "");
                return;
            }

            dict.Clear();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool HookWorkPrioritiesRemove(Dictionary<WorkTypeDef, int> dict, WorkTypeDef def)
        {
            if (ShouldSync() && FindGroup(g => ReferenceEquals(fWorkPriorities.GetValue(g), dict)) is { } group)
            {
                if (def == null || !TryEncode(group, out var groupRef))
                {
                    Dropped("removing a group work priority could not be sent");
                    return false;
                }

                SyncWorkPriorityClear(groupRef, def.defName);
                return dict.ContainsKey(def);
            }

            return dict.Remove(def);
        }

        // defName "" = remove every priority
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void SyncWorkPriorityClear(GroupRef group, string defName)
        {
            if (!active || Decode(group) is not { } target || fWorkPriorities.GetValue(target) is not Dictionary<WorkTypeDef, int> dict)
                return;

            if (string.IsNullOrEmpty(defName))
                dict.Clear();
            else if (DefDatabase<WorkTypeDef>.GetNamedSilentFail(defName) is { } def)
                dict.Remove(def);
        }

        // WorkMenu.DoManualPrioritiesCheckbox: the vanilla "manual priorities" game setting, drawn by the group work menu
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool ManualPrioritiesPrefix(Rect rect)
        {
            if (!ShouldSync())
                return true;

            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            var box = new Rect(rect.x + 5f, rect.y, 140f, 30f);
            var old = Current.Game.playSettings.useWorkPriorities;
            var value = old;
            Widgets.CheckboxLabeled(box, "ManualPriorities".Translate(), ref value);
            if (value != old)
                SyncManualPriorities(value);
            return false;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void SyncManualPriorities(bool value)
        {
            if (!active || Current.Game?.playSettings == null || Current.Game.playSettings.useWorkPriorities == value)
                return;

            Current.Game.playSettings.useWorkPriorities = value;
            foreach (var pawn in PawnsFinder.AllMapsWorldAndTemporary_Alive)
                if (pawn.Faction == Faction.OfPlayer && pawn.workSettings != null)
                    pawn.workSettings.Notify_UseWorkPrioritiesChanged();
        }

        // ---- "search for work" buttons ---------------------------------------------------------------------------------------------
        // WorkSearchUtility.SearchForWork(WorkTypeEnum, ...) is 51 bytes, so the JIT can inline it into its callers and past Multiplayer's
        // check. The callers (group menus) are rewritten to these hooks, which send SyncSearchForWork, one command per map.

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void HookSearchForWorkDef(WorkTypeDef def, List<Pawn> pawns) => SearchForWork(def, 0, false, pawns);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void HookSearchForWorkEnum(int value, List<Pawn> pawns) => SearchForWork(null, value, true, pawns);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SearchForWork(WorkTypeDef def, int value, bool isEnum, List<Pawn> pawns)
        {
            if (!ShouldSync())
            {
                if (isEnum)
                    CallUnwrapped(mSearchEnum, null, Enum.ToObject(workTypeEnumType, value), pawns);
                else
                    CallUnwrapped(mSearchDef, null, def, pawns);
                return;
            }

            try
            {
                if (pawns == null || (!isEnum && def == null))
                {
                    Dropped("search for work has no pawns or no work type");
                    return;
                }

                foreach (var (map, batch) in SplitByMap(pawns))
                    SyncSearchForWork(batch, def?.defName ?? "", value, isEnum, map);
            }
            catch (Exception e)
            {
                Dropped("search for work could not be sent", e);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void SyncSearchForWork(List<Pawn> pawns, string defName, int value, bool isEnum, Map map)
        {
            if (!active || pawns == null)
                return;

            var list = pawns.Where(p => p != null).ToList();
            try
            {
                if (isEnum)
                    mSearchEnum.Invoke(null, [Enum.ToObject(workTypeEnumType, value), list]);
                else if (DefDatabase<WorkTypeDef>.GetNamedSilentFail(defName) is { } def)
                    mSearchDef.Invoke(null, [def, list]);
            }
            catch (TargetInvocationException e)
            {
                Log.Error($"{LogPrefix}search for work failed: {e.InnerException}");
            }
        }

        // ---- the per pawn group cache (paths 1 and 2 read it in the simulation) ----------------------------------------------------

        // TacticalGroups keeps TacticUtils.pawnsWithGroups (pawn -> HashSet of groups), a cache that is not saved. Its order follows the
        // order the groups were added in this process (the host: in play order, a joined PC: the order of the save file), and
        // ColonistGroup.Disband(Pawn) does not take the group out of it. The group colour (first group that has a colour wins) and the
        // group work states read it in the simulation, so before they do it is rebuilt from the real group lists in a fixed order:
        // pawn groups by id, then colony groups by map id, then caravan groups by caravan id.
        private static Type hashSetType;
        private static MethodInfo hashSetAdd;

        // foreach over an IDictionary gives DictionaryEntry. Cast<DictionaryEntry>() would go through IEnumerable, where a generic
        // Dictionary yields KeyValuePair, and throw InvalidCastException.
        private static IEnumerable<DictionaryEntry> Entries(IDictionary dict)
        {
            foreach (DictionaryEntry entry in dict)
                yield return entry;
        }

        private static IEnumerable<object> GroupsCanonical()
        {
            if (PawnGroupList is { } pawnGroups)
            {
                var items = new List<(int id, int index, object group)>();
                for (var i = 0; i < pawnGroups.Count; i++)
                    items.Add((GroupId(pawnGroups[i]), i, pawnGroups[i]));
                foreach (var item in items.OrderBy(x => x.id).ThenBy(x => x.index))
                    yield return item.group;
            }

            if (ColonyDict is { } colonies)
                foreach (var entry in Entries(colonies).Where(e => e.Key is Map && e.Value != null).OrderBy(e => ((Map) e.Key).uniqueID))
                    yield return entry.Value;

            if (CaravanDict is { } caravans)
                foreach (var entry in Entries(caravans).Where(e => e.Key is Caravan && e.Value != null).OrderBy(e => ((Caravan) e.Key).ID))
                    yield return entry.Value;
        }

        private static void NormalizePawn(Pawn pawn)
        {
            // the three callers are job hooks of Colony Groups that act on humanlike pawns of the player (colonists, slaves, prisoners)
            if (!active || !MP.IsInMultiplayer || pawn == null || !pawn.RaceProps.Humanlike
                || (pawn.Faction != Faction.OfPlayer && pawn.HostFaction != Faction.OfPlayer))
                return;

            try
            {
                if (fPawnsWithGroups.GetValue(null) is not IDictionary cache)
                    return;

                var wanted = GroupsCanonical().Where(g => GroupPawns(g) is { } list && list.Contains(pawn)).ToList();
                var current = cache.Contains(pawn) ? cache[pawn] as IEnumerable : null;
                if (current == null && wanted.Count == 0)
                    return;
                if (current != null && current.Cast<object>().SequenceEqual(wanted, ReferenceEqualityComparer.Instance))
                    return;

                var set = Activator.CreateInstance(hashSetType);
                foreach (var g in wanted)
                    hashSetAdd.Invoke(set, [g]);
                cache[pawn] = set;
            }
            catch (Exception e)
            {
                Log.Warning($"{LogPrefix}could not rebuild the group cache of {pawn}: {e.Message}");
            }
        }

        // TacticalGroups' TryCreateRecolorJob prefix reads HarmonyPatches.cachedComps and skips an apparel the first time it sees it in
        // this process ("not cached yet" leaves the value null). A PC that has seen the apparel before does not skip it, so the cache is
        // filled first.
        private static void FillComps(Pawn pawn)
        {
            if (fCachedComps.GetValue(null) is not IDictionary cache || pawn?.apparel?.WornApparel == null)
                return;

            foreach (var apparel in pawn.apparel.WornApparel)
                if (!cache.Contains(apparel))
                    cache[apparel] = apparel.TryGetComp<CompColorable>();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void EndCurrentJobPrefix(Pawn ___pawn) => NormalizePawn(___pawn);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RecolorJobPrefix(Pawn pawn)
        {
            if (!active || !MP.IsInMultiplayer)
                return;
            NormalizePawn(pawn);
            try
            {
                FillComps(pawn);
            }
            catch (Exception e)
            {
                Log.Warning($"{LogPrefix}could not fill the apparel colour cache: {e.Message}");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void DyeHairJobPrefix(Pawn pawn) => NormalizePawn(pawn);

        // ---- install: look up every member first ---------------------------------------------------------------------------------------

        private static Type FindType(string name)
            => AccessTools.TypeByName(TgNamespace + name) ?? throw new TypeLoadException($"type {TgNamespace}{name} not found");

        private static FieldInfo Fld(Type type, string name)
            => AccessTools.Field(type, name) ?? throw new MissingFieldException(type.FullName, name);

        private static Func<ThingDef, bool> ThingDefTest(Type type, string name)
            => (Func<ThingDef, bool>) Delegate.CreateDelegate(typeof(Func<ThingDef, bool>), Method(type, name, typeof(ThingDef)));

        private static Type workTypeEnumType, workSearchType;

        private static void Lookup()
        {
            tGroup = FindType("ColonistGroup");
            tPawnGroup = FindType("PawnGroup");
            tColonyGroup = FindType("ColonyGroup");
            tCaravanGroup = FindType("CaravanGroup");
            tTacticalGroups = FindType("TacticalGroups");
            tUtils = FindType("TacticUtils");
            tBodyColor = FindType("BodyColor");
            tColorOption = FindType("ColorOption");
            tGroupColor = FindType("GroupColor");
            tGroupBills = FindType("HarmonyPatches_GroupBills");
            tSortBy = FindType("SortBy");
            workTypeEnumType = FindType("WorkTypeEnum");
            workSearchType = FindType("WorkSearchUtility");
            var tFormer = FindType("FormerGroup");
            var tPicker = FindType("Dialog_ColorPicker");
            var tMenu = FindType("TieredFloatMenu");
            var tHarmony = FindType("HarmonyPatches");
            var tBar = FindType("TacticalColonistBar");
            var tApparelUtils = FindType("ApparelUtils");

            fPawns = Fld(tGroup, "pawns");
            fGroupId = Fld(tGroup, "groupID");
            fWorkPriorities = Fld(tGroup, "groupWorkPriorities");
            fGroupColor = Fld(tGroup, "groupColor");
            fBodyColors = Fld(tGroupColor, "bodyColors");
            fGroupArea = Fld(tGroup, "groupArea");
            fDrugPolicy = Fld(tGroup, "groupDrugPolicy");
            fApparelPolicy = Fld(tGroup, "groupApparelPolicy");
            fFoodPolicy = Fld(tGroup, "groupFoodPolicy");
            fAutoDisband = Fld(tPawnGroup, "autoDisbandWithoutPawns");
            fActiveSortBy = Fld(tGroup, "activeSortBy");
            fSkillDefSort = Fld(tGroup, "skillDefSort");
            fColorOptionColor = Fld(tColorOption, "color");
            fColorOptionFavorite = Fld(tColorOption, "pawnFavoriteOnly");
            fMenuGroup = Fld(tMenu, "colonistGroup");
            fPickerMode = Fld(tPicker, "activeMode");
            fPawnGroupsList = Fld(tTacticalGroups, "pawnGroups");
            fCaravanGroupsDict = Fld(tTacticalGroups, "caravanGroups");
            fColonyGroupsDict = Fld(tTacticalGroups, "colonyGroups");
            fPawnsWithGroups = Fld(tUtils, "pawnsWithGroups");
            fCachedComps = Fld(tHarmony, "cachedComps");
            fFormerGroups = Fld(tCaravanGroup, "formerGroups");
            fFormerPawnGroups = Fld(tFormer, "pawnGroups");
            fColonistBar = Fld(tUtils, "TacticalColonistBar");
            mMarkColonistsDirty = Method(tBar, "MarkColonistsDirty");
            mAddGroup = Method(tTacticalGroups, "AddGroup", typeof(List<Pawn>));
            mUpdateData = Method(tGroup, "UpdateData");
            mSearchDef = Method(workSearchType, "SearchForWork", typeof(WorkTypeDef), typeof(List<Pawn>));
            mSearchEnum = Method(workSearchType, "SearchForWork", workTypeEnumType, typeof(List<Pawn>));
            mColorOptionGetColor = Method(tColorOption, "GetColor", typeof(Pawn));
            mBillsSelectedGroupGetter = AccessTools.PropertyGetter(tGroupBills, "BillsSelectedGroup")
                                        ?? throw new MissingMethodException(tGroupBills.FullName, "get_BillsSelectedGroup");
            isHeadgear = ThingDefTest(tApparelUtils, "IsHeadgear");
            isFeetGear = ThingDefTest(tApparelUtils, "IsFeetGear");
            isLegsGear = ThingDefTest(tApparelUtils, "IsLegsGear");
            isTorsoGear = ThingDefTest(tApparelUtils, "IsTorsoGear");
            isArmsGear = ThingDefTest(tApparelUtils, "IsArmsGear");
            playerSettingsPawn = AccessTools.FieldRefAccess<Pawn_PlayerSettings, Pawn>("pawn");
            hashSetType = typeof(HashSet<>).MakeGenericType(tGroup);
            hashSetAdd = hashSetType.GetMethod("Add") ?? throw new MissingMethodException(hashSetType.FullName, "Add");

            // the fields must have the shapes the code relies on. The static fields are not read here: reading one would run the
            // static constructor of a Colony Groups class before the game has loaded its textures and defs.
            if (!typeof(IDictionary).IsAssignableFrom(fPawnsWithGroups.FieldType) || !fPawnsWithGroups.IsStatic)
                throw new InvalidOperationException("TacticUtils.pawnsWithGroups is not a static dictionary");
            if (!typeof(IDictionary).IsAssignableFrom(fCachedComps.FieldType) || !fCachedComps.IsStatic)
                throw new InvalidOperationException("HarmonyPatches.cachedComps is not a static dictionary");
            if (!typeof(IList).IsAssignableFrom(fPawnGroupsList.FieldType) || !typeof(IDictionary).IsAssignableFrom(fCaravanGroupsDict.FieldType)
                || !typeof(IDictionary).IsAssignableFrom(fColonyGroupsDict.FieldType))
                throw new InvalidOperationException("the group collections of the world component have another type");
        }

        // ---- install: patch and register -----------------------------------------------------------------------------------------------

        // every patch the module made, so a failed feature can take its patches off again
        private static readonly List<(MethodBase original, MethodInfo patch)> applied = [];
        private static readonly List<string> installed = [], failed = [];

        private static void PatchMethod(MethodBase original, string prefix = null, string postfix = null, string transpiler = null,
            int priority = Priority.Normal)
        {
            HarmonyMethod Make(string name) => name == null ? null : new HarmonyMethod(typeof(ColonyGroups), name) { priority = priority };

            var pre = Make(prefix);
            var post = Make(postfix);
            var trans = Make(transpiler);
            MpCompat.harmony.Patch(original, pre, post, trans);
            foreach (var h in new[] { pre, post, trans })
                if (h != null)
                    applied.Add((original, h.method));
        }

        private static void TakeOff(int mark)
        {
            for (var i = applied.Count - 1; i >= mark; i--)
            {
                try
                {
                    MpCompat.harmony.Unpatch(applied[i].original, applied[i].patch);
                }
                catch (Exception)
                {
                    // nothing left to undo
                }

                applied.RemoveAt(i);
            }
        }

        // one feature = one group of buttons: when it cannot be installed its patches come off and those buttons stay local
        private static void Feature(string name, Action install)
        {
            var mark = applied.Count;
            try
            {
                install();
                installed.Add(name);
            }
            catch (Exception e)
            {
                TakeOff(mark);
                failed.Add(name);
                Log.Error($"{LogPrefix}feature '{name}' could not be installed and was taken off again, its buttons stay local: {e}");
            }
        }

        private static bool IsCallOpcode(CodeInstruction ci) => ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt;

        // a call of a method of a closed generic type, e.g. Dictionary<BodyColor, ColorOption>.set_Item
        private static Func<CodeInstruction, bool> GenericCall(Type definition, string name, int parameterCount, params Type[] typeArgs)
            => ci => IsCallOpcode(ci) && ci.operand is MethodInfo m && m.Name == name && m.GetParameters().Length == parameterCount
                     && m.DeclaringType is { IsGenericType: true } declaring && declaring.GetGenericTypeDefinition() == definition
                     && declaring.GetGenericArguments().SequenceEqual(typeArgs);

        // a store into a field, or a call of its setter when the game has a property of that name
        private static Func<CodeInstruction, bool> StoreMatcher(Type type, string member)
        {
            var field = AccessTools.Field(type, member);
            if (field != null)
                return ci => IsStore(ci, field);

            var setter = AccessTools.PropertySetter(type, member) ?? throw new MissingMemberException(type.FullName, member);
            return ci => IsCall(ci, setter);
        }

        // a call of one known method (the call sites of a tiny method that must not be patched itself)
        private static Func<CodeInstruction, bool> CallOf(MethodInfo method) => ci => IsCall(ci, method);

        private static readonly string[] SearchCallers = ["CaravanMenu", "OrderMenu", "WorkMenu", "WorkSearchUtility"];

        private static void RegisterSyncMethods()
        {
            foreach (var name in new[]
                     {
                         nameof(SyncGroupCall), nameof(SyncAddGroup), nameof(SyncMoveGroup), nameof(SyncSetField), nameof(SyncSetColor),
                         nameof(SyncRemoveColor), nameof(SyncWorkPriorityClear), nameof(SyncManualPriorities), nameof(SyncSearchForWork)
                     })
                MP.RegisterSyncMethod(typeof(ColonyGroups), name);

            MP.RegisterSyncMethod(typeof(ColonyGroups), nameof(SyncHostilityResponse)).CancelIfAnyArgNull();
            MP.RegisterSyncMethod(typeof(ColonyGroups), nameof(SyncMedCare)).CancelIfAnyArgNull();
            MP.RegisterSyncMethod(typeof(ColonyGroups), nameof(SyncBillGroup)).CancelIfAnyArgNull();
            MP.RegisterSyncMethod(typeof(ColonyGroups), nameof(SyncMovePawn)).CancelIfAnyArgNull();
        }

        // ---- late setup: Colony Groups makes its own Harmony patches after this module is built -----------------------------------------

        // Colony Groups removes the selected group of a bill in a prefix of Bill.SetPawnRestriction, SetAnySlaveRestriction and
        // SetAnyPawnRestriction (HarmonyPatches_GroupBills.SetPawnRestriction). That prefix runs in the interface, so it changed the
        // clicking PC before the command ran. BillRestrictionPostfix does the same after the method, outside the interface only.
        // The Colony Groups prefix is taken off once its patches exist (after every static constructor, and again at game start).
        private static MethodInfo tgBillPrefix;
        private static readonly List<MethodBase> billRestrictionMethods = [];

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void LateSetup()
        {
            if (!active || tgBillPrefix == null)
                return;

            try
            {
                foreach (var method in billRestrictionMethods)
                {
                    if (Harmony.GetPatchInfo(method)?.Prefixes.Any(p => p.PatchMethod == tgBillPrefix) != true)
                        continue;

                    MpCompat.harmony.Unpatch(method, tgBillPrefix);
                    if (Harmony.GetPatchInfo(method)?.Prefixes.Any(p => p.PatchMethod == tgBillPrefix) == true)
                        Log.Error($"{LogPrefix}the Colony Groups prefix on {method.Name} is still there after Unpatch, the bill group choice may desync");
                }
            }
            catch (Exception e)
            {
                Log.Error($"{LogPrefix}could not take the Colony Groups bill prefix off: {e}");
            }
        }

        // ---- the module ------------------------------------------------------------------------------------------------------------------

        public ColonyGroups(ModContentPack mod)
        {
            // 1. look every member up first: patch and register nothing when one is missing
            try
            {
                Lookup();
            }
            catch (Exception e)
            {
                Log.Error($"{LogPrefix}this Colony Groups build does not have what the module syncs, nothing patched or registered, the group buttons stay local: {e}");
                throw new InvalidOperationException($"{LogPrefix}unsupported Colony Groups build", e);
            }

            // 2. the core: group identity and the group calls
            try
            {
                MP.RegisterSyncWorker<GroupRef>(SyncGroupRef);
                var targets = new List<MethodInfo>();
                BuildCallTable(targets);
                if (callEntries.Count == 0)
                    throw new InvalidOperationException("no group method found to sync");
                RegisterSyncMethods();
                foreach (var target in targets)
                    PatchMethod(target, callEntries[callIds[MethodKey(target)]].isStatic ? nameof(GroupCallPrefixStatic) : nameof(GroupCallPrefix),
                        priority: Priority.First);
            }
            catch (Exception e)
            {
                TakeOff(0);
                Log.Error($"{LogPrefix}could not patch or register the group calls, patches taken off again, the group buttons stay local: {e}");
                throw new InvalidOperationException($"{LogPrefix}patch failed", e);
            }

            installed.Add("group calls (" + callEntries.Count + " methods)");

            // 3. the other features, each one on its own
            Feature("group id and map", () =>
            {
                PatchMethod(Method(tPawnGroup, "CreateGroupID"), postfix: nameof(CreateGroupIdPostfix));
                PatchMethod(AccessTools.DeclaredPropertyGetter(tPawnGroup, "Map") ?? throw new MissingMethodException(tPawnGroup.FullName, "get_Map"),
                    postfix: nameof(PawnGroupMapPostfix));
            });

            Feature("rename dialog", () =>
            {
                if (setNameId < 0)
                    throw new InvalidOperationException("ColonistGroup.SetName is not a group call");
                PatchMethod(Method(FindType("Dialog_RenameColonistGroup"), "SetName", typeof(string)), nameof(RenameDialogPrefix), priority: Priority.First);
            });

            Feature("presets blocked", () =>
                PatchMethod(Method(tGroup, "ActivatePreset", FindType("GroupPreset")), nameof(ActivatePresetPrefix), priority: Priority.First));

            Feature("group cache", () =>
            {
                PatchMethod(AccessTools.Method(typeof(Pawn_JobTracker), "EndCurrentJob") ?? throw new MissingMethodException("Pawn_JobTracker", "EndCurrentJob"),
                    nameof(EndCurrentJobPrefix), priority: Priority.First);
                // Ideology jobs: the methods exist in the game assembly, the check is for a game without them
                if (AccessTools.Method(typeof(JobGiver_OptimizeApparel), "TryCreateRecolorJob") is { } recolor)
                    PatchMethod(recolor, nameof(RecolorJobPrefix), priority: Priority.First);
                if (AccessTools.Method(typeof(JobGiver_DyeHair), "TryGiveJob") is { } dye)
                    PatchMethod(dye, nameof(DyeHairJobPrefix), priority: Priority.First);
            });

            Feature("button rewrites", InstallRewrites);

            Feature("colour removal", () =>
                PatchMethod(Method(FindType("Dialog_ColorPicker"), "RemoveColor"), nameof(RemoveColorPrefix), priority: Priority.First));

            Feature("manual priorities", () =>
                PatchMethod(Method(FindType("WorkMenu"), "DoManualPrioritiesCheckbox", typeof(Rect)), nameof(ManualPrioritiesPrefix), priority: Priority.First));

            // ColonistGroup.Sort stays TG's own: the game sets the en-US culture on every PC (CultureInfoUtility, from Root), and
            // the pawn list order it starts from is synced (arrows in ColonistGroup and ManagementMenu). A prefix on the 92 B Sort,
            // whose hot caller is the world tick, could be inlined past on some PCs only and sort two ways.

            Feature("bill restriction", () =>
            {
                var optionsMethod = AccessTools.Method(typeof(Dialog_BillConfig), "GeneratePawnRestrictionOptions")
                                    ?? throw new MissingMethodException("Dialog_BillConfig", "GeneratePawnRestrictionOptions");
                PatchMethod(optionsMethod, postfix: nameof(BillOptionsPostfix), priority: Priority.Last);

                var tgPrefix = AccessTools.DeclaredMethod(tGroupBills, "SetPawnRestriction", [typeof(Bill)])
                               ?? throw new MissingMethodException(tGroupBills.FullName, "SetPawnRestriction");
                var methods = new List<MethodBase>();
                foreach (var name in new[] { "SetPawnRestriction", "SetAnySlaveRestriction", "SetAnyPawnRestriction" })
                    methods.Add(AccessTools.Method(typeof(Bill), name) ?? throw new MissingMethodException("Bill", name));
                var callAll = AccessTools.Method(typeof(StaticConstructorOnStartupUtility), nameof(StaticConstructorOnStartupUtility.CallAll))
                              ?? throw new MissingMethodException("StaticConstructorOnStartupUtility", "CallAll");
                var finalize = AccessTools.Method(typeof(Game), nameof(Game.FinalizeInit)) ?? throw new MissingMethodException("Game", "FinalizeInit");

                foreach (var method in methods)
                    PatchMethod(method, postfix: nameof(BillRestrictionPostfix));
                PatchMethod(callAll, postfix: nameof(LateSetup));
                PatchMethod(finalize, postfix: nameof(LateSetup));

                // only now does LateSetup have something to do: the postfix that replaces the Colony Groups prefix is in place
                tgBillPrefix = tgPrefix;
                billRestrictionMethods.AddRange(methods);
            });

            active = true;
            LateSetup();
            Log.Message($"{LogPrefix}installed: {string.Join(", ", installed)}."
                        + (failed.Count > 0 ? $" NOT installed (those buttons stay local): {string.Join(", ", failed)}." : "")
                        + " Rewrites: " + string.Join(", ", rewrites.Select(r => $"{r.name}={r.count}")) + ".");
        }

        private static void InstallRewrites()
        {
            var settings = typeof(Pawn_PlayerSettings);
            AddRewrite("group area", ["AreaAllowedGUIGroup"], StoreMatcher(tGroup, "groupArea"), nameof(HookGroupArea));
            AddRewrite("group drug policy", ["DrugPolicyUIUtilityGroup"], StoreMatcher(tGroup, "groupDrugPolicy"), nameof(HookGroupDrugPolicy));
            AddRewrite("group apparel policy", ["ManagementMenu"], StoreMatcher(tGroup, "groupApparelPolicy"), nameof(HookGroupApparelPolicy));
            AddRewrite("group food policy", ["ManagementMenu"], StoreMatcher(tGroup, "groupFoodPolicy"), nameof(HookGroupFoodPolicy));
            AddRewrite("auto disband", ["ManageMenu"], StoreMatcher(tPawnGroup, "autoDisbandWithoutPawns"), nameof(HookAutoDisband));
            AddRewrite("sort by", ["SortMenu"], StoreMatcher(tGroup, "activeSortBy"), nameof(HookActiveSortBy));
            AddRewrite("sort skill", ["SortMenu"], StoreMatcher(tGroup, "skillDefSort"), nameof(HookSkillDefSort));
            AddRewrite("hostility response", ["HostilityResponseModeUtilityGroup", "TacticUtils"], StoreMatcher(settings, "hostilityResponse"),
                nameof(HookHostilityResponse));
            AddRewrite("medical care", ["MedicalCareUtilityGroup"], StoreMatcher(settings, "medCare"), nameof(HookMedCare));
            AddRewrite("group colour", ["Dialog_ColorPicker"], GenericCall(typeof(Dictionary<,>), "set_Item", 2, tBodyColor, tColorOption),
                nameof(HookBodyColorSet));
            AddRewrite("add group", ["TacticalColonistBar"], CallOf(mAddGroup), nameof(HookAddGroup));
            AddRewrite("init sort", ["SortMenu"], CallOf(mInitSort), nameof(HookInitSort));
            AddRewrite("reset group policies", ["Dialog_ResetGroup"], CallOf(mResetPolicies), nameof(HookResetGroupPolicies));
            AddRewrite("search for work (def)", SearchCallers, CallOf(mSearchDef), nameof(HookSearchForWorkDef));
            AddRewrite("search for work (enum)", SearchCallers, CallOf(mSearchEnum), nameof(HookSearchForWorkEnum));
            // the in-group pawn arrows: ColonistGroup.DrawPawnArrows and ManagementMenu.DrawPawnArrows (their only List<Pawn> RemoveAt / Insert calls)
            AddRewrite("pawn list remove", ["ColonistGroup", "ManagementMenu"], GenericCall(typeof(List<>), "RemoveAt", 1, typeof(Pawn)), nameof(HookPawnListRemoveAt));
            AddRewrite("pawn list insert", ["ColonistGroup", "ManagementMenu"], GenericCall(typeof(List<>), "Insert", 2, typeof(Pawn)), nameof(HookPawnListInsert));
            AddRewrite("work priorities clear", ["WorkMenu", "WidgetsWorkGroup"],
                GenericCall(typeof(Dictionary<,>), "Clear", 0, typeof(WorkTypeDef), typeof(int)), nameof(HookWorkPrioritiesClear));
            AddRewrite("work priorities remove", ["WorkMenu", "WidgetsWorkGroup"],
                GenericCall(typeof(Dictionary<,>), "Remove", 1, typeof(WorkTypeDef), typeof(int)), nameof(HookWorkPrioritiesRemove));
            AddRewrite("group list remove", ["PawnGroup"], GenericCall(typeof(List<>), "RemoveAt", 1, tPawnGroup), nameof(HookListRemoveAt));
            AddRewrite("group list insert", ["PawnGroup"], GenericCall(typeof(List<>), "Insert", 2, tPawnGroup), nameof(HookListInsert));

            var targets = FindRewriteTargets();
            if (targets.Count == 0)
                throw new InvalidOperationException("no button was found to rewrite");

            foreach (var target in targets)
                PatchMethod(target, transpiler: nameof(RewriteTranspiler));
        }
    }
}
