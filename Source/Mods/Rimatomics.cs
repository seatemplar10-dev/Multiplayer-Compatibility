using HarmonyLib;
using Multiplayer.API;
using RimWorld.Planet;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Rimatomics by Dubwise</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=1127530465"/>
    /// <remarks>
    /// 1.7 lab (FIXES row 90): research steps and projects complete from GameComp_RimatomicsResearch.WorldComponentUpdate, i.e.
    /// once per rendered FRAME (a counter to 2,000 frames, or a "notify" flag), so every PC completes a project (and clears the
    /// bench's current project) at a different tick; a headless arbiter renders no frames at the game's rate at all. Found by the
    /// lab state hash: two identical headless replays already had the frame counter at 76 vs 75.
    /// Fix: in MP the frame update does nothing; the same check runs from the world TICK every 2,000 ticks, or on the next tick
    /// after "notify" was set (by the simulation, same as the mod does).
    /// </remarks>
    [MpCompatFor("Dubwise.Rimatomics")]
    public class Rimatomics
    {
        private static AccessTools.FieldRef<object, bool> notifyField;
        private static FastInvokeHandler checkAllStepsAllBenches;

        public Rimatomics(ModContentPack mod)
        {
            var baseType = AccessTools.TypeByName("Rimatomics.GameComp_RimatomicsResearch");
            var type = AccessTools.TypeByName("Rimatomics.RimatomicsResearch");
            notifyField = AccessTools.FieldRefAccess<bool>(baseType, "notify");
            checkAllStepsAllBenches = MethodInvoker.GetHandler(AccessTools.DeclaredMethod(baseType, "CheckAllStepsAllBenches"));

            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(baseType, nameof(WorldComponent.WorldComponentUpdate)),
                prefix: new HarmonyMethod(typeof(Rimatomics), nameof(PreWorldComponentUpdate)));
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(type, nameof(WorldComponent.WorldComponentTick)),
                postfix: new HarmonyMethod(typeof(Rimatomics), nameof(PostWorldComponentTick)));
        }

        private static bool PreWorldComponentUpdate() => !MP.IsInMultiplayer;

        private static void PostWorldComponentTick(WorldComponent __instance)
        {
            if (!MP.IsInMultiplayer)
                return;

            ref var notify = ref notifyField(__instance);
            if (notify || Find.TickManager.TicksGame % 2000 == 0)
            {
                notify = false;
                checkAllStepsAllBenches(__instance);
            }
        }
    }
}
