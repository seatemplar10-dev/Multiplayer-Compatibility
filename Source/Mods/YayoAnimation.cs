using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Yayo's Animation (Continued)</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=2877292196"/>
    [MpCompatFor("com.yayo.yayoAni.continued")]
    public class YayoAnimation
    {
        // FIXES row 251 (27 Oct 2026, lab rgC6i_1): the mod adds its animation offset to AttachableThing.DrawPos. The offset is
        // written by the draw path, only for a pawn the PC draws, and the tick reads DrawPos too: a burning pawn's
        // Pawn_RotationTracker.UpdateRotation faces its own Fire (Face(fire.DrawPos)), so the PC that drew the pawn turned it
        // (Ork_Boy33070 rot 3 on the host only). In MP the offset applies while drawing only, never in the simulation.
        public YayoAnimation(ModContentPack mod)
        {
            var offset = AccessTools.DeclaredMethod("YayoAnimation.HarmonyPatches.OffsetAttachableThing:Postfix");
            if (offset == null)
                Log.Warning("[MPCompat - Yayo's Animation] OffsetAttachableThing.Postfix not found: a burning pawn's facing may differ between PCs (FIXES row 251)");
            else
                MpCompat.harmony.Patch(offset, prefix: new HarmonyMethod(typeof(YayoAnimation), nameof(PreOffsetAttachableThing)));
        }

        private static bool PreOffsetAttachableThing() => !MP.IsInMultiplayer || MP.InInterface;
    }
}
