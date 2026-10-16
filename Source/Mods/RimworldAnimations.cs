using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Rimworld Animations 2.0 by c0ffee</summary>
    /// <remarks>
    /// 1.7 lab (FIXES row 30, lab run mp35, Desync-17): CompExtendedAnimator.CompTick -> CheckAndPlaySounds ->
    /// AnimationWorker_KeyframesExtended.voiceAtTick -> Rand.Chance picks a voice line from the shared simulation Rand.
    /// The windowed host drew it (sound playback), the headless arbiter did not, so the map Rand split ~8.7k MP ticks after
    /// the join ("Wrong random state on map 1"). The sound choice is cosmetic, so its draws now run inside
    /// Rand.PushState/PopState and never move the simulation stream. Not run in game yet.
    /// </remarks>
    [MpCompatFor("c0ffee.rimworld.animations")]
    public class RimworldAnimations
    {
        public RimworldAnimations(ModContentPack mod)
            => PatchingUtilities.PatchPushPopRand("Rimworld_Animations.CompExtendedAnimator:CheckAndPlaySounds");
    }
}
