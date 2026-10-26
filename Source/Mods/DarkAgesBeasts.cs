using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Dark Ages: Beasts and Monsters (Van.Beasts, Bestiary.dll)</summary>
    /// <remarks>
    /// FIXES row 239 (26 Oct 2026, lab soak c1soak_1): <c>Bestiary.Comp_AlternateGraphics.TryGetAlternate</c>, reached from the mod's
    /// postfix on vanilla <c>PawnGraphicUtils.TryGetAlternate</c> (the animal render nodes call it), picks an alternate graphic with
    /// <c>TryRandomElementByWeight</c> on whatever Rand state is current the first time it runs and stores the pick in <c>index</c>, which
    /// PostExposeData saves. Only the PC that draws the animal runs it: the joiner's new map's starting animal had index 0 on the joiner and
    /// -1 (not saved) on the host = a full-state mismatch at the next checkpoint (also in the 22 Oct MF9 diff). Vanilla's own TryGetAlternate
    /// is stateless and seeded by the pawn's id. In MP, while nothing is stored (index -1), the mod's pick runs inside Rand.Push/PopState
    /// seeded by the pawn's id and index is put back to -1: the same look on every PC and nothing per-PC in the save. A stored index (from a
    /// single-player save) is used as it is.
    /// </remarks>
    [MpCompatFor("Van.Beasts")]
    public class DarkAgesBeasts
    {
        private static FieldInfo indexField;

        public DarkAgesBeasts(ModContentPack mod)
        {
            var compType = AccessTools.TypeByName("Bestiary.Comp_AlternateGraphics")
                           ?? throw new MissingMemberException("Bestiary.Comp_AlternateGraphics");
            indexField = AccessTools.Field(compType, "index");
            if (indexField?.FieldType != typeof(int))
                throw new MissingFieldException(compType.FullName, "index");
            var method = AccessTools.DeclaredMethod(compType, "TryGetAlternate")
                         ?? throw new MissingMethodException(compType.FullName, "TryGetAlternate");
            MpCompat.harmony.Patch(method,
                prefix: new HarmonyMethod(typeof(DarkAgesBeasts), nameof(PreTryGetAlternate)),
                finalizer: new HarmonyMethod(typeof(DarkAgesBeasts), nameof(FinalTryGetAlternate)));
        }

        private static void PreTryGetAlternate(object __instance, Pawn pawn, out bool __state)
        {
            __state = MP.IsInMultiplayer && pawn != null && (int)indexField.GetValue(__instance) == -1;
            if (__state)
                Rand.PushState(Gen.HashCombineInt(pawn.thingIDNumber, 0xBE57));
        }

        private static Exception FinalTryGetAlternate(object __instance, bool __state, Exception __exception)
        {
            if (__state)
            {
                Rand.PopState();
                indexField.SetValue(__instance, -1);
            }

            return __exception;
        }
    }
}
