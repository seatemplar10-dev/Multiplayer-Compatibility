using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>RimDark 40k - Mankind's Finest (Genes40k): custom chapter / primarch / custodes gene designs</summary>
    /// <remarks>
    /// FIXES row 26. Mankind's Finest 1.1.95+ (Workshop, 1.7 folder) lets a player design a chapter gene. The designs live in the saved game component
    /// <c>GameComponent_CustomChapterGenes</c> (list <c>chapters</c> + counter <c>nextId</c>); every design owns a GeneDef built at RUNTIME
    /// (<c>CustomChapterGeneDefBuilder.EnsureRegistered</c>: new GeneDef, <c>DefGenerator.AddImpliedDef</c>, <c>ShortHashGiver.GiveShortHash</c>).
    /// What desynced (all read from the decompile of the 1.7 Genes40k.dll, see the lane notes):
    /// 1. The def name was <c>prefix + Guid.NewGuid()</c> (<c>CustomChapterGeneDefBuilder.NewDefName</c>), a different name on every PC, so the same design
    ///    had two def names and two short hashes (short hash = StableStringHash(defName) with collision walking).
    /// 2. The windows wrote the saved state from UI code on one PC only: <c>Dialog_CreateChapterGene.Accept</c> (create / edit),
    ///    <c>Dialog_CustodesCustomization.SavePreset</c> / <c>SaveAsPreset</c> / <c>AcceptForGestator</c> (the last one writes the gene gestator building),
    ///    and the delete button of <c>Dialog_CustomChapterGeneList</c> (<c>GameComponent_CustomChapterGenes.Remove</c>, its only caller).
    /// 3. <c>Building_GeneGestator.Finish</c> (simulation, a tick) calls <c>GetOrCreateCustodesDesign</c> -> <c>Add</c> -> NewDefName: the Guid made a
    ///    different def name on every PC without any window being open.
    /// This module (Genes40k is reached by name only, no reference to the dll):
    /// - NewDefName: while in multiplayer the name is <c>prefix + "mp" + id</c> (id = the counter the component already advanced for this design,
    ///   so it is saved state and the same on every PC); never a Guid. Clashes are checked against the saved list only, never against DefDatabase
    ///   (DefDatabase differs between a host that ran the session and a joiner that loaded the save).
    /// - Every window action is replaced, in the UI only (MP.InInterface), by ONE synced static method that carries plain data (def references,
    ///   strings, ints; a related primarch gene travels as its def name, because it may itself be a runtime def) and then runs the original mutator on
    ///   every PC: SyncAdd (Add), SyncEdit (field writes + RefreshGeneDef), SyncSavePattern (GetOrCreateCustodesDesign + rename), SyncRemove (Remove),
    ///   SyncGestatorPattern (gestator.selectedDisciplines / selectedMaterial / selectedCustomChapterId). The validation messages stay local
    ///   (they run before the command is sent); the synced bodies re-check only saved state (name taken, design limit) so every PC reaches the same
    ///   decision.
    /// - Simulation callers (the gestator's Finish, vial use, load) are untouched: they already run on every PC from the same saved state, and now get
    ///   the same def name.
    /// Short hash: the GeneDef is added in the same order on every PC (synced commands in command order, load in saved-list order); the hash only
    /// depends on the def name and on the hashes already taken. Residual: Remove only drops the design from the list (the mod never unregisters the
    /// GeneDef), so on a PC that ran the session the dead def stays in DefDatabase and keeps its hash until the game restarts, while a later joiner never
    /// sees it; that matters only if a later runtime def hits the same 16 bit hash (1 in ~65,000 per dead def) or if sim code picks a random GeneDef
    /// without weights. Not fixed here (DefDatabase.Remove is private and renumbers every def).
    /// Not done here (listed in the row report): bills created by Dialog_SelectChapter / Dialog_SelectGeneseedVial / Dialog_CraftPrimarchEmbryo,
    /// the Building_GeneGestator gizmo lambdas, Core40k Dialog_GeneCustomization, Dialog_ChangeFlagColour.
    /// Inert when the loaded Genes40k has no <c>GameComponent_CustomChapterGenes</c> (the build the frozen co-op copy loads, Genes40k 1.1.14, does not):
    /// one message, nothing patched. When the type exists but any other member is missing: one error, nothing patched.
    /// Status: [unverified] compiled only, not run in game.
    /// </remarks>
    [MpCompatFor("phonicmas.rimdark.mankindsfinest")]
    internal class RimDark40kChapterGenes
    {
        private const string LogPrefix = "[MPCompat - RimDark 40k Chapter Genes] ";
        private const string TypePrefix = "Genes40k.";

        // flags of SyncEdit
        private const int EditTraits = 1, EditDisciplines = 2, EditRelated = 4, EditClearAdHoc = 8;

        // true only when every lookup, registration and patch worked; the prefixes do nothing while it is false
        private static bool active;
        private static int failures;

        // Genes40k types
        private static Type compType, geneType, levelType, templateType, flagType, traitType, disciplineType, builderType;
        private static Type createDialogType, custodesDialogType, gestatorType;

        // GameComponent_CustomChapterGenes
        private static PropertyInfo compInstance, compChapters;
        private static FieldInfo compNextId;
        private static MethodInfo compAdd, compGet, compRemove, compNameTaken, compDesignsOf, compGetOrCreate;

        // CustomChapterGene
        private static FieldInfo geneId, geneDefName, geneName, geneFlag, geneTraits, geneDisciplines, geneLocked, geneAdHoc;
        private static PropertyInfo geneRelated, geneKind;
        private static MethodInfo geneRefresh, geneSameDisciplines;

        // CustodesDisciplineLevel, CustomChapterGeneTemplateDef, CustomChapterGeneDefBuilder
        private static ConstructorInfo levelCtor;
        private static FieldInfo levelDef, levelLevel, templateKind, templateMax;
        private static MethodInfo builderPrefix, builderNewDefName;

        // Dialog_CreateChapterGene
        private static MethodInfo createAccept;
        private static FieldInfo createEditing, createTemplate, createName, createFlag, createRelated, createSelected;

        // Dialog_CustodesCustomization
        private static MethodInfo custodesSavePreset, custodesSaveAsPreset, custodesAcceptForGestator, custodesValidateLevels, custodesValidateName;
        private static MethodInfo custodesCurrentLevels;
        private static FieldInfo custodesGestator, custodesEditing, custodesTemplate, custodesName, custodesFlag;

        // Building_GeneGestator
        private static FieldInfo gestatorDisciplines, gestatorMaterial, gestatorChapterId;

        public RimDark40kChapterGenes(ModContentPack mod)
        {
            compType = AccessTools.TypeByName(TypePrefix + "GameComponent_CustomChapterGenes");
            if (compType == null)
            {
                Log.Message($"{LogPrefix}this Genes40k build has no custom chapter genes (no {TypePrefix}GameComponent_CustomChapterGenes): nothing to do.");
                return;
            }

            // 1. look up EVERYTHING before anything is registered or patched
            Try("look up the Genes40k members", FindMembers);
            if (failures > 0)
            {
                Log.Error($"{LogPrefix}this Genes40k build does not have what the module syncs ({failures} problem(s) above): nothing patched, custom chapter genes behave as without the module (desync risk).");
                return;
            }

            // 2. sync methods, then patches; any failure leaves the module inactive
            Try("register the sync methods", RegisterSyncMethods);
            if (failures == 0)
                Try("patch the windows and the def name", PatchAll);

            if (failures == 0)
            {
                active = true;
                Log.Message($"{LogPrefix}active: 5 sync methods, 6 patches.");
            }
            else
                Log.Error($"{LogPrefix}NOT active because a step failed ({failures}): every window action stays local as without the module.");
        }

        private static void Try(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                failures++;
                Log.Error($"{LogPrefix}{what} failed: {e}");
            }
        }

        #region lookups

        private static Type Need(string name)
        {
            var type = AccessTools.TypeByName(TypePrefix + name);
            if (type == null)
            {
                failures++;
                Log.Error($"{LogPrefix}type {TypePrefix}{name} not found");
            }

            return type;
        }

        private static T Need<T>(T member, Type owner, string name) where T : class
        {
            if (member == null)
            {
                failures++;
                Log.Error($"{LogPrefix}member {owner?.Name}.{name} not found");
            }

            return member;
        }

        private static FieldInfo Field(Type owner, string name) => owner == null ? null : Need(AccessTools.DeclaredField(owner, name), owner, name);
        private static PropertyInfo Prop(Type owner, string name) => owner == null ? null : Need(AccessTools.DeclaredProperty(owner, name), owner, name);

        private static MethodInfo Method(Type owner, string name, int parameterCount, Type returnType = null)
        {
            if (owner == null)
                return null;
            var found = owner.GetMethods(AccessTools.allDeclared).Where(m => m.Name == name && m.GetParameters().Length == parameterCount).ToArray();
            if (found.Length != 1 || (returnType != null && found[0].ReturnType != returnType))
            {
                failures++;
                Log.Error($"{LogPrefix}method {owner.Name}.{name}({parameterCount} parameters) not found exactly once (found {found.Length})");
                return null;
            }

            return found[0];
        }

        private static void FindMembers()
        {
            geneType = Need("CustomChapterGene");
            levelType = Need("CustodesDisciplineLevel");
            templateType = Need("CustomChapterGeneTemplateDef");
            flagType = Need("FlagIconDef");
            traitType = Need("ChapterTraitDef");
            disciplineType = Need("CustodesDisciplineDef");
            builderType = Need("CustomChapterGeneDefBuilder");
            createDialogType = Need("Dialog_CreateChapterGene");
            custodesDialogType = Need("Dialog_CustodesCustomization");
            gestatorType = Need("Building_GeneGestator");
            if (failures > 0)
                return;

            compInstance = Prop(compType, "Instance");
            compChapters = Prop(compType, "Chapters");
            compNextId = Field(compType, "nextId");
            compAdd = Method(compType, "Add", 7, geneType);
            compGet = Method(compType, "Get", 1, geneType);
            compRemove = Method(compType, "Remove", 1, typeof(bool));
            compNameTaken = Method(compType, "NameTaken", 2, typeof(bool));
            compDesignsOf = Method(compType, "DesignsOf", 2);
            compGetOrCreate = Method(compType, "GetOrCreateCustodesDesign", 1, geneType);

            geneId = Field(geneType, "id");
            geneDefName = Field(geneType, "defName");
            geneName = Field(geneType, "name");
            geneFlag = Field(geneType, "flagIconDef");
            geneTraits = Field(geneType, "traits");
            geneDisciplines = Field(geneType, "disciplines");
            geneLocked = Field(geneType, "locked");
            geneAdHoc = Field(geneType, "adHoc");
            geneRelated = Prop(geneType, "RelatedPrimarchGene");
            geneKind = Prop(geneType, "Kind");
            geneRefresh = Method(geneType, "RefreshGeneDef", 0);
            geneSameDisciplines = Method(geneType, "SameDisciplinesAs", 1, typeof(bool));

            levelCtor = levelType == null || disciplineType == null ? null : Need(AccessTools.DeclaredConstructor(levelType, new[] { disciplineType, typeof(int) }), levelType, ".ctor(def, int)");
            levelDef = Field(levelType, "def");
            levelLevel = Field(levelType, "level");
            templateKind = Field(templateType, "kind");
            templateMax = Field(templateType, "maxCustomChapters");
            builderPrefix = Method(builderType, "DefNamePrefix", 1, typeof(string));
            builderNewDefName = Method(builderType, "NewDefName", 1, typeof(string));

            createAccept = Method(createDialogType, "Accept", 0);
            createEditing = Field(createDialogType, "editing");
            createTemplate = Field(createDialogType, "template");
            createName = Field(createDialogType, "chapterName");
            createFlag = Field(createDialogType, "flagIconDef");
            createRelated = Field(createDialogType, "relatedPrimarchGene");
            createSelected = Field(createDialogType, "selected");

            custodesSavePreset = Method(custodesDialogType, "SavePreset", 0);
            custodesSaveAsPreset = Method(custodesDialogType, "SaveAsPreset", 0);
            custodesAcceptForGestator = Method(custodesDialogType, "AcceptForGestator", 0);
            custodesValidateLevels = Method(custodesDialogType, "ValidateLevels", 0, typeof(bool));
            custodesValidateName = Method(custodesDialogType, "ValidateName", 1, typeof(bool));
            custodesCurrentLevels = Need(AccessTools.DeclaredPropertyGetter(custodesDialogType, "CurrentLevels"), custodesDialogType, "CurrentLevels");
            custodesGestator = Field(custodesDialogType, "gestator");
            custodesEditing = Field(custodesDialogType, "editing");
            custodesTemplate = Field(custodesDialogType, "template");
            custodesName = Field(custodesDialogType, "presetName");
            custodesFlag = Field(custodesDialogType, "flagIconDef");

            gestatorDisciplines = Field(gestatorType, "selectedDisciplines");
            gestatorMaterial = Field(gestatorType, "selectedMaterial");
            gestatorChapterId = Field(gestatorType, "selectedCustomChapterId");
        }

        #endregion

        #region registration

        private static void RegisterSyncMethods()
        {
            MP.RegisterSyncMethod(typeof(RimDark40kChapterGenes), nameof(SyncAdd));
            MP.RegisterSyncMethod(typeof(RimDark40kChapterGenes), nameof(SyncEdit));
            MP.RegisterSyncMethod(typeof(RimDark40kChapterGenes), nameof(SyncSavePattern));
            MP.RegisterSyncMethod(typeof(RimDark40kChapterGenes), nameof(SyncRemove));
            MP.RegisterSyncMethod(typeof(RimDark40kChapterGenes), nameof(SyncGestatorPattern));
        }

        private static void PatchAll()
        {
            Patch(builderNewDefName, nameof(PreNewDefName));
            Patch(createAccept, nameof(PreCreateAccept));
            Patch(custodesSavePreset, nameof(PreCustodesSavePreset));
            Patch(custodesSaveAsPreset, nameof(PreCustodesSaveAsPreset));
            Patch(custodesAcceptForGestator, nameof(PreCustodesAcceptForGestator));
            Patch(compRemove, nameof(PreRemove));
        }

        private static void Patch(MethodBase original, string prefixName)
            => MpCompat.harmony.Patch(original, prefix: new HarmonyMethod(typeof(RimDark40kChapterGenes), prefixName));

        #endregion

        #region helpers

        private static bool InUi => active && MP.IsInMultiplayer && MP.InInterface;

        private static object GetComp() => Current.Game == null ? null : compInstance.GetValue(null);

        private static bool IsKind(Def template, string kind) => template != null && string.Equals(templateKind.GetValue(template)?.ToString(), kind, StringComparison.Ordinal);

        private static IList NewList(Type elementType) => (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType));

        private static IList MakeTraitList(List<Def> defs)
        {
            var list = NewList(traitType);
            if (defs != null)
                foreach (var def in defs)
                    if (def != null && traitType.IsInstanceOfType(def))
                        list.Add(def);
            return list;
        }

        private static IList MakeLevelList(List<Def> defs, List<int> levels)
        {
            var list = NewList(levelType);
            if (defs != null && levels != null)
                for (var i = 0; i < defs.Count && i < levels.Count; i++)
                    if (defs[i] != null && disciplineType.IsInstanceOfType(defs[i]))
                        list.Add(levelCtor.Invoke(new object[] { defs[i], levels[i] }));
            return list;
        }

        // CustodesDisciplineLevel list (Genes40k objects) -> two plain lists that travel
        private static void SplitLevels(IEnumerable levels, out List<Def> defs, out List<int> values)
        {
            defs = new List<Def>();
            values = new List<int>();
            if (levels == null)
                return;
            foreach (var entry in levels)
            {
                if (entry == null || levelDef.GetValue(entry) is not Def def)
                    continue;
                defs.Add(def);
                values.Add((int)levelLevel.GetValue(entry));
            }
        }

        private static List<Def> DefsOf(object list)
            => (list as IEnumerable)?.Cast<object>().OfType<Def>().ToList() ?? new List<Def>();

        private static string RelatedName(Def template, object relatedGene)
            => IsKind(template, "Chapter") ? (relatedGene as GeneDef)?.defName ?? "" : "";

        private static GeneDef FindGene(string defName) => defName.NullOrEmpty() ? null : DefDatabase<GeneDef>.GetNamedSilentFail(defName);

        private static string Describe(object chapter)
            => chapter == null ? "null" : $"id={geneId.GetValue(chapter)} defName={geneDefName.GetValue(chapter)}";

        // The chapter's GeneDef short hash is the line the lab compares between the PCs.
        private static string HashOf(object chapter)
        {
            var def = chapter == null ? null : DefDatabase<GeneDef>.GetNamedSilentFail((string)geneDefName.GetValue(chapter));
            return def == null ? "no def" : $"shortHash={def.shortHash} defsInDatabase={DefDatabase<GeneDef>.DefCount}";
        }

        private static void Rejected(string why)
        {
            Log.Warning($"{LogPrefix}command ignored on every PC: {why}");
            if (MP.IsExecutingSyncCommandIssuedBySelf)
                Messages.Message(why, MessageTypeDefOf.RejectInput, false);
        }

        #endregion

        #region deterministic def name

        // Replaces prefix + Guid.NewGuid(). In Add the object initializer takes id = nextId++ BEFORE it calls NewDefName, so the id of the design
        // being built is nextId - 1. (EnsureRegistered calls it only for a design without a name, which Add never produces.)
        private static bool PreNewDefName(object __0, ref string __result)
        {
            if (!active || !MP.IsInMultiplayer)
                return true;
            var comp = GetComp();
            if (comp == null)
                return true;

            var prefix = (string)builderPrefix.Invoke(null, new[] { __0 });
            var id = Math.Max(0, (int)compNextId.GetValue(comp) - 1);
            var taken = new HashSet<string>();
            if (compChapters.GetValue(comp) is IEnumerable chapters)
                foreach (var chapter in chapters)
                    if (chapter != null && geneDefName.GetValue(chapter) is string existing)
                        taken.Add(existing);

            var name = prefix + "mp" + id.ToString("D4");
            for (var bump = 1; taken.Contains(name); bump++)
                name = prefix + "mp" + id.ToString("D4") + "_" + bump;

            __result = name;
            return false;
        }

        #endregion

        #region synced commands (the bodies run on every PC, from the command)

        private static void SyncAdd(Def template, string name, Def flag, List<Def> traits, List<Def> disciplineDefs, List<int> levels, string relatedGene, bool adHoc)
        {
            var comp = GetComp();
            if (comp == null || template == null || !templateType.IsInstanceOfType(template))
            {
                Rejected("custom chapter gene: no game component or unknown template");
                return;
            }

            name = name?.Trim();
            if (name.NullOrEmpty())
            {
                Rejected("custom chapter gene: the name is empty");
                return;
            }

            if (!adHoc && (bool)compNameTaken.Invoke(comp, new object[] { name, null }))
            {
                Rejected($"custom chapter gene \"{name}\" was not saved: that name is already taken");
                return;
            }

            var count = ((IList)compDesignsOf.Invoke(comp, new[] { templateKind.GetValue(template), true })).Count;
            if (count >= (int)templateMax.GetValue(template))
            {
                Rejected($"custom chapter gene \"{name}\" was not saved: the design limit is reached");
                return;
            }

            var created = compAdd.Invoke(comp, new object[]
            {
                template, name, flag != null && flagType.IsInstanceOfType(flag) ? flag : null,
                MakeTraitList(traits), MakeLevelList(disciplineDefs, levels), FindGene(relatedGene), adHoc
            });
            Log.Message($"{LogPrefix}Add {Describe(created)} name=\"{name}\" {HashOf(created)} designs={((IList)compChapters.GetValue(comp)).Count}");
        }

        private static void SyncEdit(int id, string name, Def flag, List<Def> traits, List<Def> disciplineDefs, List<int> levels, string relatedGene, int flags)
        {
            var comp = GetComp();
            var chapter = comp == null ? null : compGet.Invoke(comp, new object[] { id });
            if (chapter == null)
            {
                Rejected($"custom chapter gene: design {id} no longer exists");
                return;
            }

            var locked = (bool)geneLocked.GetValue(chapter);
            if ((flags & EditTraits) != 0 && locked)
            {
                Rejected("custom chapter gene: the design is locked (a gene-seed made from it exists)");
                return;
            }

            name = name?.Trim();
            if (name.NullOrEmpty() || (bool)compNameTaken.Invoke(comp, new[] { name, chapter }))
            {
                Rejected($"custom chapter gene \"{name}\" was not saved: the name is empty or already taken");
                return;
            }

            geneName.SetValue(chapter, name);
            if (flag != null && flagType.IsInstanceOfType(flag))
                geneFlag.SetValue(chapter, flag);
            if ((flags & EditTraits) != 0)
                geneTraits.SetValue(chapter, MakeTraitList(traits));
            if ((flags & EditDisciplines) != 0 && !locked)
                geneDisciplines.SetValue(chapter, MakeLevelList(disciplineDefs, levels));
            if ((flags & EditRelated) != 0)
                geneRelated.SetValue(chapter, FindGene(relatedGene));
            if ((flags & EditClearAdHoc) != 0)
                geneAdHoc.SetValue(chapter, false);
            geneRefresh.Invoke(chapter, null);
            Log.Message($"{LogPrefix}Edit {Describe(chapter)} name=\"{name}\" flags={flags} {HashOf(chapter)}");
        }

        private static void SyncSavePattern(string name, Def flag, List<Def> disciplineDefs, List<int> levels)
        {
            var comp = GetComp();
            if (comp == null)
                return;

            name = name?.Trim();
            var design = compGetOrCreate.Invoke(comp, new object[] { MakeLevelList(disciplineDefs, levels) });
            if (design == null || name.NullOrEmpty() || (bool)compNameTaken.Invoke(comp, new[] { name, design }))
            {
                Rejected($"custom custodes preset \"{name}\" was not saved: the name is empty or already taken");
                return;
            }

            geneName.SetValue(design, name);
            if (flag != null && flagType.IsInstanceOfType(flag))
                geneFlag.SetValue(design, flag);
            geneAdHoc.SetValue(design, false);
            geneRefresh.Invoke(design, null);
            Log.Message($"{LogPrefix}SavePattern {Describe(design)} name=\"{name}\" {HashOf(design)}");
        }

        private static void SyncRemove(int id)
        {
            var comp = GetComp();
            var chapter = comp == null ? null : compGet.Invoke(comp, new object[] { id });
            if (chapter == null)
                return;

            var described = Describe(chapter);
            var removed = (bool)compRemove.Invoke(comp, new[] { chapter });
            Log.Message($"{LogPrefix}Remove {described} removed={removed} designs={((IList)compChapters.GetValue(comp)).Count}");
        }

        private static void SyncGestatorPattern(Thing gestator, List<Def> disciplineDefs, List<int> levels)
        {
            if (gestator == null || !gestatorType.IsInstanceOfType(gestator))
                return;

            gestatorDisciplines.SetValue(gestator, MakeLevelList(disciplineDefs, levels));
            gestatorMaterial.SetValue(gestator, null);
            gestatorChapterId.SetValue(gestator, -1);
        }

        #endregion

        #region UI prefixes (run before the window mutates anything)

        // Dialog_CreateChapterGene.Accept: create, or edit the design being edited.
        private static bool PreCreateAccept(object __instance)
        {
            if (!InUi)
                return true;

            var editing = createEditing.GetValue(__instance);
            if (editing != null && (bool)geneLocked.GetValue(editing))
                return true; // the original only shows "locked" and closes, it changes nothing

            var template = (Def)createTemplate.GetValue(__instance);
            var text = ((string)createName.GetValue(__instance) ?? "").Trim();
            var flag = createFlag.GetValue(__instance) as Def;
            var traits = DefsOf(createSelected.GetValue(__instance));
            var related = RelatedName(template, createRelated.GetValue(__instance));

            if (editing != null)
                SyncEdit((int)geneId.GetValue(editing), text, flag, traits, new List<Def>(), new List<int>(), related, EditTraits | EditRelated);
            else
                SyncAdd(template, text, flag, traits, new List<Def>(), new List<int>(), related, false);

            Messages.Message((IsKind(template, "Primarch") ? "BEWH.MankindsFinest.CustomPrimarch.Saved" : "BEWH.MankindsFinest.CustomChapter.Saved").Translate(text),
                MessageTypeDefOf.TaskCompletion, false);
            ((Window)__instance).Close();
            return false;
        }

        // Dialog_CustodesCustomization.SavePreset: create a preset or edit the one being edited.
        private static bool PreCustodesSavePreset(object __instance)
        {
            if (!InUi)
                return true;
            var comp = GetComp();
            if (comp == null)
                return true;

            var editing = custodesEditing.GetValue(__instance);
            if (!(bool)custodesValidateName.Invoke(__instance, new[] { editing }))
                return false;

            var template = (Def)custodesTemplate.GetValue(__instance);
            var text = ((string)custodesName.GetValue(__instance) ?? "").Trim();
            var flag = custodesFlag.GetValue(__instance) as Def;
            var defs = new List<Def>();
            var values = new List<int>();

            if (editing != null)
            {
                var flags = EditClearAdHoc;
                if (!(bool)geneLocked.GetValue(editing))
                {
                    if (!(bool)custodesValidateLevels.Invoke(__instance, null))
                        return false;
                    SplitLevels((IEnumerable)custodesCurrentLevels.Invoke(__instance, null), out defs, out values);
                    flags |= EditDisciplines;
                }

                SyncEdit((int)geneId.GetValue(editing), text, flag, new List<Def>(), defs, values, "", flags);
            }
            else
            {
                if (!(bool)custodesValidateLevels.Invoke(__instance, null))
                    return false;
                var max = (int)templateMax.GetValue(template);
                if (((IList)compDesignsOf.Invoke(comp, new[] { templateKind.GetValue(template), true })).Count >= max)
                {
                    Messages.Message("BEWH.MankindsFinest.CustomChapter.TooManyChapters".Translate(max), MessageTypeDefOf.RejectInput, false);
                    return false;
                }

                SplitLevels((IEnumerable)custodesCurrentLevels.Invoke(__instance, null), out defs, out values);
                SyncAdd(template, text, flag, new List<Def>(), defs, values, "", false);
            }

            Messages.Message("BEWH.MankindsFinest.CustomCustodes.PresetSaved".Translate(text), MessageTypeDefOf.TaskCompletion, false);
            ((Window)__instance).Close();
            return false;
        }

        // Dialog_CustodesCustomization.SaveAsPreset (gestator window): find or create the design of the current levels, then name it.
        private static bool PreCustodesSaveAsPreset(object __instance)
        {
            if (!InUi)
                return true;
            var comp = GetComp();
            if (comp == null)
                return true;
            if (!(bool)custodesValidateLevels.Invoke(__instance, null))
                return false;

            var levels = custodesCurrentLevels.Invoke(__instance, null);
            object existing = null;
            if (compChapters.GetValue(comp) is IEnumerable chapters)
                foreach (var chapter in chapters)
                    if (chapter != null && geneKind.GetValue(chapter)?.ToString() == "Custodes" && (bool)geneSameDisciplines.Invoke(chapter, new[] { levels }))
                    {
                        existing = chapter;
                        break;
                    }

            if (!(bool)custodesValidateName.Invoke(__instance, new[] { existing }))
                return false;

            var text = ((string)custodesName.GetValue(__instance) ?? "").Trim();
            SplitLevels((IEnumerable)levels, out var defs, out var values);
            SyncSavePattern(text, custodesFlag.GetValue(__instance) as Def, defs, values);
            Messages.Message("BEWH.MankindsFinest.CustomCustodes.PresetSaved".Translate(text), MessageTypeDefOf.TaskCompletion, false);
            return false;
        }

        // Dialog_CustodesCustomization.AcceptForGestator: writes three saved fields of the gene gestator building.
        private static bool PreCustodesAcceptForGestator(object __instance)
        {
            if (!InUi || custodesGestator.GetValue(__instance) is not Thing gestator)
                return true;
            if (!(bool)custodesValidateLevels.Invoke(__instance, null))
                return false;

            SplitLevels((IEnumerable)custodesCurrentLevels.Invoke(__instance, null), out var defs, out var values);
            SyncGestatorPattern(gestator, defs, values);
            ((Window)__instance).Close();
            return false;
        }

        // GameComponent_CustomChapterGenes.Remove: its only caller is the delete confirmation of Dialog_CustomChapterGeneList.
        private static bool PreRemove(object __0, ref bool __result)
        {
            if (!InUi || __0 == null || (bool)geneLocked.GetValue(__0))
                return true; // not from the window, or locked (the original then returns false)

            SyncRemove((int)geneId.GetValue(__0));
            __result = true;
            return false;
        }

        #endregion
    }
}
