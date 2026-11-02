using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Vanilla Factions Expanded - Medieval by Oskar Potocki, XeoNovaDan</summary>
    /// <see href="https://github.com/Vanilla-Expanded/VanillaFactionsExpanded-Medieval"/>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=2023513450"/>
    [MpCompatFor("OskarPotocki.VanillaFactionsExpanded.MedievalModule")]
    public class VanillaFactionsMedieval
    {
        // This module is for the original Medieval mod only. The rewrite VFE-Medieval 2 (OskarPotocki.VFE.Medieval2) has the same
        // VFEMedieval.JobDriver_PlayArchery:ThrowObjectAt and is handled in VanillaFactionsMedieval2.cs, which patches that method itself.
        // Do not add the Medieval 2 id to this module: both modules would then load for Medieval 2 and patch that one method twice.

        // Archery target uses RNG - but only when the player is looking at it. Which may not be the case in MP for all players.
        public VanillaFactionsMedieval(ModContentPack mod)
            => PatchingUtilities.PatchPushPopRand("VFEMedieval.JobDriver_PlayArchery:ThrowObjectAt");
    }
}