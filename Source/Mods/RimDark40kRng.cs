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
                field = AccessTools.DeclaredField(type, "rand")
                    ?? throw new MissingFieldException(type.FullName, "rand");
                if (field.FieldType != typeof(Random))
                    throw new MissingFieldException($"{type.FullName}.rand is {field.FieldType}, expected System.Random");
                weightedSelectionRandFields[type] = field;
            }

            field.SetValue(selection, PatchingUtilities.RandRedirector.Instance);
        }
    }
}
