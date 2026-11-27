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

            if (LabOff.Row(534))
                Log.Message(LogPrefix + "row 534 switched off by the lab, the research proficiency settings and dialog stay local to each PC");
            else
                PatchRow534();
        }

        // ---- Row 534 (27 Nov 2026) ----
        // ResearchManager_FinishProject_Patch.Postfix reads EducationMod.settings.enableProficiencySystem and bestowProficiencyToAll (per-PC
        // mod settings, not synced), then either grants the trait to every player pawn (sim) or opens Dialog_BestowProficiency (forcePause) on
        // every PC; its button calls ProficiencyUtility.GrantProficiencyTrait and a letter locally (unsynced trait add).
        // Fix, multiplayer only: both settings live in a saved GameComponent and are synced when a settings window closes; the Postfix runs with
        // the synced values; the dialog opens only on PCs whose own faction is the finishing faction (Faction.OfPlayer in that call);
        // the button grant and letter go through one synced method that also closes the open dialogs everywhere.
        internal static FieldInfo enableField, bestowField, modSettingsField, dialogExtensionField;
        internal static System.Type modType, dialogType;
        internal static MethodInfo grantMethod;
        internal static volatile bool SettingsActive;
        internal static bool inDialogDraw;
        internal static string dialogTitle, dialogDesc;

        private static void PatchRow534()
        {
            modType = AccessTools.TypeByName("ProgressionEducation.EducationMod");
            var settingsType = AccessTools.TypeByName("ProgressionEducation.EducationSettings");
            dialogType = AccessTools.TypeByName("ProgressionEducation.Dialog_BestowProficiency");
            modSettingsField = modType == null ? null : AccessTools.Field(modType, "settings");
            enableField = settingsType == null ? null : AccessTools.Field(settingsType, "enableProficiencySystem");
            bestowField = settingsType == null ? null : AccessTools.Field(settingsType, "bestowProficiencyToAll");
            dialogExtensionField = dialogType == null ? null : AccessTools.Field(dialogType, "extension");
            var postfix = AccessTools.Method("ProgressionEducation.ResearchManager_FinishProject_Patch:Postfix");
            grantMethod = AccessTools.Method("ProgressionEducation.ProficiencyUtility:GrantProficiencyTrait");
            var draw = dialogType == null ? null : AccessTools.Method(dialogType, "DoWindowContents");
            var letters = AccessTools.GetDeclaredMethods(typeof(LetterStack)).Where(m => m.Name == nameof(LetterStack.ReceiveLetter)).Where(m =>
            {
                var p = m.GetParameters();
                return p.Length >= 3 && p[0].ParameterType == typeof(TaggedString) && p[1].ParameterType == typeof(TaggedString) && p[2].ParameterType == typeof(LetterDef);
            }).ToList();
            if (modType == null || modSettingsField == null || enableField == null || bestowField == null || enableField.FieldType != typeof(bool)
                || bestowField.FieldType != typeof(bool) || dialogExtensionField == null || postfix == null || grantMethod == null || draw == null || letters.Count == 0)
            {
                Log.Error(LogPrefix + "row 534: members not found (mod " + (modType != null) + ", settings fields " + (enableField != null && bestowField != null)
                          + ", dialog " + (dialogType != null && dialogExtensionField != null) + ", FinishProject postfix " + (postfix != null) + ", GrantProficiencyTrait "
                          + (grantMethod != null) + ", DoWindowContents " + (draw != null) + ", ReceiveLetter overloads " + letters.Count + "), settings and dialog stay local");
                return;
            }
            MpCompat.harmony.Patch(postfix, prefix: new HarmonyMethod(typeof(ProgressionEducationCompat), nameof(FinishPrefix)),
                postfix: new HarmonyMethod(typeof(ProgressionEducationCompat), nameof(FinishRestore)));
            MpCompat.harmony.Patch(draw, prefix: new HarmonyMethod(typeof(ProgressionEducationCompat), nameof(DrawPrefix)),
                finalizer: new HarmonyMethod(typeof(ProgressionEducationCompat), nameof(DrawFinalizer)));
            MpCompat.harmony.Patch(grantMethod, prefix: new HarmonyMethod(typeof(ProgressionEducationCompat), nameof(GrantPrefix)));
            foreach (var m in letters)
                MpCompat.harmony.Patch(m, prefix: new HarmonyMethod(typeof(ProgressionEducationCompat), nameof(LetterPrefix)));
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(Mod), nameof(Mod.WriteSettings)),
                postfix: new HarmonyMethod(typeof(ProgressionEducationCompat), nameof(WriteSettingsPostfix)));
            MP.RegisterSyncMethod(typeof(ProgressionEducationCompat), nameof(SyncedSet));
            MP.RegisterSyncMethod(typeof(ProgressionEducationCompat), nameof(SyncedGrant));
            SettingsActive = true;
            Log.Message(LogPrefix + "row 534: enableProficiencySystem and bestowProficiencyToAll are saved with the game and synced, the trait dialog opens for the finishing faction's PC and its choice is synced");
        }

        private static object LocalSettings => modSettingsField.GetValue(null);
        internal static bool LocalEnable => (bool)enableField.GetValue(LocalSettings);
        internal static bool LocalBestow => (bool)bestowField.GetValue(LocalSettings);

        // Prefix of the mod's FinishProject postfix. Not multiplayer or no component: nothing changes.
        internal static bool FinishPrefix(out (bool enable, bool bestow)? __state)
        {
            __state = null;
            if (!SettingsActive || !MP.IsInMultiplayer)
                return true;
            var synced = ProgressionEducationSyncedSettings.Instance;
            if (synced == null || !synced.captured)
                return true;
            // The dialog branch (bestow to all is off, past the start of the game) belongs to the PC of the finishing faction only; the
            // trait grant loop (the other branch) is simulation and runs everywhere.
            var s = LocalSettings;
            if (synced.enable && !synced.bestow && Find.TickManager.TicksGame >= 60 && Faction.OfPlayer != MP.RealPlayerFaction)
                return false;
            __state = (LocalEnable, LocalBestow);
            enableField.SetValue(s, synced.enable);
            bestowField.SetValue(s, synced.bestow);
            return true;
        }

        internal static void FinishRestore((bool enable, bool bestow)? __state)
        {
            if (__state == null)
                return;
            var s = LocalSettings;
            enableField.SetValue(s, __state.Value.enable);
            bestowField.SetValue(s, __state.Value.bestow);
        }

        internal static void DrawPrefix(Window __instance)
        {
            inDialogDraw = true;
            var ext = dialogExtensionField.GetValue(__instance);
            dialogTitle = ext == null ? "" : (string)AccessTools.Field(ext.GetType(), "title").GetValue(ext);
            dialogDesc = ext == null ? "" : (string)AccessTools.Field(ext.GetType(), "desc").GetValue(ext);
        }

        internal static void DrawFinalizer() => inDialogDraw = false;

        private static bool InDialogClick => inDialogDraw && MP.IsInMultiplayer && !MP.IsExecutingSyncCommand;

        // The dialog button: the trait add becomes the synced call (which also sends the letter).
        internal static bool GrantPrefix(Pawn pawn, TraitDef traitToAdd)
        {
            if (!InDialogClick)
                return true;
            SyncedGrant(pawn, traitToAdd, dialogTitle, dialogDesc);
            return false;
        }

        // The dialog button's own letter would be local to that PC; SyncedGrant sends it on every PC.
        internal static bool LetterPrefix() => !InDialogClick;

        // Runs on every PC from the same arguments.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void SyncedGrant(Pawn pawn, TraitDef trait, string title, string desc)
        {
            if (pawn != null && trait != null)
                grantMethod.Invoke(null, new object[] { pawn, trait, false });
            Find.LetterStack.ReceiveLetter(title ?? "", desc ?? "", LetterDefOf.PositiveEvent);
            foreach (var w in Find.WindowStack.Windows.Where(w => w.GetType() == dialogType).ToList())
                w.Close();
        }

        internal static void WriteSettingsPostfix(Mod __instance)
        {
            try
            {
                if (!SettingsActive || Current.Game == null || __instance == null || __instance.GetType() != modType)
                    return;
                var synced = ProgressionEducationSyncedSettings.Instance;
                if (!MP.IsInMultiplayer)
                {
                    synced?.CaptureLocal();
                    return;
                }
                if (!MP.InInterface || synced == null)
                    return;
                if (synced.captured && synced.enable == LocalEnable && synced.bestow == LocalBestow)
                    return;
                SyncedSet(LocalEnable, LocalBestow);
            }
            catch (System.Exception e)
            {
                Log.Error(LogPrefix + "row 534: settings window hook failed: " + e);
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void SyncedSet(bool enable, bool bestow)
        {
            var synced = ProgressionEducationSyncedSettings.Instance;
            if (synced == null)
                return;
            synced.enable = enable;
            synced.bestow = bestow;
            synced.captured = true;
            Messages.Message(LogPrefix + "settings changed by a player: proficiency system " + enable + ", research proficiency to all pawns " + bestow,
                MessageTypeDefOf.NeutralEvent, false);
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

    /// <summary>The 2 Progression: Education values the FinishProject postfix uses in multiplayer, saved with the game (row 534). Inert unless active.</summary>
    public class ProgressionEducationSyncedSettings : GameComponent
    {
        public static ProgressionEducationSyncedSettings Instance;
        // The mod's own defaults (EducationSettings field initializers).
        public bool enable = true;
        public bool bestow;
        public bool captured;

        public ProgressionEducationSyncedSettings(Game game) { }

        public void CaptureLocal()
        {
            enable = ProgressionEducationCompat.LocalEnable;
            bestow = ProgressionEducationCompat.LocalBestow;
            captured = true;
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            Instance = this;
            if (!ProgressionEducationCompat.SettingsActive)
                return;
            if (!MP.IsInMultiplayer || (!captured && MP.IsHosting))
                CaptureLocal();
            else if (!captured)
            {
                captured = true;
                Log.Message("[MPCompat - Progression Education] row 534: no saved values in this multiplayer save, using the mod defaults on every PC. A player can push theirs by closing the settings window.");
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            if (!ProgressionEducationCompat.SettingsActive)
                return;
            Scribe_Values.Look(ref captured, "captured", false, true);
            Scribe_Values.Look(ref enable, "enable", true, true);
            Scribe_Values.Look(ref bestow, "bestow", false, true);
        }
    }
}
