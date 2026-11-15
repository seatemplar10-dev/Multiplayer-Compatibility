using System;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Ancient Amorphous Threat by BreadMo</summary>
    /// <remarks>
    /// FIXES row 180 (live session 21 Oct 2026, desyncs D4-D6 on the joiner). Two of the mod's ConfigErrors overrides ASSIGN gameplay
    /// values, and RimWorld runs ConfigErrors at boot only in dev mode (decomp4981 PlayDataLoader.cs:296), so a PC with dev mode off plays
    /// a different game:
    ///   - CompProperties_AbilitySpawnViaSkyfaller.ConfigErrors sets MaxSize (the largest dropped thing's size); with dev mode off it stays
    ///     (0,0), CompAbilityEffect_SpawnViaSkyfaller.ValidCell checks an empty rect and accepts every cell, so the boss's TurretFall draws
    ///     fewer Rand values and can target cells off the map (live: host 3 draws, joiner 2; the joiner's save held (-23, 0, 166)).
    ///   - PredefinedCharacterParmDef.ConfigErrors fills basePawnKindDef (Colonist) and baseXenotype (Baseliner) when the XML leaves them out.
    /// Both overrides are plain methods (not iterators): the assignment happens on the call, then they return the base error list, which is
    /// not enumerated here (no error spam). So they are called once after the defs load, in every mode - what dev mode does anyway, and
    /// harmless twice (same values). Not session-specific: single player without dev mode has the same bug.
    /// </remarks>
    [MpCompatFor("XMB.AncientThreat.MO")]
    public class AncientAmorphousThreat
    {
        private const string LogPrefix = "[MPCompat - AAT] ";

        public AncientAmorphousThreat(ModContentPack mod) => LongEventHandler.ExecuteWhenFinished(RunConfigSideEffects);

        private static void RunConfigSideEffects()
        {
            var skyfallerProps = AccessTools.TypeByName("AncientAmorphousThreat.CompProperties_AbilitySpawnViaSkyfaller");
            var characterParms = AccessTools.TypeByName("AncientAmorphousThreat.PredefinedCharacterParmDef");
            var maxSize = skyfallerProps == null ? null : AccessTools.DeclaredField(skyfallerProps, "MaxSize");
            if (skyfallerProps == null || characterParms == null || maxSize == null)
            {
                Log.Error(LogPrefix + "row 180: type or field not found (CompProperties_AbilitySpawnViaSkyfaller " + (skyfallerProps != null) +
                          ", MaxSize " + (maxSize != null) + ", PredefinedCharacterParmDef " + (characterParms != null) +
                          "); dev mode off keeps a different game");
                return;
            }

            int comps = 0, parms = 0;
            string example = null;
            foreach (var ability in DefDatabase<AbilityDef>.AllDefsListForReading)
            {
                if (ability.comps == null)
                    continue;
                foreach (var props in ability.comps.Where(skyfallerProps.IsInstanceOfType))
                {
                    var before = (IntVec2)maxSize.GetValue(props);
                    props.ConfigErrors(ability);   // assigns MaxSize; the returned list is left unenumerated
                    comps++;
                    if (example == null || ability.defName == "BreadMo_AAT_TurretFall")
                        example = ability.defName + " " + before + " -> " + (IntVec2)maxSize.GetValue(props);
                }
            }
            foreach (Def def in GenDefDatabase.GetAllDefsInDatabaseForDef(characterParms))
            {
                def.ConfigErrors();   // fills basePawnKindDef / baseXenotype when null; list left unenumerated
                parms++;
            }
            Log.Message(LogPrefix + "row 180: ConfigErrors side effects applied in every mode (dev mode " + Prefs.DevMode + "): " + comps +
                        " skyfaller ability comps (MaxSize, e.g. " + (example ?? "none") + "), " + parms + " PredefinedCharacterParmDef");
        }
    }
}
