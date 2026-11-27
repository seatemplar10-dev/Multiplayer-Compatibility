using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>FULL1127 class H custom row: AlphaCrafts artisan quest tick draws Rand only when CanRun(slate, Find.CurrentMap) is true, so the draw depends on the viewed map.
    /// The shared CurrentMap replacement removes the dependency.</summary>
    [MpCompatFor("sarg.alphacrafts")]
    class FullAlphaCrafts
    {
        public FullAlphaCrafts(ModContentPack mod)
        {
            var m = AccessTools.DeclaredMethod("AlphaCrafts.WorldComponent_ArtisanTradingQuests:WorldComponentTick");
            if (m != null) PatchingUtilities.ReplaceCurrentMapUsage(m);
            else Log.Error("[MPCompat - FULL1127 H] AlphaCrafts: method WorldComponentTick not found");
            Log.Message($"[MPCompat - FULL1127 H] AlphaCrafts: {(m != null ? 1 : 0)} of 1 methods patched");
        }
    }

    /// <summary>FULL1127 class J3 custom row: AnimalCache.GetAvailableAnimal draws from a static readonly System.Random built in the cctor, so PatchSystemRand (which only redirects new Random) sees no ctor.
    /// Here the ldsfld of that field in the method is replaced by the Rand-backed RandRedirector instance.</summary>
    [MpCompatFor("colossalfossil.animalsarefuncontinued")]
    class FullAnimalsAreFun
    {
        private static FieldInfo randomField;
        public static int Redirected = -1;

        public FullAnimalsAreFun(ModContentPack mod)
        {
            var m = AccessTools.DeclaredMethod("AnimalsAreFunContinued.Data.AnimalCache:GetAvailableAnimal");
            randomField = AccessTools.Field("AnimalsAreFunContinued.Data.AnimalCache:_random");
            if (m == null || randomField == null)
                Log.Error("[MPCompat - FULL1127 J3] AnimalsAreFun: AnimalCache.GetAvailableAnimal or _random not found");
            else
                MpCompat.harmony.Patch(m, transpiler: new HarmonyMethod(typeof(FullAnimalsAreFun), nameof(Transpiler)));
            Log.Message($"[MPCompat - FULL1127 J3] AnimalsAreFun: {(m != null && randomField != null ? 1 : 0)} of 1 methods patched");
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instr)
        {
            var getter = AccessTools.PropertyGetter(typeof(PatchingUtilities.RandRedirector), nameof(PatchingUtilities.RandRedirector.Instance));
            int n = 0;
            foreach (var ci in instr)
            {
                if (ci.opcode == OpCodes.Ldsfld && ci.operand is FieldInfo f && f == randomField)
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = getter;
                    n++;
                }
                yield return ci;
            }
            Redirected = n;
            if (n == 0) Log.Warning("[MPCompat - FULL1127 J3] AnimalsAreFun: no read of _random found in GetAvailableAnimal");
        }
    }
}
