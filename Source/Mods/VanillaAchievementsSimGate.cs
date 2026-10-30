using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Vanilla Achievements Expanded: tracker counts and card unlocks only in the synced simulation (FIXES rows 224, 107)</summary>
    /// <remarks>
    /// Status: [unverified] compiled only, not run in game.
    /// <para>
    /// The mod: every tracker hook (<c>AchievementHarmony.AbilityGained</c>, <c>HediffAdded</c>, <c>RecordEvent</c>, ... and the
    /// <c>UtilityMethods</c> calls its transpilers inject) changes saved tracker state (<c>triggeredCount</c>) and can unlock a card
    /// (<c>unlocked</c>, <c>dateUnlocked</c>, <c>AchievementPointManager.availablePoints</c>). The hooks run wherever the hooked game
    /// method runs: in the simulation, but also in UI code, in load code (map FinalizeLoading, GameComponent.LoadedGame) and in
    /// Multiplayer's join-point reload. Those calls happen on one PC only, or at a different moment per PC.
    /// <c>AbilityCountTracker</c> (VAE_VeryCapable, total) recounts all abilities of <c>Faction.OfPlayer</c>'s free colonists at every
    /// <c>GainAbility</c>, so one extra call on one PC gives a different saved number (row 224: host 45, joiner 61 at MP timer 215,000).
    /// <c>HediffTracker</c> family counts +1 per <c>AddHediff</c>, with no ProgramState check, so load-time hediff re-adds add on the
    /// loading PC only (row 107).
    /// </para>
    /// <para>
    /// Fix: in a multiplayer game, a prefix on every hook skips it unless the call runs in synced simulation: ProgramState Playing,
    /// no Scribe pass, no Multiplayer join-point reload, and Multiplayer ticking (map, world, constant ticker) or executing a synced
    /// command. A UI call, a load-time call and a reload call then change nothing on any PC; the same simulation event runs the hook
    /// on every PC. The card unlock (<c>AchievementCard.UnlockCard</c>) reads <c>Find.CurrentMap.Tile</c> for the unlock date: per PC,
    /// and null for a spectator (the NRE then stops the unlock half way, before the points are added). In a multiplayer game it uses
    /// the first map in <c>Find.Maps</c> instead (same on every PC). Single player: no change.
    /// </para>
    /// <para>
    /// Left alone: <c>AchievementPointManager.CheckUnlocks</c> (UnlockOnStartup at load: state checks with no counters, run from the
    /// same save on every PC), the dev-mode unlock tools, the reward purchase (synced by <see cref="VanillaAchievements"/>).
    /// A count that already differs is not repaired here: every PC saves and reloads its own state at a join point; a new joiner
    /// loads the host's snapshot. A total tracker (AbilityCountTracker total) recounts from zero at its next synced call; a cumulative
    /// tracker (HediffTracker family, triggeredCount++) stays different until every PC rejoins from the host's snapshot, so a lab check
    /// starts with a fresh join of every client.
    /// </para>
    /// <para>
    /// Known loss vs single player (same on every PC, no desync): a hook that fires inside a long event queued by a tick or a command
    /// (settle, caravan arrival, map generation, gravship landing) runs outside Multiplayer's tick and command scopes and is skipped.
    /// Known split left alone (review 30 Oct): TimeTracker with gameTime=false compares per-PC real play time; it unlocks after about
    /// 600 h of real play on one PC only.
    /// </para>
    /// </remarks>
    [MpCompatFor("vanillaexpanded.achievements")]
    public class VanillaAchievementsSimGate
    {
        private const string LogPrefix = "[MPCompat - VAE sim gate] ";
        private const int LoggedPerHook = 3;

        // AchievementsExpanded.AchievementHarmony: every static void tracker hook (KillThing is a Kill re-route, not a count: left alone).
        // RecordAddToEvent only calls RecordEvent, so RecordEvent covers both.
        private static readonly string[] HarmonyHooks =
        [
            "PawnJoinedFaction", "IncidentTriggered", "AnimalBondedEvent", "KillPawn", "ThingSpawned", "QuestEnded",
            "AverageMoodColony", "ResearchProjectFinished", "ThingBuildingSpawned", "TradeDealComplete", "HediffAdded",
            "HediffDeathEvent", "CheckPlantHarvested", "GeneAdded", "AddedEntityToPlatform", "NociosphereActivated",
            "DefeatedMechCluster", "ColonistBirth", "IngestedThing", "PrisonerJoined", "SlaveJoined", "AbilityActivated",
            "AbilityGained", "ThoughtInitialized", "TitleSet", "SettlementDefeatedEvent", "RecordEvent", "SingleLongTickTracker",
        ];

        // AchievementsExpanded.UtilityMethods: the calls AE's transpilers inject (mental break, skill level-up, record time);
        // ItemCraftedTrigger has no caller in this AE build, gated in case a later build calls it.
        private static readonly string[] UtilityHooks = ["MentalBreakTrigger", "LevelUpTrigger", "RecordTimeEvent", "ItemCraftedTrigger"];

        private static bool active;

        // Multiplayer.Client.Multiplayer.Ticking (map / world / constant ticker) and .reloading (join-point SaveAndReload)
        private static Func<bool> mpTicking;
        private static AccessTools.FieldRef<bool> mpReloading;

        private static MethodInfo findCurrentMapGetter;
        private static readonly Dictionary<string, int> blockedCalls = new();

        public VanillaAchievementsSimGate(ModContentPack mod)
        {
            try
            {
                var harmonyType = FindType("AchievementsExpanded.AchievementHarmony");
                var utilityType = FindType("AchievementsExpanded.UtilityMethods");
                var cardType = FindType("AchievementsExpanded.AchievementCard");
                var mpType = FindType("Multiplayer.Client.Multiplayer");

                var hooks = HarmonyHooks.Select(n => GetStaticVoid(harmonyType, n))
                    .Concat(UtilityHooks.Select(n => GetStaticVoid(utilityType, n)))
                    .ToList();
                var unlockCard = AccessTools.DeclaredMethod(cardType, "UnlockCard", [typeof(bool)])
                                 ?? throw new MissingMethodException(cardType.FullName, "UnlockCard(bool)");

                var tickingGetter = AccessTools.PropertyGetter(mpType, "Ticking")
                                    ?? throw new MissingMemberException(mpType.FullName, "Ticking");
                mpTicking = AccessTools.MethodDelegate<Func<bool>>(tickingGetter);
                var reloadingField = AccessTools.Field(mpType, "reloading")
                                     ?? throw new MissingFieldException(mpType.FullName, "reloading");
                if (!reloadingField.IsStatic || reloadingField.FieldType != typeof(bool))
                    throw new InvalidOperationException($"{LogPrefix}Multiplayer.reloading is not a static bool");
                mpReloading = AccessTools.StaticFieldRefAccess<bool>(reloadingField);

                findCurrentMapGetter = AccessTools.PropertyGetter(typeof(Find), nameof(Find.CurrentMap))
                                       ?? throw new MissingMemberException(typeof(Find).FullName, "CurrentMap");
                if (CountCurrentMapCalls(unlockCard) != 1)
                    throw new InvalidOperationException($"{LogPrefix}UnlockCard does not read Find.CurrentMap exactly once (mod changed)");

                // Every member exists: only now patch.
                var gate = new HarmonyMethod(typeof(VanillaAchievementsSimGate), nameof(GatePrefix));
                foreach (var hook in hooks)
                    MpCompat.harmony.Patch(hook, prefix: gate);
                MpCompat.harmony.Patch(unlockCard, transpiler: new HarmonyMethod(typeof(VanillaAchievementsSimGate), nameof(UnlockCardTranspiler)));
                active = true;
                Log.Message($"{LogPrefix}{hooks.Count} tracker hooks run only in synced simulation in MP; card unlock date from the first map");
            }
            catch (Exception e)
            {
                Log.Error($"{LogPrefix}not installed, Achievements Expanded counts stay per PC in MP: {e}");
            }
        }

        // Prefix on every tracker hook. Single player and synced simulation: run the hook. Any other MP call: skip it on this PC.
        private static bool GatePrefix(MethodBase __originalMethod)
        {
            if (!active || !MP.IsInMultiplayer)
                return true;
            var reason = BlockReason();
            if (reason == null)
                return true;
            NoteBlocked(__originalMethod, reason);
            return false;
        }

        // null = synced simulation (run the hook); otherwise why the call is outside it.
        private static string BlockReason()
        {
            if (Current.ProgramState != ProgramState.Playing)
                return "load"; // map / game init of a load
            if (Scribe.mode != LoadSaveMode.Inactive)
                return "scribe"; // ExposeData / PostLoadInit pass
            if (mpReloading())
                return "reload"; // join-point SaveAndReload (every PC reloads its own save)
            if (MP.IsExecutingSyncCommand || mpTicking())
                return null;
            return "interface"; // UI, LoadedGame, ExecuteWhenFinished: one PC, or a different moment per PC
        }

        // The first calls per hook and reason that were skipped, with the caller frames: evidence of which path ran outside the simulation.
        private static void NoteBlocked(MethodBase hook, string reason)
        {
            var key = $"{hook.Name}/{reason}";
            int count;
            lock (blockedCalls)
            {
                blockedCalls.TryGetValue(key, out count);
                blockedCalls[key] = ++count;
            }
            if (count > LoggedPerHook)
                return;
            var frames = new StackTrace(2, false).GetFrames()
                .Take(10)
                .Select(f => f.GetMethod())
                .Where(m => m != null)
                .Select(m => $"{m.DeclaringType?.FullName}.{m.Name}");
            Log.Warning($"{LogPrefix}skipped {key} #{count}: {string.Join(" < ", frames)}");
        }

        // AchievementCard.UnlockCard: "Find.WorldGrid.LongLatOf(Find.CurrentMap.Tile)" -> "LongLatOf(UnlockDateMap().Tile)".
        private static IEnumerable<CodeInstruction> UnlockCardTranspiler(IEnumerable<CodeInstruction> instr)
        {
            var replacement = AccessTools.Method(typeof(VanillaAchievementsSimGate), nameof(UnlockDateMap));
            foreach (var ci in instr)
            {
                if (ci.Calls(findCurrentMapGetter))
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = replacement;
                }
                yield return ci;
            }
        }

        // Single player: the current map, as before. MP: the first map (game state, same list on every PC; CurrentMap is per PC
        // and null for a spectator). No map at all: CurrentMap, as before.
        private static Map UnlockDateMap()
        {
            if (!active || !MP.IsInMultiplayer)
                return Find.CurrentMap;
            var maps = Find.Maps;
            return maps is { Count: > 0 } ? maps[0] : Find.CurrentMap;
        }

        private static int CountCurrentMapCalls(MethodBase method)
            => PatchProcessor.GetOriginalInstructions(method).Count(ci => ci.Calls(findCurrentMapGetter));

        private static MethodInfo GetStaticVoid(Type type, string name)
        {
            var method = AccessTools.DeclaredMethod(type, name) ?? throw new MissingMethodException(type.FullName, name);
            if (!method.IsStatic || method.ReturnType != typeof(void))
                throw new InvalidOperationException($"{LogPrefix}{type.FullName}.{name} is not a static void method (mod changed)");
            return method;
        }

        private static Type FindType(string name)
            => AccessTools.TypeByName(name) ?? throw new TypeLoadException($"{LogPrefix}type {name} not found");
    }
}
