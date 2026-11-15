using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>RimSaves by aRandomKiwi</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=1713367505"/>
    /// <remarks>
    /// FIXES row 61. Written against the decompiled 1.6 RimSaves.dll that the 1.7 co-op loads.
    /// <c>ReceiveLetter_Patch.Listener</c> (Harmony postfix of <c>LetterStack.ReceiveLetter</c>, simulation) quicksaves when a
    /// negative or positive incident letter arrives, if the per-PC options "quicksave on negative/positive incident" are on and
    /// the wall-clock throttle (<c>DateTime.UtcNow</c> seconds) allows it. <c>quicksave</c> queues
    /// <c>GameDataSaveLoader.SaveGame</c>, so one PC runs a full save (every Saving-mode ExposeData) that the others do not.
    /// Fix: the listener does nothing in MP. Multiplayer's own autosave and join points keep saving. Not run in game yet.
    /// </remarks>
    [MpCompatFor("arandomkiwi.rimsaves")]
    public class RimSaves
    {
        public RimSaves(ModContentPack mod)
        {
            // RimSaves' own Harmony postfix of LetterStack.ReceiveLetter; patching the postfix method itself skips it in MP.
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod("aRandomKiwi.ARS.ReceiveLetter_Patch:Listener"),
                prefix: new HarmonyMethod(typeof(RimSaves), nameof(PreListener)));
        }

        private static bool PreListener() => !MP.IsInMultiplayer;
    }
}
