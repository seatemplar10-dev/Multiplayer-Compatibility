using Verse;

namespace Multiplayer.Compat
{
    /// <summary>[NL] Facial Animation - WIP by Nals</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=1635901197"/>
    [MpCompatFor("Nals.FacialAnimation")]
    public class FacialAnimation
    {
        public FacialAnimation(ModContentPack mod)
        {
            // RNG
            // The first CompTick of a pawn builds its face animation dictionary: a lazy list that
            // makes a new FaceAnimation (Rand.Range in Reset) per animation def, enumerated once per JobDef.
            // ~29k Rand calls on a client that draws the pawn, none on one that does not (headless arbiter).
            // Face animation timing is cosmetic, so keep it out of the simulation's Rand stream.
            PatchingUtilities.PatchPushPopRand("FacialAnimation.FacialAnimationControllerComp:InitializeIfNeed");
        }
    }
}
