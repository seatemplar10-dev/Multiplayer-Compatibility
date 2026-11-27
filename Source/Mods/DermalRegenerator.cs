using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Dermal Regenerator by rselbo</summary>
    [MpCompatFor("rselbo.dermalregenerator.1.0.0")]
    class DermalRegenerator
    {
        public DermalRegenerator(ModContentPack mod)
        {
            if (!LabOff.Row(498)) // row 498: camera-gated Rand (GenView.ShouldSpawnMotesAt) differs per PC
            {
                PatchingUtilities.PatchPushPopRand("DermalRegenerator.Building_DermalRegeneratorNew:ThrowParticles");
                Log.Message("[MPCompat - Dermal Regenerator] row 498: 1 camera-gated Rand methods push/pop");
            }
        }
    }
}
