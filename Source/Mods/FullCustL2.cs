using HarmonyLib;
using System;
using System.Reflection;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>
    /// Harmony.PatchAll(assembly) runs every patch class in one loop (DoIf -> CreateClassProcessor(type).Patch()) with no catch,
    /// so one patch class whose target method is missing (or whose TargetMethod returns null) stops every later class of that mod
    /// and fails its static constructor (Too Many Mods, Gun Recoil, 5021 boot log). This finalizer on PatchClassProcessor.Patch
    /// logs and swallows only that missing-target exception; every other exception passes through unchanged.
    /// Installed by MpCompat's own harmony.PatchAll() in the MpCompat constructor, before any StaticConstructorOnStartup runs.
    /// </summary>
    [HarmonyPatch(typeof(PatchClassProcessor), nameof(PatchClassProcessor.Patch))]
    internal static class FullCustL2PatchAllGuard
    {
        static readonly FieldInfo containerType = AccessTools.Field(typeof(PatchClassProcessor), "containerType");
        internal static Action<string> Log = s => Verse.Log.Warning(s);

        // true only for the two Harmony messages a missing target produces
        internal static bool IsMissingTarget(Exception e)
        {
            Exception inner = e is HarmonyException && e.InnerException != null ? e.InnerException : e;
            if (inner is ArgumentException && inner.Message != null && inner.Message.StartsWith("Undefined target method for patch method")) return true;
            if (inner is NullReferenceException && inner.Message != null && inner.Message.StartsWith("Null method for")) return true;
            // Harmony PatchClassProcessor.RunMethod: plain System.Exception "Method <T>::TargetMethod() returned an unexpected result: null"
            if (inner.GetType() == typeof(Exception) && inner.Message != null && inner.Message.StartsWith("Method ")
                && (inner.Message.Contains("::TargetMethod()") || inner.Message.Contains("::TargetMethods()"))
                && inner.Message.EndsWith("returned an unexpected result: null")) return true;
            return false;
        }

        static Exception Finalizer(Exception __exception, PatchClassProcessor __instance)
        {
            if (__exception == null || !IsMissingTarget(__exception)) return __exception;
            try
            {
                Type t = containerType?.GetValue(__instance) as Type;
                Exception inner = __exception is HarmonyException && __exception.InnerException != null ? __exception.InnerException : __exception;
                Log("[MPCompat - FULL1127 L2] PatchAll: skipped " + (t?.FullName ?? "?") + " of " + (t?.Assembly.GetName().Name ?? "?") + ": " + inner.Message);
            }
            catch (Exception) { }
            return null;
        }
    }
}
