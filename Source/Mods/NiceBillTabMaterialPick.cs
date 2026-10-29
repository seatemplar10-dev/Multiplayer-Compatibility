using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Nice Bill Tab by Andromeda - material pick on an existing bill</summary>
    /// <remarks>
    /// FIXES row 270 (28 Oct 2026 live co-op, James's Desync-08 = desync 3, opinion tick 234811, trace 461): at map tick 198575
    /// pawn Human30627 on James's map ran WorkGiver_DoBill.StartOrResumeBillJob. James's PC found the ingredients and started the
    /// job (TryStartNewDoBillJob -> UniqueIDsManager.GetNextID); the host found none and drew the recheck timer
    /// (IntRange.RandomInRange). So one bill or its ingredient filter differed between the PCs.
    /// One writer of the ingredient filter is still unsynced after rows 275 / 277 / 283: Nice Bill Tab's ingredient float menu
    /// (RecipeSelection.OpenIngredientMenu: "material (have/need)" and "Maximize stat") calls RecipeSelection.SelectBetterMaterial,
    /// which for a selected bill calls TabBillsDrawer.SetMaterialToBill(selection, recipe, bill):
    /// 'bill.ingredientFilter.SetDisallowAll(); bill.ingredientFilter.SetAllow(selection.material, true);'.
    /// Multiplayer syncs ThingFilter.SetAllow / SetDisallowAll only while a known filter window is drawn
    /// (ThingFilterMarkers.DrawnThingFilter); a float menu action from this tab has none, so the filter changed on the clicking PC only.
    /// Now a pick on a bill that is in a bill stack is sent as a command (bill, recipe, material) and set on every PC.
    /// A bill that is in no stack yet (the 'Add recipe' bill that row 275 holds back) keeps the local call: row 275 sends it by value
    /// with its filter. Not covered: the clicking PC's ingredient panel can show the old material until it refreshes.
    /// Status: [unverified] compiled only, not run in game.
    /// </remarks>
    [MpCompatFor("Andromeda.NiceBillTab")]
    internal class NiceBillTabMaterialPick
    {
        private static FieldInfo selectionMaterial;

        public NiceBillTabMaterialPick(ModContentPack mod)
        {
            var selectionType = AccessTools.TypeByName("NiceBillTab.RecipeSelection");
            var setMaterial = AccessTools.DeclaredMethod("NiceBillTab.TabBillsDrawer:SetMaterialToBill");
            var materialField = selectionType == null ? null : AccessTools.DeclaredField(selectionType, "material");
            var parameters = setMaterial?.GetParameters();

            if (selectionType == null || setMaterial == null || materialField == null || materialField.FieldType != typeof(ThingDef)
                || parameters == null || parameters.Length != 3 || parameters[0].ParameterType != selectionType
                || parameters[1].ParameterType != typeof(RecipeDef) || parameters[2].ParameterType != typeof(Bill))
            {
                Log.Error("MPCompat - Nice Bill Tab: RecipeSelection.material or TabBillsDrawer.SetMaterialToBill(RecipeSelection, RecipeDef, Bill) " +
                          "not found - the material pick on a bill stays unsynced (FIXES row 270)");
                return;
            }

            selectionMaterial = materialField;
            MP.RegisterSyncMethod(typeof(NiceBillTabMaterialPick), nameof(SyncedSetMaterial));
            MpCompat.harmony.Patch(setMaterial, prefix: new HarmonyMethod(typeof(NiceBillTabMaterialPick), nameof(PreSetMaterialToBill)));
        }

        // Interface only: a bill that every PC holds is changed by a command; anything else runs the mod's own code.
        private static bool PreSetMaterialToBill(object selection, RecipeDef recipe, Bill bill)
        {
            if (!MP.IsInMultiplayer || !MP.InInterface || selection == null || bill == null)
                return true;

            var material = selectionMaterial.GetValue(selection) as ThingDef;
            if (material == null)
                return true; // the mod returns at once

            if (bill.billStack == null || !bill.billStack.Bills.Contains(bill))
                return true; // a local bill (row 275 holds it back and sends it with its filter)

            SyncedSetMaterial(bill, recipe, material);
            return false;
        }

        // Synced: the same body as the mod's SetMaterialToBill, run on every PC inside the command.
        private static void SyncedSetMaterial(Bill bill, RecipeDef recipe, ThingDef material)
        {
            if (bill == null || recipe == null || material == null)
                return;

            var produced = recipe.ProducedThingDef;
            if (produced == null || !produced.MadeFromStuff)
                return;

            if (bill.ingredientFilter == null)
            {
                Log.Warning("MPCompat - Nice Bill Tab: the bill has no ingredient filter, material pick skipped (FIXES row 270)");
                return;
            }

            bill.ingredientFilter.SetDisallowAll();
            bill.ingredientFilter.SetAllow(material, true);
        }
    }
}
