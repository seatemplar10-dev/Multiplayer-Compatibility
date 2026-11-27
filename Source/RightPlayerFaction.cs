using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>
    /// FIXES rows 515 and 516. In multifaction Multiplayer every GameComponent / WorldComponent tick runs with the spectator
    /// faction pushed, so Faction.OfPlayer is the spectator there. A check "pawn.Faction == Faction.OfPlayer" (or !=) is then
    /// wrong for every pawn of a real player faction. The transpiler below adds a dup before the named get_OfPlayer read and replaces that call with
    /// Resolve with the pawn's faction: the pawn's own faction when it is a player faction and there is more than one player
    /// faction, otherwise Faction.OfPlayer (single player and one-faction Multiplayer keep the old value).
    /// </summary>
    internal static class RightPlayerFaction
    {
        private static readonly System.Reflection.MethodInfo ofPlayerGetter = AccessTools.PropertyGetter(typeof(Faction), nameof(Faction.OfPlayer));
        private static readonly System.Reflection.MethodInfo thingFactionGetter = AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.Faction));

        // The pawn's faction (already on the stack before the OfPlayer read) when it is a player faction in multifaction.
        public static Faction Resolve(Faction pawnFaction)
        {
            if (!MP.IsInMultiplayer || pawnFaction == null || !pawnFaction.IsPlayer || PlayerFactionCount() < 2)
                return Faction.OfPlayer;
            return pawnFaction;
        }

        public static int PlayerFactionCount()
        {
            var list = Find.FactionManager?.AllFactionsListForReading;
            if (list == null) return 0;
            int n = 0;
            for (int i = 0; i < list.Count; i++)
                if (list[i].IsPlayer) n++;
            return n;
        }

        /// <summary>Swaps the first (occurrence 0) or the only "Thing.get_Faction; Faction.get_OfPlayer" pair's OfPlayer read.</summary>
        public static IEnumerable<CodeInstruction> Swap(IEnumerable<CodeInstruction> instr, int row, string what)
        {
            var list = new List<CodeInstruction>(instr);
            if (LabOff.Row(row))
                return list;
            var resolve = AccessTools.Method(typeof(RightPlayerFaction), nameof(Resolve));
            for (int i = 1; i < list.Count; i++)
            {
                if (!list[i].Calls(ofPlayerGetter) || !list[i - 1].Calls(thingFactionGetter))
                    continue;
                list.Insert(i, new CodeInstruction(OpCodes.Dup));
                list[i + 1].opcode = OpCodes.Call; list[i + 1].operand = resolve;
                Log.Message("[MPCompat] row " + row + ": " + what + " uses the right player faction in multifaction");
                return list;
            }
            Log.Warning("[MPCompat] row " + row + ": " + what + " no 'pawn.Faction; Faction.OfPlayer' read found, nothing patched");
            return list;
        }
    }
}
