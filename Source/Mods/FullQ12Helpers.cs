using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>FULL1127 class Q12, the 4 helper rows: Faction.OfPlayer read inside a world/game component tick replaced by the right player faction.</summary>
    internal static class FullQ12
    {
        private static readonly MethodInfo ofPlayerGetter = AccessTools.PropertyGetter(typeof(Faction), nameof(Faction.OfPlayer));
        private static readonly MethodInfo thingFactionGetter = AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.Faction));

        /// <summary>Swapped read counts per method (Type:Method), written when the transpiler runs (read by the D2 check).</summary>
        public static readonly Dictionary<string, int> Swapped = new Dictionary<string, int>();

        /// <summary>Rows with RightPlayerFaction.Resolve: every "Thing.get_Faction; Faction.get_OfPlayer" pair becomes "get_Faction; dup; Resolve(pawnFaction)".</summary>
        public static void PatchResolve(string mod, string[] methods)
        {
            int ok = 0;
            foreach (var name in methods)
            {
                var m = AccessTools.DeclaredMethod(name) ?? AccessTools.Method(name);
                if (m == null) { Log.Error($"[MPCompat - FULL1127 Q12] {mod}: method {name} not found"); continue; }
                MpCompat.harmony.Patch(m, transpiler: new HarmonyMethod(typeof(FullQ12), nameof(ResolveTranspiler)));
                ok++;
            }
            Log.Message($"[MPCompat - FULL1127 Q12] {mod}: {ok} of {methods.Length} methods patched");
        }

        private static IEnumerable<CodeInstruction> ResolveTranspiler(IEnumerable<CodeInstruction> instr, MethodBase __originalMethod)
        {
            var list = instr.ToList();
            var resolve = AccessTools.Method(typeof(RightPlayerFaction), nameof(RightPlayerFaction.Resolve));
            int n = 0;
            for (int i = 1; i < list.Count; i++)
            {
                if (!list[i].Calls(ofPlayerGetter) || !list[i - 1].Calls(thingFactionGetter)) continue;
                list.Insert(i, new CodeInstruction(OpCodes.Dup));
                list[i + 1].opcode = OpCodes.Call;
                list[i + 1].operand = resolve;
                n++;
            }
            Swapped[__originalMethod.DeclaringType.Name + ":" + __originalMethod.Name] = n;
            if (n == 0) Log.Warning($"[MPCompat - FULL1127 Q12] {__originalMethod.Name}: no pawn.Faction then Faction.OfPlayer read found, nothing patched");
            return list;
        }

        /// <summary>Row with PlayerFactionForTick.ForMap: every OfPlayer read of the method becomes ForMap(the Map argument).</summary>
        public static void PatchForMap(string mod, string method, int expected)
        {
            var m = AccessTools.DeclaredMethod(method) ?? AccessTools.Method(method);
            if (m == null) { Log.Error($"[MPCompat - FULL1127 Q12] {mod}: method {method} not found"); return; }
            int idx = m.GetParameters().ToList().FindIndex(p => p.ParameterType == typeof(Map));
            if (idx < 0) { Log.Error($"[MPCompat - FULL1127 Q12] {mod}: {method} has no Map parameter"); return; }
            expectedFor[m] = (idx + (m.IsStatic ? 0 : 1), expected);
            MpCompat.harmony.Patch(m, transpiler: new HarmonyMethod(typeof(FullQ12), nameof(ForMapTranspiler)));
            Log.Message($"[MPCompat - FULL1127 Q12] {mod}: 1 of 1 methods patched");
        }

        private static readonly Dictionary<MethodBase, (int arg, int expected)> expectedFor = new Dictionary<MethodBase, (int, int)>();

        private static IEnumerable<CodeInstruction> ForMapTranspiler(IEnumerable<CodeInstruction> instr, MethodBase __originalMethod)
        {
            var (arg, expected) = expectedFor[__originalMethod];
            var result = PlayerFactionForTick.SwapOfPlayer(instr, () => new CodeInstruction(OpCodes.Ldarg, arg), expected, "FULL1127 Q12 " + __originalMethod.Name).ToList();
            var forMap = AccessTools.Method(typeof(PlayerFactionForTick), nameof(PlayerFactionForTick.ForMap));
            Swapped[__originalMethod.DeclaringType.Name + ":" + __originalMethod.Name] = result.Count(c => c.Calls(forMap));
            return result;
        }
    }

    [MpCompatFor("pribovich.memorialstele")]
    class FullMemorialStele
    {
        public FullMemorialStele(ModContentPack mod) =>
            FullQ12.PatchResolve("MemorialStele", new[] { "MemorialStele.GameComponent_MemorialTracker:GameComponentTick" });
    }

    [MpCompatFor("astryl.modernsocialtab")]
    class FullModernSocialTab
    {
        public FullModernSocialTab(ModContentPack mod) =>
            FullQ12.PatchResolve("ModernSocialTab", new[] { "ModernSocialTab.OpinionHistoryComp:CollectColony" });
    }

    [MpCompatFor("forti.rimtoll")]
    class FullRimToll
    {
        public FullRimToll(ModContentPack mod) =>
            FullQ12.PatchResolve("RimToll", new[] { "RimToll.GameComponent_TollState:ReleaseWorker" });
    }

    [MpCompatFor("ninagoblin.visitsettlements")]
    class FullVisitSettlements
    {
        public FullVisitSettlements(ModContentPack mod) =>
            FullQ12.PatchForMap("VisitSettlements", "VS_WorldComponent_SettlementEvents:TriggerRaidEvent", 1);
    }
}
