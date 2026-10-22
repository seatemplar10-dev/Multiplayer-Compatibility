using System;
using System.Collections;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Every def's ConfigErrors once after the defs load, in every mode (FIXES row 180 as a class).</summary>
    /// <remarks>
    /// RimWorld runs ConfigErrors only when Prefs.DevMode is on at boot (decomp4981 PlayDataLoader.cs:296-309: Parallel.ForEach over
    /// typeof(Def).AllSubclasses(), DefDatabase&lt;T&gt;.ErrorCheckAllDefs per type), and some mods' ConfigErrors ASSIGN gameplay values
    /// (lane d180_devmode_audit, 22 Oct 2026: Ancient Amorphous Threat's skyfaller MaxSize and boss pawn defaults, TabulaRasa's gene
    /// category display priorities). A PC with dev mode off then plays a different game (live 21 Oct: desyncs D4-D6 on the joiner).
    /// This pass gives every PC the dev-mode state: def types sorted by full name, each type's defs in DefDatabase order (the order
    /// ErrorCheckAllDefs uses), ignoreConfigErrors skipped like vanilla, every error list enumerated to its end (iterator overrides
    /// assign while enumerating), exceptions swallowed, nothing logged but one summary line. With dev mode on, vanilla's parallel pass
    /// has already run: the side effects found are idempotent and stay inside their own def type (whose defs one thread checks in
    /// DefDatabase order), so the second run changes nothing. Queued from the Mod constructor, so it runs before any
    /// [StaticConstructorOnStartup] class, like vanilla's pass. Not session-specific: single player without dev mode has the same gap.
    /// </remarks>
    internal static class DeterministicConfigErrors
    {
        internal static void Run()
        {
            var watch = Stopwatch.StartNew();
            int types = 0, defs = 0, errors = 0, exceptions = 0;
            foreach (var type in typeof(Def).AllSubclasses().Where(t => !t.ContainsGenericParameters).OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                var list = (IList)typeof(DefDatabase<>).MakeGenericType(type)
                    .GetProperty("AllDefsListForReading", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (list == null || list.Count == 0)
                    continue;
                types++;
                foreach (var def in list.Cast<Def>().ToList())
                {
                    if (def.ignoreConfigErrors)
                        continue;
                    defs++;
                    try
                    {
                        foreach (var _ in def.ConfigErrors())
                            errors++;
                    }
                    catch
                    {
                        exceptions++;
                    }
                }
            }

            Log.Message($"[MPCompat] row 180: every ConfigErrors run once after the defs load (dev mode {Prefs.DevMode}): {types} def types, " +
                        $"{defs} defs, {errors} error lines (not logged), {exceptions} exceptions, {watch.ElapsedMilliseconds} ms");
        }
    }
}
