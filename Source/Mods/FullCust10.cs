using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>FULL1127 CU10 (FIXES row 542 custom rows). Reads of Faction.OfPlayer in world/game component ticks, where it is the spectator (faction 69).
    /// Tech-level and leader rows use the one real player faction both PCs choose the same way (FullSemiRandomResearch.TechFaction: OfPlayer when real, else the
    /// lowest-loadID non-spectator player faction). Map rows use the map's own player faction (PlayerFactionForTick.ForMap).</summary>
    internal static class FullCust10
    {
        public const string Log10 = "[MPCompat - FULL1127 CU10] ";
        static readonly MethodInfo techFactionM = AccessTools.Method(typeof(FullSemiRandomResearch), nameof(FullSemiRandomResearch.TechFaction));

        /// <summary>Replace every Faction.get_OfPlayer call in the method by TechFaction().</summary>
        public static void PatchTech(string spec, string what)
        {
            var m = AccessTools.Method(spec);
            if (m == null) { Log.Error(Log10 + what + ": " + spec + " not found"); return; }
            MpCompat.harmony.Patch(m, transpiler: new HarmonyMethod(typeof(FullCust10), nameof(TrTech)));
            Log.Message(Log10 + what + ": OfPlayer reads -> TechFaction registered");
        }

        static IEnumerable<CodeInstruction> TrTech(IEnumerable<CodeInstruction> instr, MethodBase __originalMethod)
        {
            var list = instr.ToList();
            int n = 0;
            foreach (var ci in list)
                if (ci.Calls(FullCust3.OfPlayerGetter)) { ci.opcode = OpCodes.Call; ci.operand = techFactionM; n++; }
            if (n == 0) Log.Error(Log10 + __originalMethod?.Name + ": no Faction.OfPlayer read found");
            return list;
        }

        static PropertyInfo threatMapP;

        /// <summary>Faction of a combat Threat's map (UI Rethemed History): the map's own player faction, else OfPlayer.</summary>
        public static Faction ForThreat(object threat)
        {
            if (threat == null) return Faction.OfPlayer;
            if (threatMapP == null) threatMapP = AccessTools.Property(threat.GetType(), "Map");
            return PlayerFactionForTick.ForMap(threatMapP?.GetValue(threat) as Map);
        }

        /// <summary>Faction for a pawn-bound test (Visit Settlements lambda): the player faction visiting the pawn's map, else OfPlayer.</summary>
        public static Faction ForPawnMap(Pawn p)
        {
            var f = Faction.OfPlayer;
            if (!MP.IsInMultiplayer || p?.Map == null || RightPlayerFaction.PlayerFactionCount() < 2) return f;
            return FullVisitRaidCu3.VisitorFaction(p.Map) ?? TileOwner(p.Map) ?? f;
        }

        /// <summary>Fallback owner of a visited map with no player pawn spawned: lowest-loadID real player caravan on its tile, else null.</summary>
        public static Faction TileOwner(Map map)
        {
            if (map == null) return null;
            Faction best = null;
            foreach (var c in Find.WorldObjects.Caravans)
            {
                if (c.Tile != map.Tile) continue;
                var f = c.Faction;
                if (f == null || !f.IsPlayer || f.def == null || !f.def.isPlayer) continue;
                if (best == null || f.loadID < best.loadID) best = f;
            }
            return best;
        }
    }

    /// <summary>Morts Ideologies (mortstrudel.mortideology): MI_WorldComponent_LeaderTracker.WorldComponentTick reads Faction.OfPlayer 5 times (primary ideo, colonist tests).</summary>
    [MpCompatFor("mortstrudel.mortideology")]
    class FullMortLeader
    {
        public FullMortLeader(ModContentPack mod) => FullCust10.PatchTech("MortsIdeologies.MI_WorldComponent_LeaderTracker:WorldComponentTick", "Morts Ideologies leader tracker");
    }

    /// <summary>Family Tree (rizryn.familytree): GameComponent_TechEraHistory.GameComponentTick reads the tech level of Faction.OfPlayer.def.</summary>
    [MpCompatFor("rizryn.familytree")]
    class FullFamilyTreeTech
    {
        public FullFamilyTreeTech(ModContentPack mod) => FullCust10.PatchTech("FamilyTreeMod.GameComponent_TechEraHistory:GameComponentTick", "Family Tree tech era");
    }

    /// <summary>VFE Tribals Auto Gathering (sk.tribalsautogathering): IsScheduledRitualTechLevelEligible reads the tech level of Faction.OfPlayer.def.</summary>
    [MpCompatFor("sk.tribalsautogathering")]
    class FullTribalsAutoTech
    {
        public FullTribalsAutoTech(ModContentPack mod) => FullCust10.PatchTech("VFE_Tribals_Auto_Gathering.AutoTribalGatheringGameComponent:IsScheduledRitualTechLevelEligible", "Tribals Auto Gathering tech level");
    }

    /// <summary>UI Rethemed History (lazyfridaystudio.uirethemed.history): CombatRecord.Join/Follow (arg 1 = Threat) and FateOf (arg 1 = Map) test HostileTo(Faction.OfPlayer)
    /// from the game component tick. Each read becomes the faction owning the threat's / the target map (history record only).</summary>
    [MpCompatFor("lazyfridaystudio.uirethemed.history")]
    class FullCombatRecordHostile
    {
        static readonly MethodInfo forThreatM = AccessTools.Method(typeof(FullCust10), nameof(FullCust10.ForThreat));
        static readonly MethodInfo forMapM = AccessTools.Method(typeof(PlayerFactionForTick), nameof(PlayerFactionForTick.ForMap));

        public FullCombatRecordHostile(ModContentPack mod)
        {
            Reg("Join", nameof(TrThreat));
            Reg("Follow", nameof(TrThreat));
            Reg("FateOf", nameof(TrMap));
        }

        static void Reg(string name, string tr)
        {
            var m = AccessTools.Method("UIRethemed.History.CombatRecord:" + name);
            if (m == null) { Log.Error(FullCust10.Log10 + "UI Rethemed History: CombatRecord." + name + " not found"); return; }
            MpCompat.harmony.Patch(m, transpiler: new HarmonyMethod(typeof(FullCombatRecordHostile), tr));
            Log.Message(FullCust10.Log10 + "UI Rethemed History: CombatRecord." + name + " OfPlayer reads -> owner of the map registered");
        }

        static IEnumerable<CodeInstruction> TrThreat(IEnumerable<CodeInstruction> instr) => Swap(instr, forThreatM, "Join/Follow");
        static IEnumerable<CodeInstruction> TrMap(IEnumerable<CodeInstruction> instr) => Swap(instr, forMapM, "FateOf");

        static IEnumerable<CodeInstruction> Swap(IEnumerable<CodeInstruction> instr, MethodInfo target, string what)
        {
            var result = new List<CodeInstruction>();
            int n = 0;
            foreach (var ci in instr)
            {
                if (ci.Calls(FullCust3.OfPlayerGetter))
                {
                    var load = new CodeInstruction(OpCodes.Ldarg_1);
                    load.labels.AddRange(ci.labels); ci.labels.Clear();
                    result.Add(load);
                    result.Add(new CodeInstruction(OpCodes.Call, target));
                    n++;
                }
                else result.Add(ci);
            }
            if (n == 0) Log.Error(FullCust10.Log10 + "CombatRecord." + what + ": no Faction.OfPlayer read found");
            return result;
        }
    }

    /// <summary>Visit Settlements (ninagoblin.visitsettlements): DropFoodPodsForSettlements filters pawns with a lambda p => ... !HostileTo(p.Faction, Faction.OfPlayer) run from the
    /// world component tick (spectator). No Map in the closure: its Pawn argument is spawned on the settlement map, so the read becomes the player faction visiting p.Map
    /// (FullVisitRaidCu3.VisitorFaction, else a player caravan on the tile, else OfPlayer). TriggerRaidEvent keeps the push of FullVisitRaidCu3 (which now also
    /// falls back to the caravan owner).</summary>
    [MpCompatFor("ninagoblin.visitsettlements")]
    class FullVisitDropFoodLambda
    {
        static readonly MethodInfo forPawnM = AccessTools.Method(typeof(FullCust10), nameof(FullCust10.ForPawnMap));

        public FullVisitDropFoodLambda(ModContentPack mod)
        {
            var host = AccessTools.TypeByName("VS_WorldComponent_SettlementEvents");
            if (host == null) { Log.Error(FullCust10.Log10 + "Visit Settlements: VS_WorldComponent_SettlementEvents not found"); return; }
            int n = 0;
            foreach (var nt in host.GetNestedTypes(AccessTools.all))
                foreach (var m in nt.GetMethods(AccessTools.allDeclared))
                {
                    var ps = m.GetParameters();
                    if (m.IsStatic || ps.Length != 1 || ps[0].ParameterType != typeof(Pawn) || m.ReturnType != typeof(bool)) continue;
                    if (!PatchProcessor.GetCurrentInstructions(m).Any(i => i.Calls(FullCust3.OfPlayerGetter))) continue;
                    MpCompat.harmony.Patch(m, transpiler: new HarmonyMethod(typeof(FullVisitDropFoodLambda), nameof(Tr)));
                    n++;
                }
            if (n == 0) Log.Error(FullCust10.Log10 + "Visit Settlements: no Pawn lambda reading Faction.OfPlayer found");
            else Log.Message(FullCust10.Log10 + "Visit Settlements: " + n + " pawn lambda OfPlayer read -> map visitor registered");
        }

        static IEnumerable<CodeInstruction> Tr(IEnumerable<CodeInstruction> instr)
        {
            foreach (var ci in instr)
            {
                if (ci.Calls(FullCust3.OfPlayerGetter))
                {
                    var load = new CodeInstruction(OpCodes.Ldarg_1);
                    load.labels.AddRange(ci.labels); ci.labels.Clear();
                    yield return load;
                    yield return new CodeInstruction(OpCodes.Call, forPawnM);
                }
                else yield return ci;
            }
        }
    }
}
