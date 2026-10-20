using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>ABF: Synstructs Core by Killathon</summary>
    /// <remarks>
    /// 1.7 lab, FIXES row 145 (20 Oct 2026, d146_loadsweep finding R-1, d149 gapsD).
    /// <c>ArtificialBeings.SC_Utils.GetBlank</c> builds ONE static "blank" pawn per process the first time anybody needs it (the Make-Blank
    /// surgery <c>Recipe_MakeBlank.ApplyOnPawn</c> or synstruct resuscitation <c>JobDriver_ResuscitateSynstruct.Resurrect</c>, both simulation code)
    /// with a full <c>PawnGenerator.GeneratePawn</c>: unique ids for the pawn, its apparel/gear, hediffs, genes and abilities, a run of Rand draws from
    /// the ticking map's random state, and <c>PawnGenerator</c>'s own tail line
    /// <c>Find.StoryWatcher.watcherPopAdaptation.Notify_PawnEvent(pawn, PopAdaptationEvent.GainedColonist)</c>, which resets the saved
    /// <c>adaptDays</c> to 0. The blank is cached in a static and never saved, so the PC that builds it first (the host, before a join) is
    /// simply ahead of the PC that builds it later (the joiner, after the join point): other thing ids, another map Rand stream, another adaptDays.
    /// Three changes, all keyed on the one call:
    /// (1) Every <c>UniqueIDsManager</c> counter, the saved <c>adaptDays</c> and the Rand state are put back after the build (same pattern as Nice
    /// Bill Tab, row 143). This is safe because the blank is only ever a TEMPLATE: <c>SC_Utils.Duplicate(blank, dest)</c> copies defs and plain
    /// values out of it (backstory defs, favorite colour def, new Trait / SkillRecord / Thought objects, policy references) and never keeps a
    /// reference to the blank pawn itself; the blank is not spawned, not in WorldPawns, not in any saved collection, so the thing ids it used are
    /// not looked up anywhere and reusing them for later real things is harmless.
    /// (2) The build runs on a fixed Rand seed, so the values <c>Duplicate</c> does copy out of it that came from Rand
    /// (<c>story.favoriteColor</c> = <c>DefDatabase&lt;ColorDef&gt;...RandomElement()</c>, PawnGenerator.cs:802) are the same on every PC no matter when
    /// that PC built its blank (with only (1) the Rand stream is restored, but the host's blank would still hold a colour drawn at T1 and the
    /// joiner's one drawn at T2, and both are copied into the saved <c>dest.story.favoriteColor</c>).
    /// (3) The cache is dropped on every game load/new game (<c>GameComponentUtility.FinalizeInit</c>). A blank built before a load keeps the old
    /// game's <c>Faction.OfPlayer</c> object; after a reload <c>Duplicate</c> sees <c>source.Faction != dest.Faction</c> and calls
    /// <c>dest.SetFaction(oldFaction)</c> (Pawn.cs:2702: adaptDays reset, ClearMind, surgery bills cleared, GameEnder check...) on the PCs that
    /// reloaded after building (the host at every join point) and not on the PC that built after loading. With the cache dropped every PC rebuilds it
    /// after its latest load, under (1) and (2) at no cost to the shared state.
    /// </remarks>
    [MpCompatFor("killathon.artificialbeings.syncore")]
    internal class AbfSynstructsCore
    {
        // Fixed seed for the blank pawn's generation (see (2) in the remarks)
        private const int BlankSeed = 0x5A1B14C7;

        private static FieldInfo[] idFields;
        private static FieldInfo blankPawnField;
        private static FieldInfo adaptDaysField;

        private static bool building;
        private static int[] savedIds;
        private static float savedAdaptDays;
        private static bool hasSavedAdaptDays;
        private static bool logged;

        public AbfSynstructsCore(ModContentPack mod)
        {
            idFields = AccessTools.GetDeclaredFields(typeof(UniqueIDsManager))
                .Where(f => !f.IsStatic && f.FieldType == typeof(int))
                .ToArray();

            blankPawnField = AccessTools.DeclaredField("ArtificialBeings.SC_Utils:blankPawn")
                ?? throw new MissingFieldException("ArtificialBeings.SC_Utils", "blankPawn");
            adaptDaysField = AccessTools.DeclaredField(typeof(StoryWatcher_PopAdaptation), "adaptDays")
                ?? throw new MissingFieldException(nameof(StoryWatcher_PopAdaptation), "adaptDays");

            // 1.7 lab, FIXES row 145: save/restore ids, adaptDays and Rand around the one-per-process blank pawn build
            var getBlank = AccessTools.DeclaredMethod("ArtificialBeings.SC_Utils:GetBlank")
                ?? throw new MissingMethodException("ArtificialBeings.SC_Utils", "GetBlank");
            MpCompat.harmony.Patch(getBlank,
                prefix: new HarmonyMethod(typeof(AbfSynstructsCore), nameof(BeginBlank)),
                finalizer: new HarmonyMethod(typeof(AbfSynstructsCore), nameof(EndBlank)));

            // 1.7 lab, FIXES row 145: (3) drop the cached blank on every load, on every PC
            MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(GameComponentUtility), nameof(GameComponentUtility.FinalizeInit)),
                postfix: new HarmonyMethod(typeof(AbfSynstructsCore), nameof(ForgetBlank)));
        }

        private static void ForgetBlank() => blankPawnField.SetValue(null, null);

        private static void BeginBlank(out bool __state)
        {
            __state = false;
            // Cached (nothing is built) or nested (cannot happen, GetBlank does not call itself): leave the call alone
            if (building || blankPawnField.GetValue(null) != null)
                return;

            var game = Current.Game;
            var ids = game?.uniqueIDsManager;
            savedIds = ids == null ? null : idFields.Select(f => (int)f.GetValue(ids)).ToArray();

            var adaptation = game?.storyWatcher?.watcherPopAdaptation;
            hasSavedAdaptDays = adaptation != null;
            savedAdaptDays = hasSavedAdaptDays ? (float)adaptDaysField.GetValue(adaptation) : 0f;

            Rand.PushState(BlankSeed);
            building = true;
            __state = true;
        }

        // Finalizer, so the counters, adaptDays and the Rand state come back even when the build throws
        private static void EndBlank(bool __state)
        {
            if (!__state)
                return;

            building = false;
            Rand.PopState();

            var game = Current.Game;
            var ids = game?.uniqueIDsManager;
            List<string> moved = null;
            if (ids != null && savedIds != null)
            {
                for (int i = 0; i < idFields.Length; i++)
                {
                    int now = (int)idFields[i].GetValue(ids);
                    if (!logged && now != savedIds[i])
                        (moved ??= new List<string>()).Add($"{idFields[i].Name} +{now - savedIds[i]}");
                    idFields[i].SetValue(ids, savedIds[i]);
                }
            }

            var adaptation = game?.storyWatcher?.watcherPopAdaptation;
            bool adaptationChanged = false;
            if (hasSavedAdaptDays && adaptation != null)
            {
                adaptationChanged = !logged && (float)adaptDaysField.GetValue(adaptation) != savedAdaptDays;
                adaptDaysField.SetValue(adaptation, savedAdaptDays);
            }

            savedIds = null;

            if (!logged)
            {
                logged = true;
                Log.Message("MPCompat - ABF Synstructs Core: the blank pawn was built on a fixed Rand seed; unique-id counters moved ["
                    + (moved == null ? "none" : string.Join(", ", moved)) + "] and adaptDays " + (adaptationChanged ? "was reset" : "was unchanged")
                    + " by the build and were put back, the Rand state was restored (FIXES row 145)");
            }
        }
    }
}
