using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using Verse;
using RimWorld;

namespace Multiplayer.Compat
{
    /// <summary>RPG Style Inventory by Sandy</summary>
    /// <see href="https://github.com/SandyTheGreat/RPG-Style-Inventory"/>
    /// <see href="https://github.com/catgirlfighter/-1.0-RPG-Style-Inventory-V3.0-"/>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=1561221991"/>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=2478833213"/>
    [MpCompatFor("Sandy.RPGStyleInventory")]
    [MpCompatFor("Nykot.RPGStyleInventory")]
    [MpCompatFor("Sandy.RPGStyleInventory.avilmask.Revamped")]
    class RPGStyleInventory
    {
        private static Type tabType;
        // Revamped only: the tab's own pawn, set when the tab is drawn (FillTab); InterfaceDrop reads it instead of the selection
        private static AccessTools.FieldRef<object, Pawn> cachedPawnField;

        public RPGStyleInventory(ModContentPack mod)
        {
            tabType = AccessTools.TypeByName("Sandy_Detailed_RPG_Inventory.Sandy_Detailed_RPG_GearTab");

            if (AccessTools.DeclaredField(tabType, "cachedPawn")?.FieldType == typeof(Pawn))
                cachedPawnField = AccessTools.FieldRefAccess<Pawn>(tabType, "cachedPawn");

            MP.RegisterSyncWorker<ITab_Pawn_Gear>(SyncITab, tabType);

            // Original re-implemented InterfaceDrop/InterfaceIngest, but some stopped doing that.
            // Sync those methods if they're declared, but skip if they aren't since we don't want
            // to sync vanilla methods (which are already synced). On top of that, vanilla marked
            // InterfaceIngest as obsolete (use FoodUtility.IngestFromInventoryNow instead),
            // so avoid HugsLib warnings about patching obsolete methods.
            foreach (var methodName in new[] { "InterfaceDrop", "InterfaceIngest" })
            {
                var method = AccessTools.DeclaredMethod(tabType, methodName);
                if (method != null)
                    MP.RegisterSyncMethod(method).SetContext(SyncContext.MapSelected);
            }

            // Remove/add forced apparel
            if (mod.PackageId == "Sandy.RPGStyleInventory.avilmask.Revamped".ToLower()) {
                MpCompat.RegisterLambdaDelegate(tabType, "PopupMenu", 0, 1);
            }

            RegisterCombatExtendedMenu(mod.PackageId == "Sandy.RPGStyleInventory.avilmask.Revamped".ToLower());
        }

        /// <summary>
        /// Revamped + Combat Extended: the "Drop and haul" entry of the gear tab menu (and "Forget hold") ran on the clicking PC only.
        /// </summary>
        /// <remarks>
        /// Thread t21. Revamped ships <c>CEPatches.dll</c>; with CE active its <c>RPG_CEPatches.PopupMenuPrefix</c> replaces the tab's
        /// <c>PopupMenu</c> with its own <c>DropDownThingMenu</c> (CE's menu copied into the tab), so the lambdas registered above are
        /// not the ones the player clicks. Its "Drop" entry calls the tab's <c>InterfaceDrop</c>, which is synced here. Its
        /// <c>CE_DropThingHaul</c> entry ("Drop and haul") calls <c>RPG_CEPatches.InterfaceDropHaul(pawn, thing, selPawn)</c> directly,
        /// and nothing syncs that. Its three branches: a worn apparel or held weapon starts <c>RemoveApparel</c> (with
        /// <c>haulDroppedApparel</c>) / <c>DropEquipment</c> through <c>Pawn_JobTracker.TryTakeOrderedJob</c> (MP syncs that call, so
        /// those two branches reached every PC anyway); an inventory item goes through <c>innerContainer.TryDrop</c> on the clicking PC
        /// only, which spawns the item on one PC. Before any branch it also calls CE's <c>HoldTrackerForget</c>, which edits the
        /// saved <c>LoadoutManager</c> hold records on one PC only. CE's own gear tab syncs this action
        /// (<c>ITab_Inventory.SyncedInterfaceDropHaul</c>, registered by CE's ModPatches/Multiplayer part); the copy in Revamped's
        /// patch does not.
        /// Fix: sync <c>InterfaceDropHaul</c> itself (all three arguments are explicit, so no context is needed; a corpse's gear tab
        /// has no <c>SelPawn</c> and cancels instead of throwing) and CE's <c>HoldTrackerForget(Pawn, Thing)</c>, which the menu's
        /// "Forget hold" entry also calls on the clicking PC only. Both are called from simulation code as well
        /// (<c>HoldTrackerForget</c> from CE's drop-unused-inventory job giver); MP only intercepts a call made from interface code,
        /// so those calls are unchanged. <c>InterfaceDropHaul</c> is 271 bytes of IL, so the JIT will not inline it into the menu
        /// lambda and skip the patch.
        /// Three more entries of the same menu wrote simulation state on the clicking PC only. CE's own tab syncs "equip" and "put away"
        /// (<c>SyncedTrySwitchToWeapon</c>, <c>SyncedTryTransferEquipmentToContainer</c>); the forced-apparel pair is what Revamped's own
        /// <c>PopupMenu</c> registration above covers, but CEPatches replaces that menu:
        /// <list type="bullet">
        /// <item>"Equip" (<c>DropDownThingMenu</c> lambda 6 -> <c>CEAccess.trySwitchToWeapon</c> -> <c>CompInventory.TrySwitchToWeapon(eq, true)</c>):
        /// the real method has explicit arguments, so it is registered itself (CE's wrapper calls it too, from inside its own synced
        /// call, where MP does not intercept). Simulation callers (equip job, take-and-equip job giver, verbs) are unchanged.</item>
        /// <item>"Put away" (lambda 5): its only real call takes a <c>ThingOwner</c>, which MP cannot send, so the lambda is synced as a
        /// delegate with just its <c>SelPawnForGear</c> field (the same effect as CE's <c>SyncedTryTransferEquipmentToContainer(Pawn)</c>).</item>
        /// <item>Forced apparel clear / set (lambdas 1 and 2, closure <c>&lt;&gt;c__DisplayClass35_1</c>): synced as delegates with the
        /// apparel and the pawn behind <c>CS$&lt;&gt;8__locals1</c>. The fields the lambdas do not read are left out: <c>eq</c> in this
        /// closure, and <c>thing</c>, <c>tab</c> (an <c>object</c>) and <c>SelPawn</c> in the outer <c>&lt;&gt;c__DisplayClass35_0</c>.</item>
        /// </list>
        /// Lambda ordinals are the compiler's names in Revamped's CEPatches.dll (sha256 043b55a3...). A rebuilt dll that renumbers them
        /// makes the lookup throw when a named field is missing (logged as a warning, that one entry stays unsynced). If the lambda now at
        /// that ordinal has the same fields, it syncs the wrong entry without a warning, so re-check the ordinals when CEPatches.dll changes.
        /// </remarks>
        private static void RegisterCombatExtendedMenu(bool revamped)
        {
            // Without CE, CEPatches.dll is still loaded but never patches the menu (RPG_CEPatch's static constructor needs both
            // mods), so nothing to sync then
            if (!ModsConfig.IsActive("CETeam.CombatExtended"))
                return;

            // Nested private class of Revamped's CEPatches.dll (absent from older RPG Style Inventory builds)
            var cePatches = AccessTools.TypeByName("CEPatches.RPG_CEPatch+RPG_CEPatches");
            // The same lookup CEPatches makes for its own HoldTrackerForget call, so both find the same type
            var holdTracker = AccessTools.TypeByName("CombatExtended.Utility_HoldTracker");
            if (cePatches == null || holdTracker == null)
            {
                if (revamped)
                    Log.Warning($"[MPCompat - RPG Style Inventory] {(cePatches == null ? "CEPatches.RPG_CEPatch+RPG_CEPatches" : "CombatExtended.Utility_HoldTracker")} not found, the Combat Extended gear tab menu stays unsynced");
                return;
            }

            var dropHaul = AccessTools.DeclaredMethod(cePatches, "InterfaceDropHaul");
            if (dropHaul == null)
                Log.Warning("[MPCompat - RPG Style Inventory] CEPatches.RPG_CEPatches.InterfaceDropHaul not found, 'Drop and haul' stays unsynced");
            else
                // Shift queues the haul job, as with MP's own sync of the vanilla InterfaceDrop
                MP.RegisterSyncMethod(dropHaul).SetContext(SyncContext.QueueOrder_Down).CancelIfAnyArgNull();

            var forget = AccessTools.DeclaredMethod(holdTracker, "HoldTrackerForget", [typeof(Pawn), typeof(Thing)]);
            if (forget == null)
                Log.Warning("[MPCompat - RPG Style Inventory] CombatExtended.Utility_HoldTracker.HoldTrackerForget not found, 'Forget hold' stays unsynced");
            else
                MP.RegisterSyncMethod(forget).CancelIfAnyArgNull();

            TryRegister("'Equip'", () =>
            {
                // Taken from the call in CEAccess.trySwitchToWeapon, not looked up by name: a second CombatExtended.dll (one mod ships
                // its own copy) could hold a CompInventory type that the menu never calls
                var access = AccessTools.DeclaredMethod(AccessTools.TypeByName("CEPatches.CEAccess"), "trySwitchToWeapon")
                             ?? throw new MissingMethodException("CEPatches.CEAccess", "trySwitchToWeapon");
                var switchTo = PatchProcessor.GetOriginalInstructions(access)
                                   .Select(instr => instr.operand)
                                   .OfType<MethodInfo>()
                                   .FirstOrDefault(m => m.Name == "TrySwitchToWeapon" && m.DeclaringType?.FullName == "CombatExtended.CompInventory")
                               ?? throw new MissingMethodException("CombatExtended.CompInventory", "TrySwitchToWeapon (no call in CEAccess.trySwitchToWeapon)");
                MP.RegisterSyncMethod(switchTo).CancelIfAnyArgNull();
            });

            TryRegister("'Put away'", () =>
            {
                foreach (var del in MpCompat.RegisterLambdaDelegate(cePatches, "DropDownThingMenu", ["SelPawnForGear"], 5))
                    del.CancelIfAnyFieldNull();
            });

            // One registration per lambda, so a failed second lookup cannot leave the first one registered without its null check
            TryRegister("forced apparel clear", () =>
            {
                foreach (var del in MpCompat.RegisterLambdaDelegate(cePatches, "DropDownThingMenu", ["CS$<>8__locals1/SelPawnForGear", "app"], 1))
                    del.CancelIfAnyFieldNull();
            });
            TryRegister("forced apparel set", () =>
            {
                foreach (var del in MpCompat.RegisterLambdaDelegate(cePatches, "DropDownThingMenu", ["CS$<>8__locals1/SelPawnForGear", "app"], 2))
                    del.CancelIfAnyFieldNull();
            });
        }

        // One failed lookup (a rebuilt CEPatches.dll that renumbers a lambda, a renamed CE method) must not stop the registrations around it
        private static void TryRegister(string what, Action register)
        {
            try
            {
                register();
            }
            catch (Exception e)
            {
                Log.Warning($"[MPCompat - RPG Style Inventory] {what} entry of the Combat Extended gear tab menu stays unsynced: {e.Message}");
            }
        }

        private static void SyncITab(SyncWorker sync, ref ITab_Pawn_Gear gearITab)
        {
            if (sync.isWriting)
            {
                // The tab the player clicked in has its pawn cached; a fresh tab would not (NRE in InterfaceDrop on every PC, row 208)
                if (cachedPawnField != null)
                    sync.Write(gearITab == null ? null : cachedPawnField(gearITab));
                return;
            }

            gearITab = Activator.CreateInstance(tabType) as ITab_Pawn_Gear;
            if (cachedPawnField == null)
                return;

            var pawn = sync.Read<Pawn>();
            // Pawn gone before the command ran: a null target cancels the command on every PC instead of throwing
            if (pawn == null)
                gearITab = null;
            else
                cachedPawnField(gearITab) = pawn;
        }
    }
}