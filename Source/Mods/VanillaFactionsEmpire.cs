using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace Multiplayer.Compat
{
    /// <summary>Vanilla Factions Expanded - Empire by Oskar Potocki, Sarg and others</summary>
    /// <remarks>
    /// FIXES row 478 (26 Nov 2026): the grand ball "Leave" gizmo (LordJob_GrandBall.GetPawnGizmos, click lambda in the display
    /// class) adds the pawn to LordJob_Ritual.pawnsForcedToLeave, calls lord.Notify_PawnLost (changes the lord's pawn list and
    /// toils) and ends the pawn's job, on the clicking PC only. The lambda captures the job and the pawn, so it is not synced
    /// itself: a prefix on it sends a synced static helper (lord, pawn) that repeats the body and skips the click sound.
    /// </remarks>
    [MpCompatFor("OskarPotocki.VFE.Empire")]
    public class VanillaFactionsEmpire
    {
        private static FieldInfo thisField, pawnField;

        public VanillaFactionsEmpire(ModContentPack mod)
        {
            if (LabOff.Row(478))
            {
                Log.Message("[MPCompat - VFE Empire] row 478 switched off by the lab, grand ball Leave button not synced");
                return;
            }
            PatchRow478();
        }

        private static void PatchRow478()
        {
            var type = AccessTools.TypeByName("VFEEmpire.LordJob_GrandBall+<>c__DisplayClass78_0");
            var method = type == null ? null : AccessTools.DeclaredMethod(type, "<GetPawnGizmos>b__0");
            thisField = type == null ? null : AccessTools.Field(type, "<>4__this");
            pawnField = type == null ? null : AccessTools.Field(type, "p");
            if (method == null || thisField == null || pawnField == null)
            {
                Log.Error("[MPCompat - VFE Empire] row 478: LordJob_GrandBall Leave lambda (<GetPawnGizmos>b__0) not found, the Leave button can desync");
                return;
            }
            MpCompat.harmony.Patch(method, prefix: new HarmonyMethod(typeof(VanillaFactionsEmpire), nameof(PreLeave)));
            MP.RegisterSyncMethod(typeof(VanillaFactionsEmpire), nameof(SyncedLeave));
            Log.Message("[MPCompat - VFE Empire] row 478: grand ball Leave button synced");
        }

        private static bool PreLeave(object __instance)
        {
            if (!MP.IsInMultiplayer)
                return true;
            var job = thisField.GetValue(__instance) as LordJob;
            SyncedLeave(job?.lord, pawnField.GetValue(__instance) as Pawn);
            return false;
        }

        private static void SyncedLeave(Lord lord, Pawn pawn)
        {
            if (!(lord?.LordJob is LordJob_Ritual job) || pawn == null)
                return;
            job.pawnsForcedToLeave.Add(pawn);
            lord.Notify_PawnLost(pawn, (PawnLostCondition)10, null);
            pawn.jobs?.EndCurrentJob((JobCondition)16, true, true);
        }
    }
}
