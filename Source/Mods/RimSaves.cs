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
    /// Fix-up 2: the hotkey path has the same problem. <c>GCQS.GameComponentUpdate</c> (per-PC input, Ctrl+Home / Ctrl+End)
    /// calls <c>GCQS.quicksave</c> (queues <c>GameDataSaveLoader.SaveGame</c>) and <c>GCQS.quickload</c> (loads a save) on
    /// the pressing PC only. Both do nothing in MP. The listener also calls <c>quicksave</c>, so that path is now gated twice.
    /// </remarks>
    [MpCompatFor("arandomkiwi.rimsaves")]
    public class RimSaves
    {
        public RimSaves(ModContentPack mod)
        {
            // RimSaves' own Harmony postfix of LetterStack.ReceiveLetter; patching the postfix method itself skips it in MP.
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod("aRandomKiwi.ARS.ReceiveLetter_Patch:Listener"),
                prefix: new HarmonyMethod(typeof(RimSaves), nameof(PreListener)));

            // Hotkey quicksave and quickload (GCQS.GameComponentUpdate, per-PC key input); skipped in MP.
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod("aRandomKiwi.ARS.GCQS:quicksave"),
                prefix: new HarmonyMethod(typeof(RimSaves), nameof(PreQuickSaveLoad)));
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod("aRandomKiwi.ARS.GCQS:quickload"),
                prefix: new HarmonyMethod(typeof(RimSaves), nameof(PreQuickSaveLoad)));
        }

        private static bool PreListener() => !MP.IsInMultiplayer;

        private static bool PreQuickSaveLoad() => !MP.IsInMultiplayer;
    }
}
