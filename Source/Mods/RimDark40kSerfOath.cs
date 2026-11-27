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
    /// FIXES row 491 (PatchSurgeryBill): CreateSurgeryBillVialPicker (prefix of HealthCardUtility.CreateSurgeryBill, which Multiplayer
    /// syncs, so it runs inside the command on every PC) opens Dialog_SelectGeneseedVial / Dialog_SelectChapter on every PC. Its Accept
    /// calls GeneseedVialKindUtility.CreateBill / ChapterChoiceUtility.CreateBill in interface code: BillStack.AddBill is synced by
    /// Multiplayer with the bill exposed at that moment, and the bill's Part is set only AFTER AddBill (Genes40k.decompiled.cs:31750-31751,
    /// :31917-31918), so every PC got the bill with Part null; and every PC's dialog could add its own bill. Fix: in multiplayer a CreateBill
    /// call from interface code sends one SyncedSurgeryBill(pawn, recipe defName, body part index, chapter?, vial defName, gene defName)
    /// and writes nothing; the command runs the mod's own CreateBill on every PC (AddBill is direct there, Part is set on the same bill) and
    /// closes that dialog on every PC. Dialogs opened inside a command are recorded under (pawn, recipe, part index); the first command
    /// for a recorded dialog wins, a later one does nothing. Single player unchanged.
    /// </remarks>
    [MpCompatFor("phonicmas.rimdark.mankindsfinest")]
    internal class RimDark40kSerfOath
    {
        private const string LogPrefix = "[MPCompat - RimDark 40k Mankind's Finest] ";
        private static MethodInfo makeSerf, isSerf, hasDeclined, declinedNotify;
        private static Type utilsType;

        public RimDark40kSerfOath(ModContentPack mod)
        {
            Patch();
            PatchSurgeryBill();
        }

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

        // ---- FIXES row 491: geneseed vial / chapter picker surgery bills ----
        private sealed class PickerEntry
        {
            public Game game;
            public Pawn pawn;
            public string recipe;
            public int part;
            public bool chapter;
            public Window dialog;
            public bool resolved;
        }

        private static readonly List<PickerEntry> pickers = new List<PickerEntry>();
        private static MethodInfo vialCreateBill, chapterCreateBill;
        private static Type vialKindType, chapterChoiceType, vialDialogType, chapterDialogType;
        private static FieldInfo kindVialDef, kindChapterGene, choiceChapterGene;

        internal static void PatchSurgeryBill()
        {
            if (LabOff.Row(491))
            {
                Log.Message(LogPrefix + "row 491 switched off by the lab, geneseed vial / chapter surgery bills not synced");
                return;
            }
            var vialUtil = AccessTools.TypeByName("Genes40k.GeneseedVialKindUtility");
            var chapterUtil = AccessTools.TypeByName("Genes40k.ChapterChoiceUtility");
            vialKindType = AccessTools.TypeByName("Genes40k.GeneseedVialKind");
            chapterChoiceType = AccessTools.TypeByName("Genes40k.ChapterChoice");
            vialDialogType = AccessTools.TypeByName("Genes40k.Dialog_SelectGeneseedVial");
            chapterDialogType = AccessTools.TypeByName("Genes40k.Dialog_SelectChapter");
            vialCreateBill = vialUtil == null ? null : AccessTools.DeclaredMethod(vialUtil, "CreateBill");
            chapterCreateBill = chapterUtil == null ? null : AccessTools.DeclaredMethod(chapterUtil, "CreateBill");
            kindVialDef = vialKindType == null ? null : AccessTools.Field(vialKindType, "vialDef");
            kindChapterGene = vialKindType == null ? null : AccessTools.Field(vialKindType, "chapterGene");
            choiceChapterGene = chapterChoiceType == null ? null : AccessTools.Field(chapterChoiceType, "chapterGene");
            if (vialCreateBill == null || chapterCreateBill == null || kindVialDef == null || kindChapterGene == null || choiceChapterGene == null
                || vialDialogType == null || chapterDialogType == null)
            {
                Log.Error(LogPrefix + "row 491: GeneseedVialKindUtility / ChapterChoiceUtility.CreateBill, their argument types or the picker dialogs not found, the surgery bill part is lost in multiplayer");
                return;
            }
            MP.RegisterSyncMethod(typeof(RimDark40kSerfOath), nameof(SyncedSurgeryBill));
            MpCompat.harmony.Patch(vialCreateBill, prefix: new HarmonyMethod(typeof(RimDark40kSerfOath), nameof(PreVialBill)));
            MpCompat.harmony.Patch(chapterCreateBill, prefix: new HarmonyMethod(typeof(RimDark40kSerfOath), nameof(PreChapterBill)));
            foreach (var t in new[] { vialDialogType, chapterDialogType })
                foreach (var ctor in AccessTools.GetDeclaredConstructors(t))
                    if (!ctor.IsStatic) MpCompat.harmony.Patch(ctor, postfix: new HarmonyMethod(typeof(RimDark40kSerfOath), nameof(PostPickerCtor)));
            Log.Message(LogPrefix + "row 491: geneseed vial / chapter surgery bill pickers synced");
        }

        private static int PartIndex(Pawn pawn, BodyPartRecord part) => part == null ? -1 : pawn?.RaceProps?.body?.GetIndexOfPart(part) ?? -1;

        // records a picker opened inside a command (the synced CreateSurgeryBill): every PC records the same list in the same order
        internal static void PostPickerCtor(Window __instance, object[] __args)
        {
            if (!MP.IsInMultiplayer || !MP.IsExecutingSyncCommand || __instance == null || __args == null) return;
            Pawn pawn = null;
            RecipeDef recipe = null;
            BodyPartRecord part = null;
            foreach (var a in __args)
            {
                if (pawn == null && a is Pawn p) pawn = p;
                else if (recipe == null && a is RecipeDef r) recipe = r;
                else if (part == null && a is BodyPartRecord b) part = b;
            }
            if (pawn == null || recipe == null) return;
            var game = Current.Game;
            pickers.RemoveAll(e => !ReferenceEquals(e.game, game));
            pickers.Add(new PickerEntry
            {
                game = game, pawn = pawn, recipe = recipe.defName, part = PartIndex(pawn, part),
                chapter = chapterDialogType.IsInstanceOfType(__instance), dialog = __instance,
            });
        }

        internal static bool PreVialBill(Pawn __0, RecipeDef __1, BodyPartRecord __2, object __3)
            => PreBill(false, __0, __1, __2, __3 == null ? null : kindVialDef.GetValue(__3) as Def, __3 == null ? null : kindChapterGene.GetValue(__3) as Def);

        internal static bool PreChapterBill(Pawn __0, RecipeDef __1, BodyPartRecord __2, object __3)
            => PreBill(true, __0, __1, __2, null, __3 == null ? null : choiceChapterGene.GetValue(__3) as Def);

        private static bool PreBill(bool chapter, Pawn pawn, RecipeDef recipe, BodyPartRecord part, Def vial, Def gene)
        {
            if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand || pawn == null || recipe == null) return true;
            SyncedSurgeryBill(pawn, recipe.defName, PartIndex(pawn, part), chapter, vial?.defName ?? "", gene?.defName ?? "");
            return false;
        }

        internal static void SyncedSurgeryBill(Pawn pawn, string recipeName, int partIndex, bool chapter, string vialName, string geneName)
        {
            if (pawn == null) return;
            var game = Current.Game;
            pickers.RemoveAll(e => !ReferenceEquals(e.game, game));
            var same = pickers.FindAll(e => e.pawn == pawn && e.recipe == recipeName && e.part == partIndex && e.chapter == chapter);
            var entry = same.Find(e => !e.resolved);
            if (same.Count > 0 && entry == null) return; // that dialog was decided by an earlier command
            var recipe = DefDatabase<RecipeDef>.GetNamedSilentFail(recipeName);
            var part = partIndex < 0 ? null : pawn.RaceProps?.body?.GetPartAtIndex(partIndex);
            var gene = string.IsNullOrEmpty(geneName) ? null : DefDatabase<GeneDef>.GetNamedSilentFail(geneName);
            var vial = string.IsNullOrEmpty(vialName) ? null : DefDatabase<ThingDef>.GetNamedSilentFail(vialName);
            if (recipe == null || (partIndex >= 0 && part == null) || (!chapter && vial == null) || (!string.IsNullOrEmpty(geneName) && gene == null))
            {
                Log.Warning(LogPrefix + $"row 491: surgery bill {recipeName} part {partIndex} vial {vialName} gene {geneName} does not resolve on this PC, not added");
                return;
            }
            object arg;
            if (chapter)
            {
                arg = Activator.CreateInstance(chapterChoiceType, true);
                choiceChapterGene.SetValue(arg, gene);
            }
            else
            {
                arg = Activator.CreateInstance(vialKindType, true);
                kindVialDef.SetValue(arg, vial);
                kindChapterGene.SetValue(arg, gene);
            }
            if (entry != null) entry.resolved = true;
            (chapter ? chapterCreateBill : vialCreateBill).Invoke(null, new object[] { pawn, recipe, part, arg });
            if (entry?.dialog != null && Current.Root?.uiRoot?.windows?.IsOpen(entry.dialog) == true) entry.dialog.Close(false);
        }
    }
}
