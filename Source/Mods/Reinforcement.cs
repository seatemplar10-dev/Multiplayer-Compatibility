using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Reinforcement (CVN mechanoid warfare core) by ShunTheWitch, both builds (Reinforcement.dll and Reinforcement_VF.dll)</summary>
    /// <remarks>
    /// FIXES row 517 (27 Nov 2026): GameComponent_ReinforcementArrival.MakeLordJob (called from DoArrival from GameComponentTick, IL_0061)
    /// picks LordJob_AssaultColony or LordJob_AssistColony by FactionUtility.HostileTo(parms.faction, Faction.OfPlayer). In multifaction MP
    /// the game component tick runs with the spectator faction as OfPlayer, so the choice follows the spectator's relations, not the raided
    /// player faction's. Fix, multifaction only: that one read becomes PlayerFactionForTick.ForMap(map) (the arrival map's own player
    /// faction); anything else (single player, one player faction, a map of a non-player owner) reads Faction.OfPlayer as before.
    /// The debug-only reads at DoArrival (drawStealDebug) are left alone. Both dlls have the same type name; each loaded one is patched.
    /// </remarks>
    [MpCompatFor("ShunTheWitch.CVNmechanoidwarfarecore")]
    public class Reinforcement
    {
        private const string LogPrefix = "[MPCompat - Reinforcement] ";

        public Reinforcement(ModContentPack mod)
        {
            if (LabOff.Row(517))
            {
                Log.Message(LogPrefix + "row 517 switched off by the lab, reinforcement lord job stays on OfPlayer");
                return;
            }

            var types = AccessTools.AllTypes().Where(t => t.FullName == "Reinforcement.GameComponent_ReinforcementArrival").ToList();
            var patched = 0;
            foreach (var type in types)
            {
                var method = AccessTools.DeclaredMethod(type, "MakeLordJob");
                if (method == null)
                {
                    Log.Error(LogPrefix + "row 517: MakeLordJob not found in " + type.Assembly.GetName().Name);
                    continue;
                }
                MpCompat.harmony.Patch(method, transpiler: new HarmonyMethod(typeof(Reinforcement), nameof(SwapOfPlayer)));
                patched++;
            }
            if (patched > 0)
                Log.Message(LogPrefix + "row 517: Reinforcement GameComponent_ReinforcementArrival.MakeLordJob uses the right player faction in multifaction (" + patched + " dll)");
            else
                Log.Error(LogPrefix + "row 517: GameComponent_ReinforcementArrival not found, the lord job stays on OfPlayer");
        }

        // MakeLordJob(IncidentParms parms, Map map, List<Pawn> pawns, int raidSeed): instance method, map is argument 2.
        private static IEnumerable<CodeInstruction> SwapOfPlayer(IEnumerable<CodeInstruction> instr) =>
            PlayerFactionForTick.SwapOfPlayer(instr, () => new CodeInstruction(OpCodes.Ldarg_2), 1, "Reinforcement MakeLordJob");
    }
}
