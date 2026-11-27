using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Vanilla Quests Expanded - Deadlife by Oskar Potocki, Sarg Bjornson, Taranchuk</summary>
    [MpCompatFor("vanillaquestsexpanded.deadlife")]
    class VanillaQuestsExpandedDeadlife
    {
        public VanillaQuestsExpandedDeadlife(ModContentPack mod)
        {
            if (LabOff.Row(496))
                Log.Message("[MPCompat - VQE Deadlife] row 496 switched off by the lab, ThrowSmoke Rand is not pushed/popped");
            else
            {
                // Skyfaller_DeadlifeICBM.Tick calls Utils.ThrowSmoke every tick; it checks GenView.ShouldSpawnMotesAt
                // (depends on each PC's camera/map) before four Rand.Range calls, so the Rand streams split.
                PatchingUtilities.PatchPushPopRand("VanillaQuestsExpandedDeadlife.Utils:ThrowSmoke");
                Log.Message("[MPCompat - VQE Deadlife] row 496: ThrowSmoke Rand push/pop on 1 methods");
            }

            if (LabOff.Row(497))
            {
                Log.Message("[MPCompat - VQE Deadlife] row 497 switched off by the lab, death pit Fill gizmo not synced");
                return;
            }
            // DeathPit.GetGizmos: the Fill button's lambda (ordinal 0, <GetGizmos>b__18_0) calls Utils.PlaceDistinctBlueprint
            // (GenConstruct.PlaceBlueprintForBuild) on the clicking PC only; ordinal 1 (dev "Release shamblers now") writes tickCounter.
            MpCompat.RegisterLambdaMethod("VanillaQuestsExpandedDeadlife.DeathPit", nameof(Thing.GetGizmos), 0);
            MpCompat.RegisterLambdaMethod("VanillaQuestsExpandedDeadlife.DeathPit", nameof(Thing.GetGizmos), 1).SetDebugOnly();
            Log.Message("[MPCompat - VQE Deadlife] row 497: death pit Fill gizmo synced");
        }
    }
}
