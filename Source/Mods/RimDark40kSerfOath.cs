using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>RimDark 40k - Mankind's Finest (Genes40k): serf oath dialog after a rescue or tend (FIXES row 482)</summary>
    /// <remarks>
    /// SerfSavedByRescue (Pawn_RelationsTracker.Notify_RescuedBy postfix) and SerfSavedByTend (TendUtility.DoTend postfix) call
    /// SerfUtility.Notify_SavedBy in the simulation. With a 15% roll it adds a Dialog_MessageBox, so every PC shows it, but the
    /// accept lambda (SerfUtility.MakeSerf = gains the Serf trait) and the refuse lambda (GameComponent_MankindFinestUtils.
    /// Notify_SerfOathDeclined = writes the declined list) ran only on the PC that clicked. Fix: in multiplayer a click on either
    /// lambda (they only exist in that dialog) sends one synced SyncedSerfOath(pawn, accept) instead; the command runs
    /// MakeSerf / Notify_SerfOathDeclined on every PC and closes the dialog everywhere. The first command wins: a pawn that is
    /// already a serf or already declined ignores a later command. Not synced: SerfUtility.TrySurviveFailedImplantation has the same
    /// shape (another dialog), not part of this row.
    /// </remarks>
    [MpCompatFor("phonicmas.rimdark.mankindsfinest")]
    internal class RimDark40kSerfOath
    {
        private const string LogPrefix = "[MPCompat - RimDark 40k Mankind's Finest] ";
        private static MethodInfo makeSerf, isSerf, hasDeclined, declinedNotify;
        private static Type utilsType;

        public RimDark40kSerfOath(ModContentPack mod) => Patch();

        internal static void Patch()
        {
            if (LabOff.Row(482))
            {
                Log.Message(LogPrefix + "row 482 switched off by the lab, serf oath dialog not synced");
                return;
            }
            var serfT = AccessTools.TypeByName("Genes40k.SerfUtility");
            utilsType = AccessTools.TypeByName("Genes40k.GameComponent_MankindFinestUtils");
            makeSerf = serfT == null ? null : AccessTools.DeclaredMethod(serfT, "MakeSerf");
            isSerf = serfT == null ? null : AccessTools.DeclaredMethod(serfT, "IsSerf");
            hasDeclined = utilsType == null ? null : AccessTools.DeclaredMethod(utilsType, "HasDeclinedSerfOath");
            declinedNotify = utilsType == null ? null : AccessTools.DeclaredMethod(utilsType, "Notify_SerfOathDeclined");
            var lambdas = new List<MethodInfo>();
            if (serfT != null)
            {
                for (var i = 0; i < 2; i++)
                {
                    MethodInfo lambda = null;
                    try { lambda = MpMethodUtil.GetLambda(serfT, "Notify_SavedBy", lambdaOrdinal: i); }
                    catch (Exception) { }
                    if (lambda != null && lambda.ReturnType == typeof(void) && lambda.GetParameters().Length == 0 && !lambda.IsStatic
                        && AccessTools.Field(lambda.DeclaringType, "saved") != null) lambdas.Add(lambda);
                }
            }
            if (makeSerf == null || isSerf == null || hasDeclined == null || declinedNotify == null || lambdas.Count != 2)
            {
                Log.Error(LogPrefix + "row 482: SerfUtility.Notify_SavedBy lambdas or MakeSerf / IsSerf / serf oath methods not found, the serf oath dialog can desync");
                return;
            }
            MP.RegisterSyncMethod(typeof(RimDark40kSerfOath), nameof(SyncedSerfOath));
            MpCompat.harmony.Patch(lambdas[0], prefix: new HarmonyMethod(typeof(RimDark40kSerfOath), nameof(PreAccept)));
            MpCompat.harmony.Patch(lambdas[1], prefix: new HarmonyMethod(typeof(RimDark40kSerfOath), nameof(PreRefuse)));
            Log.Message(LogPrefix + "row 482: serf oath dialog (accept, refuse) synced");
        }

        private static Pawn SavedOf(object closure) => closure == null ? null : AccessTools.Field(closure.GetType(), "saved")?.GetValue(closure) as Pawn;

        internal static bool PreAccept(object __instance) => Click(__instance, true);

        internal static bool PreRefuse(object __instance) => Click(__instance, false);

        private static bool Click(object closure, bool accept)
        {
            if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand) return true;
            var pawn = SavedOf(closure);
            if (pawn == null) return true;
            SyncedSerfOath(pawn, accept);
            return false;
        }

        internal static void SyncedSerfOath(Pawn saved, bool accept)
        {
            if (saved == null) return;
            var utils = Current.Game?.GetComponent(utilsType);
            if (utils == null) return;
            if ((bool)isSerf.Invoke(null, new object[] { saved }) || (bool)hasDeclined.Invoke(utils, new object[] { saved })) return;
            if (accept) makeSerf.Invoke(null, new object[] { saved });
            else declinedNotify.Invoke(utils, new object[] { saved });
            var windows = Current.Root?.uiRoot?.windows?.Windows;
            if (windows == null) return;
            foreach (var window in new List<Window>(windows))
            {
                if (window is Dialog_MessageBox box && box.buttonAAction?.Target != null && ReferenceEquals(SavedOf(box.buttonAAction.Target), saved))
                    box.Close(false);
            }
        }
    }
}
