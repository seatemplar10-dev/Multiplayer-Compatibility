using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace Multiplayer.Compat;

/// <summary>Custom Quest Framework by HaiLuan</summary>
/// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=2978572782"/>
[MpCompatFor("HaiLuan.CustomQuestFramework")]
public class CustomQuestFramework
{
    // FIXES row 278. Two problems, both in QuestEditor_Library (1.7/Assemblies/net10.0):
    // 1. Float menu lambdas start jobs from interface code: [pawn.jobs.StopAll(false, true);] then
    //    pawn.jobs.StartJob(job, ...) with a job made in the menu (local negative id, one PC only).
    //    QE_StartDialog (FloatMenuOptionProvider_Dialog), QE_InteractingWithTarget (FloatMenuOptionProvider_ExtraOperation,
    //    InteractableThing), QE_DisarmTrap (CustomTrap_Capture), QE_Open (LootBox; its shift-click branch uses
    //    TryTakeOrderedJob, which MP syncs already). All five call StartJob with the same arguments.
    //    Fix: in those lambdas, StopAll + StartJob go to a synced method that rebuilds the job on every PC.
    // 2. QE_StartDialog's driver opens CQFDialogTreeWindow (a plain Window, one per PC). A click on an option runs
    //    the option's CQFAction.Work list (quest signals, rewards, goodwill, spawns...), RemoveDialog and
    //    ConsumeRequiredThings from DoWindowContents, on the clicking PC only.
    //    Fix: the option actions only send a synced choice (things, tree defName, quest id, node key, option index).
    //    Every PC rebuilds that node's options fresh (DialogOption.GetDEOptions) and runs the chosen one, then moves
    //    its own open window (if any) to the next node. DialogTreeDef is synced by defName: CQF adds these defs at
    //    startup (CQFQuestDefBootstrap), after short hashes are given, so MP's def-by-short-hash sync is unsafe.
    private static readonly string[] JobMenuTypes =
    [
        "QuestEditor_Library.FloatMenuOptionProvider_Dialog",
        "QuestEditor_Library.FloatMenuOptionProvider_ExtraOperation",
        "QuestEditor_Library.InteractableThing",
        "QuestEditor_Library.CustomTrap_Capture",
        "QuestEditor_Library.LootBox",
    ];

    private static MethodInfo startJobFromMenuMethod;
    private static MethodInfo stopAllFromMenuMethod;
    // The pawn whose StopAll the current menu lambda skipped (the synced job start does it on every PC).
    private static Pawn pendingStopAllPawn;

    // CQFDialogTreeWindow and the types it uses
    private static Type dialogWindowType;
    private static Type dialogTreeDefType;
    private static Type dialogTextElementType;
    private static FieldInfo windowInterviewerField, windowIntervieweeField, windowQuestField, windowTreeField;
    private static FieldInfo windowCurNodeField, windowNextOptionsField, windowElementsField;
    private static MethodInfo windowGoToNodeMethod;
    private static FieldInfo treeNodeMouldsField;
    private static MethodInfo treeResolveOptionsMethod;
    private static FieldInfo nodeOptionsField;
    private static MethodInfo optionGetDEOptionsMethod;
    private static FieldInfo elementActionField, elementDisabledField, elementNextIndexField, elementTextField;
    // Window -> real time of the last choice sent from it; a second click is ignored until the choice comes back.
    private static readonly Dictionary<Window, float> choiceSentAt = new();
    private const float ChoiceTimeoutSeconds = 3f;

    public CustomQuestFramework(ModContentPack mod)
    {
        PatchJobMenus();
        PatchDialogWindow();
        MP.RegisterSyncMethod(typeof(CustomQuestFramework), nameof(SyncedStartJob));
        MP.RegisterSyncMethod(typeof(CustomQuestFramework), nameof(SyncedChooseOption));
        RegisterGizmos();
    }

    #region Jobs from float menus

    private static void PatchJobMenus()
    {
        startJobFromMenuMethod = AccessTools.DeclaredMethod(typeof(CustomQuestFramework), nameof(StartJobFromMenu));
        stopAllFromMenuMethod = AccessTools.DeclaredMethod(typeof(CustomQuestFramework), nameof(StopAllFromMenu));
        var prefix = new HarmonyMethod(typeof(CustomQuestFramework), nameof(PreMenuLambda));
        var transpiler = new HarmonyMethod(typeof(CustomQuestFramework), nameof(ReplaceJobTrackerCalls));
        var patched = new List<string>();

        foreach (var typeName in JobMenuTypes)
        {
            var type = AccessTools.TypeByName(typeName);
            if (type == null)
            {
                Log.Error($"MPCompat :: CustomQuestFramework: type {typeName} not found, its float menu jobs stay unsynced");
                continue;
            }

            // The option actions are lambdas in closure classes nested in the type (also inside iterator methods)
            foreach (var method in AllNestedTypes(type).SelectMany(t => t.GetMethods(AccessTools.allDeclared)))
            {
                if (!method.Name.Contains("b__") || method.IsAbstract)
                    continue;
                if (!PatchProcessor.ReadMethodBody(method).Any(op => IsJobTrackerCall(op.Value, nameof(Pawn_JobTracker.StartJob))))
                    continue;
                MpCompat.harmony.Patch(method, prefix: prefix, transpiler: transpiler);
                patched.Add($"{method.DeclaringType?.FullName}:{method.Name}");
            }
        }

        // Decompile of CQF for 1.7 (28 Oct 2026): one StartJob lambda per type, 5 in all
        if (patched.Count != JobMenuTypes.Length)
            Log.Warning($"MPCompat :: CustomQuestFramework: expected {JobMenuTypes.Length} float menu job lambdas, patched {patched.Count}: {patched.ToStringSafeEnumerable()}");
    }

    private static IEnumerable<Type> AllNestedTypes(Type type)
        => type.GetNestedTypes(AccessTools.all).SelectMany(nested => AllNestedTypes(nested).Prepend(nested));

    private static bool IsJobTrackerCall(object operand, string name)
        => operand is MethodInfo method && method.DeclaringType == typeof(Pawn_JobTracker) && method.Name == name;

    private static void PreMenuLambda() => pendingStopAllPawn = null;

    private static IEnumerable<CodeInstruction> ReplaceJobTrackerCalls(IEnumerable<CodeInstruction> instr, MethodBase original)
    {
        foreach (var ci in instr)
        {
            MethodInfo replacement = null;
            if (IsJobTrackerCall(ci.operand, nameof(Pawn_JobTracker.StartJob)))
                replacement = startJobFromMenuMethod;
            else if (IsJobTrackerCall(ci.operand, nameof(Pawn_JobTracker.StopAll)))
                replacement = stopAllFromMenuMethod;

            if (replacement != null)
            {
                var target = (MethodInfo)ci.operand;
                var expected = target.GetParameters().Select(p => p.ParameterType).Prepend(typeof(Pawn_JobTracker));
                if (replacement.GetParameters().Select(p => p.ParameterType).SequenceEqual(expected) && replacement.ReturnType == target.ReturnType)
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = replacement;
                }
                else
                    Log.Error($"MPCompat :: CustomQuestFramework: {target.Name} signature changed, {original.DeclaringType?.FullName}:{original.Name} stays unsynced");
            }

            yield return ci;
        }
    }

    private static void StopAllFromMenu(Pawn_JobTracker tracker, bool ifLayingKeepLaying, bool canReturnToPool)
    {
        if (MP.IsInMultiplayer && MP.InInterface)
        {
            // Done inside SyncedStartJob on every PC
            pendingStopAllPawn = tracker.pawn;
            return;
        }

        tracker.StopAll(ifLayingKeepLaying, canReturnToPool);
    }

    private static void StartJobFromMenu(Pawn_JobTracker tracker, Job newJob, JobCondition lastJobEndCondition, ThinkNode jobGiver,
        bool resumeCurJobAfterwards, bool cancelBusyStances, ThinkTreeDef thinkTree, JobTag? tag, bool fromQueue,
        bool canReturnCurJobToPool, bool? keepCarryingThingOverride, bool continueSleeping, bool addToJobsThisTick,
        bool preToilReservationsCanFail)
    {
        if (MP.IsInMultiplayer && MP.InInterface)
        {
            var stopAll = pendingStopAllPawn != null && pendingStopAllPawn == tracker.pawn;
            pendingStopAllPawn = null;
            if (newJob != null)
                SyncedStartJob(tracker.pawn, newJob.def, newJob.targetA, newJob.targetB, newJob.targetC, newJob.count, newJob.reportStringOverride, stopAll);
            return;
        }

        tracker.StartJob(newJob, lastJobEndCondition, jobGiver, resumeCurJobAfterwards, cancelBusyStances, thinkTree, tag,
            fromQueue, canReturnCurJobToPool, keepCarryingThingOverride, continueSleeping, addToJobsThisTick, preToilReservationsCanFail);
    }

    private static void SyncedStartJob(Pawn pawn, JobDef def, LocalTargetInfo targetA, LocalTargetInfo targetB, LocalTargetInfo targetC,
        int count, string reportStringOverride, bool stopAll)
    {
        if (pawn?.jobs == null || pawn.Destroyed || def == null)
            return;

        if (stopAll)
            pawn.jobs.StopAll(false, true);
        var job = JobMaker.MakeJob(def, targetA, targetB, targetC);
        job.count = count;
        job.reportStringOverride = reportStringOverride;
        // The same arguments as all five CQF call sites
        pawn.jobs.StartJob(job, JobCondition.None, null, false, true, null, null, false, false, null, false, true, false);
    }

    #endregion

    #region Dialog window choices

    private static void PatchDialogWindow()
    {
        var type = dialogWindowType = AccessTools.TypeByName("QuestEditor_Library.CQFDialogTreeWindow");
        windowInterviewerField = AccessTools.DeclaredField(type, "interviewer");
        windowIntervieweeField = AccessTools.DeclaredField(type, "interviewee");
        windowQuestField = AccessTools.DeclaredField(type, "quest");
        windowTreeField = AccessTools.DeclaredField(type, "tree");
        windowCurNodeField = AccessTools.DeclaredField(type, "curNode");
        windowNextOptionsField = AccessTools.DeclaredField(type, "nextOptions");
        windowElementsField = AccessTools.DeclaredField(type, "elements");
        windowGoToNodeMethod = AccessTools.DeclaredMethod(type, "GoToNode", [typeof(int)]);

        type = dialogTreeDefType = AccessTools.TypeByName("QuestEditor_Library.DialogTreeDef");
        treeNodeMouldsField = AccessTools.DeclaredField(type, "nodeMoulds");
        treeResolveOptionsMethod = AccessTools.DeclaredMethod(type, "ResolveOptions");

        nodeOptionsField = AccessTools.DeclaredField("QuestEditor_Library.DialogNode:options");
        optionGetDEOptionsMethod = AccessTools.DeclaredMethod("QuestEditor_Library.DialogOption:GetDEOptions");

        type = AccessTools.TypeByName("QuestEditor_Library.DialogElement_Option");
        elementActionField = AccessTools.DeclaredField(type, "action");
        elementDisabledField = AccessTools.DeclaredField(type, "disabled");
        elementNextIndexField = AccessTools.DeclaredField(type, "nextIndex");
        elementTextField = AccessTools.DeclaredField(type, "text");
        dialogTextElementType = AccessTools.TypeByName("QuestEditor_Library.DialogElement_Text");

        MpCompat.harmony.Patch(windowGoToNodeMethod, postfix: new HarmonyMethod(typeof(CustomQuestFramework), nameof(PostGoToNode)));
        MpCompat.harmony.Patch(AccessTools.DeclaredMethod(dialogWindowType, nameof(Window.Close)),
            postfix: new HarmonyMethod(typeof(CustomQuestFramework), nameof(PostWindowClose)));
    }

    private static void PostWindowClose(Window __instance) => choiceSentAt.Remove(__instance);

    // GoToNode puts the node's options in nextOptions (the window shows them next frame). In MP, swap each option's
    // action (the CQFAction list + the window's own navigation) for one that only sends the choice.
    private static void PostGoToNode(Window __instance, int index)
    {
        if (!MP.IsInMultiplayer)
            return;

        choiceSentAt.Remove(__instance);
        var tree = windowTreeField.GetValue(__instance);
        var nodes = tree == null ? null : treeNodeMouldsField.GetValue(tree) as IDictionary;
        // GoToNode returns early, with nextOptions untouched, when the node key does not exist
        if (nodes == null || !nodes.Contains(index) || !ReferenceEquals(nodes[index], windowCurNodeField.GetValue(__instance)))
            return;

        var options = (IList)windowNextOptionsField.GetValue(__instance);
        for (var i = 0; i < options.Count; i++)
        {
            var optionIndex = i;
            elementActionField.SetValue(options[i], (Action)(() => SendChoice(__instance, index, optionIndex)));
        }
    }

    private static void SendChoice(Window window, int nodeIndex, int optionIndex)
    {
        var now = Time.realtimeSinceStartup;
        if (choiceSentAt.TryGetValue(window, out var sentAt) && now - sentAt < ChoiceTimeoutSeconds)
            return;
        choiceSentAt[window] = now;

        var tree = (Def)windowTreeField.GetValue(window);
        var quest = (Quest)windowQuestField.GetValue(window);
        SyncedChooseOption((Thing)windowInterviewerField.GetValue(window), (Thing)windowIntervieweeField.GetValue(window),
            tree.defName, quest?.id ?? -1, nodeIndex, optionIndex);
    }

    private static void SyncedChooseOption(Thing interviewer, Thing interviewee, string treeDefName, int questId, int nodeIndex, int optionIndex)
    {
        var tree = GenDefDatabase.GetDefSilentFail(dialogTreeDefType, treeDefName, false);
        if (tree == null)
        {
            Log.Warning($"MPCompat :: CustomQuestFramework: no DialogTreeDef named {treeDefName}, dialog choice dropped");
            return;
        }

        var quest = questId < 0 ? null : Find.QuestManager.QuestsListForReading.FirstOrDefault(q => q.id == questId);
        // The window constructor does this too; a PC without the window may not have done it yet
        treeResolveOptionsMethod.Invoke(tree, []);
        if (treeNodeMouldsField.GetValue(tree) is not IDictionary nodes || !nodes.Contains(nodeIndex))
            return;
        var node = nodes[nodeIndex];

        // Build the node's options fresh, the same way GoToNode does, so every PC runs the same choice
        var options = new List<object>();
        foreach (var dialogOption in (IList)nodeOptionsField.GetValue(node))
            options.AddRange(((IEnumerable)optionGetDEOptionsMethod.Invoke(dialogOption, [interviewer, interviewee, tree, quest])).Cast<object>());

        // This PC's open windows on this node (none on a PC that closed or never opened it)
        var windows = Find.WindowStack.Windows.Where(w => w.GetType() == dialogWindowType
            && ReferenceEquals(windowInterviewerField.GetValue(w), interviewer) && ReferenceEquals(windowIntervieweeField.GetValue(w), interviewee)
            && ReferenceEquals(windowTreeField.GetValue(w), tree) && ReferenceEquals(windowCurNodeField.GetValue(w), node)).ToList();
        foreach (var window in windows)
            choiceSentAt.Remove(window);

        if (optionIndex < 0 || optionIndex >= options.Count)
            return;
        var option = options[optionIndex];
        if ((bool)elementDisabledField.GetValue(option))
            return;

        // CQFAction.Work list, RemoveDialog, ConsumeRequiredThings
        ((Action)elementActionField.GetValue(option))?.Invoke();

        // The window's own navigation (GoToNode adds these two to each option's action)
        var nextIndex = (int?)elementNextIndexField.GetValue(option);
        foreach (var window in windows)
        {
            if (nextIndex.HasValue)
            {
                ((IList)windowElementsField.GetValue(window)).Add(Activator.CreateInstance(dialogTextElementType, "----" + elementTextField.GetValue(option)));
                // Only PCs with the window run this; DialogCondition_Chance and _SkillCheck use Rand while it builds the options
                Rand.PushState();
                try
                {
                    windowGoToNodeMethod.Invoke(window, [nextIndex.Value]);
                }
                finally
                {
                    Rand.PopState();
                }
            }
            else
                window.Close();
        }
    }

    #endregion

    #region Gizmos

    // FIXES row 279 (CQF 1.7 decompile, 2 Nov 2026). Every gizmo action below except the caravan one is a private instance
    // lambda <Parent>b__<id>_<n> on its declaring class (it captures only `this`), so a plain synced method call is enough.
    // The windows they open (Dialog_Select, Dialog_Slider) stay local; only the callbacks that change state are synced.
    // MP runs a registered method inline when it is called from a tick or an executing command, so nothing is sent twice.
    // Content check: no installed mod XML uses CompLandFillable, CompPower_Level, CompTransmit or CaravanActionDef, and
    // the Spawner, ZoneCore, GenerationActionWorker gizmos exist on editor maps only. These are cheap to register, none is urgent.
    // The debug-only ones are dropped by the server unless dev mode is allowed for the sender (no desync, the click does nothing).
    private const string CqfNamespace = "QuestEditor_Library.";

    private static void RegisterGizmos()
    {
        // A Thing sent by id: it does not set the command's map, which a Thing from another map would (map mismatch)
        var thingById = Serializer.New<Thing, int>(
            thing => thing?.thingIDNumber ?? -1,
            id =>
            {
                MP.TryGetThingById(id, out var thing);
                return thing;
            });

        // Comps: ordinals are the lambda numbers of CompGetGizmosExtra
        TryRegister("CompLandFillable toggle", () =>
            MpCompat.RegisterLambdaMethod(CqfNamespace + "CompLandFillable", nameof(ThingComp.CompGetGizmosExtra), 0));
        // 2 = Link(selected building), 3 = "Null" (unlink), 6 = slider confirm (int), 8 = output mode toggle.
        // 0 and 4 only open the Dialog_Select / Dialog_Slider windows, 1, 5 and 7 are labels and getters: not registered.
        // The Dialog_Select list holds buildings of OTHER maps (pocket map source, sub maps), never of the comp's own map.
        // MP's Thing writer sets the command map from every spawned Thing it writes, so a Building argument would make
        // "map mismatch" with the comp's map: lambda 2 sends the building as a thing id instead.
        TryRegister("CompPower_Level link", () =>
            MpCompat.RegisterLambdaMethod(CqfNamespace + "CompPower_Level", nameof(ThingComp.CompGetGizmosExtra), 2)[0]
                .TransformArgument(0, thingById, skipTypeCheck: true)
                .CancelIfAnyArgNull());
        TryRegister("CompPower_Level", () =>
            MpCompat.RegisterLambdaMethod(CqfNamespace + "CompPower_Level", nameof(ThingComp.CompGetGizmosExtra), 3, 6, 8));
        // 2 = receiver = building (same cross-map list as above), 3 = receiver = null.
        // CompTick moves stored things to the receiver every 15 ticks, so a receiver set on one PC only is a map desync.
        TryRegister("CompTransmit receiver", () =>
            MpCompat.RegisterLambdaMethod(CqfNamespace + "CompTransmit", nameof(ThingComp.CompGetGizmosExtra), 2)[0]
                .TransformArgument(0, thingById, skipTypeCheck: true)
                .CancelIfAnyArgNull());
        TryRegister("CompTransmit unlink", () =>
            MpCompat.RegisterLambdaMethod(CqfNamespace + "CompTransmit", nameof(ThingComp.CompGetGizmosExtra), 3));
        // Building_TransmitReceiver rename: MP registers the RenamableLabel setter of every IRenameable by itself
        // (Multiplayer SyncMethods.cs, "typeof(IRenameable).AllImplementing()"), so nothing is added here.

        // Editor-map tools: one lambda each, debug permission only (they run arbitrary CQFAction lists, spawn pawns, generate map parts)
        TryRegister("GenerationActionWorker CQF_RunActions", () =>
            MpCompat.RegisterLambdaMethod(CqfNamespace + "GenerationActionWorker", nameof(Thing.GetGizmos), 0).SetDebugOnly());
        TryRegister("Spawner Spawn", () =>
            MpCompat.RegisterLambdaMethod(CqfNamespace + "Spawner", nameof(Thing.GetGizmos), 0).SetDebugOnly());
        TryRegister("ZoneCore Try Generate", () =>
            MpCompat.RegisterLambdaMethod(CqfNamespace + "ZoneCore", nameof(Thing.GetGizmos), 0).SetDebugOnly());
        // CustomMapEntrance dev gizmos. Lambda 3 = "GenerateCustomMap". It is called with thing == null, so its StartJob /
        // CurrentMap branch (a pawn entering during a job tick) does not run here. Lambda 2 = SetMapDef: GenerateCustomMap
        // reads that field, so it is synced too, or the PCs would generate maps from different defs. The CustomMapDataDefs
        // are added by CQF at startup (CQFQuestDefBootstrap), after the short hashes are given, so the def goes by defName.
        TryRegister("CustomMapEntrance GenerateCustomMap", () =>
            MpCompat.RegisterLambdaMethod(CqfNamespace + "CustomMapEntrance", nameof(Thing.GetGizmos), 3).SetDebugOnly());
        TryRegister("CustomMapEntrance SetMapDef", () =>
        {
            var mapDefType = AccessTools.TypeByName(CqfNamespace + "CustomMapDataDef");
            var byName = Serializer.New<Def, string>(def => def?.defName,
                name => name == null ? null : GenDefDatabase.GetDefSilentFail(mapDefType, name, false));
            var setMapDef = MpCompat.RegisterLambdaMethod(CqfNamespace + "CustomMapEntrance", nameof(Thing.GetGizmos), 2)[0];
            setMapDef.TransformArgument(0, byName, skipTypeCheck: true).CancelIfAnyArgNull();
            setMapDef.SetDebugOnly();
        });

        // Caravan gizmo of Patch_AddCaravanAction.PostFix: display class <>c__DisplayClass0_1, lambda 2 (it captures the
        // action def, the caravan and the editor game component, through the outer display class <>c__DisplayClass0_0).
        TryRegister("Patch_AddCaravanAction caravan action", () =>
            MpCompat.RegisterLambdaDelegate(CqfNamespace + "Patch_AddCaravanAction", "PostFix",
                ["item", "CS$<>8__locals1/__instance", "CS$<>8__locals1/comp"], 2));
    }

    // One failed lookup (a CQF update that renumbers a lambda) must not stop the registrations around it
    private static void TryRegister(string what, Action register)
    {
        try
        {
            register();
        }
        catch (Exception e)
        {
            Log.Error($"MPCompat :: CustomQuestFramework: {what} stays unsynced: {e.Message}");
        }
    }

    #endregion
}
