using System;
using System.Linq;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Character Editor by void</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=1874644848"/>
    /// <remarks>
    /// Written against CharacterEditor.dll v1.6.3 from the mod's v1.6 folder (the folder 1.7 falls back to).
    /// FIXES row 193 (live co-op 22 Oct, James's multifaction Settle): Character Editor's "load pawn from slot" on the
    /// starting-pawn page did not put the saved pawn on the page.
    /// Cause: CE decides "starting screen or running game" with CEditor.InStartingScreen
    /// (= Current.ProgramState != Playing || bGamePlus). Our Multiplayer build runs the page's own DoWindowContents
    /// under ProgramState.Entry, but CE's editor window and its slot float menu draw in their own windows, under
    /// Playing. So the load took CE's in-game route: PresetPawn.LoadPawn -> AddOrCreateExistingPawn ->
    /// SpawnOrPassPawn -> "place pawn" debug tool on the current map (OnMap) or PassToWorld - never
    /// GameInitData.startingAndOptionalPawns. Both in-game routes change the game on one PC only.
    /// Fix, only in Multiplayer and only while our Multiplayer build's multifaction pawn page is open on this PC:
    /// 1. CEditor.bGamePlus = true (CE's own "pawn page inside a running game" mode): the editor lists the page's
    ///    pawns and a loaded pawn goes into startingAndOptionalPawns. Set from the page's DoWindowContents, cleared
    ///    in every pawn page's PreOpen (CE's GamePlusPreOpen postfix must see false there), in the multifaction
    ///    page's PostClose and in Game.FinalizeInit. A field, not a patch on the InStartingScreen getter: that getter
    ///    is tiny and CharacterEditor.dll is not on the Waylith JIT's no-inline list.
    /// 2. PresetPawn.LoadPawn and PawnxTool.AddOrCreateNewPawn run under ProgramState.Entry, the way our Multiplayer
    ///    build makes the page's own pawns and runs StartingPawnUtility.RandomizePawn: no synced calls (InInterface
    ///    false), local unique ids, taleless art. Inside them (review, 2 Nov): no generated relations (CE asks for
    ///    them; our build turns them off for page pawns), no relations restored from the slot (CE finds the other
    ///    pawn by name among the real pawns), and the slot's faction is ignored (CE's faction cache can hand back
    ///    another player's faction) - each would write to real pawns or factions on one PC only.
    /// 3. PawnxTool.PassToWorld is refused (a world pawn on one PC only), and CE's capsule window is refused (its
    ///    Start calls the page's nextAct directly, past DoNext; its Load rewrites the scenario on one PC).
    /// 4. When the page closes, every open CE window closes first, while CE still sees a starting screen.
    /// MP safety: a loaded pawn is per-PC input that stays local, in the pre-game page data, until the page's Next
    /// sends it to every PC inside the synced FactionCreator.SendPawn command (the pawn is written as bytes).
    /// </remarks>
    [MpCompatFor("void.charactereditor")]
    internal class CharacterEditor
    {
        private const string LogPrefix = "[MPCompat - Character Editor] ";

        private static Type multifactionPageType;
        private static System.Reflection.Assembly ceAssembly;
        private static AccessTools.FieldRef<bool> gamePlusField;
        private static bool gamePlusSetByUs;
        private static int entryDepth;

        public CharacterEditor(ModContentPack mod)
        {
            multifactionPageType = AccessTools.TypeByName("Multiplayer.Client.Factions.Page_ConfigureStartingPawns_Multifaction");
            if (multifactionPageType == null)
            {
                Log.Warning($"{LogPrefix}Page_ConfigureStartingPawns_Multifaction not found: slot loads on the Settle pawn page stay in-game (FIXES row 193)");
                return;
            }

            var editorType = AccessTools.TypeByName("CharacterEditor.CEditor");
            var gamePlus = AccessTools.DeclaredField(editorType, "bGamePlus");
            var loadPawn = AccessTools.DeclaredMethod("CharacterEditor.PresetPawn:LoadPawn");
            var addOrCreate = AccessTools.DeclaredMethod("CharacterEditor.PawnxTool:AddOrCreateNewPawn");
            var passToWorld = AccessTools.DeclaredMethod("CharacterEditor.PawnxTool:PassToWorld");
            var setRelations = AccessTools.DeclaredMethod("CharacterEditor.RelationTool:SetRelationsFromSeparatedString");
            var slotFaction = AccessTools.DeclaredMethod("CharacterEditor.FactionTool:GetBySeparatedString");
            var capsuleContents = AccessTools.DeclaredMethod("CharacterEditor.DialogCapsuleUI:DoWindowContents");
            if (gamePlus == null || !gamePlus.IsStatic || gamePlus.FieldType != typeof(bool) || loadPawn == null || addOrCreate == null
                || passToWorld == null || setRelations == null || slotFaction == null || capsuleContents == null)
            {
                Log.Error($"{LogPrefix}a Character Editor member was not found: FIXES row 193 fix not applied");
                return;
            }
            ceAssembly = editorType.Assembly;
            gamePlusField = AccessTools.StaticFieldRefAccess<bool>(gamePlus);

            var self = typeof(CharacterEditor);
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(Page_ConfigureStartingPawns), nameof(Page_ConfigureStartingPawns.DoWindowContents)),
                prefix: new HarmonyMethod(self, nameof(PrePageDoWindowContents)));
            // Base PreOpen (the multifaction page calls it): CE's GamePlusPreOpen postfix there must never see our flag,
            // on this page or on any later pawn page. Game.FinalizeInit: no flag of ours survives into another game.
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(Page_ConfigureStartingPawns), nameof(Window.PreOpen)),
                prefix: new HarmonyMethod(self, nameof(ClearGamePlus)));
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(Game), nameof(Game.FinalizeInit)),
                prefix: new HarmonyMethod(self, nameof(ClearGamePlus)));
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(multifactionPageType, nameof(Window.PostClose)),
                prefix: new HarmonyMethod(self, nameof(PrePagePostClose)));

            var entry = new HarmonyMethod(self, nameof(PreEnterEntryState));
            var restore = new HarmonyMethod(self, nameof(RestoreProgramState));
            MpCompat.harmony.Patch(loadPawn, prefix: entry, finalizer: restore);
            MpCompat.harmony.Patch(addOrCreate, prefix: entry, finalizer: restore);
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(PawnGenerator), nameof(PawnGenerator.GeneratePawn), new[] { typeof(PawnGenerationRequest) }),
                prefix: new HarmonyMethod(self, nameof(PreGeneratePawn)));
            MpCompat.harmony.Patch(setRelations, prefix: new HarmonyMethod(self, nameof(PreSetRelations)));
            MpCompat.harmony.Patch(slotFaction, prefix: new HarmonyMethod(self, nameof(PreSlotFaction)));
            MpCompat.harmony.Patch(passToWorld, prefix: new HarmonyMethod(self, nameof(PrePassToWorld)));
            MpCompat.harmony.Patch(capsuleContents, prefix: new HarmonyMethod(self, nameof(PreCapsuleContents)));
        }

        private static bool PageOpen => MP.IsInMultiplayer && Find.WindowStack != null && Find.WindowStack.IsOpen(multifactionPageType);

        private static void PrePageDoWindowContents(Page_ConfigureStartingPawns __instance)
        {
            if (!MP.IsInMultiplayer || __instance.GetType() != multifactionPageType)
                return;
            gamePlusField() = true;
            gamePlusSetByUs = true;
        }

        private static void ClearGamePlus()
        {
            if (!gamePlusSetByUs)
                return;
            gamePlusField() = false;
            gamePlusSetByUs = false;
        }

        private static void PrePagePostClose()
        {
            if (!gamePlusSetByUs)
                return;
            // Close CE's windows while CE still treats them as the starting screen: in game mode EditorUI.Close ejects
            // CE's casket on the map, and CE's item / capsule windows offer map actions.
            try
            {
                foreach (var window in Find.WindowStack.Windows.Where(w => w.GetType().Assembly == ceAssembly).ToList())
                    window.Close(false);
            }
            catch (Exception e)
            {
                Log.Warning($"{LogPrefix}could not close Character Editor windows with the Settle pawn page: {e}");
            }
            ClearGamePlus();
        }

        // __state: the ProgramState to restore, or -1 when this call did not switch.
        private static void PreEnterEntryState(ref int __state)
        {
            __state = -1;
            if (Current.ProgramState != ProgramState.Playing || MP.IsExecutingSyncCommand || !PageOpen)
                return;
            __state = (int)Current.ProgramState;
            Current.programStateInt = ProgramState.Entry;
            entryDepth++;
        }

        private static void RestoreProgramState(int __state)
        {
            if (__state < 0)
                return;
            Current.programStateInt = (ProgramState)__state;
            entryDepth--;
        }

        private static void PreGeneratePawn(ref PawnGenerationRequest request)
        {
            if (entryDepth > 0)
                request.CanGeneratePawnRelations = false;
        }

        private static bool PreSetRelations() => entryDepth <= 0;

        private static bool PreSlotFaction(Faction factionFallback, ref Faction __result)
        {
            if (entryDepth <= 0)
                return true;
            __result = factionFallback;
            return false;
        }

        private static bool PrePassToWorld(Pawn p)
        {
            if (!PageOpen)
                return true;
            Find.GameInitData?.startingPossessions?.Remove(p);
            Messages.Message("Character Editor: on the multifaction Settle page only the colonist list can take new pawns (Multiplayer).",
                MessageTypeDefOf.RejectInput, false);
            return false;
        }

        private static bool PreCapsuleContents(Window __instance)
        {
            if (!gamePlusSetByUs)
                return true;
            __instance.Close(false);
            Messages.Message("Character Editor: the capsule is not available on the multifaction Settle page (Multiplayer).",
                MessageTypeDefOf.RejectInput, false);
            return false;
        }
    }
}
