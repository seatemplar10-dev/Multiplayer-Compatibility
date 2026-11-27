using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>FULL1127 class G: one reset table for plain static fields that survive a game load (a stale value from the previous game changes sim gates).
    /// Every entry is reset to the mod's own default before Game.LoadGame and before Game.InitNewGame.</summary>
    internal static class FullStaticDefaults
    {
        private static readonly List<(string typeName, string field, object value, string mod)> entries = new List<(string, string, object, string)>();
        private static bool hooked;

        /// <summary>Adds the rows of one mod and hooks Game.LoadGame / Game.InitNewGame once. Returns how many rows resolved to a real static field.</summary>
        public static int Register(string mod, (string typeName, string field, object value)[] rows)
        {
            int ok = 0;
            foreach (var (typeName, field, value) in rows)
            {
                var type = AccessTools.TypeByName(typeName);
                var fi = type == null ? null : AccessTools.Field(type, field);
                if (fi == null || !fi.IsStatic)
                {
                    Log.Error($"[MPCompat - FULL1127 G] {mod}: static field {typeName}::{field} not found");
                    continue;
                }
                entries.Add((typeName, field, value, mod));
                ok++;
            }
            if (!hooked)
            {
                hooked = true;
                var prefix = new HarmonyMethod(typeof(FullStaticDefaults), nameof(ResetAll));
                MpCompat.harmony.Patch(AccessTools.Method(typeof(Game), nameof(Game.LoadGame)), prefix: prefix);
                MpCompat.harmony.Patch(AccessTools.Method(typeof(Game), nameof(Game.InitNewGame)), prefix: prefix);
            }
            Log.Message($"[MPCompat - FULL1127 G] {mod}: {ok} of {rows.Length} static fields in the reset table");
            return ok;
        }

        /// <summary>Applies every registered reset. Public so a test can call it without a running game.</summary>
        public static void ResetAll()
        {
            foreach (var (typeName, field, value, _) in entries)
                AccessTools.Field(AccessTools.TypeByName(typeName), field)?.SetValue(null, value);
        }
    }

    [MpCompatFor("timuttie.justleavealready.fork.mitasamodel")]
    class FullLeaveTheMapStatics
    {
        public FullLeaveTheMapStatics(ModContentPack mod) =>
            FullStaticDefaults.Register("LeaveTheMap", new (string, string, object)[]
            {
                ("LeaveTheMap.ExitGridUpdateManager", "ticksUntilRebuild", -1)
            });
    }

    [MpCompatFor("mlie.smartervisitors")]
    class FullSmarterVisitorsStatics
    {
        public FullSmarterVisitorsStatics(ModContentPack mod) =>
            FullStaticDefaults.Register("SmarterVisitors", new (string, string, object)[]
            {
                ("SmarterVisitors.SmarterVisitors", "TimeToLeaveLords", null)
            });
    }

    [MpCompatFor("sirrandoo.mpe")]
    class FullMorePauseEventsStatics
    {
        public FullMorePauseEventsStatics(ModContentPack mod) =>
            FullStaticDefaults.Register("MorePauseEvents", new (string, string, object)[]
            {
                ("SirRandoo.MPE.Patches.CorpseObsessionPatch", "_snapshot", -1),
                ("SirRandoo.MPE.Patches.FoodBingePatch", "_snapshot", -1),
                ("SirRandoo.MPE.Patches.IdleJoyPatch", "_snapshot", -1),
                ("SirRandoo.MPE.Patches.IdlePatch", "_snapshot", -1)
            });
    }

    [MpCompatFor("mortis.apexmechanoids")]
    class FullApexMechanoidsStatics
    {
        public FullApexMechanoidsStatics(ModContentPack mod) =>
            FullStaticDefaults.Register("ApexMechanoids", new (string, string, object)[]
            {
                ("ApexMechanoids.JobGiver_AITinkerCombat", "lastShieldTargetMemoryCleanupTick", -99999)
            });
    }
}
