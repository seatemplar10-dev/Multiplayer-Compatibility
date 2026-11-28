using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>FULL1127 wave L (cu18): MP-side load faults.</summary>
    /// <remarks>
    /// Better Autocast VPE: its six area designators derive from the generic BetterAutocastVPE.CellDesignator&lt;TArea&gt;, which declares
    /// DesignateSingleCell. MP's "General designation patches" loop finds only the OPEN generic type, whose patch throws NotSupported,
    /// and never sees the closed types, so the area designators had no sync wrapper. Here the same MP prefix and finalizer are put on the
    /// DesignateSingleCell of each closed CellDesignator&lt;TArea&gt;. The designators the player clicks (Designator_Area_X_Expand and
    /// _Clear) have parameterless constructors, so MP's own designator serialization rebuilds them on the other PCs.
    /// </remarks>
    [MpCompatFor("dev.tobot.vpe.betterautocast")]
    class FullLBetterAutocastVPE
    {
        private static IEnumerable<Type> SafeTypes(Assembly asm)
        {
            try { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); }
        }

        public FullLBetterAutocastVPE(ModContentPack mod)
        {
            try
            {
                var mpPatches = AccessTools.TypeByName("Multiplayer.Client.DesignatorPatches");
                var prefix = AccessTools.DeclaredMethod(mpPatches, "DesignateSingleCell");
                var finalizer = AccessTools.DeclaredMethod(mpPatches, "DesignateFinalizer");
                if (prefix == null || finalizer == null)
                    throw new Exception("Multiplayer.Client.DesignatorPatches.DesignateSingleCell/DesignateFinalizer not found");

                int first = Priority.First;
                var prio = AccessTools.TypeByName("Multiplayer.Client.MpPriority");
                var f = prio == null ? null : AccessTools.Field(prio, "MpFirst");
                if (f != null) first = Convert.ToInt32(f.GetValue(null));

                var open = AccessTools.TypeByName("BetterAutocastVPE.CellDesignator`1");
                if (open == null) throw new Exception("BetterAutocastVPE.CellDesignator`1 not found");

                var closed = new HashSet<Type>();
                foreach (var t in SafeTypes(open.Assembly))
                    for (var b = t.BaseType; b != null; b = b.BaseType)
                        if (b.IsGenericType && !b.ContainsGenericParameters && b.GetGenericTypeDefinition() == open)
                        {
                            closed.Add(b);
                            break;
                        }

                int count = 0;
                foreach (var type in closed)
                {
                    var method = type.GetMethod("DesignateSingleCell", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, null, new[] { typeof(IntVec3) }, null);
                    if (method == null) continue;
                    MpCompat.harmony.Patch(method, new HarmonyMethod(prefix) { priority = first }, null, null, new HarmonyMethod(finalizer));
                    count++;
                }

                if (count == 0) throw new Exception("no closed CellDesignator<TArea> found");
                Log.Message($"[MPCompat - FULL1127 L] Better Autocast VPE: DesignateSingleCell sync wrapper on {count} closed CellDesignator<TArea> types registered");
            }
            catch (Exception e)
            {
                Log.Error($"[MPCompat - FULL1127 L] Better Autocast VPE: designator sync failed: {e}");
            }
        }
    }
}
