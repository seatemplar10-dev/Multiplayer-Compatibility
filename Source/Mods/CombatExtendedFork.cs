using HarmonyLib;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Detects the local Combat Extended fork build (branch sea/fork-1118). That dll has a type only the fork has,
    /// <c>CombatExtended.SeaForkRows</c>, with a const string <c>Rows</c> like ",57,32,63,...,". For every row listed there the fix is
    /// inside CE, so the matching compat patch must not run (the transpilers would not find their pattern, and the sync methods would
    /// be registered twice). With the stock dll the type is missing and every compat patch applies as before.</summary>
    internal static class CombatExtendedFork
    {
        private static string rows;
        private static bool read;

        public static bool Has(int row)
        {
            if (!read)
            {
                read = true;
                rows = AccessTools.TypeByName("CombatExtended.SeaForkRows")?.GetField("Rows")?.GetRawConstantValue() as string;
                if (rows != null)
                    Log.Message($"[MPCompat] Combat Extended fork detected, rows fixed inside CE (compat patches skipped): {rows.Trim(',')}");
            }
            return rows != null && rows.Contains("," + row + ",");
        }
    }
}
