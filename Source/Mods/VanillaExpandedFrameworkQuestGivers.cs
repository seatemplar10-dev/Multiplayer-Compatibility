using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat;

/// <summary>Vanilla Expanded Framework quest givers (VEF.Storyteller: CompQuestGiver -> QuestGiverManager -> Window_Contracts)</summary>
/// <remarks>
/// FIXES row 472 (26 Nov 2026). In the co-op pack only Rim-Effect Renegade Core uses a quest giver (RE_GalaxyMap building,
/// CompProperties_QuestGiver questManagerID 1, QuestGiverDef RE_GalaxyMap_QuestGiver, windowClass VEF.Storyteller.Window_Contracts).
/// Window_Contracts has two sim-writing buttons, "Accept quest" and "Accept quest for: <choice>". Both end in
/// AcceptQuestByInterface(preAcceptAction, requiresAccepter): preAcceptAction is QuestPart_Choice.Choose(choice) on the selected
/// quest, then QuestGiverManager.ActivateQuest(accepter, selected) adds the quest to the QuestManager, accepts it, sends the letter,
/// pays the currency (goodwill) and removes the entry from availableQuests - all on the clicking PC only. The window's other
/// buttons (row select, jump to target, select targets) are camera / selection only; it has no decline, reroll or refresh.
/// Fix: in the interface the Choose call inside preAcceptAction is captured (not run) and ActivateQuest is replaced by one synced
/// call SyncedAcceptQuest(manager id, quest id, accepter, choice part index, choice index). The manager is found again through
/// StorytellerWatcher.Instance.questGiverManagers by its id, the entry through availableQuests by its Quest.id. The command runs
/// Choose and the original ActivateQuest on every PC; a second command for the same quest finds no entry and does nothing.
/// The sound, the "quest accepted" message and selected = null stay local (UI only).
/// </remarks>
[MpCompatFor("OskarPotocki.VanillaFactionsExpanded.Core")]
public class VanillaExpandedFrameworkQuestGivers
{
    private static MethodInfo activateQuestMethod;
    private static MethodInfo questInfoQuestGetter;
    private static FieldInfo watcherInstanceField;
    private static FieldInfo questGiverManagersField;
    private static FieldInfo availableQuestsField;

    private static bool capturingChoose;
    private static QuestPart_Choice pendingPart;
    private static QuestPart_Choice.Choice pendingChoice;

    public VanillaExpandedFrameworkQuestGivers(ModContentPack mod)
    {
        if (LabOff.Row(472))
        {
            Log.Message("[MPCompat - VEF quest givers] row 472 switched off by the lab, quest giver accept not synced");
            return;
        }
        PatchQuestGiverAccept();
    }

    private static void PatchQuestGiverAccept()
    {
        var windowType = AccessTools.TypeByName("VEF.Storyteller.Window_Contracts");
        var managerType = AccessTools.TypeByName("VEF.Storyteller.QuestGiverManager");
        var infoType = AccessTools.TypeByName("VEF.Storyteller.QuestInfo");
        var watcherType = AccessTools.TypeByName("VEF.Storyteller.StorytellerWatcher");

        var acceptUi = windowType == null ? null : AccessTools.DeclaredMethod(windowType, "AcceptQuestByInterface", [typeof(Action), typeof(bool)]);
        activateQuestMethod = managerType == null || infoType == null ? null : AccessTools.DeclaredMethod(managerType, "ActivateQuest", [typeof(Pawn), infoType]);
        questInfoQuestGetter = infoType == null ? null : AccessTools.DeclaredPropertyGetter(infoType, "Quest");
        watcherInstanceField = watcherType == null ? null : AccessTools.DeclaredField(watcherType, "Instance");
        questGiverManagersField = watcherType == null ? null : AccessTools.DeclaredField(watcherType, "questGiverManagers");
        availableQuestsField = managerType == null ? null : AccessTools.DeclaredField(managerType, "availableQuests");
        var choose = AccessTools.DeclaredMethod(typeof(QuestPart_Choice), nameof(QuestPart_Choice.Choose), [typeof(QuestPart_Choice.Choice)]);

        var missing = new System.Collections.Generic.List<string>();
        if (acceptUi == null) missing.Add("Window_Contracts.AcceptQuestByInterface(Action, bool)");
        if (activateQuestMethod == null) missing.Add("QuestGiverManager.ActivateQuest(Pawn, QuestInfo)");
        if (questInfoQuestGetter == null) missing.Add("QuestInfo.Quest");
        if (watcherInstanceField == null) missing.Add("StorytellerWatcher.Instance");
        if (questGiverManagersField == null) missing.Add("StorytellerWatcher.questGiverManagers");
        if (availableQuestsField == null) missing.Add("QuestGiverManager.availableQuests");
        if (choose == null) missing.Add("QuestPart_Choice.Choose(Choice)");
        if (missing.Count > 0)
        {
            Log.Error($"[MPCompat - VEF quest givers] row 472: {string.Join(", ", missing)} not found, accepting a contract stays local and can desync");
            return;
        }

        MP.RegisterSyncMethod(typeof(VanillaExpandedFrameworkQuestGivers), nameof(SyncedAcceptQuest));
        MpCompat.harmony.Patch(acceptUi, prefix: new HarmonyMethod(typeof(VanillaExpandedFrameworkQuestGivers), nameof(PreAcceptQuestByInterface)));
        MpCompat.harmony.Patch(choose, prefix: new HarmonyMethod(typeof(VanillaExpandedFrameworkQuestGivers), nameof(PreChoose)));
        MpCompat.harmony.Patch(activateQuestMethod, prefix: new HarmonyMethod(typeof(VanillaExpandedFrameworkQuestGivers), nameof(PreActivateQuest)));
        Log.Message("[MPCompat - VEF quest givers] row 472: quest giver window accept (Choose + ActivateQuest) synced as one command");
    }

    private static bool InInterface => MP.IsInMultiplayer && MP.InInterface;

    // The window's preAcceptAction is "part.Choose(choice)". Run it inside a scope that captures the Choose call instead of running it.
    private static void PreAcceptQuestByInterface(ref Action preAcceptAction)
    {
        if (preAcceptAction == null || !InInterface)
            return;
        var original = preAcceptAction;
        preAcceptAction = () =>
        {
            capturingChoose = true;
            try { original(); }
            finally { capturingChoose = false; }
        };
    }

    private static bool PreChoose(QuestPart_Choice __instance, QuestPart_Choice.Choice choice)
    {
        if (!capturingChoose)
            return true;
        pendingPart = __instance;
        pendingChoice = choice;
        return false;
    }

    private static bool PreActivateQuest(object __instance, Pawn accepter, object questInfo)
    {
        var part = pendingPart;
        var choice = pendingChoice;
        pendingPart = null;
        pendingChoice = null;
        if (!InInterface)
            return true;

        var quest = questInfo == null ? null : questInfoQuestGetter.Invoke(questInfo, null) as Quest;
        var managerId = FindManagerId(__instance);
        if (quest == null || managerId == null)
        {
            Log.Warning("[MPCompat - VEF quest givers] row 472: could not identify the quest giver or the quest, contract not accepted");
            return false;
        }

        int partIndex = -1, choiceIndex = -1;
        if (part != null)
        {
            partIndex = quest.PartsListForReading.IndexOf(part);
            choiceIndex = partIndex < 0 || part.choices == null ? -1 : part.choices.IndexOf(choice);
            if (choiceIndex < 0)
                partIndex = -1;
        }
        SyncedAcceptQuest(managerId.Value, quest.id, accepter, partIndex, choiceIndex);
        return false;
    }

    private static int? FindManagerId(object manager)
    {
        if (manager == null || watcherInstanceField.GetValue(null) is not { } watcher || questGiverManagersField.GetValue(watcher) is not IDictionary managers)
            return null;
        foreach (DictionaryEntry entry in managers)
        {
            if (ReferenceEquals(entry.Value, manager))
                return (int)entry.Key;
        }
        return null;
    }

    private static void SyncedAcceptQuest(int managerId, int questId, Pawn accepter, int partIndex, int choiceIndex)
    {
        if (watcherInstanceField.GetValue(null) is not { } watcher || questGiverManagersField.GetValue(watcher) is not IDictionary managers
            || !managers.Contains(managerId) || managers[managerId] is not { } manager || availableQuestsField.GetValue(manager) is not IList available)
            return;

        object info = null;
        Quest quest = null;
        foreach (var entry in available)
        {
            if (entry != null && questInfoQuestGetter.Invoke(entry, null) is Quest q && q.id == questId)
            {
                info = entry;
                quest = q;
                break;
            }
        }
        // Already accepted by an earlier command (two players clicked) or reset meanwhile: nothing to do.
        if (info == null)
            return;

        var parts = quest.PartsListForReading;
        if (partIndex >= 0 && partIndex < parts.Count && parts[partIndex] is QuestPart_Choice part
            && part.choices != null && choiceIndex >= 0 && choiceIndex < part.choices.Count)
            part.Choose(part.choices[choiceIndex]);

        activateQuestMethod.Invoke(manager, [accepter, info]);
    }
}
