using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Vanilla Trading Expanded by Oskar Potocki, Taranchuk</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=2785616901"/>
    /// <remarks>
    /// Determinism only (lane r3-worldtick VTE, 13 Oct 2026). VTE fills <c>Utils.markupRange</c> (1000 floats) with
    /// <c>Rand.Range</c> once at startup (<c>Utils.Initialize</c>, a postfix on <c>StaticConstructorOnStartupUtility.CallAll</c>)
    /// and again from <c>VanillaTradingExpandedMod.WriteSettings</c>. No game exists then, so Rand still runs on its
    /// startup seed (<c>DateTime.Now</c>) and every process gets its own table. <c>Contract.GenerateReward</c> picks the
    /// reward from that table, so a contract generated in the shared world tick (or by <c>TradingManager.Startup</c>
    /// on a load) gets a different reward on every peer. Every 30000 ticks <c>TradingManager.ProcessContracts</c> rolls
    /// <c>Rand.Chance(contract.ContractFulfilmentChance() * ...)</c> on that reward; a different outcome regenerates a
    /// contract on one peer only = a different number of world Rand calls ("Wrong random state for the world").
    /// Fix: build the table from a fixed seed, at every call site and again before every game init/load, so all peers
    /// hold the same table for the same (synced) <c>maxMarkupOnNPCContract</c>.
    /// Hardening (r4, 13 Oct 2026): <c>TradingManager.FinalizeInit/LoadedGame/StartedNewGame</c> all run <c>Startup()</c>,
    /// which tops up companies, banks and NPC contracts with Rand. Each now draws from a pushed state seeded by the world
    /// seed and <c>TicksGame</c> (both saved), so what a load generates no longer depends on how many Rand calls other
    /// code made earlier in that load (a first-load joiner vs an Nth-load host), and VTE no longer advances the
    /// load-time stream for the code after it. VTE never sets <c>Rand.Seed</c> or pushes/pops itself, so the only
    /// single-machine effect is the seed its draws come from.
    /// FIXES row 393 (mod sweep 2 Nov 2026): the bank window's Confirm button (<c>Window_Bank</c>) called <c>Bank.DepositSilver</c> /
    /// <c>Bank.WithdrawSilver</c> on the clicking PC only, so the silver on the map (destroyed or dropped by pod) and the saved
    /// <c>depositAmount</c> split. Both methods are synced now; the <c>Bank</c> target travels as its faction (<c>parentFaction</c>,
    /// looked up again with <c>Utils.GetBank</c>). The silver stacks the deposit takes from travel as a <c>List&lt;Thing&gt;</c>.
    /// FIXES row 393c (follow-up, 2 Nov 2026): the loan buttons of <c>Window_Bank</c> ("Take loan", "Repay from balance") called
    /// <c>Bank.TakeLoan</c> / <c>Bank.TryRepayFromBalance</c> on the clicking PC only (a new loan, the silver pod, the
    /// <c>loanLastTimeTaken</c> entry, a lower balance). Both are synced now. <c>LoanOption</c> and <c>Loan</c> have no id, so each
    /// travels as the faction of its bank plus its index in <c>bank.bankExtension.loanOptions</c> / <c>bank.loans</c>; a loan is checked
    /// against its saved amounts and found again by them if the list moved between click and command. <c>TryRepayFromBalance</c> is
    /// also called by the bank itself (<c>Bank.Tick</c>, and the <c>DepositAmount</c> setter): MP only turns a call from the UI into a
    /// command, so the tick path is unchanged. The setter also runs in UI time inside the unsynced stock market and contract
    /// transactions, which then queue one repay command per overdue loan (the transaction itself stays a known gap). At execution a
    /// <c>TakeLoan</c> is dropped when the window would not offer the button (loan already taken, cooldown running, no balance and no
    /// fixed amount: a second player's click, or a withdrawal, arrived first) or when the negotiator has no map.
    /// Opening the bank window from the terminal (row 393b) is a separate module, <c>VanillaTradingExpandedTerminal</c>.
    /// Not covered here: contracts, stock market, transactions. Each bank is shared by all player factions (one saved balance).
    /// </remarks>
    [MpCompatFor("VanillaExpanded.VanillaTradingExpanded")]
    public class VanillaTradingExpanded
    {
        internal const string MarkupSeedKey = "MpCompat.VanillaTradingExpanded.Utils.markupRange";
        internal const string StartupSeedKey = "MpCompat.VanillaTradingExpanded.TradingManager.";

        private static MethodInfo initMarkupValuesMethod;
        private static MethodInfo getBankMethod;
        private static AccessTools.FieldRef<object, Faction> bankParentFactionField;
        private static Type bankWindowType;
        private static AccessTools.FieldRef<object, List<Thing>> bankWindowCachedThingsField;

        // Row 393c
        private static FieldInfo managerInstanceField;
        private static MethodInfo managerBanksGetter;
        private static FieldInfo bankLoansField;
        private static FieldInfo bankExtensionField;
        private static FieldInfo extensionLoanOptionsField;
        private static FieldInfo loanOptionIdField;
        private static FieldInfo loanRepayDateField;
        private static FieldInfo loanInitialRepayAmountField;
        private static MethodInfo loanIsTakenMethod;
        private static MethodInfo bankLastTimeLoanTakenMethod;
        private static MethodInfo bankDepositAmountGetter;
        private static MethodInfo loanOptionRepayTicksMethod;
        private static FieldInfo loanOptionFixedLoanAmountField;

        public VanillaTradingExpanded(ModContentPack mod)
        {
            var utilsType = AccessTools.TypeByName("VanillaTradingExpanded.Utils");
            initMarkupValuesMethod = AccessTools.DeclaredMethod(utilsType, "InitMarkupValues");

            // Every caller (Utils.Initialize at startup, Mod.WriteSettings) builds the table from the same seed.
            MpCompat.harmony.Patch(initMarkupValuesMethod,
                prefix: new HarmonyMethod(typeof(VanillaTradingExpanded), nameof(PreInitMarkupValues)),
                finalizer: new HarmonyMethod(typeof(VanillaTradingExpanded), nameof(FinalizeInitMarkupValues)));

            // Rebuild it before each game init/load (FinalizeInit runs for new and loaded games, before Startup()
            // generates contracts), so it is right even if this module was constructed after Utils.Initialize or
            // the synced mod settings changed maxMarkupOnNPCContract after startup.
            var managerType = AccessTools.TypeByName("VanillaTradingExpanded.TradingManager");
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(managerType, "FinalizeInit"),
                prefix: new HarmonyMethod(typeof(VanillaTradingExpanded), nameof(RebuildMarkupValues)));

            // Hardening: every Startup() caller draws from its own seeded state (see remarks).
            foreach (var name in new[] { "FinalizeInit", "LoadedGame", "StartedNewGame" })
            {
                MpCompat.harmony.Patch(AccessTools.DeclaredMethod(managerType, name),
                    prefix: new HarmonyMethod(typeof(VanillaTradingExpanded), nameof(PreSeededStartup)),
                    finalizer: new HarmonyMethod(typeof(VanillaTradingExpanded), nameof(FinalizeSeededStartup)));
            }

            // Row 393. Its own try/catch: a changed bank class must not take the determinism patches above down with it.
            try
            {
                RegisterBankSyncs(utilsType);
            }
            catch (Exception e)
            {
                Log.Error($"MPCompat :: Vanilla Trading Expanded: the bank deposit/withdraw sync is not fully registered: {e.InnerException ?? e}");
            }
        }

        private static void RegisterBankSyncs(Type utilsType)
        {
            var bankType = AccessTools.TypeByName("VanillaTradingExpanded.Bank")
                ?? throw new Exception("type VanillaTradingExpanded.Bank not found");
            getBankMethod = AccessTools.DeclaredMethod(utilsType, "GetBank", new[] { typeof(Faction) })
                ?? throw new Exception("method Utils.GetBank(Faction) not found");
            bankParentFactionField = AccessTools.FieldRefAccess<object, Faction>(AccessTools.DeclaredField(bankType, "parentFaction")
                ?? throw new Exception("field Bank.parentFaction not found"));
            var deposit = AccessTools.DeclaredMethod(bankType, "DepositSilver", new[] { typeof(List<Thing>), typeof(int) })
                ?? throw new Exception("method Bank.DepositSilver(List<Thing>, int) not found");
            var withdraw = AccessTools.DeclaredMethod(bankType, "WithdrawSilver", new[] { typeof(Pawn), typeof(int) })
                ?? throw new Exception("method Bank.WithdrawSilver(Pawn, int) not found");

            var windowType = AccessTools.TypeByName("VanillaTradingExpanded.Window_Bank")
                ?? throw new Exception("type VanillaTradingExpanded.Window_Bank not found");
            bankWindowType = windowType;
            bankWindowCachedThingsField = AccessTools.FieldRefAccess<object, List<Thing>>(AccessTools.DeclaredField(windowType, "cachedThings")
                ?? throw new Exception("field Window_Bank.cachedThings not found"));

            MP.RegisterSyncWorker<object>(SyncBank, bankType);
            // The window nulls its silver cache when Confirm is clicked, but the command runs later: the cache then refills from stacks
            // the deposit is about to destroy, and the next deposit would send them (read back as null, so the deposit stops early).
            // Drop gone stacks before the deposit runs, and clear every open bank window's cache once either command has run.
            MP.RegisterSyncMethod(deposit).SetPreInvoke(DropGoneSilver).SetPostInvoke(ClearBankWindowCaches);
            MP.RegisterSyncMethod(withdraw).SetPostInvoke(ClearBankWindowCaches);
            // A negotiator that left the map (dead, in a caravan) has no map: the original lowers the balance first, then throws on
            // negotiator.Map. Skip the call instead. Same answer on every PC, runs on the sending PC too.
            MpCompat.harmony.Patch(withdraw, prefix: new HarmonyMethod(typeof(VanillaTradingExpanded), nameof(PreWithdrawSilver)));

            // Row 393c. Own try/catch: a changed loan class must not undo the deposit and withdraw syncs above.
            try
            {
                RegisterLoanSyncs(bankType);
            }
            catch (Exception e)
            {
                Log.Error($"MPCompat :: Vanilla Trading Expanded: the bank loan sync (take, repay from balance) is not fully registered: {e.InnerException ?? e}");
            }
        }

        private static void RegisterLoanSyncs(Type bankType)
        {
            var loanType = AccessTools.TypeByName("VanillaTradingExpanded.Loan")
                ?? throw new Exception("type VanillaTradingExpanded.Loan not found");
            var loanOptionType = AccessTools.TypeByName("VanillaTradingExpanded.LoanOption")
                ?? throw new Exception("type VanillaTradingExpanded.LoanOption not found");
            var extensionType = AccessTools.TypeByName("VanillaTradingExpanded.BankExtension")
                ?? throw new Exception("type VanillaTradingExpanded.BankExtension not found");
            var managerType = AccessTools.TypeByName("VanillaTradingExpanded.TradingManager")
                ?? throw new Exception("type VanillaTradingExpanded.TradingManager not found");

            managerInstanceField = AccessTools.DeclaredField(managerType, "Instance")
                ?? throw new Exception("field TradingManager.Instance not found");
            managerBanksGetter = AccessTools.DeclaredPropertyGetter(managerType, "Banks")
                ?? throw new Exception("property TradingManager.Banks not found");
            bankLoansField = AccessTools.DeclaredField(bankType, "loans")
                ?? throw new Exception("field Bank.loans not found");
            bankExtensionField = AccessTools.DeclaredField(bankType, "bankExtension")
                ?? throw new Exception("field Bank.bankExtension not found");
            extensionLoanOptionsField = AccessTools.DeclaredField(extensionType, "loanOptions")
                ?? throw new Exception("field BankExtension.loanOptions not found");
            loanOptionIdField = AccessTools.DeclaredField(loanType, "loanOptionId")
                ?? throw new Exception("field Loan.loanOptionId not found");
            loanRepayDateField = AccessTools.DeclaredField(loanType, "repayDate")
                ?? throw new Exception("field Loan.repayDate not found");
            loanInitialRepayAmountField = AccessTools.DeclaredField(loanType, "initialRepayAmount")
                ?? throw new Exception("field Loan.initialRepayAmount not found");
            loanIsTakenMethod = AccessTools.DeclaredMethod(bankType, "LoanIsTaken")
                ?? throw new Exception("method Bank.LoanIsTaken not found");
            bankLastTimeLoanTakenMethod = AccessTools.DeclaredMethod(bankType, "LastTimeLoanWasTaken", new[] { loanOptionType })
                ?? throw new Exception("method Bank.LastTimeLoanWasTaken(LoanOption) not found");
            bankDepositAmountGetter = AccessTools.DeclaredPropertyGetter(bankType, "DepositAmount")
                ?? throw new Exception("property Bank.DepositAmount not found");
            loanOptionRepayTicksMethod = AccessTools.DeclaredMethod(loanOptionType, "GetRepayDateTicks")
                ?? throw new Exception("method LoanOption.GetRepayDateTicks not found");
            loanOptionFixedLoanAmountField = AccessTools.DeclaredField(loanOptionType, "fixedLoanAmount")
                ?? throw new Exception("field LoanOption.fixedLoanAmount not found");
            var takeLoan = AccessTools.DeclaredMethod(bankType, "TakeLoan", new[] { typeof(Pawn), typeof(int), typeof(int), typeof(int), loanOptionType })
                ?? throw new Exception("method Bank.TakeLoan(Pawn, int, int, int, LoanOption) not found");
            var repay = AccessTools.DeclaredMethod(bankType, "TryRepayFromBalance", new[] { loanType })
                ?? throw new Exception("method Bank.TryRepayFromBalance(Loan) not found");

            MP.RegisterSyncWorker<object>(SyncLoanOption, loanOptionType);
            MP.RegisterSyncWorker<object>(SyncLoan, loanType);
            MP.RegisterSyncMethod(takeLoan).CancelIfAnyArgNull();
            MP.RegisterSyncMethod(repay).CancelIfAnyArgNull();
            MpCompat.harmony.Patch(takeLoan, prefix: new HarmonyMethod(typeof(VanillaTradingExpanded), nameof(PreTakeLoan)));
        }

        // The sending PC also runs this (MP syncs with a transpiler, so a prefix runs before the sync check): only the map test
        // applies there. The window's own conditions are checked at execution only, in the command's map time.
        private static bool PreTakeLoan(object __instance, Pawn negotiator, object loanOption)
            => negotiator?.Map != null
            && !(MP.IsInMultiplayer && MP.IsExecutingSyncCommand && LoanBlocked(__instance, loanOption));

        // When DrawLoanOption shows no "Take loan" button: the option has a loan, its cooldown runs, or there is no balance and no fixed amount.
        private static bool LoanBlocked(object bank, object loanOption)
        {
            if ((bool)loanIsTakenMethod.Invoke(bank, new[] { loanOption, null }))
                return true;
            var lastTaken = (int)bankLastTimeLoanTakenMethod.Invoke(bank, new[] { loanOption });
            if (lastTaken > -1 && lastTaken + (int)loanOptionRepayTicksMethod.Invoke(loanOption, null) > Find.TickManager.TicksAbs)
                return true;
            return (int)bankDepositAmountGetter.Invoke(bank, null) <= 0 && loanOptionFixedLoanAmountField.GetValue(loanOption) == null;
        }

        // Banks of factions that are gone from the faction manager are skipped: the Faction reader could not find them again.
        private static IEnumerable<object> AllBanks()
        {
            var manager = managerInstanceField.GetValue(null);
            if (manager == null)
                yield break;
            foreach (var bank in (IEnumerable)managerBanksGetter.Invoke(manager, null))
            {
                if (bankParentFactionField(bank) is { } faction && Find.FactionManager.AllFactionsListForReading.Contains(faction))
                    yield return bank;
            }
        }

        private static IList LoanOptionsOf(object bank)
        {
            var extension = bank == null ? null : bankExtensionField.GetValue(bank);
            return extension == null ? null : extensionLoanOptionsField.GetValue(extension) as IList;
        }

        private static IList LoansOf(object bank) => bank == null ? null : bankLoansField.GetValue(bank) as IList;

        private static object BankOf(Faction faction) => faction == null ? null : getBankMethod.Invoke(null, new object[] { faction });

        // A LoanOption is one entry of its bank's bankExtension.loanOptions: sent as that bank's faction and the index.
        private static void SyncLoanOption(SyncWorker sync, ref object loanOption)
        {
            if (sync.isWriting)
            {
                Faction faction = null;
                var index = -1;
                if (loanOption != null)
                {
                    foreach (var bank in AllBanks())
                    {
                        index = LoanOptionsOf(bank)?.IndexOf(loanOption) ?? -1;
                        if (index >= 0)
                        {
                            faction = bankParentFactionField(bank);
                            break;
                        }
                    }
                }

                sync.Write(index >= 0);
                if (index >= 0)
                {
                    sync.Write<Faction>(faction);
                    sync.Write(index);
                }
            }
            else
            {
                loanOption = null;
                if (!sync.Read<bool>())
                    return;
                var options = LoanOptionsOf(BankOf(sync.Read<Faction>()));
                var index = sync.Read<int>();
                if (options != null && index >= 0 && index < options.Count)
                    loanOption = options[index];
            }
        }

        private static bool LoanMatches(object loan, int optionId, int repayDate, int initialRepayAmount)
            => (int)loanOptionIdField.GetValue(loan) == optionId
            && (int)loanRepayDateField.GetValue(loan) == repayDate
            && (int)loanInitialRepayAmountField.GetValue(loan) == initialRepayAmount;

        // A Loan has no id: it is sent as its bank's faction and its index in bank.loans, plus the three amounts that never change
        // (option, repay date, initial repay amount). The list can lose a loan between click and command, so the index is checked
        // and the loan is searched by those amounts when it moved. Null when it is gone (repaid meanwhile): the call is cancelled.
        private static void SyncLoan(SyncWorker sync, ref object loan)
        {
            if (sync.isWriting)
            {
                Faction faction = null;
                var index = -1;
                if (loan != null)
                {
                    foreach (var bank in AllBanks())
                    {
                        index = LoansOf(bank)?.IndexOf(loan) ?? -1;
                        if (index >= 0)
                        {
                            faction = bankParentFactionField(bank);
                            break;
                        }
                    }
                }

                sync.Write(index >= 0);
                if (index >= 0)
                {
                    sync.Write<Faction>(faction);
                    sync.Write(index);
                    sync.Write((int)loanOptionIdField.GetValue(loan));
                    sync.Write((int)loanRepayDateField.GetValue(loan));
                    sync.Write((int)loanInitialRepayAmountField.GetValue(loan));
                }
            }
            else
            {
                loan = null;
                if (!sync.Read<bool>())
                    return;
                var loans = LoansOf(BankOf(sync.Read<Faction>()));
                var index = sync.Read<int>();
                var optionId = sync.Read<int>();
                var repayDate = sync.Read<int>();
                var initialRepayAmount = sync.Read<int>();
                if (loans == null)
                    return;
                if (index >= 0 && index < loans.Count && LoanMatches(loans[index], optionId, repayDate, initialRepayAmount))
                {
                    loan = loans[index];
                    return;
                }

                foreach (var candidate in loans)
                {
                    if (LoanMatches(candidate, optionId, repayDate, initialRepayAmount))
                    {
                        loan = candidate;
                        return;
                    }
                }
            }
        }

        private static void DropGoneSilver(object instance, object[] args)
        {
            if (args[0] is List<Thing> silvers)
                silvers.RemoveAll(thing => thing == null || thing.Destroyed);
        }

        private static void ClearBankWindowCaches(object instance, object[] args)
        {
            var windows = Find.WindowStack?.Windows;
            if (windows == null)
                return;
            foreach (var window in windows)
            {
                if (window.GetType() == bankWindowType)
                    bankWindowCachedThingsField(window) = null;
            }
        }

        private static bool PreWithdrawSilver(Pawn negotiator) => negotiator?.Map != null;

        // A Bank is one per faction (TradingManager.banksByFaction), so the faction identifies it. Null on a missing bank.
        private static void SyncBank(SyncWorker sync, ref object bank)
        {
            if (sync.isWriting)
            {
                sync.Write<Faction>(bank == null ? null : bankParentFactionField(bank));
            }
            else
            {
                var faction = sync.Read<Faction>();
                bank = faction == null ? null : getBankMethod.Invoke(null, new object[] { faction });
            }
        }

        private static void PreInitMarkupValues(ref bool __state)
        {
            Rand.PushState(GenText.StableStringHash(MarkupSeedKey));
            __state = true;
        }

        private static void FinalizeInitMarkupValues(bool __state)
        {
            if (__state)
                Rand.PopState();
        }

        private static void RebuildMarkupValues() => initMarkupValuesMethod.Invoke(null, null);

        // Same on every peer: the key, the saved world seed and the saved game tick. Null-safe (0) if a part is missing.
        internal static int StartupSeed(string method)
        {
            var game = Current.Game;
            var worldSeed = game?.World?.info?.Seed ?? 0;
            var ticks = game?.tickManager?.TicksGame ?? 0;
            return Gen.HashCombineInt(Gen.HashCombineInt(GenText.StableStringHash(StartupSeedKey + method), worldSeed), ticks);
        }

        private static void PreSeededStartup(MethodBase __originalMethod, ref bool __state)
        {
            Rand.PushState(StartupSeed(__originalMethod.Name));
            __state = true;
        }

        private static void FinalizeSeededStartup(bool __state)
        {
            if (__state)
                Rand.PopState();
        }
    }
}
