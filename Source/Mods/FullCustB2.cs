using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>FULL1127 class B wave B2 (cu12): UI writes with no MP sync, one class per mod.</summary>
    internal static class FullCustB2
    {
        public const string LogB2 = "[MPCompat - FULL1127 B2] ";

        public static void Patch(MethodBase m, string pre, string post, Type owner, string what)
        {
            if (m == null) { Log.Error(LogB2 + what + ": target method not found"); return; }
            MpCompat.harmony.Patch(m,
                prefix: pre == null ? null : new HarmonyMethod(owner, pre),
                postfix: post == null ? null : new HarmonyMethod(owner, post));
        }

        public static void WatchEnd()
        {
            if (MP.IsInMultiplayer) MP.WatchEnd();
        }

        /// <summary>First field of a (nested) closure object whose value is of type T (searches nested closures).</summary>
        public static T FindClosureValue<T>(object closure, int depth = 0) where T : class
        {
            if (closure == null || depth > 3) return null;
            foreach (var f in closure.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var v = f.GetValue(closure);
                if (v is T t) return t;
            }
            foreach (var f in closure.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (f.FieldType.Name.Contains("DisplayClass"))
                {
                    var r = FindClosureValue<T>(f.GetValue(closure), depth + 1);
                    if (r != null) return r;
                }
            }
            return null;
        }
    }

    /// <summary>UI Rethemed Colonist Card: CareButtons replaces the vanilla care widget, so the MP watch around the vanilla UI is skipped.
    /// selfTend toggle = watched field; medCare float menu lambdas = replaced by a synced setter.</summary>
    [MpCompatFor("lazyfridaystudio.uirethemed.colonistcard")]
    class FullCuColonistCard
    {
        static ISyncField selfTendField;

        public FullCuColonistCard(ModContentPack mod)
        {
            var t = AccessTools.TypeByName("UIRethemed.ColonistCard.CareButtons");
            if (t == null) { Log.Error(FullCustB2.LogB2 + "colonistcard: CareButtons not found"); return; }
            selfTendField = MP.RegisterSyncField(typeof(Pawn_PlayerSettings), "selfTend");
            FullCustB2.Patch(AccessTools.Method(t, "SelfTend"), nameof(PreSelfTend), nameof(Post), typeof(FullCuColonistCard), "colonistcard SelfTend");
            FullCustB2.Patch(AccessTools.Method(t, "MedicineOptions"), null, nameof(PostMedicineOptions), typeof(FullCuColonistCard), "colonistcard MedicineOptions");
            MP.RegisterSyncMethod(typeof(FullCuColonistCard), nameof(SetMedCare));
            Log.Message(FullCustB2.LogB2 + "UI Rethemed Colonist Card: selfTend watch + medCare synced setter registered");
        }

        static void PreSelfTend(Pawn pawn)
        {
            if (!MP.IsInMultiplayer || pawn?.playerSettings == null) return;
            MP.WatchBegin();
            selfTendField.Watch(pawn.playerSettings);
        }

        static void Post() => FullCustB2.WatchEnd();

        static void PostMedicineOptions(List<FloatMenuOption> __result, Pawn pawn)
        {
            if (!MP.IsInMultiplayer || __result == null) return;
            for (int i = 0; i < 5 && i < __result.Count; i++)
            {
                var care = (MedicalCareCategory)i;
                var p = pawn;
                Action act = () => SetMedCare(p, care);
                var prop = AccessTools.Property(typeof(FloatMenuOption), "Action");
                if (prop != null && prop.CanWrite) prop.SetValue(__result[i], act);
                else AccessTools.Field(typeof(FloatMenuOption), "action")?.SetValue(__result[i], act);
            }
        }

        static void SetMedCare(Pawn pawn, MedicalCareCategory care)
        {
            if (pawn?.playerSettings != null) pawn.playerSettings.medCare = care;
        }
    }

    /// <summary>UI Rethemed Core: ManageAreasWindow.Copy (private instance of a Window) is replaced by a synced static taking the map and the source area.</summary>
    [MpCompatFor("lazyfridaystudio.uirethemed.core")]
    class FullCuCoreAreaCopy
    {
        static FieldInfo mapField;

        public FullCuCoreAreaCopy(ModContentPack mod)
        {
            var t = AccessTools.TypeByName("UIRethemed.Core.ManageAreasWindow");
            var m = t == null ? null : AccessTools.Method(t, "Copy");
            if (m == null) { Log.Error(FullCustB2.LogB2 + "core: ManageAreasWindow.Copy not found"); return; }
            mapField = AccessTools.Field(t, "map");
            FullCustB2.Patch(m, nameof(PreCopy), null, typeof(FullCuCoreAreaCopy), "core Copy");
            MP.RegisterSyncMethod(typeof(FullCuCoreAreaCopy), nameof(SyncCopy));
            Log.Message(FullCustB2.LogB2 + "UI Rethemed Core: ManageAreasWindow.Copy -> synced SyncCopy registered");
        }

        static bool PreCopy(object __instance, Area area)
        {
            if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand || area == null) return true;
            var map = mapField?.GetValue(__instance) as Map;
            if (map == null) return true;
            SyncCopy(map, area);
            return false;
        }

        static void SyncCopy(Map map, Area area)
        {
            if (map.areaManager.TryMakeNewAllowed(out Area_Allowed val))
            {
                foreach (var c in area.ActiveCells) val[c] = true;
            }
            else
                Messages.Message("MaxAreasReached".Translate(10), MessageTypeDefOf.RejectInput, true);
        }
    }

    /// <summary>UI Rethemed Quests: RewardPrefs.Row and HeaderToggle write Faction.allowGoodwillRewards / allowRoyalFavorRewards outside the vanilla watch.</summary>
    [MpCompatFor("lazyfridaystudio.uirethemed.quests")]
    class FullCuQuestsRewardPrefs
    {
        static ISyncField goodwillField, favorField;
        static FieldInfo factionsField;

        public FullCuQuestsRewardPrefs(ModContentPack mod)
        {
            var t = AccessTools.TypeByName("UIRethemed.Quests.RewardPrefs");
            if (t == null) { Log.Error(FullCustB2.LogB2 + "quests: RewardPrefs not found"); return; }
            goodwillField = MP.RegisterSyncField(typeof(Faction), "allowGoodwillRewards");
            favorField = MP.RegisterSyncField(typeof(Faction), "allowRoyalFavorRewards");
            factionsField = AccessTools.Field(t, "factions");
            FullCustB2.Patch(AccessTools.Method(t, "Row"), nameof(PreRow), nameof(Post), typeof(FullCuQuestsRewardPrefs), "quests Row");
            FullCustB2.Patch(AccessTools.Method(t, "HeaderToggle"), nameof(PreHeader), nameof(Post), typeof(FullCuQuestsRewardPrefs), "quests HeaderToggle");
            Log.Message(FullCustB2.LogB2 + "UI Rethemed Quests: RewardPrefs Row/HeaderToggle faction reward fields watched");
        }

        static void PreRow(Faction faction)
        {
            if (!MP.IsInMultiplayer || faction == null) return;
            MP.WatchBegin();
            goodwillField.Watch(faction);
            favorField.Watch(faction);
        }

        static void PreHeader()
        {
            if (!MP.IsInMultiplayer) return;
            MP.WatchBegin();
            if (factionsField?.GetValue(null) is List<Faction> list)
                foreach (var f in list) { goodwillField.Watch(f); favorField.Watch(f); }
        }

        static void Post() => FullCustB2.WatchEnd();
    }

    /// <summary>UI Rethemed Work: WorkTab.ToggleManual flips PlaySettings.useWorkPriorities on one PC.</summary>
    [MpCompatFor("lazyfridaystudio.uirethemed.work")]
    class FullCuWorkToggleManual
    {
        public FullCuWorkToggleManual(ModContentPack mod)
        {
            var t = AccessTools.TypeByName("UIRethemed.Work.WorkTab");
            var m = t == null ? null : AccessTools.Method(t, "ToggleManual");
            if (m == null) { Log.Error(FullCustB2.LogB2 + "work: WorkTab.ToggleManual not found"); return; }
            MP.RegisterSyncMethod(m);
            Log.Message(FullCustB2.LogB2 + "UI Rethemed Work: WorkTab.ToggleManual synced registered");
        }
    }

    /// <summary>MechTab: DoManualPrioritiesCheckbox writes PlaySettings.useWorkPriorities through a ref checkbox.</summary>
    [MpCompatFor("spacemoth.mechtab")]
    class FullCuMechTabManual
    {
        static ISyncField useField;

        public FullCuMechTabManual(ModContentPack mod)
        {
            var t = AccessTools.TypeByName("SM_MechTab.SM_MainTabWindow_MechsWork");
            var m = t == null ? null : AccessTools.Method(t, "DoManualPrioritiesCheckbox");
            if (m == null) { Log.Error(FullCustB2.LogB2 + "mechtab: DoManualPrioritiesCheckbox not found"); return; }
            // cu22: MP itself syncs PlaySettings.useWorkPriorities (Multiplayer.Client.SyncFields.SyncUseWorkPriorities, path
            // Verse.Current/Game/playSettings). A path-less RegisterSyncField(typeof(PlaySettings), ...) failed validation
            // (non-serializable type RimWorld.PlaySettings) and threw on Watch from the UI, so reuse MP's field.
            useField = AccessTools.Field(AccessTools.TypeByName("Multiplayer.Client.SyncFields"), "SyncUseWorkPriorities")?.GetValue(null) as ISyncField;
            if (useField == null) { Log.Error(FullCustB2.LogB2 + "mechtab: MP SyncUseWorkPriorities not found"); return; }
            FullCustB2.Patch(m, nameof(Pre), nameof(Post), typeof(FullCuMechTabManual), "mechtab");
            Log.Message(FullCustB2.LogB2 + "MechTab: useWorkPriorities watch registered");
        }

        static void Pre()
        {
            if (!MP.IsInMultiplayer || Current.Game?.playSettings == null) return;
            MP.WatchBegin();
            useField.Watch();
        }

        static void Post() => FullCustB2.WatchEnd();
    }

    /// <summary>Hospitality: GuestUtility.Adopt is reached unsynced from ChoiceLetter_GuestJoinRequest.Option_Accept (the Recruit path is already synced by the mod).</summary>
    [MpCompatFor("orion.hospitality")]
    class FullCuHospitalityAdopt
    {
        public FullCuHospitalityAdopt(ModContentPack mod)
        {
            var t = AccessTools.TypeByName("Hospitality.Utilities.GuestUtility");
            var m = t == null ? null : AccessTools.Method(t, "Adopt");
            if (m == null) { Log.Error(FullCustB2.LogB2 + "hospitality: GuestUtility.Adopt not found"); return; }
            MP.RegisterSyncMethod(m);
            Log.Message(FullCustB2.LogB2 + "Hospitality: GuestUtility.Adopt synced registered");
        }
    }

    /// <summary>OmniTab: CraftBillService.AddBill builds a bill, edits its filter/targetCount/quality and adds it to a table on one PC. Replaced by a synced static
    /// (recipe option rebuilt on every PC from RecipeDef + Precept).</summary>
    [MpCompatFor("scheigster.omnitab")]
    class FullCuOmniTabAddBill
    {
        static MethodInfo addBill;
        static ConstructorInfo optionCtor;
        static MethodInfo instanceGetter;
        static FieldInfo recipeF, preceptF;

        public FullCuOmniTabAddBill(ModContentPack mod)
        {
            var t = AccessTools.TypeByName("OmniTab.Services.Crafting.CraftBillService");
            var o = AccessTools.TypeByName("OmniTab.Services.Crafting.CraftRecipeOption");
            addBill = t == null ? null : AccessTools.Method(t, "AddBill");
            if (addBill == null || o == null) { Log.Error(FullCustB2.LogB2 + "omnitab: CraftBillService.AddBill / CraftRecipeOption not found"); return; }
            optionCtor = AccessTools.Constructor(o, new[] { typeof(RecipeDef), typeof(Precept_ThingStyle) });
            instanceGetter = AccessTools.PropertyGetter(t, "Instance");
            recipeF = AccessTools.Field(o, "Recipe");
            preceptF = AccessTools.Field(o, "Precept");
            FullCustB2.Patch(addBill, nameof(PreAdd), null, typeof(FullCuOmniTabAddBill), "omnitab");
            MP.RegisterSyncMethod(typeof(FullCuOmniTabAddBill), nameof(SyncAdd));
            Log.Message(FullCustB2.LogB2 + "OmniTab: CraftBillService.AddBill -> synced SyncAdd registered");
        }

        static bool PreAdd(ThingDef product, object recipeOption, ThingDef effectiveMaterial, QualityCategory selectedQuality, Building_WorkTable table)
        {
            if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand || recipeOption == null || table == null) return true;
            var recipe = recipeF.GetValue(recipeOption) as RecipeDef;
            if (recipe == null) return true;
            SyncAdd(product, recipe, preceptF.GetValue(recipeOption) as Precept, effectiveMaterial, selectedQuality, table);
            return false;
        }

        static void SyncAdd(ThingDef product, RecipeDef recipe, Precept precept, ThingDef effectiveMaterial, QualityCategory selectedQuality, Building_WorkTable table)
        {
            var option = optionCtor.Invoke(new object[] { recipe, precept as Precept_ThingStyle });
            addBill.Invoke(instanceGetter.Invoke(null, null), new object[] { product, option, effectiveMaterial, selectedQuality, table });
        }
    }

    /// <summary>Stockpile It: the two float menu lambdas call settings.filter.SetAllow on a live storage filter with no DrawnThingFilter (MP prefix lets it run locally).
    /// Each lambda is replaced by a synced static taking the StorageSettings and the defs.</summary>
    [MpCompatFor("sk.stockpileit")]
    class FullCuStockpileIt
    {
        public FullCuStockpileIt(ModContentPack mod)
        {
            var t = AccessTools.TypeByName("Stockpile_It_.StorageMenuUtility");
            if (t == null) { Log.Error(FullCustB2.LogB2 + "stockpileit: StorageMenuUtility not found"); return; }
            Reg(t, "<>c__DisplayClass0_1", "<CreateAddToStorageOption>b__0", nameof(PreSingle));
            Reg(t, "<>c__DisplayClass1_0", "<CreateAddSelectedItemsToStorageOption>b__2", nameof(PreMany));
            MP.RegisterSyncMethod(typeof(FullCuStockpileIt), nameof(SyncAllow));
            Log.Message(FullCustB2.LogB2 + "Stockpile It: add-to-storage float menu lambdas -> synced SyncAllow registered");
        }

        static void Reg(Type t, string cls, string method, string pre)
        {
            var nested = t.GetNestedType(cls, BindingFlags.NonPublic | BindingFlags.Public);
            var m = nested == null ? null : AccessTools.Method(nested, method);
            FullCustB2.Patch(m, pre, null, typeof(FullCuStockpileIt), "stockpileit " + method);
        }

        static bool PreSingle(object __instance)
        {
            if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand) return true;
            var settings = FullCustB2.FindClosureValue<StorageSettings>(__instance);
            var def = FullCustB2.FindClosureValue<ThingDef>(__instance);
            if (settings == null || def == null) return true;
            SyncAllow(settings, new List<ThingDef> { def });
            return false;
        }

        static bool PreMany(object __instance)
        {
            if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand) return true;
            var settings = FullCustB2.FindClosureValue<StorageSettings>(__instance);
            var defs = FullCustB2.FindClosureValue<List<ThingDef>>(__instance);
            if (settings == null || defs == null) return true;
            // the lambda re-checks parent settings and Allows per def at click time; keep that on the clicking side, then sync the result
            var parent = FindParent(__instance, settings);
            var todo = defs.Where(d => (parent == null || parent.AllowedToAccept(d)) && !settings.filter.Allows(d)).ToList();
            if (todo.Count > 0) SyncAllow(settings, todo);
            return false;
        }

        static StorageSettings FindParent(object closure, StorageSettings settings)
        {
            foreach (var f in closure.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                if (f.FieldType == typeof(StorageSettings) && f.GetValue(closure) is StorageSettings s && s != settings) return s;
            return null;
        }

        static void SyncAllow(StorageSettings settings, List<ThingDef> defs)
        {
            foreach (var d in defs) settings.filter.SetAllow(d, true);
        }
    }

    /// <summary>Apparel Policy Builder: the Apply button runs Ruleset.ApplyTo(policy), many SetAllow calls on the live policy filter on one PC. The pass runs locally on a copy of the
    /// filter (nothing live is touched), then the resulting allowed defs and disallowed special filters are applied through one synced method.
    /// The saved Ruleset itself (GameComponent_ApparelPolicyBuilder.Store) stays per PC: it is not read by the simulation.</summary>
    [MpCompatFor("skyl3lazer.apparelpolicybuilder")]
    class FullCuApparelPolicyBuilder
    {
        static bool computing;
        static MethodInfo applyTo;
        static FieldInfo disallowedSpecial, allowedDefsField;

        public FullCuApparelPolicyBuilder(ModContentPack mod)
        {
            var t = AccessTools.TypeByName("ApparelPolicyBuilder.Ruleset");
            applyTo = t == null ? null : AccessTools.Method(t, "ApplyTo");
            if (applyTo == null) { Log.Error(FullCustB2.LogB2 + "apparelpolicybuilder: Ruleset.ApplyTo not found"); return; }
            disallowedSpecial = AccessTools.Field(typeof(ThingFilter), "disallowedSpecialFilters");
            allowedDefsField = AccessTools.Field(typeof(ThingFilter), "allowedDefs");
            FullCustB2.Patch(applyTo, nameof(PreApply), null, typeof(FullCuApparelPolicyBuilder), "apparelpolicybuilder");
            MP.RegisterSyncMethod(typeof(FullCuApparelPolicyBuilder), nameof(SyncFilter));
            Log.Message(FullCustB2.LogB2 + "Apparel Policy Builder: Ruleset.ApplyTo -> synced SyncFilter registered");
        }

        static bool PreApply(object __instance, ApparelPolicy policy)
        {
            if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand || computing || policy?.filter == null) return true;
            var temp = new ThingFilter();
            temp.CopyAllowancesFrom(policy.filter);
            var tempPolicy = (ApparelPolicy)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(ApparelPolicy));
            tempPolicy.filter = temp;
            computing = true;
            try { applyTo.Invoke(__instance, new object[] { tempPolicy }); }
            finally { computing = false; }
            var allowed = (allowedDefsField.GetValue(temp) as IEnumerable<ThingDef>)?.ToList() ?? new List<ThingDef>();
            var disallowed = (disallowedSpecial.GetValue(temp) as IEnumerable<SpecialThingFilterDef>)?.ToList() ?? new List<SpecialThingFilterDef>();
            SyncFilter(policy, allowed, disallowed);
            return false;
        }

        static void SyncFilter(ApparelPolicy policy, List<ThingDef> allowed, List<SpecialThingFilterDef> disallowed)
        {
            var filter = policy.filter;
            filter.SetDisallowAll();
            foreach (var d in allowed) filter.SetAllow(d, true);
            var list = disallowedSpecial.GetValue(filter) as List<SpecialThingFilterDef>;
            if (list != null) { list.Clear(); list.AddRange(disallowed); }
        }
    }
}
