using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat;

/// <summary>Research Reinvented by PeteTimesSix</summary>
/// <see href="https://github.com/PeteTimesSix/ResearchReinvented"/>
/// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=2868392160"/>
/// <remarks>
/// FIXES row 171 (21 Oct 2026, live save-state checksum run FA4 at MP 7,500, host vs second client): the saved
/// <c>ResearchOpportunityManager._allGeneratedOpportunities</c> (+ <c>_projectsGenerated</c>, <c>_categoryStores</c>, <c>_currentProject</c>,
/// <c>RR_UniqueIDsManager.nextOpportunityID</c>) is ONE game component, but whatever fills it reads the CONTEXT faction: it generates for
/// <c>Find.ResearchManager.GetProject(null)</c> (ResearchOpportunityManager.cs:145-154) and MasterFactory reads <c>Faction.OfPlayer</c>
/// (MasterFactory.cs:46, :52). Under multifaction MP those two change with the code path, not with the world state:
///   - interface: <c>GameComponentUpdate</c> -> <c>CheckForPopups</c> (:83-90, :156-182) -> <c>CurrentProjectOpportunityCategories</c> (:55-64) ->
///     <c>CheckForRegeneration</c> -> <c>GenerateOpportunities</c> (:391-445) runs with the PC's own faction (host 67; the arbiter in
///     its headless Update; the second client 69). FA4 host log: all three generations (Smithing, Machining, MicroelectronicsBasics) came from
///     CheckForPopups; arbiter log: MicroelectronicsBasics from CheckForPopups too.
///   - map tick: pawn think -> WorkGiver_Analyse.ShouldSkip -> <c>GetFilteredOpportunities</c> -> <c>CurrentProjectOpportunities</c> ->
///     <c>CheckForRegeneration</c>, under the pawn's faction. FA4 client log: Smithing and Machining came from here, MicroelectronicsBasics never did.
///   - world tick: <c>GameComponentTick</c> (:71-81) under the spectator faction (AsyncWorldTimeComp.PreContext), whose project is null.
/// So the host (and the arbiter) saved 342 opportunities (loadID 2591-2932, 2,750 leaves) + 10 category stores for the project the host
/// faction was researching, the client saved none, and <c>nextOpportunityID</c> was 2933 against 2591.
/// Fix, two parts. (1) Interface code never writes: <c>GenerateOpportunities</c>, <c>CheckForRegeneration</c>, <c>DelayedRegeneration</c> and
/// <c>ResetAllProgress</c> do nothing while <c>MP.InInterface</c>, so the saved lists change only inside ticks, synced commands and loads, which
/// every PC runs in the same order. (2) Everything the manager reads while it generates (<c>GetProject</c>, <c>Faction.OfPlayer</c>, its ideo)
/// is read as ONE fixed faction, the first player faction in <c>AllFactionsListForReading</c> that is not the spectator (the same on every PC),
/// by pushing that faction around <c>CheckForRegeneration</c>, <c>GameComponentTick</c>, <c>StartupChecks</c> and <c>GenerateOpportunities</c>.
/// Consequence: the list always holds the fixed faction's current project. A second player faction researching another project gets no
/// opportunities of its own (it sees, and adds progress to its own manager for, the fixed faction's project); a per-faction list would need
/// the manager's saved fields swapped per faction and saved for every faction.
/// </remarks>
[MpCompatFor("PeteTimesSix.ResearchReinvented")]
public class ResearchReinvented
{
    private const string ManagerTypeName = "PeteTimesSix.ResearchReinvented.Managers.ResearchOpportunityManager";

    // Multiplayer internals, reached by reflection like the other modules do (FactionExtensions.cs:13-39, Multiplayer.cs:51)
    private static Action<Map, Faction, bool> pushFaction;
    private static Func<Faction> popFaction;
    private static Func<object> worldCompGetter;
    private static FieldInfo spectatorFactionField;
    private static bool scopeAvailable;

    [ThreadStatic] private static int scopeDepth;
    private static int interfaceSkips;
    private static Action clearCaches;

    public ResearchReinvented(ModContentPack mod)
    {
        var method = AccessTools.PropertyGetter("PeteTimesSix.ResearchReinvented.Managers.ResearchOpportunityManager:CurrentProjectOpportunities");

        MpCompat.harmony.Patch(method, transpiler: new HarmonyMethod(AddSorting));

        PatchSavedListWriters();
    }

    private static void PatchSavedListWriters()
    {
        var manager = AccessTools.TypeByName(ManagerTypeName);
        var generate = manager == null ? null : AccessTools.DeclaredMethod(manager, "GenerateOpportunities", [typeof(ResearchProjectDef), typeof(bool)]);
        var check = manager == null ? null : AccessTools.DeclaredMethod(manager, "CheckForRegeneration");
        var tick = manager == null ? null : AccessTools.DeclaredMethod(manager, "GameComponentTick");
        var startup = manager == null ? null : AccessTools.DeclaredMethod(manager, "StartupChecks");
        var delayed = manager == null ? null : AccessTools.DeclaredMethod(manager, "DelayedRegeneration");
        var reset = manager == null ? null : AccessTools.DeclaredMethod(manager, "ResetAllProgress");
        if (generate == null || check == null || tick == null || startup == null || delayed == null || reset == null)
        {
            Log.Error("[MPCompat] Research Reinvented row 171: ResearchOpportunityManager changed (a method is missing), the saved opportunity list will desync");
            return;
        }

        scopeAvailable = ResolveMultiplayerInternals();
        if (!scopeAvailable)
            Log.Error("[MPCompat] Research Reinvented row 171: Multiplayer internals not found, opportunities are generated for whichever faction is in context (interface writes are still blocked)");

        // RR's six WorkGiver caches and the prototype cache are static and keyed by the CONTEXT faction's project, so an interface call that
        // fills one before the fixed faction switches project would leave this PC with an older list than a PC that fills it in a tick.
        // Clearing them whenever a generation ran (ticks, commands, loads: the same moment on every PC) makes the refill order irrelevant.
        var clear = AccessTools.DeclaredMethod("PeteTimesSix.ResearchReinvented.Utilities.CacheClearer:ClearCaches");
        if (clear != null)
            clearCaches = (Action)Delegate.CreateDelegate(typeof(Action), clear);
        else
            Log.Error("[MPCompat] Research Reinvented row 171: CacheClearer.ClearCaches not found, a PC that opens a work menu can keep an older opportunity list in the WorkGiver caches");

        var harmony = MpCompat.harmony;
        harmony.Patch(generate, prefix: new HarmonyMethod(typeof(ResearchReinvented), nameof(PreGenerate)),
            postfix: new HarmonyMethod(typeof(ResearchReinvented), nameof(PostGenerate)),
            finalizer: new HarmonyMethod(typeof(ResearchReinvented), nameof(LeaveScope)));
        harmony.Patch(check, prefix: new HarmonyMethod(typeof(ResearchReinvented), nameof(PreCheckForRegeneration)),
            finalizer: new HarmonyMethod(typeof(ResearchReinvented), nameof(LeaveScope)));
        harmony.Patch(tick, prefix: new HarmonyMethod(typeof(ResearchReinvented), nameof(PreScope)),
            finalizer: new HarmonyMethod(typeof(ResearchReinvented), nameof(LeaveScope)));
        harmony.Patch(startup, prefix: new HarmonyMethod(typeof(ResearchReinvented), nameof(PreScope)),
            finalizer: new HarmonyMethod(typeof(ResearchReinvented), nameof(LeaveScope)));
        harmony.Patch(delayed, prefix: new HarmonyMethod(typeof(ResearchReinvented), nameof(PreSkipInInterface)));
        harmony.Patch(reset, prefix: new HarmonyMethod(typeof(ResearchReinvented), nameof(PreSkipInInterface)));
    }

    private static bool ResolveMultiplayerInternals()
    {
        try
        {
            var extensions = AccessTools.TypeByName("Multiplayer.Client.Factions.FactionExtensions");
            var push = extensions == null ? null : AccessTools.DeclaredMethod(extensions, "PushFaction", [typeof(Map), typeof(Faction), typeof(bool)]);
            var pop = extensions == null ? null : AccessTools.DeclaredMethod(extensions, "PopFaction", Type.EmptyTypes);
            var worldComp = AccessTools.PropertyGetter("Multiplayer.Client.Multiplayer:WorldComp");
            var spectator = AccessTools.Field("Multiplayer.Client.MultiplayerWorldComp:spectatorFaction");
            if (push == null || pop == null || worldComp == null || spectator == null)
                return false;

            pushFaction = (Action<Map, Faction, bool>)Delegate.CreateDelegate(typeof(Action<Map, Faction, bool>), push);
            popFaction = (Func<Faction>)Delegate.CreateDelegate(typeof(Func<Faction>), pop);
            worldCompGetter = (Func<object>)Delegate.CreateDelegate(typeof(Func<object>), worldComp);
            spectatorFactionField = spectator;
            return true;
        }
        catch (Exception e)
        {
            Log.Error($"[MPCompat] Research Reinvented row 171: could not bind Multiplayer internals: {e}");
            return false;
        }
    }

    // The faction whose research and ideo RR reads while it fills the shared list: the first player faction that is not the spectator.
    // AllFactionsListForReading is saved and loaded in order and new factions are appended, so this is the same faction on every PC.
    internal static Faction FixedFaction()
    {
        var factions = Find.FactionManager?.AllFactionsListForReading;
        if (factions == null)
            return null;

        Faction spectator = null;
        try { spectator = spectatorFactionField.GetValue(worldCompGetter()) as Faction; }
        catch (Exception) { /* no world comp yet: the Hidden test below still tells the spectator apart */ }

        for (var i = 0; i < factions.Count; i++)
        {
            var faction = factions[i];
            if (faction.IsPlayer && faction != spectator && !faction.Hidden)
                return faction;
        }
        return null;
    }

    // Pushes the fixed faction (Faction.OfPlayer + the world-level data MP swaps with it: the research manager, history, storyteller) and
    // returns true when LeaveScope has to pop it. Nothing is pushed when it is already the player faction or when we are inside our own push.
    private static bool EnterScope()
    {
        if (!scopeAvailable || scopeDepth > 0)
            return false;

        var fixedFaction = FixedFaction();
        if (fixedFaction == null || Find.FactionManager.OfPlayer == fixedFaction)
            return false;

        try
        {
            pushFaction(null, fixedFaction, true);
        }
        catch (Exception e)
        {
            Log.ErrorOnce($"[MPCompat] Research Reinvented row 171: could not push the fixed faction: {e}", 171_1);
            return false;
        }

        scopeDepth++;
        return true;
    }

    private static void LeaveScope(bool __state)
    {
        if (!__state)
            return;

        scopeDepth--;
        popFaction();
    }

    private static bool PreGenerate(out bool __state)
    {
        __state = false;
        if (!MP.IsInMultiplayer)
            return true;
        if (MP.InInterface)
            return NoteInterfaceSkip("GenerateOpportunities");

        __state = EnterScope();
        return true;
    }

    // Not when the prefix skipped the original (interface code): only a generation that really ran invalidates the caches.
    // Same test as PreGenerate's skip, so this does not depend on Harmony's __runOriginal.
    private static void PostGenerate()
    {
        if (MP.IsInMultiplayer && !MP.InInterface)
            clearCaches?.Invoke();
    }

    private static bool PreCheckForRegeneration(ref bool __result, out bool __state)
    {
        __state = false;
        if (!MP.IsInMultiplayer)
            return true;
        if (MP.InInterface)
        {
            __result = false; // "the project did not change": the getters keep their cache, nothing is generated or cancelled
            return NoteInterfaceSkip("CheckForRegeneration");
        }

        __state = EnterScope();
        return true;
    }

    // GameComponentTick (world tick, spectator faction in context) and StartupChecks (load): read the project as the fixed faction.
    private static void PreScope(out bool __state)
    {
        __state = MP.IsInMultiplayer && !MP.InInterface && EnterScope();
    }

    // DelayedRegeneration sets a per-PC flag that GameComponentTick turns into a forced regeneration; ResetAllProgress clears the saved lists.
    private static bool PreSkipInInterface(MethodBase __originalMethod)
    {
        if (MP.IsInMultiplayer && MP.InInterface)
            return NoteInterfaceSkip(__originalMethod.Name);
        return true;
    }

    private static bool NoteInterfaceSkip(string what)
    {
        if (interfaceSkips++ < 3)
            Log.Message($"[MPCompat] Research Reinvented row 171: {what} called from interface code in a session, skipped (the saved opportunity list changes only in ticks, commands and loads)");
        return false;
    }

    static IEnumerable<CodeInstruction> AddSorting(IEnumerable<CodeInstruction> insts)
    {
        var target = AccessTools.Method(typeof(Enumerable), nameof(Enumerable.ToList));
        var replace = AccessTools.Method(typeof(ResearchReinvented), nameof(SortByUniqueLoadID));

        foreach(var inst in insts)
        {
            if (inst.operand is MethodInfo method && method == target)
            {
                inst.operand = replace;
            }
            yield return inst;
        }
    }

    private static List<ILoadReferenceable> SortByUniqueLoadID(IEnumerable<ILoadReferenceable> list) => list.OrderBy(a => a.GetUniqueLoadID()).ToList();
}
