using HarmonyLib;
using Multiplayer.API;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>FIXES 569F (28 Nov 2026). As Above So Below 2: in multiplayer the band climate reads only the map snapshot
    /// (ABBandMap.climateSky/climateDeep/climateWind), never local mod settings or the built-in fallbacks, so host and arbiter agree.
    /// Outside multiplayer the original methods run. No Rand, no settings writes.</summary>
    [MpCompatFor("astryl.AsAboveSoBelow2")]
    internal class AsAboveSoBelow2Climate569
    {
        static FieldInfo fSky, fDeep, fWind, fBanded;
        static PropertyInfo pBanded;
        static MethodInfo compOf;
        static float[] defaultWind;

        public AsAboveSoBelow2Climate569(ModContentPack mod)
        {
            try
            {
                var env = AccessTools.TypeByName("AsAboveSoBelow.ABBandEnv");
                var bandMap = AccessTools.TypeByName("AsAboveSoBelow.ABBandMap");
                var life = AccessTools.TypeByName("AsAboveSoBelow.ABPatchLifecycle");
                var bands = AccessTools.TypeByName("AsAboveSoBelow.ABBands");
                var tOff = env == null || bandMap == null ? null : AccessTools.Method(env, "TempOffsetForLevel", new[] { bandMap, typeof(int) });
                var any = env == null || bandMap == null ? null : AccessTools.Method(env, "AnyOffsetConfigured", new[] { bandMap });
                var wind = env == null || bandMap == null ? null : AccessTools.Method(env, "WindFactorForLevel", new[] { bandMap, typeof(int) });
                var demand = life == null ? null : AccessTools.Method(life, "DemandExists");
                fSky = bandMap == null ? null : AccessTools.Field(bandMap, "climateSky");
                fDeep = bandMap == null ? null : AccessTools.Field(bandMap, "climateDeep");
                fWind = bandMap == null ? null : AccessTools.Field(bandMap, "climateWind");
                pBanded = bandMap == null ? null : AccessTools.Property(bandMap, "Banded");
                compOf = bands == null ? null : AccessTools.Method(bands, "CompOf", new[] { typeof(Map) });
                defaultWind = env == null ? null : AccessTools.Field(env, "DefaultSkyWindFactors")?.GetValue(null) as float[];

                if (tOff == null || any == null || wind == null || demand == null || fSky == null || fDeep == null || fWind == null
                    || pBanded == null || compOf == null || defaultWind == null)
                {
                    Log.Error($"[MPCompat - FULL1127 569F] AsAboveSoBelow2 climate: target missing (TempOffsetForLevel={tOff != null} AnyOffsetConfigured={any != null} WindFactorForLevel={wind != null} DemandExists={demand != null} fields={fSky != null}/{fDeep != null}/{fWind != null} Banded={pBanded != null} CompOf={compOf != null} defaultWind={defaultWind != null})");
                    return;
                }

                var h = MpCompat.harmony;
                h.Patch(tOff, prefix: new HarmonyMethod(typeof(AsAboveSoBelow2Climate569), nameof(PreTemp)));
                h.Patch(any, prefix: new HarmonyMethod(typeof(AsAboveSoBelow2Climate569), nameof(PreAny)));
                h.Patch(wind, prefix: new HarmonyMethod(typeof(AsAboveSoBelow2Climate569), nameof(PreWind)));
                h.Patch(demand, prefix: new HarmonyMethod(typeof(AsAboveSoBelow2Climate569), nameof(PreDemand)));
                Log.Message("[MPCompat - FULL1127 569F] AsAboveSoBelow2: band climate reads map snapshot in multiplayer");
            }
            catch (Exception e)
            {
                Log.Error("[MPCompat - FULL1127 569F] AsAboveSoBelow2 climate setup failed: " + e);
            }
        }

        static float Table(IList list, float[] fallback, int index)
        {
            int n = list != null && list.Count > 0 ? list.Count : fallback.Length;
            float At(int i) => list != null && list.Count > 0 ? (float)list[i] : fallback[i];
            if (index < n) return At(index);
            float last = At(n - 1);
            float prev = n < 2 ? last : At(n - 2);
            return last + (last - prev) * (index - n + 1);
        }

        static bool PreTemp(object bands, int level, ref float __result)
        {
            if (!MP.IsInMultiplayer) return true;
            if (level == 0) { __result = 0f; return false; }
            var list = bands == null ? null : (level > 0 ? fSky : fDeep).GetValue(bands) as IList;
            if (list == null || list.Count == 0) { __result = 0f; return false; }
            __result = Table(list, null, (level > 0 ? level : -level) - 1);
            return false;
        }

        static bool PreAny(object bands, ref bool __result)
        {
            if (!MP.IsInMultiplayer) return true;
            __result = bands != null && (NonZero(fSky.GetValue(bands) as IList) || NonZero(fDeep.GetValue(bands) as IList));
            return false;
        }

        static bool NonZero(IList list)
        {
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++) if ((float)list[i] != 0f) return true;
            return false;
        }

        static bool PreWind(object bands, int level, ref float __result)
        {
            if (!MP.IsInMultiplayer) return true;
            if (level == 0) { __result = 1f; return false; }
            if (level < 0) { __result = 0f; return false; }
            __result = Table(bands == null ? null : fWind.GetValue(bands) as IList, defaultWind, level - 1);
            return false;
        }

        static bool PreDemand(ref bool __result)
        {
            if (!MP.IsInMultiplayer) return true;
            __result = false;
            if (Current.Game == null) return false;
            var maps = Find.Maps;
            if (maps == null) return false;
            for (int i = 0; i < maps.Count; i++)
            {
                var b = compOf.Invoke(null, new object[] { maps[i] });
                if (b != null && (bool)pBanded.GetValue(b) && (NonZero(fSky.GetValue(b) as IList) || NonZero(fDeep.GetValue(b) as IList)))
                {
                    __result = true;
                    break;
                }
            }
            return false;
        }
    }
}
