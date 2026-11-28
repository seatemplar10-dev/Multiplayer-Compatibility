using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>FIXES 569P2 probe (28 Nov 2026). Always on. Logs FreezeManager.DoCellSteadyEffects cells and temperatures in ticks 6244-6252,
    /// per-tick outdoor temp/weather/conditions, and (once, first map tick) the Harmony owners of the relevant methods.
    /// Never calls Rand, never writes state. Reads only: GetTemperature/GetRoom (RoomAt, may validate regions lazily like the game itself; no Rand),
    /// Room.Temperature/UsesOutdoorTemperature (pure), TempTerrainAt, GetTerrain, CanFreeze (reads).</summary>
    internal static class FullCust569P2Shared
    {
        internal static readonly bool arbiter = Environment.GetCommandLineArgs().Any(a => a == "-arbiter");
        internal static readonly string side = arbiter ? "arbiter" : "host";
        static bool errLogged;
        internal static FieldInfo ttmCycleF, fmMapF;
        internal static MethodInfo canFreezeM;

        internal static void Err(Exception e)
        {
            if (errLogged) return;
            errLogged = true;
            try { Log.Warning("[569P2] probe error (logged once): " + e); } catch { }
        }

        internal static bool InWindow(out int tick)
        {
            tick = 0;
            var tm = Find.TickManager;
            if (tm == null) return false;
            tick = tm.TicksGame;
            return tick >= 6244 && tick <= 6252;
        }
    }

    [HarmonyPatch]
    internal static class FullCust569P2
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            MethodBase a = null;
            try
            {
                a = AccessTools.Method("Verse.FreezeManager:DoCellSteadyEffects");
                FullCust569P2Shared.ttmCycleF = AccessTools.Field("RimWorld.TempTerrainManager:cycleIndex");
                FullCust569P2Shared.fmMapF = AccessTools.Field("Verse.FreezeManager:map");
                FullCust569P2Shared.canFreezeM = AccessTools.Method("Verse.FreezeManager:CanFreeze");
            }
            catch (Exception e) { FullCust569P2Shared.Err(e); }
            if (a == null) Log.Message("[569P2] target not found: Verse.FreezeManager:DoCellSteadyEffects");
            else yield return a;
        }

        [HarmonyPriority(Priority.First)]
        static void Prefix(out ulong __state)
        {
            __state = 0;
            try { __state = Rand.StateCompressed; }
            catch (Exception e) { FullCust569P2Shared.Err(e); }
        }

        [HarmonyPriority(Priority.Last)]
        static void Postfix(object __instance, IntVec3 c, ulong __state)
        {
            try
            {
                int tick;
                if (!FullCust569P2Shared.InWindow(out tick)) return;
                var map = FullCust569P2Shared.fmMapF?.GetValue(__instance) as Map;
                if (map == null) return;
                ulong post = Rand.StateCompressed;
                int cycle = -1;
                if (FullCust569P2Shared.ttmCycleF != null && map.tempTerrain != null)
                    cycle = (int)FullCust569P2Shared.ttmCycleF.GetValue(map.tempTerrain);
                Room room = c.GetRoom(map);
                var tt = map.terrainGrid.TempTerrainAt(c);
                bool cf = FullCust569P2Shared.canFreezeM != null && (bool)FullCust569P2Shared.canFreezeM.Invoke(__instance, new object[] { c });
                Log.Message("[569P2] side=" + FullCust569P2Shared.side + " map=" + map.uniqueID + " tick=" + tick + " cell=(" + c.x + "," + c.z + ")"
                    + " cycleIndex=" + cycle
                    + " temp=" + c.GetTemperature(map).ToString("F3")
                    + " room=" + (room != null ? room.ID : -1) + " usesOutdoor=" + (room != null ? room.UsesOutdoorTemperature.ToString() : "n/a")
                    + " terrain=" + c.GetTerrain(map).defName + " tempTerrain=" + (tt != null ? tt.defName : "none")
                    + " canFreeze=" + cf + " iterPre=" + (uint)(__state >> 32) + " iterPost=" + (uint)(post >> 32));
            }
            catch (Exception e) { FullCust569P2Shared.Err(e); }
        }
    }

    [HarmonyPatch(typeof(Map), "MapPostTick")]
    internal static class FullCust569P2Map
    {
        static bool patchesLogged;

        static string Owners(MethodBase m)
        {
            if (m == null) return "METHOD NOT FOUND";
            var info = Harmony.GetPatchInfo(m);
            if (info == null) return "none";
            var sb = new StringBuilder();
            foreach (var p in info.Prefixes) sb.Append("prefix:").Append(p.owner).Append(' ');
            foreach (var p in info.Postfixes) sb.Append("postfix:").Append(p.owner).Append(' ');
            foreach (var p in info.Transpilers) sb.Append("transpiler:").Append(p.owner).Append(' ');
            foreach (var p in info.Finalizers) sb.Append("finalizer:").Append(p.owner).Append(' ');
            return sb.Length == 0 ? "none" : sb.ToString().Trim();
        }

        static void LogPatches()
        {
            var list = new List<KeyValuePair<string, Func<MethodBase>>>
            {
                new KeyValuePair<string, Func<MethodBase>>("FreezeManager.DoCellSteadyEffects", () => AccessTools.Method("Verse.FreezeManager:DoCellSteadyEffects")),
                new KeyValuePair<string, Func<MethodBase>>("FreezeManager.DoWaterFreezing", () => AccessTools.Method("Verse.FreezeManager:DoWaterFreezing")),
                new KeyValuePair<string, Func<MethodBase>>("FreezeManager.DoIceMelting", () => AccessTools.Method("Verse.FreezeManager:DoIceMelting")),
                new KeyValuePair<string, Func<MethodBase>>("FreezeManager.CanFreeze", () => AccessTools.Method("Verse.FreezeManager:CanFreeze")),
                new KeyValuePair<string, Func<MethodBase>>("TempTerrainManager.Tick", () => AccessTools.Method("RimWorld.TempTerrainManager:Tick")),
                new KeyValuePair<string, Func<MethodBase>>("GenTemperature.GetTemperatureForCell", () => AccessTools.Method("Verse.GenTemperature:GetTemperatureForCell")),
                new KeyValuePair<string, Func<MethodBase>>("GridsUtility.GetTemperature", () => AccessTools.Method("Verse.GridsUtility:GetTemperature")),
                new KeyValuePair<string, Func<MethodBase>>("MapTemperature.OutdoorTemp getter", () => AccessTools.PropertyGetter("Verse.MapTemperature:OutdoorTemp")),
                new KeyValuePair<string, Func<MethodBase>>("MapTemperature.SeasonalTemp getter", () => AccessTools.PropertyGetter("Verse.MapTemperature:SeasonalTemp")),
                new KeyValuePair<string, Func<MethodBase>>("Room.Temperature getter", () => AccessTools.PropertyGetter("Verse.Room:Temperature")),
                new KeyValuePair<string, Func<MethodBase>>("TerrainGrid.SetTempTerrain", () => AccessTools.Method("Verse.TerrainGrid:SetTempTerrain")),
                new KeyValuePair<string, Func<MethodBase>>("TerrainGrid.RemoveTempTerrain", () => AccessTools.Method("Verse.TerrainGrid:RemoveTempTerrain")),
            };
            foreach (var kv in list)
            {
                try { Log.Message("[569P2] side=" + FullCust569P2Shared.side + " patches " + kv.Key + ": " + Owners(kv.Value())); }
                catch (Exception e) { FullCust569P2Shared.Err(e); }
            }
        }

        static string Cond(GameConditionManager gcm)
        {
            if (gcm == null) return "null";
            var l = gcm.ActiveConditions;
            if (l == null || l.Count == 0) return "none";
            return string.Join(",", l.Select(c => c.def.defName));
        }

        [HarmonyPriority(Priority.First)]
        static void Prefix(Map __instance)
        {
            try
            {
                if (!patchesLogged)
                {
                    patchesLogged = true;
                    LogPatches();
                }
                int tick;
                if (!FullCust569P2Shared.InWindow(out tick)) return;
                var m = __instance;
                Log.Message("[569P2] side=" + FullCust569P2Shared.side + " map=" + m.uniqueID + " tick=" + tick
                    + " outdoor=" + m.mapTemperature.OutdoorTemp.ToString("F3") + " season=" + m.mapTemperature.SeasonalTemp.ToString("F3")
                    + " weather=" + (m.weatherManager?.curWeather?.defName ?? "null")
                    + " conditions=" + Cond(m.gameConditionManager) + " worldConditions=" + Cond(Find.World?.gameConditionManager));
            }
            catch (Exception e) { FullCust569P2Shared.Err(e); }
        }
    }
}
