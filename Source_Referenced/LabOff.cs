using System;
using System.Collections.Generic;
using HarmonyLib;
using Verse;

// LabOff.cs - lab mutation switch (design: side-session-notes/reports/multitest-offswitch-1116.md section 2).
// No flag = no rows, every Row() call is false (one bool/count test). The flag counts only when the lab harness type
// SeaMPLab.MPLab is loaded; Sea's and James's game have no harness dll, so there the flag is ignored.
// Gate timing: the harness assembly is loaded in LoadedModManager.LoadModContent, before CreateModClasses constructs the
// mods (decompile Verse/LoadedModManager.cs:49-61), so it is normally loaded when any compat constructor or patch runs. The gate
// is read lazily at the first Row()/Touch() call (not in the static constructor) and the result is latched for good: a Row()
// call that came before the harness load WOULD latch a false "no harness". The load order above is what prevents that.
// The first read runs under a lock and gateRead is set last, so no thread sees rows before the harness check has cleared them.
internal static class LabOff
{
    public const string Arg = "mplaboff";
    public static readonly string Raw;                         // "117,152" or null
    // The rows THIS dll can switch.
    static readonly int[] Owned = { 178, 361 };
    static readonly HashSet<int> rows = new HashSet<int>();    // requested AND owned
    static volatile bool gateRead;
    static readonly object gateLock = new object();

    static LabOff()
    {
        if (!GenCommandLine.TryGetCommandLineArg(Arg, out Raw) || string.IsNullOrEmpty(Raw)) { Raw = null; return; }
        foreach (var p in Raw.Split(','))
            if (int.TryParse(p.Trim(), out int r) && Array.IndexOf(Owned, r) >= 0)
                rows.Add(r);
    }

    // Reads the harness gate once (first call) and logs one line per owned requested row.
    static void ReadGate()
    {
        lock (gateLock)
        {
            if (gateRead) return;
            ReadGateLocked();
            gateRead = true;
        }
    }

    static void ReadGateLocked()
    {
        if (rows.Count == 0) return;
        if (AccessTools.TypeByName("SeaMPLab.MPLab") == null) { rows.Clear(); return; }
        var sorted = new List<int>(rows);
        sorted.Sort();
        foreach (var r in sorted)
            Log.Warning("[LabOff] OFF row " + r + " dll=Multiplayer_Compat_Referenced");
    }

    // Call once at module load so the log lines appear at startup even for BODY rows.
    public static void Touch()
    {
        if (!gateRead) ReadGate();
    }

    public static bool Row(int row)
    {
        if (!gateRead) ReadGate();
        return rows.Count != 0 && rows.Contains(row);
    }
}
