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
    /// <summary>
    /// FIXES rows 517-519 (27 Nov 2026): in multifaction MP every GameComponent / WorldComponent tick runs with the spectator faction as
    /// Faction.OfPlayer (MP AsyncWorldTimeComp.cs:154), so a mod's "hostile to the player" test there asks about the spectator, not
    /// about the player faction that owns the target map. This helper is what the transpilers of rows 517, 518 and 519 put in place of
    /// the named Faction.OfPlayer reads: the target map's own faction when it is a player faction that differs from OfPlayer, otherwise
    /// Faction.OfPlayer itself (single player, non-multifaction MP, map of a non-player owner: unchanged).
    /// </summary>
    public static class PlayerFactionForTick
    {
        public static readonly MethodInfo OfPlayerGetter = AccessTools.PropertyGetter(typeof(Faction), nameof(Faction.OfPlayer));
        static readonly MethodInfo forMapMethod = AccessTools.Method(typeof(PlayerFactionForTick), nameof(ForMap));

        public static Faction ForMap(Map map)
        {
            var player = Faction.OfPlayer;
            if (!MP.IsInMultiplayer) return player;
            var owner = map?.ParentFaction;
            return owner != null && owner.IsPlayer && owner != player ? owner : player;
        }

        /// <summary>Replaces every call to Faction.get_OfPlayer in the method by ForMap(map); the map is loaded by <paramref name="loadMap"/>.
        /// Returns the instructions; <paramref name="replaced"/> is the number of reads swapped.</summary>
        public static IEnumerable<CodeInstruction> SwapOfPlayer(IEnumerable<CodeInstruction> instr, Func<CodeInstruction> loadMap, int expected, string what)
        {
            var list = instr.ToList();
            var count = 0;
            var result = new List<CodeInstruction>(list.Count + expected);
            foreach (var ci in list)
            {
                if (ci.Calls(OfPlayerGetter))
                {
                    var load = loadMap();
                    load.labels.AddRange(ci.labels);
                    ci.labels.Clear();
                    result.Add(load);
                    result.Add(new CodeInstruction(OpCodes.Call, forMapMethod));
                    count++;
                }
                else result.Add(ci);
            }
            if (count != expected)
                Log.Error("[MPCompat] rows 517-519: " + what + " has " + count + " Faction.OfPlayer reads, expected " + expected + "; left as in the mod is not guaranteed");
            return result;
        }

        /// <summary>Index of the one local of the given type in the method body, or -1 when there is not exactly one.</summary>
        public static int OnlyLocalOfType(MethodBase method, Type type)
        {
            var locals = method.GetMethodBody()?.LocalVariables?.Where(l => l.LocalType == type).ToList();
            return locals != null && locals.Count == 1 ? locals[0].LocalIndex : -1;
        }
    }
}
