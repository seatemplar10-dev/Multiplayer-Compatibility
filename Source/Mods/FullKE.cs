using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>FULL1127 class KE (K: view-gated writes, E: real time/input in the sim). Shared helpers for the KE tables.
    /// Rows for mods that already have a module (AlphaMemes, BiomesCore) are added inside those modules and use <see cref="PatchCurrentMap"/>.</summary>
    internal static class FullKE
    {
        private static readonly MethodInfo RealtimeGetter = AccessTools.PropertyGetter(typeof(UnityEngine.Time), nameof(UnityEngine.Time.realtimeSinceStartup));
        private static readonly MethodInfo DateTimeTicksGetter = AccessTools.PropertyGetter(typeof(DateTime), nameof(DateTime.Ticks));

        /// <summary>ReplaceCurrentMapUsage for each table row. Returns how many methods were found.</summary>
        public static int PatchCurrentMap(string mod, string[] methods)
        {
            var found = 0;
            foreach (var name in methods)
            {
                var m = AccessTools.DeclaredMethod(name) ?? AccessTools.Method(name);
                if (m == null) { Log.Error($"[MPCompat - FULL1127 KE] {mod}: method {name} not found"); continue; }
                PatchingUtilities.ReplaceCurrentMapUsage(m);
                found++;
            }
            Log.Message($"[MPCompat - FULL1127 KE] {mod}: {found} of {methods.Length} methods patched");
            return found;
        }

        /// <summary>Patches each method with the named transpiler of this class. Returns how many methods were found.</summary>
        public static int PatchTranspiler(string mod, string[] methods, string transpilerName)
        {
            var found = 0;
            foreach (var name in methods)
            {
                var m = AccessTools.DeclaredMethod(name) ?? AccessTools.Method(name);
                if (m == null) { Log.Error($"[MPCompat - FULL1127 KE] {mod}: method {name} not found"); continue; }
                MpCompat.harmony.Patch(m, transpiler: new HarmonyMethod(typeof(FullKE), transpilerName));
                found++;
            }
            Log.Message($"[MPCompat - FULL1127 KE] {mod}: {found} of {methods.Length} methods patched");
            return found;
        }

        // CashRegister: Time.realtimeSinceStartup (0.7 s cache) -> TicksGame / 60f
        public static float SimSeconds() => Current.Game != null && Find.TickManager != null ? Find.TickManager.TicksGame / 60f : UnityEngine.Time.realtimeSinceStartup;

        public static IEnumerable<CodeInstruction> RealtimeToTicksTranspiler(IEnumerable<CodeInstruction> instr)
        {
            var simSeconds = AccessTools.Method(typeof(FullKE), nameof(SimSeconds));
            foreach (var ci in instr)
            {
                if (ci.Calls(RealtimeGetter)) { ci.opcode = OpCodes.Call; ci.operand = simSeconds; }
                yield return ci;
            }
        }

        // ForceDoJob: DateTime.Now.Ticks (1 s throttle, 5 s self-tend timer) -> TicksGame in 100 ns units (10,000,000 per 60 ticks)
        public static long SimTicks(ref DateTime now) => Current.Game != null && Find.TickManager != null ? Find.TickManager.TicksGame * (10000000L / 60L) : now.Ticks;

        public static IEnumerable<CodeInstruction> DateTimeTicksTranspiler(IEnumerable<CodeInstruction> instr)
        {
            var simTicks = AccessTools.Method(typeof(FullKE), nameof(SimTicks));
            foreach (var ci in instr)
            {
                if (ci.Calls(DateTimeTicksGetter)) { ci.opcode = OpCodes.Call; ci.operand = simTicks; }
                yield return ci;
            }
        }

        // MojaveMask: Find.WindowStack.Windows.Any(x => x.absorbInputAroundWindow) -> false (no deferral on local windows)
        public static bool NoWindowBlock(IEnumerable<Window> windows, Func<Window, bool> predicate) => false;

        public static IEnumerable<CodeInstruction> DropWindowDeferralTranspiler(IEnumerable<CodeInstruction> instr)
        {
            var anyWindow = typeof(Enumerable).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.Name == nameof(Enumerable.Any) && m.GetParameters().Length == 2).MakeGenericMethod(typeof(Window));
            var noBlock = AccessTools.Method(typeof(FullKE), nameof(NoWindowBlock));
            foreach (var ci in instr)
            {
                if (ci.Calls(anyWindow)) { ci.opcode = OpCodes.Call; ci.operand = noBlock; }
                yield return ci;
            }
        }
    }

    /// <summary>Mechanoid Upgrades by gogatio</summary>
    [MpCompatFor("gogatio.mechanoidupgrades")]
    class FullMechanoidUpgrades
    {
        private static readonly string[] CurrentMapMethods = { "MU.Patch_CommandRange:Postfix" };
        public FullMechanoidUpgrades(ModContentPack mod) => FullKE.PatchCurrentMap("Mechanoid Upgrades", CurrentMapMethods);
    }

    /// <summary>BioReactor by MLie</summary>
    [MpCompatFor("mlie.bioreactor")]
    class FullBioReactor
    {
        private static readonly string[] CurrentMapMethods = { "BioReactor.Building_BioReactor:makeFuel" };
        public FullBioReactor(ModContentPack mod) => FullKE.PatchCurrentMap("BioReactor", CurrentMapMethods);
    }

    /// <summary>Visit Settlements: Frame.CompleteConstruction postfix reads Find.CurrentMap.Tile; the helper swaps it for __instance.Map.</summary>
    [MpCompatFor("ninagoblin.visitsettlements")]
    class FullVisitSettlements
    {
        private static readonly string[] CurrentMapMethods = { "VS_PenalizeForBuilding:Postfix" };
        public FullVisitSettlements(ModContentPack mod) => FullKE.PatchCurrentMap("Visit Settlements", CurrentMapMethods);
    }

    /// <summary>Invader Mechanoids (EL_IM): time-seeded System.Random field used to pick the cast target.</summary>
    [MpCompatFor("el.invmech")]
    class FullInvaderMechanoids
    {
        // The 'new Random()' is the field initializer, so it lives in the constructor; GetTarget only calls rand.Next, which the RandRedirector then routes to Rand.Range.
        private static readonly string[] RandCtorTypes = { "EL_IM.JobGiver_AICastAbilityOnFactionMembers" };

        public FullInvaderMechanoids(ModContentPack mod)
        {
            var found = 0;
            foreach (var name in RandCtorTypes)
            {
                var type = AccessTools.TypeByName(name);
                var ctor = type == null ? null : AccessTools.DeclaredConstructor(type) ?? AccessTools.Constructor(type);
                if (ctor == null) { Log.Error($"[MPCompat - FULL1127 KE] Invader Mechanoids: constructor of {name} not found"); continue; }
                PatchingUtilities.PatchSystemRand(ctor, false);
                found++;
            }
            Log.Message($"[MPCompat - FULL1127 KE] Invader Mechanoids: {found} of {RandCtorTypes.Length} methods patched");
        }
    }

    /// <summary>Force Do Job: the self-tend timer used DateTime.Now; now TicksGame. The timer is written in the float menu prefix, so both ends are patched.</summary>
    [MpCompatFor("forcedojob.kv.rw")]
    class FullForceDoJob
    {
        private static readonly string[] Methods =
        {
            "ForceDoJob.WorldComp:WorldComponentTick",
            "ForceDoJob.Patch_FloatMenuMakerMap_ChoicesAtFor:Prefix",
        };
        public FullForceDoJob(ModContentPack mod)
        {
            FullKE.PatchTranspiler("Force Do Job", Methods, nameof(FullKE.DateTimeTicksTranspiler));
            FullCust2.PatchForceDoJob(); // P3CUST: ChoicesForPawn static + synced self-tend write
        }
    }

    /// <summary>Cash Register by Orion: IsActive and HasToWork cached on Time.realtimeSinceStartup.</summary>
    [MpCompatFor("orion.cashregister")]
    class FullCashRegister
    {
        private static readonly string[] Methods =
        {
            "CashRegister.Building_CashRegister:get_IsActive",
            "CashRegister.Building_CashRegister:HasToWork",
        };
        public FullCashRegister(ModContentPack mod) => FullKE.PatchTranspiler("Cash Register", Methods, nameof(FullKE.RealtimeToTicksTranspiler));
    }

    /// <summary>Mojave Mask: State.DoSwap deferred the storyteller swap while any local window absorbed input.</summary>
    [MpCompatFor("ferny.mojavemask")]
    class FullMojaveMask
    {
        private static readonly string[] Methods = { "MojaveMask.State:DoSwap" };
        public FullMojaveMask(ModContentPack mod) => FullKE.PatchTranspiler("Mojave Mask", Methods, nameof(FullKE.DropWindowDeferralTranspiler));
    }
}
