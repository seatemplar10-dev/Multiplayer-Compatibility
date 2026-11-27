using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Vanilla Quests Expanded - Cryptoforge</summary>
    [MpCompatFor("vanillaquestsexpanded.cryptoforge")]
    class VanillaQuestsExpandedCryptoforge
    {
        public VanillaQuestsExpandedCryptoforge(ModContentPack mod)
        {
            if (!LabOff.Row(498)) // row 498: camera-gated Rand (GenView.ShouldSpawnMotesAt) differs per PC
            {
                PatchingUtilities.PatchPushPopRand("VanillaQuestsExpandedCryptoforge.Utils:ThrowExtendedAirPuffUp");
                Log.Message("[MPCompat - VQE Cryptoforge] row 498: 1 camera-gated Rand methods push/pop");
            }
        }
    }
}
