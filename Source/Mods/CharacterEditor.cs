using System;
using System.Collections.Generic;
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
    /// Row 193b (other CE edits of page pawns): 5. CE windows' own DoWindowContents and CE float menu choices run
    ///    under ProgramState.Entry while CE's selected pawn is a page pawn or none, like MP's own page UI, so synced
    ///    setters on page pawns run on this PC instead of sending a command no other PC can resolve.
    /// 6. On the page: CE's pawn list is the colonist list (the page pawns), a real pawn left selected is dropped,
    ///    no relation is added inside our Entry scope, and CE's faction window and ideo button are refused.
    /// 7. CE's Delete of a page pawn keeps its list part and skips Pawn.Destroy and
    ///    WorldPawns.RemoveAndDiscardPawnViaGC, which would change the game on this PC only.
    /// Row 193c (the three gaps 193b left open, 2 Nov):
    /// 8. CE's pawn picker (DialogChoosePawn: other pawn of a thought, of a hediff such as pregnancy or a psychic bond, or a
    ///    relation partner) lists every pawn of the game, real pawns included (its first entry is CE's "humanoid" list with no
    ///    faction filter, PawnxTool.GetPawnList), and hands the choice straight to AddThought / AddHediff2. A real pawn stored
    ///    in a page pawn is a cross-PC reference the page pawn's SendPawn bytes may not resolve, and removing such a hediff
    ///    later runs vanilla code on the real pawn (Hediff_PsychicBond.PostRemoved -> Gene_PsychicBonding.RemoveBond). Another
    ///    page pawn is no better (its local id resolves on no PC, the same reason 193b adds no relation). So the picker is
    ///    refused while the page is open, like CE's faction window.
    /// 9. CE callbacks that vanilla windows call run under Playing, outside the DoWindowContents wrap of 5: CE's xenotype editor
    ///    DialogXenoType (it inherits GeneCreationDialogBase.DoWindowContents, so 5 never saw it) and every Dialog_MessageBox
    ///    confirmation whose action is a CE closure (xenotype save, apparel, title renounce). Both are wrapped now, the same
    ///    way (the confirmation also on its accept and cancel keys). Entry does not help with one write: GeneTool.SetPawnXenotype(CustomXenotype) adds the xenotype to
    ///    Current.Game.customXenotypeDatabase, a list in the save that PawnGenerator.AdjustXenotypeForFactionlessPawn reads
    ///    (Count, Rand.Range, TryRandomElement) - one PC with an extra entry draws other random numbers than the rest. For a
    ///    page pawn that call runs against a throwaway database; the pawn carries its own xenotype name, icon and genes.
    /// 10. Checked, no patch: the JIT cannot inline a patched CE method into a CE caller, because CharacterEditor.dll is a
    ///    Debug build (DebuggableAttribute with DisableOptimizations: the runtime compiles its methods unoptimized, and the
    ///    DynamicMethod Harmony builds for a patched CE caller was measured to follow that too). Measured on the lab's
    ///    Waylith CLR 10.0.13 with a Debug / Release pair of test assemblies carrying one tiny and one 57 to 77 byte method (the
    ///    Release pair skipped the patch when the caller was compiled before it, or was patched first; the Debug pair
    ///    never did). The constructor logs a warning if a CE update ever ships an optimized build.
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

        // Row 193b: CE's selected pawn (CEditor.API.Pawn), its list name, and the page pawn CE is deleting.
        private static System.Reflection.MethodInfo apiGetter, selectedPawnGetter, selectedPawnSetter, listNameSetter;
        private static System.Reflection.FieldInfo colonistsLabel;
        private static Pawn deletingPagePawn;

        // Row 193c: the non-virtual CE methods patched here (for the optimized-build warning), and the delegate fields of
        // each window type (to tell a vanilla window with a CE callback from any other).
        private static readonly List<System.Reflection.MethodBase> patchedCeMethods = new List<System.Reflection.MethodBase>();
        private static readonly Dictionary<Type, System.Reflection.FieldInfo[]> delegateFieldsByType = new Dictionary<Type, System.Reflection.FieldInfo[]>();

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
            patchedCeMethods.AddRange(new[] { loadPawn, addOrCreate, passToWorld, setRelations, slotFaction });
            PatchPagePawnEdits(editorType, self);
            PatchVanillaWindowPaths(self);
            WarnIfCeIsOptimized();
        }

        // Row 193b: every CE edit of a page pawn runs like the page's own UI (ProgramState.Entry), not only the load.
        private static void PatchPagePawnEdits(Type editorType, Type self)
        {
            apiGetter = AccessTools.PropertyGetter(editorType, "API");
            selectedPawnGetter = AccessTools.PropertyGetter(editorType, "Pawn");
            selectedPawnSetter = AccessTools.PropertySetter(editorType, "Pawn");
            listNameSetter = AccessTools.PropertySetter(editorType, "ListName");
            colonistsLabel = AccessTools.DeclaredField("CharacterEditor.Label:COLONISTS");
            var pawnListType = AccessTools.Inner(AccessTools.Inner(editorType, "EditorUI"), "BlockPawnList");
            var changeList = pawnListType == null ? null : AccessTools.DeclaredMethod(pawnListType, "ChangeList");
            var delete = AccessTools.DeclaredMethod("CharacterEditor.PawnxTool:Delete");
            if (apiGetter == null || selectedPawnGetter == null || listNameSetter == null || colonistsLabel == null || changeList == null || delete == null)
            {
                Log.Error($"{LogPrefix}a Character Editor member was not found: FIXES row 193b (page pawn edits) not applied");
                return;
            }

            patchedCeMethods.Add(changeList);
            patchedCeMethods.Add(delete);
            var restore = new HarmonyMethod(self, nameof(RestoreProgramState));
            // Only CE's own DoWindowContents (review 193b: not Window.InnerWindowOnGUI, whose click / accept / cancel
            // handling also closes and notifies windows of other mods).
            var ceWindowContents = ceAssembly.GetTypes()
                .Where(t => typeof(Window).IsAssignableFrom(t) && !t.IsAbstract && !t.ContainsGenericParameters)
                .Select(t => AccessTools.Method(t, nameof(Window.DoWindowContents), new[] { typeof(UnityEngine.Rect) }))
                .Where(m => m != null && m.DeclaringType != null && m.DeclaringType.Assembly == ceAssembly)
                .Distinct().ToList();
            foreach (var method in ceWindowContents)
            {
                try
                {
                    MpCompat.harmony.Patch(method, prefix: new HarmonyMethod(self, nameof(PreCeWindowContents)), finalizer: restore);
                }
                catch (Exception e)
                {
                    Log.Warning($"{LogPrefix}could not wrap {method.DeclaringType}.DoWindowContents (FIXES row 193b): {e.Message}");
                }
            }
            // Refused on the page: CE's faction window (its accept banishes a pawn of Faction.OfPlayer, under Entry the
            // page's temporary faction, and can queue that faction's removal), and CE's ideo button (opens vanilla
            // Dialog_ConfigureIdeo in dev edit mode on real ideos, which nothing syncs).
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod("CharacterEditor.DialogChangeFaction:DoWindowContents"),
                prefix: new HarmonyMethod(self, nameof(PreRefusedWindowContents)));
            var changeIdeo = ceAssembly.GetTypes().Select(t => AccessTools.DeclaredMethod(t, "AChangeIdeo")).FirstOrDefault(m => m != null);
            if (changeIdeo != null)
                MpCompat.harmony.Patch(changeIdeo, prefix: new HarmonyMethod(self, nameof(PreRefusedAction)));
            else
                Log.Warning($"{LogPrefix}AChangeIdeo not found: CE's ideo button stays open on the Settle page (FIXES row 193b)");
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(FloatMenuOption), nameof(FloatMenuOption.Chosen)),
                prefix: new HarmonyMethod(self, nameof(PreCeFloatMenuChosen)), finalizer: restore);
            MpCompat.harmony.Patch(changeList, prefix: new HarmonyMethod(self, nameof(PreChangeList)));
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(Pawn_RelationsTracker), nameof(Pawn_RelationsTracker.AddDirectRelation)),
                prefix: new HarmonyMethod(self, nameof(PreAddDirectRelation)));
            // Both RemoveDirectRelation overloads end in TryRemoveDirectRelation.
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(Pawn_RelationsTracker), nameof(Pawn_RelationsTracker.TryRemoveDirectRelation)),
                prefix: new HarmonyMethod(self, nameof(PreTryRemoveDirectRelation)));
            MpCompat.harmony.Patch(delete, prefix: new HarmonyMethod(self, nameof(PreCeDelete)), finalizer: new HarmonyMethod(self, nameof(EndCeDelete)));
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(Pawn), nameof(Pawn.Destroy)), prefix: new HarmonyMethod(self, nameof(PrePawnDestroy)));
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(RimWorld.Planet.WorldPawns), nameof(RimWorld.Planet.WorldPawns.RemoveAndDiscardPawnViaGC)),
                prefix: new HarmonyMethod(self, nameof(PreDiscardViaGC)));
        }

        // Row 193c: the pawn picker, the vanilla windows that call CE code, and the xenotype database.
        private static void PatchVanillaWindowPaths(Type self)
        {
            if (apiGetter == null || selectedPawnGetter == null)
                return; // PatchPagePawnEdits logged the missing member

            var choosePawn = AccessTools.DeclaredMethod("CharacterEditor.DialogChoosePawn:DoWindowContents");
            if (choosePawn != null)
                MpCompat.harmony.Patch(choosePawn, prefix: new HarmonyMethod(self, nameof(PreRefusedPawnPicker)));
            else
                Log.Warning($"{LogPrefix}DialogChoosePawn not found: CE's pawn picker can still pick a real pawn on the Settle page (FIXES row 193c)");

            // DialogXenoType inherits GeneCreationDialogBase.DoWindowContents, so the wrap of every CE-declared
            // DoWindowContents never saw it. Dialog_MessageBox runs its button, accept and cancel actions in these three.
            var enter = new HarmonyMethod(self, nameof(PreVanillaWindowOfCe));
            var restore = new HarmonyMethod(self, nameof(RestoreProgramState));
            var rectArg = new[] { typeof(UnityEngine.Rect) };
            var vanillaWindowMethods = new[]
            {
                AccessTools.DeclaredMethod(typeof(GeneCreationDialogBase), nameof(Window.DoWindowContents), rectArg),
                AccessTools.DeclaredMethod(typeof(Dialog_MessageBox), nameof(Window.DoWindowContents), rectArg),
                AccessTools.DeclaredMethod(typeof(Dialog_MessageBox), nameof(Window.OnAcceptKeyPressed)),
                AccessTools.DeclaredMethod(typeof(Dialog_MessageBox), nameof(Window.OnCancelKeyPressed)),
            };
            foreach (var method in vanillaWindowMethods)
            {
                if (method == null)
                {
                    Log.Warning($"{LogPrefix}a vanilla window method was not found: CE's xenotype editor and confirmations stay outside the Settle page's Entry scope (FIXES row 193c)");
                    continue;
                }
                MpCompat.harmony.Patch(method, prefix: enter, finalizer: restore);
            }

            var setXenotype = AccessTools.DeclaredMethod("CharacterEditor.GeneTool:SetPawnXenotype", new[] { typeof(Pawn), typeof(CustomXenotype), typeof(bool) });
            if (setXenotype != null)
            {
                MpCompat.harmony.Patch(setXenotype, prefix: new HarmonyMethod(self, nameof(PreSetPawnXenotypeCustom)),
                    finalizer: new HarmonyMethod(self, nameof(RestoreXenotypeDatabase)));
                patchedCeMethods.Add(setXenotype);
            }
            else
                Log.Warning($"{LogPrefix}GeneTool.SetPawnXenotype(CustomXenotype) not found: a custom xenotype set on a page pawn still adds to the real game's database (FIXES row 193c)");
        }

        // Row 193c: no pawn picker while the page is open. It lists every pawn of the game, and its choice goes straight into
        // a thought or hediff (other pawn), or a relation (partner, parents): a real pawn, or another page pawn, referenced
        // from a page pawn.
        private static bool PreRefusedPawnPicker(Window __instance)
        {
            if (!gamePlusSetByUs)
                return true;
            __instance.Close(false);
            RejectOnPage("choosing another pawn (thought, hediff, relation) is not available on the multifaction Settle page (Multiplayer).");
            return false;
        }

        // Row 193c: a window that is not in CE's DoWindowContents wrap (an inherited vanilla one, or a vanilla confirmation
        // box) but runs CE code gets the same Entry scope as a CE window, while the page is open and CE's selected pawn
        // is a page pawn or none.
        private static void PreVanillaWindowOfCe(Window __instance, ref int __state)
        {
            __state = -1;
            if (gamePlusSetByUs && OwnedByCharacterEditor(__instance) && SelectedIsPageLocalOrNone())
                TryEnterEntryState(ref __state);
        }

        // A window of a CE type, or a vanilla window with a delegate (Dialog_MessageBox: button and key actions) that is a
        // CE method or a CE closure.
        private static bool OwnedByCharacterEditor(Window window)
        {
            var type = window.GetType();
            if (type.Assembly == ceAssembly)
                return true;
            if (!delegateFieldsByType.TryGetValue(type, out var fields))
            {
                var found = new List<System.Reflection.FieldInfo>();
                const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;
                for (var t = type; t != null && t != typeof(Window); t = t.BaseType)
                    found.AddRange(t.GetFields(flags).Where(f => typeof(Delegate).IsAssignableFrom(f.FieldType)));
                fields = found.ToArray();
                delegateFieldsByType[type] = fields;
            }
            foreach (var field in fields)
            {
                if (field.GetValue(window) is Delegate action && action.Method.DeclaringType?.Assembly == ceAssembly)
                    return true;
            }
            return false;
        }

        // Row 193c: GeneTool.SetPawnXenotype(CustomXenotype) adds the xenotype to Current.Game.customXenotypeDatabase. That list
        // is in the save and PawnGenerator.AdjustXenotypeForFactionlessPawn reads it (Count, Rand.Range, TryRandomElement), so
        // an entry on one PC draws other random numbers than on the rest. For a page pawn the call gets a throwaway database.
        // The pawn keeps its xenotype name, icon and genes, and the page's generation request keeps the CustomXenotype itself.
        private static void PreSetPawnXenotypeCustom(Pawn __0, ref CustomXenotypeDatabase __state)
        {
            __state = null;
            if (!gamePlusSetByUs || Current.Game == null || !IsPageLocal(__0))
                return;
            __state = Current.Game.customXenotypeDatabase;
            Current.Game.customXenotypeDatabase = new CustomXenotypeDatabase();
        }

        private static void RestoreXenotypeDatabase(CustomXenotypeDatabase __state)
        {
            if (__state != null && Current.Game != null)
                Current.Game.customXenotypeDatabase = __state;
        }

        // Row 193c, item 10: the tripwire for an optimized CharacterEditor.dll. A patch on a small CE method can be skipped when
        // the JIT inlines the method into a CE caller; the Debug build CE ships today is never inlined into.
        private const int InlineWarnIlBytes = 100;

        private static void WarnIfCeIsOptimized()
        {
            try
            {
                var debuggable = ceAssembly.GetCustomAttributes(typeof(System.Diagnostics.DebuggableAttribute), false)
                    .OfType<System.Diagnostics.DebuggableAttribute>().FirstOrDefault();
                if (debuggable != null && debuggable.IsJITOptimizerDisabled)
                    return;
                var small = new List<string>();
                foreach (var method in patchedCeMethods)
                {
                    var size = method.GetMethodBody()?.GetILAsByteArray()?.Length ?? -1;
                    if (size >= 0 && size <= InlineWarnIlBytes)
                        small.Add($"{method.DeclaringType?.Name}.{method.Name} ({size} bytes of IL)");
                }
                if (small.Count > 0)
                    Log.Warning($"{LogPrefix}CharacterEditor.dll is no longer a Debug build, so the JIT may inline these patched methods into CE callers "
                        + $"and skip their patches (FIXES row 193c): {string.Join(", ", small)}. Patch the callers instead.");
            }
            catch (Exception e)
            {
                Log.Warning($"{LogPrefix}could not check whether CharacterEditor.dll is an optimized build: {e.Message}");
            }
        }

        private static bool PageOpen => MP.IsInMultiplayer && Find.WindowStack != null && Find.WindowStack.IsOpen(multifactionPageType);

        private static void PrePageDoWindowContents(Page_ConfigureStartingPawns __instance)
        {
            if (!MP.IsInMultiplayer || __instance.GetType() != multifactionPageType)
                return;
            // Row 193b: CE keeps its last list (e.g. wild animals from in-game use); on the page only the colonist list,
            // which in starting-screen mode is the page's own pawns. CE's editor reloads its list when it differs.
            bool first = !gamePlusSetByUs;
            if (first && listNameSetter != null)
                listNameSetter.Invoke(null, new object[] { colonistsLabel.GetValue(null) });
            gamePlusField() = true;
            gamePlusSetByUs = true;
            // Review 193b: an editor left open on a real pawn would edit it in starting-screen mode. Drop the selection;
            // CE's list reload then selects the first page pawn.
            if (first && selectedPawnSetter != null && !SelectedIsPageLocalOrNone())
                selectedPawnSetter.Invoke(apiGetter.Invoke(null, null), new object[] { null });
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
        private static void PreEnterEntryState(ref int __state) => TryEnterEntryState(ref __state);

        private static void TryEnterEntryState(ref int __state)
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

        // Row 193b: CE's own windows and CE float menu choices, while the page is open and CE's selected pawn is a page
        // pawn (or none), get the ProgramState.Entry the page's own DoWindowContents gets. Synced setters (work
        // priorities, policies, SyncFields watched in the UI) then run on this PC only, on a pawn that exists on this
        // PC only, instead of sending a command whose target no other PC can find. A real pawn selected: old path.
        private static void PreCeWindowContents(ref int __state)
        {
            __state = -1;
            if (gamePlusSetByUs && SelectedIsPageLocalOrNone())
                TryEnterEntryState(ref __state);
        }

        private static bool PreRefusedWindowContents(Window __instance)
        {
            if (!gamePlusSetByUs)
                return true;
            __instance.Close(false);
            RejectOnPage("this Character Editor window is not available on the multifaction Settle page (Multiplayer).");
            return false;
        }

        private static bool PreRefusedAction()
        {
            if (!gamePlusSetByUs)
                return true;
            RejectOnPage("this Character Editor action is not available on the multifaction Settle page (Multiplayer).");
            return false;
        }

        // At most one message a second: CE can hit a refused call several times in one click.
        private static long lastRejectTicks = -1000;

        private static void RejectOnPage(string text)
        {
            if (Environment.TickCount64 - lastRejectTicks < 1000)
                return;
            lastRejectTicks = Environment.TickCount64;
            Messages.Message("Character Editor: " + text, MessageTypeDefOf.RejectInput, false);
        }

        private static void PreCeFloatMenuChosen(FloatMenuOption __instance, ref int __state)
        {
            __state = -1;
            if (gamePlusSetByUs && __instance.action?.Method.DeclaringType?.Assembly == ceAssembly && SelectedIsPageLocalOrNone())
                TryEnterEntryState(ref __state);
        }

        private static bool SelectedIsPageLocalOrNone()
        {
            var api = apiGetter.Invoke(null, null);
            var pawn = api == null ? null : selectedPawnGetter.Invoke(api, null) as Pawn;
            return pawn == null || IsPageLocal(pawn);
        }

        private static bool IsPageLocal(Pawn p) =>
            p != null && !p.Spawned && Find.GameInitData?.startingAndOptionalPawns?.Contains(p) == true && Find.WorldPawns?.Contains(p) != true;

        private static bool PreChangeList(string listname)
        {
            if (!gamePlusSetByUs || string.IsNullOrEmpty(listname) || listname == (string)colonistsLabel.GetValue(null))
                return true;
            Messages.Message("Character Editor: on the multifaction Settle page only the colonist list (the page's pawns) is available (Multiplayer).",
                MessageTypeDefOf.RejectInput, false);
            return false;
        }

        // Row 193b: inside our Entry scope no relation is added. A link to a real pawn writes that pawn's relation lists
        // on this PC only; a link between two page pawns does not survive SendPawn (each pawn is sent alone, the other
        // one's local id resolves on no PC) - MP turns pawn relations off for page pawns for the same reason.
        private static bool PreAddDirectRelation()
        {
            if (entryDepth <= 0)
                return true;
            RejectOnPage("pawn relations cannot be set on the multifaction Settle page (Multiplayer).");
            return false;
        }

        private static bool PreTryRemoveDirectRelation(Pawn ___pawn, Pawn otherPawn, ref bool __result)
        {
            if (entryDepth <= 0 || (IsPageLocal(___pawn) && IsPageLocal(otherPawn)))
                return true;
            __result = false;
            return false;
        }

        // Row 193b: CE's Delete on a page pawn. CE's list part runs (editor list, then the page list). The game part is
        // skipped: Pawn.Destroy (WorldPawns, Anomaly, Master links, gear) and WorldPawns.RemoveAndDiscardPawnViaGC (drops
        // references to it game-wide) would run on this PC only. A page pawn is in no game list, so the editor and page
        // lists letting go of it is enough.
        private static void PreCeDelete(Pawn p, ref Pawn __state)
        {
            __state = deletingPagePawn;
            if (PageOpen && IsPageLocal(p))
                deletingPagePawn = p;
        }

        private static void EndCeDelete(Pawn __state) => deletingPagePawn = __state;

        private static bool PrePawnDestroy(Pawn __instance) => deletingPagePawn == null || __instance != deletingPagePawn;

        private static bool PreDiscardViaGC(Pawn p) => deletingPagePawn == null || p != deletingPagePawn;
    }
}
