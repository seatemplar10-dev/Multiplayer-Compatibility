using System.Collections.Generic;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>FIXES row 262 root cause: races that get a Pawn_TrainingTracker but have no trainability.</summary>
    /// <remarks>
    /// Every pawn whose race has intelligence Animal or ToolUser, a faction and no mechanoid flesh gets a training tracker
    /// (PawnComponentsUtility: "(int)pawn.RaceProps.intelligence &lt;= 1 &amp;&amp; pawn.Faction != null &amp;&amp; !pawn.RaceProps.IsMechanoid").
    /// On load, BackCompatibility.PawnTrainingTrackerPostLoadInit calls CanAssignToTrain for every TrainableDef, and that reads
    /// "TrainableUtility.GetTrainability(pawn) ?? pawnDef.race.trainability" and then "trainabilityDef.intelligenceOrder". A race
    /// with no &lt;trainability&gt; throws a NullReferenceException there, once per such pawn per load. Vanilla sets
    /// &lt;trainability&gt;None&lt;/trainability&gt; on its own ToolUser races (BaseDrone, BaseFleshbeast) and VF on BaseVehiclePawn,
    /// but Magos Xenobiologis (Ogliss.AdMech.Xenobiologis) leaves it out of OG_Ork_Pawn_Base and OG_Grot_Pawn_Base, so every
    /// Ork and Grot pawn throws (lab runs c11soak_2 / c12soak_1 / r257t_2 / r257t_3: errors per load = Ork and Grot tracker
    /// pawns in the loaded save, 5 / 10 / 12 / 34). This pass gives every such race the vanilla value None once the defs load.
    /// The game reads race.trainability only in CanAssignToTrain, GetTrainability, the Trainability stat line and the sentience
    /// catalyst tooltip, so the only sim change is that CanAssignToTrain answers instead of throwing. Both PCs load the same
    /// defs and run the same pass, so no state can differ between them.
    /// </remarks>
    internal static class RaceTrainabilityFix
    {
        internal static void Run()
        {
            var fixedDefs = new List<string>();
            foreach (var def in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                var race = def.race;
                if (race == null || race.trainability != null || (int)race.intelligence > (int)Intelligence.ToolUser || race.IsMechanoid)
                    continue;

                race.trainability = TrainabilityDefOf.None;
                fixedDefs.Add(def.defName);
            }

            if (fixedDefs.Count > 0)
                Log.Message($"[MPCompat] row 262: trainability None set on {fixedDefs.Count} races that get a training tracker but had none: {string.Join(", ", fixedDefs)}");
        }
    }
}
