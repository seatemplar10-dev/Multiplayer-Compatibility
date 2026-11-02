using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Vanilla Trading Expanded by Oskar Potocki, Taranchuk</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=2785616901"/>
    /// <remarks>
    /// FIXES row 393b (follow-up of row 393, 2 Nov 2026). The trading terminal's "Contact (bank)" float menu option
    /// (<c>Building_TradingTerminal.GetBankOptions</c>, lambda 0) writes the chosen bank into the terminal's saved field
    /// <c>currentVisitableFactionBank</c>, then orders the job <c>VTE_ContactBank</c>. The order is synced, the field write is not:
    /// it ran on the clicking PC only. When the pawn reached the terminal, <c>JobDriver_ContactBank</c> built <c>Window_Bank</c>
    /// from that field on every PC, so the other PCs built it with a null faction and threw, and the saved terminal differed
    /// between PCs. A transpiler turns the field write into a synced call, so every PC sets the field in the command queue
    /// before the job order that follows it (both are map commands of the same map, executed in order).
    /// Known and left: the terminal has one field, so two players who pick different banks in the same tick window share the
    /// later choice (the same on every PC). The bank window itself opens on every PC (UI only). Opening a window needs no sync,
    /// and what its buttons change is synced by <c>VanillaTradingExpanded</c> (deposit, withdraw, loans).
    /// </remarks>
    [MpCompatFor("VanillaExpanded.VanillaTradingExpanded")]
    class VanillaTradingExpandedTerminal
    {
        private static FieldInfo bankFactionField;
        private static AccessTools.FieldRef<object, Faction> bankFactionFieldRef;

        public VanillaTradingExpandedTerminal(ModContentPack mod)
        {
            try
            {
                var terminalType = AccessTools.TypeByName("VanillaTradingExpanded.Building_TradingTerminal")
                    ?? throw new Exception("type VanillaTradingExpanded.Building_TradingTerminal not found");
                bankFactionField = AccessTools.DeclaredField(terminalType, "currentVisitableFactionBank")
                    ?? throw new Exception("field Building_TradingTerminal.currentVisitableFactionBank not found");
                bankFactionFieldRef = AccessTools.FieldRefAccess<object, Faction>(bankFactionField);

                // The "Contact" option of GetBankOptions (an iterator): the only lambda there.
                var optionLambda = MpMethodUtil.GetLambda(terminalType, "GetBankOptions", lambdaOrdinal: 0)
                    ?? throw new Exception("lambda 0 of Building_TradingTerminal.GetBankOptions not found");
                // Count first, patch only on a match: a changed mod then logs an error and patches nothing.
                var stores = PatchProcessor.GetOriginalInstructions(optionLambda).Count(ci => ci.StoresField(bankFactionField));
                if (stores != 1)
                    throw new Exception($"the option lambda has {stores} writes to currentVisitableFactionBank, expected 1 (mod changed)");

                MP.RegisterSyncMethod(typeof(VanillaTradingExpandedTerminal), nameof(SyncedSetBankFaction)).CancelIfAnyArgNull();
                MpCompat.harmony.Patch(optionLambda, transpiler: new HarmonyMethod(typeof(VanillaTradingExpandedTerminal), nameof(BankOptionTranspiler)));
            }
            catch (Exception e)
            {
                Log.Error($"MPCompat :: Vanilla Trading Expanded: the terminal bank choice stays unsynced (the bank window breaks for the other players): {e.InnerException ?? e}");
            }
        }

        // The mod's "terminal.currentVisitableFactionBank = kvp.Key" (stack: terminal, faction) becomes a call with the same stack shape.
        private static IEnumerable<CodeInstruction> BankOptionTranspiler(IEnumerable<CodeInstruction> instr)
        {
            var replacement = AccessTools.Method(typeof(VanillaTradingExpandedTerminal), nameof(SyncedSetBankFaction));

            foreach (var ci in instr)
            {
                if (ci.StoresField(bankFactionField))
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = replacement;
                }

                yield return ci;
            }
        }

        private static void SyncedSetBankFaction(Thing terminal, Faction faction) => bankFactionFieldRef(terminal) = faction;
    }
}
