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
    /// <para/>
    /// FIXES row 181 (live D2, 21 Oct 2026): examining the eerie wreckage (<c>CompUseEffect_DelayedTriggerArrival</c>) is an item use, so
    /// <c>DoEffect</c> runs in the simulation on every PC and opened its two confirmation dialogs on every PC. Only a PC whose player clicked
    /// yes twice ran <c>DoEffectFinal</c> -> <c>GameComponent_AAT.SchedueSpawn(delayRange.RandomInRange, map)</c>: James's PC scheduled the boss
    /// at MP timer 84662, the host about 2,100 ticks later. In MP the dialogs open only on the PCs of the user pawn's faction (all PCs in a
    /// shared colony), and <c>DoEffectFinal</c> from a dialog is a synced command, so it runs on every PC in the same tick with the same draw.
    /// When two players both confirm, the later command is skipped by the mod's own once-only rule (<c>CompUsable_OnceOnly.UsedBy</c>: no
    /// new use while the boss is arriving or alive). Without a warning text the mod calls <c>DoEffectFinal</c> in the tick: unchanged.
    /// </remarks>
    [MpCompatFor("XMB.AncientThreat.MO")]
    public class AncientAmorphousThreat
    {
        private const string LogPrefix = "[MPCompat - AAT] ";

        private static Type aatGameCompType;
        private static AccessTools.FieldRef<object, int> ticksToArrival;
        private static AccessTools.FieldRef<object, Pawn> bossPawn;

        public AncientAmorphousThreat(ModContentPack mod)
        {
            LongEventHandler.ExecuteWhenFinished(RunConfigSideEffects);
            PatchDelayedArrival();
        }

        private static void PatchDelayedArrival()
        {
            var compType = AccessTools.TypeByName("AncientAmorphousThreat.CompUseEffect_DelayedTriggerArrival");
            var doEffect = compType == null ? null : AccessTools.DeclaredMethod(compType, "DoEffect", [typeof(Pawn)]);
            var doEffectFinal = compType == null ? null : AccessTools.DeclaredMethod(compType, "DoEffectFinal");
            aatGameCompType = AccessTools.TypeByName("AncientAmorphousThreat.GameComponent_AAT");
            if (doEffect == null || doEffectFinal == null || aatGameCompType == null)
            {
                Log.Error(LogPrefix + "row 181: DoEffect " + (doEffect != null) + ", DoEffectFinal " + (doEffectFinal != null) +
                          ", GameComponent_AAT " + (aatGameCompType != null) + "; the wreckage's boss arrival stays per PC");
                return;
            }
            ticksToArrival = AccessTools.FieldRefAccess<int>(aatGameCompType, "TickToArrival");
            bossPawn = AccessTools.FieldRefAccess<Pawn>(aatGameCompType, "pawn");

            MpCompat.harmony.Patch(doEffect, transpiler: new HarmonyMethod(typeof(AncientAmorphousThreat), nameof(DialogsForUserOnly)));
            MP.RegisterSyncMethod(doEffectFinal);
            MpCompat.harmony.Patch(doEffectFinal, prefix: new HarmonyMethod(typeof(AncientAmorphousThreat), nameof(PreDoEffectFinal)));
        }

        // DoEffect's Find.WindowStack.Add(confirmation) -> AddDialogForUser(stack, confirmation, user)
        private static IEnumerable<CodeInstruction> DialogsForUserOnly(IEnumerable<CodeInstruction> instr, MethodBase baseMethod)
        {
            var add = AccessTools.DeclaredMethod(typeof(WindowStack), nameof(WindowStack.Add));
            var replacement = AccessTools.DeclaredMethod(typeof(AncientAmorphousThreat), nameof(AddDialogForUser));
            var replaced = 0;
            foreach (var ci in instr)
            {
                if (ci.Calls(add))
                {
                    var loadUser = new CodeInstruction(OpCodes.Ldarg_1);
                    ci.MoveLabelsTo(loadUser);
                    yield return loadUser;
                    ci.opcode = OpCodes.Call;
                    ci.operand = replacement;
                    replaced++;
                }
                yield return ci;
            }
            if (replaced != 1)
                Log.Error(LogPrefix + $"row 181: {replaced} WindowStack.Add calls replaced in {baseMethod.DeclaringType?.Name}.{baseMethod.Name}, expected 1");
        }

        private static void AddDialogForUser(WindowStack stack, Window dialog, Pawn user)
        {
            if (!MP.IsInMultiplayer)
            {
                stack.Add(dialog);
                return;
            }
            var mine = MP.RealPlayerFaction;
            var theirs = user?.Faction;
            if (mine == null || theirs == null || !theirs.IsPlayer || theirs == mine)
                stack.Add(dialog);
        }

        private static bool PreDoEffectFinal()
        {
            if (!MP.IsInMultiplayer || !MP.IsExecutingSyncCommand)
                return true;
            var comp = Current.Game?.components?.FirstOrDefault(aatGameCompType.IsInstanceOfType);
            if (comp == null)
                return true;
            var boss = bossPawn(comp);
            if (ticksToArrival(comp) <= 0 && (boss == null || boss.Destroyed))
                return true;
            Log.Message(LogPrefix + "row 181: wreckage confirm skipped, the boss is already arriving or alive (ticks to arrival " +
                        ticksToArrival(comp) + ")");
            if (MP.IsExecutingSyncCommandIssuedBySelf)
                Messages.Message("The wreckage was already activated.", MessageTypeDefOf.RejectInput, false);
            return false;
        }

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
