using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>
    /// FULL1127 class G wave G1: per-PC cache phases and lazily made things in sim code.
    /// Each cache stamp is re-derived from an absolute tick bucket (TicksGame / N) right before the mod reads it,
    /// so host, joiner and reloaded PCs rebuild on the same tick.
    /// </summary>
    internal static class FullG1
    {
        public const string Tag = "[MPCompat - FULL1127 G1] ";
        static readonly Dictionary<string, long> last = new Dictionary<string, long>();
        static readonly Dictionary<string, FieldInfo> fields = new Dictionary<string, FieldInfo>();

        public static int Tick => Find.TickManager?.TicksGame ?? 0;

        static FieldInfo F(string spec)
        {
            if (!fields.TryGetValue(spec, out var f))
                fields[spec] = f = AccessTools.Field(spec);
            return f;
        }

        /// <summary>Sets a "last built" stamp so the mod's own interval test rebuilds exactly when the absolute bucket changed.</summary>
        public static void Align(string key, string stampField, int n, int tick, long token)
        {
            long b = (long)(tick / n) ^ (token << 40);
            bool changed = !last.TryGetValue(key, out var old) || old != b;
            last[key] = b;
            F(stampField)?.SetValue(null, changed ? tick - n - 1 : tick); // -1: strict ">" tests (alliesarehelpful) must rebuild too
        }

        public static bool Patch(string spec, string prefix, string what)
        {
            var m = AccessTools.Method(spec);
            if (m == null)
            {
                Log.Error(Tag + what + ": method " + spec + " not found");
                return false;
            }
            MpCompat.harmony.Patch(m, prefix: new HarmonyMethod(typeof(FullG1), prefix));
            return true;
        }

        // worksitesexpanded
        public static void SackLoot() => Align("sack", "MiningOutpost.Defense.SackLootCache:cachedTick", 180, Tick, 0);
        public static void DigLoot() => Align("dig", "MiningOutpost.WorksiteDigInPosts:lootCacheStamp", 1200, Tick, 0);
        public static void Merc() => Align("merc", "MiningOutpost.MercenaryCombatStance:cacheStamp", 250, Tick, 0);
        public static void Gating() => Align("gate", "MiningOutpost.OrbitalPlatformGating:lastEngineCheckTick", 2500, Tick, 0);
        public static void Scope() => Align("scope", "MiningOutpost.Orbital.OrbitalCombatScope:shipCellsTick", 60, Tick, 0);
        // moremechanoidsworkmodes
        public static void Charger() => Align("charger", "WVC_WorkModes.JobGiver_GetEnergy_Charger:checkDelayTick", 5000, Tick, 0);
        // apexmechanoids (CleanupShieldTargetMemory(int ticksGame))
        public static void Tinker(int ticksGame) => Align("tinker", "ApexMechanoids.JobGiver_AITinkerCombat:lastShieldTargetMemoryCleanupTick", 2500, ticksGame, 0);

        // asabovesobelow2
        public static bool AbLifecycle()
        {
            int t = Tick;
            if (!MP.IsInMultiplayer) return true;
            if (t % 250 != 0) return false;
            F("AsAboveSoBelow.ABPatchLifecycle:nextSweepTick")?.SetValue(null, t);
            return true;
        }

        public static bool AbBandSnow()
        {
            int t = Tick;
            if (!MP.IsInMultiplayer) return true;
            if (t % 250 != 0) return false;
            F("AsAboveSoBelow.ABBandSnowPatchLifetime:lastCheckTick")?.SetValue(null, -1);
            return true;
        }

        // alliesarehelpful: build the caches from the pawn's own map, on absolute buckets
        static FieldInfo mapIndexField;
        public static void TendPre(Pawn __instance, out int __state)
        {
            __state = -2;
            var map = __instance?.Map;
            if (map == null || Current.Game == null) return;
            long token = map.uniqueID + 1;
            Align("tend", "PawnTendAndRescuePatch:_lastCacheUpdateTick", 1100, Tick, token);
            Align("tendEnemy", "PawnTendAndRescuePatch:_lastEnemyCacheUpdateTick", 1000, Tick, token);
            mapIndexField = mapIndexField ?? AccessTools.Field(typeof(Game), "currentMapIndex");
            if (mapIndexField == null) return;
            __state = (sbyte)mapIndexField.GetValue(Current.Game);
            mapIndexField.SetValue(Current.Game, (sbyte)map.Index);
        }

        public static void TendFin(int __state)
        {
            if (__state != -2 && mapIndexField != null && Current.Game != null)
                mapIndexField.SetValue(Current.Game, (sbyte)__state);
        }

        // manipulatorbeam: the lazy ThingMaker.MakeThing pawn would take a thing id on one PC only
        public static bool BeamPawn(ref Pawn __result)
        {
            if (!MP.IsInMultiplayer) return true;
            __result = null;
            return false;
        }
    }

    [MpCompatFor("godsfathermixtape.worksitesexpanded")]
    class FullG1Worksites
    {
        public FullG1Worksites(ModContentPack mod)
        {
            int n = 0;
            if (FullG1.Patch("MiningOutpost.Defense.SackLootCache:Get", nameof(FullG1.SackLoot), "Worksites")) n++;
            if (FullG1.Patch("MiningOutpost.WorksiteDigInPosts:BuildLootDistance", nameof(FullG1.DigLoot), "Worksites")) n++;
            if (FullG1.Patch("MiningOutpost.MercenaryCombatStance:IsAggressiveHunter", nameof(FullG1.Merc), "Worksites")) n++;
            if (FullG1.Patch("MiningOutpost.OrbitalPlatformGating:AnyPlayerHomeHasGravEngine", nameof(FullG1.Gating), "Worksites")) n++;
            if (FullG1.Patch("MiningOutpost.Orbital.OrbitalCombatScope:IsStationInterior", nameof(FullG1.Scope), "Worksites")) n++;
            Log.Message(FullG1.Tag + "worksitesexpanded: " + n + " of 5 cache phases aligned to absolute tick buckets registered");
        }
    }

    [MpCompatFor("wvc.sergkart.biotech.moremechanoidsworkmodes")]
    class FullG1MechWorkModes
    {
        public FullG1MechWorkModes(ModContentPack mod)
        {
            bool ok = FullG1.Patch("WVC_WorkModes.JobGiver_GetEnergy_Charger:AnyMechWithLowEnergyOnSameMap", nameof(FullG1.Charger), "MechWorkModes");
            Log.Message(FullG1.Tag + "moremechanoidsworkmodes: charger check cache bucket " + (ok ? "registered" : "FAILED"));
        }
    }

    [MpCompatFor("mortis.apexmechanoids")]
    class FullG1Apex
    {
        public FullG1Apex(ModContentPack mod)
        {
            bool ok = FullG1.Patch("ApexMechanoids.JobGiver_AITinkerCombat:CleanupShieldTargetMemory", nameof(FullG1.Tinker), "Apex");
            Log.Message(FullG1.Tag + "apexmechanoids: tinker memory cleanup bucket " + (ok ? "registered" : "FAILED"));
        }
    }

    [MpCompatFor("astryl.AsAboveSoBelow2")]
    class FullG1AsAbove
    {
        public FullG1AsAbove(ModContentPack mod)
        {
            int n = 0;
            if (FullG1.Patch("AsAboveSoBelow.ABPatchLifecycle:Tick", nameof(FullG1.AbLifecycle), "AsAbove")) n++;
            if (FullG1.Patch("AsAboveSoBelow.ABBandSnowPatchLifetime:Tick", nameof(FullG1.AbBandSnow), "AsAbove")) n++;
            Log.Message(FullG1.Tag + "asabovesobelow2: " + n + " of 2 patch-lifetime ticks gated on TicksGame % 250 registered");
        }
    }

    [MpCompatFor("ninagoblin.alliesarehelpful")]
    class FullG1AlliesHelpful
    {
        public FullG1AlliesHelpful(ModContentPack mod)
        {
            var m = AccessTools.Method("PawnTendAndRescuePatch:Postfix");
            if (m == null) { Log.Error(FullG1.Tag + "alliesarehelpful: PawnTendAndRescuePatch.Postfix not found"); return; }
            MpCompat.harmony.Patch(m, prefix: new HarmonyMethod(typeof(FullG1), nameof(FullG1.TendPre)), finalizer: new HarmonyMethod(typeof(FullG1), nameof(FullG1.TendFin)));
            Log.Message(FullG1.Tag + "alliesarehelpful: tend/rescue caches use the pawn map and absolute buckets registered");
        }
    }

    [MpCompatFor("natsuki.manipulatorbeam")]
    class FullG1ManipulatorBeam
    {
        public FullG1ManipulatorBeam(ModContentPack mod)
        {
            var m = AccessTools.PropertyGetter("BeamConstructor.BeamConstructorMachineOperator:Pawn");
            if (m == null) { Log.Error(FullG1.Tag + "manipulatorbeam: BeamConstructorMachineOperator.Pawn getter not found"); return; }
            MpCompat.harmony.Patch(m, prefix: new HarmonyMethod(typeof(FullG1), nameof(FullG1.BeamPawn)));
            Log.Message(FullG1.Tag + "manipulatorbeam: lazy operator pawn skipped in multiplayer registered");
        }
    }
}
