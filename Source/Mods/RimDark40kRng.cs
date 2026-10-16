using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>
    /// Helpers shared by the two RimDark 40k modules (<see cref="RimDark40kFramework"/> and <see cref="RimDark40kMankindsFinest"/>).
    /// Not a module itself (no <see cref="MpCompatForAttribute"/>), so the loader never instantiates it.
    /// </summary>
    /// <remarks>
    /// Core40k.WeightedSelection&lt;T&gt; owns a <c>private Random rand = new Random();</c> field (Core40k.dll, ctor token 0x0600035D,
    /// <c>Core40k.WeightedSelection_1_.ctor__0x0600035D.cs.txt:9: rand = new Random();</c> in the sweep's decomp_out). GetRandom and
    /// GetRandomUnique draw from that field, and it is created by a field initializer inside the ctor of a generic class.
    /// Both <c>TraitData</c> and <c>GeneDef</c> are reference types, so the two closed ctors share one canonical code body and
    /// a Harmony patch on one closed ctor would copy that instantiation's IL for the other one as well. So the ctor is NOT patched.
    /// Instead the four non-generic callers (the only <c>new WeightedSelection&lt;&gt;()</c> sites in Core40k.dll and Genes40k.dll, checked
    /// on a full ilspycmd decompile of both) get a transpiler that stores <see cref="PatchingUtilities.RandRedirector.Instance"/>
    /// into <c>rand</c> right after the <c>newobj</c>.
    /// </remarks>
    internal static class RimDark40kRng
    {
        private const string WeightedSelectionName = "Core40k.WeightedSelection`1";

        private static readonly Dictionary<Type, FieldInfo> weightedSelectionRandFields = new();

        // Per module (log prefix): how many sites were patched / found already deterministic. Logged once by RunSteps.
        private static readonly Dictionary<string, (int replaced, int alreadyRand)> siteCounts = new();

        // null = not looked up yet. See WeightedSelectionUsesSystemRandom.
        private static bool? weightedSelectionUsesSystemRandom;

        internal static Type FindType(string logPrefix, string name)
            => AccessTools.TypeByName(name) ?? throw new TypeLoadException($"{logPrefix}type {name} not found");

        /// <summary>The one method called <paramref name="methodName"/> declared on <paramref name="typeName"/>. Throws when there is none or more than one (an overload would make AccessTools pick silently).</summary>
        internal static MethodInfo FindMethod(string logPrefix, string typeName, string methodName)
        {
            var type = FindType(logPrefix, typeName);
            var found = type.GetMethods(AccessTools.allDeclared).Where(m => m.Name == methodName).ToArray();
            if (found.Length == 0)
                throw new MissingMethodException($"{logPrefix}{typeName}.{methodName} not found");
            if (found.Length > 1)
                throw new AmbiguousMatchException($"{logPrefix}{typeName}.{methodName} has {found.Length} overloads, expected 1");
            return found[0];
        }

        /// <summary>
        /// True when <paramref name="method"/> still contains a <c>newobj System.Random::.ctor</c>. Core40k 1.2.112+ and Genes40k 1.1.95+ (the 1.7 Workshop builds)
        /// draw from Verse.Rand themselves and have no System.Random left (IL listing of both dlls, r25), so there is nothing for
        /// <see cref="PatchingUtilities.FixRNG"/> to replace; the older 1.6 builds (Core40k 1.2.32 / Genes40k 1.1.14) have 7 + 10 such constructors.
        /// </summary>
        internal static bool UsesSystemRandom(MethodBase method)
        {
            foreach (var ci in PatchProcessor.GetOriginalInstructions(method))
            {
                if (ci.opcode == OpCodes.Newobj && ci.operand is ConstructorInfo { DeclaringType: { } declaring } && declaring == typeof(Random))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Patches one site. When the method still builds a <c>System.Random</c> it goes through
        /// <see cref="PatchingUtilities.PatchSystemRand(MethodBase, bool)"/> as before. When it does not (the mod already calls Verse.Rand) the
        /// transpiler is not installed at all (it would only log a "nothing patched" warning), but a site that was patched WITH push/pop is still
        /// wrapped in Rand.PushState/PopState: it can be reached from UI code (gene previews, pawn generation for dialogs), and a Verse.Rand draw
        /// made on one client only would move the shared state.
        /// </summary>
        internal static void PatchSite(string logPrefix, MethodBase method, bool pushPop)
        {
            var counts = siteCounts.TryGetValue(logPrefix, out var c) ? c : default;
            if (UsesSystemRandom(method))
            {
                PatchingUtilities.PatchSystemRand(method, pushPop);
                counts.replaced++;
            }
            else
            {
                if (pushPop)
                    PatchingUtilities.PatchPushPopRand(method);
                counts.alreadyRand++;
            }

            siteCounts[logPrefix] = counts;
        }

        /// <summary>
        /// Whether Core40k.WeightedSelection&lt;T&gt; still owns a private <c>System.Random rand</c> field. The 1.6 builds do; the 1.7 Workshop build
        /// (Core40k 1.2.112) has no such field and picks with Rand.Range, so the callers must NOT get <see cref="RedirectWeightedSelectionRandom"/>:
        /// <see cref="UseRedirector"/> would have nothing to write to. Throws when the type itself cannot be found (lookups keep failing loudly).
        /// </summary>
        internal static bool WeightedSelectionUsesSystemRandom(string logPrefix)
        {
            if (weightedSelectionUsesSystemRandom is { } known)
                return known;

            var type = AccessTools.TypeByName(WeightedSelectionName)
                ?? AccessTools.AllTypes().FirstOrDefault(t => t.FullName == WeightedSelectionName)
                ?? throw new TypeLoadException($"{logPrefix}type {WeightedSelectionName} not found");
            var field = AccessTools.DeclaredField(type, "rand");
            return (weightedSelectionUsesSystemRandom = field != null && field.FieldType == typeof(Random)).Value;
        }

        /// <summary>Installs <see cref="RedirectWeightedSelectionRandom"/> on a caller of <c>new WeightedSelection&lt;T&gt;()</c>, but only for builds whose WeightedSelection still has a System.Random.</summary>
        internal static void PatchWeightedSelectionCaller(string logPrefix, MethodBase method)
        {
            if (WeightedSelectionUsesSystemRandom(logPrefix))
                MpCompat.harmony.Patch(method, transpiler: new HarmonyMethod(typeof(RimDark40kRng), nameof(RedirectWeightedSelectionRandom)));
        }

        /// <summary>Runs every step even if an earlier one failed (one missing method must not leave the other sites unpatched), then throws if any failed so the loader logs the module as failed.</summary>
        internal static void RunSteps(string logPrefix, params (string name, Action patch)[] steps)
        {
            List<string> failed = null;
            foreach (var (name, patch) in steps)
            {
                try
                {
                    patch();
                }
                catch (Exception e)
                {
                    Log.Error($"{logPrefix}step '{name}' failed: {e}");
                    (failed ??= []).Add(name);
                }
            }

            if (siteCounts.TryGetValue(logPrefix, out var counts))
                Log.Message($"{logPrefix}{counts.replaced} System.Random site(s) redirected to Verse.Rand, {counts.alreadyRand} already deterministic in this version of the mod.");

            if (failed != null)
                throw new InvalidOperationException($"{logPrefix}{failed.Count} of {steps.Length} patch steps failed: {string.Join(", ", failed)}");
        }

        /// <summary>
        /// Transpiler: after every <c>newobj Core40k.WeightedSelection&lt;T&gt;::.ctor</c> the new object gets its private <c>rand</c> replaced
        /// with the <see cref="PatchingUtilities.RandRedirector"/>, i.e. its draws come from the synced <see cref="Rand"/>.
        /// Stack neutral: <c>dup; call UseRedirector(object)</c>.
        /// </summary>
        internal static IEnumerable<CodeInstruction> RedirectWeightedSelectionRandom(IEnumerable<CodeInstruction> instr, MethodBase original)
        {
            var useRedirector = AccessTools.DeclaredMethod(typeof(RimDark40kRng), nameof(UseRedirector));
            var count = 0;

            foreach (var ci in instr)
            {
                yield return ci;

                if (ci.opcode == OpCodes.Newobj
                    && ci.operand is ConstructorInfo { DeclaringType: { IsGenericType: true } declaring }
                    && declaring.GetGenericTypeDefinition().FullName == WeightedSelectionName)
                {
                    yield return new CodeInstruction(OpCodes.Dup);
                    yield return new CodeInstruction(OpCodes.Call, useRedirector);
                    count++;
                }
            }

            if (count != 1)
                Log.Warning($"[MPCompat - RimDark 40k] WeightedSelection redirect patched {count} constructor calls, expected 1, in {original?.FullDescription() ?? "(unknown method)"}");
        }

        private static void UseRedirector(object selection)
        {
            var type = selection.GetType();
            if (!weightedSelectionRandFields.TryGetValue(type, out var field))
            {
                field = AccessTools.DeclaredField(type, "rand");
                // Builds without the field (Core40k 1.2.112+) already draw from Verse.Rand: nothing to redirect. This transpiler is not
                // installed for them (see PatchWeightedSelectionCaller); the null entry keeps a stale one harmless instead of throwing in a tick.
                if (field != null && field.FieldType != typeof(Random))
                    field = null;
                weightedSelectionRandFields[type] = field;
            }

            field?.SetValue(selection, PatchingUtilities.RandRedirector.Instance);
        }
    }
}
