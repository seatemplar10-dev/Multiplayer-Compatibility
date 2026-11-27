using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Adeptus Mechanicus Armoury by Ogliss - tesla arc</summary>
    /// <remarks>
    /// FIXES row 501. Projectile_Impact_ProjectileExtension_Patches.Arc (and the Combat Extended copy
    /// Projectile_Impact_ProjectileExtension_Patches_CE.Arc) read Find.CurrentMap.mapPawns.AllPawns twice to find the pawns near the hit
    /// pawn. Projectile.Impact runs in the map tick, where Find.CurrentMap is each PC's viewed map, so the arc targets differ per PC.
    /// In MP both Find.CurrentMap reads use the hit pawn's map. Single player unchanged.
    /// Status: [unverified] - zero-boot D2 test only, not run in game.
    /// </remarks>
    [MpCompatFor("Ogliss.AdMech.Armoury")]
    public class AdMechArmouryTeslaArc
    {
        public AdMechArmouryTeslaArc(ModContentPack mod)
        {
            if (LabOff.Row(501))
            {
                Log.Message("[MpCompat] AdMech Armoury tesla arc: row 501 off");
                return;
            }

            var patched = 0;
            foreach (var typeName in new[]
            {
                "AdeptusMechanicus.HarmonyInstance.Projectile_Impact_ProjectileExtension_Patches",
                "AdeptusMechanicus.HarmonyInstance.Projectile_Impact_ProjectileExtension_Patches_CE",
            })
            {
                var type = AccessTools.TypeByName(typeName);
                if (type == null)
                {
                    Log.Message($"[MpCompat] AdMech Armoury tesla arc: {typeName} not loaded, skipped");
                    continue;
                }
                var arc = AccessTools.DeclaredMethod(type, "Arc");
                if (arc == null)
                {
                    Log.Error($"AdMech Armoury tesla arc: {typeName}.Arc not found");
                    continue;
                }
                MpCompat.harmony.Patch(arc, transpiler: new HarmonyMethod(typeof(AdMechArmouryTeslaArc), nameof(ArcTranspiler)));
                patched++;
            }
            Log.Message($"[MpCompat] row 501: AdMech tesla arc uses the hit pawn's map ({patched} Arc methods)");
        }

        private static IEnumerable<CodeInstruction> ArcTranspiler(IEnumerable<CodeInstruction> instr, MethodBase original)
        {
            var currentMap = AccessTools.DeclaredPropertyGetter(typeof(Find), nameof(Find.CurrentMap));
            var replacement = AccessTools.DeclaredMethod(typeof(AdMechArmouryTeslaArc), nameof(HitPawnMap));
            // Arc(projectile, launcher, Pawn hitPawn, float radius) is static: hitPawn is argument 2
            var replaced = 0;
            foreach (var ci in instr)
            {
                if (ci.Calls(currentMap))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_2).MoveLabelsFrom(ci);
                    yield return new CodeInstruction(OpCodes.Call, replacement);
                    replaced++;
                }
                else
                    yield return ci;
            }
            if (replaced != 2)
                Log.Error($"AdMech Armoury tesla arc: expected 2 Find.CurrentMap calls in {original?.DeclaringType?.Name}.Arc, patched {replaced}");
        }

        public static Map HitPawnMap(Verse.Pawn hitPawn)
        {
            if (!MP.IsInMultiplayer)
                return Find.CurrentMap;
            return hitPawn.Map ?? Find.CurrentMap;
        }
    }
}
