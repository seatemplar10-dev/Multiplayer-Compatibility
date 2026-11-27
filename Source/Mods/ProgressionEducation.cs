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
    /// <summary>Progression: Education by Ferny</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3590863552"/>
    /// <remarks>
    /// FIXES row 532 (27 Nov 2026). Three reads of Faction.OfPlayer decide things about a player's learning board and classroom, and in
    /// multifaction OfPlayer is the spectator faction in the world tick (and the local faction on a PC in the interface):
    /// 1. EducationManager.TryInitiateClassForStudyGroup (called from the WorldComponentTick, spectator 69) makes the class lord with
    ///    LordMaker.MakeNewLord(Faction.OfPlayer, ...). Fix, multifaction only: the faction is PlayerFactionForTick.ForMap(classroom map).
    /// 2. Thing_SetFaction_Patch.Postfix compares the new faction with OfPlayer twice: when it differs from OfPlayer (another player faction
    ///    claims or owns the board) the else branch removes the classroom. Fix: a player faction newFaction counts as the player (helper below).
    /// 3. CompLearningBoard.InitializeClassroom returns when parent.Faction != OfPlayer, so a board of the other player faction gets no classroom.
    ///    Same helper. Single player and non-player factions read exactly as before (the helper returns OfPlayer then).
    /// </remarks>
    [MpCompatFor("ferny.progressioneducation")]
    public class ProgressionEducationCompat
    {
        private const string LogPrefix = "[MPCompat - Progression Education] ";
        private static readonly MethodInfo factionOrPlayerMethod = AccessTools.Method(typeof(ProgressionEducationCompat), nameof(PlayerFactionOr));

        public ProgressionEducationCompat(ModContentPack mod)
        {
            if (LabOff.Row(532))
                Log.Message(LogPrefix + "row 532 switched off by the lab, class lord and learning board checks stay on OfPlayer");
            else
                PatchRow532();
        }

        private static void PatchRow532()
        {
            var tryInitiate = AccessTools.Method("ProgressionEducation.EducationManager:TryInitiateClassForStudyGroup");
            var setFaction = AccessTools.Method("ProgressionEducation.Thing_SetFaction_Patch:Postfix");
            var initClass = AccessTools.Method("ProgressionEducation.CompLearningBoard:InitializeClassroom");
            if (tryInitiate == null || setFaction == null || initClass == null)
            {
                Log.Error(LogPrefix + "row 532: members not found (TryInitiateClassForStudyGroup " + (tryInitiate != null) + ", Thing_SetFaction_Patch.Postfix "
                          + (setFaction != null) + ", InitializeClassroom " + (initClass != null) + "), class lord and board checks stay on OfPlayer");
                return;
            }
            MpCompat.harmony.Patch(tryInitiate, transpiler: new HarmonyMethod(typeof(ProgressionEducationCompat), nameof(LordFactionTranspiler)));
            MpCompat.harmony.Patch(setFaction, transpiler: new HarmonyMethod(typeof(ProgressionEducationCompat), nameof(SetFactionTranspiler)));
            MpCompat.harmony.Patch(initClass, transpiler: new HarmonyMethod(typeof(ProgressionEducationCompat), nameof(InitClassroomTranspiler)));
            Log.Message(LogPrefix + "row 532: the class lord takes the classroom map's player faction, board claim checks accept any player faction");
        }

        // TryInitiateClassForStudyGroup: one OfPlayer read (MakeNewLord), the classroom map is the one Map local.
        private static IEnumerable<CodeInstruction> LordFactionTranspiler(IEnumerable<CodeInstruction> instr, MethodBase __originalMethod)
        {
            var local = PlayerFactionForTick.OnlyLocalOfType(__originalMethod, typeof(Map));
            if (local < 0)
            {
                Log.Error(LogPrefix + "row 532: TryInitiateClassForStudyGroup has no single Map local, the class lord stays on OfPlayer");
                return instr;
            }
            return PlayerFactionForTick.SwapOfPlayer(instr, () => new CodeInstruction(OpCodes.Ldloc, local), 1, "Education TryInitiateClassForStudyGroup");
        }

        // Thing_SetFaction_Patch.Postfix(Thing __instance, Faction newFaction): two OfPlayer reads, newFaction is argument 1.
        private static IEnumerable<CodeInstruction> SetFactionTranspiler(IEnumerable<CodeInstruction> instr) =>
            SwapWithFaction(instr, new[] { new CodeInstruction(OpCodes.Ldarg_1) }, 2, "Education Thing_SetFaction_Patch.Postfix");

        // CompLearningBoard.InitializeClassroom: one read, compared with parent.Faction.
        private static IEnumerable<CodeInstruction> InitClassroomTranspiler(IEnumerable<CodeInstruction> instr)
        {
            var parentField = AccessTools.Field(typeof(ThingComp), nameof(ThingComp.parent));
            var factionGetter = AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.Faction));
            return SwapWithFaction(instr, new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldfld, parentField), new CodeInstruction(OpCodes.Callvirt, factionGetter)
            }, 1, "Education CompLearningBoard.InitializeClassroom");
        }

        // Each Faction.OfPlayer call becomes: <load the faction being compared> ; PlayerFactionOr(that faction).
        private static IEnumerable<CodeInstruction> SwapWithFaction(IEnumerable<CodeInstruction> instr, CodeInstruction[] loadFaction, int expected, string what)
        {
            var result = new List<CodeInstruction>();
            var count = 0;
            foreach (var ci in instr)
            {
                if (ci.Calls(PlayerFactionForTick.OfPlayerGetter))
                {
                    var first = true;
                    foreach (var l in loadFaction)
                    {
                        var copy = new CodeInstruction(l.opcode, l.operand);
                        if (first)
                        {
                            copy.labels.AddRange(ci.labels);
                            ci.labels.Clear();
                            first = false;
                        }
                        result.Add(copy);
                    }
                    result.Add(new CodeInstruction(OpCodes.Call, factionOrPlayerMethod));
                    count++;
                }
                else result.Add(ci);
            }
            if (count != expected)
                Log.Error(LogPrefix + "row 532: " + what + " has " + count + " Faction.OfPlayer reads, expected " + expected);
            return result;
        }

        /// <summary>The faction itself when it is a player faction (multiplayer only), otherwise Faction.OfPlayer, so "faction == OfPlayer" becomes "faction is a player faction".</summary>
        public static Faction PlayerFactionOr(Faction faction)
        {
            var player = Faction.OfPlayer;
            if (!MP.IsInMultiplayer) return player;
            return faction != null && faction.IsPlayer ? faction : player;
        }
    }
}
