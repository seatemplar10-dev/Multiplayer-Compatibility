using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>FIXES 569P probe (28 Nov 2026). Always on (picked up by MpCompat's harmony.PatchAll, no mod gate).
    /// Logs the Rand iteration count around the vanilla methods whose Rand calls MP does not trace, and around Map.MapPostTick,
    /// only while TicksGame is 6200-6300. Reads Rand.StateCompressed and Rand.stateStack only; never calls Rand, never changes state.</summary>
    [HarmonyPatch]
    internal static class FullCust569P
    {
        static readonly bool arbiter = Environment.GetCommandLineArgs().Any(a => a == "-arbiter");
        static readonly string side = arbiter ? "arbiter" : "host";
        static bool errLogged;
        static readonly FieldInfo stackF = AccessTools.Field(typeof(Rand), "stateStack");
        static readonly Dictionary<Type, FieldInfo> mapFields = new Dictionary<Type, FieldInfo>();
        // As Above So Below 2 statics (null when the mod is absent)
        static FieldInfo pendingF, lastCheckF, nextSweepF;

        static readonly string[] Targets =
        {
            "RimWorld.WildAnimalSpawner:WildAnimalSpawnerTick",
            "RimWorld.WildPlantSpawner:WildPlantSpawnerTick",
            "RimWorld.SteadyEnvironmentEffects:SteadyEnvironmentEffectsTick",
            "RimWorld.TempTerrainManager:Tick",
            "RimWorld.IntermittentSteamSprayer:SteamSprayerTick",
            "Verse.IntermittentSteamSprayer:SteamSprayerTick",
            "Verse.Map:MapPostTick",
        };

        static IEnumerable<MethodBase> TargetMethods()
        {
            try
            {
                pendingF = AccessTools.Field("AsAboveSoBelow.ABBandedGeneration:pending");
                lastCheckF = AccessTools.Field("AsAboveSoBelow.ABBandSnowPatchLifetime:lastCheckTick");
                nextSweepF = AccessTools.Field("AsAboveSoBelow.ABPatchLifecycle:nextSweepTick");
            }
            catch (Exception e) { Err(e); }
            var seen = new HashSet<MethodBase>();
            foreach (var t in Targets)
            {
                MethodBase m = null;
                try { m = AccessTools.Method(t); } catch (Exception e) { Err(e); }
                if (m == null)
                {
                    Log.Message("[569P] target not found (skipped): " + t);
                    continue;
                }
                if (seen.Add(m)) yield return m;
            }
        }

        static void Err(Exception e)
        {
            if (errLogged) return;
            errLogged = true;
            try { Log.Warning("[569P] probe error (logged once): " + e); } catch { }
        }

        static int MapId(object inst)
        {
            if (inst is Map m) return m.uniqueID;
            if (inst == null) return -1;
            var t = inst.GetType();
            FieldInfo f;
            if (!mapFields.TryGetValue(t, out f))
            {
                f = AccessTools.Field(t, "map");
                if (f == null || !typeof(Map).IsAssignableFrom(f.FieldType))
                {
                    // IntermittentSteamSprayer keeps its Thing in "parent"
                    f = AccessTools.Field(t, "parent");
                }
                mapFields[t] = f;
            }
            if (f == null) return -1;
            var v = f.GetValue(inst);
            if (v is Map mm) return mm.uniqueID;
            if (v is Thing th) return th.Map != null ? th.Map.uniqueID : -1;
            return -1;
        }

        static void Emit(object inst, MethodBase orig, string phase)
        {
            try
            {
                var tm = Find.TickManager;
                if (tm == null) return;
                int tick = tm.TicksGame;
                if (tick < 6200 || tick > 6300) return;
                ulong sc = Rand.StateCompressed;
                string depth = "?";
                if (stackF != null)
                {
                    var st = stackF.GetValue(null) as System.Collections.ICollection;
                    if (st != null) depth = st.Count.ToString();
                }
                Log.Message("[569P] side=" + side + " map=" + MapId(inst) + " tick=" + tick + " " + orig.DeclaringType.Name + "." + orig.Name
                    + " " + phase + " iter=" + (uint)(sc >> 32) + " seed=" + (uint)(sc & 0xFFFFFFFF) + " stack=" + depth);
                if (phase == "pre" && inst is Map)
                {
                    if (pendingF != null || lastCheckF != null || nextSweepF != null)
                        Log.Message("[569P] side=" + side + " map=" + MapId(inst) + " tick=" + tick + " AASB2 pending="
                            + (pendingF == null ? "n/a" : (pendingF.GetValue(null) == null ? "null" : "set"))
                            + " lastCheckTick=" + (lastCheckF == null ? "n/a" : lastCheckF.GetValue(null))
                            + " nextSweepTick=" + (nextSweepF == null ? "n/a" : nextSweepF.GetValue(null)));
                }
            }
            catch (Exception e) { Err(e); }
        }

        [HarmonyPriority(Priority.First)]
        static void Prefix(object __instance, MethodBase __originalMethod) => Emit(__instance, __originalMethod, "pre");

        [HarmonyPriority(Priority.Last)]
        static void Postfix(object __instance, MethodBase __originalMethod) => Emit(__instance, __originalMethod, "post");
    }
}
