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

            // FIXES row 461 (26 Nov 2026): the shield radius buttons and the missile silo yield / cancel buttons write sim state in
            // their click lambdas on the clicking PC only (targetRadius is read by CompTick, WarheadYield / launchPhase by the silo).
            // The lambdas capture only "this", so they are plain instance methods of the Thing / ThingComp: sync them directly.
            if (LabOff.Row(461))
                Log.Message("[MPCompat - Rimatomics] row 461 switched off by the lab, shield radius and silo buttons not synced");
            else
                PatchGizmoLambdas();
        }

        private static void PatchGizmoLambdas()
        {
            var shield = AccessTools.TypeByName("Rimatomics.CompRimatomicsShield");
            var silo = AccessTools.TypeByName("Rimatomics.MissileSilo");
            var targets = new[]
            {
                (shield, "<CompGetGizmosExtra>b__36_0", "targetRadius"),
                (shield, "<CompGetGizmosExtra>b__36_1", "targetRadius"),
                (silo, "<GetGizmos>b__58_0", "launchPhase"),
                (silo, "<GetGizmos>b__58_1", "WarheadYield"),
                (silo, "<GetGizmos>b__58_2", "WarheadYield"),
            };
            var methods = new System.Collections.Generic.List<System.Reflection.MethodInfo>();
            foreach (var (type, name, field) in targets)
            {
                var method = type == null ? null : AccessTools.DeclaredMethod(type, name);
                if (method == null || method.IsStatic || method.ReturnType != typeof(void) || method.GetParameters().Length != 0
                    || !WritesField(method, field))
                {
                    Log.Error($"[MPCompat - Rimatomics] row 461: {type?.Name}.{name} (writes {field}) not found, that button can desync");
                    continue;
                }
                methods.Add(method);
            }
            foreach (var method in methods)
                MP.RegisterSyncMethod(method);
            Log.Message($"[MPCompat - Rimatomics] row 461: {methods.Count} of {targets.Length} shield / silo button lambdas synced");
        }

        // true when the method's IL has a stfld (or a call to the property setter) for the named member
        private static bool WritesField(System.Reflection.MethodInfo method, string member)
        {
            var il = method.GetMethodBody()?.GetILAsByteArray();
            if (il == null) return false;
            var module = method.Module;
            for (int i = 0; i < il.Length - 4; i++)
            {
                if (il[i] != 0x7D) continue; // stfld
                try
                {
                    if (module.ResolveField(System.BitConverter.ToInt32(il, i + 1), method.DeclaringType.GetGenericArguments(), null).Name == member)
                        return true;
                }
                catch { }
            }
            return false;
        }

        private static bool PreWorldComponentUpdate()
        {
            // lab switch row 90 (package G, 17 Nov): off = old behaviour
            if (LabOff.Row(90)) return true;
            return !MP.IsInMultiplayer;
        }

        private static void PostWorldComponentTick(WorldComponent __instance)
        {
            // lab switch row 90 (package G, 17 Nov): off = old behaviour
            if (LabOff.Row(90) || !MP.IsInMultiplayer)
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
