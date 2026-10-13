using HarmonyLib;
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
    /// Not covered here: VTE's windows (bank, contracts, stock market, transactions) are not synced.
    /// </remarks>
    [MpCompatFor("VanillaExpanded.VanillaTradingExpanded")]
    public class VanillaTradingExpanded
    {
        internal const string MarkupSeedKey = "MpCompat.VanillaTradingExpanded.Utils.markupRange";
        internal const string StartupSeedKey = "MpCompat.VanillaTradingExpanded.TradingManager.";

        private static MethodInfo initMarkupValuesMethod;

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
