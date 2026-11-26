using System;
using HarmonyLib;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>EBSG Framework by alite</summary>
    /// <remarks>
    /// Determinism only. Written against EBSGFramework.dll from <c>S:/Rimworld MP shiz/lab/game/Mods/3112549163/1.6/Assemblies/EBSGFramework.dll</c>.
    /// Sweep evidence (waylith/_lanes/clrsem/sweep_il, TOP.md entry 20 and HITS.tsv):
    /// <list type="bullet">
    /// <item><c>EBSGFramework.EBSGUtilities::GainRandomGeneSet</c> (0x060002F0): <c>double num2 = new Random().NextDouble() * (double)num;</c> picks which
    /// RandomXenoGenes set a pawn gains (weighted). No def of the lab mod list mentions RandomXenoGenes, so exposure is unverified - cheap to fix anyway.</item>
    /// <item><c>EBSGFramework.JobGiver_AICastAnyOfAbilityOnEnemyTarget</c>: <c>private static Random rnd = new Random();</c> (static ctor, 0x06000701) and
    /// <c>chosenAbility = presentAbilities[rnd.Next(presentAbilities.Count)];</c> in TryGiveJob - an AI think-tree node, i.e. simulation: which ability an
    /// AI pawn casts differed per machine.</item>
    /// </list>
    /// Fix: GainRandomGeneSet's <c>newobj System.Random::.ctor</c> becomes <see cref="PatchingUtilities.RandRedirector"/> (no push/pop);
    /// the static field <c>rnd</c> is not readonly, so it is set to <see cref="PatchingUtilities.RandRedirector.Instance"/> (same approach as the
    /// SmarterConstruction module). FieldInfo.SetValue runs the type initializer first if it has not run yet, so the mod's own initializer cannot overwrite it.
    /// Both uses are Next(n) / NextDouble(), which the redirector overrides (the dll targets .NET Framework 4.8, and an IL listing of the whole dll
    /// references only System.Random::.ctor (2: GainRandomGeneSet and the static ctor), ::Next (1) and ::NextDouble (1): no Random.Shared, NextSingle, NextInt64).
    /// Status: [unverified] - compiled only, not run in game yet.
    /// </remarks>
    [MpCompatFor("ebsg.framework")]
    public class EBSGFramework
    {
        private const string LogPrefix = "[MPCompat - EBSG Framework] ";

        public EBSGFramework(ModContentPack mod)
        {
            RimDark40kRng.RunSteps(LogPrefix,
                ("GainRandomGeneSet", () =>
                {
                    var method = RimDark40kRng.FindMethod(LogPrefix, "EBSGFramework.EBSGUtilities", "GainRandomGeneSet");
                    // lab switch row 16 (package G, 17 Nov): off = old behaviour
                    if (LabOff.Row(16)) return;
                    PatchingUtilities.PatchSystemRand(method, false);
                }),
                ("AI cast ability rnd field", () =>
                {
                    var type = RimDark40kRng.FindType(LogPrefix, "EBSGFramework.JobGiver_AICastAnyOfAbilityOnEnemyTarget");
                    var field = AccessTools.DeclaredField(type, "rnd")
                        ?? throw new MissingFieldException(type.FullName, "rnd");
                    if (!field.IsStatic || field.IsInitOnly || field.FieldType != typeof(Random))
                        throw new MissingFieldException($"{LogPrefix}{type.FullName}.rnd is {field}, expected a writable static System.Random");

                    // lab switch row 16 (package G, 17 Nov): off = old behaviour
                    if (LabOff.Row(16)) return;
                    field.SetValue(null, PatchingUtilities.RandRedirector.Instance);
                }));
            PatchSleepCasketConfirm();
        }

        // FIXES row 467. JobDriver_EnterSleepCasket's enter toil (job tick) adds Dialog_MessageBox.CreateConfirmation(CasketWarning,
        // action) when the last free colonist enters a non-ejectable casket; every PC shows it, the action (DeSpawnOrDeselect +
        // TryAcceptThing) ran on the clicking PC only. A confirmation built in the simulation in MP with that action is recorded under
        // (tick, order in that tick); Confirm sends SyncedCasketConfirm(tick, seq); the command runs that PC's own action and closes the
        // dialog on every PC. First command wins, later commands and later clicks on a decided dialog do nothing. "Go back" has no
        // action (nothing to sync). The direct action() call (other colonists free, ejectable casket) is not recorded: unchanged.
        private sealed class CasketEntry
        {
            public Game game;
            public int tick;
            public int seq;
            public Action action;
            public bool resolved;
        }

        private static readonly System.Collections.Generic.List<CasketEntry> casketEntries = new System.Collections.Generic.List<CasketEntry>();
        private static System.Reflection.MethodInfo casketAction;
        private static Game casketGame;
        private static int casketTick = int.MinValue;
        private static int casketSeq;

        internal static void PatchSleepCasketConfirm()
        {
            if (LabOff.Row(467))
            {
                Log.Message(LogPrefix + "row 467 switched off by the lab, sleep casket confirmation not synced");
                return;
            }
            var driver = AccessTools.TypeByName("EBSGFramework.JobDriver_EnterSleepCasket");
            casketAction = null;
            if (driver != null)
            {
                foreach (var nested in driver.GetNestedTypes(AccessTools.all))
                {
                    if (AccessTools.DeclaredField(nested, "pod") == null) continue;
                    foreach (var m in nested.GetMethods(AccessTools.all))
                        if (m.Name.StartsWith("<MakeNewToils>b__") && !m.IsStatic && m.ReturnType == typeof(void) && m.GetParameters().Length == 0)
                            casketAction = casketAction == null ? m : throw new Exception("more than one action lambda in " + nested.FullName);
                }
            }
            var confirm = AccessTools.DeclaredMethod(typeof(Dialog_MessageBox), nameof(Dialog_MessageBox.CreateConfirmation),
                new[] { typeof(TaggedString), typeof(Action), typeof(bool), typeof(string), typeof(WindowLayer) });
            if (casketAction == null || confirm == null)
            {
                Log.Error(LogPrefix + "row 467: JobDriver_EnterSleepCasket action lambda or Dialog_MessageBox.CreateConfirmation not found, the casket confirmation can desync");
                return;
            }
            Multiplayer.API.MP.RegisterSyncMethod(typeof(EBSGFramework), nameof(SyncedCasketConfirm));
            MpCompat.harmony.Patch(confirm, prefix: new HarmonyMethod(typeof(EBSGFramework), nameof(PreCreateConfirmation)));
            MpCompat.harmony.Patch(casketAction, prefix: new HarmonyMethod(typeof(EBSGFramework), nameof(PreCasketAction)));
            Log.Message(LogPrefix + "row 467: sleep casket last-colonist confirmation synced");
        }

        internal static void PreCreateConfirmation(Action confirmedAct)
        {
            if (confirmedAct == null || confirmedAct.Method != casketAction || !Multiplayer.API.MP.IsInMultiplayer || Multiplayer.API.MP.InInterface) return;
            var game = Current.Game;
            var tick = game?.tickManager?.TicksGame ?? -1;
            if (!ReferenceEquals(game, casketGame) || tick != casketTick)
            {
                casketGame = game;
                casketTick = tick;
                casketSeq = 0;
            }
            casketEntries.RemoveAll(e => !ReferenceEquals(e.game, game));
            casketEntries.Add(new CasketEntry { game = game, tick = tick, seq = casketSeq++, action = confirmedAct });
            if (casketEntries.Count > 64) casketEntries.RemoveAt(0);
        }

        internal static bool PreCasketAction(object __instance)
        {
            if (!Multiplayer.API.MP.IsInMultiplayer || Multiplayer.API.MP.IsExecutingSyncCommand) return true;
            var entry = casketEntries.Find(e => ReferenceEquals(e.action.Target, __instance) && ReferenceEquals(e.game, Current.Game));
            if (entry == null) return true;          // direct call from the toil (no dialog): unchanged
            if (!entry.resolved) SyncedCasketConfirm(entry.tick, entry.seq);
            return false;
        }

        internal static void SyncedCasketConfirm(int tick, int seq)
        {
            var entry = casketEntries.Find(e => e.tick == tick && e.seq == seq && ReferenceEquals(e.game, Current.Game));
            if (entry == null || entry.resolved) return;
            entry.resolved = true;
            entry.action();
            var windows = Current.Root?.uiRoot?.windows?.Windows;
            if (windows == null) return;
            foreach (var window in new System.Collections.Generic.List<Window>(windows))
                if (window is Dialog_MessageBox box && box.buttonAAction == entry.action) box.Close(false);
        }
    }
}
