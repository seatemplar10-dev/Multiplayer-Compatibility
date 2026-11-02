using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Smart Medicine by Uuugggg, Compact Hediffs by PeteTimesSix</summary>
    /// SmartMedicine:
    /// <see href="https://github.com/alextd/Rimworld-SmartMedicine"/>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=1309994319"/>
    /// Smart Medicine - Continued (Memegoddess, package id Memegoddess.SmartMedicine, the build the 1.7 co-op runs):
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3526251680"/>
    /// CompactHediffs:
    /// <see href="https://github.com/PeteTimesSix/CompactHediffs"/>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=2031734067"/>
    ///
    /// FIXES (t20, 2 Nov): this module only listed the original package id (Uuugggg.SmartMedicine), so it never loaded for the
    /// Continued build (Memegoddess.SmartMedicine) and none of Smart Medicine's player actions were synced. Both ids are listed now.
    /// The Continued build also moved the per-hediff care menu: HediffRowPriorityCare.LabelButton is gone, the menu is built by
    /// CreateCareMenuOptionsWithList (all Smart Medicine and Nice Health Tab menus go through it), and it added the Unlimited Surgery setting.
    /// Player actions synced here:
    /// - Stock up (gear tab and its dialog): set a count (also the +/- buttons), stop stocking one item, clear all, paste copied settings.
    /// - Per-hediff care menu (health tab, right click on a tendable hediff): "patient bed rest" toggle, "default care", and each care level.
    /// - Unlimited Surgery: the checkbox per pawn (Assign tab column, health tab overview) and the default checkbox in the medical defaults dialog.
    /// - The temporary tending spot's Cancel gizmo.
    /// Not synced on purpose: copying stock up settings (the copied pawn is per-PC interface state, it is sent along on paste),
    /// the "Stock ANYTHING" toggle (interface only) and Smart Medicine's own mod settings window (per-PC file, MP compares configs when joining).
    [MpCompatFor("Memegoddess.SmartMedicine")]
    [MpCompatFor("Uuugggg.SmartMedicine")]
    public class SmartMedicine
    {
        private delegate void StockUpPasteSettingsDelegate(Pawn pawn);

        private delegate Dictionary<ThingDef, int> StockUpSettingsDelegate(Pawn pawn);

        private static Type smartMedicineCompType;
        private static StockUpSettingsDelegate stockUpSettingsMethod;
        private static StockUpPasteSettingsDelegate stockUpPasteMethod;
        private static AccessTools.FieldRef<object, Pawn> copiedPawnField;

        // Per-hediff care menu: HediffRowPriorityCare.CreateCareMenuOptionsWithList lambdas and the game component data they change
        private static Func<Dictionary<Hediff, MedicalCareCategory>> getHediffCare;
        private static Func<HashSet<Hediff>> getIgnoredHediffs;
        private static FieldInfo bedRestPrimaryField;
        private static FieldInfo bedRestAffectedField;
        private static FieldInfo defaultCarePrimaryField;
        private static FieldInfo careLevelField;
        private static FieldInfo careOuterField;
        private static FieldInfo careAffectedField;

        // Unlimited Surgery
        private static Type surgeryUnlimitedCompType;
        private static AccessTools.FieldRef<object, HashSet<Pawn>> surgeryUnlimitedSetField;
        private static AccessTools.FieldRef<object, bool> surgeryUnlimitedDefaultField;
        private static FastInvokeHandler surgeryUnlimitedSetMethod;

        // CompatcHediffs compat
        private static FastInvokeHandler getSmartMedicinePriorityCareDictionary;
        private static AccessTools.FieldRef<object, object> delegateTypeHediffCareField;

        public SmartMedicine(ModContentPack mod)
        {
            // One section failing (a renamed method in a new Smart Medicine build) must not stop the others from being registered.
            // The failures are thrown together at the end, so the loader still logs an error instead of "Initialized compatibility".
            var failures = new List<string>();

            void Register(string section, Action action)
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    failures.Add($"{section}: {e}");
                }
            }

            Register("stock up", RegisterStockUp);
            Register("hediff care menu", RegisterHediffCareMenu);
            Register("Compact Hediffs", RegisterCompactHediffs);
            Register("unlimited surgery", RegisterUnlimitedSurgery);
            Register("temporary tending spot", RegisterTempTendSpot);
            Register("inventory drop", RegisterInventoryDrop);

            if (failures.Count > 0)
                throw new Exception($"{failures.Count} Smart Medicine section(s) not registered:\n{string.Join("\n", failures)}");
        }

        #region Stock up

        private static void RegisterStockUp()
        {
            // Stock up medicine/drugs
            var type = AccessTools.TypeByName("SmartMedicine.StockUpUtility");

            var pasteMethod = AccessTools.Method(type, "StockUpPasteSettings");

            stockUpPasteMethod = AccessTools.MethodDelegate<StockUpPasteSettingsDelegate>(pasteMethod);
            stockUpSettingsMethod = AccessTools.MethodDelegate<StockUpSettingsDelegate>(AccessTools.Method(type, "StockUpSettings"));

            // We'll need the access to copiedPawn field to modify it when pasting. Looked up before the paste prefix is patched:
            // a failed lookup after it would leave a prefix that throws on the clicking PC only.
            smartMedicineCompType = AccessTools.TypeByName("SmartMedicine.SmartMedicineGameComp");
            copiedPawnField = AccessTools.FieldRefAccess<Pawn>(smartMedicineCompType, "copiedPawn");

            MpCompat.harmony.Patch(pasteMethod,
                prefix: new HarmonyMethod(typeof(SmartMedicine), nameof(PrePasteSettings)));
            MpCompat.harmony.Patch(AccessTools.Method(type, "SetStockCount", new[] { typeof(Pawn), typeof(ThingDef), typeof(int) }),
                prefix: new HarmonyMethod(typeof(SmartMedicine), nameof(PreSetStockCount)));

            // Mod methods to sync
            MP.RegisterSyncMethod(AccessTools.Method(type, "StockUpStop", new[] { typeof(Pawn), typeof(ThingDef) }));
            MP.RegisterSyncMethod(AccessTools.Method(type, "StockUpClearSettings"));
            // Our methods to sync
            MP.RegisterSyncMethod(typeof(SmartMedicine), nameof(SyncedSetStockCount));
            MP.RegisterSyncMethod(typeof(SmartMedicine), nameof(SyncedPasteSettings));
        }

        private static bool PreSetStockCount(Pawn pawn, ThingDef thingDef, int count)
        {
            if (!MP.IsInMultiplayer)
                return true;

            var dict = stockUpSettingsMethod(pawn);

            // Make sure there's an actual change here, or else it'll end up spamming the sync method
            // That's the main reason why this method exists - if we only sync original one, it'll end up spamming calls
            if (!dict.TryGetValue(thingDef, out var current) || count != current)
                SyncedSetStockCount(pawn, thingDef, count);

            return false;
        }

        // NoInlining on every Synced* method: Multiplayer patches it (sync prefix), so its callers here must call it, never inline its body
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SyncedSetStockCount(Pawn pawn, ThingDef thingDef, int count)
        {
            var dict = stockUpSettingsMethod(pawn);
            dict[thingDef] = count;
        }

        private static bool PrePasteSettings(Pawn pawn)
        {
            if (MP.IsInMultiplayer && !MP.IsExecutingSyncCommand)
            {
                // Get the pawn to copy, and "share" it with everyone to sync up
                var comp = Current.Game.GetComponent(smartMedicineCompType);
                var copiedPawn = copiedPawnField(comp);
                SyncedPasteSettings(pawn, copiedPawn);
                return false;
            }

            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SyncedPasteSettings(Pawn pawn, Pawn copiedPawn)
        {
            var comp = Current.Game.GetComponent(smartMedicineCompType);
            // Get original copied pawn (or null if none) to restore later, and replace it with the synced one for now
            var originalPawn = copiedPawnField(comp);
            copiedPawnField(comp) = copiedPawn;

            // Call the actual method, and make sure it's not cancelled/redirected here
            stockUpPasteMethod(pawn);

            // Restore the original pawn, so if anyone else was copying then they'll be free to do so without the
            // pawn they selected being overriden
            copiedPawnField(comp) = originalPawn;
        }

        #endregion

        #region Hediff care menu

        // HediffRowPriorityCare.CreateCareMenuOptionsWithList builds the float menu for a hediff (and every hediff of the same group and body part).
        // Its option actions are lambdas that write straight into the Smart Medicine game component:
        //   lambda 0 (display class 0): "patient bed rest" - adds or removes the hediffs in PriorityCareSettingsComp.ignoredHediffs
        //   lambda 1 (display class 0): "default care"     - removes the hediff from PriorityCareSettingsComp.hediffCare
        //   lambda 3 (display class 1): one per care level - sets every affected hediff's entry in PriorityCareSettingsComp.hediffCare
        // They are patched here, not registered with MP.RegisterSyncDelegate: lambda 0 needs the live ignoredHediffs set, which cannot be sent.
        // The patched methods are lambdas run through a delegate, so no caller can inline them past the patch.
        private static void RegisterHediffCareMenu()
        {
            var type = AccessTools.TypeByName("SmartMedicine.HediffRowPriorityCare");
            const string menuMethod = "CreateCareMenuOptionsWithList";

            // Older builds (the original mod, Continued for 1.5) still build the menu in LabelButton, synced as before
            if (AccessTools.DeclaredMethod(type, menuMethod) == null && AccessTools.DeclaredMethod(type, "LabelButton") != null)
            {
                MpCompat.RegisterLambdaDelegate(type, "LabelButton", 0, 1);
                return;
            }

            var compType = AccessTools.TypeByName("SmartMedicine.PriorityCareSettingsComp");
            getHediffCare = AccessTools.MethodDelegate<Func<Dictionary<Hediff, MedicalCareCategory>>>(AccessTools.Method(compType, "Get"));
            getIgnoredHediffs = AccessTools.MethodDelegate<Func<HashSet<Hediff>>>(AccessTools.Method(compType, "GetIgnore"));
            var bedRest = MpMethodUtil.GetLambda(type, menuMethod, lambdaOrdinal: 0);
            var defaultCare = MpMethodUtil.GetLambda(type, menuMethod, lambdaOrdinal: 1);
            var setCare = MpMethodUtil.GetLambda(type, menuMethod, lambdaOrdinal: 3);

            // The ordinals are compiler numbering and shift when the mod changes, so check each lambda is the one we expect before patching it
            bedRestPrimaryField = RequireField(bedRest.DeclaringType, "primaryHediff");
            bedRestAffectedField = RequireField(bedRest.DeclaringType, "affectedHediffs");
            defaultCarePrimaryField = RequireField(defaultCare.DeclaringType, "primaryHediff");
            careLevelField = RequireField(setCare.DeclaringType, "mc");
            careOuterField = RequireField(setCare.DeclaringType, "CS$<>8__locals1");
            careAffectedField = RequireField(careOuterField.FieldType, "affectedHediffs");

            RequireLambdaShape(bedRest, readsField: "set", doesNotReadField: null);
            RequireLambdaShape(defaultCare, readsField: "primaryHediff", doesNotReadField: "set");
            RequireLambdaShape(setCare, readsField: "CS$<>8__locals1", doesNotReadField: "set");

            MP.RegisterSyncMethod(typeof(SmartMedicine), nameof(SyncedToggleBedRest)).CancelIfAnyArgNull();
            MP.RegisterSyncMethod(typeof(SmartMedicine), nameof(SyncedClearHediffCare)).CancelIfAnyArgNull();
            MP.RegisterSyncMethod(typeof(SmartMedicine), nameof(SyncedSetHediffCare)).CancelIfAnyArgNull();

            MpCompat.harmony.Patch(bedRest, prefix: new HarmonyMethod(typeof(SmartMedicine), nameof(PreBedRestLambda)));
            MpCompat.harmony.Patch(defaultCare, prefix: new HarmonyMethod(typeof(SmartMedicine), nameof(PreDefaultCareLambda)));
            MpCompat.harmony.Patch(setCare, prefix: new HarmonyMethod(typeof(SmartMedicine), nameof(PreSetCareLambda)));
        }

        private static FieldInfo RequireField(Type type, string name)
            => AccessTools.DeclaredField(type, name) ?? throw new MissingFieldException($"Couldn't find field {name} in type {type}");

        private static void RequireLambdaShape(MethodInfo lambda, string readsField, string doesNotReadField)
        {
            if (lambda.GetParameters().Length != 0)
                throw new Exception($"Lambda {lambda.DeclaringType}::{lambda.Name} takes parameters, expected none");

            var fieldsRead = PatchProcessor.GetOriginalInstructions(lambda)
                .Select(i => i.operand as FieldInfo)
                .Where(f => f != null)
                .Select(f => f.Name)
                .ToHashSet();
            if (!fieldsRead.Contains(readsField) || (doesNotReadField != null && fieldsRead.Contains(doesNotReadField)))
                throw new Exception($"Lambda {lambda.DeclaringType}::{lambda.Name} is not the expected one (reads: {string.Join(", ", fieldsRead)})");
        }

        private static bool PreBedRestLambda(object __instance)
        {
            if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand)
                return true;

            SyncedToggleBedRest((Hediff)bedRestPrimaryField.GetValue(__instance), (List<Hediff>)bedRestAffectedField.GetValue(__instance));
            return false;
        }

        private static bool PreDefaultCareLambda(object __instance)
        {
            if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand)
                return true;

            SyncedClearHediffCare((Hediff)defaultCarePrimaryField.GetValue(__instance));
            return false;
        }

        private static bool PreSetCareLambda(object __instance)
        {
            if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand)
                return true;

            var outer = careOuterField.GetValue(__instance);
            SyncedSetHediffCare((List<Hediff>)careAffectedField.GetValue(outer), (MedicalCareCategory)careLevelField.GetValue(__instance));
            return false;
        }

        // Same changes as the three lambdas make, run on every PC from the synced call
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SyncedToggleBedRest(Hediff primaryHediff, List<Hediff> affectedHediffs)
        {
            var ignored = getIgnoredHediffs();
            if (!ignored.Add(primaryHediff))
            {
                ignored.RemoveWhere(affectedHediffs.Contains);
            }
            else
            {
                foreach (var hediff in affectedHediffs)
                    ignored.Add(hediff);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SyncedClearHediffCare(Hediff primaryHediff)
            => getHediffCare().Remove(primaryHediff);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SyncedSetHediffCare(List<Hediff> affectedHediffs, MedicalCareCategory care)
        {
            var hediffCare = getHediffCare();
            foreach (var hediff in affectedHediffs)
                hediffCare[hediff] = care;
        }

        #endregion

        #region Compact Hediffs

        private static void RegisterCompactHediffs()
        {
            var type = AccessTools.TypeByName("PeteTimesSix.CompactHediffs.Rimworld.UI_compat.UI_SmartMedicine");
            if (type == null)
                return;

            // Compact Hediffs is not in the 1.7 co-op pack, so this part could not be checked against a Compact Hediffs build
            var delegateMethod = MpMethodUtil.GetLambda(type, "AddSmartMedicineFloatMenuButton", lambdaOrdinal: 1);
            var delegateType = delegateMethod.DeclaringType;

            MP.RegisterSyncDelegate(type, delegateType!.Name, delegateMethod.Name, new[] { "hediffs" });
            MpCompat.RegisterLambdaDelegate(type, "AddSmartMedicineFloatMenuButton", new[] { "CS$<>8__locals1/hediffs", "mc" }, 2);

            delegateTypeHediffCareField = AccessTools.FieldRefAccess<object>(delegateType, "hediffCares");
            MpCompat.harmony.Patch(AccessTools.Constructor(delegateType),
                prefix: new HarmonyMethod(typeof(SmartMedicine), nameof(InitHediffCareDictionary)));

            // PriorityCareComp.Get is obsolete in the Continued build (PriorityCareSettingsComp.Get returns the same dictionary)
            var careType = AccessTools.TypeByName("SmartMedicine.PriorityCareSettingsComp") ?? AccessTools.TypeByName("SmartMedicine.PriorityCareComp");
            getSmartMedicinePriorityCareDictionary = MethodInvoker.GetHandler(AccessTools.Method(careType, "Get"));
        }

        // CompactHeddifs compat
        private static void InitHediffCareDictionary(object __instance)
            => delegateTypeHediffCareField(__instance) = getSmartMedicinePriorityCareDictionary(null);

        #endregion

        #region Unlimited Surgery

        // SurgeryUnlimitedGameComponent.Set(pawn, value) is called every frame the checkbox is drawn, with the value the checkbox shows, so it
        // cannot be synced itself (it would also be a small method the JIT may inline into the drawing code). The drawing methods are wrapped
        // instead: if the pawn's entry changed while the checkbox was drawn, the old value is put back and the new one is sent as a synced call.
        // The same for the default checkbox, which the mod draws with a ref to the component's field.
        private static void RegisterUnlimitedSurgery()
        {
            surgeryUnlimitedCompType = AccessTools.TypeByName("SmartMedicine.SurgeryUnlimited.SurgeryUnlimitedGameComponent");
            surgeryUnlimitedSetField = AccessTools.FieldRefAccess<HashSet<Pawn>>(surgeryUnlimitedCompType, "surgeryUnlimited");
            surgeryUnlimitedDefaultField = AccessTools.FieldRefAccess<bool>(surgeryUnlimitedCompType, "surgeryUnlimitedDefault");
            surgeryUnlimitedSetMethod = MethodInvoker.GetHandler(AccessTools.Method(surgeryUnlimitedCompType, "Set", new[] { typeof(Pawn), typeof(bool) }));

            MP.RegisterSyncMethod(typeof(SmartMedicine), nameof(SyncedSetSurgeryUnlimited)).CancelIfAnyArgNull();
            MP.RegisterSyncMethod(typeof(SmartMedicine), nameof(SyncedSetSurgeryUnlimitedDefault));

            var pawnPrefix = new HarmonyMethod(typeof(SmartMedicine), nameof(PreSurgeryUnlimitedPawn));
            var pawnPostfix = new HarmonyMethod(typeof(SmartMedicine), nameof(PostSurgeryUnlimitedPawn));

            // Assign tab (and Medical Tab) column
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod("SmartMedicine.SurgeryUnlimited.PawnColumnWorker_UnlimitedSurgery:DoCell"),
                prefix: pawnPrefix, postfix: pawnPostfix);
            // Checkbox in the health tab overview
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod("SmartMedicine.SurgeryUnlimited.SurgergyUnlimitedPawnSettings:DrawSurgeryOption"),
                prefix: pawnPrefix, postfix: pawnPostfix);
            // Default checkbox in the medical defaults dialog. The mod draws it in a postfix of this method, so ours is a finalizer: it runs after every postfix.
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(Dialog_MedicalDefaults), nameof(Dialog_MedicalDefaults.DoWindowContents)),
                prefix: new HarmonyMethod(typeof(SmartMedicine), nameof(PreSurgeryUnlimitedDefault)),
                finalizer: new HarmonyMethod(typeof(SmartMedicine), nameof(PostSurgeryUnlimitedDefault)));
        }

        private static bool IsSurgeryUnlimited(Pawn pawn)
        {
            var comp = Current.Game?.GetComponent(surgeryUnlimitedCompType);
            return comp != null && surgeryUnlimitedSetField(comp).Contains(pawn);
        }

        private static void SetSurgeryUnlimited(Pawn pawn, bool value)
        {
            var comp = Current.Game?.GetComponent(surgeryUnlimitedCompType);
            if (comp != null)
                surgeryUnlimitedSetMethod(comp, pawn, value);
        }

        private static void PreSurgeryUnlimitedPawn(Pawn pawn, out bool? __state)
            => __state = MP.IsInMultiplayer ? IsSurgeryUnlimited(pawn) : null;

        private static void PostSurgeryUnlimitedPawn(Pawn pawn, bool? __state)
        {
            if (__state == null || !MP.IsInMultiplayer)
                return;

            var now = IsSurgeryUnlimited(pawn);
            if (now == __state.Value)
                return;

            // Put the old value back and let everyone (this PC included) apply the new one in the same tick
            SetSurgeryUnlimited(pawn, __state.Value);
            SyncedSetSurgeryUnlimited(pawn, now);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SyncedSetSurgeryUnlimited(Pawn pawn, bool unlimited)
            => SetSurgeryUnlimited(pawn, unlimited);

        private static void PreSurgeryUnlimitedDefault(out bool? __state)
        {
            var comp = MP.IsInMultiplayer ? Current.Game?.GetComponent(surgeryUnlimitedCompType) : null;
            __state = comp != null ? surgeryUnlimitedDefaultField(comp) : null;
        }

        private static void PostSurgeryUnlimitedDefault(bool? __state)
        {
            if (__state == null || !MP.IsInMultiplayer)
                return;

            var comp = Current.Game?.GetComponent(surgeryUnlimitedCompType);
            if (comp == null)
                return;

            var now = surgeryUnlimitedDefaultField(comp);
            if (now == __state.Value)
                return;

            surgeryUnlimitedDefaultField(comp) = __state.Value;
            SyncedSetSurgeryUnlimitedDefault(now);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SyncedSetSurgeryUnlimitedDefault(bool unlimited)
        {
            var comp = Current.Game?.GetComponent(surgeryUnlimitedCompType);
            if (comp == null)
                return;

            surgeryUnlimitedDefaultField(comp) = unlimited;
        }

        #endregion

        #region Temporary tending spot

        // The Cancel gizmo of the spot Smart Medicine places for tending in the field destroys the building
        private static void RegisterTempTendSpot()
        {
            var type = AccessTools.TypeByName("SmartMedicine.Building_TempTendSpot");
            var cancel = MpMethodUtil.GetLambda(type, "GetGizmos", lambdaOrdinal: 0);
            if (!PatchProcessor.GetOriginalInstructions(cancel).Any(i => i.operand is MethodInfo m && m.Name == nameof(Thing.Destroy)))
                throw new Exception($"Lambda {cancel.DeclaringType}::{cancel.Name} is not the expected one (it doesn't call Thing.Destroy)");

            MP.RegisterSyncMethod(cancel);
        }

        #endregion

        #region Patched sync methods

        private static void RegisterInventoryDrop()
        {
            // When ordering a pawn to drop something, it'll try to stop them from stocking up on it.
            // Will cause desyncs if this happened to be something they were stocking up
            // if the pawn decides to unload the things they were stocking up on.
            // Used for both vanilla and RPG style inventory InterfaceDrop method.
            PatchingUtilities.PatchCancelInInterface("SmartMedicine.InterfaceDrop_Patch:Postfix");
        }

        #endregion
    }
}
