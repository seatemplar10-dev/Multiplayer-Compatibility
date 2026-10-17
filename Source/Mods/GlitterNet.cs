using System;
using HarmonyLib;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>GlitterNet by K</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=1725544583"/>
    /// <remarks>
    /// Determinism only. Written against the 1.6 copy of GlitterNet.dll (<c>S:/Rimworld MP shiz/lab/game/Mods/1725544583/v1.6/Assemblies/GlitterNet.dll</c>;
    /// the root and v1.1-v1.5 copies are shadowed by it and not loaded). Sweep evidence (waylith/_lanes/clrsem/sweep_il, TOP.md entry 11,
    /// token 0x06000232): <c>GlitterNet.GlitterNetTickScheduler/TickList::Shuffle</c> starts with <c>Random random = new Random();</c> and is the
    /// last step of the <c>TickList</c> constructor (<c>assignmentSequence = Shuffle(assignmentSequence);</c>, decomp_out/type_GlitterNet.GlitterNetTickScheduler_v1.6.cs.txt:35).
    /// Every building that registers with the scheduler gets the tick slot <c>assignmentSequence[currentAssignment]</c>, so the permutation decides on which
    /// tick of the period each building's callback runs. The permutation is never saved and is rebuilt whenever the scheduler game component is constructed
    /// (every process start / every load), so host, joiner and arbiter ran the same callbacks on different ticks.
    /// Fix: a prefix replaces Shuffle with the same Fisher-Yates loop driven by a small fixed LCG (seed = array length). The result is the same on every
    /// machine, however and whenever the TickList is built - which is why it is NOT <see cref="PatchingUtilities.RandRedirector"/> / Verse.Rand here: the
    /// TickList is constructed while a save loads, when the synced Rand state is not guaranteed to be equal on a joiner that loads the host's save and a host
    /// that reloads its own, and a permutation that is never saved must not depend on it. It is applied outside multiplayer too (the permutation is still a
    /// spread over the period; only the order is fixed), because the first TickList can be built before the session flag is set during the join-point load.
    /// Status: [unverified] - compiled only, not run in game yet.
    /// Not handled here (not System.Random): TOP.md entry 12, GlitterNetSystemWakeEnforcer.GameComponentTick uses <c>(TicksGame + this.GetHashCode()) % 120</c>,
    /// an identity hash; that needs its own fix and a test.
    /// </remarks>
    [MpCompatFor("k.glitternet")]
    public class GlitterNet
    {
        private const string LogPrefix = "[MPCompat - GlitterNet] ";

        public GlitterNet(ModContentPack mod)
        {
            var tickListType = AccessTools.TypeByName("GlitterNet.GlitterNetTickScheduler+TickList")
                ?? throw new TypeLoadException($"{LogPrefix}type GlitterNet.GlitterNetTickScheduler+TickList not found");
            var shuffle = AccessTools.DeclaredMethod(tickListType, "Shuffle", [typeof(int[])])
                ?? throw new MissingMethodException(tickListType.FullName, "Shuffle(int[])");
            if (shuffle.ReturnType != typeof(int[]) || !shuffle.IsStatic)
                throw new MissingMethodException($"{LogPrefix}{tickListType.FullName}.Shuffle is {shuffle}, expected static int[] Shuffle(int[])");

            MpCompat.harmony.Patch(shuffle, prefix: new HarmonyMethod(typeof(GlitterNet), nameof(PreShuffle)));

            // FIXES row 50 (17 Oct, REA sweep): GlitterNetSystemWakeEnforcer.GameComponentTick times its listener ticks,
            // the goodbye letter and the next storyline quest with (TicksGame + this.GetHashCode()) % N. The class has no
            // GetHashCode override, so it is the per-process identity hash -> different ticks per PC. Use 0 instead.
            var enforcerType = AccessTools.TypeByName("GlitterNet.Events.GlitterNetSystemWakeEnforcer")
                ?? throw new TypeLoadException($"{LogPrefix}type GlitterNet.Events.GlitterNetSystemWakeEnforcer not found");
            var tick = AccessTools.DeclaredMethod(enforcerType, "GameComponentTick")
                ?? throw new MissingMethodException(enforcerType.FullName, "GameComponentTick");
            MpCompat.harmony.Patch(tick, transpiler: new HarmonyMethod(typeof(GlitterNet), nameof(NoIdentityHash)));
        }

        private static readonly System.Reflection.MethodInfo ObjectGetHashCode =
            AccessTools.Method(typeof(object), nameof(GetHashCode));

        // `this.GetHashCode()` -> `pop; ldc.i4.0` (the receiver is popped, the hash is a constant). The original has exactly
        // three such calls, all on `this` (REA call edges of the lab dll, decompiled lines 3837/3848/3860).
        private static System.Collections.Generic.IEnumerable<CodeInstruction> NoIdentityHash(
            System.Collections.Generic.IEnumerable<CodeInstruction> instr, System.Reflection.MethodBase original)
        {
            var replaced = 0;
            foreach (var ci in instr)
            {
                if (ci.Calls(ObjectGetHashCode))
                {
                    var pop = new CodeInstruction(System.Reflection.Emit.OpCodes.Pop);
                    pop.labels.AddRange(ci.labels);
                    pop.blocks.AddRange(ci.blocks);
                    yield return pop;
                    yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Ldc_I4_0);
                    replaced++;
                    continue;
                }
                yield return ci;
            }

            if (replaced != 3)
                Log.Error($"{LogPrefix}expected 3 GetHashCode calls in {original?.FullDescription()}, replaced {replaced}");
        }

        // Same shape as the original loop (for i = Length - 1; i > 1; i--: swap(i, Next(i))), with a fixed generator.
        private static bool PreShuffle(int[] __0, ref int[] __result)
        {
            var array = __0;
            unchecked
            {
                var state = 0x6D2B79F5u ^ ((uint)array.Length * 2654435761u);
                for (var i = array.Length - 1; i > 1; i--)
                {
                    state = state * 1664525u + 1013904223u;
                    var j = (int)((state >> 8) % (uint)i);
                    (array[j], array[i]) = (array[i], array[j]);
                }
            }

            __result = array;
            return false;
        }
    }
}
