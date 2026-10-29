using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>FIXES row 262: a NullReferenceException inside Pawn_TrainingTracker.CanAssignToTrain while a save loads.</summary>
    /// <remarks>
    /// Seen in every lab run once a second map exists (lab runs c11soak_2, c12soak_1, r257t_2): load PostLoadInit of a
    /// Pawn_TrainingTracker goes BackCompatibility.PawnTrainingTrackerPostLoadInit -> CanAssignToTrain(td) -> the static
    /// CanAssignToTrain(td, pawnDef, out visible, pawn), which throws a NullReferenceException (top frame is the patched
    /// method itself: vanilla body and the ProjectJedi prefix are inlined, so the frame does not say which line). The exception
    /// is caught by PostLoadIniter, so a failing tracker only loses the rest of its back-compat pass (the loop that resets
    /// trainables the race cannot learn, and Train() for finished steps) and the debug log pops open.
    /// This guard catches the exception in a Harmony finalizer, only while a save is in its PostLoadInit pass, answers
    /// "accepted" (nothing is reset, so the pawn keeps exactly the training data it was saved with), and logs ONE warning per
    /// pawn def with a probe of every reference the method touches, so the next run names the null field.
    /// Both PCs run the same guard on the same save, so no sim state can differ between them.
    /// </remarks>
    internal static class TrainingTrackerLoadGuard
    {
        private const int MaxProbeLines = 12;
        private static readonly HashSet<string> seen = new HashSet<string>();
        private static int swallowed;

        internal static void Install(Harmony harmony)
        {
            var original = AccessTools.Method(typeof(Pawn_TrainingTracker), nameof(Pawn_TrainingTracker.CanAssignToTrain),
                new[] { typeof(TrainableDef), typeof(ThingDef), typeof(bool).MakeByRefType(), typeof(Pawn) });
            if (original == null)
            {
                Log.Warning("[MPCompat] row 262: Pawn_TrainingTracker.CanAssignToTrain(td, pawnDef, out visible, pawn) not found, guard not installed");
                return;
            }

            harmony.Patch(original, finalizer: new HarmonyMethod(typeof(TrainingTrackerLoadGuard), nameof(Finalizer)));
        }

        private static Exception Finalizer(Exception __exception, ref AcceptanceReport __result, ref bool visible,
            TrainableDef td, ThingDef pawnDef, Pawn pawn)
        {
            if (__exception is not NullReferenceException || Scribe.mode != LoadSaveMode.PostLoadInit)
                return __exception;

            swallowed++;
            string probe;
            try
            {
                probe = Probe(td, pawnDef, pawn);
            }
            catch (Exception e)
            {
                probe = "probe failed: " + e.GetType().Name;
            }

            // one line per signature (pawn def + which references are null), not one per tracker per reload
            if (seen.Count < MaxProbeLines && seen.Add(probe))
                Log.Warning("[MPCompat] row 262: NullReferenceException in Pawn_TrainingTracker.CanAssignToTrain during load, " +
                            "answered 'accepted' (the tracker keeps its saved data). Probe: " + probe + " (swallowed so far: " + swallowed + ")");

            __result = AcceptanceReport.WasAccepted;
            visible = false;
            return null;
        }

        private static string Probe(TrainableDef td, ThingDef pawnDef, Pawn pawn)
        {
            var sb = new StringBuilder();

            void Add(string name, Func<object> read)
            {
                string text;
                try
                {
                    text = read()?.ToString() ?? "NULL";
                }
                catch (Exception e)
                {
                    text = "THROWS " + e.GetType().Name;
                }

                sb.Append(name).Append('=').Append(text).Append("; ");
            }

            var race = pawnDef?.race;
            Add("td", () => td?.defName);
            Add("td.tags", () => td == null ? null : td.tags == null ? null : (object)td.tags.Count);
            Add("td.requiredTrainability", () => td?.requiredTrainability?.defName ?? "none");
            Add("pawn", () => pawn == null ? null : pawn.ThingID);
            Add("pawnDef", () => pawnDef?.defName);
            Add("pawn.def==pawnDef", () => pawn == null ? null : (object)(pawn.def == pawnDef));
            Add("race", () => race == null ? null : "ok");
            Add("race.trainability", () => race?.trainability?.defName);
            Add("race.untrainableTags", () => race?.untrainableTags == null ? "null" : race.untrainableTags.Count.ToString());
            Add("race.trainableTags", () => race?.trainableTags == null ? "null" : race.trainableTags.Count.ToString());
            Add("race.specialTrainables", () => race?.specialTrainables == null ? "null" : race.specialTrainables.Count.ToString());
            Add("pawn.kindDef", () => pawn?.kindDef?.defName);
            Add("pawn.Faction", () => pawn == null ? null : (pawn.Faction?.def?.defName ?? "none"));
            Add("pawn.Map", () => pawn == null ? null : (pawn.Map == null ? "none" : "map " + pawn.Map.Index));
            Add("pawn.health", () => pawn?.health == null ? null : "ok");
            Add("pawn.health.hediffSet", () => pawn?.health?.hediffSet == null ? null : "ok");
            Add("hediffs", () => pawn?.health?.hediffSet?.hediffs == null ? null
                : pawn.health.hediffSet.hediffs.Count + " (null entries " + pawn.health.hediffSet.hediffs.Count(h => h == null) +
                  ", null defs " + pawn.health.hediffSet.hediffs.Count(h => h != null && h.def == null) + ")");
            Add("pawn.Name", () => pawn == null ? null : (pawn.Name == null ? "none" : pawn.Name.GetType().Name));
            Add("pawn.ageTracker", () => pawn?.ageTracker == null ? null : "ok");
            Add("pawn.training", () => pawn == null ? null : (pawn.training == null ? "NULL" : "ok"));
            Add("GetTrainability", () => TrainableUtility.GetTrainability(pawn)?.defName);
            Add("LabelShort", () => pawn?.LabelShort);
            return sb.ToString().TrimEnd();
        }
    }
}
