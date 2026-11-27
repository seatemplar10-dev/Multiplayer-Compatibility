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
            {
                Log.Message("[MPCompat - VQE Deadlife] row 496 switched off by the lab, ThrowSmoke Rand is not pushed/popped");
                return;
            }
            // Skyfaller_DeadlifeICBM.Tick calls Utils.ThrowSmoke every tick; it checks GenView.ShouldSpawnMotesAt
            // (depends on each PC's camera/map) before four Rand.Range calls, so the Rand streams split.
            PatchingUtilities.PatchPushPopRand("VanillaQuestsExpandedDeadlife.Utils:ThrowSmoke");
            Log.Message("[MPCompat - VQE Deadlife] row 496: ThrowSmoke Rand push/pop on 1 methods");
        }
    }
}
