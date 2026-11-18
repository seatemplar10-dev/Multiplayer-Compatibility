using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Multiplayer.API;
using RimWorld.Planet;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>FIXES row 424: vanilla WorldComponent_LocationGenerator takes its tick schedule from an object identity hash.</summary>
    /// <remarks>
    /// Status: [unverified] compiled and IL-tested only, not run in game.
    /// <para>
    /// Cause. WorldComponent_LocationGenerator.WorldComponentTick does, for every planet layer:
    /// "if (GenTicks.IsTickInterval(planetLayer2.GetHashCode().HashOffset(), 90000))" and then, when the layer has fewer generated
    /// locations than its target, GenerateUntilTarget(layer) (new world objects, Rand calls, world object ids). PlanetLayer has no
    /// GetHashCode override, so the value is the object identity hash: a different number in every process. The tick (one in 90000)
    /// on which a layer is topped up is then different on every PC.
    /// </para>
    /// <para>
    /// Fix. A transpiler swaps that one "callvirt Object.GetHashCode" for <see cref="StableKey"/>. In a multiplayer game the key is
    /// PlanetLayer.LayerID (saved with the world as "layerId", the same on every PC). Outside multiplayer the identity hash is
    /// returned as before, so single player is unchanged. If the call is not found exactly once, the method is left untouched.
    /// </para>
    /// </remarks>
    internal static class LocationGeneratorStableSchedule
    {
        private const string LogPrefix = "[MPCompat] row 424: ";
        private static bool installed;

        public static void Install(Harmony harmony)
        {
            if (installed) return;
            var target = AccessTools.Method(typeof(WorldComponent_LocationGenerator), nameof(WorldComponent_LocationGenerator.WorldComponentTick), new System.Type[0]);
            if (target == null)
            {
                Log.Warning(LogPrefix + "WorldComponent_LocationGenerator.WorldComponentTick not found, schedule not fixed.");
                return;
            }
            harmony.Patch(target, transpiler: new HarmonyMethod(typeof(LocationGeneratorStableSchedule), nameof(Transpiler)));
            installed = true;
        }

        /// <summary>The schedule key of a planet layer: its saved layer id in multiplayer, the identity hash otherwise.</summary>
        public static int StableKey(object layer)
        {
            if (MP.IsInMultiplayer && layer is PlanetLayer planetLayer)
                return planetLayer.LayerID;
            return layer.GetHashCode();
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            MethodInfo getHashCode = AccessTools.Method(typeof(object), nameof(object.GetHashCode), new System.Type[0]);
            MethodInfo stableKey = AccessTools.Method(typeof(LocationGeneratorStableSchedule), nameof(StableKey), new[] { typeof(object) });

            int found = 0;
            foreach (var ci in list)
                if ((ci.opcode == OpCodes.Callvirt || ci.opcode == OpCodes.Call) && ci.operand is MethodInfo m && m == getHashCode)
                    found++;

            if (found != 1)
            {
                Log.Warning(LogPrefix + "expected 1 Object.GetHashCode call in WorldComponentTick, found " + found + ". Schedule not fixed.");
                return list;
            }

            foreach (var ci in list)
            {
                if ((ci.opcode == OpCodes.Callvirt || ci.opcode == OpCodes.Call) && ci.operand is MethodInfo m && m == getHashCode)
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = stableKey;
                }
            }
            return list;
        }
    }
}
