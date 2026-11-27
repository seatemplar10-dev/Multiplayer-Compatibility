using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Choose Your Outfit by Oels</summary>
    /// <remarks>
    /// FIXES row 504: the CYO apparel policy window writes policy filters (ThingFilter.SetAllow / SetDisallowAll) outside the vanilla
    /// filter draw, so Multiplayer does not sync them and only the clicking PC changes. Writes covered (7): context menu add to / remove from
    /// all policies, ApplyFilter (2 sites), worn apparel list apply (2 sites), float menu add/remove apparel. Bills added by the
    /// "add bills" dialog go through BillStack.AddBill, which Multiplayer already syncs.
    /// </remarks>
    [MpCompatFor("oels.chooseyouroutfit")]
    class ChooseYourOutfit
    {
        [ThreadStatic] static int scope;
        static Type dialogType;
        static FieldInfo canWearField;
        static MethodInfo loadFilter;
        static PropertyInfo selectedPolicyProp, selectedPawnProp;

        public ChooseYourOutfit(ModContentPack mod)
        {
            if (LabOff.Row(504))
            {
                Log.Message("[MPCompat - Choose Your Outfit] row 504 switched off by the lab, policy edits not synced");
                return;
            }
            dialogType = AccessTools.TypeByName("ChooseYourOutfit.Dialog_ManageApparelPoliciesEx");
            var worn = AccessTools.TypeByName("ChooseYourOutfit.Dialog_WornApparelList");
            var floatClosure = AccessTools.TypeByName("ChooseYourOutfit.FloatMenuOptionProvider_ApparelFilter")
                ?.GetNestedTypes(AccessTools.all).FirstOrDefault(t => t.GetMethod("<GetOptionsFor>b__0", AccessTools.all) != null);
            if (dialogType == null || worn == null || floatClosure == null)
            {
                Log.Error("[MPCompat - Choose Your Outfit] row 504: CYO types not found, policy edits can desync");
                return;
            }
            canWearField = AccessTools.Field(dialogType, "canWearAllowed");
            loadFilter = AccessTools.Method(dialogType, "LoadFilter");
            selectedPolicyProp = AccessTools.Property(dialogType, "SelectedPolicy");
            selectedPawnProp = AccessTools.Property(dialogType, "SelectedPawn");

            var enter = new HarmonyMethod(typeof(ChooseYourOutfit), nameof(Enter));
            var exit = new HarmonyMethod(typeof(ChooseYourOutfit), nameof(Exit));
            int sites = 0;
            // 1,2: context menu add to / remove from all policies (display class of the GenerateContextMenu iterator)
            var ctx = dialogType.GetNestedTypes(AccessTools.all).FirstOrDefault(t => t.GetMethod("<GenerateContextMenu>b__0", AccessTools.all) != null);
            foreach (var name in new[] { "<GenerateContextMenu>b__0", "<GenerateContextMenu>b__3" })
                sites += Scope(ctx?.GetMethod(name, AccessTools.all), enter, exit);
            sites += Scope(AccessTools.Method(dialogType, "ApplyFilter"), enter, exit);                 // 3,4
            sites += Scope(AccessTools.Method(worn, "DoWindowContents"), enter, exit);                  // 5,6
            sites += Scope(floatClosure.GetMethod("<GetOptionsFor>b__0", AccessTools.all), enter, exit); // 7
            if (sites != 5)
            {
                Log.Error($"[MPCompat - Choose Your Outfit] row 504: only {sites}/5 CYO write sites found, policy edits can desync");
                return;
            }

            MpCompat.harmony.Patch(AccessTools.Method(typeof(ThingFilter), nameof(ThingFilter.SetAllow), new[] { typeof(ThingDef), typeof(bool) }),
                prefix: new HarmonyMethod(typeof(ChooseYourOutfit), nameof(PrefixSetAllow)));
            MpCompat.harmony.Patch(AccessTools.Method(typeof(ThingFilter), nameof(ThingFilter.SetDisallowAll)),
                prefix: new HarmonyMethod(typeof(ChooseYourOutfit), nameof(PrefixDisallowAll)));
            MP.RegisterSyncMethod(typeof(ChooseYourOutfit), nameof(SyncedAllow)).CancelIfAnyArgNull();
            MP.RegisterSyncMethod(typeof(ChooseYourOutfit), nameof(SyncedDisallowAll)).CancelIfAnyArgNull();
            Log.Message("[MPCompat - Choose Your Outfit] row 504: Choose Your Outfit policy edits synced (7 writes)");
        }

        static int Scope(MethodBase m, HarmonyMethod enter, HarmonyMethod exit)
        {
            if (m == null) return 0;
            MpCompat.harmony.Patch(m, prefix: enter, finalizer: exit);
            return 1;
        }

        static void Enter() => scope++;
        static void Exit() { if (scope > 0) scope--; }

        static ApparelPolicy PolicyOf(ThingFilter filter)
        {
            var all = Current.Game?.outfitDatabase?.AllOutfits;
            return all?.FirstOrDefault(p => p.filter == filter);
        }

        static bool PrefixSetAllow(ThingFilter __instance, ThingDef thingDef, bool allow)
        {
            if (scope <= 0 || !MP.IsInMultiplayer || MP.IsExecutingSyncCommand) return true;
            var policy = PolicyOf(__instance);
            if (policy == null) return true;
            SyncedAllow(policy, thingDef, allow);
            return false;
        }

        static bool PrefixDisallowAll(ThingFilter __instance)
        {
            if (scope <= 0 || !MP.IsInMultiplayer || MP.IsExecutingSyncCommand) return true;
            var policy = PolicyOf(__instance);
            if (policy == null) return true;
            SyncedDisallowAll(policy);
            return false;
        }

        // Run on every PC.
        static void SyncedAllow(ApparelPolicy policy, ThingDef def, bool allow)
        {
            policy.filter.SetAllow(def, allow);
            RefreshWindow();
        }

        static void SyncedDisallowAll(ApparelPolicy policy)
        {
            policy.filter.SetDisallowAll(null, null);
            RefreshWindow();
        }

        // The CYO window caches the allowed apparel of the selected policy; the click no longer rebuilds it, so do it once the command ran.
        static void RefreshWindow()
        {
            try
            {
                var win = Find.WindowStack?.Windows.FirstOrDefault(w => w.GetType() == dialogType);
                if (win == null) return;
                var policy = selectedPolicyProp.GetValue(win) as ApparelPolicy;
                var pawn = selectedPawnProp.GetValue(win) as Pawn;
                if (policy == null || pawn == null) return;
                var set = canWearField.GetValue(win) as System.Collections.Generic.HashSet<ThingDef>;
                if (set == null) return;
                set.Clear();
                foreach (var d in policy.filter.AllowedThingDefs)
                    if (d != null && d.IsApparel && d.apparel.PawnCanWear(pawn, false)) set.Add(d);
                loadFilter?.Invoke(win, null);
            }
            catch (Exception e) { Log.Warning("[MPCompat - Choose Your Outfit] window refresh failed: " + e.Message); }
        }
    }
}
