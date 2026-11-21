using System;
using HarmonyLib;
using Multiplayer.API;
using UnityEngine;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Supply Shelf by Firework</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3837808326"/>
    [MpCompatFor("Firework.SupplyShelf")]
    public class SupplyShelf
    {
        // Code-only check (17 Nov, fix1116/newmods/RESULT_SUPPLYSHELF.md findings 1, 2, 4), nothing booted.
        // 1. "Link workbench" gizmo: the targeter callback CompDirectSupply.Toggle(LocalTargetInfo) edits the saved benches list.
        // 2. Storage-tab stacks slider: Building_SupplyShelf.MaxStacks setter writes the saved field and ejects the excess items.
        //    Synced as a method (not a field) so EjectExcess runs on every PC.
        // 4. Mod setting "Freezing" feeds rot and power draw: locked while Multiplayer runs (same idiom as HlsFishPond).
        // All types are global-namespace, looked up by name: no assembly reference.
        public SupplyShelf(ModContentPack mod)
        {
            var compType = AccessTools.TypeByName("CompDirectSupply");
            var toggle = compType == null ? null : AccessTools.DeclaredMethod(compType, "Toggle", new[] { typeof(LocalTargetInfo) });
            if (toggle == null)
                Log.Error("MPCompat :: Supply Shelf: CompDirectSupply.Toggle(LocalTargetInfo) not found, the link workbench gizmo stays unsynced");
            else
                MP.RegisterSyncMethod(toggle).CancelIfAnyArgNull();

            var shelfType = AccessTools.TypeByName("Building_SupplyShelf");
            var setter = shelfType == null ? null : AccessTools.DeclaredPropertySetter(shelfType, "MaxStacks");
            if (setter == null)
                Log.Error("MPCompat :: Supply Shelf: Building_SupplyShelf.MaxStacks setter not found, the stacks slider stays unsynced");
            else
                MP.RegisterSyncMethod(setter);

            // 5. Filter check. The mod's storage tab calls Building_SupplyShelf.CheckFilter() every frame; when the allowed count
            //    differs from lastCount it runs OnFilterChanged (rewrites the filter, DeSpawns items, reads the per-PC multiType setting).
            //    Only the PC with the tab open ran it. The shelf has no tick, so in Multiplayer the UI call is skipped and the same
            //    check runs in the simulation on every PC (Map.MapPostTick, once a second). The filter edits themselves are synced by
            //    Multiplayer (ThingFilter.SetAllow), and the mod's Notify_SettingsChanged postfix already runs OnFilterChanged on every PC.
            shelfCheckFilter = shelfType == null ? null : AccessTools.DeclaredMethod(shelfType, "CheckFilter");
            shelfBaseType = shelfType;
            if (shelfCheckFilter == null)
                Log.Error("MPCompat :: Supply Shelf: Building_SupplyShelf.CheckFilter not found, a filter change made through the storage tab stays local to one PC");
            else
            {
                MpCompat.harmony.Patch(shelfCheckFilter, prefix: new HarmonyMethod(typeof(SupplyShelf), nameof(PreCheckFilter)));
                MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(Map), nameof(Map.MapPostTick)),
                    postfix: new HarmonyMethod(typeof(SupplyShelf), nameof(PostMapPostTick)));
            }

            var modType = AccessTools.TypeByName("SupplyShelfMod");
            var settingsWindow = modType == null ? null : AccessTools.DeclaredMethod(modType, "DoSettingsWindowContents", new[] { typeof(Rect) });
            if (settingsWindow == null)
                Log.Warning("MPCompat :: Supply Shelf: SupplyShelfMod.DoSettingsWindowContents not found, the settings are not locked in Multiplayer");
            else
                MpCompat.harmony.Patch(settingsWindow, prefix: new HarmonyMethod(typeof(SupplyShelf), nameof(PreDoSettingsWindowContents)));
        }

        private static System.Reflection.MethodInfo shelfCheckFilter;
        private static Type shelfBaseType;
        private static bool inSimulation;

        // UI path (ITab_Storage FillTab prefix of the mod): skipped in Multiplayer, the simulation does the check on every PC.
        private static bool PreCheckFilter() => !MP.IsInMultiplayer || inSimulation;

        private static void PostMapPostTick(Map __instance)
        {
            if (!MP.IsInMultiplayer || Find.TickManager.TicksGame % 60 != 0) return;
            var list = __instance.listerBuildings.allBuildingsColonist;
            inSimulation = true;
            try
            {
                for (int i = list.Count - 1; i >= 0; i--)
                    if (i < list.Count && list[i].GetType() == shelfBaseType)
                        shelfCheckFilter.Invoke(list[i], null);
            }
            finally { inSimulation = false; }
        }

        // "Freezing" feeds rot and power draw and is per PC. Locked while Multiplayer runs.
        private static bool PreDoSettingsWindowContents(Rect inRect)
        {
            if (!MP.IsInMultiplayer)
                return true;

            Widgets.Label(inRect, "Multiplayer: the Freezing setting feeds the simulation. Change it in single player, then restart the session with the same config on every PC.");
            return false;
        }
    }
}
