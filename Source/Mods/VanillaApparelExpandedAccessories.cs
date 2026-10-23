using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace Multiplayer.Compat
{
    /// <summary>Vanilla Apparel Expanded - Accessories by Oskar Potocki, xrushha, SmashPhil, legodude17</summary>
    /// <remarks>
    /// Not a sync fix: an upstream bug (it also happens in single player), kept here because MP-Compat is where the co-op build patches mods.
    /// err_mf_settle lane d, 22 Oct 2026 (live log 20:17:36, "Tried to add pawn Gaston to lord ... already a member of lord").
    /// The Resurrector Belt is handled by a prefix on <c>Pawn.Kill</c> (<c>HarmonyPatches.BeltsOnDeath</c>), which runs before vanilla
    /// <c>Pawn.Kill</c> has taken the pawn out of its lord (<c>lord?.Notify_PawnLost(...)</c>, later in Kill). A raider killed outright is therefore
    /// still a member of his raid lord when <c>UtilityMethods.ResurrectBeforeDeath</c> calls
    /// <c>LordMaker.MakeNewLord(faction, new LordJob_AssaultColony(...), map, Gen.YieldSingle(pawn))</c>: <c>LordMaker</c> registers the new lord
    /// first, then <c>Lord.AddPawns</c> logs the error and returns, so an empty assault lord stays on the map.
    /// Fix: a transpiler on <c>ResurrectBeforeDeath</c> replaces that one call with <see cref="MakeNewLordIfFree"/> (same parameters, same
    /// return type), which creates nothing when a starting pawn still has a lord - he keeps fighting in the lord he is in - and otherwise
    /// calls the original <c>MakeNewLord</c>. VAE discards the returned lord, so returning null is safe.
    /// Status: [unverified] - compiled only, not run in game.
    /// </remarks>
    [MpCompatFor("VanillaExpanded.VAEAccessories")]
    public class VanillaApparelExpandedAccessories
    {
        private const string LogPrefix = "[MPCompat - Vanilla Apparel Expanded - Accessories] ";

        public VanillaApparelExpandedAccessories(ModContentPack mod)
        {
            var target = AccessTools.DeclaredMethod("VAE_Accessories.UtilityMethods:ResurrectBeforeDeath")
                         ?? throw new MissingMethodException($"{LogPrefix}VAE_Accessories.UtilityMethods.ResurrectBeforeDeath not found");
            MpCompat.harmony.Patch(target, transpiler: new HarmonyMethod(typeof(VanillaApparelExpandedAccessories), nameof(MakeNewLordTranspiler)));
            Log.Message($"{LogPrefix}ResurrectBeforeDeath no longer makes a second lord for a pawn that is still in one");
        }

        private static IEnumerable<CodeInstruction> MakeNewLordTranspiler(IEnumerable<CodeInstruction> instr, MethodBase original)
        {
            // LordMaker has a single MakeNewLord(Faction, LordJob, Map, IEnumerable<Pawn> startingPawns = null).
            var makeNewLord = AccessTools.DeclaredMethod(typeof(LordMaker), nameof(LordMaker.MakeNewLord));
            var replacement = AccessTools.DeclaredMethod(typeof(VanillaApparelExpandedAccessories), nameof(MakeNewLordIfFree));
            var count = 0;

            foreach (var ci in instr)
            {
                if (ci.Calls(makeNewLord))
                {
                    ci.operand = replacement;
                    count++;
                }

                yield return ci;
            }

            if (count != 1)
                Log.Warning($"{LogPrefix}expected 1 call to LordMaker.MakeNewLord in {original?.FullDescription() ?? "ResurrectBeforeDeath"}, replaced {count}");
        }

        // Same parameters and return type as LordMaker.MakeNewLord, so it replaces the call instruction one to one.
        private static Lord MakeNewLordIfFree(Faction faction, LordJob lordJob, Map map, IEnumerable<Pawn> startingPawns)
        {
            // VAE passes Gen.YieldSingle(pawn), a one-shot iterator: read it once, hand the same list on.
            var pawns = startingPawns?.ToList();
            if (pawns != null && pawns.Any(p => p?.GetLord() != null))
                return null;

            return LordMaker.MakeNewLord(faction, lordJob, map, pawns);
        }
    }
}
