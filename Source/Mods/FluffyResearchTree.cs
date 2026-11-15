using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>ResearchTree (Eheieh Version), a fork of Fluffy's/Mlie's Research Tree</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3569150345"/>
    /// <remarks>
    /// FIXES row 312 (live desync 29 Oct 23:04, opinion tick 863161): FluffyResearchTree.Queue is a WorldComponent whose
    /// list _queue picks the next research project in ticks (ResearchManager.FinishProject/StopProject postfix ->
    /// Queue.TryStartNext). The tree window edits that list in interface code on one PC only, and some edits also write
    /// ResearchManager.currentProj directly (Queue.EnqueueRange with add=false, ClearQueue, dequeue). On the host log the
    /// click path was ResearchNode.Draw -> Queue.EnqueueRange -> AttemptBeginResearch -> SetCurrentProject, 11 s before
    /// the desync report. A researcher on the clicking PC then fails JobDriver_Research's FailOn(Project == null) on
    /// that PC only.
    /// Fix (MP only, single player unchanged):
    ///  1. Every queue edit made in interface code runs locally inside a snapshot and is then undone. One synced command
    ///     carries the project lists before and after the edit, the project the edit started and "clear the current
    ///     project". Every PC applies it to its live queue (as a merge when other commands changed the queue meanwhile).
    ///  2. The executing PCs build the queue from projects only (no tree layout, no per-PC visibility or tree state).
    ///  3. Queue.TryStartNext (StopProject/FinishProject in commands and ticks) removes dependents by def prerequisites,
    ///     not through the per-PC tree. Fluffy's DoBeginResearch prefix (EnqueueFirst from the per-PC tree) only runs in
    ///     interface code, inside the snapshot.
    ///  4. Tree resets and RefreshQueue (per-PC window maintenance) keep the queue's project list in MP.
    /// </remarks>
    [MpCompatFor("eheieh.researchtree")]
    public class FluffyResearchTree
    {
        private static FieldInfo queueInstanceField;   // private static Queue _instance
        private static FieldInfo queueListField;       // private readonly List<ResearchNode> _queue
        private static AccessTools.FieldRef<object, ResearchProjectDef> nodeResearchField;
        private static FastInvokeHandler queueNodeForProject;   // private static ResearchNode QueueNodeForProject(ResearchProjectDef)
        private static FastInvokeHandler updateNodeQueueRect;   // private static void updateNodeQueueRect()
        private static FastInvokeHandler attemptBeginResearch;  // private static void AttemptBeginResearch()
        private static FastInvokeHandler attemptDoCompletionLetter; // private static void attemptDoCompletionLetter(current, next)
        private static MethodInfo planInvalidate;               // QueuePlanService.Invalidate(bool clearLocked = false)
        private static readonly AccessTools.FieldRef<ResearchManager, ResearchProjectDef> currentProjField
            = AccessTools.FieldRefAccess<ResearchManager, ResearchProjectDef>("currentProj");

        // Interface edit snapshot (main thread only)
        private static int editDepth;
        private static List<object> snapNodes;
        private static List<ResearchProjectDef> snapDefs;
        private static ResearchProjectDef snapCurrent;
        private static ResearchProjectDef startedProject;

        // Tree maintenance guard
        private static List<ResearchProjectDef> reinitDefs;
        private static List<ResearchProjectDef> refreshDefs;

        public FluffyResearchTree(ModContentPack mod)
        {
            var queueType = AccessTools.TypeByName("FluffyResearchTree.Queue");
            var nodeType = AccessTools.TypeByName("FluffyResearchTree.ResearchNode");
            var planType = AccessTools.TypeByName("FluffyResearchTree.QueuePlanService");
            var doBeginPatchType = AccessTools.TypeByName("FluffyResearchTree.MainTabWindow_Research_DoBeginResearch");
            if (queueType == null || nodeType == null)
            {
                Log.Error("MPCompat - ResearchTree: FluffyResearchTree.Queue or ResearchNode not found, compat not applied");
                return;
            }

            MethodInfo M(string name, params Type[] args) => args.Length == 0
                ? AccessTools.DeclaredMethod(queueType, name)
                : AccessTools.DeclaredMethod(queueType, name, args);

            // Resolve everything first: a missing member patches nothing (a half-applied compat can split the queue)
            var targets = new Dictionary<string, MethodBase>
            {
                ["EnqueueRange"] = M("EnqueueRange"),
                ["EnqueueRangeFirst"] = M("EnqueueRangeFirst"),
                ["EnqueueFirst"] = M("EnqueueFirst"),
                ["TryDequeue(node)"] = M("TryDequeue", nodeType),
                ["TryDequeue(def)"] = M("TryDequeue", typeof(ResearchProjectDef)),
                ["ClearQueue"] = M("ClearQueue"),
                ["EnqueueDraftRange"] = M("EnqueueDraftRange"),
                ["EnqueueDraftRangeFirst"] = M("EnqueueDraftRangeFirst"),
                ["tryToMove"] = M("tryToMove"),
                ["Notify_InstantFinished"] = M("Notify_InstantFinished"),
                ["focusStartedProject"] = M("focusStartedProject"),
                ["TryStartNext"] = M("TryStartNext"),
                ["Notify_TreeWillReset"] = M("Notify_TreeWillReset"),
                ["Notify_TreeReinitialized"] = M("Notify_TreeReinitialized"),
                ["RefreshQueue"] = M("RefreshQueue"),
                ["QueueNodeForProject"] = M("QueueNodeForProject"),
                ["updateNodeQueueRect"] = M("updateNodeQueueRect"),
                ["AttemptBeginResearch"] = M("AttemptBeginResearch"),
                ["attemptDoCompletionLetter"] = M("attemptDoCompletionLetter"),
                ["QueuePlanService.Invalidate"] = planType == null ? null : AccessTools.DeclaredMethod(planType, "Invalidate"),
                ["MainTabWindow_Research_DoBeginResearch.Prefix"] = doBeginPatchType == null ? null : AccessTools.DeclaredMethod(doBeginPatchType, "Prefix"),
            };
            queueInstanceField = AccessTools.DeclaredField(queueType, "_instance");
            queueListField = AccessTools.DeclaredField(queueType, "_queue");
            var researchField = AccessTools.DeclaredField(nodeType, "Research");
            var missing = targets.Where(kv => kv.Value == null).Select(kv => kv.Key).ToList();
            if (queueInstanceField == null) missing.Add("Queue._instance");
            if (queueListField == null) missing.Add("Queue._queue");
            if (researchField == null) missing.Add("ResearchNode.Research");
            if (missing.Any())
            {
                Log.Error("MPCompat - ResearchTree: not found: " + string.Join(", ", missing) + " - compat not applied");
                return;
            }

            nodeResearchField = AccessTools.FieldRefAccess<ResearchProjectDef>(nodeType, "Research");
            queueNodeForProject = MethodInvoker.GetHandler((MethodInfo)targets["QueueNodeForProject"]);
            updateNodeQueueRect = MethodInvoker.GetHandler((MethodInfo)targets["updateNodeQueueRect"]);
            attemptBeginResearch = MethodInvoker.GetHandler((MethodInfo)targets["AttemptBeginResearch"]);
            attemptDoCompletionLetter = MethodInvoker.GetHandler((MethodInfo)targets["attemptDoCompletionLetter"]);
            planInvalidate = (MethodInfo)targets["QueuePlanService.Invalidate"];

            MP.RegisterSyncMethod(typeof(FluffyResearchTree), nameof(SyncedEditQueue));

            // 1. Queue edits made from the tree window, the queue bar, the plan panel and the vanilla research tab
            var preEdit = new HarmonyMethod(typeof(FluffyResearchTree), nameof(PreInterfaceEdit));
            var finEdit = new HarmonyMethod(typeof(FluffyResearchTree), nameof(FinInterfaceEdit));
            foreach (var name in new[] { "EnqueueRange", "EnqueueRangeFirst", "EnqueueFirst", "TryDequeue(node)", "TryDequeue(def)",
                         "ClearQueue", "EnqueueDraftRange", "EnqueueDraftRangeFirst", "tryToMove", "Notify_InstantFinished" })
                MpCompat.harmony.Patch(targets[name], prefix: preEdit, finalizer: finEdit);

            // The project an edit starts goes into the command (SetCurrentProject is debug-only in MP: dropped for a
            // player without dev rights, and vanilla AttemptBeginResearch only opens a dialog when memes are missing)
            MpCompat.harmony.Patch(targets["focusStartedProject"],
                prefix: new HarmonyMethod(typeof(FluffyResearchTree), nameof(PreFocusStartedProject)));

            // 3. Fluffy's DoBeginResearch prefix edits the queue from the per-PC tree (EnqueueFirst). In MP it runs only
            // in interface code, inside the snapshot; the clicking PC's command carries the result. EnqueueFirst has no
            // other caller, so it is also stopped there (in case the prefix was inlined into DoBeginResearch).
            MpCompat.harmony.Patch(targets["MainTabWindow_Research_DoBeginResearch.Prefix"],
                prefix: new HarmonyMethod(typeof(FluffyResearchTree), nameof(OnlyInInterfaceInMp)));
            MpCompat.harmony.Patch(targets["EnqueueFirst"],
                prefix: new HarmonyMethod(typeof(FluffyResearchTree), nameof(OnlyInInterfaceInMp)));
            MpCompat.harmony.Patch(targets["TryStartNext"],
                prefix: new HarmonyMethod(typeof(FluffyResearchTree), nameof(PreTryStartNext)));

            // 4. Per-PC tree maintenance keeps the project list
            MpCompat.harmony.Patch(targets["Notify_TreeWillReset"],
                prefix: new HarmonyMethod(typeof(FluffyResearchTree), nameof(PreTreeWillReset)));
            MpCompat.harmony.Patch(targets["Notify_TreeReinitialized"],
                prefix: new HarmonyMethod(typeof(FluffyResearchTree), nameof(PreTreeReinitialized)),
                finalizer: new HarmonyMethod(typeof(FluffyResearchTree), nameof(FinTreeReinitialized)));
            MpCompat.harmony.Patch(targets["RefreshQueue"],
                prefix: new HarmonyMethod(typeof(FluffyResearchTree), nameof(PreRefreshQueue)),
                finalizer: new HarmonyMethod(typeof(FluffyResearchTree), nameof(FinRefreshQueue)));
        }

        #region Queue access

        private static IList QueueList()
        {
            var instance = queueInstanceField.GetValue(null);
            return instance == null ? null : (IList)queueListField.GetValue(instance);
        }

        private static List<ResearchProjectDef> QueueDefs()
        {
            var list = QueueList();
            if (list == null) return null;
            var defs = new List<ResearchProjectDef>(list.Count);
            foreach (var node in list)
            {
                var def = node == null ? null : nodeResearchField(node);
                if (def != null) defs.Add(def);
            }
            return defs;
        }

        private static void RefreshQueueUi()
        {
            try
            {
                planInvalidate.Invoke(null, new object[] { false });
                updateNodeQueueRect(null);
            }
            catch (Exception e)
            {
                Log.Warning("MPCompat - ResearchTree: queue window refresh failed: " + e.Message);
            }
        }

        // Build the queue from projects only, in the given order: the same result on every PC, with or without a tree
        private static void SetQueueDefs(IList list, IEnumerable<ResearchProjectDef> defs)
        {
            list.Clear();
            var seen = new HashSet<ResearchProjectDef>();
            foreach (var def in defs)
            {
                if (def == null || !seen.Add(def)) continue;
                var node = queueNodeForProject(null, def);
                if (node != null) list.Add(node);
            }
        }

        #endregion

        #region 1. Interface edits

        private static void PreInterfaceEdit()
        {
            if (editDepth > 0)
            {
                editDepth++;
                return;
            }
            if (!MP.IsInMultiplayer || !MP.InInterface || !UnityData.IsInMainThread) return;
            var list = QueueList();
            if (list == null) return;

            editDepth = 1;
            snapNodes = list.Cast<object>().ToList();
            snapDefs = QueueDefs();
            snapCurrent = currentProjField(Find.ResearchManager);
            startedProject = null;

            // Single player's RefreshQueue puts the current project into an empty queue when the window opens. In MP that
            // is a per-PC edit and is undone (region 4), so the edit does it here and the command carries it.
            if (list.Count == 0 && snapCurrent != null && !snapCurrent.IsFinished)
            {
                var node = queueNodeForProject(null, snapCurrent);
                if (node != null) list.Add(node);
            }
        }

        // The edit is undone on this PC; the command makes it on every PC. A failed edit is undone and not sent.
        private static void FinInterfaceEdit(Exception __exception)
        {
            if (editDepth == 0) return;
            if (--editDepth > 0) return;

            var list = QueueList();
            var manager = Find.ResearchManager;
            var before = snapDefs;
            var beforeCurrent = snapCurrent;
            var started = startedProject;
            var after = QueueDefs();
            var afterCurrent = currentProjField(manager);

            if (list != null)
            {
                list.Clear();
                foreach (var node in snapNodes) list.Add(node);
            }
            currentProjField(manager) = beforeCurrent;
            snapNodes = null;
            snapDefs = null;
            snapCurrent = null;
            startedProject = null;
            RefreshQueueUi();

            if (__exception != null || after == null) return;
            var clearCurrent = started == null && afterCurrent == null && beforeCurrent != null;
            if (!after.SequenceEqual(before) || clearCurrent || started != null)
                SyncedEditQueue(before, after, beforeCurrent, started, clearCurrent);
        }

        private static void PreFocusStartedProject(ResearchProjectDef projectToStart)
        {
            if (editDepth > 0 && projectToStart != null)
                startedProject = projectToStart;
        }

        private static bool OnlyInInterfaceInMp() => !MP.IsInMultiplayer || MP.InInterface;

        private static void SyncedEditQueue(List<ResearchProjectDef> before, List<ResearchProjectDef> after,
            ResearchProjectDef beforeCurrent, ResearchProjectDef startProject, bool clearCurrent)
        {
            var list = QueueList();
            if (list == null) return;
            before ??= new List<ResearchProjectDef>();
            after ??= new List<ResearchProjectDef>();
            var live = QueueDefs();

            if (live.SequenceEqual(before))
                SetQueueDefs(list, after);
            else
            {
                // Another command changed the queue after this edit was made (a second click before the first one came
                // back, or another player): keep their removals and additions, apply this edit's result around them
                var removedByEdit = before.Except(after).ToHashSet();
                var removedMeanwhile = before.Except(live).ToHashSet();
                var addedMeanwhile = live.Except(before).ToHashSet();
                var merged = after.Where(d => !removedMeanwhile.Contains(d))
                    .Concat(live.Where(d => addedMeanwhile.Contains(d) && !removedByEdit.Contains(d)));
                SetQueueDefs(list, merged);
            }

            var manager = Find.ResearchManager;
            if (startProject != null)
            {
                if (!startProject.IsFinished && startProject.CanStartNow && currentProjField(manager) != startProject)
                    manager.SetCurrentProject(startProject);
            }
            else if (clearCurrent && currentProjField(manager) == beforeCurrent)
                currentProjField(manager) = null;

            RefreshQueueUi();
        }

        #endregion

        #region 3. Next project after StopProject / FinishProject

        // Queue.TryStartNext without the per-PC tree: TryDequeue(finished) removes the queued projects that still need it
        // through Fluffy's tree (QueuedOrActiveNodeForProject -> Tree.ResearchToNode, tab profiles, filters). Here the
        // dependents come from def prerequisites only. The rest is as in the mod: clear current if it was this project,
        // begin the new head, completion letter.
        private static bool PreTryStartNext(ResearchProjectDef finished)
        {
            if (!MP.IsInMultiplayer || MP.InInterface) return true;
            var list = QueueList();
            if (list == null || finished == null) return true;
            var defs = QueueDefs();
            if (!defs.Contains(finished)) return false;

            var keep = new List<ResearchProjectDef>(defs.Count);
            var headRemoved = false;
            for (var i = 0; i < defs.Count; i++)
            {
                var def = defs[i];
                if (def == finished || NeedsUnfinished(def, finished))
                {
                    if (i == 0) headRemoved = true;
                    continue;
                }
                keep.Add(def);
            }
            SetQueueDefs(list, keep);

            var manager = Find.ResearchManager;
            if (currentProjField(manager) == finished)
                currentProjField(manager) = null;
            if (headRemoved)
                attemptBeginResearch(null);
            var next = QueueDefs().FirstOrDefault();
            attemptBeginResearch(null);
            attemptDoCompletionLetter(null, finished, next);
            RefreshQueueUi();
            return false;
        }

        // True if project needs target through a chain of unfinished prerequisites (hidden ones included)
        private static bool NeedsUnfinished(ResearchProjectDef project, ResearchProjectDef target)
        {
            var seen = new HashSet<ResearchProjectDef>();
            var stack = new Stack<ResearchProjectDef>();
            stack.Push(project);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                foreach (var prerequisite in (current.prerequisites ?? Enumerable.Empty<ResearchProjectDef>())
                             .Concat(current.hiddenPrerequisites ?? Enumerable.Empty<ResearchProjectDef>()))
                {
                    if (prerequisite == null || prerequisite.IsFinished || !seen.Add(prerequisite)) continue;
                    if (prerequisite == target) return true;
                    stack.Push(prerequisite);
                }
            }
            return false;
        }

        #endregion

        #region 4. Tree maintenance

        // Notify_TreeWillReset empties the queue until Notify_TreeReinitialized refills it (a long event later, and
        // Tree.Initialize can call it from its worker thread). In MP the queue stays filled: ticks on this PC keep
        // reading the same projects as on the other PCs. Notify_TreeReinitialized still swaps in the new tree's nodes.
        private static bool PreTreeWillReset() => !MP.IsInMultiplayer;

        private static void PreTreeReinitialized()
        {
            reinitDefs = MP.IsInMultiplayer ? QueueDefs() : null;
        }

        // Notify_TreeReinitialized drops projects this PC's tree does not show (per-PC filters): put them back
        private static void FinTreeReinitialized()
        {
            var defs = reinitDefs;
            reinitDefs = null;
            if (defs == null || !MP.IsInMultiplayer) return;
            var list = QueueList();
            if (list != null && !QueueDefs().SequenceEqual(defs))
            {
                SetQueueDefs(list, defs);
                RefreshQueueUi();
            }
        }

        // RefreshQueue (window open) adds the current project to an empty queue and drops hidden projects, on this PC only
        private static void PreRefreshQueue()
        {
            refreshDefs = MP.IsInMultiplayer ? QueueDefs() : null;
        }

        private static void FinRefreshQueue()
        {
            var defs = refreshDefs;
            refreshDefs = null;
            if (defs == null || !MP.IsInMultiplayer) return;
            var list = QueueList();
            if (list != null && !QueueDefs().SequenceEqual(defs))
                SetQueueDefs(list, defs);
        }

        #endregion
    }
}
