using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Moody by GiantSpaceHamster</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=826998327"/>
    [MpCompatFor("GiantSpaceHamster.Moody")]
    public class Moody
    {
        // Lab rows 75/76 (1.7 lab mp72/mp73): Moody's window (MoodyWindow.WindowUpdate -> DataCache.UpdateCache -> ... ->
        // Need.CurInstantLevelPercentage) reads Need_Mood.CurInstantLevel every frame, which fills its per-tick cache
        // (lastInstantMood @ lastInstantMoodCheckTick) from the interface. The simulation is then served that value in the same
        // tick, while a PC without the window open (the headless arbiter, a player who closed it) computes it fresh -> moods
        // differ -> inspiration and mental break rolls split. In MP an interface read leaves the cache as it was.
        private static readonly AccessTools.FieldRef<Need_Mood, int> lastTick = AccessTools.FieldRefAccess<Need_Mood, int>("lastInstantMoodCheckTick");
        private static readonly AccessTools.FieldRef<Need_Mood, float> lastValue = AccessTools.FieldRefAccess<Need_Mood, float>("lastInstantMood");

        public Moody(ModContentPack mod)
        {
            MpCompat.harmony.Patch(AccessTools.PropertyGetter(typeof(Need_Mood), nameof(Need_Mood.CurInstantLevel)),
                prefix: new HarmonyMethod(typeof(Moody), nameof(PreCurInstantLevel)),
                postfix: new HarmonyMethod(typeof(Moody), nameof(PostCurInstantLevel)));
        }

        private static void PreCurInstantLevel(Need_Mood __instance, out (bool restore, int tick, float value) __state)
        {
            __state = MP.IsInMultiplayer && MP.InInterface
                ? (true, lastTick(__instance), lastValue(__instance))
                : (false, 0, 0f);
        }

        private static void PostCurInstantLevel(Need_Mood __instance, (bool restore, int tick, float value) __state)
        {
            if (!__state.restore) return;
            lastTick(__instance) = __state.tick;
            lastValue(__instance) = __state.value;
        }
    }
}
