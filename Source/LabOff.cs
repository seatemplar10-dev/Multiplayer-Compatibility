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
    static readonly int[] Owned = { 16, 18, 30, 38, 39, 40, 46, 47, 48, 49, 50, 53, 54, 56, 57, 76, 77, 90, 93, 95, 106, 110, 111, 146, 161, 163, 164, 168, 174, 180, 193, 195, 196, 206, 209, 217, 240, 245, 251, 256, 262, 268, 271, 272, 273, 275, 277, 278, 279, 285, 295, 296, 297, 299, 301, 302, 304, 305, 309, 360, 361, 369, 371, 373, 374, 375, 381, 389, 390, 392, 395, 401, 420, 422, 423, 424, 425, 435, 443, 458, 459, 460, 461, 462, 463, 464, 465, 466, 467, 468, 469, 470, 471, 472, 473, 474, 476, 477, 478, 479, 480, 481, 482, 483, 488, 489, 490, 491, 492, 493, 494, 495, 496, 497, 498, 500, 501, 502, 503, 504, 505, 506, 507, 508, 509, 510, 511, 513, 515, 516, 517, 518, 519, 520, 522, 523, 524, 525, 526, 527, 528, 529, 531, 535, 536 };
    static readonly HashSet<int> rows = new HashSet<int>();    // requested AND owned
    static volatile bool gateRead;
    static readonly object gateLock = new object();

    // Accepts "-mplaboff=117,152" (one token) and "-mplaboff 117 152" (cmd.exe splits at '=' and ','; tokens up to the next '-' one).
    static string ReadRaw()
    {
        var ints = new List<int>();
        string[] a = Environment.GetCommandLineArgs();
        string key = "-" + Arg;
        for (int i = 0; i < a.Length; i++)
        {
            string t = a[i];
            if (t == null) continue;
            if (t.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) AddParts(t.Substring(key.Length + 1), ints);
            else if (string.Equals(t, key, StringComparison.OrdinalIgnoreCase))
                for (int j = i + 1; j < a.Length && a[j] != null && !a[j].StartsWith("-"); j++) AddParts(a[j], ints);
        }
        return ints.Count == 0 ? null : string.Join(",", ints.ConvertAll(x => x.ToString()).ToArray());
    }

    static void AddParts(string s, List<int> ints)
    {
        foreach (var p in s.Split(','))
            if (int.TryParse(p.Trim(), out int v)) ints.Add(v);
    }

    static LabOff()
    {
        Raw = ReadRaw();
        if (Raw == null) return;
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
            Log.Warning("[LabOff] OFF row " + r + " dll=Multiplayer_Compat");
        flipTick = ReadFlip();
        if (flipTick >= 0) Log.Warning("[LabOff] FLIP armed tick=" + flipTick + " dll=Multiplayer_Compat");
    }

    // Call once at module load so the log lines appear at startup even for BODY rows.
    public static void Touch()
    {
        if (!gateRead) ReadGate();
    }

        // Trigger line: ONE log line the first time a row's switch is read in this process (same text in every fork).
        static readonly bool[] hitDone = new bool[1000];
        static void Hit(int row, bool off)
        {
            try
            {
                if (row < 0 || row >= hitDone.Length || hitDone[row]) return;
                hitDone[row] = true;
                var tick = -1;
                try { tick = Find.TickManager?.TicksGame ?? -1; } catch (Exception) { tick = -1; }
                Log.Message("[LabOff] HIT row " + row + " state=" + (off ? "off" : "on") + " tick=" + tick);
            }
            catch (Exception) { }
        }

    // -mplabflip=<tick> (or "-mplabflip <tick>", or a part "flip<tick>" in the -mplaboff value): one boot = control ticks, then fix ticks. The rows in -mplaboff stay OFF (old behaviour)
    // while Find.TickManager.TicksGame < tick and are ON (fix) from that tick on. No game loaded = tick -1 = OFF. The switch is not latched:
    // it is the TicksGame value at each Row() call. One log line per row at its first read at or after the tick: [LabOff] FLIP row N tick=T.
    // No -mplabflip = flipTick -1 = Row() returns what it returned before. Only a row whose gate is read per call can flip.
    public const string FlipArg = "mplabflip";
    static int flipTick = -1;
    static readonly bool[] flipDone = new bool[1000];

    static int ReadFlip()
    {
        try
        {
            var args = Environment.GetCommandLineArgs();
            var key = "-" + FlipArg;
            for (var i = 0; i < args.Length; i++)
            {
                var t = args[i];
                if (t == null) continue;
                string v = null;
                if (t.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) v = t.Substring(key.Length + 1);
                else if (string.Equals(t, key, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) v = args[i + 1];
                if (v != null && int.TryParse(v.Trim(), out var n) && n >= 0) return n;
                // the same tick as a part of the -mplaboff value: -mplaboff=117,flip600 (this is how the arbiter process gets it)
                if (t.StartsWith("-" + Arg + "=", StringComparison.OrdinalIgnoreCase))
                    foreach (var part in t.Substring(Arg.Length + 2).Split(','))
                        if (part.Trim().StartsWith("flip", StringComparison.OrdinalIgnoreCase) && int.TryParse(part.Trim().Substring(4), out var m) && m >= 0) return m;
            }
        }
        catch (Exception) { }
        return -1;
    }

    // true = the flip tick is reached, the row is ON now
    static bool Flipped(int row)
    {
        var tick = -1;
        try { tick = Find.TickManager?.TicksGame ?? -1; } catch (Exception) { tick = -1; }
        if (tick < flipTick) return false;
        try
        {
            if (row >= 0 && row < flipDone.Length && !flipDone[row])
            {
                flipDone[row] = true;
                Log.Message("[LabOff] FLIP row " + row + " tick=" + tick);
            }
        }
        catch (Exception) { }
        return true;
    }

    public static bool Row(int row)
    {
        if (!gateRead) ReadGate();
        var r = rows.Count != 0 && rows.Contains(row);
        if (r && flipTick >= 0 && Flipped(row)) r = false;
        Hit(row, r);
        return r;
    }
}
