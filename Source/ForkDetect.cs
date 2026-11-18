using System;
using System.Linq;
using HarmonyLib;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>
    /// F.80: local fork builds of some mods carry fixes that MP-Compat patched in before. A patch the fork supersedes is skipped
    /// when the fork is loaded; the stock dll keeps it (control arms run the stock dll with -mplaboff=row). The fork is found by a
    /// member that only the fork has, or by the marker class SeaForkRows.Rows (a comma list of row tokens) where the fork added no
    /// member.
    /// </summary>
    internal static class ForkDetect
    {
        /// <summary>True when <paramref name="typeName"/> has a declared method <paramref name="method"/> (a member only the fork has).</summary>
        public static bool HasMethod(string mod, string typeName, string method)
        {
            try
            {
                var type = AccessTools.TypeByName(typeName);
                var found = type != null && AccessTools.GetDeclaredMethods(type).Any(m => m.Name == method);
                Report(mod, found, $"{typeName}.{method} present");
                return found;
            }
            catch (Exception e)
            {
                Log.Warning($"MPCompat :: {mod}: fork detection by {typeName}.{method} failed, stock behaviour kept: {e.Message}");
                return false;
            }
        }

        /// <summary>True when the marker class <paramref name="markerType"/> (SeaForkRows) lists <paramref name="token"/> in its const Rows.</summary>
        public static bool HasRow(string mod, string markerType, string token)
        {
            try
            {
                var type = AccessTools.TypeByName(markerType);
                var field = type == null ? null : AccessTools.Field(type, "Rows");
                var rows = field?.GetValue(null) as string;
                var found = rows != null && Array.IndexOf(rows.Split(','), token) >= 0;
                Report(mod, found, $"{markerType}.Rows = {rows ?? "(no marker)"}, token {token}");
                return found;
            }
            catch (Exception e)
            {
                Log.Warning($"MPCompat :: {mod}: fork detection by {markerType} failed, stock behaviour kept: {e.Message}");
                return false;
            }
        }

        private static void Report(string mod, bool found, string how)
        {
            if (found)
                Log.Message($"MPCompat :: {mod}: fork build detected ({how}), the patches it supersedes are skipped");
        }
    }
}
